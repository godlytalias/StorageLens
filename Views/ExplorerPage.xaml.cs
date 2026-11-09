using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Navigation;
using StorageLens.Models;
using StorageLens.ViewModels;

namespace StorageLens.Views;

public sealed partial class ExplorerPage : Page
{
    private static readonly TimeSpan PublishedScanRefreshInterval =
        TimeSpan.FromSeconds(10);
    private int _navigationVersion;
    private DispatcherTimer? _publishedScanRefreshTimer;

    public ExplorerPage()
    {
        InitializeComponent();
    }

    public ExplorerViewModel ViewModel { get; } = new();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var version = ++_navigationVersion;
        StartPublishedScanRefresh();
        _ = HandleNavigationSafelyAsync(
            e.NavigationMode == NavigationMode.New ? e.Parameter : null,
            version);
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ++_navigationVersion;
        _publishedScanRefreshTimer?.Stop();
        ViewModel.Suspend();
        base.OnNavigatedFrom(e);
    }

    private void StartPublishedScanRefresh()
    {
        _publishedScanRefreshTimer ??= new DispatcherTimer
        {
            Interval = PublishedScanRefreshInterval,
        };
        _publishedScanRefreshTimer.Tick -= PublishedScanRefreshTimer_Tick;
        _publishedScanRefreshTimer.Tick += PublishedScanRefreshTimer_Tick;
        _publishedScanRefreshTimer.Start();
    }

    private async void PublishedScanRefreshTimer_Tick(
        object? sender,
        object args)
    {
        await ViewModel.RefreshPublishedScanAsync();
    }

    private async Task HandleNavigationSafelyAsync(
        object? parameter,
        int version)
    {
        try
        {
            await HandleNavigationAsync(parameter, version);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _navigationVersion)
            {
                ViewModel.ReportNavigationError(exception);
            }
        }
    }

    private async Task HandleNavigationAsync(object? parameter, int version)
    {
        if (parameter is string query)
        {
            await ViewModel.InitializeAsync();
            await ViewModel.OpenRequestAsync(new ExplorerNavigationRequest { SearchText = query });
        }
        else if (parameter is ExplorerNavigationRequest request)
        {
            await ViewModel.OpenRequestAsync(request);
        }
        else
        {
            await ViewModel.InitializeAsync();
        }

        if (version == _navigationVersion)
        {
            await BringSelectedFolderIntoViewAsync();
            await BringSelectedItemIntoViewAsync(version);
        }
    }

    private async Task BringSelectedItemIntoViewAsync(int version)
    {
        var selected = ViewModel.SelectedItem;
        if (selected is null)
        {
            return;
        }

        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (version != _navigationVersion ||
                !ReferenceEquals(selected, ViewModel.SelectedItem))
            {
                return;
            }

            if (StorageItemsListView.IsLoaded)
            {
                StorageItemsListView.ScrollIntoView(
                    selected,
                    ScrollIntoViewAlignment.Leading);
                if (StorageItemsListView.ContainerFromItem(selected) is
                    ListViewItem container)
                {
                    container.StartBringIntoView();
                    container.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                    return;
                }
            }

            await Task.Delay(16);
        }
    }

    private async void FolderTreeView_SelectionChanged(
        TreeView sender,
        TreeViewSelectionChangedEventArgs args)
    {
        if (args.AddedItems.FirstOrDefault() is StorageFolderNode folder)
        {
            if (!string.Equals(
                    folder.Path,
                    ViewModel.CurrentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                await ViewModel.SelectFolderPathAsync(folder.Path);
                await BringSelectedFolderIntoViewAsync();
            }
        }
    }

    private void StorageItemsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is StorageItem item)
        {
            AutomationProperties.SetName(args.ItemContainer, $"{item.Name}, {StorageFormatter.FormatBytes(item.SizeBytes)}, {item.Category}");
        }
    }

    private async void StorageItemsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StorageItem item)
        {
            await ViewModel.OpenItemAsync(item);
            if (item.IsFolder)
            {
                await BringSelectedFolderIntoViewAsync();
            }
        }
    }

    private async void OpenSelectedItemButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { IsFolder: true } item)
        {
            await ViewModel.OpenItemAsync(item);
            await BringSelectedFolderIntoViewAsync();
        }
    }

    private async void FolderTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is StorageFolderNode folder)
        {
            await ViewModel.LoadFolderChildrenAsync(folder);
        }
    }

    private async Task BringSelectedFolderIntoViewAsync()
    {
        var version = _navigationVersion;
        if (ViewModel.SelectedFolder is not { } selected)
        {
            return;
        }

        TreeViewItem? selectedContainer = null;
        foreach (var node in ViewModel.GetFolderPathNodes(selected.Path))
        {
            if (version != _navigationVersion)
            {
                return;
            }
            node.IsExpanded = true;
            var container = await WaitForContainerAsync(node);
            if (container is null)
            {
                continue;
            }

            container.IsExpanded = true;
            if (ReferenceEquals(node, selected))
            {
                selectedContainer = container;
            }
        }

        if (version != _navigationVersion)
        {
            return;
        }

        FolderTreeView.SelectedItem = selected;
        selectedContainer ??= await WaitForContainerAsync(selected);
        if (selectedContainer is not null)
        {
            if (version != _navigationVersion)
            {
                return;
            }

            selectedContainer.IsSelected = true;
            selectedContainer.StartBringIntoView();
            selectedContainer.Focus(
                Microsoft.UI.Xaml.FocusState.Programmatic);
        }
    }

    private async Task<TreeViewItem?> WaitForContainerAsync(
        StorageFolderNode node)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (FolderTreeView.ContainerFromItem(node) is
                TreeViewItem container)
            {
                return container;
            }

            await Task.Delay(16);
        }

        return null;
    }
}
