using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

sealed class UploadDatabaseCommand : ClientCommand
{
    public override string Name => "UploadDatabase";
    public override string Description => "Uploads a .bak database file to blob storage. Updates a version manifest (all.json) with all versions and the latest. Admin only.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "bakFile", Type = "file", Description = "Path to the .bak database backup file.", Required = true },
        new() { Name = "backupName", Type = "string", Description = "Backup name (used as the folder name in blob storage).", Required = true },
        new() { Name = "backupVersion", Type = "string", Description = "Version label for this backup (used as the blob name).", Required = true }
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

        if (!parameters.TryGetValue("backupName", out var name) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --backupName{Ansi.Reset}");
            return 1;
        }
        if (!parameters.TryGetValue("backupVersion", out var version) || string.IsNullOrWhiteSpace(version))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --backupVersion{Ansi.Reset}");
            return 1;
        }
        if (!parameters.TryGetValue("bakFile", out var bakFile) || string.IsNullOrWhiteSpace(bakFile))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --bakFile{Ansi.Reset}");
            return 1;
        }
        if (!File.Exists(bakFile))
        {
            Console.Error.WriteLine($"{Ansi.Red}File not found: {bakFile}{Ansi.Reset}");
            return 1;
        }

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        // Step 1: Get SAS URL from backend (admin-only endpoint, not in catalog)
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Requesting upload SAS from backend...{Ansi.Reset}");

        string sasUrl;
        using (var httpClient = new HttpClient())
        {
            var sasRequest = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/GetDatabaseUploadSas");
            sasRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            AddProtocolHeaders(sasRequest);
            sasRequest.Content = new StringContent(
                JsonSerializer.Serialize(new FunctionInvokeRequest
                {
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["containerName"] = "databases"
                    }
                }),
                Encoding.UTF8, "application/json");

            var sasResponse = await httpClient.SendAsync(sasRequest);
            var sasBody = await sasResponse.Content.ReadAsStringAsync();

            if (!sasResponse.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"{Ansi.Red}Failed to get upload SAS ({(int)sasResponse.StatusCode}): {sasBody}{Ansi.Reset}");
                return 1;
            }

            using var doc = JsonDocument.Parse(sasBody);
            sasUrl = doc.RootElement.GetProperty("sasUrl").GetString()
                ?? throw new InvalidOperationException("Backend returned empty SAS URL.");
        }

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}SAS URL obtained (valid for 60 minutes).{Ansi.Reset}");

        // Step 2: Upload the .bak file directly to blob storage
        var blobName = $"{name}/{version}.bak";
        var fileSize = new FileInfo(bakFile).Length;
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Uploading {bakFile} ({fileSize / (1024.0 * 1024):N3} Mb) as {blobName}...{Ansi.Reset}");

        var containerUri = new Uri(sasUrl);
        var containerClient = new Azure.Storage.Blobs.BlobContainerClient(containerUri);
        var blobClient = containerClient.GetBlobClient(blobName);

        await using (var fileStream = File.OpenRead(bakFile))
        await using (var blobStream = await blobClient.OpenWriteAsync(overwrite: true))
        {
            var buffer = new byte[81920];
            long totalWritten = 0;
            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
            {
                await blobStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalWritten += bytesRead;
                if (!asJson && fileSize > 0)
                {
                    var pct = (double)totalWritten / fileSize * 100;
                    Console.Write($"\r{Ansi.Dim}Uploaded {totalWritten / (1024.0 * 1024):N1} / {fileSize / (1024.0 * 1024):N1} MB ({pct:N0}%){Ansi.Reset}");
                }
            }

            if (!asJson)
                Console.WriteLine();
        }

        if (!asJson)
            Console.WriteLine($"{Ansi.Cyan}Uploaded:{Ansi.Reset} {blobName}");

        // Step 3: Update all.json manifest
        var manifestBlobName = $"{name}/all.json";
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
            manifest = new DatabaseManifest();
        }

        // Add version if not already present
        if (!manifest.Versions.Contains(version, StringComparer.OrdinalIgnoreCase))
        {
            manifest.Versions.Add(version);
        }
        manifest.Latest = version;

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using (var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)))
        {
            await manifestClient.UploadAsync(manifestStream, overwrite: true);
        }

        if (!asJson)
            Console.WriteLine($"{Ansi.Cyan}Updated:{Ansi.Reset} {manifestBlobName}");

        // Output result
        var result = new
        {
            Name = name,
            Version = version,
            BlobName = blobName,
            Manifest = manifestBlobName,
            Versions = manifest.Versions,
            Latest = manifest.Latest
        };

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        else
        {
            Console.WriteLine($"{Ansi.Cyan}Done.{Ansi.Reset} Database '{name}' version '{version}' uploaded successfully.");
            Console.WriteLine($"  Versions: {string.Join(", ", manifest.Versions)}");
            Console.WriteLine($"  Latest: {manifest.Latest}");
        }

        return 0;
    }
}

sealed class DatabaseManifest
{
    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = new();

    [JsonPropertyName("latest")]
    public string? Latest { get; set; }
}
