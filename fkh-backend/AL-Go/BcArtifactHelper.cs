// BcArtifactHelper.cs
// C# equivalent of Get-BCArtifactUrl (Artifacts/Get-BCArtifactUrl.ps1) and the relevant
// helpers from HelperFunctions.ps1 (ReplaceCDN, QueryArtifactsFromIndex).
//
// Intentionally excluded from this port:
//   - useApproximateVersion performance hack
//   - ExcludeBuilds filtering (bcContainerHelperConfig.ExcludeBuilds)
//   - Telemetry (InitTelemetryScope / TrackException / TrackTrace)
//   - Country code remapping (bcContainerHelperConfig.mapCountryCode)
//   - sasToken (obsolete in the original)
//   - acceptInsiderEula / acceptEula (always accepted here)
//   - SecondToLastMajor select mode
//
// Requirements: .NET 10 / C# 14 or later.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BcArtifacts;

/// <summary>Artifact type: Sandbox or OnPrem.</summary>
public enum ArtifactType { Sandbox, OnPrem }

/// <summary>Controls which artifact URL(s) are returned by <see cref="BcArtifactHelper.GetBcArtifactUrlAsync"/>.</summary>
public enum ArtifactSelect
{
    /// <summary>Return all matching URLs, sorted by version.</summary>
    All,
    /// <summary>Return only the highest version (default).</summary>
    Latest,
    /// <summary>Return only the lowest version.</summary>
    First,
    /// <summary>Return the version closest to (and at least) the supplied version number.</summary>
    Closest,
    /// <summary>Return the latest publicly available Sandbox release (equivalent to Latest for bcartifacts).</summary>
    Current,
    /// <summary>Return the next minor insider Sandbox release.</summary>
    NextMinor,
    /// <summary>Return the next major insider Sandbox release.</summary>
    NextMajor,
    /// <summary>Return the latest build published before today (ignores builds from today).</summary>
    Daily,
    /// <summary>Return the latest build published before the start of the current week.</summary>
    Weekly,
}

/// <summary>
/// Resolves Business Central artifact URLs from the Azure Blob / CDN indexes.
/// Equivalent to <c>Get-BCArtifactUrl</c> in BcContainerHelper.
/// </summary>
public static class BcArtifactHelper
{
    /// <summary>
    /// The <see cref="HttpClient"/> used for all HTTP requests.
    /// Replace before first call to supply a pre-configured instance (e.g. with a custom User-Agent).
    /// </summary>
    public static HttpClient HttpClient { get; set; } = new();

    // ---------------------------------------------------------------------------
    // CDN / blob-URL mapping
    // ---------------------------------------------------------------------------

    private static readonly (string Old, string New, string Blob)[] CdnMap =
    [
        ("bcartifacts.azureedge.net",         "bcartifacts-exdbf9fwegejdqak.b02.azurefd.net",         "bcartifacts.blob.core.windows.net"),
        ("bcinsider.azureedge.net",           "bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net",           "bcinsider.blob.core.windows.net"),
        ("bcpublicpreview.azureedge.net",     "bcpublicpreview-f2ajahg0e2cudpgh.b02.azurefd.net",     "bcpublicpreview.blob.core.windows.net"),
        ("businesscentralapps.azureedge.net", "businesscentralapps-hkdrdkaeangzfydv.b02.azurefd.net", "businesscentralapps.blob.core.windows.net"),
        ("bcprivate.azureedge.net",           "bcprivate-fmdwbsb3ekbkc0bt.b02.azurefd.net",           "bcprivate.blob.core.windows.net"),
    ];

    /// <summary>
    /// Replaces a legacy <c>azureedge.net</c> CDN URL or bare blob-storage hostname
    /// with the current Azure Front Door CDN endpoint.
    /// Pass <paramref name="useBlobUrl"/>=<c>true</c> to resolve to the raw blob URL instead.
    /// </summary>
    public static string ReplaceCDN(string sourceUrl, bool useBlobUrl = false)
    {
        foreach (var (old, @new, blob) in CdnMap)
        {
            var target = useBlobUrl ? blob : @new;
            foreach (var candidate in (string[])[blob, @new, old])
            {
                var prefix = $"https://{candidate}/";
                if (sourceUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return $"https://{target}/{sourceUrl[prefix.Length..]}";

                if (string.Equals(sourceUrl, candidate, StringComparison.OrdinalIgnoreCase))
                    return target;
            }
        }
        return sourceUrl;
    }

    /// <summary>
    /// Resolves a short storage account name (e.g. <c>"bcartifacts"</c>) or any known
    /// blob/CDN/legacy hostname to the canonical Azure Front Door CDN hostname.
    /// No blob storage hostname ever leaves this method.
    /// </summary>
    private static string ResolveToCdnHostname(string storageAccount)
    {
        // Expand bare short names so ReplaceCDN can match against .blob.core.windows.net entries
        if (!storageAccount.Contains('.'))
            storageAccount = $"{storageAccount}.blob.core.windows.net";
        return ReplaceCDN(storageAccount, useBlobUrl: false);
    }

    // ---------------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------------

    private static async Task<string> FetchJsonAsync(string url)
    {
        using var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement source, string propertyName, out JsonElement property)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            property = default;
            return false;
        }

        if (source.TryGetProperty(propertyName, out property))
            return true;

        foreach (var candidate in source.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string GetStringProperty(JsonElement source, string propertyName, string defaultValue = "")
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var property))
            return defaultValue;

        return property.ValueKind switch
        {
            JsonValueKind.Null => defaultValue,
            JsonValueKind.String => property.GetString() ?? defaultValue,
            _ => property.ToString() ?? defaultValue,
        };
    }

    private static bool GetBoolProperty(JsonElement source, string propertyName, bool defaultValue = false)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var property))
            return defaultValue;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsedBool) => parsedBool,
            _ => defaultValue,
        };
    }

    private static List<string> GetStringListProperty(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var property))
            return [];

        if (property.ValueKind == JsonValueKind.Array)
        {
            return property.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList();
        }

        var single = property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        return string.IsNullOrWhiteSpace(single) ? [] : [single];
    }

    private static ArtifactSelect ParseArtifactSelect(string? select)
    {
        if (string.IsNullOrWhiteSpace(select))
            return ArtifactSelect.Latest;

        if (Enum.TryParse<ArtifactSelect>(select, ignoreCase: true, out var parsed))
            return parsed;

        throw new ArgumentException($"Unknown artifact select mode '{select}'.");
    }

    private static Version GetArtifactVersion(string artifactUrl)
        => Version.Parse(artifactUrl.Split('/')[4]);

    private static string GetArtifactCountry(string artifactUrl)
        => artifactUrl.Split('?')[0].Split('/')[5];

    /// <summary>
    /// Downloads the artifact index files via CDN and returns matching entries
    /// as <c>"version/country"</c> strings (e.g. <c>"26.0.12345.0/w1"</c>).
    /// The list is sorted by version when no specific country is requested.
    /// </summary>
    private static async Task<List<string>> QueryArtifactsFromIndexAsync(
        string cdnHost,
        ArtifactType type,
        string versionPrefix = "",
        string country = "",
        DateTime? after = null,
        DateTime? before = null,
        bool doNotCheckPlatform = false)
    {
        var typeStr = type.ToString().ToLowerInvariant();
        var indexBase = $"https://{cdnHost}/{typeStr}/indexes";
        var artifacts = new List<string>();
        bool sort = false;

        // Resolve the list of countries to query
        IReadOnlyList<string> countries;
        if (string.IsNullOrEmpty(country))
        {
            var json = await FetchJsonAsync($"{indexBase}/countries.json");
            countries = JsonSerializer.Deserialize<string[]>(json)!
                .Where(c => c != "platform")
                .ToArray();
            sort = true;
        }
        else
        {
            countries = [country];
        }

        // Optionally load the platform version index for validation
        HashSet<string>? platformVersions = null;
        if (!doNotCheckPlatform)
        {
            var json = await FetchJsonAsync($"{indexBase}/platform.json");
            using var platformDoc = JsonDocument.Parse(json);
            platformVersions = platformDoc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("Version").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }

        // Process each country index
        foreach (var c in countries)
        {
            var json = await FetchJsonAsync($"{indexBase}/{c}.json");
            using var doc = JsonDocument.Parse(json);

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var entryVersion = entry.GetProperty("Version").GetString()!;

                if (!entryVersion.StartsWith(versionPrefix, StringComparison.Ordinal))
                    continue;

                // Fast path: skip platform check and date filtering entirely
                if (doNotCheckPlatform && !after.HasValue && !before.HasValue)
                {
                    artifacts.Add($"{entryVersion}/{c}");
                    continue;
                }

                // Platform existence check
                if (!doNotCheckPlatform && !(platformVersions?.Contains(entryVersion) ?? false))
                    continue;

                // Date filter
                if (after.HasValue || before.HasValue)
                {
                    if (!entry.TryGetProperty("CreationTime", out var ctProp)) continue;
                    if (ctProp.ValueKind != JsonValueKind.String) continue;
                    if (!DateTime.TryParse(ctProp.GetString(), out var creationTime)) continue;

                    bool include = true;
                    if (after is DateTime afterValue)
                        include = creationTime > afterValue;

                    if (include && before is DateTime beforeValue)
                        include = creationTime < beforeValue;

                    if (!include) continue;
                }

                artifacts.Add($"{entryVersion}/{c}");
            }
        }

        if (sort)
            artifacts.Sort((a, b) => Version.Parse(a.Split('/')[0]).CompareTo(Version.Parse(b.Split('/')[0])));

        return artifacts;
    }

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns Business Central artifact URLs matching the specified criteria.
    /// </summary>
    /// <param name="type">OnPrem or Sandbox (default: Sandbox).</param>
    /// <param name="country">
    ///   Localization code, e.g. <c>"w1"</c>, <c>"be"</c>, <c>"dk"</c>.
    ///   Pass an empty string to include all countries.
    /// </param>
    /// <param name="version">
    ///   Version prefix to filter on, e.g. <c>"26"</c>, <c>"26.0"</c>, or <c>"26.0.12345.0"</c>.
    ///   Required (and must be a full four-part version) when <paramref name="select"/> is <see cref="ArtifactSelect.Closest"/>.
    /// </param>
    /// <param name="select">Which artifact(s) to return (default: <see cref="ArtifactSelect.Latest"/>).</param>
    /// <param name="after">Only include artifacts created after this UTC date.</param>
    /// <param name="before">Only include artifacts created before this UTC date.</param>
    /// <param name="storageAccount">
    ///   Override the storage account name or hostname (default: <c>bcartifacts</c>).
    ///   Use <c>"bcinsider"</c> for insider builds.
    /// </param>
    /// <param name="doNotCheckPlatform">Skip platform-index validation (faster, but may return artifacts without a platform build).</param>
    /// <returns>Sequence of fully-qualified artifact URLs.</returns>
    public static async Task<IEnumerable<string>> GetBcArtifactUrlAsync(
        ArtifactType type = ArtifactType.Sandbox,
        string country = "",
        string version = "",
        ArtifactSelect select = ArtifactSelect.Latest,
        DateTime? after = null,
        DateTime? before = null,
        string storageAccount = "",
        bool doNotCheckPlatform = false)
    {
        // OnPrem: fix known mis-versioned builds, and treat Daily/Weekly as Latest
        if (type == ArtifactType.OnPrem)
        {
            if      (version.StartsWith("18.9"))  version = "18.10.35134.0";
            else if (version.StartsWith("17.14")) version = "17.15.35135.0";
            else if (version.StartsWith("16.18")) version = "16.19.35126.0";

            if (select is ArtifactSelect.Daily or ArtifactSelect.Weekly)
                select = ArtifactSelect.Latest;
        }

        // --- Daily / Weekly ---
        if (select is ArtifactSelect.Daily or ArtifactSelect.Weekly)
        {
            if (!string.IsNullOrEmpty(version) || after.HasValue || before.HasValue)
                throw new ArgumentException(
                    "You cannot specify version, before or after when selecting Daily or Weekly build.");

            // Determine the cutoff: anything from today (Daily) or this week (Weekly) is excluded
            var ignoreBuildsAfter = select == ArtifactSelect.Daily
                ? DateTime.Today
                : DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek);

            var current = (await GetBcArtifactUrlAsync(
                type, country, "", ArtifactSelect.Latest, null, null,
                storageAccount, doNotCheckPlatform)).FirstOrDefault();

            if (current is not null)
            {
                var currentVersion = Version.Parse(current.Split('/')[4]);
                var verPrefix = $"{currentVersion.Major}.{currentVersion.Minor}";

                // Try: latest in the same major.minor BEFORE the cutoff
                var periodic = (await GetBcArtifactUrlAsync(
                    type, country, verPrefix, ArtifactSelect.Latest,
                    null, ignoreBuildsAfter.ToUniversalTime(),
                    storageAccount, doNotCheckPlatform)).FirstOrDefault();

                // Fallback: first build AFTER the cutoff in the same major.minor
                if (periodic is null)
                    periodic = (await GetBcArtifactUrlAsync(
                        type, country, verPrefix, ArtifactSelect.First,
                        ignoreBuildsAfter.ToUniversalTime(), null,
                        storageAccount, doNotCheckPlatform)).FirstOrDefault();

                if (periodic is not null) current = periodic;
            }

            return current is not null ? [current] : [];
        }

        // --- Current ---
        if (select == ArtifactSelect.Current)
        {
            if (!string.IsNullOrEmpty(storageAccount) || type == ArtifactType.OnPrem || !string.IsNullOrEmpty(version))
                throw new ArgumentException(
                    "You cannot specify storageAccount, type=OnPrem or version when selecting Current release.");

            return await GetBcArtifactUrlAsync(
                type, country, "", ArtifactSelect.Latest, null, null,
                "", doNotCheckPlatform);
        }

        // --- NextMinor / NextMajor ---
        if (select is ArtifactSelect.NextMinor or ArtifactSelect.NextMajor)
        {
            if (!string.IsNullOrEmpty(storageAccount) || type == ArtifactType.OnPrem || !string.IsNullOrEmpty(version))
                throw new ArgumentException(
                    $"You cannot specify storageAccount, type=OnPrem or version when selecting {select} release.");

            // Determine current public version from the 'base' country
            var currentUrl = (await GetBcArtifactUrlAsync(
                ArtifactType.Sandbox, "base", "", ArtifactSelect.Latest,
                null, null, "", doNotCheckPlatform)).FirstOrDefault();

            if (currentUrl is null) return [];

            var currentVersion = Version.Parse(currentUrl.Split('/')[4]);
            var nextMajorPrefix = $"{currentVersion.Major + 1}.0.";
            var nextMinorPrefix = currentVersion.Minor >= 5
                ? nextMajorPrefix
                : $"{currentVersion.Major}.{currentVersion.Minor + 1}.";

            var targetCountry = string.IsNullOrEmpty(country) ? "w1" : country;

            var insiders = await GetBcArtifactUrlAsync(
                ArtifactType.Sandbox, targetCountry, "", ArtifactSelect.All,
                after, before, "bcinsider", doNotCheckPlatform);

            var nextMajor = insiders.Where(u => u.Split('/')[4].StartsWith(nextMajorPrefix, StringComparison.Ordinal)).LastOrDefault();
            var nextMinor = insiders.Where(u => u.Split('/')[4].StartsWith(nextMinorPrefix, StringComparison.Ordinal)).LastOrDefault();

            var chosen = select == ArtifactSelect.NextMinor ? nextMinor : nextMajor;
            return chosen is not null ? [chosen] : [];
        }

        // --- Main path ---
        if (string.IsNullOrEmpty(storageAccount))
            storageAccount = "bcartifacts";

        // Resolve once to the canonical CDN hostname – no blob URL is ever constructed
        var cdnHost = ResolveToCdnHostname(storageAccount);

        var baseUrl = $"https://{cdnHost}/{type.ToString().ToLowerInvariant()}/";

        // Determine the version prefix used for index filtering
        string versionPrefix;
        Version? closestToVersion = null;

        switch (select)
        {
            case ArtifactSelect.Closest:
                if (string.IsNullOrEmpty(version))
                    throw new ArgumentException(
                        "You must specify a version number when you want to get the closest artifact URL.");
                if (version.Count(c => c == '.') != 3 || !Version.TryParse(version, out closestToVersion))
                    throw new ArgumentException(
                        "Version number must be in the format 1.2.3.4 when you want to get the closest artifact URL.");
                versionPrefix = $"{closestToVersion.Major}.{closestToVersion.Minor}.";
                break;

            default:
                if (!string.IsNullOrEmpty(version))
                {
                    // Append a trailing dot when fewer than 3 dots are present so that
                    // e.g. "14.1" does not accidentally match "14.10", "14.11" etc.
                    if (version.Count(c => c == '.') < 3)
                        version = version.TrimEnd('.') + ".";
                }
                versionPrefix = version;
                break;
        }

        var artifactList = await QueryArtifactsFromIndexAsync(
            cdnHost, type, versionPrefix, country, after, before, doNotCheckPlatform);

        IEnumerable<string> selected = select switch
        {
            ArtifactSelect.All             => artifactList,
            ArtifactSelect.Latest          => artifactList.Count > 0 ? [artifactList[^1]] : [],
            ArtifactSelect.First           => artifactList.Count > 0 ? [artifactList[0]]  : [],
            ArtifactSelect.Closest         => SelectClosest(artifactList, closestToVersion!),
            _                              => [],
        };

        return selected.Select(a => $"{baseUrl}{a}");
    }

    /// <summary>
    /// Resolves the effective artifact URL from a dynamic project settings object, mirroring the
    /// AL-Go DetermineArtifactUrl logic and using <see cref="GetBcArtifactUrlAsync"/> for lookups.
    /// </summary>
    public static async Task<string> DetermineArtifactUrlAsync(
        JsonElement projectSettings,
        bool doNotIssueWarnings = false)
    {
        var artifact = GetStringProperty(projectSettings, "artifact");

        var projectCountry = GetStringProperty(projectSettings, "country");
        var applicationDependencyText = GetStringProperty(projectSettings, "applicationDependency");
        var applicationDependency = string.IsNullOrWhiteSpace(applicationDependencyText)
            ? null
            : Version.Parse(applicationDependencyText);

        if (string.IsNullOrEmpty(artifact) && GetBoolProperty(projectSettings, "updateDependencies"))
        {
            artifact = (await GetBcArtifactUrlAsync(
                country: projectCountry,
                select: ArtifactSelect.All))
                .Where(url => applicationDependency is not null && GetArtifactVersion(url) >= applicationDependency)
                .FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrEmpty(artifact))
            {
                artifact = (await GetBcArtifactUrlAsync(
                    storageAccount: "bcinsider",
                    country: projectCountry,
                    select: ArtifactSelect.All))
                    .Where(url => applicationDependency is not null && GetArtifactVersion(url) >= applicationDependency)
                    .FirstOrDefault() ?? string.Empty;

                if (string.IsNullOrEmpty(artifact))
                    throw new InvalidOperationException($"No artifacts found for application dependency {applicationDependencyText}.");
            }
        }

        string artifactUrl;
        string storageAccount;
        string artifactType;
        string version;
        string country;

        if (artifact.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            artifactUrl = artifact;
            storageAccount = ($"{artifactUrl}////").Split('/')[2];
            artifactType = ($"{artifactUrl}////").Split('/')[3];
            version = ($"{artifactUrl}////").Split('/')[4];
            country = GetArtifactCountry(artifactUrl);
        }
        else
        {
            var segments = ($"{artifact}/////").Split('/');
            storageAccount = segments[0];
            artifactType = string.IsNullOrEmpty(segments[1]) ? "Sandbox" : segments[1];
            version = segments[2];
            country = string.IsNullOrEmpty(segments[3]) ? projectCountry : segments[3];
            var select = string.IsNullOrEmpty(segments[4]) ? "latest" : segments[4];
            var parsedSelect = ParseArtifactSelect(select);
            var parsedType = Enum.Parse<ArtifactType>(artifactType, ignoreCase: true);

            if (version == "*")
            {
                if (applicationDependency is null)
                    throw new InvalidOperationException("applicationDependency must be specified when artifact version is '*'.");

                version = $"{applicationDependency.Major}.{applicationDependency.Minor}";

                var allArtifactUrls = (await GetBcArtifactUrlAsync(
                    storageAccount: storageAccount,
                    type: parsedType,
                    version: version,
                    country: country,
                    select: ArtifactSelect.All))
                    .Where(url => GetArtifactVersion(url) >= applicationDependency)
                    .ToList();

                artifactUrl = parsedSelect switch
                {
                    ArtifactSelect.Latest => allArtifactUrls.LastOrDefault() ?? string.Empty,
                    ArtifactSelect.First => allArtifactUrls.FirstOrDefault() ?? string.Empty,
                    _ => throw new InvalidOperationException($"Invalid artifact setting ({artifact}). Version can only be '*' if select is first or latest."),
                };

                if (string.IsNullOrEmpty(artifactUrl))
                {
                    throw new InvalidOperationException(
                        $"No artifacts found for the artifact setting ({artifact}), when application dependency is {applicationDependency}");
                }
            }
            else
            {
                artifactUrl = (await GetBcArtifactUrlAsync(
                    storageAccount: storageAccount,
                    type: parsedType,
                    version: version,
                    country: country,
                    select: parsedSelect))
                    .FirstOrDefault() ?? string.Empty;

                if (string.IsNullOrEmpty(artifactUrl))
                    throw new InvalidOperationException($"No artifacts found for the artifact setting ({artifact}).");
            }

            version = artifactUrl.Split('/')[4];
            storageAccount = artifactUrl.Split('/')[2];
        }

        var additionalCountries = GetStringListProperty(projectSettings, "additionalCountries");
        if (additionalCountries.Count > 0 || !string.Equals(country, projectCountry, StringComparison.OrdinalIgnoreCase))
        {
            var artifactVersion = Version.Parse(version);

            var atArtifactUrl = (await GetBcArtifactUrlAsync(
                storageAccount: storageAccount,
                type: Enum.Parse<ArtifactType>(artifactType, ignoreCase: true),
                country: "at",
                version: $"{artifactVersion.Major}.{artifactVersion.Minor}",
                select: ArtifactSelect.Latest))
                .FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrEmpty(atArtifactUrl))
                throw new InvalidOperationException("Latest AT artifact could not be determined.");

            var latestAtVersion = atArtifactUrl.Split('/')[4];
            var countries = (await GetBcArtifactUrlAsync(
                storageAccount: storageAccount,
                type: Enum.Parse<ArtifactType>(artifactType, ignoreCase: true),
                version: latestAtVersion,
                select: ArtifactSelect.All))
                .Select(GetArtifactCountry)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allowedCountries = countries
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!allowedCountries.Contains(projectCountry, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Country ({projectCountry}) is not a valid country code.");

            var illegalCountries = additionalCountries
                .Where(item => !allowedCountries.Contains(item, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (illegalCountries.Count > 0)
            {
                throw new InvalidOperationException(
                    $"additionalCountries contains one or more invalid country codes ({string.Join(",", illegalCountries)}).");
            }

            artifactUrl = artifactUrl.Replace(artifactUrl.Split('/')[4], atArtifactUrl.Split('/')[4], StringComparison.Ordinal);
        }

        return artifactUrl;
    }

    // ---------------------------------------------------------------------------
    // Private selection helpers
    // ---------------------------------------------------------------------------

    private static IEnumerable<string> SelectClosest(List<string> artifacts, Version target)
    {
        // First entry whose version is >= target; fall back to the highest available
        var closest = artifacts.FirstOrDefault(a => Version.Parse(a.Split('/')[0]) >= target)
                   ?? artifacts.LastOrDefault();
        return closest is not null ? [closest] : [];
    }
}
