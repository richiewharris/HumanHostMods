<#
.SYNOPSIS
    Turns wiki/data/*.json (produced by the C# extractor) into wiki/pages/**/*.mediawiki stub pages.

.DESCRIPTION
    Idempotent. Generated pages carry an HHWIKI:GENERATED marker on line 1. If a page is
    edited by hand (marker removed), the generator will leave it alone.

.PARAMETER Data
    JSON input dir. Default: ../data

.PARAMETER Pages
    MediaWiki output root. Default: ../pages
#>
[CmdletBinding()]
param(
    [string] $Data  = (Join-Path $PSScriptRoot '..\data'),
    [string] $Pages = (Join-Path $PSScriptRoot '..\pages')
)

$ErrorActionPreference = 'Stop'
$Data  = (Resolve-Path $Data).Path
$Pages = (Resolve-Path $Pages).Path

# Note: HTML comments must not contain "--" per spec, so we use a colon separator.
$Marker = '<!-- HHWIKI:GENERATED: remove this line to protect from regeneration -->'

function Read-JsonArray {
    param([string]$Name)
    $path = Join-Path $Data "$Name.json"
    if (-not (Test-Path $path)) { throw "Missing data file: $path" }
    $data = Get-Content $path -Raw | ConvertFrom-Json
    if ($null -eq $data) { return @() }
    if ($data -isnot [System.Array]) { return @($data) }
    return $data
}

function Write-Stub {
    param([string]$RelPath, [string]$Content)
    $full = Join-Path $Pages $RelPath
    $dir  = Split-Path $full -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    if (Test-Path $full) {
        $first = (Get-Content $full -TotalCount 1)
        if ($first -ne $Marker) {
            Write-Host "  skip (hand-edited): $RelPath" -ForegroundColor DarkGray
            return
        }
    }
    # Write UTF-8 *without* BOM. PS 5.1's `Set-Content -Encoding utf8` adds a BOM
    # which Fandom's editor treats as literal text. Use .NET directly instead.
    [System.IO.File]::WriteAllText($full, $Content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  wrote: $RelPath"
}

function Write-Redirect {
    # Redirect files can't carry the HHWIKI:GENERATED comment marker because MediaWiki's #REDIRECT
    # parser requires the redirect directive to be the leading content. So we treat any file whose
    # existing content matches the bare "#REDIRECT [[...]]" pattern as auto-generated and overwrite
    # it; hand-authored content (anything else) is left alone.
    param([string]$RelPath, [string]$Content)
    $full = Join-Path $Pages $RelPath
    $dir  = Split-Path $full -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    if (Test-Path $full) {
        $existing = (Get-Content $full -Raw)
        if ($existing.Trim() -notmatch '^#REDIRECT\s*\[\[[^\]]+\]\]\s*$') {
            Write-Host "  skip redirect (hand-edited): $RelPath" -ForegroundColor DarkGray
            return
        }
        if ($existing -eq $Content) { return }   # no-op when already correct
    }
    [System.IO.File]::WriteAllText($full, $Content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  wrote redirect: $RelPath"
}

function Slugify {
    # Wiki-page filename convention: use exact page title with spaces Ã¢â€ â€™ underscores, safe for the filesystem.
    param([string]$Title)
    return ($Title -replace '\s+', '_') -replace '[<>:"/\\|?*]', ''
}

function Prettify-AssetId {
    # Turn a raw addressable id (RII_Wood_Black, BFI_WB_Biochemical_Icon, etc.) into a display label.
    # Strips prefixes we know about, strips _Icon suffix, replaces underscores with spaces.
    param([string] $Id)
    if (-not $Id) { return $Id }
    $prefixes = @('BFI_WB_', 'BF_WB_', 'BFI_', 'BF_', 'BOI_', 'BO_', 'BTI_', 'BT_', 'BWI_', 'BW_',
                  'RII_', 'FWI_', 'AXE_', 'DAG_', 'SPEAR_', 'BIG_', 'TOOL_', 'RW_', 'RWI_',
                  'EQI_', 'VPI_', 'BHC_', 'BAT_', 'BLADE_')
    $s = $Id
    foreach ($p in $prefixes) { if ($s.StartsWith($p)) { $s = $s.Substring($p.Length); break } }
    if ($s.EndsWith('_Icon'))     { $s = $s.Substring(0, $s.Length - '_Icon'.Length) }
    if ($s.EndsWith('_Icon_1'))   { $s = $s.Substring(0, $s.Length - '_Icon_1'.Length) }
    return ($s -replace '_', ' ')
}

# Mapping to an item's icon filename (as uploaded to Fandom as File:X.png). The icon PNG naming
# convention across all our extractions matches the "tooltip short id" (the asset id with its
# addressable prefix and _Icon suffix stripped), so we derive the filename from the raw asset id
# rather than from the display title (titles are localized, PNG filenames are not).
function Get-IconFilename {
    param([string] $Title, [string] $Category, [string] $AssetId)
    if ($Category -eq 'Workbench') {
        $prefab = $WorkbenchPrefabMap[$Title]
        if (-not $prefab) { return $null }
        $iconMap = @{
            'WB_Anvil'          = 'WB_Anvil.png'
            'WB_Biochemical'    = 'WB_Biochemical_Workbench.png'
            'WB_Campfire'       = 'WB_Campfire.png'
            'WB_Carpentry'      = 'WB_Carpentry.png'
            'WB_Concrete_Mixer' = 'WB_Concrete_Mixer.png'
            'WB_Cutting'        = 'WB_Cutting.png'
            'WB_Electronic'     = 'WB_Electronic.png'
            'WB_Furnace'        = 'WB_Furnace.png'
            'WB_Gunsmith'       = 'WB_Gunsmith.png'
            'WB_Mechanical'     = 'WB_Mechanical.png'
        }
        return $iconMap[$prefab]
    }
    if ($AssetId) {
        $short = Get-TooltipShortId $AssetId
        if ($short) { return "$short.png" }
    }
    # Fallback: derive from the title (works when title still matches internal short name)
    return "$($Title -replace ' ', '_').png"
}

# Static fallback for workbench titles used before localization.json is loaded (e.g. old code paths
# referencing the old-style names). The authoritative title now comes from Get-DisplayName below,
# resolved after localization loads. WB_Electronic has no tooltip so falls back to this table.
$WorkbenchLegacyTitles = @{
    'WB_Anvil'          = 'Anvil Workbench'
    'WB_Biochemical'    = 'Biochemical Workbench'
    'WB_Campfire'       = 'Campfire'
    'WB_Carpentry'      = 'Carpentry Workbench'
    'WB_Concrete_Mixer' = 'Cement Mixer'
    'WB_Cutting'        = 'Cutting Workbench'
    'WB_Electronic'     = 'Electronics Workbench'
    'WB_Furnace'        = 'Furnace'
    'WB_Gunsmith'       = 'Gun Workbench'
    'WB_Mechanical'     = 'Mechanical Workbench'
    'HandMade'          = 'HandMade'
}

# Load recipes once at generator start.
$recipesPath = Join-Path $Data 'recipes.json'
$recipesData = if (Test-Path $recipesPath) { Get-Content $recipesPath -Raw | ConvertFrom-Json } else { $null }

# ---------------------------------------------------------------------------
# Localization: English display names + item types from the extracted tooltip assets.
# Every Icon_Info references a Tooltip ScriptableObject with a per-language `_Infos` array;
# languageType 2 is English. See wiki/extractor/Extract-Localization.ps1.
# ---------------------------------------------------------------------------
$locPath = Join-Path $Data 'localization.json'
$script:LocByShortName = @{}    # internal short id (Wooden_Club, Iron_Hammer, WB_Anvil, Ore_Iron) -> {name,type,property,instruction}
$script:LocByBaseSuffix = @{}   # for tier variants: fallback by material suffix (Damaged_Planks -> "Damaged Wooden Boards")
if (Test-Path $locPath) {
    $locJson = Get-Content $locPath -Raw | ConvertFrom-Json
    foreach ($e in $locJson) {
        $script:LocByShortName[$e.internal_id] = $e
    }
    # Build tier-variant suffix map: for each tier-shaped tooltip, key by the material tail so
    # base names like "Damaged_Planks" (with no direct tooltip) can inherit "Damaged Wooden Boards".
    foreach ($e in ($locJson | Where-Object { $_.internal_id -match '^[1-7]_[1-9]_' })) {
        # Strip the tier + shape prefix to get the material tail
        $tail = $e.internal_id -replace '^[1-7]_[1-9]_', ''
        $shapeTokens = @(
            'Block_Small_1\.2_', 'Block_Small_', 'Block_1\.4_', 'Block_',
            'Cuboid_', 'Half_Cylinder_L_', 'Half_Cylinder_M_', 'Half_Cylinder_S_',
            'Pyramid_Squat_', 'Pyramid_Tall_',
            'Steps_Curved_', 'Steps_',
            'Triangle_Small_1\.4_', 'Triangle_Small_', 'Triangle_1\.4_',
            'Triangle_CornerS_', 'Triangle_Corner_', 'Triangle_'
        )
        foreach ($tok in $shapeTokens) { $tail = $tail -replace "^$tok", '' }
        if ($tail -and -not $script:LocByBaseSuffix.ContainsKey($tail)) {
            $script:LocByBaseSuffix[$tail] = $e
        }
    }
    Write-Host "[loc] loaded $($script:LocByShortName.Count) tooltip entries, $($script:LocByBaseSuffix.Count) tier-variant suffixes for fallback"
}

# ---------------------------------------------------------------------------
# Per-item runtime stats: damage, durability, stack, tags, ammo. Extracted from every
# Icon_Info prefab under Assets/In_Use. See wiki/extractor/Extract-ItemStats.ps1.
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# Tier / quality system: 6-tier scaling with damage/dura/hitdown/blade multipliers and
# tooltip color codes. Extracted from the Item_Slot_Mgr MonoBehaviour in level1.unity.
# See wiki/extractor/Extractors/TierExtractor.cs.
# ---------------------------------------------------------------------------
$tierPath = Join-Path $Data 'tier_config.json'
$script:TierConfig = $null
if (Test-Path $tierPath) {
    $script:TierConfig = Get-Content $tierPath -Raw | ConvertFrom-Json
    Write-Host "[tier] loaded $($script:TierConfig.TierCount)-tier config ($($script:TierConfig.DamageFactors -join '/') damage factors)"
}

# Community names for each tier index (0-based). Mirrors classic MMO rarity naming so the
# color-coded chip on the wiki matches what players see in-game.
$script:TierNames = @('Common','Uncommon','Rare','Epic','Legendary','Mythic')

# Emit a compact wikitable showing how the base value of a weapon field scales across all
# tiers. `factorArray` is one of DamageFactors/DuraFactors/HitDownFactors/BladeHitFactors and
# `baseValue` is the item's Tier 1 (base) stat. Returns wiki markup ending with a newline.
function Build-TierTable {
    param(
        [double] $BaseDamage,
        [double] $BaseDurability,
        [double] $BaseHitDown,   # 0-1 probability
        [double] $BaseBlade      # 0-1 probability
    )
    if (-not $script:TierConfig) { return '' }
    $tc = $script:TierConfig
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('{| class="wikitable" style="font-size:0.9em; text-align:center;"')
    [void]$lines.Add('|+ Tier scaling')
    # Header row: tier chips (one header cell per line so per-cell style attributes stay legible)
    [void]$lines.Add('! Stat')
    for ($i = 0; $i -lt $tc.TierCount; $i++) {
        $c = $tc.TooltipColors[$i]
        $name = $script:TierNames[$i]
        [void]$lines.Add("! style=""background:$($c.HexRgb); color:#fff; text-shadow:0 0 3px #000;"" | T$($i+1)<br /><small>$name</small>")
    }
    # Format helper: whole numbers render clean; decimals get at most one decimal place, no trailing zero
    $fmt = {
        param($x)
        $rounded = [math]::Round($x, 1)
        if ($rounded -eq [math]::Floor($rounded)) { "$([int]$rounded)" } else { "$rounded" }
    }
    # Damage row
    if ($BaseDamage -gt 0) {
        $row = @('|-', '| Damage')
        for ($i = 0; $i -lt $tc.TierCount; $i++) {
            $row += "| $(& $fmt ($BaseDamage * $tc.DamageFactors[$i]))"
        }
        [void]$lines.Add(($row -join "`n"))
    }
    # Durability row
    if ($BaseDurability -gt 0) {
        $row = @('|-', '| Durability')
        for ($i = 0; $i -lt $tc.TierCount; $i++) {
            $v = [int][math]::Round($BaseDurability * $tc.DuraFactors[$i])
            $row += "| $v"
        }
        [void]$lines.Add(($row -join "`n"))
    }
    # Hitdown probability row (0-1 -> %)
    if ($BaseHitDown -gt 0) {
        $row = @('|-', '| Hitdown')
        for ($i = 0; $i -lt $tc.TierCount; $i++) {
            $p = [math]::Min(1.0, $BaseHitDown * $tc.HitDownFactors[$i])
            $row += "| $([int]([math]::Round($p * 100)))%"
        }
        [void]$lines.Add(($row -join "`n"))
    }
    # Blade slash probability row
    if ($BaseBlade -gt 0) {
        $row = @('|-', '| Blade slash')
        for ($i = 0; $i -lt $tc.TierCount; $i++) {
            $p = [math]::Min(1.0, $BaseBlade * $tc.BladeHitFactors[$i])
            $row += "| $([int]([math]::Round($p * 100)))%"
        }
        [void]$lines.Add(($row -join "`n"))
    }
    [void]$lines.Add('|}')
    return ($lines -join "`n") + "`n"
}

# ---------------------------------------------------------------------------
# HandMade recipes: the "craft by hand" list from the basic crafting interface. Sourced from
# a scene-embedded Craft_Items MonoBehaviour in level1 (see wiki/extractor/Extractors/HandCraftExtractor.cs).
# ---------------------------------------------------------------------------
$handMadePath = Join-Path $Data 'handmade_recipes.json'
$script:HandMadeRecipes = $null
$script:HandMadeByShortId = @{}   # short id (like WB_Anvil, Wooden_Club, IronSpike_Trap) -> recipe object
$guidMapPath = Join-Path $Data 'guid_name_map.json'
$script:GuidToName = @{}
if (Test-Path $guidMapPath) {
    $gmap = Get-Content $guidMapPath -Raw | ConvertFrom-Json
    foreach ($p in $gmap.PSObject.Properties) { $script:GuidToName[$p.Name] = $p.Value }
}
if (Test-Path $handMadePath) {
    $script:HandMadeRecipes = Get-Content $handMadePath -Raw | ConvertFrom-Json
    # Index by output short id so pages can look themselves up. Short id = internal asset name
    # minus BFI_/BTI_/RII_/DAG_/etc. prefix and _Icon suffix (same convention as tooltip short id).
    foreach ($cat in $script:HandMadeRecipes.Categories) {
        foreach ($r in $cat.Recipes) {
            if (-not $r.OutputGuid) { continue }
            $rawName = if ($script:GuidToName.ContainsKey($r.OutputGuid)) { $script:GuidToName[$r.OutputGuid] } else { '' }
            if (-not $rawName) { continue }
            # Strip Icon suffix and common addressable prefixes to get a stable id we can match against
            # workbench prefabs and trap catalog entries.
            $short = $rawName
            if ($short.EndsWith('_Icon')) { $short = $short.Substring(0, $short.Length - 5) }
            foreach ($p in @('BFI_','BF_','BTI_','BT_','BOI_','BO_','BWI_','BW_','RII_','FWI_','AXE_','DAG_','SPEAR_','BIG_','TOOL_','RWI_','RW_','EQI_','VPI_','BHC_','BAT_','BLADE_')) {
                if ($short.StartsWith($p)) { $short = $short.Substring($p.Length); break }
            }
            $script:HandMadeByShortId[$short] = $r
            # Also index by the full internal name (minus _Icon) so lookups can match either form.
            $rawSansIcon = if ($rawName.EndsWith('_Icon')) { $rawName.Substring(0, $rawName.Length - 5) } else { $rawName }
            $script:HandMadeByShortId[$rawSansIcon] = $r
        }
    }
    Write-Host "[hand] loaded $(($script:HandMadeRecipes.Categories | ForEach-Object { $_.Recipes.Count } | Measure-Object -Sum).Sum) HandMade recipes across $($script:HandMadeRecipes.Categories.Count) categories"
}

# Render a HandMade recipe as a compact wiki table row. Inputs come out as `[[Name]] x<count>`
# using the shared GUID -> display-name resolver.
function Format-HandMadeRecipe {
    param($Recipe)
    if (-not $Recipe) { return '' }
    $inputParts = @()
    foreach ($m in $Recipe.Inputs) {
        $rawName = if ($script:GuidToName.ContainsKey($m.Guid)) { $script:GuidToName[$m.Guid] } else { $m.Guid.Substring(0, [Math]::Min(8, $m.Guid.Length)) }
        # Try to resolve to a wiki page title using the localization system.
        $title = Get-DisplayName $rawName
        if (-not $title) {
            $short = $rawName
            if ($short.EndsWith('_Icon')) { $short = $short.Substring(0, $short.Length - 5) }
            $title = ($short -replace '^(RII_|BFI_|BTI_|BOI_|BWI_|FWI_|AXE_|DAG_|SPEAR_|BIG_|TOOL_|RWI_|EQI_|VPI_|BHC_|BAT_|BLADE_)', '') -replace '_',' '
        }
        $inputParts += "[[$title]] x$($m.NeedCount)"
    }
    return "{| class=""wikitable"" style=""font-size:0.9em;""`n! Craft time !! Inputs !! Yield`n|-`n| $($Recipe.CraftSeconds)s || $($inputParts -join ', ') || $($Recipe.CraftNum)`n|}"
}

$statsPath = Join-Path $Data 'item-stats.json'
$script:StatsByShortName   = @{}
$script:StatsByDisplayName = @{}   # localized name -> stats (for atomic-materials title lookup)
if (Test-Path $statsPath) {
    $statsJson = Get-Content $statsPath -Raw | ConvertFrom-Json
    foreach ($s in $statsJson) {
        $script:StatsByShortName[$s.internal_id] = $s
        # Reverse index by localized display name (via the tooltip's item_name for this short id).
        if ($script:LocByShortName.ContainsKey($s.internal_id)) {
            $dn = $script:LocByShortName[$s.internal_id].item_name
            if ($dn -and -not $script:StatsByDisplayName.ContainsKey($dn)) {
                $script:StatsByDisplayName[$dn] = $s
            }
        }
    }
    Write-Host "[stats] loaded $($script:StatsByShortName.Count) Icon_Info stat records ($($script:StatsByDisplayName.Count) resolvable by display name)"
}

# ---------------------------------------------------------------------------
# Loot tables: per-biome loot rate sets (Chinese category -> spawn rate) plus crate-prefab
# -> rate-set mappings. Loaded here so both biome pages and item pages can consume it.
# See wiki/extractor/Extract-LootTables.ps1.
# ---------------------------------------------------------------------------
$lootTablesPath = Join-Path $Data 'loot_tables.json'
$script:LootTables = $null
if (Test-Path $lootTablesPath) {
    $script:LootTables = Get-Content $lootTablesPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "[loot] loaded loot tables for $(@($script:LootTables.biomes | Get-Member -MemberType NoteProperty).Count) biomes"
}

# Slug map from the biome-crate PageTitle to the loot_tables.json biome key. Shared by both the
# per-biome loot summary (in the biome generator) and the Loot Crate page generator downstream.
$LootBiomeSlug = @{
    'Desert Loot Crate'   = 'desert'
    'Mossy Loot Crate'    = 'mossy'
    'Mountain Loot Crate' = 'mountain'
    'Snowy Loot Crate'    = 'snowy'
    'Swamp Loot Crate'    = 'swamp'
    'Warzone Loot Crate'  = 'warzone'
}

# Strip the addressable prefix + _Icon suffix to get the tooltip-side short id.
# Iron_Hammer_Icon -> Iron_Hammer; BAT_Wooden_Club_Icon -> Wooden_Club; BF_WB_Campfire -> WB_Campfire
function Get-TooltipShortId {
    param([string] $RawAssetId)
    if (-not $RawAssetId) { return '' }
    # NOTE: order matters. Longer prefixes must come first so BOI_/BFI_/BTI_/BWI_/RWI_ match before
    # the shorter BO_/BF_/BT_/BW_/RW_ variants.
    $prefixesToStrip = @('BFI_', 'BF_', 'BOI_', 'BO_', 'BTI_', 'BT_', 'BWI_', 'BW_',
                         'RII_', 'FWI_', 'AXE_', 'DAG_', 'SPEAR_', 'BIG_', 'TOOL_', 'RWI_', 'RW_',
                         'EQI_', 'VPI_', 'BHC_', 'BAT_', 'BLADE_')
    $s = $RawAssetId
    foreach ($p in $prefixesToStrip) { if ($s.StartsWith($p)) { $s = $s.Substring($p.Length); break } }
    if ($s.EndsWith('_Icon'))   { $s = $s.Substring(0, $s.Length - 5) }
    if ($s.EndsWith('_Icon_1')) { $s = $s.Substring(0, $s.Length - 7) }
    return $s
}

# Get the localization entry for a raw addressable id, with fallback to tier-variant tooltips.
function Get-LocEntry {
    param([string] $RawAssetId)
    if (-not $RawAssetId) { return $null }
    $short = Get-TooltipShortId $RawAssetId
    if ($script:LocByShortName.ContainsKey($short)) { return $script:LocByShortName[$short] }
    # Tier variant? Strip prefix to get the material tail
    if ($short -match '^[1-7]_[1-9]_') {
        $tail = $short -replace '^[1-7]_[1-9]_', ''
        $shapeTokens = @(
            'Block_Small_1\.2_', 'Block_Small_', 'Block_1\.4_', 'Block_',
            'Cuboid_', 'Half_Cylinder_L_', 'Half_Cylinder_M_', 'Half_Cylinder_S_',
            'Pyramid_Squat_', 'Pyramid_Tall_',
            'Steps_Curved_', 'Steps_',
            'Triangle_Small_1\.4_', 'Triangle_Small_', 'Triangle_1\.4_',
            'Triangle_CornerS_', 'Triangle_Corner_', 'Triangle_'
        )
        foreach ($tok in $shapeTokens) { $tail = $tail -replace "^$tok", '' }
        if ($script:LocByShortName.ContainsKey($tail)) { return $script:LocByShortName[$tail] }
        if ($script:LocByBaseSuffix.ContainsKey($tail)) { return $script:LocByBaseSuffix[$tail] }
        return $null
    }
    # Bare material (Damaged_Planks) that has no direct tooltip but tier variants do
    if ($script:LocByBaseSuffix.ContainsKey($short)) { return $script:LocByBaseSuffix[$short] }
    return $null
}

# Return the English display name for a raw addressable id, or $null if no localization.
function Get-DisplayName {
    param([string] $RawAssetId)
    $entry = Get-LocEntry $RawAssetId
    if ($entry -and $entry.item_name) { return $entry.item_name }
    return $null
}

# Return the English item_type for a raw addressable id, or $null.
function Get-DisplayType {
    param([string] $RawAssetId)
    $entry = Get-LocEntry $RawAssetId
    if ($entry -and $entry.item_type) { return $entry.item_type }
    return $null
}

# Return the Icon_Info stats record for a raw addressable id, or $null.
# Lookup uses the tooltip short id (Iron_Hammer, WB_Anvil, Ore_Iron, Wood_Black).
function Get-ItemStats {
    param([string] $RawAssetId)
    if (-not $RawAssetId) { return $null }
    $short = Get-TooltipShortId $RawAssetId
    if (-not $short) { return $null }
    if ($script:StatsByShortName.ContainsKey($short)) {
        return $script:StatsByShortName[$short]
    }
    return $null
}

# Same lookup as Get-ItemStats but keyed by localized display name (for atomic-materials pages
# that reach the generator with a title but no raw asset id).
function Get-ItemStatsByTitle {
    param([string] $Title)
    if (-not $Title) { return $null }
    if ($script:StatsByDisplayName.ContainsKey($Title)) { return $script:StatsByDisplayName[$Title] }
    # Fallback: try the naive underscore form as an internal id (Wood -> "Wood", Steel_Ingot -> "Steel Ingot" reversal)
    $candidate = $Title -replace ' ', '_'
    if ($script:StatsByShortName.ContainsKey($candidate)) { return $script:StatsByShortName[$candidate] }
    return $null
}

# Emit stat fields for an infobox given the raw asset id. Returns a list of `| key = value` lines
# ready to concat with the standard fields. Only emits fields with meaningful values (non-zero
# where zero means "not applicable"). InfoboxType steers weapon-specific vs generic fields.
function Build-StatsFields {
    param([string] $RawAssetId, [string] $InfoboxType = 'item', [PSCustomObject] $Stats = $null)
    $out = New-Object System.Collections.Generic.List[string]
    $s = if ($Stats) { $Stats } else { Get-ItemStats $RawAssetId }
    if (-not $s) { return $out }

    # Weapon stats (only render when the value is meaningful)
    if ($s._baseDamage        -gt 0) { $out.Add("| damage         = $($s._baseDamage)") }
    if ($s._headShotFactor    -gt 0) { $out.Add("| headshot       = $([math]::Round($s._headShotFactor, 2))x") }
    if ($s._baseHitDownProb   -gt 0) { $out.Add("| hitdown        = $([int]([math]::Round($s._baseHitDownProb * 100)))%") }
    if ($s._baseBladeHitProb  -gt 0) { $out.Add("| bleed          = $([int]([math]::Round($s._baseBladeHitProb * 100)))%") }
    if ($s._blockDurability   -gt 0) { $out.Add("| block          = $($s._blockDurability)") }

    # Ranged extras
    if ($s._maxMagCount       -gt 0) { $out.Add("| magazine       = $($s._maxMagCount)") }
    if ($s._noiseDistance     -gt 0) { $out.Add("| noise          = $($s._noiseDistance)m") }
    if ($s._fireRate -and ($s._fireRate -ne '' -and $s._fireRate -ne 0)) {
        $out.Add("| firerate       = $($s._fireRate)")
    }
    if ($s._ammoType -and $s._ammoType -is [string] -and $s._ammoType.Trim() -ne '') {
        $out.Add("| ammo           = $($s._ammoType)")
    }

    # Durability + repair (weapons/tools/armor)
    if ($s._BaseMaxDurability -gt 0) {
        $out.Add("| durability     = $($s._BaseMaxDurability)")
        if ($s._Can_Repair -eq 1) { $out.Add("| repairable     = Yes") }
    }

    # Wear per swing/shot (negative in the raw data; show as positive drain)
    if ($s._DuraCostPerAttack -and $s._DuraCostPerAttack -ne 0 -and $s._BaseMaxDurability -gt 0) {
        $drain = [math]::Abs([double]$s._DuraCostPerAttack)
        $out.Add("| wear           = $drain per use")
    }

    # Stacking + vendor value (generic item stats)
    if ($s.MaxStack -and $s.MaxStack -gt 1)      { $out.Add("| stack          = $($s.MaxStack)") }
    if ($s._BuySellValue -and $s._BuySellValue -gt 0) { $out.Add("| value          = $($s._BuySellValue)") }

    # Tag (only render when non-empty and not a bare weapon tag already implicit in `type`)
    if ($s._Tag -and $s._Tag -ne '' -and $s._Tag -notin @('MeleeWeapon','RangedWeapon')) {
        $out.Add("| tag            = $($s._Tag)")
    }

    return $out
}

# Authoritative prefab -> display title map, resolved from localization with legacy fallback.
$PrefabToTitleMap = @{}
$WorkbenchPrefabMap = @{}  # title -> prefab, kept for compatibility with existing code
foreach ($prefab in $WorkbenchLegacyTitles.Keys) {
    if ($prefab -eq 'HandMade') {
        $PrefabToTitleMap[$prefab] = 'HandMade'
        $WorkbenchPrefabMap['HandMade'] = $null
        continue
    }
    $displayName = Get-DisplayName $prefab
    $title = if ($displayName) { $displayName } else { $WorkbenchLegacyTitles[$prefab] }
    $PrefabToTitleMap[$prefab] = $title
    $WorkbenchPrefabMap[$title] = $prefab
}
$WorkbenchPrefabMap['HandMade'] = $null  # in case HandMade wasn't added

# ---------------------------------------------------------------------------
# Recipe item normalization + cross-ref index
# ---------------------------------------------------------------------------

# Regex matching the tier-variant shape pattern: "<tier>_<sub>_<shape...>_<material>"
# Examples: "1_1_Block_Damaged_Planks", "3_5_Half_Cylinder_L_Titanium_Mesh_Concrete"
$TierVariantRegex = '^[1-7]_[1-9]_'

# Normalize a raw recipe asset id into a wiki page title. Prefers the English display name from
# localization; falls back to structural derivation:
#   - Prefix / suffix stripping (via Prettify-AssetId)
#   - Ore rotation: "Ore Iron" -> "Iron Ore"
#   - Tier-variant collapse: "1_1_Block_Damaged_Planks" -> "Damaged Planks"
function Normalize-RecipeItemName {
    param([string] $RawId)
    if (-not $RawId) { return '' }
    # First try localization -- this gives us the game's canonical player-facing name.
    $displayName = Get-DisplayName $RawId
    if ($displayName) { return $displayName }
    if ($RawId -match $TierVariantRegex) {
        # Extract material name: strip leading "<tier>_<sub>_" then strip known shape tokens.
        $rest = ($RawId -replace $TierVariantRegex, '')
        $shapeTokens = @(
            'Block_Small_1\.2_', 'Block_Small_', 'Block_1\.4_', 'Block_',
            'Cuboid_', 'Half_Cylinder_L_', 'Half_Cylinder_M_', 'Half_Cylinder_S_',
            'Pyramid_Squat_', 'Pyramid_Tall_',
            'Steps_Curved_', 'Steps_',
            'Triangle_Small_1\.4_', 'Triangle_Small_', 'Triangle_1\.4_',
            'Triangle_CornerS_', 'Triangle_Corner_', 'Triangle_'
        )
        foreach ($tok in $shapeTokens) {
            $rest = $rest -replace "^$tok", ''
        }
        # Strip trailing _Icon that tier-variant names sometimes carry.
        if ($rest.EndsWith('_Icon')) { $rest = $rest.Substring(0, $rest.Length - 5) }
        return ($rest -replace '_', ' ')
    }
    $s = Prettify-AssetId $RawId
    if ($s -match '^Ore (.+)$') { return "$($Matches[1]) Ore" }
    return $s
}

# Build the cross-reference index over every recipe: for each normalized item name,
# which workbenches produce it (with input recipe details) and which recipes consume it.
$script:ItemProducedBy = @{}   # itemTitle -> list of PSCustomObject{ workbenchTitle, output_qty, craft_seconds, inputs=@(name,qty) }
$script:ItemUsedIn    = @{}    # itemTitle -> list of PSCustomObject{ workbenchTitle, output_name, output_qty, need_count }

# Placeholder / debug items surfaced by the recipe graph that shouldn't be indexed as real items.
$script:PlaceholderTitles = @{ 'WIP' = $true; 'Recipe Sack' = $true; 'Burning' = $true; 'BurningFire' = $true }

if ($recipesData) {
    foreach ($wb in $recipesData.workbenches) {
        $wbTitle = if ($PrefabToTitleMap.ContainsKey($wb.workbench_prefab)) { $PrefabToTitleMap[$wb.workbench_prefab] } else { $wb.workbench_prefab }
        foreach ($rec in $wb.recipes) {
            $outTitle = Normalize-RecipeItemName $rec.output_name
            if (-not $outTitle) { continue }
            if ($script:PlaceholderTitles.ContainsKey($outTitle)) { continue }
            $inputList = @($rec.inputs | ForEach-Object {
                [PSCustomObject]@{ name = (Normalize-RecipeItemName $_.name); qty = $_.need_count }
            } | Where-Object { -not $script:PlaceholderTitles.ContainsKey($_.name) })
            if (-not $script:ItemProducedBy.ContainsKey($outTitle)) {
                $script:ItemProducedBy[$outTitle] = New-Object System.Collections.Generic.List[PSCustomObject]
            }
            $script:ItemProducedBy[$outTitle].Add([PSCustomObject]@{
                workbench = $wbTitle
                output_qty = $rec.craft_num
                craft_seconds = $rec.craft_seconds
                inputs = $inputList
            })

            foreach ($m in $rec.inputs) {
                $inTitle = Normalize-RecipeItemName $m.name
                if (-not $inTitle) { continue }
                if ($script:PlaceholderTitles.ContainsKey($inTitle)) { continue }
                if (-not $script:ItemUsedIn.ContainsKey($inTitle)) {
                    $script:ItemUsedIn[$inTitle] = New-Object System.Collections.Generic.List[PSCustomObject]
                }
                $script:ItemUsedIn[$inTitle].Add([PSCustomObject]@{
                    workbench = $wbTitle
                    output_name = $outTitle
                    output_qty = $rec.craft_num
                    need_count = $m.need_count
                })
            }
        }
    }
    Write-Host "[recipes] indexed cross-refs: $($script:ItemProducedBy.Count) produced items, $($script:ItemUsedIn.Count) consumed items"
}

# Consolidate a list of recipes that share the same (output, inputs signature) but differ only
# in quantities (typical when a single "recipe" spans many tier-shape build variants). For each
# group, aggregates each input quantity into a min-max range.
#
# Input: array of PSCustomObjects with fields:
#   output_name (or output_title), craft_num (or output_qty), craft_seconds,
#   inputs: array of { name, need_count (or qty) }
#
# Output: array of consolidated PSCustomObjects:
#   { output_name, output_qty, craft_seconds, variant_count, inputs: [{ name, qty_str, min, max }] }
function Consolidate-Recipes {
    param([array] $Recipes, [string] $OutputField = 'output_name', [string] $OutputQtyField = 'craft_num', [string] $InputQtyField = 'need_count')
    $groups = [ordered]@{}
    foreach ($rec in $Recipes) {
        $outName = $rec.$OutputField
        $outQty  = $rec.$OutputQtyField
        $secs    = $rec.craft_seconds
        $inputSig = (($rec.inputs | ForEach-Object { $_.name } | Sort-Object) -join '|')
        # Signature includes output and inputs but NOT the input quantities (that's the varying axis).
        $key = "$outName|$outQty|$secs|$inputSig"
        if (-not $groups.Contains($key)) {
            $groups[$key] = [PSCustomObject]@{
                output_name   = $outName
                output_qty    = $outQty
                craft_seconds = $secs
                variant_count = 0
                # Map input-name -> list of quantities across variants
                input_qtys    = @{}
                # Preserve first-seen order of input names for stable rendering
                input_order   = @()
            }
        }
        $g = $groups[$key]
        $g.variant_count++
        foreach ($m in $rec.inputs) {
            if (-not $g.input_qtys.ContainsKey($m.name)) {
                $g.input_qtys[$m.name] = New-Object System.Collections.Generic.List[int]
                $g.input_order += $m.name
            }
            $g.input_qtys[$m.name].Add([int]$m.$InputQtyField)
        }
    }
    $result = New-Object System.Collections.Generic.List[PSCustomObject]
    foreach ($g in $groups.Values) {
        $inputList = New-Object System.Collections.Generic.List[PSCustomObject]
        foreach ($iname in $g.input_order) {
            $qtys = $g.input_qtys[$iname]
            $mn = ($qtys | Measure-Object -Minimum).Minimum
            $mx = ($qtys | Measure-Object -Maximum).Maximum
            $qs = if ($mn -eq $mx) { "$mn" } else { "$mn-$mx" }
            $inputList.Add([PSCustomObject]@{ name = $iname; qty_str = $qs; min = $mn; max = $mx })
        }
        $result.Add([PSCustomObject]@{
            output_name   = $g.output_name
            output_qty    = $g.output_qty
            craft_seconds = $g.craft_seconds
            variant_count = $g.variant_count
            inputs        = $inputList
        })
    }
    return $result
}

# Renders "Crafted at" + "Used in" cross-ref sections for a given item title.
# Returns wiki text (possibly empty) suitable for injecting into an item stub.
function Build-CrossRefSections {
    param([string] $ItemTitle)
    $out = New-Object System.Text.StringBuilder
    # Force @(...) wrap: PowerShell function returns unroll Generic.List into individual objects,
    # so a single-item list would arrive here as a bare PSCustomObject with no meaningful .Count.
    # The @($x) coercion normalizes into an array either way.
    # PowerShell assignment unrolls 1-element wrappers, so `$x = if {...} @(single) else {@()}` yields
    # a bare object when there's one entry. Assign the Generic.List directly (or $null), then check
    # .Count separately - Generic.List's Count is authoritative.
    $produced = $null
    if ($script:ItemProducedBy.ContainsKey($ItemTitle)) { $produced = $script:ItemProducedBy[$ItemTitle] }
    $used = $null
    if ($script:ItemUsedIn.ContainsKey($ItemTitle))     { $used     = $script:ItemUsedIn[$ItemTitle] }
    $producedCount = if ($produced) { $produced.Count } else { 0 }
    $usedCount     = if ($used)     { $used.Count }     else { 0 }

    if ($producedCount -gt 0) {
        # Consolidate "Crafted at" rows: same workbench + same inputs = same recipe, aggregate qty ranges.
        # Adapt to the ItemProducedBy shape (workbench field, output_qty field, inputs with qty field).
        $produced2 = $produced | ForEach-Object {
            [PSCustomObject]@{
                output_name   = $_.workbench   # Treat workbench as the "key" so consolidation groups by workbench + inputs
                craft_num     = $_.output_qty
                craft_seconds = $_.craft_seconds
                inputs        = @($_.inputs | ForEach-Object { [PSCustomObject]@{ name = $_.name; need_count = $_.qty } })
            }
        }
        $consolidated = Consolidate-Recipes -Recipes @($produced2)
        [void]$out.AppendLine("== Crafted at ==")
        [void]$out.AppendLine("")
        [void]$out.AppendLine("{| class=`"wikitable sortable`" style=`"font-size:0.9em;`"")
        [void]$out.AppendLine("! Workbench !! Qty !! Time (s) !! Inputs !! Variants")
        foreach ($c in $consolidated) {
            $inList = ($c.inputs | ForEach-Object { "[[$($_.name)]] x$($_.qty_str)" }) -join ', '
            if (-not $inList) { $inList = 'None' }
            $variantsCell = if ($c.variant_count -gt 1) { "$($c.variant_count) shapes" } else { '1' }
            [void]$out.AppendLine("|-")
            [void]$out.AppendLine("| [[$($c.output_name)]] || $($c.output_qty) || $($c.craft_seconds) || $inList || $variantsCell")
        }
        [void]$out.AppendLine("|}")
        [void]$out.AppendLine("")
    }

    if ($usedCount -gt 0) {
        # Consolidate "Used in": group by (output_name, workbench). Values collapse to a qty range.
        $groups = [ordered]@{}
        foreach ($u in $used) {
            $key = "$($u.output_name)|$($u.workbench)|$($u.output_qty)"
            if (-not $groups.Contains($key)) {
                $groups[$key] = [PSCustomObject]@{
                    output_name = $u.output_name
                    workbench   = $u.workbench
                    output_qty  = $u.output_qty
                    variant_count = 0
                    counts      = New-Object System.Collections.Generic.List[int]
                }
            }
            $groups[$key].variant_count++
            $groups[$key].counts.Add([int]$u.need_count)
        }
        [void]$out.AppendLine("== Used in ==")
        [void]$out.AppendLine("")
        [void]$out.AppendLine("{| class=`"wikitable sortable`" style=`"font-size:0.9em;`"")
        [void]$out.AppendLine("! Recipe !! Workbench !! Consumes !! Variants")
        foreach ($g in ($groups.Values | Sort-Object output_name, workbench)) {
            $mn = ($g.counts | Measure-Object -Minimum).Minimum
            $mx = ($g.counts | Measure-Object -Maximum).Maximum
            $cell = if ($mn -eq $mx) { "$mn" } else { "$mn-$mx" }
            $variantsCell = if ($g.variant_count -gt 1) { "$($g.variant_count) shapes" } else { '1' }
            [void]$out.AppendLine("|-")
            [void]$out.AppendLine("| [[$($g.output_name)]] x$($g.output_qty) || [[$($g.workbench)]] || $cell || $variantsCell")
        }
        [void]$out.AppendLine("|}")
        [void]$out.AppendLine("")
    }
    return $out.ToString()
}

# ---------------------------------------------------------------------------
# Auto-drafted description prose (based on recipe graph + category)
# ---------------------------------------------------------------------------

# Turns "Anvil Workbench" into "the [[Anvil Workbench]]".
function LinkWB { param([string] $Wb) if ($Wb) { "the [[$Wb]]" } else { '' } }

# Describes the "role" of an item in one adjective/noun phrase based on its category.
# Handles both the legacy internal categories (Melee Weapons, Building Material) and the localized
# in-game item_type values (Melee weapon, Building mats, Ore, Rock, Ammo, Firearms, etc.).
function Category-Phrase {
    param([string] $Cat)
    if (-not $Cat) { return 'item' }
    switch -Regex ($Cat) {
        'Refined Metal|Ingot'               { return 'refined metal ingot' }
        'Building Material|Building mats'   { return 'building material' }
        'Salvage'                            { return 'salvaged component' }
        'Weapon Component|Firearm Kit'      { return 'weapon assembly component' }
        'Ammunition Component|Ammo materials' { return 'ammunition component' }
        'Electronic Component|Elec\. mats|Elec mats' { return 'electronic component' }
        'Equipment|Armor'                    { return 'wearable equipment piece' }
        'Vehicle Part'                       { return 'vehicle assembly part' }
        'Medical|^Med$'                      { return 'medical consumable' }
        'Raw Material|Resources'             { return 'raw material' }
        '^Ore$'                              { return 'mineable ore' }
        'Rock'                               { return 'mineable rock' }
        '^Food$'                             { return 'edible food item' }
        'Water'                              { return 'drinkable water source' }
        'Melee weapons?|Blunt|Blade'        { return 'melee weapon' }
        'Ranged weapons?|Firearms?'         { return 'ranged weapon' }
        '^Ammo$|Ammunition|^Arrow$|Buckshot' { return 'ammunition round' }
        'Sight|Scope|Weapon Attachments'    { return 'weapon optic or attachment' }
        'Repair'                             { return 'repair kit or salvage part' }
        'Tool'                               { return 'crafted tool' }
        'Facility'                           { return 'placeable workbench' }
        'Component'                          { return 'crafted component' }
        default                              { return 'item' }
    }
}

# Compose an auto-drafted description for an item, from its recipe context and category.
# Returns a paragraph of wiki-text with links wherever possible.
function Build-ItemDescription {
    param([string] $Title, [string] $Category)

    $catPhrase = Category-Phrase $Category
    # Grammar: pick "a" vs "an" based on the first sound of the category phrase.
    $article = if ($catPhrase -match '^[aeiouAEIOU]') { 'an' } else { 'a' }

    # Look up producers + consumers from the cross-ref index
    $producers = $null
    if ($script:ItemProducedBy.ContainsKey($Title)) { $producers = $script:ItemProducedBy[$Title] }
    $consumers = $null
    if ($script:ItemUsedIn.ContainsKey($Title)) { $consumers = $script:ItemUsedIn[$Title] }

    $producerCount = if ($producers) { $producers.Count } else { 0 }
    $consumerCount = if ($consumers) { $consumers.Count } else { 0 }

    $lines = New-Object System.Collections.Generic.List[string]

    # Opening sentence
    if ($producerCount -gt 0) {
        $wbNames = @($producers | ForEach-Object { $_.workbench } | Select-Object -Unique | Sort-Object)
        $wbList = if ($wbNames.Count -eq 1) { LinkWB $wbNames[0] }
                  elseif ($wbNames.Count -eq 2) { "$(LinkWB $wbNames[0]) and $(LinkWB $wbNames[1])" }
                  else { ($wbNames[0..($wbNames.Count-2)] | ForEach-Object { LinkWB $_ }) -join ', ' + ", and $(LinkWB $wbNames[-1])" }
        $lines.Add("'''$Title''' is $article $catPhrase in ''[[Human Host]]'', crafted at $wbList.")
    } else {
        # No producer - probably a raw material, ore, foraged food, looted item, base tool, etc.
        $source = switch -Regex ($Category) {
            'Ore'           { 'obtained by [[Mining|mining]] terrain deposits' }
            'Food'          { 'looted from containers and zombie corpses, or foraged in the wild' }
            'Water'         { 'looted from containers and zombie corpses' }
            'Raw Material'  { 'harvested from the world (trees, rocks, salvage)' }
            'Melee Weapons' { 'crafted via the inventory [[HandMade]] menu or salvaged' }
            'Ranged Weapons'{ 'assembled at the [[Gun Workbench]] or salvaged' }
            'Ammunition'    { 'crafted at the [[Gun Workbench]] or salvaged' }
            default         { 'sourced from the world (loot, salvage, or base crafting)' }
        }
        $lines.Add("'''$Title''' is $article $catPhrase in ''[[Human Host]]'', $source.")
    }

    # Downstream usage
    if ($consumerCount -gt 0) {
        # Group consumer recipes by workbench to summarize where this feeds
        $consumerWBs = @($consumers | ForEach-Object { $_.workbench } | Select-Object -Unique | Sort-Object)
        $topConsumers = @($consumers | Group-Object output_name | Sort-Object Count -Descending | Select-Object -First 6 -ExpandProperty Name)
        $wbSummary = if ($consumerWBs.Count -eq 1) { LinkWB $consumerWBs[0] }
                     elseif ($consumerWBs.Count -eq 2) { "$(LinkWB $consumerWBs[0]) and $(LinkWB $consumerWBs[1])" }
                     else { "$($consumerWBs.Count) different workbenches" }
        $exampleList = ($topConsumers | ForEach-Object { "[[$_]]" }) -join ', '
        $suffix = if ($topConsumers.Count -lt $consumerCount) { ', among others' } else { '' }
        $lines.Add("It appears as an ingredient in $consumerCount downstream recipes at $wbSummary, including $exampleList$suffix. See the ''Used in'' table for the full consumer list.")
    }

    return ($lines -join ' ')
}

# Compose a workbench description from its recipe list.
function Build-WorkbenchDescription {
    param([string] $Title, [string] $Prefab)
    if (-not $Prefab) {
        # HandMade
        return "'''HandMade''' is the inventory-crafting menu in ''[[Human Host]]'', accessible from anywhere (default hotkey '''C''') with no external workbench required. It handles first-tier essentials: basic tools, weapons, torches, bandages, and workbench-placement kits."
    }
    $wb = $script:recipesData.workbenches | Where-Object workbench_prefab -eq $Prefab | Select-Object -First 1
    if (-not $wb -or $wb.recipes.Count -eq 0) {
        return "'''$Title''' is one of the workbench types in ''[[Human Host]]''. No recipes have been extracted for it yet."
    }
    $rawCount = $wb.recipes.Count
    # Consolidate to get unique-recipe count and category diversity of outputs
    $normalized = $wb.recipes | ForEach-Object {
        [PSCustomObject]@{
            output_name   = Normalize-RecipeItemName $_.output_name
            craft_num     = $_.craft_num
            craft_seconds = $_.craft_seconds
            inputs        = @($_.inputs | ForEach-Object { [PSCustomObject]@{ name = Normalize-RecipeItemName $_.name; need_count = $_.need_count } })
        }
    }
    $consolidated = @(Consolidate-Recipes -Recipes @($normalized))
    $uniqueCount = $consolidated.Count
    $outputs = @($consolidated | ForEach-Object { $_.output_name } | Select-Object -Unique | Sort-Object)
    $topOutputs = ($outputs | Select-Object -First 8 | ForEach-Object { "[[$_]]" }) -join ', '
    $extra = if ($outputs.Count -gt 8) { ", plus $($outputs.Count - 8) more" } else { '' }

    # Character sentence based on the workbench's role. Keyed by prefab so localization renames
    # don't break the mapping.
    $role = switch ($Prefab) {
        'WB_Anvil'          { 'forges metal weapons, armor, tools, and mechanical parts from smelted ingots.' }
        'WB_Biochemical'    { 'processes chemistry: gunpowder, explosives, medicines, and reagents.' }
        'WB_Campfire'       { 'is the entry-tier utility station: charcoal, glue, and early build components.' }
        'WB_Carpentry'      { 'produces wooden weapons, bows, arrows, and plank-based building pieces.' }
        'WB_Concrete_Mixer' { 'mixes concrete and cement compounds for reinforced structures.' }
        'WB_Cutting'        { 'shapes raw materials into the many building-piece variants (planks, bricks, cement, iron, alloy, glass, stone). Each recipe covers up to 18 shape variants of the same material.' }
        'WB_Electronic'     { 'assembles circuits, sensors, generators, and vehicle electronics.' }
        'WB_Furnace'        { 'smelts ore into ingots and reduces raw materials into refined building blocks.' }
        'WB_Gunsmith'       { 'assembles firearms, ammunition, and firearm attachments.' }
        'WB_Mechanical'     { 'builds vehicle parts and mechanical assemblies.' }
        default             { 'is a workbench in ''[[Human Host]]''.' }
    }

    return "The '''$Title''' $role It defines $rawCount raw recipe entries ($uniqueCount unique after consolidating shape variants), producing outputs including $topOutputs$extra."
}

function Build-RecipeTable {
    param([string] $WorkbenchPrefab)
    if (-not $recipesData -or -not $WorkbenchPrefab) { return '' }
    $wb = $recipesData.workbenches | Where-Object workbench_prefab -eq $WorkbenchPrefab | Select-Object -First 1
    if (-not $wb) { return '' }
    if ($wb.recipes.Count -eq 0) { return '' }

    # Normalize recipe names then consolidate duplicate shape-variants into range rows.
    $normalized = $wb.recipes | ForEach-Object {
        [PSCustomObject]@{
            output_name   = Normalize-RecipeItemName $_.output_name
            craft_num     = $_.craft_num
            craft_seconds = $_.craft_seconds
            inputs        = @($_.inputs | ForEach-Object { [PSCustomObject]@{ name = Normalize-RecipeItemName $_.name; need_count = $_.need_count } })
        }
    }
    $consolidated = Consolidate-Recipes -Recipes @($normalized)

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("{| class=`"wikitable sortable`" style=`"font-size:0.9em;`"")
    [void]$sb.AppendLine("|+ Recipes ($($consolidated.Count) unique, from $($wb.recipes.Count) raw entries in ``$WorkbenchPrefab.prefab``)")
    [void]$sb.AppendLine("! Output !! Qty !! Time (s) !! Inputs !! Variants")
    foreach ($c in $consolidated) {
        $inputs = ($c.inputs | ForEach-Object { "[[$($_.name)]] x$($_.qty_str)" }) -join ', '
        if (-not $inputs) { $inputs = 'None' }
        $variantsCell = if ($c.variant_count -gt 1) { "$($c.variant_count) shapes" } else { '1' }
        [void]$sb.AppendLine("|-")
        [void]$sb.AppendLine("| [[$($c.output_name)]] || $($c.output_qty) || $($c.craft_seconds) || $inputs || $variantsCell")
    }
    [void]$sb.AppendLine("|}")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("''Where an ingredient shows a range (e.g. Wood x1-14), the recipe covers multiple tier-shape build variants. The 'Variants' column shows how many shapes share this recipe signature.''")
    return $sb.ToString()
}

# =============================================================================
# Biomes
# =============================================================================
Write-Host "Biomes..." -ForegroundColor Cyan
$biomes = Read-JsonArray 'biomes'
$locations = Read-JsonArray 'locations'
$crates = Read-JsonArray 'loot_crates'
$vehicles = Read-JsonArray 'vehicle_bundles'

foreach ($b in $biomes) {
    $locHere    = $locations | Where-Object { $_.Biome -eq $b.PageTitle } | ForEach-Object { "[[$($_.PageTitle)]]" }
    $crateHere  = $crates    | Where-Object { $_.Biome -eq $b.PageTitle } | ForEach-Object { "[[$($_.PageTitle)]]" }
    $vehHere    = $vehicles  | Where-Object { $_.Biome -eq $b.PageTitle } | ForEach-Object { "[[$($_.PageTitle)]]" }

    # Fill empty cells with a text value (accessibility: screenreaders lose position on empty cells).
    $poiList    = if ($locHere)   { $locHere    -join ', ' } else { 'None' }
    $crateList  = if ($crateHere) { $crateHere  -join ', ' } else { 'None' }
    $vehList    = if ($vehHere)   { $vehHere    -join ', ' } else { 'None' }

    # Loot summary for this biome: pull the top-3 categories from each rate set so a player
    # can see at a glance what to expect. Requires loot_tables.json (via $script:LootTables).
    $lootSummary = ''
    $matchingCrate = $crates | Where-Object { $_.Biome -eq $b.PageTitle } | Select-Object -First 1
    if ($script:LootTables -and $matchingCrate) {
        $slug = $LootBiomeSlug[$matchingCrate.PageTitle]
        if ($slug -and $script:LootTables.biomes.PSObject.Properties[$slug]) {
            $biomeLoot = $script:LootTables.biomes.$slug
            $sb = New-Object System.Text.StringBuilder
            [void]$sb.AppendLine('== Loot at a glance ==')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine("Crates in this biome roll from the loot rate sets below. Only the top few categories per set are shown here; the full breakdown and every crate variant lives on [[$($matchingCrate.PageTitle)]].")
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('{| class="wikitable" style="font-size:0.9em;"')
            [void]$sb.AppendLine('! Rate set !! Top categories')
            foreach ($rsProp in ($biomeLoot.rate_sets.PSObject.Properties | Sort-Object Name)) {
                $rs = $rsProp.Value
                $top = $rs | Sort-Object -Property @{Expression={[double]$_.rate};Descending=$true} | Select-Object -First 3
                $desc = ($top | ForEach-Object {
                    $en = $script:LootTables.tag_translations.($_.tag).English
                    $label = if ($en) { $en } else { $_.tag }
                    "$label ($([math]::Round([double]$_.rate * 100, 2))%)"
                }) -join ', '
                [void]$sb.AppendLine('|-')
                [void]$sb.AppendLine("| $($rsProp.Name) || $desc")
            }
            [void]$sb.AppendLine('|}')
            [void]$sb.AppendLine('')
            $lootSummary = $sb.ToString()
        }
    }

    # Drop the stub badge when we have any real content to show: loot summary, POI list, vehicle
    # list, or a loot crate for the biome. Biomes without any of the above stay stub-badged.
    $biomeHasContent = ($lootSummary.Trim().Length -gt 0) -or
                       ($poiList -ne 'None') -or
                       ($vehList -ne 'None') -or
                       ($crateList -ne 'None')
    $biomeStubTag = if ($biomeHasContent) { '' } else { "{{Stub}}`n" }

    $content = @"
$Marker
{{Nav}}
$biomeStubTag
{{Infobox biome
| name       = $($b.PageTitle)
| terrain    = $($b.BundleFile -replace '_assets_all.*$','')
| climate    = $($b.Climate)
| threat     = $($b.Threat)
| pois       = $poiList
| vehicles   = $vehList
}}

The '''$($b.PageTitle)''' is a tier-$($b.Tier) biome in ''[[Human Host]]''. Climate: $($b.Climate). Threat level: $($b.Threat).

== Terrain ==

Terrain bundle: <code>$($b.BundleFile -replace '_assets_all.*$','')</code>. See [[Biomes]] for the full biome list.

== Points of interest ==

$($poiList)

== Loot crates ==

$($crateList)

$lootSummary== Vehicles ==

$($vehList)

== See also ==

* [[Biomes]]: biome overview
* [[Locations]]: full POI list

[[Category:Biomes]]
"@
    Write-Stub "biomes/$(Slugify $b.PageTitle).mediawiki" $content
}

# =============================================================================
# Locations (POIs)
# =============================================================================
Write-Host "Locations..." -ForegroundColor Cyan

foreach ($l in $locations) {
    $biomeLink = if ([string]::IsNullOrEmpty($l.Biome)) { 'Not applicable' } else { "[[$($l.Biome)]]" }
    $content = @"
$Marker
{{Nav}}
{{Stub}}

The '''$($l.PageTitle)''' is a point of interest found in the $biomeLink biome of ''[[Human Host]]''.

== Overview ==

This POI is a pre-built structure containing higher-density loot than surrounding terrain, typically defended by an elevated concentration of zombies. See [[Locations]] for the full POI list.

== Location ==

Native biome: $biomeLink

Asset bundle: <code>$($l.BundleFile -replace '_assets_all.*$','')</code>

== Loot ==

TBD. Common categories found in this POI type:
* Salvageable equipment
* Ammunition
* Food and water

== Strategy ==

TBD.

== See also ==

* [[Locations]]: full POI list
* ${biomeLink}: the biome this POI spawns in

[[Category:Locations]]
"@
    Write-Stub "locations/$(Slugify $l.PageTitle).mediawiki" $content
}

# =============================================================================
# Loot crates
# =============================================================================
Write-Host "Loot crates..." -ForegroundColor Cyan

# Render one rate set as a wiki table. `rates` is the array of {tag, rate, stack} rows; the
# Chinese tag is translated to English via the extractor's tag_translations map.
function Build-LootRateTable {
    param($Rates, $Translations, [string] $Name)
    if (-not $Rates -or $Rates.Count -eq 0) { return '' }
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add("=== $Name ===")
    [void]$lines.Add('')
    [void]$lines.Add('{| class="wikitable sortable" style="font-size:0.9em;"')
    [void]$lines.Add('! Category !! Spawn rate !! Stack factor')
    foreach ($r in ($Rates | Sort-Object { -[double]$_.rate })) {
        $tr = $Translations.($r.tag)
        $enName = if ($tr -and $tr.English) { $tr.English } else { $r.tag }
        $rate = "$([math]::Round([double]$r.rate * 100, 3))%"
        $stack = if ([double]$r.stack -eq 1) { '1' } else { "$([math]::Round([double]$r.stack, 2))x" }
        [void]$lines.Add('|-')
        [void]$lines.Add("| $enName <small>($($r.tag))</small> || $rate || $stack")
    }
    [void]$lines.Add('|}')
    [void]$lines.Add('')
    return ($lines -join "`n")
}

# Reverse index: given an item's English `_Tag`, what Chinese loot-category tags can spawn it?
# The tag_translations map declares each category's set of English tags; here we invert that.
$script:LootTagsByItemTag = @{}
if ($script:LootTables) {
    foreach ($chTagProp in $script:LootTables.tag_translations.PSObject.Properties) {
        $chTag = $chTagProp.Name
        $entry = $chTagProp.Value
        foreach ($enTag in ($entry.Tags | Where-Object { $_ })) {
            if (-not $script:LootTagsByItemTag.ContainsKey($enTag)) {
                $script:LootTagsByItemTag[$enTag] = New-Object System.Collections.Generic.List[string]
            }
            [void]$script:LootTagsByItemTag[$enTag].Add($chTag)
        }
    }
    Write-Host "[loot] indexed $($script:LootTagsByItemTag.Count) English tag(s) with a loot-category mapping"
}

# Human-readable biome names keyed by the loot-table slug (mirrors LootBiomeSlug in reverse).
$script:LootSlugToBiome = @{
    'desert'   = 'Desert'
    'mossy'    = 'Mossy Forest'
    'mountain' = 'Mountain Forest'
    'snowy'    = 'Winter Forest'
    'swamp'    = 'Tropical Swamp'
    'warzone'  = 'Warzone'
}

# Emit a "Loot sources" wiki section for the given item. Uses the item's Icon_Info `_Tag` value
# to find matching loot categories, then walks every biome-rate-set pair to list where the item
# can spawn and at what rate. Returns empty string when the item has no matching loot category.
function Build-LootSources {
    param([string] $AssetId, [string] $ItemTitle, [PSCustomObject] $Stats = $null)
    if (-not $script:LootTables) { return '' }
    $stats = if ($Stats) { $Stats } else { Get-ItemStats $AssetId }
    if (-not $stats) { return '' }
    $itemTag = $stats._Tag
    if (-not $itemTag) { return '' }
    if (-not $script:LootTagsByItemTag.ContainsKey($itemTag)) { return '' }
    $chineseTags = @($script:LootTagsByItemTag[$itemTag])
    if ($chineseTags.Count -eq 0) { return '' }

    # For each biome, for each rate set, find any matching category; if any, aggregate crates.
    $rows = New-Object System.Collections.Generic.List[PSCustomObject]
    foreach ($slugProp in $script:LootTables.biomes.PSObject.Properties) {
        $slug = $slugProp.Name
        $biome = $slugProp.Value
        $biomeName = $script:LootSlugToBiome[$slug]
        if (-not $biomeName) { $biomeName = $slug }
        foreach ($rsProp in $biome.rate_sets.PSObject.Properties) {
            $rsName = $rsProp.Name
            $rates = $rsProp.Value
            $matching = @($rates | Where-Object { $chineseTags -contains $_.tag })
            if ($matching.Count -eq 0) { continue }
            # A rate set can match multiple categories (e.g. Ammunition + Gun for a firearm-adjacent item).
            # Sum probabilities into a single per-rate-set spawn rate.
            $totalRate = ($matching | ForEach-Object { [double]$_.rate } | Measure-Object -Sum).Sum
            $crateNames = @($biome.crates.PSObject.Properties | Where-Object { $_.Value -eq $rsName } | ForEach-Object { $_.Name })
            $rows.Add([PSCustomObject]@{
                Biome     = $biomeName
                RateSet   = $rsName
                Rate      = $totalRate
                Crates    = $crateNames
                Categories= ($matching | ForEach-Object {
                    $en = $script:LootTables.tag_translations.($_.tag).English
                    if ($en) { $en } else { $_.tag }
                }) -join ', '
            })
        }
    }
    if ($rows.Count -eq 0) { return '' }

    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('== Loot sources ==')
    [void]$lines.Add('')
    [void]$lines.Add("'''$ItemTitle''' spawns from world loot crates. The table below lists every biome-rate-set combination that can drop the item, the summed category spawn rate for that set, and the crate prefabs that pull from it.")
    [void]$lines.Add('')
    [void]$lines.Add('{| class="wikitable sortable" style="font-size:0.9em;"')
    [void]$lines.Add('! Biome !! Rate set !! Category rate !! Sample crates')
    foreach ($r in ($rows | Sort-Object -Property @{Expression='Biome'}, @{Expression='Rate';Descending=$true})) {
        $pct = "$([math]::Round($r.Rate * 100, 3))%"
        $crateLine = if ($r.Crates.Count -gt 3) { "$($r.Crates[0..2] -join ', '), <small>+$($r.Crates.Count - 3) more</small>" } else { $r.Crates -join ', ' }
        [void]$lines.Add('|-')
        [void]$lines.Add("| [[$($r.Biome)]] || $($r.RateSet) || $pct <small>($($r.Categories))</small> || $crateLine")
    }
    [void]$lines.Add('|}')
    [void]$lines.Add('')
    [void]$lines.Add("''See [[$($rows[0].Biome) Loot Crate]] and the other biome loot pages for the full rate set breakdown.''")
    [void]$lines.Add('')
    return ($lines -join "`n")
}

# $LootBiomeSlug is defined near the top of the script alongside the loot-table load, so both
# the biome-page generator and this crate-page generator can share it.

# ---------------------------------------------------------------------------
# Trap runtime stats (damage, trigger interval, self-damage, subclass-specific fields). Extracted
# from every Trap_Base MonoBehaviour on the trap prefabs. See wiki/extractor/Extract-TrapStats.ps1.
# ---------------------------------------------------------------------------
$trapStatsPath = Join-Path $Data 'trap-stats.json'
$script:TrapStatsById = @{}
if (Test-Path $trapStatsPath) {
    $trapArr = Get-Content $trapStatsPath -Raw | ConvertFrom-Json
    foreach ($t in $trapArr) { $script:TrapStatsById[$t.internal_id] = $t }
    Write-Host "[trap] loaded runtime stats for $($script:TrapStatsById.Count) trap prefabs"
}

# Resolve an asset id (BT_IronSpike_Trap) to a trap-stats entry. The stats records key by prefab
# basename which is the asset id minus the `BT_` addressable prefix.
function Get-TrapStats {
    param([string] $AssetId)
    if (-not $AssetId) { return $null }
    $key = if ($AssetId.StartsWith('BT_')) { $AssetId.Substring(3) } else { $AssetId }
    if ($script:TrapStatsById.ContainsKey($key)) { return $script:TrapStatsById[$key] }
    return $null
}

foreach ($c in $crates) {
    $slug = $LootBiomeSlug[$c.PageTitle]
    $lootSection = ''
    $stubTag = "{{Stub}}`n"
    if ($script:LootTables -and $slug -and ($script:LootTables.biomes.PSObject.Properties[$slug])) {
        $biome = $script:LootTables.biomes.$slug
        $translations = $script:LootTables.tag_translations
        # Rate-set tables
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine('== Loot rate sets ==')
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine("Every crate in this biome pulls from one of the rate sets below. The ''Spawn rate'' column is the probability that a category yields an item when the crate rolls; the ''Stack factor'' scales the stack size for stackable categories.")
        [void]$sb.AppendLine('')
        foreach ($rsName in ($biome.rate_sets.PSObject.Properties.Name | Sort-Object)) {
            $rates = $biome.rate_sets.$rsName
            [void]$sb.Append((Build-LootRateTable -Rates $rates -Translations $translations -Name $rsName))
        }
        # Crate prefab -> rate set mapping (grouped by rate set)
        [void]$sb.AppendLine('== Crate variants ==')
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine("The following in-game crate prefabs spawn in this biome, each keyed to a rate set from above.")
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('{| class="wikitable sortable" style="font-size:0.9em;"')
        [void]$sb.AppendLine('! Prefab !! Rate set')
        $crateProps = $biome.crates.PSObject.Properties | Sort-Object { $_.Value }, { $_.Name }
        foreach ($cp in $crateProps) {
            [void]$sb.AppendLine('|-')
            [void]$sb.AppendLine("| <code>$($cp.Name)</code> || $($cp.Value)")
        }
        [void]$sb.AppendLine('|}')
        [void]$sb.AppendLine('')
        $lootSection = $sb.ToString()
        $stubTag = ''   # real content; drop stub badge
    }

    $content = @"
$Marker
{{Nav}}
$stubTag
The '''$($c.PageTitle)''' is a lootable container variant that spawns in the [[$($c.Biome)]] biome of ''[[Human Host]]''.

$lootSection== Location ==

Native biome: [[$($c.Biome)]]

Asset bundle: <code>$($c.BundleFile -replace '_assets_all.*$','')</code>

== See also ==

* [[Loot Crates]]: all crate variants
* [[$($c.Biome)]]: biome overview

[[Category:Loot Crates]]
[[Category:$($c.Biome)]]
"@
    Write-Stub "locations/$(Slugify $c.PageTitle).mediawiki" $content
}

# =============================================================================
# Workbenches
# =============================================================================
Write-Host "Workbenches..." -ForegroundColor Cyan
$workbenches = Read-JsonArray 'workbench_types'

# Human-friendly page titles + short descriptions per workbench.
$wbInfo = @{
    'AnvilWorkbench'       = @{ title='Anvil Workbench';       desc='Forges metal weapons and armor from smelted ingots.' }
    'BiochemicalWorkbench' = @{ title='Biochemical Workbench'; desc='Refines reagents into medicine, explosives, and gunpowder.' }
    'Campfire'             = @{ title='Campfire';              desc='Early-tier utility station. Cooking is planned but not yet in the current build.' }
    'CarpentryWorkbench'   = @{ title='Carpentry Workbench';   desc='Crafts wooden weapons (bows, spears), arrows, and wooden building parts.' }
    'CementMixer'          = @{ title='Cement Mixer';          desc='Produces concrete for high-tier reinforced structures.' }
    'CuttingWorkbench'     = @{ title='Cutting Workbench';     desc='Cuts hides into cloth, and large materials into workable components.' }
    'ElectronicsWorkbench' = @{ title='Electronics Workbench'; desc='Fabricates circuits, sensors, and Lua computers for vehicles and traps.' }
    'Furnace'              = @{ title='Furnace';               desc='Smelts ore into ingots. Requires fuel (Wood or Coal).' }
    'GunWorkbench'         = @{ title='Gun Workbench';         desc='Assembles firearms and firearm ammunition.' }
    'HandMade'             = @{ title='HandMade';              desc='The from-anywhere inventory crafting menu -- stone tools, torches, bandages.' }
    'MechanicalWorkbench'  = @{ title='Mechanical Workbench';  desc='Fabricates vehicle chassis parts, wheels, and engines.' }
}

foreach ($w in $workbenches) {
    # HandMade isn't a physical workbench; its dedicated top-level [[HandMade]] page (see the
    # HandMade reference block below) covers it in full, so we skip the crafting/HandMade.mediawiki
    # emission here to avoid a title collision.
    if ($w.Name -eq 'HandMade') { continue }

    $info = if ($wbInfo.ContainsKey($w.Name)) { $wbInfo[$w.Name] } else { @{ title=$w.Name; desc='TBD.' } }
    # Resolve the workbench prefab for this enum name, then override the display title with the
    # localized name if we have one.
    $enumToPrefab = @{
        'AnvilWorkbench'='WB_Anvil'; 'BiochemicalWorkbench'='WB_Biochemical'; 'Campfire'='WB_Campfire';
        'CarpentryWorkbench'='WB_Carpentry'; 'CementMixer'='WB_Concrete_Mixer'; 'CuttingWorkbench'='WB_Cutting';
        'ElectronicsWorkbench'='WB_Electronic'; 'Furnace'='WB_Furnace'; 'GunWorkbench'='WB_Gunsmith';
        'MechanicalWorkbench'='WB_Mechanical'
    }
    $prefab = if ($enumToPrefab.ContainsKey($w.Name)) { $enumToPrefab[$w.Name] } else { $null }
    if ($prefab) {
        $localizedTitle = Get-DisplayName $prefab
        if ($localizedTitle) { $info = @{ title = $localizedTitle; desc = $info.desc } }
    }
    $iconLine = ''
    $icon = Get-IconFilename -Title $info.title -Category 'Workbench'
    if ($icon) { $iconLine = "| image    = $icon`n" }
    $recipeTable = Build-RecipeTable $prefab
    $recipeSection = if ($recipeTable) { $recipeTable } else { 'No parsed recipes for this workbench yet.' }
    # Auto-drafted description from the recipe graph
    $wbDescription = Build-WorkbenchDescription -Title $info.title -Prefab $prefab
    # Drop the Stub badge once the page has real content (a recipe table).
    $wbStubTag = if ($recipeTable) { '' } else { "{{Stub}}`n" }

    # Build cost: this workbench itself is placed via a HandMade recipe under BFI_<prefab>_Icon.
    $wbBuildCost = ''
    $wbBuildRecipe = if ($script:HandMadeByShortId.ContainsKey($prefab)) { $script:HandMadeByShortId[$prefab] } else { $null }
    if ($wbBuildRecipe) {
        $wbBuildCost = "== Build cost ==`n`nPlaced from the basic 'craft by hand' interface. See [[HandMade]] for the full hand-craft catalog.`n`n$(Format-HandMadeRecipe $wbBuildRecipe)`n"
    }

    $content = @"
$Marker
{{Nav}}
$wbStubTag

{{Infobox station
| name     = $($info.title)
$iconLine| category = Crafting
}}

== Description ==

$wbDescription

== Technical ==

Internal enum value: <code>$($w.EnumName).$($w.Name)</code> (assembly <code>$($w.Assembly)</code>). Prefab: <code>$prefab</code>.

== Recipes ==

$recipeSection

$wbBuildCost== Placement ==

TBD: physical footprint and world-space clearance.

== Prerequisites ==

TBD: which workbench(es) this depends on.

== See also ==

* [[Crafting]]: the full workbench catalog

[[Category:Crafting Stations]]
"@
    Write-Stub "crafting/$(Slugify $info.title).mediawiki" $content
}

# =============================================================================
# Perks / talents
# When perks.json is present, we emit one page per real skill with its localized name,
# description, per-level values, and buff timing. Otherwise we fall back to the factor-derived
# generator (which just used skill_factors.json as a stand-in with hand-authored names).
# =============================================================================
$perksJsonPath = Join-Path $Data 'perks.json'
if (Test-Path $perksJsonPath) {
    Write-Host "Perks (from All_Skills_Set)..." -ForegroundColor Cyan
    $perkArr = Get-Content $perksJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "[perk] loaded $($perkArr.Count) perks across $($perkArr | Group-Object Category | Measure-Object).Count categories"

    # Substitute per-level values into the description template. Placeholder tokens the game uses:
    #   `[ * ]`   generic numeric slot
    #   `[ *% ]`  percentage slot (we append %)
    # Anything else inside `[ ... ]` (e.g. `[ Super Armor ]`) is a keyword decorator, left alone.
    function Format-PerkDescription {
        param([string] $Template, [float[]] $Values)
        if (-not $Template) { return '' }
        # PowerShell scriptblocks passed to [regex]::Replace don't reliably capture outer variables
        # by reference, so drive the substitution with a manual match-loop instead.
        $result = New-Object System.Text.StringBuilder
        $pos = 0
        $idx = 0
        $rx = [regex] '\[\s*(\*%?)\s*\]'
        foreach ($m in $rx.Matches($Template)) {
            [void]$result.Append($Template.Substring($pos, $m.Index - $pos))
            if ($idx -lt $Values.Count) {
                $v = $Values[$idx]; $idx++
                $formatted = if ($v -eq [math]::Floor($v) -and [math]::Abs($v) -lt 2147483647) { [int]$v } else { [math]::Round($v, 2) }
                if ($m.Groups[1].Value.EndsWith('%')) {
                    [void]$result.Append("'''${formatted}%'''")
                } else {
                    [void]$result.Append("'''${formatted}'''")
                }
            } else {
                [void]$result.Append($m.Value)
            }
            $pos = $m.Index + $m.Length
        }
        [void]$result.Append($Template.Substring($pos))
        return $result.ToString()
    }

    foreach ($perk in $perkArr) {
        # Skip entries that have no name at all (defensive; happens if Language_Text is unset in the source).
        if (-not $perk.DisplayName -or $perk.DisplayName -eq '(unnamed skill)') { continue }

        $tree = $perk.Category
        $desc = $perk.Description
        $title = $perk.DisplayName

        # Per-level table
        $levelTable = New-Object System.Text.StringBuilder
        [void]$levelTable.AppendLine('{| class="wikitable" style="font-size:0.9em;"')
        [void]$levelTable.AppendLine('! Level !! Effect')
        foreach ($lv in $perk.Levels) {
            $vals = @($lv.Values | ForEach-Object { [float]$_ })
            $filled = Format-PerkDescription -Template $desc -Values $vals
            [void]$levelTable.AppendLine('|-')
            [void]$levelTable.AppendLine("| $($lv.Level) || $filled")
        }
        [void]$levelTable.AppendLine('|}')

        # Buff info line
        $buffLine = if ($perk.IsBuff) {
            $stack = if ($perk.StackableBuff) { 'stackable' } else { 'non-stackable' }
            "'''$title''' is a $stack buff that ticks every $($perk.BuffPeriodSeconds) seconds while active."
        } else {
            "'''$title''' is a $tree-tree passive that scales with level; the table above shows the exact effect at each of its $($perk.MaxLevel) ranks."
        }

        $content = @"
$Marker
{{Nav}}

{{Infobox perk
| name     = $title
| tree     = $tree
| maxLevel = $($perk.MaxLevel)
| buff     = $(if ($perk.IsBuff) { 'Yes' } else { 'No' })
}}

$buffLine

== Effect ==

$desc

== Per-level values ==

$($levelTable.ToString())
== See also ==

* [[Perks]]: the full skill catalog

[[Category:Perks]]
[[Category:$tree tree]]
"@
        Write-Stub "perks/$(Slugify $title).mediawiki" $content
    }
    Write-Host "[perk] wrote $($perkArr.Count) perk pages"
} else {
Write-Host "Perks (factor-derived)..." -ForegroundColor Cyan
$factors = Read-JsonArray 'skill_factors'

# Human-friendly perk titles + descriptions keyed by factor field name.
$perkInfo = @{
    '_adrlinMeleeDmg_Factor'       = @{ title='Adrenaline (Melee Damage)';    tree='Fight';   desc='Grants a melee damage bonus when at low health.' }
    '_adrlinStaCost_Factor'        = @{ title='Adrenaline (Stamina)';         tree='Fight';   desc='Reduces stamina cost when at low health.' }
    '_adrlinWaterCost_Factor'      = @{ title='Adrenaline (Water)';           tree='Survive'; desc='Reduces water consumption when at low health.' }
    '_bareBluntHitDownProb_Factor' = @{ title='Bare Knuckles';                tree='Fight';   desc='Increases knockdown chance with bare fists and blunt weapons.' }
    '_bladeHitProb_Factor'         = @{ title='Blade Precision';              tree='Fight';   desc='Increases hit / crit chance with bladed weapons.' }
    '_bowDamage_Factor'            = @{ title='Bow Damage';                   tree='Fight';   desc='Increases damage dealt by bow shots.' }
    '_bowDrop_Factor'              = @{ title='Bow Steadying';                tree='Fight';   desc='Reduces arrow gravity drop, extending effective range.' }
    '_fireRecoil_Factor'           = @{ title='Steady Aim';                   tree='Fight';   desc='Reduces firearm recoil per shot.' }
    '_fireSpread_Factor'           = @{ title='Tight Groups';                 tree='Fight';   desc='Reduces firearm bullet spread.' }
    '_foodWaterDecrease_Factor'    = @{ title='Efficient Metabolism';         tree='Survive'; desc='Slows the rate at which food and water deplete.' }
    '_gunDamage_Factor'            = @{ title='Gunslinger';                   tree='Fight';   desc='Increases damage dealt by firearm shots.' }
    '_maxHP_Factor'                = @{ title='Tough';                        tree='Survive'; desc='Increases maximum health.' }
    '_maxStamina_Factor'           = @{ title='Athletic';                     tree='Survive'; desc='Increases maximum stamina.' }
    '_meleeDamage_Factor'          = @{ title='Melee Damage';                 tree='Fight';   desc='General melee damage multiplier.' }
    '_minerDmg_Factor'             = @{ title='Miner';                        tree='Survive'; desc='Increases mining damage per swing (faster ore extraction).' }
    '_moraleDmg_Factor'            = @{ title='Intimidator';                  tree='Fight';   desc='Increases damage dealt to NPC morale.' }
    '_repair_Factor'               = @{ title='Handyman';                     tree='Craft';   desc='Improves repair efficiency at workbenches.' }
    '_silenceStepDmg_Factor'       = @{ title='Silent Assassin';              tree='Fight';   desc='Bonus damage on the first hit from stealth.' }
    '_silentStepReduce_Factor'     = @{ title='Silent Step';                  tree='Survive'; desc='Reduces the sound radius of your footsteps.' }
    '_softLandDmgReduce_Factor'    = @{ title='Soft Landing';                 tree='Survive'; desc='Reduces fall damage.' }
    '_staminaRege_Factor'          = @{ title='Stamina Regen';                tree='Survive'; desc='Increases stamina regeneration rate.' }
    '_stoneSkinDmgReduce_Factor'   = @{ title='Stone Skin';                   tree='Fight';   desc='Reduces incoming damage from all sources.' }
    '_woodJackDmg_Factor'          = @{ title='Woodjack';                     tree='Survive'; desc='Increases wood-chopping damage per swing.' }
}

foreach ($f in $factors) {
    $info = if ($perkInfo.ContainsKey($f.Field)) { $perkInfo[$f.Field] } else { @{ title=$f.PerkGuess; tree='Unknown'; desc='TBD.' } }
    $content = @"
$Marker
{{Nav}}
{{Stub}}

{{Infobox perk
| name     = $($info.title)
| tree     = $($info.tree)
| effect   = $($info.desc)
| factor   = $($f.Field) on Char_Skills
}}

'''$($info.title)''' is a $($info.tree)-tree perk in ''[[Human Host]]''. The in-game display name may differ from this page title; the authoritative identifier is the runtime factor field name.

== Effect ==

$($info.desc)

== Technical ==

Backing runtime factor: <code>$($f.Field)</code> (type <code>$($f.FieldType)</code>) on <code>Char_Skills</code> in the <code>$($f.Assembly)</code> assembly.

== See also ==

* [[Perks]]: the full skill catalog
* Related factors on [[Perks#Factor catalog]]

[[Category:Perks]]
[[Category:$($info.tree) tree]]
"@
    Write-Stub "perks/$(Slugify $info.title).mediawiki" $content
}
}

# =============================================================================
# Traps (from the addressables catalog: BT_ prefix, 15 asset variants)
# =============================================================================
Write-Host "Traps..." -ForegroundColor Cyan
$catalog = Get-Content (Join-Path $Data 'asset_catalog.json') -Raw | ConvertFrom-Json
$trapCatalog = @($catalog.Traps)
$trapClasses = @(Read-JsonArray 'trap_classes')  # 4 Cecil-derived classes for technical context

foreach ($assetId in $trapCatalog) {
    # Prefer localization; fall back to prefix-strip
    $displayName = Get-DisplayName $assetId
    if ($displayName) {
        $pageTitle = $displayName
    } else {
        $name = $assetId
        if ($name.StartsWith('BT_')) { $name = $name.Substring(3) }
        $pageTitle = $name -replace '_', ' '
    }

    # Best-guess technical mapping: find a matching Trap_ class if one exists
    $classMatch = $trapClasses | Where-Object {
        $simple = $_.PageTitle -replace ' ', ''
        ($pageTitle -replace ' ', '') -like "*$simple*"
    } | Select-Object -First 1

    $techLine = if ($classMatch) {
        "Backed by class <code>$($classMatch.FullName)</code> (assembly <code>$($classMatch.Assembly)</code>), derived from <code>Trap_Base</code>."
    } else {
        'Backing class not automatically identified; likely a data variant of a Trap_Base subclass.'
    }

    # Trap runtime stats table (from the Trap_Base MonoBehaviour on the prefab)
    $trapStats = Get-TrapStats $assetId
    $behaviorSection = ''
    if ($trapStats) {
        $rows = New-Object System.Collections.Generic.List[string]
        # Core Trap_Base stats
        if ($null -ne $trapStats._TrapDamage)     { [void]$rows.Add("| Trap damage       || $($trapStats._TrapDamage) per trigger") }
        if ($null -ne $trapStats._TriggerInterval){ [void]$rows.Add("| Trigger interval  || $($trapStats._TriggerInterval)s") }
        if ($null -ne $trapStats._HitReact)       { [void]$rows.Add("| Hit-react force   || $($trapStats._HitReact)") }
        if ($null -ne $trapStats._TrapSelfDmg)    { [void]$rows.Add("| Self-damage       || $($trapStats._TrapSelfDmg) per trigger") }
        if ($null -ne $trapStats._AllowHitAlly)   { [void]$rows.Add("| Hits allies       || $(if ($trapStats._AllowHitAlly -eq 1) { 'Yes (allies + zombies)' } else { 'No (zombies only)' })") }
        if ($null -ne $trapStats._AllowSelfSmash) { [void]$rows.Add("| Smashable         || $(if ($trapStats._AllowSelfSmash -eq 1) { 'Yes' } else { 'No' })") }
        if ($null -ne $trapStats._isDynamicTrap)  { [void]$rows.Add("| Placement         || $(if ($trapStats._isDynamicTrap -eq 1) { 'Dynamic (moving)' } else { 'Static' })") }
        # Subclass extras
        if ($null -ne $trapStats._LaserContinueSeconds) { [void]$rows.Add("| Beam duration     || $($trapStats._LaserContinueSeconds)s") }
        if ($null -ne $trapStats._PerZombieHitInterval) { [void]$rows.Add("| Per-target cooldown || $($trapStats._PerZombieHitInterval)s") }
        if ($null -ne $trapStats._RotSeconds)     { [void]$rows.Add("| Rotation period   || $($trapStats._RotSeconds)s") }
        if ($null -ne $trapStats._RotSpeedFactor) { [void]$rows.Add("| Rotation speed    || $($trapStats._RotSpeedFactor)") }
        if ($null -ne $trapStats._spikeBackSeconds){ [void]$rows.Add("| Retract delay     || $($trapStats._spikeBackSeconds)s") }
        if ($rows.Count -gt 0) {
            $sb = New-Object System.Text.StringBuilder
            [void]$sb.AppendLine('== Behavior ==')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('{| class="wikitable" style="font-size:0.9em;"')
            [void]$sb.AppendLine('! Stat !! Value')
            foreach ($r in $rows) {
                [void]$sb.AppendLine('|-')
                [void]$sb.AppendLine($r)
            }
            [void]$sb.AppendLine('|}')
            [void]$sb.AppendLine('')
            $behaviorSection = $sb.ToString()
        }
    }
    $trapStubTag = if ($behaviorSection) { '' } else { "{{Stub}}`n" }

    # Trap build cost from HandMade (Category_5). The asset id is BT_<name>; HandMade indexes
    # under the trap's short id (assetId minus BT_ prefix).
    $trapBuildCost = ''
    $trapKey = if ($assetId.StartsWith('BT_')) { $assetId.Substring(3) } else { $assetId }
    $trapBuildRecipe = if ($script:HandMadeByShortId.ContainsKey($trapKey)) { $script:HandMadeByShortId[$trapKey] } else { $null }
    if ($trapBuildRecipe) {
        $trapBuildCost = "== Build cost ==`n`nPlaced from the basic 'craft by hand' interface (no workbench required). See [[HandMade]] for the full hand-craft catalog.`n`n$(Format-HandMadeRecipe $trapBuildRecipe)`n"
    }

    $content = @"
$Marker
{{Nav}}
$trapStubTag
The '''$pageTitle''' is a trap in ''[[Human Host]]'', used for base defense against horde nights.

$behaviorSection$trapBuildCost== Placement ==

TBD: placement footprint and world-space clearance.

== Technical ==

Asset id: <code>$assetId</code>. $techLine Traps are registered via <code>Trap_Mgr._ins</code>.

== See also ==

* [[Building]]: trap placement in the build menu
* [[Combat]]: horde night defense strategy

[[Category:Traps]]
[[Category:Building]]
"@
    Write-Stub "building/$(Slugify $pageTitle).mediawiki" $content
}

# =============================================================================
# Enemies (zombies + bosses per biome)
# =============================================================================
$enemiesJsonPath = Join-Path $Data 'enemies.json'
if (Test-Path $enemiesJsonPath) {
    Write-Host "Enemies..." -ForegroundColor Cyan
    $enemyArr = Get-Content $enemiesJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json

    # Human-facing biome names keyed by the extractor's slug (matches the loot-table slugs).
    $EnemyBiomeName = @{
        'baseterrain'  = 'All biomes'
        'desert'       = 'Desert'
        'desert_rocky' = 'Desert Rocky'
        'forest'       = 'Mossy Forest'
        'mountain'     = 'Mountain Forest'
        'rainforest'   = 'Tropical Jungle'
        'swamp'        = 'Tropical Swamp'
        'warzone'      = 'Warzone'
        'wasteland'    = 'Wasteland'
        'winterforest' = 'Winter Forest'
        'wintertown'   = 'Winter Town'
    }

    # Group by internal_id so we can list all biomes an enemy appears in on a single page. Some
    # prefabs are biome-tagged suffixes (Z_Man_15_SnowT_G01); we treat those as distinct entries.
    foreach ($e in $enemyArr) {
        $id = $e.internal_id
        $biomeSlug = $e.biome
        $biomeName = if ($EnemyBiomeName.ContainsKey($biomeSlug)) { $EnemyBiomeName[$biomeSlug] } else { $biomeSlug }

        # Prettify the internal id into a display title.
        # Z_Man_22          -> Zombie Man 22
        # Z_Woman_04        -> Zombie Woman 04
        # Z_Boss_01         -> Boss 01
        # Z_Man_Big_01      -> Big Zombie 01
        # Z_Man_15_SnowT_G01 -> Zombie Man 15 (Winter variant)
        $title = $id
        $suffix = ''
        if ($title -match '_(SnowT|MountainF|SnowF|WarZ|Wasteland)_G\d+$') {
            $rawSuffix = $Matches[1]
            $variantTag = switch ($rawSuffix) {
                'SnowT'     { 'Winter Town variant' }
                'SnowF'     { 'Winter Forest variant' }
                'MountainF' { 'Mountain Forest variant' }
                'WarZ'      { 'Warzone variant' }
                'Wasteland' { 'Wasteland variant' }
                default     { "$rawSuffix variant" }
            }
            $suffix = " ($variantTag)"
            $title = $title -replace '_(SnowT|MountainF|SnowF|WarZ|Wasteland)_G\d+$', ''
        }
        $title = $title -replace '^Z_Boss_','Boss '
        $title = $title -replace '^Z_Man_Big_','Big Zombie '
        $title = $title -replace '^Z_Woman_Big_','Big Zombie '
        $title = $title -replace '^Z_Man_','Zombie Man '
        $title = $title -replace '^Z_Woman_','Zombie Woman '
        $title = $title -replace '^Z_Women_','Zombie Woman '
        $title = ($title -replace '_', ' ').Trim() + $suffix

        # Stats table
        $rows = New-Object System.Collections.Generic.List[string]
        if ($null -ne $e._maxHP)         { [void]$rows.Add("| Health         || $($e._maxHP) HP") }
        if ($null -ne $e._maxStamina)    { [void]$rows.Add("| Stamina        || $($e._maxStamina)") }
        if ($null -ne $e._stamRegePerSec){ [void]$rows.Add("| Stamina regen  || $($e._stamRegePerSec)/sec") }
        if ($null -ne $e._EnableFoodWater -and $e._EnableFoodWater -eq 1) {
            [void]$rows.Add("| Food/water     || Enabled ($($e._maxFood) food, $($e._maxWater) water)")
        }
        $statsTable = ''
        if ($rows.Count -gt 0) {
            $sb = New-Object System.Text.StringBuilder
            [void]$sb.AppendLine('== Stats ==')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine('{| class="wikitable" style="font-size:0.9em;"')
            [void]$sb.AppendLine('! Stat !! Value')
            foreach ($r in $rows) {
                [void]$sb.AppendLine('|-')
                [void]$sb.AppendLine($r)
            }
            [void]$sb.AppendLine('|}')
            [void]$sb.AppendLine('')
            $statsTable = $sb.ToString()
        }

        # Enemy category
        $category = if     ($id -match '^Z_Boss')      { 'Boss'   }
                    elseif ($id -match '^Z_Man_Big|^Z_Woman_Big') { 'Big zombie' }
                    else                               { 'Zombie' }

        $content = @"
$Marker
{{Nav}}

{{Infobox enemy
| name       = $title
| category   = $category
| biome      = $biomeName
| hp         = $($e._maxHP)
}}

'''$title''' is a $category encountered in the [[$biomeName]] biome of ''[[Human Host]]''.

$statsTable== Technical ==

Prefab id: <code>$id</code> (bundle <code>zb_$biomeSlug</code>). Backed by <code>Char_Status</code> + <code>Zombie_Input</code> on the prefab.

== See also ==

* [[Enemies]]: the full enemy catalog
* [[$biomeName]]: biome overview

[[Category:Enemies]]
[[Category:$category]]
"@
        # Slug uses the internal id so biome-suffixed prefabs (e.g. Z_Man_15_SnowT_G01) get their
        # own page distinct from the base Z_Man_15 in a different biome.
        Write-Stub "enemies/$(Slugify $id).mediawiki" $content
    }
    Write-Host "[enemy] wrote $($enemyArr.Count) enemy pages"

    # Enemies hub: group by biome
    $hub = New-Object System.Text.StringBuilder
    [void]$hub.AppendLine($Marker)
    [void]$hub.AppendLine('{{Nav}}')
    [void]$hub.AppendLine('')
    [void]$hub.AppendLine("Every zombie and boss variant in ''[[Human Host]]'', grouped by the biome bundle it ships in. Base HP is shown; per-difficulty scaling is applied by the runtime and is not captured in the prefab.")
    [void]$hub.AppendLine('')
    $enemyByBiome = $enemyArr | Group-Object biome | Sort-Object Name
    foreach ($g in $enemyByBiome) {
        $slug = $g.Name
        $bname = if ($EnemyBiomeName.ContainsKey($slug)) { $EnemyBiomeName[$slug] } else { $slug }
        [void]$hub.AppendLine("== $bname ==")
        [void]$hub.AppendLine('')
        [void]$hub.AppendLine('{| class="wikitable sortable" style="font-size:0.9em;"')
        [void]$hub.AppendLine('! Enemy !! Category !! HP !! Stamina !! Regen')
        foreach ($e2 in ($g.Group | Sort-Object internal_id)) {
            $id2 = $e2.internal_id
            # Same title-derivation as above (inline, kept simple).
            $displayName = ($id2 -replace '_(SnowT|MountainF|SnowF|WarZ|Wasteland)_G\d+$', '' `
                                 -replace '^Z_Boss_','Boss ' `
                                 -replace '^Z_Man_Big_','Big Zombie ' `
                                 -replace '^Z_Woman_Big_','Big Zombie ' `
                                 -replace '^Z_Man_','Zombie Man ' `
                                 -replace '^Z_Woman_','Zombie Woman ' `
                                 -replace '^Z_Women_','Zombie Woman ' `
                                 -replace '_',' ').Trim()
            if ($id2 -match '_(SnowT|MountainF|SnowF|WarZ|Wasteland)_G\d+$') { $displayName += ' (variant)' }
            $cat2 = if ($id2 -match '^Z_Boss') { 'Boss' } elseif ($id2 -match '^Z_Man_Big|^Z_Woman_Big') { 'Big zombie' } else { 'Zombie' }
            [void]$hub.AppendLine('|-')
            [void]$hub.AppendLine("| [[$displayName]] || $cat2 || $($e2._maxHP) || $($e2._maxStamina) || $($e2._stamRegePerSec)/sec")
        }
        [void]$hub.AppendLine('|}')
        [void]$hub.AppendLine('')
    }
    [void]$hub.AppendLine('[[Category:Reference]]')
    Write-Stub 'Enemies.mediawiki' $hub.ToString()
}

# =============================================================================
# Vehicle bundle overviews (one per biome)
# =============================================================================
Write-Host "Vehicles..." -ForegroundColor Cyan

foreach ($v in $vehicles) {
    $content = @"
$Marker
{{Nav}}
{{Stub}}

The '''$($v.PageTitle)''' bundle groups all vehicle chassis native to the [[$($v.Biome)]] biome.

== Chassis ==

TBD: list of biome-specific chassis.

== Notes ==

Asset bundle: <code>$($v.BundleFile -replace '_assets_all.*$','')</code>

== See also ==

* [[Vehicles]]: overview of the vehicle system
* [[Vehicle Parts]]: parts shared across all biomes
* [[$($v.Biome)]]: biome overview

[[Category:Vehicles]]
[[Category:$($v.Biome)]]
"@
    Write-Stub "vehicles/$(Slugify $v.PageTitle).mediawiki" $content
}

# =============================================================================
# Per-item stubs from the asset catalog
# =============================================================================

# Shared helpers for the item generators.
function New-ItemStub {
    param(
        [string] $Title,
        [string] $AssetId,
        [string] $Category,     # Ore, Food, Water, Melee, Ranged, etc.
        [string] $ParentPage,   # e.g. "Mining", "Food and Water", "Weapons"
        [string] $InfoboxType,  # "item" or "weapon"
        [hashtable] $ExtraFields = @{}
    )
    $fields = New-Object System.Collections.Generic.List[string]
    $fields.Add("| name       = $Title")
    # Icon reference. All non-workbench items derive filename from the raw asset id (localized
    # titles don't match PNG filenames; internal short ids do).
    $icon = Get-IconFilename -Title $Title -Category $null -AssetId $AssetId
    if ($icon) { $fields.Add("| image      = $icon") }
    $fields.Add("| type       = $Category")
    foreach ($k in $ExtraFields.Keys) { $fields.Add("| $k = $($ExtraFields[$k])") }
    # Icon_Info stat fields (damage, durability, stack, tag, etc). Skipped for workbenches / when
    # the item has no matching Icon_Info prefab.
    $statFields = Build-StatsFields -RawAssetId $AssetId -InfoboxType $InfoboxType
    foreach ($sf in $statFields) { $fields.Add($sf) }
    $fieldBlock = $fields -join "`n"

    $script:GeneratedTitles[$Title] = $true
    $crossRefs = Build-CrossRefSections $Title
    $description = Build-ItemDescription -Title $Title -Category $Category
    # Loot-source table (which biome crates spawn this item, at what category rate).
    $lootSourcesSection = Build-LootSources -AssetId $AssetId -ItemTitle $Title
    # Drop the {{Stub}} badge when the page has real content (description + at least one cross-ref
    # or a loot-source table).
    $hasContent = ($crossRefs.Trim().Length -gt 0) -or ($lootSourcesSection.Trim().Length -gt 0)
    $stubTag = if ($hasContent) { '' } else { "{{Stub}}`n" }
    # Optional tier scaling table for items with meaningful base combat stats.
    $tierSection = ''
    $itemStats = Get-ItemStats $AssetId
    if ($itemStats -and $script:TierConfig) {
        $bd = if ($itemStats._baseDamage)       { [double]$itemStats._baseDamage }       else { 0 }
        $bu = if ($itemStats._BaseMaxDurability){ [double]$itemStats._BaseMaxDurability }else { 0 }
        $bh = if ($itemStats._baseHitDownProb)  { [double]$itemStats._baseHitDownProb }  else { 0 }
        $bb = if ($itemStats._baseBladeHitProb) { [double]$itemStats._baseBladeHitProb } else { 0 }
        if (($bd + $bu + $bh + $bb) -gt 0) {
            $tierTable = Build-TierTable -BaseDamage $bd -BaseDurability $bu -BaseHitDown $bh -BaseBlade $bb
            $tierSection = "== Tier scaling ==`n`nItems in ''[[Human Host]]'' roll one of six quality tiers on drop or craft. Each tier scales the base stats by a fixed multiplier and colors the item's inventory chip accordingly. See [[Tiers]] for the full reference.`n`n$tierTable`n"
        }
    }
    return @"
$Marker
{{Nav}}
$stubTag
{{Infobox $InfoboxType
$fieldBlock
}}

== Description ==

$description

$crossRefs$lootSourcesSection$tierSection== Technical ==

Addressable asset id: <code>$AssetId</code>.

== See also ==

* [[$ParentPage]]

[[Category:$Category]]
"@
}

# Track which wiki-page titles the categorized generators have produced so the atomic-materials
# generator can skip duplicates.
$script:GeneratedTitles = @{}

# --- Ores --------------------------------------------------------------------
Write-Host "Ores (per-asset stubs)..." -ForegroundColor Cyan
$catalogAll = Get-Content (Join-Path $Data 'asset_catalog.json') -Raw | ConvertFrom-Json

foreach ($assetId in $catalogAll.Ores) {
    # Prefer localized name; fall back to prefix-strip + ore rotation for the few without tooltips
    $title = Get-DisplayName $assetId
    if (-not $title) {
        $s = $assetId
        if ($s.StartsWith('BO_Ore_'))   { $title = ($s.Substring('BO_Ore_'.Length) -replace '_', ' ') + ' Ore' }
        elseif ($s.StartsWith('BO_'))   { $title = $s.Substring('BO_'.Length) -replace '_', ' ' }
        else                             { $title = $s -replace '_', ' ' }
    }
    $cat = Get-DisplayType $assetId
    if (-not $cat) { $cat = 'Ore' }
    Write-Stub "items/ores/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category $cat -ParentPage 'Mining' -InfoboxType 'item' -ExtraFields @{ source = 'Mined from terrain' })
}

# --- Food items --------------------------------------------------------------
Write-Host "Food items..." -ForegroundColor Cyan
foreach ($name in $catalogAll.Foods) {
    $assetId = "FWI_${name}_Icon"
    $title = Get-DisplayName $assetId
    if (-not $title) { $title = $name -replace '_', ' ' }
    $cat = Get-DisplayType $assetId
    if (-not $cat) { $cat = 'Food' }
    $src = if ($name -match 'Canned|Sausage') { 'Looted from containers and zombie corpses' } else { 'Foraged in the wild' }
    Write-Stub "items/food/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category $cat -ParentPage 'Food and Water' -InfoboxType 'item' -ExtraFields @{ source = $src })
}

# --- Water items -------------------------------------------------------------
Write-Host "Water items..." -ForegroundColor Cyan
foreach ($name in $catalogAll.Waters) {
    $assetId = "FWI_${name}_Icon"
    $title = Get-DisplayName $assetId
    if (-not $title) { $title = $name -replace '_', ' ' }
    $cat = Get-DisplayType $assetId
    if (-not $cat) { $cat = 'Water' }
    Write-Stub "items/water/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category $cat -ParentPage 'Food and Water' -InfoboxType 'item' -ExtraFields @{ source = 'Looted from containers and zombie corpses' })
}

# --- Melee weapons -----------------------------------------------------------
Write-Host "Melee weapons..." -ForegroundColor Cyan
$meleePrefixMap = @{ 'AXE_' = 'Axe'; 'DAG_' = 'Blade'; 'SPEAR_' = 'Spear'; 'BIG_' = 'Blunt' }
foreach ($assetId in $catalogAll.MeleeWeapons) {
    $prefix = ($meleePrefixMap.Keys | Where-Object { $assetId.StartsWith($_) } | Select-Object -First 1)
    $subtype = if ($prefix) { $meleePrefixMap[$prefix] } else { 'Melee' }
    $title = Get-DisplayName $assetId
    if (-not $title) {
        $stripped = if ($prefix) { $assetId.Substring($prefix.Length) } else { $assetId }
        $title = $stripped -replace '_', ' '
    }
    $cat = Get-DisplayType $assetId
    if (-not $cat) { $cat = 'Melee Weapons' }
    Write-Stub "items/melee/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category $cat -ParentPage 'Melee Weapons' -InfoboxType 'weapon' -ExtraFields @{ subtype = $subtype })
}

# --- Ranged weapons + ammo + optics -----------------------------------------
Write-Host "Ranged items..." -ForegroundColor Cyan
foreach ($assetId in $catalogAll.RangedWeapons) {
    $name = $assetId.Substring('RW_'.Length)
    $title = Get-DisplayName $assetId
    if (-not $title) { $title = $name -replace '_', ' ' }
    $cat = Get-DisplayType $assetId
    if (-not $cat) {
        if ($name -match 'AmmoBox$')            { $cat = 'Ammunition' }
        elseif ($name -match '^Arrow_')         { $cat = 'Ammunition' }
        elseif ($name -match '^Bow_')           { $cat = 'Ranged Weapons' }
        elseif ($name -match 'Sight$|^Scope_')  { $cat = 'Weapon Attachments' }
        else                                     { $cat = 'Ranged Weapons' }
    }
    $sub = if ($name -match 'AmmoBox$') { 'Cased' } elseif ($name -match '^Arrow_') { 'Arrow' } elseif ($name -match '^Bow_') { 'Bow' } elseif ($name -match 'Sight$|^Scope_') { 'Optic' } else { 'Firearm' }
    $parent = if ($cat -eq 'Ammunition') { 'Ammunition' } elseif ($cat -eq 'Weapon Attachments') { 'Weapons' } else { 'Ranged Weapons' }
    Write-Stub "items/ranged/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category $cat -ParentPage $parent -InfoboxType 'weapon' -ExtraFields @{ subtype = $sub })
}

# =============================================================================
# Atomic materials: one stub per unique recipe item that no earlier phase covered.
# =============================================================================
Write-Host "Atomic materials (from recipes)..." -ForegroundColor Cyan

# Enumerate every normalized item name appearing anywhere in the recipe graph.
$allRecipeItems = New-Object System.Collections.Generic.HashSet[string]
foreach ($k in $script:ItemProducedBy.Keys) { [void]$allRecipeItems.Add($k) }
foreach ($k in $script:ItemUsedIn.Keys)     { [void]$allRecipeItems.Add($k) }

# Skip items that are already covered by a categorized generator (ores/food/water/melee/ranged/traps),
# by a hand-written page, or are placeholders / duplicates.
$knownHandwritten = @(
    # Legacy workbench titles (pre-localization) plus localized ones so we skip either way
    'Anvil','Anvil Workbench','Biochemical Workbench','Campfire','Carpentry Workbench',
    'Cement Mixer','Concrete Mixer','Cutting Workbench','Electronics Workbench',
    'Furnace','Gun Workbench','Gunsmith Workbench','HandMade','Mechanical Workbench'
)
# Also skip anything the trap generator already produced (its titles now come from localization)
foreach ($assetId in $trapCatalog) {
    $displayName = Get-DisplayName $assetId
    if ($displayName) {
        $knownHandwritten += $displayName
    } else {
        $n = $assetId; if ($n.StartsWith('BT_')) { $n = $n.Substring(3) }
        $knownHandwritten += ($n -replace '_',' ')
    }
}
foreach ($t in $knownHandwritten) { $script:GeneratedTitles[$t] = $true }

$skipPlaceholders = @('WIP','Recipe Sack','BurningFire','Burning','Recipe Sack','Recipe_Sack')

# Category inference by name patterns
function Guess-MaterialCategory {
    param([string] $Title)
    if ($Title -match 'Iron$|Steel$|Copper|Brass|Chrome|Titanium|Tungsten|Alloy') { return 'Refined Metal' }
    if ($Title -match '^Forged ')                                                 { return 'Refined Metal' }
    if ($Title -match '^Scrap ')                                                  { return 'Salvage' }
    if ($Title -match 'Concrete|Cement|Brick|Mortar')                             { return 'Building Material' }
    if ($Title -match '^Plank|^Log ')                                             { return 'Building Material' }
    if ($Title -match 'Glass|Marble|Obsidian|Lapis|Epidote|Chrismatite|Limestone|Sand|Clay|Sulfur') { return 'Building Material' }
    if ($Title -match 'BAT |BLADE |Machete|Bat|Club')                             { return 'Melee Weapon' }
    if ($Title -match '^RWI |Handgun Parts|Rifle Parts|Shotgun Parts|SMG Parts|Gun Kit') { return 'Weapon Component' }
    if ($Title -match '^EQI ')                                                    { return 'Equipment' }
    if ($Title -match '^VPI ')                                                    { return 'Vehicle Part' }
    if ($Title -match 'Bullet|Beads|mm |D45 |ACP|9x19')                           { return 'Ammunition Component' }
    if ($Title -match 'Bandage|Medical|Medkit|Anesthetic|Antibiotics|Pain Killer|Syringe|Scalpel|Iodophor|Alcohol') { return 'Medical' }
    if ($Title -match 'Circuit|CPU|Motor|Generator|Battery|Panel|Solar|Wire|Transformer|Sensor|Emitter') { return 'Electronic Component' }
    if ($Title -match 'Wood$|Wood ')                                              { return 'Raw Material' }
    if ($Title -match 'Bone|Tree Sap|Feather|Rope|Rubber|Plant Fiber|Starch|Duct Tape|Paper|Glue') { return 'Raw Material' }
    if ($Title -match 'Charcoal|Cobblestone|Nails|Gear|Spring|Bearing|Magnet|Pulley|Steering Wheel|Rebar|Iron Arrowhead') { return 'Component' }
    return 'Material'
}

# Icon lookup for atomic materials: try common transformations, fall back to no icon.
function Guess-MaterialIcon {
    param([string] $Title)
    $iconDir = 'C:\Users\SMC\HumanHostMods\wiki\assets\curated\icons'
    $candidates = @(
        "$($Title -replace ' ', '_').png",
        "$($Title -replace ' ', '_')_Icon.png",
        "RII_$($Title -replace ' ', '_').png"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $iconDir $c)) { return $c }
    }
    return $null
}

$materialsWritten = 0
foreach ($title in ($allRecipeItems | Sort-Object)) {
    if ($script:GeneratedTitles.ContainsKey($title)) { continue }
    if ($skipPlaceholders -contains $title) { continue }
    # Skip anything left over that still looks like a tier variant (shouldn't happen post-normalize).
    if ($title -match '^\d+ \d+ ') { continue }

    $script:GeneratedTitles[$title] = $true
    $cat = Guess-MaterialCategory $title
    $icon = Guess-MaterialIcon $title

    $fields = New-Object System.Collections.Generic.List[string]
    $fields.Add("| name       = $title")
    if ($icon) { $fields.Add("| image      = $icon") }
    $fields.Add("| type       = $cat")
    # Stats lookup by localized title (atomic materials arrive here with no raw asset id).
    $matStats = Get-ItemStatsByTitle $title
    if ($matStats) {
        $statFields = Build-StatsFields -RawAssetId $null -InfoboxType 'item' -Stats $matStats
        foreach ($sf in $statFields) { $fields.Add($sf) }
    }
    $fieldBlock = $fields -join "`n"
    $crossRefs = Build-CrossRefSections $title
    $description = Build-ItemDescription -Title $title -Category $cat
    # Loot-source table (which biome crates spawn this item, at what category rate).
    $matLootSources = if ($matStats) { Build-LootSources -AssetId $null -ItemTitle $title -Stats $matStats } else { '' }
    # Drop {{Stub}} once the page has real content (description + at least one cross-ref or a
    # loot-source table).
    $matHasContent = ($crossRefs.Trim().Length -gt 0) -or ($matLootSources.Trim().Length -gt 0)
    $stubTag = if ($matHasContent) { '' } else { "{{Stub}}`n" }

    $content = @"
$Marker
{{Nav}}
$stubTag
{{Infobox item
$fieldBlock
}}

== Description ==

$description

$crossRefs$matLootSources== See also ==

* [[Crafting]]: workbench overview
* [[Items]]: full item catalog

[[Category:$cat]]
[[Category:Items]]
"@
    Write-Stub "items/materials/$(Slugify $title).mediawiki" $content
    $materialsWritten++
}
Write-Host "Atomic materials written: $materialsWritten"

# =============================================================================
# Recipes hub: alphabetical index of every material with its workbench + role
# =============================================================================
# HandMade reference page: every recipe craftable from the basic "craft by hand" interface,
# grouped by the category the game exposes in the UI.
if ($script:HandMadeRecipes) {
    Write-Host "HandMade reference..." -ForegroundColor Cyan
    # Human-readable labels for the 6 categories. Names are inferred from the recipe content
    # (Category_0 has medical + Tool Kit + rope, Category_1 is early melee, etc.). If the game
    # exposes localized labels for these, they live in a Tag_Menu ScriptableObject we haven't
    # cracked, so we ship curated labels here.
    $handMadeCategoryLabel = @{
        'Category_0' = 'Medical, tools, base essentials'
        'Category_1' = 'Melee weapons'
        'Category_2' = 'Bows and arrows'
        'Category_3' = 'Bone armor'
        'Category_4' = 'Workbenches and base building'
        'Category_5' = 'Traps'
    }
    $hm = New-Object System.Text.StringBuilder
    [void]$hm.AppendLine($Marker)
    [void]$hm.AppendLine('{{Nav}}')
    [void]$hm.AppendLine('')
    [void]$hm.AppendLine("The '''HandMade''' catalog is the game's basic 'craft by hand' interface: recipes you can craft without any workbench, as long as you have the ingredients on you. All 10 [[Crafting|workbenches]] and most early-game tools live here, which makes HandMade the effective bootstrap for every progression path in ''[[Human Host]]''.")
    [void]$hm.AppendLine('')
    [void]$hm.AppendLine("Total: $(($script:HandMadeRecipes.Categories | ForEach-Object { $_.Recipes.Count } | Measure-Object -Sum).Sum) recipes across $($script:HandMadeRecipes.Categories.Count) categories.")
    [void]$hm.AppendLine('')
    foreach ($cat in $script:HandMadeRecipes.Categories) {
        $label = if ($handMadeCategoryLabel.ContainsKey($cat.CategoryLabel)) { $handMadeCategoryLabel[$cat.CategoryLabel] } else { $cat.CategoryLabel }
        [void]$hm.AppendLine("== $label ==")
        [void]$hm.AppendLine('')
        [void]$hm.AppendLine('{| class="wikitable sortable" style="font-size:0.9em;"')
        [void]$hm.AppendLine('! Output !! Qty !! Time (s) !! Inputs')
        foreach ($r in $cat.Recipes) {
            $outRaw = if ($script:GuidToName.ContainsKey($r.OutputGuid)) { $script:GuidToName[$r.OutputGuid] } else { $r.OutputGuid.Substring(0, [Math]::Min(8, $r.OutputGuid.Length)) }
            $outTitle = Get-DisplayName $outRaw
            if (-not $outTitle) {
                $s = $outRaw
                if ($s.EndsWith('_Icon')) { $s = $s.Substring(0, $s.Length - 5) }
                $outTitle = ($s -replace '^(RII_|BFI_|BTI_|BOI_|BWI_|FWI_|AXE_|DAG_|SPEAR_|BIG_|TOOL_|RWI_|EQI_|VPI_|BHC_|BAT_|BLADE_)', '') -replace '_',' '
            }
            $inputParts = @()
            foreach ($m in $r.Inputs) {
                $inRaw = if ($script:GuidToName.ContainsKey($m.Guid)) { $script:GuidToName[$m.Guid] } else { $m.Guid.Substring(0, [Math]::Min(8, $m.Guid.Length)) }
                $inTitle = Get-DisplayName $inRaw
                if (-not $inTitle) {
                    $s = $inRaw
                    if ($s.EndsWith('_Icon')) { $s = $s.Substring(0, $s.Length - 5) }
                    $inTitle = ($s -replace '^(RII_|BFI_|BTI_|BOI_|BWI_|FWI_|AXE_|DAG_|SPEAR_|BIG_|TOOL_|RWI_|EQI_|VPI_|BHC_|BAT_|BLADE_)', '') -replace '_',' '
                }
                $inputParts += "[[$inTitle]] x$($m.NeedCount)"
            }
            [void]$hm.AppendLine('|-')
            [void]$hm.AppendLine("| [[$outTitle]] || $($r.CraftNum) || $($r.CraftSeconds) || $($inputParts -join ', ')")
        }
        [void]$hm.AppendLine('|}')
        [void]$hm.AppendLine('')
    }
    [void]$hm.AppendLine("== Progression notes ==")
    [void]$hm.AppendLine('')
    [void]$hm.AppendLine("The HandMade catalog is the only way to place workbenches into the world. Every workbench recipe under ''Workbenches and base building'' is what lets you unlock the crafting tree it hosts. A common early-game path is: craft rope + wooden club + wooden stick by hand, harvest scrap iron from the world, use the HandMade [[Furnace]] recipe to smelt Forged Iron, then use HandMade to place the [[Anvil]] which unlocks the metal weapon tree (including the [[Iron Hammer]]).")
    [void]$hm.AppendLine('')
    [void]$hm.AppendLine("Because the game does not encode this as a hard recipe prerequisite (see [[Recipes]] for the shape of a recipe entry), it is a soft progression: nothing stops you from finding pre-built workbenches or scavenged ingredients out in the world, but the intended bootstrap goes through HandMade first.")
    [void]$hm.AppendLine('')
    [void]$hm.AppendLine('[[Category:Crafting]]')
    [void]$hm.AppendLine('[[Category:Reference]]')
    Write-Stub 'HandMade.mediawiki' $hm.ToString()
}

Write-Host "Tiers reference..." -ForegroundColor Cyan
if ($script:TierConfig) {
    $tc = $script:TierConfig
    $tier = New-Object System.Text.StringBuilder
    [void]$tier.AppendLine($Marker)
    [void]$tier.AppendLine('{{Nav}}')
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine("Every item in ''[[Human Host]]'' rolls one of $($tc.TierCount) quality tiers when it drops from loot or comes out of a workbench. Higher tiers hit harder, last longer, and are rarer. This page is the reference for the exact multipliers and the color codes the game paints on each item's inventory chip.")
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine('== Tier chart ==')
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine('{| class="wikitable" style="font-size:0.95em; text-align:center;"')
    [void]$tier.AppendLine('! Tier !! Name !! Color !! Hex !! Damage x !! Durability x !! Hitdown x !! Blade x !! Loot rate')
    for ($i = 0; $i -lt $tc.TierCount; $i++) {
        $c = $tc.TooltipColors[$i]
        $name = $script:TierNames[$i]
        $damage = $tc.DamageFactors[$i]
        $dura = $tc.DuraFactors[$i]
        $hd = $tc.HitDownFactors[$i]
        $blade = $tc.BladeHitFactors[$i]
        $rate = if ($i -lt $tc.RandomRates.Count) { "$([math]::Round($tc.RandomRates[$i] * 100, 1))%" } else { '-' }
        [void]$tier.AppendLine('|-')
        [void]$tier.AppendLine("| '''T$($i+1)''' || $name || style=""background:$($c.HexRgb);"" |  || <code>$($c.HexRgb)</code> || $damage || $dura || $hd || $blade || $rate")
    }
    [void]$tier.AppendLine('|}')
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine('== How tier scaling works ==')
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine("Each item has a set of base stats (damage, durability, hit-down probability, blade-slash probability) declared on its Icon_Info prefab. When the item is generated at a given tier, the runtime multiplies those base values by the tier's factor. A ''Tier 6'' Iron Hammer, for example, deals 10x the base damage and lasts 6x as long as its ''Tier 1'' counterpart, while its knockdown chance doubles from the base.")
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine("Loot rates reflect the runtime spawn weights before any player-perk modifiers (a Char_Skills ''_lootQualityRateBoost'' shifts the roll toward higher tiers).")
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine("Every weapon and tool page lists its own tier-scaling table under the ''Tier scaling'' heading, using this page's multipliers applied to the item's base stat line.")
    [void]$tier.AppendLine('')
    [void]$tier.AppendLine('[[Category:Reference]]')
    Write-Stub 'Tiers.mediawiki' $tier.ToString()
}

Write-Host "Recipes hub..." -ForegroundColor Cyan

# Bucket every item by its inferred category, then render one sortable table per category.
$itemsByCat = @{}
$hubItems = New-Object System.Collections.Generic.List[string]
foreach ($t in ($allRecipeItems | Sort-Object)) {
    if ($skipPlaceholders -contains $t) { continue }
    if ($t -match '^\d+ \d+ ') { continue }
    $hubItems.Add($t)
    $cat = Guess-MaterialCategory $t
    if (-not $itemsByCat.ContainsKey($cat)) { $itemsByCat[$cat] = New-Object System.Collections.Generic.List[string] }
    $itemsByCat[$cat].Add($t)
}

# Category ordering: raw materials and refined stock first, then components, then finished goods.
$categoryOrder = @(
    'Raw Material','Refined Metal','Salvage','Building Material','Component',
    'Electronic Component','Ammunition Component','Weapon Component','Melee Weapon',
    'Medical','Equipment','Vehicle Part','Material'
)
$orderedCats = @()
foreach ($c in $categoryOrder) { if ($itemsByCat.ContainsKey($c)) { $orderedCats += $c } }
foreach ($c in ($itemsByCat.Keys | Sort-Object)) { if ($orderedCats -notcontains $c) { $orderedCats += $c } }

$hub = New-Object System.Text.StringBuilder
[void]$hub.AppendLine($Marker)
[void]$hub.AppendLine('{{Nav}}')
[void]$hub.AppendLine('')
[void]$hub.AppendLine("Complete index of every material in the crafting graph, grouped by category. Each row shows which workbenches craft the item and how many downstream recipes consume it. Click any item to jump to its page for the full recipe details.")
[void]$hub.AppendLine('')
[void]$hub.AppendLine("Total: $($hubItems.Count) items across $($orderedCats.Count) categories. See [[Crafting]] for the workbench catalog.")
[void]$hub.AppendLine('')

# TOC linking to each category section
[void]$hub.AppendLine('== Categories ==')
[void]$hub.AppendLine('')
foreach ($c in $orderedCats) {
    $anchor = ($c -replace ' ', '_')
    [void]$hub.AppendLine("* [[#$anchor|$c]] ($($itemsByCat[$c].Count))")
}
[void]$hub.AppendLine('')

foreach ($c in $orderedCats) {
    [void]$hub.AppendLine("== $c ==")
    [void]$hub.AppendLine('')
    [void]$hub.AppendLine('{| class="wikitable sortable" style="font-size:0.9em;"')
    [void]$hub.AppendLine('! Item !! Produced by !! Used in (recipes)')
    foreach ($t in ($itemsByCat[$c] | Sort-Object)) {
        $producedBy = if ($script:ItemProducedBy.ContainsKey($t)) {
            ($script:ItemProducedBy[$t] | ForEach-Object { "[[$($_.workbench)]]" } | Select-Object -Unique) -join ', '
        } else { '(not craftable)' }
        $usedInCount = if ($script:ItemUsedIn.ContainsKey($t)) { $script:ItemUsedIn[$t].Count } else { 0 }
        [void]$hub.AppendLine('|-')
        [void]$hub.AppendLine("| [[$t]] || $producedBy || $usedInCount")
    }
    [void]$hub.AppendLine('|}')
    [void]$hub.AppendLine('')
}

[void]$hub.AppendLine('[[Category:Crafting]]')
Write-Stub 'Recipes.mediawiki' $hub.ToString()

# =============================================================================
# Redirects: old titles -> new localized titles.
# Emits stubs at each existing generated file location whose title differs from the localized one
# so links from external sources / bookmarks / hand-authored pages keep resolving.
# =============================================================================
Write-Host "Redirects for renamed pages..." -ForegroundColor Cyan
$redirectMap = @{}

# Build the old->new title map by re-running each generator's legacy naming vs its localized name
foreach ($assetId in $catalogAll.Ores) {
    $newTitle = Get-DisplayName $assetId
    if (-not $newTitle) { continue }
    $s = $assetId
    $oldTitle = if ($s.StartsWith('BO_Ore_'))   { ($s.Substring('BO_Ore_'.Length) -replace '_',' ') + ' Ore' }
                elseif ($s.StartsWith('BO_'))    { $s.Substring('BO_'.Length) -replace '_',' ' }
                else                              { $s -replace '_',' ' }
    if ($oldTitle -and $oldTitle -ne $newTitle) { $redirectMap[$oldTitle] = $newTitle }
}
foreach ($name in $catalogAll.Foods) {
    $newTitle = Get-DisplayName "FWI_${name}_Icon"
    $oldTitle = $name -replace '_',' '
    if ($newTitle -and $oldTitle -and $oldTitle -ne $newTitle) { $redirectMap[$oldTitle] = $newTitle }
}
foreach ($name in $catalogAll.Waters) {
    $newTitle = Get-DisplayName "FWI_${name}_Icon"
    $oldTitle = $name -replace '_',' '
    if ($newTitle -and $oldTitle -and $oldTitle -ne $newTitle) { $redirectMap[$oldTitle] = $newTitle }
}
$meleePrefixes = @('AXE_','DAG_','SPEAR_','BIG_')
foreach ($assetId in $catalogAll.MeleeWeapons) {
    $newTitle = Get-DisplayName $assetId
    $s = $assetId
    foreach ($p in $meleePrefixes) { if ($s.StartsWith($p)) { $s = $s.Substring($p.Length); break } }
    $oldTitle = $s -replace '_',' '
    if ($newTitle -and $oldTitle -and $oldTitle -ne $newTitle) { $redirectMap[$oldTitle] = $newTitle }
}
foreach ($assetId in $catalogAll.RangedWeapons) {
    $newTitle = Get-DisplayName $assetId
    $oldTitle = $assetId.Substring('RW_'.Length) -replace '_',' '
    if ($newTitle -and $oldTitle -and $oldTitle -ne $newTitle) { $redirectMap[$oldTitle] = $newTitle }
}
foreach ($assetId in $trapCatalog) {
    $newTitle = Get-DisplayName $assetId
    $n = $assetId; if ($n.StartsWith('BT_')) { $n = $n.Substring(3) }
    $oldTitle = $n -replace '_',' '
    if ($newTitle -and $oldTitle -and $oldTitle -ne $newTitle) { $redirectMap[$oldTitle] = $newTitle }
}
# Workbenches: old titles came from the WorkbenchLegacyTitles map
foreach ($prefab in $WorkbenchLegacyTitles.Keys) {
    if ($prefab -eq 'HandMade') { continue }
    $newTitle = Get-DisplayName $prefab
    $oldTitle = $WorkbenchLegacyTitles[$prefab]
    if ($newTitle -and $oldTitle -ne $newTitle) { $redirectMap[$oldTitle] = $newTitle }
}

# Orphan perk pages: the previous factor-derived generator used hand-authored titles that don't
# exist in the game. Map each to the closest real skill from All_Skills_Set where the intent is
# unambiguous; the rest fall through to the [[Perks]] hub as a soft landing.
$orphanPerkRedirects = @{
    'Adrenaline (Melee Damage)' = 'Adrenaline'
    'Adrenaline (Stamina)'      = 'Adrenaline'
    'Adrenaline (Water)'        = 'Adrenaline'
    'Athletic'                  = 'Marathon Champion'
    'Bare Knuckles'             = 'Martial Artist'
    'Blade Precision'           = 'Butcher'
    'Bow Damage'                = 'Archer'
    'Bow Steadying'             = 'Archer'
    'Efficient Metabolism'      = 'Slowed Metabolism'
    'Gunslinger'                = 'Gunsmith'
    'Handyman'                  = 'Repairman'
    'Intimidator'               = 'High Morale'
    'Melee Damage'              = 'Melee Master'
    'Silent Assassin'           = 'Lethal Ambush'
    'Silent Step'               = 'Perks'
    'Stamina Regen'             = 'Special Forces'
    'Steady Aim'                = 'Jacked Grunt'
    'Tight Groups'              = 'Sharpshooter'
    'Tough'                     = 'Vital Force'
    'Woodjack'                  = 'Lumberjack'
}
foreach ($old in $orphanPerkRedirects.Keys) {
    $redirectMap[$old] = $orphanPerkRedirects[$old]
}

# Delete the old perk pages themselves so the redirects in `pages/redirects/` are what remains
# under those titles. `Write-Redirect` replaces the existing bare `{{Stub}}` page since the old
# content has the HHWIKI:GENERATED marker, so the uploader posts the redirect over the top.
foreach ($oldTitle in $orphanPerkRedirects.Keys) {
    $slug = Slugify $oldTitle
    $oldPath = Join-Path $Pages "perks/$slug.mediawiki"
    if (Test-Path $oldPath) { Remove-Item $oldPath -Force }
}

Write-Host "  $($redirectMap.Count) redirects to emit"
foreach ($old in ($redirectMap.Keys | Sort-Object)) {
    $new = $redirectMap[$old]
    # Only emit if the old title differs and isn't identical after case-insensitive compare
    if ($old -ieq $new) { continue }
    $content = "#REDIRECT [[$new]]`n"
    Write-Redirect "redirects/$(Slugify $old).mediawiki" $content
}

Write-Host "" -ForegroundColor Green
Write-Host "[generator] done." -ForegroundColor Green
