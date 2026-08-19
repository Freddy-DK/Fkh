namespace Fkh.Models;

/// <summary>Recurring cluster uptime schedule, stored as the '_admins.Uptime' setting.</summary>
public sealed class UptimeConfig
{
    public string? TimeZone { get; set; }

    /// <summary>
    /// Per-weekday uptime rule (keys: Mon..Sun; a missing day is off). Supported formats:
    ///   "HH:mm-HH:mm" — auto-start at the first time and auto-stop at the second.
    ///   "HH:mm-"      — start-only: auto-start at the time, never auto-stop (manual stop).
    ///   "-HH:mm"      — stop-only: auto-stop at the time, never auto-start (manual start).
    /// </summary>
    public Dictionary<string, string>? Weekdays { get; set; }

    public NagerHolidayConfig? UseNagerHolidays { get; set; }
}

public sealed class NagerHolidayConfig
{
    /// <summary>Comma-separated country/subdivision codes, e.g. "DK,DE,DE-BE".</summary>
    public string? Countries { get; set; }

    /// <summary>Comma-separated holiday-type globs, e.g. "Public,Bank" or "*".</summary>
    public string? Types { get; set; }
}

/// <summary>A single holiday entry from the Nager.Date API.</summary>
public sealed class NagerHoliday
{
    public string Date { get; set; } = "";
    public bool Global { get; set; }
    public List<string>? Counties { get; set; }
    public List<string>? Types { get; set; }
    public string? CountryCode { get; set; }
}

/// <summary>One-off cluster start/stop overrides, stored in the 'clusterschedule.json' blob.</summary>
public sealed class ClusterScheduleOverrides
{
    public DateTimeOffset? NextStart { get; set; }
    public DateTimeOffset? NextStop { get; set; }

    /// <summary>Most recent recurring start/stop edge already applied. Prevents an edge from
    /// re-firing, so a manual start/stop is respected until the next scheduled edge.</summary>
    public DateTimeOffset? LastScheduleStart { get; set; }
    public DateTimeOffset? LastScheduleStop { get; set; }
}

/// <summary>Computed view of the cluster schedule for status reporting.</summary>
public sealed class ScheduleSummary
{
    public bool Enabled { get; set; }
    public string? TimeZone { get; set; }
    public bool DesiredRunning { get; set; }
    public bool TodayExcluded { get; set; }
    public string? TodayWindow { get; set; }
    public DateTimeOffset? NextStart { get; set; }
    public DateTimeOffset? NextStop { get; set; }
    public DateTimeOffset? OverrideStart { get; set; }
    public DateTimeOffset? OverrideStop { get; set; }
}
