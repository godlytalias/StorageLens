using StorageLens.Models;

namespace StorageLens.Localization;

public static class StorageText
{
    public static string IsolatePath(string value) =>
        "\u2066" + value + "\u2069";

    public static string Category(string value) =>
        LocalizedOrFallback(
            value switch
            {
                "Apps & games" => "CategoryAppsGames",
                "Videos" => "CategoryVideos",
                "Photos" => "CategoryPhotos",
                "System" => "CategorySystem",
                "Documents" => "CategoryDocuments",
                "Cache & temporary" => "CategoryCacheTemporary",
                _ => "CategoryOther",
            },
            value);

    public static string Tag(string value) =>
        LocalizedOrFallback(
            value switch
            {
                "System" => "TagSystem",
                "Cache" => "TagCache",
                "Review" => "TagReview",
                "Personal" => "TagPersonal",
                "Apps" => "TagApps",
                _ => "TagOther",
            },
            value);

    public static string Kind(string value)
    {
        if (string.Equals(value, "File", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizedOrFallback("KindFile", value);
        }

        if (value.EndsWith(
                " file",
                StringComparison.OrdinalIgnoreCase))
        {
            var extension = value[..^5];
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AppText.Get("KindExtensionFileFormat"),
                extension);
        }

        return Tag(value);
    }

    public static string ScanPhase(string value) =>
        LocalizedOrFallback(
            value switch
            {
                "Enumerating" => "ScanPhaseEnumerating",
                "Finalizing" => "ScanPhaseFinalizing",
                "Fingerprinting" => "ScanPhaseFingerprinting",
                "Complete" => "ScanPhaseComplete",
                _ => "ScanPhaseScanning",
            },
            value == "Enumerating" ? "Indexing" : value);

    public static string PolicyName(CleanupPolicyId policy, string fallback) =>
        LocalizedOrFallback($"CleanupPolicy{(int)policy}Name", fallback);

    public static string PolicyDescription(
        CleanupPolicyId policy,
        string fallback) =>
        LocalizedOrFallback(
            $"CleanupPolicy{(int)policy}Description",
            fallback);

    public static string PolicyCategory(string value)
    {
        var policy = StorageLens.Services.CleanupPolicyService.ParsePolicy(
            value);
        if (!policy.HasValue)
        {
            return value;
        }

        return PolicyName(
            policy.Value,
            StorageLens.Services.CleanupPolicyService
                .GetDefinition(policy.Value)
                .Name);
    }

    private static string LocalizedOrFallback(
        string key,
        string fallback)
    {
        var value = AppText.Get(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
