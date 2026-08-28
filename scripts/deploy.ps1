<#
    Manually copy all built plugin DLLs into the game's BepInEx/plugins/HHMods folder.
    Use this when the MSBuild AfterBuild copy fails (usually due to Program Files ACLs).

    Usage:
      pwsh -File scripts\deploy.ps1                    # uses default game path
      pwsh -File scripts\deploy.ps1 -GameDir "D:\..."  # explicit path
      pwsh -File scripts\deploy.ps1 -Configuration Release
#>
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Human Host",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pluginsDir = Join-Path $GameDir "BepInEx\plugins\HHMods"

if (-not (Test-Path $GameDir)) { throw "Game directory not found: $GameDir" }
if (-not (Test-Path $pluginsDir)) { New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null }

$projects = @('HHMods.Core','HHMods.Minimap','HHMods.Perks','HHMods.QoL','HHMods.Vehicles','HHMods.Weapons','HHMods.Recipes')
$deployed = 0
foreach ($p in $projects) {
    $dll = Join-Path $root "src\$p\bin\$Configuration\$p.dll"
    if (Test-Path $dll) {
        Copy-Item -Path $dll -Destination $pluginsDir -Force
        $pdb = [System.IO.Path]::ChangeExtension($dll, '.pdb')
        if (Test-Path $pdb) { Copy-Item -Path $pdb -Destination $pluginsDir -Force }
        Write-Host "  [ok] $p.dll" -ForegroundColor Green
        $deployed++
    } else {
        Write-Host "  [skip] $p.dll not found (not built yet)" -ForegroundColor Yellow
    }
}
Write-Host "`nDeployed $deployed plugin(s) to $pluginsDir"
