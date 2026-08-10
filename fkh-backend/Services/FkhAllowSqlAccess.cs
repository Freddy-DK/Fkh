using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhAllowSqlAccess : FkhServiceBase
{
    public const string ServicePrefix = "mssql-ext-";
    public const string PolicyPrefix = "mssql-allow-ip-";
    public const string AutoRevokeAnnotation = "fkh/sql-access-revoke-at";

    public FkhAllowSqlAccess(ILogger<FkhAllowSqlAccess> logger) : base(logger) { }

    public async Task<object> AllowSqlAccessAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        if (!parameters.TryGetValue("ip", out var ip) || string.IsNullOrWhiteSpace(ip))
            throw new ArgumentException("Parameter 'ip' is required.");
        var hours = parameters.TryGetValue("hours", out var h) && double.TryParse(h, out var parsed) && parsed > 0
            ? parsed
            : 2;

        var sanitizedUser = SanitizeAppName(githubUsername);
        var serviceName = $"{ServicePrefix}{sanitizedUser}";
        var policyName = $"{PolicyPrefix}{sanitizedUser}";
        var cidr = ip.Contains('/') ? ip : $"{ip}/32";
        var revokeAt = DateTimeOffset.UtcNow.AddHours(hours);

        Logger.LogInformation(
            "Allowing SQL access for user '{User}' from {Cidr} for {Hours}h (until {RevokeAt} UTC).",
            githubUsername, cidr, hours, revokeAt);

        var client = await GetKubernetesClientAsync();

        // ── Create or update LoadBalancer service ─────────────────────────────────
        var service = new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = serviceName,
                NamespaceProperty = Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = "mssql",
                    ["fkh/purpose"] = "sql-external-access",
                    ["fkh/owner"] = sanitizedUser,
                },
                Annotations = new Dictionary<string, string>
                {
                    [AutoRevokeAnnotation] = revokeAt.UtcDateTime.ToString("o"),
                },
            },
            Spec = new V1ServiceSpec
            {
                Type = "LoadBalancer",
                ExternalTrafficPolicy = "Local",
                LoadBalancerSourceRanges = new List<string> { cidr },
                Selector = new Dictionary<string, string> { ["app"] = "mssql" },
                Ports = new List<V1ServicePort>
                {
                    new() { Protocol = "TCP", Port = 1433, TargetPort = 1433 },
                },
            },
        };

        try
        {
            await client.ReadNamespacedServiceAsync(serviceName, Namespace);
            await client.ReplaceNamespacedServiceAsync(service, serviceName, Namespace);
            Logger.LogInformation("Updated existing SQL access service '{Service}'.", serviceName);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.CreateNamespacedServiceAsync(service, Namespace);
            Logger.LogInformation("Created SQL access service '{Service}'.", serviceName);
        }

        // ── Create or update NetworkPolicy ────────────────────────────────────────
        var policy = new V1NetworkPolicy
        {
            Metadata = new V1ObjectMeta
            {
                Name = policyName,
                NamespaceProperty = Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["fkh/purpose"] = "sql-external-access",
                    ["fkh/owner"] = sanitizedUser,
                },
                Annotations = new Dictionary<string, string>
                {
                    [AutoRevokeAnnotation] = revokeAt.UtcDateTime.ToString("o"),
                },
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { ["app"] = "mssql" },
                },
                PolicyTypes = new List<string> { "Ingress" },
                Ingress = new List<V1NetworkPolicyIngressRule>
                {
                    new()
                    {
                        FromProperty = new List<V1NetworkPolicyPeer>
                        {
                            new() { IpBlock = new V1IPBlock { Cidr = cidr } },
                        },
                        Ports = new List<V1NetworkPolicyPort>
                        {
                            new() { Protocol = "TCP", Port = 1433 },
                        },
                    },
                },
            },
        };

        try
        {
            await client.ReadNamespacedNetworkPolicyAsync(policyName, Namespace);
            await client.ReplaceNamespacedNetworkPolicyAsync(policy, policyName, Namespace);
            Logger.LogInformation("Updated existing SQL access network policy '{Policy}'.", policyName);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.CreateNamespacedNetworkPolicyAsync(policy, Namespace);
            Logger.LogInformation("Created SQL access network policy '{Policy}'.", policyName);
        }

        // ── Wait for external IP assignment ───────────────────────────────────────
        string? externalIp = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var svc = await client.ReadNamespacedServiceAsync(serviceName, Namespace);
            var ingress = svc.Status?.LoadBalancer?.Ingress?.FirstOrDefault();
            if (ingress is not null)
            {
                externalIp = ingress.Ip ?? ingress.Hostname;
                break;
            }
        }

        var endpoint = externalIp is not null ? $"{externalIp},1433" : "(pending — check service status)";

        // ── Create SQL login and database users if mySqlPassword is set ───────────
        var sqlLoginInfo = "";
        var sqlLog = new List<string>();
        if (parameters.TryGetValue("mySqlPassword", out var mySqlPassword) && !string.IsNullOrWhiteSpace(mySqlPassword))
        {
            var sqlLogin = sanitizedUser.Replace("'", "''");
            var escapedPassword = mySqlPassword.Replace("'", "''");

            var podName = await FindMssqlPodAsync(client);
            sqlLog.Add($"Pod: {podName}");

            // Find all databases matching <username>-*
            var listDbScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -h -1 -W " +
                $"-Q \"SELECT name FROM sys.databases WHERE name LIKE '{sqlLogin}-%'\"";
            var dbResult = await ExecInMssqlPodAsync(client, podName, listDbScript);
            sqlLog.Add($"ListDBs: {dbResult}");
            var databases = dbResult.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l != "name" && !l.StartsWith("---") && !l.StartsWith("("))
                .ToList();
            sqlLog.Add($"Matched DBs: [{string.Join(", ", databases)}]");

            // Drop database users first (login can't be dropped while users exist)
            foreach (var db in databases)
            {
                var safeDb = db.Replace("'", "''");
                await ExecInMssqlPodAsync(client, podName,
                    $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -Q " +
                    $"\"USE [{safeDb}]; DROP USER [{sqlLogin}]\" 2>/dev/null || true");
            }

            // Drop login (now safe — no users reference it)
            await ExecInMssqlPodAsync(client, podName,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -Q " +
                $"\"DROP LOGIN [{sqlLogin}]\" 2>/dev/null || true");

            // Create login fresh
            var createResult = await ExecInMssqlPodAsync(client, podName,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q " +
                $"\"CREATE LOGIN [{sqlLogin}] WITH PASSWORD = '{escapedPassword}', CHECK_POLICY = OFF\"");
            sqlLog.Add($"CreateLogin: {createResult}");

            // Grant server-level permissions
            var grantResult = await ExecInMssqlPodAsync(client, podName,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q " +
                $"\"GRANT VIEW ANY DATABASE TO [{sqlLogin}]\"");
            sqlLog.Add($"Grant: {grantResult}");

            // Re-create database users with db_owner
            foreach (var db in databases)
            {
                var safeDb = db.Replace("'", "''");
                var result = await ExecInMssqlPodAsync(client, podName,
                    $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q " +
                    $"\"USE [{safeDb}]; CREATE USER [{sqlLogin}] FOR LOGIN [{sqlLogin}]; ALTER ROLE [db_owner] ADD MEMBER [{sqlLogin}]\"");
                sqlLog.Add($"CreateUser[{db}]: {result}");
            }

            sqlLoginInfo = databases.Count > 0
                ? $"{sqlLogin} (db_owner on {string.Join(", ", databases)})"
                : $"{sqlLogin} (no databases matching '{sqlLogin}_*' found)";
        }

        return new
        {
            User = githubUsername,
            AllowedIp = cidr,
            SqlEndpoint = endpoint,
            AutoRevoke = $"{revokeAt:yyyy-MM-dd HH:mm} UTC ({hours}h)",
            SqlLogin = string.IsNullOrEmpty(sqlLoginInfo) ? null : sqlLoginInfo,
            Diagnostics = sqlLog.Count > 0 ? sqlLog : null,
        };
    }

    public async Task<object> RevokeSqlAccessAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var sanitizedUser = SanitizeAppName(githubUsername);

        return await RevokeForUserAsync(sanitizedUser, githubUsername);
    }

    public async Task<object> RevokeForUserAsync(string sanitizedUser, string displayName)
    {
        var serviceName = $"{ServicePrefix}{sanitizedUser}";
        var policyName = $"{PolicyPrefix}{sanitizedUser}";

        Logger.LogInformation("Revoking SQL access for user '{User}'.", displayName);
        var client = await GetKubernetesClientAsync();

        var removed = new List<string>();

        // ── Drop the SQL login (cascades to all database users) ───────────────────
        try
        {
            var sqlLogin = sanitizedUser.Replace("'", "''");
            var podName = await FindMssqlPodAsync(client);

            // Drop database users first (login can't be dropped while users exist)
            var listDbScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -h -1 -W " +
                $"-Q \"SELECT name FROM sys.databases WHERE name LIKE '{sqlLogin}-%'\"";
            var dbResult = await ExecInMssqlPodAsync(client, podName, listDbScript);
            var databases = dbResult.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l != "name" && !l.StartsWith("---") && !l.StartsWith("("))
                .ToList();

            foreach (var db in databases)
            {
                var safeDb = db.Replace("'", "''");
                await ExecInMssqlPodAsync(client, podName,
                    $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -Q " +
                    $"\"USE [{safeDb}]; DROP USER [{sqlLogin}]\" 2>/dev/null || true");
            }

            await ExecInMssqlPodAsync(client, podName,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -Q " +
                $"\"DROP LOGIN [{sqlLogin}]\" 2>/dev/null || true");
            removed.Add($"SQL login '{sqlLogin}'");
            Logger.LogInformation("Dropped SQL login '{Login}'.", sqlLogin);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to drop SQL login for '{User}' (may not exist).", displayName);
        }

        try
        {
            await client.DeleteNamespacedServiceAsync(serviceName, Namespace);
            removed.Add($"Service '{serviceName}'");
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone
        }

        try
        {
            await client.DeleteNamespacedNetworkPolicyAsync(policyName, Namespace);
            removed.Add($"NetworkPolicy '{policyName}'");
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone
        }

        if (removed.Count == 0)
        {
            return new { User = displayName, Message = "No SQL access resources found.", Removed = Array.Empty<string>() };
        }

        Logger.LogInformation("Revoked SQL access for user '{User}': {Resources}", displayName, string.Join(", ", removed));
        return new { User = displayName, Removed = removed };
    }

    public async Task CheckAndRevokeExpiredAccessAsync()
    {
        Logger.LogInformation("Checking for expired SQL access grants...");
        var client = await GetKubernetesClientAsync();

        var services = await client.ListNamespacedServiceAsync(Namespace, labelSelector: "fkh/purpose=sql-external-access");
        var revoked = 0;

        foreach (var svc in services.Items)
        {
            if (svc.Metadata.Annotations == null ||
                !svc.Metadata.Annotations.TryGetValue(AutoRevokeAnnotation, out var revokeAtStr))
                continue;

            if (!DateTimeOffset.TryParse(revokeAtStr, out var revokeAt))
            {
                Logger.LogWarning("Invalid revoke annotation '{Value}' on service '{Service}'.", revokeAtStr, svc.Metadata.Name);
                continue;
            }

            if (DateTimeOffset.UtcNow >= revokeAt)
            {
                var owner = svc.Metadata.Labels != null && svc.Metadata.Labels.TryGetValue("fkh/owner", out var o) ? o : "unknown";
                Logger.LogInformation("Auto-revoking expired SQL access for '{Owner}'.", owner);
                await RevokeForUserAsync(owner, owner);
                revoked++;
            }
        }

        Logger.LogInformation("SQL access revoke check complete. Revoked {Count} grant(s).", revoked);
    }
}
