<#
.SYNOPSIS
    Extracts the loot rate sets used by each biome's crate bundles. Emits data/loot_tables.json.

.DESCRIPTION
    Every crate prefab (Cardboards, MetalBoxes, Suitcases, etc.) references a `_LootRateSet`
    ScriptableObject that carries an array of {tag, spawnRate, stackFactor}. Each tag is a
    localized (Chinese in the shipped assets) category label. This extractor:

      1. Walks the dumped loot-crate bundles under wiki/assets/raw/loot_crates_<biome>/
      2. For each MonoBehaviour asset with `_LootSpawnRates`, records the tag-rate list
      3. Emits per-biome loot-rate sets plus a global tag translation for the wiki generator

    Assumes AssetRipper dumps have been produced (Extract-LootTables.ps1 does not run the ripper).
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\Users\SMC\HumanHostMods\wiki\assets\raw',
    [string] $Out  = 'C:\Users\SMC\HumanHostMods\wiki\data\loot_tables.json'
)

$ErrorActionPreference = 'Stop'

# Chinese loot-tag translation. Curated from the crate assets; every tag seen is listed here so
# the wiki page can render an English label alongside the raw tag. Second element is a set of
# English Icon_Info _Tag values that best match the category (used as a coarse "what spawns here"
# hint on item pages later).
$TagTranslations = @{
    '军用装备' = @{ English = 'Military equipment'; Tags = @('Head','Chest','ArmArmor','LegArmor','HandArmor','FootArmor','Shield') }
    '办公用品' = @{ English = 'Office supplies';    Tags = @() }
    '医用原料' = @{ English = 'Medical supplies';   Tags = @('SimpleBandage','MedicalBandage','MedicalCase','Medkit','PainKiller','Antibiotics','FractureSplint') }
    '工具'     = @{ English = 'Tools';              Tags = @() }
    '工具包'   = @{ English = 'Tool kits';          Tags = @() }
    '布料'     = @{ English = 'Cloth';              Tags = @() }
    '废墟特供' = @{ English = 'Ruin-exclusive';     Tags = @() }
    '废旧材料' = @{ English = 'Salvage materials';  Tags = @() }
    '弹药'     = @{ English = 'Ammunition';         Tags = @('.45','9x19','5.56x45','7.62x39','7.62x51','7.62x54','12x70','Arrow') }
    '枪械'     = @{ English = 'Firearms';           Tags = @('Gun') }
    '枪械包'   = @{ English = 'Firearm kits';       Tags = @() }
    '枪械附件' = @{ English = 'Gun attachments';    Tags = @('Scope') }
    '枪械零件' = @{ English = 'Gun parts';          Tags = @() }
    '民用装备' = @{ English = 'Civilian equipment'; Tags = @() }
    '水'       = @{ English = 'Water';              Tags = @('Water') }
    '沙漠特供' = @{ English = 'Desert-exclusive';   Tags = @() }
    '灯具'     = @{ English = 'Lighting';           Tags = @() }
    '皮革'     = @{ English = 'Leather';            Tags = @() }
    '羽毛'     = @{ English = 'Feathers';           Tags = @() }
    '腐败食物' = @{ English = 'Spoiled food';       Tags = @() }
    '警用装备' = @{ English = 'Police equipment';   Tags = @() }
    '近战武器' = @{ English = 'Melee weapons';      Tags = @('MeleeWeapon','Bow') }
    '雨林特供' = @{ English = 'Jungle-exclusive';   Tags = @() }
    '雪地特供' = @{ English = 'Snow-exclusive';     Tags = @() }
    '食物'     = @{ English = 'Food';               Tags = @('Food') }
    '骨头'     = @{ English = 'Bones';              Tags = @() }
}

# Biomes we've dumped. Slugs match the wiki loot_crates.json PageTitle slugs.
$BiomeDirs = Get-ChildItem $Root -Directory -Filter 'loot_crates_*' -ErrorAction SilentlyContinue

$result = [ordered]@{
    tag_translations = $TagTranslations
    biomes           = [ordered]@{}
}

foreach ($dir in $BiomeDirs) {
    $slug = $dir.Name -replace '^loot_crates_',''
    $mbDir = Join-Path $dir.FullName 'ExportedProject\Assets\MonoBehaviour'
    if (-not (Test-Path $mbDir)) { continue }

    $rateSets = [ordered]@{}
    foreach ($assetFile in (Get-ChildItem $mbDir -Filter '*.asset' -File)) {
        # Force UTF-8 read: PS 5.1's Get-Content defaults to system ANSI, which corrupts the
        # Chinese loot tags on Windows English installs (double-encoded mojibake).
        $lines = Get-Content $assetFile.FullName -Encoding UTF8
        # Only files with `_LootSpawnRates:` are loot rate sets; others are unrelated.
        if (-not ($lines | Where-Object { $_ -match '^\s*_LootSpawnRates:' })) { continue }
        $rates = New-Object System.Collections.Generic.List[PSCustomObject]
        $current = $null
        foreach ($line in $lines) {
            if ($line -match '^\s*-\s*_spawnLootTag:\s*(.+)$') {
                if ($current) { $rates.Add([PSCustomObject]$current) }
                $current = @{ tag = $matches[1].Trim(); rate = 0.0; stack = 1.0 }
            }
            elseif ($current -and $line -match '^\s*_spawnRateRange:\s*(\S+)') { $current.rate  = [double]$matches[1] }
            elseif ($current -and $line -match '^\s*_stackFactor:\s*(\S+)')    { $current.stack = [double]$matches[1] }
        }
        if ($current) { $rates.Add([PSCustomObject]$current) }
        $rateSets[$assetFile.BaseName] = $rates.ToArray()
    }

    # Crate prefabs: walk the In_Use tree, collect prefab name + which rate set it references.
    # We identify the rate set by name from the _LootRateSet GUID lookup, which requires the
    # asset's .meta file. Simpler: since AssetRipper puts each rate set as a MonoBehaviour asset
    # under Assets\MonoBehaviour/<Name>.asset with a sibling .meta carrying its GUID, we can
    # build a GUID->asset-name map and resolve prefab references through it.
    $guidToName = @{}
    foreach ($metaFile in (Get-ChildItem $mbDir -Filter '*.asset.meta' -File)) {
        $m = Get-Content $metaFile.FullName -TotalCount 3 | Select-String -Pattern '^guid:\s*([0-9a-f]{32})' | Select-Object -First 1
        if ($m) {
            $guid = $m.Matches[0].Groups[1].Value
            $name = $metaFile.BaseName -replace '\.asset$',''
            $guidToName[$guid] = $name
        }
    }

    $crates = [ordered]@{}
    $prefabRoot = Join-Path $dir.FullName 'ExportedProject\Assets\In_Use'
    if (Test-Path $prefabRoot) {
        foreach ($prefab in (Get-ChildItem $prefabRoot -Filter '*.prefab' -File -Recurse)) {
            # Find the `_LootRateSet: {fileID:..., guid: X, type: 2}` line
            $lootMatch = Select-String -Path $prefab.FullName -Pattern '_LootRateSet:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]{32})' -List
            if (-not $lootMatch) { continue }
            $guid = $lootMatch.Matches[0].Groups[1].Value
            $rsName = if ($guidToName.ContainsKey($guid)) { $guidToName[$guid] } else { "guid:$guid" }
            $crates[$prefab.BaseName] = $rsName
        }
    }

    $result.biomes[$slug] = [ordered]@{
        rate_sets = $rateSets
        crates    = $crates
    }
    Write-Host "[loot] $slug : $($rateSets.Count) rate set(s), $($crates.Count) crate prefab(s)"
}

# Write JSON
$json = $result | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[loot] wrote $Out"
