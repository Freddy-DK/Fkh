terraform {
  required_version = ">= 1.7.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 2.47"
    }
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "~> 2.30"
    }
    helm = {
      source  = "hashicorp/helm"
      version = "~> 2.14"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  backend "azurerm" {}
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {}
}

provider "azuread" {
  tenant_id = var.tenant_id
}

locals {
  product_prefix              = "fkh"
  storage_account_org_id = substr(replace(var.fkhDeploymentName, "-", ""), 0, 14)

  resource_group_name    = "${local.product_prefix}-${var.fkhDeploymentName}"
  aks_cluster_name       = "${local.product_prefix}-${var.fkhDeploymentName}-aks"
  aks_dns_prefix         = local.aks_cluster_name
  function_plan_name     = "${local.product_prefix}-${var.fkhDeploymentName}-plan"
  function_app_name      = "${local.product_prefix}-${var.fkhDeploymentName}-backend"
  function_identity_name = "${local.product_prefix}-${var.fkhDeploymentName}-identity"
  function_storage_name  = "${local.product_prefix}${local.storage_account_org_id}func"
  dbs_storage_name       = "${local.product_prefix}${local.storage_account_org_id}dbs"

  # CORS origins for the Function App — includes the Static Web App URL when enabled, plus localhost for development
  cors_origins = concat(
    var.enable_web_app ? ["https://${azurerm_static_web_app.web[0].default_host_name}"] : [],
    ["http://localhost:5173"]
  )
}

# ============================================================================
# Resource Group
# ============================================================================

resource "azurerm_resource_group" "this" {
  name     = local.resource_group_name
  location = var.location

  tags = {
    deployment = var.fkhDeploymentName
    environment = "prod"
    managed_by  = "terraform"
  }
}

# ============================================================================
# AKS Cluster with Linux system node pool
# ============================================================================

resource "azurerm_kubernetes_cluster" "this" {
  name                = local.aks_cluster_name
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  dns_prefix          = local.aks_dns_prefix
  sku_tier            = var.aks_sku_tier
  cost_analysis_enabled = var.aks_sku_tier != "Free"
  oidc_issuer_enabled = true

  default_node_pool {
    name                         = "linuxpool"
    node_count                   = 1
    vm_size                      = var.linux_vm_size
    os_sku                       = "Ubuntu"
    temporary_name_for_rotation  = "linuxtmp"

    upgrade_settings {
      max_surge                = "10%"
      drain_timeout_in_minutes = 30
    }
  }

  network_profile {
    network_plugin      = "azure"
    network_plugin_mode = "overlay"
  }

  identity {
    type = "SystemAssigned"
  }

  oms_agent {
    log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id
  }

  tags = azurerm_resource_group.this.tags
}

# ============================================================================
# Windows node pool with autoscaler
# ============================================================================

resource "azurerm_kubernetes_cluster_node_pool" "win" {
  name                         = "win"
  kubernetes_cluster_id        = azurerm_kubernetes_cluster.this.id
  vm_size                      = var.windows_vm_size
  os_type                      = "Windows"
  os_sku                       = "Windows2022"
  temporary_name_for_rotation  = "wintmp"
  min_count                    = var.windows_min_node_count
  max_count                    = var.windows_max_node_count
  auto_scaling_enabled         = true

  upgrade_settings {
    max_surge                = "10%"
    drain_timeout_in_minutes = 30
  }

  lifecycle {
    ignore_changes = [node_count]
  }
}

# ============================================================================
# Windows Spot node pool with autoscaler (cheaper, can be evicted)
# ============================================================================

resource "azurerm_kubernetes_cluster_node_pool" "winspot" {
  count                        = var.windows_spot_enabled ? 1 : 0
  name                         = "spot"
  kubernetes_cluster_id        = azurerm_kubernetes_cluster.this.id
  vm_size                      = var.windows_spot_vm_size
  os_type                      = "Windows"
  os_sku                       = "Windows2022"
  temporary_name_for_rotation  = "spottmp"
  min_count             = var.windows_spot_min_node_count
  max_count             = var.windows_spot_max_node_count
  auto_scaling_enabled  = true
  priority              = "Spot"
  eviction_policy       = "Delete"
  spot_max_price        = -1  # pay up to on-demand price

  upgrade_settings {
    drain_timeout_in_minutes = 30
  }

  node_labels = {
    "kubernetes.azure.com/scalesetpriority" = "spot"
  }

  node_taints = [
    "kubernetes.azure.com/scalesetpriority=spot:NoSchedule"
  ]

  lifecycle {
    ignore_changes = [node_count]
  }
}

# ============================================================================
# AKS Cluster configuration complete
# ============================================================================

