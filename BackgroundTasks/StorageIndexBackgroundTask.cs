using System.Diagnostics;
using System.Runtime.InteropServices;
using StorageLens.Models;
using StorageLens.Services;
using Windows.ApplicationModel.Background;

namespace StorageLens.BackgroundTasks;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("E25BCE3D-6E15-4B68-A3D2-D72FB9D6BCE3")]
[ComSourceInterfaces(typeof(IBackgroundTask))]
public sealed partial class StorageIndexBackgroundTask : IBackgroundTask
{
    private CancellationTokenSource? _cancellation;

    [MTAThread]
    public async void Run(IBackgroundTaskInstance taskInstance)
    {
        var deferral = taskInstance.GetDeferral();
        _cancellation = new CancellationTokenSource();
        taskInstance.Canceled += (_, _) => _cancellation.Cancel();

        var completedDrives = 0;
        long indexedFiles = 0;
        var failures = new List<string>();
        try
        {
            var settings = AutomationSettingsService.Load();
            if (!settings.BackgroundIndexEnabled)
            {
                return;
            }

            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle;
            }
            catch (InvalidOperationException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await AppServices.InitializeAsync(_cancellation.Token);
            foreach (var drive in DriveDiscoveryService.GetAvailableDrives())
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var result = await AppServices.Sync.SyncDriveAsync(
                        drive,
                        cancellationToken: _cancellation.Token);
                    if (result.Status != StorageScanStatus.Completed)
                    {
                        failures.Add(
                            $"{drive.Letter}: {result.ErrorMessage ?? result.Status.ToString()}");
                        continue;
                    }

                    completedDrives++;
                    indexedFiles += result.FilesScanned;
                    var scan = await AppServices.Repository.GetLatestCompletedScanAsync(
                        drive.Letter,
                        _cancellation.Token);
                    if (scan is not null)
                    {
                        await AppServices.DuplicateDetection.ComputeHashesAsync(
                            scan.Id,
                            cancellationToken: _cancellation.Token,
                            groupBatchSize: 250,
                            maxFiles: 20_000,
                            maxBytes: 32L * 1024 * 1024 * 1024,
                            maxDuration: TimeSpan.FromMinutes(5),
                            maximumFileSize: 4L * 1024 * 1024 * 1024);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add($"{drive.Letter}: {exception.Message}");
                }
            }

            await AppServices.Repository.PruneHistoryAsync(
                completedScansPerDrive: 2,
                cancellationToken: _cancellation.Token);
            await AppServices.Repository.CheckpointAsync(_cancellation.Token);
            AutomationSettingsService.RecordBackgroundIndexResult(
                DateTimeOffset.UtcNow,
                failures.Count == 0
                    ? $"Synced {indexedFiles:N0} files across {completedDrives} drive(s)."
                    : $"Synced {completedDrives} drive(s). {string.Join(" ", failures)}");
            if (failures.Count > 0)
            {
                NotificationService.ShowGrowthAlert(
                    "Storage Lens skipped a drive",
                    string.Join(" ", failures),
                    "storagelens://automation");
            }
        }
        catch (OperationCanceledException)
        {
            AutomationSettingsService.RecordBackgroundIndexResult(
                DateTimeOffset.UtcNow,
                $"Cancelled after {completedDrives} drive(s); completed scans were retained.");
        }
        catch (Exception exception)
        {
            AutomationSettingsService.RecordBackgroundIndexResult(
                DateTimeOffset.UtcNow,
                $"Failed: {exception.Message}");
            NotificationService.ShowGrowthAlert(
                "Storage Lens idle indexing paused",
                exception.Message,
                "storagelens://automation");
        }
        finally
        {
            _cancellation.Dispose();
            deferral.Complete();
        }
    }
}
