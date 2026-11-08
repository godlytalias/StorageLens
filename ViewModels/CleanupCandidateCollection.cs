using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Data;
using StorageLens.Models;
using Windows.Foundation;

namespace StorageLens.ViewModels;

public sealed partial class CleanupCandidateCollection :
    ObservableCollection<CleanupCandidate>,
    ISupportIncrementalLoading,
    IDisposable
{
    private readonly Func<int, int, CancellationToken,
        Task<IReadOnlyList<CleanupCandidate>>> _loadPageAsync;
    private readonly Func<CleanupCandidate, bool> _isSelected;
    private readonly Action<CleanupCandidate, bool> _selectionChanged;
    private readonly Action<Exception>? _loadFailed;
    private readonly SynchronizationContext? _uiContext =
        SynchronizationContext.Current;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly long _totalCount;
    private bool _disposed;
    private bool _refreshingSelection;
    private bool _hasMoreItems = true;

    public CleanupCandidateCollection(
        long totalCount,
        Func<int, int, CancellationToken,
            Task<IReadOnlyList<CleanupCandidate>>> loadPageAsync,
        Func<CleanupCandidate, bool> isSelected,
        Action<CleanupCandidate, bool> selectionChanged,
        Action<Exception>? loadFailed = null)
    {
        _totalCount = Math.Max(0, totalCount);
        _loadPageAsync = loadPageAsync;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
        _loadFailed = loadFailed;
        HasMoreItems = totalCount > 0;
    }

    public bool HasMoreItems
    {
        get => _hasMoreItems;
        private set
        {
            if (_hasMoreItems == value)
            {
                return;
            }

            _hasMoreItems = value;
            OnPropertyChanged(
                new PropertyChangedEventArgs(nameof(HasMoreItems)));
        }
    }

    public async Task<int> LoadNextPageAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await LoadPageCoreAsync(
            200,
            cancellationToken);
        return (int)result.Count;
    }

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(
        uint count)
    {
        var pageSize = Math.Clamp((int)Math.Max(count, 50), 50, 300);
        return LoadMoreItemsSafelyAsync(pageSize)
            .AsAsyncOperation();
    }

    private async Task<LoadMoreItemsResult> LoadMoreItemsSafelyAsync(int pageSize)
    {
        try
        {
            return await LoadPageCoreAsync(pageSize, CancellationToken.None);
        }
        catch (OperationCanceledException) when (
            _disposed || _lifetimeCancellation.IsCancellationRequested)
        {
            return new LoadMoreItemsResult();
        }
        catch (Exception exception)
        {
            await PostToUiAsync(() =>
            {
                if (!_disposed)
                {
                    HasMoreItems = false;
                    _loadFailed?.Invoke(exception);
                }
            });
            return new LoadMoreItemsResult();
        }
    }

    public void Cancel()
    {
        if (!_lifetimeCancellation.IsCancellationRequested)
        {
            _lifetimeCancellation.Cancel();
        }
    }

    public void RefreshSelection()
    {
        _refreshingSelection = true;
        try
        {
            foreach (var candidate in this)
            {
                candidate.IsSelected =
                    candidate.IsSelectionEnabled &&
                    _isSelected(candidate);
            }
        }
        finally
        {
            _refreshingSelection = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        foreach (var candidate in this)
        {
            candidate.PropertyChanged -= Candidate_PropertyChanged;
        }

    }

    private async Task<LoadMoreItemsResult> LoadPageCoreAsync(
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (_disposed || !HasMoreItems)
        {
            return new LoadMoreItemsResult();
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || !HasMoreItems)
            {
                return new LoadMoreItemsResult();
            }

            using var linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetimeCancellation.Token);
            var page = await _loadPageAsync(
                pageSize,
                Count,
                linkedCancellation.Token);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            await PostToUiAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var candidate in page)
                {
                    candidate.IsSelected =
                        candidate.IsSelectionEnabled &&
                        _isSelected(candidate);
                    candidate.PropertyChanged += Candidate_PropertyChanged;
                    Add(candidate);
                }

                HasMoreItems = Count < _totalCount &&
                               page.Count > 0;
            });
            return new LoadMoreItemsResult
            {
                Count = checked((uint)page.Count),
            };
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private Task PostToUiAsync(Action action)
    {
        if (_uiContext is null ||
            ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            action();
            return Task.CompletedTask;
        }

        var completion =
            new TaskCompletionSource(TaskCreationOptions
                .RunContinuationsAsynchronously);
        _uiContext.Post(
            _ =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            null);
        return completion.Task;
    }

    private void Candidate_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!_disposed &&
            !_refreshingSelection &&
            sender is CleanupCandidate candidate &&
            e.PropertyName == nameof(CleanupCandidate.IsSelected))
        {
            _selectionChanged(candidate, candidate.IsSelected);
        }
    }
}
