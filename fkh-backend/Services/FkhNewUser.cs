using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhNewUser : FkhServiceBase
{
    public FkhNewUser(ILogger<FkhNewUser> logger) : base(logger) { }

    public async Task<object> NewUserAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var tenant = parameters.TryGetValue("tenant", out var t) ? t : "default";
        var username = parameters["username"];
        var permissions = parameters["permissions"];

        Logger.LogInformation(
            "User '{User}' creating new user '{NewUser}' in container '{Container}' (tenant={Tenant}).",
            githubUsername, username, appName, tenant);

        var client = await GetKubernetesClientAsync();

        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{appName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var containerName = pod.Spec.Containers[0].Name;

        // Escape single quotes in parameters for safe embedding in PowerShell script
        var escapedTenant = tenant.Replace("'", "''");
        var escapedUsername = username.Replace("'", "''");

        // Build optional parameters for New-NAVServerUser
        var optionalParams = "";
        if (parameters.TryGetValue("fullName", out var fullName) && !string.IsNullOrEmpty(fullName))
            optionalParams += $" -FullName '{fullName.Replace("'", "''")}'";
        if (parameters.TryGetValue("licenseType", out var licenseType) && !string.IsNullOrEmpty(licenseType))
            optionalParams += $" -LicenseType {licenseType.Replace("'", "''")}";
        if (parameters.TryGetValue("company", out var company) && !string.IsNullOrEmpty(company))
            optionalParams += $" -Company '{company.Replace("'", "''")}'";
        if (parameters.TryGetValue("profileID", out var profileID) && !string.IsNullOrEmpty(profileID))
            optionalParams += $" -ProfileId '{profileID.Replace("'", "''")}'";
        if (parameters.TryGetValue("authenticationEmail", out var authEmail) && !string.IsNullOrEmpty(authEmail))
            optionalParams += $" -AuthenticationEmail '{authEmail.Replace("'", "''")}'";

        // Build permission assignment script
        var permissionSetIds = permissions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var permScript = "";
        foreach (var permSetId in permissionSetIds)
        {
            var escapedPermSetId = permSetId.Replace("'", "''");
            permScript += $"\nNew-NAVServerUserPermissionSet -ServerInstance BC -Tenant '{escapedTenant}' -Username '{escapedUsername}' -PermissionSetId '{escapedPermSetId}'";
        }

        var script = $@"
$ErrorActionPreference = 'Stop'
. 'c:\run\prompt.ps1'
New-NAVServerUser -ServerInstance BC -Tenant '{escapedTenant}' -UserName '{escapedUsername}'{optionalParams} -State Enabled
{permScript}
Write-Output 'User created successfully.'
";

        var result = await ExecInBcPodAsync(client, podName, containerName, script);

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            throw new InvalidOperationException($"Failed to create user in container '{appName}':\n{result.Stderr.TrimEnd()}");
        }

        return new
        {
            Container = appName,
            Tenant = tenant,
            Username = username,
            Permissions = permissionSetIds,
            Message = $"User '{username}' created successfully."
        };
    }

}
