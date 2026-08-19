using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fkh.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

/// <summary>
/// Evaluates public/bank holidays via the Nager.Date API to decide whether a given
/// date should be excluded from the cluster uptime schedule.
/// </summary>
public class FkhHolidayService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ILogger<FkhHolidayService> _logger;

    // Cached per (countryCode:year). Failed fetches are evicted so they are retried.
    private readonly ConcurrentDictionary<string, Task<List<NagerHoliday>?>> _cache = new();

    public FkhHolidayService(ILogger<FkhHolidayService> logger) => _logger = logger;

    /// <summary>
    /// A date is excluded only when EVERY listed country/subdivision has a matching holiday
    /// on that date. If any country is a working day — or the API is unreachable — the date
    /// is not excluded (fail ON).
    /// </summary>
    public async Task<bool> IsExcludedAsync(DateOnly date, string? countries, string? types)
    {
        if (string.IsNullOrWhiteSpace(countries))
            return false;

        var tokens = countries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        var typeGlobs = (string.IsNullOrWhiteSpace(types) ? "*" : types)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (typeGlobs.Length == 0)
            typeGlobs = new[] { "*" };

        var iso = date.ToString("yyyy-MM-dd");

        foreach (var token in tokens)
        {
            var dash = token.IndexOf('-');
            var countryCode = dash > 0 ? token[..dash] : token;
            var subdivision = dash > 0 ? token : null;

            var holidays = await GetHolidaysAsync(countryCode, date.Year);
            if (holidays is null)
                return false; // fail ON: cannot confirm a holiday, so treat as a working day

            var match = holidays.Any(h =>
                string.Equals(h.Date, iso, StringComparison.Ordinal)
                && TypeMatches(h.Types, typeGlobs)
                && SubdivisionMatches(h, subdivision));

            if (!match)
                return false; // someone in this country is working
        }

        return true;
    }

    private static bool SubdivisionMatches(NagerHoliday holiday, string? subdivision)
    {
        if (subdivision is null)
            return holiday.Global;
        return holiday.Global
            || (holiday.Counties is not null && holiday.Counties.Contains(subdivision, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TypeMatches(List<string>? holidayTypes, string[] globs)
    {
        if (holidayTypes is null || holidayTypes.Count == 0)
            return false;
        return holidayTypes.Any(t => globs.Any(g => GlobMatch(t, g)));
    }

    private static bool GlobMatch(string value, string pattern)
    {
        if (pattern == "*")
            return true;
        if (!pattern.Contains('*'))
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private async Task<List<NagerHoliday>?> GetHolidaysAsync(string countryCode, int year)
    {
        var key = $"{countryCode.ToUpperInvariant()}:{year}";
        var task = _cache.GetOrAdd(key, _ => FetchHolidaysAsync(countryCode, year));
        var result = await task;
        if (result is null)
            _cache.TryRemove(new KeyValuePair<string, Task<List<NagerHoliday>?>>(key, task));
        return result;
    }

    private async Task<List<NagerHoliday>?> FetchHolidaysAsync(string countryCode, int year)
    {
        try
        {
            var url = $"https://date.nager.at/api/v3/PublicHolidays/{year}/{countryCode.ToUpperInvariant()}";
            var json = await _http.GetStringAsync(url);
            return JsonSerializer.Deserialize<List<NagerHoliday>>(json, JsonSerializerOptions.Web) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Nager holidays for {Country} {Year}. Treating date as a working day.", countryCode, year);
            return null;
        }
    }
}
