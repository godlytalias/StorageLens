namespace StorageLens.Models;
using StorageLens.Localization;

public sealed class DriveSummary
{
    public required string Name { get; init; }

    public required string Letter { get; init; }

    public required string FileSystem { get; init; }

    public string VolumeId { get; init; } = string.Empty;

    public required long TotalBytes { get; init; }

    public required long UsedBytes { get; init; }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(Letter);

    public long FreeBytes => TotalBytes - UsedBytes;

    public double UsedPercentage => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes * 100;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Letter)
            ? Name
            : string.IsNullOrWhiteSpace(Name)
                ? StorageText.IsolatePath(Letter)
                : $"{StorageText.IsolatePath(Letter)} ({Name})";

    public static DriveSummary Unavailable() =>
        new()
        {
            Name = AppText.Get("DriveNoneAvailable"),
            Letter = string.Empty,
            FileSystem = string.Empty,
            TotalBytes = 0,
            UsedBytes = 0,
        };
}
