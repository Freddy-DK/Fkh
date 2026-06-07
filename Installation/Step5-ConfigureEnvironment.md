# Step 5 — Configure Your Environment

> **Performed by:** GitHub Organization Administrator, using values collected in earlier steps

In this step you complete the deployment configuration and create the GitHub Secrets required by the workflows.

All work in this step happens in the private deployment repository you created in Step 1.

## What you will configure

| Item | Stored in | Contains secrets? |
|---|---|---|
| Deployment settings | `config/deployment.tfvars` | No |
| SQL Server SA password | GitHub Secret | Yes |
| GitHub App private key | GitHub Secret | Yes |

---

## 5.1 — Edit deployment.tfvars

Open `config/deployment.tfvars` in your deployment repository. Use the sections below as a checklist.

### Deployment name

```hcl
fkhDeploymentName = "contoso"
```

This is a short identifier for the deployment. It is used in Azure resource names, for example:

```text
fkh-contoso-aks
fkh-contoso-backend
```

Use lowercase letters and numbers only. Uppercase letters, hyphens, and other special characters are not allowed because Azure storage account names derived from this value have the same restrictions.

### Azure settings

```hcl
subscription_id        = "00000000-0000-0000-0000-000000000000"
tenant_id              = "00000000-0000-0000-0000-000000000000"
azure_deploy_client_id = "00000000-0000-0000-0000-000000000000"
location               = "westeurope"
state_location         = ""
```

| Setting | What to enter | Where to find it |
|---|---|---|
| `subscription_id` | Azure subscription ID | Azure Portal → **Subscriptions** → target subscription → **Subscription ID** |
| `tenant_id` | Entra ID tenant ID | Azure Portal → **Microsoft Entra ID** → **Overview** → **Tenant ID** |
| `azure_deploy_client_id` | Client ID of the deployment identity from Step 2 | Managed Identity: Azure Portal → MI → **Overview** → **Client ID**. App Registration: Azure Portal → **App registrations** → app → **Overview** → **Application (client) ID** |
| `location` | Azure region for Fkh resources | Choose a region close to your users, for example `westeurope`, `eastus`, or `swedencentral` |
| `state_location` | Azure region for the Terraform state resource group and storage account | Leave empty to use the same region as `location`. Set explicitly only if your organization requires Terraform state to live in a different region. |

If you use Azure CLI, you can also find these values with:

```pwsh
az account show --query id -o tsv
az account show --query tenantId -o tsv
```

### Kubernetes settings

These settings have safe defaults for a first deployment. You can tune them later.

| Setting | Default | Guidance |
|---|---|---|
| `aks_sku_tier` | `"Free"` | Use `Free` for dev/test. Use `Standard` or `Premium` when you need an SLA. |
| `linux_vm_size` | `"Standard_D4s_v5"` | Linux system node pool. D4s is the minimum recommended size for SQL Server. |
| `windows_vm_size` | `"Standard_D4s_v5"` | Windows node pool for Business Central containers. |
| `windows_min_node_count` | `0` | Use `0` to scale to zero when idle. Use `1` to keep a warm node. |
| `windows_max_node_count` | `10` | Maximum Windows nodes the autoscaler can create. |

> **Cost tip:** with `windows_min_node_count = 0`, you pay for Windows nodes only when containers are running. The tradeoff is that the first container starts more slowly because AKS must provision a Windows node.

The spot pool, overprovision, and prepull settings are optional optimizations. Leave them unchanged for your first deployment unless you already know you need them.

### SQL Server settings

```hcl
namespace        = "app"
sql_storage_size = "128Gi"
```

Keep these defaults unless you have a specific reason to change them.

The SQL Server SA password is not stored in this file. You will create it as a GitHub Secret later in this step.

### Contact email for certificates

```hcl
contact_email_for_letsencrypt = "admin@example.com"
```

Enter a real email address. Let's Encrypt uses this address for certificate expiry notifications.

### AAD container authentication

```hcl
enable_aad_container_auth = false
```

Set this to `true` only if users should sign in to Business Central containers with their Microsoft 365 accounts.

If you set it to `true`, the deployment identity must have the **`Application.ReadWrite.OwnedBy`** Microsoft Graph permission. See Step 2, section A.5 or B.4.

### AAD multi-tenant

```hcl
aad_auth_is_multitenant = false
```

Set this to `true` to create AAD App Registrations as multi-tenant, allowing users from other Entra ID tenants to sign in to containers. Only takes effect when `enable_aad_container_auth` is `true`.

### GitHub teams

Use the teams created or selected in Step 4.

```hcl
allowed_org_teams = [
  { org = "my-company", team = "Fkh-members" }
]

admin_org_teams = [
  { org = "my-company", team = "Fkh-admins" }
]

support_org_teams = [
  # { org = "my-company", team = "Fkh-support" }
]

allowed_users = [
  # { user = "octocat", role = "member" }
]
```

| Setting | What it controls |
|---|---|
| `allowed_org_teams` | Teams whose members can provision Business Central containers |
| `admin_org_teams` | Teams whose members get admin access and normal access |
| `support_org_teams` | Teams whose members get support access |
| `allowed_users` | Explicit GitHub usernames and roles (`admin`, `member`, or `support`) |

> **Important:** organization and team names are case-sensitive.

### OIDC repositories

```hcl
allowed_oidc_repos = [
  # "my-company/my-bc-app"
]
```

Use this only when another GitHub repository needs to authenticate to the Fkh Function App through OIDC, for example a CI/CD pipeline that creates containers.

Leave this list empty if you do not need that scenario.

### GitHub App settings

```hcl
github_app_id              = "123456"
github_app_installation_id = "12345678"
```

| Setting | Where to find it |
|---|---|
| `github_app_id` | Step 3.4 → GitHub App settings page |
| `github_app_installation_id` | Step 3.4 → final number in the GitHub App installation URL |

Do not put the GitHub App private key in `deployment.tfvars`. You will store it as a GitHub Secret.

### Default user settings

```hcl
default_user_settings = <<-EOT
  {
    "_members": {
      "MaxContainers": 3
    },
    "_admins": {
      "MaxContainers": 10
    }
  }
EOT
```

This controls default limits, such as how many simultaneous containers a user can have. Adjust the numbers to match your environment.

---

## 5.2 — Commit deployment.tfvars

Commit and push `config/deployment.tfvars` to the `main` branch of the deployment repository.

This file should contain only non-secret settings. Sensitive values belong in GitHub Secrets.

---

## 5.3 — Create GitHub Secrets

In the deployment repository, go to:

```text
Settings → Secrets and variables → Actions → New repository secret
```

Create the following repository secrets.

### SQL_SA_PASSWORD

Value: the SA password for the SQL Server that runs in AKS.

You choose this password. It must be at least 8 characters and meet SQL Server complexity requirements, including uppercase, lowercase, and a number or special character.

> **Important:** changing this password after deployment requires manual intervention. Choose a strong password and store it in a password manager.

### GH_APP_PRIVATE_KEY

Value: the PEM-encoded private key for the GitHub App from Step 3.

Paste the full contents of the `.pem` file, including these lines:

```text
-----BEGIN RSA PRIVATE KEY-----
...
-----END RSA PRIVATE KEY-----
```

---

## Step summary

After completing this step, your deployment repository should contain:

| Item | Location | Source |
|---|---|---|
| Completed configuration file | `config/deployment.tfvars` | Values collected in Steps 1–4 |
| `SQL_SA_PASSWORD` | GitHub Secret | Password you choose |
| `GH_APP_PRIVATE_KEY` | GitHub Secret | GitHub App private key from Step 3 |

> The deployment identity client ID (`azure_deploy_client_id`) and Azure IDs (`tenant_id`, `subscription_id`) are all in `config/deployment.tfvars` — no secrets needed for these.

You are ready to deploy.

---

*Previous: [Step 4 — Set Up GitHub Teams](Step4-GitHubTeams.md)*  
*Next: [Step 6 — Deploy](Step6-Deploy.md)*
