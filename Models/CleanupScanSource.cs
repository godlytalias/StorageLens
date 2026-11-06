namespace StorageLens.Models;

public sealed record CleanupScanSource(
    Guid ScanId,
    string DriveLetter,
    DateTimeOffset EvaluatedUtc,
    long ItemCount,
    long SizeBytes);

public sealed record CleanupBatchScope(
    CleanupScanSource Source,
    CleanupPolicyId Policy,
    string? FolderPath = null);
