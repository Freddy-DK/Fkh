using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fkh.Services;

public abstract class FkhListVersionedBlobBase : FkhServiceBase
{
    protected FkhListVersionedBlobBase(ILogger logger) : base(logger) { }

    private const string ManifestSuffix = "/all.json";

    protected async Task<object> ListVersionedBlobAsync(
        string containerName,
        string referenceParameterName,
        Dictionary<string, string> parameters)
    {
        var reference = parameters.TryGetValue(referenceParameterName, out var r) && !string.IsNullOrWhiteSpace(r)
            ? r
            : "*/latest";

        var parts = reference.Split('/', 2);
        var namePattern = string.IsNullOrWhiteSpace(parts[0]) ? "*" : parts[0];
        var versionPattern = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "latest";

#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        if (!await containerClient.ExistsAsync())
            return new { Items = Array.Empty<object>() };

        // Resolve the set of names to inspect. A literal name (no wildcard characters)
        // is fetched directly; otherwise enumerate manifests and filter with a
        // PowerShell '-like'-style wildcard match.
        List<string> names;
        if (IsLiteral(namePattern))
        {
            names = new List<string> { namePattern };
        }
        else
        {
            var nameRegex = WildcardToRegex(namePattern);
            var prefix = LiteralPrefix(namePattern);
            names = new List<string>();
            await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix))
            {
                if (!blob.Name.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = blob.Name[..^ManifestSuffix.Length];
                if (name.Contains('/'))
                    continue;
                if (nameRegex.IsMatch(name))
                    names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var items = new List<object>();
        foreach (var name in names)
        {
            var manifest = await TryDownloadManifestAsync(containerClient, name);
            if (manifest is null)
                continue;

            IEnumerable<string> versions;
            if (string.Equals(versionPattern, "latest", StringComparison.OrdinalIgnoreCase))
            {
                // 'latest' is a keyword resolving to the manifest's latest pointer.
                versions = string.IsNullOrWhiteSpace(manifest.Latest)
                    ? Enumerable.Empty<string>()
                    : new[] { manifest.Latest };
            }
            else if (IsLiteral(versionPattern))
            {
                versions = manifest.Versions.Where(v => string.Equals(v, versionPattern, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var versionRegex = WildcardToRegex(versionPattern);
                versions = manifest.Versions.Where(v => versionRegex.IsMatch(v));
            }

            foreach (var version in versions)
            {
                items.Add(new
                {
                    Name = name,
                    Version = version,
                    IsLatest = string.Equals(version, manifest.Latest, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        return new { Items = items };
    }

    /// <summary>True when the pattern contains no wildcard metacharacters (* ?).</summary>
    private static bool IsLiteral(string pattern)
        => pattern.IndexOfAny(new[] { '*', '?' }) < 0;

    /// <summary>Returns the leading literal portion of a wildcard pattern (used as a blob-listing prefix).</summary>
    private static string LiteralPrefix(string pattern)
    {
        var stop = pattern.IndexOfAny(new[] { '*', '?' });
        return stop < 0 ? pattern : pattern[..stop];
    }

    /// <summary>
    /// Converts a wildcard pattern into an anchored, case-insensitive regex.
    /// Supports only '*' (any run of characters) and '?' (a single character);
    /// every other character is matched literally.
    /// </summary>
    private static Regex WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task<VersionedBlobManifest?> TryDownloadManifestAsync(BlobContainerClient containerClient, string name)

    {
        var manifestClient = containerClient.GetBlobClient($"{name}{ManifestSuffix}");
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

    private sealed class VersionedBlobManifest
    {
        public List<string> Versions { get; set; } = new();
        public string? Latest { get; set; }
    }
}
