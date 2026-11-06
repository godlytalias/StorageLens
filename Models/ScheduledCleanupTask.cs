using CommunityToolkit.Mvvm.ComponentModel;
using StorageLens.Localization;

namespace StorageLens.Models;

public sealed class ScheduledCleanupTask : ObservableObject
{
    private bool _isEnabled;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public required int IntervalDays { get; init; }

    public required IReadOnlyList<string> Categories { get; init; }

    public CleanupMode Mode { get; init; } = CleanupMode.ReviewOnly;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public DateTimeOffset LastRun { get; init; } = DateTimeOffset.Now.AddDays(-7);

    public DateTimeOffset NextRun => LastRun.AddDays(IntervalDays);

    public bool IsDue(DateTimeOffset now) =>
        IsEnabled && NextRun <= now;

    public string IntervalText => IntervalDays switch
    {
        1 => AppText.Get("ScheduleDaily"),
        7 => AppText.Get("ScheduleWeekly"),
        14 => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            AppText.Get("ScheduleEveryWeeksFormat"),
            2),
        30 => AppText.Get("ScheduleMonthly"),
        _ => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            AppText.Get("ScheduleEveryDaysFormat"),
            IntervalDays),
    };

    public string CategorySummary => string.Join(
        ", ",
        Categories.Select(StorageText.PolicyCategory));

    public string ModeText => Mode == CleanupMode.RecycleSafeItems
        ? AppText.Get("ScheduleModeRecycle")
        : AppText.Get("ScheduleModeReview");

    public string NextRunText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        AppText.Get("ScheduleNextReviewFormat"),
        NextRun);
}
