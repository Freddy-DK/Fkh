<#
.SYNOPSIS
    Deploys an Fkh environment: runs terraform apply and publishes the function.

.PARAMETER VarFile
    Path to the organization .tfvars file, e.g. organizations/my-org.tfvars,
    or an https:// URL to download the file from.

.PARAMETER AutoApprove
    If specified, passes -auto-approve to terraform apply (no interactive prompt).

.EXAMPLE
    .\deploy.ps1 -VarFile organizations/my-org.tfvars
    .\deploy.ps1 -VarFile organizations/my-org.tfvars -AutoApprove
    .\deploy.ps1 -VarFile https://my-storage.blob.core.windows.net/config/my-org.tfvars
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $VarFile,

    [Parameter(Mandatory = $false)]
    [switch] $AutoApprove
)

$ErrorActionPreference = "Stop"

function Show-KubernetesDiagnostics {
    Write-Host ""
    Write-Host -ForegroundColor Red @'
 _______                   __                                         _          __      _ _          _ 
|__   __|                 / _|                                       | |        / _|    (_) |        | |
   | | ___ _ __ _ __ __ _| |_ ___  _ __ _ __ ___     __ _ _ __  _ __ | |_   _  | |_ __ _ _| | ___  __| |
   | |/ _ \ '__| '__/ _` |  _/ _ \| '__| '_ ` _ \   / _` | '_ \| '_ \| | | | | |  _/ _` | | |/ _ \/ _` |
   | |  __/ |  | | | (_| | || (_) | |  | | | | | | | (_| | |_) | |_) | | |_| | | || (_| | | |  __/ (_| |
   |_|\___|_|  |_|  \__,_|_| \___/|_|  |_| |_| |_|  \__,_| .__/| .__/|_|\__, | |_| \__,_|_|_|\___|\__,_|
                                                         | |   | |       __/ |                          
                                                         |_|   |_|      |___/                           
'@

    $kubectlCommand = Get-Command kubectl -ErrorAction SilentlyContinue
    if (-not $kubectlCommand) {
        Write-Host "kubectl is not installed or not on PATH. Skipping Kubernetes diagnostics." -ForegroundColor Yellow
        return
    }

    $commands = @(
        @{ Label = "Current context"; Cmd = "kubectl config current-context" },
        @{ Label = "Cluster info"; Cmd = "kubectl cluster-info" },
        @{ Label = "Storage classes"; Cmd = "kubectl get storageclass" },
        @{ Label = "PVCs in app namespace"; Cmd = "kubectl get pvc -n app -o wide" },
        @{ Label = "Describe mssql-data-pvc"; Cmd = "kubectl describe pvc mssql-data-pvc -n app" },
        @{ Label = "Recent events in app namespace"; Cmd = "kubectl get events -n app --sort-by=.metadata.creationTimestamp" },
        @{ Label = "Node status"; Cmd = "kubectl get nodes -o wide" }
    )

    foreach ($entry in $commands) {
        Write-Host ""
        Write-Host ("--- " + $entry.Label + " ---") -ForegroundColor Yellow
        try {
            Invoke-Expression $entry.Cmd
        }
        catch {
            Write-Host ("Failed: " + $entry.Cmd) -ForegroundColor Red
            Write-Host $_.Exception.Message -ForegroundColor Red
        }
    }
}

# ── Resolve paths ─────────────────────────────────────────────────────────────

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$FunctionProjectPath = Join-Path $scriptDir ".." "fkh-backend"
$FunctionProjectPath = Resolve-Path $FunctionProjectPath

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

Push-Location $scriptDir
try {

# ── Step 1: Terraform init ───────────────────────────────────────────────────

Write-Host ""
Write-Host -ForegroundColor Cyan @'
 _______                   __                       _       _ _   
|__   __|                 / _|                     (_)     (_) |  
   | | ___ _ __ _ __ __ _| |_ ___  _ __ _ __ ___    _ _ __  _| |_ 
   | |/ _ \ '__| '__/ _` |  _/ _ \| '__| '_ ` _ \  | | '_ \| | __|
   | |  __/ |  | | | (_| | || (_) | |  | | | | | | | | | | | | |_ 
   |_|\___|_|  |_|  \__,_|_| \___/|_|  |_| |_| |_| |_|_| |_|_|\__|
                                                                  
                                                                  
'@

# Force en-US locale so .NET tools don't misparse "8.0" as "80"
# on systems where the decimal separator is a comma (e.g. da-DK).
$env:LANG = "en_US.UTF-8"
$env:LC_ALL = "en_US.UTF-8"

# ── Parse key values from the .tfvars file for state storage setup ────────────
function Get-TfVar([string]$Name, [string]$File) {
    $line = Get-Content $File | Where-Object { $_ -match "^\s*$Name\s*=" } | Select-Object -First 1
    if ($line -match '=\s*"([^"]+)"') { return $Matches[1] }
    throw "Could not find $Name in $File"
}

$tfSubscriptionId = Get-TfVar "subscription_id" $VarFile
$tfLocation       = Get-TfVar "location"        $VarFile
$tfDeploymentName = Get-TfVar "fkhDeploymentName" $VarFile
try { $tfStateLocation = Get-TfVar "state_location" $VarFile } catch { $tfStateLocation = "" }
if (-not $tfStateLocation) { $tfStateLocation = $tfLocation }

$stateRg      = "fkh-$tfDeploymentName-state"
$stateAccount = "fkh$($tfDeploymentName.Replace('-','').Substring(0, [Math]::Min($tfDeploymentName.Replace('-','').Length, 14)))state"
$stateContainer = "tfstate"
$stateKey       = "$tfDeploymentName.tfstate"

Write-Host "Ensuring state storage: RG=$stateRg Account=$stateAccount Container=$stateContainer" -ForegroundColor Cyan

az account set --subscription $tfSubscriptionId
if ($LASTEXITCODE -ne 0) { throw "Failed to set Azure subscription to $tfSubscriptionId." }

az group create --name $stateRg --location $tfStateLocation --output none 2>$null
az storage account create --name $stateAccount --resource-group $stateRg --location $tfStateLocation --sku Standard_LRS --kind StorageV2 --allow-blob-public-access false --output none 2>$null

# Grant the current user "Storage Blob Data Contributor" on the state storage account
$currentUserId = az ad signed-in-user show --query id -o tsv
$stateAccountId = az storage account show --name $stateAccount --resource-group $stateRg --query id -o tsv
az role assignment create --role "Storage Blob Data Contributor" --assignee $currentUserId --scope $stateAccountId --output none 2>$null

az storage container create --name $stateContainer --account-name $stateAccount --auth-mode login --output none 2>$null

# Wait for RBAC propagation – role assignments can take up to a few minutes
$maxRetries = 12
$retryDelay = 10
for ($i = 1; $i -le $maxRetries; $i++) {
    $null = az storage blob list --container-name $stateContainer --account-name $stateAccount --auth-mode login --num-results 1 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Storage RBAC permission confirmed." -ForegroundColor Green
        break
    }
    if ($i -eq $maxRetries) {
        throw "Storage Blob Data Contributor role did not propagate after $($maxRetries * $retryDelay)s. Re-run the script or assign the role manually."
    }
    Write-Host "Waiting for RBAC propagation ($i/$maxRetries)..." -ForegroundColor Yellow
    Start-Sleep -Seconds $retryDelay
}

terraform init -reconfigure `
    -backend-config="resource_group_name=$stateRg" `
    -backend-config="storage_account_name=$stateAccount" `
    -backend-config="container_name=$stateContainer" `
    -backend-config="key=$stateKey" `
    -backend-config="subscription_id=$tfSubscriptionId" `
    -backend-config="use_azuread_auth=true"
if ($LASTEXITCODE -ne 0) { throw "Terraform init failed." }

# ── Remove orphaned resources from state ──────────────────────────────────────
# Resources that were once managed by Terraform but are no longer in the .tf
# files. Add new entries here when removing providers/resources.

$stateList = terraform state list 2>$null
$orphaned = $stateList | Where-Object { $_ -like 'github_*' }
foreach ($resource in $orphaned) {
    Write-Host "Removing orphaned resource: $resource" -ForegroundColor Yellow
    terraform state rm $resource
}

# ── Recover secrets from existing state (avoids re-prompting on redeploys) ────

if ([string]::IsNullOrWhiteSpace($env:TF_VAR_sql_sa_password) -or [string]::IsNullOrWhiteSpace($env:TF_VAR_github_app_private_key)) {
    Write-Host "Checking Terraform state for existing secrets..." -ForegroundColor Cyan
    try {
        $stateJson = terraform state pull 2>$null | ConvertFrom-Json
        if ($stateJson -and $stateJson.resources) {
            if ([string]::IsNullOrWhiteSpace($env:TF_VAR_sql_sa_password)) {
                $mssqlSecret = $stateJson.resources |
                    Where-Object { $_.type -eq "kubernetes_secret" -and $_.name -eq "mssql" } |
                    Select-Object -First 1
                $saPassword = $mssqlSecret.instances[0].attributes.data.'sa-password'
                if (-not [string]::IsNullOrWhiteSpace($saPassword)) {
                    $env:TF_VAR_sql_sa_password = $saPassword
                    Write-Host "TF_VAR_sql_sa_password restored from Terraform state." -ForegroundColor Green
                }
            }

            if ([string]::IsNullOrWhiteSpace($env:TF_VAR_github_app_private_key)) {
                $functionApp = $stateJson.resources |
                    Where-Object { $_.type -eq "azurerm_windows_function_app" -and $_.name -eq "this" } |
                    Select-Object -First 1
                $privateKey = $functionApp.instances[0].attributes.app_settings.GITHUB_APP_PRIVATE_KEY
                if (-not [string]::IsNullOrWhiteSpace($privateKey)) {
                    $env:TF_VAR_github_app_private_key = $privateKey
                    Write-Host "TF_VAR_github_app_private_key restored from Terraform state." -ForegroundColor Green
                }
            }
        }
    }
    catch {
        Write-Host "Could not read secrets from state (may be a fresh deploy)." -ForegroundColor Yellow
    }
}

# ── Prompt for any secrets still missing ──────────────────────────────────────

if ([string]::IsNullOrWhiteSpace($env:TF_VAR_sql_sa_password)) {
    $SqlSaPassword = Read-Host "Enter SQL SA password" -AsSecureString

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlSaPassword)
    try {
        $plainSqlSaPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }

    if ([string]::IsNullOrWhiteSpace($plainSqlSaPassword)) {
        throw "SQL SA password is required. Set TF_VAR_sql_sa_password or enter it when prompted."
    }

    $env:TF_VAR_sql_sa_password = $plainSqlSaPassword
    Write-Host "TF_VAR_sql_sa_password has been set for this session." -ForegroundColor Green
}

if ([string]::IsNullOrWhiteSpace($env:TF_VAR_github_app_private_key)) {
    $pemPath = Read-Host "Enter path to GitHub App private key (.pem file)"
    $pemPath = $pemPath.Trim('"', "'", ' ')

    if (-not (Test-Path $pemPath)) {
        throw "File not found: $pemPath"
    }

    $env:TF_VAR_github_app_private_key = Get-Content $pemPath -Raw
    Write-Host "TF_VAR_github_app_private_key has been set from $pemPath." -ForegroundColor Green
}

# ── Step 2: Bootstrap Azure infrastructure ────────────────────────────────────
# The Kubernetes provider depends on AKS kubeconfig values. On a fresh deploy
# AKS doesn't exist yet, so we must create it first with a targeted apply
# before any command that triggers full provider initialization.

Write-Host ""
Write-Host -ForegroundColor Cyan @'
 ____              _       _                   _                          
|  _ \            | |     | |                 | |                         
| |_) | ___   ___ | |_ ___| |_ _ __ __ _ _ __ | |                         
|  _ < / _ \ / _ \| __/ __| __| '__/ _` | '_ \| |                         
| |_) | (_) | (_) | |_\__ \ |_| | | (_| | |_) |_|                         
|____/ \___/ \___/ \__|___/\__|_|  \__,_| .__/(_)                         
                                        | |                                
                                        |_|                                
'@

$bootstrapArgs = @(
    "apply",
    "-var-file=$VarFile",
    "-target=azurerm_resource_group.this",
    "-target=azurerm_kubernetes_cluster.this",
    "-target=azurerm_kubernetes_cluster_node_pool.win",
    "-target=azurerm_kubernetes_cluster_node_pool.winspot",
    "-target=azurerm_container_registry.this",
    "-target=azurerm_role_assignment.aks_acr_pull",
    "-target=azurerm_role_assignment.function_acr_push",
    "-target=azurerm_storage_account.function",
    "-target=azurerm_storage_account.dbs",
    "-target=azurerm_service_plan.function",
    "-target=azurerm_log_analytics_workspace.this",
    "-target=azurerm_application_insights.this",
    "-target=azurerm_windows_function_app.this",
    "-target=azurerm_user_assigned_identity.function",
    "-target=azurerm_federated_identity_credential.github_actions",
    "-target=azurerm_role_assignment.function_aks",
    "-target=azurerm_role_assignment.function_dbs_storage",
    "-target=azurerm_role_assignment.function_log_analytics_reader",
    "-target=data.azuread_service_principal.msgraph",
    "-target=azuread_app_role_assignment.function_graph_app_owned"
)
if ($AutoApprove) { $bootstrapArgs += "-auto-approve" }

terraform @bootstrapArgs
if ($LASTEXITCODE -ne 0) {
    throw "Bootstrap apply failed."
}

# ── Refresh kubeconfig after bootstrap (AKS now exists) ──────────────────────
$aksRg   = "fkh-$tfDeploymentName"
$aksName = "fkh-$tfDeploymentName-aks"
Write-Host "Fetching AKS credentials for $aksName..." -ForegroundColor Cyan
az aks get-credentials --resource-group $aksRg --name $aksName --overwrite-existing
if ($LASTEXITCODE -ne 0) { Write-Host "Warning: could not fetch AKS credentials." -ForegroundColor Yellow }

# ── Build and push MSSQL FTS image to ACR ─────────────────────────────────────
$acrName = terraform output -raw acr_name
$acrLoginServer = terraform output -raw acr_login_server
if ($acrName -and $acrLoginServer) {
    Write-Host "Building and pushing MSSQL FTS image to $acrLoginServer..." -ForegroundColor Cyan
    az acr login --name $acrName
    $imageTag = "$acrLoginServer/mssql-server-fts:2022-latest"
    docker build -t $imageTag "$PSScriptRoot/../mssql-fts"
    if ($LASTEXITCODE -ne 0) { throw "Docker build failed." }
    docker push $imageTag
    if ($LASTEXITCODE -ne 0) { throw "Docker push failed." }
    Write-Host "MSSQL FTS image pushed successfully." -ForegroundColor Green
}

# ── Step 3: Full Terraform apply ──────────────────────────────────────────────

Write-Host ""
Write-Host -ForegroundColor Cyan @'
 _______                   __                                         _       
|__   __|                 / _|                                       | |      
   | | ___ _ __ _ __ __ _| |_ ___  _ __ _ __ ___     __ _ _ __  _ __ | |_   _ 
   | |/ _ \ '__| '__/ _` |  _/ _ \| '__| '_ ` _ \   / _` | '_ \| '_ \| | | | |
   | |  __/ |  | | | (_| | || (_) | |  | | | | | | | (_| | |_) | |_) | | |_| |
   |_|\___|_|  |_|  \__,_|_| \___/|_|  |_| |_| |_|  \__,_| .__/| .__/|_|\__, |
                                                         | |   | |       __/ |
                                                         |_|   |_|      |___/ 
'@

$applyArgs = @("apply", "-var-file=$VarFile")
if ($AutoApprove) { $applyArgs += "-auto-approve" }

terraform @applyArgs
if ($LASTEXITCODE -ne 0) {
    Show-KubernetesDiagnostics
    throw "Terraform apply failed."
}

$functionAppName = terraform output -raw function_app_name
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionAppName)) {
    throw "Could not retrieve function_app_name from Terraform output."
}

$resourceGroupName = terraform output -raw resource_group_name
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resourceGroupName)) {
    throw "Could not retrieve resource_group_name from Terraform output."
}

# ── Step 4: Publish function code + web app ───────────────────────────────────────

& (Join-Path $scriptDir "deploy-functionupdate.ps1") -VarFile $VarFile
if ($LASTEXITCODE -ne 0) { throw "Function publish failed." }

# Publish to staging if enabled
$stagingFunctionAppName = terraform output -raw staging_function_app_name 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($stagingFunctionAppName)) {
    Write-Host "Publishing to staging function app: $stagingFunctionAppName" -ForegroundColor Cyan
    & (Join-Path $scriptDir "deploy-functionupdate.ps1") -VarFile $VarFile -Staging
    if ($LASTEXITCODE -ne 0) { throw "Staging function publish failed." }
}

# ── Step 5: Sync GitHub Actions secrets from Terraform outputs ────────────────

$ghCommand = Get-Command gh -ErrorAction SilentlyContinue
if ($ghCommand) {
    Write-Host ""
    Write-Host "Syncing GitHub Actions secrets from Terraform outputs..." -ForegroundColor Cyan

    $secrets = @{
        AZURE_CLIENT_ID        = terraform output -raw identity_client_id
        AZURE_TENANT_ID        = terraform output -raw tenant_id
        AZURE_SUBSCRIPTION_ID  = terraform output -raw subscription_id
        ACR_LOGIN_SERVER       = terraform output -raw acr_login_server
        DBS_STORAGE_ACCOUNT    = terraform output -raw dbs_storage_account_name
    }

    $repo = terraform output -raw create_images_repo
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repo)) {
        Write-Host "  create_images_repo is not set — skipping secret sync." -ForegroundColor Yellow
        Write-Host "  Set TF_VAR_create_images_repo=org/repo to enable secret sync for CreateImages." -ForegroundColor Yellow
        return
    }

    foreach ($kv in $secrets.GetEnumerator()) {
        if (-not [string]::IsNullOrWhiteSpace($kv.Value)) {
            gh secret set $kv.Key --repo $repo --body $kv.Value
            Write-Host "  Set secret $($kv.Key)" -ForegroundColor Green
        }
        else {
            Write-Host "  Skipped $($kv.Key) (empty output)" -ForegroundColor Yellow
        }
    }
}
else {
    Write-Host "GitHub CLI (gh) not found — skipping GitHub Actions secret sync." -ForegroundColor Yellow
    Write-Host "Set these secrets manually in the repo: AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID, ACR_LOGIN_SERVER" -ForegroundColor Yellow
}

Write-Host ""
Write-Host -ForegroundColor Green @'
 _____             _                                  _                               _      _       
|  __ \           | |                                | |                             | |    | |      
| |  | | ___ _ __ | | ___  _   _ _ __ ___   ___ _ __ | |_    ___ ___  _ __ ___  _ __ | | ___| |_ ___ 
| |  | |/ _ \ '_ \| |/ _ \| | | | '_ ` _ \ / _ \ '_ \| __|  / __/ _ \| '_ ` _ \| '_ \| |/ _ \ __/ _ \
| |__| |  __/ |_) | | (_) | |_| | | | | | |  __/ | | | |_  | (_| (_) | | | | | | |_) | |  __/ ||  __/
|_____/ \___| .__/|_|\___/ \__, |_| |_| |_|\___|_| |_|\__|  \___\___/|_| |_| |_| .__/|_|\___|\__\___|
            | |             __/ |                                              | |                   
            |_|            |___/                                               |_|                   
'@
}
finally {
    Pop-Location
}
