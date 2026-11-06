using Windows.ApplicationModel.Resources;

namespace StorageLens.Localization;

public static class AppText
{
    private static readonly ResourceLoader Loader = ResourceLoader.GetForViewIndependentUse();

    public static string Get(string key) => Loader.GetString(key);
}
