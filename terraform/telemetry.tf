# ── Anonymous usage telemetry (central fkh-usage service) ────────────────────
# Events contain no names, emails, URLs or IPs. Users and containers are counted via
# HMAC hashes keyed with telemetry_salt, which never leaves this deployment.

locals {
  fkh_usage_endpoint = "https://fkh-usage-api.azurewebsites.net/api"

  fkh_telemetry_config = jsonencode({
    aksSkuTier             = var.aks_sku_tier
    kubernetesVersion      = var.kubernetes_version
    maintenanceWindow      = var.aks_maintenance_window
    acrSku                 = var.acr_sku
    keyVaultSku            = var.keyvault_sku
    linuxVmSize            = var.linux_vm_size
    windowsVmSize          = var.windows_vm_size
    windowsMinNodeCount    = var.windows_min_node_count
    windowsMaxNodeCount    = var.windows_max_node_count
    windowsOverprovision   = var.windows_overprovision
    windowsSpotEnabled     = var.windows_spot_enabled
    windowsSpotVmSize      = var.windows_spot_vm_size
    windowsSpotMinNodes    = var.windows_spot_min_node_count
    windowsSpotMaxNodes    = var.windows_spot_max_node_count
    prepullImageCount      = length(var.windows_prepull_images)
    containerDefaultCpu    = var.container_default_cpu
    containerDefaultMemory = var.container_default_memory
    sqlStorageSize         = var.sql_storage_size
    sqlMemoryLimitMb       = var.sql_memory_limit_mb
    kubecostEnabled        = var.kubecost_enabled
    stagingBackend         = var.enable_staging_backend
    webApp                 = var.enable_web_app
    aadContainerAuth       = var.enable_aad_container_auth
    aadAuthIsMultitenant   = var.aad_auth_is_multitenant
    functionTimeoutMinutes = var.function_timeout_minutes
    oidcRepoCount          = length(var.allowed_oidc_repos)
    adoConnectionCount     = length(var.allowed_ado_connections)
  })

  telemetry_app_settings = {
    FKH_USAGE_ENDPOINT          = local.fkh_usage_endpoint
    FKH_TELEMETRY_DEPLOYMENT_ID = random_uuid.telemetry_deployment_id.result
    FKH_TELEMETRY_SALT          = random_password.telemetry_salt.result
    FKH_TELEMETRY_CONFIG        = local.fkh_telemetry_config
  }
}

resource "random_uuid" "telemetry_deployment_id" {}

resource "random_password" "telemetry_salt" {
  length  = 48
  special = false
}

output "usage_endpoint" {
  description = "Central fkh-usage endpoint receiving anonymous usage events and optional registrations."
  value       = local.fkh_usage_endpoint
}

output "registration_payload" {
  description = "JSON registration payload sent to fkh-usage by the deploy workflow."
  sensitive   = true
  value = jsonencode({
    deploymentId   = random_uuid.telemetry_deployment_id.result
    deploymentName = var.fkhDeploymentName
    location       = var.location
    backendUrl     = "https://${azurerm_windows_function_app.this.default_hostname}/api"
    webAppUrl      = var.enable_web_app ? "https://${azurerm_static_web_app.web[0].default_host_name}" : null
    registration   = var.registration
  })
}
