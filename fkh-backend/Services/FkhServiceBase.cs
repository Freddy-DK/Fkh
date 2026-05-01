using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerService;
using Fkh.Models;
using k8s;
using k8s.KubeConfigModels;
using k8s.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Fkh.Services;

public abstract class FkhServiceBase
{
    protected readonly string ResourceGroup;
    protected readonly string ClusterName;
    protected readonly string SubscriptionId;
    protected readonly string AcrName;
    protected readonly string ClientId;
    protected readonly string BaseImage;
    protected readonly string AksLocation;
    protected readonly string ContactEmail;
    protected readonly string DbsStorageAccountName;
    protected readonly string? LogAnalyticsWorkspaceId;
    protected readonly string AadTenantId;
    protected readonly string? AadGraphClientId;
    protected readonly string AadAppNamePrefix;
    protected readonly string? AadAppAdditionalOwner;
    protected readonly ILogger Logger;

    protected const string Namespace = "app";
    protected const string AcrRepository = "businesscentral";
    protected readonly string FoldersValue;

    protected FkhServiceBase(ILogger logger)
    {
        Logger = logger;
        var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") ?? "localhost";
        FoldersValue = $@"c:\run\my=https://{websiteHostname}/api/containerscripts\ContainerScripts";
        SubscriptionId = Environment.GetEnvironmentVariable("AKS_SUBSCRIPTION_ID")
            ?? throw new InvalidOperationException("AKS_SUBSCRIPTION_ID is not configured.");
        ResourceGroup = Environment.GetEnvironmentVariable("AKS_RESOURCE_GROUP")
            ?? throw new InvalidOperationException("AKS_RESOURCE_GROUP is not configured.");
        ClusterName = Environment.GetEnvironmentVariable("AKS_CLUSTER_NAME")
            ?? throw new InvalidOperationException("AKS_CLUSTER_NAME is not configured.");
        AcrName = Environment.GetEnvironmentVariable("ACR_NAME")
            ?? throw new InvalidOperationException("ACR_NAME is not configured.");
        ClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
            ?? throw new InvalidOperationException("AZURE_CLIENT_ID is not configured.");
        BaseImage = Environment.GetEnvironmentVariable("BASE_IMAGE")
            ?? throw new InvalidOperationException("BASE_IMAGE is not configured.");
        AksLocation = Environment.GetEnvironmentVariable("AKS_LOCATION")
            ?? throw new InvalidOperationException("AKS_LOCATION is not configured.");
        ContactEmail = Environment.GetEnvironmentVariable("CONTACT_EMAIL_FOR_LETSENCRYPT")
            ?? throw new InvalidOperationException("CONTACT_EMAIL_FOR_LETSENCRYPT is not configured.");
        DbsStorageAccountName = Environment.GetEnvironmentVariable("DBS_STORAGE_ACCOUNT_NAME")
            ?? throw new InvalidOperationException("DBS_STORAGE_ACCOUNT_NAME is not configured.");
        LogAnalyticsWorkspaceId = Environment.GetEnvironmentVariable("LOG_ANALYTICS_WORKSPACE_ID");
        AadTenantId = Environment.GetEnvironmentVariable("AAD_TENANT_ID")
            ?? throw new InvalidOperationException("AAD_TENANT_ID is not configured.");
        AadGraphClientId = Environment.GetEnvironmentVariable("AAD_GRAPH_CLIENT_ID");
        AadAppNamePrefix = Environment.GetEnvironmentVariable("AAD_APP_NAME_PREFIX") ?? "";
        AadAppAdditionalOwner = Environment.GetEnvironmentVariable("AAD_APP_ADDITIONAL_OWNER");
    }

    /// <summary>
    /// Creates a credential that authenticates as the deployer's app registration
    /// via workload identity federation, for Microsoft Graph operations.
    /// </summary>
    protected TokenCredential CreateGraphCredential()
    {
        if (string.IsNullOrEmpty(AadGraphClientId))
            throw new InvalidOperationException(
                "AAD_GRAPH_CLIENT_ID is not configured. Set enable_aad_container_auth = true in deployment.tfvars.");

#pragma warning disable CS0618
        var miCredential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        return new ClientAssertionCredential(
            AadTenantId,
            AadGraphClientId,
            async (ct) => (await miCredential.GetTokenAsync(
                new TokenRequestContext(new[] { "api://AzureADTokenExchange" }), ct)).Token);
    }

    protected string AcrLoginServer => $"{AcrName}.azurecr.io";

    protected async Task<Kubernetes> GetKubernetesClientAsync()
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var armClient = new ArmClient(credential);

        var aksId = ContainerServiceManagedClusterResource
            .CreateResourceIdentifier(SubscriptionId, ResourceGroup, ClusterName);
        var cluster = armClient.GetContainerServiceManagedClusterResource(aksId);
        var creds = await cluster.GetClusterUserCredentialsAsync();
        var kubeconfig = creds.Value.Kubeconfigs[0].Value;

        using var stream = new MemoryStream(kubeconfig);
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);

        // Azure Functions sandbox blocks writes to the Windows certificate store.
        // Use EphemeralKeySet so the PFX stays in memory.
        if (config.SslCaCerts != null)
        {
            var updated = new X509Certificate2Collection();
            foreach (var cert in config.SslCaCerts)
            {
                updated.Add(new X509Certificate2(cert.RawData, (string?)null, X509KeyStorageFlags.EphemeralKeySet));
            }
            config.SslCaCerts = updated;
        }
        if (config.ClientCertificateData != null)
        {
            var certBytes = Convert.FromBase64String(config.ClientCertificateData);
            var keyBytes = config.ClientCertificateKeyData != null
                ? Convert.FromBase64String(config.ClientCertificateKeyData)
                : null;

            if (keyBytes != null)
            {
                var certPem = System.Text.Encoding.UTF8.GetString(certBytes);
                var keyPem = System.Text.Encoding.UTF8.GetString(keyBytes);
                config.ClientCertificateData = null;
                config.ClientCertificateKeyData = null;
                var clientCert = X509Certificate2.CreateFromPem(certPem, keyPem);
                // Export/reimport with EphemeralKeySet to avoid certificate store writes
                config.ClientCertificateData = Convert.ToBase64String(
                    clientCert.Export(X509ContentType.Pfx));
            }
        }

        return new Kubernetes(config);
    }

    /// <summary>
    /// Ensures at least one Windows VM is Ready and has a running CNS pod.
    /// If no Windows VM exists, triggers the cluster autoscaler by creating a
    /// temporary placeholder pod, then throws <see cref="RetryAfterException"/>
    /// so the caller retries after the VM is provisioned.
    /// </summary>
    protected async Task EnsureWindowsNodeReadyAsync(Kubernetes client, bool useSpot = false)
    {
        // Check for Ready Windows nodes
        var nodes = await client.ListNodeAsync();
        var windowsNodes = nodes.Items
            .Where(n => n.Metadata.Labels.TryGetValue("kubernetes.io/os", out var os) && os == "windows")
            .ToList();

        // When targeting spot, only consider nodes with the spot label
        if (useSpot)
        {
            windowsNodes = windowsNodes
                .Where(n => n.Metadata.Labels.TryGetValue("kubernetes.azure.com/scalesetpriority", out var priority) && priority == "spot")
                .ToList();
        }

        var readyWindowsNodes = windowsNodes
            .Where(n => n.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True"))
            .ToList();

        if (readyWindowsNodes.Count == 0)
        {
            var nodeType = useSpot ? "Windows Spot" : "Windows";
            Logger.LogInformation("No Ready {NodeType} nodes found. Triggering autoscaler...", nodeType);
            await CreatePlaceholderPodAsync(client, useSpot);
            throw new RetryAfterException(
                $"No {nodeType} VM is available. The cluster autoscaler is provisioning one. Please retry in a few minutes.",
                retryAfterSeconds: 120);
        }

        // Check that CNS is running on at least one Ready Windows node
        // Note: Windows CNS pods may use different labels than Linux ones (k8s-app=azure-cns),
        // so we match by pod name prefix instead.
        var allKubeSystemPods = await client.ListNamespacedPodAsync("kube-system");
        var cnsPods = new k8s.Models.V1PodList
        {
            Items = allKubeSystemPods.Items
                .Where(p => p.Metadata.Name.StartsWith("azure-cns", StringComparison.OrdinalIgnoreCase))
                .ToList()
        };
        var readyNodeNames = readyWindowsNodes.Select(n => n.Metadata.Name).ToHashSet();
        var cnsOnWindows = cnsPods.Items
            .Where(p => p.Spec.NodeName != null
                && readyNodeNames.Contains(p.Spec.NodeName)
                && p.Status.Phase == "Running"
                && p.Status.ContainerStatuses?.All(c => c.Ready) == true)
            .ToList();

        if (cnsOnWindows.Count == 0)
        {
            var nodeType = useSpot ? "Windows Spot" : "Windows";
            Logger.LogInformation("{NodeType} nodes exist but CNS is not ready yet.", nodeType);
            throw new RetryAfterException(
                $"A {nodeType} VM is starting up but networking is not ready yet. Please retry in a couple of minutes.",
                retryAfterSeconds: 60);
        }

        Logger.LogInformation("Windows VM ready with healthy CNS on {NodeCount} node(s).", cnsOnWindows.Count);
    }

    private async Task CreatePlaceholderPodAsync(Kubernetes client, bool useSpot = false)
    {
        var podName = useSpot ? "fkh-windows-spot-warmup" : "fkh-windows-warmup";

        // Check if placeholder already exists
        try
        {
            await client.ReadNamespacedPodAsync(podName, Namespace);
            Logger.LogInformation("Warmup placeholder pod already exists.");
            return;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Expected — create it
        }

        var nodeSelector = new Dictionary<string, string> { ["kubernetes.io/os"] = "windows" };
        var tolerations = new List<V1Toleration>();
        if (useSpot)
        {
            nodeSelector["kubernetes.azure.com/scalesetpriority"] = "spot";
            tolerations.Add(new V1Toleration
            {
                Key = "kubernetes.azure.com/scalesetpriority",
                OperatorProperty = "Equal",
                Value = "spot",
                Effect = "NoSchedule"
            });
        }

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = Namespace,
                Labels = new Dictionary<string, string> { ["fkh/purpose"] = "warmup" }
            },
            Spec = new V1PodSpec
            {
                NodeSelector = nodeSelector,
                Tolerations = tolerations.Count > 0 ? tolerations : null,
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = "warmup",
                        Image = "mcr.microsoft.com/windows/nanoserver:ltsc2022",
                        Command = new List<string> { "cmd", "/c", "echo ready && ping -n 600 127.0.0.1 > nul" },
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity>
                            {
                                ["cpu"] = new ResourceQuantity("100m"),
                                ["memory"] = new ResourceQuantity("128Mi")
                            }
                        }
                    }
                },
                RestartPolicy = "Never"
            }
        };

        await client.CreateNamespacedPodAsync(pod, Namespace);
        Logger.LogInformation("Created warmup placeholder pod to trigger Windows VM autoscaling.");
    }

    /// <summary>
    /// Cleans up the warmup placeholder pod if it exists.
    /// Call after a Windows VM is confirmed ready.
    /// </summary>
    protected async Task CleanupPlaceholderPodAsync(Kubernetes client, bool useSpot = false)
    {
        var podName = useSpot ? "fkh-windows-spot-warmup" : "fkh-windows-warmup";
        try
        {
            await client.DeleteNamespacedPodAsync(podName, Namespace);
            Logger.LogInformation("Cleaned up warmup placeholder pod '{PodName}'.", podName);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    protected static string GetImageTag(string artifactUrl)
    {
        var uri = new Uri(artifactUrl);
        return uri.AbsolutePath.Replace('/', '-').TrimStart('-').ToLowerInvariant();
    }

    protected static string SanitizeAppName(string name)
    {
        var appName = name.Replace('.', '-').Replace('_', '-').ToLowerInvariant();
        if (appName.Length > 63) appName = appName[..63];
        return appName.TrimEnd('-');
    }

    /// <summary>
    /// Resolves the app label from parameters.
    /// For admins: if name contains a '-', it is used as the full name; otherwise username is prefixed.
    /// For non-admins: username is always prefixed (name must not contain '-').
    /// </summary>
    protected static string ResolveAppName(Dictionary<string, string> parameters)
    {
        var name = parameters["name"];
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminVal)
            && string.Equals(adminVal, "true", StringComparison.OrdinalIgnoreCase);
        if (isAdmin && name.Contains('-'))
        {
            return SanitizeAppName(name);
        }
        var githubUsername = parameters["_githubUsername"];
        return SanitizeAppName($"{githubUsername}-{name}");
    }



    protected const string SqlcmdPath = "/opt/mssql-tools18/bin/sqlcmd";

    protected async Task<string> FindMssqlPodAsync(Kubernetes client)
    {
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: "app=mssql");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running MSSQL pod found in namespace '{Namespace}'.");
        return pod.Metadata.Name;
    }

    protected record ExecResult(string Stdout, string Stderr)
    {
        public override string ToString() =>
            string.IsNullOrWhiteSpace(Stderr) ? Stdout : $"{Stdout}\n[stderr]: {Stderr}";
    }

    protected async Task<ExecResult> ExecInMssqlPodAsync(Kubernetes client, string podName, string bashScript)
    {
        var command = new[] { "/bin/bash", "-c", bashScript };
        var ws = await client.WebSocketNamespacedPodExecAsync(
            podName, Namespace, command, "mssql",
            stderr: true, stdin: false, stdout: true, tty: false);

        using var demux = new StreamDemuxer(ws);
        demux.Start();

        var stdoutStream = demux.GetStream(1, null);
        var stderrStream = demux.GetStream(2, null);

        using var stdoutReader = new StreamReader(stdoutStream);
        using var stderrReader = new StreamReader(stderrStream);

        var stdoutTask = stdoutReader.ReadToEndAsync();
        var stderrTask = stderrReader.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        var stderr = stderrTask.Result;
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Logger.LogWarning("MSSQL pod exec stderr: {StdErr}", stderr);
        }

        return new ExecResult(stdoutTask.Result, stderr);
    }

    protected const string ContainerFilesBlobContainer = "containerfiles";

    /// <summary>
    /// Generates a container-level SAS URL scoped to the {appName}/ prefix within the 'containerfiles' blob container.
    /// Creates the blob container if it doesn't exist. Grants Read, Create, and Write permissions.
    /// </summary>
    protected async Task<string> GenerateContainerBlobSasUrlAsync(string appName)
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);

        var blobContainerClient = blobServiceClient.GetBlobContainerClient(ContainerFilesBlobContainer);
        await blobContainerClient.CreateIfNotExistsAsync();

        var delegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24));

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = ContainerFilesBlobContainer,
            Resource = "c",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(24)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Create | BlobSasPermissions.Write);

        var containerUri = blobContainerClient.Uri;
        var blobUriBuilder = new BlobUriBuilder(containerUri)
        {
            Sas = sasBuilder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName)
        };
        // Return the URL including the appName prefix so scripts can access blobs directly under it
        return $"{blobUriBuilder.ToUri().ToString().Split('?')[0]}/{appName}?{blobUriBuilder.ToUri().Query.TrimStart('?')}";
    }

    protected const string AutoStopAnnotation = "fkh/auto-stop-at";

    /// <summary>
    /// Parses an autostop value. Accepts:
    ///   "4h"    → 4 hours from now
    ///   "18:00" → today (or tomorrow) at 18:00 in the client's timezone
    ///   "6PM"   → today (or tomorrow) at 18:00 in the client's timezone
    /// Returns null if the value is empty or unparseable.
    /// </summary>
    protected static (DateTimeOffset StopAt, string Description)? ParseAutoStop(string? value, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();

        // Hours format: integer followed by 'h' (e.g., "4h", "12h")
        if (value.EndsWith("h", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[..^1], out var hours)
            && hours > 0)
        {
            var stopAt = DateTimeOffset.UtcNow.AddHours(hours);
            stopAt = EnforceMinimumAutoStop(stopAt);
            return (stopAt, $"{hours}h");
        }

        // Time-of-day format (e.g., "18:00", "6PM", "6:30 PM")
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)
            && parsed.Date == DateTime.MinValue.Date)
        {
            var tz = TimeZoneInfo.Utc;
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                try { tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
                catch (TimeZoneNotFoundException) { /* fall back to UTC */ }
            }

            var nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var localStop = new DateTime(nowInTz.Year, nowInTz.Month, nowInTz.Day, parsed.Hour, parsed.Minute, parsed.Second);
            if (localStop <= nowInTz)
                localStop = localStop.AddDays(1);

            var utcStop = TimeZoneInfo.ConvertTimeToUtc(localStop, tz);
            var stopAt = new DateTimeOffset(utcStop, TimeSpan.Zero);
            stopAt = EnforceMinimumAutoStop(stopAt);
            return (stopAt, value);
        }

        return null;
    }

    private static DateTimeOffset EnforceMinimumAutoStop(DateTimeOffset stopAt)
    {
        var minimumStopAt = DateTimeOffset.UtcNow.AddHours(2);
        return stopAt < minimumStopAt ? minimumStopAt : stopAt;
    }

    protected async Task SetAutoStopAnnotationAsync(Kubernetes client, string deploymentName, DateTimeOffset stopAt)
    {
        var patch = new V1Patch(
            JsonSerializer.Serialize(new
            {
                metadata = new { annotations = new Dictionary<string, string> { [AutoStopAnnotation] = stopAt.UtcDateTime.ToString("o") } }
            }),
            V1Patch.PatchType.MergePatch);
        await client.PatchNamespacedDeploymentAsync(patch, deploymentName, Namespace);
        Logger.LogInformation("Set auto-stop annotation on '{Deployment}' to {StopAt}", deploymentName, stopAt);
    }

    protected async Task ClearAutoStopAnnotationAsync(Kubernetes client, string deploymentName)
    {
        var patch = new V1Patch(
            JsonSerializer.Serialize(new
            {
                metadata = new { annotations = new Dictionary<string, string?> { [AutoStopAnnotation] = (string?)null } }
            }),
            V1Patch.PatchType.MergePatch);
        await client.PatchNamespacedDeploymentAsync(patch, deploymentName, Namespace);
        Logger.LogInformation("Cleared auto-stop annotation on '{Deployment}'.", deploymentName);
    }
}
