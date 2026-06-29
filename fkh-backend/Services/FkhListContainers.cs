using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhListContainers : FkhServiceBase
{
    public FkhListContainers(ILogger<FkhListContainers> logger) : base(logger) { }

    public async Task<object> ListContainersAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var showAll = parameters.TryGetValue("all", out var allValue)
            && string.Equals(allValue, "true", StringComparison.OrdinalIgnoreCase);

        // Resolve the client's timezone for displaying local times
        TimeZoneInfo clientTz = TimeZoneInfo.Utc;
        if (parameters.TryGetValue("_timezone", out var tzId) && !string.IsNullOrWhiteSpace(tzId))
        {
            try { clientTz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
            catch (TimeZoneNotFoundException) { /* fall back to UTC */ }
        }

        var client = await GetKubernetesClientAsync();
        var allDeployments = await client.ListNamespacedDeploymentAsync(Namespace);

        // Fetch all BC pods upfront for log checks
        var allPods = await client.ListNamespacedPodAsync(Namespace, labelSelector: "app-type=windows-servicetier");
        var podsByApp = allPods.Items
            .Where(p => p.Metadata.Labels != null && p.Metadata.Labels.ContainsKey("app"))
            .GroupBy(p => p.Metadata.Labels["app"])
            .ToDictionary(g => g.Key, g => g.First());

        // Filter to BC service tier deployments (those whose pod template has app-type=windows-servicetier)
        var deployments = allDeployments.Items
            .Where(d => d.Spec.Template.Metadata.Labels != null
                && d.Spec.Template.Metadata.Labels.TryGetValue("app-type", out var appType)
                && appType == "windows-servicetier")
            .ToList();

        var usernamePrefix = $"{githubUsername.ToLowerInvariant()}-";

        var filtered = showAll
            ? deployments.Where(d =>
            {
                var appLabel = d.Spec.Template.Metadata.Labels.TryGetValue("app", out var app) ? app : "";
                return CanAccessContainer(parameters, appLabel);
            }).ToList()
            : deployments.Where(d =>
            {
                var appLabel = d.Spec.Template.Metadata.Labels.TryGetValue("app", out var app) ? app : "";
                return appLabel.StartsWith(usernamePrefix, StringComparison.OrdinalIgnoreCase);
            }).ToList();

        var runningOnly = parameters.TryGetValue("runningOnly", out var runningOnlyValue)
            && string.Equals(runningOnlyValue, "true", StringComparison.OrdinalIgnoreCase);

        if (filtered.Count == 0)
        {
            return new { Containers = Array.Empty<object>() };
        }

        // Get pod metrics if available
        Dictionary<string, PodMetrics> podMetricsMap = new();
        try
        {
            var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: "app-type=windows-servicetier");
            foreach (var pod in pods.Items)
            {
                try
                {
                    var metrics = await client.GetNamespacedCustomObjectAsync<PodMetrics>(
                        "metrics.k8s.io", "v1beta1", Namespace, "pods", pod.Metadata.Name);
                    if (metrics != null)
                        podMetricsMap[pod.Metadata.Labels["app"]] = metrics;
                }
                catch { /* metrics-server may not be available */ }
            }
        }
        catch { /* metrics API not available */ }

        // Get services to resolve FQDNs
        var services = await client.ListNamespacedServiceAsync(Namespace);
        var serviceMap = services.Items
            .Where(s => s.Spec?.Selector != null && s.Spec.Selector.ContainsKey("app"))
            .GroupBy(s => s.Spec.Selector["app"])
            .ToDictionary(g => g.Key, g => g.First());

        var containerResults = new List<object>();

        foreach (var deployment in filtered)
        {
            var appLabel = deployment.Spec.Template.Metadata.Labels["app"];
            var deploymentName = deployment.Metadata.Name;
            var replicas = deployment.Spec.Replicas ?? 0;
            var readyReplicas = deployment.Status?.ReadyReplicas ?? 0;
            var image = deployment.Spec.Template.Spec.Containers.FirstOrDefault()?.Image ?? "unknown";
            var container = deployment.Spec.Template.Spec.Containers.FirstOrDefault();

            // Strip registry prefix for readability
            var shortImage = image.Contains('/') ? image[(image.LastIndexOf('/') + 1)..] : image;

            var status = replicas == 0 ? "Stopped" : readyReplicas >= replicas ? "Running" : "Starting";

            // Detect Pending pods and extract scheduling failure reason
            string? pendingReason = null;
            if (status == "Starting" && podsByApp.TryGetValue(appLabel, out var pendingPod) && pendingPod.Status?.Phase == "Pending")
            {
                status = "Pending";
                var condition = pendingPod.Status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
                pendingReason = condition?.Message;
            }
            else if (status == "Starting" && !podsByApp.ContainsKey(appLabel))
            {
                status = "Pending";
                pendingReason = "No pod has been created yet. The scheduler may be waiting for a node.";
            }

            // If pod is "Running", check container logs for readiness
            if (status == "Running" && podsByApp.TryGetValue(appLabel, out var pod))
            {
                try
                {
                    var stream = await client.ReadNamespacedPodLogAsync(
                        pod.Metadata.Name,
                        Namespace,
                        container: pod.Spec.Containers[0].Name);
                    using var reader = new StreamReader(stream);
                    var logs = await reader.ReadToEndAsync();
                    if (logs.Contains("CONTAINER STARTUP FAILED"))
                    {
                        status = "Failed";
                    }
                    else if (!logs.Contains("Ready for connections!"))
                    {
                        status = "Initializing";
                    }
                }
                catch
                {
                    // If log read fails, keep Running status
                }
            }

            // Extract container name by stripping the "username-" prefix (split on last '-' since usernames can contain hyphens)
            var podName = appLabel.Contains('-') && appLabel.LastIndexOf('-') < appLabel.Length - 1
                ? appLabel[(appLabel.LastIndexOf('-') + 1)..] : appLabel;

            // Auto-stop time
            string? autoStopStr = null;
            if (deployment.Metadata.Annotations?.TryGetValue("fkh/auto-stop-at", out var stopAtRaw) == true
                && DateTimeOffset.TryParse(stopAtRaw, out var stopAt))
            {
                var remaining = stopAt - DateTimeOffset.UtcNow;
                var timeLeft = remaining.TotalMinutes > 0
                    ? $"in {remaining.Hours}h{remaining.Minutes:D2}m"
                    : "overdue";
                var localStop = TimeZoneInfo.ConvertTime(stopAt, clientTz);
                autoStopStr = $"{localStop:yyyy-MM-dd HH:mm} ({timeLeft})";
            }

            // Repo and project metadata
            string? repo = deployment.Metadata.Annotations?.TryGetValue("fkh/repo", out var r) == true && !string.IsNullOrEmpty(r) ? r : null;
            string? proj = deployment.Metadata.Annotations?.TryGetValue("fkh/project", out var p) == true && !string.IsNullOrEmpty(p) ? p : null;
            var devScope = deployment.Metadata.Annotations?.TryGetValue("fkh/dev-scope", out var dv) == true
                && string.Equals(dv, "true", StringComparison.OrdinalIgnoreCase);

            // Container env vars – extract database, multitenant, auth info
            var envVars = container?.Env;
            var databaseNameEnv = envVars?.FirstOrDefault(e => e.Name == "databaseName")?.Value;
            var isMultitenant = string.Equals(envVars?.FirstOrDefault(e => e.Name == "multitenant")?.Value, "Y", StringComparison.OrdinalIgnoreCase);

            // Web client URL from service FQDN
            string? webClientUrl = null;
            if (serviceMap.TryGetValue(appLabel, out var svc))
            {
                var dnsLabel = svc.Metadata.Annotations?.TryGetValue("service.beta.kubernetes.io/azure-dns-label-name", out var label) == true ? label : null;
                if (dnsLabel != null)
                {
                    var fqdn = $"{dnsLabel}.{AksLocation}.cloudapp.azure.com";
                    webClientUrl = isMultitenant
                        ? $"https://{fqdn}/BC/?tenant=default"
                        : $"https://{fqdn}/BC/";
                }
            }
            var tenantDatabaseEnv = isMultitenant && !string.IsNullOrWhiteSpace(databaseNameEnv) ? $"{databaseNameEnv}-default" : null;
            var authEnv = envVars?.FirstOrDefault(e => e.Name == "auth")?.Value;
            var authenticationEmailEnv = envVars?.FirstOrDefault(e => e.Name == "authenticationEMail")?.Value;

            // Memory metrics
            string? memoryStr = null;
            if (podMetricsMap.TryGetValue(appLabel, out var metrics))
            {
                var containerMetrics = metrics.Containers?.FirstOrDefault();
                if (containerMetrics?.Usage != null)
                {
                    if (containerMetrics.Usage.TryGetValue("memory", out var memVal))
                    {
                        var usedBytes = memVal.ToDouble();
                        var limitBytes = container?.Resources?.Limits?.TryGetValue("memory", out var limVal) == true
                            ? limVal.ToDouble() : 0;
                        var usedMb = usedBytes / (1024 * 1024);
                        memoryStr = limitBytes > 0
                            ? $"{usedMb:F0}Mb used (of {limitBytes / (1024 * 1024):F0}Mb)"
                            : $"{usedMb:F0}Mb used";
                    }
                }
            }

            // Skip non-running containers when --runningOnly is specified
            if (runningOnly && status != "Running")
                continue;

            containerResults.Add(new
            {
                AppLabel = appLabel,
                Name = podName,
                Status = status,
                StatusDetail = $"{readyReplicas}/{replicas} ready",
                Reason = pendingReason,
                Image = shortImage,
                AutoStop = autoStopStr,
                Repo = repo,
                Project = proj,
                WebClient = webClientUrl,
                Database = databaseNameEnv,
                TenantDatabase = tenantDatabaseEnv,
                Multitenant = isMultitenant,
                DevScope = devScope,
                Auth = authEnv,
                AuthenticationEmail = authenticationEmailEnv,
                Memory = memoryStr,
            });
        }

        return new { Containers = containerResults };
    }

    // Lightweight models for pod metrics API
    private class PodMetrics
    {
        public List<ContainerMetrics>? Containers { get; set; }
    }

    private class ContainerMetrics
    {
        public string? Name { get; set; }
        public Dictionary<string, ResourceQuantity>? Usage { get; set; }
    }
}
