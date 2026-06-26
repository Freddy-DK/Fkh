using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

sealed class DownloadDatabaseCommand : ClientCommand
{
    public override string Name => "DownloadDatabase";
    public override string Description => "Downloads a database backup (.bak) from blob storage. Specify the database as 'name/version' (e.g. 'grmwithtests/latest').";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "database", Type = "string", Description = "Database to download as 'name/version' (e.g. 'grmwithtests/latest' or 'bhgwithtests/202604011126').", Required = true },
        new() { Name = "output", Type = "string", Description = "File path to save the downloaded .bak file. Defaults to 'name-version.bak' in the current directory.", Required = false }
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

        if (!parameters.TryGetValue("database", out var database) || string.IsNullOrWhiteSpace(database))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --database{Ansi.Reset}");
            return 1;
        }

        var parts = database.Split('/', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            Console.Error.WriteLine($"{Ansi.Red}Invalid database value '{database}'. Expected 'name/version' (e.g. 'grmwithtests/latest' or 'bhgwithtests/202604011126').{Ansi.Reset}");
            return 1;
        }

        var dbName = parts[0];
        var dbVersion = parts[1];

        parameters.TryGetValue("output", out var outputPath);

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        // Step 1: Get read-only SAS URL from backend
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Requesting download SAS from backend...{Ansi.Reset}");

        string sasUrl;
        using (var httpClient = new HttpClient())
        {
            var sasRequest = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/GetDatabaseDownloadSas");
            sasRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            AddProtocolHeaders(sasRequest);
            sasRequest.Content = new StringContent(
                JsonSerializer.Serialize(new FunctionInvokeRequest
                {
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                }),
                Encoding.UTF8, "application/json");

            var sasResponse = await httpClient.SendAsync(sasRequest);
            var sasBody = await sasResponse.Content.ReadAsStringAsync();

            if (!sasResponse.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"{Ansi.Red}Failed to get SAS ({(int)sasResponse.StatusCode}): {sasBody}{Ansi.Reset}");
                return 1;
            }

            using var doc = JsonDocument.Parse(sasBody);
            sasUrl = doc.RootElement.GetProperty("sasUrl").GetString()
                ?? throw new InvalidOperationException("Backend returned empty SAS URL.");
        }

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}SAS URL obtained (valid for 60 minutes).{Ansi.Reset}");

        // Step 2: Read all.json manifest to resolve version
        var containerUri = new Uri(sasUrl);
        var containerClient = new Azure.Storage.Blobs.BlobContainerClient(containerUri);
        var manifestBlobName = $"{dbName}/all.json";
        var manifestClient = containerClient.GetBlobClient(manifestBlobName);

        DatabaseManifest manifest;
        try
        {
            var downloadResponse = await manifestClient.DownloadContentAsync();
            var existingJson = downloadResponse.Value.Content.ToString();
            manifest = JsonSerializer.Deserialize<DatabaseManifest>(existingJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new DatabaseManifest();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            Console.Error.WriteLine($"{Ansi.Red}No uploaded database named '{dbName}' found (missing {manifestBlobName}).{Ansi.Reset}");
            return 1;
        }

        string resolvedVersion;
        if (string.Equals(dbVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(manifest.Latest))
            {
                Console.Error.WriteLine($"{Ansi.Red}Database '{dbName}' manifest has no 'latest' version.{Ansi.Reset}");
                return 1;
            }
            resolvedVersion = manifest.Latest;
        }
        else
        {
            if (!manifest.Versions.Contains(dbVersion, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"{Ansi.Red}Version '{dbVersion}' not found for database '{dbName}'. Available versions: {string.Join(", ", manifest.Versions)}{Ansi.Reset}");
                return 1;
            }
            resolvedVersion = dbVersion;
        }

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Resolved version: {resolvedVersion}{Ansi.Reset}");

        // Step 3: Download the .bak blob
        var blobName = $"{dbName}/{resolvedVersion}.bak";
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync())
        {
            Console.Error.WriteLine($"{Ansi.Red}Database backup blob '{blobName}' not found.{Ansi.Reset}");
            return 1;
        }

        var fileName = outputPath ?? $"{dbName}-{resolvedVersion}.bak";

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Downloading {blobName}...{Ansi.Reset}");

        var blobProperties = await blobClient.GetPropertiesAsync();
        var totalBytes = blobProperties.Value.ContentLength;

        await using (var blobStream = await blobClient.OpenReadAsync())
        await using (var fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await blobStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (!asJson && totalBytes > 0)
                {
                    var pct = (double)totalRead / totalBytes * 100;
                    Console.Write($"\r{Ansi.Dim}Downloaded {totalRead / (1024.0 * 1024):N1} / {totalBytes / (1024.0 * 1024):N1} MB ({pct:N0}%){Ansi.Reset}");
                }
            }

            if (!asJson)
                Console.WriteLine();
        }

        // Output result
        var result = new
        {
            Database = dbName,
            Version = resolvedVersion,
            FileName = Path.GetFullPath(fileName),
            SizeBytes = new FileInfo(fileName).Length
        };

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        else
        {
            Console.WriteLine($"{Ansi.Cyan}Done.{Ansi.Reset} Database '{dbName}' version '{resolvedVersion}' saved to {Path.GetFullPath(fileName)} ({result.SizeBytes / (1024.0 * 1024):N1} MB)");
        }

        return 0;
    }
}
