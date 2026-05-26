using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhMountTenant : FkhServiceBase
{
    public FkhMountTenant(ILogger<FkhMountTenant> logger) : base(logger) { }

    public async Task<object> MountTenantAsync(Dictionary<string, string> parameters)
    {
        var containerName = ResolveAppName(parameters);
        var tenant = parameters["tenant"];
        var environmentType = parameters.TryGetValue("environmentType", out var et) && !string.IsNullOrWhiteSpace(et) ? et : null;

        var databaseName = $"{containerName}-{tenant}";

        var client = await GetKubernetesClientAsync();

        // Find the BC pod for this container
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={containerName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{containerName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var bcContainerName = pod.Spec.Containers[0].Name;

        // Read database credentials from the mssql-secret
        var secret = await client.ReadNamespacedSecretAsync("mssql-secret", Namespace);
        var saPassword = System.Text.Encoding.UTF8.GetString(secret.Data["sa-password"]);
        var saPasswordBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(saPassword));

        // Build the Mount-NAVTenant script
        var envTypeParam = environmentType != null ? $" -EnvironmentType '{environmentType}'" : "";
        var mountScript =
            $"$securePassword = ConvertTo-SecureString ([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{saPasswordBase64}'))) -AsPlainText -Force; " +
            "$databaseCredentials = New-Object System.Management.Automation.PSCredential('sa', $securePassword); " +
            $"Mount-NAVTenant -ServerInstance $ServerInstance -Tenant '{tenant}' " +
            $"-DatabaseName '{databaseName}' -DatabaseServer 'mssql-service' -DatabaseInstance '' " +
            $"-DatabaseCredentials $databaseCredentials " +
            $"-Force -AllowAppDatabaseWrite:$false -OverwriteTenantIdInDatabase{envTypeParam} -WarningAction SilentlyContinue";

        Logger.LogInformation("Mounting tenant '{Tenant}' with database '{Database}' in container '{Container}'...", tenant, databaseName, containerName);

        var result = await RunDetachedInBcPodAsync(
            client, podName, bcContainerName,
            jobPrefix: "fkh-mount",
            jobIdInput: $"{containerName}|{tenant}|{databaseName}",
            script: mountScript,
            retryAfterSeconds: 10,
            retryMessage: "Mounting tenant — still running...");

        if (!string.IsNullOrWhiteSpace(result.Stderr))
            throw new InvalidOperationException($"Failed to mount tenant '{tenant}' in container '{containerName}': {result.Stderr}");

        Logger.LogInformation("Tenant '{Tenant}' mounted successfully in container '{Container}'.", tenant, containerName);
        return new { message = $"Tenant '{tenant}' mounted with database '{databaseName}' in container '{containerName}'.", containerName, tenant, databaseName };
    }
}
