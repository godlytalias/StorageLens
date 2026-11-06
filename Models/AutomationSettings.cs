namespace StorageLens.Models;

public sealed class AutomationSettings
{
    public int Version { get; set; } = 3;

    public bool MonitorEnabled { get; set; }

    public int MonitorIntervalDays { get; set; } = 1;

    public bool NotifyOnLowSpace { get; set; } = true;

    public bool NotifyOnFolderGrowth { get; set; } = true;

    public int LowSpaceThresholdGb { get; set; } = 20;

    public int GrowthThresholdGb { get; set; } = 5;

    public int MinimumTrackedFolderSizeGb { get; set; } = 1;

    public bool BackgroundIndexEnabled { get; set; }

    public int BackgroundIndexIntervalDays { get; set; } = 1;

    public DateTimeOffset? LastBackgroundIndexUtc { get; set; }

    public string? LastBackgroundIndexStatus { get; set; }

    public List<ScheduledCleanupTaskSnapshot> CleanupTasks { get; set; } = [];
}

public sealed class ScheduledCleanupTaskSnapshot
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public int IntervalDays { get; set; }

    public List<string> Categories { get; set; } = [];

    public bool IsEnabled { get; set; }

    public CleanupMode Mode { get; set; } = CleanupMode.ReviewOnly;

    public DateTimeOffset LastRun { get; set; }
}
