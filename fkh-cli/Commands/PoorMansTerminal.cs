using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

sealed class PoorMansTerminal
{
    private readonly string _backendUrl;
    private readonly TokenProvider _tokenProvider;
    private readonly string _containerName;
    private readonly int _width;
    private string _currentPath = "C:\\";

    public PoorMansTerminal(string backendUrl, TokenProvider tokenProvider, string containerName, int width = 220)
    {
        _backendUrl = backendUrl.TrimEnd('/');
        _tokenProvider = tokenProvider;
        _containerName = containerName;
        _width = width;
    }

    public async Task<int> RunAsync()
    {
        Console.WriteLine($"{Ansi.Yellow}Backend terminal — no tab completion or arrow keys{Ansi.Reset}");
        Console.WriteLine($"{Ansi.Dim}Type 'exit' or 'quit' to close.{Ansi.Reset}");
        Console.WriteLine();

        var initResult = await InvokeAsync(". 'C:\\run\\prompt.ps1' -silent; Write-Output \"@@FKH_PWD:$($PWD.Path)\"");
        if (initResult is not null)
        {
            var initPath = ExtractPwd(initResult.Output);
            if (!string.IsNullOrWhiteSpace(initPath))
                _currentPath = initPath;
        }

        while (true)
        {
            Console.Write($"{Ansi.Cyan}PS {_currentPath}{Ansi.Reset}> ");
            var input = Console.ReadLine();

            if (input is null)
                break;

            var trimmed = input.Trim();

            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "quit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase))
            {
                Console.Clear();
                continue;
            }

            var escapedPath = _currentPath.Replace("'", "''");
            var widthCmd = $"try {{ $Host.UI.RawUI.BufferSize = [System.Management.Automation.Host.Size]::new({_width}, 9999) }} catch {{}}";
            var wrapped = $"{widthCmd}; . 'C:\\run\\prompt.ps1' -silent; Set-Location '{escapedPath}'; {trimmed}; Write-Output \"@@FKH_PWD:$($PWD.Path)\"";

            var result = await InvokeAsync(wrapped);
            if (result is null)
                continue;

            var outputLines = result.Output.Split('\n');
            var newPath = _currentPath;
            var displayLines = new List<string>();

            foreach (var line in outputLines)
            {
                var trimmedLine = line.TrimEnd('\r');
                if (trimmedLine.StartsWith("@@FKH_PWD:", StringComparison.Ordinal))
                    newPath = trimmedLine["@@FKH_PWD:".Length..].Trim();
                else
                    displayLines.Add(trimmedLine);
            }

            _currentPath = newPath;
            var output = string.Join('\n', displayLines).TrimEnd();
            if (!string.IsNullOrEmpty(output))
                Console.WriteLine(output);

            if (!string.IsNullOrWhiteSpace(result.Stderr))
                Console.Error.WriteLine($"{Ansi.Red}{result.Stderr.TrimEnd()}{Ansi.Reset}");
        }

        return 0;
    }

    private static string? ExtractPwd(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmedLine = line.TrimEnd('\r');
            if (trimmedLine.StartsWith("@@FKH_PWD:", StringComparison.Ordinal))
                return trimmedLine["@@FKH_PWD:".Length..].Trim();
        }
        return null;
    }

    private async Task<InvokeResult?> InvokeAsync(string command)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

            // Launch the script; the backend returns a jobId immediately.
            var launchToken = await _tokenProvider.GetTokenAsync();
            var (launchOk, launchBody) = await PostAsync(httpClient, "InvokeScript", launchToken,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = _containerName,
                    ["command"] = command,
                });
            if (!launchOk)
            {
                Console.Error.WriteLine($"{Ansi.Red}Error: {launchBody}{Ansi.Reset}");
                return null;
            }

            string jobId;
            string container;
            using (var doc = JsonDocument.Parse(launchBody))
            {
                var root = doc.RootElement;
                jobId = root.TryGetProperty("jobId", out var j) ? j.GetString() ?? "" : "";
                container = root.TryGetProperty("container", out var c) ? c.GetString() ?? _containerName : _containerName;
            }
            if (string.IsNullOrEmpty(jobId))
            {
                Console.Error.WriteLine($"{Ansi.Red}Error: {launchBody}{Ansi.Reset}");
                return null;
            }

            var jobParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = container,
                ["jobId"] = jobId,
            };

            // Poll until the job is no longer running.
            while (true)
            {
                var token = await _tokenProvider.GetTokenAsync();
                var (ok, body) = await PostAsync(httpClient, "ScriptStatus", token, jobParams);
                if (!ok)
                {
                    Console.Error.WriteLine($"{Ansi.Red}Error: {body}{Ansi.Reset}");
                    return null;
                }
                using var doc = JsonDocument.Parse(body);
                var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (!string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase))
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            // Fetch the result (one-shot).
            var resultToken = await _tokenProvider.GetTokenAsync();
            var (rok, resultBody) = await PostAsync(httpClient, "ScriptResult", resultToken, jobParams);
            if (!rok)
            {
                Console.Error.WriteLine($"{Ansi.Red}Error: {resultBody}{Ansi.Reset}");
                return null;
            }

            using (var doc = JsonDocument.Parse(resultBody))
            {
                var root = doc.RootElement;
                var output = root.TryGetProperty("output", out var op) ? op.GetString() ?? "" : "";
                var error = root.TryGetProperty("error", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString() : null;
                return new InvokeResult(output, error);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            return null;
        }
    }

    private async Task<(bool Ok, string Body)> PostAsync(HttpClient httpClient, string route, string token, Dictionary<string, string> parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_backendUrl}/{route}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ClientCommand.AddProtocolHeaders(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new FunctionInvokeRequest { Parameters = parameters }),
            Encoding.UTF8, "application/json");
        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    private record InvokeResult(string Output, string? Stderr);
}
