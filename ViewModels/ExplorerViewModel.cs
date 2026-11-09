using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageLens.Localization;
using StorageLens.Models;
using StorageLens.Services;

namespace StorageLens.ViewModels;

public sealed class ExplorerViewModel : ObservableObject
{
    private static readonly IReadOnlyList<string> CategoryValues =
    [
        "All items",
        "Apps & games",
        "Videos",
        "Photos",
        "System",
        "Documents",
        "Cache & temporary",
        "Other",
    ];
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _publishedScanRefreshLock = new(1, 1);
    private readonly Dictionary<StorageFolderNode, Task> _folderLoadTasks = [];
    private DriveSummary _selectedDrive;
    private int _selectedDriveIndex;
    private StorageScanRun? _scan;
    private string _searchText = string.Empty;
    private int _selectedCategoryIndex;
    private int _selectedSortIndex;
    private StorageFolderNode? _selectedFolder;
    private StorageItem? _selectedItem;
    private string _currentPath = string.Empty;
    private bool _scopeAllDrive;
    private bool _isBusy;
    private string? _errorMessage;
    private CancellationTokenSource? _queryCancellation;
    private CancellationTokenSource? _driveLoadCancellation;
    private CancellationTokenSource? _filterCancellation;
    private bool _isSuspended;
    private bool _driveDataLoaded;
    private int _queryGeneration;
    private int _driveLoadGeneration;

    public ExplorerViewModel()
    {
        Drives = new ObservableCollection<DriveSummary>(DriveDiscoveryService.GetAvailableDrives());
        FolderTree = [];
        Categories =
        [
            AppText.Get("ExplorerAllItems"),
            StorageText.Category("Apps & games"),
            StorageText.Category("Videos"),
            StorageText.Category("Photos"),
            StorageText.Category("System"),
            StorageText.Category("Documents"),
            StorageText.Category("Cache & temporary"),
            StorageText.Category("Other"),
        ];
        SortOptions =
        [
            AppText.Get("ExplorerLargestFirst"),
            AppText.Get("ExplorerRecentlyModified"),
            AppText.Get("ExplorerSortName"),
        ];
        FilteredItems = [];
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
        NavigateUpCommand = new AsyncRelayCommand(NavigateUpAsync, () => CanNavigateUp);
        RevealSelectedCommand = new RelayCommand(RevealSelectedItem, () => SelectedItem is not null);
        _selectedDriveIndex = Drives.Count > 0 ? 0 : -1;
        _selectedDrive = _selectedDriveIndex >= 0
            ? Drives[_selectedDriveIndex]
            : DriveSummary.Unavailable();
    }

    public ObservableCollection<DriveSummary> Drives { get; }

    public ObservableCollection<StorageFolderNode> FolderTree { get; }

    public ObservableCollection<string> Categories { get; }

    public ObservableCollection<string> SortOptions { get; }

    public ObservableCollection<StorageItem> FilteredItems { get; }

    public DriveSummary SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (value is not null)
            {
                SelectDrive(value);
            }
        }
    }

    public int SelectedDriveIndex
    {
        get => _selectedDriveIndex;
        set
        {
            if (value >= 0 && value < Drives.Count)
            {
                SelectDrive(Drives[value]);
            }
        }
    }

    public bool HasAvailableDrive => SelectedDrive.IsAvailable;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                DebounceRefresh();
            }
        }
    }

    public int SelectedCategoryIndex
    {
        get => _selectedCategoryIndex;
        set
        {
            if (SetProperty(ref _selectedCategoryIndex, value))
            {
                _scopeAllDrive = value > 0;
                DebounceRefresh();
            }
        }
    }

    public int SelectedSortIndex
    {
        get => _selectedSortIndex;
        set
        {
            if (SetProperty(ref _selectedSortIndex, value))
            {
                DebounceRefresh();
            }
        }
    }

    public StorageFolderNode? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (value is not null && SetProperty(ref _selectedFolder, value))
            {
                _ = SelectFolderPathAsync(value.Path);
            }
        }
    }

    public StorageItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(CanOpenSelectedItem));
                OnPropertyChanged(nameof(SelectedItemNameText));
                OnPropertyChanged(nameof(SelectedItemPathText));
                RevealSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CurrentPath => _currentPath;

    public string LocationText => HasAvailableDrive
        ? _scopeAllDrive
            ? string.IsNullOrWhiteSpace(SearchText)
                ? $"{StorageText.IsolatePath(SelectedDrive.Letter + "\\")} / " +
                  Categories[SelectedCategoryIndex]
                : $"{StorageText.IsolatePath(SelectedDrive.Letter + "\\")} / " +
                  SearchText
            : StorageText.IsolatePath(_currentPath)
        : SelectedDrive.DisplayName;

    public string SelectedItemNameText =>
        SelectedItem?.Name ?? AppText.Get("ExplorerNoItemSelected");

    public string SelectedItemPathText =>
        SelectedItem is null
            ? AppText.Get("ExplorerSelectItemHint")
            : SelectedItem.DisplayPath;

    public bool CanNavigateUp => HasAvailableDrive &&
        !_scopeAllDrive &&
        !string.Equals(
            _currentPath,
            $@"{SelectedDrive.Letter}\",
            StringComparison.OrdinalIgnoreCase);

    public bool CanOpenSelectedItem => SelectedItem?.IsFolder == true;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasScanData => _scan is not null;

    public bool HasResults => FilteredItems.Count > 0;

    public int ResultCount => FilteredItems.Count;

    public IAsyncRelayCommand ClearFiltersCommand { get; }

    public IAsyncRelayCommand NavigateUpCommand { get; }

    public IRelayCommand RevealSelectedCommand { get; }

    public async Task InitializeAsync()
    {
        await AppServices.InitializeAsync();
        _isSuspended = false;
        if (HasAvailableDrive)
        {
            await RefreshPublishedScanAsync();
        }
    }

    public async Task RefreshPublishedScanAsync()
    {
        if (_isSuspended || !HasAvailableDrive)
        {
            return;
        }

        if (!await _publishedScanRefreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var drive = SelectedDrive;
            var scan = await Task.Run(() =>
                AppServices.Repository.GetLatestCompletedScanAsync(
                    drive.Letter));
            if (_isSuspended ||
                !ReferenceEquals(drive, SelectedDrive))
            {
                return;
            }

            if (_driveDataLoaded &&
                _scan?.Id == scan?.Id &&
                _scan?.CompletedUtc == scan?.CompletedUtc)
            {
                return;
            }

            await LoadSelectedDriveAsync(
                scan,
                preserveNavigation: _driveDataLoaded);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_isSuspended)
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            _publishedScanRefreshLock.Release();
        }
    }

    public void Suspend()
    {
        _isSuspended = true;
        Interlocked.Increment(ref _queryGeneration);
        Interlocked.Increment(ref _driveLoadGeneration);
        _queryCancellation?.Cancel();
        _driveLoadCancellation?.Cancel();
        _filterCancellation?.Cancel();
        IsBusy = false;
    }

    public void ReportNavigationError(Exception exception) =>
        ErrorMessage = exception.Message;

    public async Task OpenRequestAsync(ExplorerNavigationRequest request)
    {
        await AppServices.InitializeAsync();
        _isSuspended = false;
        _filterCancellation?.Cancel();
        if (!string.IsNullOrWhiteSpace(request.DriveLetter))
        {
            var drive = Drives.FirstOrDefault(item =>
                string.Equals(item.Letter, request.DriveLetter, StringComparison.OrdinalIgnoreCase));
            if (drive is not null &&
                !string.Equals(drive.Letter, SelectedDrive.Letter, StringComparison.OrdinalIgnoreCase))
            {
                SelectDrive(drive, load: false);
            }
        }

        await RefreshPublishedScanAsync();

        SetProperty(
            ref _searchText,
            request.SearchText ?? string.Empty,
            nameof(SearchText));
        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            var categoryIndex = CategoryValues
                .Select((category, index) => (category, index))
                .FirstOrDefault(item =>
                    string.Equals(item.category, request.Category, StringComparison.OrdinalIgnoreCase))
                .index;
            SetProperty(
                ref _selectedCategoryIndex,
                categoryIndex,
                nameof(SelectedCategoryIndex));
            _scopeAllDrive = true;
        }
        else if (!string.IsNullOrWhiteSpace(request.FolderPath))
        {
            SetProperty(
                ref _selectedCategoryIndex,
                0,
                nameof(SelectedCategoryIndex));
            await SelectFolderPathAsync(request.FolderPath);
        }
        else if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            _scopeAllDrive = true;
        }

        await RefreshItemsAsync(request.SelectedItemPath);
    }

    public async Task OpenItemAsync(StorageItem item)
    {
        if (item.IsFolder)
        {
            await SelectFolderPathAsync(item.Path);
        }
        else
        {
            SelectedItem = item;
        }
    }

    public IReadOnlyList<StorageFolderNode> GetFolderPathNodes(
        string path)
    {
        var chain = new List<StorageFolderNode>();
        FindFolderPath(FolderTree, path, chain);
        return chain;
    }

    public async Task SelectFolderPathAsync(string path)
    {
        _isSuspended = false;
        try
        {
            _scopeAllDrive = false;
            _filterCancellation?.Cancel();
            SetProperty(
                ref _selectedCategoryIndex,
                0,
                nameof(SelectedCategoryIndex));
            _currentPath = StorageRepository.NormalizeFolderPath(path);
            _selectedFolder =
                await SynchronizeFolderTreeAsync(_currentPath) ??
                FindFolderNode(FolderTree, _currentPath);
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(SelectedFolder));
            NotifyLocationState();
            await RefreshItemsAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    public async Task LoadFolderChildrenAsync(StorageFolderNode folder)
    {
        if (_folderLoadTasks.TryGetValue(folder, out var existingTask))
        {
            await existingTask;
            return;
        }

        var loadTask = LoadFolderChildrenCoreAsync(folder);
        _folderLoadTasks[folder] = loadTask;
        try
        {
            await loadTask;
        }
        finally
        {
            _folderLoadTasks.Remove(folder);
        }
    }

    private async Task LoadFolderChildrenCoreAsync(StorageFolderNode folder)
    {
        try
        {
            if (_scan is null ||
                folder.Children.Count > 0 ||
                !folder.HasChildren)
            {
                return;
            }

            var preferences = AppPreferencesService.Load();
            var children = await Task.Run(() =>
                AppServices.Repository.GetFolderChildrenAsync(
                    _scan.Id,
                    folder.Path,
                    preferences.ShowSystemItems,
                    preferences.ShowHiddenItems));
            foreach (var child in children)
            {
                if (folder.Children.All(existing =>
                        !string.Equals(
                            existing.Path,
                            child.Path,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    folder.Children.Add(child);
                }
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task<StorageFolderNode?> SynchronizeFolderTreeAsync(
        string targetPath)
    {
        if (FolderTree.Count == 0)
        {
            return null;
        }

        ClearSelectedNodes(FolderTree);
        var root = FolderTree.FirstOrDefault();
        if (root is null)
        {
            return null;
        }

        var ancestorPaths = new Stack<string>();
        var cursor = targetPath;
        while (!string.Equals(
                   cursor,
                   root.Path,
                   StringComparison.OrdinalIgnoreCase))
        {
            ancestorPaths.Push(cursor);
            var parent = StoragePath.GetParent(cursor);
            if (string.Equals(
                    parent,
                    cursor,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            cursor = parent;
        }

        StorageFolderNode current = root;
        current.IsExpanded = true;
        while (ancestorPaths.TryPop(out var desiredPath))
        {
            var child = current.Children.FirstOrDefault(item =>
                string.Equals(
                    item.Path,
                    desiredPath,
                    StringComparison.OrdinalIgnoreCase));
            if (child is null &&
                _scan is not null)
            {
                child = await AppServices.Repository.GetFolderNodeAsync(
                    _scan.Id,
                    desiredPath);
                if (child is not null)
                {
                    current.Children.Add(child);
                }
            }

            foreach (var sibling in current.Children)
            {
                sibling.IsExpanded = false;
                sibling.IsSelected = false;
                if (!ReferenceEquals(sibling, child))
                {
                    CollapseBranch(sibling.Children);
                }
            }

            if (child is null)
            {
                return null;
            }

            child.IsExpanded = true;
            current = child;
        }

        current.IsSelected = true;
        current.IsExpanded = current.HasChildren;
        return current;
    }

    private static void ClearSelectedNodes(
        IEnumerable<StorageFolderNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsSelected = false;
            ClearSelectedNodes(node.Children);
        }
    }

    private static void CollapseBranch(
        IEnumerable<StorageFolderNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = false;
            node.IsSelected = false;
            CollapseBranch(node.Children);
        }
    }

    private async Task LoadSelectedDriveAsync(
        StorageScanRun? publishedScan = null,
        bool preserveNavigation = false)
    {
        if (!HasAvailableDrive)
        {
            _scan = null;
            FolderTree.Clear();
            FilteredItems.Clear();
            _currentPath = string.Empty;
            OnPropertyChanged(nameof(HasScanData));
            NotifyResults();
            NotifyLocationState();
            return;
        }

        var generation =
            Interlocked.Increment(ref _driveLoadGeneration);
        _driveLoadCancellation?.Cancel();
        _driveLoadCancellation?.Dispose();
        var loadCancellation = new CancellationTokenSource();
        _driveLoadCancellation = loadCancellation;
        var token = loadCancellation.Token;
        IsBusy = true;
        ErrorMessage = null;
        _driveDataLoaded = false;
        try
        {
            var drive = SelectedDrive;
            var previousPath = preserveNavigation ? _currentPath : null;
            var previousSelectedItemPath =
                preserveNavigation ? SelectedItem?.Path : null;
            var scan = publishedScan ?? await Task.Run(() =>
                AppServices.Repository.GetLatestCompletedScanAsync(
                    drive.Letter,
                    token));
            token.ThrowIfCancellationRequested();
            if (!IsCurrentDriveLoad(
                    generation,
                    drive.Letter,
                    token))
            {
                return;
            }

            if (scan is null)
            {
                _scan = null;
                OnPropertyChanged(nameof(HasScanData));
                FolderTree.Clear();
                FilteredItems.Clear();
                NotifyResults();
                _currentPath = $@"{drive.Letter}\";
                NotifyLocationState();
                _driveDataLoaded = true;
                return;
            }

            var root = new StorageFolderNode
            {
                Name = drive.DisplayName,
                Path = $@"{drive.Letter}\",
                Tag = "Drive",
                SizeBytes = scan.BytesScanned,
                HasChildren = true,
            };
            var preferences = AppPreferencesService.Load();
            var children = await Task.Run(() =>
                AppServices.Repository.GetFolderChildrenAsync(
                    scan.Id,
                    root.Path,
                    preferences.ShowSystemItems,
                    preferences.ShowHiddenItems,
                    cancellationToken: token));
            token.ThrowIfCancellationRequested();
            if (!IsCurrentDriveLoad(
                    generation,
                    drive.Letter,
                    token))
            {
                return;
            }

            foreach (var child in children)
            {
                root.Children.Add(child);
            }

            _scan = scan;
            OnPropertyChanged(nameof(HasScanData));
            FolderTree.Clear();
            FolderTree.Add(root);
            StorageFolderNode? selectedFolder = null;
            if (!string.IsNullOrWhiteSpace(previousPath))
            {
                selectedFolder =
                    await SynchronizeFolderTreeAsync(previousPath);
            }

            if (selectedFolder is null)
            {
                ClearSelectedNodes(FolderTree);
                root.IsExpanded = true;
                root.IsSelected = true;
                selectedFolder = root;
            }

            _selectedFolder = selectedFolder;
            _currentPath = selectedFolder.Path;
            OnPropertyChanged(nameof(SelectedFolder));
            NotifyLocationState();
            await RefreshItemsAsync(previousSelectedItemPath);
            _driveDataLoaded = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            if (generation == _driveLoadGeneration &&
                !_isSuspended)
            {
                IsBusy = false;
            }

            if (ReferenceEquals(_driveLoadCancellation, loadCancellation))
            {
                _driveLoadCancellation = null;
            }

            loadCancellation.Dispose();
        }
    }

    private bool IsCurrentDriveLoad(
        int generation,
        string driveLetter,
        CancellationToken cancellationToken) =>
        generation == _driveLoadGeneration &&
        !cancellationToken.IsCancellationRequested &&
        !_isSuspended &&
        HasAvailableDrive &&
        string.Equals(
            driveLetter,
            SelectedDrive.Letter,
            StringComparison.OrdinalIgnoreCase);

    private async Task RefreshItemsAsync(string? selectedItemPath = null)
    {
        if (_scan is null || _isSuspended)
        {
            FilteredItems.Clear();
            NotifyResults();
            return;
        }

        var generation = Interlocked.Increment(ref _queryGeneration);
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        var queryCancellation = new CancellationTokenSource();
        _queryCancellation = queryCancellation;
        var token = queryCancellation.Token;
        IsBusy = true;
        ErrorMessage = null;

        var lockAcquired = false;
        try
        {
            await _loadLock.WaitAsync(token);
            lockAcquired = true;
            var scan = _scan;
            if (scan is null)
            {
                return;
            }

            var path = _currentPath;
            var search = SearchText;
            var sort = SelectedSortIndex;
            var scopeAllDrive = _scopeAllDrive;
            var preferences = AppPreferencesService.Load();
            var category = SelectedCategoryIndex > 0
                ? CategoryValues[SelectedCategoryIndex]
                : null;
            var previousSelection = selectedItemPath ?? SelectedItem?.Path;
            IReadOnlyList<StorageItem> items;
            if (scopeAllDrive)
            {
                items = await Task.Run(() =>
                    AppServices.Repository.SearchAsync(
                        scan.Id,
                        search,
                        category,
                        sort,
                        includeSystem: preferences.ShowSystemItems,
                        includeHidden: preferences.ShowHiddenItems,
                        cancellationToken: token));
            }
            else if (!string.IsNullOrWhiteSpace(selectedItemPath) &&
                     string.IsNullOrWhiteSpace(search) &&
                     category is null)
            {
                items = await Task.Run(() =>
                    AppServices.Repository.GetChildrenForNavigationAsync(
                        scan.Id,
                        path,
                        selectedItemPath,
                        sort,
                        preferences.ShowSystemItems,
                        preferences.ShowHiddenItems,
                        cancellationToken: token));
            }
            else
            {
                items = await Task.Run(() =>
                    AppServices.Repository.GetChildrenAsync(
                        scan.Id,
                        path,
                        category,
                        search,
                        sort,
                        includeSystem: preferences.ShowSystemItems,
                        includeHidden: preferences.ShowHiddenItems,
                        cancellationToken: token));
            }

            token.ThrowIfCancellationRequested();
            if (generation != _queryGeneration || _isSuspended)
            {
                return;
            }

            FilteredItems.Clear();
            foreach (var item in items)
            {
                FilteredItems.Add(item);
            }

            SelectedItem = previousSelection is not null
                ? FilteredItems.FirstOrDefault(item =>
                    string.Equals(item.Path, previousSelection, StringComparison.OrdinalIgnoreCase))
                  ?? FilteredItems.FirstOrDefault()
                : FilteredItems.FirstOrDefault();
            NotifyResults();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            if (lockAcquired)
            {
                _loadLock.Release();
            }

            if (generation == _queryGeneration &&
                !token.IsCancellationRequested)
            {
                IsBusy = false;
            }
            else if (_isSuspended &&
                     generation == _queryGeneration)
            {
                IsBusy = false;
            }

            if (ReferenceEquals(_queryCancellation, queryCancellation))
            {
                _queryCancellation = null;
            }

            queryCancellation.Dispose();
        }
    }

    private async Task ClearFiltersAsync()
    {
        SearchText = string.Empty;
        SelectedCategoryIndex = 0;
        SelectedSortIndex = 0;
        if (HasAvailableDrive)
        {
            await SelectFolderPathAsync($@"{SelectedDrive.Letter}\");
        }
    }

    private async Task NavigateUpAsync()
    {
        if (!CanNavigateUp)
        {
            return;
        }

        await SelectFolderPathAsync(StoragePath.GetParent(_currentPath));
    }

    private void DebounceRefresh()
    {
        _filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        _ = RefreshItemsAfterDelayAsync(cancellation);
    }

    private void RevealSelectedItem()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            FileExplorerService.Reveal(SelectedItem.Path);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task RefreshItemsAfterDelayAsync(
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(150, cancellation.Token);
            await RefreshItemsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation))
            {
                _filterCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void NotifyResults()
    {
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(LocationText));
    }

    private void NotifyLocationState()
    {
        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(LocationText));
        ((IAsyncRelayCommand)NavigateUpCommand).NotifyCanExecuteChanged();
    }

    private void SelectDrive(DriveSummary drive, bool load = true)
    {
        var index = Drives.IndexOf(drive);
        if (index < 0 || !drive.IsAvailable)
        {
            return;
        }

        var changed = !ReferenceEquals(_selectedDrive, drive);
        _selectedDrive = drive;
        if (_selectedDriveIndex != index)
        {
            _selectedDriveIndex = index;
            OnPropertyChanged(nameof(SelectedDriveIndex));
        }

        if (!changed)
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedDrive));
        OnPropertyChanged(nameof(HasAvailableDrive));
        NotifyLocationState();
        if (load)
        {
            _ = LoadSelectedDriveAsync();
        }
    }

    private static StorageFolderNode? FindFolderNode(
        IEnumerable<StorageFolderNode> nodes,
        string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindFolderNode(node.Children, path);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static bool FindFolderPath(
        IEnumerable<StorageFolderNode> nodes,
        string path,
        List<StorageFolderNode> chain)
    {
        foreach (var node in nodes)
        {
            chain.Add(node);
            if (string.Equals(
                    node.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (FindFolderPath(node.Children, path, chain))
            {
                return true;
            }

            chain.RemoveAt(chain.Count - 1);
        }

        return false;
    }

}

public static class StoragePath
{
    public static string GetParent(string path)
    {
        var fullPath = StorageRepository.NormalizeFolderPath(path);
        var parent = Path.GetDirectoryName(fullPath.TrimEnd('\\'));
        return string.IsNullOrWhiteSpace(parent)
            ? fullPath
            : StorageRepository.NormalizeFolderPath(parent);
    }
}
