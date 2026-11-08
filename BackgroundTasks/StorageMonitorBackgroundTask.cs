using System.Runtime.InteropServices;
using StorageLens.Models;
using StorageLens.Services;
using Windows.ApplicationModel.Background;

namespace StorageLens.BackgroundTasks;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("7A8BBBE6-8C7A-4CDA-9DF8-D0A6A9F68B4E")]
[ComSourceInterfaces(typeof(IBackgroundTask))]
public sealed partial class StorageMonitorBackgroundTask : IBackgroundTask
{
    private CancellationTokenSource? _cancellation;

    [MTAThread]
    public async void Run(IBackgroundTaskInstance taskInstance)
    {
        var deferral = taskInstance.GetDeferral();
        _cancellation = new CancellationTokenSource();
        taskInstance.Canceled += (_, _) => _cancellation.Cancel();

        try
        {
            var settings = AutomationSettingsService.Load();
            if (settings.MonitorEnabled)
            {
                await CheckDriveSpaceAsync(settings, _cancellation.Token);
            }

            await CheckScheduledReviewsAsync(settings, _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            NotificationService.ShowGrowthAlert(
                "Storage Lens monitor paused",
                $"The background check could not finish: {exception.Message}",
                "storagelens://automation");
        }
        finally
        {
            _cancellation.Dispose();
            deferral.Complete();
        }
    }

    private static async Task CheckDriveSpaceAsync(AutomationSettings settings, CancellationToken cancellationToken)
    {
        var previous = await StorageMonitorStateService.LoadAsync();
        var snapshot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var thresholdBytes = settings.LowSpaceThresholdGb * 1024L * 1024 * 1024;
        var growthThresholdBytes = settings.GrowthThresholdGb * 1024L * 1024 * 1024;

        foreach (var drive in DriveInfo.GetDrives().Where(item => item.IsReady && item.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = drive.Name.TrimEnd('\\');
            var freeBytes = drive.AvailableFreeSpace;
            snapshot[key] = freeBytes;

            if (settings.NotifyOnLowSpace && freeBytes <= thresholdBytes)
            {
                NotificationService.ShowGrowthAlert(
                    "Storage space is running low",
                    $"{drive.Name} has {StorageFormatter.FormatBytes(freeBytes)} free. Open Storage Lens to review the folders consuming the most space.",
                    $"storagelens://growth?drive={Uri.EscapeDataString(key)}");
                continue;
            }

            if (settings.NotifyOnFolderGrowth &&
                previous.TryGetValue(key, out var previousFree) &&
                previousFree - freeBytes >= growthThresholdBytes)
            {
                NotificationService.ShowGrowthAlert(
                    $"{drive.Name} is growing quickly",
                    $"Available space decreased by {StorageFormatter.FormatBytes(previousFree - freeBytes)} since the last check.",
                    $"storagelens://growth?drive={Uri.EscapeDataString(key)}");
            }
        }

        await CheckFolderGrowthAsync(settings, previous, snapshot, cancellationToken);
        await StorageMonitorStateService.SaveAsync(snapshot);
    }

    private static async Task CheckFolderGrowthAsync(
        AutomationSettings settings,
        IReadOnlyDictionary<string, long> previous,
        IDictionary<string, long> snapshot,
        CancellationToken cancellationToken)
    {
        var minimumTrackedSizeBytes = Math.Max(1, settings.MinimumTrackedFolderSizeGb) * 1024L * 1024 * 1024;
        var growthThresholdBytes = settings.GrowthThresholdGb * 1024L * 1024 * 1024;
        await AppServices.InitializeAsync(cancellationToken);
        var growth = new List<(string Path, long AddedBytes, long CurrentBytes)>();

        foreach (var drive in DriveInfo.GetDrives().Where(item =>
                     item.IsReady &&
                     item.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var insights = await AppServices.Repository.GetGrowingFoldersAsync(
                drive.Name.TrimEnd('\\'),
                minimumTrackedSizeBytes,
                40,
                cancellationToken);
            foreach (var insight in insights)
            {
                var snapshotKey = $"folder:{insight.Path}";
                snapshot[snapshotKey] = insight.SizeBytes;
                if (previous.TryGetValue(
                        snapshotKey,
                        out var previousSize) &&
                    insight.SizeBytes - previousSize >= growthThresholdBytes)
                {
                    growth.Add((
                        insight.Path,
                        insight.SizeBytes - previousSize,
                        insight.SizeBytes));
                }
            }
        }

        if (settings.NotifyOnFolderGrowth)
        {
            foreach (var alert in growth.OrderByDescending(item => item.AddedBytes).Take(2))
            {
                NotificationService.ShowGrowthAlert(
                    $"{Path.GetFileName(alert.Path.TrimEnd(Path.DirectorySeparatorChar))} is growing quickly",
                    $"{alert.Path} added {StorageFormatter.FormatBytes(alert.AddedBytes)} and is now {StorageFormatter.FormatBytes(alert.CurrentBytes)}.",
                    $"storagelens://growth?folder={Uri.EscapeDataString(alert.Path)}");
            }
        }
    }

    private static async Task CheckScheduledReviewsAsync(
        AutomationSettings settings,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        foreach (var staleSchedule in settings.CleanupTasks.Where(task =>
                     task.IsEnabled && task.LastRun.AddDays(Math.Max(1, task.IntervalDays)) <= now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AutomationSettingsService.TryClaimScheduleRun(
                    staleSchedule.Id,
                    now,
                    out var schedule,
                    out var previousLastRun) ||
                schedule is null)
            {
                continue;
            }

            try
            {
                if (schedule.Mode == CleanupMode.RecycleSafeItems)
                {
                    var result = await RunAutomaticCleanupAsync(schedule, cancellationToken);
                    if (result.FailedCount > 0)
                    {
                        AutomationSettingsService.RestoreScheduleRun(
                            schedule.Id,
                            now,
                            previousLastRun);
                    }

                    NotificationService.ShowGrowthAlert(
                        $"{schedule.Name} completed",
                        $"Moved {result.SucceededCount} safe items ({StorageFormatter.FormatBytes(result.BytesRecycled)}) to the Recycle Bin. {result.FailedCount} failed and {result.SkippedCount} were skipped.",
                        result.FailedCount + result.SkippedCount > 0
                            ? "storagelens://cleanup"
                            : "storagelens://automation");
                }
                else
                {
                    NotificationService.ShowGrowthAlert(
                        $"{schedule.Name} is ready",
                        $"Review {string.Join(", ", schedule.Categories)} before cleanup. Storage Lens did not delete any files.",
                        BuildCleanupActivation(schedule.Categories));
                }
            }
            catch
            {
                AutomationSettingsService.RestoreScheduleRun(
                    schedule.Id,
                    now,
                    previousLastRun);
                throw;
            }
        }

        await Task.CompletedTask;
    }

    private static async Task<CleanupResult> RunAutomaticCleanupAsync(
        ScheduledCleanupTaskSnapshot schedule,
        CancellationToken cancellationToken)
    {
        const int maximumCandidatesPerRun = 5_000;
        await AppServices.InitializeAsync(cancellationToken);
        var candidates = new List<CleanupCandidate>();
        var policies = schedule.Categories
            .Select(CleanupPolicyService.ParsePolicy)
            .Where(policy => policy.HasValue)
            .Select(policy => policy!.Value)
            .Where(policy => CleanupPolicyService.GetDefinition(policy).AutomaticEligible)
            .Distinct()
            .ToList();

        foreach (var drive in DriveDiscoveryService.GetAvailableDrives())
        {
            foreach (var policy in policies)
            {
                var offset = 0;
                const int pageSize = 1000;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (candidates.Count >= maximumCandidatesPerRun)
                    {
                        break;
                    }

                    var page = await AppServices.CleanupCandidates.GetCandidatesAsync(
                        drive,
                        policy,
                        pageSize,
                        offset,
                        cancellationToken);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    candidates.AddRange(
                        page.Where(item =>
                                item.IsEligibleForAutomaticCleanup)
                            .Take(maximumCandidatesPerRun - candidates.Count));
                    offset += page.Count;
                    if (page.Count < pageSize)
                    {
                        break;
                    }
                }

                if (candidates.Count >= maximumCandidatesPerRun)
                {
                    break;
                }
            }

            if (candidates.Count >= maximumCandidatesPerRun)
            {
                break;
            }
        }

        return await AppServices.Cleanup.RecycleAsync(
            new CleanupRequest
            {
                Candidates = candidates,
                IsAutomatic = true,
            },
            cancellationToken: cancellationToken);
    }

    private static string BuildCleanupActivation(IEnumerable<string> categories)
    {
        var policies = categories
            .Select(CleanupPolicyService.ParsePolicy)
            .Where(policy => policy.HasValue)
            .Select(policy => (int)policy!.Value);
        return $"storagelens://cleanup?policies={string.Join(',', policies)}";
    }
}
