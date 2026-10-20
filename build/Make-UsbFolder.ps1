<#
.SYNOPSIS
  Creates artifacts\FileOrbit-USB: a folder to copy onto a USB drive and use on other PCs.
.DESCRIPTION
  Contains the portable FileOrbit.exe (runs straight from the drive and offers to install itself on
  the PC), the MSI installer, and plain-language instructions. Run Build-Installer.ps1 first.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$artifacts = Join-Path $root 'artifacts'
$portable = Join-Path $artifacts "FileOrbit-$version-win-x64"
$msi = Join-Path $artifacts "FileOrbit-$version-x64.msi"
$usb = Join-Path $artifacts 'FileOrbit-USB'

foreach ($required in $portable, $msi) {
    if (-not (Test-Path $required)) { throw "Missing $required. Run build\Build-Installer.ps1 first." }
}

if (Test-Path $usb) { Remove-Item $usb -Recurse -Force }
New-Item -ItemType Directory $usb | Out-Null
Get-ChildItem $portable | Where-Object Name -ne 'START HERE.txt' | Copy-Item -Destination $usb
Copy-Item $msi (Join-Path $usb "FileOrbit Setup $version.msi")

Set-Content -Path (Join-Path $usb 'START HERE.txt') -Encoding utf8 -Value @"
FileOrbit $version - USB copy
=============================

Works on 64-bit Windows 10 (version 2004 or later) and Windows 11. No internet connection needed.

OPTION 1 - Install on this PC (recommended)
  Double-click "FileOrbit Setup $version.msi". It installs for the current user only
  (no administrator password), adds FileOrbit to the Start menu and starts it.
  Uninstall later from Settings > Apps > Installed apps > FileOrbit.

OPTION 2 - Run straight from the USB drive
  Double-click FileOrbit.exe. FileOrbit offers to copy itself onto the PC:
    - Yes: it installs to your user folder, so it keeps working after the drive is removed.
    - No:  it runs from the drive for this session only (the right-click menu entry is not added,
           so nothing breaks when you unplug the drive).

USING FILEORBIT
  - Select a file in File Explorer and press Ctrl+Alt+Space, or
  - right-click a file > Show more options > Convert with FileOrbit, or
  - drag a file onto the FileOrbit window.
  FileOrbit keeps running in the notification area (next to the clock) so it can respond instantly.

IF WINDOWS BLOCKS IT
  FileOrbit is not code-signed yet. If Smart App Control is turned on, Windows may block it entirely;
  otherwise you may see "Windows protected your PC" - choose More info > Run anyway.

Your files never leave the PC: FileOrbit converts everything locally and has no network features.
"@

$size = (Get-ChildItem $usb | Measure-Object Length -Sum).Sum / 1MB
Get-ChildItem $usb | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host ("USB folder: {0} ({1:N0} MB). Copy the whole folder to the USB drive." -f $usb, $size)
