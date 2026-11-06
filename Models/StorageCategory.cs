namespace StorageLens.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using StorageLens.Localization;

public sealed partial class StorageCategory : ObservableObject
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Glyph { get; init; }

    public required long SizeBytes { get; init; }

    public required int ItemCount { get; init; }

    public double Percentage { get; set; }

    public string DisplayName => StorageText.Category(Name);

    public string DisplayDescription =>
        LocalizeDescription(Name, Description);

    private static string LocalizeDescription(string name, string fallback)
    {
        var key = name switch
        {
            "Apps & games" => "CategoryAppsGamesDescription",
            "Videos" => "CategoryVideosDescription",
            "Photos" => "CategoryPhotosDescription",
            "System" => "CategorySystemDescription",
            "Documents" => "CategoryDocumentsDescription",
            "Cache & temporary" => "CategoryCacheTemporaryDescription",
            _ => "CategoryOtherDescription",
        };
        var localized = AppText.Get(key);
        return string.IsNullOrWhiteSpace(localized) ? fallback : localized;
    }
}
