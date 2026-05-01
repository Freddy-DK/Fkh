using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhPublishApp : FkhServiceBase
{
    public FkhPublishApp(ILogger<FkhPublishApp> logger) : base(logger) { }

    public async Task<object> PublishAppAsync(Dictionary<string, string> parameters, Dictionary<string, byte[]> files)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var syncMode = parameters.TryGetValue("syncMode", out var sm) ? sm : "Add";
        var devScopeParam = parameters.TryGetValue("devScope", out var ds)
            && string.Equals(ds, "true", StringComparison.OrdinalIgnoreCase);

        if (!files.TryGetValue("appFile", out var appFileBytes) || appFileBytes.Length == 0)
        {
            throw new InvalidOperationException("No app file was uploaded.");
        }

        var client = await GetKubernetesClientAsync();

        // Check deployment annotation for dev-scope (set when container was created with moveAllAppsToDevScope)
        var useDevScope = devScopeParam;
        if (!useDevScope)
        {
            try
            {
                var deployment = await client.ReadNamespacedDeploymentAsync(appName, Namespace);
                useDevScope = deployment.Metadata?.Annotations?.TryGetValue("fkh/dev-scope", out var devScopeAnnotation) == true
                    && string.Equals(devScopeAnnotation, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* deployment not found — proceed without dev scope */ }
        }

        Logger.LogInformation(
            "User '{User}' publishing app to container '{Container}' ({Size} bytes, syncMode={SyncMode}, devScope={DevScope}).",
            githubUsername, appName, appFileBytes.Length, syncMode, useDevScope);

        // Find the BC pod for this container
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{appName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var containerName = pod.Spec.Containers[0].Name;

        // Copy the .app file into the pod via stdin (tar stream)
        var destPath = "c:\\run\\my";
        var fileName = "app.app";
        Logger.LogInformation("Copying app file to pod '{Pod}' at {Dest}\\{File}...", podName, destPath, fileName);
        await CopyFileToPodAsync(client, podName, containerName, appFileBytes, destPath, fileName);

        // Run publish inside the pod — either via dev endpoint or Publish-NAVApp
        Logger.LogInformation("Publishing app in container '{Container}' (devScope={DevScope})...", appName, useDevScope);

        string script;
        if (useDevScope)
        {
            // Dev endpoint publish (like VS Code does) — posts to the BC dev services endpoint
            var schemaUpdateMode = syncMode switch
            {
                "Clean" => "recreate",
                "ForceSync" => "forcesync",
                _ => "synchronize"
            };
            script = $@"
$ErrorActionPreference = 'Stop'
$appPath = '{destPath}\{fileName}'
$tenant = 'default'
try {{
    $customConfig = Get-Content 'c:\run\ServiceSettings.json' | ConvertFrom-Json
    $devPort = $customConfig.DeveloperServicesPort
    if (-not $devPort) {{ $devPort = '7049' }}
    $serverInstance = $customConfig.ServerInstance
    if (-not $serverInstance) {{ $serverInstance = 'BC' }}
    $url = ""http://localhost:$devPort/$serverInstance/dev/apps?SchemaUpdateMode={schemaUpdateMode}&tenant=$tenant""
    Write-Host ""Publishing to dev endpoint: $url""
    $appBytes = [System.IO.File]::ReadAllBytes($appPath)
    $appName = [System.IO.Path]::GetFileName($appPath)
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $httpClient = [System.Net.Http.HttpClient]::new($handler)
    $httpClient.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
    $multipartContent = [System.Net.Http.MultipartFormDataContent]::new()
    $fileContent = [System.Net.Http.ByteArrayContent]::new($appBytes)
    $fileHeader = [System.Net.Http.Headers.ContentDispositionHeaderValue]::new('form-data')
    $fileHeader.Name = $appName
    $fileHeader.FileName = $appName
    $fileContent.Headers.ContentDisposition = $fileHeader
    $multipartContent.Add($fileContent)
    $result = $httpClient.PostAsync($url, $multipartContent).GetAwaiter().GetResult()
    if (-not $result.IsSuccessStatusCode) {{
        $msg = $result.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        throw ""Dev endpoint publish failed ($($result.StatusCode)): $msg""
    }}
    Write-Host ""App published successfully via dev endpoint.""
    . 'c:\run\prompt.ps1'
    $appInfo = Get-NAVAppInfo -Path $appPath
    $appInfo | Select-Object Name, Publisher, Version | ConvertTo-Json
}} catch {{
    Write-Error ""Failed to publish app: $_""
    exit 1
}} finally {{
    Remove-Item -Path $appPath -Force -ErrorAction SilentlyContinue
}}
";
        }
        else
        {
            script = $@"
$ErrorActionPreference = 'Stop'
. 'c:\run\prompt.ps1'  # Load BC admin tools into the session
$appPath = '{destPath}\{fileName}'
$serverInstance = 'BC'
$tenant = 'default'
try {{
    $appInfo = Get-NAVAppInfo -Path $appPath
    Write-Host ""Publishing app: $($appInfo.Name) v$($appInfo.Version)""
    Publish-NAVApp -ServerInstance $serverInstance -Path $appPath -SkipVerification
    Sync-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant -Mode {syncMode} -Force
    $existingApp = Get-NAVAppInfo -ServerInstance $serverInstance -Tenant $tenant -Name $appInfo.Name -TenantSpecificProperties | Where-Object {{ $_.IsInstalled -eq $true }}
    if ($existingApp) {{
        Write-Host ""Upgrading from v$($existingApp.Version)...""
        Start-NAVAppDataUpgrade -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }} else {{
        Install-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }}
    Write-Host ""App published successfully.""
    $appInfo | Select-Object Name, Publisher, Version | ConvertTo-Json
}} catch {{
    Write-Error ""Failed to publish app: $_""
    exit 1
}} finally {{
    Remove-Item -Path $appPath -Force -ErrorAction SilentlyContinue
}}
";
        }

        var result = await ExecInBcPodAsync(client, podName, containerName, script);

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            throw new InvalidOperationException($"App publishing failed in container '{appName}':\n{result.Stderr.TrimEnd()}");
        }

        // Try to parse the JSON output from the script
        string? appInfoJson = null;
        var lines = result.Stdout.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('{'))
            {
                appInfoJson = trimmed;
                break;
            }
        }

        return new
        {
            Message = "App published successfully.",
            Container = appName,
            Output = result.Stdout.TrimEnd(),
            Stderr = string.IsNullOrWhiteSpace(result.Stderr) ? null : result.Stderr.TrimEnd(),
        };
    }

    private async Task CopyFileToPodAsync(Kubernetes client, string podName, string containerName, byte[] fileData, string destDir, string fileName)
    {
        // Send the file to the pod via stdin as base64.
        // We use stdin instead of command-line arguments to avoid URI length limits
        // in the Kubernetes exec API (which encodes commands as query parameters).
        var base64 = Convert.ToBase64String(fileData);

        var psCommand = $@"
if (-not (Test-Path '{destDir}')) {{ New-Item -ItemType Directory -Path '{destDir}' -Force | Out-Null }}
$b64 = [Console]::In.ReadLine()
[System.IO.File]::WriteAllBytes('{destDir}\{fileName}', [System.Convert]::FromBase64String($b64))
Write-Host 'COPY_OK'
";
        var command = new[] { "powershell", "-NoProfile", "-Command", psCommand };
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

    private async Task<ExecResult> ExecInBcPodAsync(Kubernetes client, string podName, string containerName, string psScript)
    {
        var command = new[] { "powershell", "-NoProfile", "-Command", psScript };
        var ws = await client.WebSocketNamespacedPodExecAsync(
            podName, Namespace, command, containerName,
            stderr: true, stdin: false, stdout: true, tty: false);

        using var demux = new k8s.StreamDemuxer(ws);
        demux.Start();

        var stdoutStream = demux.GetStream(1, null);
        var stderrStream = demux.GetStream(2, null);

        using var stdoutReader = new StreamReader(stdoutStream);
        using var stderrReader = new StreamReader(stderrStream);

        var stdoutTask = stdoutReader.ReadToEndAsync();
        var stderrTask = stderrReader.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        var stderr = stderrTask.Result;
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Logger.LogWarning("BC pod exec stderr: {StdErr}", stderr);
        }

        return new ExecResult(stdoutTask.Result, stderr);
    }
}
