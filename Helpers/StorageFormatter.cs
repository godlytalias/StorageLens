using System.Globalization;
using StorageLens.Localization;

namespace StorageLens;

public static class StorageFormatter
{
    private static readonly string[] Units =
    [
        AppText.Get("ByteUnitBytes"),
        AppText.Get("ByteUnitKilobytes"),
        AppText.Get("ByteUnitMegabytes"),
        AppText.Get("ByteUnitGigabytes"),
        AppText.Get("ByteUnitTerabytes"),
    ];

    public static string FormatBytes(long bytes)
    {
        if (bytes == 0)
        {
            return $"0 {Units[0]}";
        }

        var absoluteBytes = Math.Abs((double)bytes);
        var unitIndex = Math.Min((int)Math.Log(absoluteBytes, 1024), Units.Length - 1);
        var value = absoluteBytes / Math.Pow(1024, unitIndex);
        var formatted = value >= 100 ? value.ToString("0", CultureInfo.CurrentCulture) : value >= 10 ? value.ToString("0.0", CultureInfo.CurrentCulture) : value.ToString("0.00", CultureInfo.CurrentCulture);
        return $"{(bytes < 0 ? "-" : string.Empty)}{formatted} {Units[unitIndex]}";
    }

    public static string FormatPercentage(double value) => (value / 100).ToString("P0", CultureInfo.CurrentCulture);

    public static string FormatItemCount(long count)
    {
        var format = AppText.Get(count == 1 ? "ItemCountSingularFormat" : "ItemCountPluralFormat");
        return string.Format(CultureInfo.CurrentCulture, format, count);
    }
}
