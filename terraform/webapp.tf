# ============================================================================
# Static Web App — Fkh Web UI
# ============================================================================

resource "azurerm_static_web_app" "web" {
  count               = var.enable_web_app ? 1 : 0
  name                = "${local.product_prefix}-${var.fkhDeploymentName}-web"
  resource_group_name = azurerm_resource_group.this.name
  location            = var.static_web_app_location

  sku_tier = "Free"
  sku_size = "Free"

  tags = azurerm_resource_group.this.tags
}
