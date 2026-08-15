# ── Key Vault ─────────────────────────────────────────────────────────────────
#
# Stores arbitrary secrets managed through the backend's GetSecret/SetSecret
# admin functions. Access is granted via Azure RBAC only — the backend Function's
# managed identity is the sole principal allowed to read and write secrets
# (people with portal access can be granted roles separately in Azure).

resource "azurerm_key_vault" "this" {
  name                      = "${local.product_prefix}-${var.fkhDeploymentName}-keyvault"
  resource_group_name       = azurerm_resource_group.this.name
  location                  = azurerm_resource_group.this.location
  tenant_id                 = var.tenant_id
  sku_name                   = lower(var.keyvault_sku)
  rbac_authorization_enabled = true

  tags = azurerm_resource_group.this.tags
}

# Grant the Function's identity read + write access to secrets in the Key Vault.
resource "azurerm_role_assignment" "function_keyvault_secrets" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_user_assigned_identity.function.principal_id
}
