<#
.SYNOPSIS
  Closes Resonite, builds and installs InventorySearch into rml_mods, and starts Resonite again in desktop mode.

.DESCRIPTION
  Adapted from the Worlds tab project's tools\redeploy-resonite.ps1 (same procedure). Resonite locks the mod DLL while it
  runs, so installing needs the game closed. This asks the game to close the way the window's close button does (it gets a
  chance to save), waits for every Resonite process to exit, builds with DeployToGame, then starts the game.

  It never kills the game: if Resonite has not exited after -CloseTimeoutSeconds the script stops and nothing is built.

  Launching: NOT steam://rungameid or steam://run. Resonite has several launch options, so Steam waits on a chooser dialog
  and never starts it. Start Resonite.exe directly with -Screen (desktop mode); that works while Steam is running.

.PARAMETER NoLaunch   Build and install only; do not start the game afterwards.
.PARAMETER LaunchArgs Arguments for Resonite.exe; the default -Screen starts desktop mode.
.PARAMETER NoClose    Do not close the game (the build fails if it is still running).
#>
param(
    [switch]$NoLaunch,
    [switch]$NoClose,
    [int]$CloseTimeoutSeconds = 90,
    [string]$LaunchArgs = '-Screen'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$names = 'Resonite', 'Renderite.Host', 'Renderite.Renderer'

function Get-ResoniteProcesses { Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.Name } }

if (-not $NoClose) {
    $running = @(Get-ResoniteProcesses)
    if ($running.Count -gt 0) {
        Write-Host "Closing Resonite ($($running.Count) processes)..."
        foreach ($p in $running) { if ($p.MainWindowHandle -ne 0) { [void]$p.CloseMainWindow() } }
        $deadline = (Get-Date).AddSeconds($CloseTimeoutSeconds)
        while ((Get-ResoniteProcesses) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
        if (Get-ResoniteProcesses) { throw "Resonite did not exit within $CloseTimeoutSeconds seconds. Close it by hand and run this again." }
    }
}

Write-Host 'Building and installing the mod...'
Push-Location $repo
try {
    dotnet build src -c Release -p:DeployToGame=true
    if ($LASTEXITCODE -ne 0) { throw 'Build or install failed.' }
}
finally { Pop-Location }

if (-not $NoLaunch) {
    Write-Host "Starting Resonite ($LaunchArgs)..."
    $dir = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Resonite'
    if (-not (Test-Path "$dir\Resonite.exe")) { $dir = 'E:\Program Files (x86)\Steam\steamapps\common\Resonite' }
    Start-Process -FilePath "$dir\Resonite.exe" -ArgumentList $LaunchArgs -WorkingDirectory $dir
}

Write-Host 'Done.'
