using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Fkh.Services;

public abstract class FkhRemoveVersionedBlobBase : FkhServiceBase
{
    protected FkhRemoveVersionedBlobBase(ILogger logger) : base(logger) { }

    protected async Task<object> RemoveVersionedBlobAsync(
        string containerName,
        string itemKind,
        string referenceParameterName,
        Func<string, string, string> getBlobName,
        Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue(referenceParameterName, out var reference) || string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException($"Missing required parameter '{referenceParameterName}'.");

        var parts = reference.Split('/', 2);
        var name = parts[0];
        if (string.IsNullOrWhiteSpace(name) || (parts.Length == 2 && string.IsNullOrWhiteSpace(parts[1])))
            throw new ArgumentException($"Invalid {referenceParameterName} value '{reference}'. Expected 'name/version' or just 'name' to remove the latest version.");
        var version = parts.Length == 2 ? parts[1] : "latest";

        var username = parameters.GetValueOrDefault("_githubUsername", "unknown");

#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        var manifestBlobName = $"{name}/all.json";
        var manifestClient = containerClient.GetBlobClient(manifestBlobName);

        VersionedBlobManifest manifest;
        try
        {
            var downloadResponse = await manifestClient.DownloadContentAsync();
            var existingJson = downloadResponse.Value.Content.ToString();
            manifest = JsonSerializer.Deserialize<VersionedBlobManifest>(existingJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new VersionedBlobManifest();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"No uploaded {itemKind} named '{name}' found (missing {manifestBlobName}).");
        }

        string resolvedVersion;
        var removingLatest = string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase);
        if (removingLatest)
        {
            if (string.IsNullOrWhiteSpace(manifest.Latest))
                throw new InvalidOperationException($"{itemKind} '{name}' manifest has no 'latest' version to remove.");
            resolvedVersion = manifest.Latest;
        }
        else
        {
            if (!manifest.Versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Version '{version}' not found for {itemKind} '{name}'. Available versions: {string.Join(", ", manifest.Versions)}");
            resolvedVersion = version;
        }

        var wasLatest = string.Equals(manifest.Latest, resolvedVersion, StringComparison.OrdinalIgnoreCase);

        Logger.LogInformation(
            "User '{User}' removing {ItemKind} '{Name}' version '{Version}' from container '{Container}'.",
            username, itemKind, name, resolvedVersion, containerName);

        // Delete the versioned blob.
        var blobName = getBlobName(name, resolvedVersion);
        await containerClient.GetBlobClient(blobName).DeleteIfExistsAsync();

        // Remove the version from the manifest.
        manifest.Versions.RemoveAll(v => string.Equals(v, resolvedVersion, StringComparison.OrdinalIgnoreCase));

        var manifestDeleted = false;
        if (manifest.Versions.Count == 0)
        {
            // No versions remain; remove the manifest entirely.
            manifest.Latest = null;
            await manifestClient.DeleteIfExistsAsync();
            manifestDeleted = true;
        }
        else
        {
            if (wasLatest)
            {
                // Point 'latest' at the most recently uploaded remaining version (the previous "second latest").
                manifest.Latest = manifest.Versions[^1];
            }

            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
            await manifestClient.UploadAsync(manifestStream, overwrite: true);
        }

        Logger.LogInformation(
            "Removed {ItemKind} '{Name}' version '{Version}'. Remaining versions: {Count}. New latest: {Latest}.",
            itemKind, name, resolvedVersion, manifest.Versions.Count, manifest.Latest ?? "(none)");

        return new
        {
            Name = name,
            RemovedVersion = resolvedVersion,
            BlobName = blobName,
            Versions = manifest.Versions,
            Latest = manifest.Latest,
            ManifestDeleted = manifestDeleted
        };
    }

    private sealed class VersionedBlobManifest
    {
        public List<string> Versions { get; set; } = new();
        public string? Latest { get; set; }
    }
}
