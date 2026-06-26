Param(
    [Hashtable]$parameters
)

# PublishBcContainerApp override

Write-Host "PublishBcContainerApp Parameters:"
$parameters | ConvertTo-Json | Out-Host

if ($doNotPublishApps) {
    return
}

$parts = "$ENV:GITHUB_REPOSITORY". Split('/')
$projectHash = (Get-FileHash -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes("$($ENV:_project)-$($ENV:_buildMode)-$($ENV:GITHUB_RUN_ID)"))) -Algorithm SHA256).Hash.Substring(0, 16)
$containerName = "$($parts[0])-$($parts[1])-$projectHash".ToLower() -replace "[^a-z0-9\-]"

Write-Host "Sort apps in dependency order"
$result = & (Join-Path $PSScriptRoot 'sortapps.ps1') -appFiles $parameters.appFile

Write-Host "Waiting for container $containerName to be ready"
fkh WaitForContainer --name $containerName --useOIDC
Write-Host "Container $containerName is ready. Publishing apps."
foreach($app in $result.SortedApps) {
    Write-Host "Publishing $app to $containerName"
    fkh PublishApp --name $containerName --appFile $app --syncMode ForceSync --sync --install --useOIDC
}
Write-Host "Finished publishing apps to $containerName"
