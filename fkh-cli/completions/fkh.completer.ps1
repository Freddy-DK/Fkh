# PowerShell tab-completion for the fkh CLI
#
# Usage: . fkh.completer.ps1              (dot-source to enable)
# Or add to your $PROFILE to enable permanently.
#
# The cache (~/.fkh/completions.json) is generated automatically on first load
# and refreshed in the background when older than 4 hours.

$script:_fkhCompletionData = $null
$script:_fkhCachePath = [System.IO.Path]::Combine(
    [System.Environment]::GetFolderPath('UserProfile'), '.fkh', 'completions.json')

function Get-FkhCompletionData {
    if ($script:_fkhCompletionData) {
        return $script:_fkhCompletionData
    }
    if ([System.IO.File]::Exists($script:_fkhCachePath)) {
        try {
            $script:_fkhCompletionData = Get-Content $script:_fkhCachePath -Raw | ConvertFrom-Json
        }
        catch { }
    }
    # Refresh in background if cache is missing or older than 4 hours
    $stale = -not [System.IO.File]::Exists($script:_fkhCachePath) -or
             ([datetime]::UtcNow - [System.IO.File]::GetLastWriteTimeUtc($script:_fkhCachePath)).TotalHours -gt 4
    if ($stale) {
        Start-Job -ScriptBlock { & fkh --completions >$null 2>&1 } | Out-Null
    }
    return $script:_fkhCompletionData
}

Register-ArgumentCompleter -CommandName fkh -Native -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)

    $tokens = $commandAst.ToString().Substring(0, $cursorPosition) -split '\s+'
    # $tokens[0] = "fkh", $tokens[1] = command (if present), rest = args

    $commands = Get-FkhCompletionData
    if (-not $commands) { return }

    # Global options that apply to any command
    $globalOptions = @(
        @{ Name = '--backendUrl';  Desc = 'Override the backend URL' }
        @{ Name = '--ghUser';      Desc = 'GitHub user for gh auth' }
        @{ Name = '--nowait';      Desc = "Don't wait for completion" }
        @{ Name = '--asJson';      Desc = 'Output result as JSON' }
        @{ Name = '--output';      Desc = 'Save binary output to file' }
        @{ Name = '--help';        Desc = 'Show help' }
    )

    if ($tokens.Count -le 2) {
        # Completing the command name (position 1)
        $filter = if ($tokens.Count -eq 2) { $tokens[1] } else { '' }
        $commands | Where-Object { $_.name -like "$filter*" } | ForEach-Object {
            [System.Management.Automation.CompletionResult]::new(
                $_.name, $_.name, 'ParameterValue', $_.description)
        }
        return
    }

    # We have a command — complete its parameters
    $cmdName = $tokens[1].ToLower()
    $cmd = $commands | Where-Object { $_.name -eq $cmdName }
    if (-not $cmd) { return }

    # Collect already-used parameter names
    $usedParams = @()
    for ($i = 2; $i -lt $tokens.Count; $i++) {
        if ($tokens[$i] -match '^--(.+)$') {
            $usedParams += $Matches[1].ToLower()
        }
    }

    # Determine if we're completing a --param name or a value
    $currentToken = $tokens[-1]

    if ($currentToken -like '--*') {
        # Completing a parameter name
        $prefix = $currentToken.Substring(2)
        $allParams = @()

        # Command-specific params
        foreach ($p in $cmd.parameters) {
            if ($usedParams -notcontains $p.name.ToLower()) {
                $reqText = if ($p.required) { 'required' } else { 'optional' }
                $allParams += @{ Name = "--$($p.name)"; Desc = "[$reqText] $($p.description)" }
            }
        }

        # Global options
        foreach ($g in $globalOptions) {
            $gName = $g.Name.Substring(2)
            if ($usedParams -notcontains $gName.ToLower()) {
                $allParams += $g
            }
        }

        $allParams | Where-Object { $_.Name.Substring(2) -like "$prefix*" } | ForEach-Object {
            [System.Management.Automation.CompletionResult]::new(
                $_.Name, $_.Name, 'ParameterName', $_.Desc)
        }
    }
}

# Load cache eagerly on dot-source so first tab is instant
# If no cache exists yet, generate it synchronously (one-time cost)
if (-not [System.IO.File]::Exists($script:_fkhCachePath)) {
    try { & fkh --completions >$null 2>&1 } catch { }
}
$null = Get-FkhCompletionData
