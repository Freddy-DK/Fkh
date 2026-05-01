. c:\run\SetupDatabase.ps1

# CreateEncryptionKey.ps1
# Uploads c:\run\my\DynamicsNAV.key to blob storage if the blob doesn't already exist.
Write-Host "Check for encryption key"
$containerSasUrl = $env:ContainerBlobContainer
if ($containerSasUrl) {
    $uri = [System.Uri]$containerSasUrl
    $blobUrl = "$($uri.Scheme)://$($uri.Host)$($uri.AbsolutePath)/DynamicsNAV.key$($uri.Query)"
    Write-Host "ContainerBlobContainer is set. Checking for existing key in blob storage..."
    $keyPath = 'c:\run\my\DynamicsNAV.key'
    if (Test-Path $keyPath) {
        # Check if the blob already exists before uploading
        $blobExists = $false
        try {
            Invoke-WebRequest -Uri $blobUrl -Method Head -UseBasicParsing -ErrorAction Stop | Out-Null
            $blobExists = $true
        }
        catch {
            if ($_.Exception.Response.StatusCode.value__ -ne 404) {
                Write-Host "Warning: Could not check blob existence: $($_.Exception.Message)"
            }
        }
        if ($blobExists) {
            Write-Host "Encryption key already exists in blob storage (skipped upload)."
        }
        else {
            Write-Host "Encryption key found at $keyPath. Uploading to blob storage..."
            $bytes = [System.IO.File]::ReadAllBytes($keyPath)
            $headers = @{ 'x-ms-blob-type' = 'BlockBlob' }
            Invoke-WebRequest -Uri $blobUrl -Method Put -Headers $headers -Body $bytes -UseBasicParsing -ErrorAction Stop
            Write-Host "Encryption key uploaded to blob storage."
        }
    }
}