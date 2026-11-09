using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StorageLens.Models;
using StorageLens.Services;
using StorageLens.ViewModels;

namespace StorageLens.Views;

public sealed partial class CleanupPage : Page
{
    private bool _closingForCleanup;

    public CleanupPage()
    {
        InitializeComponent();
        Loaded += CleanupPage_Loaded;
    }

    public CleanupViewModel ViewModel { get; } = new();

    private async void ReviewSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        CleanupResultInfoBar.IsOpen = false;
        CleanupReviewDialog.XamlRoot = XamlRoot;
        await CleanupReviewDialog.ShowAsync();
    }

    private string? _pendingRecommendation;
    private bool _refreshPending;
    private IReadOnlyList<CleanupPolicyId> _pendingPolicies = [];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.NavigationMode == NavigationMode.Back)
        {
            _refreshPending = true;
        }
        else if (e.Parameter is CleanupNavigationRequest request)
        {
            _refreshPending |= request.Refresh;
            _pendingRecommendation = request.RecommendationName;
            _pendingPolicies = request.PolicyIds;
        }

        ViewModel.RefreshCandidateSelectionSummary();
        base.OnNavigatedTo(e);
    }

    private void RecommendationDetailsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var accessibleName = element is null
            ? string.Empty
            : AutomationProperties.GetName(element);
        var recommendation = ViewModel.Recommendations.FirstOrDefault(
            item =>
                string.Equals(
                    item.AccessibleSummary,
                    accessibleName,
                    StringComparison.Ordinal) ||
                accessibleName?.StartsWith(
                    item.DisplayName,
                    StringComparison.Ordinal) == true);
        recommendation ??= element?.Tag is CleanupPolicyId policy
            ? ViewModel.Recommendations.FirstOrDefault(item =>
                item.PolicyId == policy)
            : element?.DataContext as CleanupRecommendation;
        if (recommendation is not null)
        {
            ShowRecommendation(recommendation);
        }
    }

    private void ShowRecommendation(CleanupRecommendation recommendation)
    {
        Frame.Navigate(
            typeof(CleanupCandidatesPage),
            ViewModel.CreateCandidateReviewRequest(recommendation));
    }

    private async void CleanupReviewDialog_PrimaryButtonClick(
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
        var wasCancelled = false;
        var cleanup = ViewModel.RecycleSelectedAsync();
        NotificationService.ShowCleanupStarted();
        try
        {
            _closingForCleanup = true;
            CleanupReviewDialog.Hide();
        }
        finally
        {
            _closingForCleanup = false;
            deferral.Complete();
        }

        try
        {
            result = await cleanup;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        catch (Exception exception)
        {
            ShowCleanupError(exception);
            NotificationService.ShowCleanupFailed(exception.Message);
        }

        if (result is not null)
        {
            ShowCleanupResult(result);
            NotificationService.ShowCleanupFinished(result);
        }
        else if (wasCancelled)
        {
            ShowCleanupCancelled();
            NotificationService.ShowCleanupCancelled();
        }

        await ViewModel.RefreshAsync();
    }

    private void CleanupReviewDialog_Closing(
        ContentDialog sender,
        ContentDialogClosingEventArgs args)
    {
        if (ViewModel.IsCleaning && !_closingForCleanup)
        {
            args.Cancel = true;
        }
    }

    private void ShowCleanupResult(CleanupResult result)
    {
        var summary = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StorageLens.Localization.AppText.Get(
                "CleanupResultSummaryFormat"),
            result.SucceededCount,
            result.FailedCount,
            result.SkippedCount,
            StorageFormatter.FormatBytes(result.BytesRecycled));
        var details = result.Failures.Count == 0
            ? StorageLens.Localization.AppText.Get(
                result.SucceededCount == 0 && result.SkippedCount > 0
                    ? "CleanupResultNoChangesDetails"
                    : result.ExcludedCount > 0
                        ? "CleanupResultExcludedDetailsFormat"
                        : "CleanupResultNoFailures")
            : string.Join(
                Environment.NewLine,
                result.Failures
                    .Take(5)
                    .Select(item => $"{item.Path}: {item.Error}"));
        if (result.Failures.Count == 0 &&
            result.ExcludedCount > 0 &&
            result.SkippedCount == 0)
        {
            details = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                details,
                result.ExcludedCount);
        }
        CleanupResultInfoBar.Title =
            StorageLens.Localization.AppText.Get(
                result.SucceededCount == 0
                    ? "CleanupResultNoChangesTitle"
                    : result.FailedCount == 0
                        ? "CleanupResultSuccessTitle"
                        : "CleanupResultPartialTitle");
        CleanupResultInfoBar.Message =
            $"{summary}{Environment.NewLine}{details}";
        CleanupResultInfoBar.Severity =
            result.SucceededCount > 0 && result.FailedCount == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
        CleanupResultInfoBar.IsOpen = true;
    }

    private void ShowCleanupCancelled()
    {
        CleanupResultInfoBar.Title = string.Empty;
        CleanupResultInfoBar.Message =
            StorageLens.Localization.AppText.Get("CleanupCancelled");
        CleanupResultInfoBar.Severity = InfoBarSeverity.Informational;
        CleanupResultInfoBar.IsOpen = true;
    }

    private void ShowCleanupError(Exception exception)
    {
        CleanupResultInfoBar.Title = StorageLens.Localization.AppText.Get(
            "CleanupResultPartialTitle");
        CleanupResultInfoBar.Message = exception.Message;
        CleanupResultInfoBar.Severity = InfoBarSeverity.Error;
        CleanupResultInfoBar.IsOpen = true;
    }

    private async void CleanupPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        if (_refreshPending)
        {
            _refreshPending = false;
            await ViewModel.RefreshAsync();
        }

        if (_pendingPolicies.Count > 0)
        {
            ViewModel.SelectPolicies(_pendingPolicies);
            _pendingPolicies = [];
        }

        if (string.IsNullOrWhiteSpace(_pendingRecommendation))
        {
            return;
        }

        var recommendation = ViewModel.FindRecommendation(_pendingRecommendation);
        _pendingRecommendation = null;
        if (recommendation is not null)
        {
            ShowRecommendation(recommendation);
        }
    }
}
