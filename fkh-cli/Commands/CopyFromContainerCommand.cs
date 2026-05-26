using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

sealed class CopyFromContainerCommand : ClientCommand
{
    public override string Name => "CopyFromContainer";
    public override string Description => "Copies a file from a container to the local machine.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "name", Type = "string", Description = "Name of the container.", Required = true },
        new() { Name = "containerFilename", Type = "string", Description = "Path to the file inside the container (supports wildcards).", Required = true },
        new() { Name = "localFilename", Type = "string", Description = "Local path to save the file to.", Required = true }
    ];

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

        if (!parameters.TryGetValue("containerFilename", out var filename) || string.IsNullOrWhiteSpace(filename))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --containerFilename{Ansi.Reset}");
            return 1;
        }

        if (!parameters.TryGetValue("localFilename", out var localFile) || string.IsNullOrWhiteSpace(localFile))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --localFilename{Ansi.Reset}");
            return 1;
        }

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        Console.WriteLine($"{Ansi.Dim}Downloading {filename} from container '{containerName}' via backend...{Ansi.Reset}");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/CopyFileFromContainer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddProtocolHeaders(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new FunctionInvokeRequest
            {
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = containerName,
                    ["containerFilename"] = filename
                }
            }),
            Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{Ansi.Red}Download failed ({(int)response.StatusCode}): {body}{Ansi.Reset}");
            return 1;
        }

        using var doc = JsonDocument.Parse(body);
        var fileContent = doc.RootElement.GetProperty("fileContent").GetString();
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            Console.Error.WriteLine($"{Ansi.Red}Backend returned empty file content.{Ansi.Reset}");
            return 1;
        }

        var fileBytes = Convert.FromBase64String(fileContent);
        await File.WriteAllBytesAsync(localFile, fileBytes);

        Console.WriteLine($"{Ansi.Cyan}Saved to {Path.GetFullPath(localFile)}{Ansi.Reset}");
        return 0;
    }
}
