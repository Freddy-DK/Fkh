using System.Text.RegularExpressions;
using Azure.Containers.ContainerRegistry;
using Azure.Identity;
using Azure.Storage.Blobs;
using Fkh.Models;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Fkh.Services;

public class FkhCreateContainer : FkhServiceBase
{
    private readonly GitHubAppTokenService _gitHubAppTokenService;
    private readonly FkhUserSettings _userSettings;

    public FkhCreateContainer(ILogger<FkhCreateContainer> logger, GitHubAppTokenService gitHubAppTokenService, FkhUserSettings userSettings) : base(logger)
    {
        _gitHubAppTokenService = gitHubAppTokenService;
        _userSettings = userSettings;
    }

    public async Task<object> CreateContainerAsync(Dictionary<string, string> parameters)
    {
        var name = parameters.TryGetValue("name", out var n) ? n : null;
        var artifactUrl = parameters["artifactUrl"];
        var adminUsername = parameters["adminUsername"];
        var adminPassword = parameters["adminPassword"];
        var githubUsername = parameters["_githubUsername"];
        var defaultCpu = Environment.GetEnvironmentVariable("CONTAINER_DEFAULT_CPU") ?? "250m";
        var defaultMemory = Environment.GetEnvironmentVariable("CONTAINER_DEFAULT_MEMORY") ?? "3Gi";
        var cpuRequest = parameters.TryGetValue("cpu", out var cpu) ? cpu : defaultCpu;
        var memoryRequest = parameters.TryGetValue("memory", out var mem) ? mem : defaultMemory;
        var repo = parameters.TryGetValue("repo", out var r) ? r : null;
        var project = parameters.TryGetValue("project", out var p) ? p : null;
        var useSpot = parameters.TryGetValue("spot", out var spotValue)
            && string.Equals(spotValue, "true", StringComparison.OrdinalIgnoreCase);
        var useDatabase = parameters.TryGetValue("useDatabase", out var udb) ? udb : null;
        var tenantDatabase = parameters.TryGetValue("tenantDatabase", out var tdb) ? tdb : null;
        var moveAllAppsToDevScope = parameters.TryGetValue("moveAllAppsToDevScope", out var devScopeVal)
            && string.Equals(devScopeVal, "true", StringComparison.OrdinalIgnoreCase);
        var multitenant = !string.IsNullOrWhiteSpace(tenantDatabase)
            || (parameters.TryGetValue("multitenant", out var mtVal)
                && string.Equals(mtVal, "true", StringComparison.OrdinalIgnoreCase));

        var licenseFileUrl = parameters.TryGetValue("licenseFileUrl", out var lfUrl) ? lfUrl : null;
        var authenticationEmail = parameters.TryGetValue("authenticationEmail", out var authEmail) ? authEmail : null;
        var useAadAuth = !string.IsNullOrWhiteSpace(authenticationEmail);
        var aadAuthIsMultitenant = string.Equals(
            Environment.GetEnvironmentVariable("AAD_AUTH_IS_MULTITENANT") ?? "false",
            "true", StringComparison.OrdinalIgnoreCase);

        var imageTag = GetImageTag(artifactUrl);
        var fullImage = $"{AcrLoginServer}/{AcrRepository}:{imageTag}";
        var appName = ResolveAppName(parameters);
        var databaseName = appName;

        if (!Regex.IsMatch(databaseName, @"^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException("Name contains invalid characters. Only letters, digits, hyphens, and underscores are allowed.");

        Logger.LogInformation("Checking ACR for image {Image}", fullImage);
        await EnsureImageExistsAsync(imageTag, fullImage, artifactUrl);

        Logger.LogInformation("Image found. Ensuring a Windows node is ready...");
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
                + "Please stop or remove an existing container before creating a new one.");
        }

        await EnsureWindowsNodeReadyAsync(client, useSpot);
        await CleanupPlaceholderPodAsync(client, useSpot);

        Logger.LogInformation("Creating Kubernetes resources for {AppName}...", appName);

        var deploymentName = $"{appName}-deployment";
        var serviceName = $"{appName}-service";
        var secretName = $"{appName}-secret";

        // ── Fail if deployment already exists ────────────────────────────────
        await EnsureDeploymentDoesNotExistAsync(client, deploymentName);

        // ── Check database does not exist, download backup, and restore via k8s exec ─
        string? tenantDatabaseName = null;
        if (multitenant)
        {
            tenantDatabaseName = $"{databaseName}-default";
            string appSasUrl;
            string tenantSasUrl;
            if (!string.IsNullOrWhiteSpace(useDatabase))
            {
                appSasUrl = await ResolveUseDatabaseAsync(useDatabase);
                tenantSasUrl = !string.IsNullOrWhiteSpace(tenantDatabase)
                    ? await ResolveUseDatabaseAsync(tenantDatabase)
                    : throw new InvalidOperationException("When using --useDatabase with --multitenant, --tenantDatabase must also be specified.");
            }
            else if (!string.IsNullOrWhiteSpace(tenantDatabase))
            {
                appSasUrl = await GetDatabaseBackupSasUrlAsync(imageTag);
                tenantSasUrl = await ResolveUseDatabaseAsync(tenantDatabase);
            }
            else
            {
                appSasUrl = await GetDatabaseBackupSasUrlAsync(imageTag, "-app");
                tenantSasUrl = await GetDatabaseBackupSasUrlAsync(imageTag, "-tenant");
            }
            await EnsureDatabaseDoesNotExistAsync(client, databaseName);
            await EnsureDatabaseDoesNotExistAsync(client, tenantDatabaseName);
            await RestoreDatabaseViaExecAsync(client, appSasUrl, databaseName);
            await RestoreDatabaseViaExecAsync(client, tenantSasUrl, tenantDatabaseName);
        }
        else
        {
            var sasUrl = !string.IsNullOrWhiteSpace(useDatabase)
                ? await ResolveUseDatabaseAsync(useDatabase)
                : await GetDatabaseBackupSasUrlAsync(imageTag);
            await EnsureDatabaseDoesNotExistAsync(client, databaseName);
            await RestoreDatabaseViaExecAsync(client, sasUrl, databaseName);
        }

        if (moveAllAppsToDevScope)
        {
            Logger.LogInformation("Moving all apps to dev scope for database '{DatabaseName}'...", databaseName);
            await MoveAllAppsToDevScopeAsync(client, databaseName);
        }

        // ── Get SQL disk usage after restore ─────────────────────────────────
        var diskInfo = await GetSqlDiskUsageAsync(client);

        // ── Create Kubernetes resources ──────────────────────────────────────
        await CreateAdminSecretAsync(client, secretName, adminPassword);

        var dnsLabel = appName;
        var publicDnsName = $"{dnsLabel}.{AksLocation}.cloudapp.azure.com";

        // ── Create per-container AAD App Registration if AAD auth is requested ───
        string? aadAppClientId = null;
        string? aadAppObjectId = null;
        if (useAadAuth)
        {
            var redirectUri = $"https://{publicDnsName}/BC/SignIn";
            (aadAppObjectId, aadAppClientId) = await CreateAadAppRegistrationAsync(appName, redirectUri, aadAuthIsMultitenant);
        }

        await CreateDeploymentAsync(client, deploymentName, appName, fullImage, adminUsername, secretName, publicDnsName, databaseName, cpuRequest, memoryRequest, repo, project, multitenant, useSpot, licenseFileUrl, authenticationEmail, aadAppClientId, aadAppObjectId, aadAuthIsMultitenant, moveAllAppsToDevScope);
        await CreateLoadBalancerServiceAsync(client, serviceName, appName, dnsLabel);

        // Set auto-stop annotation if requested
        string? autoStopInfo = null;
        parameters.TryGetValue("autostop", out var autoStopValue);
        parameters.TryGetValue("_timezone", out var autoStopTz);
        var autoStop = ParseAutoStop(autoStopValue, autoStopTz);
        if (autoStop is not null)
        {
            await SetAutoStopAnnotationAsync(client, deploymentName, autoStop.Value.StopAt);
            autoStopInfo = $"{autoStop.Value.StopAt:yyyy-MM-dd HH:mm} UTC ({autoStop.Value.Description})";
        }

        Logger.LogInformation("Deployment {Deployment} and service {Service} created in namespace {Namespace}",
            deploymentName, serviceName, Namespace);

        return new
        {
            Message = "Container created.",
            Deployment = deploymentName,
            Service = serviceName,
            Image = fullImage,
            Fqdn = publicDnsName,
            WebClient = useAadAuth
                ? $"https://{publicDnsName}/BC/SignIn?tenant=default"
                : $"https://{publicDnsName}/BC?tenant=default",
            Database = databaseName,
            TenantDatabase = tenantDatabaseName,
            Multitenant = multitenant,
            Auth = useAadAuth ? "AAD" : "NavUserPassword",
            AuthenticationEmail = authenticationEmail,
            SqlDisk = diskInfo,
            AutoStop = autoStopInfo,
        };
    }

    private async Task EnsureDeploymentDoesNotExistAsync(Kubernetes client, string deploymentName)
    {
        try
        {
            await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);
            throw new InvalidOperationException(
                $"A deployment named '{deploymentName}' already exists in namespace '{Namespace}'. Please choose a different name or remove the existing container first.");
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Expected — deployment does not exist
        }
    }

    private async Task EnsureDatabaseDoesNotExistAsync(Kubernetes client, string databaseName)
    {
        var podName = await FindMssqlPodAsync(client);
        var script = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -h -1 -W " +
            $"-Q \"SET NOCOUNT ON; IF DB_ID(N'{databaseName}') IS NOT NULL PRINT N'EXISTS' ELSE PRINT N'NOTEXISTS'\"";
        var result = await ExecInMssqlPodAsync(client, podName, script);
        if (result.Stdout.Contains("EXISTS") && !result.Stdout.Contains("NOTEXISTS"))
        {
            throw new InvalidOperationException(
                $"A database named '{databaseName}' already exists on the SQL server. Please choose a different name or remove the existing container first.");
        }
    }

    private async Task<string> GetDatabaseBackupSasUrlAsync(string imageTag, string suffix = "")
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);
        var containerClient = blobServiceClient.GetBlobContainerClient("cronus");
        var blobName = imageTag + suffix;
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync())
        {
            throw new InvalidOperationException(
                $"Database backup blob '{blobName}' not found in container 'cronus' on storage account '{DbsStorageAccountName}'.");
        }

        // Generate a user-delegation SAS valid for 1 hour
        var delegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        var sasBuilder = new Azure.Storage.Sas.BlobSasBuilder
        {
            BlobContainerName = "cronus",
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
        };
        sasBuilder.SetPermissions(Azure.Storage.Sas.BlobSasPermissions.Read);

        var blobUriBuilder = new Azure.Storage.Blobs.BlobUriBuilder(blobClient.Uri)
        {
            Sas = sasBuilder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName)
        };

        Logger.LogInformation("Generated SAS URL for blob '{BlobName}'.", blobName);
        return blobUriBuilder.ToUri().ToString();
    }

    private async Task MoveAllAppsToDevScopeAsync(Kubernetes client, string databaseName)
    {
        var podName = await FindMssqlPodAsync(client);

        // Set Published As = 2 (Dev) and Tenant ID = 'default' on all published apps
        var updateSql = $"UPDATE [{databaseName}].[dbo].[Published Application] SET [Published As] = 2, [Tenant ID] = 'default'";
        var safeSql1 = updateSql.Replace("\"", "\\\"").Replace("$", "\\$");
        var updateScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{safeSql1}\"";
        var updateResult = await ExecInMssqlPodAsync(client, podName, updateScript);
        if (!string.IsNullOrWhiteSpace(updateResult.Stderr))
        {
            Logger.LogWarning("moveAllAppsToDevScope UPDATE stderr: {StdErr}", updateResult.Stderr);
        }

        // Delete uninstalled app records from the tenant database
        var deleteSql = $"DELETE FROM [default].[dbo].[$ndo$navappuninstalledapp]";
        var safeSql2 = deleteSql.Replace("\"", "\\\"").Replace("$", "\\$");
        var deleteScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{safeSql2}\"";
        var deleteResult = await ExecInMssqlPodAsync(client, podName, deleteScript);
        if (!string.IsNullOrWhiteSpace(deleteResult.Stderr))
        {
            Logger.LogWarning("moveAllAppsToDevScope DELETE stderr: {StdErr}", deleteResult.Stderr);
        }

        Logger.LogInformation("All apps moved to dev scope for database '{DatabaseName}'.", databaseName);
    }

    private async Task<string> GetSqlDiskUsageAsync(Kubernetes client)
    {
        try
        {
            var podName = await FindMssqlPodAsync(client);
            var result = await ExecInMssqlPodAsync(client, podName, "df -h /var/opt/mssql/data | tail -1 | awk '{print $3 \" used / \" $2 \" total (\" $5 \" full)\"}'");
            var info = result.Stdout.Trim();
            return string.IsNullOrEmpty(info) ? "unknown" : info;
        }
        catch
        {
            return "unknown";
        }
    }

    private async Task EnsureImageExistsAsync(string tag, string fullImage, string artifactUrl)
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var client = new ContainerRegistryClient(new Uri($"https://{AcrLoginServer}"), credential);

        try
        {
            var artifact = client.GetArtifact(AcrRepository, tag);
            await artifact.GetManifestPropertiesAsync();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            Logger.LogInformation("Image not found in ACR. Triggering createImages workflow for {ArtifactUrl}...", artifactUrl);
            await _gitHubAppTokenService.TriggerCreateImagesWorkflowAsync(artifactUrl);
            throw new RetryAfterException(
                $"Image does not exist yet: {fullImage}. The createImages workflow has been triggered automatically.",
                retryAfterSeconds: 300);
        }
    }

    private async Task CreateAdminSecretAsync(Kubernetes client, string secretName, string adminPassword)
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, NamespaceProperty = Namespace },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["password"] = System.Text.Encoding.UTF8.GetBytes(adminPassword)
            }
        };

        try { await client.DeleteNamespacedSecretAsync(secretName, Namespace); } catch { /* may not exist */ }
        await client.CreateNamespacedSecretAsync(secret, Namespace);
    }

    private async Task CreateDeploymentAsync(
        Kubernetes client, string deploymentName, string appName, string fullImage,
        string adminUsername, string secretName, string publicDnsName, string databaseName,
        string cpuRequest, string memoryRequest, string? repo, string? project, bool multitenant, bool useSpot, string? licenseFileUrl, string? authenticationEmail, string? aadAppClientId, string? aadAppObjectId, bool aadAuthIsMultitenant, bool moveAllAppsToDevScope)
    {
        var annotations = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(repo))
            annotations["fkh/repo"] = repo;
        if (!string.IsNullOrWhiteSpace(project))
            annotations["fkh/project"] = project;
        if (!string.IsNullOrWhiteSpace(aadAppObjectId))
            annotations["fkh/aad-app-object-id"] = aadAppObjectId;
        if (moveAllAppsToDevScope)
            annotations["fkh/dev-scope"] = "true";

        var nodeSelector = new Dictionary<string, string>
        {
            ["kubernetes.io/os"] = "windows"
        };
        if (useSpot)
        {
            nodeSelector["kubernetes.azure.com/scalesetpriority"] = "spot";
        }

        var tolerations = new List<V1Toleration>();
        if (useSpot)
        {
            tolerations.Add(new V1Toleration
            {
                Key = "kubernetes.azure.com/scalesetpriority",
                OperatorProperty = "Equal",
                Value = "spot",
                Effect = "NoSchedule"
            });
        }

        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = deploymentName,
                NamespaceProperty = Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = appName,
                    ["app-type"] = "windows-servicetier"
                },
                Annotations = annotations.Count > 0 ? annotations : null
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { ["app"] = appName }
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>
                        {
                            ["app"] = appName,
                            ["app-type"] = "windows-servicetier"
                        }
                    },
                    Spec = new V1PodSpec
                    {
                        EnableServiceLinks = false,
                        NodeSelector = nodeSelector,
                        Tolerations = tolerations.Count > 0 ? tolerations : null,
                        Affinity = new V1Affinity
                        {
                            PodAntiAffinity = new V1PodAntiAffinity
                            {
                                PreferredDuringSchedulingIgnoredDuringExecution = new List<V1WeightedPodAffinityTerm>
                                {
                                    new()
                                    {
                                        Weight = 100,
                                        PodAffinityTerm = new V1PodAffinityTerm
                                        {
                                            LabelSelector = new V1LabelSelector
                                            {
                                                MatchLabels = new Dictionary<string, string>
                                                {
                                                    ["app-type"] = "windows-servicetier"
                                                }
                                            },
                                            TopologyKey = "kubernetes.io/hostname"
                                        }
                                    }
                                }
                            }
                        },
                        Containers = new List<V1Container>
                        {
                            new()
                            {
                                Name = appName,
                                Image = fullImage,
                                Command = new List<string> { "powershell", "-NoProfile", "-Command",
                                    "$ErrorActionPreference = 'Stop'; try { & c:\\run\\start.ps1 } catch { Write-Host \"CONTAINER STARTUP FAILED: $_\"; Write-Host $_.ScriptStackTrace }; if ($LASTEXITCODE) { Write-Host \"CONTAINER STARTUP FAILED with exit code: $LASTEXITCODE\" }; Write-Host 'CONTAINER STARTUP FAILED — container will stay alive for troubleshooting. Use GetContainerLog, InvokeScript, or Open to investigate. Remove the container when done.'; while ($true) { Start-Sleep -Seconds 3600 }" },
                                Ports = new List<V1ContainerPort>
                                {
                                    new() { ContainerPort = 80 }, new() { ContainerPort = 443 }, new() { ContainerPort = 7047 }, new() { ContainerPort = 7048 }, new() { ContainerPort = 7049 },
                                },
                                Env = BuildEnvVars(adminUsername, secretName, publicDnsName, databaseName, multitenant, licenseFileUrl, authenticationEmail, aadAppClientId, aadAuthIsMultitenant, await GenerateContainerBlobSasUrlAsync(appName)),
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new ResourceQuantity(cpuRequest),
                                        ["memory"] = new ResourceQuantity(memoryRequest)
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        await client.CreateNamespacedDeploymentAsync(deployment, Namespace);
    }

    private List<V1EnvVar> BuildEnvVars(string adminUsername, string secretName, string publicDnsName, string databaseName, bool multitenant, string? licenseFileUrl, string? authenticationEmail, string? aadAppClientId, bool aadAuthIsMultitenant, string encryptionKeyBlobSasUrl)
    {
        var envVars = new List<V1EnvVar>
        {
            new() { Name = "accept_eula", Value = "Y" },
            new() { Name = "username", Value = adminUsername },
            new()
            {
                Name = "password",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = secretName, Key = "password" }
                }
            },
            new() { Name = "publicDnsName", Value = publicDnsName },
            new() { Name = "contactEMailForLetsEncrypt", Value = ContactEmail },
            new() { Name = 'enableApiServices', Value = "Y" },
            new() { Name = "folders", Value = FoldersValue },
            new()
            {
                Name = "databasePassword",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = "mssql-secret", Key = "sa-password" }
                }
            },
            new() { Name = "databaseUsername", Value = "sa" },
            new() { Name = "databaseServer", Value = "mssql-service" },
            new() { Name = "databaseInstance", Value = "" },
            new() { Name = "databaseName", Value = databaseName },
            new()
            {
                Name = "encryptionPassword",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = "encryption-secret", Key = "encryptionPassword" }
                }
            },
            new()
            {
                Name = "pfxCertificatePassword",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = "encryption-secret", Key = "encryptionPassword" }
                }
            },
            new() { Name = "ContainerBlobContainer", Value = encryptionKeyBlobSasUrl },
        };

        if (!string.IsNullOrWhiteSpace(licenseFileUrl))
        {
            envVars.Add(new V1EnvVar { Name = "licensefile", Value = licenseFileUrl });
        }

        if (multitenant)
        {
            envVars.Add(new V1EnvVar { Name = "multitenant", Value = "Y" });
        }

        if (aadAuthIsMultitenant)
        {
            envVars.Add(new V1EnvVar { Name = "aad_app_is_multitenant", Value = "Y" });
        }

        if (!string.IsNullOrWhiteSpace(aadAppClientId))
        {
            var appIdUri = $"api://{aadAppClientId}";
            var redirectUri = $"https://{publicDnsName}/BC/SignIn";
            envVars.Add(new V1EnvVar { Name = "authenticationEMail", Value = authenticationEmail! });
            envVars.Add(new V1EnvVar { Name = "aadAppId", Value = aadAppClientId });
            envVars.Add(new V1EnvVar { Name = "appIdUri", Value = appIdUri });
            envVars.Add(new V1EnvVar { Name = "auth", Value = "AAD" });
            envVars.Add(new V1EnvVar { Name = "aadtenant", Value = AadTenantId });
            envVars.Add(new V1EnvVar { Name = "federationloginendpoint", Value = $"https://login.microsoftonline.com/{AadTenantId}/wsfed?wa=wsignin1.0%26wtrealm={Uri.EscapeDataString(appIdUri)}%26wreply={Uri.EscapeDataString(redirectUri)}" });
        }

        return envVars;
    }

    private async Task<(string ObjectId, string ClientId)> CreateAadAppRegistrationAsync(string appName, string redirectUri, bool multitenant)
    {
        var signInAudience = multitenant ? "AzureADMultipleOrgs" : "AzureADMyOrg";
        Logger.LogInformation("Creating AAD App Registration for container '{AppName}' with redirect URI: {RedirectUri} (signInAudience: {Audience})", appName, redirectUri, signInAudience);

        var graphClient = new GraphServiceClient(CreateGraphCredential());

        var app = await graphClient.Applications.PostAsync(new Application
        {
            DisplayName = $"{AadAppNamePrefix}fkh-{appName}-auth",
            SignInAudience = signInAudience,
            Web = new WebApplication
            {
                RedirectUris = new List<string> { redirectUri },
                ImplicitGrantSettings = new ImplicitGrantSettings
                {
                    EnableIdTokenIssuance = true
                }
            },
            RequiredResourceAccess = new List<RequiredResourceAccess>
            {
                // Dynamics 365 Business Central — API.ReadWrite.All (Application)
//                new RequiredResourceAccess
//                {
//                    ResourceAppId = "996def3d-b36c-4153-8607-a6fd3c01b89f",
//                    ResourceAccess = new List<ResourceAccess>
//                    {
//                        new ResourceAccess { Id = Guid.Parse("a42b0b75-311e-488d-b67e-8fe84f924341"), Type = "Role" }
//                    }
//                },
                // Microsoft Graph — EWS.AccessAsUser.All + User.Read (Delegated)
                new RequiredResourceAccess
                {
                    ResourceAppId = "00000003-0000-0000-c000-000000000000",
                    ResourceAccess = new List<ResourceAccess>
                    {
//                        new ResourceAccess { Id = Guid.Parse("9769c687-087d-48ac-9cb3-c37dde652038"), Type = "Scope" },
                        new ResourceAccess { Id = Guid.Parse("e1fe6dd8-ba31-4d61-89e7-88639da4683d"), Type = "Scope" }
                    }
                }
            }
        }) ?? throw new InvalidOperationException("Failed to create AAD App Registration — Graph API returned null.");

        // Set Application ID URI to api://<appId>
        // Microsoft Graph has eventual consistency for newly created applications,
        // so a PATCH right after POST can fail with "Resource '<id>' does not exist".
        // Retry with backoff until the app becomes available.
        var patchBody = new Application
        {
            IdentifierUris = new List<string> { $"api://{app.AppId}" }
        };
        const int maxAttempts = 10;
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await graphClient.Applications[app.Id].PatchAsync(patchBody);
                break;
            }
            catch (ODataError ex) when (attempt < maxAttempts &&
                (ex.ResponseStatusCode == 404 ||
                 (ex.Error?.Message?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                Logger.LogInformation("AAD App Registration not yet replicated (attempt {Attempt}/{Max}); retrying in {Delay}s...", attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }

        // Add additional human owner if configured
        if (!string.IsNullOrWhiteSpace(AadAppAdditionalOwner))
        {
            try
            {
                await graphClient.Applications[app.Id].Owners.Ref.PostAsync(new Microsoft.Graph.Models.ReferenceCreate
                {
                    OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{AadAppAdditionalOwner}"
                });
                Logger.LogInformation("Added additional owner {OwnerId} to AAD App Registration {AppName}", AadAppAdditionalOwner, app.DisplayName);
            }
            catch (ODataError ex)
            {
                Logger.LogWarning(ex, "Failed to add additional owner {OwnerId} to AAD App Registration {AppName}: {Message}", AadAppAdditionalOwner, app.DisplayName, ex.Error?.Message);
            }
        }

        Logger.LogInformation("AAD App Registration created: {DisplayName} (appId: {AppId}, objectId: {ObjectId})", app.DisplayName, app.AppId, app.Id);
        return (app.Id!, app.AppId!);
    }

    private async Task CreateLoadBalancerServiceAsync(Kubernetes client, string serviceName, string appName, string dnsLabel)
    {
        var service = new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = serviceName,
                NamespaceProperty = Namespace,
                Annotations = new Dictionary<string, string>
                {
                    ["service.beta.kubernetes.io/azure-dns-label-name"] = dnsLabel,
                    ["service.beta.kubernetes.io/azure-load-balancer-health-probe-protocol"] = "tcp"
                }
            },
            Spec = new V1ServiceSpec
            {
                Type = "LoadBalancer",
                Selector = new Dictionary<string, string> { ["app"] = appName },
                Ports = new List<V1ServicePort>
                {
                    new() { Name = "http", Port = 80, TargetPort = 80 },
                    new() { Name = "https", Port = 443, TargetPort = 443 },
                    new() { Name = "soap", Port = 7047, TargetPort = 7047 },
                    new() { Name = "odata", Port = 7048, TargetPort = 7048 },
                    new() { Name = "dev", Port = 7049, TargetPort = 7049 },
                }
            }
        };

        await client.CreateNamespacedServiceAsync(service, Namespace);
    }
}
