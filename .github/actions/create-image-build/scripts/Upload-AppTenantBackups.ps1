$ErrorActionPreference = "Stop"
$storageAccount = $env:DBS_STORAGE_ACCOUNT
$containerName = "cronus"
$exists = $true
foreach($dbname in @('app','tenant')) {
  $blobName = "$($env:IMAGE_TAG)-$dbname"
  $existingBlob = az storage blob show --account-name $storageAccount --container-name $containerName --name $blobName --auth-mode login 2>&1
  if ($LASTEXITCODE -ne 0) {
      $exists = $false
      break
  }
}
if ($exists) {
    Write-Host "Blobs for app and tenant databases already exist. Skipping upload."
    return
}

$bccontainerName = -join (1..8 | % { [char](Get-Random -InputObject (97..122)) })
"BC_CONTAINER_NAME=$bccontainerName" | Out-File -FilePath $env:GITHUB_ENV -Append

Import-Module BcContainerHelper -disableNameChecking
$credential = New-Object pscredential -ArgumentList 'admin', (ConvertTo-SecureString -String ([Guid]::NewGuid().ToString()) -AsPlainText -Force)
New-BcContainer -accept_eula -containerName $bccontainerName -artifactUrl $env:ARTIFACT_URL -auth UserPassword -Credential $credential -multitenant -myScripts @( @{ "SetupTenant.ps1" = {
  $customConfigFile = Join-Path (Get-Item "C:\Program Files\Microsoft Dynamics NAV\*\Service").FullName "CustomSettings.config"
  [xml]$customConfig = [System.IO.File]::ReadAllText($customConfigFile)
  $serverInstance = $customConfig.SelectSingleNode("//appSettings/add[@key='ServerInstance']").Value
  $multitenant = ($customConfig.SelectSingleNode("//appSettings/add[@key='Multitenant']").Value -eq "true")
  $databaseServer = $customConfig.SelectSingleNode("//appSettings/add[@key='DatabaseServer']").Value
  $databaseInstance = $customConfig.SelectSingleNode("//appSettings/add[@key='DatabaseInstance']").Value
  $databaseName = $customConfig.SelectSingleNode("//appSettings/add[@key='DatabaseName']").Value
  Write-Host -ForegroundColor Yellow "HERE WE GO $serverInstance $databaseName"
  $databaseServerInstance = $databaseServer
  if ("$databaseInstance" -ne "") {
      $databaseServerInstance = "$databaseServer\$databaseInstance"
  }
  Dismount-NavTenant -ServerInstance $ServerInstance -Tenant 'default' -Force | Out-Host
  Backup-SqlDatabase -ServerInstance $databaseServerInstance -database 'default' -BackupFile 'c:\run\my\tenant.bak'
  Backup-SqlDatabase -ServerInstance $databaseServerInstance -database $databaseName -BackupFile "c:\run\my\app.bak"
  Mount-NavTenant -ServerInstance $ServerInstance -Tenant 'default' -DatabaseName 'default' -OverwriteTenantIdInDatabase -Force | Out-Host
}}) -ErrorAction SilentlyIgnore
foreach($dbname in @('app','tenant')) {
  $blobName = "$($env:IMAGE_TAG)-$dbname"
  $bakFile = Join-Path $bcContainerHelperConfig.hostHelperFolder "extensions\$($bccontainerName)\my\$($dbname).bak"
  $existingBlob = az storage blob show --account-name $storageAccount --container-name $containerName --name $blobName --auth-mode login 2>&1
  if ($LASTEXITCODE -ne 0) {
      Write-Host "Blob '$blobName' not found in container '$containerName'. Uploading..."
      az storage container create --account-name $storageAccount --name $containerName --auth-mode login | Out-Null
      az storage blob upload --account-name $storageAccount --container-name $containerName --name $blobName --file $bakFile --auth-mode login --overwrite false
      if ($LASTEXITCODE -ne 0) { throw "Failed to upload database backup to storage account" }
      Write-Host "Upload complete."
  } else {
      Write-Host "Blob '$blobName' already exists in container '$containerName'. Skipping upload."
  }
}
