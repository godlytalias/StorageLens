using System.Text.Json;
using StorageLens.Models;
using Windows.Storage;

namespace StorageLens.Services;

public static class AutomationSettingsService
{
    private const string SettingsKey = "StorageLens.AutomationSettings.v2";
    private const string LoadWarningKey = "StorageLens.AutomationSettings.LoadWarning";
    private static readonly Mutex SettingsMutex = new(false, @"Local\StorageLens.AutomationSettings");

    public static string? LastLoadWarning { get; private set; }

    public static AutomationSettings Load()
    {
        return RunExclusive(LoadUnlocked);
    }

    public static void Save(AutomationSettings settings)
    {
        RunExclusive(() =>
        {
            var current = LoadUnlocked();
            var currentById = current.CleanupTasks.ToDictionary(task => task.Id);
            foreach (var task in settings.CleanupTasks)
            {
                if (currentById.TryGetValue(task.Id, out var existing) &&
                    existing.LastRun > task.LastRun)
                {
                    task.LastRun = existing.LastRun;
                }
            }

            if (current.LastBackgroundIndexUtc > settings.LastBackgroundIndexUtc)
            {
                settings.LastBackgroundIndexUtc = current.LastBackgroundIndexUtc;
                settings.LastBackgroundIndexStatus = current.LastBackgroundIndexStatus;
            }

            SaveUnlocked(settings);
            ApplicationData.Current.LocalSettings.Values.Remove(LoadWarningKey);
            LastLoadWarning = null;
        });
    }

    public static bool TryClaimScheduleRun(
        Guid scheduleId,
        DateTimeOffset now,
        out ScheduledCleanupTaskSnapshot? schedule,
        out DateTimeOffset previousLastRun)
    {
        ScheduledCleanupTaskSnapshot? claimed = null;
        DateTimeOffset previous = default;
        var result = RunExclusive(() =>
        {
            var settings = LoadUnlocked();
            if (!TryClaimScheduleRunCore(
                    settings,
                    scheduleId,
                    now,
                    out claimed,
                    out previous))
            {
                return false;
            }

            SaveUnlocked(settings);
            return true;
        });
        schedule = claimed;
        previousLastRun = previous;
        return result;
    }

    public static void RestoreScheduleRun(
        Guid scheduleId,
        DateTimeOffset claimedAt,
        DateTimeOffset previousLastRun)
    {
        RunExclusive(() =>
        {
            var settings = LoadUnlocked();
            if (!RestoreScheduleRunCore(
                    settings,
                    scheduleId,
                    claimedAt,
                    previousLastRun))
            {
                return;
            }

            SaveUnlocked(settings);
        });
    }

    public static void RecordBackgroundIndexResult(
        DateTimeOffset completedUtc,
        string status)
    {
        RunExclusive(() =>
        {
            var settings = LoadUnlocked();
            settings.LastBackgroundIndexUtc = completedUtc;
            settings.LastBackgroundIndexStatus = status;
            SaveUnlocked(settings);
        });
    }

    internal static bool TryClaimScheduleRunCore(
        AutomationSettings settings,
        Guid scheduleId,
        DateTimeOffset now,
        out ScheduledCleanupTaskSnapshot? schedule,
        out DateTimeOffset previousLastRun)
    {
        schedule = null;
        previousLastRun = default;
        var current = settings.CleanupTasks.FirstOrDefault(task => task.Id == scheduleId);
        if (current is null ||
            !current.IsEnabled ||
            current.LastRun.AddDays(Math.Max(1, current.IntervalDays)) > now)
        {
            return false;
        }

        previousLastRun = current.LastRun;
        current.LastRun = now;
        schedule = Clone(current);
        return true;
    }

    internal static bool RestoreScheduleRunCore(
        AutomationSettings settings,
        Guid scheduleId,
        DateTimeOffset claimedAt,
        DateTimeOffset previousLastRun)
    {
        var current = settings.CleanupTasks.FirstOrDefault(task => task.Id == scheduleId);
        if (current is null || current.LastRun != claimedAt)
        {
            return false;
        }

        current.LastRun = previousLastRun;
        return true;
    }

    private static AutomationSettings LoadUnlocked()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        var migratedLegacySettings = false;
        if (!values.TryGetValue(SettingsKey, out var raw) &&
            values.TryGetValue("StorageLens.AutomationSettings.v1", out raw))
        {
            migratedLegacySettings = true;
        }

        if (raw is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var hadVersion = document.RootElement.TryGetProperty(
                    nameof(AutomationSettings.Version),
                    out _);
                var settings = JsonSerializer.Deserialize<AutomationSettings>(json) ?? CreateDefault();
                Normalize(settings);
                if (migratedLegacySettings || !hadVersion || settings.Version < 3)
                {
                    settings.CleanupTasks.RemoveAll(task =>
                        task.Name is "Weekly cache review" or "Monthly duplicate review");
                    settings.Version = 3;
                    SaveUnlocked(settings);
                    if (migratedLegacySettings)
                    {
                        values.Remove("StorageLens.AutomationSettings.v1");
                    }
                }

                settings.MinimumTrackedFolderSizeGb =
                    Math.Max(1, settings.MinimumTrackedFolderSizeGb);
                return settings;
            }
            catch (JsonException)
            {
                return RecoverInvalidSettings(raw, migratedLegacySettings);
            }
        }

        if (raw is not null)
        {
            return RecoverInvalidSettings(raw, migratedLegacySettings);
        }

        if (ApplicationData.Current.LocalSettings.Values.TryGetValue(
                LoadWarningKey,
                out var warning) &&
            warning is string warningText)
        {
            LastLoadWarning = warningText;
        }

        return CreateDefault();
    }

    private static AutomationSettings RecoverInvalidSettings(
        object raw,
        bool migratedLegacySettings)
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        var warning = "Automation settings were unreadable and were reset to safe defaults. The original data was preserved locally.";
        values[$"StorageLens.AutomationSettings.Invalid.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"] =
            raw.ToString();
        values.Remove(
            migratedLegacySettings
                ? "StorageLens.AutomationSettings.v1"
                : SettingsKey);
        values[LoadWarningKey] = warning;
        LastLoadWarning = warning;
        return CreateDefault();
    }

    private static void SaveUnlocked(AutomationSettings settings)
    {
        Normalize(settings);
        settings.Version = 3;
        ApplicationData.Current.LocalSettings.Values[SettingsKey] =
            JsonSerializer.Serialize(settings);
    }

    private static void Normalize(AutomationSettings settings)
    {
        settings.MonitorIntervalDays = settings.MonitorIntervalDays is 2 or 7
            ? settings.MonitorIntervalDays
            : 1;
        settings.LowSpaceThresholdGb = Math.Clamp(settings.LowSpaceThresholdGb, 1, 500);
        settings.GrowthThresholdGb = Math.Clamp(settings.GrowthThresholdGb, 1, 500);
        settings.MinimumTrackedFolderSizeGb =
            Math.Clamp(settings.MinimumTrackedFolderSizeGb, 1, 500);
        settings.BackgroundIndexIntervalDays =
            Math.Clamp(settings.BackgroundIndexIntervalDays, 1, 7);
        settings.CleanupTasks ??= [];

        var seenIds = new HashSet<Guid>();
        settings.CleanupTasks.RemoveAll(task =>
        {
            if (task is null)
            {
                return true;
            }

            if (task.Id == Guid.Empty)
            {
                task.Id = Guid.NewGuid();
            }

            if (!seenIds.Add(task.Id))
            {
                return true;
            }

            task.Name = string.IsNullOrWhiteSpace(task.Name)
                ? "Cleanup schedule"
                : task.Name.Trim();
            task.IntervalDays = Math.Clamp(task.IntervalDays, 1, 30);
            task.Categories = (task.Categories ?? [])
                .Select(CleanupPolicyService.ParsePolicy)
                .Where(policy => policy.HasValue)
                .Select(policy => CleanupPolicyService.GetDefinition(policy!.Value))
                .Where(definition => definition.Safety != CleanupSafety.Caution)
                .Select(definition => definition.Id switch
                {
                    CleanupPolicyId.TemporaryFiles => "Cache & temporary",
                    CleanupPolicyId.OldDownloads => "Downloads",
                    CleanupPolicyId.PackageCaches => "Package caches",
                    CleanupPolicyId.LargeDuplicates => "Duplicates",
                    _ => definition.Name,
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (task.Mode == CleanupMode.RecycleSafeItems &&
                task.Categories.Any(category =>
                    CleanupPolicyService.ParsePolicy(category) is not CleanupPolicyId policy ||
                    !CleanupPolicyService.GetDefinition(policy).AutomaticEligible))
            {
                task.Mode = CleanupMode.ReviewOnly;
            }

            return task.Categories.Count == 0;
        });
    }

    private static AutomationSettings CreateDefault() =>
        new()
        {
            MonitorEnabled = false,
            MonitorIntervalDays = 1,
            NotifyOnLowSpace = true,
            NotifyOnFolderGrowth = true,
            LowSpaceThresholdGb = 20,
            GrowthThresholdGb = 5,
            MinimumTrackedFolderSizeGb = 1,
            BackgroundIndexEnabled = false,
            BackgroundIndexIntervalDays = 1,
            CleanupTasks = [],
        };

    private static ScheduledCleanupTaskSnapshot Clone(
        ScheduledCleanupTaskSnapshot schedule) =>
        new()
        {
            Id = schedule.Id,
            Name = schedule.Name,
            IntervalDays = schedule.IntervalDays,
            Categories = schedule.Categories.ToList(),
            IsEnabled = schedule.IsEnabled,
            Mode = schedule.Mode,
            LastRun = schedule.LastRun,
        };

    private static T RunExclusive<T>(Func<T> action)
    {
        var acquired = false;
        try
        {
            try
            {
                acquired = SettingsMutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("Timed out waiting for automation settings.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                SettingsMutex.ReleaseMutex();
            }
        }
    }

    private static void RunExclusive(Action action)
    {
        RunExclusive(() =>
        {
            action();
            return true;
        });
    }
}
