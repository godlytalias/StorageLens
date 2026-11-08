using StorageLens.Models;

namespace StorageLens.Services;

public sealed class CleanupBatchService
{
    private const int PageSize = 1000;
    private readonly CleanupCandidateService _candidates;
    private readonly CleanupService _cleanup;

    public CleanupBatchService(
        CleanupCandidateService candidates,
        CleanupService cleanup)
    {
        _candidates = candidates;
        _cleanup = cleanup;
    }

    public async Task<CleanupResult> RecycleAsync(
        IReadOnlyList<CleanupBatchScope> scopes,
        CleanupCandidateSelectionState selection,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (scopes.Any(scope =>
                CleanupPolicyService.GetDefinition(scope.Policy).Safety ==
                CleanupSafety.Caution))
        {
            throw new InvalidOperationException(
                "Windows-managed files cannot be recycled by Storage Lens.");
        }

        var snapshot = selection.CreateSnapshot();
        var seenPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var excluded = 0;
        var remainingIndexed = 0;
        var processed = 0;
        long recycledBytes = 0;
        var failures = new List<CleanupFailure>();
        var total = (int)Math.Min(
            int.MaxValue,
            scopes.Sum(scope => scope.Source.ItemCount));
        progress?.Report(new CleanupProgress
        {
            Processed = 0,
            Total = total,
            BytesRecycled = 0,
        });
        await using var worker = new CleanupExecutionWorker();
        foreach (var scope in scopes)
        {
            var offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await Task.Run(
                    () => _candidates.GetCandidatesAsync(
                        scope.Source,
                        scope.Policy,
                        scope.FolderPath,
                        PageSize,
                        offset,
                        cancellationToken),
                    cancellationToken);
                if (page.Count == 0)
                {
                    break;
                }

                var included = page.Where(candidate =>
                        snapshot.IsSelected(candidate) &&
                        seenPaths.Add(candidate.Item.Path))
                    .ToArray();
                var pageExcluded = page.Count - included.Length;
                excluded += pageExcluded;
                var pageStart = processed + pageExcluded;
                var bytesBeforePage = recycledBytes;
                var pageProgress = new BatchProgress(update =>
                    progress?.Report(new CleanupProgress
                    {
                        Processed = pageStart + update.Processed,
                        Total = total,
                        BytesRecycled =
                            bytesBeforePage + update.BytesRecycled,
                        CurrentPath = update.CurrentPath,
                    }));
                var result = included.Length == 0
                    ? new CleanupResult()
                    : await _cleanup.RecycleAsync(
                        new CleanupRequest
                        {
                            Candidates = included,
                            IsAutomatic = false,
                        },
                        pageProgress,
                        cancellationToken,
                        worker);
                succeeded += result.SucceededCount;
                failed += result.FailedCount;
                skipped += result.SkippedCount;
                recycledBytes += result.BytesRecycled;
                remainingIndexed += result.RemainingIndexedCount;
                failures.AddRange(result.Failures);
                processed += page.Count;

                // Successful and stale entries disappear from the query.
                offset += pageExcluded + result.RemainingIndexedCount;
                progress?.Report(new CleanupProgress
                {
                    Processed = processed,
                    Total = total,
                    BytesRecycled = recycledBytes,
                });
                if (page.Count < PageSize)
                {
                    break;
                }
            }
        }

        return new CleanupResult
        {
            SucceededCount = succeeded,
            FailedCount = failed,
            SkippedCount = skipped,
            ExcludedCount = excluded,
            BytesRecycled = recycledBytes,
            RemainingIndexedCount = remainingIndexed,
            Failures = failures,
        };
    }

    private sealed class BatchProgress(
        Action<CleanupProgress> report) : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => report(value);
    }
}
