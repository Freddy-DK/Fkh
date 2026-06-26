using System.Net.Http;
using System.Text.Json;

namespace Fkh.Models;

public sealed class ClientVersionInfo
{
    /// <summary>The client name (e.g. "VS Code extension", "CLI").</summary>
    public required string Client { get; init; }

    /// <summary>The version of this client that supports the protocol version.</summary>
    public required string Version { get; init; }
}

public sealed class SupportedClientVersions
{
    /// <summary>The protocol version these client versions support.</summary>
    public required int Version { get; init; }

    /// <summary>The client versions that support this protocol version.</summary>
    public required List<ClientVersionInfo> Clients { get; init; }
}

public static class ProtocolVersionConfig
{
    /// <summary>
    /// The current protocol version supported by the backend.
    /// IMPORTANT: When bumping this value, add an entry for the previous version to
    /// SupportedClientVersions.json in the repository root, listing which client
    /// versions supported that protocol version.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>Default client name when none is specified in the request.</summary>
    public const string DefaultClient = "VS Code extension";

    private const string SupportedClientVersionsUrl =
        "https://raw.githubusercontent.com/Freddy-DK/Fkh/main/SupportedClientVersions.json";

    private static readonly HttpClient _httpClient = new();
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static List<SupportedClientVersions>? _cachedVersions;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private const int CacheDurationMinutes = 10;

    private static async Task<List<SupportedClientVersions>?> GetSupportedClientVersionsByProtocolAsync()
    {
        if (DateTime.UtcNow < _cacheExpiry && _cachedVersions is not null)
            return _cachedVersions;

        await _cacheLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow < _cacheExpiry && _cachedVersions is not null)
                return _cachedVersions;

            var json = await _httpClient.GetStringAsync(SupportedClientVersionsUrl);
            _cachedVersions = JsonSerializer.Deserialize<List<SupportedClientVersions>>(json, JsonSerializerOptions.Web);
            _cacheExpiry = DateTime.UtcNow.AddMinutes(CacheDurationMinutes);
            return _cachedVersions;
        }
        catch
        {
            // Return stale data on error, or null if never successfully fetched
            return _cachedVersions;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Validates the client protocol version against the backend's supported version.
    /// Returns null if valid, or an error message if the version is incompatible.
    /// </summary>
    public static async Task<string?> ValidateAsync(int clientVersion, string clientName)
    {
        if (clientVersion > CurrentVersion)
        {
            // Client is too new for this backend — find which client version supports the current protocol
            var supportedClientVersions = await GetSupportedClientVersionsByProtocolAsync();
            var current = supportedClientVersions?.FirstOrDefault(o => o.Version == CurrentVersion);
            var clientEntry = current?.Clients.FirstOrDefault(c =>
                string.Equals(c.Client, clientName, StringComparison.OrdinalIgnoreCase));

            if (clientEntry is not null)
            {
                return $"Protocol version {clientVersion} is not supported. " +
                       $"The backend only supports protocol version {CurrentVersion}. " +
                       $"Please use {clientName} version {clientEntry.Version}.";
            }

            return $"Protocol version {clientVersion} is not supported. " +
                   $"The backend only supports protocol version {CurrentVersion}. " +
                   $"Your client '{clientName}' is not a supported client.";
        }

        if (clientVersion < CurrentVersion)
        {
            // Client is too old — just tell them to upgrade
            return $"Protocol version {clientVersion} is outdated. " +
                   $"The backend requires protocol version {CurrentVersion}. " +
                   $"Please upgrade your {clientName} to the latest version.";
        }

        return null;
    }

    public static async Task<List<ClientVersionInfo>?> GetSupportedClientVersionsAsync()
    {
        var supportedClientVersions = await GetSupportedClientVersionsByProtocolAsync();
        return supportedClientVersions?
            .FirstOrDefault(o => o.Version == CurrentVersion)?
            .Clients;
    }

    public static int ParseProtocolVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out var version))
            return 1; // Default to version 1 if not specified

        return version;
    }

    public static string ParseClientName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DefaultClient : value;
    }
}
