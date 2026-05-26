<#
.SYNOPSIS
    Publishes Azure Function code and (optionally) the web app to an existing deployment.

.PARAMETER VarFile
    Path to the organization .tfvars file (or https:// URL). Used to derive resource names.

.PARAMETER Staging
    Publish to the staging Function App instead of production.

.EXAMPLE
    .\deploy-functionupdate.ps1 -VarFile organizations/my-org.tfvars
    .\deploy-functionupdate.ps1 -VarFile organizations/my-org.tfvars -Staging
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $VarFile,

    [Parameter(Mandatory = $false)]
    [switch] $Staging
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$FunctionProjectPath = Join-Path $scriptDir ".." "fkh-backend" | Resolve-Path

if (-not (Test-Path $FunctionProjectPath)) {
    throw "Function project path not found: $FunctionProjectPath"
}

# ── Resolve VarFile ───────────────────────────────────────────────────────────

if ($VarFile.StartsWith("https://")) {
    $downloadedFile = Join-Path $scriptDir "organizations" "_from_url.tfvars"
    New-Item -ItemType Directory -Path (Split-Path $downloadedFile) -Force | Out-Null
    Invoke-WebRequest -Uri $VarFile -OutFile $downloadedFile -UseBasicParsing
    Write-Host "Downloaded tfvars from URL -> $downloadedFile" -ForegroundColor Green
    $VarFile = $downloadedFile
}

if (-not (Test-Path $VarFile)) {
    throw "Var file not found: $VarFile"
}

# ── Parse tfvars ──────────────────────────────────────────────────────────────

function Get-TfVar([string]$Name, [string]$File) {
    $line = Get-Content $File | Where-Object { $_ -match "^\s*$Name\s*=" } | Select-Object -First 1
    if ($line -match '=\s*"([^"]+)"') { return $Matches[1] }
    throw "Could not find $Name in $File"
}

$deploymentName    = Get-TfVar "fkhDeploymentName" $VarFile
$ResourceGroupName = "fkh-$deploymentName"
$FunctionAppName   = if ($Staging) { "fkh-$deploymentName-backend-staging" } else { "fkh-$deploymentName-backend" }

Write-Host "  Deployment:     $deploymentName" -ForegroundColor Gray
Write-Host "  Function app:   $FunctionAppName" -ForegroundColor Gray
Write-Host "  Resource group: $ResourceGroupName" -ForegroundColor Gray
if ($Staging) {
    Write-Host "  Target: STAGING" -ForegroundColor Yellow
}

# ── Publish function code ────────────────────────────────────────────────────

Write-Host ""
Write-Host -ForegroundColor Cyan @'
 _____       _     _ _     _        __                  _   _                             _      
|  __ \     | |   | (_)   | |      / _|                | | (_)                           | |     
| |__) |   _| |__ | |_ ___| |__   | |_ _   _ _ __   ___| |_ _  ___  _ __     ___ ___   __| | ___ 
|  ___/ | | | '_ \| | / __| '_ \  |  _| | | | '_ \ / __| __| |/ _ \| '_ \   / __/ _ \ / _` |/ _ \
| |   | |_| | |_) | | \__ \ | | | | | | |_| | | | | (__| |_| | (_) | | | | | (_| (_) | (_| |  __/
|_|    \__,_|_.__/|_|_|___/_| |_| |_|  \__,_|_| |_|\___|\__|_|\___/|_| |_|  \___\___/ \__,_|\___|

'@
Write-Host "  Publishing function code" -ForegroundColor Cyan
Write-Host "  Project : $FunctionProjectPath" -ForegroundColor Gray

if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
    throw "Azure Functions Core Tools (func) is not installed. Install from https://aka.ms/azure-functions-core-tools"
}

Push-Location $FunctionProjectPath
try {
    func azure functionapp publish $FunctionAppName --dotnet-isolated --verbose
    # func may exit non-zero only due to a transient sync-triggers failure after a
    # successful deployment. Restart the app to force trigger sync instead.
    if ($LASTEXITCODE -ne 0) {
        Write-Host "func publish exited with code $LASTEXITCODE — restarting function app to force trigger sync..." -ForegroundColor Yellow
        az functionapp restart --name $FunctionAppName --resource-group $ResourceGroupName
        if ($LASTEXITCODE -ne 0) { throw "func publish and fallback restart both failed." }
        Write-Host "Function app restarted successfully." -ForegroundColor Green
    }

    # When deploying to production, also deploy to staging so it's never older than prod
    if (-not $Staging) {
        Push-Location $scriptDir
        try {
            $stagingAppName = terraform output -raw staging_function_app_name 2>$null
        }
        finally {
            Pop-Location
        }
        if (-not [string]::IsNullOrWhiteSpace($stagingAppName)) {
            Write-Host ""
            Write-Host "  Also publishing to staging: $stagingAppName" -ForegroundColor Yellow
            func azure functionapp publish $stagingAppName --dotnet-isolated --verbose
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Staging publish exited with code $LASTEXITCODE — restarting staging function app..." -ForegroundColor Yellow
                az functionapp restart --name $stagingAppName --resource-group $ResourceGroupName
                if ($LASTEXITCODE -ne 0) { Write-Host "Warning: staging publish failed, but production was deployed successfully." -ForegroundColor Yellow }
            }
        }
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host -ForegroundColor Green @'
 ______                _   _                             _                    _     _ _     _              _ 
|  ____|              | | (_)                           | |                  | |   | (_)   | |            | |
| |__ _   _ _ __   ___| |_ _  ___  _ __     ___ ___   __| | ___   _ __  _   _| |__ | |_ ___| |__   ___  __| |
|  __| | | | '_ \ / __| __| |/ _ \| '_ \   / __/ _ \ / _` |/ _ \ | '_ \| | | | '_ \| | / __| '_ \ / _ \/ _` |
| |  | |_| | | | | (__| |_| | (_) | | | | | (_| (_) | (_| |  __/ | |_) | |_| | |_) | | \__ \ | | |  __/ (_| |
|_|   \__,_|_| |_|\___|\__|_|\___/|_| |_|  \___\___/ \__,_|\___| | .__/ \__,_|_.__/|_|_|___/_| |_|\___|\__,_|
                                                                 | |                                         
                                                                 |_|                                         
'@

# ── Build and deploy web app (skip when deploying to staging only) ────────────

if (-not $Staging) {
    $webAppName = "fkh-$deploymentName-web"
    $webDeployToken = $null
    try {
        $secretsJson = az staticwebapp secrets list --name $webAppName --resource-group $ResourceGroupName 2>$null | ConvertFrom-Json
        $webDeployToken = $secretsJson.properties.apiKey
    } catch { }

    if (-not [string]::IsNullOrWhiteSpace($webDeployToken)) {
        $webPath = Join-Path $scriptDir ".." "fkh-web"
        if (Test-Path $webPath) {
            Write-Host "Building and deploying web app..." -ForegroundColor Cyan

            # Read GitHub App Client ID from tfvars for the Vite build
            try { $env:VITE_GITHUB_CLIENT_ID = Get-TfVar "github_app_client_id" $VarFile } catch { $env:VITE_GITHUB_CLIENT_ID = "" }

            Push-Location $webPath
            try {
                npm ci
                if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
                npm run build
                if ($LASTEXITCODE -ne 0) { throw "npm run build failed." }
                npx @azure/static-web-apps-cli deploy ./dist --deployment-token $webDeployToken --env production
                if ($LASTEXITCODE -ne 0) { throw "SWA deploy failed." }
                Write-Host "Web app deployed successfully." -ForegroundColor Green
            }
            finally {
                $env:VITE_GITHUB_CLIENT_ID = $null
                Pop-Location
            }
        }
        else {
            Write-Host "fkh-web folder not found — skipping web app deploy." -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "Web app not enabled — skipping." -ForegroundColor Yellow
    }
} # end if (-not $Staging)
