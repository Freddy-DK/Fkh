# ── Storage Account (database backups) ────────────────────────────────────────

resource "azurerm_storage_account" "dbs" {
  name                     = local.dbs_storage_name
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = "Standard"
  account_replication_type = "LRS"

  tags = azurerm_resource_group.this.tags
}

# ── Settings blob container + default user settings ──────────────────────────

resource "azurerm_storage_container" "settings" {
  name                  = "settings"
  storage_account_id    = azurerm_storage_account.dbs.id
  container_access_type = "private"
}

resource "azurerm_storage_blob" "default_user_settings" {
  name                   = "defaultusersettings.json"
  storage_account_name   = azurerm_storage_account.dbs.name
  storage_container_name = azurerm_storage_container.settings.name
  type                   = "Block"
  content_type           = "application/json"
  source_content         = var.default_user_settings
}

# ── Storage Account (required by Azure Functions runtime) ─────────────────────

resource "azurerm_storage_account" "function" {
  name                     = local.function_storage_name
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = "Standard"
  account_replication_type = "LRS"

  tags = azurerm_resource_group.this.tags
}

# ── App Service Plan (Consumption = pay-per-execution, no idle cost) ──────────

resource "azurerm_service_plan" "function" {
  name                = local.function_plan_name
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  os_type             = "Windows"
  sku_name            = "Y1"
}

# ── Shared app settings (used by both production and staging Function Apps) ──

locals {
  function_app_settings = {
    FUNCTIONS_WORKER_RUNTIME                = "dotnet-isolated"
    AZURE_CLIENT_ID                         = azurerm_user_assigned_identity.function.client_id
    AKS_SUBSCRIPTION_ID                     = var.subscription_id
    AKS_RESOURCE_GROUP                      = azurerm_resource_group.this.name
    AKS_CLUSTER_NAME                        = azurerm_kubernetes_cluster.this.name
    ACR_NAME                                = azurerm_container_registry.this.name
    BASE_IMAGE                              = var.base_image
    ALLOWED_ORG_TEAMS                       = jsonencode(var.allowed_org_teams)
    ADMIN_ORG_TEAMS                          = jsonencode(var.admin_org_teams)
    SUPPORT_ORG_TEAMS                        = jsonencode(var.support_org_teams)
    ALLOWED_USERS                            = jsonencode(var.allowed_users)
    ALLOWED_OIDC_REPOS                       = jsonencode(var.allowed_oidc_repos)
    AKS_LOCATION                             = var.location
    CONTACT_EMAIL_FOR_LETSENCRYPT             = var.contact_email_for_letsencrypt
    GITHUB_APP_ID                            = var.github_app_id
    GITHUB_APP_CLIENT_ID                     = var.github_app_client_id
    GITHUB_APP_PRIVATE_KEY                   = var.github_app_private_key
    GITHUB_APP_INSTALLATION_ID               = var.github_app_installation_id
    GITHUB_REPO_OWNER                        = split("/", var.create_images_repo)[0]
    GITHUB_REPO_NAME                         = split("/", var.create_images_repo)[1]
    DBS_STORAGE_ACCOUNT_NAME                 = azurerm_storage_account.dbs.name
    LOG_ANALYTICS_WORKSPACE_ID               = azurerm_log_analytics_workspace.this.id
    CONTAINER_DEFAULT_CPU                     = var.container_default_cpu
    CONTAINER_DEFAULT_MEMORY                  = var.container_default_memory
    AAD_TENANT_ID                            = var.tenant_id
    AAD_AUTH_IS_MULTITENANT                   = tostring(var.aad_auth_is_multitenant)
    AAD_APP_NAME_PREFIX                       = var.aad_app_name_prefix
    AAD_APP_ADDITIONAL_OWNER                   = var.aad_app_additional_owner
    AAD_GRAPH_CLIENT_ID                       = var.enable_aad_container_auth ? data.azuread_client_config.current.client_id : ""
    APPINSIGHTS_INSTRUMENTATIONKEY          = azurerm_application_insights.this.instrumentation_key
    APPLICATIONINSIGHTS_CONNECTION_STRING   = azurerm_application_insights.this.connection_string
    FUNCTION_TIMEOUT_MINUTES                = tostring(var.function_timeout_minutes)
    AzureFunctionsJobHost__functionTimeout  = format("00:%02d:00", var.function_timeout_minutes)
  }
}

# ── Azure Function App ───────────────────────────────────────────────────────

resource "azurerm_windows_function_app" "this" {
  name                = local.function_app_name
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location

  storage_account_name       = azurerm_storage_account.function.name
  storage_account_access_key = azurerm_storage_account.function.primary_access_key
  service_plan_id            = azurerm_service_plan.function.id

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.function.id]
  }

  site_config {
    application_stack {
      dotnet_version              = "v8.0"
      use_dotnet_isolated_runtime = true
    }
    dynamic "cors" {
      for_each = length(local.cors_origins) > 0 ? [1] : []
      content {
        allowed_origins     = local.cors_origins
        support_credentials = true
      }
    }
  }

  app_settings = local.function_app_settings

  tags = azurerm_resource_group.this.tags
}

# ── Staging Function App (optional, same identity + settings as production) ──

resource "azurerm_windows_function_app" "staging" {
  count               = var.enable_staging_backend ? 1 : 0
  name                = "${local.function_app_name}-staging"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location

  storage_account_name       = azurerm_storage_account.function.name
  storage_account_access_key = azurerm_storage_account.function.primary_access_key
  service_plan_id            = azurerm_service_plan.function.id

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.function.id]
  }

  site_config {
    application_stack {
      dotnet_version              = "v8.0"
      use_dotnet_isolated_runtime = true
    }
    dynamic "cors" {
      for_each = length(local.cors_origins) > 0 ? [1] : []
      content {
        allowed_origins     = local.cors_origins
        support_credentials = true
      }
    }
  }

  # nonsensitive() is required to work around an azurerm provider bug where
  # sensitive values in the map cause "inconsistent values for sensitive
  # attribute" errors during plan expansion (the provider treats app_settings
  # as sensitive internally, so double-sensitivity triggers the bug).
  app_settings = nonsensitive(local.function_app_settings)

  tags = azurerm_resource_group.this.tags
}
