using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

sealed class EditCommand : ClientCommand
{
    public override string Name => "Edit";
    public override string Description => "Copies a file from a container, opens it in an editor, and copies it back when the editor closes.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "name", Type = "string", Description = "Name of the container.", Required = true },
        new() { Name = "containerFilename", Type = "string", Description = "Path to the file inside the container (supports wildcards).", Required = true },
        new() { Name = "wait", Type = "flag", Description = "Block the process until the editor closes (default opens a new window).", Required = false }
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

        // Default: re-invoke fkh edit in a new window (with --wait) so the current process returns immediately
        if (!args.Any(a => string.Equals(a, "--wait", StringComparison.OrdinalIgnoreCase)))
        {
            var exePath = Environment.ProcessPath ?? "fkh";
            var reArgs = args.Concat(new[] { "--wait" });

            // When running under 'dotnet run', ProcessPath is dotnet.exe — we need
            // to re-invoke as 'dotnet run -- <args>' instead of 'dotnet.exe <args>'.
            if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
                exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                reArgs = new[] { "run", "--" }.Concat(reArgs);
            }

            Console.WriteLine($"{Ansi.Cyan}Launching edit in a new window...{Ansi.Reset}");
            if (!LaunchInNewTerminal(exePath, reArgs))
            {
                Console.Error.WriteLine($"{Ansi.Yellow}Could not open a new terminal window — running inline.{Ansi.Reset}");
                // Fall through to inline execution below
            }
            else
            {
                return 0;
            }
        }

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        // Download file from container via backend
        Console.WriteLine($"{Ansi.Dim}Downloading {filename} from container '{containerName}' via backend...{Ansi.Reset}");

        string resolvedFilename;
        byte[] fileBytes;
        using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
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
            resolvedFilename = doc.RootElement.GetProperty("fileName").GetString() ?? Path.GetFileName(filename);

            if (string.IsNullOrWhiteSpace(fileContent))
            {
                Console.Error.WriteLine($"{Ansi.Red}Backend returned empty file content.{Ansi.Reset}");
                return 1;
            }

            fileBytes = Convert.FromBase64String(fileContent);
        }

        // Write to temp file
        var tempFileName = $"fkh-edit-{resolvedFilename}";
        var tempDir = Path.GetTempPath();
        var localFile = Path.Combine(tempDir, tempFileName);
        await File.WriteAllBytesAsync(localFile, fileBytes);

        var beforeHash = GetFileHash(localFile);

        // Open in editor and wait for it to close
        var editor = GetEditorCommand();
        Console.WriteLine($"{Ansi.Cyan}Opening in {Path.GetFileNameWithoutExtension(editor)} — save and close to upload changes...{Ansi.Reset}");
        var editorProcess = Process.Start(new ProcessStartInfo
        {
            FileName = editor,
            ArgumentList = { localFile },
            UseShellExecute = false,
        });
        if (editorProcess is null)
        {
            Console.Error.WriteLine($"{Ansi.Red}Could not start editor ({editor}).{Ansi.Reset}");
            return 1;
        }
        editorProcess.WaitForExit();

        // Check if file was modified
        var afterHash = GetFileHash(localFile);
        if (beforeHash == afterHash)
        {
            Console.WriteLine($"{Ansi.Yellow}No changes detected — skipping upload.{Ansi.Reset}");
            File.Delete(localFile);
            return 0;
        }

        // Upload back to container via backend
        Console.WriteLine($"{Ansi.Dim}Uploading changes to {filename} in container '{containerName}' via backend...{Ansi.Reset}");

        using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            using var content = new MultipartFormDataContent();

            var parametersJson = JsonSerializer.Serialize(new FunctionInvokeRequest
            {
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = containerName,
                    ["containerFilename"] = filename
                }
            });
            content.Add(new StringContent(parametersJson, Encoding.UTF8, "application/json"), "parameters");

            var updatedFileBytes = await File.ReadAllBytesAsync(localFile);
            content.Add(new ByteArrayContent(updatedFileBytes), "file", resolvedFilename);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/CopyFileToContainer");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            AddProtocolHeaders(request);
            request.Content = content;

            var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"{Ansi.Red}Upload failed ({(int)response.StatusCode}): {body}{Ansi.Reset}");
                Console.Error.WriteLine($"{Ansi.Yellow}Your changes are preserved locally at: {localFile}{Ansi.Reset}");
                return 1;
            }
        }

        Console.WriteLine($"{Ansi.Cyan}File updated in container.{Ansi.Reset}");
        File.Delete(localFile);
        return 0;
    }

    private static string GetFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
