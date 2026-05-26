using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const string Help = """
FKH CLI

Usage:
    fkh <command> [--key "value" ...]

Configuration (checked in order):
  1. --backendUrl <url>     command-line override
  2. FKH_BACKEND_URL environment variable
  3. ~/.fkh/settings.json   (recommended for dotnet tool install)
  4. fkh.settings.json next to the executable

    {
        "backendUrl": "https://fkh-<org>-backend.azurewebsites.net/api"
    }

Authentication (checked in order):
  1. --useOIDC             Fetch OIDC token from GitHub Actions environment
                           (uses ACTIONS_ID_TOKEN_REQUEST_URL/TOKEN, auto-refreshes)
  2. GH_TOKEN              GitHub personal access token
  3. gh auth token         GitHub CLI (interactive fallback)
                           Use --ghUser <user> or GH_USER env var to target a
                           specific GitHub account

Note: --useOIDC is exclusive — when specified, no other auth methods are tried.
""";

var asJson = args.Contains("--asJson", StringComparer.OrdinalIgnoreCase);
var useOidc = args.Contains("--useOIDC", StringComparer.OrdinalIgnoreCase);

try
{
    if (args.Length > 0 && string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
    {
        var version = typeof(FunctionCatalogResponse).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(FunctionCatalogResponse).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        Console.WriteLine(version);
        return 0;
    }

    if (args.Length > 0 && string.Equals(args[0], "--completions", StringComparison.OrdinalIgnoreCase))
    {
        return await GenerateCompletionDataAsync(args);
    }

    var settings = LoadSettings();

    // Apply command-line overrides
    var cliBackendUrl = FindArgValue(args, "backendUrl");
    if (!string.IsNullOrWhiteSpace(cliBackendUrl))
        settings.BackendUrl = cliBackendUrl;
    settings.User = FindArgValue(args, "ghUser") ?? Environment.GetEnvironmentVariable("GH_USER");
    settings.UseOidc = useOidc;
    var wantsHelp = args.Length == 0 || args.Contains("-h") || args.Contains("--help");
    var helpCommand = (args.Length >= 2 && wantsHelp && !args[0].StartsWith("-")) ? args[0] : null;

    // ── Check for client-side commands first (not in the server catalog) ──────
    var clientCommands = ClientCommands.All;
    if (!wantsHelp && args.Length > 0)
    {
        var commandName = args[0];
        var clientCmd = clientCommands.FirstOrDefault(c =>
            string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));
        if (clientCmd is not null)
        {
            return await clientCmd.ExecuteAsync(args, settings, asJson);
        }
    }

    FunctionCatalogResponse catalog;

    try
    {
        catalog = await GetFunctionCatalogAsync(settings.BackendUrl);
    }
    catch (Exception ex)
    {
        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
        }
        else
        {
            Console.WriteLine(Help);
            Console.WriteLine();
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
        }
        return 1;
    }

    if (wantsHelp)
    {
        if (helpCommand is not null)
        {
            var func = catalog.Functions.FirstOrDefault(f =>
                string.Equals(f.Name, helpCommand, StringComparison.OrdinalIgnoreCase));
            if (func is not null)
            {
                PrintCommandUsage(func);
                return 0;
            }
            var clientCmd = clientCommands.FirstOrDefault(c =>
                string.Equals(c.Name, helpCommand, StringComparison.OrdinalIgnoreCase));
            if (clientCmd is not null)
            {
                PrintClientCommandUsage(clientCmd);
                return 0;
            }
            Console.Error.WriteLine($"{Ansi.Red}Unknown command: {helpCommand}{Ansi.Reset}");
            Console.WriteLine();
        }
        PrintUsage(catalog);
        return 0;
    }

    ParsedArgs parsed;
    FunctionDefinition function;
    try
    {
        parsed = ParseArgs(args, catalog);
        function = catalog.Functions.First(f =>
            string.Equals(f.Name, parsed.Command, StringComparison.OrdinalIgnoreCase));
        EnsureRequiredParameters(function, parsed.Parameters);
    }
    catch (InvalidOperationException ex) when (IsUsageError(ex.Message))
    {
        Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
        Console.WriteLine();
        // If we can identify the command, show its help; otherwise show general usage
        var errorCommand = args.Length > 0 && !args[0].StartsWith("-") ? args[0] : null;
        var errorFunc = errorCommand is not null
            ? catalog.Functions.FirstOrDefault(f => string.Equals(f.Name, errorCommand, StringComparison.OrdinalIgnoreCase))
            : null;
        if (errorFunc is not null)
        {
            PrintCommandUsage(errorFunc);
        }
        else
        {
            var errorClientCmd = errorCommand is not null
                ? clientCommands.FirstOrDefault(c => string.Equals(c.Name, errorCommand, StringComparison.OrdinalIgnoreCase))
                : null;
            if (errorClientCmd is not null)
                PrintClientCommandUsage(errorClientCmd);
            else
                PrintUsage(catalog);
        }
        return 1;
    }

    var endpoint = ResolveEndpoint(function.Route, settings);
    var tokenProvider = new TokenProvider(useOidc: settings.UseOidc, ghUser: settings.User);
    var token = await tokenProvider.GetTokenAsync();

    // Send the client's timezone so the server can resolve time-of-day autostop values
    parsed.Parameters["_timezone"] = Environment.GetEnvironmentVariable("FKH_TIMEZONE") is string tz && !string.IsNullOrWhiteSpace(tz)
        ? tz.Trim()
        : TimeZoneInfo.Local.Id;

    // Detect file-type parameters
    var fileParams = function.Parameters
        .Where(p => string.Equals(p.Type, "file", StringComparison.OrdinalIgnoreCase))
        .Select(p => p.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Resolve file paths and validate they exist
    var filesToUpload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var fp in fileParams)
    {
        if (parsed.Parameters.TryGetValue(fp, out var filePath) && !string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"{Ansi.Red}File not found: {filePath}{Ansi.Reset}");
                return 1;
            }
            filesToUpload[fp] = filePath;
            parsed.Parameters.Remove(fp);
        }
    }

    var hasFiles = filesToUpload.Count > 0;

    if (!parsed.AsJson)
    {
        if (hasFiles)
        {
            foreach (var (paramName, filePath) in filesToUpload)
            {
                var fileSize = new FileInfo(filePath).Length;
                Console.WriteLine($"{Ansi.Dim}Uploading {paramName}: {filePath} ({fileSize / (1024.0 * 1024):N3} Mb){Ansi.Reset}");
            }
        }
        Console.WriteLine($"{Ansi.Dim}Calling {endpoint}{Ansi.Reset}");
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromMinutes(30);

    var retryInProgress = false;

    while (true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (hasFiles)
        {
            // Send as multipart/form-data with file attachments
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(
                JsonSerializer.Serialize(new FunctionInvokeRequest { Parameters = parsed.Parameters }),
                Encoding.UTF8,
                "application/json"), "parameters");

            foreach (var (paramName, filePath) in filesToUpload)
            {
                var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(fileContent, paramName, Path.GetFileName(filePath));
            }

            request.Content = multipart;
        }
        else
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(new FunctionInvokeRequest { Parameters = parsed.Parameters }),
                Encoding.UTF8,
                "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Fkh-Protocol-Version", ClientCommand.ProtocolVersion.ToString());
        request.Headers.Add("X-Fkh-Client", ClientCommand.ClientApp);

        var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            // Retry responses still use { "message": "...", "retryAfterSeconds": N }
            var message = TryGetMessage(body) ?? body;
            var retrySeconds = 0;
            if (response.Headers.TryGetValues("Retry-After", out var retryValues))
                int.TryParse(retryValues.FirstOrDefault(), out retrySeconds);
            if (retrySeconds > 0)
            {
                if (!parsed.AsJson)
                {
                    if (!retryInProgress)
                    {
                        Console.Write($"{Ansi.Yellow}{message}{Ansi.Reset}");
                        retryInProgress = true;
                    }
                    Console.Write(".");
                }
                if (parsed.NoWait)
                {
                    if (!parsed.AsJson && retryInProgress) Console.WriteLine();
                    Console.WriteLine("--nowait specified, not waiting for completion.");
                    return 0;
                }
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cts.Token);
                // Refresh token (OIDC tokens are short-lived)
                token = await tokenProvider.GetTokenAsync();
                continue;
            }
        }

        if (retryInProgress && !parsed.AsJson) Console.WriteLine();

        if (response.IsSuccessStatusCode)
        {
            // Handle binary file responses (e.g. GetContainerEventLog returns base64-encoded .evtx)
            if (TrySaveBinaryResponse(body, parsed, parsed.Output))
            {
                return 0;
            }

            if (parsed.AsJson)
            {
                // Pretty-print the raw JSON from the server
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    Console.WriteLine(body);
                }
            }
            else
            {
                Console.WriteLine(FormatJsonAsText(body));
            }

            return 0;
        }

        if (parsed.AsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { error = $"Request failed ({(int)response.StatusCode}): {body}" }));
        }
        else
        {
            Console.Error.WriteLine($"{Ansi.Red}Request failed ({(int)response.StatusCode}): {body}{Ansi.Reset}");
        }
        return 1;
    }
}
catch (Exception ex)
{
    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
    }
    else
    {
        Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
    }
    return 1;
}

static ParsedArgs ParseArgs(string[] args, FunctionCatalogResponse catalog)
{
    if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
    {
        return new ParsedArgs { ShowHelp = true };
    }

    var command = args[0].ToLowerInvariant();
    var function = catalog.Functions.FirstOrDefault(f =>
        string.Equals(f.Name, command, StringComparison.OrdinalIgnoreCase));
    if (function is null)
    {
        throw new InvalidOperationException($"Unsupported command: {command}");
    }

    var parsed = new ParsedArgs { Command = function.Name };

    // Build a set of boolean parameter names for this function (flags, no value needed)
    var booleanParams = new HashSet<string>(
        function.Parameters.Where(p => string.Equals(p.Type, "boolean", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name),
        StringComparer.OrdinalIgnoreCase);

    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown argument: {arg}");
        }

        var key = arg[2..];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Parameter name cannot be empty after '--'.");
        }

        if (string.Equals(key, "nowait", StringComparison.OrdinalIgnoreCase))
        {
            parsed.NoWait = true;
            continue;
        }

        if (string.Equals(key, "asJson", StringComparison.OrdinalIgnoreCase))
        {
            parsed.AsJson = true;
            continue;
        }

        if (string.Equals(key, "open", StringComparison.OrdinalIgnoreCase))
        {
            parsed.Open = true;
            continue;
        }

        if (string.Equals(key, "useOIDC", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (string.Equals(key, "ghUser", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "backendUrl", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            if (i >= args.Length)
            {
                throw new InvalidOperationException($"Missing value for --{key}");
            }
            continue;
        }

        if (string.Equals(key, "output", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            if (i >= args.Length)
            {
                throw new InvalidOperationException("Missing value for --output");
            }
            parsed.Output = args[i];
            continue;
        }

        if (booleanParams.Contains(key))
        {
            // Boolean flag — presence means true, no value expected
            parsed.Parameters[key] = "true";
        }
        else if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            // No following value — treat as boolean flag
            parsed.Parameters[key] = "true";
        }
        else
        {
            i++;
            parsed.Parameters[key] = args[i];
        }
    }

    return parsed;
}

static string ResolveEndpoint(string route, CliSettings settings)
{
    var backendUrl = settings.BackendUrl;
    if (string.IsNullOrWhiteSpace(backendUrl))
    {
        throw new InvalidOperationException(
            "No function endpoint configured. Set FKH_BACKEND_URL or create ~/.fkh/settings.json with a backendUrl property.");
    }

    backendUrl = backendUrl.TrimEnd('/');
    return $"{backendUrl}/{route}";
}

static async Task<FunctionCatalogResponse> GetFunctionCatalogAsync(string? backendUrl)
{
    if (string.IsNullOrWhiteSpace(backendUrl))
    {
        throw new InvalidOperationException(
            "No backend URL configured. Set FKH_BACKEND_URL, create ~/.fkh/settings.json, or place fkh.settings.json next to the executable with a backendUrl property.");
    }

    if (!Uri.TryCreate(backendUrl, UriKind.Absolute, out var baseUri)
        || (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
    {
        throw new InvalidOperationException(
            $"Invalid backend URL: '{backendUrl}'. Must be an absolute http/https URL (e.g. https://fkh-myorg-backend.azurewebsites.net/api).");
    }

    var functionsUrl = $"{backendUrl.TrimEnd('/')}/functions";

    using var client = new HttpClient();
    HttpResponseMessage response;
    try
    {
        response = await client.GetAsync(functionsUrl);
    }
    catch (HttpRequestException ex)
    {
        throw new InvalidOperationException(
            $"Could not reach the backend at {functionsUrl}. {ex.Message}");
    }

    var body = await response.Content.ReadAsStringAsync();

    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
        response.StatusCode == System.Net.HttpStatusCode.Forbidden)
    {
        throw new InvalidOperationException(
            $"Authentication failed ({(int)response.StatusCode}) when calling {functionsUrl}. {body}");
    }

    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"Failed to fetch function catalog from {functionsUrl} ({(int)response.StatusCode}): {body}");
    }

    var catalog = JsonSerializer.Deserialize<FunctionCatalogResponse>(body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (catalog is null || catalog.Functions.Count == 0)
    {
        throw new InvalidOperationException(
            $"Function catalog returned from {functionsUrl} is empty or invalid.");
    }

    return catalog;
}

static void EnsureRequiredParameters(FunctionDefinition function, Dictionary<string, string> parameters)
{
    var knownNames = function.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unknown = parameters.Keys.Where(k => !knownNames.Contains(k)).ToList();
    if (unknown.Count > 0)
    {
        throw new InvalidOperationException(
            $"Unknown parameters for {function.Name}: {string.Join(", ", unknown)}");
    }

    // Check for missing required parameters
    var missing = function.Parameters
        .Where(p => p.Required
            && !string.Equals(p.Type, "file", StringComparison.OrdinalIgnoreCase)
            && (!parameters.TryGetValue(p.Name, out var v) || string.IsNullOrWhiteSpace(v)))
        .Select(p => p.Name)
        .ToList();

    // Also check file-type required params (they won't be in parameters dict yet)
    var missingFiles = function.Parameters
        .Where(p => p.Required
            && string.Equals(p.Type, "file", StringComparison.OrdinalIgnoreCase)
            && (!parameters.TryGetValue(p.Name, out var v) || string.IsNullOrWhiteSpace(v)))
        .Select(p => p.Name)
        .ToList();
    missing.AddRange(missingFiles);

    if (missing.Count > 0)
    {
        throw new InvalidOperationException(
            $"Missing required parameters for {function.Name}: {string.Join(", ", missing.Select(m => $"--{m}"))}");
    }

    // Auto-detect IP if not provided
    foreach (var parameter in function.Parameters.Where(p =>
        string.Equals(p.Name, "ip", StringComparison.OrdinalIgnoreCase)
        && (!parameters.TryGetValue(p.Name, out var v) || string.IsNullOrWhiteSpace(v))))
    {
        var detectedIp = DetectPublicIp();
        if (detectedIp is not null)
            parameters[parameter.Name] = detectedIp;
    }

    // Apply defaults for optional parameters
    foreach (var parameter in function.Parameters)
    {
        if (!parameters.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            if (!string.IsNullOrWhiteSpace(parameter.DefaultValue))
            {
                parameters[parameter.Name] = parameter.DefaultValue;
            }
        }
    }
}

static string? DetectPublicIp()
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = client.GetStringAsync("https://api.ipify.org?format=text").GetAwaiter().GetResult();
        var ip = response.Trim();
        return string.IsNullOrWhiteSpace(ip) ? null : ip;
    }
    catch
    {
        return null;
    }
}

static bool IsUsageError(string message)
{
    return message.StartsWith("Unsupported command:", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("Unknown argument:", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("Missing value for --", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("Parameter name cannot be empty", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("Unknown parameters for", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("Missing required parameters for", StringComparison.OrdinalIgnoreCase);
}

static void PrintCommandUsage(FunctionDefinition function)
{
    Console.WriteLine($"Usage: fkh {function.Name.ToLowerInvariant()} [options]");
    Console.WriteLine();
    Console.WriteLine($"  {function.Description}");
    Console.WriteLine();
    if (function.Parameters.Count > 0)
    {
        Console.WriteLine("Parameters:");
        foreach (var parameter in function.Parameters)
        {
            var requiredText = parameter.Required ? "required" : "optional";
            var defaultText = string.IsNullOrWhiteSpace(parameter.DefaultValue)
                ? string.Empty
                : $", default='{parameter.DefaultValue}'";
            Console.WriteLine(
                $"    --{parameter.Name} <{parameter.Type}> [{requiredText}{defaultText}]");
            Console.WriteLine(
                $"        {parameter.Description}");
        }
    }
    Console.WriteLine();
    PrintCommonOptions();
}

static void PrintClientCommandUsage(ClientCommand cmd)
{
    Console.WriteLine($"Usage: fkh {cmd.Name.ToLowerInvariant()} [options]");
    Console.WriteLine();
    Console.WriteLine($"  {cmd.Description}");
    Console.WriteLine();
    if (cmd.Parameters.Count > 0)
    {
        Console.WriteLine("Parameters:");
        foreach (var param in cmd.Parameters)
        {
            var requiredText = param.Required ? "required" : "optional";
            Console.WriteLine($"    --{param.Name} <{param.Type}> [{requiredText}]");
            Console.WriteLine($"        {param.Description}");
        }
    }
    Console.WriteLine();
    PrintCommonOptions();
}

static void PrintCommonOptions()
{
    Console.WriteLine("Common options:");
    Console.WriteLine("    --backendUrl <url>  Override the backend URL");
    Console.WriteLine("    --ghUser <user>     GitHub user account for gh auth token (or GH_USER env var)");
    Console.WriteLine("    --useOIDC           Fetch OIDC token from GitHub Actions (auto-refreshes)");
    Console.WriteLine("    --nowait            Don't wait for completion");
    Console.WriteLine("    --asJson            Output the result as JSON");
    Console.WriteLine("    --output <path>     Save binary output to a file");
    Console.WriteLine("    --open              Open the downloaded file after saving");
    Console.WriteLine("    -h, --help          Show help");
}

static void PrintUsage(FunctionCatalogResponse catalog)
{
    Console.WriteLine("FKH CLI");
    Console.WriteLine();
    Console.WriteLine("Usage: fkh <command> [--key \"value\" ...]");
    Console.WriteLine();
    PrintCommonOptions();
    Console.WriteLine();
    Console.WriteLine("Commands:");

    // Determine column width for alignment
    var allNames = catalog.Functions.Select(f => f.Name.ToLowerInvariant()).ToList();
    var clientCmds = ClientCommands.All;
    allNames.AddRange(clientCmds.Select(c => c.Name.ToLowerInvariant()));
    var maxLen = allNames.Max(n => n.Length);

    foreach (var function in catalog.Functions)
    {
        var name = function.Name.ToLowerInvariant();
        Console.WriteLine($"  {name.PadRight(maxLen)}  {function.Description}");
    }

    if (clientCmds.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Client-side commands:");
        foreach (var cmd in clientCmds)
        {
            var name = cmd.Name.ToLowerInvariant();
            Console.WriteLine($"  {name.PadRight(maxLen)}  {cmd.Description}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Use 'fkh <command> --help' for more information about a command.");
}

static async Task<int> GenerateCompletionDataAsync(string[] args)
{
    var settings = LoadSettings();
    var cliBackendUrl = FindArgValue(args, "backendUrl");
    if (!string.IsNullOrWhiteSpace(cliBackendUrl))
        settings.BackendUrl = cliBackendUrl;

    var commands = new List<object>();

    // Client-side commands (always available, no backend needed)
    foreach (var cmd in ClientCommands.All)
    {
        commands.Add(new
        {
            name = cmd.Name.ToLowerInvariant(),
            description = cmd.Description,
            parameters = cmd.Parameters.Select(p => new
            {
                name = p.Name,
                type = p.Type,
                description = p.Description,
                required = p.Required
            })
        });
    }

    // Catalog functions (from backend)
    try
    {
        var catalog = await GetFunctionCatalogAsync(settings.BackendUrl);
        foreach (var func in catalog.Functions)
        {
            commands.Add(new
            {
                name = func.Name.ToLowerInvariant(),
                description = func.Description,
                parameters = func.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type,
                    description = p.Description,
                    required = p.Required
                })
            });
        }
    }
    catch
    {
        // Backend unreachable — still return client-side commands
    }

    var json = JsonSerializer.Serialize(commands, new JsonSerializerOptions { WriteIndented = false });
    Console.WriteLine(json);

    // Write cache file for shell completers (fast file read, no process spawn on tab)
    try
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fkh");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "completions.json"), json);
    }
    catch
    {
        // Non-critical — completion still works via stdout
    }

    return 0;
}

static string? FindArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], $"--{name}", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string? TryGetMessage(string responseBody)
{
    if (string.IsNullOrWhiteSpace(responseBody))
    {
        return null;
    }

    try
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.String)
        {
            return messageElement.GetString();
        }
    }
    catch
    {
        // Plain text response.
    }

    return null;
}

static bool TrySaveBinaryResponse(string body, ParsedArgs parsed, string? outputPath)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Handle base64-encoded file responses (e.g. eventLog)
        if (root.TryGetProperty("eventLog", out var eventLogProp) && eventLogProp.ValueKind == JsonValueKind.String)
        {
            var base64 = eventLogProp.GetString();
            if (string.IsNullOrWhiteSpace(base64))
            {
                Console.Error.WriteLine($"{Ansi.Red}No event log data returned.{Ansi.Reset}");
                return true;
            }

            string fileName;
            if (outputPath != null)
            {
                fileName = outputPath;
            }
            else
            {
                var appName = root.TryGetProperty("container", out var containerProp) && containerProp.ValueKind == JsonValueKind.String
                    ? containerProp.GetString() ?? "container"
                    : "container";
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm");
                fileName = Path.Combine(Path.GetTempPath(), $"{appName}-eventlog-{timestamp}.evtx");
            }

            var bytes = Convert.FromBase64String(base64);
            File.WriteAllBytes(fileName, bytes);
            Console.WriteLine($"{Ansi.Cyan}Event log saved to {Path.GetFullPath(fileName)} ({bytes.Length / 1024.0:N1} KB){Ansi.Reset}");

            if (parsed.Open)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(fileName)) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{Ansi.Yellow}Could not open file: {ex.Message}{Ansi.Reset}");
                }
            }

            return true;
        }
    }
    catch
    {
        // Not a binary response — fall through to normal handling
    }
    return false;
}

static string FormatJsonAsText(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        FormatElement(sb, doc.RootElement, indent: 0);
        return sb.ToString().TrimEnd();
    }
    catch
    {
        return json;
    }
}

static void FormatElement(StringBuilder sb, JsonElement element, int indent)
{
    var prefix = new string(' ', indent * 2);

    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var prop in element.EnumerateObject())
            {
                var label = prop.Name;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    sb.AppendLine($"{prefix}{Ansi.Cyan}{label}:{Ansi.Reset}");
                    FormatElement(sb, prop.Value, indent + 1);
                }
                else if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var arr = prop.Value;
                    if (arr.GetArrayLength() == 0)
                        continue;
                    // Check if it's an array of simple values
                    var first = arr[0];
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine($"{prefix}{Ansi.Cyan}{label}:{Ansi.Reset}");
                        foreach (var item in arr.EnumerateArray())
                        {
                            sb.AppendLine();
                            FormatElement(sb, item, indent + 1);
                        }
                    }
                    else
                    {
                        var values = string.Join(", ", arr.EnumerateArray().Select(v => v.ToString()));
                        sb.AppendLine($"{prefix}{Ansi.Cyan}{label}:{Ansi.Reset} {values}");
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    // Skip null values
                }
                else
                {
                    sb.AppendLine($"{prefix}{Ansi.Cyan}{label}:{Ansi.Reset} {prop.Value}");
                }
            }
            break;

        case JsonValueKind.Array:
            foreach (var item in element.EnumerateArray())
            {
                FormatElement(sb, item, indent);
                sb.AppendLine();
            }
            break;

        default:
            sb.AppendLine($"{prefix}{element}");
            break;
    }
}

static CliSettings LoadSettings()
{
    // 1. Environment variable takes priority
    var envUrl = Environment.GetEnvironmentVariable("FKH_BACKEND_URL");
    if (!string.IsNullOrWhiteSpace(envUrl))
    {
        return new CliSettings { BackendUrl = envUrl };
    }

    // 2. User profile settings (~/.fkh/settings.json) — works for dotnet tool installs
    var userSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".fkh", "settings.json");
    var loaded = TryLoadSettingsFile(userSettingsPath);
    if (loaded is not null) return loaded;

    // 3. Settings file next to the executable
    var localSettingsPath = Path.Combine(AppContext.BaseDirectory, "fkh.settings.json");
    return TryLoadSettingsFile(localSettingsPath) ?? new CliSettings();
}

static CliSettings? TryLoadSettingsFile(string path)
{
    if (!File.Exists(path)) return null;
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<CliSettings>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });
}

sealed class ParsedArgs
{
    public bool ShowHelp { get; init; }
    public string? Command { get; init; }
    public bool NoWait { get; set; }
    public bool AsJson { get; set; }
    public bool Open { get; set; }
    public string? Output { get; set; }
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed class CliSettings
{
    public string? BackendUrl { get; set; }
    public string? User { get; set; }
    public bool UseOidc { get; set; }
}

sealed class FunctionCatalogResponse
{
    [JsonPropertyName("functions")]
    public List<FunctionDefinition> Functions { get; init; } = new();
}

sealed class FunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("route")]
    public string Route { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public List<FunctionParameterDefinition> Parameters { get; init; } = new();
}

sealed class FunctionParameterDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; init; }
}

sealed class FunctionInvokeRequest
{
    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

static class Ansi
{
    static readonly bool _enabled = SupportsAnsi();

    public static string Cyan => _enabled ? "\x1b[36m" : "";
    public static string Red => _enabled ? "\x1b[31m" : "";
    public static string Yellow => _enabled ? "\x1b[33m" : "";
    public static string Dim => _enabled ? "\x1b[2m" : "";
    public static string Reset => _enabled ? "\x1b[0m" : "";

    static bool SupportsAnsi()
    {
        if (Console.IsOutputRedirected) return false;
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
        if (Environment.GetEnvironmentVariable("WT_SESSION") is not null) return true;
        if (Environment.GetEnvironmentVariable("TERM_PROGRAM") is not null) return true;
        if (Environment.GetEnvironmentVariable("TERM") is not null) return true;
        if (TryEnableVirtualTerminalProcessing()) return true;
        return false;
    }

    static bool TryEnableVirtualTerminalProcessing()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return false;

        var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        if (!GetConsoleMode(handle, out var mode)) return false;

        const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0) return true;

        return SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
