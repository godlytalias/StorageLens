using StorageLens.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StorageLens.Services;

public sealed class CleanupService
{
    private readonly StorageRepository _repository;
    private readonly Action<string> _recycle;

    public CleanupService(StorageRepository repository, Action<string>? recycle = null)
    {
        _repository = repository;
        _recycle = recycle ?? RecycleBinService.Recycle;
    }

    public async Task<CleanupResult> RecycleAsync(
        CleanupRequest request,
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

        for (var index = 0; index < request.Candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = request.Candidates[index];
            long? pendingActionId = null;
            var recycled = false;
            progress?.Report(new CleanupProgress
            {
                Processed = index,
                Total = request.Candidates.Count,
                BytesRecycled = bytesRecycled,
                CurrentPath = candidate.Item.Path,
            });

            try
            {
                var record = await GetCurrentRecordAsync(candidate, cancellationToken);
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

                    continue;
                }

                var policy = CleanupPolicyService.Evaluate(record, candidate.PolicyId, now, request.IsAutomatic);
                if (!policy.IsAllowed)
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
                        policy.Reason,
                        cancellationToken);
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
                    continue;
                }

                pendingActionId = await _repository.BeginCleanupActionAsync(
                    candidate.ScanId,
                    candidate.PolicyId,
                    candidate.Item.Path,
                    candidate.Item.SizeBytes,
                    request.IsAutomatic,
                    cancellationToken);
                _recycle(candidate.Item.Path);
                recycled = true;
                succeeded++;
                bytesRecycled += candidate.Item.DisplaySizeBytes;
                try
                {
                    await _repository.CompleteCleanupActionAsync(
                        pendingActionId.Value,
                        "Recycled",
                        null,
                        CancellationToken.None);
                }
                catch (Exception auditException)
                {
                    failures.Add(new CleanupFailure
                    {
                        Path = candidate.Item.Path,
                        Error = $"The file was recycled, but its audit record could not be finalized: {auditException.Message}",
                    });
                }
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
                        Error = $"The file was recycled, but the storage index could not be updated: {indexException.Message}",
                    });
                    try
                    {
                        await _repository.CompleteCleanupActionAsync(
                            pendingActionId.Value,
                            "RecycledIndexStale",
                            indexException.Message,
                            CancellationToken.None);
                    }
                    catch (Exception auditException)
                    {
                        failures.Add(new CleanupFailure
                        {
                            Path = candidate.Item.Path,
                            Error = $"The file was recycled, but its stale-index audit result could not be saved: {auditException.Message}",
                        });
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or Win32Exception
                    or InvalidOperationException
                    or COMException
                    or NotSupportedException)
            {
                failed++;
                remainingIndexed++;
                failures.Add(new CleanupFailure
                {
                    Path = candidate.Item.Path,
                    Error = exception.Message,
                });
                if (pendingActionId.HasValue)
                {
                    try
                    {
                        await _repository.CompleteCleanupActionAsync(
                            pendingActionId.Value,
                            recycled ? "Recycled" : "Failed",
                            exception.Message,
                            CancellationToken.None);
                    }
                    catch (Exception auditException)
                    {
                        failures.Add(new CleanupFailure
                        {
                            Path = candidate.Item.Path,
                            Error = $"The cleanup failed and its audit result could not be saved: {auditException.Message}",
                        });
                    }
                }
                else
                {
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
                            Error = $"The cleanup failed and its audit record could not be saved: {auditException.Message}",
                        });
                    }
                }
            }
            finally
            {
                progress?.Report(new CleanupProgress
                {
                    Processed = index + 1,
                    Total = request.Candidates.Count,
                    BytesRecycled = bytesRecycled,
                    CurrentPath = candidate.Item.Path,
                });
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
    }

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
