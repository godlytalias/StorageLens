using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using StorageLens.Models;
using StorageLens.ViewModels;

namespace StorageLens.Views;

public sealed partial class AutomationPage : Page
{
    public AutomationPage()
    {
        InitializeComponent();
    }

    public AutomationViewModel ViewModel { get; } = new();

    private async void AddScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginNewSchedule();
        ScheduleValidationInfoBar.IsOpen = false;
        ScheduleDialog.XamlRoot = XamlRoot;
        await ScheduleDialog.ShowAsync();
    }

    private async void EditScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var accessibleName = element is null
            ? string.Empty
            : AutomationProperties.GetName(element);
        var task = ViewModel.CleanupSchedules.FirstOrDefault(item =>
            string.Equals(
                item.Name,
                accessibleName,
                StringComparison.Ordinal));
        task ??= element?.Tag is Guid id
            ? ViewModel.CleanupSchedules.FirstOrDefault(item =>
                item.Id == id)
            : element?.DataContext as ScheduledCleanupTask;
        if (task is null)
        {
            return;
        }

        ViewModel.BeginEditSchedule(task);
        ScheduleValidationInfoBar.IsOpen = false;
        ScheduleDialog.XamlRoot = XamlRoot;
        await ScheduleDialog.ShowAsync();
    }

    private void DeleteScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        var accessibleName = element is null
            ? string.Empty
            : AutomationProperties.GetName(element);
        var task = ViewModel.CleanupSchedules.FirstOrDefault(item =>
            string.Equals(
                item.Name,
                accessibleName,
                StringComparison.Ordinal));
        task ??= element?.Tag is Guid id
            ? ViewModel.CleanupSchedules.FirstOrDefault(item =>
                item.Id == id)
            : element?.DataContext as ScheduledCleanupTask;
        if (task is not null)
        {
            ViewModel.DeleteScheduleCommand.Execute(task);
        }
    }

    private void ScheduleDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ViewModel.SaveSchedule())
        {
            args.Cancel = true;
            ScheduleValidationInfoBar.IsOpen = true;
        }
    }
}
