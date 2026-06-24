output "cluster_name" {
  description = "The AKS cluster name."
  value       = azurerm_kubernetes_cluster.this.name
}

output "resource_group_name" {
  description = "The resource group name."
  value       = azurerm_resource_group.this.name
}

output "location" {
  description = "The Azure region."
  value       = azurerm_resource_group.this.location
}

output "namespace" {
  description = "The Kubernetes namespace."
  value       = kubernetes_namespace.workload.metadata[0].name
}

output "sql_service_fqdn" {
  description = "Internal FQDN for the SQL Server service."
  value       = "mssql-service.${var.namespace}.svc.cluster.local:1433"
}

output "function_app_name" {
  description = "Name of the Azure Function App. Used by deploy-functionupdate.ps1 to publish code."
  value       = azurerm_windows_function_app.this.name
}

output "function_url" {
  description = "Base URL of the Azure Function. Paste this into FUNCTION_URL in the VS Code extension."
  value       = "https://${azurerm_windows_function_app.this.default_hostname}/api"
}

output "staging_function_app_name" {
  description = "Name of the staging Function App (empty if staging is disabled)."
  value       = var.enable_staging_backend ? azurerm_windows_function_app.staging[0].name : ""
}

output "staging_function_url" {
  description = "Base URL of the staging Function App (empty if staging is disabled)."
  value       = var.enable_staging_backend ? "https://${azurerm_windows_function_app.staging[0].default_hostname}/api" : ""
}

output "acr_login_server" {
  description = "ACR login server for docker login."
  value       = azurerm_container_registry.this.login_server
}

output "identity_client_id" {
  description = "Client ID of the GitHub Actions managed identity (used for OIDC federation)."
  value       = azurerm_user_assigned_identity.github_actions.client_id
}

output "tenant_id" {
  description = "Azure AD tenant ID (used for OIDC federation)."
  value       = var.tenant_id
}

output "subscription_id" {
  description = "Azure subscription ID (used for OIDC federation)."
  value       = var.subscription_id
}

output "managed_identity_client_id" {
  description = "Client ID of the Function's Managed Identity."
  value       = azurerm_user_assigned_identity.function.client_id
}

output "kube_config_command" {
  description = "Command to fetch kubeconfig."
  value       = "az aks get-credentials --resource-group ${azurerm_resource_group.this.name} --name ${azurerm_kubernetes_cluster.this.name} --overwrite-existing"
}

output "acr_name" {
  description = "Name of the Azure Container Registry."
  value       = azurerm_container_registry.this.name
}

output "dbs_storage_account_name" {
  description = "Name of the storage account for database backups."
  value       = azurerm_storage_account.dbs.name
}

output "web_app_url" {
  description = "URL of the Fkh web app (empty if web app is disabled)."
  value       = var.enable_web_app ? "https://${azurerm_static_web_app.web[0].default_host_name}" : ""
}

output "web_app_deployment_token" {
  description = "Deployment token for the Static Web App (used in CI/CD to deploy the built files). Empty if web app is disabled."
  value       = var.enable_web_app ? azurerm_static_web_app.web[0].api_key : ""
  sensitive   = true
}

output "ado_identity_client_id" {
  description = "Client ID of the Azure DevOps managed identity. Use this when creating Workload Identity Federation service connections in Azure DevOps."
  value       = length(var.allowed_ado_connections) > 0 ? azurerm_user_assigned_identity.ado[0].client_id : ""
}
