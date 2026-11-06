using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Globalization;
using StorageLens.Models;
using StorageLens.Views;

namespace StorageLens;

public sealed partial class MainPage : Page
{
    private string? _lastActivationArgument;

    public MainPage()
    {
        InitializeComponent();
        FlowDirection =
            CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
                ? Microsoft.UI.Xaml.FlowDirection.RightToLeft
                : Microsoft.UI.Xaml.FlowDirection.LeftToRight;
        ContentFrame.Navigate(typeof(OverviewPage));
    }

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        switch ((args.InvokedItemContainer as NavigationViewItem)?.Tag?.ToString())
        {
            case "Overview":
                Navigate(typeof(OverviewPage));
                break;
            case "Explorer":
                Navigate(typeof(ExplorerPage));
                break;
            case "Cleanup":
                Navigate(typeof(CleanupPage));
                break;
            case "Automation":
                Navigate(typeof(AutomationPage));
                break;
            case "Help":
                Navigate(typeof(HelpPage));
                break;
        }
    }

    private void NavigationView_BackRequested(
        NavigationView sender,
        NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        AppNavigationView.SelectedItem = e.SourcePageType.Name switch
        {
            nameof(OverviewPage) => AppNavigationView.MenuItems[0],
            nameof(ExplorerPage) => AppNavigationView.MenuItems[1],
            nameof(CleanupPage) or nameof(CleanupCandidatesPage) =>
                AppNavigationView.MenuItems[2],
            nameof(AutomationPage) => AppNavigationView.MenuItems[3],
            nameof(HelpPage) => AppNavigationView.FooterMenuItems[0],
            nameof(SettingsPage) => AppNavigationView.SettingsItem,
            _ => AppNavigationView.SelectedItem,
        };
    }

    private void Navigate(Type pageType, object? parameter = null)
    {
        if (ContentFrame.CurrentSourcePageType == pageType && parameter is null)
        {
            return;
        }

        ContentFrame.Navigate(pageType, parameter);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ApplyActivationArgument(e.Parameter as string);
        base.OnNavigatedTo(e);
    }

    public void ApplyActivationArgument(string? activationArgument, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(activationArgument))
        {
            activationArgument = Environment.GetCommandLineArgs()
                .FirstOrDefault(argument => argument.StartsWith("storagelens://", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(activationArgument))
        {
            var commandLine = Environment.CommandLine;
            var protocolStart = commandLine.IndexOf("storagelens://", StringComparison.OrdinalIgnoreCase);
            if (protocolStart >= 0)
            {
                var protocolEnd = commandLine.IndexOf(' ', protocolStart);
                activationArgument = protocolEnd < 0
                    ? commandLine[protocolStart..]
                    : commandLine[protocolStart..protocolEnd];
            }
        }

        if (!string.IsNullOrWhiteSpace(activationArgument))
        {
            if (!force &&
                string.Equals(_lastActivationArgument, activationArgument, StringComparison.Ordinal))
            {
                return;
            }

            _lastActivationArgument = activationArgument;
            HandlePendingActivation(activationArgument);
        }
    }

    private void HandlePendingActivation(string activationArgument)
    {
        if (!Uri.TryCreate(activationArgument, UriKind.Absolute, out var activationUri))
        {
            return;
        }

        if (string.Equals(activationUri.Host, "cleanup", StringComparison.OrdinalIgnoreCase))
        {
            var policyIds = GetActivationQueryValue(activationArgument, "policies")?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Enum.TryParse<CleanupPolicyId>(value, out var policy) ? policy : (CleanupPolicyId?)null)
                .Where(policy => policy.HasValue)
                .Select(policy => policy!.Value)
                .ToList() ?? [];
            Navigate(
                typeof(CleanupPage),
                new CleanupNavigationRequest
                {
                    PolicyIds = policyIds,
                });
        }
        else if (string.Equals(
                     activationUri.Host,
                     "automation",
                     StringComparison.OrdinalIgnoreCase))
        {
            Navigate(typeof(AutomationPage));
        }
        else if (string.Equals(
                     activationUri.Host,
                     "growth",
                     StringComparison.OrdinalIgnoreCase))
        {
            var folder = GetActivationQueryValue(activationArgument, "folder");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Navigate(
                    typeof(ExplorerPage),
                    new ExplorerNavigationRequest
                    {
                        DriveLetter = folder.Length >= 2 ? folder[..2] : null,
                        FolderPath = folder,
                    });
            }
            else
            {
                Navigate(
                    typeof(OverviewPage),
                    new OverviewNavigationRequest
                    {
                        DriveLetter = GetActivationQueryValue(activationArgument, "drive"),
                    });
            }
        }
    }

    private static string? GetActivationQueryValue(string activationArgument, string key)
    {
        if (!Uri.TryCreate(activationArgument, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var prefix = $"{key}=";
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[prefix.Length..]);
            }
        }

        return null;
    }
}
