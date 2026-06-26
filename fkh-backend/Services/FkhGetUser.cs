using k8s;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Fkh.Services;

public class FkhGetUser : FkhServiceBase
{
    public FkhGetUser(ILogger<FkhGetUser> logger) : base(logger) { }

    public async Task<object> GetUserAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var tenant = parameters.TryGetValue("tenant", out var t) ? t : "default";
        var filterUsername = parameters.TryGetValue("username", out var u) ? u : null;

        Logger.LogInformation(
            "User '{User}' getting user info from container '{Container}' (tenant={Tenant}, username={Username}).",
            githubUsername, appName, tenant, filterUsername ?? "(all)");

        var client = await GetKubernetesClientAsync();

        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{appName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var containerName = pod.Spec.Containers[0].Name;

        // Escape single quotes in parameters for safe embedding in PowerShell script
        var escapedTenant = tenant.Replace("'", "''");

        var userFilter = "";
        if (!string.IsNullOrEmpty(filterUsername))
        {
            var escapedUsername = filterUsername.Replace("'", "''");
            userFilter = $" | Where-Object {{ $_.UserName -eq '{escapedUsername}' }}";
        }

        var script = $@"
$ErrorActionPreference = 'Stop'
. 'c:\run\prompt.ps1'
$users = @(Get-NAVServerUser -ServerInstance BC -Tenant '{escapedTenant}'{userFilter})
$result = @()
foreach ($user in $users) {{
    $permSets = @(Get-NAVServerUserPermissionSet -ServerInstance BC -Tenant '{escapedTenant}' -Username $user.UserName |
        Select-Object PermissionSetID, CompanyName, Scope, AppID, PermissionSetName, AppName)
    $result += @{{
        UserName = $user.UserName
        FullName = $user.FullName
        State = [string]$user.State
        LicenseType = [string]$user.LicenseType
        AuthenticationEmail = $user.AuthenticationEmail
        ApplicationID = $user.ApplicationID.ToString()
        ProfileID = $user.ProfileID
        Company = $user.Company
        LanguageID = $user.LanguageID
        PermissionSets = $permSets
    }}
}}
ConvertTo-Json -InputObject $result -Depth 5
";

        var result = await ExecInBcPodAsync(client, podName, containerName, script);

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            throw new InvalidOperationException($"Failed to get user info from container '{appName}':\n{result.Stderr.TrimEnd()}");
        }

        // Parse the JSON array output
        var jsonStart = result.Stdout.IndexOf('[');
        var jsonStartObj = result.Stdout.IndexOf('{');
        if (jsonStart < 0 || (jsonStartObj >= 0 && jsonStartObj < jsonStart))
            jsonStart = jsonStartObj;

        if (jsonStart < 0)
        {
            return new
            {
                Container = appName,
                Tenant = tenant,
                Users = Array.Empty<object>()
            };
        }

        var jsonText = result.Stdout[jsonStart..].TrimEnd();
        using var doc = JsonDocument.Parse(jsonText);
        var users = new List<object>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
                users.Add(JsonSerializer.Deserialize<object>(item.GetRawText())!);
        }
        else
        {
            users.Add(JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText())!);
        }

        return new
        {
            Container = appName,
            Tenant = tenant,
            Users = users.ToArray()
        };
    }

}
