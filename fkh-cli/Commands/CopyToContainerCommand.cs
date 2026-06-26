using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

sealed class CopyToContainerCommand : ClientCommand
{
    public override string Name => "CopyToContainer";
    public override string Description => "Copies a local file to a container.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "name", Type = "string", Description = "Name of the container.", Required = true },
        new() { Name = "localFilename", Type = "string", Description = "Local path of the file to upload.", Required = true },
        new() { Name = "containerFilename", Type = "string", Description = "Destination path inside the container.", Required = true }
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

        if (!parameters.TryGetValue("localFilename", out var localFile) || string.IsNullOrWhiteSpace(localFile))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --localFilename{Ansi.Reset}");
            return 1;
        }

        if (!File.Exists(localFile))
        {
            Console.Error.WriteLine($"{Ansi.Red}File not found: {localFile}{Ansi.Reset}");
            return 1;
        }

        if (!parameters.TryGetValue("containerFilename", out var filename) || string.IsNullOrWhiteSpace(filename))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --containerFilename{Ansi.Reset}");
            return 1;
        }

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        Console.WriteLine($"{Ansi.Dim}Uploading {localFile} to {filename} in container '{containerName}' via backend...{Ansi.Reset}");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var content = new MultipartFormDataContent();

        // Add parameters as a JSON field
        var parametersJson = JsonSerializer.Serialize(new FunctionInvokeRequest
        {
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = containerName,
                ["containerFilename"] = filename
            }
        });
        content.Add(new StringContent(parametersJson, Encoding.UTF8, "application/json"), "parameters");

        // Add the file
        var fileBytes = await File.ReadAllBytesAsync(localFile);
        content.Add(new ByteArrayContent(fileBytes), "file", Path.GetFileName(localFile));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/CopyFileToContainer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddProtocolHeaders(request);
        request.Content = content;

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{Ansi.Red}Upload failed ({(int)response.StatusCode}): {body}{Ansi.Reset}");
            return 1;
        }

        Console.WriteLine($"{Ansi.Cyan}File copied to container.{Ansi.Reset}");
        return 0;
    }
}
