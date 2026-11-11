using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StorageLens.Models;
using StorageLens.ViewModels;

namespace StorageLens.Views;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OverviewPage_Loaded;
    }

    public OverviewViewModel ViewModel { get; } = new();

    private void ReviewCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(CleanupPage));
    }

    private void ExploreStorageButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(
            typeof(ExplorerPage),
            new ExplorerNavigationRequest
            {
                DriveLetter = ViewModel.SelectedDrive.Letter,
                FolderPath = $@"{ViewModel.SelectedDrive.Letter}\",
            });
    }

    private void CategoryTile_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var accessibleName = GetAccessibleName(element);
        var category = ViewModel.Categories.FirstOrDefault(item =>
            string.Equals(
                item.DisplayName,
                accessibleName,
                StringComparison.Ordinal));
        category ??= element?.Tag is string name
            ? ViewModel.Categories.FirstOrDefault(item =>
                string.Equals(
                    item.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            : element?.DataContext as StorageCategory;
        if (category is null)
        {
            return;
        }

        Frame.Navigate(
            typeof(ExplorerPage),
            new ExplorerNavigationRequest
            {
                DriveLetter = ViewModel.SelectedDrive.Letter,
                Category = category.Name,
            });
    }

    private void TopFolder_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var folder = element?.Tag is string path
            ? ViewModel.TopFolders.FirstOrDefault(item =>
                string.Equals(
                    item.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase))
            : element?.DataContext as StorageFolderNode;
        if (folder is null)
        {
            var accessibleName = GetAccessibleName(element);
            folder = ViewModel.TopFolders.FirstOrDefault(item =>
                string.Equals(
                    item.AccessibleSummary,
                    accessibleName,
                    StringComparison.Ordinal));
        }

        if (folder is not null)
        {
            NavigateToFolder(folder.Path);
        }
    }

    private void GrowingFolder_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var accessibleName = GetAccessibleName(element);
        var folder = ViewModel.GrowingFolders.FirstOrDefault(item =>
            string.Equals(
                item.Name,
                accessibleName,
                StringComparison.Ordinal));
        folder ??= element?.Tag is string path
            ? ViewModel.GrowingFolders.FirstOrDefault(item =>
                string.Equals(
                    item.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase))
            : element?.DataContext as FolderGrowthInsight;
        if (folder is not null)
        {
            NavigateToFolder(folder.Path);
        }
    }

    private void CleanupRecommendation_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var accessibleName = GetAccessibleName(element);
        var recommendation = ViewModel.CleanupSuggestions.FirstOrDefault(
            item =>
                string.Equals(
                    item.AccessibleSummary,
                    accessibleName,
                    StringComparison.Ordinal) ||
                accessibleName?.StartsWith(
                    item.DisplayName,
                    StringComparison.Ordinal) == true);
        recommendation ??= element?.Tag is CleanupPolicyId policy
            ? ViewModel.CleanupSuggestions.FirstOrDefault(item =>
                item.PolicyId == policy)
            : element?.DataContext as CleanupRecommendation;
        if (recommendation is not null)
        {
            Frame.Navigate(
                typeof(CleanupPage),
                new CleanupNavigationRequest
                {
                    DriveLetter = ViewModel.SelectedDrive.Letter,
                    RecommendationName = recommendation.Name,
                });
        }
    }

    private void LargestFile_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var item = element?.Tag is string path
            ? ViewModel.LargestFiles.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase))
            : element?.DataContext as StorageItem;
        if (item is null)
        {
            var accessibleName = GetAccessibleName(element);
            item = ViewModel.LargestFiles.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.AccessibleSummary,
                    accessibleName,
                    StringComparison.Ordinal));
        }

        if (item is null)
        {
            return;
        }

        Frame.Navigate(
            typeof(ExplorerPage),
            new ExplorerNavigationRequest
            {
                DriveLetter = ViewModel.SelectedDrive.Letter,
                FolderPath = ViewModels.StoragePath.GetParent(item.Path),
                SelectedItemPath = item.Path,
            });
    }

    private void NavigateToFolder(string path)
    {
        Frame.Navigate(
            typeof(ExplorerPage),
            new ExplorerNavigationRequest
            {
                DriveLetter = ViewModel.SelectedDrive.Letter,
                FolderPath = path,
            });
    }

    private static string GetAccessibleName(FrameworkElement? element) =>
        element is null
            ? string.Empty
            : AutomationProperties.GetName(element);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is OverviewNavigationRequest request &&
            !string.IsNullOrWhiteSpace(request.DriveLetter))
        {
            var drive = ViewModel.Drives.FirstOrDefault(item =>
                string.Equals(item.Letter, request.DriveLetter, StringComparison.OrdinalIgnoreCase));
            if (drive is not null)
            {
                ViewModel.SelectedDrive = drive;
            }
        }

        base.OnNavigatedTo(e);
    }

    private async void OverviewPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }
}
