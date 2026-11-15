<#
.SYNOPSIS
    Builds and pushes the mssql-server-fts image to ACR.

.DESCRIPTION
    sql_version in the tfvars file selects the mcr.microsoft.com/mssql/server base image:
    2022 or 2025 (uses <version>-latest), or a specific tag such as 2025-CU9-ubuntu-24.04.
    The image is pushed as mssql-server-fts:<base tag>.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $VarFile,

    [Parameter(Mandatory = $true)]
    [string] $AcrLoginServer
)

$ErrorActionPreference = "Stop"

$sqlVersion = "2022"
$line = Get-Content $VarFile | Where-Object { $_ -match '^\s*sql_version\s*=' } | Select-Object -First 1
if ($line -match '=\s*"([^"]+)"') { $sqlVersion = $Matches[1] }

if ($sqlVersion -notmatch '^(2022|2025)(-.+)?$') {
    throw "Invalid sql_version '$sqlVersion'. Use 2022, 2025 or a specific mcr.microsoft.com/mssql/server tag such as 2025-CU9-ubuntu-24.04."
}
$sqlMajor = $Matches[1]
$baseTag = if ($sqlVersion.Contains('-')) { $sqlVersion } else { "$sqlVersion-latest" }

$baseImage = "mcr.microsoft.com/mssql/server:$baseTag"
$image = "$AcrLoginServer/mssql-server-fts:$baseTag"
Write-Host "SQL Server $sqlMajor ($baseImage) -> $image" -ForegroundColor Cyan

docker build --pull `
    --build-arg "BASE_IMAGE=$baseImage" `
    --build-arg "SQL_MAJOR=$sqlMajor" `
    -t $image `
    $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw "Docker build failed." }

docker push $image
if ($LASTEXITCODE -ne 0) { throw "Docker push of $image failed." }
