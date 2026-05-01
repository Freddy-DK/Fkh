$ENV:DatabaseInstance = ''
if ("$env:licensefile" -eq "") {
    $ENV:licensefile = @(Get-Item "C:\Program Files\Microsoft Dynamics NAV\*\Service\*.bclicense")[0].FullName
}
# ReadEncryptionKey.ps1
# If ContainerBlobContainer is set and the encryption key blob exists, downloads it to c:\run\my\DynamicsNAV.key
Write-Host "Checking for encryption key in blob storage..."
$containerSasUrl = $env:ContainerBlobContainer
if ($containerSasUrl) {
    $uri = [System.Uri]$containerSasUrl
    $blobUrl = "$($uri.Scheme)://$($uri.Host)$($uri.AbsolutePath)/DynamicsNAV.key$($uri.Query)"
    Write-Host "ContainerBlobContainer is set. Attempting to download encryption key from blob storage..."
    $targetPath = 'c:\run\my\DynamicsNAV.key'
    try {
        Invoke-WebRequest -Uri $blobUrl -Method Get -OutFile $targetPath -UseBasicParsing -ErrorAction Stop
        Write-Host "Encryption key downloaded to $targetPath"
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 404) {
            Write-Host "No encryption key found in blob storage (first run)."
        }
        else {
            throw
        }
    }
}
. c:\run\SetupVariables.ps1
