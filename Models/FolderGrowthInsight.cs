namespace StorageLens.Models;
using CommunityToolkit.Mvvm.ComponentModel;

public sealed partial class FolderGrowthInsight : ObservableObject
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public string DisplayPath =>
        StorageLens.Localization.StorageText.IsolatePath(Path);

    public required long SizeBytes { get; init; }

    public required long AddedBytes { get; init; }

    public required double GrowthPercentage { get; init; }

    public required int WindowDays { get; init; }

    public string AddedText => $"+{StorageFormatter.FormatBytes(AddedBytes)}";

    public string GrowthText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        StorageLens.Localization.AppText.Get("GrowthSummaryFormat"),
        GrowthPercentage,
        WindowDays);
}
