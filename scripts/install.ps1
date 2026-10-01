<#
.SYNOPSIS
    Builds Ledger in Release mode (or reuses the last build if the source hasn't changed), installs
    it for the current user and creates shortcuts.

.DESCRIPTION
    Program files go to %LOCALAPPDATA%\Programs\Ledger. Your financial data lives
    separately in %LOCALAPPDATA%\Ledger and is never touched by this script.
    Re-run it at any time to update the installed copy.

.PARAMETER StartMenu
    Also add a Start menu shortcut.

.PARAMETER Rebuild
    Build even if the source hasn't changed since the last build (normally it's reused).
#>
param([switch]$StartMenu, [switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Ledger'
$exe = Join-Path $installDir 'Ledger.exe'

$running = Get-Process Ledger -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    throw 'Ledger is running from the install folder. Close it, then run this script again.'
}

. (Join-Path $PSScriptRoot 'publish-common.ps1')
$staging = Publish-Ledger $repo -Force:$Rebuild

Write-Host "Installing to $installDir..."
New-Item -ItemType Directory -Force $installDir | Out-Null
# /MIR keeps the install folder an exact copy of the build (it only ever holds program files),
# except the uninstaller and its notes if Ledger-Setup installed this copy before.
robocopy $staging $installDir /MIR /XF 'Uninstall Ledger.exe' install-info.txt /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy exit $LASTEXITCODE)." }

function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $installDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Ledger - personal finance, kept on this PC'
    $lnk.Save()
    Write-Host "Shortcut: $path"
}

New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Ledger.lnk')
if ($StartMenu) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Ledger.lnk')
}

Write-Host 'Done.'
