using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhGetVersion : FkhServiceBase
{
    public FkhGetVersion(ILogger<FkhGetVersion> logger) : base(logger) { }

    public async Task<object> GetVersionAsync(Dictionary<string, string> parameters)
    {
        var backendVersion = Environment.GetEnvironmentVariable("FKH_VERSION");
        var deploymentRepo = Environment.GetEnvironmentVariable("DEPLOYMENT_REPO");
        var backendDeployedAt = Environment.GetEnvironmentVariable("DEPLOYED_AT");
        var fkhFork = Environment.GetEnvironmentVariable("FKH_FORK");
        string? clusterVersion = null;
        string? clusterDeployedAt = null;

        try
        {
            var client = await GetKubernetesClientAsync();
            var configMap = await client.CoreV1.ReadNamespacedConfigMapAsync("fkh-version", Namespace);
            if (configMap?.Data is not null)
            {
                configMap.Data.TryGetValue("version", out clusterVersion);
                configMap.Data.TryGetValue("deployed-at", out clusterDeployedAt);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read fkh-version ConfigMap");
        }

        object? forkStatus = null;
        if (!string.IsNullOrEmpty(fkhFork) && !string.Equals(fkhFork, "Freddy-DK/Fkh", StringComparison.OrdinalIgnoreCase))
        {
            var ahead = Environment.GetEnvironmentVariable("FKH_FORK_AHEAD");
            var behind = Environment.GetEnvironmentVariable("FKH_FORK_BEHIND");
            var mergeBaseSha = Environment.GetEnvironmentVariable("FKH_FORK_MERGE_BASE_SHA");
            var mergeBaseDate = Environment.GetEnvironmentVariable("FKH_FORK_MERGE_BASE_DATE");
            if (!string.IsNullOrEmpty(ahead))
            {
                forkStatus = new
                {
                    Ahead = int.TryParse(ahead, out var a) ? a : 0,
                    Behind = int.TryParse(behind, out var b) ? b : 0,
                    MergeBaseSha = mergeBaseSha,
                    MergeBaseDate = mergeBaseDate,
                };
            }
        }

        return new
        {
            BackendVersion = FormatVersion(backendVersion),
            BackendDeployedAt = backendDeployedAt,
            ClusterVersion = FormatVersion(clusterVersion),
            ClusterDeployedAt = clusterDeployedAt,
            DeploymentRepo = deploymentRepo,
            FkhFork = fkhFork,
            ForkStatus = forkStatus,
        };
    }

    private static string FormatVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return "Not available";
        if (version.StartsWith('v') && Version.TryParse(version[1..], out var parsed))
            return parsed.ToString();
        return version;
    }
}
