# ── Managed Identity for the Azure Function ───────────────────────────────────
#
# This identity is what the Function uses to talk to AKS.
# No credentials, no secrets — Azure handles authentication at the infrastructure level.

resource "azurerm_user_assigned_identity" "function" {
  name                = local.function_identity_name
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
}

# Grant the Function's identity the "Azure Kubernetes Service Contributor" role
# on the AKS cluster.
resource "azurerm_role_assignment" "function_aks" {
  scope                = azurerm_kubernetes_cluster.this.id
  role_definition_name = "Azure Kubernetes Service Contributor Role"
  principal_id         = azurerm_user_assigned_identity.function.principal_id
}

# Grant the Function's identity "Storage Blob Data Contributor" on the dbs
# storage account so the Function can manage database backup downloads/uploads.
resource "azurerm_role_assignment" "function_dbs_storage" {
  scope                = azurerm_storage_account.dbs.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.function.principal_id
}

# Grant the Function's identity "Log Analytics Reader" so it can query
# ContainerRegistryRepositoryEvents for image pull timestamps.
resource "azurerm_role_assignment" "function_log_analytics_reader" {
  scope                = azurerm_log_analytics_workspace.this.id
  role_definition_name = "Log Analytics Reader"
  principal_id         = azurerm_user_assigned_identity.function.principal_id
}

# ── Graph access for per-container AAD App management ─────────────────────────
# The function creates a dedicated AAD App Registration for each container that
# uses AAD authentication, and deletes it when the container is removed.
# The Function MI authenticates as the deployer's app registration via workload
# identity federation — the federated credential on the deployer is created
# manually (see Installation/Step2-AzureIdentity.md, section B.4).
# The deployer's Application.ReadWrite.OwnedBy permission covers create/delete
# because apps created through it are owned by the deployer.

data "azuread_client_config" "current" {}

# ── Separate Managed Identity for the CreateImages GitHub Actions workflow ─────
#
# Least-privilege: this identity only has ACR Push + Storage Blob access.
# It deliberately does NOT have AKS or Log Analytics permissions.

resource "azurerm_user_assigned_identity" "github_actions" {
  name                = "${local.function_identity_name}-gh"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
}

resource "azurerm_federated_identity_credential" "github_actions" {
  name                      = "github-actions-createimages"
  user_assigned_identity_id = azurerm_user_assigned_identity.github_actions.id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = "https://token.actions.githubusercontent.com"
  subject                   = "repo:${var.create_images_repo}:ref:refs/heads/main"
}

resource "azurerm_role_assignment" "github_actions_acr_push" {
  scope                = azurerm_container_registry.this.id
  role_definition_name = "AcrPush"
  principal_id         = azurerm_user_assigned_identity.github_actions.principal_id
}

resource "azurerm_role_assignment" "github_actions_dbs_storage" {
  scope                = azurerm_storage_account.dbs.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.github_actions.principal_id
}

# ── Managed Identity for Azure DevOps OIDC connections ────────────────────────
#
# Least-privilege: this identity has NO role assignments.
# It exists solely as a trust anchor for ADO service connections that authenticate
# to the Fkh backend via OIDC. The backend validates the token and checks the
# subject claim against the allowed_ado_connections list.

resource "azurerm_user_assigned_identity" "ado" {
  count               = length(var.allowed_ado_connections) > 0 ? 1 : 0
  name                = "${local.function_identity_name}-ado"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
}

resource "azurerm_federated_identity_credential" "ado" {
  for_each = { for idx, conn in var.allowed_ado_connections : "${conn.devops_org}-${conn.devops_project}-${conn.devops_connection_name}" => conn }

  name                      = "ado-${substr(md5("${each.value.devops_org}/${each.value.devops_project}/${each.value.devops_connection_name}"), 0, 8)}"
  user_assigned_identity_id = azurerm_user_assigned_identity.ado[0].id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = "https://vstoken.dev.azure.com/${each.value.devops_org_id}"
  subject                   = "sc://${each.value.devops_org}/${each.value.devops_project}/${each.value.devops_connection_name}"
}
