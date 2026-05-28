$ErrorActionPreference = "Stop"
Import-Module BcContainerHelper -disableNameChecking
$storageAccount = $env:DBS_STORAGE_ACCOUNT
$containerName = "cronus"
$blobName = $env:IMAGE_TAG
$bakFile = $env:BAK_FILE
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
