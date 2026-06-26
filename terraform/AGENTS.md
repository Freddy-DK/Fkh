# terraform

Infrastructure-as-code for a full Fkh deployment: **AKS** (Linux system + Windows worker pools), **Azure Container Registry**, **Azure Functions**, storage, monitoring, optional **Static Web App**, and in-cluster **SQL Server** (mssql-fts image).

## Tech stack

- **Terraform >= 1.7**
- Providers: **azurerm** ~> 4.0, **azuread** ~> 2.47, **kubernetes** ~> 2.30, **helm** ~> 2.14, **random** ~> 3.6
- Remote state: **azurerm** backend (Azure Storage + Azure AD auth)

## Directory structure

```
terraform/
├── main.tf              # Providers, locals, resource group, AKS entry
├── variables.tf         # Input variables (incl. fkhDeploymentName)
├── outputs.tf           # acr_login_server, backend URL, SWA token, etc.
├── kubernetes.tf        # K8s secrets, SQL workload, Helm, BC-related resources
├── function.tf          # Function App, app settings, staging slot
├── acr.tf               # Container registry
├── webapp.tf            # Static Web App (optional)
├── identity.tf          # UAMI, federated credentials, RBAC
├── monitoring.tf        # Log Analytics, Application Insights
├── deploy.ps1           # Local full deploy helper
└── deploy-functionupdate.ps1
```

Org-specific values live in the **private deployment repo**: `config/deployment.tfvars` (see [deployment-repo/AGENTS.md](../deployment-repo/AGENTS.md)).

## Apply commands

### CI (recommended for production)

Triggered by `Deploy Full Stack` in the deployment repo → `.github/workflows/DeployFkhFullStack.yml`:

- `terraform init -reconfigure` with backend config from tfvars / workflow inputs
- Bootstrap apply (AKS, ACR, function skeleton) then full apply
- Build/push `mssql-fts` image, publish function, deploy web

### Local

```powershell
cd terraform
terraform init -reconfigure `
  -backend-config="resource_group_name=fkh-<name>-state" `
  -backend-config="storage_account_name=..." `
  -backend-config="container_name=tfstate" `
  -backend-config="key=<name>.tfstate" `
  -backend-config="use_azuread_auth=true"

terraform plan -var-file="path\to\deployment.tfvars"
terraform apply -var-file="path\to\deployment.tfvars"
```

Or:

```powershell
.\deploy.ps1 -VarFile path\to\deployment.tfvars
.\deploy-functionupdate.ps1 -VarFile path\to\deployment.tfvars
```

Sensitive variables via environment (CI sets these as secrets):

- `TF_VAR_sql_sa_password`
- `TF_VAR_github_app_private_key`
- `ARM_USE_OIDC`, `ARM_CLIENT_ID`, `ARM_TENANT_ID`, `ARM_SUBSCRIPTION_ID`

## Test commands

No automated Terraform tests in CI. Locally:

```powershell
terraform validate
terraform fmt -check
```

## Naming & conventions

- Resource prefix: `fkh-{fkhDeploymentName}-*` (e.g. `fkh-contoso-aks`, `fkh-contoso-backend`).
- `fkhDeploymentName` must be **lowercase alphanumeric only** (see `variables.tf`).
- Tags: `deployment`, `environment`, `managed_by = terraform`.
- Function CORS includes Static Web App URL and `http://localhost:5173` when web app is enabled.

## Architecture patterns

- **OIDC** federated credential for GitHub Actions deployment identity.
- Function App **user-assigned managed identity** → AKS, ACR, storage, Graph (when configured).
- Kubernetes provider targets the AKS cluster created in the same stack.
- SQL Server runs in-cluster using image `mssql-server-fts:2022-latest` from ACR (built from [mssql-fts/](../mssql-fts/AGENTS.md)).
- BC containers use ACR repository `businesscentral`; orchestration env vars wired in `function.tf`.

## When changing infrastructure

1. Update `.tf` files and `variables.tf` / `outputs.tf` as needed.
2. Document new secrets or tfvars keys in [Installation/](../Installation/AGENTS.md) if operators must set them.
3. Full stack changes go through deployment repo workflow; backend-only code may use **Update Backend** workflow without full Terraform apply.
4. Keep `deployment-repo/config/deployment.tfvars` template in sync with new required variables (comments in template).

## Related

- [mssql-fts/AGENTS.md](../mssql-fts/AGENTS.md)
- [fkh-backend/AGENTS.md](../fkh-backend/AGENTS.md) — consumes app settings from Terraform
- [.github/workflows/DeployFkhFullStack.yml](../.github/workflows/DeployFkhFullStack.yml)
