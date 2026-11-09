using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using StorageLens.Services;
using System.Runtime.InteropServices;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace StorageLens;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var appTitleBar = new TitleBar
        {
            Title = "Storage Lens",
            IconSource = new ImageIconSource
            {
                ImageSource = new BitmapImage(
                    new Uri("ms-appx:///Assets/AppIcon.ico")),
            },
        };
        AppTitleBarHost.Children.Add(appTitleBar);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(appTitleBar);

        AppWindow.SetIcon(
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var scale =
            GetDpiForWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)) /
            96d;
        var preferredWidth = (int)Math.Round(1440 * scale);
        var preferredHeight = (int)Math.Round(900 * scale);
        var minimumWidth = (int)Math.Round(800 * scale);
        var minimumHeight = (int)Math.Round(600 * scale);
        var width = Math.Min(
            preferredWidth,
            Math.Max(minimumWidth, displayArea.WorkArea.Width - 64));
        var height = Math.Min(
            preferredHeight,
            Math.Max(minimumHeight, displayArea.WorkArea.Height - 64));
        AppWindow.Resize(new SizeInt32(width, height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(
                minimumWidth,
                displayArea.WorkArea.Width);
            presenter.PreferredMinimumHeight = Math.Min(
                minimumHeight,
                displayArea.WorkArea.Height);
        }

        if (Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = AppPreferencesService.Load().ThemeIndex switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        RootFrame.Navigate(typeof(MainPage), App.PendingLaunchArgument);
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.ApplyActivationArgument(App.PendingLaunchArgument);
        }
    }

    public void HandleActivation(string argument)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.ApplyActivationArgument(argument, true);
        }

        Activate();
    }

    [LibraryImport(
        "user32.dll",
        EntryPoint = "GetDpiForWindow",
        SetLastError = true)]
    private static partial uint GetDpiForWindow(nint windowHandle);
}
