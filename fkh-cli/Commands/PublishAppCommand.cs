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
        new() { Name = "syncMode", Type = "string", Description = "Sync mode: Add, ForceSync, Clean, Development (default: Add).", Required = false },
        new() { Name = "devScope", Type = "boolean", Description = "Publish to dev/tenant scope using the dev endpoint (like VS Code).", Required = false }
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
        var devScope = parameters.TryGetValue("devScope", out var ds)
            && string.Equals(ds, "true", StringComparison.OrdinalIgnoreCase);
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

        // Fetch container details (devScope, credentials, webClientUrl) in one call
        var details = await GetContainerDetailsAsync(httpClient, backendUrl, token, containerName);

        // Auto-detect devScope from container metadata if not explicitly set
        if (!devScope && details != null)
        {
            devScope = details.Value.DevScope;
        }

        var fileSize = new FileInfo(appFile).Length;
        var scopeLabel = devScope ? "tenant (dev endpoint)" : "global";
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Publishing {Path.GetFileName(appFile)} ({fileSize / (1024.0 * 1024):N3} Mb) to container '{containerName}' [scope: {scopeLabel}, syncMode: {syncMode}]...{Ansi.Reset}");

        // ── Dev scope: publish directly to the container's dev endpoint ───────────
        if (devScope)
        {
            if (details is null)
            {
                Console.Error.WriteLine($"{Ansi.Red}Could not retrieve details for container '{containerName}'.{Ansi.Reset}");
                return 1;
            }

            var adminUsername = details.Value.AdminUsername;
            var adminPassword = details.Value.AdminPassword;

            var devEndpointUrl = BuildDevEndpointUrl(details?.WebClientUrl, syncMode);
            if (devEndpointUrl is null)
            {
                Console.Error.WriteLine($"{Ansi.Red}Could not determine dev endpoint URL for container '{containerName}'. Is it running?{Ansi.Reset}");
                return 1;
            }

            return await PublishViaDevEndpointAsync(devEndpointUrl, appFile, adminUsername, adminPassword, asJson);
        }

        // ── Global scope: upload file + run script in container ───────────────────
        // ── Step 1: Upload the .app file ─────────────────────────────────────────

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
        var wroteProgress = false;

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
                {
                    Console.Write(".");
                    wroteProgress = true;
                }
                continue;
            }

            // Result file exists — parse it
            if (!asJson && wroteProgress)
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
    Write-Host ('Publishing app: ' + $appInfo.Name + ' v' + $appInfo.Version)

    Publish-NAVApp -ServerInstance $serverInstance -Path $appPath -SkipVerification
    Write-Host 'Publish-NAVApp completed'

    Sync-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant -Mode {syncMode} -Force
    Write-Host 'Sync-NAVApp completed'

    $existingApp = Get-NAVAppInfo -ServerInstance $serverInstance -Tenant $tenant -Name $appInfo.Name -TenantSpecificProperties | Where-Object {{ $_.IsInstalled -eq $true }}
    if ($existingApp) {{
        Write-Host ('Upgrading from v' + $existingApp.Version)
        Start-NAVAppDataUpgrade -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }} else {{
        Write-Host 'Installing app'
        Install-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }}
    Write-Host 'Install/Upgrade completed'

    $resultJson = $appInfo | Select-Object Name, Publisher, @{{N='Version';E={{$_.Version.ToString()}}}} | ConvertTo-Json -Compress
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

    private static string? BuildDevEndpointUrl(string? webClientUrl, string syncMode)
    {
        if (webClientUrl is null) return null;

        // WebClient URL is like https://fqdn/BC/ or https://fqdn/BC/?tenant=default
        // Dev endpoint is https://fqdn:7049/BC/dev/apps?SchemaUpdateMode=...&tenant=default
        var uri = new Uri(webClientUrl);
        var host = uri.Host;
        // Extract server instance from path (first segment, typically "BC")
        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var serverInstance = pathSegments.Length > 0 ? pathSegments[0] : "BC";

        var schemaUpdateMode = syncMode switch
        {
            "Clean" => "recreate",
            "ForceSync" => "forcesync",
            _ => "synchronize"
        };

        return $"https://{host}:7049/{serverInstance}/dev/apps?SchemaUpdateMode={schemaUpdateMode}&tenant=default";
    }

    private static async Task<int> PublishViaDevEndpointAsync(string devEndpointUrl, string appFile, string adminUsername, string adminPassword, bool asJson)
    {
        try
        {
            if (!asJson)
                Console.WriteLine($"{Ansi.Dim}Posting to {devEndpointUrl}{Ansi.Reset}");

            using var fileStream = new FileStream(appFile, FileMode.Open, FileAccess.Read);
            var appFileName = Path.GetFileName(appFile);

            var multipartContent = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = appFileName,
                FileName = appFileName
            };
            multipartContent.Add(fileContent);

            // Skip TLS certificate validation for self-signed certs on the container
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var devHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            // Basic auth with the container's admin credentials
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{adminUsername}:{adminPassword}"));
            devHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var response = await devHttpClient.PostAsync(devEndpointUrl, multipartContent);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"{Ansi.Red}Dev endpoint publish failed ({(int)response.StatusCode}): {body}{Ansi.Reset}");
                return 1;
            }

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { message = "App published successfully via dev endpoint.", response = body }));
            }
            else
            {
                Console.WriteLine($"{Ansi.Cyan}App published successfully via dev endpoint.{Ansi.Reset}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to publish via dev endpoint: {ex.Message}{Ansi.Reset}");
            return 1;
        }
    }

    private record struct ContainerDetails(string AdminUsername, string AdminPassword, bool DevScope, string? WebClientUrl);

    private static async Task<ContainerDetails?> GetContainerDetailsAsync(HttpClient httpClient, string backendUrl, string token, string containerName)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/GetContainerDetails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new FunctionInvokeRequest
                {
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["name"] = containerName
                    }
                }),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"{Ansi.Red}Failed to retrieve container details ({(int)response.StatusCode}): {errorBody}{Ansi.Reset}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            var username = doc.RootElement.TryGetProperty("AdminUsername", out var u) ? u.GetString()
                : doc.RootElement.TryGetProperty("adminUsername", out u) ? u.GetString() : null;
            var password = doc.RootElement.TryGetProperty("AdminPassword", out var p) ? p.GetString()
                : doc.RootElement.TryGetProperty("adminPassword", out p) ? p.GetString() : null;
            var hasDevScope = doc.RootElement.TryGetProperty("DevScope", out var dv) ? dv.GetBoolean()
                : doc.RootElement.TryGetProperty("devScope", out dv) && dv.GetBoolean();
            var webClient = doc.RootElement.TryGetProperty("WebClientUrl", out var wc) ? wc.GetString()
                : doc.RootElement.TryGetProperty("webClientUrl", out wc) ? wc.GetString() : null;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            return new ContainerDetails(username, password, hasDevScope, webClient);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to retrieve container details: {ex.Message}{Ansi.Reset}");
            return null;
        }
    }
}
