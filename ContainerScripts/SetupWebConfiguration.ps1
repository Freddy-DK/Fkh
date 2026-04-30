. c:\run\SetupWebConfiguration.ps1

if ($auth -eq "AccessControlService") {
    $navsettingsFile = Join-Path $wwwRootPath "$webServerInstance\navsettings.json"
    $aadApplicationId = "$($env:aadAppId)"
    $aadAuthorityUri = "https://login.microsoftonline.com/$($env:aadTenant)"
    if ("$env:aad_app_is_multitenant" -eq "Y") {
        $aadAuthorityUri = "https://login.microsoftonline.com/common"
    }
    $config = Get-Content $navSettingsFile | ConvertFrom-Json
    Write-Host "Setting AadApplicationId to $aadApplicationId in $navsettingsFile"
    Add-Member -InputObject $config.NAVWebSettings -NotePropertyName "AadApplicationId" -NotePropertyValue $aadApplicationId -ErrorAction SilentlyContinue
    $config.NAVWebSettings."AadApplicationId" = $aadApplicationId
    Write-Host "Setting AadAuthorityUri to $aadAuthorityUri in $navsettingsFile"
    Add-Member -InputObject $config.NAVWebSettings -NotePropertyName "AadAuthorityUri" -NotePropertyValue $aadAuthorityUri -ErrorAction SilentlyContinue
    $config.NAVWebSettings."AadAuthorityUri" = $aadAuthorityUri
    $config | ConvertTo-Json -Depth 10 | Set-Content $navsettingsFile -Force
}
