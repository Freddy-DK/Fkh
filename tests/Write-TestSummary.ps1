<#
.SYNOPSIS
  Appends a Markdown summary of TRX test results to the GitHub Actions step summary.
#>
param(
    [string]$Title = 'Test results',
    [string]$ResultsDir = 'TestResults'
)

$files = @(Get-ChildItem -Path $ResultsDir -Filter '*.trx' -ErrorAction SilentlyContinue | Sort-Object Name)

$lines = @("## $Title", '')

if ($files.Count -eq 0) {
    $lines += '_No test result files were produced._'
}
else {
    $lines += '| Suite | Total | Passed | Failed | Skipped | Outcome |'
    $lines += '|---|---:|---:|---:|---:|---|'
    foreach ($file in $files) {
        try {
            [xml]$doc = Get-Content -Raw -Path $file.FullName
            $counters = $doc.TestRun.ResultSummary.Counters
            $total = [int]$counters.total
            $executed = [int]$counters.executed
            $passed = [int]$counters.passed
            $failed = [int]$counters.failed
            $skipped = [Math]::Max(0, $total - $executed)
            $outcome = if ($failed -gt 0) { 'failed' } else { 'passed' }
            $lines += "| $($file.BaseName) | $total | $passed | $failed | $skipped | $outcome |"
        }
        catch {
            $lines += "| $($file.BaseName) | ? | ? | ? | ? | parse error |"
        }
    }
}

$content = ($lines -join "`n")

if ([string]::IsNullOrEmpty($env:GITHUB_STEP_SUMMARY)) {
    Write-Host $content
}
else {
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $content
}
