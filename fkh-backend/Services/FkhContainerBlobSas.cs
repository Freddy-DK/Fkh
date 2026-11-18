using System.Globalization;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

/// <summary>
/// Keeps every container's blob SAS (encryption key, certificate) valid while the container exists,
/// and recovers containers whose pod was recreated with an already expired SAS.
/// </summary>
public class FkhContainerBlobSas : FkhServiceBase
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    // A recovered container that fails the same way again needs a human, not a restart loop.
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromHours(24);
    internal const string RecoveredAtAnnotation = "fkh/blob-sas-recovered-at";
    private const int LogTailLines = 50;

    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public FkhContainerBlobSas(ILogger<FkhContainerBlobSas> logger) : base(logger) { }

    public async Task CheckAndRenewAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCheck < CheckInterval)
            return;
        _lastCheck = now;

        var client = await GetKubernetesClientAsync();
        var deployments = (await client.ListNamespacedDeploymentAsync(Namespace)).Items
            .Where(d => d.Spec.Template.Metadata.Labels != null
                && d.Spec.Template.Metadata.Labels.TryGetValue("app-type", out var appType)
                && appType == "windows-servicetier"
                && d.Spec.Template.Metadata.Labels.ContainsKey("app"))
            .ToList();
        var secrets = (await client.ListNamespacedSecretAsync(Namespace)).Items
            .ToDictionary(s => s.Metadata.Name);
        var podsByApp = (await client.ListNamespacedPodAsync(Namespace, labelSelector: "app-type=windows-servicetier")).Items
            .Where(p => p.Metadata.Labels != null && p.Metadata.Labels.ContainsKey("app"))
            .ToLookup(p => p.Metadata.Labels["app"]);

        var renewed = 0;
        var recovered = 0;
        foreach (var deployment in deployments)
        {
            var appName = deployment.Spec.Template.Metadata.Labels["app"];
            if (!secrets.TryGetValue($"{appName}-secret", out var secret))
                continue;

            try
            {
                if (ContainerBlobSasNeedsRenewal(GetContainerBlobSasExpiry(secret), now))
                {
                    await RenewContainerBlobSasAsync(client, appName);
                    renewed++;
                }

                if ((deployment.Spec.Replicas ?? 0) > 0
                    && !RecoveredRecently(deployment, now)
                    && await HasExpiredSasStartupFailureAsync(client, podsByApp[appName]))
                {
                    Logger.LogWarning("Container '{AppName}' failed to start on an expired blob SAS. Redeploying it with a renewed SAS.", appName);
                    await RenewContainerBlobSasAndRedeployAsync(client, appName, deployment.Metadata.Name,
                        new Dictionary<string, string> { [RecoveredAtAnnotation] = now.ToString("o", CultureInfo.InvariantCulture) });
                    recovered++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Blob SAS maintenance failed for container '{AppName}'.", appName);
            }
        }

        if (renewed > 0 || recovered > 0)
            Logger.LogInformation("Blob SAS maintenance renewed {Renewed} and recovered {Recovered} container(s).", renewed, recovered);
    }

    private static bool RecoveredRecently(V1Deployment deployment, DateTimeOffset now) =>
        deployment.Metadata.Annotations != null
        && deployment.Metadata.Annotations.TryGetValue(RecoveredAtAnnotation, out var value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var recoveredAt)
        && now - recoveredAt < RecoveryCooldown;

    private async Task<bool> HasExpiredSasStartupFailureAsync(Kubernetes client, IEnumerable<V1Pod> pods)
    {
        foreach (var pod in pods.Where(p => p.Status?.Phase == "Running"))
        {
            using var stream = await client.ReadNamespacedPodLogAsync(
                pod.Metadata.Name, Namespace, container: pod.Spec.Containers[0].Name, tailLines: LogTailLines);
            using var reader = new StreamReader(stream);
            if (IsExpiredSasStartupFailure(await reader.ReadToEndAsync()))
                return true;
        }
        return false;
    }

    internal static bool IsExpiredSasStartupFailure(string log) =>
        log.Contains("CONTAINER STARTUP FAILED", StringComparison.Ordinal)
        && log.Contains("encryption key", StringComparison.OrdinalIgnoreCase)
        && log.Contains("(403) Forbidden", StringComparison.Ordinal);
}
