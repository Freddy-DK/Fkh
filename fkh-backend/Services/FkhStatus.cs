using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.ContainerService;
using Azure.Storage.Blobs;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Fkh.Services;

public class FkhStatus : FkhServiceBase
{
    public FkhStatus(ILogger<FkhStatus> logger) : base(logger) { }

    public async Task<object> GetStatusAsync(Dictionary<string, string> parameters)
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618

        // Check cluster power state first — if not running, return minimal status
        var powerState = await GetClusterPowerStateAsync(credential);

        // Collect version/deployment info (same env vars as GetVersion)
        string? clusterVersion = null, clusterDeployedAt = null;
        try
        {
            var k8s = await GetKubernetesClientAsync();
            var configMap = await k8s.CoreV1.ReadNamespacedConfigMapAsync("fkh-version", Namespace);
            if (configMap?.Data is not null)
            {
                configMap.Data.TryGetValue("version", out clusterVersion);
                configMap.Data.TryGetValue("deployed-at", out clusterDeployedAt);
            }
        }
        catch { }

        var versionInfo = new
        {
            BackendVersion = Environment.GetEnvironmentVariable("FKH_VERSION"),
            BackendDeployedAt = Environment.GetEnvironmentVariable("DEPLOYED_AT"),
            ClusterVersion = clusterVersion,
            ClusterDeployedAt = clusterDeployedAt,
            DeploymentRepo = Environment.GetEnvironmentVariable("DEPLOYMENT_REPO"),
            FkhFork = Environment.GetEnvironmentVariable("FKH_FORK"),
        };

        if (!string.Equals(powerState, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                Timestamp = DateTimeOffset.UtcNow,
                ClusterPowerState = powerState ?? "Unknown",
                BackendUrl = $"https://{Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")}/api",
                Version = versionInfo,
                Message = string.Equals(powerState, "Stopped", StringComparison.OrdinalIgnoreCase)
                    ? "The cluster is stopped. Use 'fkh startfkh' to start it."
                    : $"The cluster is currently {powerState?.ToLowerInvariant() ?? "unknown"}. Please wait for it to be fully running.",
            };
        }

        // Run independent data collection tasks in parallel
        var kubeTask = GetKubernetesStatusAsync();
        var storageTask = GetStorageStatusAsync(credential);
        var quotaTask = GetSubscriptionQuotaAsync(credential);
        var securityTask = Task.FromResult(GetSecurityStatus());

        await Task.WhenAll(kubeTask, storageTask, quotaTask, securityTask);

        return new
        {
            Timestamp = DateTimeOffset.UtcNow,
            ClusterPowerState = powerState,
            BackendUrl = $"https://{Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")}/api",
            Version = versionInfo,
            Kubernetes = await kubeTask,
            Storage = await storageTask,
            Quota = await quotaTask,
            Security = await securityTask,
        };
    }

    private async Task<string?> GetClusterPowerStateAsync(Azure.Core.TokenCredential credential)
    {
        try
        {
            var armClient = new ArmClient(credential);
            var aksId = ContainerServiceManagedClusterResource
                .CreateResourceIdentifier(SubscriptionId, ResourceGroup, ClusterName);
            var cluster = armClient.GetContainerServiceManagedClusterResource(aksId);
            var data = (await cluster.GetAsync()).Value.Data;
            return data.PowerStateCode?.ToString();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check AKS cluster power state");
            return null;
        }
    }

    private async Task<object> GetKubernetesStatusAsync()
    {
        var client = await GetKubernetesClientAsync();

        // Collect node info, pod info, and metrics concurrently
        var nodesTask = client.ListNodeAsync();
        var appPodsTask = client.ListNamespacedPodAsync(Namespace);
        var allPodsTask = client.ListPodForAllNamespacesAsync();
        var allDeploymentsTask = client.ListNamespacedDeploymentAsync(Namespace);
        var nodeMetricsTask = GetCurrentNodeMetricsAsync(client);
        var historicalTask = GetHistoricalNodeMetricsAsync();

        await Task.WhenAll(nodesTask, appPodsTask, allPodsTask, allDeploymentsTask, nodeMetricsTask, historicalTask);

        var nodes = await nodesTask;
        var appPods = await appPodsTask;
        var allPods = await allPodsTask;
        var allDeployments = await allDeploymentsTask;
        var currentMetrics = await nodeMetricsTask;
        var historicalMetrics = await historicalTask;

        // Fetch real disk usage from kubelet stats/summary (works on both Linux and Windows)
        var nodeDiskStats = await GetNodeDiskStatsAsync(client, nodes.Items.Select(n => n.Metadata.Name));

        // ── Linux pods (mssql, system services) ─────────────────────────────────
        var linuxNodes = nodes.Items
            .Where(n => n.Metadata.Labels.TryGetValue("kubernetes.io/os", out var os) && os == "linux")
            .ToList();

        var linuxStatus = new List<object>();
        foreach (var node in linuxNodes)
        {
            var name = node.Metadata.Name;
            var ready = node.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True");
            var cpuCapCores = node.Status.Capacity.TryGetValue("cpu", out var cpu) ? cpu.ToDouble() : 0;
            var memCapBytes = node.Status.Capacity.TryGetValue("memory", out var mem) ? mem.ToDouble() : 0;
            var diskCapBytes = node.Status.Capacity.TryGetValue("ephemeral-storage", out var disk) ? ParseStorageBytes(disk.ToString()) : 0;
            var diskAllocBytes = node.Status.Allocatable.TryGetValue("ephemeral-storage", out var diskA) ? ParseStorageBytes(diskA.ToString()) : 0;

            var podsOnNode = appPods.Items.Where(p => p.Spec.NodeName == name)
                .Select(p => p.Metadata.Labels.TryGetValue("app", out var app) ? app : p.Metadata.Name)
                .OrderBy(p => p).ToList();

            var nodeStatus = BuildNodeMetricsOutput(name, cpuCapCores, memCapBytes, diskCapBytes, diskAllocBytes, currentMetrics, historicalMetrics, nodeDiskStats);
            nodeStatus["Name"] = name;
            nodeStatus["Status"] = ready ? "Ready" : "NotReady";
            nodeStatus["Pods"] = podsOnNode;

            linuxStatus.Add(nodeStatus);
        }

        // ── MSSQL pod status ────────────────────────────────────────────────────
        object? mssqlStatus = null;
        try
        {
            var mssqlPods = await client.ListNamespacedPodAsync(Namespace, labelSelector: "app=mssql");
            var mssqlPod = mssqlPods.Items.FirstOrDefault(p => p.Status?.Phase == "Running");
            if (mssqlPod != null)
            {
                // Get SQL data drive usage via exec
                string? sqlDiskUsage = null;
                try
                {
                    var result = await ExecInMssqlPodAsync(client, mssqlPod.Metadata.Name,
                        "df -h /var/opt/mssql 2>/dev/null | tail -1 | awk '{print \"used: \" $3 \" / \" $2 \" (\" $5 \" full)\"}'");
                    sqlDiskUsage = result.Stdout.Trim();
                    if (string.IsNullOrWhiteSpace(sqlDiskUsage)) sqlDiskUsage = null;
                }
                catch { /* exec may fail */ }

                // Get pod memory metrics
                string? memoryUsage = null;
                try
                {
                    var metrics = await client.GetNamespacedCustomObjectAsync<PodMetricsResult>(
                        "metrics.k8s.io", "v1beta1", Namespace, "pods", mssqlPod.Metadata.Name);
                    var container = metrics?.Containers?.FirstOrDefault();
                    if (container?.Usage != null)
                    {
                        var memVal = container.Usage.TryGetValue("memory", out var m) ? FormatBytes(m.ToDouble()) : null;
                        var cpuVal = container.Usage.TryGetValue("cpu", out var c) ? c.ToString() : null;
                        memoryUsage = memVal != null ? $"{memVal} memory, {cpuVal} cpu" : null;
                    }
                }
                catch { /* metrics may not be available */ }

                // Get database list with sizes and state
                List<object>? databases = null;
                try
                {
                    var dbQuery = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -h -1 -W -s \"|\" " +
                        "-Q \"SELECT d.name, d.state_desc, d.recovery_model_desc, " +
                        "CAST(SUM(CASE WHEN mf.type = 0 THEN mf.size END) * 8.0 / 1024 AS DECIMAL(10,1)) AS DataSizeMB, " +
                        "CAST(SUM(CASE WHEN mf.type = 1 THEN mf.size END) * 8.0 / 1024 AS DECIMAL(10,1)) AS LogSizeMB, " +
                        "d.compatibility_level " +
                        "FROM sys.databases d " +
                        "JOIN sys.master_files mf ON d.database_id = mf.database_id " +
                        "WHERE d.name NOT IN ('master','tempdb','model','msdb') " +
                        "GROUP BY d.name, d.state_desc, d.recovery_model_desc, d.compatibility_level " +
                        "ORDER BY d.name\"";
                    var dbResult = await ExecInMssqlPodAsync(client, mssqlPod.Metadata.Name, dbQuery);
                    var dbLines = dbResult.Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(l => l.Contains('|'))
                        .ToList();

                    databases = new List<object>();
                    foreach (var line in dbLines)
                    {
                        var parts = line.Split('|', StringSplitOptions.TrimEntries);
                        if (parts.Length >= 5 && !parts[0].StartsWith("---") && !parts[0].StartsWith("("))
                        {
                            databases.Add(new
                            {
                                Name = parts[0],
                                State = parts[1],
                                RecoveryModel = parts[2],
                                DataSizeMB = parts[3],
                                LogSizeMB = parts[4],
                                CompatibilityLevel = parts.Length >= 6 ? parts[5] : null,
                            });
                        }
                    }

                    // keep empty list so "databases: []" always appears
                }
                catch { /* sqlcmd may fail */ }

                mssqlStatus = new
                {
                    Pod = mssqlPod.Metadata.Name,
                    Node = mssqlPod.Spec.NodeName,
                    Phase = mssqlPod.Status.Phase,
                    SqlDataDrive = sqlDiskUsage,
                    ResourceUsage = memoryUsage,
                    DatabaseCount = databases?.Count,
                    Databases = databases,
                };
            }
        }
        catch { /* mssql pod may not exist */ }

        // ── Windows VMs and BC containers ───────────────────────────────────────
        var windowsNodes = nodes.Items
            .Where(n => n.Metadata.Labels.TryGetValue("kubernetes.io/os", out var os) && os == "windows")
            .OrderBy(n => n.Metadata.Name)
            .ToList();

        // CNS status
        var kubeSystemPods = await client.ListNamespacedPodAsync("kube-system");
        var cnsPods = kubeSystemPods.Items
            .Where(p => p.Metadata.Name.StartsWith("azure-cns", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Image pre-pull pods
        var prepullPods = appPods.Items
            .Where(p => p.Metadata.Labels.TryGetValue("fkh/purpose", out var purpose) && purpose == "image-prepull")
            .ToList();

        // BC deployments (windows-servicetier)
        var bcDeployments = allDeployments.Items
            .Where(d => d.Spec.Template.Metadata.Labels != null
                && d.Spec.Template.Metadata.Labels.TryGetValue("app-type", out var appType)
                && appType == "windows-servicetier")
            .ToList();

        // Pod metrics for BC pods
        var podMetrics = new Dictionary<string, PodMetricsResult>();
        try
        {
            var bcPods = await client.ListNamespacedPodAsync(Namespace, labelSelector: "app-type=windows-servicetier");
            foreach (var pod in bcPods.Items)
            {
                try
                {
                    var m = await client.GetNamespacedCustomObjectAsync<PodMetricsResult>(
                        "metrics.k8s.io", "v1beta1", Namespace, "pods", pod.Metadata.Name);
                    if (m != null && pod.Metadata.Labels.TryGetValue("app", out var appLabel))
                        podMetrics[appLabel] = m;
                }
                catch { }
            }
        }
        catch { }

        var vmResults = new List<object>();
        foreach (var node in windowsNodes)
        {
            var name = node.Metadata.Name;
            var ready = node.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True");
            var cpuCapCores = node.Status.Capacity.TryGetValue("cpu", out var cpu) ? cpu.ToDouble() : 0;
            var memCapBytes = node.Status.Capacity.TryGetValue("memory", out var mem) ? mem.ToDouble() : 0;
            var diskCapBytes = node.Status.Capacity.TryGetValue("ephemeral-storage", out var disk) ? ParseStorageBytes(disk.ToString()) : 0;
            var diskAllocBytes = node.Status.Allocatable.TryGetValue("ephemeral-storage", out var diskA) ? ParseStorageBytes(diskA.ToString()) : 0;

            var cnsReady = cnsPods.Any(p =>
                p.Spec.NodeName == name
                && p.Status.Phase == "Running"
                && p.Status.ContainerStatuses?.All(c => c.Ready) == true);

            // All pods on this node (for summing requests)
            var podsOnNode = appPods.Items.Where(p => p.Spec.NodeName == name).ToList();

            // BC containers on this node
            var containersOnNode = bcDeployments
                .Where(d =>
                {
                    var appLabel = d.Spec.Template.Metadata.Labels.TryGetValue("app", out var app) ? app : "";
                    return podsOnNode.Any(p => p.Metadata.Labels.TryGetValue("app", out var podApp) && podApp == appLabel);
                })
                .Select(d =>
                {
                    var appLabel = d.Spec.Template.Metadata.Labels["app"];
                    var replicas = d.Spec.Replicas ?? 0;
                    var readyReplicas = d.Status?.ReadyReplicas ?? 0;
                    var status = replicas == 0 ? "Stopped" : readyReplicas >= replicas ? "Running" : "Starting";

                    string? memStr = null;
                    if (podMetrics.TryGetValue(appLabel, out var pm))
                    {
                        var c = pm.Containers?.FirstOrDefault();
                        if (c?.Usage?.TryGetValue("memory", out var mv) == true)
                            memStr = FormatBytes(mv.ToDouble());
                    }

                    // Requested resources from pod spec
                    var pod = podsOnNode.FirstOrDefault(p => p.Metadata.Labels.TryGetValue("app", out var podApp) && podApp == appLabel);
                    var reqCpu = pod?.Spec.Containers?.FirstOrDefault()?.Resources?.Requests?.TryGetValue("cpu", out var rc) == true ? rc.ToString() : null;
                    var reqMem = pod?.Spec.Containers?.FirstOrDefault()?.Resources?.Requests?.TryGetValue("memory", out var rm) == true ? rm.ToString() : null;

                    return new { Name = appLabel, Status = status, Memory = memStr, CpuRequest = reqCpu, MemoryRequest = reqMem };
                })
                .OrderBy(c => c.Name)
                .ToList();

            // Sum all pod resource requests on this node (all namespaces, including system pods)
            double totalReqCpuCores = 0;
            double totalReqMemBytes = 0;
            foreach (var pod in allPods.Items.Where(p => p.Spec.NodeName == name))
            {
                foreach (var container in pod.Spec.Containers ?? Enumerable.Empty<V1Container>())
                {
                    if (container.Resources?.Requests?.TryGetValue("cpu", out var podCpu) == true)
                        totalReqCpuCores += podCpu.ToDouble();
                    if (container.Resources?.Requests?.TryGetValue("memory", out var podMem) == true)
                        totalReqMemBytes += podMem.ToDouble();
                }
            }

            var allocCpuCores = node.Status.Allocatable.TryGetValue("cpu", out var allocCpu) ? allocCpu.ToDouble() : cpuCapCores;
            var allocMemBytes = node.Status.Allocatable.TryGetValue("memory", out var allocMem) ? allocMem.ToDouble() : memCapBytes;

            var nodeStatus = BuildNodeMetricsOutput(name, cpuCapCores, memCapBytes, diskCapBytes, diskAllocBytes, currentMetrics, historicalMetrics, nodeDiskStats);
            nodeStatus["Name"] = name;
            nodeStatus["Status"] = ready ? "Ready" : "NotReady";
            nodeStatus["Cns"] = cnsReady ? "Ready" : "NotReady";

            // Image pre-pull status for this node
            var prepullPod = prepullPods.FirstOrDefault(p => p.Spec.NodeName == name);
            if (prepullPod != null)
            {
                var initStatuses = prepullPod.Status?.InitContainerStatuses;
                var totalInit = prepullPod.Spec.InitContainers?.Count ?? 0;
                if (totalInit > 0 && initStatuses != null)
                {
                    var completedInit = initStatuses.Count(s => s.State?.Terminated?.Reason == "Completed");
                    if (completedInit >= totalInit && prepullPod.Status?.Phase == "Running")
                        nodeStatus["ImagePrepull"] = $"Ready ({totalInit}/{totalInit} images cached)";
                    else
                        nodeStatus["ImagePrepull"] = $"Pulling ({completedInit}/{totalInit})";
                }
                else
                {
                    nodeStatus["ImagePrepull"] = prepullPod.Status?.Phase ?? "Unknown";
                }
            }

            nodeStatus["CpuRequested"] = $"{FormatCpu(totalReqCpuCores)}/{FormatCpu(allocCpuCores)} ({Pct(totalReqCpuCores, allocCpuCores)}% allocated)";
            nodeStatus["MemoryRequested"] = $"{FormatBytes(totalReqMemBytes)}/{FormatBytes(allocMemBytes)} ({Pct(totalReqMemBytes, allocMemBytes)}% allocated)";
            nodeStatus["ContainerCount"] = containersOnNode.Count;
            nodeStatus["Containers"] = containersOnNode;

            vmResults.Add(nodeStatus);
        }

        // Summary stats
        var totalBcContainers = bcDeployments.Count;
        var runningBcContainers = bcDeployments.Count(d => (d.Spec.Replicas ?? 0) > 0 && (d.Status?.ReadyReplicas ?? 0) > 0);
        var stoppedBcContainers = bcDeployments.Count(d => (d.Spec.Replicas ?? 0) == 0);

        return new
        {
            LinuxNodes = linuxStatus,
            Mssql = mssqlStatus,
            WindowsVMs = vmResults,
            Summary = new
            {
                WindowsVMCount = windowsNodes.Count,
                WindowsVMReady = windowsNodes.Count(n => n.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True")),
                TotalBcContainers = totalBcContainers,
                RunningBcContainers = runningBcContainers,
                StoppedBcContainers = stoppedBcContainers,
            },
        };
    }

    private async Task<object> GetStorageStatusAsync(Azure.Core.TokenCredential credential)
    {
        var results = new List<object>();

        try
        {
            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);

            var containers = new List<object>();
            await foreach (var container in blobServiceClient.GetBlobContainersAsync())
            {
                long totalSize = 0;
                int blobCount = 0;
                var blobContainerClient = blobServiceClient.GetBlobContainerClient(container.Name);
                await foreach (var blob in blobContainerClient.GetBlobsAsync())
                {
                    totalSize += blob.Properties.ContentLength ?? 0;
                    blobCount++;
                }

                containers.Add(new
                {
                    Name = container.Name,
                    BlobCount = blobCount,
                    TotalSize = FormatBytes(totalSize),
                });
            }

            results.Add(new
            {
                AccountName = DbsStorageAccountName,
                Containers = containers,
            });
        }
        catch (Exception ex)
        {
            results.Add(new
            {
                AccountName = DbsStorageAccountName,
                Error = ex.Message,
            });
        }

        return results;
    }

    private async Task<object> GetSubscriptionQuotaAsync(Azure.Core.TokenCredential credential)
    {
        try
        {
            var armClient = new ArmClient(credential);
            var subscription = armClient.GetDefaultSubscription();

            // Run Kubernetes and Azure quota lookups in parallel
            var aksTask = GetAksQuotaAsync(armClient);
            var azureTask = GetAzureComputeQuotaAsync(subscription);

            await Task.WhenAll(aksTask, azureTask);

            return new
            {
                Kubernetes = await aksTask,
                Azure = await azureTask,
            };
        }
        catch (Exception ex)
        {
            return new { Error = ex.Message };
        }
    }

    private async Task<object> GetAksQuotaAsync(ArmClient armClient)
    {
        var aksId = ContainerServiceManagedClusterResource
            .CreateResourceIdentifier(SubscriptionId, ResourceGroup, ClusterName);
        var cluster = armClient.GetContainerServiceManagedClusterResource(aksId);
        var clusterData = (await cluster.GetAsync()).Value.Data;

        var agentPools = clusterData.AgentPoolProfiles?.Select(p => new
        {
            Name = p.Name,
            VmSize = p.VmSize,
            Count = p.Count,
            MinCount = p.MinCount,
            MaxCount = p.MaxCount,
            Mode = p.Mode?.ToString(),
            OsSku = p.OSSku?.ToString(),
            EnableAutoScaling = p.IsAutoScalingEnabled,
        }).ToList();

        return new
        {
            SubscriptionId,
            ResourceGroup,
            ClusterName,
            AgentPools = agentPools,
        };
    }

    private async Task<object> GetAzureComputeQuotaAsync(Azure.ResourceManager.Resources.SubscriptionResource subscription)
    {
        try
        {
            var location = new Azure.Core.AzureLocation(AksLocation);
            var usages = new List<object>();

            await foreach (var usage in subscription.GetUsagesAsync(location))
            {
                if (usage.CurrentValue > 0)
                {
                    usages.Add(new
                    {
                        Name = usage.Name.LocalizedValue,
                        CurrentValue = usage.CurrentValue,
                        Limit = usage.Limit,
                    });
                }
            }

            return new
            {
                Location = AksLocation,
                Usages = usages,
            };
        }
        catch (Exception ex)
        {
            return new { Error = ex.Message };
        }
    }

    private static object GetSecurityStatus()
    {
        // Access the static FailedAttempts from FunctionBase
        var blockedIps = FunctionBase.GetBlockedIps();
        var recentFailures = FunctionBase.GetRecentFailedAttempts();

        return new
        {
            BlockedIpCount = blockedIps.Count,
            BlockedIps = blockedIps,
            RecentFailedAttemptCount = recentFailures.Count,
            RecentFailedAttempts = recentFailures,
        };
    }

    // ── Metrics model (shared with status) ──────────────────────────────────────
    private class PodMetricsResult
    {
        public List<ContainerMetricsResult>? Containers { get; set; }
    }

    private class ContainerMetricsResult
    {
        public string? Name { get; set; }
        public Dictionary<string, ResourceQuantity>? Usage { get; set; }
    }

    private static string FormatMemory(string memKi)
    {
        if (memKi.EndsWith("Ki") && long.TryParse(memKi[..^2], out var ki))
        {
            var gi = ki / (1024.0 * 1024.0);
            return $"{gi:F1}Gi";
        }
        return memKi;
    }

    private static string FormatStorage(string value)
    {
        if (value.EndsWith("Ki") && long.TryParse(value[..^2], out var ki))
        {
            var gi = ki / (1024.0 * 1024.0);
            return $"{gi:F1}Gi";
        }
        if (long.TryParse(value, out var bytes))
        {
            var gi = bytes / (1024.0 * 1024.0 * 1024.0);
            return $"{gi:F1}Gi";
        }
        return value;
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1}Gi";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F0}Mi";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F0}Ki";
        return $"{bytes:F0}B";
    }

    private static string FormatCpu(double cores)
    {
        var millicores = (int)Math.Round(cores * 1000);
        if (millicores >= 1000 && millicores % 1000 == 0)
            return $"{millicores / 1000}";
        return $"{millicores}m";
    }

    private static double ParseStorageBytes(string value)
    {
        if (value.EndsWith("Ki") && long.TryParse(value[..^2], out var ki))
            return ki * 1024.0;
        if (long.TryParse(value, out var bytes))
            return bytes;
        return 0;
    }

    // ── Node metrics helpers ────────────────────────────────────────────────────

    private class CurrentNodeMetrics
    {
        public double CpuCores { get; set; }
        public double MemoryBytes { get; set; }
    }

    private class HistoricalNodeMetrics
    {
        public double CpuNanoCoresAvg30m { get; set; }
        public double CpuNanoCoresAvg60m { get; set; }
        public double CpuNanoCoresMax5m { get; set; }
        public double MemoryBytesAvg30m { get; set; }
        public double MemoryBytesAvg60m { get; set; }
        public double MemoryBytesMax5m { get; set; }
        public double DiskUsedPercentAvg30m { get; set; }
        public double DiskUsedPercentAvg60m { get; set; }
        public double DiskUsedPercentMax5m { get; set; }
    }

    private async Task<Dictionary<string, CurrentNodeMetrics>> GetCurrentNodeMetricsAsync(Kubernetes client)
    {
        var result = new Dictionary<string, CurrentNodeMetrics>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = await client.CustomObjects.ListClusterCustomObjectAsync(
                "metrics.k8s.io", "v1beta1", "nodes");
            var jsonStr = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(jsonStr);

            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var name = item.GetProperty("metadata").GetProperty("name").GetString();
                var usage = item.GetProperty("usage");
                var cpuStr = usage.GetProperty("cpu").GetString()!;
                var memStr = usage.GetProperty("memory").GetString()!;

                result[name!] = new CurrentNodeMetrics
                {
                    CpuCores = new ResourceQuantity(cpuStr).ToDouble(),
                    MemoryBytes = new ResourceQuantity(memStr).ToDouble(),
                };
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch current node metrics");
        }
        return result;
    }

    private async Task<Dictionary<string, HistoricalNodeMetrics>> GetHistoricalNodeMetricsAsync()
    {
        var result = new Dictionary<string, HistoricalNodeMetrics>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(LogAnalyticsWorkspaceId))
            return result;

        try
        {
#pragma warning disable CS0618
            var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
            var logsClient = new LogsQueryClient(credential);

            var resourceId = new Azure.Core.ResourceIdentifier(LogAnalyticsWorkspaceId);

            // Try Perf table (Container Insights v1 / oms_agent)
            // Averages over 30m and 60m, plus max 5-minute peak over 24h
            var kql = @"
let cpuMem = Perf
| where ObjectName == 'K8SNode'
| where CounterName in ('cpuUsageNanoCores', 'memoryWorkingSetBytes')
| where TimeGenerated > ago(65m)
| project TimeGenerated, Computer, Metric = CounterName, Val = CounterValue;
let disk = InsightsMetrics
| where Namespace == 'disk' and Name == 'used_percent'
| where TimeGenerated > ago(65m)
| extend Computer0 = tostring(parse_json(Tags).hostName)
| project TimeGenerated, Computer = iff(isnotempty(Computer0), Computer0, Computer), Metric = 'diskUsedPercent', Val;
let allData = union cpuMem, disk;
let avgs = allData
| summarize
    Avg30m = avgif(Val, TimeGenerated > ago(35m)),
    Avg60m = avg(Val)
  by Computer, Metric;
let cpuMem24h = Perf
| where ObjectName == 'K8SNode'
| where CounterName in ('cpuUsageNanoCores', 'memoryWorkingSetBytes')
| where TimeGenerated > ago(24h)
| project TimeGenerated, Computer, Metric = CounterName, Val = CounterValue;
let disk24h = InsightsMetrics
| where Namespace == 'disk' and Name == 'used_percent'
| where TimeGenerated > ago(24h)
| extend Computer0 = tostring(parse_json(Tags).hostName)
| project TimeGenerated, Computer = iff(isnotempty(Computer0), Computer0, Computer), Metric = 'diskUsedPercent', Val;
let allData24h = union cpuMem24h, disk24h;
let max5m = allData24h
| summarize AvgVal = avg(Val) by Computer, Metric, bin(TimeGenerated, 5m)
| summarize Max5m = max(AvgVal) by Computer, Metric;
avgs
| join kind=leftouter max5m on Computer, Metric
| project Computer, Metric, Avg30m, Avg60m, Max5m";

            var response = await logsClient.QueryResourceAsync(
                resourceId, kql, new QueryTimeRange(TimeSpan.FromHours(25)));

            foreach (var row in response.Value.Table.Rows)
            {
                var computer = row[0]?.ToString() ?? "";
                var metric = row[1]?.ToString() ?? "";
                var avg30m = row[2] is not null ? Convert.ToDouble(row[2]) : 0;
                var avg60m = row[3] is not null ? Convert.ToDouble(row[3]) : 0;
                var max5m = row[4] is not null ? Convert.ToDouble(row[4]) : 0;

                if (!result.ContainsKey(computer))
                    result[computer] = new HistoricalNodeMetrics();

                var metrics = result[computer];
                switch (metric)
                {
                    case "cpuUsageNanoCores":
                        metrics.CpuNanoCoresAvg30m = avg30m;
                        metrics.CpuNanoCoresAvg60m = avg60m;
                        metrics.CpuNanoCoresMax5m = max5m;
                        break;
                    case "memoryWorkingSetBytes":
                        metrics.MemoryBytesAvg30m = avg30m;
                        metrics.MemoryBytesAvg60m = avg60m;
                        metrics.MemoryBytesMax5m = max5m;
                        break;
                    case "diskUsedPercent":
                        metrics.DiskUsedPercentAvg30m = avg30m;
                        metrics.DiskUsedPercentAvg60m = avg60m;
                        metrics.DiskUsedPercentMax5m = max5m;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch historical node metrics from Log Analytics");
        }
        return result;
    }

    private Dictionary<string, object> BuildNodeMetricsOutput(
        string nodeName, double cpuCapCores, double memCapBytes,
        double diskCapBytes, double diskAllocBytes,
        Dictionary<string, CurrentNodeMetrics> currentMetrics,
        Dictionary<string, HistoricalNodeMetrics> historicalMetrics,
        Dictionary<string, (double UsedBytes, double CapacityBytes)> nodeDiskStats)
    {
        var output = new Dictionary<string, object>();
        var capCpuStr = FormatCpu(cpuCapCores);
        var capMemStr = FormatBytes(memCapBytes);

        // ── Current CPU / Memory ────────────────────────────────────────────
        if (currentMetrics.TryGetValue(nodeName, out var cur))
        {
            var cpuPct = cpuCapCores > 0 ? (int)Math.Round(cur.CpuCores / cpuCapCores * 100) : 0;
            output["Cpu"] = $"{FormatCpu(cur.CpuCores)}/{capCpuStr} ({cpuPct}% utilization)";

            var memPct = memCapBytes > 0 ? (int)Math.Round(cur.MemoryBytes / memCapBytes * 100) : 0;
            output["Memory"] = $"{FormatBytes(cur.MemoryBytes)}/{capMemStr} ({memPct}% utilization)";
        }
        else
        {
            output["Cpu"] = capCpuStr;
            output["Memory"] = capMemStr;
        }

        // ── Current Disk ────────────────────────────────────────────────────
        // Prefer real stats from kubelet stats/summary; fall back to Capacity-Allocatable
        double diskUsedBytes, diskTotalBytes;
        if (nodeDiskStats.TryGetValue(nodeName, out var realDisk) && realDisk.CapacityBytes > 0)
        {
            diskUsedBytes = realDisk.UsedBytes;
            diskTotalBytes = realDisk.CapacityBytes;
        }
        else
        {
            diskUsedBytes = diskCapBytes - diskAllocBytes;
            diskTotalBytes = diskCapBytes;
        }
        var diskUsedPct = diskTotalBytes > 0
            ? (int)Math.Round(diskUsedBytes / diskTotalBytes * 100)
            : 0;
        output["Disk"] = $"{FormatBytes(diskUsedBytes)}/{FormatBytes(diskTotalBytes)} ({diskUsedPct}% full)";

        // ── Historical from Log Analytics ───────────────────────────────────
        if (historicalMetrics.TryGetValue(nodeName, out var hist))
        {
            // CPU
            if (hist.CpuNanoCoresAvg30m > 0 || hist.CpuNanoCoresAvg60m > 0)
            {
                var cores30 = hist.CpuNanoCoresAvg30m / 1_000_000_000;
                var cores60 = hist.CpuNanoCoresAvg60m / 1_000_000_000;
                var coresMax = hist.CpuNanoCoresMax5m / 1_000_000_000;

                output["CpuAvg30m"] = $"{FormatCpu(cores30)}/{capCpuStr} ({Pct(cores30, cpuCapCores)}% utilization)";
                output["CpuAvg60m"] = $"{FormatCpu(cores60)}/{capCpuStr} ({Pct(cores60, cpuCapCores)}% utilization)";
                output["CpuMax5m"] = $"{FormatCpu(coresMax)}/{capCpuStr} ({Pct(coresMax, cpuCapCores)}% utilization)";
            }

            // Memory
            if (hist.MemoryBytesAvg30m > 0 || hist.MemoryBytesAvg60m > 0)
            {
                output["MemoryAvg30m"] = $"{FormatBytes(hist.MemoryBytesAvg30m)}/{capMemStr} ({Pct(hist.MemoryBytesAvg30m, memCapBytes)}% utilization)";
                output["MemoryAvg60m"] = $"{FormatBytes(hist.MemoryBytesAvg60m)}/{capMemStr} ({Pct(hist.MemoryBytesAvg60m, memCapBytes)}% utilization)";
                output["MemoryMax5m"] = $"{FormatBytes(hist.MemoryBytesMax5m)}/{capMemStr} ({Pct(hist.MemoryBytesMax5m, memCapBytes)}% utilization)";
            }

            // Disk
            if (hist.DiskUsedPercentAvg30m > 0 || hist.DiskUsedPercentAvg60m > 0)
            {
                output["DiskAvg30m"] = FormatDiskFromPercent(hist.DiskUsedPercentAvg30m, diskCapBytes);
                output["DiskAvg60m"] = FormatDiskFromPercent(hist.DiskUsedPercentAvg60m, diskCapBytes);
                output["DiskMax5m"] = FormatDiskFromPercent(hist.DiskUsedPercentMax5m, diskCapBytes);
            }
        }

        return output;
    }

    private async Task<Dictionary<string, (double UsedBytes, double CapacityBytes)>> GetNodeDiskStatsAsync(
        Kubernetes client, IEnumerable<string> nodeNames)
    {
        var result = new ConcurrentDictionary<string, (double UsedBytes, double CapacityBytes)>(StringComparer.OrdinalIgnoreCase);

        var tasks = nodeNames.Select(async nodeName =>
        {
            try
            {
                var raw = await client.CoreV1.ConnectGetNodeProxyWithPathAsync(nodeName, "stats/summary");
                using var doc = JsonDocument.Parse(raw);
                var fs = doc.RootElement.GetProperty("node").GetProperty("fs");
                var usedBytes = fs.TryGetProperty("usedBytes", out var used) ? used.GetDouble() : 0;
                var capacityBytes = fs.TryGetProperty("capacityBytes", out var cap) ? cap.GetDouble() : 0;
                result[nodeName] = (usedBytes, capacityBytes);
            }
            catch { /* stats/summary may not be accessible on all nodes */ }
        });

        await Task.WhenAll(tasks);
        return result.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static int Pct(double value, double total) =>
        total > 0 ? (int)Math.Round(value / total * 100) : 0;

    private static string FormatDiskFromPercent(double usedPercent, double capacityBytes)
    {
        var pct = (int)Math.Round(usedPercent);
        var usedBytes = capacityBytes * usedPercent / 100;
        return $"{FormatBytes(usedBytes)}/{FormatBytes(capacityBytes)} ({pct}% full)";
    }
}
