using StorageLens.Models;

namespace StorageLens.Services;

public sealed class StorageSyncService
{
    private readonly StorageRepository _repository;
    private readonly StorageScanService _scanner;

    public StorageSyncService(
        StorageRepository repository,
        StorageScanService scanner)
    {
        _repository = repository;
        _scanner = scanner;
    }

    public async Task<StorageScanResult> SyncDriveAsync(
        DriveSummary drive,
        IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var latest = await _repository.GetLatestCompletedScanAsync(
            drive.Letter,
            cancellationToken);
        if (latest is null || latest.ScanFormat < 4)
        {
            return await _scanner.ScanDriveAsync(
                drive,
                progress,
                cancellationToken);
        }

        var checkpoint = await _repository.GetJournalCheckpointAsync(
            latest.Id,
            cancellationToken);
        if (checkpoint is null)
        {
            return await _scanner.ScanDriveAsync(
                drive,
                progress,
                cancellationToken);
        }

        ScanExecutionLock? executionLock = null;
        try
        {
            executionLock = await ScanExecutionLock.AcquireAsync(
                _repository.DatabasePath,
                cancellationToken);
            var journal = UsnJournalService.TryReadChanges(
                drive.Letter + "\\",
                checkpoint.JournalId,
                checkpoint.NextUsn,
                cancellationToken);
            if (!journal.Success || journal.JournalId == 0)
            {
                executionLock.Dispose();
                executionLock = null;
                return await _scanner.ScanDriveAsync(
                    drive,
                    progress,
                    cancellationToken);
            }

            var started = DateTimeOffset.UtcNow;
            if (journal.Directories.Count == 0)
            {
                await _repository.SaveJournalCheckpointAsync(
                    latest.Id,
                    unchecked((long)journal.JournalId),
                    journal.NextUsn,
                    cancellationToken);
                return CompletedResult(
                    latest,
                    started,
                    "No indexed directories changed.");
            }

            var refreshed = 0;
            var paths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var directory in journal.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = await _repository.GetFolderPathByFileIdAsync(
                    checkpoint.RunKey,
                    directory.ParentReference,
                    cancellationToken);
                if (path is null)
                {
                    executionLock.Dispose();
                    executionLock = null;
                    return await _scanner.ScanDriveAsync(
                        drive,
                        progress,
                        cancellationToken);
                }

                paths.Add(path);
            }

            var orderedPaths = paths
                .OrderBy(path => path.Count(character => character == '\\'))
                .ToList();
            foreach (var path in orderedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new StorageScanProgress
                {
                    DriveLetter = drive.Letter,
                    CurrentPath = $"Syncing {path}",
                    FilesScanned = latest.FilesScanned,
                    FoldersScanned = latest.FoldersScanned,
                    BytesScanned = latest.BytesScanned,
                    AllocatedBytesScanned = latest.AllocatedBytesScanned,
                    InaccessibleItems = latest.InaccessibleItems,
                    PercentComplete = paths.Count == 0
                        ? 1
                        : (double)refreshed / paths.Count,
                });
                IReadOnlyList<NativeDirectoryEntry> entries;
                try
                {
                    entries = NativeDirectoryReader.Enumerate(path);
                }
                catch (Exception)
                {
                    executionLock.Dispose();
                    executionLock = null;
                    return await _scanner.ScanDriveAsync(
                        drive,
                        progress,
                        cancellationToken);
                }

                var outcome = await _repository.RefreshDirectorySnapshotAsync(
                    checkpoint.RunKey,
                    path,
                    entries,
                    cancellationToken);
                if (!outcome.Applied)
                {
                    executionLock.Dispose();
                    executionLock = null;
                    return await _scanner.ScanDriveAsync(
                        drive,
                        progress,
                        cancellationToken);
                }

                refreshed++;
            }

            var totals = await _repository.FinalizeIncrementalSyncAsync(
                checkpoint.RunKey,
                cancellationToken);
            await _repository.SaveJournalCheckpointAsync(
                latest.Id,
                unchecked((long)journal.JournalId),
                journal.NextUsn,
                cancellationToken);
            await _repository.CheckpointAsync(cancellationToken);
            progress?.Report(new StorageScanProgress
            {
                DriveLetter = drive.Letter,
                CurrentPath = $"Synced {refreshed} changed directories",
                FilesScanned = totals.Files,
                FoldersScanned = totals.Folders,
                BytesScanned = totals.LogicalBytes,
                AllocatedBytesScanned = totals.AllocatedBytes,
                InaccessibleItems = latest.InaccessibleItems,
                PercentComplete = 1,
            });
            return CompletedResult(
                latest,
                started,
                $"Refreshed {refreshed} changed directories.",
                totals);
        }
        finally
        {
            executionLock?.Dispose();
        }
    }

    private static StorageScanResult CompletedResult(
        StorageScanRun latest,
        DateTimeOffset started,
        string? warning,
        ScanTotals? totals = null) =>
        new()
        {
            ScanId = latest.Id,
            DriveLetter = latest.DriveLetter,
            Status = StorageScanStatus.Completed,
            FilesScanned = totals?.Files ?? latest.FilesScanned,
            FoldersScanned = totals?.Folders ?? latest.FoldersScanned,
            BytesScanned = totals?.LogicalBytes ?? latest.BytesScanned,
            AllocatedBytesScanned =
                totals?.AllocatedBytes ?? latest.AllocatedBytesScanned,
            InaccessibleItems = latest.InaccessibleItems,
            Duration = DateTimeOffset.UtcNow - started,
            WarningMessage = warning,
        };
}
