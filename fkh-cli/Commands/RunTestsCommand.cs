using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

sealed class RunTestsCommand : ClientCommand
{
    private static readonly HashSet<string> ParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "tenant", "extensionId", "appName", "testCodeunitRange", "timeoutMinutes", "output"
    };
    private static readonly Regex TenantPattern = new("^[A-Za-z0-9][A-Za-z0-9-]{0,127}$", RegexOptions.CultureInvariant);
    private const int DefaultTimeoutMinutes = 30;
    private const int MaxTimeoutMinutes = 120;

    public override string Name => "RunTests";
    public override string Description => "Runs tests from a published test app inside a Business Central container.";
    public override bool SupportsNowait => false;
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "name", Type = "string", Description = "Name of the container.", Required = true },
        new() { Name = "tenant", Type = "string", Description = "Business Central tenant. Default: default", Required = false },
        new() { Name = "extensionId", Type = "string", Description = "ID of the published test app.", Required = true },
        new() { Name = "appName", Type = "string", Description = "Optional test app name used for validation and reporting.", Required = false },
        new() { Name = "testCodeunitRange", Type = "string", Description = "Optional Business Central filter selecting test codeunit IDs.", Required = false },
        new() { Name = "timeoutMinutes", Type = "string", Description = "Hard timeout for the test run inside the container (1-120). Default: 30", Required = false },
        new() { Name = "output", Type = "string", Description = "Local destination for JUnit XML.", Required = true }
    ];

    public override async Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
    {
        RunTestsRequest request;
        try
        {
            request = ValidateParameters(args);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            return 2;
        }

        try
        {
            if (File.Exists(request.Output))
                File.Delete(request.Output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"{Ansi.Red}Could not prepare JUnit output '{request.Output}': {ex.Message}{Ansi.Reset}");
            return 2;
        }

        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 2;

        var parameters = ParseClientArgs(args);
        var tokenProvider = CreateTokenProvider(parameters, settings);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        try
        {
            while (true)
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/RunTests");
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokenProvider.GetTokenAsync());
                AddProtocolHeaders(httpRequest);
                httpRequest.Content = new StringContent(
                    JsonSerializer.Serialize(new FunctionInvokeRequest
                    {
                        Parameters = request.ToParameters()
                    }),
                    Encoding.UTF8,
                    "application/json");

                using var response = await httpClient.SendAsync(httpRequest);
                var body = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    var retrySeconds = GetRetrySeconds(response);
                    if (!asJson)
                        Console.Write(".");
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds));
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"{Ansi.Red}Test infrastructure failed ({(int)response.StatusCode}): {GetErrorMessage(body)}{Ansi.Reset}");
                    return 2;
                }

                var result = JsonSerializer.Deserialize<RunTestsResponse>(body, JsonOptions);
                if (result is null)
                {
                    Console.Error.WriteLine($"{Ansi.Red}Backend returned an empty test result.{Ansi.Reset}");
                    return 2;
                }

                var exitCode = MaterializeResult(result, request.Output, out var materializationError);
                if (exitCode == 2)
                {
                    Console.Error.WriteLine($"{Ansi.Red}{materializationError}{Ansi.Reset}");
                    return 2;
                }
                WriteSummary(result, asJson);
                return exitCode;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"{Ansi.Red}Test infrastructure failed: {ex.Message}{Ansi.Reset}");
            return 2;
        }
    }

    internal static RunTestsRequest ValidateParameters(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, "--nowait", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("--nowait is not supported by runtests because JUnit must be materialized before the command returns.");

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--", StringComparison.Ordinal)
                && ParameterNames.Contains(argument[2..])
                && (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Missing value for {argument}");
        }

        var parameters = ParseClientArgs(args);
        var unknownParameters = parameters.Keys.Where(key => !ParameterNames.Contains(key)).ToList();
        if (unknownParameters.Count > 0)
            throw new InvalidOperationException($"Unknown parameters for runtests: {string.Join(", ", unknownParameters)}.");

        var name = GetRequired(parameters, "name");
        var extensionId = GetRequired(parameters, "extensionId");
        var output = GetRequired(parameters, "output");

        if (!name.All(character => char.IsLetterOrDigit(character) || character == '-'))
            throw new InvalidOperationException("--name may only contain letters, digits, and hyphens.");

        if (!Guid.TryParse(extensionId, out var parsedExtensionId) || parsedExtensionId == Guid.Empty)
            throw new InvalidOperationException("--extensionId must be a non-empty GUID.");

        var tenant = parameters.TryGetValue("tenant", out var tenantValue)
            ? tenantValue
            : "default";
        if (!TenantPattern.IsMatch(tenant))
            throw new InvalidOperationException("--tenant may only contain letters, digits, and hyphens.");

        parameters.TryGetValue("appName", out var appName);
        if (appName?.IndexOfAny(['\r', '\n']) >= 0 || appName?.Length > 250)
            throw new InvalidOperationException("--appName is invalid.");

        parameters.TryGetValue("testCodeunitRange", out var testCodeunitRange);
        testCodeunitRange = testCodeunitRange?.Trim();
        if (testCodeunitRange is not null
            && (testCodeunitRange.Length == 0 || testCodeunitRange.IndexOfAny(['\r', '\n']) >= 0 || testCodeunitRange.Length > 250))
            throw new InvalidOperationException("--testCodeunitRange is invalid.");

        var timeoutMinutes = DefaultTimeoutMinutes;
        if (parameters.TryGetValue("timeoutMinutes", out var timeoutValue) && !string.IsNullOrWhiteSpace(timeoutValue))
        {
            if (!int.TryParse(timeoutValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out timeoutMinutes)
                || timeoutMinutes < 1 || timeoutMinutes > MaxTimeoutMinutes)
                throw new InvalidOperationException($"--timeoutMinutes must be a whole number between 1 and {MaxTimeoutMinutes}.");
        }

        return new RunTestsRequest(name, tenant, parsedExtensionId.ToString(), appName, testCodeunitRange, timeoutMinutes, output);
    }

    internal static int MaterializeResult(RunTestsResponse result, string outputPath)
        => MaterializeResult(result, outputPath, out _);

    internal static int MaterializeResult(RunTestsResponse result, string outputPath, out string? error)
    {
        error = null;
        if (string.Equals(result.Outcome, "infrastructureFailure", StringComparison.OrdinalIgnoreCase))
        {
            error = "Backend reported a test infrastructure failure.";
            return 2;
        }

        if (string.IsNullOrWhiteSpace(result.JunitBase64))
        {
            error = "Backend returned no JUnit.";
            return 2;
        }

        byte[] junitBytes;
        bool junitFailed;
        try
        {
            junitBytes = Convert.FromBase64String(result.JunitBase64);
            junitFailed = ValidateJUnit(junitBytes);
        }
        catch (Exception ex) when (ex is FormatException or XmlException or InvalidOperationException)
        {
            error = $"Backend returned invalid JUnit: {ex.Message}";
            return 2;
        }

        var expectedFailure = result.Outcome.ToLowerInvariant() switch
        {
            "passed" => false,
            "failed" => true,
            _ => (bool?)null
        };
        if (expectedFailure is null || expectedFailure.Value != junitFailed)
        {
            error = "Backend test outcome does not match JUnit.";
            return 2;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(tempPath, junitBytes);
                File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"Could not write JUnit to '{outputPath}': {ex.Message}";
            return 2;
        }

        return result.Outcome.ToLowerInvariant() switch
        {
            "passed" => 0,
            "failed" => 1,
            _ => 2
        };
    }

    private static bool ValidateJUnit(byte[] junitBytes)
    {
        if (junitBytes.Length == 0)
            throw new InvalidOperationException("JUnit is empty.");

        using var stream = new MemoryStream(junitBytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root is null || root.Name.LocalName is not ("testsuite" or "testsuites"))
            throw new InvalidOperationException("JUnit root element is invalid.");

        var testCases = root.DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "testcase")
            .ToList();
        if (testCases.Count == 0)
            throw new InvalidOperationException("JUnit contains no tests.");

        return testCases.Any(testCase => testCase.Elements().Any(
            element => element.Name.LocalName is "failure" or "error"));
    }

    private static string GetRequired(Dictionary<string, string> parameters, string name)
    {
        if (!parameters.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required parameter --{name}");
        return value;
    }

    private static int GetRetrySeconds(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values)
            && int.TryParse(values.FirstOrDefault(), out var retrySeconds)
            && retrySeconds > 0)
            return retrySeconds;
        return 5;
    }

    private static string GetErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? "Unknown backend error.";
        }
        catch (JsonException)
        {
        }
        return "The backend rejected the test request.";
    }

    private static void WriteSummary(RunTestsResponse result, bool asJson)
    {
        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result.Outcome,
                result.Tests,
                result.Failures,
                result.Errors,
                result.Skipped,
                result.DurationSeconds,
                result.Log
            }));
            return;
        }

        foreach (var line in result.Log ?? [])
            Console.WriteLine(line);
        Console.WriteLine($"Tests: {result.Tests}, Failures: {result.Failures}, Errors: {result.Errors}, Skipped: {result.Skipped}, Duration: {result.DurationSeconds:N3}s");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal sealed record RunTestsRequest(string Name, string Tenant, string ExtensionId, string? AppName, string? TestCodeunitRange, int TimeoutMinutes, string Output)
    {
        public Dictionary<string, string> ToParameters()
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = Name,
                ["tenant"] = Tenant,
                ["extensionId"] = ExtensionId,
                ["timeoutMinutes"] = TimeoutMinutes.ToString(CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(AppName))
                parameters["appName"] = AppName;
            if (!string.IsNullOrWhiteSpace(TestCodeunitRange))
                parameters["testCodeunitRange"] = TestCodeunitRange;
            return parameters;
        }
    }

    internal sealed class RunTestsResponse
    {
        public string Outcome { get; init; } = "infrastructureFailure";
        public int Tests { get; init; }
        public int Failures { get; init; }
        public int Errors { get; init; }
        public int Skipped { get; init; }
        public double DurationSeconds { get; init; }
        public string? JunitBase64 { get; init; }
        public string[]? Log { get; init; }
    }
}