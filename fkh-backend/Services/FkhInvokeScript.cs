using Fkh.Models;
using k8s;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Fkh.Services;

public class FkhInvokeScript : FkhServiceBase
{
    public FkhInvokeScript(ILogger<FkhInvokeScript> logger) : base(logger) { }

    /// <summary>
    /// Launches a script (--command) detached in the container and returns a job id immediately.
    /// </summary>
    public async Task<object> InvokeScriptAsync(Dictionary<string, string> parameters)
    {
        var script = parameters.TryGetValue("command", out var cmd) ? cmd : null;
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new InvalidOperationException("Either --command or --file must be provided.");
        }

        return await LaunchScriptInContainerAsync(parameters, script);
    }

    /// <summary>
    /// Invokes a script when called with --file (multipart upload).
    /// Also handles --command when sent as multipart (files dict will be empty).
    /// </summary>
    public async Task<object> InvokeScriptWithFileAsync(Dictionary<string, string> parameters, Dictionary<string, byte[]> files)
    {
        string script;

        if (files.TryGetValue("scriptFile", out var fileBytes) && fileBytes.Length > 0)
        {
            script = Encoding.UTF8.GetString(fileBytes);
        }
        else if (parameters.TryGetValue("command", out var cmd) && !string.IsNullOrWhiteSpace(cmd))
        {
            script = cmd;
        }
        else
        {
            throw new InvalidOperationException("Either --command or --file must be provided.");
        }

        return await LaunchScriptInContainerAsync(parameters, script);
    }

    /// <summary>
    /// Launches the script detached in the container's pod and returns { jobId, container, status }.
    /// The caller polls ScriptStatus/ScriptResult to observe completion — the backend never blocks.
    /// </summary>
    private async Task<object> LaunchScriptInContainerAsync(Dictionary<string, string> parameters, string script)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var scriptParams = parameters.TryGetValue("scriptParams", out var sp) ? sp : "";

        Logger.LogInformation(
            "User '{User}' launching script job in container '{Container}'.",
            githubUsername, appName);

        var client = await GetKubernetesClientAsync();
        var (podName, containerName) = await FindBcPodAsync(client, appName);

        var jobId = NewDetachedJobId();
        await LaunchDetachedJobAsync(client, podName, containerName, jobId, script, scriptParams);

        Logger.LogInformation("Launched script job '{JobId}' in container '{Container}'.", jobId, appName);

        return new
        {
            JobId = jobId,
            Container = appName,
            Status = "Running",
        };
    }
}
