using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhDismountTenant : FkhServiceBase
{
    public FkhDismountTenant(ILogger<FkhDismountTenant> logger) : base(logger) { }

    public async Task<object> DismountTenantAsync(Dictionary<string, string> parameters)
    {
        var containerName = ResolveAppName(parameters);
        var tenant = parameters["tenant"];
        var doNotRemoveDatabase = parameters.TryGetValue("doNotRemoveDatabase", out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        var client = await GetKubernetesClientAsync();

        // Find the BC pod for this container
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={containerName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{containerName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var bcContainerName = pod.Spec.Containers[0].Name;

        // Dismount the tenant from Business Central
        Logger.LogInformation("Dismounting tenant '{Tenant}' from container '{Container}'...", tenant, containerName);
        var dismountScript = $". 'C:\\run\\prompt.ps1' -silent; Dismount-NAVTenant -ServerInstance $ServerInstance -Tenant '{tenant}' -Force";
        var dismountResult = await ExecInBcPodAsync(client, podName, bcContainerName, dismountScript);

        if (!string.IsNullOrWhiteSpace(dismountResult.Stderr))
        {
            // Dismount-NAVTenant may throw but still succeed — verify the tenant state
            var verifyScript = $". 'C:\\run\\prompt.ps1' -silent; (Get-NAVTenant -ServerInstance $ServerInstance -Tenant '{tenant}').State";
            var verifyResult = await ExecInBcPodAsync(client, podName, bcContainerName, verifyScript);

            if (!string.Equals(verifyResult.Stdout.Trim(), "Dismounted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Failed to dismount tenant '{tenant}' from container '{containerName}': {dismountResult.Stderr}");
        }

        Logger.LogInformation("Tenant '{Tenant}' dismounted from container '{Container}'.", tenant, containerName);

        if (!doNotRemoveDatabase)
        {
            // Remove the tenant database from SQL Server
            var databaseName = $"{containerName}-{tenant}";
            Logger.LogInformation("Removing database '{Database}'...", databaseName);

            var mssqlPod = await FindMssqlPodAsync(client);
            var dropSql = $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
            var dropScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{dropSql}\" && echo 'DROP_COMPLETE'";
            var dropResult = await ExecInMssqlPodAsync(client, mssqlPod, dropScript);

            if (!dropResult.Stdout.Contains("DROP_COMPLETE"))
                throw new InvalidOperationException($"Failed to remove database '{databaseName}'. {dropResult}");

            Logger.LogInformation("Database '{Database}' removed.", databaseName);
            return new { message = $"Tenant '{tenant}' dismounted and database '{databaseName}' removed.", containerName, tenant, databaseName };
        }

        return new { message = $"Tenant '{tenant}' dismounted from container '{containerName}'. Database was kept.", containerName, tenant };
    }
}
