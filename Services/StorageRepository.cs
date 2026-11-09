using Microsoft.Data.Sqlite;
using StorageLens.Models;

namespace StorageLens.Services;

public sealed class StorageRepository
{
    private const int CurrentSchemaVersion = 6;
    private const long MinimumGrowthSampleBytes = 1L * 1024 * 1024 * 1024;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ScanFormatInfo>
        _scanFormats = [];
    private bool _initialized;

    public StorageRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
            Pooling = true,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var databaseIsNew = !File.Exists(DatabasePath);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA journal_size_limit=67108864;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS ScanRuns (
                    Id TEXT PRIMARY KEY,
                    DriveLetter TEXT NOT NULL,
                    DriveName TEXT NOT NULL,
                    FileSystem TEXT NOT NULL,
                    TotalBytes INTEGER NOT NULL,
                    UsedBytes INTEGER NOT NULL,
                    FreeBytes INTEGER NOT NULL,
                    StartedUtc INTEGER NOT NULL,
                    CompletedUtc INTEGER,
                    Status INTEGER NOT NULL,
                    FilesScanned INTEGER NOT NULL DEFAULT 0,
                    FoldersScanned INTEGER NOT NULL DEFAULT 0,
                    BytesScanned INTEGER NOT NULL DEFAULT 0,
                    InaccessibleItems INTEGER NOT NULL DEFAULT 0,
                    ErrorMessage TEXT,
                    OwnerProcessId INTEGER,
                    OwnerProcessStartedUtc INTEGER,
                    LeaseHeartbeatUtc INTEGER,
                    Generation INTEGER NOT NULL DEFAULT 1,
                    VolumeSerialNumber TEXT,
                    AllocatedBytesScanned INTEGER NOT NULL DEFAULT 0,
                    RunKey INTEGER UNIQUE,
                    ScanFormat INTEGER NOT NULL DEFAULT 1,
                    JournalId INTEGER,
                    LastUsn INTEGER,
                    ScanPhase TEXT,
                    PhaseStartedUtc INTEGER
                );

                CREATE INDEX IF NOT EXISTS IX_ScanRuns_Drive_Status_Start
                    ON ScanRuns (DriveLetter, Status, StartedUtc DESC);

                CREATE TABLE IF NOT EXISTS ScanCategorySummaries (
                    ScanId TEXT NOT NULL REFERENCES ScanRuns(Id) ON DELETE CASCADE,
                    Category TEXT NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    ItemCount INTEGER NOT NULL,
                    PRIMARY KEY (ScanId, Category)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS ScanCleanupSummaries (
                    ScanId TEXT NOT NULL REFERENCES ScanRuns(Id) ON DELETE CASCADE,
                    PolicyId INTEGER NOT NULL,
                    ItemCount INTEGER NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    EvaluatedUtc INTEGER NOT NULL,
                    PRIMARY KEY (ScanId, PolicyId)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS FileEntries (
                    ScanId TEXT NOT NULL REFERENCES ScanRuns(Id) ON DELETE CASCADE,
                    FullPath TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    ParentPath TEXT NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    CreatedUtc INTEGER NOT NULL,
                    ModifiedUtc INTEGER NOT NULL,
                    AccessedUtc INTEGER NOT NULL,
                    Attributes INTEGER NOT NULL,
                    Extension TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Tag TEXT NOT NULL,
                    VolumeSerialNumber INTEGER NOT NULL DEFAULT 0,
                    FileIndexHigh INTEGER NOT NULL DEFAULT 0,
                    FileIndexLow INTEGER NOT NULL DEFAULT 0,
                    ContentHash TEXT,
                    AllocatedSizeBytes INTEGER NOT NULL DEFAULT 0,
                    PhysicalSizeBytes INTEGER NOT NULL DEFAULT 0,
                    FileId128 TEXT NOT NULL DEFAULT '',
                    ReparseTag INTEGER NOT NULL DEFAULT 0,
                    LinkCount INTEGER NOT NULL DEFAULT 1,
                    IsHardLinkAlias INTEGER NOT NULL DEFAULT 0,
                    IsIncomplete INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ScanId, FullPath)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS FolderEntries (
                    ScanId TEXT NOT NULL REFERENCES ScanRuns(Id) ON DELETE CASCADE,
                    FullPath TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    ParentPath TEXT NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    DirectSizeBytes INTEGER NOT NULL,
                    FileCount INTEGER NOT NULL,
                    DirectFileCount INTEGER NOT NULL,
                    Depth INTEGER NOT NULL,
                    Attributes INTEGER NOT NULL,
                    ModifiedUtc INTEGER NOT NULL,
                    Category TEXT NOT NULL,
                    Tag TEXT NOT NULL,
                    AllocatedSizeBytes INTEGER NOT NULL DEFAULT 0,
                    ReparseTag INTEGER NOT NULL DEFAULT 0,
                    IsIncomplete INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ScanId, FullPath)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS ScanIssues (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ScanId TEXT NOT NULL REFERENCES ScanRuns(Id) ON DELETE CASCADE,
                    Path TEXT NOT NULL,
                    IssueType INTEGER NOT NULL,
                    ReparseTag INTEGER NOT NULL DEFAULT 0,
                    ErrorCode INTEGER,
                    Message TEXT NOT NULL,
                    DetectedUtc INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_ScanIssues_ScanTime
                    ON ScanIssues (ScanId, DetectedUtc);

                CREATE TABLE IF NOT EXISTS FolderNodes (
                    RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ParentId INTEGER,
                    Name TEXT NOT NULL,
                    PathHash TEXT NOT NULL,
                    LogicalSize INTEGER NOT NULL DEFAULT 0,
                    AllocatedSize INTEGER NOT NULL DEFAULT 0,
                    DirectLogicalSize INTEGER NOT NULL DEFAULT 0,
                    DirectAllocatedSize INTEGER NOT NULL DEFAULT 0,
                    FileCount INTEGER NOT NULL DEFAULT 0,
                    DirectFileCount INTEGER NOT NULL DEFAULT 0,
                    Depth INTEGER NOT NULL,
                    Attributes INTEGER NOT NULL,
                    ReparseTag INTEGER NOT NULL,
                    ModifiedUtc INTEGER NOT NULL,
                    Category INTEGER NOT NULL,
                    Tag INTEGER NOT NULL,
                    FileId128 TEXT NOT NULL DEFAULT '',
                    FileId64 TEXT NOT NULL DEFAULT '',
                    IsIncomplete INTEGER NOT NULL DEFAULT 0,
                    LastSeenGeneration INTEGER NOT NULL DEFAULT 1,
                    UNIQUE (RunKey, ParentId, Name)
                );

                CREATE INDEX IF NOT EXISTS IX_FolderNodes_ParentSize
                    ON FolderNodes (RunKey, ParentId, AllocatedSize DESC);
                CREATE INDEX IF NOT EXISTS IX_FolderNodes_PathHash
                    ON FolderNodes (RunKey, PathHash);
                CREATE INDEX IF NOT EXISTS IX_FolderNodes_FileId
                    ON FolderNodes (RunKey, FileId64);

                CREATE TABLE IF NOT EXISTS PhysicalFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
                    FileIdentityKey TEXT NOT NULL,
                    FileId128 TEXT NOT NULL,
                    VolumeSerialNumber INTEGER NOT NULL,
                    LogicalSize INTEGER NOT NULL,
                    AllocatedSize INTEGER NOT NULL,
                    CreatedUtc INTEGER NOT NULL,
                    ModifiedUtc INTEGER NOT NULL,
                    AccessedUtc INTEGER NOT NULL,
                    Attributes INTEGER NOT NULL,
                    ReparseTag INTEGER NOT NULL,
                    LinkCount INTEGER NOT NULL DEFAULT 1,
                    ChangeTime INTEGER NOT NULL DEFAULT 0,
                    ContentHash TEXT,
                    SampleHash TEXT,
                    HashState INTEGER NOT NULL DEFAULT 0,
                    LastSeenGeneration INTEGER NOT NULL DEFAULT 1,
                    UNIQUE (RunKey, FileIdentityKey)
                );

                CREATE INDEX IF NOT EXISTS IX_PhysicalFiles_SizeHash
                    ON PhysicalFiles (RunKey, LogicalSize, HashState);
                CREATE INDEX IF NOT EXISTS IX_PhysicalFiles_ContentHash
                    ON PhysicalFiles (RunKey, ContentHash)
                    WHERE ContentHash IS NOT NULL;

                CREATE TABLE IF NOT EXISTS FileNames (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
                    ParentId INTEGER NOT NULL,
                    PhysicalFileId INTEGER NOT NULL REFERENCES PhysicalFiles(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL,
                    Extension TEXT NOT NULL,
                    Category INTEGER NOT NULL,
                    Tag INTEGER NOT NULL,
                    IsHardLinkAlias INTEGER NOT NULL DEFAULT 0,
                    LastSeenGeneration INTEGER NOT NULL DEFAULT 1,
                    UNIQUE (RunKey, ParentId, Name)
                );

                CREATE INDEX IF NOT EXISTS IX_FileNames_ParentSize
                    ON FileNames (RunKey, ParentId);
                CREATE INDEX IF NOT EXISTS IX_FileNames_Search
                    ON FileNames (RunKey, Name, Category);

                CREATE TABLE IF NOT EXISTS NormalizedCleanupCandidates (
                    RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
                    PolicyId INTEGER NOT NULL,
                    FileNameId INTEGER NOT NULL REFERENCES FileNames(Id) ON DELETE CASCADE,
                    PRIMARY KEY (RunKey, PolicyId, FileNameId)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS IX_NormalizedCleanupCandidates_Page
                    ON NormalizedCleanupCandidates (RunKey, PolicyId);

                CREATE TABLE IF NOT EXISTS FolderGrowthSamples (
                    VolumeKey TEXT NOT NULL,
                    PathHash TEXT NOT NULL,
                    SampleUtc INTEGER NOT NULL,
                    LogicalSize INTEGER NOT NULL,
                    AllocatedSize INTEGER NOT NULL,
                    FileCount INTEGER NOT NULL,
                    PRIMARY KEY (VolumeKey, PathHash, SampleUtc)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS ScanWorkItems (
                    RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
                    PathHash TEXT NOT NULL,
                    FullPath TEXT NOT NULL,
                    Depth INTEGER NOT NULL,
                    State INTEGER NOT NULL DEFAULT 0,
                    AttemptCount INTEGER NOT NULL DEFAULT 0,
                    LastError TEXT,
                    UpdatedUtc INTEGER NOT NULL,
                    PRIMARY KEY (RunKey, PathHash)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS IX_ScanWorkItems_Pending
                    ON ScanWorkItems (RunKey, State, UpdatedUtc);

                CREATE TABLE IF NOT EXISTS CleanupHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TimestampUtc INTEGER NOT NULL,
                    ScanId TEXT,
                    PolicyId INTEGER NOT NULL,
                    OriginalPath TEXT NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    Automatic INTEGER NOT NULL,
                    Result TEXT NOT NULL,
                    Error TEXT
                );

                CREATE INDEX IF NOT EXISTS IX_CleanupHistory_Time
                    ON CleanupHistory (TimestampUtc DESC);

                CREATE TABLE IF NOT EXISTS SchemaVersion (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL
                );

                INSERT INTO SchemaVersion (Id, Version)
                VALUES (1, 1)
                ON CONFLICT(Id) DO NOTHING;
        """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await ApplyMigrationsAsync(connection, cancellationToken);
            await ConfigureIndexesAsync(connection, databaseIsNew, cancellationToken);
            using var scanLock = await ScanExecutionLock.TryAcquireAsync(
                DatabasePath,
                cancellationToken);
            if (scanLock is not null)
            {
                await ReconcileInterruptedScansAsync(connection, cancellationToken);
            }
            await ReconcilePendingCleanupActionsAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<StorageScanRun?> GetLatestCompletedScanAsync(
        string driveLetter,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, DriveLetter, DriveName, FileSystem, TotalBytes, UsedBytes, FreeBytes,
                   StartedUtc, CompletedUtc, Status, FilesScanned, FoldersScanned,
                   BytesScanned, InaccessibleItems, ErrorMessage,
                   AllocatedBytesScanned, RunKey, ScanFormat
            FROM ScanRuns
            WHERE DriveLetter = $drive
              AND Status = $status
              AND ($volume = '' OR VolumeSerialNumber = $volume
                   OR VolumeSerialNumber IS NULL OR VolumeSerialNumber = '')
            ORDER BY CompletedUtc DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$drive", NormalizeDriveLetter(driveLetter));
        command.Parameters.AddWithValue("$status", (int)StorageScanStatus.Completed);
        command.Parameters.AddWithValue(
            "$volume",
            VolumeIdentityService.TryGetVolumeSerial(driveLetter) ??
            string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadScanRun(reader)
            : null;
    }

    public async Task<StorageScanRun?> GetScanAsync(
        Guid scanId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, DriveLetter, DriveName, FileSystem, TotalBytes, UsedBytes, FreeBytes,
                   StartedUtc, CompletedUtc, Status, FilesScanned, FoldersScanned,
                   BytesScanned, InaccessibleItems, ErrorMessage,
                   AllocatedBytesScanned, RunKey, ScanFormat
            FROM ScanRuns
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", scanId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadScanRun(reader)
            : null;
    }

    public async Task<JournalCheckpoint?> GetJournalCheckpointAsync(
        Guid scanId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JournalId, LastUsn, ScanFormat, RunKey
            FROM ScanRuns
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", scanId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.IsDBNull(0) ||
            reader.IsDBNull(1))
        {
            return null;
        }

        return new JournalCheckpoint(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt64(3));
    }

    public async Task SaveJournalCheckpointAsync(
        Guid scanId,
        long journalId,
        long nextUsn,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanRuns
            SET JournalId = $journal,
                LastUsn = $next
            WHERE Id = $id AND Status = $completed
            """;
        command.Parameters.AddWithValue("$journal", journalId);
        command.Parameters.AddWithValue("$next", nextUsn);
        command.Parameters.AddWithValue("$id", scanId.ToString("N"));
        command.Parameters.AddWithValue(
            "$completed",
            (int)StorageScanStatus.Completed);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The journal checkpoint could not be advanced.");
        }
    }

    public async Task<string?> GetFolderPathByFileIdAsync(
        long runKey,
        ulong fileReferenceNumber,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var key = fileReferenceNumber.ToString(
            "X16",
            System.Globalization.CultureInfo.InvariantCulture);
        long? folderId = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id
                FROM FolderNodes
                WHERE RunKey = $run AND FileId64 = $fileId
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$run", runKey);
            command.Parameters.AddWithValue("$fileId", key);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value != DBNull.Value)
            {
                folderId = Convert.ToInt64(value);
            }
        }

        return folderId.HasValue
            ? await BuildFolderPathAsync(
                connection,
                runKey,
                folderId.Value,
                cancellationToken)
            : null;
    }

    internal async Task<DirectoryRefreshOutcome> RefreshDirectorySnapshotAsync(
        long runKey,
        string directoryPath,
        IReadOnlyList<NativeDirectoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var parentId = await ResolveFolderIdAsync(
            connection,
            runKey,
            directoryPath,
            cancellationToken);
        if (!parentId.HasValue)
        {
            return new DirectoryRefreshOutcome(
                false,
                "The changed directory is not in the current index.");
        }

        var existingFiles = new Dictionary<string, ExistingFileRow>(
            StringComparer.Ordinal);
        var existingFolders = new Dictionary<string, ExistingFolderRow>(
            StringComparer.Ordinal);
        long oldDirectLogical;
        long oldDirectAllocated;
        long oldDirectFiles;
        await using (var folder = connection.CreateCommand())
        {
            folder.CommandText = """
                SELECT DirectLogicalSize, DirectAllocatedSize, DirectFileCount
                FROM FolderNodes
                WHERE RunKey = $run AND Id = $id
                """;
            folder.Parameters.AddWithValue("$run", runKey);
            folder.Parameters.AddWithValue("$id", parentId.Value);
            await using var reader = await folder.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new DirectoryRefreshOutcome(
                    false,
                    "The changed directory row disappeared.");
            }

            oldDirectLogical = reader.GetInt64(0);
            oldDirectAllocated = reader.GetInt64(1);
            oldDirectFiles = reader.GetInt64(2);
        }

        await using (var files = connection.CreateCommand())
        {
            files.CommandText = """
                SELECT n.Id, n.Name, n.IsHardLinkAlias, p.Id, p.FileIdentityKey,
                       p.LogicalSize, p.AllocatedSize
                FROM FileNames n
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE n.RunKey = $run AND n.ParentId = $parent
                """;
            files.Parameters.AddWithValue("$run", runKey);
            files.Parameters.AddWithValue("$parent", parentId.Value);
            await using var reader = await files.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingFiles[reader.GetString(1)] = new ExistingFileRow(
                    reader.GetInt64(0),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetBoolean(2));
            }
        }

        await using (var folders = connection.CreateCommand())
        {
            folders.CommandText = """
                SELECT Id, Name, FileId128
                FROM FolderNodes
                WHERE RunKey = $run AND ParentId = $parent
                """;
            folders.Parameters.AddWithValue("$run", runKey);
            folders.Parameters.AddWithValue("$parent", parentId.Value);
            await using var reader = await folders.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingFolders[reader.GetString(1)] = new ExistingFolderRow(
                    reader.GetInt64(0),
                    reader.GetString(2));
            }
        }

        var currentFiles = entries
            .Where(entry =>
                ((FileAttributes)entry.Attributes &
                 FileAttributes.Directory) == 0)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var currentFolders = entries
            .Where(entry =>
                ((FileAttributes)entry.Attributes &
                 FileAttributes.Directory) != 0)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        if (existingFolders.Count != currentFolders.Count ||
            existingFolders.Keys.Any(name =>
                !currentFolders.ContainsKey(name)) ||
            currentFolders.Any(item =>
            {
                if (!existingFolders.TryGetValue(item.Key, out var existing))
                {
                    return true;
                }

                return !string.IsNullOrWhiteSpace(existing.FileId128) &&
                       !string.Equals(
                           existing.FileId128,
                           item.Value.FileId,
                           StringComparison.OrdinalIgnoreCase);
            }))
        {
            return new DirectoryRefreshOutcome(
                false,
                "The directory hierarchy changed; a verification scan is required.");
        }

        var volumeSerial = await GetRunVolumeSerialAsync(
            runKey,
            cancellationToken);
        foreach (var pair in existingFiles)
        {
            if (currentFiles.TryGetValue(pair.Key, out var currentEntry))
            {
                var identityKey = string.IsNullOrWhiteSpace(currentEntry.FileId)
                    ? GetPathHash(
                        CombineDisplayPath(directoryPath, currentEntry.Name))
                    : $"{volumeSerial:X16}:{currentEntry.FileId}";
                if (!pair.Value.IsAlias &&
                    !string.Equals(
                        pair.Value.IdentityKey,
                        identityKey,
                        StringComparison.Ordinal) &&
                    await HasOtherPhysicalNamesAsync(
                        connection,
                        pair.Value.PhysicalId,
                        pair.Value.NameId,
                        cancellationToken))
                {
                    return new DirectoryRefreshOutcome(
                        false,
                        "A canonical hard-link name changed; a verification scan is required.");
                }

                continue;
            }

            if (!pair.Value.IsAlias)
            {
                var hasOtherNames = await HasOtherPhysicalNamesAsync(
                    connection,
                    pair.Value.PhysicalId,
                    pair.Value.NameId,
                    cancellationToken);
                if (hasOtherNames)
                {
                    return new DirectoryRefreshOutcome(
                        false,
                        "A canonical hard-link name changed; a verification scan is required.");
                }
            }
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long newDirectLogical = 0;
            long newDirectAllocated = 0;
            foreach (var current in currentFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = current.Value;
                var identityKey = string.IsNullOrWhiteSpace(entry.FileId)
                    ? GetPathHash(CombineDisplayPath(directoryPath, entry.Name))
                    : $"{volumeSerial:X16}:{entry.FileId}";
                newDirectLogical += entry.LogicalSize;

                if (existingFiles.TryGetValue(current.Key, out var existing))
                {
                    if (string.Equals(
                            existing.IdentityKey,
                            identityKey,
                            StringComparison.Ordinal))
                    {
                            if (!existing.IsAlias)
                        {
                            newDirectAllocated += entry.AllocatedSize;
                        }

                        await UpdatePhysicalFileSnapshotAsync(
                            connection,
                            transaction,
                            existing.PhysicalId,
                            entry,
                            cancellationToken);
                        await RefreshFileNamePoliciesAsync(
                            connection,
                            transaction,
                            runKey,
                            existing.NameId,
                            directoryPath,
                            volumeSerial,
                            entry,
                            cancellationToken);
                        continue;
                    }

                    await DeleteFileNameAsync(
                        connection,
                        transaction,
                        existing,
                        cancellationToken);
                }

                var physicalId = await FindPhysicalFileIdAsync(
                    connection,
                    runKey,
                    identityKey,
                    cancellationToken,
                    transaction);
                var isAlias = physicalId.HasValue;
                if (!physicalId.HasValue)
                {
                    physicalId = await InsertPhysicalFileSnapshotAsync(
                        connection,
                        transaction,
                        runKey,
                        identityKey,
                        volumeSerial,
                        entry,
                        cancellationToken);
                }

                if (!isAlias)
                {
                    newDirectAllocated += entry.AllocatedSize;
                }

                await InsertFileNameSnapshotAsync(
                    connection,
                    transaction,
                    runKey,
                    parentId.Value,
                    physicalId.Value,
                    entry,
                    isAlias,
                    directoryPath,
                    cancellationToken);
            }

            foreach (var existing in existingFiles)
            {
                if (!currentFiles.ContainsKey(existing.Key))
                {
                    await DeleteFileNameAsync(
                        connection,
                        transaction,
                        existing.Value,
                        cancellationToken);
                }
            }

            var logicalDelta = newDirectLogical - oldDirectLogical;
            var allocatedDelta = newDirectAllocated - oldDirectAllocated;
            var fileCountDelta = currentFiles.Count - oldDirectFiles;
            await UpdateFolderAncestorsAsync(
                connection,
                transaction,
                runKey,
                parentId.Value,
                logicalDelta,
                allocatedDelta,
                fileCountDelta,
                newDirectLogical,
                newDirectAllocated,
                currentFiles.Count,
                cancellationToken);
            await using (var duplicate = connection.CreateCommand())
            {
                duplicate.Transaction = transaction;
                duplicate.CommandText = """
                    DELETE FROM NormalizedCleanupCandidates
                    WHERE RunKey = $run AND PolicyId = $policy
                    """;
                duplicate.Parameters.AddWithValue("$run", runKey);
                duplicate.Parameters.AddWithValue(
                    "$policy",
                    (int)CleanupPolicyId.LargeDuplicates);
                await duplicate.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new DirectoryRefreshOutcome(true, string.Empty);
    }

    internal async Task<ScanTotals> FinalizeIncrementalSyncAsync(
        long runKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ScanRuns
                SET FilesScanned = (
                        SELECT COUNT(*) FROM FileNames WHERE RunKey = $run
                    ),
                    FoldersScanned = (
                        SELECT COUNT(*) FROM FolderNodes WHERE RunKey = $run
                    ),
                    BytesScanned = (
                        SELECT COALESCE(SUM(LogicalSize), 0)
                        FROM PhysicalFiles WHERE RunKey = $run
                    ),
                    AllocatedBytesScanned = (
                        SELECT COALESCE(SUM(AllocatedSize), 0)
                        FROM PhysicalFiles WHERE RunKey = $run
                    ),
                    CompletedUtc = $completed
                WHERE RunKey = $run AND Status = $completedStatus;

                DELETE FROM ScanCategorySummaries
                WHERE ScanId = (
                    SELECT Id FROM ScanRuns WHERE RunKey = $run
                );
                INSERT INTO ScanCategorySummaries (
                    ScanId, Category, SizeBytes, ItemCount
                )
                SELECT r.Id,
                       CASE n.Category
                           WHEN 0 THEN 'Apps & games'
                           WHEN 1 THEN 'Videos'
                           WHEN 2 THEN 'Photos'
                           WHEN 3 THEN 'System'
                           WHEN 4 THEN 'Documents'
                           WHEN 5 THEN 'Cache & temporary'
                           ELSE 'Other'
                       END,
                       SUM(CASE WHEN n.IsHardLinkAlias = 0
                                THEN p.AllocatedSize ELSE 0 END),
                       COUNT(*)
                FROM ScanRuns r
                INNER JOIN FileNames n ON n.RunKey = r.RunKey
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE r.RunKey = $run
                GROUP BY r.Id, n.Category;

                DELETE FROM ScanCleanupSummaries
                WHERE ScanId = (
                    SELECT Id FROM ScanRuns WHERE RunKey = $run
                );
                DELETE FROM NormalizedCleanupCandidates
                WHERE RunKey = $run
                  AND FileNameId IN (
                      SELECT Id
                      FROM FileNames
                      WHERE RunKey = $run AND IsHardLinkAlias = 1
                  );
                """;
            update.Parameters.AddWithValue("$run", runKey);
            update.Parameters.AddWithValue(
                "$completed",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue(
                "$completedStatus",
                (int)StorageScanStatus.Completed);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await RecordFolderGrowthSamplesAsync(
                connection,
                transaction,
                runKey,
                cancellationToken);
        }

        await using (var totals = connection.CreateCommand())
        {
            totals.Transaction = transaction;
            totals.CommandText = """
                SELECT FilesScanned, FoldersScanned, BytesScanned,
                       AllocatedBytesScanned
                FROM ScanRuns
                WHERE RunKey = $run AND Status = $completed
                """;
            totals.Parameters.AddWithValue("$run", runKey);
            totals.Parameters.AddWithValue(
                "$completed",
                (int)StorageScanStatus.Completed);
            await using var reader =
                await totals.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "The incremental scan totals disappeared.");
            }

            var result = new ScanTotals(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3));
            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
    }

    public async Task RecordFolderGrowthSamplesAsync(
        Guid scanId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using var key = connection.CreateCommand();
        key.Transaction = transaction;
        key.CommandText = "SELECT RunKey FROM ScanRuns WHERE Id = $scan";
        key.Parameters.AddWithValue("$scan", scanId.ToString("N"));
        var value = await key.ExecuteScalarAsync(cancellationToken);
        if (value is null || value == DBNull.Value)
        {
            throw new InvalidOperationException(
                "The scan run for folder growth sampling was not found.");
        }

        await RecordFolderGrowthSamplesAsync(
            connection,
            transaction,
            Convert.ToInt64(value),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RecordFolderGrowthSamplesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runKey,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO FolderGrowthSamples (
                VolumeKey, PathHash, SampleUtc, LogicalSize,
                AllocatedSize, FileCount
            )
            SELECT COALESCE(NULLIF(r.VolumeSerialNumber, ''), r.DriveLetter),
                   f.PathHash, $sample, f.LogicalSize, f.AllocatedSize,
                   f.FileCount
            FROM ScanRuns r
            INNER JOIN FolderNodes f ON f.RunKey = r.RunKey
            WHERE r.RunKey = $run
              AND f.AllocatedSize >= $minimum;

            DELETE FROM FolderGrowthSamples
            WHERE SampleUtc < $stale
              AND VolumeKey = (
                  SELECT COALESCE(NULLIF(VolumeSerialNumber, ''), DriveLetter)
                  FROM ScanRuns
                  WHERE RunKey = $run
              );
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$sample", now);
        command.Parameters.AddWithValue("$minimum", MinimumGrowthSampleBytes);
        command.Parameters.AddWithValue(
            "$stale",
            DateTimeOffset.UtcNow.AddDays(-35).ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> GetRunVolumeSerialAsync(
        long runKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT VolumeSerialNumber
            FROM ScanRuns
            WHERE RunKey = $run
            """;
        command.Parameters.AddWithValue("$run", runKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string text ||
            !long.TryParse(
                text,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var serial))
        {
            return 0;
        }

        return serial;
    }

    private static async Task<bool> HasOtherPhysicalNamesAsync(
        SqliteConnection connection,
        long physicalId,
        long fileNameId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM FileNames
                WHERE PhysicalFileId = $physical AND Id <> $name
            )
            """;
        command.Parameters.AddWithValue("$physical", physicalId);
        command.Parameters.AddWithValue("$name", fileNameId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task DeleteFileNameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExistingFileRow existing,
        CancellationToken cancellationToken)
    {
        await using (var deleteName = connection.CreateCommand())
        {
            deleteName.Transaction = transaction;
            deleteName.CommandText =
                "DELETE FROM FileNames WHERE Id = $id";
            deleteName.Parameters.AddWithValue("$id", existing.NameId);
            await deleteName.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var deletePhysical = connection.CreateCommand();
        deletePhysical.Transaction = transaction;
        deletePhysical.CommandText = """
            DELETE FROM PhysicalFiles
            WHERE Id = $id
              AND NOT EXISTS (
                  SELECT 1 FROM FileNames
                  WHERE PhysicalFileId = $id
              )
            """;
        deletePhysical.Parameters.AddWithValue("$id", existing.PhysicalId);
        await deletePhysical.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdatePhysicalFileSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long physicalId,
        NativeDirectoryEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE PhysicalFiles
            SET LogicalSize = $logical,
                AllocatedSize = $allocated,
                CreatedUtc = $created,
                ModifiedUtc = $modified,
                AccessedUtc = $accessed,
                Attributes = $attributes,
                ReparseTag = $reparse,
                ContentHash = CASE
                    WHEN LogicalSize <> $logical
                      OR ModifiedUtc <> $modified
                      OR ChangeTime <> $change
                    THEN NULL ELSE ContentHash END,
                SampleHash = CASE
                    WHEN LogicalSize <> $logical
                      OR ModifiedUtc <> $modified
                      OR ChangeTime <> $change
                    THEN NULL ELSE SampleHash END,
                HashState = CASE
                    WHEN LogicalSize <> $logical
                      OR ModifiedUtc <> $modified
                      OR ChangeTime <> $change
                    THEN 0 ELSE HashState END,
                ChangeTime = $change
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", physicalId);
        command.Parameters.AddWithValue("$logical", entry.LogicalSize);
        command.Parameters.AddWithValue("$allocated", entry.AllocatedSize);
        command.Parameters.AddWithValue(
            "$created",
            entry.CreatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$modified",
            entry.ModifiedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$accessed",
            entry.AccessedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$attributes", entry.Attributes);
        command.Parameters.AddWithValue(
            "$reparse",
            unchecked((long)entry.ReparsePointTag));
        command.Parameters.AddWithValue("$change", entry.ChangeTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<long?> FindPhysicalFileIdAsync(
        SqliteConnection connection,
        long runKey,
        string identityKey,
        CancellationToken cancellationToken,
        SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id
            FROM PhysicalFiles
            WHERE RunKey = $run AND FileIdentityKey = $identity
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$identity", identityKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value == DBNull.Value
            ? null
            : Convert.ToInt64(value);
    }

    private static async Task<long> InsertPhysicalFileSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runKey,
        string identityKey,
        long volumeSerial,
        NativeDirectoryEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PhysicalFiles (
                RunKey, FileIdentityKey, FileId128, VolumeSerialNumber,
                LogicalSize, AllocatedSize, CreatedUtc, ModifiedUtc,
                AccessedUtc, Attributes, ReparseTag, LinkCount,
                ChangeTime, LastSeenGeneration
            )
            VALUES (
                $run, $identity, $fileId, $volume, $logical, $allocated,
                $created, $modified, $accessed, $attributes, $reparse, 1,
                $change, 1
            )
            RETURNING Id
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$identity", identityKey);
        command.Parameters.AddWithValue("$fileId", entry.FileId);
        command.Parameters.AddWithValue("$volume", volumeSerial);
        command.Parameters.AddWithValue("$logical", entry.LogicalSize);
        command.Parameters.AddWithValue("$allocated", entry.AllocatedSize);
        command.Parameters.AddWithValue(
            "$created",
            entry.CreatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$modified",
            entry.ModifiedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$accessed",
            entry.AccessedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$attributes", entry.Attributes);
        command.Parameters.AddWithValue(
            "$reparse",
            unchecked((long)entry.ReparsePointTag));
        command.Parameters.AddWithValue("$change", entry.ChangeTime);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertFileNameSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runKey,
        long parentId,
        long physicalId,
        NativeDirectoryEntry entry,
        bool alias,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var fullPath = CombineDisplayPath(directoryPath, entry.Name);
        var extension = Path.GetExtension(entry.Name);
        var category = StorageCategoryClassifier.ClassifyPath(
            fullPath,
            extension,
            (FileAttributes)entry.Attributes);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO FileNames (
                RunKey, ParentId, PhysicalFileId, Name, Extension,
                Category, Tag, IsHardLinkAlias, LastSeenGeneration
            )
            VALUES (
                $run, $parent, $physical, $name, $extension, $category,
                $tag, $alias, 1
            )
            RETURNING Id
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$parent", parentId);
        command.Parameters.AddWithValue("$physical", physicalId);
        command.Parameters.AddWithValue("$name", entry.Name);
        command.Parameters.AddWithValue("$extension", extension);
        command.Parameters.AddWithValue(
            "$category",
            StorageCategoryClassifier.CategoryId(category));
        command.Parameters.AddWithValue(
            "$tag",
            StorageCategoryClassifier.TagId(
                StorageCategoryClassifier.TagPath(fullPath, category)));
        command.Parameters.AddWithValue("$alias", alias ? 1 : 0);
        var fileNameId = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
        var record = new StorageFileRecord
        {
            ScanId = Guid.Empty,
            Name = entry.Name,
            FullPath = fullPath,
            ParentPath = directoryPath,
            SizeBytes = entry.LogicalSize,
            AllocatedSizeBytes = entry.AllocatedSize,
            PhysicalSizeBytes = alias ? 0 : entry.AllocatedSize,
            CreatedUtc = entry.CreatedUtc,
            ModifiedUtc = entry.ModifiedUtc,
            AccessedUtc = entry.AccessedUtc,
            ChangeTime = entry.ChangeTime,
            Attributes = entry.Attributes,
            Extension = extension,
            Category = category,
            Tag = StorageCategoryClassifier.TagPath(fullPath, category),
            FileId128 = entry.FileId,
            ReparseTag = entry.ReparsePointTag,
            IsHardLinkAlias = alias,
        };
        foreach (var policy in GetMaterializedPolicies(record))
        {
            await using var candidate = connection.CreateCommand();
            candidate.Transaction = transaction;
            candidate.CommandText = """
                INSERT OR IGNORE INTO NormalizedCleanupCandidates (
                    RunKey, PolicyId, FileNameId
                ) VALUES ($run, $policy, $file)
                """;
            candidate.Parameters.AddWithValue("$run", runKey);
            candidate.Parameters.AddWithValue("$policy", (int)policy);
            candidate.Parameters.AddWithValue("$file", fileNameId);
            await candidate.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task RefreshFileNamePoliciesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runKey,
        long fileNameId,
        string directoryPath,
        long volumeSerial,
        NativeDirectoryEntry entry,
        CancellationToken cancellationToken)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM NormalizedCleanupCandidates
                WHERE RunKey = $run
                  AND FileNameId = $file
                  AND PolicyId <> $duplicate
                """;
            clear.Parameters.AddWithValue("$run", runKey);
            clear.Parameters.AddWithValue("$file", fileNameId);
            clear.Parameters.AddWithValue(
                "$duplicate",
                (int)CleanupPolicyId.LargeDuplicates);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        var fullPath = CombineDisplayPath(directoryPath, entry.Name);
        var extension = Path.GetExtension(entry.Name);
        var category = StorageCategoryClassifier.ClassifyPath(
            fullPath,
            extension,
            (FileAttributes)entry.Attributes);
        var record = new StorageFileRecord
        {
            ScanId = Guid.Empty,
            Name = entry.Name,
            FullPath = fullPath,
            ParentPath = directoryPath,
            SizeBytes = entry.LogicalSize,
            AllocatedSizeBytes = entry.AllocatedSize,
            PhysicalSizeBytes = entry.AllocatedSize,
            CreatedUtc = entry.CreatedUtc,
            ModifiedUtc = entry.ModifiedUtc,
            AccessedUtc = entry.AccessedUtc,
            ChangeTime = entry.ChangeTime,
            Attributes = entry.Attributes,
            Extension = extension,
            Category = category,
            Tag = StorageCategoryClassifier.TagPath(fullPath, category),
            VolumeSerialNumber = volumeSerial,
            FileId128 = entry.FileId,
            ReparseTag = entry.ReparsePointTag,
        };
        foreach (var policy in GetMaterializedPolicies(record))
        {
            await using var candidate = connection.CreateCommand();
            candidate.Transaction = transaction;
            candidate.CommandText = """
                INSERT OR IGNORE INTO NormalizedCleanupCandidates (
                    RunKey, PolicyId, FileNameId
                ) VALUES ($run, $policy, $file)
                """;
            candidate.Parameters.AddWithValue("$run", runKey);
            candidate.Parameters.AddWithValue("$policy", (int)policy);
            candidate.Parameters.AddWithValue("$file", fileNameId);
            await candidate.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdateFolderAncestorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runKey,
        long folderId,
        long logicalDelta,
        long allocatedDelta,
        long fileCountDelta,
        long newDirectLogical,
        long newDirectAllocated,
        long newDirectFileCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE Ancestors(Id, ParentId) AS (
                SELECT Id, ParentId
                FROM FolderNodes
                WHERE RunKey = $run AND Id = $folder
                UNION ALL
                SELECT f.Id, f.ParentId
                FROM FolderNodes f
                INNER JOIN Ancestors a ON f.Id = a.ParentId
                WHERE f.RunKey = $run
            )
            UPDATE FolderNodes
            SET LogicalSize = MAX(0, LogicalSize + $logicalDelta),
                AllocatedSize = MAX(0, AllocatedSize + $allocatedDelta),
                FileCount = MAX(0, FileCount + $fileDelta),
                DirectLogicalSize = CASE WHEN Id = $folder
                    THEN $newLogical ELSE DirectLogicalSize END,
                DirectAllocatedSize = CASE WHEN Id = $folder
                    THEN $newAllocated ELSE DirectAllocatedSize END,
                DirectFileCount = CASE WHEN Id = $folder
                    THEN $newFileCount ELSE DirectFileCount END,
                ModifiedUtc = $modified
            WHERE Id IN (SELECT Id FROM Ancestors)
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$logicalDelta", logicalDelta);
        command.Parameters.AddWithValue("$allocatedDelta", allocatedDelta);
        command.Parameters.AddWithValue("$fileDelta", fileCountDelta);
        command.Parameters.AddWithValue("$newLogical", newDirectLogical);
        command.Parameters.AddWithValue("$newAllocated", newDirectAllocated);
        command.Parameters.AddWithValue("$newFileCount", newDirectFileCount);
        command.Parameters.AddWithValue(
            "$modified",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StorageScanWriteSession> BeginScanAsync(
        DriveSummary drive,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        var owner = ScanOwner.Current;
        await using (var resume = connection.CreateCommand())
        {
            resume.CommandText = """
                SELECT r.Id, r.RunKey
                FROM ScanRuns r
                WHERE r.DriveLetter = $drive
                  AND r.ScanFormat >= 4
                  AND r.Status IN ($failed, $cancelled)
                  AND r.RunKey IS NOT NULL
                  AND (
                      EXISTS (
                          SELECT 1
                          FROM ScanWorkItems w
                          WHERE w.RunKey = r.RunKey
                      )
                      OR r.ScanPhase IN ('Finalizing', 'Completing')
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ScanRuns completed
                      WHERE completed.DriveLetter = r.DriveLetter
                        AND completed.Status = $completed
                        AND completed.StartedUtc > r.StartedUtc
                  )
                ORDER BY r.StartedUtc DESC
                LIMIT 1
                """;
            resume.Parameters.AddWithValue("$drive", drive.Letter);
            resume.Parameters.AddWithValue(
                "$failed",
                (int)StorageScanStatus.Failed);
            resume.Parameters.AddWithValue(
                "$cancelled",
                (int)StorageScanStatus.Cancelled);
            resume.Parameters.AddWithValue(
                "$completed",
                (int)StorageScanStatus.Completed);
            await using var reader = await resume.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var resumedScanId = Guid.ParseExact(reader.GetString(0), "N");
                var runKey = reader.GetInt64(1);
                reader.Close();
                await ResetRunningWorkAndClaimAsync(
                    connection,
                    resumedScanId,
                    runKey,
                    owner,
                    cancellationToken);
                return new StorageScanWriteSession(
                    connection,
                    resumedScanId,
                    owner,
                    runKey,
                    resumed: true);
            }
        }

        var scanId = Guid.NewGuid();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO ScanRuns (
                    Id, DriveLetter, DriveName, FileSystem, TotalBytes, UsedBytes,
                    FreeBytes, StartedUtc, Status, FilesScanned, FoldersScanned,
                    BytesScanned, InaccessibleItems, OwnerProcessId,
                    OwnerProcessStartedUtc, LeaseHeartbeatUtc, Generation,
                    VolumeSerialNumber, ScanFormat
                )
                VALUES (
                    $id, $drive, $name, $fileSystem, $total, $used,
                    $free, $started, $status, 0, 0, 0, 0, $pid,
                    $processStarted, $heartbeat, 1, $volumeSerial, 4
                );
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$id", scanId.ToString("N"));
            command.Parameters.AddWithValue("$drive", drive.Letter);
            command.Parameters.AddWithValue("$name", drive.Name);
            command.Parameters.AddWithValue("$fileSystem", drive.FileSystem);
            command.Parameters.AddWithValue("$total", drive.TotalBytes);
            command.Parameters.AddWithValue("$used", drive.UsedBytes);
            command.Parameters.AddWithValue("$free", drive.FreeBytes);
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$status", (int)StorageScanStatus.Running);
            command.Parameters.AddWithValue("$pid", owner.ProcessId);
            command.Parameters.AddWithValue(
                "$processStarted",
                owner.ProcessStartedUtc);
            command.Parameters.AddWithValue(
                "$heartbeat",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue(
                "$volumeSerial",
                VolumeIdentityService.TryGetVolumeSerial(drive.Letter) ??
                string.Empty);
            var runKey = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken));
            await using var keyCommand = connection.CreateCommand();
            keyCommand.CommandText = """
                UPDATE ScanRuns
                SET RunKey = $key
                WHERE rowid = $key
                """;
            keyCommand.Parameters.AddWithValue("$key", runKey);
            if (await keyCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "The normalized scan run key could not be assigned.");
            }

            return new StorageScanWriteSession(
                connection,
                scanId,
                owner,
                runKey);
        }
    }

    private static async Task ResetRunningWorkAndClaimAsync(
        SqliteConnection connection,
        Guid scanId,
        long runKey,
        ScanOwner owner,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE ScanRuns
                SET Status = $running,
                    CompletedUtc = NULL,
                    ErrorMessage = NULL,
                    FilesScanned = (
                        SELECT COUNT(*)
                        FROM FileNames
                        WHERE RunKey = $run),
                    FoldersScanned = (
                        SELECT COUNT(*)
                        FROM FolderNodes
                        WHERE RunKey = $run),
                    BytesScanned = COALESCE((
                        SELECT SUM(LogicalSize)
                        FROM PhysicalFiles
                        WHERE RunKey = $run), 0),
                    AllocatedBytesScanned = COALESCE((
                        SELECT SUM(AllocatedSize)
                        FROM PhysicalFiles
                        WHERE RunKey = $run), 0),
                    OwnerProcessId = $pid,
                    OwnerProcessStartedUtc = $started,
                    LeaseHeartbeatUtc = $heartbeat,
                    ScanPhase = 'Resuming',
                    PhaseStartedUtc = $heartbeat
                WHERE Id = $id
                  AND RunKey = $run
                  AND Status IN ($failed, $cancelled)
                """;
            claim.Parameters.AddWithValue("$run", runKey);
            claim.Parameters.AddWithValue(
                "$running",
                (int)StorageScanStatus.Running);
            claim.Parameters.AddWithValue("$id", scanId.ToString("N"));
            claim.Parameters.AddWithValue("$pid", owner.ProcessId);
            claim.Parameters.AddWithValue(
                "$started",
                owner.ProcessStartedUtc);
            claim.Parameters.AddWithValue(
                "$heartbeat",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            claim.Parameters.AddWithValue(
                "$failed",
                (int)StorageScanStatus.Failed);
            claim.Parameters.AddWithValue(
                "$cancelled",
                (int)StorageScanStatus.Cancelled);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "The interrupted scan could not be claimed for resume.");
            }
        }

        await using (var reset = connection.CreateCommand())
        {
            reset.Transaction = transaction;
            reset.CommandText = """
                UPDATE ScanWorkItems
                SET State = 0,
                    AttemptCount = AttemptCount + 1
                WHERE RunKey = $run AND State = 1
                """;
            reset.Parameters.AddWithValue("$run", runKey);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StorageCategory>> GetCategoriesAsync(
        Guid scanId,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedCategoriesAsync(
                format.RunKey,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var categories = new List<StorageCategory>();
        await using (var cached = connection.CreateCommand())
        {
            cached.CommandText = """
                SELECT Category, SizeBytes, ItemCount
                FROM ScanCategorySummaries
                WHERE ScanId = $scanId
                ORDER BY SizeBytes DESC
                """;
            cached.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
            await using var reader = await cached.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                AddCategory(categories, reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        if (categories.Count == 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Category, SUM(SizeBytes), COUNT(*)
                FROM FileEntries
                WHERE ScanId = $scanId
                GROUP BY Category
                ORDER BY SUM(SizeBytes) DESC
                """;
            command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    AddCategory(
                        categories,
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2));
                }
            }

            await SaveCategorySummariesAsync(
                connection,
                scanId,
                categories,
                cancellationToken);
        }

        var totalBytes = Math.Max(1, categories.Sum(category => category.SizeBytes));
        foreach (var category in categories)
        {
            category.Percentage = (double)category.SizeBytes / totalBytes * 100;
        }

        return categories;
    }

    private async Task<IReadOnlyList<StorageCategory>> GetNormalizedCategoriesAsync(
        long runKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.Category, SUM(p.AllocatedSize), COUNT(*)
            FROM FileNames n
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE n.RunKey = $run
              AND n.IsHardLinkAlias = 0
            GROUP BY n.Category
            ORDER BY SUM(p.AllocatedSize) DESC
            """;
        command.Parameters.AddWithValue("$run", runKey);
        var categories = new List<StorageCategory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            AddCategory(
                categories,
                StorageCategoryClassifier.CategoryName(reader.GetInt32(0)),
                reader.GetInt64(1),
                reader.GetInt64(2));
        }

        var totalBytes = Math.Max(1, categories.Sum(category => category.SizeBytes));
        foreach (var category in categories)
        {
            category.Percentage = (double)category.SizeBytes / totalBytes * 100;
        }

        return categories;
    }

    public async Task<IReadOnlyList<StorageItem>> GetChildrenAsync(
        Guid scanId,
        string parentPath,
        string? category = null,
        string? searchText = null,
        int sortIndex = 0,
        bool includeSystem = true,
        bool includeHidden = true,
        int limit = 500,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var scanFormat = await GetScanFormatAsync(scanId, cancellationToken);
        if (scanFormat.Format >= 4)
        {
            return await GetNormalizedChildrenAsync(
                scanFormat.RunKey,
                parentPath,
                category,
                searchText,
                sortIndex,
                includeSystem,
                includeHidden,
                limit,
                offset,
                cancellationToken);
        }

        var parameters = new List<SqliteParameter>
        {
            new("$scanId", scanId.ToString("N")),
            new("$parentPath", NormalizeFolderPath(parentPath)),
            new("$limit", limit),
            new("$offset", offset),
        };
        var filters = new List<string>
        {
            "ScanId = $scanId",
            "ParentPath = $parentPath",
        };
        if (!string.IsNullOrWhiteSpace(category))
        {
            filters.Add("Category = $category COLLATE NOCASE");
            parameters.Add(new SqliteParameter("$category", category));
        }
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            filters.Add("(Name LIKE $search ESCAPE '\\' OR FullPath LIKE $search ESCAPE '\\')");
            parameters.Add(new SqliteParameter("$search", $"%{EscapeLike(searchText)}%"));
        }
        AddVisibilityFilters(filters, parameters, includeSystem, includeHidden);

        var order = sortIndex switch
        {
            1 => "ModifiedUtc DESC",
            2 => "Name COLLATE NOCASE ASC",
            _ => "SizeBytes DESC",
        };

        var where = string.Join(" AND ", filters);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH Items AS (
                SELECT 1 AS IsFolder, FullPath, Name, SizeBytes, ModifiedUtc, Category,
                       CASE WHEN Depth <= 1 THEN 'Drive' ELSE Tag END AS Kind, Attributes
                FROM FolderEntries
                WHERE {where.Replace("ParentPath", "ParentPath")}

                UNION ALL

                SELECT 0 AS IsFolder, FullPath, Name, SizeBytes, ModifiedUtc, Category,
                       CASE WHEN LENGTH(Extension) > 0 THEN Extension ELSE 'File' END AS Kind,
                       Attributes
                FROM FileEntries
                WHERE {where}
            )
            SELECT IsFolder, FullPath, Name, SizeBytes, ModifiedUtc, Category, Kind, Attributes
            FROM Items
            ORDER BY {order}, Name COLLATE NOCASE
            LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadStorageItemsAsync(reader, cancellationToken);
    }

    public async Task<IReadOnlyList<StorageItem>> GetChildrenForNavigationAsync(
        Guid scanId,
        string parentPath,
        string selectedItemPath,
        int sortIndex = 0,
        bool includeSystem = true,
        bool includeHidden = true,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var items = await GetChildrenAsync(
            scanId,
            parentPath,
            sortIndex: sortIndex,
            includeSystem: includeSystem,
            includeHidden: includeHidden,
            limit: limit,
            cancellationToken: cancellationToken);
        var targetParent = Path.GetDirectoryName(selectedItemPath);
        if (limit <= 0 ||
            string.IsNullOrWhiteSpace(targetParent) ||
            items.Any(item => string.Equals(
                item.Path,
                selectedItemPath,
                StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(
                NormalizeFolderPath(targetParent),
                NormalizeFolderPath(parentPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return items;
        }

        var selected = await GetItemByPathAsync(
            scanId,
            selectedItemPath,
            cancellationToken);
        if (selected is null)
        {
            return items;
        }

        // Keep explicit navigation targets without loading an entire large directory.
        return items.Append(selected).ToArray();
    }

    private async Task<IReadOnlyList<StorageItem>> GetNormalizedChildrenAsync(
        long runKey,
        string parentPath,
        string? category,
        string? searchText,
        int sortIndex,
        bool includeSystem,
        bool includeHidden,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var parentId = await ResolveFolderIdAsync(
            connection,
            runKey,
            parentPath,
            cancellationToken);
        if (!parentId.HasValue)
        {
            return [];
        }

        var items = new List<StorageItem>();
        await using (var folders = connection.CreateCommand())
        {
            folders.CommandText = """
                SELECT f.Id, f.Name, f.LogicalSize, f.AllocatedSize, f.ModifiedUtc,
                       f.Category, f.Tag, f.Attributes, f.ReparseTag, f.IsIncomplete,
                       EXISTS (
                           SELECT 1 FROM FolderNodes child
                           WHERE child.RunKey = f.RunKey
                             AND child.ParentId = f.Id
                       )
                FROM FolderNodes f
                WHERE f.RunKey = $run AND f.ParentId = $parent
                ORDER BY f.AllocatedSize DESC, f.Name
                """;
            folders.Parameters.AddWithValue("$run", runKey);
            folders.Parameters.AddWithValue("$parent", parentId.Value);
            await using var reader = await folders.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var categoryName =
                    StorageCategoryClassifier.CategoryName(reader.GetInt32(5));
                var attributes = (FileAttributes)reader.GetInt64(7);
                if ((!includeSystem &&
                     ((categoryName == "System") ||
                      (attributes & FileAttributes.System) != 0)) ||
                    (!includeHidden &&
                     (attributes & FileAttributes.Hidden) != 0) ||
                    (!string.IsNullOrWhiteSpace(category) &&
                     !string.Equals(
                         categoryName,
                         category,
                         StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(searchText) &&
                     reader.GetString(1).Contains(
                         searchText,
                         StringComparison.OrdinalIgnoreCase) == false))
                {
                    continue;
                }

                items.Add(new StorageItem
                {
                    Name = reader.GetString(1),
                    Path = CombineDisplayPath(parentPath, reader.GetString(1)),
                    Category = categoryName,
                    Kind = StorageCategoryClassifier.TagName(reader.GetInt32(6)),
                    ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                        reader.GetInt64(4)),
                    SizeBytes = reader.GetInt64(2),
                    AllocatedSizeBytes = reader.GetInt64(3),
                    LogicalSizeBytes = reader.GetInt64(2),
                    IsFolder = true,
                    IsSystem = (attributes & FileAttributes.System) != 0,
                    IsIncomplete = reader.GetBoolean(9),
                });
            }
        }

        await using (var files = connection.CreateCommand())
        {
            files.CommandText = """
                SELECT n.Id, n.Name, n.Extension, n.Category, n.Tag,
                       n.IsHardLinkAlias, p.LogicalSize, p.AllocatedSize,
                       p.ModifiedUtc, p.Attributes, p.ReparseTag
                FROM FileNames n
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE n.RunKey = $run AND n.ParentId = $parent
                ORDER BY p.AllocatedSize DESC, n.Name
                """;
            files.Parameters.AddWithValue("$run", runKey);
            files.Parameters.AddWithValue("$parent", parentId.Value);
            await using var reader = await files.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var categoryName =
                    StorageCategoryClassifier.CategoryName(reader.GetInt32(3));
                var attributes = (FileAttributes)reader.GetInt64(9);
                if ((!includeSystem &&
                     ((categoryName == "System") ||
                      (attributes & FileAttributes.System) != 0)) ||
                    (!includeHidden &&
                     (attributes & FileAttributes.Hidden) != 0) ||
                    (!string.IsNullOrWhiteSpace(category) &&
                     !string.Equals(
                         categoryName,
                         category,
                         StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(searchText) &&
                     reader.GetString(1).Contains(
                         searchText,
                         StringComparison.OrdinalIgnoreCase) == false))
                {
                    continue;
                }

                var hardAlias = reader.GetBoolean(5);
                items.Add(new StorageItem
                {
                    Name = reader.GetString(1),
                    Path = CombineDisplayPath(parentPath, reader.GetString(1)),
                    Category = categoryName,
                    Kind = string.IsNullOrWhiteSpace(reader.GetString(2))
                        ? "File"
                        : reader.GetString(2).TrimStart('.').ToUpperInvariant() +
                          " file",
                    ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                        reader.GetInt64(8)),
                    SizeBytes = reader.GetInt64(6),
                    AllocatedSizeBytes = hardAlias ? 0 : reader.GetInt64(7),
                    LogicalSizeBytes = reader.GetInt64(6),
                    IsFolder = false,
                    IsSystem = (attributes & FileAttributes.System) != 0,
                    IsCloudOnly =
                        unchecked((uint)reader.GetInt64(10)) ==
                        StorageReparseTags.CloudFiles,
                    IsHardLinkAlias = hardAlias,
                });
            }
        }

        var sorted = sortIndex switch
        {
            1 => items
                .OrderByDescending(item => item.ModifiedAt)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            2 => items.OrderBy(
                item => item.Name,
                StringComparer.OrdinalIgnoreCase),
            _ => items
                .OrderByDescending(item => item.DisplaySizeBytes)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
        };
        return sorted
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    private static async Task<long?> ResolveFolderIdAsync(
        SqliteConnection connection,
        long runKey,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = NormalizeFolderPath(path);
        var root = Path.GetPathRoot(fullPath) ??
                   throw new ArgumentException("The folder path has no root.", nameof(path));
        var rootName = root.TrimEnd('\\');
        long? parentId = null;
        await using (var rootCommand = connection.CreateCommand())
        {
            rootCommand.CommandText = """
                SELECT Id
                FROM FolderNodes
                WHERE RunKey = $run AND ParentId IS NULL AND Name = $name
                """;
            rootCommand.Parameters.AddWithValue("$run", runKey);
            rootCommand.Parameters.AddWithValue("$name", rootName);
            var rootId = await rootCommand.ExecuteScalarAsync(cancellationToken);
            if (rootId is null || rootId == DBNull.Value)
            {
                return null;
            }

            parentId = Convert.ToInt64(rootId);
        }

        var relative = fullPath[root.Length..]
            .Trim('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in relative)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id
                FROM FolderNodes
                WHERE RunKey = $run
                  AND ParentId = $parent
                  AND Name = $name
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$run", runKey);
            command.Parameters.AddWithValue("$parent", parentId);
            command.Parameters.AddWithValue("$name", segment);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null || value == DBNull.Value)
            {
                return null;
            }

            parentId = Convert.ToInt64(value);
        }

        return parentId;
    }

    private static string CombineDisplayPath(string parentPath, string name) =>
        Path.Combine(
            parentPath.EndsWith('\\')
                ? parentPath
                : parentPath + "\\",
            name);

    public async Task<IReadOnlyList<StorageItem>> SearchAsync(
        Guid scanId,
        string searchText,
        string? category = null,
        int sortIndex = 0,
        bool includeSystem = true,
        bool includeHidden = true,
        int limit = 500,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await SearchNormalizedAsync(
                format.RunKey,
                searchText,
                category,
                sortIndex,
                includeSystem,
                includeHidden,
                limit,
                offset,
                cancellationToken);
        }

        var filters = new List<string> { "ScanId = $scanId" };
        var parameters = new List<SqliteParameter>
        {
            new("$scanId", scanId.ToString("N")),
            new("$search", $"%{EscapeLike(searchText)}%"),
            new("$limit", limit),
            new("$offset", offset),
        };
        filters.Add("(Name LIKE $search ESCAPE '\\' OR FullPath LIKE $search ESCAPE '\\')");
        if (!string.IsNullOrWhiteSpace(category))
        {
            filters.Add("Category = $category COLLATE NOCASE");
            parameters.Add(new SqliteParameter("$category", category));
        }
        AddVisibilityFilters(filters, parameters, includeSystem, includeHidden);

        var order = sortIndex switch
        {
            1 => "ModifiedUtc DESC",
            2 => "Name COLLATE NOCASE ASC",
            _ => "SizeBytes DESC",
        };
        var where = string.Join(" AND ", filters);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH Items AS (
                SELECT 1 AS IsFolder, FullPath, Name, SizeBytes, ModifiedUtc, Category, Tag AS Kind, Attributes
                FROM FolderEntries
                WHERE {where}
                UNION ALL
                SELECT 0 AS IsFolder, FullPath, Name, SizeBytes, ModifiedUtc, Category,
                       CASE WHEN LENGTH(Extension) > 0 THEN Extension ELSE 'File' END AS Kind,
                       Attributes
                FROM FileEntries
                WHERE {where}
            )
            SELECT IsFolder, FullPath, Name, SizeBytes, ModifiedUtc, Category, Kind, Attributes
            FROM Items
            ORDER BY {order}, Name COLLATE NOCASE
            LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadStorageItemsAsync(reader, cancellationToken);
    }

    private async Task<IReadOnlyList<StorageItem>> SearchNormalizedAsync(
        long runKey,
        string searchText,
        string? category,
        int sortIndex,
        bool includeSystem,
        bool includeHidden,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var results = new List<StorageItem>();
        var escaped = EscapeLike(searchText);
        var queryLimit = Math.Min(10_000, checked(limit + offset));
        var folderRows = new List<(
            long Id,
            string Name,
            long Logical,
            long Allocated,
            long Modified,
            int Category,
            int Tag,
            long Attributes,
            bool IsIncomplete)>();
        await using var folders = connection.CreateCommand();
        folders.CommandText = """
            SELECT Id, ParentId, Name, LogicalSize, AllocatedSize, ModifiedUtc,
                   Category, Tag, Attributes, ReparseTag, IsIncomplete
            FROM FolderNodes
            WHERE RunKey = $run
              AND Name LIKE $search ESCAPE '\'
              AND ($includeSystem = 1 OR Category <> $system)
              AND ($includeHidden = 1 OR (Attributes & $hidden) = 0)
              AND ($category < 0 OR Category = $category)
            ORDER BY AllocatedSize DESC, Name
            LIMIT $limit
            """;
        folders.Parameters.AddWithValue("$run", runKey);
        folders.Parameters.AddWithValue("$search", $"%{escaped}%");
        folders.Parameters.AddWithValue("$includeSystem", includeSystem ? 1 : 0);
        folders.Parameters.AddWithValue(
            "$system",
            StorageCategoryClassifier.CategoryId("System"));
        folders.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
        folders.Parameters.AddWithValue("$hidden", (long)FileAttributes.Hidden);
        folders.Parameters.AddWithValue(
            "$category",
            string.IsNullOrWhiteSpace(category)
                ? -1
                : StorageCategoryClassifier.CategoryId(category));
        folders.Parameters.AddWithValue("$limit", queryLimit);
        await using (var reader = await folders.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                folderRows.Add((
                    reader.GetInt64(0),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt64(8),
                    reader.GetBoolean(10)));
            }
        }

        foreach (var row in folderRows)
        {
            results.Add(new StorageItem
            {
                EntryId = row.Id,
                Name = row.Name,
                Path = await BuildFolderPathAsync(
                    connection,
                    runKey,
                    row.Id,
                    cancellationToken),
                Category =
                    StorageCategoryClassifier.CategoryName(row.Category),
                Kind = StorageCategoryClassifier.TagName(row.Tag),
                ModifiedAt =
                    DateTimeOffset.FromUnixTimeMilliseconds(row.Modified),
                SizeBytes = row.Logical,
                LogicalSizeBytes = row.Logical,
                AllocatedSizeBytes = row.Allocated,
                IsFolder = true,
                IsSystem =
                    ((FileAttributes)row.Attributes & FileAttributes.System) != 0,
                IsIncomplete = row.IsIncomplete,
            });
        }

        var fileRows = new List<(
            long Id,
            long ParentId,
            string Name,
            string Extension,
            int Category,
            int Tag,
            bool IsAlias,
            long Logical,
            long Allocated,
            long Modified,
            long Attributes,
            long Reparse)>();
        await using var files = connection.CreateCommand();
        files.CommandText = """
            SELECT n.Id, n.ParentId, n.Name, n.Extension, n.Category, n.Tag,
                   n.IsHardLinkAlias, p.LogicalSize, p.AllocatedSize,
                   p.ModifiedUtc, p.Attributes, p.ReparseTag
            FROM FileNames n
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE n.RunKey = $run
              AND n.Name LIKE $search ESCAPE '\'
              AND ($includeSystem = 1 OR n.Category <> $system)
              AND ($includeHidden = 1 OR (p.Attributes & $hidden) = 0)
              AND ($category < 0 OR n.Category = $category)
            ORDER BY p.AllocatedSize DESC, n.Name
            LIMIT $limit
            """;
        files.Parameters.AddWithValue("$run", runKey);
        files.Parameters.AddWithValue("$search", $"%{escaped}%");
        files.Parameters.AddWithValue("$includeSystem", includeSystem ? 1 : 0);
        files.Parameters.AddWithValue(
            "$system",
            StorageCategoryClassifier.CategoryId("System"));
        files.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
        files.Parameters.AddWithValue("$hidden", (long)FileAttributes.Hidden);
        files.Parameters.AddWithValue(
            "$category",
            string.IsNullOrWhiteSpace(category)
                ? -1
                : StorageCategoryClassifier.CategoryId(category));
        files.Parameters.AddWithValue("$limit", queryLimit);
        await using (var reader = await files.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                fileRows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetBoolean(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11)));
            }
        }

        foreach (var row in fileRows)
        {
            results.Add(new StorageItem
            {
                EntryId = row.Id,
                Name = row.Name,
                Path = await BuildFilePathAsync(
                    connection,
                    runKey,
                    row.ParentId,
                    row.Name,
                    cancellationToken),
                Category =
                    StorageCategoryClassifier.CategoryName(row.Category),
                Kind = string.IsNullOrWhiteSpace(row.Extension)
                    ? "File"
                    : row.Extension.TrimStart('.').ToUpperInvariant() + " file",
                ModifiedAt =
                    DateTimeOffset.FromUnixTimeMilliseconds(row.Modified),
                SizeBytes = row.Logical,
                LogicalSizeBytes = row.Logical,
                AllocatedSizeBytes = row.IsAlias ? 0 : row.Allocated,
                IsFolder = false,
                IsSystem =
                    ((FileAttributes)row.Attributes & FileAttributes.System) != 0,
                IsCloudOnly =
                    unchecked((uint)row.Reparse) ==
                    StorageReparseTags.CloudFiles,
                IsHardLinkAlias = row.IsAlias,
            });
        }

        IEnumerable<StorageItem> sorted = sortIndex switch
        {
            1 => results.OrderByDescending(item => item.ModifiedAt),
            2 => results.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => results.OrderByDescending(item => item.DisplaySizeBytes),
        };
        return sorted.Skip(offset).Take(limit).ToList();
    }

    private static async Task<string> BuildFolderPathAsync(
        SqliteConnection connection,
        long runKey,
        long folderId,
        CancellationToken cancellationToken)
    {
        var paths = await BuildFolderPathsAsync(
            connection,
            runKey,
            [folderId],
            cancellationToken);
        return paths.GetValueOrDefault(folderId) ?? string.Empty;
    }

    private static async Task<IReadOnlyDictionary<long, string>>
        BuildFolderPathsAsync(
            SqliteConnection connection,
            long runKey,
            IReadOnlyCollection<long> folderIds,
            CancellationToken cancellationToken)
    {
        var ids = folderIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, string>();
        }

        await using var command = connection.CreateCommand();
        var parameters = ids
            .Select((_, index) => "$id" + index)
            .ToArray();
        command.CommandText = $"""
            WITH RECURSIVE
            Ancestors(Id, ParentId) AS (
                SELECT Id, ParentId
                FROM FolderNodes
                WHERE RunKey = $run
                  AND Id IN ({string.Join(", ", parameters)})
                UNION
                SELECT f.Id, f.ParentId
                FROM FolderNodes f
                INNER JOIN Ancestors a ON f.Id = a.ParentId
                WHERE f.RunKey = $run
            ),
            Paths(Id, Path) AS (
                SELECT f.Id,
                       CASE WHEN f.ParentId IS NULL
                            THEN f.Name || '\'
                            ELSE f.Name END
                FROM FolderNodes f
                INNER JOIN Ancestors a ON a.Id = f.Id
                WHERE f.RunKey = $run AND f.ParentId IS NULL
                UNION ALL
                SELECT f.Id,
                       p.Path ||
                       CASE WHEN SUBSTR(p.Path, LENGTH(p.Path), 1) = '\'
                            THEN '' ELSE '\' END ||
                       f.Name
                FROM FolderNodes f
                INNER JOIN Ancestors a ON a.Id = f.Id
                INNER JOIN Paths p ON p.Id = f.ParentId
                WHERE f.RunKey = $run
            )
            SELECT Id, Path
            FROM Paths
            WHERE Id IN ({string.Join(", ", parameters)})
            """;
        command.Parameters.AddWithValue("$run", runKey);
        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], ids[index]);
        }

        var paths = new Dictionary<long, string>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths[reader.GetInt64(0)] = reader.GetString(1);
        }

        return paths;
    }

    private static async Task<string> BuildFilePathAsync(
        SqliteConnection connection,
        long runKey,
        long parentId,
        string name,
        CancellationToken cancellationToken) =>
        CombineDisplayPath(
            await BuildFolderPathAsync(
                connection,
                runKey,
                parentId,
                cancellationToken),
            name);

    public async Task<StorageItem?> GetItemByPathAsync(
        Guid scanId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedItemByPathAsync(
                format.RunKey,
                path,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1, FullPath, Name, SizeBytes, ModifiedUtc, Category, Tag, Attributes
            FROM FolderEntries
            WHERE ScanId = $scanId AND FullPath = $path
            UNION ALL
            SELECT 0, FullPath, Name, SizeBytes, ModifiedUtc, Category, Extension, Attributes
            FROM FileEntries
            WHERE ScanId = $scanId AND FullPath = $path
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$path", path.TrimEnd('\\'));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadStorageItem(reader)
            : null;
    }

    private async Task<StorageItem?> GetNormalizedItemByPathAsync(
        long runKey,
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var normalized = StorageRepository.NormalizeFolderPath(path);
        var fileId = await ResolveFolderIdAsync(
            connection,
            runKey,
            normalized,
            cancellationToken);
        if (fileId.HasValue)
        {
            await using var folder = connection.CreateCommand();
            folder.CommandText = """
                SELECT Name, LogicalSize, AllocatedSize, ModifiedUtc,
                       Category, Tag, Attributes, IsIncomplete
                FROM FolderNodes
                WHERE RunKey = $run AND Id = $id
                """;
            folder.Parameters.AddWithValue("$run", runKey);
            folder.Parameters.AddWithValue("$id", fileId.Value);
            await using var reader = await folder.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var attributes = (FileAttributes)reader.GetInt64(6);
                return new StorageItem
                {
                    EntryId = fileId.Value,
                    Name = reader.GetString(0),
                    Path = normalized,
                    Category = StorageCategoryClassifier.CategoryName(
                        reader.GetInt32(4)),
                    Kind = StorageCategoryClassifier.TagName(reader.GetInt32(5)),
                    ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                        reader.GetInt64(3)),
                    SizeBytes = reader.GetInt64(1),
                    LogicalSizeBytes = reader.GetInt64(1),
                    AllocatedSizeBytes = reader.GetInt64(2),
                    IsFolder = true,
                    IsSystem = (attributes & FileAttributes.System) != 0,
                    IsIncomplete = reader.GetBoolean(7),
                };
            }
        }

        var trimmed = Path.GetFullPath(path).TrimEnd('\\');
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var parentId = await ResolveFolderIdAsync(
            connection,
            runKey,
            parent,
            cancellationToken);
        if (!parentId.HasValue)
        {
            return null;
        }

        await using var file = connection.CreateCommand();
        file.CommandText = """
            SELECT n.Id, n.Name, n.Extension, n.Category, n.Tag,
                   n.IsHardLinkAlias, p.LogicalSize, p.AllocatedSize,
                   p.ModifiedUtc, p.Attributes, p.ReparseTag
            FROM FileNames n
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE n.RunKey = $run
              AND n.ParentId = $parent
              AND n.Name = $name
            LIMIT 1
            """;
        file.Parameters.AddWithValue("$run", runKey);
        file.Parameters.AddWithValue("$parent", parentId.Value);
        file.Parameters.AddWithValue("$name", Path.GetFileName(trimmed));
        await using var fileReader = await file.ExecuteReaderAsync(cancellationToken);
        if (!await fileReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var attributesValue = (FileAttributes)fileReader.GetInt64(9);
        var alias = fileReader.GetBoolean(5);
        return new StorageItem
        {
            EntryId = fileReader.GetInt64(0),
            Name = fileReader.GetString(1),
            Path = Path.GetFullPath(path),
            Category = StorageCategoryClassifier.CategoryName(
                fileReader.GetInt32(3)),
            Kind = string.IsNullOrWhiteSpace(fileReader.GetString(2))
                ? "File"
                : fileReader.GetString(2).TrimStart('.').ToUpperInvariant() +
                  " file",
            ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                fileReader.GetInt64(8)),
            SizeBytes = fileReader.GetInt64(6),
            LogicalSizeBytes = fileReader.GetInt64(6),
            AllocatedSizeBytes = alias ? 0 : fileReader.GetInt64(7),
            IsFolder = false,
            IsSystem = (attributesValue & FileAttributes.System) != 0,
            IsCloudOnly =
                unchecked((uint)fileReader.GetInt64(10)) ==
                StorageReparseTags.CloudFiles,
            IsHardLinkAlias = alias,
        };
    }

    private async Task<StorageFileRecord?> GetNormalizedFileRecordByPathAsync(
        long runKey,
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var trimmed = Path.GetFullPath(path).TrimEnd('\\');
        var parentPath = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return null;
        }

        var parentId = await ResolveFolderIdAsync(
            connection,
            runKey,
            parentPath,
            cancellationToken);
        if (!parentId.HasValue)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.Name, n.Extension, n.Category, n.Tag, n.IsHardLinkAlias,
                   p.LogicalSize, p.AllocatedSize, p.CreatedUtc, p.ModifiedUtc,
                   p.AccessedUtc, p.Attributes, p.ReparseTag,
                   p.VolumeSerialNumber, p.FileId128, p.ContentHash
            FROM FileNames n
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE n.RunKey = $run
              AND n.ParentId = $parent
              AND n.Name = $name
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$parent", parentId.Value);
        command.Parameters.AddWithValue("$name", Path.GetFileName(trimmed));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var alias = reader.GetBoolean(4);
        return new StorageFileRecord
        {
            ScanId = await GetScanIdForRunAsync(runKey, cancellationToken),
            Name = reader.GetString(0),
            FullPath = trimmed,
            ParentPath = parentPath,
            SizeBytes = reader.GetInt64(5),
            AllocatedSizeBytes = reader.GetInt64(6),
            PhysicalSizeBytes = alias ? 0 : reader.GetInt64(6),
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            AccessedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            Attributes = reader.GetInt64(10),
            Extension = reader.GetString(1),
            Category = StorageCategoryClassifier.CategoryName(reader.GetInt32(2)),
            Tag = StorageCategoryClassifier.TagName(reader.GetInt32(3)),
            VolumeSerialNumber = reader.GetInt64(12),
            FileId128 = reader.GetString(13),
            ReparseTag = unchecked((uint)reader.GetInt64(11)),
            IsHardLinkAlias = alias,
            ContentHash = reader.IsDBNull(14) ? null : reader.GetString(14),
        };
    }

    public async Task<IReadOnlyList<StorageFolderNode>> GetTopFoldersAsync(
        Guid scanId,
        bool includeSystem = true,
        bool includeHidden = true,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var roots = await GetRootFolderChildrenAsync(
            scanId,
            includeSystem,
            includeHidden,
            (int)Math.Clamp((long)limit * 4, 24, 128),
            cancellationToken);
        return await FolderUsageInsights.SelectLargestAsync(
            roots,
            (path, childLimit, token) => GetFolderChildrenAsync(
                scanId,
                path,
                includeSystem,
                includeHidden,
                childLimit,
                token),
            limit,
            cancellationToken);
    }

    private async Task<IReadOnlyList<StorageFolderNode>> GetRootFolderChildrenAsync(
        Guid scanId,
        bool includeSystem,
        bool includeHidden,
        int limit,
        CancellationToken cancellationToken)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            var root = await GetRootFolderPathAsync(
                format.RunKey,
                cancellationToken);
            return await GetNormalizedFolderChildrenAsync(
                format.RunKey,
                root,
                includeSystem,
                includeHidden,
                limit,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FullPath, Name, SizeBytes, Tag,
                   EXISTS (
                       SELECT 1 FROM FolderEntries child
                       WHERE child.ScanId = FolderEntries.ScanId
                         AND child.ParentPath = FolderEntries.FullPath
                   )
            FROM FolderEntries
            WHERE ScanId = $scanId
              AND ParentPath = (
                  SELECT FullPath FROM FolderEntries
                  WHERE ScanId = $scanId
                  ORDER BY Depth ASC, Length(FullPath) ASC
                  LIMIT 1
              )
              AND ($includeSystem = 1 OR Category <> 'System')
              AND ($includeHidden = 1 OR (Attributes & $hidden) = 0)
            ORDER BY SizeBytes DESC, FullPath COLLATE NOCASE
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$includeSystem", includeSystem ? 1 : 0);
        command.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
        command.Parameters.AddWithValue("$hidden", (long)FileAttributes.Hidden);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StorageFolderNode>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StorageFolderNode
            {
                Path = reader.GetString(0),
                Name = reader.GetString(1),
                SizeBytes = reader.GetInt64(2),
                AllocatedSizeBytes = reader.GetInt64(2),
                Tag = reader.GetString(3),
                HasChildren = reader.GetBoolean(4),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<StorageItem>> GetLargestFilesAsync(
        Guid scanId,
        bool includeSystem = true,
        bool includeHidden = true,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedLargestFilesAsync(
                format.RunKey,
                includeSystem,
                includeHidden,
                limit,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 0 AS IsFolder, FullPath, Name, SizeBytes, ModifiedUtc,
                   Category, Extension AS Kind, Attributes
            FROM FileEntries
            WHERE ScanId = $scanId
              AND ($includeSystem = 1 OR Category <> 'System')
              AND ($includeHidden = 1 OR (Attributes & $hidden) = 0)
            ORDER BY SizeBytes DESC, FullPath COLLATE NOCASE
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$includeSystem", includeSystem ? 1 : 0);
        command.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
        command.Parameters.AddWithValue("$hidden", (long)FileAttributes.Hidden);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadStorageItemsAsync(reader, cancellationToken);
    }

    private async Task<IReadOnlyList<StorageItem>> GetNormalizedLargestFilesAsync(
        long runKey,
        bool includeSystem,
        bool includeHidden,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var items = new List<StorageItem>();
        var fileRows = new List<(long Id, long ParentId, string Name, string Ext,
            int Category, int Tag, bool Alias, long Logical, long Allocated,
            long Modified, long Attributes, long Reparse)>();
        await using (var files = connection.CreateCommand())
        {
            files.CommandText = """
                SELECT n.Id, n.ParentId, n.Name, n.Extension, n.Category, n.Tag,
                       n.IsHardLinkAlias, p.LogicalSize, p.AllocatedSize,
                       p.ModifiedUtc, p.Attributes, p.ReparseTag
                FROM FileNames n
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE n.RunKey = $run
                  AND n.IsHardLinkAlias = 0
                  AND ($includeSystem = 1 OR n.Category <> $system)
                  AND ($includeHidden = 1 OR (p.Attributes & $hidden) = 0)
                ORDER BY p.AllocatedSize DESC, n.Name, n.Id
                LIMIT $limit
                """;
            files.Parameters.AddWithValue("$run", runKey);
            files.Parameters.AddWithValue("$includeSystem", includeSystem ? 1 : 0);
            files.Parameters.AddWithValue(
                "$system",
                StorageCategoryClassifier.CategoryId("System"));
            files.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
            files.Parameters.AddWithValue("$hidden", (long)FileAttributes.Hidden);
            files.Parameters.AddWithValue("$limit", limit);
            await using var reader = await files.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                fileRows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetBoolean(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11)));
            }
        }

        foreach (var row in fileRows)
        {
            items.Add(new StorageItem
            {
                EntryId = row.Id,
                Name = row.Name,
                Path = await BuildFilePathAsync(
                    connection,
                    runKey,
                    row.ParentId,
                    row.Name,
                    cancellationToken),
                Category = StorageCategoryClassifier.CategoryName(row.Category),
                Kind = string.IsNullOrWhiteSpace(row.Ext)
                    ? "File"
                    : row.Ext.TrimStart('.').ToUpperInvariant() + " file",
                ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.Modified),
                SizeBytes = row.Logical,
                LogicalSizeBytes = row.Logical,
                AllocatedSizeBytes = row.Alias ? 0 : row.Allocated,
                IsFolder = false,
                IsSystem = ((FileAttributes)row.Attributes & FileAttributes.System) != 0,
                IsCloudOnly = unchecked((uint)row.Reparse) ==
                              StorageReparseTags.CloudFiles,
                IsHardLinkAlias = row.Alias,
            });
        }

        return items
            .OrderByDescending(item => item.DisplaySizeBytes)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<StorageFolderNode>> GetFolderChildrenAsync(
        Guid scanId,
        string parentPath,
        bool includeSystem = true,
        bool includeHidden = true,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedFolderChildrenAsync(
                format.RunKey,
                parentPath,
                includeSystem,
                includeHidden,
                limit,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FullPath, Name, SizeBytes, Tag,
                   EXISTS (
                       SELECT 1 FROM FolderEntries child
                       WHERE child.ScanId = FolderEntries.ScanId
                         AND child.ParentPath = FolderEntries.FullPath
                   )
            FROM FolderEntries
            WHERE ScanId = $scanId
              AND ParentPath = $parentPath
              AND ($includeSystem = 1 OR Category <> 'System')
              AND ($includeHidden = 1 OR (Attributes & $hidden) = 0)
            ORDER BY SizeBytes DESC, Name COLLATE NOCASE
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$parentPath", NormalizeFolderPath(parentPath));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$includeSystem", includeSystem ? 1 : 0);
        command.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
        command.Parameters.AddWithValue("$hidden", (long)FileAttributes.Hidden);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<StorageFolderNode>();
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new StorageFolderNode
            {
                Path = reader.GetString(0),
                Name = reader.GetString(1),
                SizeBytes = reader.GetInt64(2),
                Tag = reader.GetString(3),
                HasChildren = reader.GetBoolean(4),
            });
        }

        return nodes;
    }

    public async Task<StorageFolderNode?> GetFolderNodeAsync(
        Guid scanId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        var normalized = NormalizeFolderPath(path);
        if (format.Format >= 4)
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            var folderId = await ResolveFolderIdAsync(
                connection,
                format.RunKey,
                normalized,
                cancellationToken);
            if (!folderId.HasValue)
            {
                return null;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.Name, f.LogicalSize, f.AllocatedSize, f.Tag,
                       EXISTS (
                           SELECT 1
                           FROM FolderNodes child
                           WHERE child.RunKey = f.RunKey
                             AND child.ParentId = f.Id
                       )
                FROM FolderNodes f
                WHERE f.RunKey = $run AND f.Id = $id
                """;
            command.Parameters.AddWithValue("$run", format.RunKey);
            command.Parameters.AddWithValue("$id", folderId.Value);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new StorageFolderNode
            {
                EntryId = folderId.Value,
                Name = reader.GetString(0),
                Path = normalized,
                Tag = StorageCategoryClassifier.TagName(
                    reader.GetInt32(3)),
                SizeBytes = reader.GetInt64(1),
                AllocatedSizeBytes = reader.GetInt64(2),
                HasChildren = reader.GetBoolean(4),
            };
        }

        await using var legacy = await OpenConnectionAsync(cancellationToken);
        await using var legacyCommand = legacy.CreateCommand();
        legacyCommand.CommandText = """
            SELECT Name, SizeBytes, AllocatedSizeBytes, Tag,
                   EXISTS (
                       SELECT 1
                       FROM FolderEntries child
                       WHERE child.ScanId = FolderEntries.ScanId
                         AND child.ParentPath = FolderEntries.FullPath
                   )
            FROM FolderEntries
            WHERE ScanId = $scan AND FullPath = $path
            LIMIT 1
            """;
        legacyCommand.Parameters.AddWithValue("$scan", scanId.ToString("N"));
        legacyCommand.Parameters.AddWithValue("$path", normalized);
        await using var legacyReader =
            await legacyCommand.ExecuteReaderAsync(cancellationToken);
        if (!await legacyReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StorageFolderNode
        {
            Name = legacyReader.GetString(0),
            Path = normalized,
            SizeBytes = legacyReader.GetInt64(1),
            AllocatedSizeBytes = legacyReader.GetInt64(2),
            Tag = legacyReader.GetString(3),
            HasChildren = legacyReader.GetBoolean(4),
        };
    }

    private async Task<IReadOnlyList<StorageFolderNode>> GetNormalizedFolderChildrenAsync(
        long runKey,
        string parentPath,
        bool includeSystem,
        bool includeHidden,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var parentId = await ResolveFolderIdAsync(
            connection,
            runKey,
            parentPath,
            cancellationToken);
        if (!parentId.HasValue)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.Id, f.Name, f.LogicalSize, f.AllocatedSize,
                   f.Tag, f.Category, f.Attributes, EXISTS (
                       SELECT 1 FROM FolderNodes child
                       WHERE child.RunKey = f.RunKey
                         AND child.ParentId = f.Id
                   )
            FROM FolderNodes f
            WHERE f.RunKey = $run
              AND f.ParentId = $parent
              AND ($includeSystem = 1 OR f.Category <> $system)
              AND ($includeHidden = 1 OR (f.Attributes & $hidden) = 0)
            ORDER BY f.AllocatedSize DESC, f.Name
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$parent", parentId.Value);
        command.Parameters.AddWithValue("$includeSystem", includeSystem ? 1 : 0);
        command.Parameters.AddWithValue(
            "$system",
            StorageCategoryClassifier.CategoryId("System"));
        command.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
        command.Parameters.AddWithValue("$hidden", (long)FileAttributes.Hidden);
        command.Parameters.AddWithValue("$limit", limit);
        var nodes = new List<StorageFolderNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new StorageFolderNode
            {
                EntryId = reader.GetInt64(0),
                Path = CombineDisplayPath(parentPath, reader.GetString(1)),
                Name = reader.GetString(1),
                SizeBytes = reader.GetInt64(2),
                AllocatedSizeBytes = reader.GetInt64(3),
                Tag = StorageCategoryClassifier.TagName(reader.GetInt32(4)),
                HasChildren = reader.GetBoolean(7),
            });
        }

        return nodes;
    }

    private async Task<string> GetRootFolderPathAsync(
        long runKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Name
            FROM FolderNodes
            WHERE RunKey = $run AND ParentId IS NULL
            ORDER BY Id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$run", runKey);
        var name = await command.ExecuteScalarAsync(cancellationToken);
        return name is string root
            ? NormalizeFolderPath(root + "\\")
            : string.Empty;
    }

    public async Task<IReadOnlyList<FolderGrowthInsight>> GetGrowingFoldersAsync(
        string driveLetter,
        long minimumTrackedBytes,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var candidateLimit = Math.Clamp((long)limit * 16, 64, 512);
        var latest = await GetLatestCompletedScanAsync(
            driveLetter,
            cancellationToken);
        IReadOnlyList<FolderGrowthInsight> results;
        if (latest is { ScanFormat: >= 4 })
        {
            results = await GetNormalizedGrowingFoldersAsync(
                driveLetter,
                minimumTrackedBytes,
                (int)candidateLimit,
                cancellationToken);
        }
        else
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
            WITH LatestRuns AS (
                SELECT Id, CompletedUtc
                FROM ScanRuns
                WHERE DriveLetter = $drive AND Status = $completed
                  AND ($volume = '' OR VolumeSerialNumber = $volume
                       OR VolumeSerialNumber IS NULL OR VolumeSerialNumber = '')
                ORDER BY CompletedUtc DESC
                LIMIT 2
            ),
            NewRun AS (
                SELECT Id, CompletedUtc FROM LatestRuns ORDER BY CompletedUtc DESC LIMIT 1
            ),
            OldRun AS (
                SELECT Id, CompletedUtc FROM LatestRuns ORDER BY CompletedUtc DESC LIMIT 1 OFFSET 1
            )
            SELECT newFolder.FullPath,
                   newFolder.Name,
                   newFolder.SizeBytes,
                   newFolder.SizeBytes - oldFolder.SizeBytes AS AddedBytes,
                   CAST(newFolder.SizeBytes - oldFolder.SizeBytes AS REAL) /
                       NULLIF(oldFolder.SizeBytes, 0) * 100 AS GrowthPercentage,
                   CAST((newRun.CompletedUtc - oldRun.CompletedUtc + 86399999) / 86400000 AS INTEGER) AS Days
            FROM FolderEntries newFolder
            INNER JOIN NewRun newRun ON newRun.Id = newFolder.ScanId
            INNER JOIN OldRun oldRun ON 1 = 1
            INNER JOIN FolderEntries oldFolder
                ON oldFolder.ScanId = oldRun.Id
               AND oldFolder.FullPath = newFolder.FullPath
            WHERE newFolder.SizeBytes >= $minimum
              AND newFolder.SizeBytes > oldFolder.SizeBytes
            ORDER BY AddedBytes DESC
            LIMIT $limit
            """;
            command.Parameters.AddWithValue("$drive", NormalizeDriveLetter(driveLetter));
            command.Parameters.AddWithValue("$completed", (int)StorageScanStatus.Completed);
            command.Parameters.AddWithValue(
                "$volume",
                VolumeIdentityService.TryGetVolumeSerial(driveLetter) ??
                string.Empty);
            command.Parameters.AddWithValue("$minimum", minimumTrackedBytes);
            command.Parameters.AddWithValue("$limit", candidateLimit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var legacyResults = new List<FolderGrowthInsight>();
            while (await reader.ReadAsync(cancellationToken))
            {
                legacyResults.Add(new FolderGrowthInsight
                {
                    Path = reader.GetString(0),
                    Name = reader.GetString(1),
                    SizeBytes = reader.GetInt64(2),
                    AddedBytes = reader.GetInt64(3),
                    GrowthPercentage = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                    WindowDays = Math.Max(1, reader.GetInt32(5)),
                });
            }

            results = legacyResults;
        }

        return FolderGrowthInsights.SelectLargest(
            results,
            limit);
    }

    private async Task<IReadOnlyList<FolderGrowthInsight>> GetNormalizedGrowingFoldersAsync(
        string driveLetter,
        long minimumTrackedBytes,
        int limit,
        CancellationToken cancellationToken)
    {
        var volume = VolumeIdentityService.TryGetVolumeSerial(driveLetter) ??
                     string.Empty;
        var sampled = await GetSampledGrowingFoldersAsync(
            driveLetter,
            volume,
            minimumTrackedBytes,
            limit,
            cancellationToken);
        if (sampled.Count > 0)
        {
            return sampled;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(long FolderId, long RunKey, string Name, long Size,
            long Allocated, long Added, double Percentage, int Days)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH Runs AS (
                    SELECT RunKey, CompletedUtc,
                           ROW_NUMBER() OVER (
                               ORDER BY CompletedUtc DESC
                           ) AS Position
                    FROM ScanRuns
                    WHERE DriveLetter = $drive
                      AND Status = $completed
                      AND ScanFormat >= 4
                      AND ($volume = '' OR VolumeSerialNumber = $volume
                           OR VolumeSerialNumber IS NULL
                           OR VolumeSerialNumber = '')
                )
                SELECT n.Id, n.RunKey, n.Name, n.LogicalSize, n.AllocatedSize,
                       n.AllocatedSize - o.AllocatedSize,
                       CAST(n.AllocatedSize - o.AllocatedSize AS REAL) /
                           NULLIF(o.AllocatedSize, 0) * 100,
                       MAX(1, CAST((nr.CompletedUtc - old.CompletedUtc
                                    + 86399999) / 86400000 AS INTEGER))
                FROM FolderNodes n
                INNER JOIN Runs nr
                    ON nr.RunKey = n.RunKey AND nr.Position = 1
                INNER JOIN Runs old ON old.Position = 2
                INNER JOIN FolderNodes o
                    ON o.RunKey = old.RunKey
                   AND o.PathHash = n.PathHash
                WHERE n.AllocatedSize >= $minimum
                  AND n.AllocatedSize > o.AllocatedSize
                ORDER BY (n.AllocatedSize - o.AllocatedSize) DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$drive", NormalizeDriveLetter(driveLetter));
            command.Parameters.AddWithValue("$completed", (int)StorageScanStatus.Completed);
            command.Parameters.AddWithValue("$volume", volume);
            command.Parameters.AddWithValue("$minimum", minimumTrackedBytes);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                    reader.GetInt32(7)));
            }
        }

        var results = new List<FolderGrowthInsight>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(new FolderGrowthInsight
            {
                Path = await BuildFolderPathAsync(
                    connection,
                    row.RunKey,
                    row.FolderId,
                    cancellationToken),
                Name = row.Name,
                SizeBytes = row.Allocated,
                AddedBytes = row.Added,
                GrowthPercentage = row.Percentage,
                WindowDays = row.Days,
            });
        }

        return results;
    }

    private async Task<IReadOnlyList<FolderGrowthInsight>>
        GetSampledGrowingFoldersAsync(
            string driveLetter,
            string volume,
            long minimumTrackedBytes,
            int limit,
            CancellationToken cancellationToken)
    {
        var volumeKey = string.IsNullOrWhiteSpace(volume)
            ? NormalizeDriveLetter(driveLetter)
            : volume;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(long FolderId, long RunKey, string Name, long Size,
            long Added, double Percentage, int Days)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH Ranked AS (
                    SELECT PathHash, SampleUtc, LogicalSize, AllocatedSize,
                           ROW_NUMBER() OVER (
                               PARTITION BY PathHash
                               ORDER BY SampleUtc DESC
                           ) AS Position
                    FROM FolderGrowthSamples
                    WHERE VolumeKey = $volume
                ),
                Growth AS (
                    SELECT current.PathHash,
                           current.AllocatedSize,
                           current.AllocatedSize - previous.AllocatedSize AS Added,
                           CAST(current.AllocatedSize - previous.AllocatedSize AS REAL) /
                               NULLIF(previous.AllocatedSize, 0) * 100 AS Percentage,
                           MAX(1, CAST(
                               (current.SampleUtc - previous.SampleUtc + 86399999) /
                               86400000 AS INTEGER)) AS Days
                    FROM Ranked current
                    INNER JOIN Ranked previous
                        ON previous.PathHash = current.PathHash
                       AND previous.Position = 2
                    WHERE current.Position = 1
                ),
                LatestRun AS (
                    SELECT RunKey
                    FROM ScanRuns
                    WHERE Status = $completed
                      AND ScanFormat >= 4
                      AND DriveLetter = $drive
                    ORDER BY CompletedUtc DESC
                    LIMIT 1
                )
                SELECT f.Id, f.RunKey, f.Name, g.AllocatedSize,
                       g.Added, COALESCE(g.Percentage, 0), g.Days
                FROM Growth g
                INNER JOIN FolderNodes f
                    ON f.PathHash = g.PathHash
                INNER JOIN LatestRun r ON r.RunKey = f.RunKey
                WHERE g.AllocatedSize >= $minimum
                  AND g.Added > 0
                ORDER BY g.Added DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$volume", volumeKey);
            command.Parameters.AddWithValue(
                "$completed",
                (int)StorageScanStatus.Completed);
            command.Parameters.AddWithValue(
                "$drive",
                NormalizeDriveLetter(driveLetter));
            command.Parameters.AddWithValue("$minimum", minimumTrackedBytes);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetDouble(5),
                    reader.GetInt32(6)));
            }
        }

        var results = new List<FolderGrowthInsight>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(new FolderGrowthInsight
            {
                Path = await BuildFolderPathAsync(
                    connection,
                    row.RunKey,
                    row.FolderId,
                    cancellationToken),
                Name = row.Name,
                SizeBytes = row.Size,
                AddedBytes = row.Added,
                GrowthPercentage = row.Percentage,
                WindowDays = row.Days,
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<StorageFolderRecord>> GetTrackedFolderPathsAsync(
        long minimumTrackedBytes,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var normalizedRuns = await GetLatestNormalizedRunsAsync(cancellationToken);
        if (normalizedRuns.Count > 0)
        {
            return await GetNormalizedTrackedFoldersAsync(
                normalizedRuns,
                minimumTrackedBytes,
                limit,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH Latest AS (
                SELECT DriveLetter, Id,
                       ROW_NUMBER() OVER (
                           PARTITION BY COALESCE(
                               NULLIF(VolumeSerialNumber, ''),
                               DriveLetter)
                           ORDER BY CompletedUtc DESC) AS RowNumber
                FROM ScanRuns
                WHERE Status = $completed
            )
            SELECT f.ScanId, f.FullPath, f.Name, f.ParentPath, f.SizeBytes, f.DirectSizeBytes,
                   f.FileCount, f.DirectFileCount, f.Depth, f.Attributes, f.ModifiedUtc,
                   f.Category, f.Tag
            FROM FolderEntries f
            INNER JOIN Latest l ON l.Id = f.ScanId AND l.RowNumber = 1
            WHERE f.SizeBytes >= $minimum
              AND f.Depth > 0
              AND f.Category <> 'System'
              AND f.Depth <= 5
              AND NOT EXISTS (
                  SELECT 1
                  FROM FolderEntries child
                  WHERE child.ScanId = f.ScanId
                    AND child.ParentPath = f.FullPath
                    AND child.SizeBytes >= $minimum
              )
            ORDER BY f.SizeBytes DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$completed", (int)StorageScanStatus.Completed);
        command.Parameters.AddWithValue("$minimum", minimumTrackedBytes);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var folders = new List<StorageFolderRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(new StorageFolderRecord
            {
                ScanId = Guid.ParseExact(reader.GetString(0), "N"),
                FullPath = reader.GetString(1),
                Name = reader.GetString(2),
                ParentPath = reader.GetString(3),
                SizeBytes = reader.GetInt64(4),
                DirectSizeBytes = reader.GetInt64(5),
                FileCount = reader.GetInt64(6),
                DirectFileCount = reader.GetInt64(7),
                Depth = reader.GetInt32(8),
                Attributes = reader.GetInt64(9),
                ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                Category = reader.GetString(11),
                Tag = reader.GetString(12),
            });
        }

        return folders;
    }

    private async Task<IReadOnlyList<(Guid ScanId, long RunKey)>> GetLatestNormalizedRunsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH Ranked AS (
                SELECT Id, RunKey, COALESCE(
                           NULLIF(VolumeSerialNumber, ''), DriveLetter
                       ) AS VolumeKey,
                       ROW_NUMBER() OVER (
                           PARTITION BY COALESCE(
                               NULLIF(VolumeSerialNumber, ''), DriveLetter)
                           ORDER BY CompletedUtc DESC
                       ) AS Position
                FROM ScanRuns
                WHERE ScanFormat >= 4 AND Status = $completed
            )
            SELECT Id, RunKey
            FROM Ranked
            WHERE Position = 1
            """;
        command.Parameters.AddWithValue("$completed", (int)StorageScanStatus.Completed);
        var runs = new List<(Guid ScanId, long RunKey)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add((
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetInt64(1)));
        }

        return runs;
    }

    private async Task<IReadOnlyList<StorageFolderRecord>> GetNormalizedTrackedFoldersAsync(
        IReadOnlyList<(Guid ScanId, long RunKey)> runs,
        long minimumTrackedBytes,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(Guid ScanId, long RunKey, long Id, long ParentId,
            string Name, long Logical, long Allocated, long Files, int Depth,
            long Attributes, long Modified, int Category, int Tag, uint Reparse,
            bool Incomplete)>();
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.Id, f.ParentId, f.Name, f.LogicalSize,
                       f.AllocatedSize, f.FileCount, f.Depth, f.Attributes,
                       f.ModifiedUtc, f.Category, f.Tag, f.ReparseTag,
                       f.IsIncomplete
                FROM FolderNodes f
                WHERE f.RunKey = $run
                  AND f.AllocatedSize >= $minimum
                  AND f.Depth > 0
                  AND f.Depth <= 5
                  AND NOT EXISTS (
                      SELECT 1
                      FROM FolderNodes child
                      WHERE child.RunKey = f.RunKey
                        AND child.ParentId = f.Id
                        AND child.AllocatedSize >= $minimum
                  )
                ORDER BY f.AllocatedSize DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$run", run.RunKey);
            command.Parameters.AddWithValue("$minimum", minimumTrackedBytes);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    run.ScanId,
                    run.RunKey,
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt32(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt32(9),
                    reader.GetInt32(10),
                    unchecked((uint)reader.GetInt64(11)),
                    reader.GetBoolean(12)));
            }
        }

        var folders = new List<StorageFolderRecord>();
        foreach (var row in rows
                     .OrderByDescending(item => item.Allocated)
                     .Take(limit))
        {
            var path = await BuildFolderPathAsync(
                connection,
                row.RunKey,
                row.Id,
                cancellationToken);
            folders.Add(new StorageFolderRecord
            {
                ScanId = row.ScanId,
                FullPath = path,
                Name = row.Name,
                ParentPath = row.ParentId == 0
                    ? string.Empty
                    : await BuildFolderPathAsync(
                        connection,
                        row.RunKey,
                        row.ParentId,
                        cancellationToken),
                SizeBytes = row.Logical,
                AllocatedSizeBytes = row.Allocated,
                DirectSizeBytes = 0,
                FileCount = row.Files,
                DirectFileCount = 0,
                Depth = row.Depth,
                Attributes = row.Attributes,
                ReparseTag = row.Reparse,
                IsIncomplete = row.Incomplete,
                ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.Modified),
                Category = StorageCategoryClassifier.CategoryName(row.Category),
                Tag = StorageCategoryClassifier.TagName(row.Tag),
            });
        }

        return folders;
    }

    public async Task<IReadOnlyList<StorageFileRecord>> GetCleanupCandidatesAsync(
        Guid scanId,
        CleanupPolicyId policy,
        DateTimeOffset now,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedCleanupCandidatesAsync(
                format.RunKey,
                policy,
                limit,
                offset,
                null,
                now,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var select = BuildCleanupSelect(command, policy, now);
        command.CommandText = $"""
            {select}
            LIMIT $limit OFFSET $offset
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<StorageFileRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadFileRecord(reader));
        }

        return records;
    }

    public async Task<IReadOnlyList<CleanupCandidateFolderSummary>>
        GetCleanupCandidateFoldersAsync(
            Guid scanId,
            CleanupPolicyId policy,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedCleanupCandidateFoldersAsync(
                format.RunKey,
                policy,
                now,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.CommandText = BuildCleanupSelect(
            command,
            policy,
            now,
            """
            ParentPath,
            COUNT(*) AS CandidateCount,
            COALESCE(SUM(SizeBytes), 0) AS CandidateBytes
            """,
            groupByParent: true);

        var folders = new List<CleanupCandidateFolderSummary>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(new CleanupCandidateFolderSummary
            {
                Path = reader.GetString(0),
                DirectCandidateCount = reader.GetInt64(1),
                DirectCandidateBytes = reader.GetInt64(2),
            });
        }

        return folders;
    }

    public async Task<IReadOnlyList<StorageFileRecord>>
        GetCleanupCandidatesInFolderAsync(
            Guid scanId,
            CleanupPolicyId policy,
            string parentPath,
            DateTimeOffset now,
            int limit = 200,
            int offset = 0,
            CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedCleanupCandidatesAsync(
                format.RunKey,
                policy,
                limit,
                offset,
                parentPath,
                now,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var select = BuildCleanupSelect(
            command,
            policy,
            now,
            parentPath: parentPath);
        command.CommandText = $"""
            {select}
            LIMIT $limit OFFSET $offset
            """;

        var records = new List<StorageFileRecord>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadFileRecord(reader));
        }

        return records;
    }

    public async Task RecordScanIssueAsync(
        Guid scanId,
        ScanIssue issue,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScanIssues (
                ScanId, Path, IssueType, ReparseTag, ErrorCode, Message, DetectedUtc
            )
            VALUES (
                $scanId, $path, $type, $tag, $error, $message, $detected
            )
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$path", issue.Path);
        command.Parameters.AddWithValue("$type", (int)issue.Type);
        command.Parameters.AddWithValue("$tag", unchecked((long)issue.ReparseTag));
        command.Parameters.AddWithValue(
            "$error",
            issue.ErrorCode.HasValue ? issue.ErrorCode.Value : DBNull.Value);
        command.Parameters.AddWithValue("$message", issue.Message);
        command.Parameters.AddWithValue(
            "$detected",
            issue.DetectedUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScanIssue>> GetScanIssuesAsync(
        Guid scanId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Path, IssueType, ReparseTag, ErrorCode, Message, DetectedUtc
            FROM ScanIssues
            WHERE ScanId = $scanId
            ORDER BY DetectedUtc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);
        var issues = new List<ScanIssue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            issues.Add(new ScanIssue
            {
                Path = reader.GetString(0),
                Type = (ScanIssueType)reader.GetInt32(1),
                ReparseTag = unchecked((uint)reader.GetInt64(2)),
                ErrorCode = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Message = reader.GetString(4),
                DetectedUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(5)),
            });
        }

        return issues;
    }

    public async Task<long> GetCleanupCandidateCountAsync(
        Guid scanId,
        CleanupPolicyId policy,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedCleanupCandidateMetricAsync(
                format.RunKey,
                policy,
                now,
                count: true,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        var select = BuildCleanupSelect(command, policy, now, selectColumns: "COUNT(*)");
        command.CommandText = select;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<long> GetCleanupCandidateBytesAsync(
        Guid scanId,
        CleanupPolicyId policy,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedCleanupCandidateMetricAsync(
                format.RunKey,
                policy,
                now,
                count: false,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        var select = BuildCleanupSelect(command, policy, now, selectColumns: "COALESCE(SUM(SizeBytes), 0)");
        command.CommandText = select;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<CleanupPolicySummary>> GetCleanupSummariesAsync(
        Guid scanId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedCleanupSummariesAsync(
                format.RunKey,
                now,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var cachedSummaries = await GetCachedCleanupSummariesAsync(
            connection,
            scanId,
            now.AddDays(-1),
            cancellationToken);
        if (cachedSummaries.Count > 0)
        {
            return cachedSummaries;
        }

        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue(
            "$protectedAttributes",
            (long)(FileAttributes.System |
                   FileAttributes.Device |
                   FileAttributes.ReparsePoint));
        command.CommandText = """
            SELECT FullPath, SizeBytes, ModifiedUtc, Category, ContentHash
            FROM FileEntries
            WHERE ScanId = $scanId
              AND (Attributes & $protectedAttributes) = 0
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        long temporaryCount = 0;
        long temporaryBytes = 0;
        long downloadCount = 0;
        long downloadBytes = 0;
        long packageCount = 0;
        long packageBytes = 0;
        long windowsOldCount = 0;
        long windowsOldBytes = 0;
        var recentCutoff = now.AddHours(-24).ToUnixTimeMilliseconds();
        var oldDownloadCutoff = now.AddDays(-90).ToUnixTimeMilliseconds();
        var duplicateGroups = new Dictionary<string, DuplicateHashSummary>(
            StringComparer.Ordinal);
        long processed = 0;
        while (reader.Read())
        {
            if ((processed++ & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var path = reader.GetString(0);
            var size = reader.GetInt64(1);
            var modified = reader.GetInt64(2);
            var category = reader.GetString(3);
            if (modified <= recentCutoff &&
                category == "Cache & temporary")
            {
                if (CleanupPolicyService.IsKnownTemporaryFile(path))
                {
                    temporaryCount++;
                    temporaryBytes += size;
                }

                if (CleanupPolicyService.IsKnownPackageCache(path))
                {
                    packageCount++;
                    packageBytes += size;
                }
            }

            if (modified <= oldDownloadCutoff &&
                CleanupPolicyService.IsInsideDownloads(path))
            {
                downloadCount++;
                downloadBytes += size;
            }

            if (StorageCategoryClassifier.IsInside(path, @"C:\Windows.old"))
            {
                windowsOldCount++;
                windowsOldBytes += size;
            }

            if (!reader.IsDBNull(4))
            {
                var hash = reader.GetString(4);
                if (hash.Length == 64)
                {
                    if (duplicateGroups.TryGetValue(hash, out var summary))
                    {
                        summary.Count++;
                        summary.TotalBytes += size;
                    }
                    else
                    {
                        duplicateGroups.Add(
                            hash,
                            new DuplicateHashSummary
                            {
                                Count = 1,
                                TotalBytes = size,
                                RetainedBytes = size,
                            });
                    }
                }
            }
        }

        reader.Close();
        var duplicateCount = duplicateGroups.Values
            .Where(group => group.Count > 1)
            .Sum(group => (long)group.Count - 1);
        var duplicateBytes = duplicateGroups.Values
            .Where(group => group.Count > 1)
            .Sum(group => group.TotalBytes - group.RetainedBytes);
        var summaries = new List<CleanupPolicySummary>
        {
            new(CleanupPolicyId.TemporaryFiles, temporaryCount, temporaryBytes),
            new(CleanupPolicyId.OldDownloads, downloadCount, downloadBytes),
            new(CleanupPolicyId.PackageCaches, packageCount, packageBytes),
            new(CleanupPolicyId.PreviousWindowsInstallation, windowsOldCount, windowsOldBytes),
            new(CleanupPolicyId.LargeDuplicates, duplicateCount, duplicateBytes),
        };
        await SaveCleanupSummariesAsync(
            connection,
            scanId,
            now,
            summaries,
            cancellationToken);
        return summaries;
    }

    private async Task<IReadOnlyList<CleanupPolicySummary>> GetNormalizedCleanupSummariesAsync(
        long runKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var packageFilter = AddNormalizedCleanupFilters(
            command,
            CleanupPolicyId.PackageCaches,
            now);
        command.CommandText = $"""
            SELECT c.PolicyId, COUNT(*), COALESCE(SUM(
                       CASE WHEN n.IsHardLinkAlias = 0
                            THEN p.AllocatedSize ELSE 0 END), 0)
            FROM NormalizedCleanupCandidates c
            INNER JOIN FileNames n ON n.Id = c.FileNameId
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE c.RunKey = $run
              AND (c.PolicyId != $packagePolicy OR (1 = 1
                   {packageFilter}))
            GROUP BY c.PolicyId
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue(
            "$packagePolicy",
            (int)CleanupPolicyId.PackageCaches);
        var byPolicy = new Dictionary<CleanupPolicyId, CleanupPolicySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var policy = (CleanupPolicyId)reader.GetInt32(0);
            byPolicy[policy] = new CleanupPolicySummary(
                policy,
                reader.GetInt64(1),
                reader.GetInt64(2));
        }

        return CleanupPolicyService.Policies
            .Select(policy => byPolicy.GetValueOrDefault(policy.Id) ??
                              new CleanupPolicySummary(policy.Id, 0, 0))
            .ToList();
    }

    private async Task<IReadOnlyList<StorageFileRecord>> GetNormalizedCleanupCandidatesAsync(
        long runKey,
        CleanupPolicyId policy,
        int limit,
        int offset,
        string? parentPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(long NameId, long ParentId, string Name, string Extension,
            int Category, int Tag, bool Alias, long Logical, long Allocated,
            long Created, long Modified, long Accessed, long Attributes,
            uint Reparse, long Volume, string FileId, string? Hash)>();
        await using (var command = connection.CreateCommand())
        {
            var parentFilter = string.Empty;
            var folderCte = string.Empty;
            var policyFilter = AddNormalizedCleanupFilters(
                command,
                policy,
                now);
            if (parentPath is not null)
            {
                command.Parameters.AddWithValue(
                    "$folderHash",
                    GetPathHash(parentPath));
                folderCte = """
                    WITH RECURSIVE CandidateFolderTree(Id) AS (
                        SELECT Id
                        FROM FolderNodes
                        WHERE RunKey = $run AND PathHash = $folderHash
                        UNION
                        SELECT child.Id
                        FROM FolderNodes child
                        INNER JOIN CandidateFolderTree parent
                            ON child.ParentId = parent.Id
                        WHERE child.RunKey = $run
                    )
                    """;
                parentFilter =
                    " AND n.ParentId IN (SELECT Id FROM CandidateFolderTree)";
            }

            command.CommandText = $"""
                {folderCte}
                SELECT n.Id, n.ParentId, n.Name, n.Extension, n.Category, n.Tag,
                       n.IsHardLinkAlias, p.LogicalSize, p.AllocatedSize,
                       p.CreatedUtc, p.ModifiedUtc, p.AccessedUtc, p.Attributes,
                       p.ReparseTag, p.VolumeSerialNumber, p.FileId128,
                       p.ContentHash
                FROM NormalizedCleanupCandidates c
                INNER JOIN FileNames n ON n.Id = c.FileNameId
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE c.RunKey = $run AND c.PolicyId = $policy
                {policyFilter}
                $$PARENT_FILTER$$
                ORDER BY p.AllocatedSize DESC, n.Name, n.Id
                LIMIT $limit OFFSET $offset
                """.Replace(
                    "$$PARENT_FILTER$$",
                    parentFilter,
                    StringComparison.Ordinal);
            command.Parameters.AddWithValue("$run", runKey);
            command.Parameters.AddWithValue("$policy", (int)policy);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetBoolean(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    reader.GetInt64(12),
                    unchecked((uint)reader.GetInt64(13)),
                    reader.GetInt64(14),
                    reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16)));
            }
        }

        var records = new List<StorageFileRecord>(rows.Count);
        var scanId = await GetScanIdForRunAsync(runKey, cancellationToken);
        var parentPaths = await BuildFolderPathsAsync(
            connection,
            runKey,
            rows.Select(row => row.ParentId).Distinct().ToArray(),
            cancellationToken);
        foreach (var row in rows)
        {
            var resolvedParentPath =
                parentPaths.GetValueOrDefault(row.ParentId) ?? string.Empty;
            records.Add(new StorageFileRecord
            {
                ScanId = scanId,
                Name = row.Name,
                FullPath = CombineDisplayPath(resolvedParentPath, row.Name),
                ParentPath = resolvedParentPath,
                SizeBytes = row.Logical,
                AllocatedSizeBytes = row.Allocated,
                PhysicalSizeBytes = row.Alias ? 0 : row.Allocated,
                CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.Created),
                ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.Modified),
                AccessedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.Accessed),
                Attributes = row.Attributes,
                Extension = row.Extension,
                Category = StorageCategoryClassifier.CategoryName(row.Category),
                Tag = StorageCategoryClassifier.TagName(row.Tag),
                VolumeSerialNumber = row.Volume,
                FileId128 = row.FileId,
                ReparseTag = row.Reparse,
                IsHardLinkAlias = row.Alias,
                ContentHash = row.Hash,
            });
        }

        return records;
    }

    private async Task<IReadOnlyList<CleanupCandidateFolderSummary>>
        GetNormalizedCleanupCandidateFoldersAsync(
            long runKey,
            CleanupPolicyId policy,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        var policyFilter = AddNormalizedCleanupFilters(
            command,
            policy,
            now);
        command.CommandText = $"""
            WITH RECURSIVE
            CandidateFolders(FolderId, CandidateCount, CandidateBytes) AS (
                SELECT n.ParentId,
                       COUNT(*),
                       COALESCE(SUM(
                           CASE WHEN n.IsHardLinkAlias = 0
                                THEN p.AllocatedSize ELSE 0 END), 0)
                FROM NormalizedCleanupCandidates c
                INNER JOIN FileNames n ON n.Id = c.FileNameId
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE c.RunKey = $run AND c.PolicyId = $policy
                {policyFilter}
                GROUP BY n.ParentId
            ),
            Ancestors(Id, ParentId) AS (
                SELECT f.Id, f.ParentId
                FROM FolderNodes f
                INNER JOIN CandidateFolders c ON c.FolderId = f.Id
                WHERE f.RunKey = $run
                UNION
                SELECT f.Id, f.ParentId
                FROM FolderNodes f
                INNER JOIN Ancestors a ON f.Id = a.ParentId
                WHERE f.RunKey = $run
            ),
            Paths(Id, Path) AS (
                SELECT f.Id,
                       CASE WHEN f.ParentId IS NULL
                            THEN f.Name || '\'
                            ELSE f.Name END
                FROM FolderNodes f
                INNER JOIN Ancestors a ON a.Id = f.Id
                WHERE f.RunKey = $run AND f.ParentId IS NULL
                UNION ALL
                SELECT f.Id,
                       p.Path ||
                       CASE WHEN SUBSTR(p.Path, LENGTH(p.Path), 1) = '\'
                            THEN '' ELSE '\' END ||
                       f.Name
                FROM FolderNodes f
                INNER JOIN Ancestors a ON a.Id = f.Id
                INNER JOIN Paths p ON p.Id = f.ParentId
                WHERE f.RunKey = $run
            )
            SELECT p.Path, c.CandidateCount, c.CandidateBytes
            FROM CandidateFolders c
            INNER JOIN Paths p ON p.Id = c.FolderId
            ORDER BY c.CandidateBytes DESC, p.Path
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$policy", (int)policy);

        var folders = new List<CleanupCandidateFolderSummary>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(new CleanupCandidateFolderSummary
            {
                Path = reader.GetString(0),
                DirectCandidateCount = reader.GetInt64(1),
                DirectCandidateBytes = reader.GetInt64(2),
            });
        }

        return folders;
    }

    private static string AddNormalizedCleanupFilters(
        SqliteCommand command,
        CleanupPolicyId policy,
        DateTimeOffset now)
    {
        if (policy != CleanupPolicyId.PackageCaches)
        {
            return string.Empty;
        }

        command.Parameters.AddWithValue(
            "$oldModified",
            now.AddHours(-24).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$protectedAttributes",
            (long)(FileAttributes.System |
                   FileAttributes.Device |
                   FileAttributes.ReparsePoint));
        return """
            AND p.ModifiedUtc <= $oldModified
            AND n.IsHardLinkAlias = 0
            AND (p.Attributes & $protectedAttributes) = 0
            """;
    }

    private async Task<long> GetNormalizedCleanupCandidateMetricAsync(
        long runKey,
        CleanupPolicyId policy,
        DateTimeOffset now,
        bool count,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filter = AddNormalizedCleanupFilters(command, policy, now);
        var metric = count
            ? "COUNT(*)"
            : """
              COALESCE(SUM(
                  CASE WHEN n.IsHardLinkAlias = 0
                       THEN p.AllocatedSize ELSE 0 END), 0)
              """;
        command.CommandText = $"""
            SELECT {metric}
            FROM NormalizedCleanupCandidates c
            INNER JOIN FileNames n ON n.Id = c.FileNameId
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE c.RunKey = $run AND c.PolicyId = $policy
            {filter}
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue("$policy", (int)policy);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<Guid> GetScanIdForRunAsync(
        long runKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM ScanRuns WHERE RunKey = $run";
        command.Parameters.AddWithValue("$run", runKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string id
            ? Guid.ParseExact(id, "N")
            : Guid.Empty;
    }

    private static async Task<IReadOnlyList<CleanupPolicySummary>> GetCachedCleanupSummariesAsync(
        SqliteConnection connection,
        Guid scanId,
        DateTimeOffset minimumEvaluatedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PolicyId, ItemCount, SizeBytes
            FROM ScanCleanupSummaries
            WHERE ScanId = $scanId
              AND EvaluatedUtc >= $minimum
            ORDER BY PolicyId
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue(
            "$minimum",
            minimumEvaluatedUtc.ToUnixTimeMilliseconds());
        var summaries = new List<CleanupPolicySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new CleanupPolicySummary(
                (CleanupPolicyId)reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }

        return summaries.Count == CleanupPolicyService.Policies.Count
            ? summaries
            : [];
    }

    private static async Task SaveCleanupSummariesAsync(
        SqliteConnection connection,
        Guid scanId,
        DateTimeOffset evaluatedUtc,
        IReadOnlyList<CleanupPolicySummary> summaries,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScanCleanupSummaries (
                ScanId, PolicyId, ItemCount, SizeBytes, EvaluatedUtc
            )
            VALUES ($scanId, $policy, $count, $size, $evaluated)
            ON CONFLICT(ScanId, PolicyId) DO UPDATE SET
                ItemCount = excluded.ItemCount,
                SizeBytes = excluded.SizeBytes,
                EvaluatedUtc = excluded.EvaluatedUtc
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$policy", 0);
        command.Parameters.AddWithValue("$count", 0L);
        command.Parameters.AddWithValue("$size", 0L);
        command.Parameters.AddWithValue(
            "$evaluated",
            evaluatedUtc.ToUnixTimeMilliseconds());
        await command.PrepareAsync(cancellationToken);
        foreach (var summary in summaries)
        {
            command.Parameters["$policy"].Value = (int)summary.PolicyId;
            command.Parameters["$count"].Value = summary.ItemCount;
            command.Parameters["$size"].Value = summary.SizeBytes;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<DuplicateSizeGroup>> GetUnhashedDuplicateSizeGroupsAsync(
        Guid scanId,
        long minimumFileSize = 1024 * 1024,
        long maximumFileSize = 32L * 1024 * 1024 * 1024,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedDuplicateSizeGroupsAsync(
                format.RunKey,
                minimumFileSize,
                maximumFileSize,
                limit,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.SizeBytes, GROUP_CONCAT(f.FullPath, char(31))
            FROM FileEntries f
            WHERE f.ScanId = $scanId
              AND f.SizeBytes >= $minimum
              AND f.ContentHash IS NULL
              AND f.Category <> 'System'
              AND f.Attributes & $reparse = 0
              AND f.IsHardLinkAlias = 0
              AND f.SizeBytes < $maximum
            GROUP BY f.SizeBytes
            HAVING COUNT(*) > 1
            ORDER BY f.SizeBytes ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$minimum", minimumFileSize);
        command.Parameters.AddWithValue("$maximum", maximumFileSize);
        command.Parameters.AddWithValue("$reparse", (long)FileAttributes.ReparsePoint);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<DuplicateSizeGroup>();
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new DuplicateSizeGroup(
                reader.GetInt64(0),
                reader.GetString(1).Split('\x1f', StringSplitOptions.RemoveEmptyEntries)));
        }

        return groups;
    }

    public async Task<StorageFileRecord?> GetDuplicateRetainedRecordAsync(
        Guid scanId,
        StorageFileRecord candidate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(candidate.ContentHash) ||
            candidate.ContentHash.Length != 64)
        {
            return null;
        }

        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            return await GetNormalizedDuplicateRetainedRecordAsync(
                format.RunKey,
                candidate,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ScanId, FullPath, Name, ParentPath, SizeBytes, CreatedUtc,
                   ModifiedUtc, AccessedUtc, Attributes, Extension, Category, Tag,
                   VolumeSerialNumber, FileIndexHigh, FileIndexLow, ContentHash,
                   AllocatedSizeBytes, PhysicalSizeBytes, FileId128, ReparseTag,
                   LinkCount, IsHardLinkAlias, IsIncomplete
            FROM FileEntries
            WHERE ScanId = $scanId
              AND ContentHash = $hash
              AND (FileId128 = '' OR FileId128 <> $fileId)
              AND IsHardLinkAlias = 0
              AND FullPath <> $path
            ORDER BY CreatedUtc ASC, FullPath ASC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$hash", candidate.ContentHash);
        command.Parameters.AddWithValue("$fileId", candidate.FileId128);
        command.Parameters.AddWithValue("$path", candidate.FullPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadFileRecord(reader)
            : null;
    }

    private async Task<StorageFileRecord?> GetNormalizedDuplicateRetainedRecordAsync(
        long runKey,
        StorageFileRecord candidate,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        long? parentId = null;
        string? name = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT n.ParentId, n.Name
                FROM FileNames n
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE n.RunKey = $run
                  AND p.ContentHash = $hash
                  AND p.FileIdentityKey <> $identity
                  AND n.IsHardLinkAlias = 0
                ORDER BY p.CreatedUtc, n.Id
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$run", runKey);
            command.Parameters.AddWithValue("$hash", candidate.ContentHash);
            command.Parameters.AddWithValue(
                "$identity",
                string.IsNullOrWhiteSpace(candidate.FileId128)
                    ? GetPathHash(candidate.FullPath)
                    : $"{candidate.VolumeSerialNumber:X16}:{candidate.FileId128}");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                parentId = reader.GetInt64(0);
                name = reader.GetString(1);
            }
        }

        if (!parentId.HasValue || name is null)
        {
            return null;
        }

        var path = await BuildFilePathAsync(
            connection,
            runKey,
            parentId.Value,
            name,
            cancellationToken);
        return (await GetNormalizedFileRecordByPathAsync(
            runKey,
            path,
            cancellationToken))!;
    }

    public async Task SetFileHashAsync(
        Guid scanId,
        string path,
        string hash,
        CancellationToken cancellationToken = default,
        FileIdentity? identity = null)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            await SetNormalizedFileHashesAsync(
                format.RunKey,
                [new FileHashUpdate(path, hash, identity)],
                cancellationToken);
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE FileEntries
            SET ContentHash = $hash,
                VolumeSerialNumber = CASE
                    WHEN $hasIdentity = 1 THEN $volumeSerial ELSE VolumeSerialNumber END,
                FileIndexHigh = CASE
                    WHEN $hasIdentity = 1 THEN $fileIndexHigh ELSE FileIndexHigh END,
                FileIndexLow = CASE
                    WHEN $hasIdentity = 1 THEN $fileIndexLow ELSE FileIndexLow END
            WHERE ScanId = $scanId AND FullPath = $path
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$hasIdentity", identity is null ? 0 : 1);
        command.Parameters.AddWithValue("$volumeSerial", identity?.VolumeSerialNumber ?? 0);
        command.Parameters.AddWithValue("$fileIndexHigh", identity?.FileIndexHigh ?? 0);
        command.Parameters.AddWithValue("$fileIndexLow", identity?.FileIndexLow ?? 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"The duplicate fingerprint record for '{path}' no longer exists.");
        }

        await using var invalidate = connection.CreateCommand();
        invalidate.CommandText =
            "DELETE FROM ScanCleanupSummaries WHERE ScanId = $scanId";
        invalidate.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        await invalidate.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetFileHashesAsync(
        Guid scanId,
        IReadOnlyList<FileHashUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            await SetNormalizedFileHashesAsync(
                format.RunKey,
                updates,
                cancellationToken);
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE FileEntries
            SET ContentHash = $hash,
                VolumeSerialNumber = CASE
                    WHEN $hasIdentity = 1 THEN $volumeSerial ELSE VolumeSerialNumber END,
                FileIndexHigh = CASE
                    WHEN $hasIdentity = 1 THEN $fileIndexHigh ELSE FileIndexHigh END,
                FileIndexLow = CASE
                    WHEN $hasIdentity = 1 THEN $fileIndexLow ELSE FileIndexLow END
            WHERE ScanId = $scanId AND FullPath = $path
            """;
        command.Parameters.AddWithValue("$hash", string.Empty);
        command.Parameters.AddWithValue("$hasIdentity", 0);
        command.Parameters.AddWithValue("$volumeSerial", 0L);
        command.Parameters.AddWithValue("$fileIndexHigh", 0L);
        command.Parameters.AddWithValue("$fileIndexLow", 0L);
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$path", string.Empty);
        await command.PrepareAsync(cancellationToken);
        for (var index = 0; index < updates.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var update = updates[index];
            command.Parameters["$hash"].Value = update.Hash;
            command.Parameters["$hasIdentity"].Value = update.Identity is null ? 0 : 1;
            command.Parameters["$volumeSerial"].Value =
                update.Identity?.VolumeSerialNumber ?? 0;
            command.Parameters["$fileIndexHigh"].Value =
                update.Identity?.FileIndexHigh ?? 0;
            command.Parameters["$fileIndexLow"].Value =
                update.Identity?.FileIndexLow ?? 0;
            command.Parameters["$path"].Value = update.Path;
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"The duplicate fingerprint record for '{update.Path}' no longer exists.");
            }
        }

        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText =
                "DELETE FROM ScanCleanupSummaries WHERE ScanId = $scanId";
            invalidate.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
            await invalidate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<DuplicateSizeGroup>> GetNormalizedDuplicateSizeGroupsAsync(
        long runKey,
        long minimumFileSize,
        long maximumFileSize,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(long Size, long ParentId, string Name)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH EligibleSizes AS (
                    SELECT p.LogicalSize
                    FROM PhysicalFiles p
                    INNER JOIN FileNames n
                        ON n.PhysicalFileId = p.Id
                       AND n.IsHardLinkAlias = 0
                    WHERE p.RunKey = $run
                      AND p.LogicalSize >= $minimum
                      AND p.LogicalSize < $maximum
                      AND p.ContentHash IS NULL
                      AND (p.Attributes & $reparse) = 0
                    GROUP BY p.LogicalSize
                    HAVING COUNT(*) > 1
                    ORDER BY p.LogicalSize ASC
                    LIMIT $limit
                )
                SELECT p.LogicalSize, n.ParentId, n.Name
                FROM EligibleSizes e
                INNER JOIN PhysicalFiles p ON p.LogicalSize = e.LogicalSize
                INNER JOIN FileNames n
                    ON n.PhysicalFileId = p.Id
                   AND n.IsHardLinkAlias = 0
                WHERE p.RunKey = $run
                  AND p.ContentHash IS NULL
                  AND (p.Attributes & $reparse) = 0
                ORDER BY p.LogicalSize, n.Name
                """;
            command.Parameters.AddWithValue("$run", runKey);
            command.Parameters.AddWithValue("$minimum", minimumFileSize);
            command.Parameters.AddWithValue("$maximum", maximumFileSize);
            command.Parameters.AddWithValue(
                "$reparse",
                (long)FileAttributes.ReparsePoint);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2)));
            }
        }

        var bySize = new Dictionary<long, List<string>>();
        foreach (var row in rows)
        {
            if (!bySize.TryGetValue(row.Size, out var paths))
            {
                paths = [];
                bySize.Add(row.Size, paths);
            }

            paths.Add(await BuildFilePathAsync(
                connection,
                runKey,
                row.ParentId,
                row.Name,
                cancellationToken));
        }

        return bySize
            .Select(group => new DuplicateSizeGroup(group.Key, group.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<DuplicateSizeGroup>> GetUnsampledDuplicateSizeGroupsAsync(
        Guid scanId,
        long minimumFileSize = 1024 * 1024,
        long maximumFileSize = 4L * 1024 * 1024 * 1024,
        int limit = 250,
        CancellationToken cancellationToken = default,
        int? candidateLimit = null)
    {
        if (candidateLimit <= 0)
        {
            return [];
        }

        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format < 4)
        {
            return await GetUnhashedDuplicateSizeGroupsAsync(
                scanId,
                minimumFileSize,
                maximumFileSize,
                limit,
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(long Size, long ParentId, string Name)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH EligibleSizes AS (
                    SELECT p.LogicalSize
                    FROM PhysicalFiles p
                    INNER JOIN FileNames n
                        ON n.PhysicalFileId = p.Id
                       AND n.IsHardLinkAlias = 0
                    WHERE p.RunKey = $run
                      AND p.LogicalSize >= $minimum
                      AND p.LogicalSize < $maximum
                      AND p.HashState = 0
                      AND (p.Attributes & $reparse) = 0
                    GROUP BY p.LogicalSize
                    HAVING COUNT(*) > 1
                    ORDER BY p.LogicalSize
                    LIMIT $limit
                )
                SELECT p.LogicalSize, n.ParentId, n.Name
                FROM EligibleSizes e
                INNER JOIN PhysicalFiles p ON p.LogicalSize = e.LogicalSize
                INNER JOIN FileNames n
                    ON n.PhysicalFileId = p.Id
                   AND n.IsHardLinkAlias = 0
                WHERE p.RunKey = $run AND p.HashState = 0
                ORDER BY p.LogicalSize, n.Name
                LIMIT $candidateLimit
                """;
            command.Parameters.AddWithValue("$run", format.RunKey);
            command.Parameters.AddWithValue("$minimum", minimumFileSize);
            command.Parameters.AddWithValue("$maximum", maximumFileSize);
            command.Parameters.AddWithValue(
                "$reparse",
                (long)FileAttributes.ReparsePoint);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue(
                "$candidateLimit",
                candidateLimit ?? int.MaxValue);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2)));
            }
        }

        return await BuildDuplicateGroupsAsync(
            connection,
            format.RunKey,
            rows,
            cancellationToken);
    }

    public async Task<IReadOnlyList<DuplicateSizeGroup>> GetSampleHashCollisionGroupsAsync(
        Guid scanId,
        int limit = 250,
        CancellationToken cancellationToken = default,
        int? candidateLimit = null)
    {
        if (candidateLimit <= 0)
        {
            return [];
        }

        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format < 4)
        {
            return await GetUnhashedDuplicateSizeGroupsAsync(
                scanId,
                limit: limit,
                cancellationToken: cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<(long Size, long ParentId, string Name)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH Collisions AS (
                    SELECT LogicalSize, SampleHash
                    FROM PhysicalFiles
                    WHERE RunKey = $run
                      AND HashState = 1
                      AND SampleHash IS NOT NULL
                    GROUP BY LogicalSize, SampleHash
                    HAVING COUNT(*) > 1
                    LIMIT $limit
                )
                SELECT p.LogicalSize, n.ParentId, n.Name
                FROM Collisions c
                INNER JOIN PhysicalFiles p
                    ON p.LogicalSize = c.LogicalSize
                   AND p.SampleHash = c.SampleHash
                INNER JOIN FileNames n
                    ON n.PhysicalFileId = p.Id
                   AND n.IsHardLinkAlias = 0
                WHERE p.RunKey = $run AND p.HashState = 1
                ORDER BY p.LogicalSize, n.Name
                LIMIT $candidateLimit
                """;
            command.Parameters.AddWithValue("$run", format.RunKey);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue(
                "$candidateLimit",
                candidateLimit ?? int.MaxValue);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2)));
            }
        }

        return await BuildDuplicateGroupsAsync(
            connection,
            format.RunKey,
            rows,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<DuplicateSizeGroup>> BuildDuplicateGroupsAsync(
        SqliteConnection connection,
        long runKey,
        IReadOnlyList<(long Size, long ParentId, string Name)> rows,
        CancellationToken cancellationToken)
    {
        var bySize = new Dictionary<long, List<string>>();
        foreach (var row in rows)
        {
            if (!bySize.TryGetValue(row.Size, out var paths))
            {
                paths = [];
                bySize.Add(row.Size, paths);
            }

            paths.Add(await BuildFilePathAsync(
                connection,
                runKey,
                row.ParentId,
                row.Name,
                cancellationToken));
        }

        return bySize
            .Select(group => new DuplicateSizeGroup(group.Key, group.Value))
            .ToList();
    }

    public async Task SetFileSampleHashesAsync(
        Guid scanId,
        IReadOnlyList<FileHashUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format < 4)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(update.Path).TrimEnd('\\');
            var parentPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                continue;
            }

            var parentId = await ResolveFolderIdAsync(
                connection,
                format.RunKey,
                parentPath,
                cancellationToken);
            if (!parentId.HasValue)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE PhysicalFiles
                SET SampleHash = $hash,
                    HashState = 1
                WHERE Id = (
                    SELECT n.PhysicalFileId
                    FROM FileNames n
                    WHERE n.RunKey = $run
                      AND n.ParentId = $parent
                      AND n.Name = $name
                )
                  AND HashState = 0
                """;
            command.Parameters.AddWithValue("$hash", update.Hash);
            command.Parameters.AddWithValue("$run", format.RunKey);
            command.Parameters.AddWithValue("$parent", parentId.Value);
            command.Parameters.AddWithValue("$name", Path.GetFileName(fullPath));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetNormalizedFileHashesAsync(
        long runKey,
        IReadOnlyList<FileHashUpdate> updates,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(update.Path).TrimEnd('\\');
            var parentPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                continue;
            }

            var parentId = await ResolveFolderIdAsync(
                connection,
                runKey,
                parentPath,
                cancellationToken);
            if (!parentId.HasValue)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE PhysicalFiles
                SET ContentHash = $hash,
                    HashState = 2,
                    FileId128 = CASE
                        WHEN $fileId <> '' THEN $fileId ELSE FileId128 END,
                    VolumeSerialNumber = CASE
                        WHEN $hasIdentity = 1 THEN $volume
                        ELSE VolumeSerialNumber END
                WHERE Id = (
                    SELECT n.PhysicalFileId
                    FROM FileNames n
                    WHERE n.RunKey = $run
                      AND n.ParentId = $parent
                      AND n.Name = $name
                )
                """;
            command.Parameters.AddWithValue("$hash", update.Hash);
            command.Parameters.AddWithValue(
                "$fileId",
                update.Identity?.FileId128 ?? string.Empty);
            command.Parameters.AddWithValue(
                "$hasIdentity",
                update.Identity is null ? 0 : 1);
            command.Parameters.AddWithValue(
                "$volume",
                update.Identity?.VolumeSerialNumber ?? 0);
            command.Parameters.AddWithValue("$run", runKey);
            command.Parameters.AddWithValue("$parent", parentId.Value);
            command.Parameters.AddWithValue("$name", Path.GetFileName(fullPath));
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"The normalized file '{update.Path}' could not be fingerprinted.");
            }
        }

        await MaterializeDuplicateCandidatesAsync(
            connection,
            transaction,
            runKey,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MaterializeDuplicateCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM NormalizedCleanupCandidates
            WHERE RunKey = $run AND PolicyId = $policy;
            WITH Ranked AS (
                SELECT n.Id,
                       ROW_NUMBER() OVER (
                           PARTITION BY p.ContentHash
                           ORDER BY p.CreatedUtc, n.Id
                       ) AS DuplicateRank
                FROM FileNames n
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE n.RunKey = $run
                  AND n.IsHardLinkAlias = 0
                  AND p.ContentHash IS NOT NULL
                  AND LENGTH(p.ContentHash) = 64
                  AND (p.Attributes & $protected) = 0
            )
            INSERT INTO NormalizedCleanupCandidates (RunKey, PolicyId, FileNameId)
            SELECT $run, $policy, Id
            FROM Ranked
            WHERE DuplicateRank > 1;
            """;
        command.Parameters.AddWithValue("$run", runKey);
        command.Parameters.AddWithValue(
            "$policy",
            (int)CleanupPolicyId.LargeDuplicates);
        command.Parameters.AddWithValue(
            "$protected",
            (long)(FileAttributes.System |
                   FileAttributes.Device |
                   FileAttributes.ReparsePoint));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RemoveNormalizedIndexedFileAsync(
        long runKey,
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var trimmed = Path.GetFullPath(path).TrimEnd('\\');
        var parentPath = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return;
        }

        var parentId = await ResolveFolderIdAsync(
            connection,
            runKey,
            parentPath,
            cancellationToken);
        if (!parentId.HasValue)
        {
            return;
        }

        long fileNameId;
        long physicalId;
        long logicalSize;
        long allocatedSize;
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT n.Id, n.PhysicalFileId, p.LogicalSize,
                       CASE WHEN n.IsHardLinkAlias = 0
                            THEN p.AllocatedSize ELSE 0 END
                FROM FileNames n
                INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
                WHERE n.RunKey = $run
                  AND n.ParentId = $parent
                  AND n.Name = $name
                """;
            select.Parameters.AddWithValue("$run", runKey);
            select.Parameters.AddWithValue("$parent", parentId.Value);
            select.Parameters.AddWithValue("$name", Path.GetFileName(trimmed));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            fileNameId = reader.GetInt64(0);
            physicalId = reader.GetInt64(1);
            logicalSize = reader.GetInt64(2);
            allocatedSize = reader.GetInt64(3);
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteName = connection.CreateCommand())
        {
            deleteName.Transaction = transaction;
            deleteName.CommandText =
                "DELETE FROM FileNames WHERE Id = $id";
            deleteName.Parameters.AddWithValue("$id", fileNameId);
            await deleteName.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateFolders = connection.CreateCommand())
        {
            updateFolders.Transaction = transaction;
            updateFolders.CommandText = """
                WITH RECURSIVE Ancestors(Id, ParentId) AS (
                    SELECT Id, ParentId
                    FROM FolderNodes
                    WHERE RunKey = $run AND Id = $parent
                    UNION ALL
                    SELECT f.Id, f.ParentId
                    FROM FolderNodes f
                    INNER JOIN Ancestors a ON f.Id = a.ParentId
                    WHERE f.RunKey = $run
                )
                UPDATE FolderNodes
                SET LogicalSize = MAX(0, LogicalSize - $logical),
                    AllocatedSize = MAX(0, AllocatedSize - $allocated),
                    FileCount = MAX(0, FileCount - 1),
                    DirectLogicalSize = CASE WHEN Id = $parent
                        THEN MAX(0, DirectLogicalSize - $logical)
                        ELSE DirectLogicalSize END,
                    DirectAllocatedSize = CASE WHEN Id = $parent
                        THEN MAX(0, DirectAllocatedSize - $allocated)
                        ELSE DirectAllocatedSize END,
                    DirectFileCount = CASE WHEN Id = $parent
                        THEN MAX(0, DirectFileCount - 1)
                        ELSE DirectFileCount END
                WHERE Id IN (SELECT Id FROM Ancestors)
                """;
            updateFolders.Parameters.AddWithValue("$run", runKey);
            updateFolders.Parameters.AddWithValue("$parent", parentId.Value);
            updateFolders.Parameters.AddWithValue("$logical", logicalSize);
            updateFolders.Parameters.AddWithValue("$allocated", allocatedSize);
            await updateFolders.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deletePhysical = connection.CreateCommand())
        {
            deletePhysical.Transaction = transaction;
            deletePhysical.CommandText = """
                DELETE FROM PhysicalFiles
                WHERE Id = $id
                  AND NOT EXISTS (
                      SELECT 1 FROM FileNames
                      WHERE PhysicalFileId = $id
                  )
                """;
            deletePhysical.Parameters.AddWithValue("$id", physicalId);
            await deletePhysical.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordCleanupActionAsync(
        Guid? scanId,
        CleanupPolicyId policy,
        string path,
        long sizeBytes,
        bool automatic,
        string result,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CleanupHistory (
                TimestampUtc, ScanId, PolicyId, OriginalPath, SizeBytes,
                Automatic, Result, Error
            )
            VALUES (
                $timestamp, $scanId, $policy, $path, $size,
                $automatic, $result, $error
            )
            """;
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$scanId", scanId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$policy", (int)policy);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$automatic", automatic ? 1 : 0);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> BeginCleanupActionAsync(
        Guid? scanId,
        CleanupPolicyId policy,
        string path,
        long sizeBytes,
        bool automatic,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CleanupHistory (
                TimestampUtc, ScanId, PolicyId, OriginalPath, SizeBytes,
                Automatic, Result, Error
            )
            VALUES (
                $timestamp, $scanId, $policy, $path, $size,
                $automatic, 'Pending', NULL
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$scanId", scanId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$policy", (int)policy);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$automatic", automatic ? 1 : 0);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task CompleteCleanupActionAsync(
        long actionId,
        string result,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CleanupHistory
            SET Result = $result,
                Error = $error
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", actionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Cleanup audit action {actionId} could not be finalized.");
        }
    }

    public async Task RemoveIndexedFileAsync(
        Guid scanId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var format = await GetScanFormatAsync(scanId, cancellationToken);
        if (format.Format >= 4)
        {
            await RemoveNormalizedIndexedFileAsync(
                format.RunKey,
                path,
                cancellationToken);
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        string? parentPath = null;
        long sizeBytes = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ParentPath, SizeBytes
                FROM FileEntries
                WHERE ScanId = $scanId AND FullPath = $path
                """;
            command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
            command.Parameters.AddWithValue("$path", path);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                parentPath = reader.GetString(0);
                sizeBytes = reader.GetInt64(1);
            }
        }

        if (parentPath is null)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM FileEntries
                WHERE ScanId = $scanId AND FullPath = $path
                """;
            delete.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
            delete.Parameters.AddWithValue("$path", path);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var ancestor = parentPath;
        while (!string.IsNullOrWhiteSpace(ancestor))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE FolderEntries
                SET SizeBytes = MAX(0, SizeBytes - $size),
                    FileCount = MAX(0, FileCount - 1),
                    DirectSizeBytes = CASE
                        WHEN FullPath = $parent
                        THEN MAX(0, DirectSizeBytes - $size)
                        ELSE DirectSizeBytes
                    END,
                    DirectFileCount = CASE
                        WHEN FullPath = $parent
                        THEN MAX(0, DirectFileCount - 1)
                        ELSE DirectFileCount
                    END
                WHERE ScanId = $scanId AND FullPath = $ancestor
                """;
            update.Parameters.AddWithValue("$size", sizeBytes);
            update.Parameters.AddWithValue("$parent", parentPath);
            update.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
            update.Parameters.AddWithValue("$ancestor", ancestor);
            await update.ExecuteNonQueryAsync(cancellationToken);

            if (Path.GetPathRoot(ancestor)?.TrimEnd('\\').Equals(
                    ancestor.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                break;
            }

            ancestor = Directory.GetParent(ancestor.TrimEnd('\\'))?.FullName ?? string.Empty;
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CleanupHistoryItem>> GetCleanupHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TimestampUtc, OriginalPath, SizeBytes, PolicyId, Automatic, Result, Error
            FROM CleanupHistory
            ORDER BY TimestampUtc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var history = new List<CleanupHistoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new CleanupHistoryItem
            {
                TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                Path = reader.GetString(1),
                SizeBytes = reader.GetInt64(2),
                Policy = (CleanupPolicyId)reader.GetInt32(3),
                Automatic = reader.GetBoolean(4),
                Result = reader.GetString(5),
                Error = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return history;
    }

    public async Task PruneHistoryAsync(
        int completedScansPerDrive = 2,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var staleRuns = new List<(string ScanId, long RunKey, int Format)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                WITH Ranked AS (
                    SELECT Id, RunKey, ScanFormat,
                           ROW_NUMBER() OVER (
                               PARTITION BY COALESCE(
                                   NULLIF(VolumeSerialNumber, ''),
                                   DriveLetter)
                               ORDER BY StartedUtc DESC
                           ) AS RowNumber
                    FROM ScanRuns
                    WHERE Status = $completed
                )
                SELECT Id, RunKey, ScanFormat
                FROM Ranked
                WHERE RowNumber > 1
                """;
            query.Parameters.AddWithValue(
                "$completed",
                (int)StorageScanStatus.Completed);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                staleRuns.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    reader.GetInt32(2)));
            }
        }

        foreach (var run in staleRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.Format >= 4)
            {
                await DeleteInChunksAsync(
                    connection,
                    """
                    DELETE FROM FileNames
                    WHERE Id IN (
                        SELECT Id FROM FileNames
                        WHERE RunKey = $run
                        LIMIT $limit
                    )
                    """,
                    ("$run", run.RunKey),
                    cancellationToken);
                await DeleteInChunksAsync(
                    connection,
                    """
                    DELETE FROM PhysicalFiles
                    WHERE Id IN (
                        SELECT Id FROM PhysicalFiles
                        WHERE RunKey = $run
                        LIMIT $limit
                    )
                    """,
                    ("$run", run.RunKey),
                    cancellationToken);
            }
            else
            {
                await DeleteInChunksAsync(
                    connection,
                    """
                    DELETE FROM FileEntries
                    WHERE ScanId = $scan
                      AND FullPath IN (
                          SELECT FullPath FROM FileEntries
                          WHERE ScanId = $scan
                          LIMIT $limit
                      )
                    """,
                    ("$scan", run.ScanId),
                    cancellationToken);
            }
        }

        await using var cleanupRuns = connection.CreateCommand();
        cleanupRuns.CommandText = """
            DELETE FROM ScanRuns
            WHERE Id IN (
                SELECT Id
                FROM (
                    SELECT Id,
                           ROW_NUMBER() OVER (
                               PARTITION BY COALESCE(
                                   NULLIF(VolumeSerialNumber, ''),
                                   DriveLetter)
                               ORDER BY StartedUtc DESC
                           ) AS RowNumber
                    FROM ScanRuns
                    WHERE Status <> $running
                )
                WHERE RowNumber > $keep
            );
            DELETE FROM ScanRuns
            WHERE StartedUtc < $stale
              AND Status <> $completed
              AND Status <> $running;
            """;
        cleanupRuns.Parameters.AddWithValue(
            "$running",
            (int)StorageScanStatus.Running);
        cleanupRuns.Parameters.AddWithValue(
            "$completed",
            (int)StorageScanStatus.Completed);
        cleanupRuns.Parameters.AddWithValue("$keep", completedScansPerDrive);
        cleanupRuns.Parameters.AddWithValue(
            "$stale",
            DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeMilliseconds());
        await cleanupRuns.ExecuteNonQueryAsync(cancellationToken);
        await CheckpointAsync(cancellationToken);
    }

    private static async Task DeleteInChunksAsync(
        SqliteConnection connection,
        string commandText,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken,
        int chunkSize = 10_000)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            command.Parameters.AddWithValue("$limit", chunkSize);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            if (deleted == 0)
            {
                return;
            }
        }
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    public StorageIndexUsage GetStorageUsage()
    {
        long GetLength(string path)
        {
            try
            {
                return new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }

        var databaseBytes = GetLength(DatabasePath);
        var walBytes = GetLength(DatabasePath + "-wal");
        var sharedMemoryBytes = GetLength(DatabasePath + "-shm");
        return new StorageIndexUsage(
            databaseBytes,
            walBytes,
            sharedMemoryBytes,
            databaseBytes + walBytes + sharedMemoryBytes);
    }

    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        using var executionLock = await ScanExecutionLock.AcquireAsync(
            DatabasePath,
            cancellationToken,
            TimeSpan.FromSeconds(5));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM; ANALYZE; PRAGMA optimize;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task CheckpointAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA wal_checkpoint(PASSIVE); PRAGMA optimize;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IndexHealthResult> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        using var executionLock = await ScanExecutionLock.AcquireAsync(
            DatabasePath,
            cancellationToken,
            TimeSpan.FromSeconds(5));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        string quickCheck;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA quick_check(1)";
            quickCheck =
                Convert.ToString(
                    await command.ExecuteScalarAsync(cancellationToken)) ??
                string.Empty;
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText =
            "SELECT COUNT(*) FROM pragma_foreign_key_check";
        var violationCount = Convert.ToInt64(
            await foreignKeys.ExecuteScalarAsync(cancellationToken));
        return new IndexHealthResult(
            string.Equals(
                quickCheck,
                "ok",
                StringComparison.OrdinalIgnoreCase) &&
            violationCount == 0,
            quickCheck,
            violationCount);
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-32768;
            PRAGMA mmap_size=134217728;
            PRAGMA synchronous=NORMAL;
            PRAGMA journal_size_limit=67108864;
            PRAGMA busy_timeout=30000;
            PRAGMA foreign_keys=ON;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ConfigureIndexesAsync(
        SqliteConnection connection,
        bool databaseIsNew,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = databaseIsNew
            ? """
                CREATE INDEX IX_FileEntries_ScanParent
                    ON FileEntries (ScanId, ParentPath, SizeBytes DESC);
                CREATE INDEX IX_FileEntries_ScanCategory
                    ON FileEntries (ScanId, Category, SizeBytes DESC);
                CREATE INDEX IX_FileEntries_ScanSize
                    ON FileEntries (ScanId, SizeBytes);
                CREATE INDEX IX_FolderEntries_ScanParent
                    ON FolderEntries (ScanId, ParentPath, SizeBytes DESC);
                """
            : """
                DROP INDEX IF EXISTS IX_FileEntries_ScanModified;
                DROP INDEX IF EXISTS IX_FileEntries_Path;
                DROP INDEX IF EXISTS IX_FolderEntries_ScanPath;
                DROP INDEX IF EXISTS IX_FolderEntries_ScanCategory;
                """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private StorageScanRun ReadScanRun(SqliteDataReader reader)
    {
        var bytesScanned = reader.GetInt64(12);
        var scanFormat = reader.FieldCount > 17
            ? reader.GetInt32(17)
            : 1;
        var allocatedBytesScanned = reader.FieldCount > 15
            ? reader.GetInt64(15)
            : bytesScanned;
        if (scanFormat < 4 && allocatedBytesScanned == 0)
        {
            allocatedBytesScanned = bytesScanned;
        }

        var scan = new StorageScanRun
        {
            Id = Guid.ParseExact(reader.GetString(0), "N"),
            DriveLetter = reader.GetString(1),
            DriveName = reader.GetString(2),
            FileSystem = reader.GetString(3),
            TotalBytes = reader.GetInt64(4),
            UsedBytes = reader.GetInt64(5),
            FreeBytes = reader.GetInt64(6),
            StartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            CompletedUtc = reader.IsDBNull(8)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            Status = (StorageScanStatus)reader.GetInt32(9),
            FilesScanned = reader.GetInt64(10),
            FoldersScanned = reader.GetInt64(11),
            BytesScanned = bytesScanned,
            InaccessibleItems = reader.GetInt64(13),
            ErrorMessage = reader.IsDBNull(14) ? null : reader.GetString(14),
            AllocatedBytesScanned = allocatedBytesScanned,
            RunKey = reader.FieldCount > 16 && !reader.IsDBNull(16)
                ? reader.GetInt64(16)
                : 0,
            ScanFormat = scanFormat,
        };
        _scanFormats[scan.Id] = new ScanFormatInfo(scan.RunKey, scan.ScanFormat);
        return scan;
    }

    private async Task<ScanFormatInfo> GetScanFormatAsync(
        Guid scanId,
        CancellationToken cancellationToken)
    {
        if (_scanFormats.TryGetValue(scanId, out var format))
        {
            return format;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(RunKey, 0), ScanFormat
            FROM ScanRuns
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", scanId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var info = await reader.ReadAsync(cancellationToken)
            ? new ScanFormatInfo(reader.GetInt64(0), reader.GetInt32(1))
            : throw new InvalidOperationException(
                $"Scan {scanId:N} no longer exists.");
        _scanFormats[scanId] = info;
        return info;
    }

    private sealed record ScanFormatInfo(long RunKey, int Format);

    private static async Task<IReadOnlyList<StorageItem>> ReadStorageItemsAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var items = new List<StorageItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadStorageItem(reader));
        }

        return items;
    }

    private static StorageItem ReadStorageItem(SqliteDataReader reader)
    {
        var attributes = (FileAttributes)reader.GetInt64(7);
        return new StorageItem
        {
            Name = reader.GetString(2),
            Path = reader.GetString(1),
            Category = reader.GetString(5),
            Kind = reader.GetString(6),
            ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            SizeBytes = reader.GetInt64(3),
            IsFolder = reader.GetBoolean(0),
            IsSystem = (attributes & FileAttributes.System) != 0,
            IsCloudOnly = false,
        };
    }

    private static StorageFileRecord ReadFileRecord(SqliteDataReader reader) =>
        new()
        {
            ScanId = Guid.ParseExact(reader.GetString(0), "N"),
            FullPath = reader.GetString(1),
            Name = reader.GetString(2),
            ParentPath = reader.GetString(3),
            SizeBytes = reader.GetInt64(4),
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            AccessedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            Attributes = reader.GetInt64(8),
            Extension = reader.GetString(9),
            Category = reader.GetString(10),
            Tag = reader.GetString(11),
            VolumeSerialNumber = reader.FieldCount > 12 ? reader.GetInt64(12) : 0,
            FileIndexHigh = reader.FieldCount > 13 ? reader.GetInt64(13) : 0,
            FileIndexLow = reader.FieldCount > 14 ? reader.GetInt64(14) : 0,
            ContentHash = reader.FieldCount > 15 && !reader.IsDBNull(15)
                ? reader.GetString(15)
                : null,
            AllocatedSizeBytes = reader.FieldCount > 16 ? reader.GetInt64(16) : 0,
            PhysicalSizeBytes = reader.FieldCount > 17 ? reader.GetInt64(17) : 0,
            FileId128 = reader.FieldCount > 18 && !reader.IsDBNull(18)
                ? reader.GetString(18)
                : string.Empty,
            ReparseTag = reader.FieldCount > 19
                ? unchecked((uint)reader.GetInt64(19))
                : 0,
            LinkCount = reader.FieldCount > 20
                ? checked((uint)reader.GetInt64(20))
                : 1,
            IsHardLinkAlias = reader.FieldCount > 21 && reader.GetInt32(21) != 0,
            IsIncomplete = reader.FieldCount > 22 && reader.GetInt32(22) != 0,
        };

    private static void AddCategory(
        ICollection<StorageCategory> categories,
        string name,
        long sizeBytes,
        long itemCount)
    {
        categories.Add(new StorageCategory
        {
            Name = name,
            Description = StorageCategoryClassifier.DescriptionFor(name),
            Glyph = StorageCategoryClassifier.GlyphFor(name),
            SizeBytes = sizeBytes,
            ItemCount = checked((int)Math.Min(int.MaxValue, itemCount)),
        });
    }

    private static async Task SaveCategorySummariesAsync(
        SqliteConnection connection,
        Guid scanId,
        IReadOnlyList<StorageCategory> categories,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScanCategorySummaries (
                ScanId, Category, SizeBytes, ItemCount
            )
            VALUES ($scanId, $category, $size, $count)
            ON CONFLICT(ScanId, Category) DO UPDATE SET
                SizeBytes = excluded.SizeBytes,
                ItemCount = excluded.ItemCount
            """;
        command.Parameters.AddWithValue("$scanId", scanId.ToString("N"));
        command.Parameters.AddWithValue("$category", string.Empty);
        command.Parameters.AddWithValue("$size", 0L);
        command.Parameters.AddWithValue("$count", 0L);
        await command.PrepareAsync(cancellationToken);
        foreach (var category in categories)
        {
            command.Parameters["$category"].Value = category.Name;
            command.Parameters["$size"].Value = category.SizeBytes;
            command.Parameters["$count"].Value = category.ItemCount;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var version = await GetSchemaVersionAsync(connection, transaction, cancellationToken);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Storage index schema version {version} is newer than this app supports ({CurrentSchemaVersion}).");
        }

        while (version < CurrentSchemaVersion)
        {
            switch (version)
            {
                case 1:
                    await EnsureFileIdentityColumnsAsync(
                        connection,
                        transaction,
                        cancellationToken);
                    version = 2;
                    break;
                case 2:
                    await EnsureProductionColumnsAsync(
                        connection,
                        transaction,
                        cancellationToken);
                    version = 3;
                    break;
                case 3:
                    await EnsureNormalizedSchemaColumnsAsync(
                        connection,
                        transaction,
                        cancellationToken);
                    version = 4;
                    break;
                case 4:
                    await EnsureColumnsAsync(
                        connection,
                        transaction,
                        "PhysicalFiles",
                        [("ChangeTime", "INTEGER NOT NULL DEFAULT 0")],
                        cancellationToken);
                    version = 5;
                    break;
                case 5:
                    await EnsureLegacyDuplicateIndexAsync(
                        connection,
                        transaction,
                        cancellationToken);
                    version = 6;
                    break;
                default:
                    throw new InvalidDataException(
                        $"Storage index schema version {version} cannot be migrated.");
            }

            await SetSchemaVersionAsync(
                connection,
                transaction,
                version,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version FROM SchemaVersion WHERE Id = 1";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? throw new InvalidDataException("Storage index schema version is missing.")
            : Convert.ToInt32(value);
    }

    private static async Task SetSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE SchemaVersion SET Version = $version WHERE Id = 1";
        command.Parameters.AddWithValue("$version", version);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException("Storage index schema version could not be updated.");
        }
    }

    private static async Task ReconcilePendingCleanupActionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var pending = new List<(long Id, string Path)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, OriginalPath
                FROM CleanupHistory
                WHERE Result = 'Pending'
                ORDER BY Id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        foreach (var (id, path) in pending)
        {
            bool? exists = null;
            try
            {
                _ = File.GetAttributes(path);
                exists = true;
            }
            catch (FileNotFoundException)
            {
                exists = false;
            }
            catch (DirectoryNotFoundException)
            {
                exists = false;
            }
            catch (IOException)
            {
                // An indeterminate path stays Pending so a later launch can retry recovery.
            }
            catch (UnauthorizedAccessException)
            {
                // An indeterminate path stays Pending so a later launch can retry recovery.
            }

            if (exists is null)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE CleanupHistory
                SET Result = $result,
                    Error = $error
                WHERE Id = $id AND Result = 'Pending'
                """;
            command.Parameters.AddWithValue(
                "$result",
                exists.Value ? "Interrupted" : "SourceMissingAfterPending");
            command.Parameters.AddWithValue(
                "$error",
                exists.Value
                    ? "Storage Lens stopped before recycling was confirmed. The source file still exists."
                    : "Storage Lens stopped after cleanup began; the source path is no longer present.");
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ReconcileInterruptedScansAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var interrupted = new List<(string Id, int? ProcessId, long? StartedUtc)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, OwnerProcessId, OwnerProcessStartedUtc
                FROM ScanRuns
                WHERE Status = $running
                """;
            command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                interrupted.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }

        foreach (var lease in interrupted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lease.ProcessId.HasValue &&
                lease.StartedUtc.HasValue &&
                ScanOwner.IsAlive(lease.ProcessId.Value, lease.StartedUtc.Value))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ScanRuns
                SET Status = $failed,
                    CompletedUtc = $completed,
                    ErrorMessage = $error,
                    OwnerProcessId = NULL,
                    OwnerProcessStartedUtc = NULL,
                    LeaseHeartbeatUtc = NULL
                WHERE Id = $id
                  AND Status = $running
                  AND (($pid IS NULL AND OwnerProcessId IS NULL)
                       OR (OwnerProcessId = $pid
                           AND OwnerProcessStartedUtc = $started))
                """;
            command.Parameters.AddWithValue("$failed", (int)StorageScanStatus.Failed);
            command.Parameters.AddWithValue(
                "$completed",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue(
                "$error",
                "The owning process stopped before the scan was published. Partial data is retained for recovery.");
            command.Parameters.AddWithValue("$id", lease.Id);
            command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
            command.Parameters.AddWithValue(
                "$pid",
                lease.ProcessId.HasValue ? lease.ProcessId.Value : DBNull.Value);
            command.Parameters.AddWithValue(
                "$started",
                lease.StartedUtc.HasValue ? lease.StartedUtc.Value : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureFileIdentityColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(FileEntries)";
            command.Transaction = transaction;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[]
                 {
                     ("VolumeSerialNumber", "INTEGER NOT NULL DEFAULT 0"),
                     ("FileIndexHigh", "INTEGER NOT NULL DEFAULT 0"),
                     ("FileIndexLow", "INTEGER NOT NULL DEFAULT 0"),
                 })
        {
            if (columns.Contains(column.Item1))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"ALTER TABLE FileEntries ADD COLUMN {column.Item1} {column.Item2}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureProductionColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await EnsureColumnsAsync(
            connection,
            transaction,
            "ScanRuns",
            [
                ("OwnerProcessId", "INTEGER"),
                ("OwnerProcessStartedUtc", "INTEGER"),
                ("LeaseHeartbeatUtc", "INTEGER"),
                ("Generation", "INTEGER NOT NULL DEFAULT 1"),
                ("VolumeSerialNumber", "TEXT"),
                ("AllocatedBytesScanned", "INTEGER NOT NULL DEFAULT 0"),
            ],
            cancellationToken);
        await EnsureColumnsAsync(
            connection,
            transaction,
            "FileEntries",
            [
                ("AllocatedSizeBytes", "INTEGER NOT NULL DEFAULT 0"),
                ("PhysicalSizeBytes", "INTEGER NOT NULL DEFAULT 0"),
                ("FileId128", "TEXT NOT NULL DEFAULT ''"),
                ("FileId64", "TEXT NOT NULL DEFAULT ''"),
                ("ReparseTag", "INTEGER NOT NULL DEFAULT 0"),
                ("LinkCount", "INTEGER NOT NULL DEFAULT 1"),
                ("IsHardLinkAlias", "INTEGER NOT NULL DEFAULT 0"),
                ("IsIncomplete", "INTEGER NOT NULL DEFAULT 0"),
            ],
            cancellationToken);
        await EnsureColumnsAsync(
            connection,
            transaction,
            "FolderEntries",
            [
                ("AllocatedSizeBytes", "INTEGER NOT NULL DEFAULT 0"),
                ("ReparseTag", "INTEGER NOT NULL DEFAULT 0"),
                ("IsIncomplete", "INTEGER NOT NULL DEFAULT 0"),
            ],
            cancellationToken);

    }

    private static async Task EnsureColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<(string Name, string Definition)> columns,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var (name, definition) in columns)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureNormalizedSchemaColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var removeInvalidBootstrap = connection.CreateCommand())
        {
            removeInvalidBootstrap.Transaction = transaction;
            removeInvalidBootstrap.CommandText = """
                DROP TABLE IF EXISTS NormalizedCleanupCandidates;
                DROP TABLE IF EXISTS FileNames;
                DROP TABLE IF EXISTS ScanWorkItems;
                DROP TABLE IF EXISTS PhysicalFiles;
                DROP TABLE IF EXISTS FolderNodes;
                """;
            await removeInvalidBootstrap.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureColumnsAsync(
            connection,
            transaction,
            "ScanRuns",
            [
                ("RunKey", "INTEGER"),
                ("ScanFormat", "INTEGER NOT NULL DEFAULT 1"),
                ("JournalId", "INTEGER"),
                ("LastUsn", "INTEGER"),
                ("ScanPhase", "TEXT"),
                ("PhaseStartedUtc", "INTEGER"),
            ],
            cancellationToken);
        await using (var backfill = connection.CreateCommand())
        {
            backfill.Transaction = transaction;
            backfill.CommandText = """
                UPDATE ScanRuns
                SET RunKey = rowid
                WHERE RunKey IS NULL
                """;
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ScanRuns_RunKey
                ON ScanRuns (RunKey)
            """;
        await index.ExecuteNonQueryAsync(cancellationToken);

        await CreateNormalizedTablesAsync(
            connection,
            transaction,
            cancellationToken);
    }

    private static async Task EnsureLegacyDuplicateIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_FileEntries_DuplicateHash
            ON FileEntries (
                ScanId, ContentHash, CreatedUtc, FullPath, SizeBytes
            )
            WHERE ContentHash IS NOT NULL
              AND LENGTH(ContentHash) = 64
              AND IsHardLinkAlias = 0
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateNormalizedTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        CREATE TABLE FolderNodes (
            RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ParentId INTEGER,
            Name TEXT NOT NULL,
            PathHash TEXT NOT NULL,
            LogicalSize INTEGER NOT NULL DEFAULT 0,
            AllocatedSize INTEGER NOT NULL DEFAULT 0,
            DirectLogicalSize INTEGER NOT NULL DEFAULT 0,
            DirectAllocatedSize INTEGER NOT NULL DEFAULT 0,
            FileCount INTEGER NOT NULL DEFAULT 0,
            DirectFileCount INTEGER NOT NULL DEFAULT 0,
            Depth INTEGER NOT NULL,
            Attributes INTEGER NOT NULL,
            ReparseTag INTEGER NOT NULL,
            ModifiedUtc INTEGER NOT NULL,
            Category INTEGER NOT NULL,
            Tag INTEGER NOT NULL,
            FileId128 TEXT NOT NULL DEFAULT '',
            FileId64 TEXT NOT NULL DEFAULT '',
            IsIncomplete INTEGER NOT NULL DEFAULT 0,
            LastSeenGeneration INTEGER NOT NULL DEFAULT 1,
            UNIQUE (RunKey, ParentId, Name)
        );
        CREATE INDEX IX_FolderNodes_ParentSize
            ON FolderNodes (RunKey, ParentId, AllocatedSize DESC);
        CREATE INDEX IX_FolderNodes_PathHash
            ON FolderNodes (RunKey, PathHash);
        CREATE INDEX IX_FolderNodes_FileId
            ON FolderNodes (RunKey, FileId64);

        CREATE TABLE PhysicalFiles (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
            FileIdentityKey TEXT NOT NULL,
            FileId128 TEXT NOT NULL,
            VolumeSerialNumber INTEGER NOT NULL,
            LogicalSize INTEGER NOT NULL,
            AllocatedSize INTEGER NOT NULL,
            CreatedUtc INTEGER NOT NULL,
            ModifiedUtc INTEGER NOT NULL,
            AccessedUtc INTEGER NOT NULL,
            Attributes INTEGER NOT NULL,
            ReparseTag INTEGER NOT NULL,
            LinkCount INTEGER NOT NULL DEFAULT 1,
            ChangeTime INTEGER NOT NULL DEFAULT 0,
            ContentHash TEXT,
            SampleHash TEXT,
            HashState INTEGER NOT NULL DEFAULT 0,
            LastSeenGeneration INTEGER NOT NULL DEFAULT 1,
            UNIQUE (RunKey, FileIdentityKey)
        );
        CREATE INDEX IX_PhysicalFiles_SizeHash
            ON PhysicalFiles (RunKey, LogicalSize, HashState);
        CREATE INDEX IX_PhysicalFiles_ContentHash
            ON PhysicalFiles (RunKey, ContentHash)
            WHERE ContentHash IS NOT NULL;

        CREATE TABLE FileNames (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
            ParentId INTEGER NOT NULL,
            PhysicalFileId INTEGER NOT NULL REFERENCES PhysicalFiles(Id) ON DELETE CASCADE,
            Name TEXT NOT NULL,
            Extension TEXT NOT NULL,
            Category INTEGER NOT NULL,
            Tag INTEGER NOT NULL,
            IsHardLinkAlias INTEGER NOT NULL DEFAULT 0,
            LastSeenGeneration INTEGER NOT NULL DEFAULT 1,
            UNIQUE (RunKey, ParentId, Name)
        );
        CREATE INDEX IX_FileNames_ParentSize
            ON FileNames (RunKey, ParentId);
        CREATE INDEX IX_FileNames_Search
            ON FileNames (RunKey, Name, Category);

        CREATE TABLE NormalizedCleanupCandidates (
            RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
            PolicyId INTEGER NOT NULL,
            FileNameId INTEGER NOT NULL REFERENCES FileNames(Id) ON DELETE CASCADE,
            PRIMARY KEY (RunKey, PolicyId, FileNameId)
        ) WITHOUT ROWID;
        CREATE INDEX IX_NormalizedCleanupCandidates_Page
            ON NormalizedCleanupCandidates (RunKey, PolicyId);

        CREATE TABLE ScanWorkItems (
            RunKey INTEGER NOT NULL REFERENCES ScanRuns(RunKey) ON DELETE CASCADE,
            PathHash TEXT NOT NULL,
            FullPath TEXT NOT NULL,
            Depth INTEGER NOT NULL,
            State INTEGER NOT NULL DEFAULT 0,
            AttemptCount INTEGER NOT NULL DEFAULT 0,
            LastError TEXT,
            UpdatedUtc INTEGER NOT NULL,
            PRIMARY KEY (RunKey, PathHash)
        ) WITHOUT ROWID;
        CREATE INDEX IX_ScanWorkItems_Pending
            ON ScanWorkItems (RunKey, State, UpdatedUtc);
        """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string NormalizeDriveLetter(string driveLetter)
    {
        var normalized = driveLetter.Trim();
        if (normalized.EndsWith('\\'))
        {
            normalized = normalized[..^1];
        }

        return normalized.EndsWith(':')
            ? normalized
            : normalized + ":";
    }

    internal static string NormalizeFolderPath(string path)
    {
        var normalized = Path.GetFullPath(path).TrimEnd('\\');
        return normalized.Length == 2 && normalized[1] == ':'
            ? normalized + "\\"
            : normalized;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string GetPathHash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                path.TrimEnd('\\').ToUpperInvariant())));

    private static IEnumerable<CleanupPolicyId> GetMaterializedPolicies(
        StorageFileRecord record)
    {
        var now = DateTimeOffset.Now;
        if (CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.TemporaryFiles,
                now,
                false).IsAllowed)
        {
            yield return CleanupPolicyId.TemporaryFiles;
        }

        if (CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.OldDownloads,
                now,
                false).IsAllowed)
        {
            yield return CleanupPolicyId.OldDownloads;
        }

        if (CleanupPolicyService.IsKnownPackageCache(record.FullPath))
        {
            yield return CleanupPolicyId.PackageCaches;
        }

        if (StorageCategoryClassifier.IsInside(
                record.FullPath,
                @"C:\Windows.old"))
        {
            yield return CleanupPolicyId.PreviousWindowsInstallation;
        }
    }

    private static string BuildCleanupSelect(
        SqliteCommand command,
        CleanupPolicyId policy,
        DateTimeOffset now,
        string? selectColumns = null,
        bool groupByParent = false,
        string? parentPath = null)
    {
        var protectedAttributes = (long)(FileAttributes.System | FileAttributes.Device | FileAttributes.ReparsePoint);
        command.Parameters.AddWithValue("$protectedAttributes", protectedAttributes);
        var parentFilter = string.Empty;
        if (parentPath is not null)
        {
            var normalizedParent = NormalizeFolderPath(parentPath);
            command.Parameters.AddWithValue(
                "$candidateParent",
                normalizedParent);
            command.Parameters.AddWithValue(
                "$candidateDescendant",
                EnsureTrailingSeparator(normalizedParent));
            command.Parameters.AddWithValue(
                "$candidateDescendantUpper",
                GetPrefixUpperBound(
                    EnsureTrailingSeparator(normalizedParent)));
            parentFilter =
                """
                AND (
                    ParentPath = $candidateParent
                    OR (
                        ParentPath >= $candidateDescendant
                        AND ParentPath < $candidateDescendantUpper
                    )
                )
                """;
        }

        var selection = selectColumns ?? """
                ScanId, FullPath, Name, ParentPath, SizeBytes, CreatedUtc, ModifiedUtc,
                AccessedUtc, Attributes, Extension, Category, Tag, VolumeSerialNumber,
                FileIndexHigh, FileIndexLow, ContentHash, AllocatedSizeBytes,
                PhysicalSizeBytes, FileId128, ReparseTag, LinkCount,
                IsHardLinkAlias, IsIncomplete
            """;
        var common = """
            (Attributes & $protectedAttributes) = 0
            """;
        string where;
        string order;

        if (policy == CleanupPolicyId.TemporaryFiles ||
            policy == CleanupPolicyId.PackageCaches)
        {
            var roots = policy == CleanupPolicyId.TemporaryFiles
                ? CleanupPolicyService.GetTemporaryRoots()
                : CleanupPolicyService.GetPackageCacheRoots();
            command.Parameters.AddWithValue(
                "$oldModified",
                now.AddHours(-24).ToUnixTimeMilliseconds());
            if (policy == CleanupPolicyId.TemporaryFiles)
            {
                command.Parameters.AddWithValue(
                    "$cacheCategory",
                    "Cache & temporary");
            }

            return BuildRootedCleanupSelect(
                command,
                roots,
                selection,
                policy == CleanupPolicyId.TemporaryFiles
                    ? "AND Category = $cacheCategory"
                    : string.Empty,
                common,
                parentFilter,
                groupByParent,
                selectColumns is not null);
        }
        else if (policy == CleanupPolicyId.OldDownloads)
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            command.Parameters.AddWithValue(
                "$downloadsPrefix",
                NormalizeFolderPath(downloads));
            command.Parameters.AddWithValue(
                "$downloadsUpper",
                GetPrefixUpperBound(
                    EnsureTrailingSeparator(downloads)));
            command.Parameters.AddWithValue("$oldModified", now.AddDays(-90).ToUnixTimeMilliseconds());
            where = $"""
                ScanId = $scanId
                AND ParentPath >= $downloadsPrefix
                AND ParentPath < $downloadsUpper
                AND ModifiedUtc <= $oldModified
                AND {common}
                """;
            order = "ModifiedUtc ASC, SizeBytes DESC";
        }
        else if (policy == CleanupPolicyId.LargeDuplicates)
        {
            if (groupByParent)
            {
                return $"""
                    WITH Ranked AS (
                        SELECT ParentPath, SizeBytes,
                               ROW_NUMBER() OVER (
                                   PARTITION BY ContentHash
                                   ORDER BY CreatedUtc ASC, FullPath COLLATE NOCASE
                               ) AS DuplicateRank
                        FROM FileEntries
                            INDEXED BY IX_FileEntries_DuplicateHash
                        WHERE ScanId = $scanId
                          AND ContentHash IS NOT NULL
                          AND LENGTH(ContentHash) = 64
                          AND IsHardLinkAlias = 0
                          AND (Attributes & $protectedAttributes) = 0
                    )
                    SELECT ParentPath,
                           COUNT(*) AS CandidateCount,
                           COALESCE(SUM(SizeBytes), 0) AS CandidateBytes
                    FROM Ranked
                    WHERE DuplicateRank > 1
                      {parentFilter}
                    GROUP BY ParentPath
                    ORDER BY CandidateBytes DESC, ParentPath
                    """;
            }

            return selectColumns switch
            {
                "COUNT(*)" => """
                    WITH Ranked AS (
                        SELECT FullPath, ParentPath,
                               ROW_NUMBER() OVER (
                                   PARTITION BY ContentHash
                                   ORDER BY CreatedUtc ASC, FullPath COLLATE NOCASE
                               ) AS DuplicateRank
                        FROM FileEntries
                            INDEXED BY IX_FileEntries_DuplicateHash
                        WHERE ScanId = $scanId
                          AND ContentHash IS NOT NULL
                          AND LENGTH(ContentHash) = 64
                          AND IsHardLinkAlias = 0
                          AND (Attributes & $protectedAttributes) = 0
                    )
                    SELECT COUNT(*)
                    FROM Ranked
                    WHERE DuplicateRank > 1
                      $$PARENT_FILTER$$
                    """.Replace(
                        "$$PARENT_FILTER$$",
                        parentFilter,
                        StringComparison.Ordinal),
                "COALESCE(SUM(SizeBytes), 0)" => """
                    WITH Ranked AS (
                        SELECT SizeBytes, ParentPath,
                               ROW_NUMBER() OVER (
                                   PARTITION BY ContentHash
                                   ORDER BY CreatedUtc ASC, FullPath COLLATE NOCASE
                               ) AS DuplicateRank
                        FROM FileEntries
                            INDEXED BY IX_FileEntries_DuplicateHash
                        WHERE ScanId = $scanId
                          AND ContentHash IS NOT NULL
                          AND LENGTH(ContentHash) = 64
                          AND IsHardLinkAlias = 0
                          AND (Attributes & $protectedAttributes) = 0
                    )
                    SELECT COALESCE(SUM(SizeBytes), 0)
                    FROM Ranked
                    WHERE DuplicateRank > 1
                      $$PARENT_FILTER$$
                    """.Replace(
                        "$$PARENT_FILTER$$",
                        parentFilter,
                        StringComparison.Ordinal),
                _ => """
                    WITH Ranked AS (
                        SELECT ScanId, FullPath, Name, ParentPath, SizeBytes, CreatedUtc,
                               ModifiedUtc, AccessedUtc, Attributes, Extension, Category, Tag,
                               VolumeSerialNumber, FileIndexHigh, FileIndexLow, ContentHash,
                               AllocatedSizeBytes, PhysicalSizeBytes, FileId128,
                               ReparseTag, LinkCount, IsHardLinkAlias, IsIncomplete,
                               ROW_NUMBER() OVER (
                                   PARTITION BY ContentHash
                                   ORDER BY CreatedUtc ASC, FullPath COLLATE NOCASE
                               ) AS DuplicateRank
                        FROM FileEntries
                            INDEXED BY IX_FileEntries_DuplicateHash
                        WHERE ScanId = $scanId
                          AND ContentHash IS NOT NULL
                          AND LENGTH(ContentHash) = 64
                          AND IsHardLinkAlias = 0
                          AND (Attributes & $protectedAttributes) = 0
                    )
                    SELECT f.ScanId, f.FullPath, f.Name, f.ParentPath, f.SizeBytes,
                           f.CreatedUtc, f.ModifiedUtc, f.AccessedUtc, f.Attributes,
                           f.Extension, f.Category, f.Tag, f.VolumeSerialNumber,
                           f.FileIndexHigh, f.FileIndexLow, f.ContentHash,
                           f.AllocatedSizeBytes, f.PhysicalSizeBytes, f.FileId128,
                           f.ReparseTag, f.LinkCount, f.IsHardLinkAlias,
                           f.IsIncomplete
                    FROM Ranked f
                    WHERE f.DuplicateRank > 1
                      $$PARENT_FILTER$$
                    ORDER BY f.SizeBytes DESC
                    """.Replace(
                        "$$PARENT_FILTER$$",
                        parentFilter,
                        StringComparison.Ordinal),
            };
        }
        else if (policy == CleanupPolicyId.PreviousWindowsInstallation)
        {
            command.Parameters.AddWithValue(
                "$windowsOldPrefix",
                NormalizeFolderPath(@"C:\Windows.old"));
            command.Parameters.AddWithValue(
                "$windowsOldUpper",
                GetPrefixUpperBound(
                    EnsureTrailingSeparator(@"C:\Windows.old")));
            where = $"""
                ScanId = $scanId
                AND ParentPath >= $windowsOldPrefix
                AND ParentPath < $windowsOldUpper
                AND {common}
                """;
            order = "SizeBytes DESC";
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
        }

        where += $"""

            {parentFilter}
            """;
        var aggregate = selectColumns is not null;
        return $"""
            SELECT {selection}
            FROM FileEntries INDEXED BY IX_FileEntries_ScanParent
            WHERE {where}
            {(groupByParent ? "GROUP BY ParentPath ORDER BY CandidateBytes DESC, ParentPath" : aggregate ? string.Empty : $"ORDER BY {order}")}
            """;
    }

    private static string BuildRootedCleanupSelect(
        SqliteCommand command,
        IEnumerable<string> roots,
        string selection,
        string categoryFilter,
        string commonFilter,
        string parentFilter,
        bool groupByParent,
        bool aggregate)
    {
        const string sourceColumns = """
            ScanId, FullPath, Name, ParentPath, SizeBytes, CreatedUtc, ModifiedUtc,
            AccessedUtc, Attributes, Extension, Category, Tag, VolumeSerialNumber,
            FileIndexHigh, FileIndexLow, ContentHash, AllocatedSizeBytes,
            PhysicalSizeBytes, FileId128, ReparseTag, LinkCount,
            IsHardLinkAlias, IsIncomplete
            """;
        var normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root.Length)
            .Aggregate(
                new List<string>(),
                (accepted, root) =>
                {
                    if (!accepted.Any(parent =>
                            IsPathWithinFolder(root, parent)))
                    {
                        accepted.Add(root);
                    }

                    return accepted;
                });
        var branches = normalizedRoots
            .Select((root, index) =>
            {
                var parameter = "$root" + index;
                command.Parameters.AddWithValue(parameter, root);
                command.Parameters.AddWithValue(
                    parameter + "Upper",
                    GetPrefixUpperBound(EnsureTrailingSeparator(root)));
                return $"""
                    SELECT {sourceColumns}
                    FROM FileEntries
                        INDEXED BY IX_FileEntries_ScanParent
                    WHERE ScanId = $scanId
                      AND ModifiedUtc <= $oldModified
                      AND ParentPath >= {parameter}
                      AND ParentPath < {parameter}Upper
                      {categoryFilter}
                      AND {commonFilter}
                      {parentFilter}
                    """;
            })
            .ToArray();
        if (branches.Length == 0)
        {
            throw new InvalidOperationException(
                "The cleanup policy has no configured storage roots.");
        }

        var suffix = groupByParent
            ? "GROUP BY ParentPath ORDER BY CandidateBytes DESC, ParentPath"
            : aggregate
                ? string.Empty
                : "ORDER BY SizeBytes DESC";
        return $"""
            WITH CandidateFiles AS (
                {string.Join("\nUNION ALL\n", branches)}
            )
            SELECT {selection}
            FROM CandidateFiles
            {suffix}
            """;
    }

    private static bool IsPathWithinFolder(string path, string folder) =>
        string.Equals(
            path,
            folder,
            StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(
            EnsureTrailingSeparator(folder),
            StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string GetPrefixUpperBound(string prefix)
    {
        var separator = prefix.EndsWith(Path.DirectorySeparatorChar)
            ? prefix[^1]
            : Path.DirectorySeparatorChar;
        return (separator == '\\'
            ? prefix[..^1] + "]"
            : prefix + char.MaxValue);
    }

    private static void AddVisibilityFilters(
        List<string> filters,
        List<SqliteParameter> parameters,
        bool includeSystem,
        bool includeHidden)
    {
        if (!includeSystem)
        {
            filters.Add("Category <> 'System'");
            filters.Add($"(Attributes & {(long)FileAttributes.System}) = 0");
        }

        if (!includeHidden)
        {
            filters.Add($"(Attributes & {(long)FileAttributes.Hidden}) = 0");
        }
    }
}

public sealed class StorageScanWriteSession : IAsyncDisposable
{
    private const int FinalizationBatchSize = 10_000;
    private const int MaximumFolderCacheEntries = 50_000;
    private readonly SqliteConnection _connection;
    private readonly ScanOwner _owner;
    private readonly long _runKey;
    private readonly Dictionary<string, long> _folderIds =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _closed;

    internal StorageScanWriteSession(
        SqliteConnection connection,
        Guid scanId,
        ScanOwner owner,
        long runKey,
        bool resumed = false)
    {
        _connection = connection;
        ScanId = scanId;
        _owner = owner;
        _runKey = runKey;
        IsResumed = resumed;
    }

    public Guid ScanId { get; }

    public bool IsResumed { get; }

    internal long RunKey => _runKey;

    public async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanRuns
            SET LeaseHeartbeatUtc = $heartbeat
            WHERE Id = $id
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue(
            "$heartbeat",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue(
            "$started",
            _owner.ProcessStartedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The scan lease is no longer owned by this process.");
        }
    }

    public async Task SetPhaseAsync(
        string phase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            throw new ArgumentException(
                "The scan phase cannot be empty.",
                nameof(phase));
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanRuns
            SET LeaseHeartbeatUtc = $heartbeat,
                ScanPhase = $phase,
                PhaseStartedUtc = CASE
                    WHEN ScanPhase = $phase THEN PhaseStartedUtc
                    ELSE $heartbeat
                    END
            WHERE Id = $id
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue(
            "$heartbeat",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$phase", phase);
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue(
            "$running",
            (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue(
            "$started",
            _owner.ProcessStartedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The scan lease was lost while changing the scan phase.");
        }
    }

    public async Task SetJournalCheckpointAsync(
        long journalId,
        long nextUsn,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanRuns
            SET JournalId = $journal,
                LastUsn = $usn
            WHERE Id = $id
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue("$journal", journalId);
        command.Parameters.AddWithValue("$usn", nextUsn);
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue(
            "$started",
            _owner.ProcessStartedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The scan lease was lost while recording the journal checkpoint.");
        }
    }

    public async Task SaveProgressAsync(
        long filesScanned,
        long foldersScanned,
        long bytesScanned,
        long allocatedBytesScanned,
        long inaccessibleItems,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanRuns
            SET FilesScanned = $files,
                FoldersScanned = $folders,
                BytesScanned = $bytes,
                AllocatedBytesScanned = $allocated,
                InaccessibleItems = $inaccessible,
                LeaseHeartbeatUtc = $heartbeat,
                ScanPhase = 'Enumerating',
                PhaseStartedUtc = CASE
                    WHEN ScanPhase = 'Enumerating'
                    THEN COALESCE(PhaseStartedUtc, $heartbeat)
                    ELSE $heartbeat
                    END
            WHERE Id = $id
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue("$files", filesScanned);
        command.Parameters.AddWithValue("$folders", foldersScanned);
        command.Parameters.AddWithValue("$bytes", bytesScanned);
        command.Parameters.AddWithValue("$allocated", allocatedBytesScanned);
        command.Parameters.AddWithValue("$inaccessible", inaccessibleItems);
        command.Parameters.AddWithValue(
            "$heartbeat",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue("$started", _owner.ProcessStartedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The scan lease was lost while saving progress.");
        }
    }

    internal Task<long> EnqueueDirectoryAsync(
        string path,
        int depth,
        ScanFolderMetadata metadata,
        CancellationToken cancellationToken,
        bool queueWork = true) =>
        EnqueueDirectoriesAsync(
            [
                new ScanDirectoryEntry(
                    path,
                    depth,
                    metadata,
                    queueWork),
            ],
            cancellationToken);

    internal async Task<long> EnqueueDirectoriesAsync(
        IReadOnlyList<ScanDirectoryEntry> directories,
        CancellationToken cancellationToken)
    {
        if (directories.Count == 0)
        {
            return 0;
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await EnsureLeaseInTransactionAsync(transaction, cancellationToken);
        await using var folderUpdate = _connection.CreateCommand();
        folderUpdate.Transaction = transaction;
        folderUpdate.CommandText = """
            UPDATE FolderNodes
            SET Attributes = $attributes,
                ReparseTag = $reparse,
                ModifiedUtc = $modified,
                FileId128 = $fileId,
                FileId64 = CASE
                    WHEN LENGTH($fileId) >= 16
                    THEN SUBSTR($fileId, 1, 16)
                    ELSE '' END,
                IsIncomplete = $incomplete
            WHERE RunKey = $run AND Id = $id
            """;
        folderUpdate.Parameters.AddWithValue("$attributes", 0L);
        folderUpdate.Parameters.AddWithValue("$reparse", 0L);
        folderUpdate.Parameters.AddWithValue("$modified", 0L);
        folderUpdate.Parameters.AddWithValue("$fileId", string.Empty);
        folderUpdate.Parameters.AddWithValue("$incomplete", 0);
        folderUpdate.Parameters.AddWithValue("$run", _runKey);
        folderUpdate.Parameters.AddWithValue("$id", 0L);
        folderUpdate.Prepare();

        await using var work = _connection.CreateCommand();
        work.Transaction = transaction;
        work.CommandText = """
            INSERT OR IGNORE INTO ScanWorkItems (
                RunKey, PathHash, FullPath, Depth, State, AttemptCount,
                UpdatedUtc
            )
            VALUES ($run, $hash, $path, $depth, 0, 0, $updated)
            """;
        work.Parameters.AddWithValue("$run", _runKey);
        work.Parameters.AddWithValue("$hash", string.Empty);
        work.Parameters.AddWithValue("$path", string.Empty);
        work.Parameters.AddWithValue("$depth", 0);
        work.Parameters.AddWithValue("$updated", 0L);
        work.Prepare();

        var updatedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long createdFolders = 0;
        for (var index = 0; index < directories.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var directory = directories[index];
            var folder = await EnsureFolderIdAsync(
                directory.Path,
                transaction,
                cancellationToken);
            createdFolders += folder.CreatedCount;
            folderUpdate.Parameters["$attributes"].Value =
                directory.Metadata.Attributes;
            folderUpdate.Parameters["$reparse"].Value =
                unchecked((long)directory.Metadata.ReparseTag);
            folderUpdate.Parameters["$modified"].Value =
                directory.Metadata.ModifiedUtc.ToUnixTimeMilliseconds();
            folderUpdate.Parameters["$fileId"].Value =
                directory.Metadata.FileId128;
            folderUpdate.Parameters["$incomplete"].Value =
                directory.Metadata.IsIncomplete ? 1 : 0;
            folderUpdate.Parameters["$id"].Value = folder.Id;
            if (folderUpdate.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"The directory '{directory.Path}' could not be persisted.");
            }

            if (directory.QueueWork)
            {
                work.Parameters["$hash"].Value =
                    GetPathHash(directory.Path);
                work.Parameters["$path"].Value = directory.Path;
                work.Parameters["$depth"].Value = directory.Depth;
                work.Parameters["$updated"].Value = updatedUtc;
                work.ExecuteNonQuery();
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return createdFolders;
    }

    public async Task<IReadOnlyList<ScanWorkItem>> ClaimDirectoriesAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        var items = new List<ScanWorkItem>();
        await using (var select = _connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT PathHash, FullPath, Depth
                FROM ScanWorkItems
                WHERE RunKey = $run AND State = 0
                ORDER BY UpdatedUtc
                LIMIT $limit
                """;
            select.Parameters.AddWithValue("$run", _runKey);
            select.Parameters.AddWithValue("$limit", batchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ScanWorkItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2)));
            }
        }

        await using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ScanWorkItems
                SET State = 1,
                    AttemptCount = AttemptCount + 1,
                    UpdatedUtc = $updated
                WHERE RunKey = $run
                  AND PathHash = $hash
                  AND State = 0
                """;
            update.Parameters.AddWithValue(
                "$updated",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$run", _runKey);
            update.Parameters.AddWithValue("$hash", string.Empty);
            update.Prepare();
            foreach (var item in items)
            {
                update.Parameters["$hash"].Value = item.PathHash;
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"The scan work item '{item.FullPath}' was claimed concurrently.");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    public Task CompleteDirectoryAsync(
        ScanWorkItem item,
        CancellationToken cancellationToken) =>
        FinalizeDirectoriesAsync(
            [item],
            [],
            [],
            cancellationToken);

    internal async Task FinalizeDirectoriesAsync(
        IReadOnlyList<ScanWorkItem> directories,
        IReadOnlyList<string> incompletePaths,
        IReadOnlyList<ScanIssue> issues,
        CancellationToken cancellationToken)
    {
        if (directories.Count == 0 &&
            incompletePaths.Count == 0 &&
            issues.Count == 0)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await EnsureLeaseInTransactionAsync(transaction, cancellationToken);

        if (incompletePaths.Count > 0)
        {
            await using var incomplete = _connection.CreateCommand();
            incomplete.Transaction = transaction;
            incomplete.CommandText = """
                UPDATE FolderNodes
                SET IsIncomplete = 1
                WHERE RunKey = $run AND PathHash = $hash
                """;
            incomplete.Parameters.AddWithValue("$run", _runKey);
            incomplete.Parameters.AddWithValue("$hash", string.Empty);
            incomplete.Prepare();
            foreach (var path in incompletePaths)
            {
                incomplete.Parameters["$hash"].Value = GetPathHash(path);
                if (incomplete.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"The incomplete directory '{path}' could not be recorded.");
                }
            }
        }

        if (issues.Count > 0)
        {
            await using var issueCommand = _connection.CreateCommand();
            issueCommand.Transaction = transaction;
            issueCommand.CommandText = """
                INSERT INTO ScanIssues (
                    ScanId, Path, IssueType, ReparseTag, ErrorCode,
                    Message, DetectedUtc
                )
                VALUES (
                    $scanId, $path, $type, $tag, $error, $message,
                    $detected
                )
                """;
            issueCommand.Parameters.AddWithValue(
                "$scanId",
                ScanId.ToString("N"));
            issueCommand.Parameters.AddWithValue("$path", string.Empty);
            issueCommand.Parameters.AddWithValue("$type", 0);
            issueCommand.Parameters.AddWithValue("$tag", 0L);
            issueCommand.Parameters.AddWithValue("$error", DBNull.Value);
            issueCommand.Parameters.AddWithValue("$message", string.Empty);
            issueCommand.Parameters.AddWithValue("$detected", 0L);
            issueCommand.Prepare();
            foreach (var issue in issues)
            {
                issueCommand.Parameters["$path"].Value = issue.Path;
                issueCommand.Parameters["$type"].Value = (int)issue.Type;
                issueCommand.Parameters["$tag"].Value =
                    unchecked((long)issue.ReparseTag);
                issueCommand.Parameters["$error"].Value =
                    issue.ErrorCode.HasValue
                        ? issue.ErrorCode.Value
                        : DBNull.Value;
                issueCommand.Parameters["$message"].Value = issue.Message;
                issueCommand.Parameters["$detected"].Value =
                    issue.DetectedUtc.ToUnixTimeMilliseconds();
                if (issueCommand.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"The scan issue for '{issue.Path}' could not be recorded.");
                }
            }
        }

        if (directories.Count > 0)
        {
            await using var complete = _connection.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE ScanWorkItems
                SET State = 2,
                    LastError = NULL,
                    UpdatedUtc = $updated
                WHERE RunKey = $run
                  AND PathHash = $hash
                  AND State = 1
                """;
            complete.Parameters.AddWithValue("$run", _runKey);
            complete.Parameters.AddWithValue("$hash", string.Empty);
            complete.Parameters.AddWithValue(
                "$updated",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            complete.Prepare();
            foreach (var directory in directories)
            {
                complete.Parameters["$hash"].Value = directory.PathHash;
                if (complete.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"The scan work item '{directory.FullPath}' was no longer owned.");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task MarkDirectoryIncompleteAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE FolderNodes
            SET IsIncomplete = 1
            WHERE RunKey = $run
              AND PathHash = $hash
              AND EXISTS (
                  SELECT 1
                  FROM ScanRuns
                  WHERE Id = $id
                    AND Status = $running
                    AND OwnerProcessId = $pid
                    AND OwnerProcessStartedUtc = $started
              )
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$hash", GetPathHash(path));
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue(
            "$running",
            (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue(
            "$started",
            _owner.ProcessStartedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"The incomplete directory '{path}' could not be recorded.");
        }
    }

    public async Task FailDirectoryAsync(
        ScanWorkItem item,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanWorkItems
            SET State = 2,
                LastError = $error,
                UpdatedUtc = $updated
            WHERE RunKey = $run
              AND PathHash = $hash
              AND State = 1
              AND EXISTS (
                  SELECT 1 FROM ScanRuns
                  WHERE Id = $scan
                    AND Status = $running
                    AND OwnerProcessId = $pid
                    AND OwnerProcessStartedUtc = $started
              )
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$hash", item.PathHash);
        command.Parameters.AddWithValue("$error", exception.Message);
        command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$scan", ScanId.ToString("N"));
        command.Parameters.AddWithValue(
            "$running",
            (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue(
            "$started",
            _owner.ProcessStartedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearCompletedWorkItemsAsync(
        CancellationToken cancellationToken,
        IProgress<StorageScanFinalizationProgress>? progress = null)
    {
        long totalItems;
        await using (var count = _connection.CreateCommand())
        {
            count.CommandText = """
                SELECT COUNT(*)
                FROM ScanWorkItems
                WHERE RunKey = $run AND State = 2
                """;
            count.Parameters.AddWithValue("$run", _runKey);
            totalItems = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken));
        }

        long processedItems = 0;
        ReportFinalizationProgress(
            progress,
            "Clearing completed scan queue",
            0,
            totalItems);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM ScanWorkItems
                WHERE (RunKey, PathHash) IN (
                    SELECT RunKey, PathHash
                    FROM ScanWorkItems
                    WHERE RunKey = $run AND State = 2
                    LIMIT 10000
                )
                    AND EXISTS (
                        SELECT 1 FROM ScanRuns
                        WHERE Id = $scan
                          AND Status = $running
                          AND OwnerProcessId = $pid
                          AND OwnerProcessStartedUtc = $started
                    )
                """;
                command.Parameters.AddWithValue("$run", _runKey);
                command.Parameters.AddWithValue("$scan", ScanId.ToString("N"));
                command.Parameters.AddWithValue(
                    "$running",
                    (int)StorageScanStatus.Running);
                command.Parameters.AddWithValue("$pid", _owner.ProcessId);
                command.Parameters.AddWithValue(
                    "$started",
                    _owner.ProcessStartedUtc);
            var deleted =
                await command.ExecuteNonQueryAsync(cancellationToken);
            if (deleted == 0)
            {
                return;
            }

            await HeartbeatAsync(cancellationToken);
            processedItems += deleted;
            ReportFinalizationProgress(
                progress,
                "Clearing completed scan queue",
                Math.Min(processedItems, totalItems),
                totalItems);
        }
    }

    public async Task<ScanTotals> InsertFilesAsync(
        IReadOnlyList<StorageFileRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new ScanTotals(0, 0, 0, 0);
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await using var physical = CreatePhysicalFileInsert(
            transaction,
            cancellationToken);
        await using var physicalUpdate = CreatePhysicalFileUpdate(
            transaction,
            cancellationToken);
        await using var fileName = CreateFileNameInsert(
            transaction,
            cancellationToken);
        await using var fileNameUpdate = CreateFileNameUpdate(
            transaction,
            cancellationToken);
        await using var candidate = CreateCandidateInsert(
            transaction,
            cancellationToken);
        long insertedFiles = 0;
        long insertedFolders = 0;
        long insertedLogicalBytes = 0;
        long insertedAllocatedBytes = 0;
        for (var index = 0; index < records.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var record = records[index];
            var parent = await EnsureFolderIdAsync(
                record.ParentPath,
                transaction,
                cancellationToken);
            insertedFolders += parent.CreatedCount;
            var identityKey = string.IsNullOrWhiteSpace(record.FileId128)
                ? GetPathHash(record.FullPath)
                : $"{record.VolumeSerialNumber:X16}:{record.FileId128}";
            SetPhysicalFileParameters(physical, identityKey, record);
            var physicalResult = physical.ExecuteScalar();
            var physicalInserted =
                physicalResult is not null &&
                physicalResult != DBNull.Value;
            long physicalId;
            if (physicalInserted)
            {
                physicalId = Convert.ToInt64(physicalResult);
                insertedLogicalBytes += record.SizeBytes;
                insertedAllocatedBytes +=
                    record.AllocatedSizeBytes == 0
                        ? record.SizeBytes
                        : record.AllocatedSizeBytes;
            }
            else
            {
                SetPhysicalFileParameters(
                    physicalUpdate,
                    identityKey,
                    record);
                var updatedPhysicalId = physicalUpdate.ExecuteScalar();
                if (updatedPhysicalId is null ||
                    updatedPhysicalId == DBNull.Value)
                {
                    throw new InvalidOperationException(
                        $"The file identity '{record.FullPath}' could not be persisted.");
                }

                physicalId = Convert.ToInt64(updatedPhysicalId);
            }

            fileName.Parameters["$parent"].Value = parent.Id;
            fileName.Parameters["$physical"].Value = physicalId;
            fileName.Parameters["$name"].Value = record.Name;
            fileName.Parameters["$extension"].Value = record.Extension;
            fileName.Parameters["$category"].Value =
                StorageCategoryClassifier.CategoryId(record.Category);
            fileName.Parameters["$tag"].Value =
                StorageCategoryClassifier.TagId(record.Tag);
            fileName.Parameters["$hardAlias"].Value =
                record.IsHardLinkAlias ? 1 : 0;
            var fileNameResult = fileName.ExecuteScalar();
            long fileNameId;
            if (fileNameResult is not null &&
                fileNameResult != DBNull.Value)
            {
                fileNameId = Convert.ToInt64(fileNameResult);
                insertedFiles++;
            }
            else
            {
                fileNameUpdate.Parameters["$parent"].Value = parent.Id;
                fileNameUpdate.Parameters["$physical"].Value = physicalId;
                fileNameUpdate.Parameters["$name"].Value = record.Name;
                fileNameUpdate.Parameters["$extension"].Value =
                    record.Extension;
                fileNameUpdate.Parameters["$category"].Value =
                    StorageCategoryClassifier.CategoryId(record.Category);
                fileNameUpdate.Parameters["$tag"].Value =
                    StorageCategoryClassifier.TagId(record.Tag);
                fileNameUpdate.Parameters["$hardAlias"].Value =
                    record.IsHardLinkAlias ? 1 : 0;
                var updatedFileNameId = fileNameUpdate.ExecuteScalar();
                if (updatedFileNameId is null ||
                    updatedFileNameId == DBNull.Value)
                {
                    throw new InvalidOperationException(
                        $"The file name '{record.FullPath}' could not be persisted.");
                }

                fileNameId = Convert.ToInt64(updatedFileNameId);
            }

            foreach (var policy in GetMaterializedPolicies(record))
            {
                candidate.Parameters["$policy"].Value = (int)policy;
                candidate.Parameters["$fileName"].Value = fileNameId;
                candidate.ExecuteNonQuery();
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new ScanTotals(
            insertedFiles,
            insertedFolders,
            insertedLogicalBytes,
            insertedAllocatedBytes);
    }

    public async Task InsertFoldersAsync(
        IReadOnlyList<StorageFolderRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE FolderNodes
            SET LogicalSize = $size,
                AllocatedSize = $allocatedSize,
                DirectLogicalSize = $directSize,
                DirectAllocatedSize = $directAllocated,
                FileCount = $fileCount,
                DirectFileCount = $directFileCount,
                Attributes = $attributes,
                ReparseTag = $reparseTag,
                ModifiedUtc = $modified,
                Category = $category,
                Tag = $tag,
                FileId128 = $fileId,
                FileId64 = CASE
                    WHEN LENGTH($fileId) >= 16 THEN SUBSTR($fileId, 1, 16)
                    ELSE '' END,
                IsIncomplete = $incomplete
            WHERE Id = $id AND RunKey = $runKey
            """;
        command.Parameters.AddWithValue("$id", 0L);
        command.Parameters.AddWithValue("$runKey", _runKey);
        command.Parameters.AddWithValue("$size", 0L);
        command.Parameters.AddWithValue("$allocatedSize", 0L);
        command.Parameters.AddWithValue("$directSize", 0L);
        command.Parameters.AddWithValue("$directAllocated", 0L);
        command.Parameters.AddWithValue("$fileCount", 0L);
        command.Parameters.AddWithValue("$directFileCount", 0L);
        command.Parameters.AddWithValue("$attributes", 0L);
        command.Parameters.AddWithValue("$modified", 0L);
        command.Parameters.AddWithValue("$category", 0);
        command.Parameters.AddWithValue("$tag", 0);
        command.Parameters.AddWithValue("$fileId", string.Empty);
        command.Parameters.AddWithValue("$reparseTag", 0L);
        command.Parameters.AddWithValue("$incomplete", 0);
        await command.PrepareAsync(cancellationToken);
        for (var index = 0; index < records.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var record = records[index];
            var folder = await EnsureFolderIdAsync(
                record.FullPath,
                transaction,
                cancellationToken);
            command.Parameters["$id"].Value = folder.Id;
            command.Parameters["$size"].Value = record.SizeBytes;
            command.Parameters["$directSize"].Value = record.DirectSizeBytes;
            command.Parameters["$directAllocated"].Value =
                GetDirectAllocatedSize(record);
            command.Parameters["$fileCount"].Value = record.FileCount;
            command.Parameters["$directFileCount"].Value = record.DirectFileCount;
            command.Parameters["$attributes"].Value = record.Attributes;
            command.Parameters["$modified"].Value = record.ModifiedUtc.ToUnixTimeMilliseconds();
            command.Parameters["$category"].Value =
                StorageCategoryClassifier.CategoryId(record.Category);
            command.Parameters["$tag"].Value =
                StorageCategoryClassifier.TagId(record.Tag);
            command.Parameters["$fileId"].Value = record.FileId128;
            command.Parameters["$allocatedSize"].Value =
                record.AllocatedSizeBytes == 0
                    ? record.SizeBytes
                    : record.AllocatedSizeBytes;
            command.Parameters["$reparseTag"].Value =
                unchecked((long)record.ReparseTag);
            command.Parameters["$incomplete"].Value =
                record.IsIncomplete ? 1 : 0;
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Folder index row '{record.FullPath}' could not be updated.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecomputeFolderTotalsAsync(
        CancellationToken cancellationToken,
        IProgress<StorageScanFinalizationProgress>? progress = null)
    {
        await SetPhaseAsync("Finalizing", cancellationToken);
        var counts = await GetFinalizationCountsAsync(cancellationToken);
        HashSet<long>? referencedPhysicalFiles = [];
        var categorySizes = new long[7];
        var categoryCounts = new long[7];
        long lastFileNameId = 0;
        long processedFileNames = 0;
        ReportFinalizationProgress(
            progress,
            "Resolving hard links",
            0,
            counts.FileNames);
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var rows = await ReadAliasBatchAsync(
                lastFileNameId,
                cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            var aliasUpdates = new List<FinalizationAliasUpdate>();
            foreach (var row in rows)
            {
                var category = row.Category >= 0 &&
                               row.Category < categorySizes.Length
                    ? row.Category
                    : categorySizes.Length - 1;
                var isAlias = !referencedPhysicalFiles.Add(
                    row.PhysicalFileId);
                categoryCounts[category]++;
                if (!isAlias)
                {
                    categorySizes[category] += row.AllocatedSize;
                }

                if (row.IsHardLinkAlias != isAlias)
                {
                    aliasUpdates.Add(new FinalizationAliasUpdate(
                        row.Id,
                        isAlias));
                }
            }
            await ApplyAliasBatchAsync(
                aliasUpdates,
                cancellationToken);
            lastFileNameId = rows[^1].Id;
            processedFileNames += rows.Count;
            ReportFinalizationProgress(
                progress,
                "Resolving hard links",
                processedFileNames,
                counts.FileNames);
            await HeartbeatAsync(cancellationToken);
        }

        await RemoveHardLinkAliasCandidatesAsync(
            progress,
            cancellationToken);
        await RemoveOrphanedPhysicalFilesAsync(
            referencedPhysicalFiles,
            counts.PhysicalFiles,
            progress,
            cancellationToken);
        referencedPhysicalFiles = null;

        await ResetFolderTotalsAsync(
            counts.Folders,
            progress,
            cancellationToken);
        await RecomputeDirectFolderTotalsAsync(
            counts.FileNames,
            progress,
            cancellationToken);
        await RollupFolderTotalsAsync(
            counts.Folders,
            progress,
            cancellationToken);
        await ReplaceCategorySummariesAsync(
            categorySizes,
            categoryCounts,
            cancellationToken);
        ReportFinalizationProgress(
            progress,
            "Folder totals complete",
            counts.Folders,
            counts.Folders);
    }

    private async Task<FinalizationCounts> GetFinalizationCountsAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM FileNames WHERE RunKey = $run),
                (SELECT COUNT(*) FROM PhysicalFiles WHERE RunKey = $run),
                (SELECT COUNT(*) FROM FolderNodes WHERE RunKey = $run)
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new FinalizationCounts(0, 0, 0);
        }

        return new FinalizationCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private async Task<List<FinalizationAliasRow>> ReadAliasBatchAsync(
        long afterId,
        CancellationToken cancellationToken)
    {
        var rows = new List<FinalizationAliasRow>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT n.Id, n.PhysicalFileId, n.IsHardLinkAlias,
                   p.AllocatedSize, n.Category
            FROM FileNames AS n NOT INDEXED
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE n.RunKey = $run AND n.Id > $after
            ORDER BY n.Id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$limit", FinalizationBatchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FinalizationAliasRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2) != 0,
                reader.GetInt64(3),
                reader.GetInt32(4)));
        }

        return rows;
    }

    private async Task ApplyAliasBatchAsync(
        IReadOnlyList<FinalizationAliasUpdate> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await SetAliasFlagsAsync(
            transaction,
            updates.Where(update => update.IsAlias).Select(update => update.Id).ToList(),
            true,
            cancellationToken);
        await SetAliasFlagsAsync(
            transaction,
            updates.Where(update => !update.IsAlias).Select(update => update.Id).ToList(),
            false,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetAliasFlagsAsync(
        SqliteTransaction transaction,
        IReadOnlyList<long> ids,
        bool isAlias,
        CancellationToken cancellationToken)
    {
        const int parameterBatchSize = 500;
        for (var offset = 0; offset < ids.Count; offset += parameterBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(parameterBatchSize, ids.Count - offset);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE FileNames
                SET IsHardLinkAlias = {(isAlias ? 1 : 0)}
                WHERE RunKey = $run
                  AND Id IN ({
                      string.Join(
                          ", ",
                          Enumerable.Range(0, count).Select(index => $"$id{index}"))
                  })
                """;
            command.Parameters.AddWithValue("$run", _runKey);
            for (var index = 0; index < count; index++)
            {
                command.Parameters.AddWithValue(
                    $"$id{index}",
                    ids[offset + index]);
            }

            var updated =
                await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated != count)
            {
                throw new InvalidOperationException(
                    "One or more file names could not be updated during hard-link finalization.");
            }
        }
    }

    private async Task RemoveHardLinkAliasCandidatesAsync(
        IProgress<StorageScanFinalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long totalAliases;
        await using (var count = _connection.CreateCommand())
        {
            count.CommandText = """
                SELECT COUNT(*)
                FROM NormalizedCleanupCandidates c
                INNER JOIN FileNames n
                    ON n.Id = c.FileNameId AND n.RunKey = c.RunKey
                WHERE c.RunKey = $run AND n.IsHardLinkAlias = 1
                """;
            count.Parameters.AddWithValue("$run", _runKey);
            totalAliases = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken));
        }

        long processedAliases = 0;
        ReportFinalizationProgress(
            progress,
            "Removing hard-link cleanup duplicates",
            0,
            totalAliases);
        while (totalAliases > processedAliases)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM NormalizedCleanupCandidates
                WHERE (RunKey, PolicyId, FileNameId) IN (
                    SELECT c.RunKey, c.PolicyId, c.FileNameId
                    FROM NormalizedCleanupCandidates c
                    INNER JOIN FileNames n
                        ON n.Id = c.FileNameId AND n.RunKey = c.RunKey
                    WHERE c.RunKey = $run AND n.IsHardLinkAlias = 1
                    LIMIT $limit
                )
                """;
            command.Parameters.AddWithValue("$run", _runKey);
            command.Parameters.AddWithValue("$limit", FinalizationBatchSize);
            var deleted =
                await command.ExecuteNonQueryAsync(cancellationToken);
            if (deleted == 0)
            {
                break;
            }

            processedAliases += deleted;
            ReportFinalizationProgress(
                progress,
                "Removing hard-link cleanup duplicates",
                processedAliases,
                totalAliases);
            await HeartbeatAsync(cancellationToken);
        }
    }

    private async Task RemoveOrphanedPhysicalFilesAsync(
        HashSet<long> referencedPhysicalFiles,
        long totalPhysicalFiles,
        IProgress<StorageScanFinalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long lastPhysicalFileId = 0;
        long processedPhysicalFiles = 0;
        ReportFinalizationProgress(
            progress,
            "Removing orphaned index records",
            0,
            totalPhysicalFiles);
        while (true)
        {
            var physicalIds = await ReadPhysicalIdBatchAsync(
                lastPhysicalFileId,
                cancellationToken);
            if (physicalIds.Count == 0)
            {
                break;
            }

            var orphaned = physicalIds
                .Where(id => !referencedPhysicalFiles.Contains(id))
                .ToList();
            await DeletePhysicalFilesAsync(orphaned, cancellationToken);
            lastPhysicalFileId = physicalIds[^1];
            processedPhysicalFiles += physicalIds.Count;
            ReportFinalizationProgress(
                progress,
                "Removing orphaned index records",
                processedPhysicalFiles,
                totalPhysicalFiles);
            await HeartbeatAsync(cancellationToken);
        }
    }

    private async Task<List<long>> ReadPhysicalIdBatchAsync(
        long afterId,
        CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Id
            FROM PhysicalFiles NOT INDEXED
            WHERE RunKey = $run AND Id > $after
            ORDER BY Id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$limit", FinalizationBatchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task DeletePhysicalFilesAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM PhysicalFiles
            WHERE Id = $id AND RunKey = $run
            """;
        command.Parameters.AddWithValue("$id", 0L);
        command.Parameters.AddWithValue("$run", _runKey);
        command.Prepare();
        for (var index = 0; index < ids.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            command.Parameters["$id"].Value = ids[index];
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ResetFolderTotalsAsync(
        long totalFolders,
        IProgress<StorageScanFinalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long lastFolderId = 0;
        long processedFolders = 0;
        ReportFinalizationProgress(
            progress,
            "Resetting folder totals",
            0,
            totalFolders);
        while (true)
        {
            var rows = await ReadFolderIdBatchAsync(
                lastFolderId,
                cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            var batchStart = lastFolderId;
            lastFolderId = rows[^1];
            await using var transaction =
                (SqliteTransaction)await _connection.BeginTransactionAsync(
                    cancellationToken);
            await HeartbeatInTransactionAsync(transaction, cancellationToken);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE FolderNodes
                SET LogicalSize = 0,
                    AllocatedSize = 0,
                    DirectLogicalSize = 0,
                    DirectAllocatedSize = 0,
                    FileCount = 0,
                    DirectFileCount = 0
                WHERE RunKey = $run
                  AND Id > $after
                  AND Id <= $through
                """;
            command.Parameters.AddWithValue("$run", _runKey);
            command.Parameters.AddWithValue("$after", batchStart);
            command.Parameters.AddWithValue("$through", lastFolderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            processedFolders += rows.Count;
            ReportFinalizationProgress(
                progress,
                "Resetting folder totals",
                processedFolders,
                totalFolders);
        }
    }

    private async Task<List<long>> ReadFolderIdBatchAsync(
        long afterId,
        CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Id
            FROM FolderNodes NOT INDEXED
            WHERE RunKey = $run AND Id > $after
            ORDER BY Id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$limit", FinalizationBatchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task RecomputeDirectFolderTotalsAsync(
        long totalFileNames,
        IProgress<StorageScanFinalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long lastParentId = 0;
        long lastFileNameId = 0;
        long processedFileNames = 0;
        long? currentParentId = null;
        long directLogical = 0;
        long directAllocated = 0;
        long directFiles = 0;
        var pending = new List<FinalizationFolderTotal>();
        ReportFinalizationProgress(
            progress,
            "Calculating direct folder totals",
            0,
            totalFileNames);

        while (true)
        {
            var rows = await ReadDirectFileBatchAsync(
                lastParentId,
                lastFileNameId,
                cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                if (currentParentId.HasValue &&
                    currentParentId.Value != row.ParentId)
                {
                    pending.Add(new FinalizationFolderTotal(
                        currentParentId.Value,
                        directLogical,
                        directAllocated,
                        directFiles));
                    directLogical = 0;
                    directAllocated = 0;
                    directFiles = 0;
                }

                currentParentId = row.ParentId;
                directLogical += row.LogicalSize;
                if (!row.IsAlias)
                {
                    directAllocated += row.AllocatedSize;
                }

                directFiles++;
            }

            lastParentId = rows[^1].ParentId;
            lastFileNameId = rows[^1].Id;
            processedFileNames += rows.Count;
            await WriteDirectFolderTotalsAsync(pending, cancellationToken);
            pending.Clear();
            ReportFinalizationProgress(
                progress,
                "Calculating direct folder totals",
                processedFileNames,
                totalFileNames);
            await HeartbeatAsync(cancellationToken);
        }

        if (currentParentId.HasValue)
        {
            pending.Add(new FinalizationFolderTotal(
                currentParentId.Value,
                directLogical,
                directAllocated,
                directFiles));
        }

        await WriteDirectFolderTotalsAsync(pending, cancellationToken);
    }

    private async Task<List<FinalizationDirectRow>> ReadDirectFileBatchAsync(
        long afterParentId,
        long afterFileNameId,
        CancellationToken cancellationToken)
    {
        var rows = new List<FinalizationDirectRow>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT n.Id, n.ParentId, p.LogicalSize, p.AllocatedSize,
                   n.IsHardLinkAlias
            FROM FileNames AS n INDEXED BY IX_FileNames_ParentSize
            INNER JOIN PhysicalFiles p ON p.Id = n.PhysicalFileId
            WHERE n.RunKey = $run
              AND (
                  n.ParentId > $parent
                  OR (n.ParentId = $parent AND n.Id > $file)
              )
            ORDER BY n.ParentId, n.Id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$parent", afterParentId);
        command.Parameters.AddWithValue("$file", afterFileNameId);
        command.Parameters.AddWithValue("$limit", FinalizationBatchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FinalizationDirectRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt32(4) != 0));
        }

        return rows;
    }

    private async Task WriteDirectFolderTotalsAsync(
        IReadOnlyList<FinalizationFolderTotal> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE FolderNodes
            SET DirectLogicalSize = $logical,
                DirectAllocatedSize = $allocated,
                DirectFileCount = $files,
                LogicalSize = $logical,
                AllocatedSize = $allocated,
                FileCount = $files
            WHERE Id = $id AND RunKey = $run
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$id", 0L);
        command.Parameters.AddWithValue("$logical", 0L);
        command.Parameters.AddWithValue("$allocated", 0L);
        command.Parameters.AddWithValue("$files", 0L);
        command.Prepare();
        for (var index = 0; index < rows.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var row = rows[index];
            command.Parameters["$id"].Value = row.Id;
            command.Parameters["$logical"].Value = row.LogicalSize;
            command.Parameters["$allocated"].Value = row.AllocatedSize;
            command.Parameters["$files"].Value = row.FileCount;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "A folder total could not be finalized.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RollupFolderTotalsAsync(
        long totalFolders,
        IProgress<StorageScanFinalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var folders =
            new Dictionary<long, FinalizationFolderRollupState>();
        long lastFolderId = 0;
        while (true)
        {
            var rows = await ReadFolderRollupBatchAsync(
                lastFolderId,
                cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                folders.Add(row.Id, row);
            }

            lastFolderId = rows[^1].Id;
            await HeartbeatAsync(cancellationToken);
        }

        foreach (var folder in folders.Values)
        {
            if (!folder.ParentId.HasValue)
            {
                continue;
            }

            if (!folders.TryGetValue(folder.ParentId.Value, out var parent))
            {
                throw new InvalidOperationException(
                    "The folder tree contains a missing parent.");
            }

            parent.RemainingChildren++;
        }

        foreach (var folder in folders.Values)
        {
            AssignFolderDepth(folder, folders);
        }

        var ready = new Queue<FinalizationFolderRollupState>();
        foreach (var folder in folders.Values)
        {
            if (folder.RemainingChildren == 0)
            {
                ready.Enqueue(folder);
            }
        }

        var updates =
            new List<FinalizationFolderRollupUpdate>(FinalizationBatchSize);
        long processedFolders = 0;
        ReportFinalizationProgress(
            progress,
            "Rolling up folder trees",
            0,
            totalFolders);
        while (ready.TryDequeue(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            updates.Add(new FinalizationFolderRollupUpdate(
                folder.Id,
                folder.LogicalSize,
                folder.AllocatedSize,
                folder.FileCount,
                folder.Depth));
            if (folder.ParentId.HasValue)
            {
                var parent = folders[folder.ParentId.Value];
                parent.LogicalSize += folder.LogicalSize;
                parent.AllocatedSize += folder.AllocatedSize;
                parent.FileCount += folder.FileCount;
                parent.RemainingChildren--;
                if (parent.RemainingChildren == 0)
                {
                    ready.Enqueue(parent);
                }
            }

            processedFolders++;
            if (updates.Count >= FinalizationBatchSize)
            {
                await WriteRolledUpFolderTotalsAsync(
                    updates,
                    cancellationToken);
                updates.Clear();
                ReportFinalizationProgress(
                    progress,
                    "Rolling up folder trees",
                    processedFolders,
                    totalFolders);
            }
        }

        await WriteRolledUpFolderTotalsAsync(updates, cancellationToken);
        if (processedFolders != folders.Count)
        {
            throw new InvalidOperationException(
                "The folder tree could not be rolled up because it is cyclic.");
        }

        ReportFinalizationProgress(
            progress,
            "Rolling up folder trees",
            processedFolders,
            totalFolders);
    }

    private static void AssignFolderDepth(
        FinalizationFolderRollupState folder,
        IReadOnlyDictionary<long, FinalizationFolderRollupState> folders)
    {
        if (folder.Depth >= 0)
        {
            return;
        }

        var path = new List<FinalizationFolderRollupState>();
        var visiting = new HashSet<long>();
        var current = folder;
        while (current.Depth < 0)
        {
            if (!visiting.Add(current.Id))
            {
                throw new InvalidOperationException(
                    "The folder tree is cyclic and cannot assign folder depths.");
            }

            path.Add(current);
            if (!current.ParentId.HasValue)
            {
                break;
            }

            current = folders[current.ParentId.Value];
        }

        var last = path[^1];
        if (last.Depth < 0)
        {
            last.Depth = last.ParentId is long parentId
                ? folders[parentId].Depth + 1
                : 0;
        }

        for (var index = path.Count - 2; index >= 0; index--)
        {
            var parentId = path[index].ParentId ??
                throw new InvalidOperationException(
                    "The folder tree contains an invalid parent.");
            var parent = folders[parentId];
            path[index].Depth = parent.Depth + 1;
        }
    }

    private async Task<List<FinalizationFolderRollupState>> ReadFolderRollupBatchAsync(
        long afterId,
        CancellationToken cancellationToken)
    {
        var rows = new List<FinalizationFolderRollupState>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ParentId, DirectLogicalSize,
                   DirectAllocatedSize, DirectFileCount
            FROM FolderNodes NOT INDEXED
            WHERE RunKey = $run
              AND Id > $after
            ORDER BY Id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$limit", FinalizationBatchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FinalizationFolderRollupState(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    private async Task WriteRolledUpFolderTotalsAsync(
        IReadOnlyList<FinalizationFolderRollupUpdate> rows,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE FolderNodes
            SET LogicalSize = $logical,
                AllocatedSize = $allocated,
                FileCount = $files,
                Depth = $depth
            WHERE Id = $id AND RunKey = $run
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$id", 0L);
        command.Parameters.AddWithValue("$logical", 0L);
        command.Parameters.AddWithValue("$allocated", 0L);
        command.Parameters.AddWithValue("$files", 0L);
        command.Parameters.AddWithValue("$depth", 0);
        command.Prepare();
        for (var index = 0; index < rows.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var row = rows[index];
            command.Parameters["$id"].Value = row.Id;
            command.Parameters["$logical"].Value = row.LogicalSize;
            command.Parameters["$allocated"].Value = row.AllocatedSize;
            command.Parameters["$files"].Value = row.FileCount;
            command.Parameters["$depth"].Value = row.Depth;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "A rolled-up folder total could not be finalized.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReplaceCategorySummariesAsync(
        IReadOnlyList<long> categorySizes,
        IReadOnlyList<long> categoryCounts,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await using var delete = _connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText =
            "DELETE FROM ScanCategorySummaries WHERE ScanId = $scan";
        delete.Parameters.AddWithValue("$scan", ScanId.ToString("N"));
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ScanCategorySummaries (
                ScanId, Category, SizeBytes, ItemCount
            )
            VALUES ($scan, $category, $size, $count)
            """;
        insert.Parameters.AddWithValue("$scan", ScanId.ToString("N"));
        insert.Parameters.AddWithValue("$category", string.Empty);
        insert.Parameters.AddWithValue("$size", 0L);
        insert.Parameters.AddWithValue("$count", 0L);
        insert.Prepare();
        for (var category = 0; category < categoryCounts.Count; category++)
        {
            if (categoryCounts[category] == 0)
            {
                continue;
            }

            insert.Parameters["$category"].Value =
                StorageCategoryClassifier.CategoryName(category);
            insert.Parameters["$size"].Value = categorySizes[category];
            insert.Parameters["$count"].Value = categoryCounts[category];
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void ReportFinalizationProgress(
        IProgress<StorageScanFinalizationProgress>? progress,
        string stage,
        long completedItems,
        long totalItems)
    {
        progress?.Report(new StorageScanFinalizationProgress
        {
            Stage = stage,
            CompletedItems = completedItems,
            TotalItems = totalItems,
        });
    }

    private sealed record FinalizationCounts(
        long FileNames,
        long PhysicalFiles,
        long Folders);

    private sealed record FinalizationAliasRow(
        long Id,
        long PhysicalFileId,
        bool IsHardLinkAlias,
        long AllocatedSize,
        int Category);

    private sealed record FinalizationAliasUpdate(
        long Id,
        bool IsAlias);

    private sealed record FinalizationDirectRow(
        long Id,
        long ParentId,
        long LogicalSize,
        long AllocatedSize,
        bool IsAlias);

    private sealed record FinalizationFolderTotal(
        long Id,
        long LogicalSize,
        long AllocatedSize,
        long FileCount);

    private sealed record FinalizationFolderRollupUpdate(
        long Id,
        long LogicalSize,
        long AllocatedSize,
        long FileCount,
        int Depth);

    private sealed class FinalizationFolderRollupState(
        long id,
        long? parentId,
        long logicalSize,
        long allocatedSize,
        long fileCount)
    {
        public long Id { get; } = id;

        public long? ParentId { get; } = parentId;

        public long LogicalSize { get; set; } = logicalSize;

        public long AllocatedSize { get; set; } = allocatedSize;

        public long FileCount { get; set; } = fileCount;

        public int RemainingChildren { get; set; }

        public int Depth { get; set; } = -1;
    }

    public async Task<ScanTotals> GetScanTotalsAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM FileNames WHERE RunKey = $run),
                (SELECT COUNT(*) FROM FolderNodes WHERE RunKey = $run),
                COALESCE((SELECT SUM(LogicalSize)
                          FROM PhysicalFiles WHERE RunKey = $run), 0),
                COALESCE((SELECT SUM(AllocatedSize)
                          FROM PhysicalFiles WHERE RunKey = $run), 0)
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ScanTotals(0, 0, 0, 0);
        }

        return new ScanTotals(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private async Task<(long Id, long CreatedCount)> EnsureFolderIdAsync(
        string path,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var normalized = StorageRepository.NormalizeFolderPath(path);
        if (_folderIds.TryGetValue(normalized, out var existing))
        {
            return (existing, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var trimmed = normalized.TrimEnd('\\');
        var root = Path.GetPathRoot(normalized);
        var isRoot = string.Equals(
            trimmed,
            root?.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
        long? parentId = null;
        long createdAncestors = 0;
        var depth = 0;
        if (!isRoot)
        {
            var parentPath = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                throw new InvalidOperationException(
                    $"Folder '{path}' has no resolvable parent.");
            }

            var parent = await EnsureFolderIdAsync(
                parentPath,
                transaction,
                cancellationToken);
            parentId = parent.Id;
            createdAncestors = parent.CreatedCount;
            depth = normalized.Count(character => character == '\\');
        }

        var name = isRoot
            ? trimmed
            : Path.GetFileName(trimmed);
        var category = StorageCategoryClassifier.ClassifyPath(
            normalized,
            string.Empty,
            FileAttributes.Directory);
        var tag = StorageCategoryClassifier.TagPath(normalized, category);
        if (!parentId.HasValue)
        {
            await using var existingRoot = _connection.CreateCommand();
            existingRoot.Transaction = transaction;
            existingRoot.CommandText = """
                SELECT Id
                FROM FolderNodes
                WHERE RunKey = $run AND ParentId IS NULL AND Name = $name
                """;
            existingRoot.Parameters.AddWithValue("$run", _runKey);
            existingRoot.Parameters.AddWithValue("$name", name);
            var existingRootId =
                await existingRoot.ExecuteScalarAsync(cancellationToken);
            if (existingRootId is not null &&
                existingRootId != DBNull.Value)
            {
                var persistedRootId = Convert.ToInt64(existingRootId);
                CacheFolderId(normalized, persistedRootId);
                return (persistedRootId, 0);
            }
        }

        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO FolderNodes (
                RunKey, ParentId, Name, PathHash, Depth, Attributes,
                ReparseTag, ModifiedUtc, Category, Tag, LastSeenGeneration
            )
            VALUES (
                $run, $parent, $name, $hash, $depth, 0, 0, 0,
                $category, $tag, 1
            )
            ON CONFLICT(RunKey, ParentId, Name) DO NOTHING
            RETURNING Id
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue(
            "$parent",
            parentId.HasValue ? parentId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$hash", GetPathHash(normalized));
        command.Parameters.AddWithValue("$depth", depth);
        command.Parameters.AddWithValue(
            "$category",
            StorageCategoryClassifier.CategoryId(category));
        command.Parameters.AddWithValue(
            "$tag",
            StorageCategoryClassifier.TagId(tag));
        var insertedId = command.ExecuteScalar();
        long id;
        var inserted = insertedId is not null && insertedId != DBNull.Value;
        if (inserted)
        {
            id = Convert.ToInt64(insertedId);
        }
        else
        {
            await using var existingFolder = _connection.CreateCommand();
            existingFolder.Transaction = transaction;
            existingFolder.CommandText = """
                SELECT Id
                FROM FolderNodes
                WHERE RunKey = $run
                  AND ParentId IS $parent
                  AND Name = $name
                """;
            existingFolder.Parameters.AddWithValue("$run", _runKey);
            existingFolder.Parameters.AddWithValue(
                "$parent",
                parentId.HasValue ? parentId.Value : DBNull.Value);
            existingFolder.Parameters.AddWithValue("$name", name);
            var existingId =
                await existingFolder.ExecuteScalarAsync(cancellationToken);
            if (existingId is null || existingId == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"The directory '{path}' could not be resolved after insertion.");
            }

            id = Convert.ToInt64(existingId);
        }

        CacheFolderId(normalized, id);
        return (id, createdAncestors + (inserted ? 1 : 0));
    }

    private void CacheFolderId(string path, long folderId)
    {
        if (_folderIds.Count >= MaximumFolderCacheEntries)
        {
            _folderIds.Clear();
        }

        _folderIds[path] = folderId;
    }

    private SqliteCommand CreatePhysicalFileInsert(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PhysicalFiles (
                RunKey, FileIdentityKey, FileId128, VolumeSerialNumber,
                LogicalSize, AllocatedSize, CreatedUtc, ModifiedUtc,
                AccessedUtc, Attributes, ReparseTag, LinkCount,
                ChangeTime, LastSeenGeneration
            )
            VALUES (
                $run, $identity, $fileId, $volume, $logical, $allocated,
                $created, $modified, $accessed, $attributes, $reparse,
                $links, $change, 1
            )
            ON CONFLICT(RunKey, FileIdentityKey) DO NOTHING
            RETURNING Id
            """;
        AddPhysicalFileParameters(command);
        command.Prepare();
        return command;
    }

    private SqliteCommand CreatePhysicalFileUpdate(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE PhysicalFiles
            SET
                FileId128 = $fileId,
                VolumeSerialNumber = $volume,
                LogicalSize = $logical,
                AllocatedSize = $allocated,
                CreatedUtc = $created,
                ModifiedUtc = $modified,
                AccessedUtc = $accessed,
                Attributes = $attributes,
                ReparseTag = $reparse,
                LinkCount = $links,
                ChangeTime = $change,
                HashState = CASE
                    WHEN PhysicalFiles.LogicalSize <> $logical
                      OR PhysicalFiles.ModifiedUtc <> $modified
                      OR PhysicalFiles.ChangeTime <> $change
                    THEN 0 ELSE PhysicalFiles.HashState END,
                ContentHash = CASE
                    WHEN PhysicalFiles.LogicalSize <> $logical
                      OR PhysicalFiles.ModifiedUtc <> $modified
                      OR PhysicalFiles.ChangeTime <> $change
                    THEN NULL ELSE PhysicalFiles.ContentHash END,
                SampleHash = CASE
                    WHEN PhysicalFiles.LogicalSize <> $logical
                      OR PhysicalFiles.ModifiedUtc <> $modified
                      OR PhysicalFiles.ChangeTime <> $change
                    THEN NULL ELSE PhysicalFiles.SampleHash END
            WHERE RunKey = $run
              AND FileIdentityKey = $identity
            RETURNING Id
            """;
        AddPhysicalFileParameters(command);
        command.Prepare();
        return command;
    }

    private void AddPhysicalFileParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$identity", string.Empty);
        command.Parameters.AddWithValue("$fileId", string.Empty);
        command.Parameters.AddWithValue("$volume", 0L);
        command.Parameters.AddWithValue("$logical", 0L);
        command.Parameters.AddWithValue("$allocated", 0L);
        command.Parameters.AddWithValue("$created", 0L);
        command.Parameters.AddWithValue("$modified", 0L);
        command.Parameters.AddWithValue("$accessed", 0L);
        command.Parameters.AddWithValue("$attributes", 0L);
        command.Parameters.AddWithValue("$reparse", 0L);
        command.Parameters.AddWithValue("$links", 1L);
        command.Parameters.AddWithValue("$change", 0L);
    }

    private static void SetPhysicalFileParameters(
        SqliteCommand command,
        string identityKey,
        StorageFileRecord record)
    {
        command.Parameters["$identity"].Value = identityKey;
        command.Parameters["$fileId"].Value = record.FileId128;
        command.Parameters["$volume"].Value =
            record.VolumeSerialNumber;
        command.Parameters["$logical"].Value = record.SizeBytes;
        command.Parameters["$allocated"].Value =
            record.AllocatedSizeBytes == 0
                ? record.SizeBytes
                : record.AllocatedSizeBytes;
        command.Parameters["$created"].Value =
            record.CreatedUtc.ToUnixTimeMilliseconds();
        command.Parameters["$modified"].Value =
            record.ModifiedUtc.ToUnixTimeMilliseconds();
        command.Parameters["$accessed"].Value =
            record.AccessedUtc.ToUnixTimeMilliseconds();
        command.Parameters["$attributes"].Value = record.Attributes;
        command.Parameters["$reparse"].Value =
            unchecked((long)record.ReparseTag);
        command.Parameters["$links"].Value = record.LinkCount;
        command.Parameters["$change"].Value = record.ChangeTime;
    }

    private SqliteCommand CreateFileNameInsert(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO FileNames (
                RunKey, ParentId, PhysicalFileId, Name, Extension,
                Category, Tag, IsHardLinkAlias, LastSeenGeneration
            )
            VALUES (
                $run, $parent, $physical, $name, $extension, $category,
                $tag, $hardAlias, 1
            )
            ON CONFLICT(RunKey, ParentId, Name) DO NOTHING
            RETURNING Id
            """;
        AddFileNameParameters(command);
        command.Prepare();
        return command;
    }

    private SqliteCommand CreateFileNameUpdate(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE FileNames
            SET PhysicalFileId = $physical,
                Extension = $extension,
                Category = $category,
                Tag = $tag,
                IsHardLinkAlias = CASE
                    WHEN FileNames.PhysicalFileId = $physical
                    THEN FileNames.IsHardLinkAlias
                    ELSE $hardAlias
                    END,
                LastSeenGeneration = 1
            WHERE RunKey = $run
              AND ParentId = $parent
              AND Name = $name
            RETURNING Id
            """;
        AddFileNameParameters(command);
        command.Prepare();
        return command;
    }

    private void AddFileNameParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$parent", 0L);
        command.Parameters.AddWithValue("$physical", 0L);
        command.Parameters.AddWithValue("$name", string.Empty);
        command.Parameters.AddWithValue("$extension", string.Empty);
        command.Parameters.AddWithValue("$category", 0);
        command.Parameters.AddWithValue("$tag", 0);
        command.Parameters.AddWithValue("$hardAlias", 0);
    }

    private SqliteCommand CreateCandidateInsert(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO NormalizedCleanupCandidates (
                RunKey, PolicyId, FileNameId
            )
            VALUES ($run, $policy, $fileName)
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$policy", 0);
        command.Parameters.AddWithValue("$fileName", 0L);
        command.Prepare();
        return command;
    }

    private static IEnumerable<CleanupPolicyId> GetMaterializedPolicies(
        StorageFileRecord record)
    {
        var now = DateTimeOffset.Now;
        if (CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.TemporaryFiles,
                now,
                false).IsAllowed)
        {
            yield return CleanupPolicyId.TemporaryFiles;
        }

        if (CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.OldDownloads,
                now,
                false).IsAllowed)
        {
            yield return CleanupPolicyId.OldDownloads;
        }

        if (CleanupPolicyService.IsKnownPackageCache(record.FullPath))
        {
            yield return CleanupPolicyId.PackageCaches;
        }

    }

    private static string GetPathHash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                path.TrimEnd('\\').ToUpperInvariant())));

    private static long GetDirectAllocatedSize(StorageFolderRecord record) =>
        record.DirectAllocatedSizeBytes;

    public async Task CompleteAsync(
        long filesScanned,
        long foldersScanned,
        long bytesScanned,
        long inaccessibleItems,
        CancellationToken cancellationToken,
        long? allocatedBytesScanned = null)
    {
        await UpdateRunAsync(
            StorageScanStatus.Completed,
            DateTimeOffset.UtcNow,
            filesScanned,
            foldersScanned,
            bytesScanned,
            inaccessibleItems,
            null,
            cancellationToken,
            allocatedBytesScanned: allocatedBytesScanned);
        _closed = true;
    }

    internal async Task MaterializePreviousWindowsInstallationCandidatesAsync(
        CancellationToken cancellationToken,
        IProgress<StorageScanFinalizationProgress>? progress = null)
    {
        const int candidateBatchSize = 10_000;
        var policyId =
            (int)CleanupPolicyId.PreviousWindowsInstallation;
        ReportFinalizationProgress(
            progress,
            "Finding Previous Windows files",
            0,
            0);
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            await using var delete = _connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM NormalizedCleanupCandidates
                WHERE (RunKey, PolicyId, FileNameId) IN (
                    SELECT RunKey, PolicyId, FileNameId
                    FROM NormalizedCleanupCandidates
                    WHERE RunKey = $run AND PolicyId = $policy
                    LIMIT $limit
                )
                """;
            delete.Parameters.AddWithValue("$run", _runKey);
            delete.Parameters.AddWithValue("$policy", policyId);
            delete.Parameters.AddWithValue("$limit", candidateBatchSize);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                break;
            }

            await HeartbeatAsync(cancellationToken);
        }

        long? previousWindowsRoot = null;
        await using (var root = _connection.CreateCommand())
        {
            root.CommandText = """
                SELECT Id
                FROM FolderNodes
                WHERE RunKey = $run AND PathHash = $hash
                """;
            root.Parameters.AddWithValue("$run", _runKey);
            root.Parameters.AddWithValue(
                "$hash",
                GetPathHash(
                    StorageRepository.NormalizeFolderPath(
                        @"C:\Windows.old")));
            var value =
                await root.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value != DBNull.Value)
            {
                previousWindowsRoot = Convert.ToInt64(value);
            }
        }

        if (!previousWindowsRoot.HasValue)
        {
            await HeartbeatAsync(cancellationToken);
            return;
        }

        var folderIds =
            await ReadPreviousWindowsFolderIdsAsync(
                previousWindowsRoot.Value,
                cancellationToken);
        var previousWindowsFolders = folderIds.ToHashSet();
        long lastParentId = 0;
        long lastFileNameId = 0;
        long processedFileNames = 0;
        while (true)
        {
            var rows = await ReadPreviousWindowsFileBatchAsync(
                lastParentId,
                lastFileNameId,
                cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            var candidateIds = rows
                .Where(row =>
                    !row.IsAlias &&
                    previousWindowsFolders.Contains(row.ParentId))
                .Select(row => row.Id)
                .ToList();
            await InsertPreviousWindowsCandidatesAsync(
                policyId,
                candidateIds,
                cancellationToken);
            lastParentId = rows[^1].ParentId;
            lastFileNameId = rows[^1].Id;
            processedFileNames += rows.Count;
            ReportFinalizationProgress(
                progress,
                "Finding Previous Windows files",
                processedFileNames,
                0);
            await HeartbeatAsync(cancellationToken);
        }
    }

    private async Task<HashSet<long>> ReadPreviousWindowsFolderIdsAsync(
        long rootId,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<long>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE PreviousWindowsTree(Id) AS (
                SELECT Id
                FROM FolderNodes
                WHERE RunKey = $run AND Id = $root
                UNION ALL
                SELECT child.Id
                FROM FolderNodes child
                INNER JOIN PreviousWindowsTree parent
                    ON child.ParentId = parent.Id
                WHERE child.RunKey = $run
            )
            SELECT Id
            FROM PreviousWindowsTree
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$root", rootId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if ((index++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task<List<PreviousWindowsFileRow>> ReadPreviousWindowsFileBatchAsync(
        long afterParentId,
        long afterFileNameId,
        CancellationToken cancellationToken)
    {
        var rows = new List<PreviousWindowsFileRow>();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ParentId, IsHardLinkAlias
            FROM FileNames INDEXED BY IX_FileNames_ParentSize
            WHERE RunKey = $run
              AND (
                  ParentId > $parent
                  OR (ParentId = $parent AND Id > $file)
              )
            ORDER BY ParentId, Id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$parent", afterParentId);
        command.Parameters.AddWithValue("$file", afterFileNameId);
        command.Parameters.AddWithValue("$limit", FinalizationBatchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PreviousWindowsFileRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2) != 0));
        }

        return rows;
    }

    private async Task InsertPreviousWindowsCandidatesAsync(
        int policyId,
        IReadOnlyList<long> fileNameIds,
        CancellationToken cancellationToken)
    {
        if (fileNameIds.Count == 0)
        {
            return;
        }

        await using var transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(
                cancellationToken);
        await HeartbeatInTransactionAsync(transaction, cancellationToken);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO NormalizedCleanupCandidates (
                RunKey, PolicyId, FileNameId
            )
            VALUES ($run, $policy, $file)
            """;
        command.Parameters.AddWithValue("$run", _runKey);
        command.Parameters.AddWithValue("$policy", policyId);
        command.Parameters.AddWithValue("$file", 0L);
        command.Prepare();
        for (var index = 0; index < fileNameIds.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            command.Parameters["$file"].Value = fileNameIds[index];
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record PreviousWindowsFileRow(
        long Id,
        long ParentId,
        bool IsAlias);

    public async Task FailAsync(
        StorageScanStatus status,
        string? error,
        long filesScanned,
        long foldersScanned,
        long bytesScanned,
        long inaccessibleItems,
        CancellationToken cancellationToken,
        long? allocatedBytesScanned = null)
    {
        var changed = await UpdateRunAsync(
            status,
            DateTimeOffset.UtcNow,
            filesScanned,
            foldersScanned,
            bytesScanned,
            inaccessibleItems,
            error,
            cancellationToken,
            allocatedBytesScanned: allocatedBytesScanned);
        if (changed)
        {
            _closed = true;
        }
    }

    private async Task<bool> UpdateRunAsync(
        StorageScanStatus status,
        DateTimeOffset completedUtc,
        long filesScanned,
        long foldersScanned,
        long bytesScanned,
        long inaccessibleItems,
        string? error,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null,
        long? allocatedBytesScanned = null)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ScanRuns
            SET Status = $status,
                CompletedUtc = $completed,
                FilesScanned = $files,
                FoldersScanned = $folders,
                BytesScanned = $bytes,
                AllocatedBytesScanned = $allocatedBytes,
                InaccessibleItems = $inaccessible,
                ErrorMessage = $error,
                OwnerProcessId = NULL,
                OwnerProcessStartedUtc = NULL,
                LeaseHeartbeatUtc = NULL,
                ScanPhase = $phase,
                PhaseStartedUtc = $completed
            WHERE Id = $id
              AND Status = $expectedStatus
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$completed", completedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$files", filesScanned);
        command.Parameters.AddWithValue("$folders", foldersScanned);
        command.Parameters.AddWithValue("$bytes", bytesScanned);
        command.Parameters.AddWithValue(
            "$allocatedBytes",
            allocatedBytesScanned ?? bytesScanned);
        command.Parameters.AddWithValue("$inaccessible", inaccessibleItems);
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$phase",
            status == StorageScanStatus.Completed
                ? "Complete"
                : status.ToString());
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue("$expectedStatus", (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue("$started", _owner.ProcessStartedUtc);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (!changed)
        {
            throw new InvalidOperationException(
                "The scan cannot be published because its lease is no longer valid.");
        }

        return true;
    }

    private async Task HeartbeatInTransactionAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ScanRuns
            SET LeaseHeartbeatUtc = $heartbeat
            WHERE Id = $id
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue(
            "$heartbeat",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue(
            "$started",
            _owner.ProcessStartedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The scan lease is no longer owned by this process.");
        }
    }

    private async Task EnsureLeaseInTransactionAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM ScanRuns
            WHERE Id = $id
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue("$running", (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue("$started", _owner.ProcessStartedUtc);
        var lease = await command.ExecuteScalarAsync(cancellationToken);
        if (lease is null || lease == DBNull.Value)
        {
            throw new InvalidOperationException(
                "The scan lease is no longer owned by this process.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            try
            {
                await FailWithoutResettingProgressAsync(
                    StorageScanStatus.Failed,
                    "Scan session closed before completion.",
                    CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Another owner already reclaimed or completed the lease.
            }
        }

        await _connection.DisposeAsync();
    }

    private async Task FailWithoutResettingProgressAsync(
        StorageScanStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanRuns
            SET Status = $status,
                CompletedUtc = $completed,
                ErrorMessage = $error,
                OwnerProcessId = NULL,
                OwnerProcessStartedUtc = NULL,
                LeaseHeartbeatUtc = NULL,
                ScanPhase = $phase,
                PhaseStartedUtc = $completed
            WHERE Id = $id
              AND Status = $running
              AND OwnerProcessId = $pid
              AND OwnerProcessStartedUtc = $started
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue(
            "$completed",
            completedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$phase", status.ToString());
        command.Parameters.AddWithValue("$id", ScanId.ToString("N"));
        command.Parameters.AddWithValue(
            "$running",
            (int)StorageScanStatus.Running);
        command.Parameters.AddWithValue("$pid", _owner.ProcessId);
        command.Parameters.AddWithValue("$started", _owner.ProcessStartedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The scan cannot be closed because its lease is no longer valid.");
        }

        _closed = true;
    }
}

public sealed record DuplicateSizeGroup(long SizeBytes, IReadOnlyList<string> Paths);

public sealed record FileHashUpdate(
    string Path,
    string Hash,
    FileIdentity? Identity);

public sealed record CleanupPolicySummary(
    CleanupPolicyId PolicyId,
    long ItemCount,
    long SizeBytes);

public sealed record JournalCheckpoint(
    long JournalId,
    long NextUsn,
    int ScanFormat,
    long RunKey);

public sealed record DirectoryRefreshOutcome(
    bool Applied,
    string Reason);

internal sealed record ExistingFileRow(
    long NameId,
    long PhysicalId,
    string IdentityKey,
    long LogicalSize,
    long AllocatedSize,
    bool IsAlias);

internal sealed record ExistingFolderRow(
    long Id,
    string FileId128);

public sealed record ScanWorkItem(
    string PathHash,
    string FullPath,
    int Depth);

internal sealed record ScanFolderMetadata(
    long Attributes,
    uint ReparseTag,
    DateTimeOffset ModifiedUtc,
    string FileId128,
    bool IsIncomplete);

internal sealed record ScanDirectoryEntry(
    string Path,
    int Depth,
    ScanFolderMetadata Metadata,
    bool QueueWork);

public sealed record ScanTotals(
    long Files,
    long Folders,
    long LogicalBytes,
    long AllocatedBytes);

public sealed record IndexHealthResult(
    bool IsHealthy,
    string QuickCheckResult,
    long ForeignKeyViolations);

internal sealed class DuplicateHashSummary
{
    public int Count { get; set; }

    public long TotalBytes { get; set; }

    public long RetainedBytes { get; init; }
}

public sealed record StorageIndexUsage(
    long DatabaseBytes,
    long WriteAheadLogBytes,
    long SharedMemoryBytes,
    long TotalBytes);
