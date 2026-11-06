using StorageLens.Models;

namespace StorageLens.Services;

public sealed class CleanupCandidateService
{
    private readonly StorageRepository _repository;

    public CleanupCandidateService(StorageRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<CleanupRecommendation>> GetRecommendationsAsync(
        DriveSummary drive,
        DateTimeOffset now,
        int candidateLimit = 500,
        CancellationToken cancellationToken = default)
    {
        var scan = await _repository.GetLatestCompletedScanAsync(
            drive.Letter,
            cancellationToken);
        var recommendations = new List<CleanupRecommendation>();
        foreach (var definition in CleanupPolicyService.Policies)
        {
            IReadOnlyList<CleanupCandidateFolderSummary> folders = scan is null
                ? []
                : await _repository.GetCleanupCandidateFoldersAsync(
                    scan.Id,
                    definition.Id,
                    now,
                    cancellationToken);
            var count = folders.Sum(folder => folder.DirectCandidateCount);
            var bytes = folders.Sum(folder => folder.DirectCandidateBytes);
            recommendations.Add(ToRecommendation(
                definition,
                scan is null
                    ? []
                    : [new CleanupScanSource(
                        scan.Id,
                        drive.Letter,
                        now,
                        count,
                        bytes)]));
        }

        return recommendations;
    }

    public async Task<IReadOnlyList<CleanupRecommendation>> GetRecommendationsAsync(
        IEnumerable<DriveSummary> drives,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var recommendations = new List<CleanupRecommendation>();
        foreach (var drive in drives.DistinctBy(
                     item => item.Letter,
                     StringComparer.OrdinalIgnoreCase))
        {
            recommendations.AddRange(await GetRecommendationsAsync(
                drive,
                now,
                cancellationToken: cancellationToken));
        }

        return CleanupPolicyService.Policies
            .Select(definition => ToRecommendation(
                definition,
                recommendations
                    .Where(item => item.PolicyId == definition.Id)
                    .SelectMany(item => item.Sources)
                    .ToArray()))
            .ToArray();
    }

    public async Task<IReadOnlyList<CleanupCandidate>> GetCandidatesAsync(
        DriveSummary drive,
        CleanupPolicyId policy,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var scan = await _repository.GetLatestCompletedScanAsync(
            drive.Letter,
            cancellationToken);
        return scan is null
            ? []
            : await GetCandidatesAsync(
                new CleanupScanSource(
                    scan.Id,
                    drive.Letter,
                    DateTimeOffset.Now,
                    0,
                    0),
                policy,
                null,
                limit,
                offset,
                cancellationToken);
    }

    public async Task<IReadOnlyList<CleanupCandidate>> GetCandidatesAsync(
        CleanupScanSource source,
        CleanupPolicyId policy,
        string? folderPath = null,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var records = folderPath is null
            ? await _repository.GetCleanupCandidatesAsync(
                source.ScanId,
                policy,
                source.EvaluatedUtc,
                limit,
                offset,
                cancellationToken)
            : await _repository.GetCleanupCandidatesInFolderAsync(
                source.ScanId,
                policy,
                folderPath,
                source.EvaluatedUtc,
                limit,
                offset,
                cancellationToken);
        return records
            .Where(record => !string.IsNullOrWhiteSpace(record.FullPath))
            .Select(record =>
            {
                var safety = CleanupPolicyService.Evaluate(
                    record,
                    policy,
                    source.EvaluatedUtc,
                    false);
                return new CleanupCandidate
                {
                    ScanId = source.ScanId,
                    PolicyId = policy,
                    Item = ToStorageItem(record),
                    FileRecord = record,
                    EligibilityReason = safety.Reason,
                    IsSelectionEnabled = safety.IsAllowed,
                    IsEligibleForAutomaticCleanup =
                        CleanupPolicyService.Evaluate(
                            record,
                            policy,
                            source.EvaluatedUtc,
                            true).IsAllowed,
                };
            })
            .ToArray();
    }

    public async Task<long> GetCandidateCountAsync(
        DriveSummary drive,
        CleanupPolicyId policy,
        CancellationToken cancellationToken = default)
    {
        var scan = await _repository.GetLatestCompletedScanAsync(
            drive.Letter,
            cancellationToken);
        return scan is null
            ? 0
            : await _repository.GetCleanupCandidateCountAsync(
                scan.Id,
                policy,
                DateTimeOffset.Now,
                cancellationToken);
    }

    public async Task<IReadOnlyList<CleanupCandidateFolderNode>> GetCandidateFoldersAsync(
        CleanupScanSource source,
        CleanupPolicyId policy,
        CancellationToken cancellationToken = default)
    {
        var summaries = await _repository.GetCleanupCandidateFoldersAsync(
            source.ScanId,
            policy,
            source.EvaluatedUtc,
            cancellationToken);
        return CleanupCandidateFolderNode.BuildTree(summaries, source.ScanId);
    }

    public async Task<IReadOnlyList<CleanupCandidateFolderNode>> GetCandidateFoldersAsync(
        DriveSummary drive,
        CleanupPolicyId policy,
        CancellationToken cancellationToken = default)
    {
        var scan = await _repository.GetLatestCompletedScanAsync(
            drive.Letter,
            cancellationToken);
        return scan is null
            ? []
            : await GetCandidateFoldersAsync(
                new CleanupScanSource(
                    scan.Id,
                    drive.Letter,
                    DateTimeOffset.Now,
                    0,
                    0),
                policy,
                cancellationToken);
    }

    public async Task<IReadOnlyList<CleanupCandidate>> GetCandidatesInFolderAsync(
        DriveSummary drive,
        CleanupPolicyId policy,
        string folderPath,
        int limit = 200,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var scan = await _repository.GetLatestCompletedScanAsync(
            drive.Letter,
            cancellationToken);
        return scan is null
            ? []
            : await GetCandidatesAsync(
                new CleanupScanSource(
                    scan.Id,
                    drive.Letter,
                    DateTimeOffset.Now,
                    0,
                    0),
                policy,
                folderPath,
                limit,
                offset,
                cancellationToken);
    }

    public static StorageItem ToStorageItem(StorageFileRecord record) =>
        new()
        {
            Name = record.Name,
            Path = record.FullPath,
            Category = record.Category,
            Kind = string.IsNullOrWhiteSpace(record.Extension)
                ? "File"
                : record.Extension.TrimStart('.').ToUpperInvariant() + " file",
            ModifiedAt = record.ModifiedUtc,
            SizeBytes = record.SizeBytes,
            LogicalSizeBytes = record.SizeBytes,
            AllocatedSizeBytes = record.IsHardLinkAlias
                ? 0
                : record.AllocatedSizeBytes == 0
                    ? record.SizeBytes
                    : record.AllocatedSizeBytes,
            IsHardLinkAlias = record.IsHardLinkAlias,
            IsSystem = record.IsSystem,
            IsCloudOnly = record.IsCloudOnly,
            IsReclaimable = true,
        };

    private static CleanupRecommendation ToRecommendation(
        CleanupPolicyDefinition definition,
        IReadOnlyList<CleanupScanSource> sources) =>
        new()
        {
            PolicyId = definition.Id,
            ScanId = sources.Count == 1 ? sources[0].ScanId : Guid.Empty,
            Sources = sources,
            Name = definition.Name,
            Description = definition.Description,
            Glyph = definition.Glyph,
            Safety = definition.Safety,
            ReclaimableBytes = sources.Sum(source => source.SizeBytes),
            ItemCount = (int)Math.Min(
                int.MaxValue,
                sources.Sum(source => source.ItemCount)),
            IsAutomaticEligible = definition.AutomaticEligible,
            IsSelected = definition.Safety == CleanupSafety.Safe &&
                         sources.Any(source => source.ItemCount > 0),
        };
}
