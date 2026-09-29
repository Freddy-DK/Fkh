using System.Runtime.InteropServices;
using System.Text.Json;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerService;
using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

/// <summary>
/// Collects a non-identifying snapshot of the deployment (versions, VM sizes, feature flags)
/// for BackendStarted and the daily Heartbeat usage events.
/// </summary>
public class FkhUsageSnapshot : FkhServiceBase
{
    private static DateOnly _lastHeartbeat;

    public FkhUsageSnapshot(ILogger<FkhUsageSnapshot> logger) : base(logger) { }

    public void TrackBackendStarted()
    {
        if (!FkhTelemetry.Enabled)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                FkhTelemetry.Track("BackendStarted", await CollectAsync(includeContainerCounts: false));
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to collect usage snapshot for BackendStarted.");
            }
        });
    }

    /// <summary>Sends at most one Heartbeat per UTC day per backend instance.</summary>
    public void TrackHeartbeatIfDue()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!FkhTelemetry.Enabled || _lastHeartbeat == today)
            return;
        _lastHeartbeat = today;

        _ = Task.Run(async () =>
        {
            try
            {
                FkhTelemetry.Track("Heartbeat", await CollectAsync(includeContainerCounts: true));
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to collect usage snapshot for Heartbeat.");
            }
        });
    }

    private async Task<Dictionary<string, object?>> CollectAsync(bool includeContainerCounts)
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["region"] = AksLocation,
            ["config"] = ParseConfig(),
        };

        string? powerState = null;
        try
        {
#pragma warning disable CS0618
            var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
            var aksId = ContainerServiceManagedClusterResource.CreateResourceIdentifier(SubscriptionId, ResourceGroup, ClusterName);
            var data = (await new ArmClient(credential).GetContainerServiceManagedClusterResource(aksId).GetAsync()).Value.Data;
            powerState = data.PowerStateCode?.ToString();
            snapshot["aks"] = new Dictionary<string, object?>
            {
                ["kubernetesVersion"] = data.KubernetesVersion,
                ["currentKubernetesVersion"] = data.CurrentKubernetesVersion,
                ["skuTier"] = data.Sku?.Tier?.ToString(),
                ["supportPlan"] = data.SupportPlan?.ToString(),
                ["autoUpgradeChannel"] = data.AutoUpgradeProfile?.UpgradeChannel?.ToString(),
                ["nodeOsUpgradeChannel"] = data.AutoUpgradeProfile?.NodeOSUpgradeChannel?.ToString(),
                ["powerState"] = powerState,
                ["nodePools"] = data.AgentPoolProfiles.OrderBy(p => p.Name).Select(p => new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["mode"] = p.Mode?.ToString(),
                    ["osSku"] = p.OSSku?.ToString(),
                    ["vmSize"] = p.VmSize,
                    ["count"] = p.Count,
                    ["minCount"] = p.MinCount,
                    ["maxCount"] = p.MaxCount,
                    ["priority"] = p.ScaleSetPriority?.ToString(),
                    ["orchestratorVersion"] = p.CurrentOrchestratorVersion,
                    ["nodeImageVersion"] = p.NodeImageVersion,
                }).ToList(),
            };
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read AKS cluster for usage snapshot.");
        }

        if (!string.Equals(powerState, "Running", StringComparison.OrdinalIgnoreCase))
            return snapshot;

        try
        {
            var client = await GetKubernetesClientAsync();
            var configMap = await client.CoreV1.ReadNamespacedConfigMapAsync("fkh-version", Namespace);
            if (configMap?.Data is not null && configMap.Data.TryGetValue("version", out var clusterVersion))
                snapshot["clusterVersion"] = clusterVersion;

            if (includeContainerCounts)
            {
                var deployments = await client.ListNamespacedDeploymentAsync(Namespace, labelSelector: "app-type=windows-servicetier");
                snapshot["containerCount"] = deployments.Items.Count;
                snapshot["runningContainerCount"] = deployments.Items.Count(d => (d.Spec.Replicas ?? 0) > 0);

                var nodes = await client.ListNodeAsync(labelSelector: "kubernetes.io/os=windows");
                snapshot["windowsNodeCount"] = nodes.Items.Count;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read Kubernetes state for usage snapshot.");
        }

        return snapshot;
    }

    private static JsonElement? ParseConfig()
    {
        var json = Environment.GetEnvironmentVariable("FKH_TELEMETRY_CONFIG");
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
