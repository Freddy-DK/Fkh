<#
.SYNOPSIS
  Wraps 'terraform apply' with automatic import-on-conflict for resources that
  already exist in Azure but are missing from Terraform state.

.DESCRIPTION
  When Azure resources are created or modified outside of Terraform (manual changes,
  partial applies, state drift), 'terraform apply' fails with 409 Conflict or
  "already exists" errors. This script catches those errors, imports the existing
  resources into state, and retries the apply automatically.

  Handles:
  - "A resource with the ID ... already exists" (general AzureRM pattern)
  - "RoleAssignmentExists" (role-assignment-specific 409)

.PARAMETER VarFile
  Path to the .tfvars file passed to terraform apply.

.PARAMETER Targets
  Optional array of -target resource addresses for targeted applies.

.PARAMETER MaxRetries
  Maximum number of import-and-retry cycles (default: 3).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$VarFile,

    [string[]]$Targets = @(),

    [int]$MaxRetries = 3
)

$ErrorActionPreference = 'Stop'

function Invoke-TerraformApply {
    $tfArgs = @('apply', '-var-file', $VarFile, '-auto-approve', '-no-color')
    foreach ($t in $Targets) {
        $tfArgs += '-target'
        $tfArgs += $t
    }
    Write-Host "Running: terraform $($tfArgs -join ' ')" -ForegroundColor Cyan
    $output = & terraform @tfArgs 2>&1 | Out-String
    return @{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Get-ResourceConflicts([string]$Output) {
    $conflicts = @()

    # ── Pattern 1: General "already exists" ──────────────────────────────────
    # Error: A resource with the ID "/subscriptions/.../..." already exists - to be managed via Terraform ...
    #   with azurerm_xxx.yyy,
    $alreadyExistsPattern = '(?s)A resource with the ID "([^"]+)" already exists.*?with\s+([^\s,]+)'
    $matches1 = [regex]::Matches($Output, $alreadyExistsPattern)
    foreach ($m in $matches1) {
        $conflicts += @{
            ResourceAddress = $m.Groups[2].Value
            AzureId         = $m.Groups[1].Value
            Source          = 'already-exists'
        }
    }

    # ── Pattern 2: RoleAssignmentExists (409) ────────────────────────────────
    # Error: ...RoleAssignmentExists: The role assignment already exists. The ID of the existing role assignment is <GUID>.
    #   with azurerm_role_assignment.xxx,
    $rolePattern = '(?s)RoleAssignmentExists:.*?The ID of the existing role assignment is ([0-9a-fA-F-]+).*?with\s+(azurerm_role_assignment\.[^\s,]+)'
    $matches2 = [regex]::Matches($Output, $rolePattern)
    foreach ($m in $matches2) {
        # Skip if we already captured this via pattern 1
        $addr = $m.Groups[2].Value
        if ($conflicts | Where-Object { $_.ResourceAddress -eq $addr }) { continue }
        $conflicts += @{
            ResourceAddress = $addr
            AzureId         = $null
            RoleGuid        = $m.Groups[1].Value
            Source          = 'role-assignment'
        }
    }

    return $conflicts
}

function Resolve-ImportId([hashtable]$Conflict) {
    if ($Conflict.Source -eq 'already-exists') {
        # Full Azure resource ID is already in the error message
        return $Conflict.AzureId
    }

    if ($Conflict.Source -eq 'role-assignment') {
        # Look up full ID via Azure CLI using the role assignment GUID
        $guid = $Conflict.RoleGuid
        Write-Host "  Looking up role assignment '$guid' via Azure CLI..." -ForegroundColor DarkYellow
        $fullId = az role assignment list --all --query "[?name=='$guid'].id | [0]" -o tsv 2>$null
        if ($fullId) { return $fullId }
        Write-Host "  WARNING: Could not find role assignment '$guid' in Azure." -ForegroundColor Red
        return $null
    }

    return $null
}

function Import-Resource([string]$ResourceAddress, [string]$ImportId) {
    Write-Host "  Importing: terraform import '$ResourceAddress' '$ImportId'" -ForegroundColor Yellow
    $importOutput = & terraform import -var-file $VarFile $ResourceAddress $ImportId 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host $importOutput
        Write-Host "  WARNING: Import failed for $ResourceAddress" -ForegroundColor Red
        return $false
    }
    Write-Host "  Imported successfully." -ForegroundColor Green
    return $true
}

# ── Main loop ────────────────────────────────────────────────────────────────

for ($attempt = 1; $attempt -le ($MaxRetries + 1); $attempt++) {
    if ($attempt -gt 1) {
        Write-Host "`n=== Retry attempt $($attempt - 1) of $MaxRetries ===" -ForegroundColor Magenta
    }

    $result = Invoke-TerraformApply

    if ($result.ExitCode -eq 0) {
        Write-Host $result.Output
        Write-Host "`nTerraform apply succeeded." -ForegroundColor Green
        exit 0
    }

    Write-Host $result.Output

    # Check if failures are importable conflicts
    $conflicts = Get-ResourceConflicts $result.Output
    if ($conflicts.Count -eq 0) {
        Write-Host "`nTerraform apply failed with non-recoverable errors." -ForegroundColor Red
        exit $result.ExitCode
    }

    if ($attempt -gt $MaxRetries) {
        Write-Host "`nMaximum retries ($MaxRetries) exhausted. Failing." -ForegroundColor Red
        exit $result.ExitCode
    }

    Write-Host "`nDetected $($conflicts.Count) resource conflict(s). Importing existing resources into state..." -ForegroundColor Yellow
    $importedAny = $false
    foreach ($conflict in $conflicts) {
        Write-Host "  Resource: $($conflict.ResourceAddress) [$($conflict.Source)]" -ForegroundColor Yellow
        $importId = Resolve-ImportId $conflict
        if (-not $importId) { continue }
        $imported = Import-Resource $conflict.ResourceAddress $importId
        if ($imported) { $importedAny = $true }
    }

    if (-not $importedAny) {
        Write-Host "`nNo resources could be imported. Failing." -ForegroundColor Red
        exit 1
    }
}
