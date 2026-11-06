using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StorageLens.Localization;
using StorageLens.Models;
using StorageLens.ViewModels;
using Windows.System;

namespace StorageLens.Views;

public sealed partial class CleanupCandidatesPage : Page
{
    private bool _hasCompletedCleanup;
    private bool _reviewOpen;

    public CleanupCandidatesPage()
    {
        InitializeComponent();
    }

    public CleanupViewModel ViewModel { get; } = new();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CleanupCandidateReviewRequest request)
        {
            return;
        }

        _hasCompletedCleanup = false;
        FolderCleanupResultInfoBar.IsOpen = false;
        await ViewModel.InitializeCandidateReviewAsync(request);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelCleanupCommand.Execute(null);
        ViewModel.CancelCandidateLoading();
        base.OnNavigatedFrom(e);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.BackStack.LastOrDefault()?.SourcePageType == typeof(CleanupPage))
        {
            Frame.GoBack();
            return;
        }

        Frame.Navigate(
            typeof(CleanupPage),
            new CleanupNavigationRequest
            {
                Refresh = _hasCompletedCleanup,
            });
    }

    private async void RecycleFolderFilesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_reviewOpen || !ViewModel.CanRecycleActiveFolder)
        {
            return;
        }

        _reviewOpen = true;
        FolderCleanupResultInfoBar.IsOpen = false;
        try
        {
            FolderCleanupReviewDialog.XamlRoot = XamlRoot;
            await FolderCleanupReviewDialog.ShowAsync();
        }
        finally
        {
            _reviewOpen = false;
        }
    }

    private async void FolderCleanupReviewDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (ViewModel.IsCleaning)
        {
            return;
        }

        var deferral = args.GetDeferral();
        CleanupResult? result = null;
        _hasCompletedCleanup = true;
        try
        {
            result = await ViewModel.RecycleActiveFolderAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FolderCleanupResultInfoBar.Title = AppText.Get(
                "CleanupResultPartialTitle");
            FolderCleanupResultInfoBar.Message = exception.Message;
            FolderCleanupResultInfoBar.Severity = InfoBarSeverity.Error;
            FolderCleanupResultInfoBar.IsOpen = true;
        }
        finally
        {
            deferral.Complete();
        }

        FolderCleanupReviewDialog.Hide();
        if (result is not null)
        {
            FolderCleanupResultInfoBar.Title = AppText.Get(
                result.SucceededCount == 0
                    ? "CleanupResultNoChangesTitle"
                    : result.FailedCount == 0
                        ? "CleanupResultSuccessTitle"
                        : "CleanupResultPartialTitle");
            FolderCleanupResultInfoBar.Message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AppText.Get("CleanupResultSummaryFormat"),
                result.SucceededCount,
                result.FailedCount,
                result.SkippedCount,
                StorageFormatter.FormatBytes(result.BytesRecycled));
            if (result.Failures.Count > 0)
            {
                FolderCleanupResultInfoBar.Message +=
                    Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        result.Failures.Take(5).Select(item =>
                            $"{item.Path}: {item.Error}"));
            }

            FolderCleanupResultInfoBar.Severity =
                result.FailedCount == 0 && result.SucceededCount > 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning;
            FolderCleanupResultInfoBar.IsOpen = true;
        }
    }

    private void FolderCleanupReviewDialog_Closing(
        ContentDialog sender,
        ContentDialogClosingEventArgs args)
    {
        args.Cancel = ViewModel.IsCleaning;
    }

    private async void CleanupCandidateTreeView_SelectionChanged(
        TreeView sender,
        TreeViewSelectionChangedEventArgs args)
    {
        if (args.AddedItems.FirstOrDefault() is
            CleanupCandidateFolderNode folder)
        {
            await ViewModel.SelectCandidateFolderAsync(folder);
        }
    }

    private void CleanupCandidateOpenButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var accessibleName = element is null
            ? string.Empty
            : AutomationProperties.GetName(element);
        var candidate =
            ViewModel.ActiveCandidates?.FirstOrDefault(item =>
                string.Equals(
                    item.AccessibleSummary,
                    accessibleName,
                    StringComparison.Ordinal));
        candidate ??= element?.Tag is string path
            ? ViewModel.ActiveCandidates?.FirstOrDefault(item =>
                string.Equals(
                    item.Item.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase))
            : element?.DataContext as CleanupCandidate;
        if (candidate is null)
        {
            return;
        }

        Frame.Navigate(
            typeof(ExplorerPage),
            new ExplorerNavigationRequest
            {
                DriveLetter = candidate.Item.Path.Length >= 2
                    ? candidate.Item.Path[..2]
                    : null,
                FolderPath = candidate.Item.IsFolder
                    ? candidate.Item.Path
                    : StoragePath.GetParent(candidate.Item.Path),
                SelectedItemPath = candidate.Item.Path,
            });
    }

    private async void OpenStorageSenseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var launched = await Launcher.LaunchUriAsync(
            new Uri("ms-settings:storagesense"));
        if (!launched)
        {
            launched = await Launcher.LaunchUriAsync(
                new Uri("ms-settings:storage"));
        }

        if (!launched)
        {
            ViewModel.ReportCandidateError(
                AppText.Get("ManagedCleanupLaunchFailed"));
        }
    }
}
