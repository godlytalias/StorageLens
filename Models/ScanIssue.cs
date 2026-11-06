namespace StorageLens.Models;

public enum ScanIssueType
{
    AccessDenied,
    IoError,
    ReparseExcluded,
    VirtualizedRoot,
    ChangedDuringScan,
    MetadataUnavailable,
}

public sealed class ScanIssue
{
    public required string Path { get; init; }

    public required ScanIssueType Type { get; init; }

    public uint ReparseTag { get; init; }

    public int? ErrorCode { get; init; }

    public required string Message { get; init; }

    public required DateTimeOffset DetectedUtc { get; init; }
}
