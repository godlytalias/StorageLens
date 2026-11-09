using StorageLens.Models;
using Microsoft.Data.Sqlite;

namespace StorageLens.Services;

public sealed class StorageScanService
{
    private const int FileBatchSize = 5000;
    private static readonly TimeSpan HeartbeatInterval =
        TimeSpan.FromSeconds(5);
    private readonly StorageRepository _repository;
    private readonly DuplicateDetectionService _duplicateDetection;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public StorageScanService(StorageRepository repository)
    {
        _repository = repository;
        _duplicateDetection = new DuplicateDetectionService(repository);
    }

    public async Task<StorageScanResult> ScanDriveAsync(
        DriveSummary drive,
        IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ScanDriveCoreAsync(
            drive,
            progress,
            cancellationToken,
            rootPathOverride: null);
    }

    internal async Task<StorageScanResult> ScanTestRootAsync(
        DriveSummary drive,
        string rootPath,
        IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(rootPath) ||
            !Directory.Exists(rootPath))
        {
            throw new ArgumentException(
                "The test scan root must be an existing rooted path.",
                nameof(rootPath));
        }

        return await ScanDriveCoreAsync(
            drive,
            progress,
            cancellationToken,
            rootPath);
    }

    private async Task<StorageScanResult> ScanDriveCoreAsync(
        DriveSummary drive,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken,
        string? rootPathOverride)
    {
        await _scanLock.WaitAsync(cancellationToken);
        var started = DateTimeOffset.UtcNow;
        long filesScanned = 0;
        long foldersScanned = 0;
        long bytesScanned = 0;
        long allocatedBytesScanned = 0;
        long inaccessibleItems = 0;
        long initialItemCount = 0;
        var scanId = Guid.Empty;
        ScanExecutionLock? executionLock = null;
        StorageScanWriteSession? scanSession = null;
        var indexPublished = false;

        try
        {
            executionLock = await ScanExecutionLock.AcquireAsync(
                _repository.DatabasePath,
                cancellationToken);
            scanSession = await _repository.BeginScanAsync(
                drive,
                cancellationToken);
            var session = scanSession;
            scanId = session.ScanId;
            if (session.IsResumed &&
                await _repository.GetScanAsync(scanId, cancellationToken)
                    is { } resumedScan)
            {
                filesScanned = resumedScan.FilesScanned;
                foldersScanned = resumedScan.FoldersScanned;
                bytesScanned = resumedScan.BytesScanned;
                allocatedBytesScanned =
                    resumedScan.AllocatedBytesScanned;
                inaccessibleItems = resumedScan.InaccessibleItems;
                initialItemCount = filesScanned + foldersScanned;
            }
            var rootPath = StorageRepository.NormalizeFolderPath(
                rootPathOverride ?? $@"{drive.Letter}\");
            var journal = rootPathOverride is null
                ? UsnJournalService.TryGetInfo(rootPath)
                : null;
            var volumeSerialNumber = GetVolumeSerial(drive);
            if (journal is not null)
            {
                await session.SetJournalCheckpointAsync(
                    unchecked((long)journal.JournalId),
                    journal.NextUsn,
                    cancellationToken);
            }
            var pendingFiles = new List<StorageFileRecord>(FileBatchSize);

            async Task FlushPendingFilesAsync()
            {
                if (pendingFiles.Count == 0)
                {
                    return;
                }

                var inserted = await session.InsertFilesAsync(
                    pendingFiles,
                    cancellationToken);
                pendingFiles.Clear();
                filesScanned += inserted.Files;
                foldersScanned += inserted.Folders;
                bytesScanned += inserted.LogicalBytes;
                allocatedBytesScanned += inserted.AllocatedBytes;
            }

            var rootIdentity = FileIdentityService.TryGetIdentity(rootPath);
            var lastProgressUtc = DateTimeOffset.UtcNow;
            var lastHeartbeatUtc = lastProgressUtc;
            if (!session.IsResumed)
            {
                foldersScanned += await session.EnqueueDirectoryAsync(
                    rootPath,
                    0,
                    new ScanFolderMetadata(
                        (long)FileAttributes.Directory,
                        0,
                        DateTimeOffset.UnixEpoch,
                        rootIdentity?.FileId128 ?? string.Empty,
                        false),
                    cancellationToken);
            }

            while (true)
            {
                var workBatch = await session.ClaimDirectoriesAsync(
                    Math.Clamp(
                        Environment.ProcessorCount,
                        4,
                        32),
                    cancellationToken);
                if (workBatch.Count == 0)
                {
                    break;
                }

                var enumerationTasks = workBatch.ToDictionary(
                    item => item.PathHash,
                    item => Task.Run(() =>
                        PrepareDirectory(
                            item,
                            scanId,
                            volumeSerialNumber,
                            cancellationToken)));
                var directoriesToEnqueue = new List<ScanDirectoryEntry>();
                var incompleteDirectories = new List<string>();
                var scanIssues = new List<ScanIssue>();
                foreach (var current in workBatch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentNormalized =
                        StorageRepository.NormalizeFolderPath(
                            current.FullPath);

                    try
                    {
                        var prepared = await enumerationTasks[
                            current.PathHash];
                        foreach (var record in prepared.Files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            pendingFiles.Add(record);
                            if (pendingFiles.Count >= FileBatchSize)
                            {
                                await FlushPendingFilesAsync();
                            }

                            if (DateTimeOffset.UtcNow - lastProgressUtc >=
                                TimeSpan.FromMilliseconds(250))
                            {
                                ReportProgress(
                                    drive,
                                    currentNormalized,
                                    filesScanned,
                                    foldersScanned,
                                    bytesScanned,
                                    allocatedBytesScanned,
                                    inaccessibleItems,
                                    progress,
                                    started: started,
                                    itemCountOffset: initialItemCount);
                                await session.SaveProgressAsync(
                                    filesScanned,
                                    foldersScanned,
                                    bytesScanned,
                                    allocatedBytesScanned,
                                    inaccessibleItems,
                                    cancellationToken);
                                lastProgressUtc = DateTimeOffset.UtcNow;
                            }
                        }

                        directoriesToEnqueue.AddRange(prepared.Directories);
                        scanIssues.AddRange(prepared.Issues);
                        inaccessibleItems += prepared.InaccessibleItems;
                    }
                    catch (Exception exception)
                        when (IsInaccessibleException(exception))
                    {
                        inaccessibleItems++;
                        incompleteDirectories.Add(currentNormalized);
                        scanIssues.Add(
                            new ScanIssue
                            {
                                Path = currentNormalized,
                                Type = exception is UnauthorizedAccessException
                                    ? ScanIssueType.AccessDenied
                                    : ScanIssueType.IoError,
                                ErrorCode =
                                    exception is System.ComponentModel.Win32Exception win32
                                        ? win32.NativeErrorCode
                                        : exception.HResult,
                                Message = exception.Message,
                                DetectedUtc = DateTimeOffset.UtcNow,
                            });
                    }
                }

                if (pendingFiles.Count > 0)
                {
                    await FlushPendingFilesAsync();
                }

                foldersScanned += await session.EnqueueDirectoriesAsync(
                    directoriesToEnqueue,
                    cancellationToken);
                await session.FinalizeDirectoriesAsync(
                    workBatch,
                    incompleteDirectories,
                    scanIssues,
                    cancellationToken);
                if (DateTimeOffset.UtcNow - lastHeartbeatUtc >=
                    HeartbeatInterval)
                {
                    await session.HeartbeatAsync(cancellationToken);
                    lastHeartbeatUtc = DateTimeOffset.UtcNow;
                }
            }

            if (pendingFiles.Count > 0)
            {
                await FlushPendingFilesAsync();
            }

            ReportProgress(
                drive,
                rootPath,
                filesScanned,
                foldersScanned,
                bytesScanned,
                allocatedBytesScanned,
                inaccessibleItems,
                progress,
                started: started,
                itemCountOffset: initialItemCount);

            await session.SetPhaseAsync("Finalizing", cancellationToken);
            var finalizationProgress = progress is null
                ? null
                : new CallbackProgress<StorageScanFinalizationProgress>(
                    update =>
                    {
                        var stage = update.TotalItems > 0
                            ? $"{update.Stage} ({update.CompletedItems:N0} of {update.TotalItems:N0})"
                            : update.Stage;
                        ReportProgress(
                            drive,
                            stage,
                            filesScanned,
                            foldersScanned,
                            bytesScanned,
                            allocatedBytesScanned,
                            inaccessibleItems,
                            progress,
                            0.99,
                            started,
                            "Finalizing");
                    });
            await session.RecomputeFolderTotalsAsync(
                cancellationToken,
                finalizationProgress);
            await session
                .MaterializePreviousWindowsInstallationCandidatesAsync(
                    cancellationToken,
                    finalizationProgress);
            var scanTotals = await session.GetScanTotalsAsync(cancellationToken);
            filesScanned = scanTotals.Files;
            foldersScanned = scanTotals.Folders;
            bytesScanned = scanTotals.LogicalBytes;
            allocatedBytesScanned = scanTotals.AllocatedBytes;
            ReportProgress(
                drive,
                "Finalizing resumable work queue",
                filesScanned,
                foldersScanned,
                bytesScanned,
                allocatedBytesScanned,
                inaccessibleItems,
                progress,
                0.99,
                started,
                "Finalizing");
            await session.SetPhaseAsync("Completing", cancellationToken);
            await session.ClearCompletedWorkItemsAsync(
                cancellationToken,
                finalizationProgress);
            await session.CompleteAsync(
                filesScanned,
                foldersScanned,
                bytesScanned,
                inaccessibleItems,
                cancellationToken,
                allocatedBytesScanned);
            indexPublished = true;
            ReportProgress(
                drive,
                rootPath,
                filesScanned,
                foldersScanned,
                bytesScanned,
                allocatedBytesScanned,
                inaccessibleItems,
                progress,
                1,
                started);
            var warning = rootPathOverride is null
                ? null
                : await RunPostScanMaintenanceAsync(
                    scanId,
                    cancellationToken);

            return new StorageScanResult
            {
                ScanId = scanId,
                DriveLetter = drive.Letter,
                Status = StorageScanStatus.Completed,
                FilesScanned = filesScanned,
                FoldersScanned = foldersScanned,
                BytesScanned = bytesScanned,
                AllocatedBytesScanned = allocatedBytesScanned,
                InaccessibleItems = inaccessibleItems,
                Duration = DateTimeOffset.UtcNow - started,
                WarningMessage = warning,
            };
        }
        catch (OperationCanceledException)
        {
            if (indexPublished)
            {
                return new StorageScanResult
                {
                    ScanId = scanId,
                    DriveLetter = drive.Letter,
                    Status = StorageScanStatus.Completed,
                    FilesScanned = filesScanned,
                    FoldersScanned = foldersScanned,
                    BytesScanned = bytesScanned,
                    AllocatedBytesScanned = allocatedBytesScanned,
                    InaccessibleItems = inaccessibleItems,
                    Duration = DateTimeOffset.UtcNow - started,
                    WarningMessage =
                        "The index is complete; remaining optimization was cancelled.",
                };
            }

            if (scanId != Guid.Empty)
            {
                await MarkScanFailedAsync(
                    scanId,
                    StorageScanStatus.Cancelled,
                    null,
                    inaccessibleItems);
            }

            return new StorageScanResult
            {
                ScanId = scanId,
                DriveLetter = drive.Letter,
                Status = StorageScanStatus.Cancelled,
                FilesScanned = filesScanned,
                FoldersScanned = foldersScanned,
                BytesScanned = bytesScanned,
                AllocatedBytesScanned = allocatedBytesScanned,
                InaccessibleItems = inaccessibleItems,
                Duration = DateTimeOffset.UtcNow - started,
            };
        }
        catch (Exception exception)
        {
            if (scanId != Guid.Empty)
            {
                await MarkScanFailedAsync(
                    scanId,
                    StorageScanStatus.Failed,
                    exception.Message,
                    inaccessibleItems);
            }

            return new StorageScanResult
            {
                ScanId = scanId,
                DriveLetter = drive.Letter,
                Status = StorageScanStatus.Failed,
                FilesScanned = filesScanned,
                FoldersScanned = foldersScanned,
                BytesScanned = bytesScanned,
                AllocatedBytesScanned = allocatedBytesScanned,
                InaccessibleItems = inaccessibleItems,
                Duration = DateTimeOffset.UtcNow - started,
                ErrorMessage = exception.Message,
            };
        }
        finally
        {
            try
            {
                if (scanSession is not null)
                {
                    await scanSession.DisposeAsync();
                }
            }
            finally
            {
                executionLock?.Dispose();
                _scanLock.Release();
            }
        }
    }

    public async Task<string?> RunPostScanMaintenanceAsync(
        Guid scanId,
        CancellationToken cancellationToken = default)
    {
        string? warning = null;
        try
        {
            await _duplicateDetection.ComputeHashesAsync(
                scanId,
                cancellationToken: cancellationToken,
                groupBatchSize: 100,
                maxFiles: 250,
                maxBytes: 2L * 1024 * 1024 * 1024,
                maxDuration: TimeSpan.FromSeconds(8),
                maximumFileSize: 256L * 1024 * 1024);
            using var historyBudget =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            historyBudget.CancelAfter(TimeSpan.FromSeconds(5));
            await _repository.PruneHistoryAsync(
                completedScansPerDrive: 2,
                cancellationToken: historyBudget.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            warning = AppendWarning(
                warning,
                "The index is complete; the remaining duplicate analysis will resume during idle indexing.");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            warning = AppendWarning(
                warning,
                $"The index is complete, but duplicate analysis or history pruning did not finish: {exception.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return warning;
    }

    private async Task MarkScanFailedAsync(
        Guid scanId,
        StorageScanStatus status,
        string? error,
        long inaccessible)
    {
        await using var connection = await _repository.OpenConnectionAsync(CancellationToken.None);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ScanRuns
            SET Status = $status,
                CompletedUtc = $completed,
                FilesScanned = (
                    SELECT COUNT(*)
                    FROM FileNames
                    WHERE FileNames.RunKey = ScanRuns.RunKey),
                FoldersScanned = (
                    SELECT COUNT(*)
                    FROM FolderNodes
                    WHERE FolderNodes.RunKey = ScanRuns.RunKey),
                BytesScanned = COALESCE((
                    SELECT SUM(LogicalSize)
                    FROM PhysicalFiles
                    WHERE PhysicalFiles.RunKey = ScanRuns.RunKey), 0),
                AllocatedBytesScanned = COALESCE((
                    SELECT SUM(AllocatedSize)
                    FROM PhysicalFiles
                    WHERE PhysicalFiles.RunKey = ScanRuns.RunKey), 0),
                InaccessibleItems = $inaccessible,
                ErrorMessage = $error,
                OwnerProcessId = NULL,
                OwnerProcessStartedUtc = NULL,
                LeaseHeartbeatUtc = NULL,
                ScanPhase = $phase,
                PhaseStartedUtc = $completed
            WHERE Id = $scanId
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$inaccessible", inaccessible);
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$phase", status.ToString());
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
        var owner = ScanOwner.Current;
        command.Parameters.AddWithValue("$pid", owner.ProcessId);
        command.Parameters.AddWithValue("$started", owner.ProcessStartedUtc);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static string AppendWarning(
        string? existing,
        string addition) =>
        string.IsNullOrWhiteSpace(existing)
            ? addition
            : existing + " " + addition;

    private static void ReportProgress(
        DriveSummary drive,
        string path,
        long files,
        long folders,
        long logicalBytes,
        long allocatedBytes,
        long inaccessible,
        IProgress<StorageScanProgress>? progress,
        double? overridePercentage = null,
        DateTimeOffset? started = null,
        string? phase = null,
        long itemCountOffset = 0)
    {
        var percent = overridePercentage ??
                      Math.Clamp(
                          (double)allocatedBytes / Math.Max(1, drive.UsedBytes),
                          0,
                          0.97);
        var elapsed = started.HasValue
            ? DateTimeOffset.UtcNow - started.Value
            : TimeSpan.Zero;
        var effectivePhase = phase ?? (overridePercentage.HasValue &&
                                       overridePercentage.Value >= 1
            ? "Complete"
            : overridePercentage.HasValue
                ? "Finalizing"
                : "Enumerating");
        progress?.Report(new StorageScanProgress
        {
            DriveLetter = drive.Letter,
            CurrentPath = path,
            FilesScanned = files,
            FoldersScanned = folders,
            BytesScanned = logicalBytes,
            AllocatedBytesScanned = allocatedBytes,
            InaccessibleItems = inaccessible,
            PercentComplete = percent,
            Phase = effectivePhase,
            Elapsed = elapsed,
            ItemsPerSecond = effectivePhase == "Enumerating" &&
                             elapsed.TotalSeconds > 0
                ? Math.Max(0, files + folders - itemCountOffset) /
                  elapsed.TotalSeconds
                : 0,
        });
    }

    private static bool IsInaccessibleException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
            or DirectoryNotFoundException
            or System.ComponentModel.Win32Exception;

    private PreparedDirectory PrepareDirectory(
        ScanWorkItem workItem,
        Guid scanId,
        long volumeSerialNumber,
        CancellationToken cancellationToken)
    {
        var currentPath = StorageRepository.NormalizeFolderPath(
            workItem.FullPath);
        var files = new List<StorageFileRecord>();
        var directories = new List<ScanDirectoryEntry>();
        var issues = new List<ScanIssue>();
        var inaccessibleItems = 0;
        foreach (var entry in EnumerateDirectory(currentPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.Combine(currentPath, entry.Name);
            if (ShouldSkipPath(filePath))
            {
                continue;
            }

            var attributes = (FileAttributes)entry.Attributes;
            var isDirectory =
                (attributes & FileAttributes.Directory) != 0;
            var reparseAction = StorageReparsePolicy.GetAction(
                isDirectory,
                entry.ReparsePointTag);
            if (isDirectory)
            {
                var normalizedChild =
                    StorageRepository.NormalizeFolderPath(filePath);
                directories.Add(
                    new ScanDirectoryEntry(
                        normalizedChild,
                        workItem.Depth + 1,
                        new ScanFolderMetadata(
                            entry.Attributes,
                            entry.ReparsePointTag,
                            entry.ModifiedUtc,
                            entry.FileId,
                            reparseAction != ReparseAction.Traverse),
                        reparseAction == ReparseAction.Traverse));
                if (reparseAction != ReparseAction.Traverse)
                {
                    issues.Add(
                        new ScanIssue
                        {
                            Path = normalizedChild,
                            Type = entry.ReparsePointTag ==
                                   StorageReparseTags.ProjectedFileSystem
                                ? ScanIssueType.VirtualizedRoot
                                : ScanIssueType.ReparseExcluded,
                            ReparseTag = entry.ReparsePointTag,
                            Message =
                                $"Directory traversal was limited for reparse tag 0x{entry.ReparsePointTag:X8}.",
                            DetectedUtc = DateTimeOffset.UtcNow,
                        });
                    inaccessibleItems++;
                }

                continue;
            }

            var extension = Path.GetExtension(entry.Name);
            var category = StorageCategoryClassifier.ClassifyPath(
                filePath,
                extension,
                attributes);
            files.Add(
                new StorageFileRecord
                {
                    ScanId = scanId,
                    Name = entry.Name,
                    FullPath = filePath,
                    ParentPath = currentPath,
                    SizeBytes = entry.LogicalSize,
                    AllocatedSizeBytes = entry.AllocatedSize,
                    PhysicalSizeBytes = entry.AllocatedSize,
                    CreatedUtc = entry.CreatedUtc,
                    ModifiedUtc = entry.ModifiedUtc,
                    AccessedUtc = entry.AccessedUtc,
                    ChangeTime = entry.ChangeTime,
                    Attributes = (long)attributes,
                    Extension = extension,
                    Category = category,
                    Tag = StorageCategoryClassifier.TagPath(
                        filePath,
                        category),
                    VolumeSerialNumber = volumeSerialNumber,
                    FileId128 = entry.FileId,
                    ReparseTag = entry.ReparsePointTag,
                    IsHardLinkAlias = false,
                });
        }

        return new PreparedDirectory(
            files,
            directories,
            issues,
            inaccessibleItems);
    }

    private static IReadOnlyList<NativeDirectoryEntry> EnumerateDirectory(
        string directoryPath)
    {
        try
        {
            return NativeDirectoryReader.Enumerate(directoryPath);
        }
        catch (System.ComponentModel.Win32Exception exception)
            when (exception.NativeErrorCode is 1 or 50 or 87)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
                MatchType = MatchType.Win32,
            };
            var entries = new List<NativeDirectoryEntry>();
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directoryPath,
                         "*",
                         options))
            {
                var info = new FileInfo(path);
                var attributes = info.Attributes;
                var identity = FileIdentityService.TryGetIdentity(path);
                var fileId = identity?.FileId128 ?? string.Empty;
                entries.Add(new NativeDirectoryEntry(
                    info.Name,
                    (attributes & FileAttributes.Directory) != 0
                        ? 0
                        : info.Length,
                    (attributes & FileAttributes.Directory) != 0
                        ? 0
                        : info.Length,
                    (uint)attributes,
                    0,
                    fileId,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc,
                    info.LastAccessTimeUtc,
                    info.LastWriteTimeUtc.ToFileTime()));
            }

            return entries;
        }
    }

    private bool ShouldSkipPath(string path)
    {
        if (StorageCategoryClassifier.IsInside(path, Path.GetDirectoryName(_repository.DatabasePath)!))
        {
            return true;
        }

        var name = Path.GetFileName(path.TrimEnd('\\'));
        return name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    private static long ParseVolumeSerial(string volumeId) =>
        ulong.TryParse(
            volumeId,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? unchecked((long)value)
            : 0;

    private static long GetVolumeSerial(DriveSummary drive)
    {
        var volumeId = string.IsNullOrWhiteSpace(drive.VolumeId)
            ? VolumeIdentityService.TryGetVolumeSerial(drive.Letter)
            : drive.VolumeId;
        return ParseVolumeSerial(volumeId ?? string.Empty);
    }

    private sealed class CallbackProgress<T>(
        Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record PreparedDirectory(
        IReadOnlyList<StorageFileRecord> Files,
        IReadOnlyList<ScanDirectoryEntry> Directories,
        IReadOnlyList<ScanIssue> Issues,
        int InaccessibleItems);
}
