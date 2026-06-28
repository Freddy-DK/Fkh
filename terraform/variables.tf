# ── Azure ─────────────────────────────────────────────────────────────────────

variable "subscription_id" {
  description = "The Azure subscription ID to deploy into."
  type        = string
}

variable "tenant_id" {
  description = "The Azure AD tenant ID."
  type        = string
}

variable "azure_deploy_client_id" {
  description = "Client ID of the deployment identity (App Registration or Managed Identity). Used by deploy workflows for OIDC login — not consumed by Terraform resources."
  type        = string
  default     = ""
}

variable "location" {
  description = "Azure region for all resources."
  type        = string
  default     = "westeurope"
}

variable "state_location" {
  description = "Azure region for the Terraform state resource group and storage account. Leave empty to use 'location'. This variable is consumed by the deploy scripts; Terraform itself does not reference it."
  type        = string
  default     = ""
}

variable "fkhDeploymentName" {
  description = "Short identifier for this Fkh deployment. Used as a prefix for all Azure resource names (e.g. fkh-<name>-aks)."
  type        = string
}

# ── AKS ───────────────────────────────────────────────────────────────────────

variable "linux_vm_size" {
  description = "VM size for the Linux node pool. Use v5 series (v6 is not yet supported by AKS Windows nodes and SQL Server images)."
  type        = string
  default     = "Standard_D4s_v5"
}

variable "windows_vm_size" {
  description = "VM size for the Windows node pool. Use v5 series (v6 is not yet supported by AKS Windows nodes and SQL Server images)."
  type        = string
  default     = "Standard_D4s_v5"
}

variable "aks_sku_tier" {
  description = "AKS control plane tier. 'Free' for dev/test (no SLA). 'Standard' for production (99.95% SLA, ~$73/month)."
  type        = string
  default     = "Free"

  validation {
    condition     = contains(["Free", "Standard", "Premium"], var.aks_sku_tier)
    error_message = "aks_sku_tier must be one of: Free, Standard, Premium."
  }
}

variable "acr_sku" {
  description = "Azure Container Registry pricing tier. 'Basic' for dev/test, 'Standard' for production, 'Premium' for geo-replication."
  type        = string
  default     = "Basic"

  validation {
    condition     = contains(["Basic", "Standard", "Premium"], var.acr_sku)
    error_message = "acr_sku must be one of: Basic, Standard, Premium."
  }
}

variable "windows_min_node_count" {
  description = "Minimum number of Windows nodes to keep running (0 = scale to zero, 1 = always keep a warm node)."
  type        = number
  default     = 0
}

variable "windows_max_node_count" {
  description = "Maximum number of Windows nodes the autoscaler can scale to."
  type        = number
  default     = 10
}

variable "windows_spot_enabled" {
  description = "Whether to create a Windows Spot node pool for cheaper, interruptible workloads."
  type        = bool
  default     = false
}

variable "windows_spot_vm_size" {
  description = "VM size for the Windows Spot node pool. Use v5 series (v6 is not yet supported by AKS Windows nodes and SQL Server images)."
  type        = string
  default     = "Standard_D8s_v5"
}

variable "windows_spot_min_node_count" {
  description = "Minimum number of Windows Spot nodes (0 = scale to zero)."
  type        = number
  default     = 0
}

variable "windows_spot_max_node_count" {
  description = "Maximum number of Windows Spot nodes the autoscaler can scale to."
  type        = number
  default     = 10
}

variable "windows_overprovision" {
  description = "Keep a low-priority placeholder pod on Windows VMs to reserve spare capacity. When a real container is created it preempts the placeholder instantly, and the autoscaler provisions a new VM in the background."
  type        = bool
  default     = false
}

variable "windows_overprovision_cpu" {
  description = "CPU request for the overprovision placeholder pod (e.g. '250m', '500m')."
  type        = string
  default     = "250m"
}

variable "windows_overprovision_memory" {
  description = "Memory request for the overprovision placeholder pod (e.g. '3Gi', '6Gi')."
  type        = string
  default     = "3Gi"
}

variable "container_default_cpu" {
  description = "Default CPU request for BC containers when not specified by the user (e.g. '250m', '500m')."
  type        = string
  default     = "250m"
}

variable "container_default_memory" {
  description = "Default memory request for BC containers when not specified by the user (e.g. '3Gi', '4Gi')."
  type        = string
  default     = "3Gi"
}

variable "windows_prepull_images" {
  description = "List of container images to pre-pull on Windows nodes (e.g. ACR images). Empty list disables pre-pulling."
  type        = list(string)
  default     = []
}

# ── SQL Server ────────────────────────────────────────────────────────────────

variable "sql_sa_password" {
  description = "The SA password for SQL Server. Must be at least 8 characters."
  type        = string
  sensitive   = true

  validation {
    condition     = length(var.sql_sa_password) >= 8
    error_message = "SQL SA password must be at least 8 characters long."
  }
}

variable "namespace" {
  description = "Kubernetes namespace for the workload."
  type        = string
  default     = "app"
}

variable "sql_storage_size" {
  description = "Storage size for the SQL Server PVC."
  type        = string
  default     = "128Gi"
}

variable "sql_memory_limit_mb" {
  description = "SQL Server max memory (buffer pool) in MB. Controls MSSQL_MEMORY_LIMIT_MB inside the container."
  type        = number
  default     = 10240
}

# ── GitHub ────────────────────────────────────────────────────────────────────

# ── Function access config ────────────────────────────────────────────────────

variable "allowed_org_teams" {
  description = "List of GitHub org/team pairs the Azure Function will accept."
  type = list(object({
    org  = string
    team = string
  }))
}

variable "admin_org_teams" {
  description = "List of GitHub org/team pairs that grant admin access. Members also have normal access."
  type = list(object({
    org  = string
    team = string
  }))
  default = []
}

variable "support_org_teams" {
  description = "List of GitHub org/team pairs that grant support access. Support permissions are enforced in the backend."
  type = list(object({
    org  = string
    team = string
  }))
  default = []
}

variable "allowed_users" {
  description = "Explicit GitHub usernames and their roles (admin, member, or support). Checked in addition to team membership."
  type = list(object({
    user = string
    role = string
  }))
  default = []

  validation {
    condition = alltrue([
      for u in var.allowed_users : contains(["admin", "member", "support"], lower(u.role))
    ])
    error_message = "Each allowed_users entry must have role admin, member, or support."
  }
}

variable "allowed_oidc_repos" {
  description = "List of GitHub repositories (org/repo) allowed to authenticate via OIDC from GitHub Actions workflows."
  type        = list(string)
  default     = []
}

variable "allowed_ado_connections" {
  description = <<-EOT
    Azure DevOps service connections allowed to authenticate via OIDC.
    Each entry requires a Workload Identity Federation service connection in Azure DevOps.

    Create the service connection in Azure DevOps:
      Project Settings → Service connections → New → Azure Resource Manager
        → Workload Identity Federation (manual)
      Set the Client ID to the ado_identity_client_id Terraform output.
  EOT
  type = list(object({
    devops_org_id          = string
    devops_org             = string
    devops_project         = string
    devops_connection_name = string
    entra_subject          = optional(string, "")
  }))
  default = []
}


# ── Container image ──────────────────────────────────────────────────────────

variable "base_image" {
  description = "Base Docker image for Business Central container builds."
  type        = string
  default     = "mcr.microsoft.com/businesscentral:ltsc2022"
}

# ── GitHub App (per-organization, triggers image builds) ─────────────────────────

variable "github_app_id" {
  description = "GitHub App ID for triggering image-build workflows."
  type        = string
}

variable "github_app_client_id" {
  description = "Client ID of the GitHub App. Used by GitHub Actions app-token workflows and OAuth login in the web app. Find it on the GitHub App settings page."
  type        = string
  default     = ""
}

variable "github_app_private_key" {
  description = "PEM-encoded private key of the GitHub App. Set via TF_VAR_github_app_private_key, never in tfvars files."
  type        = string
  sensitive   = true
}

variable "github_app_installation_id" {
  description = "Installation ID of the GitHub App on the target repository."
  type        = string
}

variable "create_images_repo" {
  description = "GitHub org/repo of the deployment repository where the CreateImages workflow runs. Automatically set from github.repository context when deploying via GitHub Actions."
  type        = string
  default     = ""
}

variable "contact_email_for_letsencrypt" {
  description = "Contact email for Let's Encrypt certificate generation."
  type        = string
}

# ── User Settings ─────────────────────────────────────────────────────────────

variable "default_user_settings" {
  description = "Default user settings JSON. Deployed to the 'settings/defaultusersettings.json' blob. Keys '_members' and '_admins' define defaults for each role."
  type        = string
  default     = <<-EOT
    {
      "_members": {
        "MaxContainers": 3
      },
      "_admins": {
        "MaxContainers": 10
      }
    }
  EOT
}

variable "enable_aad_container_auth" {
  description = "Enable per-container AAD authentication. Passes the deployer's client ID to the Function so it can authenticate to Microsoft Graph via workload identity federation. Requires the deployer to be an App Registration (Option B) with Application.ReadWrite.OwnedBy and a federated credential trusting the Function MI (see Step 2, B.4)."
  type        = bool
  default     = false
}

variable "aad_auth_is_multitenant" {
  description = "When true, AAD App Registrations created for containers use multi-tenant sign-in (AzureADMultipleOrgs), allowing users from other Entra ID tenants. When false (default), apps are single-tenant (AzureADMyOrg). Only takes effect when enable_aad_container_auth is true."
  type        = bool
  default     = false
}

variable "aad_app_name_prefix" {
  description = "Optional prefix inserted before 'fkh' in AAD App Registration display names. For example, setting this to 'dbc-' produces 'dbc-fkh-<container>-auth' instead of 'fkh-<container>-auth'. Leave empty for the default naming."
  type        = string
  default     = ""
}

variable "aad_app_additional_owner" {
  description = "Optional Entra ID object ID of a user to add as an additional owner on every AAD App Registration created for containers. Leave empty to skip. Only takes effect when enable_aad_container_auth is true."
  type        = string
  default     = ""
}

variable "kubecost_enabled" {
  description = "Deploy Kubecost free tier for per-pod cost analysis."
  type        = bool
  default     = false
}

variable "enable_staging_backend" {
  description = "Deploy a staging Function App alongside production for testing backend changes."
  type        = bool
  default     = false
}

variable "enable_web_app" {
  description = "Deploy the Fkh web app as an Azure Static Web App."
  type        = bool
  default     = false
}

variable "static_web_app_location" {
  description = "Azure region for the Static Web App. Not all regions support Static Web Apps — westeurope and centralus are commonly available."
  type        = string
  default     = "westeurope"
}

variable "function_timeout_minutes" {
  description = "Maximum execution time for Azure Function invocations (in minutes). Also used by the CLI as its HTTP request timeout. Consumption plan maximum is 10."
  type        = number
  default     = 10

  validation {
    condition     = var.function_timeout_minutes >= 1 && var.function_timeout_minutes <= 10
    error_message = "function_timeout_minutes must be between 1 and 10 (Consumption plan limit)."
  }
}
