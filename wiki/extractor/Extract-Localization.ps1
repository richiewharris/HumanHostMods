<#
.SYNOPSIS
    Extracts the game's per-item English display names + tooltip text from AssetRipper-exported
    Tooltip .asset files. Emits data/localization.json.

.DESCRIPTION
    Every Icon_Info prefab references a _ToolTipText ScriptableObject that carries a `_Infos` array
    of one entry per language. English is languageType: 2. Each entry supplies:
        _ItemName          : player-facing display name (may differ from internal asset ID)
        _ItemType          : short subtype label ("Melee weapon", "Refined metal", etc.)
        _ItemProperty      : property line (often empty)
        _ItemInstruction   : usage note (often empty)

    The tooltip filename encodes the item's internal short name:
        Wooden_Club_Tooltip.asset   -> internal id "Wooden_Club" -> display name "Crude Wooden Club"

    This extractor walks every *_Tooltip.asset file under the icon and main extractions, parses the
    English block, and emits a map from internal id to the four English fields.
#>
[CmdletBinding()]
param(
    [string] $IconsDir = 'C:\Users\SMC\HumanHostMods\wiki\assets\raw\all_icons\ExportedProject\Assets\MonoBehaviour',
    [string] $MainDir  = 'C:\Users\SMC\HumanHostMods\wiki\assets\raw\main\ExportedProject\Assets\MonoBehaviour',
    [string] $Out      = 'C:\Users\SMC\HumanHostMods\wiki\data\localization.json'
)

$ErrorActionPreference = 'Stop'
$ENGLISH_LANGUAGE_TYPE = 2

$dirs = @($IconsDir, $MainDir) | Where-Object { Test-Path $_ }
$tooltipFiles = @()
foreach ($d in $dirs) {
    $tooltipFiles += Get-ChildItem $d -Filter '*_Tooltip.asset' -File -ErrorAction SilentlyContinue
}
$tooltipFiles = $tooltipFiles | Sort-Object Name -Unique
Write-Host "[loc] scanning $($tooltipFiles.Count) tooltip files..."

$entries = New-Object System.Collections.Generic.List[PSCustomObject]

foreach ($f in $tooltipFiles) {
    $internalId = $f.BaseName -replace '_Tooltip$',''
    $lines = Get-Content $f.FullName
    # Walk to find the languageType: 2 block; the next _ItemName / _ItemType / _ItemProperty /
    # _ItemInstruction lines belong to English.
    $inEnglish = $false
    $itemName = ''
    $itemType = ''
    $itemProperty = ''
    $itemInstruction = ''
    foreach ($line in $lines) {
        $trim = $line.TrimStart()
        if ($trim -match '^- languageType:\s*(\d+)') {
            $langId = [int]$matches[1]
            $inEnglish = ($langId -eq $ENGLISH_LANGUAGE_TYPE)
            continue
        }
        if (-not $inEnglish) { continue }
        if ($trim -match '^_ItemName:\s*(.*)$')        { $itemName        = $matches[1].Trim() }
        elseif ($trim -match '^_ItemType:\s*(.*)$')    { $itemType        = $matches[1].Trim() }
        elseif ($trim -match '^_ItemProperty:\s*(.*)$'){ $itemProperty    = $matches[1].Trim() }
        elseif ($trim -match '^_ItemInstruction:\s*(.*)$'){ $itemInstruction = $matches[1].Trim() }
    }

    if (-not $itemName) { continue }  # skip tooltips with no English entry (defensive)
    $entries.Add([PSCustomObject]@{
        internal_id       = $internalId
        item_name         = $itemName
        item_type         = $itemType
        item_property     = $itemProperty
        item_instruction  = $itemInstruction
    })
}

# Write JSON
$json = $entries | Sort-Object internal_id | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[loc] extracted $($entries.Count) English tooltips -> $Out"

# Also emit a quick summary: how many display names differ from a naive prettification of the internal id
$naive = { param($id) ($id -replace '_', ' ') }
$diffs = $entries | Where-Object { (& $naive $_.internal_id) -ne $_.item_name }
Write-Host "[loc] display name differs from naive underscore-to-space rendering for $($diffs.Count) / $($entries.Count) items"
Write-Host "[loc] sample differences:"
$diffs | Select-Object -First 15 | ForEach-Object {
    "  $($_.internal_id)  ->  $($_.item_name)"
}
