using Microsoft.Windows.ApplicationModel.Background;
using StorageLens.BackgroundTasks;
using StorageLens.Models;
using Windows.ApplicationModel.Background;

namespace StorageLens.Services;

public static class BackgroundTaskRegistrationService
{
    private const string TaskNamePrefix = "StorageLensDailyStorageMonitor";
    private const string IndexTaskPrefix = "StorageLensIdleDriveIndex";
    private const string IndexMaintenanceTaskName =
        IndexTaskPrefix + "-v4-maintenance";
    private const string IndexUserAwayTaskName =
        IndexTaskPrefix + "-v4-user-away";
    private const uint IndexMaintenanceFreshnessMinutes = 15;
    private static readonly SemaphoreSlim RegistrationLock = new(1, 1);

    public static async Task<string> ApplyAutomationRegistrationAsync(
        AutomationSettings settings)
    {
        var (shouldRegister, intervalDays) = GetRegistrationPlan(settings);
        var monitoringStatus =
            await ApplyMonitoringRegistrationAsync(shouldRegister, intervalDays);
        var indexStatus =
            await ApplyIndexRegistrationAsync(
                settings.BackgroundIndexEnabled,
                settings.BackgroundIndexIntervalDays);
        return $"{monitoringStatus} {indexStatus}".Trim();
    }

    internal static (bool ShouldRegister, int IntervalDays) GetRegistrationPlan(
        AutomationSettings settings)
    {
        var intervals = settings.CleanupTasks
            .Where(task => task.IsEnabled)
            .Select(task => Math.Max(1, task.IntervalDays))
            .ToList();
        if (settings.MonitorEnabled)
        {
            intervals.Add(Math.Max(1, settings.MonitorIntervalDays));
        }

        return (intervals.Count > 0, intervals.Count > 0 ? intervals.Min() : 1);
    }

    public static async Task<string> ApplyMonitoringRegistrationAsync(bool shouldRegister, int intervalDays)
    {
        await RegistrationLock.WaitAsync();
        try
        {
            if (!shouldRegister)
            {
                UnregisterAllExcept(null);
                return "Storage monitoring is off.";
            }

            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access is BackgroundAccessStatus.DeniedBySystemPolicy or
                BackgroundAccessStatus.DeniedByUser)
            {
                return "Windows blocked background execution. Open Windows Settings and allow background access for Storage Lens.";
            }

            var intervalMinutes = (uint)Math.Clamp(intervalDays * 24 * 60, 15, 10080);
            var taskName = $"{TaskNamePrefix}-{intervalMinutes}";
            if (!BackgroundTaskRegistration.AllTasks.Values.Any(registration =>
                    string.Equals(registration.Name, taskName, StringComparison.OrdinalIgnoreCase)))
            {
                var builder = new Microsoft.Windows.ApplicationModel.Background.BackgroundTaskBuilder
                {
                    Name = taskName,
                };
                builder.SetTaskEntryPointClsid(typeof(StorageMonitorBackgroundTask).GUID);
                builder.SetTrigger(new TimeTrigger(intervalMinutes, false));
                builder.Register();
            }

            UnregisterAllExcept(taskName);
            return $"Storage monitoring is registered. Checks run approximately every {intervalDays} day{(intervalDays == 1 ? string.Empty : "s")}.";
        }
        catch (Exception exception)
        {
            return $"Background monitoring could not be registered: {exception.Message}";
        }
        finally
        {
            RegistrationLock.Release();
        }
    }

    public static async Task<string> ApplyIndexRegistrationAsync(
        bool shouldRegister,
        int intervalDays = 1)
    {
        await RegistrationLock.WaitAsync();
        try
        {
            if (!shouldRegister)
            {
                UnregisterIndexTask();
                return "Daily idle indexing is off.";
            }

            intervalDays = Math.Clamp(intervalDays, 1, 7);
            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access is BackgroundAccessStatus.DeniedBySystemPolicy or
                BackgroundAccessStatus.DeniedByUser)
            {
                return "Windows blocked daily idle indexing.";
            }

            var plan = GetIndexRegistrationPlan(true);
            foreach (var task in plan)
            {
                if (BackgroundTaskRegistration.AllTasks.Values.Any(registration =>
                        string.Equals(
                            registration.Name,
                            task.Name,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var builder = new Microsoft.Windows.ApplicationModel.Background.BackgroundTaskBuilder
                {
                    Name = task.Name,
                };
                builder.SetTaskEntryPointClsid(typeof(StorageIndexBackgroundTask).GUID);
                if (task.Trigger == BackgroundIndexTriggerKind.Maintenance)
                {
                    builder.SetTrigger(new MaintenanceTrigger(
                        task.FreshnessMinutes,
                        false));
                }
                else
                {
                    builder.SetTrigger(new SystemTrigger(
                        SystemTriggerType.UserAway,
                        false));
                }

                builder.Register();
            }

            UnregisterIndexTaskExcept(plan.Select(task => task.Name).ToList());
            return
                $"Drive indexing is registered approximately every {intervalDays} day" +
                $"{(intervalDays == 1 ? string.Empty : "s")} at Windows idle opportunities.";
        }
        catch (Exception exception)
        {
            return $"Daily idle indexing could not be registered: {exception.Message}";
        }
        finally
        {
            RegistrationLock.Release();
        }
    }

    internal static IReadOnlyList<BackgroundIndexTaskPlan>
        GetIndexRegistrationPlan(bool shouldRegister)
    {
        if (!shouldRegister)
        {
            return [];
        }

        return
        [
            new BackgroundIndexTaskPlan(
                IndexMaintenanceTaskName,
                BackgroundIndexTriggerKind.Maintenance,
                IndexMaintenanceFreshnessMinutes),
            new BackgroundIndexTaskPlan(
                IndexUserAwayTaskName,
                BackgroundIndexTriggerKind.UserAway,
                0),
        ];
    }

    public static async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = AutomationSettingsService.Load();
        await ApplyAutomationRegistrationAsync(settings);
    }

    private static void UnregisterAllExcept(string? retainedName)
    {
        foreach (var registration in BackgroundTaskRegistration.AllTasks.Values.ToList())
        {
            if (registration.Name.StartsWith(TaskNamePrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(registration.Name, retainedName, StringComparison.OrdinalIgnoreCase))
            {
                registration.Unregister(true);
            }
        }
    }

    private static void UnregisterIndexTask()
    {
        UnregisterIndexTaskExcept(null);
    }

    private static void UnregisterIndexTaskExcept(
        IReadOnlyCollection<string>? retainedNames)
    {
        foreach (var registration in BackgroundTaskRegistration.AllTasks.Values.ToList())
        {
            if (registration.Name.StartsWith(
                    IndexTaskPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                !(retainedNames?.Contains(
                    registration.Name,
                    StringComparer.OrdinalIgnoreCase) ?? false))
            {
                registration.Unregister(true);
            }
        }
    }
}

internal enum BackgroundIndexTriggerKind
{
    Maintenance,
    UserAway,
}

internal sealed record BackgroundIndexTaskPlan(
    string Name,
    BackgroundIndexTriggerKind Trigger,
    uint FreshnessMinutes);
