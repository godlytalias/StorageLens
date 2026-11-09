using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageLens.Models;
using StorageLens.Services;
using StorageLens.Localization;

namespace StorageLens.ViewModels;

public sealed class AutomationViewModel : ObservableObject
{
    private readonly AutomationSettings _settings;
    private ScheduledCleanupTask? _editingSchedule;
    private bool _monitorEnabled;
    private int _monitorIntervalIndex;
    private bool _notifyOnLowSpace;
    private bool _notifyOnFolderGrowth;
    private double _lowSpaceThresholdGb;
    private double _growthThresholdGb;
    private double _minimumTrackedFolderSizeGb;
    private bool _backgroundIndexEnabled;
    private double _backgroundIndexIntervalDays;
    private string _statusText = string.Empty;
    private bool _hasUnsavedChanges;
    private string _draftName = string.Empty;
    private int _draftIntervalIndex;
    private bool _draftEnabled = true;
    private bool _draftAutomaticCleanup;

    public AutomationViewModel()
    {
        _settings = AutomationSettingsService.LoadForUi();
        _monitorEnabled = _settings.MonitorEnabled;
        _monitorIntervalIndex = FindIndex(MonitorIntervals, _settings.MonitorIntervalDays);
        _notifyOnLowSpace = _settings.NotifyOnLowSpace;
        _notifyOnFolderGrowth = _settings.NotifyOnFolderGrowth;
        _lowSpaceThresholdGb = _settings.LowSpaceThresholdGb;
        _growthThresholdGb = _settings.GrowthThresholdGb;
        _minimumTrackedFolderSizeGb = Math.Max(1, _settings.MinimumTrackedFolderSizeGb);
        _backgroundIndexEnabled = _settings.BackgroundIndexEnabled;
        _backgroundIndexIntervalDays = Math.Clamp(
            _settings.BackgroundIndexIntervalDays,
            1,
            7);
        StatusText = AutomationSettingsService.LastLoadWarning ??
                     AppText.Get("AutomationSettingsStored");

        CleanupSchedules = new ObservableCollection<ScheduledCleanupTask>(
            _settings.CleanupTasks.Select(ToTask));
        foreach (var task in CleanupSchedules)
        {
            task.PropertyChanged += CleanupSchedule_PropertyChanged;
        }

        DraftCategories =
        [
            new("Cache & temporary", true),
            new("Downloads"),
            new("Duplicates"),
        ];

        SaveMonitoringSettingsCommand = new AsyncRelayCommand(
            SaveMonitoringSettingsAsync,
            () => HasUnsavedChanges);
        PreviewAlertCommand = new RelayCommand(PreviewAlert);
        DeleteScheduleCommand = new RelayCommand<ScheduledCleanupTask>(DeleteSchedule);
    }

    public static IReadOnlyList<int> MonitorIntervals { get; } = [1, 2, 7];

    public static IReadOnlyList<int> ScheduleIntervals { get; } = [1, 7, 14, 30];

    public ObservableCollection<string> MonitorIntervalLabels { get; } =
    [
        AppText.Get("ScheduleDaily"),
        AppText.Get("MonitorEveryTwoDays"),
        AppText.Get("ScheduleWeekly"),
    ];

    public ObservableCollection<string> ScheduleIntervalLabels { get; } =
    [
        AppText.Get("ScheduleDaily"),
        AppText.Get("ScheduleWeekly"),
        AppText.Get("ScheduleEveryTwoWeeks"),
        AppText.Get("ScheduleMonthly"),
    ];

    public ObservableCollection<ScheduledCleanupTask> CleanupSchedules { get; }

    public ObservableCollection<CleanupCategorySelection> DraftCategories { get; }

    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (SetProperty(ref _monitorEnabled, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public int MonitorIntervalIndex
    {
        get => _monitorIntervalIndex;
        set
        {
            if (SetProperty(ref _monitorIntervalIndex, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public bool NotifyOnLowSpace
    {
        get => _notifyOnLowSpace;
        set
        {
            if (SetProperty(ref _notifyOnLowSpace, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public bool NotifyOnFolderGrowth
    {
        get => _notifyOnFolderGrowth;
        set
        {
            if (SetProperty(ref _notifyOnFolderGrowth, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public double LowSpaceThresholdGb
    {
        get => _lowSpaceThresholdGb;
        set
        {
            if (SetProperty(ref _lowSpaceThresholdGb, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public double GrowthThresholdGb
    {
        get => _growthThresholdGb;
        set
        {
            if (SetProperty(ref _growthThresholdGb, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public double MinimumTrackedFolderSizeGb
    {
        get => _minimumTrackedFolderSizeGb;
        set
        {
            if (SetProperty(ref _minimumTrackedFolderSizeGb, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public bool BackgroundIndexEnabled
    {
        get => _backgroundIndexEnabled;
        set
        {
            if (SetProperty(ref _backgroundIndexEnabled, value))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public double BackgroundIndexIntervalDays
    {
        get => _backgroundIndexIntervalDays;
        set
        {
            if (SetProperty(
                    ref _backgroundIndexIntervalDays,
                    Math.Clamp(value, 1, 7)))
            {
                MarkMonitoringSettingsDirty();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                SaveMonitoringSettingsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DraftName
    {
        get => _draftName;
        set => SetProperty(ref _draftName, value);
    }

    public int DraftIntervalIndex
    {
        get => _draftIntervalIndex;
        set => SetProperty(ref _draftIntervalIndex, value);
    }

    public bool DraftEnabled
    {
        get => _draftEnabled;
        set => SetProperty(ref _draftEnabled, value);
    }

    public bool DraftAutomaticCleanup
    {
        get => _draftAutomaticCleanup;
        set => SetProperty(ref _draftAutomaticCleanup, value);
    }

    public IAsyncRelayCommand SaveMonitoringSettingsCommand { get; }

    public IRelayCommand PreviewAlertCommand { get; }

    public IRelayCommand<ScheduledCleanupTask> DeleteScheduleCommand { get; }

    public string NextScanText
    {
        get
        {
            if (!BackgroundIndexEnabled)
            {
                return AppText.Get("BackgroundIndexOff");
            }

            return AppText.Get("BackgroundIndexWaiting");
        }
    }

    public string BackgroundIndexStatusText
    {
        get
        {
            if (!_settings.LastBackgroundIndexUtc.HasValue)
            {
                return AppText.Get("IdleIndexNotRun");
            }

            return _settings.LastBackgroundIndexStatus is { Length: > 0 } status
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    AppText.Get("LastIdleIndexStatusFormat"),
                    _settings.LastBackgroundIndexUtc.Value.ToLocalTime(),
                    status)
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    AppText.Get("LastIdleIndexFormat"),
                    _settings.LastBackgroundIndexUtc.Value.ToLocalTime());
        }
    }

    public void BeginNewSchedule()
    {
        _editingSchedule = null;
        DraftName = string.Empty;
        DraftIntervalIndex = 0;
        DraftEnabled = true;
        DraftAutomaticCleanup = false;
        foreach (var category in DraftCategories)
        {
            category.IsSelected = category.Name == "Cache & temporary";
        }
    }

    public void BeginEditSchedule(ScheduledCleanupTask task)
    {
        _editingSchedule = task;
        DraftName = task.Name;
        DraftIntervalIndex = FindIndex(ScheduleIntervals, task.IntervalDays);
        DraftEnabled = task.IsEnabled;
        DraftAutomaticCleanup = task.Mode == CleanupMode.RecycleSafeItems;
        foreach (var category in DraftCategories)
        {
            category.IsSelected = task.Categories.Contains(category.Name, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool SaveSchedule()
    {
        var categories = DraftCategories
            .Where(category => category.IsSelected)
            .Select(category => category.Name)
            .ToList();
        if (string.IsNullOrWhiteSpace(DraftName) || categories.Count == 0)
        {
            return false;
        }

        if (categories.Any(category =>
                CleanupPolicyService.ParsePolicy(category) is not CleanupPolicyId policy ||
                CleanupPolicyService.GetDefinition(policy).Safety == CleanupSafety.Caution))
        {
            return false;
        }

        if (DraftAutomaticCleanup &&
            categories.Any(category =>
                CleanupPolicyService.ParsePolicy(category) is not CleanupPolicyId policy ||
                !CleanupPolicyService.GetDefinition(policy).AutomaticEligible))
        {
            return false;
        }

        var interval = ScheduleIntervals[Math.Clamp(DraftIntervalIndex, 0, ScheduleIntervals.Count - 1)];
        var existing = _editingSchedule;
        if (existing is not null)
        {
            CleanupSchedules.Remove(existing);
            existing.PropertyChanged -= CleanupSchedule_PropertyChanged;
        }

        var task = new ScheduledCleanupTask
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = DraftName.Trim(),
            IntervalDays = interval,
            Categories = categories,
            IsEnabled = DraftEnabled,
            Mode = DraftAutomaticCleanup
                ? CleanupMode.RecycleSafeItems
                : CleanupMode.ReviewOnly,
            LastRun = existing?.LastRun ?? DateTimeOffset.Now,
        };
        task.PropertyChanged += CleanupSchedule_PropertyChanged;
        CleanupSchedules.Add(task);
        PersistSchedulesOnly();
        QueueRegistrationRefresh();
        return true;
    }

    private async Task SaveMonitoringSettingsAsync()
    {
        Persist();
        OnPropertyChanged(nameof(NextScanText));
        StatusText = await RefreshRegistrationAsync();
        HasUnsavedChanges = false;
    }

    private void MarkMonitoringSettingsDirty()
    {
        HasUnsavedChanges = true;
        StatusText = AppText.Get("AutomationUnsavedChanges");
        OnPropertyChanged(nameof(NextScanText));
    }

    private void PreviewAlert()
    {
        NotificationService.ShowGrowthAlert(
            AppText.Get("PreviewAlertTitle"),
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AppText.Get("PreviewAlertMessageFormat"),
                LowSpaceThresholdGb),
            "storagelens://growth");
        StatusText = AppText.Get("PreviewAlertSent");
    }

    private void DeleteSchedule(ScheduledCleanupTask? task)
    {
        if (task is null)
        {
            return;
        }

        task.PropertyChanged -= CleanupSchedule_PropertyChanged;
        CleanupSchedules.Remove(task);
        PersistSchedulesOnly();
        QueueRegistrationRefresh();
    }

    private void CleanupSchedule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScheduledCleanupTask.IsEnabled))
        {
            PersistSchedulesOnly();
            QueueRegistrationRefresh();
        }
    }

    private void Persist()
    {
        _settings.MonitorEnabled = MonitorEnabled;
        _settings.MonitorIntervalDays = MonitorIntervals[Math.Clamp(MonitorIntervalIndex, 0, MonitorIntervals.Count - 1)];
        _settings.NotifyOnLowSpace = NotifyOnLowSpace;
        _settings.NotifyOnFolderGrowth = NotifyOnFolderGrowth;
        _settings.LowSpaceThresholdGb = (int)LowSpaceThresholdGb;
        _settings.GrowthThresholdGb = (int)GrowthThresholdGb;
        _settings.MinimumTrackedFolderSizeGb = Math.Max(1, (int)MinimumTrackedFolderSizeGb);
        _settings.BackgroundIndexEnabled = BackgroundIndexEnabled;
        _settings.BackgroundIndexIntervalDays = Math.Clamp(
            (int)BackgroundIndexIntervalDays,
            1,
            7);
        _settings.CleanupTasks = CleanupSchedules.Select(ToSnapshot).ToList();
        AutomationSettingsService.Save(_settings);
    }

    private void PersistSchedulesOnly()
    {
        _settings.CleanupTasks =
            CleanupSchedules.Select(ToSnapshot).ToList();
        AutomationSettingsService.Save(_settings);
    }

    private static ScheduledCleanupTask ToTask(ScheduledCleanupTaskSnapshot snapshot) =>
        new()
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            IntervalDays = snapshot.IntervalDays,
            Categories = snapshot.Categories,
            IsEnabled = snapshot.IsEnabled,
            Mode = snapshot.Mode,
            LastRun = snapshot.LastRun,
        };

    private static ScheduledCleanupTaskSnapshot ToSnapshot(ScheduledCleanupTask task) =>
        new()
        {
            Id = task.Id,
            Name = task.Name,
            IntervalDays = task.IntervalDays,
            Categories = task.Categories.ToList(),
            IsEnabled = task.IsEnabled,
            Mode = task.Mode,
            LastRun = task.LastRun,
        };

    private void QueueRegistrationRefresh()
    {
        _ = RefreshRegistrationAndReportAsync();
    }

    private async Task<string> RefreshRegistrationAsync()
    {
        return await BackgroundTaskRegistrationService.ApplyAutomationRegistrationAsync(
            _settings);
    }

    private async Task RefreshRegistrationAndReportAsync()
    {
        StatusText = await RefreshRegistrationAsync();
    }

    private static int FindIndex(IReadOnlyList<int> values, int value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return 0;
    }
}
