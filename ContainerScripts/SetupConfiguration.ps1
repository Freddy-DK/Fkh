. c:\run\SetupConfiguration.ps1

if ($auth -eq "AccessControlService") {
    $CustomConfigFile =  Join-Path $ServiceTierFolder "CustomSettings.config"
    $CustomConfig = [xml](Get-Content $CustomConfigFile)
    if ($null -ne $customConfig.SelectSingleNode("//appSettings/add[@key='ValidAudiences']")) {
        $validAudiences = "$($env:aadAppId);$($env:appIdUri);https://api.businesscentral.dynamics.com;$validAudiences"
        Write-Host "Setting ValidAudiences to $validAudiences in $CustomConfigFile"
        $CustomConfig.SelectSingleNode("//appSettings/add[@key='ValidAudiences']").value = $validAudiences
        $CustomConfig.Save($CustomConfigFile)
    }
}
