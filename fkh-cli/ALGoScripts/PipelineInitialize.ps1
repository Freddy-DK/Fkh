if ($doNotPublishApps) {
    Write-Host "Not publishing apps, skipping pipeline initialization"
    return
}

# BcContainerHelper override

$parts = "$ENV:GITHUB_REPOSITORY". Split('/')
$projectHash = (Get-FileHash -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes("$($ENV:_project)-$($ENV:_buildMode)-$($ENV:GITHUB_RUN_ID)"))) -Algorithm SHA256).Hash.Substring(0, 16)
$containerName = "$($parts[0])-$($parts[1])-$projectHash".ToLower() -replace "[^a-z0-9\-]"

$containersJson = fkh listcontainers --all --asjson --useOIDC
Write-Host $containersJson
$containers = $containersJson | convertfrom-json
$container = $containers.containers | Where-Object { $_.appLabel -eq $containerName }
if (-not $container) {
    $adminUsername = "admin"
    $adminPassword = GetRandomPassword
    $settings = $ENV:Settings | ConvertFrom-Json
    $secrets = $ENV:Secrets | ConvertFrom-Json
    $params = @{
        name          = $containerName
        artifactUrl   = $artifact
        adminUsername = $adminUsername
        adminPassword = $adminPassword
        autostop      = '6h'
        repo          = $ENV:GITHUB_REPOSITORY
        project       = $ENV:_project
    }
    if ($secrets.PSObject.Properties.Name -eq "licenseFileUrl") {
        Write-Host "LicenseFileUrl secret found"
        $licenseFileUrl = $secrets.licenseFileUrl
        if ($licenseFileUrl -notlike 'https://*') {
            Write-Host "Secrets are base64 encoded"
            $licenseFileUrl = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($licenseFileUrl))
        }
        $params += @{
            licenseFileUrl = $licenseFileUrl
        }
    }
    if ($settings.PSObject.Properties.Name -eq "fkh") {
        $fkhSettings = $settings."fkh"
        foreach ($prop in 'useDatabase', 'cpu', 'memory') {
            $createContainerProp = "CreateContainer.$prop"
            if ($fkhSettings.PSObject.Properties.Name -eq $createContainerProp) {
                $params[$prop] = $fkhSettings."$createContainerProp"
            }
        }
    }
    $fkhArgs = @("createcontainer", "--useOIDC", "--asJson")
    foreach ($key in $params.Keys) {
        if ($params[$key]) {
            $fkhArgs += "--$key"
            $fkhArgs += $params[$key]
        }
    }

    Write-Host "Creating container $containerName $($fkhArgs -join ' ')"
    $result = fkh @fkhArgs
    Write-Host "CreateContainer Result: $result"
    $container = $result | ConvertFrom-Json
}
$bcAuthContext = @{
    "username" = $adminUsername
    "password" = ConvertTo-SecureString -String $adminPassword -AsPlainText -Force
}
Set-Variable -Name 'bcAuthContext' -value $bcAuthContext -scope 1
Set-Variable -Name 'environment' -value $container.webClient -scope 1
