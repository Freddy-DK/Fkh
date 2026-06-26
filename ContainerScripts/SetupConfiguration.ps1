. c:\run\SetupConfiguration.ps1

$CustomConfigFile = Join-Path $ServiceTierFolder "CustomSettings.config"
$CustomConfig = [xml](Get-Content $CustomConfigFile)
$changed = $false

if ($auth -eq "AccessControlService") {
    if ($null -ne $CustomConfig.SelectSingleNode("//appSettings/add[@key='ValidAudiences']")) {
        $validAudiences = "$($env:aadAppId);$($env:appIdUri);https://api.businesscentral.dynamics.com;$validAudiences"
        Write-Host "Setting ValidAudiences to $validAudiences in $CustomConfigFile"
        $CustomConfig.SelectSingleNode("//appSettings/add[@key='ValidAudiences']").value = $validAudiences
        $changed = $true
    }
}

foreach ($key in @('EnableDebugging', 'DebuggingAllowed')) {
    $node = $CustomConfig.SelectSingleNode("//appSettings/add[@key='$key']")
    if ($null -ne $node -and $node.value -ne 'true') {
        Write-Host "Setting $key to true in $CustomConfigFile"
        $node.value = 'true'
        $changed = $true
    }
}

if ($changed) {
    $CustomConfig.Save($CustomConfigFile)
}
