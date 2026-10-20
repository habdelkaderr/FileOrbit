<#
.SYNOPSIS
  Builds a self-contained, ReadyToRun release of FileOrbit into artifacts\publish.
.DESCRIPTION
  Self-contained so end users need no .NET runtime. ReadyToRun shortens start-up, which matters
  because every Explorer context-menu click starts FileOrbit.exe briefly to forward the file.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\publish')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
$project = Join-Path $root 'src\FileOrbit.App\FileOrbit.App.csproj'
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

foreach ($file in 'README.md', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $root $file) $Output -Force
}

$exe = Join-Path $Output 'FileOrbit.exe'
if (-not (Test-Path $exe)) { throw 'FileOrbit.exe missing from publish output' }
if (-not (Test-Path (Join-Path $Output 'FileOrbit.Engines.Ghostscript.dll'))) { Write-Warning 'Ghostscript engine plugin missing from publish output' }
$size = (Get-ChildItem $Output -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published {0} ({1:N0} MB) to {2}" -f (Get-Item $exe).VersionInfo.ProductVersion, $size, (Resolve-Path $Output))
