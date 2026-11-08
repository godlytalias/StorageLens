using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageLens.Localization;
using StorageLens.Models;
using StorageLens.Services;

namespace StorageLens.ViewModels;

public sealed class CleanupViewModel : ObservableObject
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Task? _initialization;
    private bool _isBusy;
    private bool _isCandidateLoading;
    private bool _isCandidateFolderLoading;
    private bool _isCleaning;
    private double _cleanupProgress;
    private int _cleanupProcessedCount;
    private int _cleanupTotalCount;
    private string _cleanupCurrentPath = string.Empty;
    private string? _errorMessage;
    private string? _candidateErrorMessage;
    private string _statusText = string.Empty;
    private string _scopeText = string.Empty;
    private CleanupRecommendation? _activeRecommendation;
    private CleanupCandidateCollection? _activeCandidates;
    private CleanupCandidateFolderNode? _selectedCandidateFolder;
    private CancellationTokenSource? _candidateCancellation;
    private CancellationTokenSource? _folderCancellation;
    private CancellationTokenSource? _cleanupCancellation;
    private int _candidateVersion;
    private int _folderVersion;
    private CleanupCandidateSelectionState _candidateSelections = new();
    private CleanupCandidateSelectionSummary _folderSelection;

    public CleanupViewModel()
    {
        SelectSafeItemsCommand = new RelayCommand(SelectSafeItems);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        IncludeActiveFolderCommand = new RelayCommand(
            () => SetActiveFolderSelection(true),
            () => CanChangeActiveFolderSelection);
        ExcludeActiveFolderCommand = new RelayCommand(
            () => SetActiveFolderSelection(false),
            () => CanChangeActiveFolderSelection);
        ClearCategoryFilesCommand = new RelayCommand(
            ClearCategoryFiles,
            () => CanChangeActiveFolderSelection);
        CancelCleanupCommand = new RelayCommand(
            CancelCleanup,
            () => CanCancelCleanup);
    }

    public ObservableCollection<CleanupRecommendation> Recommendations { get; } = [];

    public ObservableCollection<CleanupRecommendation> SelectedRecommendations { get; } = [];

    public ObservableCollection<CleanupCandidateFolderNode> CandidateFolders { get; } = [];

    public IRelayCommand SelectSafeItemsCommand { get; }

    public IRelayCommand ClearSelectionCommand { get; }

    public IRelayCommand IncludeActiveFolderCommand { get; }

    public IRelayCommand ExcludeActiveFolderCommand { get; }

    public IRelayCommand ClearCategoryFilesCommand { get; }

    public IRelayCommand CancelCleanupCommand { get; }

    public CleanupCandidateCollection? ActiveCandidates
    {
        get => _activeCandidates;
        private set => SetProperty(ref _activeCandidates, value);
    }

    public CleanupCandidateFolderNode? SelectedCandidateFolder
    {
        get => _selectedCandidateFolder;
        private set => SetProperty(ref _selectedCandidateFolder, value);
    }

    public string ScopeText
    {
        get => _scopeText;
        private set => SetProperty(ref _scopeText, value);
    }

    public string ActiveRecommendationName =>
        _activeRecommendation?.DisplayName ?? string.Empty;

    public string ActiveRecommendationDescription =>
        _activeRecommendation?.DisplayDescription ?? string.Empty;

    public string ActiveRecommendationSafety =>
        _activeRecommendation?.SafetyLabel ?? string.Empty;

    public bool IsWindowsManagedCleanup =>
        _activeRecommendation?.IsWindowsManaged == true;

    public bool IsPackageCacheCleanup =>
        _activeRecommendation?.PolicyId == CleanupPolicyId.PackageCaches;

    public int SelectedCount => Recommendations.Count(item =>
        item.IsSelected && item.IsActionable);

    public long SelectedBytes => Recommendations
        .Where(item => item.IsSelected && item.IsActionable)
        .Sum(item => item.SelectedReclaimableBytes);

    public string SelectedSizeText => StorageFormatter.FormatBytes(SelectedBytes);

    public string SelectedGroupLabel => AppText.Get(
        SelectedCount == 1 ? "SelectedGroupSingular" : "SelectedGroupPlural");

    public bool CanReview => !IsBusy && !IsCleaning && Recommendations.Any(item =>
        item.IsSelected && item.IsActionable && item.SelectedItemCount > 0);

    public bool CanChangeActiveFolderSelection =>
        _activeRecommendation?.IsActionable == true &&
        SelectedCandidateFolder is not null &&
        !IsCandidateLoading &&
        !IsCandidateFolderLoading &&
        !IsCleaning;

    public bool CanRecycleActiveFolder =>
        CanChangeActiveFolderSelection &&
        _folderSelection.SelectedCount > 0;

    public bool CanEditCandidateSelection => !IsCleaning;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanReview));
            }
        }
    }

    public bool IsCandidateLoading
    {
        get => _isCandidateLoading;
        private set
        {
            if (SetProperty(ref _isCandidateLoading, value))
            {
                NotifyCandidateState();
            }
        }
    }

    public bool IsCandidateFolderLoading
    {
        get => _isCandidateFolderLoading;
        private set
        {
            if (SetProperty(ref _isCandidateFolderLoading, value))
            {
                NotifyCandidateState();
            }
        }
    }

    public bool IsCleaning
    {
        get => _isCleaning;
        private set
        {
            if (SetProperty(ref _isCleaning, value))
            {
                OnPropertyChanged(nameof(CanReview));
                OnPropertyChanged(nameof(CanEditCandidateSelection));
                OnPropertyChanged(nameof(CanCancelCleanup));
                OnPropertyChanged(nameof(IsCleanupProgressIndeterminate));
                CancelCleanupCommand.NotifyCanExecuteChanged();
                NotifyCandidateState();
            }
        }
    }

    public double CleanupProgress
    {
        get => _cleanupProgress;
        private set => SetProperty(ref _cleanupProgress, value);
    }

    public int CleanupProcessedCount
    {
        get => _cleanupProcessedCount;
        private set => SetProperty(ref _cleanupProcessedCount, value);
    }

    public int CleanupTotalCount
    {
        get => _cleanupTotalCount;
        private set
        {
            if (SetProperty(ref _cleanupTotalCount, value))
            {
                OnPropertyChanged(nameof(IsCleanupProgressIndeterminate));
            }
        }
    }

    public bool IsCleanupProgressIndeterminate =>
        IsCleaning && CleanupTotalCount == 0;

    public string CleanupCurrentPath
    {
        get => _cleanupCurrentPath;
        private set
        {
            if (SetProperty(ref _cleanupCurrentPath, value))
            {
                OnPropertyChanged(nameof(HasCleanupCurrentPath));
            }
        }
    }

    public bool HasCleanupCurrentPath =>
        !string.IsNullOrWhiteSpace(CleanupCurrentPath);

    public bool CanCancelCleanup =>
        IsCleaning && _cleanupCancellation?.IsCancellationRequested != true;

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

    public string? CandidateErrorMessage
    {
        get => _candidateErrorMessage;
        private set
        {
            if (SetProperty(ref _candidateErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasCandidateError));
                OnPropertyChanged(nameof(HasNoCandidates));
            }
        }
    }

    public bool HasCandidateError =>
        !string.IsNullOrWhiteSpace(CandidateErrorMessage);

    public bool HasNoCandidates =>
        !IsCandidateLoading &&
        !IsCandidateFolderLoading &&
        (ActiveCandidates?.Count ?? 0) == 0 &&
        !HasCandidateError;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ActiveCandidateFolderPath =>
        SelectedCandidateFolder?.DisplayPath ?? string.Empty;

    public string ActiveCandidateSelectionSizeText =>
        StorageFormatter.FormatBytes(_folderSelection.SelectedBytes);

    public string ActiveCandidateSelectionText =>
        SelectedCandidateFolder is null
            ? string.Empty
            : IsWindowsManagedCleanup
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    AppText.Get("CleanupInspectionOnlyFormat"),
                    SelectedCandidateFolder.CandidateCount)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    AppText.Get("CleanupFolderSelectionFormat"),
                    _folderSelection.SelectedCount,
                    SelectedCandidateFolder.CandidateCount);

    public CleanupResult? LastResult { get; private set; }

    public Task InitializeAsync() =>
        _initialization ??= LoadRecommendationsAsync();

    public Task RefreshAsync() => LoadRecommendationsAsync();

    public CleanupRecommendation? FindRecommendation(string name) =>
        Recommendations.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    public CleanupCandidateReviewRequest CreateCandidateReviewRequest(
        CleanupRecommendation recommendation) =>
        new()
        {
            Recommendation = recommendation.CreateReviewSnapshot(),
            SelectionState = _candidateSelections,
        };

    public async Task InitializeCandidateReviewAsync(
        CleanupCandidateReviewRequest request)
    {
        await AppServices.InitializeAsync();
        _candidateSelections = request.SelectionState;
        await OpenRecommendationAsync(request.Recommendation);
    }

    public async Task OpenRecommendationAsync(
        CleanupRecommendation recommendation)
    {
        CancelCandidateLoading();
        var cancellation = new CancellationTokenSource();
        _candidateCancellation = cancellation;
        var token = cancellation.Token;
        var version = ++_candidateVersion;
        _activeRecommendation = recommendation;
        OnPropertyChanged(nameof(ActiveRecommendationName));
        OnPropertyChanged(nameof(ActiveRecommendationDescription));
        OnPropertyChanged(nameof(ActiveRecommendationSafety));
        OnPropertyChanged(nameof(IsWindowsManagedCleanup));
        OnPropertyChanged(nameof(IsPackageCacheCleanup));
        SelectedCandidateFolder = null;
        CandidateFolders.Clear();
        CandidateErrorMessage = null;
        IsCandidateLoading = true;
        try
        {
            var roots = new List<CleanupCandidateFolderNode>();
            foreach (var source in recommendation.Sources)
            {
                var folders = await Task.Run(
                    () => AppServices.CleanupCandidates.GetCandidateFoldersAsync(
                        source,
                        recommendation.PolicyId,
                        token),
                    token);
                token.ThrowIfCancellationRequested();
                if (version != _candidateVersion)
                {
                    return;
                }

                _candidateSelections.RegisterFolderTree(
                    source.ScanId,
                    recommendation.PolicyId,
                    folders);
                roots.AddRange(folders);
            }

            foreach (var root in roots)
            {
                CandidateFolders.Add(root);
            }

            var initial = roots.FirstOrDefault();
            while (initial is { DirectCandidateCount: 0, Children.Count: 1 })
            {
                initial.IsExpanded = true;
                initial = initial.Children[0];
            }

            IsCandidateLoading = false;
            if (initial is not null)
            {
                await SelectCandidateFolderAsync(initial);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == _candidateVersion)
            {
                ReportLoadError(exception);
            }
        }
        finally
        {
            if (version == _candidateVersion)
            {
                IsCandidateLoading = false;
                UpdateActiveFolderSummary();
            }
        }
    }

    public async Task SelectCandidateFolderAsync(
        CleanupCandidateFolderNode folder)
    {
        if (IsCleaning ||
            _activeRecommendation is null ||
            ReferenceEquals(folder, SelectedCandidateFolder) &&
            (ActiveCandidates is not null || IsCandidateFolderLoading))
        {
            return;
        }

        var source = _activeRecommendation.Sources.FirstOrDefault(item =>
            item.ScanId == folder.ScanId);
        if (source is null)
        {
            return;
        }

        _folderCancellation?.Cancel();
        DisposeActiveCandidates();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _candidateCancellation?.Token ?? CancellationToken.None);
        _folderCancellation = cancellation;
        var token = cancellation.Token;
        var version = ++_folderVersion;
        var policy = _activeRecommendation.PolicyId;
        SelectedCandidateFolder = folder;
        CandidateErrorMessage = null;
        IsCandidateFolderLoading = true;
        UpdateActiveFolderSummary();
        try
        {
            var collection = new CleanupCandidateCollection(
                folder.CandidateCount,
                (limit, offset, pageToken) => Task.Run(
                    () => AppServices.CleanupCandidates.GetCandidatesAsync(
                        source,
                        policy,
                        folder.Path,
                        limit,
                        offset,
                        pageToken),
                    pageToken),
                _candidateSelections.IsSelected,
                OnCandidateSelectionChanged,
                ReportLoadError);
            ActiveCandidates = collection;
            await collection.LoadNextPageAsync(token);
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested || version != _folderVersion)
        {
        }
        catch (Exception exception)
        {
            if (version == _folderVersion)
            {
                ReportLoadError(exception);
            }
        }
        finally
        {
            if (version == _folderVersion)
            {
                IsCandidateFolderLoading = false;
                UpdateActiveFolderSummary();
            }

            if (ReferenceEquals(_folderCancellation, cancellation))
            {
                _folderCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public void CancelCandidateLoading()
    {
        ++_candidateVersion;
        ++_folderVersion;
        _candidateCancellation?.Cancel();
        _candidateCancellation?.Dispose();
        _candidateCancellation = null;
        _folderCancellation?.Cancel();
        DisposeActiveCandidates();
        IsCandidateLoading = false;
        IsCandidateFolderLoading = false;
    }

    public void ReportCandidateError(string message) =>
        CandidateErrorMessage = message;

    public void SelectPolicies(IEnumerable<CleanupPolicyId> policies)
    {
        var selected = policies.ToHashSet();
        foreach (var recommendation in Recommendations)
        {
            recommendation.IsSelected =
                selected.Contains(recommendation.PolicyId) &&
                recommendation.IsActionable &&
                recommendation.ItemCount > 0;
        }
    }

    public void RefreshCandidateSelectionSummary()
    {
        foreach (var recommendation in Recommendations)
        {
            var summary = _candidateSelections.GetPolicySelectionSummary(
                recommendation);
            recommendation.UpdateSelection(
                summary.SelectedCount,
                summary.SelectedBytes);
        }

        UpdateSelectionSummary();
    }

    public async Task<CleanupResult> RecycleSelectedAsync(
        CancellationToken cancellationToken = default)
    {
        var selected = Recommendations
            .Where(item => item.IsSelected && item.IsActionable)
            .ToArray();
        var scopes = selected.SelectMany(item =>
                item.Sources.Select(source => new CleanupBatchScope(
                    source,
                    item.PolicyId)))
            .ToArray();
        var result = await ExecuteCleanupAsync(scopes, cancellationToken);
        foreach (var recommendation in selected)
        {
            _candidateSelections.SetAllSelected(recommendation, false);
            recommendation.IsSelected = false;
        }

        return result;
    }

    public async Task<CleanupResult> RecycleActiveFolderAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanRecycleActiveFolder ||
            _activeRecommendation is not { } recommendation ||
            SelectedCandidateFolder is not { } folder)
        {
            throw new InvalidOperationException(
                "Select eligible files in a folder before starting cleanup.");
        }

        var source = recommendation.Sources.Single(item =>
            item.ScanId == folder.ScanId);
        var version = _candidateVersion;
        var selection = _candidateSelections;
        var result = await ExecuteCleanupAsync(
            [new CleanupBatchScope(
                source with
                {
                    ItemCount = folder.CandidateCount,
                    SizeBytes = folder.CandidateBytes,
                },
                recommendation.PolicyId,
                folder.Path)],
            cancellationToken);
        selection.SetFolderSelected(
            recommendation,
            source.ScanId,
            folder.Path,
            false);
        ActiveCandidates?.RefreshSelection();
        if (version == _candidateVersion)
        {
            UpdateActiveFolderSummary();
        }

        return result;
    }

    public async Task RefreshActiveFolderAfterCleanupAsync()
    {
        if (_activeRecommendation is not { } recommendation ||
            SelectedCandidateFolder is not { } folder)
        {
            return;
        }

        var scanId = folder.ScanId;
        var path = folder.Path;
        await OpenRecommendationAsync(recommendation);
        if (!ReferenceEquals(_activeRecommendation, recommendation) ||
            _candidateCancellation?.IsCancellationRequested == true)
        {
            return;
        }

        var refreshedFolder = FindFolder(
            CandidateFolders,
            scanId,
            path);
        if (refreshedFolder is not null)
        {
            await SelectCandidateFolderAsync(refreshedFolder);
        }
    }

    private async Task<CleanupResult> ExecuteCleanupAsync(
        IReadOnlyList<CleanupBatchScope> scopes,
        CancellationToken cancellationToken)
    {
        if (IsCleaning)
        {
            throw new InvalidOperationException("Cleanup is already running.");
        }

        CleanupProgress = 0;
        CleanupProcessedCount = 0;
        CleanupTotalCount = 0;
        CleanupCurrentPath = string.Empty;
        ErrorMessage = null;
        StatusText = AppText.Get("PreparingCleanup");
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cleanupCancellation = cancellation;
        IsCleaning = true;
        try
        {
            var progress = new Progress<CleanupProgress>(update =>
            {
                if (!IsCleaning)
                {
                    return;
                }

                CleanupProgress = update.Total == 0
                    ? 0
                    : Math.Clamp((double)update.Processed / update.Total, 0, 1);
                CleanupProcessedCount = update.Processed;
                CleanupTotalCount = update.Total;
                CleanupCurrentPath = update.CurrentPath;
                StatusText = cancellation.IsCancellationRequested
                    ? AppText.Get("CancellingCleanup")
                    : update.Total == 0
                        ? AppText.Get("PreparingCleanup")
                        : string.Format(
                            CultureInfo.CurrentCulture,
                            AppText.Get("CleanupProgressFormat"),
                            update.Processed,
                            update.Total);
            });
            var batch = new CleanupBatchService(
                AppServices.CleanupCandidates,
                AppServices.Cleanup);
            LastResult = await batch.RecycleAsync(
                scopes,
                _candidateSelections,
                progress,
                cancellation.Token);
            CleanupProgress = 1;
            CleanupCurrentPath = string.Empty;
            StatusText = string.Format(
                CultureInfo.CurrentCulture,
                AppText.Get("CleanupResultSummaryFormat"),
                LastResult.SucceededCount,
                LastResult.FailedCount,
                LastResult.SkippedCount,
                StorageFormatter.FormatBytes(LastResult.BytesRecycled));
            return LastResult;
        }
        catch (OperationCanceledException)
        {
            StatusText = AppText.Get("CleanupCancelled");
            CleanupCurrentPath = string.Empty;
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = AppText.Get("CleanupFailed");
            CleanupCurrentPath = string.Empty;
            throw;
        }
        finally
        {
            _cleanupCancellation = null;
            IsCleaning = false;
        }
    }

    private void CancelCleanup()
    {
        if (_cleanupCancellation is not
            { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        cancellation.Cancel();
        StatusText = AppText.Get("CancellingCleanup");
        OnPropertyChanged(nameof(CanCancelCleanup));
        CancelCleanupCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadRecommendationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await AppServices.InitializeAsync(cancellationToken);
            var selections = Recommendations.ToDictionary(
                item => item.PolicyId,
                item => item.IsSelected);
            var drives = DriveDiscoveryService.GetAvailableDrives();
            var recommendations = await Task.Run(
                () => AppServices.CleanupCandidates.GetRecommendationsAsync(
                    drives,
                    DateTimeOffset.Now,
                    cancellationToken),
                cancellationToken);
            foreach (var existing in Recommendations)
            {
                existing.PropertyChanged -= Recommendation_PropertyChanged;
            }

            Recommendations.Clear();
            foreach (var recommendation in recommendations)
            {
                if (selections.TryGetValue(
                        recommendation.PolicyId,
                        out var selected))
                {
                    recommendation.IsSelected =
                        selected && recommendation.IsActionable &&
                        recommendation.ItemCount > 0;
                }

                recommendation.PropertyChanged += Recommendation_PropertyChanged;
                Recommendations.Add(recommendation);
            }

            var indexedDrives = recommendations
                .SelectMany(item => item.Sources)
                .Select(source => source.DriveLetter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(StorageText.IsolatePath)
                .ToArray();
            ScopeText = indexedDrives.Length == 0
                ? AppText.Get("OverviewNotScanned")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    AppText.Get("CleanupIndexedDrivesFormat"),
                    string.Join(", ", indexedDrives));
            RefreshCandidateSelectionSummary();
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            _loadLock.Release();
        }
    }

    private void Recommendation_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupRecommendation.IsSelected))
        {
            if (sender is CleanupRecommendation
                {
                    IsSelected: true,
                    SelectedItemCount: 0,
                } recommendation)
            {
                _candidateSelections.SetAllSelected(recommendation, true);
                var summary = _candidateSelections.GetPolicySelectionSummary(
                    recommendation);
                recommendation.UpdateSelection(
                    summary.SelectedCount,
                    summary.SelectedBytes);
            }

            UpdateSelectionSummary();
        }
    }

    private void UpdateSelectionSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSizeText));
        OnPropertyChanged(nameof(SelectedGroupLabel));
        OnPropertyChanged(nameof(CanReview));
        SelectedRecommendations.Clear();
        foreach (var recommendation in Recommendations.Where(item =>
                     item.IsSelected && item.IsActionable))
        {
            SelectedRecommendations.Add(recommendation.CreateReviewSnapshot());
        }
    }

    private void SelectSafeItems()
    {
        foreach (var recommendation in Recommendations)
        {
            recommendation.IsSelected =
                recommendation.IsSafe && recommendation.ItemCount > 0;
        }
    }

    private void ClearSelection()
    {
        foreach (var recommendation in Recommendations)
        {
            recommendation.IsSelected = false;
        }
    }

    private void ClearCategoryFiles()
    {
        if (_activeRecommendation is { } recommendation && !IsCleaning)
        {
            _candidateSelections.SetAllSelected(recommendation, false);
            ActiveCandidates?.RefreshSelection();
            UpdateActiveFolderSummary();
        }
    }

    private void SetActiveFolderSelection(bool selected)
    {
        if (!CanChangeActiveFolderSelection ||
            _activeRecommendation is not { } recommendation ||
            SelectedCandidateFolder is not { } folder)
        {
            return;
        }

        _candidateSelections.SetFolderSelected(
            recommendation,
            folder.ScanId,
            folder.Path,
            selected);
        ActiveCandidates?.RefreshSelection();
        UpdateActiveFolderSummary();
    }

    private void OnCandidateSelectionChanged(
        CleanupCandidate candidate,
        bool selected)
    {
        if (candidate.IsSelectionEnabled && !IsCleaning)
        {
            _candidateSelections.SetFileSelected(candidate, selected);
            UpdateActiveFolderSummary();
        }
    }

    private void UpdateActiveFolderSummary()
    {
        _folderSelection =
            _activeRecommendation is { } recommendation &&
            SelectedCandidateFolder is { } folder
                ? _candidateSelections.GetFolderSelectionSummary(
                    recommendation,
                    folder.ScanId,
                    folder)
                : default;
        OnPropertyChanged(nameof(ActiveCandidateFolderPath));
        OnPropertyChanged(nameof(ActiveCandidateSelectionText));
        OnPropertyChanged(nameof(ActiveCandidateSelectionSizeText));
        NotifyCandidateState();
    }

    private void NotifyCandidateState()
    {
        OnPropertyChanged(nameof(HasNoCandidates));
        OnPropertyChanged(nameof(CanChangeActiveFolderSelection));
        OnPropertyChanged(nameof(CanRecycleActiveFolder));
        IncludeActiveFolderCommand.NotifyCanExecuteChanged();
        ExcludeActiveFolderCommand.NotifyCanExecuteChanged();
        ClearCategoryFilesCommand.NotifyCanExecuteChanged();
    }

    private void ReportLoadError(Exception exception) =>
        CandidateErrorMessage = string.Format(
            CultureInfo.CurrentCulture,
            AppText.Get("CleanupCandidatesLoadFailed"),
            exception.Message);

    private void DisposeActiveCandidates()
    {
        ActiveCandidates?.Dispose();
        ActiveCandidates = null;
    }

    private static CleanupCandidateFolderNode? FindFolder(
        IEnumerable<CleanupCandidateFolderNode> folders,
        Guid scanId,
        string path)
    {
        foreach (var folder in folders)
        {
            if (folder.ScanId == scanId &&
                string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }

            var match = FindFolder(folder.Children, scanId, path);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
