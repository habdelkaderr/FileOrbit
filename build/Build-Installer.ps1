<#
.SYNOPSIS
  Builds the FileOrbit installer (artifacts\FileOrbit-<version>-x64.msi) from the portable build.
.DESCRIPTION
  The MSI packages the same single-file FileOrbit.exe as the portable zip, so both downloads run the
  identical program. Uses the WiX Toolset MSBuild SDK (restored from NuGet); nothing else to install.
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$artifacts = Join-Path $root 'artifacts'
$portable = Join-Path $artifacts "FileOrbit-$version-win-x64"

& (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -Configuration $Configuration

$msi = Join-Path $artifacts "FileOrbit-$version-x64.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }
$wixproj = Join-Path $root 'installer\FileOrbit.Installer.wixproj'
dotnet build $wixproj -c $Configuration "-p:PublishDir=$portable\" -o $artifacts
if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($LASTEXITCODE)" }

$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$msi.sha256" -Encoding ascii -Value "$hash  $(Split-Path $msi -Leaf)"
Write-Host ("Installer: {0} ({1:N0} MB)`nSHA-256: {2}" -f $msi, ((Get-Item $msi).Length / 1MB), $hash)
