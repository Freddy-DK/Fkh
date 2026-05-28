$ErrorActionPreference = "Stop"
Install-Module -Name BcContainerHelper -Force
Import-Module BcContainerHelper -disableNameChecking
$artifactUrl = $env:ARTIFACT_URL
$baseImage = Get-BestGenericImageName
$labels = Get-BcContainerImageLabels $baseImage
$osversion = $labels.osversion
Write-Host "Downloading artifacts for: $artifactUrl"
Write-Host "Using base image: $baseImage"
$appUri = [Uri]::new($artifactUrl)
$imageTag = "$($appUri.AbsolutePath.Replace('/','-').TrimStart('-'))".ToLowerInvariant()
$artifactPath = Download-Artifacts -artifactUrl $artifactUrl -includePlatform
$manifest = Get-Content -Path (Join-Path $artifactPath[0] "manifest.json") | ConvertFrom-Json
$bakFile = Join-Path $artifactPath[0] $manifest.database
if (!(Test-Path $bakFile)) {
    throw "Unable to locate database backup in artifacts"
}
# Save variables for subsequent steps
"IMAGE_TAG=$imageTag" | Out-File -FilePath $env:GITHUB_ENV -Append
"IMAGE_NAME=my:$imageTag" | Out-File -FilePath $env:GITHUB_ENV -Append
"OS_VERSION=$osversion" | Out-File -FilePath $env:GITHUB_ENV -Append
"BAK_FILE=$bakFile" | Out-File -FilePath $env:GITHUB_ENV -Append
"ARTIFACT_URL=$artifactUrl" | Out-File -FilePath $env:GITHUB_ENV -Append
"BASE_IMAGE=$baseImage" | Out-File -FilePath $env:GITHUB_ENV -Append
