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
using System.Security.Cryptography;
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
    /// If name already starts with the user's own "username-" prefix (or, for admins,
    /// any prefix containing a '-'), it is used as the full app name.
    /// Otherwise the username is prefixed automatically.
    /// Non-admins are rejected if the name contains a hyphen but does not match their own prefix.
    /// </summary>
    protected static string ResolveAppName(Dictionary<string, string> parameters)
    {
        var name = parameters["name"];
        var githubUsername = parameters["_githubUsername"];
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminVal)
            && string.Equals(adminVal, "true", StringComparison.OrdinalIgnoreCase);

        var ownPrefix = $"{githubUsername}-";

        if (name.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // User supplied their own full app label — use as-is
            return SanitizeAppName(name);
        }

        if (name.Contains('-'))
        {
            // Name references someone else's container
            if (!isAdmin)
            {
                throw new UnauthorizedAccessException(
                    "You do not have permission to manage containers that belong to other users.");
            }
            // Admin can reference any container
            return SanitizeAppName(name);
        }

        // Short name without prefix — prepend the user's username
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

    protected async Task<string> ResolveUseDatabaseAsync(string useDatabase)
    {
        if (useDatabase.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return useDatabase;

        var parts = useDatabase.Split('/', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException($"Invalid useDatabase value '{useDatabase}'. Expected a URL (https://...) or 'name/version' (e.g. 'mydb/latest').");

        var dbName = parts[0];
        var dbVersion = parts[1];

#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);
        var containerClient = blobServiceClient.GetBlobContainerClient("databases");

        var manifestClient = containerClient.GetBlobClient($"{dbName}/all.json");
        if (!await manifestClient.ExistsAsync())
            throw new InvalidOperationException($"No uploaded database named '{dbName}' found (missing {dbName}/all.json in 'databases' container).");

        var downloadResponse = await manifestClient.DownloadContentAsync();
        var manifestJson = downloadResponse.Value.Content.ToString();
        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        string resolvedVersion;
        if (string.Equals(dbVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("latest", out var latestProp) || latestProp.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException($"Database '{dbName}' manifest has no 'latest' version.");
            resolvedVersion = latestProp.GetString()!;
        }
        else
        {
            if (!root.TryGetProperty("versions", out var versionsProp) || versionsProp.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Database '{dbName}' manifest has no 'versions' array.");

            var found = false;
            foreach (var v in versionsProp.EnumerateArray())
            {
                if (string.Equals(v.GetString(), dbVersion, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                throw new InvalidOperationException($"Version '{dbVersion}' not found for database '{dbName}'. Available versions: {string.Join(", ", versionsProp.EnumerateArray().Select(v => v.GetString()))}.");

            resolvedVersion = dbVersion;
        }

        var blobName = $"{dbName}/{resolvedVersion}.bak";
        var blobClient = containerClient.GetBlobClient(blobName);
        if (!await blobClient.ExistsAsync())
            throw new InvalidOperationException($"Database backup blob '{blobName}' not found in 'databases' container.");

        var delegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = "databases",
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var blobUriBuilder = new BlobUriBuilder(blobClient.Uri)
        {
            Sas = sasBuilder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName)
        };

        Logger.LogInformation("Resolved useDatabase '{UseDatabase}' to blob '{BlobName}' (version: {Version}).", useDatabase, blobName, resolvedVersion);
        return blobUriBuilder.ToUri().ToString();
    }

    protected async Task RestoreDatabaseViaExecAsync(Kubernetes client, string sasUrl, string databaseName)
    {
        Logger.LogInformation("Restoring database '{DatabaseName}' via k8s exec...", databaseName);
        var podName = await FindMssqlPodAsync(client);

        // Step 1: Download the backup file into the mssql pod
        Logger.LogInformation("Downloading database backup to MSSQL pod...");
        var downloadScript = $"wget -O '/var/opt/mssql/data/{databaseName}.bak' '{sasUrl}' 2>&1 && echo 'DOWNLOAD_OK'";
        var downloadResult = await ExecInMssqlPodAsync(client, podName, downloadScript);
        if (!downloadResult.Stdout.Contains("DOWNLOAD_OK"))
            throw new InvalidOperationException($"Failed to download database backup for '{databaseName}'. {downloadResult}");

        // Step 2: Get logical file names from the backup
        Logger.LogInformation("Reading logical file names from backup...");
        var fileListSql = $"SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK=N'/var/opt/mssql/data/{databaseName}.bak'";
        var fileListScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -h -1 -W -s \"|\" -Q \"{fileListSql}\"";
        var fileListResult = await ExecInMssqlPodAsync(client, podName, fileListScript);

        string? dataLogicalName = null;
        string? logLogicalName = null;
        foreach (var line in fileListResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = line.Split('|');
            if (cols.Length < 3) continue;
            var logicalName = cols[0].Trim();
            var fileType = cols[2].Trim();
            if (fileType == "D" && dataLogicalName == null)
                dataLogicalName = logicalName;
            else if (fileType == "L" && logLogicalName == null)
                logLogicalName = logicalName;
        }

        if (string.IsNullOrEmpty(dataLogicalName) || string.IsNullOrEmpty(logLogicalName))
            throw new InvalidOperationException($"Failed to determine logical file names from backup for '{databaseName}'. {fileListResult}");

        // Step 3: Restore database with MOVE clauses
        Logger.LogInformation("Restoring database from backup file...");
        var restoreSql = $"RESTORE DATABASE [{databaseName}] FROM DISK=N'/var/opt/mssql/data/{databaseName}.bak'" +
            $" WITH MOVE N'{dataLogicalName}' TO N'/var/opt/mssql/data/{databaseName}.mdf'" +
            $", MOVE N'{logLogicalName}' TO N'/var/opt/mssql/log/{databaseName}_log.ldf'" +
            ", REPLACE";
        var restoreScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{restoreSql}\" && echo 'RESTORE_COMPLETE'";
        var restoreResult = await ExecInMssqlPodAsync(client, podName, restoreScript);
        if (!restoreResult.Stdout.Contains("RESTORE_COMPLETE"))
            throw new InvalidOperationException($"Failed to restore database '{databaseName}'. {restoreResult}");

        // Step 4: Clean up the backup file
        await ExecInMssqlPodAsync(client, podName, $"rm -f '/var/opt/mssql/data/{databaseName}.bak'");
        Logger.LogInformation("Database '{DatabaseName}' restored successfully.", databaseName);
    }

    protected async Task<ExecResult> ExecInBcPodAsync(Kubernetes client, string podName, string containerName, string psScript, bool usePwsh = true)
    {
        var powershellExecutable = usePwsh ? "pwsh" : "powershell";
        var command = new[] { powershellExecutable, "-NoProfile", "-Command", psScript };
        var ws = await client.WebSocketNamespacedPodExecAsync(
            podName, Namespace, command, containerName,
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
            Logger.LogWarning("BC pod {PowerShellExecutable} exec stderr: {StdErr}", powershellExecutable, stderr);
        }

        return new ExecResult(stdoutTask.Result, stderr);
    }

    /// <summary>
    /// Runs a PowerShell script detached inside a BC pod with fire-and-poll semantics.
    /// Uses file-based markers for idempotent retries and throws <see cref="RetryAfterException"/>
    /// if the script hasn't finished within ~30 seconds.
    /// </summary>
    protected record DetachedJobResult(string Stdout, string Stderr);

    protected async Task<DetachedJobResult> RunDetachedInBcPodAsync(
        Kubernetes client,
        string podName,
        string containerName,
        string jobPrefix,
        string jobIdInput,
        string script,
        string scriptParams = "",
        int retryAfterSeconds = 10,
        string retryMessage = "Still running...")
    {
        var jobId = ComputeDetachedJobId(jobIdInput);
        var basePath = $"C:\\run\\my\\{jobPrefix}-{jobId}";
        var scriptPath = $"{basePath}.ps1";
        var wrapperPath = $"{basePath}-run.ps1";
        var stdoutPath = $"{basePath}.stdout";
        var stderrPath = $"{basePath}.stderr";
        var donePath = $"{basePath}.done";

        // Check if job is already complete (retry after previous timeout)
        var doneCheck = await ExecInBcPodAsync(client, podName, containerName,
            $"if (Test-Path '{donePath}') {{ 'DONE' }} else {{ 'PENDING' }}");

        if (doneCheck.Stdout.Trim() == "DONE")
            return await CollectDetachedResultAsync(client, podName, containerName, basePath, stdoutPath, stderrPath);

        // Check if job is already running (script file exists but no done marker)
        var runningCheck = await ExecInBcPodAsync(client, podName, containerName,
            $"if (Test-Path '{scriptPath}') {{ 'RUNNING' }} else {{ 'NEW' }}");

        if (runningCheck.Stdout.Trim() == "NEW")
        {
            // First invocation — write script and wrapper, launch detached
            var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            await ExecInBcPodAsync(client, podName, containerName,
                $"[IO.File]::WriteAllBytes('{scriptPath}', [Convert]::FromBase64String('{scriptBase64}'))");

            var wrapperScript = $@"
try {{
    . 'C:\run\prompt.ps1' -silent
    & {{ . '{scriptPath}' {scriptParams} }} 2> '{stderrPath}' 6>&1 3>&1 4>&1 5>&1 | Out-File '{stdoutPath}' -Encoding utf8
}} catch {{
    $_.Exception.Message | Out-File '{stderrPath}' -Append -Encoding utf8
}} finally {{
    'DONE' | Out-File '{donePath}' -NoNewline
}}";
            var wrapperBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapperScript));
            await ExecInBcPodAsync(client, podName, containerName,
                $"[IO.File]::WriteAllBytes('{wrapperPath}', [Convert]::FromBase64String('{wrapperBase64}'))");

            await ExecInBcPodAsync(client, podName, containerName,
                $"Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile','-File','{wrapperPath}' -WindowStyle Hidden");
        }

        // Wait up to 30 seconds for the script to finish before returning 202
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(5_000);

            var pollCheck = await ExecInBcPodAsync(client, podName, containerName,
                $"if (Test-Path '{donePath}') {{ 'DONE' }} else {{ 'PENDING' }}");

            if (pollCheck.Stdout.Trim() == "DONE")
                return await CollectDetachedResultAsync(client, podName, containerName, basePath, stdoutPath, stderrPath);
        }

        throw new RetryAfterException(retryMessage, retryAfterSeconds);
    }

    private async Task<DetachedJobResult> CollectDetachedResultAsync(
        Kubernetes client, string podName, string containerName,
        string basePath, string stdoutPath, string stderrPath)
    {
        var stdoutResult = await ExecInBcPodAsync(client, podName, containerName,
            $"if (Test-Path '{stdoutPath}') {{ Get-Content '{stdoutPath}' -Raw }} else {{ '' }}");
        var stderrResult = await ExecInBcPodAsync(client, podName, containerName,
            $"if (Test-Path '{stderrPath}') {{ Get-Content '{stderrPath}' -Raw }} else {{ '' }}");

        // Clean up all job files
        try
        {
            await ExecInBcPodAsync(client, podName, containerName,
                $"Remove-Item '{basePath}*' -Force -ErrorAction SilentlyContinue");
        }
        catch { /* best-effort cleanup */ }

        return new DetachedJobResult(stdoutResult.Stdout.TrimEnd(), stderrResult.Stdout.TrimEnd());
    }

    private static string ComputeDetachedJobId(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
