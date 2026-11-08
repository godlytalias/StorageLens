using System.Text.Json;
using StorageLens.Models;
using Windows.Storage;

namespace StorageLens.Services;

public static class AppPreferencesService
{
    private const string SettingsKey = "StorageLens.AppPreferences.v1";

    public static AppPreferences Load()
    {
        if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingsKey, out var raw) &&
            raw is string json &&
            !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize(
                           json,
                           StorageLensJsonContext.Default.AppPreferences) ??
                       new AppPreferences();
            }
            catch (JsonException)
            {
                return new AppPreferences();
            }
        }

        return new AppPreferences();
    }

    public static void Save(AppPreferences preferences)
    {
        ApplicationData.Current.LocalSettings.Values[SettingsKey] =
            JsonSerializer.Serialize(
                preferences,
                StorageLensJsonContext.Default.AppPreferences);
    }
}
