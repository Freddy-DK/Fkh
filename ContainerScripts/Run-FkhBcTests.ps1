[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RequestBase64
)

$ErrorActionPreference = 'Stop'

function Get-BoundedFileContent {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [long] $MaxBytes
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ''
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $truncated = $stream.Length -gt $MaxBytes
        if ($truncated) {
            $stream.Seek(-$MaxBytes, [IO.SeekOrigin]::End) | Out-Null
        }

        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            $content = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }

        if ($truncated) {
            return "[truncated]`r`n$content"
        }
        return $content
    } finally {
        $stream.Dispose()
    }
}

$maxJUnitBytes = 10MB
$maxStdoutBytes = [long]([Math]::Ceiling($maxJUnitBytes / 3.0) * 4) + 256KB
$maxStderrBytes = 32KB

try {
    $requestJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64))
    $request = $requestJson | ConvertFrom-Json
} catch {
    throw 'The test request is invalid.'
}

$timeoutMinutes = [int]$request.TimeoutMinutes
if ($timeoutMinutes -lt 1 -or $timeoutMinutes -gt 120) {
    throw 'The test timeout must be between 1 and 120 minutes.'
}

$operationId = [Guid]::NewGuid().ToString('N')
$basePath = "C:\run\my\fkh-runtests-$operationId"
$resultPath = "$basePath.xml"
$stdoutPath = "$basePath.stdout"
$stderrPath = "$basePath.stderr"
$workerPath = 'C:\run\my\Invoke-FkhBcTests.ps1'
$process = $null

try {
    if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
        throw "The FKH test worker is missing at '$workerPath'."
    }

    $process = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $workerPath, '-RequestBase64', $RequestBase64, '-ResultPath', $resultPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru `
        -WindowStyle Hidden

    if (-not $process.WaitForExit($timeoutMinutes * 60 * 1000)) {
        try {
            $process.Kill($true)
        } catch {
            $process.Kill()
        }
        throw "Business Central test execution timed out after $timeoutMinutes minute(s)."
    }
    $process.WaitForExit()

    $stdout = Get-BoundedFileContent -Path $stdoutPath -MaxBytes $maxStdoutBytes
    $stderr = Get-BoundedFileContent -Path $stderrPath -MaxBytes $maxStderrBytes
    if ($process.ExitCode -ne 0) {
        $message = if ([string]::IsNullOrWhiteSpace($stderr)) { 'The test worker failed without diagnostics.' } else { $stderr.Trim() }
        throw $message
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        throw $stderr.Trim()
    }

    Write-Output $stdout.TrimEnd()
} finally {
    if ($null -ne $process) {
        $process.Dispose()
    }
    Remove-Item -LiteralPath $resultPath, $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
}