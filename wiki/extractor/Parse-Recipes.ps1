<#
.SYNOPSIS
    Parses the extracted AssetRipper output into recipes.json.

.DESCRIPTION
    Walks the extracted project under wiki/assets/raw/main/ExportedProject/Assets/:
      1. Builds a GUID -> asset name map from every .meta file.
      2. Parses each WB_*.prefab under In_Use/Conts/Mods/Player_Made/Workbench/*.
         Extracts _workbenchType and every recipe (iconRef GUID, craftNum, craftSeconds, matsData).
      3. Resolves GUIDs to human names.
      4. Emits recipes.json into wiki/data/.

    Simple line-oriented parser rather than a full YAML library - the Unity YAML for these prefabs
    follows a very regular structure so hand-parsing is more predictable than pulling in a dep.
#>
[CmdletBinding()]
param(
    [string] $ExtractedRoot = 'C:\Users\SMC\HumanHostMods\wiki\assets\raw\main\ExportedProject\Assets',
    [string] $CatalogPath   = 'C:\Program Files (x86)\Steam\steamapps\common\Human Host\Human Host_Data\StreamingAssets\aa\catalog.json',
    [string] $Out           = 'C:\Users\SMC\HumanHostMods\wiki\data\recipes.json'
)

$ErrorActionPreference = 'Stop'

Write-Host "[recipes] loading GUID -> name map produced by CatalogEntryParser..."
# The C# extractor now writes a proper GUID map to data/guid_name_map.json by parsing Unity's
# ContentCatalogData binary format (KeyDataString + EntryDataString + BucketDataString).
# Keys sharing an entry-index are aliases for the same asset; the map picks the most readable
# alias per GUID.
$mapPath = Join-Path (Split-Path $Out -Parent) 'guid_name_map.json'
if (Test-Path $mapPath) {
    $mapJson = Get-Content $mapPath -Raw
    $mapObj  = $mapJson | ConvertFrom-Json
    $guidMap = @{}
    foreach ($p in $mapObj.PSObject.Properties) { $guidMap[$p.Name] = $p.Value }
    Write-Host "[recipes] loaded $($guidMap.Count) GUIDs from catalog parser"
} else {
    Write-Warning "guid_name_map.json missing - falling back to empty map (recipes will show raw GUIDs)"
    $guidMap = @{}
}

# Preserved for the older parse path (unused when map above loads); left in scope for stability.
$keyBytes = @()

# Extract all (offset, string) tuples from the buffer. Format per key: [type-byte or padding]
# then 4-byte little-endian length, then N bytes of ASCII. Rather than parsing rigidly,
# scan for length-prefixed ASCII runs and record their offsets.
$keyList = New-Object System.Collections.Generic.List[PSCustomObject]
$i = 0
while ($i -le $keyBytes.Length - 5) {
    # Interpret 4 bytes at position i as a little-endian length
    $len = $keyBytes[$i] -bor ($keyBytes[$i+1] -shl 8) -bor ($keyBytes[$i+2] -shl 16) -bor ($keyBytes[$i+3] -shl 24)
    if ($len -ge 3 -and $len -le 256 -and ($i + 4 + $len) -le $keyBytes.Length) {
        # Peek at the bytes
        $allAscii = $true
        for ($k = 0; $k -lt $len; $k++) {
            $b = $keyBytes[$i + 4 + $k]
            if ($b -lt 32 -or $b -ge 127) { $allAscii = $false; break }
        }
        if ($allAscii) {
            $str = [System.Text.Encoding]::ASCII.GetString($keyBytes, $i + 4, $len)
            # Filter reasonable strings
            if ($str -match '^[A-Za-z0-9_.\-/]+$') {
                $keyList.Add([PSCustomObject]@{ Offset = $i; Value = $str; IsGuid = ($str.Length -eq 32 -and $str -match '^[a-f0-9]{32}$') })
                $i = $i + 4 + $len
                continue
            }
        }
    }
    $i++
}

# Legacy in-line heuristic replaced by the C# parser above; block removed.

# WorkbenchType enum from prefab _workbenchType: N observations. Filled from data as we parse.
$wbEnumMap = @{}

function Parse-Recipes {
    param([string]$Path)
    $lines = Get-Content $Path
    $recipes = New-Object System.Collections.Generic.List[PSCustomObject]

    $workbenchType = -1
    $currentRecipe = $null
    $currentMat = $null
    $inCraftItems = $false
    $inPerIcon    = $false
    $inMatsData   = $false
    $lastGuidVar  = ''  # tracks whether the just-seen m_AssetGUID line belongs to iconRef or matIcon

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        $t = $l.TrimStart()

        # top-level workbench type
        if ($t -match '^_workbenchType:\s*(\d+)') {
            $workbenchType = [int]$matches[1]
            continue
        }
        if ($t -match '^_CraftItemsData:') { $inCraftItems = $true; continue }
        if (-not $inCraftItems) { continue }

        # New category entry inside _CraftItemsData: "- BigCategory:"
        if ($t -match '^- BigCategory:') { $inPerIcon = $false; $inMatsData = $false; continue }
        # perIconData: begin
        if ($t -match '^perIconData:') { $inPerIcon = $true; $inMatsData = $false; continue }

        # A new recipe entry begins with "- iconRef:" at any indent
        if ($inPerIcon -and $t -match '^- iconRef:') {
            # Commit any previous recipe first
            if ($currentRecipe) { $recipes.Add($currentRecipe) | Out-Null }
            $currentRecipe = [PSCustomObject]@{
                output_guid    = ''
                output_name    = ''
                craft_num      = 0
                craft_seconds  = 0
                inputs         = New-Object System.Collections.Generic.List[PSCustomObject]
            }
            $inMatsData  = $false
            $lastGuidVar = 'iconRef'
            continue
        }

        if (-not $currentRecipe) { continue }

        # Recipe-level fields
        if ($t -match '^craftNum:\s*(\d+)')     { $currentRecipe.craft_num = [int]$matches[1]; continue }
        if ($t -match '^craftSeconds:\s*([\d.]+)') { $currentRecipe.craft_seconds = [double]$matches[1]; continue }

        if ($t -match '^matsData:') { $inMatsData = $true; continue }

        # A new material entry begins with "- matIcon:"
        if ($inMatsData -and $t -match '^- matIcon:') {
            if ($currentMat) { $currentRecipe.inputs.Add($currentMat) | Out-Null }
            $currentMat  = [PSCustomObject]@{ guid=''; name=''; need_count=0 }
            $lastGuidVar = 'matIcon'
            continue
        }

        if ($t -match '^matNeedCount:\s*(\d+)' -and $currentMat) {
            $currentMat.need_count = [int]$matches[1]
            # matNeedCount closes a material entry
            $currentRecipe.inputs.Add($currentMat) | Out-Null
            $currentMat = $null
            continue
        }

        # GUID line - belongs to whichever iconRef/matIcon we're currently in
        if ($t -match '^m_AssetGUID:\s*([a-f0-9]{32})') {
            $guid = $matches[1]
            $name = if ($guidMap.ContainsKey($guid)) { $guidMap[$guid] } else { "unknown/$($guid.Substring(0,8))" }
            if ($lastGuidVar -eq 'matIcon' -and $currentMat) {
                $currentMat.guid = $guid; $currentMat.name = $name
            } elseif ($lastGuidVar -eq 'iconRef' -and $currentRecipe) {
                $currentRecipe.output_guid = $guid; $currentRecipe.output_name = $name
            }
            continue
        }
    }

    # Commit tail
    if ($currentMat -and $currentRecipe) { $currentRecipe.inputs.Add($currentMat) | Out-Null }
    if ($currentRecipe) { $recipes.Add($currentRecipe) | Out-Null }

    return @{
        workbench_type_id = $workbenchType
        recipes           = $recipes
    }
}

$wbDir = Join-Path $ExtractedRoot 'In_Use\Conts\Mods\Player_Made\Workbench'
if (-not (Test-Path $wbDir)) { throw "Workbench dir missing: $wbDir" }
$workbenches = @()
foreach ($wb in Get-ChildItem $wbDir -Directory) {
    $prefab = Get-ChildItem $wb.FullName -Filter '*.prefab' | Where-Object { $_.BaseName -like 'WB_*' } | Select-Object -First 1
    if (-not $prefab) { continue }
    Write-Host "[recipes] parsing $($prefab.Name)..."
    $parsed = Parse-Recipes -Path $prefab.FullName
    # Key as string so ConvertTo-Json accepts the hashtable
    $wbEnumMap[[string]$parsed.workbench_type_id] = $prefab.BaseName
    $workbenches += [PSCustomObject]@{
        workbench_prefab    = $prefab.BaseName
        workbench_type_id   = $parsed.workbench_type_id
        recipe_count        = $parsed.recipes.Count
        recipes             = $parsed.recipes
    }
}

$result = [PSCustomObject]@{
    generated_at = (Get-Date).ToString('o')
    guid_map_size = $guidMap.Count
    workbench_enum_observed = $wbEnumMap
    workbenches  = $workbenches | Sort-Object workbench_prefab
}
$json = $result | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ""
Write-Host "[recipes] wrote $Out"
Write-Host "[recipes] $($workbenches.Count) workbenches, $((($workbenches | Measure-Object -Property recipe_count -Sum).Sum)) total recipes"
$workbenches | ForEach-Object {
    Write-Host ("  {0,-24}  workbenchType={1,-3}  recipes={2}" -f $_.workbench_prefab, $_.workbench_type_id, $_.recipe_count)
}
