using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageLens.Localization;
using StorageLens.Models;
using StorageLens.Services;

namespace StorageLens.ViewModels;

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DriveSummary _selectedDrive;
    private bool _isScanning;
    private bool _hasScanData;
    private double _scanProgress;
    private string _scanStatus = AppText.Get("OverviewNotScanned");
    private string? _errorMessage;
    private long _cleanupPotentialBytes;
    private int _minimumTrackedFolderSizeGb = 1;
    private string _coverageStatus = string.Empty;
    private CancellationTokenSource? _scanCancellation;
    private bool _initialized;

    public OverviewViewModel()
    {
        Drives = new ObservableCollection<DriveSummary>(DriveDiscoveryService.GetAvailableDrives());
        Categories = [];
        TopFolders = [];
        LargestFiles = [];
        CleanupSuggestions = [];
        GrowingFolders = [];
        GrowingFolders.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasNoGrowingFolders));
        ScanIssues = [];
        _selectedDrive = Drives.FirstOrDefault() ?? throw new InvalidOperationException(
            "No fixed or removable drives are available.");
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
    }

    public ObservableCollection<DriveSummary> Drives { get; }

    public ObservableCollection<StorageCategory> Categories { get; }

    public ObservableCollection<StorageFolderNode> TopFolders { get; }

    public ObservableCollection<StorageItem> LargestFiles { get; }

    public ObservableCollection<CleanupRecommendation> CleanupSuggestions { get; }

    public ObservableCollection<FolderGrowthInsight> GrowingFolders { get; }

    public bool HasNoGrowingFolders => GrowingFolders.Count == 0;

    public ObservableCollection<ScanIssue> ScanIssues { get; }

    public string CoverageStatus
    {
        get => _coverageStatus;
        private set => SetProperty(ref _coverageStatus, value);
    }

    public bool HasCoverageStatus => !string.IsNullOrWhiteSpace(CoverageStatus);

    public DriveSummary SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (value is not null && SetProperty(ref _selectedDrive, value))
            {
                OnPropertyChanged(nameof(UsedStorageText));
                OnPropertyChanged(nameof(FreeStorageText));
                OnPropertyChanged(nameof(TotalStorageText));
                OnPropertyChanged(nameof(CleanupPotentialText));
                _ = RefreshDataAsync();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                ScanCommand.NotifyCanExecuteChanged();
                CancelScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasScanData
    {
        get => _hasScanData;
        private set
        {
            if (SetProperty(ref _hasScanData, value))
            {
                OnPropertyChanged(nameof(NeedsFirstScan));
            }
        }
    }

    public bool NeedsFirstScan => !HasScanData;

    public double ScanProgress
    {
        get => _scanProgress;
        private set => SetProperty(ref _scanProgress, value);
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
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

    public string UsedStorageText => StorageFormatter.FormatBytes(SelectedDrive.UsedBytes);

    public string FreeStorageText => StorageFormatter.FormatBytes(SelectedDrive.FreeBytes);

    public string TotalStorageText => StorageFormatter.FormatBytes(SelectedDrive.TotalBytes);

    public string CleanupPotentialText => StorageFormatter.FormatBytes(_cleanupPotentialBytes);

    public string GrowthTrackingThresholdText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        AppText.Get("GrowthTrackingThresholdFormat"),
        _minimumTrackedFolderSizeGb);

    public IAsyncRelayCommand ScanCommand { get; }

    public IRelayCommand CancelScanCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await AppServices.InitializeAsync();
        await RefreshDataAsync();
        if (AppPreferencesService.Load().ScanOnLaunch)
        {
            await ScanAsync();
        }
    }

    public async Task RefreshDataAsync()
    {
        if (AppServices.Repository is null)
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            ErrorMessage = null;
            var scan = await Task.Run(() =>
                AppServices.Repository.GetLatestCompletedScanAsync(SelectedDrive.Letter));
            if (scan is null)
            {
                ClearIndexedData();
                HasScanData = false;
                ScanStatus = AppText.Get("OverviewNotScanned");
                return;
            }

            HasScanData = true;
            ScanStatus = AppText.Get("OverviewScanSummaryFormat")
                .Replace("{0}", scan.CompletedUtc?.ToLocalTime().ToString("g") ?? string.Empty, StringComparison.Ordinal)
                .Replace(
                    "{1}",
                    scan.FilesScanned.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                    StringComparison.Ordinal);
            ScanProgress = 1;

            var preferences = AppPreferencesService.Load();
            var categoriesTask = Task.Run(() =>
                AppServices.Repository.GetCategoriesAsync(scan.Id));
            var topFoldersTask = Task.Run(() =>
                AppServices.Repository.GetTopFoldersAsync(
                    scan.Id,
                    includeSystem: preferences.ShowSystemItems,
                    includeHidden: preferences.ShowHiddenItems));
            var largestFilesTask = Task.Run(() =>
                AppServices.Repository.GetLargestFilesAsync(
                    scan.Id,
                    includeSystem: preferences.ShowSystemItems,
                    includeHidden: preferences.ShowHiddenItems,
                    limit: 8));
            var recommendationsTask = Task.Run(() =>
                AppServices.CleanupCandidates.GetRecommendationsAsync(
                    SelectedDrive,
                    DateTimeOffset.Now));
            var growthTask = RefreshGrowthInsightsAsync(scan.Id);
            var issuesTask = Task.Run(() =>
                AppServices.Repository.GetScanIssuesAsync(scan.Id));

            await Task.WhenAll(
                categoriesTask,
                topFoldersTask,
                largestFilesTask,
                recommendationsTask,
                growthTask,
                issuesTask);

            Categories.Clear();
            foreach (var category in await categoriesTask)
            {
                Categories.Add(category);
            }

            TopFolders.Clear();
            foreach (var folder in await topFoldersTask)
            {
                TopFolders.Add(folder);
            }

            LargestFiles.Clear();
            foreach (var item in await largestFilesTask)
            {
                LargestFiles.Add(item);
            }

            var recommendations = await recommendationsTask;
            _cleanupPotentialBytes = recommendations
                .Where(item => item.ItemCount > 0)
                .Sum(item => item.ReclaimableBytes);
            OnPropertyChanged(nameof(CleanupPotentialText));

            CleanupSuggestions.Clear();
            var suggestions = preferences.SmartCleanupSuggestions
                ? recommendations
                    .Where(item => item.ItemCount > 0)
                    .Take(3)
                : [];
            foreach (var recommendation in suggestions)
            {
                CleanupSuggestions.Add(recommendation);
            }

            var issues = await issuesTask;
            ScanIssues.Clear();
            foreach (var issue in issues)
            {
                ScanIssues.Add(issue);
            }

            var indexedBytes = scan.AllocatedBytesScanned > 0
                ? scan.AllocatedBytesScanned
                : scan.BytesScanned;
            var unattributed = Math.Max(
                0,
                SelectedDrive.UsedBytes - indexedBytes);
            CoverageStatus = AppText.Get(
                    scan.ScanFormat >= 4 &&
                    scan.AllocatedBytesScanned > 0
                        ? "OverviewCoverageFormat"
                        : "OverviewLegacyCoverageFormat")
                .Replace(
                    "{0}",
                    StorageFormatter.FormatBytes(indexedBytes),
                    StringComparison.Ordinal)
                .Replace(
                    "{1}",
                    StorageFormatter.FormatBytes(unattributed),
                    StringComparison.Ordinal)
                .Replace(
                    "{2}",
                    issues.Count.ToString(
                        System.Globalization.CultureInfo.CurrentCulture),
                    StringComparison.Ordinal);
            OnPropertyChanged(nameof(HasCoverageStatus));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task RefreshGrowthInsightsAsync(Guid? scanId = null)
    {
        try
        {
            _minimumTrackedFolderSizeGb = Math.Max(
                1,
                AutomationSettingsService.Load().MinimumTrackedFolderSizeGb);
            var minimumTrackedBytes = _minimumTrackedFolderSizeGb * 1024L * 1024 * 1024;
            GrowingFolders.Clear();
            ScanIssues.Clear();
            CoverageStatus = string.Empty;
            OnPropertyChanged(nameof(HasCoverageStatus));
            foreach (var insight in await Task.Run(() =>
                             AppServices.Repository.GetGrowingFoldersAsync(
                                 SelectedDrive.Letter,
                                 minimumTrackedBytes)))
            {
                GrowingFolders.Add(insight);
            }

            OnPropertyChanged(nameof(GrowthTrackingThresholdText));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        ErrorMessage = null;
        IsScanning = true;
        ScanProgress = 0;
        ScanStatus = AppText.Get("ScanningDriveFormat")
            .Replace("{0}", SelectedDrive.Letter, StringComparison.Ordinal);
        _scanCancellation = new CancellationTokenSource();

        var progress = new Progress<StorageScanProgress>(update =>
        {
            ScanProgress = update.PercentComplete ?? ScanProgress;
            ScanStatus = AppText.Get("ScanProgressFormat")
                .Replace("{0}", update.FilesScanned.ToString("N0"), StringComparison.Ordinal)
                .Replace("{1}", StorageFormatter.FormatBytes(update.BytesScanned), StringComparison.Ordinal)
                .Replace("{2}", update.InaccessibleItems.ToString("N0"), StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(update.CurrentPath))
            {
                ScanStatus =
                    $"{StorageText.ScanPhase(update.Phase)} · {update.CurrentPath} · {ScanStatus}";
            }
            if (update.ItemsPerSecond > 0)
            {
                ScanStatus +=
                    " · " + string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        AppText.Get("ScanRateFormat"),
                        update.ItemsPerSecond);
            }
        });

        try
        {
            var drive = RefreshDriveSummary(SelectedDrive);
            var result = await Task.Run(
                () => AppServices.Scanner.ScanDriveAsync(
                    drive,
                    progress,
                    _scanCancellation.Token));
            switch (result.Status)
            {
                case StorageScanStatus.Completed:
                    await RefreshDataAsync();
                    ScanStatus = string.IsNullOrWhiteSpace(result.WarningMessage)
                        ? AppText.Get("ScanCompletedFormat")
                            .Replace("{0}", result.FilesScanned.ToString("N0"), StringComparison.Ordinal)
                            .Replace(
                                "{1}",
                                result.InaccessibleItems.ToString("N0"),
                                StringComparison.Ordinal)
                        : result.WarningMessage;
                    break;
                case StorageScanStatus.Cancelled:
                    ScanStatus = AppText.Get("ScanCancelled");
                    break;
                default:
                    ErrorMessage = result.ErrorMessage ?? AppText.Get("ScanFailed");
                    ScanStatus = AppText.Get("ScanFailed");
                    break;
            }
        }
        finally
        {
            IsScanning = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
        }
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        ScanStatus = AppText.Get("CancellingScan");
    }

    private void ClearIndexedData()
    {
        Categories.Clear();
        TopFolders.Clear();
        LargestFiles.Clear();
        CleanupSuggestions.Clear();
        GrowingFolders.Clear();
        _cleanupPotentialBytes = 0;
        OnPropertyChanged(nameof(CleanupPotentialText));
    }

    private static DriveSummary RefreshDriveSummary(DriveSummary drive)
    {
        var refreshed = DriveDiscoveryService.GetAvailableDrives()
            .FirstOrDefault(item => string.Equals(item.Letter, drive.Letter, StringComparison.OrdinalIgnoreCase));
        return refreshed ?? drive;
    }
}
