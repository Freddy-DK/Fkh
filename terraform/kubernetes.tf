# ============================================================================
# Configure Kubernetes provider using AKS credentials
# ============================================================================

provider "kubernetes" {
  host                   = azurerm_kubernetes_cluster.this.kube_config[0].host
  client_certificate     = base64decode(azurerm_kubernetes_cluster.this.kube_config[0].client_certificate)
  client_key             = base64decode(azurerm_kubernetes_cluster.this.kube_config[0].client_key)
  cluster_ca_certificate = base64decode(azurerm_kubernetes_cluster.this.kube_config[0].cluster_ca_certificate)
}

provider "helm" {
  kubernetes {
    host                   = azurerm_kubernetes_cluster.this.kube_config[0].host
    client_certificate     = base64decode(azurerm_kubernetes_cluster.this.kube_config[0].client_certificate)
    client_key             = base64decode(azurerm_kubernetes_cluster.this.kube_config[0].client_key)
    cluster_ca_certificate = base64decode(azurerm_kubernetes_cluster.this.kube_config[0].cluster_ca_certificate)
  }
}

# ============================================================================
# Namespace
# ============================================================================

resource "kubernetes_namespace" "workload" {
  metadata {
    name = var.namespace
  }

  depends_on = [azurerm_kubernetes_cluster.this]
}

# ============================================================================
# Kubernetes Secret for SQL SA password
# ============================================================================

resource "kubernetes_secret" "mssql" {
  metadata {
    name      = "mssql-secret"
    namespace = kubernetes_namespace.workload.metadata[0].name
  }

  data = {
    "sa-password" = var.sql_sa_password
  }

  type = "Opaque"
}

# ============================================================================
# Random encryption password (generated once, lives for the cluster lifetime)
# ============================================================================

resource "random_password" "encryption" {
  length  = 32
  special = true
}

resource "kubernetes_secret" "encryption" {
  metadata {
    name      = "encryption-secret"
    namespace = kubernetes_namespace.workload.metadata[0].name
  }

  data = {
    "encryptionPassword" = random_password.encryption.result
  }

  type = "Opaque"
}

# ============================================================================
# PersistentVolumeClaim for SQL Server data (Premium SSD, 128Gi)
# ============================================================================

resource "kubernetes_persistent_volume_claim" "mssql_data" {
  metadata {
    name      = "mssql-data-pvc"
    namespace = kubernetes_namespace.workload.metadata[0].name
  }

  wait_until_bound = false

  timeouts {
    create = "5m"
  }

  spec {
    access_modes       = ["ReadWriteOnce"]
    storage_class_name = "managed-csi-premium"

    resources {
      requests = {
        storage = var.sql_storage_size
      }
    }
  }

  provisioner "local-exec" {
    command = <<-EOT
      echo "=== Checking PVC Status ==="
      kubectl get pvc mssql-data-pvc -n ${kubernetes_namespace.workload.metadata[0].name} -o wide
      echo ""
      echo "=== PVC Detailed Description ==="
      kubectl describe pvc mssql-data-pvc -n ${kubernetes_namespace.workload.metadata[0].name}
      echo ""
      echo "=== Recent Kubernetes Events ==="
      kubectl get events -n ${kubernetes_namespace.workload.metadata[0].name} --sort-by='.lastTimestamp' | tail -20
      echo ""
      echo "=== Available Storage Classes ==="
      kubectl get storageclass
    EOT
    on_failure = continue
  }
}

# ============================================================================
# SQL Server Deployment on Linux
# ============================================================================

resource "kubernetes_deployment" "mssql" {
  metadata {
    name      = "mssql-deployment"
    namespace = kubernetes_namespace.workload.metadata[0].name
  }

  wait_for_rollout = false

  timeouts {
    create = "15m"
    update = "15m"
  }

  spec {
    replicas = 1

    strategy {
      type = "Recreate"
    }

    selector {
      match_labels = {
        app = "mssql"
      }
    }

    template {
      metadata {
        labels = {
          app = "mssql"
        }
      }

      spec {
        node_selector = {
          "kubernetes.io/os" = "linux"
        }

        security_context {
          fs_group = 10001
        }

        # Init container to fix permissions on mounted volumes
        init_container {
          name  = "fix-permissions"
          image = "${azurerm_container_registry.this.login_server}/mssql-server-fts:2022-latest"

          command = ["/bin/bash", "-c", "chown -R 10001:0 /var/opt/mssql/data /var/opt/mssql/log"]

          security_context {
            run_as_user = 0
          }

          volume_mount {
            name       = "mssql-data"
            mount_path = "/var/opt/mssql/data"
            sub_path   = "data"
          }

          volume_mount {
            name       = "mssql-data"
            mount_path = "/var/opt/mssql/log"
            sub_path   = "log"
          }
        }

        container {
          name  = "mssql"
          image = "${azurerm_container_registry.this.login_server}/mssql-server-fts:2022-latest"

          port {
            container_port = 1433
            protocol       = "TCP"
          }

          env {
            name  = "ACCEPT_EULA"
            value = "Y"
          }

          env {
            name = "MSSQL_SA_PASSWORD"
            value_from {
              secret_key_ref {
                name = kubernetes_secret.mssql.metadata[0].name
                key  = "sa-password"
              }
            }
          }

          env {
            name  = "MSSQL_DATA_DIR"
            value = "/var/opt/mssql/data"
          }

          env {
            name  = "MSSQL_LOG_DIR"
            value = "/var/opt/mssql/log"
          }

          env {
            name  = "MSSQL_MEMORY_LIMIT_MB"
            value = tostring(var.sql_memory_limit_mb)
          }

          volume_mount {
            name       = "mssql-data"
            mount_path = "/var/opt/mssql/data"
            sub_path   = "data"
          }

          volume_mount {
            name       = "mssql-data"
            mount_path = "/var/opt/mssql/log"
            sub_path   = "log"
          }

          resources {
            requests = {
              memory = "2Gi"
              cpu    = "500m"
            }
          }

          readiness_probe {
            tcp_socket {
              port = 1433
            }
            initial_delay_seconds = 15
            period_seconds        = 10
          }

          liveness_probe {
            tcp_socket {
              port = 1433
            }
            initial_delay_seconds = 30
            period_seconds        = 20
          }
        }

        volume {
          name = "mssql-data"
          persistent_volume_claim {
            claim_name = kubernetes_persistent_volume_claim.mssql_data.metadata[0].name
          }
        }
      }
    }
  }
}

# ============================================================================
# ClusterIP Service for SQL Server (internal only)
# ============================================================================

resource "kubernetes_service" "mssql" {
  metadata {
    name      = "mssql-service"
    namespace = kubernetes_namespace.workload.metadata[0].name
  }

  spec {
    type = "ClusterIP"

    selector = {
      app = "mssql"
    }

    port {
      protocol    = "TCP"
      port        = 1433
      target_port = 1433
    }
  }
}

# ============================================================================
# Network Policy: restrict SQL Server access to Windows app containers only
# ============================================================================

resource "kubernetes_network_policy" "mssql_allow_windows_app_only" {
  metadata {
    name      = "mssql-allow-windows-app-only"
    namespace = kubernetes_namespace.workload.metadata[0].name
  }

  spec {
    pod_selector {
      match_labels = {
        app = "mssql"
      }
    }

    policy_types = ["Ingress"]

    ingress {
      from {
        pod_selector {
          match_labels = {
            "app-type" = "windows-servicetier"
          }
        }
      }

      ports {
        protocol = "TCP"
        port     = "1433"
      }
    }
  }
}

# ============================================================================
# Image Pre-Pull DaemonSet (optional, runs on Windows nodes)
# ============================================================================
# Deploys a pause container per image on every Windows node so the image layers
# are cached locally. This avoids the ~14-minute pull when creating a container.

resource "kubernetes_daemonset" "image_prepull" {
  count = length(var.windows_prepull_images) > 0 ? 1 : 0

  metadata {
    name      = "fkh-image-prepull"
    namespace = kubernetes_namespace.workload.metadata[0].name
    labels = {
      "fkh/purpose" = "image-prepull"
    }
  }

  spec {
    selector {
      match_labels = {
        "fkh/purpose" = "image-prepull"
      }
    }

    template {
      metadata {
        labels = {
          "fkh/purpose" = "image-prepull"
        }
      }

      spec {
        node_selector = {
          "kubernetes.io/os" = "windows"
        }

        # Tolerate spot taint so pre-pull also runs on spot nodes
        toleration {
          key      = "kubernetes.azure.com/scalesetpriority"
          operator = "Equal"
          value    = "spot"
          effect   = "NoSchedule"
        }

        # Low priority so these don't block real workloads
        priority_class_name = "system-node-critical"

        dynamic "init_container" {
          for_each = var.windows_prepull_images
          content {
            name    = "prepull-${init_container.key}"
            image   = init_container.value
            command = ["cmd", "/c", "echo Image pulled successfully"]

            resources {
              requests = {
                cpu    = "500m"
                memory = "1Gi"
              }
              limits = {
                cpu    = "500m"
                memory = "1Gi"
              }
            }
          }
        }

        container {
          name    = "pause"
          image   = "mcr.microsoft.com/oss/kubernetes/pause:3.9"
          command = ["cmd", "/c", "ping -n 2147483647 127.0.0.1 > nul"]

          resources {
            requests = {
              cpu    = "10m"
              memory = "32Mi"
            }
            limits = {
              cpu    = "10m"
              memory = "32Mi"
            }
          }
        }
      }
    }
  }
}

# ============================================================================
# Overprovisioning: keep spare capacity for instant BC container scheduling
# ============================================================================
# A low-priority placeholder pod reserves 500m CPU + 3Gi on a Windows VM.
# When a real BC container is created, it preempts (evicts) the placeholder and
# starts immediately. The displaced placeholder triggers the autoscaler
# to provision a new VM in the background, restoring spare capacity.

resource "kubernetes_priority_class" "overprovision" {
  count = var.windows_overprovision ? 1 : 0

  metadata {
    name = "fkh-overprovision"
  }

  value          = -1
  global_default = false
  description    = "Low-priority class for overprovisioning placeholder pods. Preempted by any normal workload."
}

resource "kubernetes_deployment" "overprovision" {
  count = var.windows_overprovision ? 1 : 0

  metadata {
    name      = "fkh-overprovision"
    namespace = kubernetes_namespace.workload.metadata[0].name
    labels = {
      "fkh/purpose" = "overprovision"
    }
  }

  spec {
    replicas = 1

    selector {
      match_labels = {
        "fkh/purpose" = "overprovision"
      }
    }

    template {
      metadata {
        labels = {
          "fkh/purpose" = "overprovision"
        }
      }

      spec {
        priority_class_name = kubernetes_priority_class.overprovision[0].metadata[0].name

        node_selector = {
          "kubernetes.io/os" = "windows"
        }

        container {
          name    = "pause"
          image   = "mcr.microsoft.com/oss/kubernetes/pause:3.9"
          command = ["cmd", "/c", "ping -n 2147483647 127.0.0.1 > nul"]

          resources {
            requests = {
              cpu    = var.windows_overprovision_cpu
              memory = var.windows_overprovision_memory
            }
            limits = {
              cpu    = var.windows_overprovision_cpu
              memory = var.windows_overprovision_memory
            }
          }
        }
      }
    }
  }
}
