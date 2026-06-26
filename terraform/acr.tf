# ── Azure Container Registry ──────────────────────────────────────────────────

resource "azurerm_container_registry" "this" {
  name                = "${local.product_prefix}${local.storage_account_org_id}acr"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  sku                 = var.acr_sku
  admin_enabled       = false

  tags = azurerm_resource_group.this.tags
}

# ── Grant AKS pull access to the container registry ──────────────────────────
# "AcrPull" lets the kubelet pull images without imagePullSecrets.

resource "azurerm_role_assignment" "aks_acr_pull" {
  scope                = azurerm_container_registry.this.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_kubernetes_cluster.this.kubelet_identity[0].object_id
}

