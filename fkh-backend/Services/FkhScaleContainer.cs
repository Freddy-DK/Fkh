using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhScaleContainer : FkhServiceBase
{
    private readonly FkhUserSettings _userSettings;

    public FkhScaleContainer(ILogger<FkhScaleContainer> logger, FkhUserSettings userSettings) : base(logger)
    {
        _userSettings = userSettings;
    }

    public async Task<object> StopContainerAsync(Dictionary<string, string> parameters)
    {
        var result = await ScaleAsync(parameters, 0);
        // Clear auto-stop annotation when manually stopping
        var name = parameters.TryGetValue("name", out var n) ? n : null;
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";
        var client = await GetKubernetesClientAsync();
        await ClearAutoStopAnnotationAsync(client, deploymentName);
        return result;
    }

    public async Task<object> StartContainerAsync(Dictionary<string, string> parameters)
    {
        // Ensure a Windows node with healthy CNS is available before scaling up
        var name = parameters.TryGetValue("name", out var n2) ? n2 : null;
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";

        var client = await GetKubernetesClientAsync();

        // ── Enforce MaxContainers limit ──────────────────────────────────────
        var isAdmin = parameters.TryGetValue("_isAdmin", out var isAdminVal)
            && string.Equals(isAdminVal, "true", StringComparison.OrdinalIgnoreCase);
        var maxNode = await _userSettings.GetResolvedSettingAsync(githubUsername, isAdmin, "MaxContainers");
        var maxContainers = maxNode?.GetValue<int>() ?? -1;

        var allDeployments = await client.ListNamespacedDeploymentAsync(Namespace);
        var usernamePrefix = $"{githubUsername.ToLowerInvariant()}-";
        var activeCount = allDeployments.Items
            .Count(d => d.Spec.Template.Metadata.Labels != null
                && d.Spec.Template.Metadata.Labels.TryGetValue("app", out var app)
                && app.StartsWith(usernamePrefix, StringComparison.OrdinalIgnoreCase)
                && (d.Spec.Replicas ?? 0) > 0);

        if (maxContainers >= 0 && activeCount >= maxContainers)
        {
            throw new InvalidOperationException(
                $"You already have {activeCount} active container(s). Your limit is {maxContainers}. "
                + "Please stop or remove an existing container before starting another one.");
        }

        // Check if the existing deployment targets spot nodes
        var deployment = await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);
        var useSpot = deployment.Spec.Template.Spec.NodeSelector != null
            && deployment.Spec.Template.Spec.NodeSelector.TryGetValue("kubernetes.azure.com/scalesetpriority", out var priority)
            && priority == "spot";

        await EnsureWindowsNodeReadyAsync(client, useSpot);
        await CleanupPlaceholderPodAsync(client, useSpot);

        // Generate a fresh container blob SAS URL and inject it as an env var
        await InjectContainerBlobContainerEnvAsync(client, appName, deploymentName);

        var result = await ScaleAsync(parameters, 1);

        parameters.TryGetValue("autostop", out var autoStopValue);
        parameters.TryGetValue("_timezone", out var autoStopTz);
        var autoStop = ParseAutoStop(autoStopValue, autoStopTz);
        if (autoStop is not null)
        {
            await SetAutoStopAnnotationAsync(client, deploymentName, autoStop.Value.StopAt);
        }

        return new
        {
            result.Container,
            result.Deployment,
            result.Replicas,
            AutoStop = autoStop is not null
                ? $"{autoStop.Value.StopAt:yyyy-MM-dd HH:mm} UTC ({autoStop.Value.Description})"
                : (string?)null,
        };
    }

    public async Task<object> ExtendAutoStopAsync(Dictionary<string, string> parameters)
    {
        var name = parameters.TryGetValue("name", out var n3) ? n3 : null;
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";

        var client = await GetKubernetesClientAsync();
        var deployment = await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);

        if (deployment.Metadata.Annotations == null
            || !deployment.Metadata.Annotations.TryGetValue(AutoStopAnnotation, out var stopAtStr)
            || !DateTimeOffset.TryParse(stopAtStr, out var currentStopAt))
        {
            // No auto-stop set — set one 2 hours from now
            currentStopAt = DateTimeOffset.UtcNow;
        }

        // If the current stop time is in the past, extend from now instead
        if (currentStopAt < DateTimeOffset.UtcNow)
            currentStopAt = DateTimeOffset.UtcNow;

        var newStopAt = currentStopAt.AddHours(2);
        await SetAutoStopAnnotationAsync(client, deploymentName, newStopAt);

        return new { Container = appName, AutoStop = $"{newStopAt:yyyy-MM-dd HH:mm} UTC" };
    }

    public async Task<object> SetAutoStopAsync(Dictionary<string, string> parameters)
    {
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";

        parameters.TryGetValue("autostop", out var autoStopValue);
        parameters.TryGetValue("_timezone", out var autoStopTz);
        var autoStop = ParseAutoStop(autoStopValue, autoStopTz)
            ?? throw new ArgumentException("Invalid autostop value. Use '<n>h' (e.g. '2h') or a time of day (e.g. '18:00' or '6PM').");

        var client = await GetKubernetesClientAsync();
        await SetAutoStopAnnotationAsync(client, deploymentName, autoStop.StopAt);
        return new { Container = appName, AutoStop = $"{autoStop.StopAt:yyyy-MM-dd HH:mm} UTC ({autoStop.Description})" };
    }

    public async Task<object> ClearAutoStopForContainerAsync(Dictionary<string, string> parameters)
    {
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";

        var client = await GetKubernetesClientAsync();
        await ClearAutoStopAnnotationAsync(client, deploymentName);
        return new { Container = appName, AutoStop = (string?)null };
    }

    public async Task<object> StopAllContainersAsync(Dictionary<string, string> parameters)
    {
        var client = await GetKubernetesClientAsync();
        var allDeployments = await client.ListNamespacedDeploymentAsync(Namespace);

        var containerDeployments = allDeployments.Items
            .Where(d => d.Spec.Template.Metadata.Labels != null
                && d.Spec.Template.Metadata.Labels.TryGetValue("app-type", out var appType)
                && appType == "windows-servicetier"
                && (d.Spec.Replicas ?? 0) > 0)
            .ToList();

        var stopped = new List<string>();
        foreach (var deployment in containerDeployments)
        {
            var deploymentName = deployment.Metadata.Name;
            var appName = deployment.Spec.Template.Metadata.Labels.TryGetValue("app", out var app) ? app : deploymentName;

            Logger.LogInformation("Stopping deployment '{Deployment}'...", deploymentName);
            deployment.Spec.Replicas = 0;
            await client.ReplaceNamespacedDeploymentAsync(deployment, deploymentName, Namespace);
            await ClearAutoStopAnnotationAsync(client, deploymentName);
            stopped.Add(appName);
        }

        Logger.LogInformation("Stopped {Count} container(s).", stopped.Count);
        return new { StoppedCount = stopped.Count, StoppedContainers = stopped };
    }

    private record ScaleResult(string Container, string Deployment, int Replicas);

    private async Task<ScaleResult> ScaleAsync(Dictionary<string, string> parameters, int replicas)
    {
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";

        Logger.LogInformation("Scaling deployment '{Deployment}' to {Replicas} replicas...", deploymentName, replicas);
        var client = await GetKubernetesClientAsync();

        var deployment = await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);
        deployment.Spec.Replicas = replicas;
        await client.ReplaceNamespacedDeploymentAsync(deployment, deploymentName, Namespace);

        var action = replicas == 0 ? "Stopped" : "Started";
        Logger.LogInformation("{Action} deployment '{Deployment}'.", action, deploymentName);
        return new ScaleResult(appName, deploymentName, replicas);
    }

    private async Task InjectContainerBlobContainerEnvAsync(Kubernetes client, string appName, string deploymentName)
    {
        try
        {
            var sasUrl = await GenerateContainerBlobSasUrlAsync(appName);

            // Patch the deployment to add/update the ContainerBlobContainer env var with a fresh SAS
            var deployment = await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);
            var container = deployment.Spec.Template.Spec.Containers[0];
            container.Env ??= new List<V1EnvVar>();

            var existing = container.Env.FirstOrDefault(e => e.Name == "ContainerBlobContainer");
            if (existing != null)
            {
                existing.Value = sasUrl;
                existing.ValueFrom = null;
            }
            else
            {
                container.Env.Add(new V1EnvVar { Name = "ContainerBlobContainer", Value = sasUrl });
            }

            await client.ReplaceNamespacedDeploymentAsync(deployment, deploymentName, Namespace);
            Logger.LogInformation("Injected ContainerBlobContainer env var for container '{AppName}'.", appName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to inject ContainerBlobContainer env var for container '{AppName}'.", appName);
        }
    }
}
