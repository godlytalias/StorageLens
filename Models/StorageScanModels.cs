using StorageLens.Services;

namespace StorageLens.Models;

public enum StorageScanStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

public sealed class StorageScanRun
{
    public long RunKey { get; init; }

    public int ScanFormat { get; init; } = 1;

    public required Guid Id { get; init; }

    public required string DriveLetter { get; init; }

    public required string DriveName { get; init; }

    public required string FileSystem { get; init; }

    public required long TotalBytes { get; init; }

    public required long UsedBytes { get; init; }

    public required long FreeBytes { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public StorageScanStatus Status { get; init; }

    public long FilesScanned { get; init; }

    public long FoldersScanned { get; init; }

    public long BytesScanned { get; init; }

    public long AllocatedBytesScanned { get; init; }

    public long InaccessibleItems { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class StorageScanProgress
{
    public required string DriveLetter { get; init; }

    public required string CurrentPath { get; init; }

    public long FilesScanned { get; init; }

    public long FoldersScanned { get; init; }

    public long BytesScanned { get; init; }

    public long AllocatedBytesScanned { get; init; }

    public long InaccessibleItems { get; init; }

    public double? PercentComplete { get; init; }

    public string Phase { get; init; } = "Scanning";

    public double ItemsPerSecond { get; init; }

    public TimeSpan Elapsed { get; init; }
}

public sealed class StorageScanResult
{
    public required Guid ScanId { get; init; }

    public required string DriveLetter { get; init; }

    public required StorageScanStatus Status { get; init; }

    public long FilesScanned { get; init; }

    public long FoldersScanned { get; init; }

    public long BytesScanned { get; init; }

    public long AllocatedBytesScanned { get; init; }

    public long InaccessibleItems { get; init; }

    public required TimeSpan Duration { get; init; }

    public string? ErrorMessage { get; init; }

    public string? WarningMessage { get; init; }
}

public sealed class StorageFileRecord
{
    public Guid ScanId { get; init; }

    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required string ParentPath { get; init; }

    public required long SizeBytes { get; init; }

    public long AllocatedSizeBytes { get; init; }

    public long PhysicalSizeBytes { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset ModifiedUtc { get; init; }

    public required DateTimeOffset AccessedUtc { get; init; }

    public long ChangeTime { get; init; }

    public required long Attributes { get; init; }

    public required string Extension { get; init; }

    public required string Category { get; init; }

    public required string Tag { get; init; }

    public long VolumeSerialNumber { get; init; }

    public long FileIndexHigh { get; init; }

    public long FileIndexLow { get; init; }

    public string FileId128 { get; init; } = string.Empty;

    public uint ReparseTag { get; init; }

    public uint LinkCount { get; init; }

    public bool IsHardLinkAlias { get; init; }

    public bool IsIncomplete { get; init; }

    public string? ContentHash { get; init; }

    public bool IsSystem => ((FileAttributes)Attributes & FileAttributes.System) != 0;

    public bool IsHidden => ((FileAttributes)Attributes & FileAttributes.Hidden) != 0;

    public bool IsCloudOnly => ReparseTag == StorageReparseTags.CloudFiles ||
                                ((Attributes & 0x00040000L) != 0 &&
                                 (Attributes & (long)FileAttributes.ReparsePoint) != 0);
}

public sealed class StorageFolderRecord
{
    public Guid ScanId { get; init; }

    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required string ParentPath { get; init; }

    public required long SizeBytes { get; init; }

    public long AllocatedSizeBytes { get; init; }

    public required long DirectSizeBytes { get; init; }

    public long DirectAllocatedSizeBytes { get; init; }

    public required long FileCount { get; init; }

    public required long DirectFileCount { get; init; }

    public required int Depth { get; init; }

    public required long Attributes { get; init; }

    public uint ReparseTag { get; init; }

    public bool IsIncomplete { get; init; }

    public string FileId128 { get; init; } = string.Empty;

    public required DateTimeOffset ModifiedUtc { get; init; }

    public required string Category { get; init; }

    public required string Tag { get; init; }
}
