using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fkh.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

/// <summary>
/// Drives automatic AKS cluster start/stop based on the '_admins.Uptime' schedule,
/// holiday exclusions, and one-off overrides. Invoked every minute by the timer.
/// </summary>
public class FkhClusterSchedule
{
    private static readonly string[] DayKeys = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private const int ScanDays = 14;

    private readonly ILogger<FkhClusterSchedule> _logger;
    private readonly FkhClusterControl _clusterControl;
    private readonly FkhUserSettings _settings;
    private readonly FkhHolidayService _holidays;

    public FkhClusterSchedule(
        ILogger<FkhClusterSchedule> logger,
        FkhClusterControl clusterControl,
        FkhUserSettings settings,
        FkhHolidayService holidays)
    {
        _logger = logger;
        _clusterControl = clusterControl;
        _settings = settings;
        _holidays = holidays;
    }

    public async Task CheckAndApplyScheduleAsync()
    {
        var nowUtc = DateTimeOffset.UtcNow;

        var powerState = await _clusterControl.GetPowerStateAsync();
        // Only act on stable states — never interrupt an in-progress start/stop.
        var running = string.Equals(powerState, "Running", StringComparison.OrdinalIgnoreCase);
        var stopped = string.Equals(powerState, "Stopped", StringComparison.OrdinalIgnoreCase);
        if (!running && !stopped)
            return;

        var overrides = await _clusterControl.GetOverridesAsync();

        // 1. Pending overrides fire first.
        if (overrides.NextStop is { } nextStop && nowUtc >= nextStop)
        {
            if (running)
            {
                _logger.LogInformation("Schedule: firing one-off stop (scheduled {StopAt} UTC).", nextStop);
                await _clusterControl.StopClusterForScheduleAsync();
            }
            overrides.NextStop = null;
            await _clusterControl.SaveOverridesAsync(overrides);
            return;
        }

        if (overrides.NextStart is { } nextStart && nowUtc >= nextStart)
        {
            if (stopped)
            {
                _logger.LogInformation("Schedule: firing one-off start (scheduled {StartAt} UTC).", nextStart);
                await _clusterControl.StartClusterForScheduleAsync();
            }
            overrides.NextStart = null;
            await _clusterControl.SaveOverridesAsync(overrides);
            return;
        }

        // 2. Recurring schedule — edge-triggered. Each start/stop time fires once; a manual
        //    start/stop is then respected until the next scheduled edge. This also lets a rule
        //    be one-directional (start-only never auto-stops, stop-only never auto-starts).
        var cfg = await GetUptimeConfigAsync();
        if (cfg?.Weekdays is null || cfg.Weekdays.Count == 0)
            return; // scheduler disabled

        var tz = ResolveTimeZone(cfg.TimeZone);
        var (recentStart, recentStop) = await MostRecentEdgesAsync(cfg, tz, nowUtc);

        // The later of the two past edges decides the current desired state.
        var startGoverns = recentStart is { } rs && (recentStop is null || rs >= recentStop);
        var stopGoverns = recentStop is { } rt && (recentStart is null || rt > recentStart);
        var changed = false;

        if (recentStart is { } startEdge && !(overrides.LastScheduleStart is { } ls && ls >= startEdge))
        {
            if (startGoverns && stopped && overrides.NextStart is null)
            {
                _logger.LogInformation("Schedule: starting cluster (start edge {Edge} UTC).", startEdge);
                await _clusterControl.StartClusterForScheduleAsync();
            }
            overrides.LastScheduleStart = startEdge;
            changed = true;
        }

        if (recentStop is { } stopEdge && !(overrides.LastScheduleStop is { } lt && lt >= stopEdge))
        {
            if (stopGoverns && running && overrides.NextStop is null)
            {
                _logger.LogInformation("Schedule: stopping cluster (stop edge {Edge} UTC).", stopEdge);
                await _clusterControl.StopClusterForScheduleAsync();
            }
            overrides.LastScheduleStop = stopEdge;
            changed = true;
        }

        if (changed)
            await _clusterControl.SaveOverridesAsync(overrides);
    }

    /// <summary>Builds a read-only summary of the schedule for status reporting.</summary>
    public async Task<ScheduleSummary> GetSummaryAsync(DateTimeOffset nowUtc)
    {
        var overrides = await _clusterControl.GetOverridesAsync();
        var summary = new ScheduleSummary
        {
            OverrideStart = overrides.NextStart,
            OverrideStop = overrides.NextStop,
        };

        var cfg = await GetUptimeConfigAsync();
        if (cfg?.Weekdays is null || cfg.Weekdays.Count == 0)
            return summary;

        summary.Enabled = true;
        var tz = ResolveTimeZone(cfg.TimeZone);
        summary.TimeZone = tz.Id;

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime);
        summary.TodayExcluded = await IsExcludedAsync(cfg, localToday);
        summary.TodayWindow = summary.TodayExcluded
            ? null
            : (cfg.Weekdays.TryGetValue(DayKeys[(int)localToday.DayOfWeek], out var w) ? w : null);

        summary.DesiredRunning = summary.TodayExcluded ? false : await ComputeDesiredRunningAsync(cfg, tz, nowUtc);
        (summary.NextStart, summary.NextStop) = await ComputeNextTransitionsAsync(cfg, tz, nowUtc);
        return summary;
    }

    private async Task<UptimeConfig?> GetUptimeConfigAsync()
    {
        var node = await _settings.GetGlobalSettingAsync("Uptime");
        if (node is null)
            return null;
        try
        {
            return node.Deserialize<UptimeConfig>(JsonSerializerOptions.Web);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse '_admins.Uptime' setting. Scheduler disabled.");
            return null;
        }
    }

    /// <summary>A single day's schedule edges (either may be absent for a one-directional rule).</summary>
    private readonly record struct DayWindow(DateTimeOffset? Start, DateTimeOffset? Stop);

    /// <summary>Desired running state = the later of the two most recent past edges was a start.</summary>
    private async Task<bool> ComputeDesiredRunningAsync(UptimeConfig cfg, TimeZoneInfo tz, DateTimeOffset nowUtc)
    {
        var (recentStart, recentStop) = await MostRecentEdgesAsync(cfg, tz, nowUtc);
        return recentStart is { } rs && (recentStop is null || rs >= recentStop);
    }

    /// <summary>Finds the most recent past start edge and stop edge (each searched independently).</summary>
    private async Task<(DateTimeOffset? Start, DateTimeOffset? Stop)> MostRecentEdgesAsync(
        UptimeConfig cfg, TimeZoneInfo tz, DateTimeOffset nowUtc)
    {
        DateTimeOffset? start = null, stop = null;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime);

        for (var offset = 0; offset <= ScanDays; offset++)
        {
            var window = await GetWindowAsync(cfg, tz, localToday.AddDays(-offset));
            if (window is not { } w)
                continue;

            if (start is null && w.Start is { } s && s <= nowUtc)
                start = s;
            if (stop is null && w.Stop is { } t && t <= nowUtc)
                stop = t;

            if (start is not null && stop is not null)
                break;
        }

        return (start, stop);
    }

    private async Task<(DateTimeOffset? NextStart, DateTimeOffset? NextStop)> ComputeNextTransitionsAsync(
        UptimeConfig cfg, TimeZoneInfo tz, DateTimeOffset nowUtc)
    {
        DateTimeOffset? nextStart = null, nextStop = null;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime);

        for (var offset = 0; offset <= ScanDays; offset++)
        {
            var window = await GetWindowAsync(cfg, tz, localToday.AddDays(offset));
            if (window is not { } w)
                continue;

            if (nextStart is null && w.Start is { } s && s > nowUtc)
                nextStart = s;
            if (nextStop is null && w.Stop is { } t && t > nowUtc)
                nextStop = t;

            if (nextStart is not null && nextStop is not null)
                break;
        }

        return (nextStart, nextStop);
    }

    private async Task<DayWindow?> GetWindowAsync(
        UptimeConfig cfg, TimeZoneInfo tz, DateOnly localDate)
    {
        if (await IsExcludedAsync(cfg, localDate))
            return null;

        if (cfg.Weekdays is null || !cfg.Weekdays.TryGetValue(DayKeys[(int)localDate.DayOfWeek], out var range)
            || string.IsNullOrWhiteSpace(range))
            return null;

        if (!TryParseRange(range, out var startTime, out var stopTime))
            return null;

        DateTimeOffset? start = startTime is { } st
            ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(st), tz), TimeSpan.Zero)
            : null;
        DateTimeOffset? stop = stopTime is { } sp
            ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(sp), tz), TimeSpan.Zero)
            : null;
        return new DayWindow(start, stop);
    }

    private Task<bool> IsExcludedAsync(UptimeConfig cfg, DateOnly localDate)
    {
        var holidays = cfg.UseNagerHolidays;
        if (holidays is null || string.IsNullOrWhiteSpace(holidays.Countries))
            return Task.FromResult(false);
        return _holidays.IsExcludedAsync(localDate, holidays.Countries, holidays.Types);
    }

    private static bool TryParseRange(string range, out TimeOnly? start, out TimeOnly? stop)
    {
        start = null;
        stop = null;
        var parts = range.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (parts[0].Length > 0)
        {
            if (!TimeOnly.TryParse(parts[0], CultureInfo.InvariantCulture, out var s))
                return false;
            start = s;
        }
        if (parts[1].Length > 0)
        {
            if (!TimeOnly.TryParse(parts[1], CultureInfo.InvariantCulture, out var t))
                return false;
            stop = t;
        }

        if (start is null && stop is null)
            return false; // "-" / empty is not a valid rule
        if (start is { } sv && stop is { } tv && tv <= sv)
            return false; // a full window must stop after it starts
        return true;
    }

    private TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            _logger.LogWarning("Uptime schedule has no TimeZone set; defaulting to UTC.");
            return TimeZoneInfo.Utc;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogWarning("Unknown TimeZone '{TimeZone}' in Uptime schedule; defaulting to UTC.", timeZoneId);
            return TimeZoneInfo.Utc;
        }
    }
}
