using Fkh.Models;
using k8s;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Fkh.Services;

public class FkhInvokeScript : FkhServiceBase
{
    public FkhInvokeScript(ILogger<FkhInvokeScript> logger) : base(logger) { }

    /// <summary>
    /// Invokes a script when called with --command (no file upload, regular JSON body).
    /// </summary>
    public async Task<object> InvokeScriptAsync(Dictionary<string, string> parameters)
    {
        var script = parameters.TryGetValue("command", out var cmd) ? cmd : null;
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new InvalidOperationException("Either --command or --file must be provided.");
        }

        return await RunScriptInContainerAsync(parameters, script);
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

        return await RunScriptInContainerAsync(parameters, script);
    }

    private async Task<object> RunScriptInContainerAsync(Dictionary<string, string> parameters, string script)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var scriptParams = parameters.TryGetValue("scriptParams", out var sp) ? sp : "";

        Logger.LogInformation(
            "User '{User}' invoking script in container '{Container}'.",
            githubUsername, appName);

        var client = await GetKubernetesClientAsync();

        // Find the BC pod for this container
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{appName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var containerName = pod.Spec.Containers[0].Name;

        var result = await RunDetachedInBcPodAsync(
            client, podName, containerName,
            jobPrefix: "fkh",
            jobIdInput: $"{appName}|{script}|{scriptParams}",
            script: script,
            scriptParams: scriptParams,
            retryAfterSeconds: 5,
            retryMessage: "Script still running...");

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            var message = string.IsNullOrWhiteSpace(result.Stdout)
                ? $"Script failed in container '{appName}':\n{result.Stderr}"
                : $"Script failed in container '{appName}':\n{result.Stderr}\n\nOutput:\n{result.Stdout}";
            throw new InvalidOperationException(message);
        }

        return new
        {
            Container = appName,
            Output = result.Stdout,
        };
    }
}
