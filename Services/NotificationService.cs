using StorageLens.Localization;
using StorageLens.Models;
using System.Globalization;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace StorageLens.Services;

public static class NotificationService
{
    public static void ShowGrowthAlert(string title, string body, string launchArgument)
    {
        Show(
            title,
            body,
            launchArgument,
            Guid.NewGuid().ToString("N")[..16],
            "StorageLensAlerts");
    }

    public static void ShowCleanupStarted()
    {
        Show(
            AppText.Get("CleanupInProgressInfoBar.Title"),
            AppText.Get("CleanupInProgressInfoBar.Message"),
            "storagelens://cleanup",
            "ManualCleanup",
            "Cleanup");
    }

    public static void ShowCleanupFinished(CleanupResult result)
    {
        var title = AppText.Get(
            result.SucceededCount == 0
                ? "CleanupResultNoChangesTitle"
                : result.FailedCount == 0
                    ? "CleanupResultSuccessTitle"
                    : "CleanupResultPartialTitle");
        var body = string.Format(
            CultureInfo.CurrentCulture,
            AppText.Get("CleanupResultSummaryFormat"),
            result.SucceededCount,
            result.FailedCount,
            result.SkippedCount,
            StorageFormatter.FormatBytes(result.BytesRecycled));
        Show(
            title,
            body,
            "storagelens://cleanup",
            "ManualCleanup",
            "Cleanup");
    }

    public static void ShowCleanupCancelled()
    {
        var message = AppText.Get("CleanupCancelled");
        Show(
            message,
            message,
            "storagelens://cleanup",
            "ManualCleanup",
            "Cleanup");
    }

    public static void ShowCleanupFailed(string message)
    {
        Show(
            AppText.Get("CleanupFailed"),
            message,
            "storagelens://cleanup",
            "ManualCleanup",
            "Cleanup");
    }

    private static void Show(
        string title,
        string body,
        string launchArgument,
        string tag,
        string group)
    {
        var toastXml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
        var textNodes = toastXml.GetElementsByTagName("text");
        textNodes[0].AppendChild(toastXml.CreateTextNode(title));
        textNodes[1].AppendChild(toastXml.CreateTextNode(body));

        var toast = toastXml.DocumentElement;
        toast?.SetAttribute("launch", launchArgument);
        toast?.SetAttribute("activationType", "protocol");

        var notification = new ToastNotification(toastXml)
        {
            Tag = tag,
            Group = group,
        };
        ToastNotificationManager.CreateToastNotifier().Show(notification);
    }
}
