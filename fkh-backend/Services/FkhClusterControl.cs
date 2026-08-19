using System.Text.Json;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerService;
using Azure.Storage.Blobs;
using Fkh.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhClusterControl : FkhServiceBase
{
    private const string SettingsContainer = "settings";
    private const string OverridesBlob = "clusterschedule.json";

    private readonly FkhScaleContainer _scaleContainer;

    public FkhClusterControl(ILogger<FkhClusterControl> logger, FkhScaleContainer scaleContainer) : base(logger)
    {
        _scaleContainer = scaleContainer;
    }

    public async Task<object> StopClusterAsync(Dictionary<string, string> parameters)
    {
        // Optional one-off override: when the cluster should automatically start again.
        parameters.TryGetValue("autostart", out var autoStartValue);
        parameters.TryGetValue("_timezone", out var tz);
        var autoStart = ParseAutoStop(autoStartValue, tz);
        var overrides = await GetOverridesAsync();
        if (autoStart is not null)
        {
            overrides.NextStart = autoStart.Value.StopAt;
            overrides.NextStop = null;
            await SaveOverridesAsync(overrides);
        }

        var cluster = GetClusterResource();
        var data = (await cluster.GetAsync()).Value.Data;
        var powerState = data.PowerStateCode?.ToString();

        var autoStartText = overrides.NextStart is { } ns ? $"{ns:yyyy-MM-dd HH:mm} UTC" : null;

        if (string.Equals(powerState, "Stopped", StringComparison.OrdinalIgnoreCase))
            return new { Message = "Cluster is already stopped.", PowerState = "Stopped", AutoStart = autoStartText };

        // Stop all containers before shutting down the cluster
        try
        {
            Logger.LogInformation("Stopping all containers before cluster shutdown...");
            await _scaleContainer.StopAllContainersAsync(parameters);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to stop containers before cluster shutdown. Proceeding with cluster stop.");
        }

        Logger.LogInformation("Stopping AKS cluster {Cluster} in resource group {RG}...", ClusterName, ResourceGroup);
        await cluster.StopAsync(Azure.WaitUntil.Started);
        Logger.LogInformation("AKS cluster {Cluster} stop initiated.", ClusterName);

        return new { Message = "Cluster stop initiated. It may take a few minutes to fully stop.", PowerState = "Stopping", AutoStart = autoStartText };
    }

    public async Task<object> StartClusterAsync(Dictionary<string, string> parameters)
    {
        // Optional one-off override: when the cluster should automatically stop again.
        parameters.TryGetValue("autostop", out var autoStopValue);
        parameters.TryGetValue("_timezone", out var tz);
        var autoStop = ParseAutoStop(autoStopValue, tz);
        var overrides = await GetOverridesAsync();
        if (autoStop is not null)
        {
            overrides.NextStop = autoStop.Value.StopAt;
            overrides.NextStart = null;
            await SaveOverridesAsync(overrides);
        }

        var cluster = GetClusterResource();
        var data = (await cluster.GetAsync()).Value.Data;
        var powerState = data.PowerStateCode?.ToString();

        var autoStopText = overrides.NextStop is { } ns ? $"{ns:yyyy-MM-dd HH:mm} UTC" : null;

        if (string.Equals(powerState, "Running", StringComparison.OrdinalIgnoreCase))
            return new { Message = "Cluster is already running.", PowerState = "Running", AutoStop = autoStopText };

        Logger.LogInformation("Starting AKS cluster {Cluster} in resource group {RG}...", ClusterName, ResourceGroup);
        await cluster.StartAsync(Azure.WaitUntil.Started);
        Logger.LogInformation("AKS cluster {Cluster} start initiated.", ClusterName);

        return new { Message = "Cluster start initiated. It may take a few minutes before the cluster is fully running.", PowerState = "Starting", AutoStop = autoStopText };
    }

    // ── Schedule primitives (used by the auto-schedule timer) ────────────────────

    public async Task<string?> GetPowerStateAsync()
    {
        var data = (await GetClusterResource().GetAsync()).Value.Data;
        return data.PowerStateCode?.ToString();
    }

    public async Task StartClusterForScheduleAsync()
    {
        Logger.LogInformation("Schedule: starting AKS cluster {Cluster}...", ClusterName);
        await GetClusterResource().StartAsync(Azure.WaitUntil.Started);
    }

    public async Task StopClusterForScheduleAsync()
    {
        try
        {
            await _scaleContainer.StopAllContainersAsync(new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Schedule: failed to stop containers before cluster shutdown. Proceeding.");
        }

        Logger.LogInformation("Schedule: stopping AKS cluster {Cluster}...", ClusterName);
        await GetClusterResource().StopAsync(Azure.WaitUntil.Started);
    }

    // ── One-off override persistence (settings-container blob) ──────────────────

    public async Task<ClusterScheduleOverrides> GetOverridesAsync()
    {
        var blobClient = GetOverridesBlobClient();
        if (!await blobClient.ExistsAsync())
            return new ClusterScheduleOverrides();

        try
        {
            var content = (await blobClient.DownloadContentAsync()).Value.Content.ToString();
            return JsonSerializer.Deserialize<ClusterScheduleOverrides>(content, JsonSerializerOptions.Web)
                ?? new ClusterScheduleOverrides();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to parse cluster-schedule overrides blob. Ignoring.");
            return new ClusterScheduleOverrides();
        }
    }

    public async Task SaveOverridesAsync(ClusterScheduleOverrides overrides)
    {
        var blobClient = GetOverridesBlobClient();
        var json = JsonSerializer.Serialize(overrides, JsonSerializerOptions.Web);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    private BlobClient GetOverridesBlobClient()
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);
        return blobServiceClient.GetBlobContainerClient(SettingsContainer).GetBlobClient(OverridesBlob);
    }

    private ContainerServiceManagedClusterResource GetClusterResource()
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var armClient = new ArmClient(credential);
        var aksId = ContainerServiceManagedClusterResource
            .CreateResourceIdentifier(SubscriptionId, ResourceGroup, ClusterName);
        return armClient.GetContainerServiceManagedClusterResource(aksId);
    }
}
