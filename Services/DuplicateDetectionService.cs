using System.Collections.Concurrent;
using System.Security.Cryptography;
using StorageLens.Models;

namespace StorageLens.Services;

public sealed class DuplicateDetectionService
{
    internal const string HashFailedMarker = "__HASH_FAILED__";
    private readonly StorageRepository _repository;

    public DuplicateDetectionService(StorageRepository repository)
    {
        _repository = repository;
    }

    public async Task<int> ComputeHashesAsync(
        Guid scanId,
        IProgress<DuplicateDetectionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int groupBatchSize = 250,
        int? maxFiles = null,
        long? maxBytes = null,
        TimeSpan? maxDuration = null,
        long maximumFileSize = 32L * 1024 * 1024 * 1024)
    {
        var hashed = 0;
        var attempted = 0;
        long bytesQueued = 0;
        using var budgetCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (maxDuration.HasValue)
        {
            budgetCancellation.CancelAfter(maxDuration.Value);
        }

        try
        {
            var collisionGroups =
                await _repository.GetSampleHashCollisionGroupsAsync(
                    scanId,
                    Math.Max(1, groupBatchSize),
                    budgetCancellation.Token);
            if (collisionGroups.Count == 0)
            {
                attempted += await ComputeSampleHashesCoreAsync(
                    scanId,
                    progress,
                    budgetCancellation.Token,
                    groupBatchSize,
                    maxFiles,
                    maximumFileSize);
                if (maxFiles.HasValue &&
                    attempted >= maxFiles.Value)
                {
                    return hashed;
                }

                collisionGroups =
                    await _repository.GetSampleHashCollisionGroupsAsync(
                        scanId,
                        Math.Max(1, groupBatchSize),
                        budgetCancellation.Token);
            }

            while (collisionGroups.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = collisionGroups
                    .SelectMany(group =>
                        group.Paths.Select(path => (
                            group.SizeBytes,
                            Path: path)))
                    .Take(maxFiles.HasValue
                        ? Math.Max(0, maxFiles.Value - attempted)
                        : int.MaxValue)
                    .TakeWhile(candidate =>
                    {
                        var projected = bytesQueued + candidate.SizeBytes;
                        if (maxBytes.HasValue &&
                            projected > maxBytes.Value)
                        {
                            return false;
                        }

                        bytesQueued = projected;
                        return true;
                    })
                    .ToList();
                if (candidates.Count == 0)
                {
                    break;
                }

                var updates = new ConcurrentBag<FileHashUpdate>();
                var processed = 0;
                try
                {
                    await Parallel.ForEachAsync(
                        candidates,
                        new ParallelOptions
                        {
                            CancellationToken = budgetCancellation.Token,
                            MaxDegreeOfParallelism =
                                Math.Max(
                                    2,
                                    Math.Min(Environment.ProcessorCount, 4)),
                        },
                        async (candidate, token) =>
                        {
                            Interlocked.Increment(ref attempted);
                            try
                            {
                                var fileInfo = new FileInfo(candidate.Path);
                                if (!fileInfo.Exists ||
                                    fileInfo.Length != candidate.SizeBytes)
                                {
                                    updates.Add(new FileHashUpdate(
                                        candidate.Path,
                                        HashFailedMarker,
                                        null));
                                    return;
                                }

                                await using var stream = new FileStream(
                                    candidate.Path,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.Read | FileShare.Delete,
                                    1_048_576,
                                    FileOptions.Asynchronous |
                                    FileOptions.SequentialScan);
                                var hashBytes =
                                    await SHA256.HashDataAsync(stream, token);
                                updates.Add(new FileHashUpdate(
                                    candidate.Path,
                                    Convert.ToHexString(hashBytes),
                                    FileIdentityService.TryGetIdentity(
                                        candidate.Path)));
                                Interlocked.Increment(ref hashed);
                            }
                            catch (Exception exception) when (
                                exception is IOException
                                    or UnauthorizedAccessException)
                            {
                                updates.Add(new FileHashUpdate(
                                    candidate.Path,
                                    HashFailedMarker,
                                    null));
                            }
                            finally
                            {
                                progress?.Report(
                                    new DuplicateDetectionProgress(
                                        candidate.Path,
                                        Interlocked.Increment(ref processed),
                                        candidates.Count));
                            }
                        });
                }
                finally
                {
                    if (!updates.IsEmpty)
                    {
                        await _repository.SetFileHashesAsync(
                            scanId,
                            updates.ToArray(),
                            CancellationToken.None);
                    }
                }

                if (maxFiles.HasValue && attempted >= maxFiles.Value)
                {
                    break;
                }

                collisionGroups =
                    await _repository.GetSampleHashCollisionGroupsAsync(
                        scanId,
                        Math.Max(1, groupBatchSize),
                        budgetCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            maxDuration.HasValue)
        {
        }

        return hashed;
    }

    private async Task<int> ComputeSampleHashesCoreAsync(
        Guid scanId,
        IProgress<DuplicateDetectionProgress>? progress,
        CancellationToken cancellationToken,
        int groupBatchSize,
        int? maxFiles,
        long maximumFileSize)
    {
        var attempted = 0;
        while (!maxFiles.HasValue || attempted < maxFiles.Value)
        {
            var groups =
                await _repository.GetUnsampledDuplicateSizeGroupsAsync(
                    scanId,
                    maximumFileSize: maximumFileSize,
                    limit: Math.Max(1, groupBatchSize),
                    cancellationToken: cancellationToken);
            if (groups.Count == 0)
            {
                break;
            }

            var candidates = groups
                .SelectMany(group =>
                    group.Paths.Select(path => (
                        group.SizeBytes,
                        Path: path)))
                .Take(maxFiles.HasValue
                    ? Math.Max(0, maxFiles.Value - attempted)
                    : int.MaxValue)
                .ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            var updates = new ConcurrentBag<FileHashUpdate>();
            var processed = 0;
            try
            {
                await Parallel.ForEachAsync(
                    candidates,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism =
                            Math.Max(
                                2,
                                Math.Min(Environment.ProcessorCount, 4)),
                    },
                    async (candidate, token) =>
                    {
                        Interlocked.Increment(ref attempted);
                        try
                        {
                            var sampleHash =
                                await ComputeSampleHashAsync(
                                    candidate.Path,
                                    candidate.SizeBytes,
                                    token);
                            updates.Add(new FileHashUpdate(
                                candidate.Path,
                                sampleHash,
                                null));
                        }
                        catch (Exception exception) when (
                            exception is IOException
                                or UnauthorizedAccessException)
                        {
                            updates.Add(new FileHashUpdate(
                                candidate.Path,
                                HashFailedMarker,
                                null));
                        }
                        finally
                        {
                            progress?.Report(
                                new DuplicateDetectionProgress(
                                    candidate.Path,
                                    Interlocked.Increment(ref processed),
                                    candidates.Count));
                        }
                    });
            }
            finally
            {
                if (!updates.IsEmpty)
                {
                    await _repository.SetFileSampleHashesAsync(
                        scanId,
                        updates.ToArray(),
                        CancellationToken.None);
                }
            }
        }

        return attempted;
    }

    private static async Task<string> ComputeSampleHashAsync(
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        const int sampleLength = 64 * 1024;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            sampleLength,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var incremental = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var buffer = new byte[sampleLength];
        var positions = length <= sampleLength
            ? [0L]
            : length <= sampleLength * 2
                ? [0L, (long)sampleLength]
                : length <= sampleLength * 3
                    ? [0L, (long)sampleLength, (long)sampleLength * 2]
                    : new[]
            {
                0L,
                Math.Max(0, length / 2 - sampleLength / 2),
                Math.Max(0, length - sampleLength),
            };
        foreach (var position in positions)
        {
            stream.Seek(position, SeekOrigin.Begin);
            var remaining = length <= sampleLength * 3
                ? stream.Length
                : Math.Min(sampleLength, length - position);
            var bytesRead = 0;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(
                        bytesRead,
                        Math.Min(
                            buffer.Length - bytesRead,
                            checked((int)remaining))),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
                remaining -= read;
                if (bytesRead == buffer.Length)
                {
                    break;
                }
            }

            incremental.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexString(incremental.GetHashAndReset());
    }
}

public sealed record DuplicateDetectionProgress(
    string CurrentPath,
    int Processed,
    int Total);
