using Microsoft.Windows.ApplicationModel.Background;
using StorageLens.BackgroundTasks;
using StorageLens.Models;
using Windows.ApplicationModel.Background;

namespace StorageLens.Services;

public static class BackgroundTaskRegistrationService
{
    private const string TaskNamePrefix = "StorageLensDailyStorageMonitor";
    private const string IndexTaskPrefix = "StorageLensIdleDriveIndex";
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
            var intervalMinutes = intervalDays * 24 * 60;
            var indexTaskName = $"{IndexTaskPrefix}-v3-{intervalMinutes}";
            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access is BackgroundAccessStatus.DeniedBySystemPolicy or
                BackgroundAccessStatus.DeniedByUser)
            {
                return "Windows blocked daily idle indexing.";
            }

            if (!BackgroundTaskRegistration.AllTasks.Values.Any(registration =>
                    string.Equals(
                        registration.Name,
                        indexTaskName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                var builder = new Microsoft.Windows.ApplicationModel.Background.BackgroundTaskBuilder
                {
                    Name = indexTaskName,
                };
                builder.SetTaskEntryPointClsid(typeof(StorageIndexBackgroundTask).GUID);
                builder.SetTrigger(
                    new MaintenanceTrigger(
                        (uint)intervalMinutes,
                        false));
                builder.AddCondition(
                    new SystemCondition(SystemConditionType.UserNotPresent));
                builder.Register();
            }

            UnregisterIndexTaskExcept(indexTaskName);
            return
                $"Drive indexing is registered approximately every {intervalDays} day" +
                $"{(intervalDays == 1 ? string.Empty : "s")} during Windows maintenance periods.";
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

    private static void UnregisterIndexTaskExcept(string? retainedName)
    {
        foreach (var registration in BackgroundTaskRegistration.AllTasks.Values.ToList())
        {
            if (registration.Name.StartsWith(
                    IndexTaskPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    registration.Name,
                    retainedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                registration.Unregister(true);
            }
        }
    }
}
