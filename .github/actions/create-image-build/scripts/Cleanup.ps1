function Remove-DockerImageIfExists {
    param([string]$Image)

    Write-Host "----------------------------------------"
    Write-Host "Remove-DockerImageIfExists called"
    Write-Host "Image argument: '$Image'"

    if ([string]::IsNullOrWhiteSpace($Image)) {
        Write-Host "SKIP: image argument is empty"
        $global:LASTEXITCODE = 0
        return
    }

    Write-Host "Inspecting image: $Image"
    docker image inspect "$Image" *> $null
    $inspectExitCode = $LASTEXITCODE
    Write-Host "docker image inspect exit code: $inspectExitCode"

    if ($inspectExitCode -eq 0) {
        Write-Host "FOUND: $Image"
        Write-Host "Removing image: $Image"

        docker image rm -f "$Image"
        $rmiExitCode = $LASTEXITCODE

        Write-Host "docker rmi exit code: $rmiExitCode"

        if ($rmiExitCode -ne 0) {
            Write-Host "WARNING: docker rmi failed for image: $Image"
        }
    }
    else {
        Write-Host "NOT FOUND: $Image"
    }

    $global:LASTEXITCODE = 0
}

# Remove the temp container (if multitenant backup step ran)
Remove-BcContainer -containerName $env:BC_CONTAINER_NAME -ErrorAction SilentlyContinue

# Remove the temp build image (my:tag)
Remove-DockerImageIfExists $env:IMAGE_NAME

# Remove the ACR-tagged images (already pushed, no need to keep locally)
if ($env:IMAGE_TAG -and $env:ACR_LOGIN_SERVER) {
  Remove-DockerImageIfExists "$env:ACR_LOGIN_SERVER/businesscentral:$env:IMAGE_TAG"
  Remove-DockerImageIfExists "$env:ACR_LOGIN_SERVER/businesscentral-$($env:OS_VERSION):$env:IMAGE_TAG"
}

# Remove this build's artifact cache (specific version only)
if ($env:ARTIFACT_URL) {
  $appUri = [Uri]::new($env:ARTIFACT_URL)
  # Path structure: C:\bcartifacts.cache\<type>\<version>  e.g. sandbox\28.0.46665.49991
  $segments = $appUri.AbsolutePath.TrimStart('/') -split '/'
  # segments[0] = type (sandbox), segments[1] = version, segments[2] = country
  if ($segments.Count -ge 2) {
    $cacheFolder = Join-Path "C:\bcartifacts.cache" $segments[0] $segments[1]
    if (Test-Path $cacheFolder) {
      Remove-Item -Path $cacheFolder -Recurse -Force -ErrorAction SilentlyContinue
      Write-Host "Cleaned artifact cache: $cacheFolder"
    }
  }
}
