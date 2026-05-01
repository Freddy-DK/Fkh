<#
.SYNOPSIS
    Publishes Azure Function code to an existing Function App.

.PARAMETER FunctionAppName
    Name of the Azure Function App. If omitted, read from Terraform output 'function_app_name'.

.PARAMETER ResourceGroupName
    Name of the Azure resource group containing the Function App. If omitted, read from Terraform output 'resource_group_name'.

.PARAMETER FunctionProjectPath
    Path to the FKH backend project folder. Defaults to ../fkh-backend relative to this script.

.EXAMPLE
    .\deploy-functionupdate.ps1
    .\deploy-functionupdate.ps1 -FunctionAppName fkh-myorg-backend
#>
param(
    [Parameter(Mandatory = $false)]
    [string] $FunctionAppName = "",

    [Parameter(Mandatory = $false)]
    [string] $ResourceGroupName = "",

    [Parameter(Mandatory = $false)]
    [string] $FunctionProjectPath = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($FunctionProjectPath)) {
    $FunctionProjectPath = Join-Path $scriptDir ".." "fkh-backend"
}
$FunctionProjectPath = Resolve-Path $FunctionProjectPath

if (-not (Test-Path $FunctionProjectPath)) {
    throw "Function project path not found: $FunctionProjectPath"
}

# ── Resolve function app name and resource group ─────────────────────────────

Push-Location $scriptDir
try {
    if ([string]::IsNullOrWhiteSpace($FunctionAppName)) {
        $FunctionAppName = terraform output -raw function_app_name
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($FunctionAppName)) {
            throw "Could not retrieve function_app_name from Terraform output. Pass -FunctionAppName explicitly."
        }
    }
    if ([string]::IsNullOrWhiteSpace($ResourceGroupName)) {
        $ResourceGroupName = terraform output -raw resource_group_name
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ResourceGroupName)) {
            throw "Could not retrieve resource_group_name from Terraform output. Pass -ResourceGroupName explicitly."
        }
    }
}
finally {
    Pop-Location
}
Write-Host "  Function app: $FunctionAppName" -ForegroundColor Gray
Write-Host "  Resource group: $ResourceGroupName" -ForegroundColor Gray

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
