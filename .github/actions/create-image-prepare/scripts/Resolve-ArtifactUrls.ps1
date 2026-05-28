Install-Module -Name BcContainerHelper -Force -ErrorAction SilentlyContinue
Import-Module BcContainerHelper -DisableNameChecking
$urls = $env:ARTIFACT_URLS -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
if ($urls.Count -eq 0) { throw "No valid artifact URLs provided" }
$resolved = @()
foreach ($url in $urls) {
  if ($url -like 'https://*') {
    $resolved += $url
  } else {
    # Resolve shorthand (e.g. "///us/latest") using the same logic as the backend
    $segments = ("$url/////") -split '/'
    $storageAccount = if ($segments[0]) { $segments[0] } else { '' }
    $type            = if ($segments[1]) { $segments[1] } else { 'Sandbox' }
    $version         = $segments[2]
    $country         = if ($segments[3]) { $segments[3] } else { 'us' }
    $select          = if ($segments[4]) { $segments[4] } else { 'Latest' }
    $params = @{ type = $type; country = $country; select = $select }
    if ($version) { $params.version = $version }
    if ($storageAccount) { $params.storageAccount = $storageAccount }
    $artifactUrl = Get-BCArtifactUrl @params
    if (-not $artifactUrl) { throw "Could not resolve artifact shorthand: $url" }
    Write-Host "Resolved '$url' -> '$artifactUrl'"
    $resolved += $artifactUrl
  }
}
$resolvedStr = $resolved -join ','
"resolved=$resolvedStr" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
