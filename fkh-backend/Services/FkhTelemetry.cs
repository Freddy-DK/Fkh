using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fkh.Services;

/// <summary>
/// Anonymous usage telemetry sent to the central fkh-usage service.
/// Every event is a single fire-and-forget POST; failures are ignored and callers are never delayed.
/// Never pass names, emails, URLs or other identifying values — hash them with <see cref="Hash"/>.
/// </summary>
public static class FkhTelemetry
{
    public const int SchemaVersion = 1;
    private const int MaxInFlight = 20;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static SemaphoreSlim _inFlight = new(MaxInFlight, MaxInFlight);

    private static string? _endpoint = Normalize(Environment.GetEnvironmentVariable("FKH_USAGE_ENDPOINT"))?.TrimEnd('/');
    private static string? _deploymentId = Normalize(Environment.GetEnvironmentVariable("FKH_TELEMETRY_DEPLOYMENT_ID"));
    private static byte[]? _salt = Normalize(Environment.GetEnvironmentVariable("FKH_TELEMETRY_SALT")) is { } s ? Encoding.UTF8.GetBytes(s) : null;
    private static string _environment = Normalize(Environment.GetEnvironmentVariable("FKH_TELEMETRY_ENVIRONMENT")) ?? "production";
    private static Func<string, string, Task> _send = PostAsync;

    public static bool Enabled => _endpoint is not null && _deploymentId is not null;

    /// <summary>Queues an event for sending and returns immediately.</summary>
    public static void Track(string name, IDictionary<string, object?>? properties = null)
    {
        if (!Enabled)
            return;

        try
        {
            var json = BuildPayload(name, properties);
            var inFlight = _inFlight;
            if (!inFlight.Wait(0))
                return;

            var url = $"{_endpoint}/event";
            _ = Task.Run(() => SendAndReleaseAsync(url, json, inFlight));
        }
        catch
        {
            // Telemetry must never affect Fkh.
        }
    }

    /// <summary>Keyed hash (HMAC-SHA256, per-deployment secret salt) truncated to 16 hex characters.</summary>
    public static string? Hash(string? value)
    {
        if (string.IsNullOrEmpty(value) || _salt is null)
            return null;

        var hash = HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    public static string? UserHash(string? username) => Hash(username is null ? null : $"user:{username}");

    public static string? ContainerHash(string? appName) => Hash(appName is null ? null : $"container:{appName}");

    internal static string BuildPayload(string name, IDictionary<string, object?>? properties)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["schemaVersion"] = SchemaVersion,
            ["deploymentId"] = _deploymentId,
            ["environment"] = _environment,
            ["backendVersion"] = Environment.GetEnvironmentVariable("FKH_VERSION"),
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["properties"] = properties?
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static async Task SendAndReleaseAsync(string url, string json, SemaphoreSlim inFlight)
    {
        try
        {
            await _send(url, json).ConfigureAwait(false);
        }
        catch
        {
            // Ignore — the central service may be unreachable.
        }
        finally
        {
            inFlight.Release();
        }
    }

    private static async Task PostAsync(string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content).ConfigureAwait(false);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static void ConfigureForTests(string? endpoint, string? deploymentId, string? salt, Func<string, string, Task> send, int maxInFlight = MaxInFlight)
    {
        _endpoint = endpoint;
        _deploymentId = deploymentId;
        _salt = salt is null ? null : Encoding.UTF8.GetBytes(salt);
        _environment = "test";
        _send = send;
        _inFlight = new SemaphoreSlim(maxInFlight, maxInFlight);
    }
}

/// <summary>Non-identifying facts extracted from a BC artifact URL.</summary>
public sealed record ArtifactInfo(string Type, string? Version, string? Major, string? Country)
{
    private static readonly string[] KnownHosts = ["bcartifacts", "bcinsider", "bcpublicpreview"];

    /// <summary>Only Microsoft's public artifact storage is parsed; anything else is reported as "custom".</summary>
    public static ArtifactInfo Parse(string? artifactUrl)
    {
        if (string.IsNullOrWhiteSpace(artifactUrl) || !Uri.TryCreate(artifactUrl, UriKind.Absolute, out var uri))
            return new ArtifactInfo("unknown", null, null, null);

        if (!KnownHosts.Any(h => uri.Host.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
            return new ArtifactInfo("custom", null, null, null);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var type = segments.Length > 0 ? segments[0].ToLowerInvariant() : "unknown";
        var version = segments.Length > 1 && System.Version.TryParse(segments[1], out var v) ? v.ToString() : null;
        var major = version is null ? null : version.Split('.')[0];
        var country = segments.Length > 2 ? segments[2].ToLowerInvariant() : null;
        return new ArtifactInfo(type, version, major, country);
    }
}
