# ── Deployment configuration ─────────────────────────────────────────────────────────────────
# Edit this file with your organization's values.
# Deploy by running the "Deploy Full Stack" workflow in GitHub Actions.
#
# Never commit secrets (sql_sa_password, github_app_private_key) to source control.
# Set them as GitHub Secrets in this repository instead.
# The azure_deploy_client_id is safe to commit — it is not a secret.

#  _____             _                                  _     _   _                      
# |  __ \           | |                                | |   | \ | |                     
# | |  | | ___ _ __ | | ___  _   _ _ __ ___   ___ _ __ | |_  |  \| | __ _ _ __ ___   ___ 
# | |  | |/ _ \ '_ \| |/ _ \| | | | '_ ` _ \ / _ \ '_ \| __| | . ` |/ _` | '_ ` _ \ / _ \
# | |__| |  __/ |_) | | (_) | |_| | | | | | |  __/ | | | |_  | |\  | (_| | | | | | |  __/
# |_____/ \___| .__/|_|\___/ \__, |_| |_| |_|\___|_| |_|\__| |_| \_|\__,_|_| |_| |_|\___|
#             | |             __/ |                                                      
#             |_|            |___/                                                       
#
# Deployment name — a short identifier for this Fkh deployment.
# Used as a prefix for all Azure resource names (e.g. fkh-contoso-aks, fkh-contoso-backend).
# Needs to be lower case, letters and numbers only
fkhDeploymentName = "myorg"


#                                   _____      _   _   _                 
#     /\                           / ____|    | | | | (_)                
#    /  \    _____   _ _ __ ___   | (___   ___| |_| |_ _ _ __   __ _ ___ 
#   / /\ \  |_  / | | | '__/ _ \   \___ \ / _ \ __| __| | '_ \ / _` / __|
#  / ____ \  / /| |_| | | |  __/   ____) |  __/ |_| |_| | | | | (_| \__ \
# /_/    \_\/___|\__,_|_|  \___|  |_____/ \___|\__|\__|_|_| |_|\__, |___/
#                                                               __/ |    
#                                                              |___/     
# Azure — the subscription, tenant, and region where all Fkh infrastructure
# (AKS cluster, Function App, Container Registry, storage, etc.) will be created.
# Get these from the Azure Portal or by running:
#   az account show --query '{subscription_id:id, tenant_id:tenantId}' -o table
subscription_id         = "00000000-0000-0000-0000-000000000000"
tenant_id               = "00000000-0000-0000-0000-000000000000"
azure_deploy_client_id  = "00000000-0000-0000-0000-000000000000"  # Client ID of your deployment identity (App Registration or Managed Identity) from Step 2
location                = "westeurope"   # Azure region (e.g. westeurope, eastus, swedencentral)
state_location  = ""             # Azure region for the Terraform state resource group and storage account. Leave empty to use 'location'.


#  _  __     _                          _                _____      _   _   _                 
# | |/ /    | |                        | |              / ____|    | | | | (_)                
# | ' /_   _| |__   ___ _ __ _ __   ___| |_ ___  ___   | (___   ___| |_| |_ _ _ __   __ _ ___ 
# |  <| | | | '_ \ / _ \ '__| '_ \ / _ \ __/ _ \/ __|   \___ \ / _ \ __| __| | '_ \ / _` / __|
# | . \ |_| | |_) |  __/ |  | | | |  __/ ||  __/\__ \   ____) |  __/ |_| |_| | | | | (_| \__ \
# |_|\_\__,_|_.__/ \___|_|  |_| |_|\___|\__\___||___/  |_____/ \___|\__|\__|_|_| |_|\__, |___/
#                                                                                    __/ |    
#                                                                                   |___/     
aks_sku_tier                 = "Free"             # Free (dev/test, no SLA) | Standard (99.95% SLA) | Premium (99.99% SLA)
acr_sku                      = "Basic"            # Basic (dev/test) | Standard (production) | Premium (geo-replication)
linux_vm_size                = "Standard_D4s_v5"  # v6 not supported for sqlserver
windows_vm_size              = "Standard_D4s_v5"  # v6 not supported for hyhervisor gen1
windows_min_node_count       = 0                  # Set to 1 to keep a warm Windows node (~$70-100/mo)
windows_max_node_count       = 10                 # Maximum Windows nodes the autoscaler can scale to
windows_overprovision        = false              # Set to true to keep spare capacity for instant container scheduling
windows_overprovision_cpu    = "250m"             # CPU reserved by the overprovision placeholder
windows_overprovision_memory = "3Gi"              # Memory reserved by the overprovision placeholder
container_default_cpu        = "250m"             # Default CPU request for BC containers
container_default_memory     = "3Gi"              # Default memory request for BC containers
windows_spot_enabled         = false              # Set to true to add a Spot pricing Windows pool for lower cost
windows_spot_vm_size         = "Standard_D2ds_v5" # VM size for spot nodes
windows_spot_min_node_count  = 0                  # Minimum spot nodes (0 = scale to zero when idle)
windows_spot_max_node_count  = 10                 # Maximum spot nodes the autoscaler can scale to

# Images to pre-pull on Windows nodes (speeds up container creation)
windows_prepull_images = [
  # "<acr-name>.azurecr.io/businesscentral:<tag>"
]

# Kubecost — free per-pod cost analysis dashboard
# Needs minimum D4s Linux VM Size to enable
kubecost_enabled = false

# Staging — deploy a staging Function App alongside production for testing backend changes
enable_staging_backend = false

# Web App — deploy a Static Web App for browser-based container management
enable_web_app          = false
static_web_app_location = "westeurope"   # Azure region for the Static Web App (not all regions supported, see https://aka.ms/swa/regions)

# Function timeout — maximum execution time per request (in minutes). Also used by the CLI as its HTTP timeout.
# Consumption plan maximum is 10 minutes.
function_timeout_minutes = 10

# SQL Server
# sql_sa_password = ""  # set as GitHub Secret: SQL_SA_PASSWORD
namespace           = "app"
sql_storage_size    = "128Gi"
sql_memory_limit_mb = 10240              # SQL Server max buffer pool memory in MB (default 10 GiB)


#   _____            _        _                     _____      _   _   _                 
#  / ____|          | |      (_)                   / ____|    | | | | (_)                
# | |     ___  _ __ | |_ __ _ _ _ __   ___ _ __   | (___   ___| |_| |_ _ _ __   __ _ ___ 
# | |    / _ \| '_ \| __/ _` | | '_ \ / _ \ '__|   \___ \ / _ \ __| __| | '_ \ / _` / __|
# | |___| (_) | | | | || (_| | | | | |  __/ |      ____) |  __/ |_| |_| | | | | (_| \__ \
#  \_____\___/|_| |_|\__\__,_|_|_| |_|\___|_|     |_____/ \___|\__|\__|_|_| |_|\__, |___/
#                                                                               __/ |    
#                                                                              |___/     
# Contact email for Let's Encrypt
contact_email_for_letsencrypt = "admin@example.com"

# AAD container authentication — allows users to sign in with Microsoft 365 accounts
# Requires an App Registration deployer (Option B in Step 2) with:
#   1. Application.ReadWrite.OwnedBy Microsoft Graph permission (granted with admin consent)
#   2. A federated credential trusting the Function managed identity (added after first deploy)
# See Installation/Step2-AzureIdentity.md for details.
enable_aad_container_auth = false
aad_auth_is_multitenant  = false
# Optional prefix before 'fkh' in AAD app names (e.g. "dbc-" → dbc-fkh-<container>-auth)
aad_app_name_prefix      = ""
# Optional: Entra ID object ID of a user to add as co-owner on created AAD apps
# Find a user's object ID with: az ad user show --id user@domain.com --query id -o tsv
aad_app_additional_owner  = ""


#   _____ _ _   _    _       _         _____      _   _   _                 
#  / ____(_) | | |  | |     | |       / ____|    | | | | (_)                
# | |  __ _| |_| |__| |_   _| |__    | (___   ___| |_| |_ _ _ __   __ _ ___ 
# | | |_ | | __|  __  | | | | '_ \    \___ \ / _ \ __| __| | '_ \ / _` / __|
# | |__| | | |_| |  | | |_| | |_) |   ____) |  __/ |_| |_| | | | | (_| \__ \
#  \_____|_|\__|_|  |_|\__,_|_.__/   |_____/ \___|\__|\__|_|_| |_|\__, |___/
#                                                                  __/ |    
#                                                                 |___/     
# Authentication — GitHub is used to determine who the user is
# Authorization — GitHub teams that control who can use this Fkh deployment.
# These teams must already exist in your GitHub organization (see Step 4 of the installation guide).
# Values are case-sensitive.

# Member teams — users in these teams can provision containers
# Format: { org = "<github-org>", team = "<team-slug>" }
# The team slug is visible in the team URL: https://github.com/orgs/<org>/teams/<team-slug>
allowed_org_teams = [
  # { org = "my-company",    team = "Fkh-members" },
  # { org = "partner-org",   team = "Fkh-members" }
]

# Admin teams — members get admin access (and also have normal access)
# Format: { org = "<github-org>", team = "<team-slug>" }
# The team slug is visible in the team URL: https://github.com/orgs/<org>/teams/<team-slug>
admin_org_teams = [
  # { org = "my-company",    team = "Fkh-admins" }
]

# Support teams — members get support access (permissions defined in the backend)
# Format: { org = "<github-org>", team = "<team-slug>" }
# The team slug is visible in the team URL: https://github.com/orgs/<org>/teams/<team-slug>
support_org_teams = [
  # { org = "my-company", team = "Fkh-supporters" }
]

# Explicit users — grant access by GitHub username without team membership
# Format: { user = "<github-username>", role = "<role>" }
# Role must be admin, member, or support
allowed_users = [
  # { user = "octocat", role = "member" },
  # { user = "support-agent", role = "support" }
]

# Repositories — GitHub repos allowed to call Fkh via OIDC from GitHub Actions
# Please note that the AUTH token provided must be the ID token
allowed_oidc_repos = [
  # "my-company/my-bc-app"
]

# Azure DevOps — service connections allowed to call Fkh via OIDC from Azure Pipelines.
# Create the service connection in Azure DevOps:
#   Project Settings → Service connections → New → Azure Resource Manager
#     → Workload Identity Federation (manual)
#   - Service connection name: must match devops_connection_name below
#   - Application (client) ID: use the ado_identity_client_id Terraform output
#   - Tenant ID: your Azure AD tenant
#   - Subscription ID/Name: your Azure subscription
#   Save as draft, then copy the "Subject identifier" value into entra_subject below.
#   Deploy Terraform to create the federated credential, then verify the connection in ADO.
allowed_ado_connections = [
  # {
  #   devops_org_id          = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"   # Azure DevOps organization ID (open https://dev.azure.com/<org>/_apis/connectiondata and use the instanceId value)
  #   devops_org             = "my-org"                                 # Azure DevOps organization name
  #   devops_project         = "my-project"                             # Azure DevOps project name
  #   devops_connection_name = "fkh-oidc"                               # Service connection name (must match exactly)
  #   entra_subject          = "/eid1/c/pub/t/..."                      # Subject identifier from the ADO service connection form
  # }
]

# GitHub App — triggers image-build workflows in this deployment repo
github_app_id              = "1234567"  # paste your App ID here
github_app_client_id       = ""          # Client ID of the GitHub App (for web app OAuth login). Find it on the GitHub App settings page.
# github_app_private_key   = ""  # set as GitHub Secret: GH_APP_PRIVATE_KEY
github_app_installation_id = "123456789"  # paste your Installation ID here


#  _    _                   _____      _   _   _                 
# | |  | |                 / ____|    | | | | (_)                
# | |  | |___  ___ _ __   | (___   ___| |_| |_ _ _ __   __ _ ___ 
# | |  | / __|/ _ \ '__|   \___ \ / _ \ __| __| | '_ \ / _` / __|
# | |__| \__ \  __/ |      ____) |  __/ |_| |_| | | | | (_| \__ \
#  \____/|___/\___|_|     |_____/ \___|\__|\__|_|_| |_|\__, |___/
#                                                       __/ |    
#                                                      |___/     
# Default user settings (deployed to settings/usersettings.json in storage)
# _members = defaults for all users, _admins = defaults for admin users
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
