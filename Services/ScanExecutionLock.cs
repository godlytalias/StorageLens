namespace StorageLens.Services;

internal sealed class ScanExecutionLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private ScanExecutionLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<ScanExecutionLock> AcquireAsync(
        string databasePath,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var lockPath = GetLockPath(databasePath);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Create(lockPath);
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Another Storage Lens process is already indexing this device.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Storage Lens could not acquire the indexing lock.");
                }
            }

            await Task.Delay(150, cancellationToken);
        }
    }

    public static Task<ScanExecutionLock?> TryAcquireAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult<ScanExecutionLock?>(Create(
                GetLockPath(databasePath)));
        }
        catch (IOException)
        {
            return Task.FromResult<ScanExecutionLock?>(null);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<ScanExecutionLock?>(null);
        }
    }

    private static string GetLockPath(string databasePath) =>
        Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            "storage-scan.lock");

    private static ScanExecutionLock Create(string lockPath) =>
        new(new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
