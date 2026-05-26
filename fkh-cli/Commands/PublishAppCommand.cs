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
        new() { Name = "devScope", Type = "boolean", Description = "Publish to dev/tenant scope using the dev endpoint (like VS Code).", Required = false },
        new() { Name = "sync", Type = "boolean", Description = "Run Sync-NAVApp after publishing (implied by --install).", Required = false },
        new() { Name = "install", Type = "boolean", Description = "Run Sync-NAVApp and Install/Upgrade after publishing.", Required = false }
    ];

    // Path inside the container for the app file
    private const string AppDestPath = @"c:\run\my\fkh-publish-app.app";

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
        var installFlag = parameters.TryGetValue("install", out var inst)
            && string.Equals(inst, "true", StringComparison.OrdinalIgnoreCase);
        var syncFlag = parameters.TryGetValue("sync", out var sy)
            && string.Equals(sy, "true", StringComparison.OrdinalIgnoreCase);

        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        TokenProvider tokenProvider;
        string token;
        try
        {
            tokenProvider = CreateTokenProvider(parameters, settings);
            token = await tokenProvider.GetTokenAsync();
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

        // ── Step 2: Run the publish script via InvokeScript ──────────────────────
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Publishing app in container '{containerName}'...{Ansi.Reset}");

        var scriptContent = BuildPublishScript(syncMode, syncFlag || installFlag, installFlag);
        var result = await InvokeScriptAsync(httpClient, backendUrl, tokenProvider, containerName, scriptContent, asJson);

        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}App publishing failed:{Ansi.Reset}");
            if (!string.IsNullOrWhiteSpace(result.Output))
                Console.Error.WriteLine($"{Ansi.Red}{result.Output}{Ansi.Reset}");
            if (!string.IsNullOrWhiteSpace(result.Stderr))
                Console.Error.WriteLine($"{Ansi.Red}{result.Stderr}{Ansi.Reset}");
            return 1;
        }

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { container = containerName, output = result.Output }));
        }
        else
        {
            Console.WriteLine($"{Ansi.Cyan}App published successfully.{Ansi.Reset}");
            if (!string.IsNullOrWhiteSpace(result.Output))
                Console.WriteLine(result.Output);
        }
        return 0;
    }

    private static string BuildPublishScript(string syncMode, bool doSync, bool doInstall)
    {
        return $@"
$ErrorActionPreference = 'Stop'
$appPath = '{AppDestPath}'
$serverInstance = 'BC'
$tenant = 'default'
$syncMode = '{syncMode}'
$doSync = {(doSync ? "$true" : "$false")}
$doInstall = {(doInstall ? "$true" : "$false")}

$appInfo = Get-NAVAppInfo -Path $appPath
Write-Host ('Publishing app: ' + $appInfo.Name + ' v' + $appInfo.Version)

Publish-NAVApp -ServerInstance $serverInstance -Path $appPath -SkipVerification
Write-Host 'Publish-NAVApp completed'

if ($doSync -or $doIInstall) {{
    Sync-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant -Mode $syncMode -Force
    Write-Host 'Sync-NAVApp completed'
}}

if ($doInstall) {{
    $existingApp = Get-NAVAppInfo -ServerInstance $serverInstance -Tenant $tenant -Id $appInfo.AppId -TenantSpecificProperties | Where-Object {{ $_.IsInstalled -eq $true }}
    if ($existingApp -and $existingApp.Version -eq $appInfo.Version) {{
        Write-Host ('App ' + $appInfo.Name + ' v' + $appInfo.Version + ' is already installed, skipping install/upgrade')
    }} elseif ($existingApp) {{
        Write-Host ('Upgrading from v' + $existingApp.Version)
        Start-NAVAppDataUpgrade -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }} else {{
        Write-Host 'Installing app'
        Install-NAVApp -ServerInstance $serverInstance -Name $appInfo.Name -Version $appInfo.Version -Tenant $tenant
    }}
    Write-Host 'Install/Upgrade completed'
}}

Remove-Item -Path $appPath -Force -ErrorAction SilentlyContinue

$appInfo | Select-Object Name, Publisher, @{{N='Version';E={{$_.Version.ToString()}}}} | ConvertTo-Json -Compress
";
    }

    private async Task<int> CopyFileToContainerAsync(HttpClient httpClient, string backendUrl, string token, string containerName, string localFile, string remotePath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/CopyFileToContainer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddProtocolHeaders(request);

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

    private async Task<InvokeResult> InvokeScriptAsync(HttpClient httpClient, string backendUrl, TokenProvider tokenProvider, string containerName, string command, bool asJson)
    {
        while (true)
        {
            string token;
            try { token = await tokenProvider.GetTokenAsync(); }
            catch (Exception ex) { return new InvokeResult { ExitCode = 1, Output = ex.Message }; }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/InvokeScript");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                AddProtocolHeaders(request);
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

                // Handle 202 Accepted (script still running, retry after delay)
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    var retrySeconds = 10;
                    if (response.Headers.TryGetValues("Retry-After", out var retryValues))
                        int.TryParse(retryValues.FirstOrDefault(), out retrySeconds);

                    if (!asJson)
                        Console.Write(".");

                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds));
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new InvokeResult { ExitCode = 1, Output = body };
                }

                // Extract "output" and "stderr" fields from JSON response
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var output = doc.RootElement.TryGetProperty("output", out var outputEl)
                        ? outputEl.GetString() ?? ""
                        : body;
                    var stderr = doc.RootElement.TryGetProperty("stderr", out var stderrEl) && stderrEl.ValueKind != JsonValueKind.Null
                        ? stderrEl.GetString()
                        : null;
                    return new InvokeResult { ExitCode = 0, Output = output, Stderr = stderr };
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
    }

    private sealed class InvokeResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public string? Stderr { get; init; }
        public bool IsTimeout { get; init; }
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
            AddProtocolHeaders(request);
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
