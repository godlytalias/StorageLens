using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StorageLens.ViewModels;

namespace StorageLens.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    public SettingsViewModel ViewModel { get; } = new();

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (App.Window.Content is not FrameworkElement rootElement)
        {
            return;
        }

        rootElement.RequestedTheme = ViewModel.SelectedThemeIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.Window.Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = ViewModel.SelectedThemeIndex switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        await ViewModel.InitializeAsync();
    }
}
