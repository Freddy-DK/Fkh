az acr login --name $env:ACR_LOGIN_SERVER
if ($LASTEXITCODE -ne 0) { throw "Failed to login to ACR" }
