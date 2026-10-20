<#
.SYNOPSIS
  Runs the test suite (except Microsoft Office tests) in CI and release builds.
.DESCRIPTION
  Each test group runs in its own process. When the WPF UI tests and the Windows media/PDF/OCR engine
  tests share one process, Windows can fault in ntdll (0xC000000D) while tearing that process down,
  after every test has passed; isolating the groups avoids that and attributes any crash to its group.
  On GitHub Actions, results and failures are also written as annotations on the run summary.
#>
param([string]$Configuration = 'Release')
$onGitHub = $env:GITHUB_ACTIONS -eq 'true'
$failed = @()
foreach ($group in 'FileOrbit.Tests.Unit', 'FileOrbit.Tests.Integration', 'FileOrbit.Tests.Visual') {
    $output = dotnet test --no-build -c $Configuration -- --filter-not-trait 'Category=Office' --filter-namespace $group 2>&1
    $code = $LASTEXITCODE
    $output | Write-Host
    $lines = @($output | ForEach-Object { "$_" -replace "`e\[[0-9;]*m", '' })
    $summary = ($lines | Where-Object { $_ -match '^\s*(total|succeeded|failed|skipped):' } | ForEach-Object { $_.Trim() }) -join ', '
    if ($code -eq 0) {
        if ($onGitHub) { Write-Host "::notice::[$group] passed: $summary" }
        continue
    }

    $failed += $group
    if ($onGitHub) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*failed ') { Write-Host "::error::[$group] $($lines[$i].Trim()) -- $(($lines[($i + 1)..($i + 3)] -join ' ').Trim())" }
        }

        Write-Host "::error::[$group] exit code $code. $summary"
    }
}

if ($failed.Count -gt 0) {
    Write-Host "Failed groups: $($failed -join ', ')"
    exit 1
}
