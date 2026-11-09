using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StorageLens.Localization;

namespace StorageLens.Models;

public sealed partial class StorageFolderNode : ObservableObject
{
    public long EntryId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public string DisplayPath => StorageText.IsolatePath(Path);

    public required string Tag { get; init; }

    public required long SizeBytes { get; init; }

    public long AllocatedSizeBytes { get; init; }

    public long DisplaySizeBytes => AllocatedSizeBytes == 0
        ? SizeBytes
        : AllocatedSizeBytes;

    public long GrowthBytes { get; init; }

    public DateTimeOffset? GrowthBaselineUtc { get; init; }

    public bool HasGrowth => GrowthBytes > 0;

    public string GrowthText => HasGrowth
        ? $"+{StorageFormatter.FormatBytes(GrowthBytes)}"
        : string.Empty;

    public string AccessibleSummary =>
        $"{Name}, {DisplayTag}, {StorageFormatter.FormatBytes(DisplaySizeBytes)}, " +
        $"{(HasGrowth ? GrowthText + ", " : string.Empty)}{DisplayPath}";

    public string DisplayTag => StorageText.Tag(Tag);

    public bool IsReclaimable { get; init; }

    public bool HasChildren { get; init; }

    public ObservableCollection<StorageFolderNode> Children { get; init; } = [];

    private bool _isExpanded;

    private bool _isSelected;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
