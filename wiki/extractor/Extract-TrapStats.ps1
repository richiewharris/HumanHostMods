<#
.SYNOPSIS
    Extracts per-trap runtime stats (damage, trigger interval, self-damage, allow-ally, subclass-
    specific fields) from every trap prefab under Assets/In_Use/Conts/Mods/Player_Made/Trap.
    Emits data/trap-stats.json.

.DESCRIPTION
    Each trap prefab hosts a Trap_Base subclass MonoBehaviour (Trap_Spike, Trap_Laser, Trap_RotBlade,
    Trap_SensorSpike). We walk the prefab YAML, capture the fields we can render on wiki pages, and
    emit one record per prefab keyed by its filename minus `.prefab`.

    Class fields we recognize (all live on Trap_Base or one of its subclasses in Trap.dll):
        _isDynamicTrap        static vs dynamic placement
        _AllowHitAlly         whether the trap can damage players/pets
        _TriggerInterval      seconds between triggers
        _TrapDamage           damage per trigger
        _HitReact             hit-react coefficient
        _TrapSelfDmg          durability drained per trigger
        _AllowSelfSmash       trap can be smashed by a solid impact
        _LaserContinueSeconds Laser trap's beam duration
        _PerZombieHitInterval Laser trap's per-target retrigger cooldown
        _RotSeconds           RotBlade / RotSaw rotation period
        _RotSpeedFactor       RotBlade / RotSaw speed scale
        _spikeBackSeconds     SensorSpike retract delay
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\Users\SMC\HumanHostMods\wiki\assets\raw\main\ExportedProject\Assets\In_Use\Conts\Mods\Player_Made\Trap',
    [string] $Out  = 'C:\Users\SMC\HumanHostMods\wiki\data\trap-stats.json'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Root)) { throw "Missing trap prefab root: $Root" }

$ScalarFields = @(
    '_isDynamicTrap','_AllowHitAlly','_TriggerInterval','_TrapDamage','_HitReact','_TrapSelfDmg',
    '_AllowSelfSmash','_LaserContinueSeconds','_PerZombieHitInterval','_RotSeconds',
    '_RotSpeedFactor','_spikeBackSeconds','_spikeStartLoPosY','_spikeEndLoPosY'
)

$prefabs = Get-ChildItem $Root -Filter '*.prefab' -File -Recurse
Write-Host "[trap] scanning $($prefabs.Count) trap prefabs..."

$entries = New-Object System.Collections.Generic.List[PSCustomObject]
foreach ($f in $prefabs) {
    $id = $f.BaseName
    $lines = Get-Content $f.FullName -Encoding UTF8
    # Two-pass: buffer each MonoBehaviour block, then keep only blocks containing `_TrapDamage`
    # (the Trap_Base signature). This way fields serialized before `_TrapDamage` in the block
    # (like `_isDynamicTrap`, `_AllowHitAlly`, `_TriggerInterval`) are captured too.
    # Split the prefab into Unity YAML blocks on any `--- !u!<n>` delimiter, then process each.
    # Prefabs start with `%YAML` headers before the first block; those get dropped safely.
    # Split the prefab into Unity YAML blocks on any `--- !u!<n>` delimiter, then process each.
    # Prefabs start with `%YAML` headers before the first block; those get dropped safely.
    # Note: PowerShell's `if ($list)` returns false for an empty collection, so we test $null explicitly.
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
    Write-Verbose "[$id] parsed $($blocks.Count) blocks"
    foreach ($block in $blocks) {
        $hasTrapDamage = $false
        foreach ($bl in $block) { if ($bl -match '_TrapDamage:') { $hasTrapDamage = $true; break } }
        if (-not $hasTrapDamage) { continue }
        Write-Verbose "[$id] found Trap_* block (size=$($block.Count))"
        foreach ($bl in $block) {
            if ($bl -match '^\s{2}(\w+):\s+(\S.*)$') {
                $name = $matches[1]; $val = $matches[2].Trim()
                if ($name -in $ScalarFields) {
                    $num = 0.0
                    if ([double]::TryParse($val, [ref] $num)) {
                        $stats[$name] = if ($num -eq [math]::Floor($num) -and [math]::Abs($num) -lt 2147483647) {
                            [int]$num
                        } else { $num }
                    } else {
                        $stats[$name] = $val
                    }
                }
            }
        }
        break  # only the first Trap_* block per prefab
    }
    if ($stats.Count -eq 0) { continue }
    $obj = [ordered]@{ internal_id = $id }
    foreach ($k in $ScalarFields) {
        if ($stats.ContainsKey($k)) { $obj[$k] = $stats[$k] }
    }
    $entries.Add([PSCustomObject]$obj)
}

Write-Host "[trap] captured stats for $($entries.Count) trap(s)"
$json = $entries | Sort-Object internal_id | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[trap] wrote $Out"
