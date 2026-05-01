using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

sealed class PublishAppCommand : ClientCommand
{
    public override string Name => "PublishApp";
    public override string Description => "Publishes a .app file to a running Business Central container.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "name", Type = "string", Description = "Name of the container.", Required = true },
        new() { Name = "appFile", Type = "file", Description = "Path to the .app file to publish.", Required = true },
        new() { Name = "syncMode", Type = "string", Description = "Sync mode: Add, ForceSync, Clean, Development (default: Add).", Required = false }
    ];

    // Paths inside the container for the detached publish workflow
    private const string AppDestPath = @"c:\run\my\fkh-publish-app.app";
    private const string ScriptPath = @"c:\run\my\fkh-publish.ps1";
    private const string ResultPath = @"c:\run\my\fkh-publish.result";
    private const string LogPath = @"c:\run\my\fkh-publish.log";

    public override async Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
    {
        Dictionary<string, string> parameters;
        try
        {
            parameters = ParseClientArgs(args);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            return 1;
        }

        if (!parameters.TryGetValue("name", out var containerName) || string.IsNullOrWhiteSpace(containerName))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --name{Ansi.Reset}");
            return 1;
        }

        if (!parameters.TryGetValue("appFile", out var appFile) || string.IsNullOrWhiteSpace(appFile))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --appFile{Ansi.Reset}");
            return 1;
        }

        if (!File.Exists(appFile))
        {
            Console.Error.WriteLine($"{Ansi.Red}File not found: {appFile}{Ansi.Reset}");
            return 1;
        }

        var syncMode = parameters.TryGetValue("syncMode", out var sm) ? sm : "Add";
        var noWait = args.Any(a => string.Equals(a, "--nowait", StringComparison.OrdinalIgnoreCase));

        var backendUrl = settings.BackendUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(backendUrl))
        {
            Console.Error.WriteLine($"{Ansi.Red}No backend URL configured.{Ansi.Reset}");
            return 1;
        }

        string token;
        try
        {
            token = GetToken(parameters, settings.User);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            return 1;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // ── Step 1: Upload the .app file ─────────────────────────────────────────
        var fileSize = new FileInfo(appFile).Length;
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Uploading {Path.GetFileName(appFile)} ({fileSize / (1024.0 * 1024):N3} Mb) to container '{containerName}'...{Ansi.Reset}");

        var uploadResult = await CopyFileToContainerAsync(httpClient, backendUrl, token, containerName, appFile, AppDestPath);
        if (uploadResult != 0) return uploadResult;

        // ── Step 2: Upload the publish script ────────────────────────────────────
        var scriptContent = BuildPublishScript(syncMode);
        var scriptTempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(scriptTempFile, scriptContent, new UTF8Encoding(false));

            var scriptUploadResult = await CopyFileToContainerAsync(httpClient, backendUrl, token, containerName, scriptTempFile, ScriptPath);
            if (scriptUploadResult != 0) return scriptUploadResult;
        }
        finally
        {
            File.Delete(scriptTempFile);
        }

        // ── Step 3: Launch the script detached ───────────────────────────────────
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Publishing app in container '{containerName}'...{Ansi.Reset}");

        var launchScript = $"Remove-Item '{ResultPath}','{LogPath}' -Force -ErrorAction SilentlyContinue; " +
                           $"Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile','-File','{ScriptPath}' -WindowStyle Hidden; " +
                           $"Write-Host 'LAUNCHED'";

        var launchResult = await InvokeScriptAsync(httpClient, backendUrl, token, containerName, launchScript);
        if (launchResult.ExitCode != 0) return launchResult.ExitCode;

        if (!launchResult.Output.Contains("LAUNCHED"))
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to launch publish script.{Ansi.Reset}");
            if (!string.IsNullOrWhiteSpace(launchResult.Output))
                Console.Error.WriteLine($"{Ansi.Red}{launchResult.Output}{Ansi.Reset}");
            return 1;
        }

        if (noWait)
        {
            if (!asJson)
                Console.WriteLine($"{Ansi.Yellow}Publish started in background. Use 'fkh invokescript --name {containerName} --command \"Get-Content {ResultPath} -ErrorAction SilentlyContinue\"' to check progress.{Ansi.Reset}");
            return 0;
        }

        // ── Step 4: Poll for the result file ─────────────────────────────────────
        var pollScript = $"if (Test-Path '{ResultPath}') {{ Get-Content '{ResultPath}' -Raw }} else {{ Write-Host 'PENDING' }}";

        while (true)
        {
            await Task.Delay(5_000);

            var pollResult = await InvokeScriptAsync(httpClient, backendUrl, token, containerName, pollScript);

            // If we can't reach the backend (timeout, etc.), just retry
            if (pollResult.ExitCode != 0 && pollResult.IsTimeout)
            {
                if (!asJson)
                    Console.WriteLine($"{Ansi.Yellow}Poll timed out — retrying...{Ansi.Reset}");
                continue;
            }

            if (pollResult.ExitCode != 0)
            {
                Console.Error.WriteLine($"{Ansi.Red}Failed to check publish status: {pollResult.Output}{Ansi.Reset}");
                return 1;
            }

            var output = pollResult.Output.Trim();
            if (output == "PENDING")
            {
                if (!asJson)
                    Console.Write(".");
                continue;
            }

            // Result file exists — parse it
            if (!asJson && Console.CursorLeft > 0)
                Console.WriteLine();

            // The result file contains "OK|<json>" or "ERROR|<message>"
            if (output.StartsWith("OK|", StringComparison.Ordinal))
            {
                var resultJson = output[3..];
                if (asJson)
                {
                    Console.WriteLine(resultJson);
                }
                else
                {
                    Console.WriteLine($"{Ansi.Cyan}App published successfully.{Ansi.Reset}");
                    Console.WriteLine(FormatJsonAsText(resultJson));
                }
                return 0;
            }
            else if (output.StartsWith("ERROR|", StringComparison.Ordinal))
            {
                var errorMsg = output[6..];
                Console.Error.WriteLine($"{Ansi.Red}App publishing failed:{Ansi.Reset}");
                Console.Error.WriteLine($"{Ansi.Red}{errorMsg}{Ansi.Reset}");
                return 1;
            }
            else
            {
                // Unexpected format — show raw
                if (asJson)
                    Console.WriteLine(JsonSerializer.Serialize(new { output }));
                else
                    Console.WriteLine(output);
                return 0;
            }
        }
    }

    private static string BuildPublishScript(string syncMode)
    {
        // This script runs detached in the pod. It writes its result to a marker file.
        return $@"
$ErrorActionPreference = 'Stop'
try {{
    . 'c:\run\prompt.ps1'
    $appPath = '{AppDestPath}'
    $serverInstance = 'BC'
    $tenant = 'default'

    $appInfo = Get-NAVAppInfo -Path $appPath
    'Publishing app: ' + $appInfo.Name + ' v' + $appInfo.Version | Out-File '{LogPath}' -Append

    Publish-NAVApp -ServerInstance $serverInstance -Path $appPath -SkipVerification
    'Publish-NAVApp completed' | Out-File '{LogPath}' -Append

    Sync-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant -Mode {syncMode}
    'Sync-NAVApp completed' | Out-File '{LogPath}' -Append

    $existingApp = Get-NAVAppInfo -ServerInstance $serverInstance -Tenant $tenant -Name $appInfo.Name -TenantSpecificProperties | Where-Object {{ $_.IsInstalled -eq $true }}
    if ($existingApp) {{
        'Upgrading from v' + $existingApp.Version | Out-File '{LogPath}' -Append
        Start-NAVAppDataUpgrade -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }} else {{
        Install-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }}
    'Install/Upgrade completed' | Out-File '{LogPath}' -Append

    $resultJson = $appInfo | Select-Object Name, Publisher, Version | ConvertTo-Json -Compress
    'OK|' + $resultJson | Out-File '{ResultPath}' -NoNewline
}} catch {{
    'ERROR|' + $_.Exception.Message | Out-File '{ResultPath}' -NoNewline
}} finally {{
    Remove-Item -Path '{AppDestPath}' -Force -ErrorAction SilentlyContinue
    Remove-Item -Path '{ScriptPath}' -Force -ErrorAction SilentlyContinue
}}
";
    }

    private async Task<int> CopyFileToContainerAsync(HttpClient httpClient, string backendUrl, string token, string containerName, string localFile, string remotePath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/CopyFileToContainer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var multipart = new MultipartFormDataContent();
        var paramsDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = containerName,
            ["containerFilename"] = remotePath
        };
        multipart.Add(new StringContent(
            JsonSerializer.Serialize(new FunctionInvokeRequest { Parameters = paramsDict }),
            Encoding.UTF8,
            "application/json"), "parameters");

        var fileStream = new FileStream(localFile, FileMode.Open, FileAccess.Read);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent, "file", Path.GetFileName(localFile));

        request.Content = multipart;

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{Ansi.Red}File upload failed ({(int)response.StatusCode}): {body}{Ansi.Reset}");
            return 1;
        }
        return 0;
    }

    private async Task<InvokeResult> InvokeScriptAsync(HttpClient httpClient, string backendUrl, string token, string containerName, string command)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/InvokeScript");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new FunctionInvokeRequest
                {
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["name"] = containerName,
                        ["command"] = command
                    }
                }),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var isTimeout = response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                    && body.Contains("timed out", StringComparison.OrdinalIgnoreCase);
                return new InvokeResult { ExitCode = 1, Output = body, IsTimeout = isTimeout };
            }

            // Extract "output" field from JSON response
            try
            {
                using var doc = JsonDocument.Parse(body);
                var output = doc.RootElement.TryGetProperty("output", out var outputEl)
                    ? outputEl.GetString() ?? ""
                    : body;
                return new InvokeResult { ExitCode = 0, Output = output };
            }
            catch
            {
                return new InvokeResult { ExitCode = 0, Output = body };
            }
        }
        catch (TaskCanceledException)
        {
            return new InvokeResult { ExitCode = 1, Output = "Request timed out.", IsTimeout = true };
        }
        catch (HttpRequestException ex)
        {
            return new InvokeResult { ExitCode = 1, Output = ex.Message, IsTimeout = true };
        }
    }

    private sealed class InvokeResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public bool IsTimeout { get; init; }
    }

    private static string FormatJsonAsText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            FormatElement(doc.RootElement, sb, 0);
            return sb.ToString();
        }
        catch
        {
            return json;
        }
    }

    private static void FormatElement(JsonElement element, StringBuilder sb, int indent)
    {
        var prefix = new string(' ', indent * 2);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        sb.AppendLine($"{prefix}{prop.Name}:");
                        FormatElement(prop.Value, sb, indent + 1);
                    }
                    else
                    {
                        sb.AppendLine($"{prefix}{prop.Name}: {prop.Value}");
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    FormatElement(item, sb, indent);
                    sb.AppendLine();
                }
                break;
            default:
                sb.Append($"{prefix}{element}");
                break;
        }
    }
}
