using k8s;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fkh.Services;

public class FkhGetAppInfo : FkhServiceBase
{
    public FkhGetAppInfo(ILogger<FkhGetAppInfo> logger) : base(logger) { }

    public async Task<object> GetAppInfoAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var tenant = parameters.TryGetValue("tenant", out var t) ? t : "default";
        var filterAppName = parameters.TryGetValue("appName", out var an) ? an : null;
        var filterAppPublisher = parameters.TryGetValue("appPublisher", out var ap) ? ap : null;
        var filterAppId = parameters.TryGetValue("appId", out var ai) ? ai : null;
        var sortByDependencies = parameters.TryGetValue("sort", out var sort) && IsFlagSet(sort);

        if (filterAppId != null && (filterAppId.Contains('*') || filterAppId.Contains('?')))
        {
            throw new ArgumentException("Wildcards are not supported in the appId filter. Please specify an exact app ID (GUID).");
        }

        Logger.LogInformation(
            "User '{User}' getting app info from container '{Container}' (tenant={Tenant}).",
            githubUsername, appName, tenant);

        var client = await GetKubernetesClientAsync();

        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{appName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var containerName = pod.Spec.Containers[0].Name;

        var script = $@"
$ErrorActionPreference = 'Stop'
. 'c:\run\prompt.ps1'

$inArgs = @{{
    ServerInstance = 'bc'
    TenantSpecificProperties = $true
    Tenant = '{tenant}'
}}

$apps = @(Get-NAVAppInfo @inArgs |
    ForEach-Object {{
        $app = Get-NAVAppInfo -Id ""$($_.AppId)"" -Publisher $_.Publisher -Name $_.Name -Version $_.Version @inArgs

        [pscustomobject]@{{
            AppId = $app.AppId.Value.ToString()
            Name = $app.Name
            Publisher = $app.Publisher
            Version = $app.Version.ToString()
            Dependencies = [object[]]@($app.Dependencies | ForEach-Object {{
                $dependencyId = if ($_.Id) {{ $_.Id }} else {{ $_.AppId }}
                [pscustomobject]@{{
                    Id = if ($null -ne $dependencyId.Value) {{ $dependencyId.Value.ToString() }} else {{ $dependencyId.ToString() }}
                    Publisher = $_.Publisher
                    Name = $_.Name
                    Version = $_.MinVersion.ToString()
                }}
            }})
            ExtensionType = $app.ExtensionType.ToString()
            Scope = $app.Scope.ToString()
            IsInstalled = $app.IsInstalled
            IsPublished = $app.IsPublished
            SyncState = $app.SyncState.ToString()
            NeedsUpgrade = $app.NeedsUpgrade
        }}
    }})

ConvertTo-Json -InputObject $apps -Depth 10
";

        var result = await ExecInBcPodAsync(client, podName, containerName, script);

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            throw new InvalidOperationException($"Failed to get app info from container '{appName}':\n{result.Stderr.TrimEnd()}");
        }

        // Parse the JSON output
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
                Apps = Array.Empty<object>(),
                Message = "No apps found."
            };
        }

        var jsonText = result.Stdout[jsonStart..].TrimEnd();
        using var doc = JsonDocument.Parse(jsonText);
        var apps = new List<JsonElement>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
                apps.Add(item);
        }
        else
        {
            apps.Add(doc.RootElement);
        }

        // Apply client-side filters
        if (!string.IsNullOrEmpty(filterAppId))
        {
            apps = apps.Where(a =>
                a.TryGetProperty("AppId", out var id) &&
                string.Equals(id.GetString(), filterAppId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrEmpty(filterAppName))
        {
            var pattern = WildcardToRegex(filterAppName);
            apps = apps.Where(a =>
                a.TryGetProperty("Name", out var n) &&
                Regex.IsMatch(n.GetString() ?? "", pattern, RegexOptions.IgnoreCase)).ToList();
        }
        if (!string.IsNullOrEmpty(filterAppPublisher))
        {
            var pattern = WildcardToRegex(filterAppPublisher);
            apps = apps.Where(a =>
                a.TryGetProperty("Publisher", out var p) &&
                Regex.IsMatch(p.GetString() ?? "", pattern, RegexOptions.IgnoreCase)).ToList();
        }

        if (sortByDependencies)
        {
            apps = SortByDependencies(apps);
        }

        return new
        {
            Container = appName,
            Tenant = tenant,
            Apps = apps.Select(a => JsonSerializer.Deserialize<object>(a.GetRawText())).ToArray()
        };
    }

    private static bool IsFlagSet(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static List<JsonElement> SortByDependencies(List<JsonElement> apps)
    {
        var indexesByAppId = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < apps.Count; index++)
        {
            var appId = GetStringProperty(apps[index], "AppId");
            if (string.IsNullOrWhiteSpace(appId))
                continue;

            if (!indexesByAppId.TryGetValue(appId, out var indexes))
            {
                indexes = new List<int>();
                indexesByAppId[appId] = indexes;
            }

            indexes.Add(index);
        }

        var sorted = new List<JsonElement>(apps.Count);
        var visited = new bool[apps.Count];
        var visiting = new bool[apps.Count];

        for (var index = 0; index < apps.Count; index++)
        {
            Visit(index);
        }

        return sorted;

        void Visit(int index)
        {
            if (visited[index])
                return;

            if (visiting[index])
                return;

            visiting[index] = true;

            foreach (var dependencyId in GetDependencyIds(apps[index]))
            {
                if (!indexesByAppId.TryGetValue(dependencyId, out var dependencyIndexes))
                    continue;

                foreach (var dependencyIndex in dependencyIndexes)
                {
                    if (dependencyIndex != index)
                        Visit(dependencyIndex);
                }
            }

            visiting[index] = false;
            visited[index] = true;
            sorted.Add(apps[index]);
        }
    }

    private static IEnumerable<string> GetDependencyIds(JsonElement app)
    {
        if (!app.TryGetProperty("Dependencies", out var dependencies))
            yield break;

        if (dependencies.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var dependency in dependencies.EnumerateArray())
        {
            var dependencyId = GetStringProperty(dependency, "Id");
            if (!string.IsNullOrWhiteSpace(dependencyId))
                yield return dependencyId;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static string WildcardToRegex(string pattern)
    {
        return "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
    }

}
