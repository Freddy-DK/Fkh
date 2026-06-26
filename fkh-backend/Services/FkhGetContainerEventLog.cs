using k8s;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Fkh.Services;

public class FkhGetContainerEventLog : FkhServiceBase
{
    public FkhGetContainerEventLog(ILogger<FkhGetContainerEventLog> logger) : base(logger) { }

    public async Task<object> GetContainerEventLogAsync(Dictionary<string, string> parameters)
    {
        var name = parameters.TryGetValue("name", out var n) ? n : null;
        var githubUsername = parameters["_githubUsername"];
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminValue)
            && string.Equals(adminValue, "true", StringComparison.OrdinalIgnoreCase);

        var appName = ResolveAppName(parameters);

        var client = await GetKubernetesClientAsync();
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");

        if (pods.Items.Count == 0 && isAdmin && !string.IsNullOrWhiteSpace(name))
        {
            var adminAppName = SanitizeAppName(name);
            pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={adminAppName}");
        }

        if (pods.Items.Count == 0)
        {
            return new { EventLog = (string?)null, Message = $"No container found for '{name}'." };
        }

        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"Container '{name}' is not running. Start it first.");

        var podName = pod.Metadata.Name;
        var containerName = pod.Spec.Containers[0].Name;

        Logger.LogInformation(
            "User '{User}' downloading event log from container '{Container}'.",
            githubUsername, appName);

        // Export the Windows Application event log to a temp file, read as base64, then clean up
        var script = @"
$tempFile = Join-Path $env:TEMP ('eventlog_' + [guid]::NewGuid().ToString('N') + '.evtx')
try {
    wevtutil epl Application $tempFile /ow:true 2>&1 | Out-Null
    if (-not (Test-Path $tempFile)) {
        throw 'Failed to export event log.'
    }
    $bytes = [System.IO.File]::ReadAllBytes($tempFile)
    [System.Convert]::ToBase64String($bytes)
} finally {
    if (Test-Path $tempFile) { Remove-Item $tempFile -Force -ErrorAction SilentlyContinue }
}
";

        var result = await ExecInBcPodAsync(client, podName, containerName, script);

        var base64Content = result.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(base64Content))
        {
            var errorMsg = string.IsNullOrWhiteSpace(result.Stderr)
                ? "Event log export returned empty content."
                : result.Stderr.Trim();
            throw new InvalidOperationException($"Failed to export event log: {errorMsg}");
        }

        return new
        {
            EventLog = base64Content,
            Container = appName,
            FileName = $"{appName}-eventlog.evtx"
        };
    }
}
