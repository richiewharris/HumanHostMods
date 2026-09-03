<#
.SYNOPSIS
    Extracts basic per-enemy stats (HP, stamina, food/water) from the dumped zb_* bundle prefabs.
    Emits data/enemies.json.

.DESCRIPTION
    Each `zb_<biome>_assets_all.*.bundle` dump contains a set of Z_Man_*.prefab GameObjects. Each
    prefab hosts a Char_Status MonoBehaviour with public serialized fields we can read as YAML:
        _maxHP, _origMaxHP, _maxStamina, _stamRegePerSec
        _EnableFoodWater, _maxFood, _maxWater
        _foodWaterInterval, _foodCostPerTime, _waterCostPerTime
        _DnaDmgMaxHp_Factor

    Zombie_Input-specific fields (is_Boss, _mutantLevel, _isRushAttack) come through empty in the
    ripper output because the class layout isn't resolved by AssetRipper for these prefabs, so
    those live only on the runtime and are not captured here. HP + stamina are enough for a first-
    pass enemy catalog on the wiki.
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\Users\SMC\HumanHostMods\wiki\assets\raw',
    [string] $Out  = 'C:\Users\SMC\HumanHostMods\wiki\data\enemies.json'
)

$ErrorActionPreference = 'Stop'

# Fields to capture from any MonoBehaviour block that carries `_maxHP` (the Char_Status signature).
$ScalarFields = @(
    '_maxHP','_origMaxHP','_maxStamina','_origMaxStamina','_stamRegePerSec',
    '_EnableFoodWater','_maxFood','_maxWater','_foodWaterInterval',
    '_foodCostPerTime','_waterCostPerTime','_DnaDmgMaxHp_Factor'
)

# Discover all zb_* bundle dumps and enumerate their zombie prefabs.
$biomeDirs = Get-ChildItem $Root -Directory -Filter 'zb_*' -ErrorAction SilentlyContinue
if (-not $biomeDirs) { throw "No zb_* dumps under $Root; run AssetRipper first." }

$entries = New-Object System.Collections.Generic.List[PSCustomObject]

foreach ($biomeDir in $biomeDirs) {
    $biome = $biomeDir.Name -replace '^zb_',''
    $prefabRoot = Join-Path $biomeDir.FullName 'ExportedProject\Assets\GameObject'
    if (-not (Test-Path $prefabRoot)) { continue }

    # Only mainline zombie prefabs: Z_Man_XX.prefab, not the _NoHead / Dead_Face / debris variants.
    $prefabs = Get-ChildItem $prefabRoot -Filter 'Z_*.prefab' -File | Where-Object {
        $_.BaseName -notmatch '(NoHead|_Dead|_Head|_Body|_Corpse|Ragdoll)$'
    }
    Write-Host "[enemy] $biome : $($prefabs.Count) zombie prefab(s)"
    foreach ($f in $prefabs) {
        $lines = Get-Content $f.FullName -Encoding UTF8
        # Two-pass block parse (see Extract-TrapStats for the pattern).
        $blocks = New-Object System.Collections.Generic.List[System.Collections.Generic.List[string]]
        $currentBlock = $null
        foreach ($line in $lines) {
            if ($line -match '^---\s+!u!\d+') {
                if ($null -ne $currentBlock) { $blocks.Add($currentBlock) }
                $currentBlock = New-Object System.Collections.Generic.List[string]
                continue
            }
            if ($null -ne $currentBlock) { [void]$currentBlock.Add($line) }
        }
        if ($null -ne $currentBlock) { $blocks.Add($currentBlock) }

        $stats = @{}
        foreach ($block in $blocks) {
            $hasMaxHP = $false
            foreach ($bl in $block) { if ($bl -match '^\s{2}_maxHP:') { $hasMaxHP = $true; break } }
            if (-not $hasMaxHP) { continue }
            foreach ($bl in $block) {
                if ($bl -match '^\s{2}(\w+):\s+(\S.*)$') {
                    $name = $matches[1]; $val = $matches[2].Trim()
                    if ($name -in $ScalarFields) {
                        $num = 0.0
                        if ([double]::TryParse($val, [ref] $num)) {
                            $stats[$name] = if ($num -eq [math]::Floor($num) -and [math]::Abs($num) -lt 2147483647) { [int]$num } else { $num }
                        } else { $stats[$name] = $val }
                    }
                }
            }
            break
        }
        if ($stats.Count -eq 0) { continue }

        $obj = [ordered]@{
            biome        = $biome
            internal_id  = $f.BaseName
        }
        foreach ($k in $ScalarFields) {
            if ($stats.ContainsKey($k)) { $obj[$k] = $stats[$k] }
        }
        $entries.Add([PSCustomObject]$obj)
    }
}

Write-Host "[enemy] captured $($entries.Count) enemy prefabs across $($biomeDirs.Count) biome bundles"
$json = $entries | Sort-Object biome, internal_id | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[enemy] wrote $Out"

# Summary
$byBiome = $entries | Group-Object biome | Sort-Object Name
foreach ($g in $byBiome) {
    $names = ($g.Group | ForEach-Object { $_.internal_id }) -join ', '
    Write-Host "  $($g.Name) ($($g.Count)): $names"
}
