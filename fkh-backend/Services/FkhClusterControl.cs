using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerService;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhClusterControl : FkhServiceBase
{
    private readonly FkhScaleContainer _scaleContainer;

    public FkhClusterControl(ILogger<FkhClusterControl> logger, FkhScaleContainer scaleContainer) : base(logger)
    {
        _scaleContainer = scaleContainer;
    }

    public async Task<object> StopClusterAsync(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("confirm", out var confirm) ||
            !string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This will stop the entire AKS cluster and all containers. Pass --confirm to proceed.");
        }

        var cluster = GetClusterResource();
        var data = (await cluster.GetAsync()).Value.Data;
        var powerState = data.PowerStateCode?.ToString();

        if (string.Equals(powerState, "Stopped", StringComparison.OrdinalIgnoreCase))
            return new { Message = "Cluster is already stopped.", PowerState = "Stopped" };

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

        return new { Message = "Cluster stop initiated. It may take a few minutes to fully stop.", PowerState = "Stopping" };
    }

    public async Task<object> StartClusterAsync(Dictionary<string, string> parameters)
    {
        var cluster = GetClusterResource();
        var data = (await cluster.GetAsync()).Value.Data;
        var powerState = data.PowerStateCode?.ToString();

        if (string.Equals(powerState, "Running", StringComparison.OrdinalIgnoreCase))
            return new { Message = "Cluster is already running.", PowerState = "Running" };

        Logger.LogInformation("Starting AKS cluster {Cluster} in resource group {RG}...", ClusterName, ResourceGroup);
        await cluster.StartAsync(Azure.WaitUntil.Started);
        Logger.LogInformation("AKS cluster {Cluster} start initiated.", ClusterName);

        return new { Message = "Cluster start initiated. It may take a few minutes before the cluster is fully running.", PowerState = "Starting" };
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
