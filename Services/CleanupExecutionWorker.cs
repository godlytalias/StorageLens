using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace StorageLens.Services;

internal sealed partial class CleanupExecutionWorker : IAsyncDisposable
{
    private readonly BlockingCollection<WorkItem> _workItems = new();
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _disposeStarted;

    public CleanupExecutionWorker()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Storage Lens cleanup",
        };
        if (!_thread.TrySetApartmentState(ApartmentState.STA))
        {
            throw new PlatformNotSupportedException(
                "A single-threaded apartment is required to move files to the Recycle Bin.");
        }

        _thread.Start();
    }

    public async Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _started.Task.ConfigureAwait(false);
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            throw new ObjectDisposedException(nameof(CleanupExecutionWorker));
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _workItems.Add(new WorkItem(action, completion));
        }
        catch (InvalidOperationException exception) when (
            Volatile.Read(ref _disposeStarted) != 0)
        {
            throw new ObjectDisposedException(
                nameof(CleanupExecutionWorker),
                exception);
        }

        await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _workItems.CompleteAdding();
        }

        await _stopped.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        var result = OleInitialize(IntPtr.Zero);
        if (result < 0)
        {
            _started.TrySetException(
                new COMException(
                    "Windows could not initialize the Recycle Bin worker.",
                    result));
            _stopped.TrySetResult();
            return;
        }

        Exception? terminalException = null;
        try
        {
            _started.TrySetResult();
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                try
                {
                    workItem.Action();
                    workItem.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    workItem.Completion.TrySetException(exception);
                }
            }
        }
        catch (Exception exception)
        {
            terminalException = exception;
            _started.TrySetException(exception);
            FailQueuedWork(exception);
        }
        finally
        {
            OleUninitialize();
            if (terminalException is null)
            {
                _stopped.TrySetResult();
            }
            else
            {
                _stopped.TrySetException(terminalException);
            }
        }
    }

    private void FailQueuedWork(Exception exception)
    {
        while (_workItems.TryTake(out var workItem))
        {
            workItem.Completion.TrySetException(exception);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(IntPtr reserved);

    [LibraryImport("ole32.dll")]
    private static partial void OleUninitialize();

    private sealed record WorkItem(
        Action Action,
        TaskCompletionSource Completion);
}
