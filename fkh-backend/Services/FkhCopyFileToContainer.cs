using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhCopyFileToContainer : FkhServiceBase
{
    public FkhCopyFileToContainer(ILogger<FkhCopyFileToContainer> logger) : base(logger) { }

    public async Task<object> CopyFileToContainerAsync(Dictionary<string, string> parameters, Dictionary<string, byte[]> files)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);

        if (!parameters.TryGetValue("containerFilename", out var destPath) || string.IsNullOrWhiteSpace(destPath))
            throw new InvalidOperationException("Missing required parameter 'containerFilename'.");

        if (!files.TryGetValue("file", out var fileBytes) || fileBytes.Length == 0)
            throw new InvalidOperationException("No file was uploaded.");

        Logger.LogInformation(
            "User '{User}' uploading file to '{Dest}' in container '{Container}' ({Size} bytes).",
            githubUsername, destPath, appName, fileBytes.Length);

        var client = await GetKubernetesClientAsync();

        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{appName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var containerName = pod.Spec.Containers[0].Name;

        await CopyFileToPodAsync(client, podName, containerName, fileBytes, destPath);

        Logger.LogInformation("File uploaded to '{Dest}' in container '{Container}'.", destPath, appName);

        return new
        {
            Message = "File copied to container.",
            Container = appName,
            DestinationPath = destPath,
            Size = fileBytes.Length,
        };
    }

    private async Task CopyFileToPodAsync(Kubernetes client, string podName, string containerName, byte[] fileData, string destPath)
    {
        // Send the file to the pod via stdin as base64.
        // We use stdin instead of command-line arguments to avoid URI length limits
        // in the Kubernetes exec API (which encodes commands as query parameters).
        var destDir = Path.GetDirectoryName(destPath)?.Replace('/', '\\') ?? "";
        var base64 = Convert.ToBase64String(fileData);

        var psCommand = $@"
if (-not (Test-Path '{destDir}')) {{ New-Item -ItemType Directory -Path '{destDir}' -Force | Out-Null }}
$b64 = [Console]::In.ReadLine()
[System.IO.File]::WriteAllBytes('{destPath}', [System.Convert]::FromBase64String($b64))
Write-Host 'COPY_OK'
";
        var command = new[] { "pwsh", "-NoProfile", "-Command", psCommand };
        var ws = await client.WebSocketNamespacedPodExecAsync(
            podName, Namespace, command, containerName,
            stderr: true, stdin: true, stdout: true, tty: false);

        using var demux = new k8s.StreamDemuxer(ws);
        demux.Start();

        // Send base64 data via stdin as a single line (terminated by newline so ReadLine returns)
        using (var stdinStream = demux.GetStream((byte?)null, (byte)0))
        {
            var stdinBytes = System.Text.Encoding.UTF8.GetBytes(base64 + "\n");
            await stdinStream.WriteAsync(stdinBytes);
        }

        var stdoutStream = demux.GetStream(1, null);
        var stderrStream = demux.GetStream(2, null);

        using var stdoutReader = new StreamReader(stdoutStream);
        using var stderrReader = new StreamReader(stderrStream);

        var stdoutTask = stdoutReader.ReadToEndAsync();
        var stderrTask = stderrReader.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        var stdout = stdoutTask.Result;
        if (!stdout.Contains("COPY_OK"))
        {
            throw new InvalidOperationException($"Failed to copy file to pod: {stderrTask.Result}");
        }

        Logger.LogInformation("File copied to pod ({Size} bytes).", fileData.Length);
    }

}
