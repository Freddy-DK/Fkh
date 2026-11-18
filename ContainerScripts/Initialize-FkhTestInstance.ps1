[CmdletBinding()]
param(
    [string] $Tenant = 'default',

    [switch] $Remove
)

# AAD service tiers refuse app-only tokens in UI sessions, so tests use a localhost-only NavUserPassword tier on the same database.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 6) {
    # Started from pwsh, PSModulePath includes PowerShell 7 module folders that break Windows PowerShell (e.g. Get-Acl).
    $env:PSModulePath = (($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -notmatch '\\PowerShell\\' }) -join ';'
}
if (-not (Get-Command Get-NAVServerInstance -ErrorAction SilentlyContinue)) {
    . 'C:\run\prompt.ps1' -silent
}
if (-not $ServerInstance) {
    $ServerInstance = 'BC'
}
$instance = 'FkhTest'
$webPort = 8181
$ports = [ordered]@{
    ManagementServicesPort       = 7145
    ClientServicesPort           = 7146
    SOAPServicesPort             = 7147
    ODataServicesPort            = 7148
    DeveloperServicesPort        = 7149
    SnapshotDebuggerServicesPort = 7183
    ManagementApiServicesPort    = 7186
}
$settings = [ordered]@{
    ServerInstance               = $instance
    ClientServicesCredentialType = 'NavUserPassword'
    PublicWebBaseUrl             = "http://localhost:$webPort/"
    EnableTaskScheduler          = 'false'
    SOAPServicesEnabled          = 'false'
    ODataServicesEnabled         = 'false'
    DeveloperServicesEnabled     = 'false'
    SnapshotDebuggerEnabled      = 'false'
}

$serviceFolder = (Get-Item 'C:\Program Files\Microsoft Dynamics NAV\*\Service' | Select-Object -First 1).FullName
$webPublishFolder = (Get-Item 'C:\Program Files\Microsoft Dynamics NAV\*\Web Client\WebPublish' | Select-Object -First 1).FullName
$mutex = New-Object Threading.Mutex($false, 'Global\FkhTestInstance')
[void]$mutex.WaitOne()
try {
    if ($Remove) {
        if (Get-NAVWebServerInstance -WebServerInstance $instance -ErrorAction SilentlyContinue) {
            Write-Host "Removing the '$instance' test web client."
            $appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
            $appPool = & $appcmd list app "/site.name:$instance" /text:apppool.name | Select-Object -First 1
            & $appcmd stop site "/site.name:$instance" | Out-Null
            if ($appPool) {
                & $appcmd stop apppool "/apppool.name:$appPool" | Out-Null
            }
            Remove-NAVWebServerInstance -WebServerInstance $instance | Out-Null
        }
        if (Get-NAVServerInstance -ServerInstance $instance -ErrorAction SilentlyContinue) {
            Write-Host "Removing the '$instance' test service tier."
            Remove-NAVServerInstance -ServerInstance $instance -Force | Out-Null
        }
        return
    }

    if (-not (Get-NAVServerInstance -ServerInstance $instance -ErrorAction SilentlyContinue)) {
        Write-Host "Creating the '$instance' test service tier."
        [xml]$mainConfig = [IO.File]::ReadAllText((Join-Path $serviceFolder 'CustomSettings.config'))
        $mainSetting = { param($key) $mainConfig.SelectSingleNode("//appSettings/add[@key='$key']").Value }
        $newInstanceParams = @{
            ServerInstance                = $instance
            DatabaseServer                = & $mainSetting 'DatabaseServer'
            DatabaseName                  = & $mainSetting 'DatabaseName'
            ClientServicesCredentialType  = 'NavUserPassword'
            ServicesCertificateThumbprint = & $mainSetting 'ServicesCertificateThumbprint'
            ServiceAccount                = 'NetworkService'
            Force                         = $true
        }
        New-NAVServerInstance @newInstanceParams @ports | Out-Null
        # Run as LocalSystem like the main tier, so it can read the encryption key, the certificate, and bind its ports.
        & sc.exe config "MicrosoftDynamicsNavServer`$$instance" obj= LocalSystem | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not configure the '$instance' test service tier to run as LocalSystem."
        }

        $instanceConfigPath = (Get-Item (Join-Path $serviceFolder "Instances\$instance\CustomSettings.config")).FullName
        foreach ($entry in @($ports.GetEnumerator()) + @($settings.GetEnumerator())) {
            $node = $mainConfig.SelectSingleNode("//appSettings/add[@key='$($entry.Key)']")
            if ($node) {
                $node.Value = [string]$entry.Value
            }
        }
        $mainConfig.Save($instanceConfigPath)

        $keysFolder = (Get-Item 'C:\ProgramData\Microsoft\Microsoft Dynamics NAV\*\Server\Keys' | Select-Object -First 1).FullName
        $mainKey = Join-Path $keysFolder "$ServerInstance.key"
        if (Test-Path -LiteralPath $mainKey) {
            Copy-Item -LiteralPath $mainKey -Destination (Join-Path $keysFolder "$instance.key") -Force
        }
    }

    if ((Get-NAVServerInstance -ServerInstance $instance).State -ne 'Running') {
        Write-Host "Starting the '$instance' test service tier."
        Start-NAVServerInstance -ServerInstance $instance | Out-Null
    }

    if (-not (Get-NAVWebServerInstance -WebServerInstance $instance -ErrorAction SilentlyContinue)) {
        Write-Host "Creating the '$instance' test web client on localhost:$webPort."
        New-NAVWebServerInstance `
            -WebServerInstance $instance `
            -Server 'localhost' `
            -ServerInstance $instance `
            -ClientServicesCredentialType 'NavUserPassword' `
            -ClientServicesPort $ports.ClientServicesPort `
            -ManagementServicesPort $ports.ManagementServicesPort `
            -SiteDeploymentType 'RootSite' `
            -WebSitePort $webPort `
            -PublishFolder $webPublishFolder | Out-Null
        & "$env:windir\System32\inetsrv\appcmd.exe" set site "/site.name:$instance" "/bindings:http/127.0.0.1:$($webPort):" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict the '$instance' test web client to localhost."
        }
    }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}

$deadline = (Get-Date).AddMinutes(5)
while ($true) {
    $tenantState = Get-NAVTenant -ServerInstance $instance -Tenant $Tenant -ErrorAction SilentlyContinue | Select-Object -ExpandProperty State
    if ($tenantState -eq 'Operational') {
        break
    }
    if ((Get-Date) -gt $deadline) {
        throw "Tenant '$Tenant' is not operational on the '$instance' test service tier (state: '$tenantState')."
    }
    Start-Sleep -Seconds 5
}

"http://127.0.0.1:$webPort/cs?tenant=$([Uri]::EscapeDataString($Tenant))"
