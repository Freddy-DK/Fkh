using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhAllowWinRmAccess : FkhServiceBase
{
    public const string ServicePrefix = "winrm-ext-";
    public const string PolicyPrefix = "winrm-allow-ip-";
    public const string AutoRevokeAnnotation = "fkh/winrm-access-revoke-at";
    public const string PurposeLabel = "winrm-external-access";
    private const int WinRmPort = 5986;

    public FkhAllowWinRmAccess(ILogger<FkhAllowWinRmAccess> logger) : base(logger) { }

    private static string BuildResourceName(string prefix, string sanitizedUser, string appName)
    {
        var name = $"{prefix}{sanitizedUser}-{appName}";
        if (name.Length > 63) name = name[..63];
        return name.TrimEnd('-');
    }

    public async Task<object> AllowWinRmAccessAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var ip = parameters["ip"];
        var hours = parameters.TryGetValue("hours", out var h) && double.TryParse(h, out var parsed) && parsed > 0
            ? parsed
            : 2;

        var sanitizedUser = SanitizeAppName(githubUsername);
        var serviceName = BuildResourceName(ServicePrefix, sanitizedUser, appName);
        var policyName = BuildResourceName(PolicyPrefix, sanitizedUser, appName);
        var cidr = ip.Contains('/') ? ip : $"{ip}/32";
        var revokeAt = DateTimeOffset.UtcNow.AddHours(hours);

        Logger.LogInformation(
            "Allowing WinRM access for user '{User}' to container '{App}' from {Cidr} for {Hours}h (until {RevokeAt} UTC).",
            githubUsername, appName, cidr, hours, revokeAt);

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
                    ["app"] = appName,
                    ["fkh/purpose"] = PurposeLabel,
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
                Selector = new Dictionary<string, string> { ["app"] = appName },
                Ports = new List<V1ServicePort>
                {
                    new() { Protocol = "TCP", Port = WinRmPort, TargetPort = WinRmPort },
                },
            },
        };

        try
        {
            await client.ReadNamespacedServiceAsync(serviceName, Namespace);
            await client.ReplaceNamespacedServiceAsync(service, serviceName, Namespace);
            Logger.LogInformation("Updated existing WinRM access service '{Service}'.", serviceName);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.CreateNamespacedServiceAsync(service, Namespace);
            Logger.LogInformation("Created WinRM access service '{Service}'.", serviceName);
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
                    ["app"] = appName,
                    ["fkh/purpose"] = PurposeLabel,
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
                    MatchLabels = new Dictionary<string, string> { ["app"] = appName },
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
                            new() { Protocol = "TCP", Port = WinRmPort },
                        },
                    },
                },
            },
        };

        try
        {
            await client.ReadNamespacedNetworkPolicyAsync(policyName, Namespace);
            await client.ReplaceNamespacedNetworkPolicyAsync(policy, policyName, Namespace);
            Logger.LogInformation("Updated existing WinRM access network policy '{Policy}'.", policyName);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.CreateNamespacedNetworkPolicyAsync(policy, Namespace);
            Logger.LogInformation("Created WinRM access network policy '{Policy}'.", policyName);
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

        var endpoint = externalIp is not null ? $"{externalIp}:{WinRmPort}" : "(pending — check service status)";

        return new
        {
            User = githubUsername,
            Container = appName,
            AllowedIp = cidr,
            WinRmEndpoint = endpoint,
            AutoRevoke = $"{revokeAt:yyyy-MM-dd HH:mm} UTC ({hours}h)",
            Connect = externalIp is not null
                ? $"Enter-PSSession -ConnectionUri https://{externalIp}:{WinRmPort}/wsman " +
                  "-Credential (Get-Credential) -Authentication Basic " +
                  "-SessionOption (New-PSSessionOption -SkipCACheck -SkipCNCheck)"
                : null,
        };
    }

    public async Task<object> RevokeWinRmAccessAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var sanitizedUser = SanitizeAppName(githubUsername);

        string? appName = null;
        if (parameters.TryGetValue("name", out var nameVal) && !string.IsNullOrWhiteSpace(nameVal))
            appName = ResolveAppName(parameters);

        var scope = appName is null ? "all containers" : $"container '{appName}'";
        Logger.LogInformation("Revoking WinRM access for user '{User}' ({Scope}).", githubUsername, scope);

        var client = await GetKubernetesClientAsync();
        var removed = await RevokeResourcesAsync(client, sanitizedUser, appName);

        if (removed.Count == 0)
        {
            return new { User = githubUsername, Message = $"No WinRM access resources found for {scope}.", Removed = Array.Empty<string>() };
        }

        Logger.LogInformation("Revoked WinRM access for user '{User}' ({Scope}): {Resources}", githubUsername, scope, string.Join(", ", removed));
        return new { User = githubUsername, Scope = scope, Removed = removed };
    }

    private async Task<List<string>> RevokeResourcesAsync(Kubernetes client, string sanitizedUser, string? appName)
    {
        var selector = $"fkh/purpose={PurposeLabel},fkh/owner={sanitizedUser}";
        if (appName is not null) selector += $",app={appName}";

        var removed = new List<string>();

        var services = await client.ListNamespacedServiceAsync(Namespace, labelSelector: selector);
        foreach (var svc in services.Items)
        {
            await client.DeleteNamespacedServiceAsync(svc.Metadata.Name, Namespace);
            removed.Add($"Service '{svc.Metadata.Name}'");
        }

        var policies = await client.ListNamespacedNetworkPolicyAsync(Namespace, labelSelector: selector);
        foreach (var pol in policies.Items)
        {
            await client.DeleteNamespacedNetworkPolicyAsync(pol.Metadata.Name, Namespace);
            removed.Add($"NetworkPolicy '{pol.Metadata.Name}'");
        }

        return removed;
    }

    public async Task CheckAndRevokeExpiredAccessAsync()
    {
        Logger.LogInformation("Checking for expired WinRM access grants...");
        var client = await GetKubernetesClientAsync();

        var services = await client.ListNamespacedServiceAsync(Namespace, labelSelector: $"fkh/purpose={PurposeLabel}");
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
                var app = svc.Metadata.Labels != null && svc.Metadata.Labels.TryGetValue("app", out var a) ? a : null;
                Logger.LogInformation("Auto-revoking expired WinRM access for '{Owner}' (container '{App}').", owner, app ?? "?");
                await RevokeResourcesAsync(client, owner, app);
                revoked++;
            }
        }

        Logger.LogInformation("WinRM access revoke check complete. Revoked {Count} grant(s).", revoked);
    }
}
