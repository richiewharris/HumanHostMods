<#
.SYNOPSIS
    Extracts per-item runtime stats (damage, durability, stack, tags, ammo, etc.) from every
    Icon_Info prefab under the game's In_Use asset tree. Emits data/item-stats.json.

.DESCRIPTION
    Icon_Info prefabs are Unity MonoBehaviours dropped into Assets/In_Use/**/*_Icon.prefab.
    They carry the numeric stats we want in weapon/item infoboxes:
        _baseDamage           weapon base damage
        _headShotFactor       headshot multiplier
        _baseHitDownProb      hit-down probability (0-1)
        _baseBladeHitProb     blade slash probability (0-1)
        _blockDurability      shield/block durability
        _DuraCostPerAttack    durability drained per swing/shot
        _BaseMaxDurability    weapon/tool max durability
        _Can_Repair           whether a repair kit can restore it
        _RepairIconRef        addressable ref to the repair tool needed (if repairable)
        MaxStack              stack cap in inventory
        _Can_Stack            stack toggle
        _BuySellValue         vendor value
        _SlotType             equip slot enum
        _Tag                  primary tag ("MeleeWeapon", "RangedWeapon", "Food", "Water", ...)
        _Tags                 extra tags
        _maxMagCount          ranged mag size
        _noiseDistance        shot noise radius
        _fireRate             cyclic rate
        _ammoType             ammo GUID ref

    Ranged extras land on some prefabs: `_ammoType` GUID, `_maxMagCount`.
    The prefab's leaf directory is typically the internal item id (BIG_Iron_Hammer -> Iron_Hammer),
    and the prefab file name is `<id>_Icon.prefab`. We key by the file basename minus `_Icon`.

    This is best-effort YAML parsing (no full YAML lib on a stock Windows PS 5.1); we look for
    the top-level MonoBehaviour block that starts with `_Can_Stack:` and read the flat scalar
    fields it contains. Nested references like `_RepairIconRef.m_AssetGUID` are captured too.
#>
[CmdletBinding()]
param(
    [string] $IconsRoot = 'C:\Users\SMC\HumanHostMods\wiki\assets\raw\all_icons\ExportedProject\Assets\In_Use',
    [string] $Out       = 'C:\Users\SMC\HumanHostMods\wiki\data\item-stats.json'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $IconsRoot)) { throw "Missing IconsRoot: $IconsRoot" }

# Fields we recognize as scalar values on the Icon_Info MonoBehaviour block.
$ScalarFields = @(
    '_Can_Stack','MaxStack','_BaseMaxDurability','_Can_Repair','_SlotType','_Tag','_BuySellValue',
    '_blockDurability','_baseDamage','_baseHitDownProb','_baseBladeHitProb','_headShotFactor',
    '_maxMagCount','_noiseDistance','_fireRate','_DuraCostPerAttack','_ammoType'
)

$prefabs = Get-ChildItem $IconsRoot -Filter '*_Icon.prefab' -File -Recurse -ErrorAction SilentlyContinue
Write-Host "[stats] scanning $($prefabs.Count) Icon_Info prefabs..."

$entries = New-Object System.Collections.Generic.List[PSCustomObject]

# GUID -> internal_id map. Every Icon_Info prefab has a sibling .meta with a `guid:` line;
# we index each so downstream references (`_ammoTypeGuid`, `_RepairIconGuid`) can resolve
# back to a display id.
$guidToId = @{}
foreach ($f in $prefabs) {
    $metaPath = "$($f.FullName).meta"
    if (-not (Test-Path $metaPath)) { continue }
    $g = Get-Content $metaPath -TotalCount 3 | Select-String -Pattern '^guid:\s*([0-9a-f]{32})' | Select-Object -First 1
    if ($g) {
        $guid = $g.Matches[0].Groups[1].Value
        $internalId = $f.BaseName -replace '_Icon$',''
        if (-not $guidToId.ContainsKey($guid)) { $guidToId[$guid] = $internalId }
    }
}
Write-Host "[stats] indexed $($guidToId.Count) prefab GUIDs"

foreach ($f in $prefabs) {
    $internalId = $f.BaseName -replace '_Icon$',''
    $lines = Get-Content $f.FullName

    # State: track when we're inside a MonoBehaviour block that carries item stats.
    $inMB = $false
    $seenCanStack = $false
    $stats = @{}
    $repairGuid = $null
    $ammoGuid = $null
    $tooltipGuid = $null
    $modelGuid = $null
    $iconGuid = $null
    $disResGuid = $null
    $extraTags = New-Object System.Collections.Generic.List[string]
    $inTagsList = $false
    $inRepairRef = $false
    $inAmmoRef = $false
    $inTooltipRef = $false
    $inModelRef = $false
    $inIconRef = $false
    $inDisResRef = $false

    foreach ($line in $lines) {
        if ($line -match '^MonoBehaviour:') {
            $inMB = $true
            $seenCanStack = $false
            continue
        }
        if ($line -match '^--- !u!') {
            $inMB = $false
            $seenCanStack = $false
            continue
        }
        if (-not $inMB) { continue }

        # Track reference sub-blocks (indented under a named field).
        if ($line -match '^\s{2}(\w+):\s*$') {
            $fname = $matches[1]
            $inRepairRef  = ($fname -eq '_RepairIconRef')
            $inAmmoRef    = ($fname -eq '_ammoType')
            $inTooltipRef = ($fname -eq '_ToolTipText')
            $inModelRef   = ($fname -eq 'ModelRef')
            $inIconRef    = ($fname -eq 'Icon')
            $inDisResRef  = ($fname -eq '_DisResources')
            $inTagsList   = ($fname -eq '_Tags')
            continue
        }

        # Scalar top-level fields (two-space indent).
        if ($line -match '^\s{2}(\w+):\s+(.*)$') {
            $fname = $matches[1]; $fval = $matches[2].Trim()
            # Any new named field ends nested-block tracking except when the value is a brace-object.
            $inRepairRef = $inAmmoRef = $inTooltipRef = $inModelRef = $inIconRef = $inDisResRef = $false
            $inTagsList = $false

            if ($fname -eq '_Can_Stack') { $seenCanStack = $true }
            if ($fname -in $ScalarFields) {
                # Convert numeric-looking values to numbers so the JSON is typed properly.
                $num = 0.0
                if ([double]::TryParse($fval, [ref] $num)) {
                    if ($num -eq [math]::Floor($num) -and [math]::Abs($num) -lt 2147483647) {
                        $stats[$fname] = [int]$num
                    } else {
                        $stats[$fname] = $num
                    }
                } else {
                    $stats[$fname] = $fval
                }
                continue
            }

            # In-line asset refs (Icon: {fileID: ..., guid: xxx, type: 2})
            if ($fval -match 'guid:\s*([0-9a-f]{32})') {
                switch ($fname) {
                    'Icon'          { $iconGuid   = $matches[1] }
                    '_ToolTipText'  { $tooltipGuid= $matches[1] }
                    '_DisResources' { $disResGuid = $matches[1] }
                    default {}
                }
            }
            continue
        }

        # Extra-indent lines inside a nested ref block (four-space indent).
        if ($line -match '^\s{4}m_AssetGUID:\s*([0-9a-f]{32})') {
            $g = $matches[1]
            if     ($inRepairRef)  { $repairGuid  = $g }
            elseif ($inAmmoRef)    { $ammoGuid    = $g }
            elseif ($inTooltipRef) { $tooltipGuid = $g }
            elseif ($inModelRef)   { $modelGuid   = $g }
            elseif ($inIconRef)    { $iconGuid    = $g }
            elseif ($inDisResRef)  { $disResGuid  = $g }
            continue
        }

        # _Tags list entries look like: `  - some_tag`
        if ($inTagsList -and $line -match '^\s{2}-\s+(.+)$') {
            $extraTags.Add($matches[1].Trim())
        }
    }

    # Only emit prefabs that actually looked like Icon_Info items (they always have _Can_Stack).
    if (-not $seenCanStack) { continue }

    $obj = [ordered]@{
        internal_id = $internalId
    }
    foreach ($k in $ScalarFields) {
        if ($stats.ContainsKey($k)) { $obj[$k] = $stats[$k] }
    }
    if ($extraTags.Count -gt 0) { $obj['_Tags'] = $extraTags.ToArray() }
    if ($repairGuid)  {
        $obj['_RepairIconGuid'] = $repairGuid
        if ($guidToId.ContainsKey($repairGuid)) { $obj['_RepairIconId'] = $guidToId[$repairGuid] }
    }
    if ($ammoGuid)    {
        $obj['_ammoTypeGuid']   = $ammoGuid
        if ($guidToId.ContainsKey($ammoGuid))   { $obj['_ammoTypeId']   = $guidToId[$ammoGuid] }
    }
    if ($tooltipGuid) { $obj['_ToolTipGuid']    = $tooltipGuid }
    if ($modelGuid)   { $obj['ModelGuid']       = $modelGuid }
    if ($iconGuid)    { $obj['IconGuid']        = $iconGuid }
    if ($disResGuid)  { $obj['_DisResGuid']     = $disResGuid }

    $entries.Add([PSCustomObject]$obj)
}

Write-Host "[stats] extracted $($entries.Count) item stat records"

$json = $entries | Sort-Object internal_id | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[stats] wrote $Out"

# Quick summary of what we picked up
$withDamage    = ($entries | Where-Object { $_._baseDamage    -gt 0 }).Count
$withDurability= ($entries | Where-Object { $_._BaseMaxDurability -gt 0 }).Count
$withStack     = ($entries | Where-Object { $_.MaxStack -gt 1 }).Count
$withMag       = ($entries | Where-Object { $_._maxMagCount   -gt 0 }).Count
Write-Host "[stats] $withDamage with baseDamage>0, $withDurability with durability>0, $withStack with stack>1, $withMag with mag>0"
