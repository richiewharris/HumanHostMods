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

function Slugify {
    # Wiki-page filename convention: use exact page title with spaces → underscores, safe for the filesystem.
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

# Mapping from a wiki page title to its icon filename (as uploaded to Fandom as File:X.png).
# Returns $null if we have no known icon; the infobox skips the image field in that case.
function Get-IconFilename {
    param([string] $Title, [string] $Category)
    switch ($Category) {
        'Ore' {
            # Ores split into "* Ore" (Ore_<Name>.png) and standalone rocks (<Name>.png)
            if ($Title -like '* Ore') {
                $base = $Title.Substring(0, $Title.Length - 4).Trim()
                return "Ore_$($base -replace ' ', '_').png"
            }
            return "$($Title -replace ' ', '_').png"
        }
        'Workbench' {
            switch ($Title) {
                'Anvil Workbench'        { return 'WB_Anvil.png' }
                'Biochemical Workbench'  { return 'WB_Biochemical_Workbench.png' }
                'Campfire'               { return 'WB_Campfire.png' }
                'Carpentry Workbench'    { return 'WB_Carpentry.png' }
                'Cement Mixer'           { return 'WB_Concrete_Mixer.png' }
                'Cutting Workbench'      { return 'WB_Cutting.png' }
                'Electronics Workbench'  { return 'WB_Electronic.png' }
                'Furnace'                { return 'WB_Furnace.png' }
                'Gun Workbench'          { return 'WB_Gunsmith.png' }
                'Mechanical Workbench'   { return 'WB_Mechanical.png' }
                'HandMade'               { return $null }
                default                  { return $null }
            }
        }
        default {
            return "$($Title -replace ' ', '_').png"
        }
    }
}

# Wiki-page-title -> workbench-prefab-name mapping for the recipe lookup.
$WorkbenchPrefabMap = @{
    'Anvil Workbench'        = 'WB_Anvil'
    'Biochemical Workbench'  = 'WB_Biochemical'
    'Campfire'               = 'WB_Campfire'
    'Carpentry Workbench'    = 'WB_Carpentry'
    'Cement Mixer'           = 'WB_Concrete_Mixer'
    'Cutting Workbench'      = 'WB_Cutting'
    'Electronics Workbench'  = 'WB_Electronic'
    'Furnace'                = 'WB_Furnace'
    'Gun Workbench'          = 'WB_Gunsmith'
    'Mechanical Workbench'   = 'WB_Mechanical'
    'HandMade'               = $null
}

# Load recipes once at generator start.
$recipesPath = Join-Path $Data 'recipes.json'
$recipesData = if (Test-Path $recipesPath) { Get-Content $recipesPath -Raw | ConvertFrom-Json } else { $null }

# Wiki-page-title -> workbench-prefab-name reverse map for the recipe cross-refs.
$PrefabToTitleMap = @{}
foreach ($k in $WorkbenchPrefabMap.Keys) {
    $prefab = $WorkbenchPrefabMap[$k]
    if ($prefab) { $PrefabToTitleMap[$prefab] = $k }
}

# ---------------------------------------------------------------------------
# Recipe item normalization + cross-ref index
# ---------------------------------------------------------------------------

# Regex matching the tier-variant shape pattern: "<tier>_<sub>_<shape...>_<material>"
# Examples: "1_1_Block_Damaged_Planks", "3_5_Half_Cylinder_L_Titanium_Mesh_Concrete"
$TierVariantRegex = '^[1-7]_[1-9]_'

# Normalize a raw recipe asset id into a wiki page title. Handles:
#   - Prefix / suffix stripping (via Prettify-AssetId)
#   - Ore rotation: "Ore Iron" -> "Iron Ore"
#   - Tier-variant collapse: "1_1_Block_Damaged_Planks" -> "Damaged Planks"
function Normalize-RecipeItemName {
    param([string] $RawId)
    if (-not $RawId) { return '' }
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

if ($recipesData) {
    foreach ($wb in $recipesData.workbenches) {
        $wbTitle = if ($PrefabToTitleMap.ContainsKey($wb.workbench_prefab)) { $PrefabToTitleMap[$wb.workbench_prefab] } else { $wb.workbench_prefab }
        foreach ($rec in $wb.recipes) {
            $outTitle = Normalize-RecipeItemName $rec.output_name
            if (-not $outTitle) { continue }
            $inputList = @($rec.inputs | ForEach-Object {
                [PSCustomObject]@{ name = (Normalize-RecipeItemName $_.name); qty = $_.need_count }
            })
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
    $produced = if ($script:ItemProducedBy.ContainsKey($ItemTitle)) { $script:ItemProducedBy[$ItemTitle] } else { @() }
    $used     = if ($script:ItemUsedIn.ContainsKey($ItemTitle))     { $script:ItemUsedIn[$ItemTitle] }     else { @() }

    if ($produced.Count -gt 0) {
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

    if ($used.Count -gt 0) {
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

    $content = @"
$Marker
{{Nav}}
{{Stub}}

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

== Vehicles ==

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

foreach ($c in $crates) {
    $content = @"
$Marker
{{Nav}}
{{Stub}}

The '''$($c.PageTitle)''' is a lootable container variant that spawns in the [[$($c.Biome)]] biome of ''[[Human Host]]''.

== Contents ==

Contents scale to the biome tier. Common items include salvage, ammunition, and biome-appropriate resources.

== Location ==

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
    $info = if ($wbInfo.ContainsKey($w.Name)) { $wbInfo[$w.Name] } else { @{ title=$w.Name; desc='TBD.' } }
    $iconLine = ''
    $icon = Get-IconFilename -Title $info.title -Category 'Workbench'
    if ($icon) { $iconLine = "| image    = $icon`n" }
    # Recipe table from the parsed data
    $prefab = $WorkbenchPrefabMap[$info.title]
    $recipeTable = Build-RecipeTable $prefab
    $recipeSection = if ($recipeTable) { $recipeTable } else { 'No parsed recipes for this workbench yet.' }

    $content = @"
$Marker
{{Nav}}
{{Stub}}

{{Infobox station
| name     = $($info.title)
$iconLine| category = Crafting
}}

The '''$($info.title)''' is one of the eleven workbench types in ''[[Human Host]]''.

$($info.desc)

Internal enum value: <code>$($w.EnumName).$($w.Name)</code> (assembly <code>$($w.Assembly)</code>). Prefab: <code>$prefab</code>.

== Recipes ==

$recipeSection

== Placement ==

TBD: build cost and placement footprint.

== Prerequisites ==

TBD: which workbench(es) this depends on.

== See also ==

* [[Crafting]]: the full workbench catalog

[[Category:Crafting Stations]]
"@
    Write-Stub "crafting/$(Slugify $info.title).mediawiki" $content
}

# =============================================================================
# Perks -- one page per skill factor
# =============================================================================
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

# =============================================================================
# Traps (from the addressables catalog: BT_ prefix, 15 asset variants)
# =============================================================================
Write-Host "Traps..." -ForegroundColor Cyan
$catalog = Get-Content (Join-Path $Data 'asset_catalog.json') -Raw | ConvertFrom-Json
$trapCatalog = @($catalog.Traps)
$trapClasses = @(Read-JsonArray 'trap_classes')  # 4 Cecil-derived classes for technical context

foreach ($assetId in $trapCatalog) {
    # BT_Sensor_Spike -> "Sensor Spike"
    $name = $assetId
    if ($name.StartsWith('BT_')) { $name = $name.Substring(3) }
    $pageTitle = $name -replace '_', ' '

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

    $content = @"
$Marker
{{Nav}}
{{Stub}}

The '''$pageTitle''' is a trap in ''[[Human Host]]'', used for base defense against horde nights.

== Behavior ==

TBD: trigger conditions, damage, cooldown.

== Placement ==

TBD.

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
    # Icon reference. Category strings passed in are the display categories ("Melee Weapons",
    # "Ammunition") but Get-IconFilename expects our simpler taxonomy ("Ore", "Workbench").
    $iconCat = switch ($Category) {
        'Ore' { 'Ore' }
        default { $null }  # everything else uses direct name -> Name.png
    }
    $icon = Get-IconFilename -Title $Title -Category $iconCat
    if ($icon) { $fields.Add("| image      = $icon") }
    $fields.Add("| type       = $Category")
    foreach ($k in $ExtraFields.Keys) { $fields.Add("| $k = $($ExtraFields[$k])") }
    $fieldBlock = $fields -join "`n"

    $script:GeneratedTitles[$Title] = $true
    $crossRefs = Build-CrossRefSections $Title
    return @"
$Marker
{{Nav}}
{{Stub}}

{{Infobox $InfoboxType
$fieldBlock
}}

The '''$Title''' is a $($Category.ToLower()) in ''[[Human Host]]''.

== Description ==

TBD.

== Source ==

TBD.

$crossRefs
== Technical ==

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
    # BO_Ore_Iron -> "Iron Ore"; BO_Sulfur -> "Sulfur"; BO_Lapis_Lazuli -> "Lapis Lazuli"
    $s = $assetId
    if ($s.StartsWith('BO_Ore_')) { $title = ($s.Substring('BO_Ore_'.Length) -replace '_', ' ') + ' Ore' }
    elseif ($s.StartsWith('BO_')) { $title = $s.Substring('BO_'.Length) -replace '_', ' ' }
    else { $title = $s -replace '_', ' ' }
    Write-Stub "items/ores/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category 'Ore' -ParentPage 'Mining' -InfoboxType 'item' -ExtraFields @{ source = 'Mined from terrain' })
}

# --- Food items --------------------------------------------------------------
Write-Host "Food items..." -ForegroundColor Cyan
foreach ($name in $catalogAll.Foods) {
    $title = $name -replace '_', ' '
    $assetId = "FWI_${name}_Icon"
    $src = if ($name -match 'Canned|Sausage') { 'Looted from containers and zombie corpses' } else { 'Foraged in the wild' }
    Write-Stub "items/food/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category 'Food' -ParentPage 'Food and Water' -InfoboxType 'item' -ExtraFields @{ source = $src })
}

# --- Water items -------------------------------------------------------------
Write-Host "Water items..." -ForegroundColor Cyan
foreach ($name in $catalogAll.Waters) {
    $title = $name -replace '_', ' '
    $assetId = "FWI_${name}_Icon"
    Write-Stub "items/water/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category 'Water' -ParentPage 'Food and Water' -InfoboxType 'item' -ExtraFields @{ source = 'Looted from containers and zombie corpses' })
}

# --- Melee weapons -----------------------------------------------------------
Write-Host "Melee weapons..." -ForegroundColor Cyan
$meleePrefixMap = @{ 'AXE_' = 'Axe'; 'DAG_' = 'Blade'; 'SPEAR_' = 'Spear'; 'BIG_' = 'Blunt' }
foreach ($assetId in $catalogAll.MeleeWeapons) {
    $prefix = ($meleePrefixMap.Keys | Where-Object { $assetId.StartsWith($_) } | Select-Object -First 1)
    $subtype = if ($prefix) { $meleePrefixMap[$prefix] } else { 'Melee' }
    $stripped = if ($prefix) { $assetId.Substring($prefix.Length) } else { $assetId }
    $title = $stripped -replace '_', ' '
    Write-Stub "items/melee/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category 'Melee Weapons' -ParentPage 'Melee Weapons' -InfoboxType 'weapon' -ExtraFields @{ type = 'Melee'; subtype = $subtype; workbench = '[[Anvil Workbench]] or [[Carpentry Workbench]]' })
}

# --- Ranged weapons + ammo + optics -----------------------------------------
Write-Host "Ranged items..." -ForegroundColor Cyan
foreach ($assetId in $catalogAll.RangedWeapons) {
    $name = $assetId.Substring('RW_'.Length)
    $title = $name -replace '_', ' '
    # Category by naming pattern
    if ($name -match 'AmmoBox$')       { $cat = 'Ammunition'; $sub = 'Cased' }
    elseif ($name -match '^Arrow_')    { $cat = 'Ammunition'; $sub = 'Arrow' }
    elseif ($name -match '^Bow_')      { $cat = 'Ranged Weapons'; $sub = 'Bow' }
    elseif ($name -match 'Sight$|^Scope_') { $cat = 'Weapon Attachments'; $sub = 'Optic' }
    else                                { $cat = 'Ranged Weapons'; $sub = 'Firearm' }
    $parent = if ($cat -eq 'Ammunition') { 'Ammunition' } elseif ($cat -eq 'Weapon Attachments') { 'Weapons' } else { 'Ranged Weapons' }
    Write-Stub "items/ranged/$(Slugify $title).mediawiki" (New-ItemStub -Title $title -AssetId $assetId -Category $cat -ParentPage $parent -InfoboxType 'weapon' -ExtraFields @{ type = 'Ranged'; subtype = $sub })
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
    'Anvil Workbench','Biochemical Workbench','Campfire','Carpentry Workbench','Cement Mixer',
    'Cutting Workbench','Electronics Workbench','Furnace','Gun Workbench','HandMade','Mechanical Workbench',
    'Bladed Pole','IronPipe Trap','IronSpike Single','IronSpike Trap','Laser Trap','LogSpike Single',
    'LogSpike Trap','Nail Trap','Rotating Blade','Rotating Saw','Sensor Spike','SpearSpike Single',
    'SpearSpike Trap','Spiked Great Trunk','Spiked Trunk'
)
foreach ($t in $knownHandwritten) { $script:GeneratedTitles[$t] = $true }

$skipPlaceholders = @('WIP','Recipe Sack','BurningFire')

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
    $fieldBlock = $fields -join "`n"
    $crossRefs = Build-CrossRefSections $title

    $content = @"
$Marker
{{Nav}}
{{Stub}}

{{Infobox item
$fieldBlock
}}

'''$title''' is a $($cat.ToLower()) in ''[[Human Host]]''. Auto-generated from the crafting-recipe graph; see [[Crafting]] for the full workbench catalog.

$crossRefs
== See also ==

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
Write-Host "Recipes hub..." -ForegroundColor Cyan
$hub = New-Object System.Text.StringBuilder
[void]$hub.AppendLine($Marker)
[void]$hub.AppendLine('{{Nav}}')
[void]$hub.AppendLine('')
[void]$hub.AppendLine("Alphabetical index of every material in the crafting graph. For each item, the ''Produced by'' column lists the workbenches that craft it, and the ''Used in'' column shows how many recipes consume it (click the item for details).")
[void]$hub.AppendLine('')
[void]$hub.AppendLine("Total items: $($allRecipeItems.Count). See [[Crafting]] for the workbench catalog.")
[void]$hub.AppendLine('')
[void]$hub.AppendLine('{| class="wikitable sortable" style="font-size:0.9em;"')
[void]$hub.AppendLine('! Item !! Category !! Produced by !! Used in (recipes)')
foreach ($t in ($allRecipeItems | Sort-Object)) {
    if ($skipPlaceholders -contains $t) { continue }
    if ($t -match '^\d+ \d+ ') { continue }
    $producedBy = if ($script:ItemProducedBy.ContainsKey($t)) {
        ($script:ItemProducedBy[$t] | ForEach-Object { "[[$($_.workbench)]]" } | Select-Object -Unique) -join ', '
    } else { '(not craftable)' }
    $usedInCount = if ($script:ItemUsedIn.ContainsKey($t)) { $script:ItemUsedIn[$t].Count } else { 0 }
    $cat = Guess-MaterialCategory $t
    [void]$hub.AppendLine('|-')
    [void]$hub.AppendLine("| [[$t]] || $cat || $producedBy || $usedInCount")
}
[void]$hub.AppendLine('|}')
[void]$hub.AppendLine('')
[void]$hub.AppendLine('[[Category:Crafting]]')
Write-Stub 'Recipes.mediawiki' $hub.ToString()

Write-Host "" -ForegroundColor Green
Write-Host "[generator] done." -ForegroundColor Green
