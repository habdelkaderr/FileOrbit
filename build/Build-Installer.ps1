<#
.SYNOPSIS
  Publishes FileOrbit and builds the per-user MSI installer into artifacts\.
.DESCRIPTION
  Uses the WiX Toolset MSBuild SDK (restored from NuGet), so no separate tool installation is needed.
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'artifacts\publish'
$artifacts = Join-Path $root 'artifacts'

& (Join-Path $PSScriptRoot 'Publish.ps1') -Configuration $Configuration -Output $publish

$wixproj = Join-Path $root 'installer\FileOrbit.Installer.wixproj'
dotnet build $wixproj -c $Configuration "-p:PublishDir=$publish\" -o $artifacts
if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($LASTEXITCODE)" }
Get-ChildItem $artifacts -Filter *.msi | ForEach-Object { Write-Host ("Installer: {0} ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB)) }
