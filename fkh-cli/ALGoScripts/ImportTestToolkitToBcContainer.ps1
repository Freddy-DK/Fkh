Param(
    [Hashtable]$parameters
)

# BcContainerHelper override

Write-Host "ImportTestToolkitToBcContainer Parameters:"
$parameters | ConvertTo-Json | Out-Host

if ($doNotPublishApps) {
    return
}

$parts = "$ENV:GITHUB_REPOSITORY". Split('/')
$projectHash = (Get-FileHash -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes("$($ENV:_project)-$($ENV:_buildMode)-$($ENV:GITHUB_RUN_ID)"))) -Algorithm SHA256).Hash.Substring(0, 16)
$containerName = "$($parts[0])-$($parts[1])-$projectHash".ToLower() -replace "[^a-z0-9\-]"

Write-Host "Waiting for container $containerName to be ready"
fkh WaitForContainer --name $containerName --useOIDC
Write-Host "Container $containerName is ready. Importing test toolkit."

$scriptParams = @()
if ($parameters.includeTestFrameworkOnly)  { $scriptParams += "--includeTestFrameworkOnly"  }
if ($parameters.includeTestLibrariesOnly)  { $scriptParams += "--includeTestLibrariesOnly"  }
if ($parameters.includeTestRunnerOnly)     { $scriptParams += "--includeTestRunnerOnly"     }
if ($parameters.includePerformanceToolkit) { $scriptParams += "--includePerformanceToolkit" }

Write-Host "ImportTestToolkit $($scriptParams -join ' ') to $containerName"
fkh importTestToolkit --name $containerName --useOIDC @scriptParams
Write-Host "Finished importing test toolkit to $containerName"
