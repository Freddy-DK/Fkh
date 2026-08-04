namespace Fkh.Models;

/// <summary>Recurring cluster uptime schedule, stored as the '_admins.Uptime' setting.</summary>
public sealed class UptimeConfig
{
    public string? TimeZone { get; set; }

    /// <summary>Per-weekday uptime window as "HH:mm-HH:mm" (keys: Mon..Sun). A missing day is off.</summary>
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
