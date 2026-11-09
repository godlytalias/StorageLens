using StorageLens.Models;

namespace StorageLens.Services;

internal static class BackgroundIndexPolicy
{
    internal static async Task<bool> IsDueAsync(
        AutomationSettings settings,
        IReadOnlyList<DriveSummary> drives,
        Func<string, CancellationToken, Task<int?>> getLatestScanFormatAsync,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!settings.BackgroundIndexEnabled)
        {
            return false;
        }

        var intervalDays = Math.Clamp(
            settings.BackgroundIndexIntervalDays,
            1,
            7);
        if (settings.LastBackgroundIndexUtc is null ||
            now - settings.LastBackgroundIndexUtc.Value >=
                TimeSpan.FromDays(intervalDays))
        {
            return true;
        }

        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanFormat = await getLatestScanFormatAsync(
                drive.Letter,
                cancellationToken);
            if (!scanFormat.HasValue || scanFormat.Value < 4)
            {
                return true;
            }
        }

        return false;
    }
}
