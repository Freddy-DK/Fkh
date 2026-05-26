namespace Fkh.Models;

public sealed class ClientVersionInfo
{
    /// <summary>The client name (e.g. "VS Code extension", "CLI").</summary>
    public required string Client { get; init; }

    /// <summary>The version of this client that supports the protocol version.</summary>
    public required string Version { get; init; }
}

public sealed class ObsoleteProtocolVersion
{
    /// <summary>The protocol version that is no longer supported.</summary>
    public required int Version { get; init; }

    /// <summary>The clients that used this protocol version and their version numbers.</summary>
    public required List<ClientVersionInfo> Clients { get; init; }
}

public static class ProtocolVersionConfig
{
    /// <summary>The current protocol version supported by the backend.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Default client name when none is specified in the request.</summary>
    public const string DefaultClient = "VS Code extension";

    /// <summary>
    /// Protocol versions and the client versions that support them.
    /// Maintained manually when updating the protocol version.
    /// </summary>
    public static readonly List<ObsoleteProtocolVersion> ObsoleteVersions = new()
    {
        new ObsoleteProtocolVersion
        {
            Version = 1,
            Clients = new()
            {
                new ClientVersionInfo { Client = "VS Code extension", Version = "?" },
                new ClientVersionInfo { Client = "CLI", Version = "?" },
                new ClientVersionInfo { Client = "Web App", Version = "?" },
            }
        },
    };

    /// <summary>
    /// Validates the client protocol version against the backend's supported version.
    /// Returns null if valid, or an error message if the version is incompatible.
    /// </summary>
    public static string? Validate(int clientVersion, string clientName)
    {
        if (clientVersion > CurrentVersion)
        {
            // Client is too new for this backend — find which client version supports the current protocol
            var current = ObsoleteVersions.FirstOrDefault(o => o.Version == CurrentVersion);
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
