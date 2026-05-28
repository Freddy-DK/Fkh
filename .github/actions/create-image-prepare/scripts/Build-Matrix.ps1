$urls = $env:ARTIFACT_URLS -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
if ($urls.Count -eq 0) { throw "No valid artifact URLs provided" }
$matrix = @{ artifactUrl = @($urls) } | ConvertTo-Json -Compress
"matrix=$matrix" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
