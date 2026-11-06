using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StorageLens.Localization;

namespace StorageLens.Models;

public sealed partial class StorageItem : ObservableObject
{
    public long EntryId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public string DisplayPath => StorageText.IsolatePath(Path);

    public required string Category { get; init; }

    public string DisplayCategory => StorageText.Category(Category);

    public required string Kind { get; init; }

    public string DisplayKind => StorageText.Kind(Kind);

    public string AccessibleSummary =>
        $"{Name}, {DisplayCategory}, {StorageFormatter.FormatBytes(DisplaySizeBytes)}, {DisplayPath}";

    public required DateTimeOffset ModifiedAt { get; init; }

    public string Modified => ModifiedAt.Date == DateTimeOffset.Now.Date
        ? string.Format(CultureInfo.CurrentCulture, AppText.Get("ModifiedTodayFormat"), ModifiedAt)
        : ModifiedAt.Date == DateTimeOffset.Now.Date.AddDays(-1)
            ? AppText.Get("ModifiedYesterday")
            : ModifiedAt.ToString("MMM d", CultureInfo.CurrentCulture);

    public required long SizeBytes { get; init; }

    public long AllocatedSizeBytes { get; init; }

    public long LogicalSizeBytes { get; init; }

    public bool IsHardLinkAlias { get; init; }

    public bool IsIncomplete { get; init; }

    public long DisplaySizeBytes => IsHardLinkAlias
        ? 0
        : AllocatedSizeBytes == 0
            ? SizeBytes
            : AllocatedSizeBytes;

    public bool IsFolder { get; init; }

    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";

    public bool IsReclaimable { get; init; }

    public bool IsSystem { get; init; }

    public bool IsCloudOnly { get; init; }
}
