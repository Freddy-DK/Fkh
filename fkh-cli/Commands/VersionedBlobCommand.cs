using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

abstract class VersionedBlobCommand : ClientCommand
{
    protected sealed class UploadSpec
    {
        public required string LocalPathParameterName { get; init; }
        public required string NameParameterName { get; init; }
        public required string VersionParameterName { get; init; }
        public required string SasFunctionName { get; init; }
        public required string ItemKind { get; init; }
        public required string BlobDescription { get; init; }
        public required Func<string, string, string> GetBlobName { get; init; }
        public Dictionary<string, string> SasParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    protected sealed class DownloadSpec
    {
        public string? ReferenceParameterName { get; init; }
        public string? ReferenceExample { get; init; }
        public string ReferenceFormat { get; init; } = "name/version";
        public string DefaultVersion { get; init; } = "latest";
        public string? NameParameterName { get; init; }
        public string? VersionParameterName { get; init; }
        public string? OutputPathParameterName { get; init; }
        public bool OutputPathRequired { get; init; }
        public required string SasFunctionName { get; init; }
        public required string ItemKind { get; init; }
        public required string BlobDescription { get; init; }
        public required Func<string, string, string> GetBlobName { get; init; }
        public required Func<string, string, string> GetDefaultOutputPath { get; init; }
        public Func<string, string, string, long, object>? CreateResult { get; init; }
        public Dictionary<string, string> SasParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    protected async Task<int> UploadVersionedBlobAsync(string[] args, CliSettings settings, bool asJson, UploadSpec spec)
    {
        if (!TryParseClientArgs(args, out var parameters))
            return 1;

        if (!TryGetRequiredParameter(parameters, spec.NameParameterName, out var name))
            return 1;
        if (!parameters.TryGetValue(spec.VersionParameterName, out var version) || string.IsNullOrWhiteSpace(version))
            version = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        if (!TryGetRequiredParameter(parameters, spec.LocalPathParameterName, out var localPath))
            return 1;
        if (!File.Exists(localPath))
        {
            Console.Error.WriteLine($"{Ansi.Red}File not found: {localPath}{Ansi.Reset}");
            return 1;
        }

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        var sasUrl = await RequestSasUrlAsync(backendUrl, token, spec.SasFunctionName, spec.SasParameters, "upload", asJson);
        if (sasUrl is null)
            return 1;

        var containerClient = new Azure.Storage.Blobs.BlobContainerClient(new Uri(sasUrl));

        var manifestBlobName = $"{name}/all.json";
        var manifestClient = containerClient.GetBlobClient(manifestBlobName);
        var manifest = await DownloadManifestAsync(manifestClient) ?? new VersionedBlobManifest();

        // Reuse the existing version's casing so a re-upload overwrites the same blob instead of creating a case-variant duplicate.
        var existingVersion = manifest.Versions.FirstOrDefault(v => string.Equals(v, version, StringComparison.OrdinalIgnoreCase));
        if (existingVersion is not null)
            version = existingVersion;

        var blobName = spec.GetBlobName(name, version);
        var fileSize = new FileInfo(localPath).Length;
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Uploading {localPath} ({fileSize / (1024.0 * 1024):N3} Mb) as {blobName}...{Ansi.Reset}");

        var blobClient = containerClient.GetBlobClient(blobName);

        await using (var fileStream = File.OpenRead(localPath))
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

        manifest = await DownloadManifestAsync(manifestClient) ?? manifest;
        if (!manifest.Versions.Contains(version, StringComparer.OrdinalIgnoreCase))
            manifest.Versions.Add(version);
        manifest.Versions.Sort(StringComparer.OrdinalIgnoreCase);
        manifest.Latest = version;

        await UploadManifestAsync(manifestClient, manifest);

        if (!asJson)
            Console.WriteLine($"{Ansi.Cyan}Updated:{Ansi.Reset} {manifestBlobName}");

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
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }
        else
        {
            Console.WriteLine($"{Ansi.Cyan}Done.{Ansi.Reset} {spec.ItemKind} '{name}' version '{version}' uploaded successfully.");
            Console.WriteLine($"  Versions: {string.Join(", ", manifest.Versions)}");
            Console.WriteLine($"  Latest: {manifest.Latest}");
        }

        return 0;
    }

    protected async Task<int> DownloadVersionedBlobAsync(string[] args, CliSettings settings, bool asJson, DownloadSpec spec)
    {
        if (!TryParseClientArgs(args, out var parameters))
            return 1;

        if (!TryResolveDownloadReference(parameters, spec, out var name, out var version, out var outputPath))
            return 1;

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        var sasUrl = await RequestSasUrlAsync(backendUrl, token, spec.SasFunctionName, spec.SasParameters, "download", asJson);
        if (sasUrl is null)
            return 1;

        var containerClient = new Azure.Storage.Blobs.BlobContainerClient(new Uri(sasUrl));
        var manifestBlobName = $"{name}/all.json";
        var manifestClient = containerClient.GetBlobClient(manifestBlobName);
        var manifest = await DownloadManifestAsync(manifestClient);
        if (manifest is null)
        {
            Console.Error.WriteLine($"{Ansi.Red}No uploaded {spec.ItemKind.ToLowerInvariant()} named '{name}' found (missing {manifestBlobName}).{Ansi.Reset}");
            return 1;
        }

        if (!TryResolveVersion(name, version, manifest, spec.ItemKind, out var resolvedVersion))
            return 1;

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Resolved version: {resolvedVersion}{Ansi.Reset}");

        var blobName = spec.GetBlobName(name, resolvedVersion);
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync())
        {
            Console.Error.WriteLine($"{Ansi.Red}{spec.BlobDescription} blob '{blobName}' not found.{Ansi.Reset}");
            return 1;
        }

        var localPath = outputPath ?? spec.GetDefaultOutputPath(name, resolvedVersion);

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Downloading {blobName}...{Ansi.Reset}");

        var blobProperties = await blobClient.GetPropertiesAsync();
        var totalBytes = blobProperties.Value.ContentLength;

        await using (var blobStream = await blobClient.OpenReadAsync())
        await using (var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
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

        var fullPath = Path.GetFullPath(localPath);
        var sizeBytes = new FileInfo(localPath).Length;
        var result = spec.CreateResult?.Invoke(name, resolvedVersion, fullPath, sizeBytes)
            ?? new
            {
                Name = name,
                Version = resolvedVersion,
                FileName = fullPath,
                SizeBytes = sizeBytes
            };

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }
        else
        {
            Console.WriteLine($"{Ansi.Cyan}Done.{Ansi.Reset} {spec.ItemKind} '{name}' version '{resolvedVersion}' saved to {fullPath} ({sizeBytes / (1024.0 * 1024):N1} MB)");
        }

        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static bool TryParseClientArgs(string[] args, out Dictionary<string, string> parameters)
    {
        try
        {
            parameters = ParseClientArgs(args);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    private static bool TryGetRequiredParameter(Dictionary<string, string> parameters, string parameterName, out string value)
    {
        if (!parameters.TryGetValue(parameterName, out value!) || string.IsNullOrWhiteSpace(value))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --{parameterName}{Ansi.Reset}");
            value = string.Empty;
            return false;
        }
        return true;
    }

    private static bool TryResolveDownloadReference(Dictionary<string, string> parameters, DownloadSpec spec, out string name, out string version, out string? outputPath)
    {
        name = string.Empty;
        version = string.Empty;
        outputPath = null;

        if (!string.IsNullOrWhiteSpace(spec.ReferenceParameterName))
        {
            if (!TryGetRequiredParameter(parameters, spec.ReferenceParameterName, out var reference))
            {
                return false;
            }

            var parts = reference.Split('/', 2);
            if (string.IsNullOrWhiteSpace(parts[0]) || (parts.Length == 2 && string.IsNullOrWhiteSpace(parts[1])))
            {
                Console.Error.WriteLine($"{Ansi.Red}Invalid {spec.ReferenceParameterName} value '{reference}'. Expected '{spec.ReferenceFormat}' or a name without version to use '{spec.DefaultVersion}' (e.g. '{spec.ReferenceExample}').{Ansi.Reset}");
                return false;
            }

            name = parts[0];
            version = parts.Length == 2 ? parts[1] : spec.DefaultVersion;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(spec.NameParameterName) || !TryGetRequiredParameter(parameters, spec.NameParameterName, out name))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(spec.VersionParameterName) || !TryGetRequiredParameter(parameters, spec.VersionParameterName, out version))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(spec.OutputPathParameterName))
        {
            parameters.TryGetValue(spec.OutputPathParameterName, out outputPath);
            if (spec.OutputPathRequired && string.IsNullOrWhiteSpace(outputPath))
            {
                Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --{spec.OutputPathParameterName}{Ansi.Reset}");
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveVersion(string name, string version, VersionedBlobManifest manifest, string itemKind, out string resolvedVersion)
    {
        if (string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(manifest.Latest))
            {
                Console.Error.WriteLine($"{Ansi.Red}{itemKind} '{name}' manifest has no 'latest' version.{Ansi.Reset}");
                resolvedVersion = string.Empty;
                return false;
            }
            resolvedVersion = manifest.Latest;
            return true;
        }

        if (!manifest.Versions.Contains(version, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"{Ansi.Red}Version '{version}' not found for {itemKind.ToLowerInvariant()} '{name}'. Available versions: {string.Join(", ", manifest.Versions)}{Ansi.Reset}");
            resolvedVersion = string.Empty;
            return false;
        }

        resolvedVersion = version;
        return true;
    }

    private static async Task<string?> RequestSasUrlAsync(string backendUrl, string token, string sasFunctionName, Dictionary<string, string> sasParameters, string action, bool asJson)
    {
        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Requesting {action} SAS from backend...{Ansi.Reset}");

        using var httpClient = new HttpClient();
        var sasRequest = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/{sasFunctionName}");
        sasRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddProtocolHeaders(sasRequest);
        sasRequest.Content = new StringContent(
            JsonSerializer.Serialize(new FunctionInvokeRequest
            {
                Parameters = sasParameters
            }),
            Encoding.UTF8, "application/json");

        var sasResponse = await httpClient.SendAsync(sasRequest);
        var sasBody = await sasResponse.Content.ReadAsStringAsync();

        if (!sasResponse.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to get {action} SAS ({(int)sasResponse.StatusCode}): {sasBody}{Ansi.Reset}");
            return null;
        }

        using var doc = JsonDocument.Parse(sasBody);
        var sasUrl = doc.RootElement.GetProperty("sasUrl").GetString();
        if (string.IsNullOrWhiteSpace(sasUrl))
            throw new InvalidOperationException("Backend returned empty SAS URL.");

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}SAS URL obtained (valid for 60 minutes).{Ansi.Reset}");

        return sasUrl;
    }

    private static async Task<VersionedBlobManifest?> DownloadManifestAsync(Azure.Storage.Blobs.BlobClient manifestClient)
    {
        try
        {
            var downloadResponse = await manifestClient.DownloadContentAsync();
            var existingJson = downloadResponse.Value.Content.ToString();
            return JsonSerializer.Deserialize<VersionedBlobManifest>(existingJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new VersionedBlobManifest();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static async Task UploadManifestAsync(Azure.Storage.Blobs.BlobClient manifestClient, VersionedBlobManifest manifest)
    {
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        await manifestClient.UploadAsync(manifestStream, overwrite: true);
    }
}

sealed class VersionedBlobManifest
{
    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = new();

    [JsonPropertyName("latest")]
    public string? Latest { get; set; }
}