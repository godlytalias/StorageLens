using StorageLens.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StorageLens.Services;

public sealed class CleanupService
{
    private readonly StorageRepository _repository;
    private readonly Action<string>? _recycle;
    private readonly Func<
        IReadOnlyList<string>,
        bool,
        nint,
        IReadOnlyList<RecycleBinItemResult>>? _recycleBatch;

    public CleanupService(StorageRepository repository, Action<string>? recycle = null)
    {
        _repository = repository;
        _recycle = recycle;
        _recycleBatch = recycle is null
            ? RecycleBinService.RecycleBatch
            : null;
    }

    internal CleanupService(
        StorageRepository repository,
        Func<
            IReadOnlyList<string>,
            bool,
            nint,
            IReadOnlyList<RecycleBinItemResult>> recycleBatch)
    {
        _repository = repository;
        _recycleBatch = recycleBatch;
    }

    internal CleanupService(
        StorageRepository repository,
        Func<
            IReadOnlyList<string>,
            bool,
            IReadOnlyList<RecycleBinItemResult>> recycleBatch)
    {
        _repository = repository;
        _recycleBatch = (paths, showProgressUi, _) =>
            recycleBatch(paths, showProgressUi);
    }

    public Task<CleanupResult> RecycleAsync(
        CleanupRequest request,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            async () =>
            {
                await using var worker = new CleanupExecutionWorker();
                return await RecycleCoreAsync(
                    request,
                    worker,
                    progress,
                    cancellationToken);
            },
            cancellationToken);

    internal Task<CleanupResult> RecycleAsync(
        CleanupRequest request,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken,
        CleanupExecutionWorker worker) =>
        Task.Run(
            () => RecycleCoreAsync(
                request,
                worker,
                progress,
                cancellationToken),
            cancellationToken);

    private async Task<CleanupResult> RecycleCoreAsync(
        CleanupRequest request,
        CleanupExecutionWorker worker,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var remainingIndexed = 0;
        long bytesRecycled = 0;
        var failures = new List<CleanupFailure>();
        var now = DateTimeOffset.Now;
        var prepared = new List<PreparedCleanup>();
        var processed = 0;
        progress?.Report(new CleanupProgress
        {
            Processed = 0,
            Total = request.Candidates.Count,
        });
        try
        {
            for (var index = 0; index < request.Candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = request.Candidates[index];
                progress?.Report(new CleanupProgress
                {
                    Processed = processed,
                    Total = request.Candidates.Count,
                    BytesRecycled = bytesRecycled,
                    CurrentPath = candidate.Item.Path,
                });
                try
                {
                    var record = await GetCurrentRecordAsync(
                        candidate,
                        cancellationToken);
                    if (record is null)
                    {
                        skipped++;
                        await _repository.RecordCleanupActionAsync(
                            candidate.ScanId,
                            candidate.PolicyId,
                            candidate.Item.Path,
                            candidate.Item.SizeBytes,
                            request.IsAutomatic,
                            "Skipped",
                            "The file no longer exists or changed after the scan.",
                            cancellationToken);
                        try
                        {
                            await _repository.RemoveIndexedFileAsync(
                                candidate.ScanId,
                                candidate.Item.Path,
                                CancellationToken.None);
                        }
                        catch (Exception indexException)
                        {
                            remainingIndexed++;
                            failures.Add(new CleanupFailure
                            {
                                Path = candidate.Item.Path,
                                Error =
                                    $"The stale candidate was skipped, but its index entry could not be removed: {indexException.Message}",
                            });
                        }

                        ReportProcessed(candidate);
                        continue;
                    }

                    var policy = CleanupPolicyService.Evaluate(
                        record,
                        candidate.PolicyId,
                        now,
                        request.IsAutomatic);
                    if (!policy.IsAllowed)
                    {
                        remainingIndexed++;
                        skipped++;
                        await _repository.RecordCleanupActionAsync(
                            candidate.ScanId,
                            candidate.PolicyId,
                            candidate.Item.Path,
                            candidate.Item.SizeBytes,
                            request.IsAutomatic,
                            "Skipped",
                            policy.Reason,
                            cancellationToken);
                        ReportProcessed(candidate);
                        continue;
                    }

                    if (candidate.PolicyId == CleanupPolicyId.LargeDuplicates &&
                        !await HasValidRetainedCopyAsync(
                            candidate,
                            cancellationToken))
                    {
                        skipped++;
                        remainingIndexed++;
                        await _repository.RecordCleanupActionAsync(
                            candidate.ScanId,
                            candidate.PolicyId,
                            candidate.Item.Path,
                            candidate.Item.SizeBytes,
                            request.IsAutomatic,
                            "Skipped",
                            "No distinct retained copy passed content and identity validation.",
                            cancellationToken);
                        ReportProcessed(candidate);
                        continue;
                    }

                    var pendingActionId =
                        await _repository.BeginCleanupActionAsync(
                            candidate.ScanId,
                            candidate.PolicyId,
                            candidate.Item.Path,
                            candidate.Item.SizeBytes,
                            request.IsAutomatic,
                            cancellationToken);
                    prepared.Add(new PreparedCleanup(
                        candidate,
                        pendingActionId));
                }
                catch (Exception exception) when (
                    IsRecoverableCleanupException(exception))
                {
                    failed++;
                    remainingIndexed++;
                    failures.Add(new CleanupFailure
                    {
                        Path = candidate.Item.Path,
                        Error = exception.Message,
                    });
                    try
                    {
                        await _repository.RecordCleanupActionAsync(
                            candidate.ScanId,
                            candidate.PolicyId,
                            candidate.Item.Path,
                            candidate.Item.SizeBytes,
                            request.IsAutomatic,
                            "Failed",
                            exception.Message,
                            CancellationToken.None);
                    }
                    catch (Exception auditException)
                    {
                        failures.Add(new CleanupFailure
                        {
                            Path = candidate.Item.Path,
                            Error =
                                $"The cleanup failed and its audit record could not be saved: {auditException.Message}",
                        });
                    }

                    ReportProcessed(candidate);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await CancelPreparedAsync(
                prepared,
                "Cleanup was cancelled before the Shell operation started.");
            throw;
        }
        catch
        {
            await CancelPreparedAsync(
                prepared,
                "Cleanup preparation failed before the Shell operation started.");
            throw;
        }

        if (_recycleBatch is not null)
        {
            RecycleBinItemResult[] batchResults;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchResults = (await worker.RunAsync(() =>
                    _recycleBatch(
                        prepared.Select(item =>
                                item.Candidate.Item.Path)
                            .ToArray(),
                        !request.IsAutomatic,
                        request.OwnerWindowHandle)))
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                await CancelPreparedAsync(
                    prepared,
                    "Cleanup was cancelled before the Shell operation started.");
                throw;
            }
            catch (Exception exception) when (
                IsRecoverableCleanupException(exception))
            {
                batchResults = prepared
                    .Select(item => new RecycleBinItemResult
                    {
                        Path = item.Candidate.Item.Path,
                        HResult = exception.HResult,
                        Error = exception.Message,
                    })
                    .ToArray();
            }
            catch
            {
                await CancelPreparedAsync(
                    prepared,
                    "The Shell operation could not be started.");
                throw;
            }

            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                var result = index < batchResults.Length &&
                             string.Equals(
                                 batchResults[index].Path,
                                 item.Candidate.Item.Path,
                                 StringComparison.OrdinalIgnoreCase)
                    ? batchResults[index]
                    : new RecycleBinItemResult
                    {
                        Path = item.Candidate.Item.Path,
                        HResult = unchecked((int)0x80004005),
                        Error =
                            "Windows did not report a result for this item.",
                    };
                await FinalizePreparedAsync(item, result);
                ReportProcessed(item.Candidate);
            }
        }
        else
        {
            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await worker.RunAsync(() =>
                        _recycle!(item.Candidate.Item.Path));
                    await FinalizePreparedAsync(
                        item,
                        new RecycleBinItemResult
                        {
                            Path = item.Candidate.Item.Path,
                            Succeeded = true,
                        });
                }
                catch (OperationCanceledException)
                {
                    await CancelPreparedAsync(
                        prepared.Skip(index),
                        "Cleanup was cancelled.");
                    throw;
                }
                catch (Exception exception) when (
                    IsRecoverableCleanupException(exception))
                {
                    await FinalizePreparedAsync(
                        item,
                        new RecycleBinItemResult
                        {
                            Path = item.Candidate.Item.Path,
                            HResult = exception.HResult,
                            Error = exception.Message,
                        });
                }

                ReportProcessed(item.Candidate);
            }
        }

        return new CleanupResult
        {
            SucceededCount = succeeded,
            FailedCount = failed,
            SkippedCount = skipped,
            BytesRecycled = bytesRecycled,
            RemainingIndexedCount = remainingIndexed,
            Failures = failures,
        };

        void ReportProcessed(CleanupCandidate candidate)
        {
            processed++;
            progress?.Report(new CleanupProgress
            {
                Processed = processed,
                Total = request.Candidates.Count,
                BytesRecycled = bytesRecycled,
                CurrentPath = candidate.Item.Path,
            });
        }

        async Task FinalizePreparedAsync(
            PreparedCleanup item,
            RecycleBinItemResult recycleResult)
        {
            var candidate = item.Candidate;
            if (recycleResult.Succeeded)
            {
                succeeded++;
                bytesRecycled += candidate.Item.DisplaySizeBytes;
                try
                {
                    await _repository.CompleteCleanupActionAsync(
                        item.PendingActionId,
                        "Recycled",
                        null,
                        CancellationToken.None);
                }
                catch (Exception auditException)
                {
                    failures.Add(new CleanupFailure
                    {
                        Path = candidate.Item.Path,
                        Error =
                            $"The file was recycled, but its audit record could not be finalized: {auditException.Message}",
                    });
                }

                try
                {
                    await _repository.RemoveIndexedFileAsync(
                        item.Candidate.ScanId,
                        candidate.Item.Path,
                        CancellationToken.None);
                }
                catch (Exception indexException)
                {
                    remainingIndexed++;
                    failures.Add(new CleanupFailure
                    {
                        Path = candidate.Item.Path,
                        Error =
                            $"The file was recycled, but the storage index could not be updated: {indexException.Message}",
                    });
                    try
                    {
                        await _repository.CompleteCleanupActionAsync(
                            item.PendingActionId,
                            "RecycledIndexStale",
                            indexException.Message,
                            CancellationToken.None);
                    }
                    catch (Exception auditException)
                    {
                        failures.Add(new CleanupFailure
                        {
                            Path = candidate.Item.Path,
                            Error =
                                $"The file was recycled, but its stale-index audit result could not be saved: {auditException.Message}",
                        });
                    }
                }
            }
            else
            {
                failed++;
                remainingIndexed++;
                var error = recycleResult.Error ??
                            "Windows could not move the item to the Recycle Bin.";
                failures.Add(new CleanupFailure
                {
                    Path = candidate.Item.Path,
                    Error = error,
                });
                try
                {
                    await _repository.CompleteCleanupActionAsync(
                        item.PendingActionId,
                        "Failed",
                        error,
                        CancellationToken.None);
                }
                catch (Exception auditException)
                {
                    failures.Add(new CleanupFailure
                    {
                        Path = candidate.Item.Path,
                        Error =
                            $"The cleanup failed and its audit result could not be saved: {auditException.Message}",
                    });
                }
            }
        }

        async Task CancelPreparedAsync(
            IEnumerable<PreparedCleanup> items,
            string reason)
        {
            foreach (var item in items)
            {
                try
                {
                    await _repository.CompleteCleanupActionAsync(
                        item.PendingActionId,
                        "Cancelled",
                        reason,
                        CancellationToken.None);
                }
                catch (Exception auditException)
                {
                    failures.Add(new CleanupFailure
                    {
                        Path = item.Candidate.Item.Path,
                        Error =
                            $"The cleanup was cancelled, but its audit result could not be saved: {auditException.Message}",
                    });
                }
            }
        }
    }

    private static bool IsRecoverableCleanupException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or Win32Exception
            or InvalidOperationException
            or COMException
            or NotSupportedException;

    private sealed record PreparedCleanup(
        CleanupCandidate Candidate,
        long PendingActionId);

    private static async Task<StorageFileRecord?> GetCurrentRecordAsync(
        CleanupCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var fileInfo = new FileInfo(candidate.Item.Path);
            if (!fileInfo.Exists)
            {
                return null;
            }

            var attributes = fileInfo.Attributes;
            var expected = candidate.FileRecord;
            if (fileInfo.Length != expected.SizeBytes ||
                Math.Abs((fileInfo.LastWriteTimeUtc - expected.ModifiedUtc.UtcDateTime).TotalSeconds) > 2)
            {
                return null;
            }

            var identity = FileIdentityService.TryGetIdentity(fileInfo.FullName);
            if (identity is null ||
                identity.VolumeSerialNumber != expected.VolumeSerialNumber ||
                (!string.IsNullOrWhiteSpace(expected.FileId128)
                    ? !string.Equals(
                        identity.FileId128,
                        expected.FileId128,
                        StringComparison.OrdinalIgnoreCase)
                    : identity.FileIndexHigh != expected.FileIndexHigh ||
                      identity.FileIndexLow != expected.FileIndexLow))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(expected.ContentHash))
            {
                await using var stream = new FileStream(
                    fileInfo.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    1_048_576,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var currentHash = Convert.ToHexString(
                    await System.Security.Cryptography.SHA256.HashDataAsync(
                        stream,
                        cancellationToken));
                if (!string.Equals(
                        currentHash,
                        expected.ContentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return new StorageFileRecord
            {
                ScanId = candidate.ScanId,
                Name = fileInfo.Name,
                FullPath = fileInfo.FullName,
                ParentPath = fileInfo.DirectoryName ?? string.Empty,
                SizeBytes = fileInfo.Length,
                CreatedUtc = fileInfo.CreationTimeUtc,
                ModifiedUtc = fileInfo.LastWriteTimeUtc,
                AccessedUtc = fileInfo.LastAccessTimeUtc,
                Attributes = (long)attributes,
                Extension = fileInfo.Extension,
                Category = candidate.Item.Category,
                Tag = string.Empty,
                VolumeSerialNumber = identity.VolumeSerialNumber,
                FileIndexHigh = identity.FileIndexHigh,
                FileIndexLow = identity.FileIndexLow,
                FileId128 = identity.FileId128,
                AllocatedSizeBytes = expected.AllocatedSizeBytes,
                PhysicalSizeBytes = expected.PhysicalSizeBytes,
                ContentHash = expected.ContentHash,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<bool> HasValidRetainedCopyAsync(
        CleanupCandidate candidate,
        CancellationToken cancellationToken)
    {
        var retained = await _repository.GetDuplicateRetainedRecordAsync(
            candidate.ScanId,
            candidate.FileRecord,
            cancellationToken);
        if (retained is null ||
            (!string.IsNullOrWhiteSpace(candidate.FileRecord.FileId128) &&
             string.Equals(
                 retained.FileId128,
                 candidate.FileRecord.FileId128,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var retainedCandidate = new CleanupCandidate
        {
            ScanId = candidate.ScanId,
            PolicyId = candidate.PolicyId,
            Item = CleanupCandidateService.ToStorageItem(retained),
            FileRecord = retained,
        };
        return await GetCurrentRecordAsync(
            retainedCandidate,
            cancellationToken) is not null;
    }
}
