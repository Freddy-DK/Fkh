. c:\run\AdditionalSetup.ps1
if (!(Get-NAVServerUser -ServerInstance $ServerInstance @tenantParam -ErrorAction Ignore | Where-Object { $_.UserName -eq $username })) {
    Write-Host "Creating SUPER user"
    New-NavServerUser -ServerInstance $ServerInstance @tenantParam -Username $username -Password $securePassword -AuthenticationEMail $authenticationEMail
    New-NavServerUserPermissionSet -ServerInstance $ServerInstance @tenantParam -username $username -PermissionSetId SUPER
}
else {
    Write-Host "SUPER user already exists"
    Set-NavServerUser -ServerInstance $ServerInstance @tenantParam -Username $username -Password $securePassword
    if ($authenticationEMail) {
        Write-Host "Updating authentication email"
        Set-NavServerUser -ServerInstance $ServerInstance @tenantParam -Username $username -AuthenticationEMail $authenticationEMail
    }
}
