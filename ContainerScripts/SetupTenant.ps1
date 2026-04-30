function Get-PlainText() {
    [CmdletBinding()]
    Param(
        [parameter(ValueFromPipeline, Mandatory = $true)]
        [System.Security.SecureString] $SecureString
    )
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::FreeBSTR($bstr)
    }
}

$tenantId = 'default'
$tenantDatabaseName = "$($ENV:DatabaseName)-$tenantId"

$CustomConfigFile =  Join-Path $ServiceTierFolder "CustomSettings.config"
$CustomConfig = [xml](Get-Content $CustomConfigFile)
$tenantEnvironmentType = $customConfig.SelectSingleNode("//appSettings/add[@key='TenantEnvironmentType']")
$DatabaseServerInstance = "$DatabaseServer"
if ("$DatabaseInstance" -ne "") {
    $DatabaseServerInstance += "\$DatabaseInstance"
}
$Params = @{ "Force"=$true; "AllowAppDatabaseWrite" = $false; "OverwriteTenantIdInDatabase" = $true }
if ($tenantEnvironmentType -ne $null) {
    $Params += @{"EnvironmentType" = $tenantEnvironmentType.value }
}

Write-Host "Checking for existing tenant '$tenantId' on '$ServerInstance'..."
$existingTenant = Get-NAVTenant -ServerInstance $ServerInstance -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $tenantId }
if ($existingTenant) {
    Write-Host "Tenant '$tenantId' already exists on '$ServerInstance', skipping mount and sync."
    while ($existingTenant.State -ne "Operational") {
        Write-Host "Tenant '$tenantId' is in state '$($existingTenant.State)'."
        Start-Sleep -seconds 5
        $existingTenant = Get-NAVTenant -ServerInstance $ServerInstance -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $tenantId }
    }
}
else {
    Write-Host "Mounting tenant '$tenantId' with database '$($DatabaseServerInstance):$tenantDatabaseName' on '$ServerInstance'"
    Mount-NavTenant -ServerInstance $ServerInstance -Tenant $tenantId -DatabaseName $tenantDatabaseName -DatabaseCredentials $databaseCredentials @Params -WarningAction SilentlyContinue -Verbose

    Write-Host "Sync'ing Tenant"    
    Sync-NAVTenant  -ServerInstance $ServerInstance `
                    -Tenant $tenantId `
                    -Force
}

