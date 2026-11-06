using Windows.Storage;

namespace StorageLens.Services;

public static class AppServices
{
    private static readonly SemaphoreSlim InitializationLock = new(1, 1);

    public static StorageRepository Repository { get; private set; } = null!;

    public static StorageScanService Scanner { get; private set; } = null!;

    public static CleanupService Cleanup { get; private set; } = null!;

    public static CleanupCandidateService CleanupCandidates { get; private set; } = null!;

    public static DuplicateDetectionService DuplicateDetection { get; private set; } = null!;

    public static StorageSyncService Sync { get; private set; } = null!;

    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Repository is not null)
        {
            return;
        }

        await InitializationLock.WaitAsync(cancellationToken);
        try
        {
            if (Repository is not null)
            {
                return;
            }

            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var repository = new StorageRepository(Path.Combine(localFolder, "StorageIndex", "storage-lens.db"));
            await repository.InitializeAsync(cancellationToken);
            Repository = repository;
            Scanner = new StorageScanService(repository);
            Cleanup = new CleanupService(repository);
            CleanupCandidates = new CleanupCandidateService(repository);
            DuplicateDetection = new DuplicateDetectionService(repository);
            Sync = new StorageSyncService(repository, Scanner);
        }
        finally
        {
            InitializationLock.Release();
        }
    }
}
