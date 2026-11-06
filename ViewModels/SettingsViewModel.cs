using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageLens.Models;
using StorageLens.Services;
using StorageLens.Localization;

namespace StorageLens.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppPreferences _preferences;
    private int _selectedThemeIndex;
    private bool _scanOnLaunch;
    private bool _showSystemItems;
    private bool _showHiddenItems;
    private bool _smartCleanupSuggestions;
    private bool _confirmBeforeCleanup;
    private bool _excludeCloudOnlyFiles;
    private StorageIndexUsage? _indexUsage;
    private bool _isCompacting;
    private string _indexStatus = string.Empty;

    public SettingsViewModel()
    {
        _preferences = AppPreferencesService.Load();
        _selectedThemeIndex = _preferences.ThemeIndex;
        _scanOnLaunch = _preferences.ScanOnLaunch;
        _showSystemItems = _preferences.ShowSystemItems;
        _showHiddenItems = _preferences.ShowHiddenItems;
        _smartCleanupSuggestions = _preferences.SmartCleanupSuggestions;
        _confirmBeforeCleanup = true;
        _excludeCloudOnlyFiles = true;
        ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);
        CompactIndexCommand = new AsyncRelayCommand(
            CompactIndexAsync,
            () => !IsCompacting);
        CheckIndexCommand = new AsyncRelayCommand(
            CheckIndexAsync,
            () => !IsCompacting);
    }

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (SetProperty(ref _selectedThemeIndex, value))
            {
                Persist();
            }
        }

    }

    public bool ScanOnLaunch
    {
        get => _scanOnLaunch;
        set
        {
            if (SetProperty(ref _scanOnLaunch, value))
            {
                Persist();
            }
        }
    }

    public bool ShowSystemItems
    {
        get => _showSystemItems;
        set
        {
            if (SetProperty(ref _showSystemItems, value))
            {
                Persist();
            }
        }
    }

    public bool ShowHiddenItems
    {
        get => _showHiddenItems;
        set
        {
            if (SetProperty(ref _showHiddenItems, value))
            {
                Persist();
            }
        }
    }

    public bool SmartCleanupSuggestions
    {
        get => _smartCleanupSuggestions;
        set
        {
            if (SetProperty(ref _smartCleanupSuggestions, value))
            {
                Persist();
            }
        }
    }

    public bool ConfirmBeforeCleanup
    {
        get => _confirmBeforeCleanup;
        private set => SetProperty(ref _confirmBeforeCleanup, value);
    }

    public bool ExcludeCloudOnlyFiles
    {
        get => _excludeCloudOnlyFiles;
        private set => SetProperty(ref _excludeCloudOnlyFiles, value);
    }

    public bool IsCompacting
    {
        get => _isCompacting;
        private set
        {
            if (SetProperty(ref _isCompacting, value))
            {
                CompactIndexCommand.NotifyCanExecuteChanged();
                CheckIndexCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string LocalIndexSizeText => _indexUsage is null
        ? AppText.Get("LocalIndexChecking")
        : StorageFormatter.FormatBytes(_indexUsage.TotalBytes);

    public string LocalIndexDetailText => _indexUsage is null
        ? string.Empty
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            AppText.Get("LocalIndexDetailFormat"),
            StorageFormatter.FormatBytes(_indexUsage.DatabaseBytes),
            StorageFormatter.FormatBytes(_indexUsage.WriteAheadLogBytes));

    public string IndexStatus
    {
        get => _indexStatus;
        private set => SetProperty(ref _indexStatus, value);
    }

    public IRelayCommand ResetToDefaultsCommand { get; }

    public IAsyncRelayCommand CompactIndexCommand { get; }

    public IAsyncRelayCommand CheckIndexCommand { get; }

    public async Task InitializeAsync()
    {
        await AppServices.InitializeAsync();
        RefreshIndexUsage();
        IndexStatus = _indexUsage is { TotalBytes: > 0 }
            ? AppText.Get("LocalIndexStoredOnDevice")
            : AppText.Get("LocalIndexNotCreated");
    }

    private async Task CompactIndexAsync()
    {
        if (IsCompacting)
        {
            return;
        }

        IsCompacting = true;
        IndexStatus = AppText.Get("LocalIndexCompacting");
        try
        {
            await Task.Run(() => AppServices.Repository.CompactAsync());
            RefreshIndexUsage();
            IndexStatus = AppText.Get("LocalIndexCompacted");
        }
        catch (Exception exception)
        {
            IndexStatus = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AppText.Get("LocalIndexCompactFailedFormat"),
                exception.Message);
        }
        finally
        {
            IsCompacting = false;
        }
    }

    private async Task CheckIndexAsync()
    {
        if (IsCompacting)
        {
            return;
        }

        IsCompacting = true;
        IndexStatus = AppText.Get("LocalIndexCheckingHealth");
        try
        {
            var result = await Task.Run(() =>
                AppServices.Repository.CheckHealthAsync());
            IndexStatus = result.IsHealthy
                ? AppText.Get("LocalIndexHealthy")
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    AppText.Get("LocalIndexHealthFailedFormat"),
                    result.QuickCheckResult,
                    result.ForeignKeyViolations);
        }
        catch (Exception exception)
        {
            IndexStatus = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AppText.Get("LocalIndexHealthExceptionFormat"),
                exception.Message);
        }
        finally
        {
            IsCompacting = false;
        }
    }

    private void RefreshIndexUsage()
    {
        _indexUsage = AppServices.Repository.GetStorageUsage();
        OnPropertyChanged(nameof(LocalIndexSizeText));
        OnPropertyChanged(nameof(LocalIndexDetailText));
    }

    private void ResetToDefaults()
    {
        SelectedThemeIndex = 0;
        ScanOnLaunch = false;
        ShowSystemItems = false;
        ShowHiddenItems = false;
        SmartCleanupSuggestions = true;
        ConfirmBeforeCleanup = true;
        ExcludeCloudOnlyFiles = true;
        Persist();
    }

    private void Persist()
    {
        _preferences.ThemeIndex = SelectedThemeIndex;
        _preferences.ScanOnLaunch = ScanOnLaunch;
        _preferences.ShowSystemItems = ShowSystemItems;
        _preferences.ShowHiddenItems = ShowHiddenItems;
        _preferences.SmartCleanupSuggestions = SmartCleanupSuggestions;
        _preferences.ConfirmBeforeCleanup = true;
        _preferences.ExcludeCloudOnlyFiles = true;
        AppPreferencesService.Save(_preferences);
    }
}
