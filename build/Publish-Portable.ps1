<#
.SYNOPSIS
  Builds the portable FileOrbit folder and zip: a single self-contained FileOrbit.exe that runs
  without installing anything (no .NET runtime needed).
.OUTPUTS
  artifacts\FileOrbit-<version>-win-x64\       the folder people open and run
  artifacts\FileOrbit-<version>-win-x64.zip    the same folder, zipped for a GitHub release
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$name = "FileOrbit-$version-win-x64"
$output = Join-Path $root "artifacts\$name"
$zip = Join-Path $root "artifacts\$name.zip"

foreach ($path in $output, $zip) { if (Test-Path $path) { Remove-Item $path -Recurse -Force } }

$project = Join-Path $root 'src\FileOrbit.App\FileOrbit.App.csproj'
dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Packaging guard: the engine plugin must come from this same build.
$appVersion = (Get-Item (Join-Path $output 'FileOrbit.exe')).VersionInfo.FileVersion
$plugin = Join-Path $output 'FileOrbit.Engines.Ghostscript.dll'
if (-not (Test-Path $plugin)) { throw 'Engine plugin missing from the portable folder.' }
$pluginVersion = (Get-Item $plugin).VersionInfo.FileVersion
if ($pluginVersion -ne $appVersion) { throw "Engine plugin version $pluginVersion does not match FileOrbit $appVersion (stale build output)." }

Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $output -Force
Set-Content -Path (Join-Path $output 'START HERE.txt') -Encoding utf8 -Value @"
FileOrbit $version - portable

1. Double-click FileOrbit.exe. No installation is needed.
2. FileOrbit stays in the notification area (next to the clock) so it can respond instantly.
3. To convert a file:
   - select it in File Explorer and press Ctrl+Alt+Space, or
   - right-click it > Show more options > Convert with FileOrbit, or
   - drag it onto the FileOrbit window.

If Windows shows "Windows protected your PC", choose More info > Run anyway (the app is not
code-signed yet). To remove FileOrbit: open Settings in FileOrbit and turn off the Explorer menu
entry and start-up option, exit it from the notification-area icon, then delete this folder.
"@

Compress-Archive -Path $output -DestinationPath $zip -CompressionLevel Optimal

# Checksum so downloaders can verify the zip (Get-FileHash <zip> -Algorithm SHA256).
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Encoding ascii -Value "$hash  $name.zip"
Write-Host "SHA-256: $hash"
Get-ChildItem $output | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host ("Portable folder: {0}`nZip: {1} ({2:N0} MB)" -f $output, $zip, ((Get-Item $zip).Length / 1MB))
