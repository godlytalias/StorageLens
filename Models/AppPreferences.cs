namespace StorageLens.Models;

public sealed class AppPreferences
{
    public int ThemeIndex { get; set; }

    public bool ScanOnLaunch { get; set; }

    public bool ShowSystemItems { get; set; }

    public bool ShowHiddenItems { get; set; }

    public bool SmartCleanupSuggestions { get; set; } = true;

    public bool ConfirmBeforeCleanup { get; set; } = true;

    public bool ExcludeCloudOnlyFiles { get; set; } = true;
}
