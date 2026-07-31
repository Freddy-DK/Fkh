$ErrorActionPreference = "Stop"
Import-Module BcContainerHelper -disableNameChecking
New-BcImage -artifactUrl $env:ARTIFACT_URL -imageName $env:IMAGE_NAME -baseImage $env:BASE_IMAGE -multitenant:$false

docker tag $env:IMAGE_NAME "$env:ACR_LOGIN_SERVER/businesscentral:$env:IMAGE_TAG"
docker push "$env:ACR_LOGIN_SERVER/businesscentral:$env:IMAGE_TAG"
docker tag $env:IMAGE_NAME "$env:ACR_LOGIN_SERVER/businesscentral-$($env:OS_VERSION):$env:IMAGE_TAG"
docker push "$env:ACR_LOGIN_SERVER/businesscentral-$($env:OS_VERSION):$env:IMAGE_TAG"
