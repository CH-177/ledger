<#
.SYNOPSIS
    Builds the shareable Windows release: a one-file installer and a portable zip.

.DESCRIPTION
    Output goes to dist\Windows-v<version>\:
      Ledger-Setup-<version>.exe       Installs for the current user (no admin rights), with desktop
                                       and Start menu shortcuts and an entry in Installed apps.
      Ledger-<version>-portable.zip    Unzip and run anywhere; keeps its data in a Data folder beside it.
      README.txt                       What to tell the person you give it to.

    The app is the same trimmed Release build install.ps1 installs. Nothing personal is included:
    the build holds no data, and each person's data is created on their own PC the first time Ledger
    opens. The installer is compiled from installer\Setup.cs against the .NET Framework that ships
    with Windows, using the .NET SDK's C# compiler, so no other tools are needed.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = $props.Project.PropertyGroup[0].InformationalVersion
$numeric = $props.Project.PropertyGroup[0].Version
if (-not $version) { throw 'No version in Directory.Build.props.' }

$work = Join-Path $repo 'artifacts\package'
$out = Join-Path $repo "dist\Windows-v$version"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $work, $out | Out-Null

Write-Host "Ledger $version"
. (Join-Path $PSScriptRoot 'publish-common.ps1')
$app = Publish-Ledger $repo   # the same build install.ps1 uses, so installing afterwards is quick
if (Get-ChildItem $app -Filter *.db -Recurse) { throw 'A database file ended up in the build; refusing to package it.' }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$payload = Join-Path $work 'payload.zip'
Write-Host 'Compressing...'
[IO.Compression.ZipFile]::CreateFromDirectory($app, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

# ---- Compile Setup and its uninstaller --------------------------------------------------------
$sdk = (dotnet --list-sdks | Select-Object -Last 1) -replace '^(\S+) \[(.+)\]$', '$2\$1'
$csc = Join-Path $sdk 'Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.IO.Compression', 'System.IO.Compression.FileSystem' |
    ForEach-Object { "/r:$(Join-Path $fx "$_.dll")" }
$versionFile = Join-Path $work 'Version.cs'
@"
[assembly: System.Reflection.AssemblyVersion("$numeric.0")]
[assembly: System.Reflection.AssemblyFileVersion("$numeric.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
"@ | Set-Content $versionFile -Encoding utf8
$common = @('/noconfig', '/nostdlib+', '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/langversion:7.3',
    "/win32icon:$(Join-Path $repo 'src\Ledger.App\Assets\Ledger.ico')",
    "/win32manifest:$(Join-Path $repo 'installer\Setup.manifest')") + $refs
$source = Join-Path $repo 'installer\Setup.cs'

$uninstaller = Join-Path $work 'uninstall.exe'
dotnet $csc @common '/define:UNINSTALLER' "/out:$uninstaller" $source $versionFile
if ($LASTEXITCODE -ne 0) { throw 'Compiling the uninstaller failed.' }

$setup = Join-Path $out "Ledger-Setup-$version.exe"
Write-Host 'Building the installer...'
dotnet $csc @common "/resource:$payload,payload.zip" "/resource:$uninstaller,uninstall.exe" "/out:$setup" $source $versionFile
if ($LASTEXITCODE -ne 0) { throw 'Compiling the installer failed.' }

# ---- Portable zip -----------------------------------------------------------------------------
$portable = Join-Path $work 'portable\Ledger'
New-Item -ItemType Directory -Force $portable | Out-Null
Copy-Item "$app\*" $portable -Recurse
@'
This file makes Ledger portable: its data is kept in the Data folder next to Ledger.exe instead of
in your Windows user folder. Delete this file to use the normal location instead.
'@ | Set-Content (Join-Path $portable 'Ledger.portable') -Encoding utf8
$zip = Join-Path $out "Ledger-$version-portable.zip"
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $portable), $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

# ---- README for whoever receives it -----------------------------------------------------------
@"
Ledger $version for Windows
===========================

Ledger is a personal finance app. It keeps track of your accounts, spending, bills, budget and
savings goals, and everything stays on your PC: there's no account to create, no cloud, and nothing
is ever sent anywhere.

Needs: Windows 10 (version 2004 or later) or Windows 11, 64-bit. Nothing else to install.


Installing
----------
Run Ledger-Setup-$version.exe and choose Install. It installs just for you, with no administrator
rights, and adds Ledger to the desktop and the Start menu.

Windows may say "Windows protected your PC", because this installer isn't signed by a publisher
Microsoft knows. Choose "More info", then "Run anyway".

When Ledger opens, it asks you to create your first account, then offers a two-minute tour. Help
(the ? in the sidebar, or F1 on any page) explains everything else.

Updating: run a newer Setup the same way. Your data is kept.
Removing: Windows Settings > Apps > Installed apps > Ledger > Uninstall. Your data is kept unless
you delete it yourself (it's in %LOCALAPPDATA%\Ledger).


Portable version (no installing)
--------------------------------
Unzip Ledger-$version-portable.zip anywhere (a USB stick works) and run Ledger\Ledger.exe. This
copy keeps its data in Ledger\Data beside it, so take the whole folder with you.


Your data
---------
Ledger's data exists only on your PC. Every so often, open Settings > Data > Back up now... and save
the backup to a USB drive or a cloud folder. If your PC fails, that file is how you get it all back.

Paycheck estimates include Pennsylvania state and local taxes. If you live elsewhere, turn off
"I live and work in Pennsylvania" in Settings > Paycheck taxes.
"@ | Set-Content (Join-Path $out 'README.txt') -Encoding utf8

Write-Host ''
Get-ChildItem $out | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16)
    '{0,-34} {1,8:N1} MB   SHA256 {2}...' -f $_.Name, ($_.Length / 1MB), $hash
}
Write-Host "`nDone: $out"
