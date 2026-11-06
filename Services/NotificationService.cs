using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace StorageLens.Services;

public static class NotificationService
{
    public static void ShowGrowthAlert(string title, string body, string launchArgument)
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
            Tag = Guid.NewGuid().ToString("N")[..16],
            Group = "StorageLensAlerts",
        };
        ToastNotificationManager.CreateToastNotifier().Show(notification);
    }
}
