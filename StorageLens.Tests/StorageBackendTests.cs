using Microsoft.Data.Sqlite;
using StorageLens.Models;
using StorageLens.Services;

namespace StorageLens.Tests;

public sealed class StorageBackendTests
{
    [Fact]
    public async Task Repository_Initializes()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageLensTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new StorageRepository(Path.Combine(root, "storage.db"));
            await repository.InitializeAsync();
            await using var connection = new SqliteConnection(
                $"Data Source={repository.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            Assert.Contains("IX_FileEntries_ScanSize", names);
            Assert.Contains("IX_FileEntries_DuplicateHash", names);
            Assert.Contains("IX_FileNames_PhysicalFile", names);
            Assert.Contains("IX_NormalizedCleanupCandidates_FileName", names);
            Assert.DoesNotContain("IX_FileEntries_Path", names);
            Assert.DoesNotContain("IX_FileEntries_ScanModified", names);
            Assert.DoesNotContain("IX_FolderEntries_ScanPath", names);
            Assert.True(repository.GetStorageUsage().TotalBytes > 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Repository_WritesAndReadsAScan()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session = await fixture.Repository.BeginScanAsync(fixture.Drive);
        await session.InsertFilesAsync(
            [
                new StorageFileRecord
                {
                    ScanId = session.ScanId,
                    Name = "one.txt",
                    FullPath = Path.Combine(fixture.ScanRoot, "one.txt"),
                    ParentPath = fixture.ScanRoot,
                    SizeBytes = 10,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    ModifiedUtc = DateTimeOffset.UtcNow,
                    AccessedUtc = DateTimeOffset.UtcNow,
                    Attributes = (long)FileAttributes.Normal,
                    Extension = ".txt",
                    Category = "Documents",
                    Tag = "Personal",
                },
            ],
            CancellationToken.None);
        await session.CompleteAsync(1, 1, 10, 0, CancellationToken.None);
        var scan = await fixture.Repository.GetLatestCompletedScanAsync(fixture.Drive.Letter);
        Assert.NotNull(scan);
    }

    [Fact]
    public void Classifier_ClassifiesTemporaryAndPhotoFiles()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "scan", "cache.tmp");
        var photo = Path.Combine(Path.GetTempPath(), "scan", "photo.jpg");

        Assert.Equal(
            "Cache & temporary",
            StorageCategoryClassifier.ClassifyPath(
                temporary,
                ".tmp",
                FileAttributes.Normal));
        Assert.Equal(
            "Photos",
            StorageCategoryClassifier.ClassifyPath(
                @"C:\Users\Alex\Pictures\photo.jpg",
                ".jpg",
                FileAttributes.Normal));
    }

    [Fact]
    public async Task FolderInsights_SurfacesHeavyDescendantsWithoutOverlappingTotals()
    {
        var users = CreateUsageFolder(@"C:\Users", 100, true);
        var profile = CreateUsageFolder(@"C:\Users\Alex", 100, true);
        var appData = CreateUsageFolder(@"C:\Users\Alex\AppData", 60, true);
        var local = CreateUsageFolder(@"C:\Users\Alex\AppData\Local", 60, true);
        var microsoft = CreateUsageFolder(
            @"C:\Users\Alex\AppData\Local\Microsoft", 45, true);
        var edge = CreateUsageFolder(
            @"C:\Users\Alex\AppData\Local\Microsoft\Edge", 45);
        var cache = CreateUsageFolder(
            @"C:\Users\Alex\AppData\Local\Cache", 15);
        var projects = CreateUsageFolder(@"C:\Users\Alex\Projects", 40);
        var programs = CreateUsageFolder(@"C:\Programs", 30);
        var children = new Dictionary<string, IReadOnlyList<StorageFolderNode>>
        {
            [users.Path] = [profile],
            [profile.Path] = [appData, projects],
            [appData.Path] = [local],
            [local.Path] = [microsoft, cache],
            [microsoft.Path] = [edge],
        };

        var result = await FolderUsageInsights.SelectLargestAsync(
            [users, programs],
            (path, _, _) => Task.FromResult(
                children.GetValueOrDefault(path) ?? []),
            3);

        Assert.Equal(
            new[] { edge.Path, projects.Path, programs.Path },
            result.Select(folder => folder.Path));
        Assert.DoesNotContain(result, folder =>
            folder.Path == users.Path || folder.Path == profile.Path);
        Assert.All(result, parent => Assert.DoesNotContain(
            result,
            child => child.Path.StartsWith(
                parent.Path + "\\",
                StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task FolderInsights_RetainsFoldersWhoseSpaceIsInDirectFiles()
    {
        var images = CreateUsageFolder(@"C:\VirtualMachines", 100, true);
        var other = CreateUsageFolder(@"C:\Projects", 40);
        var smallChild = CreateUsageFolder(
            @"C:\VirtualMachines\Logs", 10);

        var result = await FolderUsageInsights.SelectLargestAsync(
            [images, other],
            (_, _, _) => Task.FromResult<IReadOnlyList<StorageFolderNode>>(
                [smallChild]),
            2);

        Assert.Equal(images, result[0]);
        Assert.Equal(other, result[1]);
        Assert.DoesNotContain(smallChild, result);
    }

    [Fact]
    public async Task FolderInsights_BoundsDeepHierarchyQueries()
    {
        var queries = 0;
        var result = await FolderUsageInsights.SelectLargestAsync(
            [CreateUsageFolder(@"C:\Deep", 100, true)],
            (path, _, _) =>
            {
                Assert.True(++queries <= 64, "Folder insight queries must be bounded.");
                return Task.FromResult<IReadOnlyList<StorageFolderNode>>(
                    [CreateUsageFolder(path + @"\Nested", 100, true)]);
            },
            1);

        var folder = Assert.Single(result);
        Assert.StartsWith(@"C:\Deep\Nested", folder.Path);
        Assert.Equal(100, folder.DisplaySizeBytes);
    }

    [Fact]
    public async Task FolderInsights_HonorsCancellationBeforeQuerying()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FolderUsageInsights.SelectLargestAsync(
                [CreateUsageFolder(@"C:\Users", 100, true)],
                (_, _, _) => throw new InvalidOperationException(
                    "Canceled insight requests must not query the index."),
                6,
                cancellation.Token));
    }

    [Fact]
    public void GrowthInsights_ReplacesParentRollupsWithLargestChildren()
    {
        var result = FolderGrowthInsights.SelectLargest(
            [
                CreateGrowthInsight(@"C:\", 100, 110),
                CreateGrowthInsight(@"C:\Users", 95, 105),
                CreateGrowthInsight(@"C:\Users\Alex", 90, 100),
                CreateGrowthInsight(
                    @"C:\Users\Alex\AppData",
                    85,
                    95),
                CreateGrowthInsight(
                    @"C:\Users\Alex\AppData\Local",
                    80,
                    90),
                CreateGrowthInsight(@"C:\Other", 5, 20),
            ],
            3);

        Assert.Equal(2, result.Count);
        Assert.Equal(
            new[]
            {
                @"C:\Users\Alex\AppData\Local",
                @"C:\Other",
            },
            result.Select(insight => insight.Path));
        Assert.DoesNotContain(
            result,
            insight => insight.Path is @"C:\" or @"C:\Users" or @"C:\Users\Alex");
    }

    [Fact]
    public void GrowthInsights_RetainsDirectFileHeavyParent()
    {
        var parent = CreateGrowthInsight(@"C:\Users\Alex", 100, 200);

        var result = FolderGrowthInsights.SelectLargest(
            [
                parent,
                CreateGrowthInsight(
                    @"C:\Users\Alex\AppData",
                    20,
                    50),
            ],
            3);

        Assert.Equal(parent, Assert.Single(result));
    }

    [Fact]
    public void GrowthInsights_AlwaysOmitsDriveRoot()
    {
        var child = CreateGrowthInsight(@"C:\Users", 20, 50);

        var result = FolderGrowthInsights.SelectLargest(
            [
                CreateGrowthInsight(@"C:\", 100, 200),
                child,
            ],
            3);

        Assert.Equal(child, Assert.Single(result));
        Assert.DoesNotContain(result, insight => insight.Path == @"C:\");
    }

    [Fact]
    public async Task Repository_OverviewFoldersReachHeavyApplicationSubfolders()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync(
            @"Users\Alex\AppData\Local\AppOne\cache.dat", 16384);
        await fixture.WriteFileAsync(
            @"Users\Alex\Projects\build.bin", 8192);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var folders = await fixture.Repository.GetTopFoldersAsync(
            scan.ScanId,
            limit: 2);

        Assert.Equal(2, folders.Count);
        Assert.EndsWith(@"\AppOne", folders[0].Path);
        Assert.EndsWith(@"\Projects", folders[1].Path);
        Assert.All(folders, folder => Assert.False(folder.HasChildren));
    }

    [Fact]
    public async Task Repository_LargestFilesExcludeFoldersAndRespectVisibility()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var large = await fixture.WriteFileAsync(
            @"Group\large.bin", 98304);
        var small = await fixture.WriteFileAsync(
            @"Group\small.bin", 32768);
        var hidden = await fixture.WriteFileAsync(
            @"Group\hidden.bin", 262144);
        var system = await fixture.WriteFileAsync(
            @"Group\system.bin", 524288);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var files = await fixture.Repository.GetLargestFilesAsync(
            scan.ScanId,
            includeSystem: false,
            includeHidden: false,
            limit: 8);

        Assert.Equal(new[] { large, small }, files.Select(file => file.Path));
        Assert.All(files, file => Assert.False(file.IsFolder));
        var all = await fixture.Repository.GetLargestFilesAsync(
            scan.ScanId,
            limit: 2);
        Assert.Equal(new[] { system, hidden }, all.Select(file => file.Path));
    }

    [Fact]
    public async Task Repository_LegacyOverviewSeparatesDeepFoldersAndFiles()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var scanId = Guid.NewGuid();
        await using (var connection = new SqliteConnection(
                         $"Data Source={fixture.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ScanRuns (
                    Id, DriveLetter, DriveName, FileSystem, TotalBytes,
                    UsedBytes, FreeBytes, StartedUtc, CompletedUtc,
                    Status, ScanFormat
                ) VALUES (
                    $scan, 'C:', 'Legacy fixture', 'NTFS', 1000,
                    300, 700, $now, $now, $completed, 2
                );
                INSERT INTO FolderEntries (
                    ScanId, FullPath, Name, ParentPath, SizeBytes,
                    DirectSizeBytes, FileCount, DirectFileCount, Depth,
                    Attributes, ModifiedUtc, Category, Tag
                ) VALUES
                    ($scan, 'C:\', 'C:', '', 300, 0, 3, 0, 0, 0, $now, 'Other', 'Folder'),
                    ($scan, 'C:\Users', 'Users', 'C:\', 300, 0, 3, 0, 1, 0, $now, 'Other', 'Folder'),
                    ($scan, 'C:\Users\Alex', 'Alex', 'C:\Users', 300, 0, 3, 0, 2, 0, $now, 'Other', 'Folder'),
                    ($scan, 'C:\Users\Alex\Cache', 'Cache', 'C:\Users\Alex', 200, 200, 2, 2, 3, 0, $now, 'Other', 'Cache'),
                    ($scan, 'C:\Users\Alex\Projects', 'Projects', 'C:\Users\Alex', 100, 100, 1, 1, 3, 0, $now, 'Other', 'Folder');
                INSERT INTO FileEntries (
                    ScanId, FullPath, Name, ParentPath, SizeBytes,
                    CreatedUtc, ModifiedUtc, AccessedUtc, Attributes,
                    Extension, Category, Tag
                ) VALUES
                    ($scan, 'C:\Users\Alex\Cache\large.bin', 'large.bin', 'C:\Users\Alex\Cache', 150, $now, $now, $now, 0, '.bin', 'Other', 'File'),
                    ($scan, 'C:\Users\Alex\Cache\small.bin', 'small.bin', 'C:\Users\Alex\Cache', 50, $now, $now, $now, 0, '.bin', 'Other', 'File'),
                    ($scan, 'C:\Users\Alex\Projects\build.bin', 'build.bin', 'C:\Users\Alex\Projects', 100, $now, $now, $now, 0, '.bin', 'Other', 'File');
                """;
            command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
            command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue(
                "$completed",
                (int)StorageScanStatus.Completed);
            await command.ExecuteNonQueryAsync();
        }

        var folders = await fixture.Repository.GetTopFoldersAsync(
            scanId,
            limit: 2);
        var files = await fixture.Repository.GetLargestFilesAsync(
            scanId,
            limit: 2);

        Assert.Equal(
            new[] { @"C:\Users\Alex\Cache", @"C:\Users\Alex\Projects" },
            folders.Select(folder => folder.Path));
        Assert.Equal(
            new[]
            {
                @"C:\Users\Alex\Cache\large.bin",
                @"C:\Users\Alex\Projects\build.bin",
            },
            files.Select(file => file.Path));
        Assert.All(files, file => Assert.False(file.IsFolder));
        var navigationItems =
            await fixture.Repository.GetChildrenForNavigationAsync(
                scanId,
                @"C:\Users\Alex\Cache",
                @"C:\Users\Alex\Cache\small.bin",
                limit: 1);
        Assert.Equal(2, navigationItems.Count);
        Assert.Contains(navigationItems, item =>
            item.Path == @"C:\Users\Alex\Cache\small.bin");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Repository_NavigationIncludesTargetBeyondInitialPage(
        int sortIndex)
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("a.bin", 16384);
        await fixture.WriteFileAsync("b.bin", 16384);
        await fixture.WriteFileAsync("c.bin", 16384);
        var target = await fixture.WriteFileAsync("z-target.bin", 1024);
        var outside = await fixture.WriteFileAsync(
            @"Other\outside.bin", 32768);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var items = await fixture.Repository.GetChildrenForNavigationAsync(
            scan.ScanId,
            fixture.ScanRoot,
            target,
            sortIndex,
            limit: 2);

        Assert.Equal(3, items.Count);
        Assert.Single(items, item => item.Path == target);
        Assert.All(items, item => Assert.Equal(
            fixture.ScanRoot,
            Path.GetDirectoryName(item.Path)));
        var outOfScope = await fixture.Repository.GetChildrenForNavigationAsync(
            scan.ScanId,
            fixture.ScanRoot,
            outside,
            sortIndex,
            limit: 2);
        Assert.Equal(2, outOfScope.Count);
        Assert.DoesNotContain(outOfScope, item => item.Path == outside);
    }

    [Fact]
    public async Task Scanner_IndexesChildrenAndAggregatesFolderSizes()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("Photos\\one.jpg", 10);
        await fixture.WriteFileAsync("Photos\\two.jpg", 20);
        await fixture.WriteFileAsync("Docs\\notes.txt", 30);

        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        Assert.True(
            result.Status == StorageScanStatus.Completed,
            result.ErrorMessage ?? "The scan did not complete.");
        Assert.Equal(3, result.FilesScanned);
        var rootChildren = await fixture.Repository.GetChildrenAsync(result.ScanId, fixture.ScanRoot);
        var rootNames = rootChildren.Select(item => item.Name).ToArray();
        Assert.Contains("Docs", rootNames);
        Assert.Contains("Photos", rootNames);
        var photos = rootChildren.Single(item => item.Name == "Photos");
        Assert.Equal(30, photos.SizeBytes);

        var photoChildren = await fixture.Repository.GetChildrenAsync(result.ScanId, photos.Path);
        Assert.Equal(2, photoChildren.Count);
        Assert.All(photoChildren, item => Assert.Equal("Cache & temporary", item.Category));
    }

    [Fact]
    public async Task Scanner_HandlesEmptyFolder()
    {
        await using var fixture = await ScanFixture.CreateAsync();

        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        Assert.Equal(StorageScanStatus.Completed, result.Status);
        Assert.Equal(0, result.FilesScanned);
    }

    [Fact]
    public async Task Scanner_HandlesOneFile()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("one.txt", 10);

        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        Assert.Equal(StorageScanStatus.Completed, result.Status);
        Assert.Equal(1, result.FilesScanned);
    }

    [Fact]
    public async Task Scanner_HandlesOneNestedFile()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("Nested\\one.txt", 10);

        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        Assert.Equal(StorageScanStatus.Completed, result.Status);
        Assert.Equal(1, result.FilesScanned);
        var root = await fixture.Repository.GetChildrenAsync(result.ScanId, fixture.ScanRoot);
        var nested = await fixture.Repository.GetChildrenAsync(result.ScanId, root[0].Path);
        Assert.Single(nested);
    }

    [Fact]
    public async Task Scanner_HandlesMultipleFiles()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("Photos\\one.jpg", 10);
        await fixture.WriteFileAsync("Photos\\two.jpg", 20);
        await fixture.WriteFileAsync("Docs\\notes.txt", 30);

        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        Assert.Equal(StorageScanStatus.Completed, result.Status);
        Assert.Equal(3, result.FilesScanned);
        var children = await fixture.Repository.GetChildrenAsync(
            result.ScanId,
            fixture.ScanRoot);
        Assert.Equal(2, children.Count);
    }

    [Fact]
    public async Task Scanner_HandlesTwoFilesInOneFolder()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("Photos\\one.jpg", 10);
        await fixture.WriteFileAsync("Photos\\two.jpg", 20);
        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var root = await fixture.Repository.GetChildrenAsync(result.ScanId, fixture.ScanRoot);
        var photos = root.Single();
        var children = await fixture.Repository.GetChildrenAsync(result.ScanId, photos.Path);
        Assert.Equal(2, children.Count);
        Assert.Equal(30, photos.SizeBytes);
    }

    [Fact]
    public async Task DuplicateDetection_IndexesOnlyOneCleanupCandidate()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var firstPath = await fixture.WriteRepeatedFileAsync("first.bin", 1_100_000, 42);
        var secondPath = await fixture.WriteRepeatedFileAsync("second.bin", 1_100_000, 42);

        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.LargeDuplicates);
        Assert.Equal(StorageScanStatus.Completed, result.Status);
        Assert.Single(candidates);
        Assert.Contains(candidates[0].Item.Path, new[] { firstPath, secondPath });
    }

    [Fact]
    public async Task CleanupCandidates_FolderSelectionIncludesDescendantFiles()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteRepeatedFileAsync(
            "Original\\first.bin",
            1_100_000,
            91);
        await fixture.WriteRepeatedFileAsync(
            "Copies\\second.bin",
            1_100_000,
            91);

        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var tree = await fixture.Candidates.GetCandidateFoldersAsync(
            fixture.Drive,
            CleanupPolicyId.LargeDuplicates);
        var scanFolder = tree
            .SelectMany(FlattenCandidateFolders)
            .Single(folder => string.Equals(
                folder.Path,
                fixture.ScanRoot,
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, scanFolder.DirectCandidateCount);
        Assert.Equal(1, scanFolder.CandidateCount);

        var files =
            await fixture.Candidates.GetCandidatesInFolderAsync(
                fixture.Drive,
                CleanupPolicyId.LargeDuplicates,
                scanFolder.Path);
        Assert.Single(files);
        Assert.StartsWith(
            scanFolder.Path + "\\",
            files[0].FileRecord.ParentPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanupSelection_ExcludesIndividualFileFromCleanup()
    {
        var scanId = Guid.NewGuid();
        var selection = new CleanupCandidateSelectionState();
        var candidate = CreateCleanupCandidate(
            scanId,
            CleanupPolicyId.TemporaryFiles,
            @"C:\Temp\Cache\keep.tmp",
            100);

        selection.SetFileSelected(candidate, false);

        Assert.False(selection.IsSelected(candidate));
        Assert.Empty(selection.FilterSelected([candidate]));
    }

    [Fact]
    public void CleanupSelection_FolderOverrideAppliesToLazyDescendants()
    {
        var scanId = Guid.NewGuid();
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles);
        var selection = new CleanupCandidateSelectionState();
        var descendant = CreateCleanupCandidate(
            scanId,
            CleanupPolicyId.TemporaryFiles,
            @"C:\Users\Alex\AppData\Local\Temp\Nested\lazy.tmp",
            200);

        selection.SetFolderSelected(
            recommendation,
            scanId,
            @"C:\Users\Alex\AppData\Local\Temp",
            false);

        Assert.False(selection.IsSelected(descendant));
    }

    [Fact]
    public void CleanupSelection_LaterChildOverridesParentExclusion()
    {
        var scanId = Guid.NewGuid();
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles);
        var selection = new CleanupCandidateSelectionState();
        var child = CreateCleanupCandidate(
            scanId,
            CleanupPolicyId.TemporaryFiles,
            @"C:\Temp\Nested\child.tmp",
            300);

        selection.SetFolderSelected(
            recommendation,
            scanId,
            @"C:\Temp",
            false);
        selection.SetFolderSelected(
            recommendation,
            scanId,
            @"C:\Temp\Nested",
            true);
        selection.SetFileSelected(child, true);

        Assert.True(selection.IsSelected(child));
    }

    [Fact]
    public void CleanupSelection_ReportsSelectedCountAndBytes()
    {
        var scanId = Guid.NewGuid();
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles);
        var selection = new CleanupCandidateSelectionState();
        var first = CreateCleanupCandidate(
            scanId,
            CleanupPolicyId.TemporaryFiles,
            @"C:\Temp\first.tmp",
            100);
        var folder = CleanupCandidateFolderNode.BuildTree(
            [
                new CleanupCandidateFolderSummary
                {
                    Path = @"C:\Temp",
                    DirectCandidateCount = 2,
                    DirectCandidateBytes = 300,
                },
            ]).SelectMany(FlattenCandidateFolders)
            .Single(node => node.Path == @"C:\Temp");
        selection.SetFileSelected(first, false);

        var summary =
            selection.GetFolderSelectionSummary(
                recommendation,
                scanId,
                folder);

        Assert.Equal(1, summary.SelectedCount);
        Assert.Equal(200, summary.SelectedBytes);
    }

    [Fact]
    public void CleanupSelection_ReportsRecursiveFolderTreeTotals()
    {
        var scanId = Guid.NewGuid();
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles,
            scanId,
            3,
            700);
        var selection = new CleanupCandidateSelectionState();
        var roots = CleanupCandidateFolderNode.BuildTree(
            [
                new CleanupCandidateFolderSummary
                {
                    Path = @"C:\Temp",
                    DirectCandidateCount = 1,
                    DirectCandidateBytes = 100,
                },
                new CleanupCandidateFolderSummary
                {
                    Path = @"C:\Temp\Nested",
                    DirectCandidateCount = 2,
                    DirectCandidateBytes = 600,
                },
            ]);
        selection.RegisterFolderTree(
            scanId,
            CleanupPolicyId.TemporaryFiles,
            roots);
        selection.SetFolderSelected(
            recommendation,
            scanId,
            @"C:\Temp",
            false);
        var child = CreateCleanupCandidate(
            scanId,
            CleanupPolicyId.TemporaryFiles,
            @"C:\Temp\Nested\keep.tmp",
            300);
        selection.SetFileSelected(child, true);

        var summary =
            selection.GetPolicySelectionSummary(recommendation);

        Assert.Equal(1, summary.SelectedCount);
        Assert.Equal(300, summary.SelectedBytes);
    }

    [Fact]
    public void CleanupSelection_RegisteredTreesSurviveCollectionReuse()
    {
        var scanId = Guid.NewGuid();
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles,
            scanId,
            2,
            300);
        var selection = new CleanupCandidateSelectionState();
        var roots = CleanupCandidateFolderNode.BuildTree(
            [
                new CleanupCandidateFolderSummary
                {
                    Path = @"C:\Temp",
                    DirectCandidateCount = 2,
                    DirectCandidateBytes = 300,
                },
            ],
            scanId).ToList();
        selection.RegisterFolderTree(
            scanId,
            recommendation.PolicyId,
            roots);
        selection.SetFolderSelected(
            recommendation,
            scanId,
            @"C:\Temp",
            false);

        roots.Clear();
        roots.AddRange(CleanupCandidateFolderNode.BuildTree(
            [
                new CleanupCandidateFolderSummary
                {
                    Path = @"C:\OtherCache",
                    DirectCandidateCount = 5,
                    DirectCandidateBytes = 900,
                },
            ],
            scanId));
        selection.RegisterFolderTree(
            scanId,
            CleanupPolicyId.PackageCaches,
            roots);

        var summary = selection.GetPolicySelectionSummary(recommendation);
        Assert.Equal(0, summary.SelectedCount);
        Assert.Equal(0, summary.SelectedBytes);
    }

    [Fact]
    public void CleanupSelection_SnapshotIsUnaffectedByLaterOverrides()
    {
        var scanId = Guid.NewGuid();
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles,
            scanId);
        var candidate = CreateCleanupCandidate(
            scanId,
            recommendation.PolicyId,
            @"C:\Temp\Nested\keep.tmp",
            100);
        var selection = new CleanupCandidateSelectionState();
        selection.SetFolderSelected(
            recommendation,
            scanId,
            @"C:\Temp",
            false);
        selection.SetFileSelected(candidate, true);

        var snapshot = selection.CreateSnapshot();
        selection.SetFileSelected(candidate, false);

        Assert.True(snapshot.IsSelected(candidate));
        Assert.False(selection.IsSelected(candidate));
        snapshot.SetFolderSelected(
            recommendation,
            scanId,
            @"C:\Temp",
            true);
        Assert.False(selection.IsSelected(candidate));
    }

    [Fact]
    public void CleanupSelection_AggregatesIndependentDriveScopes()
    {
        var firstScan = Guid.NewGuid();
        var secondScan = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles,
            itemCount: 3,
            reclaimableBytes: 1000,
            sources:
            [
                new CleanupScanSource(firstScan, "C:", now, 2, 300),
                new CleanupScanSource(secondScan, "Q:", now, 1, 700),
            ]);
        var selection = new CleanupCandidateSelectionState();
        foreach (var source in recommendation.Sources)
        {
            selection.RegisterFolderTree(
                source.ScanId,
                recommendation.PolicyId,
                CleanupCandidateFolderNode.BuildTree(
                    [
                        new CleanupCandidateFolderSummary
                        {
                            Path = source.DriveLetter + @"\Temp",
                            DirectCandidateCount = source.ItemCount,
                            DirectCandidateBytes = source.SizeBytes,
                        },
                    ],
                    source.ScanId));
        }

        selection.SetAllSelected(recommendation, false);
        selection.SetFolderSelected(
            recommendation,
            firstScan,
            @"C:\Temp",
            true);
        selection.SetFileSelected(
            CreateCleanupCandidate(
                firstScan,
                recommendation.PolicyId,
                @"C:\Temp\first.tmp",
                100),
            false);

        var summary = selection.GetPolicySelectionSummary(recommendation);
        Assert.Equal(1, summary.SelectedCount);
        Assert.Equal(200, summary.SelectedBytes);
        selection.SetFolderSelected(
            recommendation,
            secondScan,
            @"Q:\Temp",
            true);
        summary = selection.GetPolicySelectionSummary(recommendation);
        Assert.Equal(2, summary.SelectedCount);
        Assert.Equal(900, summary.SelectedBytes);
    }

    [Fact]
    public void CleanupRecommendation_ReviewSnapshotIsIsolated()
    {
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.PackageCaches,
            Guid.NewGuid(),
            500,
            10_000);
        var snapshot = recommendation.CreateReviewSnapshot();

        snapshot.UpdateCandidateTotals(1, 100);
        snapshot.UpdateSelection(1, 100);

        Assert.Equal(500, recommendation.ItemCount);
        Assert.Equal(10_000, recommendation.ReclaimableBytes);
        Assert.Equal(1, snapshot.ItemCount);
        Assert.Equal(100, snapshot.ReclaimableBytes);
    }

    [Fact]
    public async Task GrowthQuery_UsesFirstSeenBaselineAcrossScans()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("GrowingFolder\\initial.bin", 10);
        var firstScan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        await fixture.WriteFileAsync("GrowingFolder\\added.bin", 50);

        var secondScan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var growth = await fixture.Repository.GetGrowingFoldersAsync(
            fixture.Drive.Letter,
            1);
        var folder = Assert.Single(growth, item => item.Name == "GrowingFolder");
        Assert.True(folder.AddedBytes >= 50);
        Assert.Equal(
            StorageScanStatus.Completed,
            firstScan.Status);
        Assert.Equal(
            StorageScanStatus.Completed,
            secondScan.Status);
        await fixture.WriteFileAsync("GrowingFolder\\latest.bin", 5);

        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var latestGrowth = await fixture.Repository.GetGrowingFoldersAsync(
            fixture.Drive.Letter,
            1);
        var latestFolder = Assert.Single(
            latestGrowth,
            item => item.Name == "GrowingFolder");
        Assert.True(latestFolder.AddedBytes > folder.AddedBytes);

        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var unchangedGrowth = await fixture.Repository.GetGrowingFoldersAsync(
            fixture.Drive.Letter,
            1);
        var unchangedFolder = Assert.Single(
            unchangedGrowth,
            item => item.Name == "GrowingFolder");
        Assert.Equal(latestFolder.AddedBytes, unchangedFolder.AddedBytes);
        await using var connection = new SqliteConnection(
            $"Data Source={fixture.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(DISTINCT RunKey) FROM FileNames),
                (SELECT COUNT(DISTINCT RunKey) FROM FolderNodes)
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1));
    }

    [Fact]
    public async Task GrowthBaseline_IsResetOnRecreationAndRemovedOnDeletion()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var folderPath = Path.Combine(fixture.ScanRoot, "Lifecycle");
        await fixture.WriteFileAsync(
            "Lifecycle\\initial.bin",
            20);
        var first = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var original = await ReadFolderBaselineAsync(
            fixture.Repository,
            first.ScanId,
            "Lifecycle");
        Assert.NotNull(original);

        Directory.Delete(folderPath, true);
        await Task.Delay(20);
        await fixture.WriteFileAsync(
            "Lifecycle\\replacement.bin",
            80);
        var recreatedScan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var recreated = await ReadFolderBaselineAsync(
            fixture.Repository,
            recreatedScan.ScanId,
            "Lifecycle");
        Assert.NotNull(recreated);
        Assert.Equal(original.PathHash, recreated.PathHash);
        Assert.True(recreated.FirstSeenUtc > original.FirstSeenUtc);
        Assert.Equal(
            recreated.AllocatedSize,
            recreated.BaselineAllocatedSize);
        Assert.DoesNotContain(
            await fixture.Repository.GetGrowingFoldersAsync(
                fixture.Drive.Letter,
                1),
            item => item.Name == "Lifecycle");

        Directory.Delete(folderPath, true);
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        await using var connection = new SqliteConnection(
            $"Data Source={fixture.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM FolderGrowthBaselines
            WHERE PathHash = $hash
            """;
        command.Parameters.AddWithValue("$hash", recreated.PathHash);
        Assert.Equal(
            0,
            Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ExplorerQueries_ExposePositiveFolderGrowth()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync(
            "GrowingFolder\\initial.bin",
            10);
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        await fixture.WriteFileAsync(
            "GrowingFolder\\additional.bin",
            40);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var folderPath = Path.Combine(
            fixture.ScanRoot,
            "GrowingFolder");

        var treeNode = Assert.Single(
            await fixture.Repository.GetFolderChildrenAsync(
                scan.ScanId,
                fixture.ScanRoot));
        Assert.True(treeNode.HasGrowth);
        Assert.True(treeNode.GrowthBytes > 0);
        Assert.NotNull(treeNode.GrowthBaselineUtc);

        var child = Assert.Single(
            await fixture.Repository.GetChildrenAsync(
                scan.ScanId,
                fixture.ScanRoot),
            item => item.IsFolder);
        Assert.True(child.HasGrowth);
        Assert.True(child.GrowthBytes > 0);

        var folderNode = await fixture.Repository.GetFolderNodeAsync(
            scan.ScanId,
            folderPath);
        Assert.NotNull(folderNode);
        Assert.True(folderNode.HasGrowth);
        Assert.Equal(treeNode.GrowthBytes, folderNode.GrowthBytes);

        var folderItem = await fixture.Repository.GetItemByPathAsync(
            scan.ScanId,
            folderPath);
        Assert.NotNull(folderItem);
        Assert.True(folderItem.HasGrowth);
        Assert.Equal(treeNode.GrowthBytes, folderItem.GrowthBytes);

        var searchItem = Assert.Single(
            await fixture.Repository.SearchAsync(
                scan.ScanId,
                "GrowingFolder"),
            item => item.IsFolder);
        Assert.True(searchItem.HasGrowth);
        Assert.Equal(treeNode.GrowthBytes, searchItem.GrowthBytes);
    }

    [Fact]
    public void CleanupPolicy_RejectsSystemAndCloudOnlyFiles()
    {
        var baseRecord = new StorageFileRecord
        {
            ScanId = Guid.NewGuid(),
            Name = "cache.tmp",
            FullPath = Path.Combine(Path.GetTempPath(), "cache.tmp"),
            ParentPath = Path.GetTempPath(),
            SizeBytes = 10,
            CreatedUtc = DateTimeOffset.Now.AddDays(-2),
            ModifiedUtc = DateTimeOffset.Now.AddDays(-2),
            AccessedUtc = DateTimeOffset.Now.AddDays(-2),
            Attributes = (long)FileAttributes.Normal,
            Extension = ".tmp",
            Category = "Cache & temporary",
            Tag = "Cache",
        };

        var systemRecord = new StorageFileRecord
        {
            ScanId = baseRecord.ScanId,
            Name = baseRecord.Name,
            FullPath = baseRecord.FullPath,
            ParentPath = baseRecord.ParentPath,
            SizeBytes = baseRecord.SizeBytes,
            CreatedUtc = baseRecord.CreatedUtc,
            ModifiedUtc = baseRecord.ModifiedUtc,
            AccessedUtc = baseRecord.AccessedUtc,
            Attributes = (long)FileAttributes.System,
            Extension = baseRecord.Extension,
            Category = baseRecord.Category,
            Tag = baseRecord.Tag,
        };
        var cloudRecord = new StorageFileRecord
        {
            ScanId = baseRecord.ScanId,
            Name = baseRecord.Name,
            FullPath = baseRecord.FullPath,
            ParentPath = baseRecord.ParentPath,
            SizeBytes = baseRecord.SizeBytes,
            CreatedUtc = baseRecord.CreatedUtc,
            ModifiedUtc = baseRecord.ModifiedUtc,
            AccessedUtc = baseRecord.AccessedUtc,
            Attributes = 0x00400000,
            ReparseTag = StorageReparseTags.CloudFiles,
            Extension = baseRecord.Extension,
            Category = baseRecord.Category,
            Tag = baseRecord.Tag,
        };

        Assert.False(CleanupPolicyService.Evaluate(
            systemRecord,
            CleanupPolicyId.TemporaryFiles,
            DateTimeOffset.Now,
            true).IsAllowed);
        Assert.False(CleanupPolicyService.Evaluate(
            cloudRecord,
            CleanupPolicyId.TemporaryFiles,
            DateTimeOffset.Now,
            false).IsAllowed);
        Assert.True(CleanupPolicyService.Evaluate(
            baseRecord,
            CleanupPolicyId.TemporaryFiles,
            DateTimeOffset.Now,
            true).IsAllowed);
    }

    [Fact]
    public void CleanupPolicy_PackageCachesAreManualAndAllowlisted()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var root in CleanupPolicyService.GetPackageCacheRoots())
        {
            var record = CreateRecord(
                Guid.NewGuid(),
                root,
                Path.Combine(root, "review-cache.bin"),
                100,
                now.AddDays(-2));
            Assert.True(CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.PackageCaches,
                now,
                false).IsAllowed);
            Assert.False(CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.PackageCaches,
                now,
                true).IsAllowed);
        }

        foreach (var path in new[]
                 {
                     @"C:\Windows\Installer\keep.msi",
                     @"C:\ProgramData\Package Cache\keep.msi",
                     Path.Combine(
                         Environment.GetFolderPath(
                             Environment.SpecialFolder.UserProfile),
                         "Documents",
                         "keep.bin"),
                 })
        {
            var record = CreateRecord(
                Guid.NewGuid(),
                Path.GetDirectoryName(path)!,
                path,
                100,
                now.AddDays(-2));
            Assert.False(CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.PackageCaches,
                now,
                false).IsAllowed);
        }
    }

    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(48, true)]
    public void CleanupPolicy_PackageCachesRespectAgeBoundary(
        int hoursOld,
        bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var root = CleanupPolicyService.GetPackageCacheRoots()[0];
        var record = CreateRecord(
            Guid.NewGuid(),
            root,
            Path.Combine(root, "review-cache.bin"),
            100,
            now.AddHours(-hoursOld));

        Assert.Equal(
            expected,
            CleanupPolicyService.Evaluate(
                record,
                CleanupPolicyId.PackageCaches,
                now,
                false).IsAllowed);
    }

    [Fact]
    public async Task CleanupCandidates_PackageMetricsMatchPinnedEligibleFiles()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var root = Path.Combine(
            CleanupPolicyService.GetPackageCacheRoots()[0],
            "StorageLensTests",
            Guid.NewGuid().ToString("N"));
        await using var session = await fixture.Repository.BeginScanAsync(
            fixture.Drive);
        await session.InsertFilesAsync(
            [
                CreateRecord(
                    session.ScanId,
                    root,
                    Path.Combine(root, "old-cache.bin"),
                    100,
                    now.AddHours(-25)),
                CreateRecord(
                    session.ScanId,
                    root,
                    Path.Combine(root, "recent-cache.bin"),
                    200,
                    now.AddHours(-23)),
            ],
            CancellationToken.None);
        await session.CompleteAsync(2, 1, 300, 0, CancellationToken.None);
        var source = new CleanupScanSource(
            session.ScanId,
            fixture.Drive.Letter,
            now,
            2,
            300);

        var candidates = await fixture.Candidates.GetCandidatesAsync(
            source,
            CleanupPolicyId.PackageCaches);
        var candidate = Assert.Single(candidates);
        Assert.EndsWith("old-cache.bin", candidate.Item.Path);
        Assert.True(candidate.IsSelectionEnabled);
        Assert.False(candidate.IsEligibleForAutomaticCleanup);
        Assert.Equal(
            1,
            await fixture.Repository.GetCleanupCandidateCountAsync(
                source.ScanId,
                CleanupPolicyId.PackageCaches,
                now));
        var folders = await fixture.Candidates.GetCandidateFoldersAsync(
            source,
            CleanupPolicyId.PackageCaches);
        Assert.Equal(1, Assert.Single(folders).CandidateCount);
        var summaries = await fixture.Repository.GetCleanupSummariesAsync(
            source.ScanId,
            now);
        Assert.Equal(
            1,
            summaries.Single(item =>
                item.PolicyId == CleanupPolicyId.PackageCaches).ItemCount);
        Assert.Empty(await fixture.Candidates.GetCandidatesAsync(
            source with { EvaluatedUtc = now.AddHours(-2) },
            CleanupPolicyId.PackageCaches));
    }

    [Fact]
    public void CleanupPolicy_RequiresWindowsManagedCleanupForWindowsOld()
    {
        var record = CreateRecord(
            Guid.NewGuid(),
            @"C:\Windows.old\Windows\System32",
            @"C:\Windows.old\Windows\System32\example.dll",
            1024);

        var policy = CleanupPolicyService.Evaluate(
            record,
            CleanupPolicyId.PreviousWindowsInstallation,
            DateTimeOffset.Now,
            false);

        Assert.False(policy.IsAllowed);
        Assert.Contains("Storage Sense", policy.Reason);
        var definition = CleanupPolicyService.GetDefinition(
            CleanupPolicyId.PreviousWindowsInstallation);
        Assert.Equal(CleanupSafety.Caution, definition.Safety);
    }

    [Fact]
    public void RecycleBin_IsAvailableForSystemDrive()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.True(RecycleBinService.CanRecycle(root));
    }

    [Fact]
    public async Task RecycleBin_MovesAndRestoresATemporaryFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"StorageLens-Recycle-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "Storage Lens recycle test");
        try
        {
            await using var worker = new CleanupExecutionWorker();
            await worker.RunAsync(() => RecycleBinService.Recycle(path));
            Assert.False(File.Exists(path));
            RestoreFromRecycleBin(Path.GetFileName(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task RecycleBin_MovesMultipleTemporaryFilesInOneBatch()
    {
        var paths = Enumerable.Range(0, 2)
            .Select(_ => Path.Combine(
                Path.GetTempPath(),
                $"StorageLens-BatchRecycle-{Guid.NewGuid():N}.txt"))
            .ToArray();
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, "Storage Lens batch recycle test");
        }

        try
        {
            await using var worker = new CleanupExecutionWorker();
            var results = await worker.RunAsync(() =>
                RecycleBinService.RecycleBatch(paths, showProgressUi: false));

            Assert.Equal(2, results.Count);
            Assert.All(results, result => Assert.True(result.Succeeded));
            Assert.All(paths, path => Assert.False(File.Exists(path)));
            foreach (var path in paths)
            {
                RestoreFromRecycleBin(Path.GetFileName(path));
            }

            Assert.All(paths, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    RestoreFromRecycleBin(Path.GetFileName(path));
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Cleanup_UsesOneShellBatchAndSelectsUiFromAutomationMode(
        bool isAutomatic,
        bool expectedShowProgressUi)
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var paths = new[]
        {
            await fixture.WriteFileAsync(
                "batch-first.tmp", 10, setOldTimestamp: true),
            await fixture.WriteFileAsync(
                "batch-second.tmp", 20, setOldTimestamp: true),
        };
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.TemporaryFiles);
        var batchCount = 0;
        var recycledPaths = Array.Empty<string>();
        var showProgressUi = false;
        nint ownerWindowHandle = 0;
        var cleanup = new CleanupService(
            fixture.Repository,
            (batch, shellUi, owner) =>
            {
                batchCount++;
                recycledPaths = batch.ToArray();
                showProgressUi = shellUi;
                ownerWindowHandle = owner;
                foreach (var path in batch)
                {
                    File.Delete(path);
                }

                return batch.Select(path => new RecycleBinItemResult
                {
                    Path = path,
                    Succeeded = true,
                }).ToArray();
            });

        var result = await cleanup.RecycleAsync(new CleanupRequest
        {
            Candidates = candidates,
            IsAutomatic = isAutomatic,
            OwnerWindowHandle = 1234,
        });

        Assert.Equal(1, batchCount);
        Assert.Equal(expectedShowProgressUi, showProgressUi);
        Assert.Equal(1234, ownerWindowHandle);
        Assert.Equal(paths.Order(), recycledPaths.Order());
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task Cleanup_FinalizesMixedShellBatchResultsIndependently()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var succeededPath = await fixture.WriteFileAsync(
            "batch-succeeded.tmp", 10, setOldTimestamp: true);
        var failedPath = await fixture.WriteFileAsync(
            "batch-failed.tmp", 20, setOldTimestamp: true);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.TemporaryFiles);
        var cleanup = new CleanupService(
            fixture.Repository,
            (paths, _) =>
            {
                File.Delete(succeededPath);
                return paths.Select(path =>
                    path == succeededPath
                        ? new RecycleBinItemResult
                        {
                            Path = path,
                            Succeeded = true,
                        }
                        : new RecycleBinItemResult
                        {
                            Path = path,
                            HResult = unchecked((int)0x80070020),
                            Error = "The file is in use.",
                        })
                    .ToArray();
            });

        var result = await cleanup.RecycleAsync(new CleanupRequest
        {
            Candidates = candidates,
        });

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.RemainingIndexedCount);
        Assert.False(File.Exists(succeededPath));
        Assert.True(File.Exists(failedPath));
        Assert.Null(await fixture.Repository.GetItemByPathAsync(
            scan.ScanId,
            succeededPath));
        Assert.NotNull(await fixture.Repository.GetItemByPathAsync(
            scan.ScanId,
            failedPath));
        var history = await fixture.Repository.GetCleanupHistoryAsync();
        Assert.Contains(history, item =>
            item.Path == succeededPath && item.Result == "Recycled");
        Assert.Contains(history, item =>
            item.Path == failedPath &&
            item.Result == "Failed" &&
            item.Error == "The file is in use.");
    }

    [Fact]
    public async Task Cleanup_SkipsAFileThatChangedAfterScan()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("changed.tmp", 10, setOldTimestamp: true);
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)9, 20).ToArray());
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.TemporaryFiles);
        var recycled = 0;
        var cleanup = new CleanupService(
            fixture.Repository,
            _ => recycled++);

        var result = await cleanup.RecycleAsync(new CleanupRequest
        {
            Candidates = candidates,
        });

        Assert.Equal(0, recycled);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(path));
        Assert.Empty(
            await fixture.Candidates.GetCandidatesAsync(
                fixture.Drive,
                CleanupPolicyId.TemporaryFiles));
    }

    [Fact]
    public async Task Cleanup_SkipsAReplacementWithTheSameSizeAndTimestamp()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("replacement.tmp", 10, setOldTimestamp: true);
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.TemporaryFiles);
        var candidate = Assert.Single(candidates);
        var originalPath = path + ".original";
        File.Move(path, originalPath);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)7, 10).ToArray());
        File.SetLastWriteTimeUtc(path, candidate.FileRecord.ModifiedUtc.UtcDateTime);
        var recycled = 0;
        var cleanup = new CleanupService(
            fixture.Repository,
            _ => recycled++);

        var result = await cleanup.RecycleAsync(new CleanupRequest
        {
            Candidates = candidates,
        });

        Assert.Equal(0, recycled);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Cleanup_SkipsADuplicateWhoseContentChangedAfterFingerprinting()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var firstPath = await fixture.WriteRepeatedFileAsync("first.bin", 1_100_000, 17);
        var secondPath = await fixture.WriteRepeatedFileAsync("second.bin", 1_100_000, 17);
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.LargeDuplicates);
        var candidate = Assert.Single(candidates);
        await File.WriteAllBytesAsync(
            candidate.Item.Path,
            Enumerable.Repeat((byte)23, 1_100_000).ToArray());
        File.SetLastWriteTimeUtc(
            candidate.Item.Path,
            candidate.FileRecord.ModifiedUtc.UtcDateTime);
        var recycled = 0;
        var cleanup = new CleanupService(
            fixture.Repository,
            _ => recycled++);

        var result = await cleanup.RecycleAsync(new CleanupRequest
        {
            Candidates = candidates,
        });

        Assert.Equal(0, recycled);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task Cleanup_RemovesEligibleItemFromIndexAndWritesAudit()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("safe.tmp", 10, setOldTimestamp: true);
        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.TemporaryFiles);
        var candidateIdentity = Assert.Single(candidates).FileRecord;
        var actualIdentity = FileIdentityService.TryGetIdentity(path);
        Assert.NotNull(actualIdentity);
        Assert.Equal(candidateIdentity.VolumeSerialNumber, actualIdentity.VolumeSerialNumber);
        Assert.Equal(
            candidateIdentity.FileId128,
            actualIdentity.FileId128,
            ignoreCase: true);
        var cleanup = new CleanupService(
            fixture.Repository,
            File.Delete);

        var cleanupResult = await cleanup.RecycleAsync(new CleanupRequest
        {
            Candidates = candidates,
        });

        Assert.True(
            cleanupResult.SucceededCount == 1,
            string.Join("; ", cleanupResult.Failures.Select(
                failure => $"{failure.Path}: {failure.Error}")));
        Assert.False(File.Exists(path));
        Assert.Null(await fixture.Repository.GetItemByPathAsync(result.ScanId, path));
        var history = await fixture.Repository.GetCleanupHistoryAsync();
        Assert.Single(history);
        Assert.Equal("Recycled", history[0].Result);
    }

    [Fact]
    public async Task Cleanup_RecordsUnsupportedRecyclerAsFailure()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var path = await fixture.WriteFileAsync(
            "unsupported.tmp",
            10,
            setOldTimestamp: true);
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.TemporaryFiles);
        var cleanup = new CleanupService(
            fixture.Repository,
            _ => throw new NotSupportedException(
                "Recycle Bin interop is unavailable."));

        var result = await cleanup.RecycleAsync(new CleanupRequest
        {
            Candidates = candidates,
        });

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.RemainingIndexedCount);
        Assert.True(File.Exists(path));
        var history = await fixture.Repository.GetCleanupHistoryAsync();
        Assert.Single(history);
        Assert.Equal("Failed", history[0].Result);
        Assert.Contains("interop", history[0].Error);
    }

    [Fact]
    public async Task CleanupBatch_UsesOneBackgroundStaThreadAndReportsProgress()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var paths = new[]
        {
            await fixture.WriteFileAsync(
                "first.tmp",
                10,
                setOldTimestamp: true),
            await fixture.WriteFileAsync(
                "second.tmp",
                20,
                setOldTimestamp: true),
        };
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var callerThreadId = Environment.CurrentManagedThreadId;
        var workerThreadIds = new List<int>();
        var apartments = new List<ApartmentState>();
        var progressUpdates = new List<CleanupProgress>();
        var batch = new CleanupBatchService(
            fixture.Candidates,
            new CleanupService(fixture.Repository, path =>
            {
                workerThreadIds.Add(Environment.CurrentManagedThreadId);
                apartments.Add(Thread.CurrentThread.GetApartmentState());
                File.Delete(path);
            }));

        var result = await batch.RecycleAsync(
            [new CleanupBatchScope(
                new CleanupScanSource(
                    scan.ScanId,
                    fixture.Drive.Letter,
                    DateTimeOffset.UtcNow,
                    2,
                    30),
                CleanupPolicyId.TemporaryFiles)],
            new CleanupCandidateSelectionState(),
            new CallbackProgress<CleanupProgress>(progressUpdates.Add));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(2, workerThreadIds.Count);
        Assert.Single(workerThreadIds.Distinct());
        Assert.DoesNotContain(callerThreadId, workerThreadIds);
        Assert.All(apartments, apartment =>
            Assert.Equal(ApartmentState.STA, apartment));
        Assert.NotEmpty(progressUpdates);
        Assert.Equal(0, progressUpdates[0].Processed);
        Assert.Equal(2, progressUpdates[0].Total);
        Assert.Contains(
            progressUpdates,
            update => paths.Contains(
                update.CurrentPath,
                StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2, progressUpdates[^1].Processed);
    }

    [Fact]
    public async Task CleanupBatch_RecyclesOnlySelectedFolderDescendants()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var first = await fixture.WriteFileAsync(
            @"Selected\first.tmp", 10, setOldTimestamp: true);
        var nested = await fixture.WriteFileAsync(
            @"Selected\Nested\child.tmp", 20, setOldTimestamp: true);
        var keep = await fixture.WriteFileAsync(
            @"Selected\Nested\keep.tmp", 30, setOldTimestamp: true);
        var prefixSibling = await fixture.WriteFileAsync(
            @"SelectedOther\outside.tmp", 40, setOldTimestamp: true);
        var other = await fixture.WriteFileAsync(
            @"Other\outside.tmp", 50, setOldTimestamp: true);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var source = new CleanupScanSource(
            scan.ScanId,
            fixture.Drive.Letter,
            DateTimeOffset.UtcNow,
            5,
            150);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            source,
            CleanupPolicyId.TemporaryFiles);
        var selection = new CleanupCandidateSelectionState();
        selection.SetFileSelected(
            candidates.Single(item => item.Item.Path == keep),
            false);
        var recycled = new List<string>();
        var batch = new CleanupBatchService(
            fixture.Candidates,
            new CleanupService(fixture.Repository, path =>
            {
                recycled.Add(path);
                File.Delete(path);
            }));

        var result = await batch.RecycleAsync(
            [new CleanupBatchScope(
                source,
                CleanupPolicyId.TemporaryFiles,
                Path.Combine(fixture.ScanRoot, "Selected"))],
            selection);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.ExcludedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Contains(first, recycled);
        Assert.Contains(nested, recycled);
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(prefixSibling));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task CleanupBatch_RecyclesOldDownloadsFromSelectedFolder()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            $"StorageLensCleanupTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloads);
        var path = Path.Combine(downloads, "old-download.txt");
        await File.WriteAllTextAsync(path, "Storage Lens cleanup test");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-91));
        try
        {
            var scan = await fixture.Scanner.ScanTestRootAsync(
                fixture.Drive,
                downloads);
            var source = new CleanupScanSource(
                scan.ScanId,
                fixture.Drive.Letter,
                DateTimeOffset.UtcNow,
                1,
                new FileInfo(path).Length);
            var folders = await fixture.Candidates.GetCandidateFoldersAsync(
                source,
                CleanupPolicyId.OldDownloads);
            var folder = Assert.Single(
                folders.SelectMany(FlattenCandidateFolders),
                item => item.Path == downloads);
            Assert.Equal(1, folder.CandidateCount);
            var batch = new CleanupBatchService(
                fixture.Candidates,
                new CleanupService(fixture.Repository, File.Delete));

            var result = await batch.RecycleAsync(
                [new CleanupBatchScope(
                    source,
                    CleanupPolicyId.OldDownloads,
                    folder.Path)],
                new CleanupCandidateSelectionState());

            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(0, result.ExcludedCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.False(File.Exists(path));
            var history = await fixture.Repository.GetCleanupHistoryAsync();
            Assert.Single(history);
            Assert.Equal("Recycled", history[0].Result);
        }
        finally
        {
            if (Directory.Exists(downloads))
            {
                Directory.Delete(downloads, true);
            }
        }
    }

    [Fact]
    public async Task CleanupBatch_HandlesRemovedRowsAndExclusionsAcrossPages()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session = await fixture.Repository.BeginScanAsync(
            fixture.Drive);
        var now = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 1002)
            .Select(index => CreateRecord(
                session.ScanId,
                fixture.ScanRoot,
                Path.Combine(fixture.ScanRoot, $"missing-{index:D4}.tmp"),
                100,
                now.AddDays(-2)))
            .ToArray();
        await session.InsertFilesAsync(records, CancellationToken.None);
        await session.CompleteAsync(1002, 1, 100200, 0, CancellationToken.None);
        var source = new CleanupScanSource(
            session.ScanId,
            fixture.Drive.Letter,
            now,
            1002,
            100200);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            source,
            CleanupPolicyId.TemporaryFiles);
        var selection = new CleanupCandidateSelectionState();
        selection.SetFileSelected(candidates[0], false);
        var batch = new CleanupBatchService(
            fixture.Candidates,
            new CleanupService(fixture.Repository, _ =>
                throw new InvalidOperationException(
                    "Missing files must never reach the recycle action.")));

        var result = await batch.RecycleAsync(
            [new CleanupBatchScope(source, CleanupPolicyId.TemporaryFiles)],
            selection);

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, result.ExcludedCount);
        Assert.Equal(1001, result.SkippedCount);
        Assert.Equal(
            1,
            await fixture.Candidates.GetCandidateCountAsync(
                fixture.Drive,
                CleanupPolicyId.TemporaryFiles));
    }

    [Fact]
    public async Task CleanupBatch_DoesNotFollowANewerScan()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var original = await fixture.WriteFileAsync(
            "original.tmp", 10, setOldTimestamp: true);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var source = new CleanupScanSource(
            scan.ScanId,
            fixture.Drive.Letter,
            DateTimeOffset.UtcNow,
            1,
            10);
        var added = await fixture.WriteFileAsync(
            "added.tmp", 20, setOldTimestamp: true);
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var batch = new CleanupBatchService(
            fixture.Candidates,
            new CleanupService(fixture.Repository, File.Delete));

        var result = await batch.RecycleAsync(
            [new CleanupBatchScope(source, CleanupPolicyId.TemporaryFiles)],
            new CleanupCandidateSelectionState());

        Assert.Equal(0, result.SucceededCount);
        Assert.True(File.Exists(original));
        Assert.True(File.Exists(added));
    }

    [Fact]
    public async Task CleanupBatch_KeepsTheOriginalSelectionAcrossScopes()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var first = await fixture.WriteFileAsync(
            @"First\one.tmp", 10, setOldTimestamp: true);
        var second = await fixture.WriteFileAsync(
            @"Second\two.tmp", 20, setOldTimestamp: true);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var source = new CleanupScanSource(
            scan.ScanId,
            fixture.Drive.Letter,
            DateTimeOffset.UtcNow,
            1,
            20);
        var recommendation = CreateCleanupRecommendation(
            CleanupPolicyId.TemporaryFiles,
            scan.ScanId);
        var selection = new CleanupCandidateSelectionState();
        var secondFolder = Path.GetDirectoryName(second)!;
        var batch = new CleanupBatchService(
            fixture.Candidates,
            new CleanupService(fixture.Repository, path =>
            {
                selection.SetFolderSelected(
                    recommendation,
                    scan.ScanId,
                    secondFolder,
                    false);
                File.Delete(path);
            }));

        var result = await batch.RecycleAsync(
            [
                new CleanupBatchScope(
                    source,
                    recommendation.PolicyId,
                    Path.GetDirectoryName(first)),
                new CleanupBatchScope(
                    source,
                    recommendation.PolicyId,
                    secondFolder),
            ],
            selection);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.ExcludedCount);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task CleanupBatch_ReportsFailuresAndContinuesOtherFiles()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var failed = await fixture.WriteFileAsync(
            "failed.tmp", 10, setOldTimestamp: true);
        var succeeded = await fixture.WriteFileAsync(
            "succeeded.tmp", 20, setOldTimestamp: true);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var batch = new CleanupBatchService(
            fixture.Candidates,
            new CleanupService(fixture.Repository, path =>
            {
                if (path == failed)
                {
                    throw new IOException("The fixture file is locked.");
                }

                File.Delete(path);
            }));

        var result = await batch.RecycleAsync(
            [new CleanupBatchScope(
                new CleanupScanSource(
                    scan.ScanId,
                    fixture.Drive.Letter,
                    DateTimeOffset.UtcNow,
                    2,
                    30),
                CleanupPolicyId.TemporaryFiles)],
            new CleanupCandidateSelectionState());

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.RemainingIndexedCount);
        Assert.True(File.Exists(failed));
        Assert.False(File.Exists(succeeded));
        Assert.Equal(failed, Assert.Single(result.Failures).Path);
        Assert.Equal(
            1,
            await fixture.Candidates.GetCandidateCountAsync(
                fixture.Drive,
                CleanupPolicyId.TemporaryFiles));
    }

    [Fact]
    public async Task CleanupBatch_CancellationLeavesRemainingFilesUntouched()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync(
            "first.tmp", 10, setOldTimestamp: true);
        await fixture.WriteFileAsync(
            "second.tmp", 20, setOldTimestamp: true);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        using var cancellation = new CancellationTokenSource();
        var batch = new CleanupBatchService(
            fixture.Candidates,
            new CleanupService(fixture.Repository, path =>
            {
                File.Delete(path);
                cancellation.Cancel();
            }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batch.RecycleAsync(
                [new CleanupBatchScope(
                    new CleanupScanSource(
                        scan.ScanId,
                        fixture.Drive.Letter,
                        DateTimeOffset.UtcNow,
                        2,
                        30),
                    CleanupPolicyId.TemporaryFiles)],
                new CleanupCandidateSelectionState(),
                cancellationToken: cancellation.Token));

        Assert.Single(Directory.GetFiles(fixture.ScanRoot));
        Assert.Equal(
            1,
            await fixture.Candidates.GetCandidateCountAsync(
                fixture.Drive,
                CleanupPolicyId.TemporaryFiles));
    }

    [Fact]
    public void ScheduledTask_IsDueUsesEnabledStateAndNextRun()
    {
        var now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var task = new ScheduledCleanupTask
        {
            Name = "Daily review",
            IntervalDays = 1,
            Categories = ["Cache & temporary"],
            IsEnabled = true,
            LastRun = now.AddDays(-2),
        };

        Assert.True(task.IsDue(now));
        task.IsEnabled = false;
        Assert.False(task.IsDue(now));
    }

    [Fact]
    public async Task ScheduledCleanup_RecyclesSafeTemporaryFileAndWritesAutomaticAudit()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var oldPath = await fixture.WriteFileAsync(
            $"scheduled-{Guid.NewGuid():N}.tmp",
            10,
            setOldTimestamp: true);
        var recentPath = await fixture.WriteFileAsync(
            $"recent-{Guid.NewGuid():N}.tmp",
            20);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var cleanup = new CleanupService(fixture.Repository);
        var runner = new ScheduledCleanupRunner(
            () => [fixture.Drive],
            (drive, policy, limit, offset, cancellationToken) =>
                fixture.Candidates.GetCandidatesAsync(
                    drive,
                    policy,
                    limit,
                    offset,
                    cancellationToken),
            (request, cancellationToken) => cleanup.RecycleAsync(
                request,
                cancellationToken: cancellationToken));

        try
        {
            var result = await runner.RunAsync(
                new ScheduledCleanupTaskSnapshot
                {
                    Id = Guid.NewGuid(),
                    Name = "Scheduled temporary cleanup",
                    IntervalDays = 1,
                    Categories = ["Cache & temporary"],
                    IsEnabled = true,
                    Mode = CleanupMode.RecycleSafeItems,
                    LastRun = DateTimeOffset.UtcNow.AddDays(-1),
                });
            var sourceWasRecycled = !File.Exists(oldPath);
            if (sourceWasRecycled)
            {
                RestoreFromRecycleBin(Path.GetFileName(oldPath));
            }

            Assert.True(sourceWasRecycled);
            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(0, result.SkippedCount);
            Assert.True(File.Exists(recentPath));
            Assert.Null(
                await fixture.Repository.GetItemByPathAsync(
                    scan.ScanId,
                    oldPath));
            Assert.NotNull(
                await fixture.Repository.GetItemByPathAsync(
                    scan.ScanId,
                    recentPath));
            var audit = Assert.Single(
                await fixture.Repository.GetCleanupHistoryAsync());
            Assert.Equal(oldPath, audit.Path);
            Assert.Equal(CleanupPolicyId.TemporaryFiles, audit.Policy);
            Assert.True(audit.Automatic);
            Assert.Equal("Recycled", audit.Result);
        }
        finally
        {
            if (!File.Exists(oldPath))
            {
                RestoreFromRecycleBin(Path.GetFileName(oldPath));
            }
        }
    }

    [Fact]
    public async Task ScheduledCleanup_ReviewOnlyDoesNotRunDiscoveryOrCleanup()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"StorageLens-ScheduledReview-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "Storage Lens review-only test");
        try
        {
            var runner = new ScheduledCleanupRunner(
                () => throw new InvalidOperationException(
                    "Review-only schedules must not discover drives."),
                (_, _, _, _, _) => throw new InvalidOperationException(
                    "Review-only schedules must not query candidates."),
                (_, _) => throw new InvalidOperationException(
                    "Review-only schedules must not recycle files."));

            var result = await runner.RunAsync(
                new ScheduledCleanupTaskSnapshot
                {
                    Id = Guid.NewGuid(),
                    Name = "Review temporary files",
                    IntervalDays = 1,
                    Categories = ["Cache & temporary"],
                    IsEnabled = true,
                    Mode = CleanupMode.ReviewOnly,
                    LastRun = DateTimeOffset.UtcNow.AddDays(-1),
                });

            Assert.True(File.Exists(path));
            Assert.Equal(0, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(0, result.SkippedCount);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ScheduledCleanup_AutomaticModeQueriesOnlyAutomaticPolicies()
    {
        var queriedPolicies = new List<CleanupPolicyId>();
        var cleanupCalled = false;
        var drive = new DriveSummary
        {
            Name = "Test drive",
            Letter = "C:",
            FileSystem = "NTFS",
            TotalBytes = 1L << 30,
            UsedBytes = 0,
        };
        var runner = new ScheduledCleanupRunner(
            () => [drive],
            (_, policy, _, _, _) =>
            {
                queriedPolicies.Add(policy);
                return Task.FromResult<IReadOnlyList<CleanupCandidate>>([]);
            },
            (_, _) =>
            {
                cleanupCalled = true;
                return Task.FromResult(new CleanupResult());
            });

        var result = await runner.RunAsync(
            new ScheduledCleanupTaskSnapshot
            {
                Id = Guid.NewGuid(),
                Name = "Automatic temporary cleanup",
                IntervalDays = 1,
                Categories =
                [
                    "Cache & temporary",
                    "Downloads",
                    "Package caches",
                    "Duplicates",
                ],
                IsEnabled = true,
                Mode = CleanupMode.RecycleSafeItems,
                LastRun = DateTimeOffset.UtcNow.AddDays(-1),
            });

        Assert.Equal(
            CleanupPolicyId.TemporaryFiles,
            Assert.Single(queriedPolicies));
        Assert.False(cleanupCalled);
        Assert.Equal(0, result.SucceededCount);
    }

    [Fact]
    public void AutomationClaim_IsExclusiveAndDoesNotRestoreANewerRun()
    {
        var previous = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var id = Guid.NewGuid();
        var settings = new AutomationSettings
        {
            CleanupTasks =
            [
                new ScheduledCleanupTaskSnapshot
                {
                    Id = id,
                    Name = "Daily cache review",
                    IntervalDays = 1,
                    Categories = ["Cache & temporary"],
                    IsEnabled = true,
                    LastRun = previous,
                },
            ],
        };

        Assert.True(AutomationSettingsService.TryClaimScheduleRunCore(
            settings,
            id,
            now,
            out var claimed,
            out var claimedPrevious));
        Assert.NotNull(claimed);
        Assert.Equal(previous, claimedPrevious);
        Assert.Equal(now, claimed.LastRun);
        Assert.False(AutomationSettingsService.TryClaimScheduleRunCore(
            settings,
            id,
            now,
            out _,
            out _));

        var newerRun = now.AddMinutes(1);
        settings.CleanupTasks[0].LastRun = newerRun;
        Assert.False(AutomationSettingsService.RestoreScheduleRunCore(
            settings,
            id,
            now,
            previous));
        Assert.Equal(newerRun, settings.CleanupTasks[0].LastRun);
    }

    [Fact]
    public void AutomationRegistration_UsesTheSoonestEnabledSchedule()
    {
        var settings = new AutomationSettings
        {
            MonitorEnabled = true,
            MonitorIntervalDays = 7,
            CleanupTasks =
            [
                new ScheduledCleanupTaskSnapshot
                {
                    Id = Guid.NewGuid(),
                    Name = "Daily cleanup",
                    IntervalDays = 1,
                    Categories = ["Cache & temporary"],
                    IsEnabled = true,
                    LastRun = DateTimeOffset.Now,
                },
                new ScheduledCleanupTaskSnapshot
                {
                    Id = Guid.NewGuid(),
                    Name = "Disabled cleanup",
                    IntervalDays = 1,
                    Categories = ["Cache & temporary"],
                    IsEnabled = false,
                    LastRun = DateTimeOffset.Now,
                },
            ],
        };

        var plan = BackgroundTaskRegistrationService.GetRegistrationPlan(settings);

        Assert.True(plan.ShouldRegister);
        Assert.Equal(1, plan.IntervalDays);
    }

    [Fact]
    public void IdleIndexRegistration_UsesLowDelayIdleTriggers()
    {
        var plan = BackgroundTaskRegistrationService.GetIndexRegistrationPlan(
            true);

        Assert.Collection(
            plan,
            maintenance =>
            {
                Assert.Equal(
                    BackgroundIndexTriggerKind.Maintenance,
                    maintenance.Trigger);
                Assert.Equal(15u, maintenance.FreshnessMinutes);
            },
            userAway =>
            {
                Assert.Equal(
                    BackgroundIndexTriggerKind.UserAway,
                    userAway.Trigger);
                Assert.Equal(0u, userAway.FreshnessMinutes);
            });
        Assert.Empty(
            BackgroundTaskRegistrationService.GetIndexRegistrationPlan(
                false));
    }

    [Fact]
    public async Task IdleIndexDue_WithoutAnyCompletedScan_IsImmediate()
    {
        var now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        var settings = new AutomationSettings
        {
            BackgroundIndexEnabled = true,
            BackgroundIndexIntervalDays = 7,
            LastBackgroundIndexUtc = now.AddMinutes(-5),
        };
        var drive = CreateIndexDrive("C:");

        var isDue = await BackgroundIndexPolicy.IsDueAsync(
            settings,
            [drive],
            (_, _) => Task.FromResult<int?>(null),
            now,
            CancellationToken.None);

        Assert.True(isDue);
    }

    [Fact]
    public async Task IdleIndexDue_UsesCadenceOnlyWhenAllIndexesAreCurrent()
    {
        var now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        var settings = new AutomationSettings
        {
            BackgroundIndexEnabled = true,
            BackgroundIndexIntervalDays = 1,
            LastBackgroundIndexUtc = now.AddHours(-1),
        };
        var currentDrives = new[]
        {
            CreateIndexDrive("C:"),
            CreateIndexDrive("D:"),
        };

        var notYetDue = await BackgroundIndexPolicy.IsDueAsync(
            settings,
            currentDrives,
            (_, _) => Task.FromResult<int?>(4),
            now,
            CancellationToken.None);
        var legacyIndexDue = await BackgroundIndexPolicy.IsDueAsync(
            settings,
            currentDrives,
            (letter, _) => Task.FromResult<int?>(
                letter == "D:" ? 3 : 4),
            now,
            CancellationToken.None);
        settings.LastBackgroundIndexUtc = now.AddDays(-1);
        var cadenceDue = await BackgroundIndexPolicy.IsDueAsync(
            settings,
            currentDrives,
            (_, _) => Task.FromResult<int?>(4),
            now,
            CancellationToken.None);

        Assert.False(notYetDue);
        Assert.True(legacyIndexDue);
        Assert.True(cadenceDue);
    }

    [Fact]
    public async Task Repository_ReconcilesInterruptedCleanupAudits()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var existingPath = await fixture.WriteFileAsync("pending.tmp", 10);
        var missingPath = Path.Combine(fixture.ScanRoot, "missing.tmp");
        await fixture.Repository.BeginCleanupActionAsync(
            null,
            CleanupPolicyId.TemporaryFiles,
            existingPath,
            10,
            false);
        await fixture.Repository.BeginCleanupActionAsync(
            null,
            CleanupPolicyId.TemporaryFiles,
            missingPath,
            10,
            false);

        var reopened = new StorageRepository(fixture.Repository.DatabasePath);
        await reopened.InitializeAsync();
        var history = await reopened.GetCleanupHistoryAsync();

        Assert.Contains(history, item =>
            item.Path == existingPath && item.Result == "Interrupted");
        Assert.Contains(history, item =>
            item.Path == missingPath && item.Result == "SourceMissingAfterPending");
    }

    [Fact]
    public async Task Repository_ReconcilesAProcessInterruptedScan()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var session = await fixture.Repository.BeginScanAsync(fixture.Drive);
        var path = await fixture.WriteFileAsync("partial.tmp", 10);
        await session.InsertFilesAsync(
            [
                new StorageFileRecord
                {
                    ScanId = session.ScanId,
                    Name = Path.GetFileName(path),
                    FullPath = path,
                    ParentPath = fixture.ScanRoot,
                    SizeBytes = 10,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    ModifiedUtc = DateTimeOffset.UtcNow,
                    AccessedUtc = DateTimeOffset.UtcNow,
                    Attributes = (long)FileAttributes.Normal,
                    Extension = ".tmp",
                    Category = "Cache & temporary",
                    Tag = "Cache",
                },
            ],
            CancellationToken.None);

        var reopened = new StorageRepository(fixture.Repository.DatabasePath);
        await reopened.InitializeAsync();
        var run = await reopened.GetScanAsync(session.ScanId);

        Assert.NotNull(run);
        Assert.Equal(StorageScanStatus.Running, run.Status);
        Assert.Equal("partial.tmp", (await reopened.GetItemByPathAsync(
            session.ScanId,
            path))?.Name);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Repository_MigratesVersionOneAndPreservesRows()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "StorageLensTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "storage.db");
        try
        {
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE SchemaVersion (
                        Id INTEGER PRIMARY KEY,
                        Version INTEGER NOT NULL
                    );
                    INSERT INTO SchemaVersion (Id, Version) VALUES (1, 1);
                    CREATE TABLE FileEntries (
                        ScanId TEXT NOT NULL,
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
                        ContentHash TEXT,
                        PRIMARY KEY (ScanId, FullPath)
                    );
                    INSERT INTO FileEntries VALUES (
                        'scan', 'C:\legacy.tmp', 'legacy.tmp', 'C:\',
                        12, 0, 0, 0, 0, '.tmp', 'Cache & temporary', 'Cache', NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new StorageRepository(databasePath);
            await repository.InitializeAsync();
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText =
                    "SELECT Version FROM SchemaVersion WHERE Id = 1";
                Assert.Equal(8L, (long)(await versionCommand.ExecuteScalarAsync())!);

                await using var rowCommand = connection.CreateCommand();
                rowCommand.CommandText = """
                    SELECT VolumeSerialNumber, FileIndexHigh, FileIndexLow, Name
                    FROM FileEntries
                    WHERE FullPath = 'C:\legacy.tmp'
                    """;
                await using var reader = await rowCommand.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt64(0));
                Assert.Equal("legacy.tmp", reader.GetString(3));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Repository_MigratesVersionSevenWithPruneIndexes()
        {
            await using var fixture = await ScanFixture.CreateAsync();
            await using (var connection = new SqliteConnection(
                             $"Data Source={fixture.Repository.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP INDEX IF EXISTS IX_FileNames_PhysicalFile;
                    DROP INDEX IF EXISTS IX_NormalizedCleanupCandidates_FileName;
                    UPDATE SchemaVersion SET Version = 7 WHERE Id = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();
            var migrated = new StorageRepository(
                fixture.Repository.DatabasePath);
            await migrated.InitializeAsync();
            await using (var connection = new SqliteConnection(
                             $"Data Source={migrated.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Version
                    FROM SchemaVersion
                    WHERE Id = 1;
                    """;
                Assert.Equal(8L, (long)(await command.ExecuteScalarAsync())!);
                command.CommandText = """
                    SELECT name
                    FROM sqlite_master
                    WHERE type = 'index'
                    """;
                var indexes = new List<string>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    indexes.Add(reader.GetString(0));
                }

                Assert.Contains("IX_FileNames_PhysicalFile", indexes);
                Assert.Contains(
                    "IX_NormalizedCleanupCandidates_FileName",
                    indexes);
            }
        }

    [Fact]
    public async Task Repository_PrunesStaleRunAcrossMultipleChunks()
        {
            await using var fixture = await ScanFixture.CreateAsync();
            var first = await fixture.Repository.BeginScanAsync(fixture.Drive);
            const int fileCount = 650;
            var records = new List<StorageFileRecord>();
            for (var index = 0; index < fileCount; index++)
            {
                var name = $"first-{index:D4}.bin";
                records.Add(new StorageFileRecord
                {
                    ScanId = first.ScanId,
                    Name = name,
                    FullPath = Path.Combine(fixture.ScanRoot, name),
                    ParentPath = fixture.ScanRoot,
                    SizeBytes = index + 1,
                    AllocatedSizeBytes = index + 1,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    ModifiedUtc = DateTimeOffset.UtcNow,
                    AccessedUtc = DateTimeOffset.UtcNow,
                    Attributes = (long)FileAttributes.Normal,
                    Extension = ".bin",
                    Category = "Other",
                    Tag = "Other",
                });
            }

            await first.InsertFilesAsync(records, CancellationToken.None);
            await first.CompleteAsync(
                fileCount,
                1,
                fileCount,
                0,
                CancellationToken.None,
                fileCount);
            await first.DisposeAsync();
            await using (var connection = new SqliteConnection(
                             $"Data Source={fixture.Repository.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO NormalizedCleanupCandidates (
                        RunKey, PolicyId, FileNameId
                    )
                    SELECT RunKey, 1, Id
                    FROM FileNames
                    WHERE RunKey = $run
                    """;
                command.Parameters.AddWithValue("$run", first.RunKey);
                await command.ExecuteNonQueryAsync();
            }

            var latest = await fixture.Repository.BeginScanAsync(fixture.Drive);
            await latest.InsertFilesAsync(
                [
                    new StorageFileRecord
                    {
                        ScanId = latest.ScanId,
                        Name = "latest.bin",
                        FullPath = Path.Combine(fixture.ScanRoot, "latest.bin"),
                        ParentPath = fixture.ScanRoot,
                        SizeBytes = 1,
                        AllocatedSizeBytes = 1,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        ModifiedUtc = DateTimeOffset.UtcNow,
                        AccessedUtc = DateTimeOffset.UtcNow,
                        Attributes = (long)FileAttributes.Normal,
                        Extension = ".bin",
                        Category = "Other",
                        Tag = "Other",
                    },
                ],
                CancellationToken.None);
            await latest.CompleteAsync(
                1,
                1,
                1,
                0,
                CancellationToken.None,
                1);
            await latest.DisposeAsync();

            await fixture.Repository.PruneHistoryAsync(
                completedScansPerDrive: 2);
            await using (var connection = new SqliteConnection(
                             $"Data Source={fixture.Repository.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM FileNames WHERE RunKey = $old),
                        (SELECT COUNT(*) FROM PhysicalFiles WHERE RunKey = $old),
                        (SELECT COUNT(*)
                         FROM NormalizedCleanupCandidates
                         WHERE RunKey = $old),
                        (SELECT COUNT(*) FROM FileNames WHERE RunKey = $latest),
                        (SELECT COUNT(*) FROM ScanRuns
                         WHERE RunKey IN ($old, $latest))
                    """;
                command.Parameters.AddWithValue("$old", first.RunKey);
                command.Parameters.AddWithValue("$latest", latest.RunKey);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt64(0));
                Assert.Equal(0, reader.GetInt64(1));
                Assert.Equal(0, reader.GetInt64(2));
                Assert.Equal(1, reader.GetInt64(3));
                Assert.Equal(2, reader.GetInt64(4));
            }
        }

    [Fact]
    public void AppUpdateCoordinator_VersionsAndLegacyInstanceKeys()
    {
        var oldKey = AppUpdateCoordinator.BuildMainInstanceKey(
            new Version(1, 0, 10, 0));
        var newKey = AppUpdateCoordinator.BuildMainInstanceKey(
            new Version(1, 0, 11, 0));

        Assert.NotEqual(oldKey, newKey);
        Assert.True(AppUpdateCoordinator.TryGetInstanceVersion(
            oldKey,
            out var oldVersion));
        Assert.True(AppUpdateCoordinator.TryGetInstanceVersion(
            newKey,
            out var newVersion));
        Assert.True(oldVersion < newVersion);
        Assert.True(AppUpdateCoordinator.TryGetInstanceVersion(
            AppUpdateCoordinator.LegacyMainInstanceKey,
            out var legacyVersion));
        Assert.True(legacyVersion < oldVersion);
        Assert.False(AppUpdateCoordinator.TryGetInstanceVersion(
            "StorageLens.BackgroundTask",
            out _));
        Assert.True(AppUpdateCoordinator.TryGetPackageVersionFromPath(
            @"C:\Program Files\WindowsApps\Contoso.StorageLens_1.0.10.0_x64__publisher\StorageLens\StorageLens.exe",
            "Contoso.StorageLens",
            "publisher",
            out var packageVersion));
        Assert.Equal(new Version(1, 0, 10, 0), packageVersion);
        Assert.False(AppUpdateCoordinator.TryGetPackageVersionFromPath(
            @"C:\Program Files\WindowsApps\Another.StorageLens_1.0.10.0_x64__publisher\StorageLens\StorageLens.exe",
            "Contoso.StorageLens",
            "publisher",
            out _));
    }

    [Fact]
    public async Task Repository_MigratesVersionSixWithEarliestGrowthSample()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync(
            "MigrationFolder\\file.bin",
            10);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        string volumeKey;
        string pathHash;
        await using (var connection = new SqliteConnection(
                         $"Data Source={fixture.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(
                           NULLIF(run.VolumeSerialNumber, ''),
                           run.DriveLetter
                       ),
                       folder.PathHash
                FROM ScanRuns run
                INNER JOIN FolderNodes folder ON folder.RunKey = run.RunKey
                WHERE run.Id = $scan AND folder.Name = 'MigrationFolder'
                """;
            command.Parameters.AddWithValue("$scan", scan.ScanId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            volumeKey = reader.GetString(0);
            pathHash = reader.GetString(1);
            await reader.CloseAsync();
        }

        var earliest = DateTimeOffset.UtcNow
            .AddDays(-30)
            .ToUnixTimeMilliseconds();
        await using (var connection = new SqliteConnection(
                         $"Data Source={fixture.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM FolderGrowthSamples
                WHERE VolumeKey = $volume AND PathHash = $hash;
                INSERT INTO FolderGrowthSamples (
                    VolumeKey, PathHash, SampleUtc, LogicalSize,
                    AllocatedSize, FileCount
                ) VALUES ($volume, $hash, $earliest, 100, 128, 1);
                INSERT INTO FolderGrowthSamples (
                    VolumeKey, PathHash, SampleUtc, LogicalSize,
                    AllocatedSize, FileCount
                ) VALUES ($volume, $hash, $latest, 200, 256, 2);
                DROP TABLE FolderGrowthBaselines;
                UPDATE SchemaVersion SET Version = 6 WHERE Id = 1;
                """;
            command.Parameters.AddWithValue("$volume", volumeKey);
            command.Parameters.AddWithValue("$hash", pathHash);
            command.Parameters.AddWithValue("$earliest", earliest);
            command.Parameters.AddWithValue(
                "$latest",
                earliest + 86_400_000);
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var migrated = new StorageRepository(
            fixture.Repository.DatabasePath);
        await migrated.InitializeAsync();
        await using (var connection = new SqliteConnection(
                         $"Data Source={migrated.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.FirstSeenUtc, b.BaselineAllocatedSize,
                       b.FolderIdentity, f.FileId128
                FROM FolderGrowthBaselines b
                INNER JOIN ScanRuns run
                    ON COALESCE(
                           NULLIF(run.VolumeSerialNumber, ''),
                           run.DriveLetter
                       ) = b.VolumeKey
                INNER JOIN FolderNodes f
                    ON f.RunKey = run.RunKey
                   AND f.PathHash = b.PathHash
                WHERE run.Id = $scan
                  AND f.Name = 'MigrationFolder'
                ORDER BY run.CompletedUtc DESC
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$scan", scan.ScanId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(earliest, reader.GetInt64(0));
            Assert.Equal(128, reader.GetInt64(1));
            Assert.Equal(reader.GetString(3), reader.GetString(2));
        }
    }

    [Fact]
    public async Task DuplicateDetection_ContinuesPastTheFirstGroupBatch()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session = await fixture.Repository.BeginScanAsync(fixture.Drive);
        var records = new List<StorageFileRecord>();
        var now = DateTimeOffset.UtcNow.AddDays(-2);
        foreach (var definition in new[]
                 {
                     ("group-one-a.bin", 1_100_000, (byte)31),
                     ("group-one-b.bin", 1_100_000, (byte)31),
                     ("group-two-a.bin", 1_100_001, (byte)47),
                     ("group-two-b.bin", 1_100_001, (byte)47),
                 })
        {
            var path = await fixture.WriteRepeatedFileAsync(
                definition.Item1,
                definition.Item2,
                definition.Item3);
            records.Add(new StorageFileRecord
            {
                ScanId = session.ScanId,
                Name = Path.GetFileName(path),
                FullPath = path,
                ParentPath = fixture.ScanRoot,
                SizeBytes = definition.Item2,
                CreatedUtc = now,
                ModifiedUtc = now,
                AccessedUtc = now,
                Attributes = (long)FileAttributes.Normal,
                Extension = ".bin",
                Category = "Other",
                Tag = "File",
            });
        }

        await session.InsertFilesAsync(records, CancellationToken.None);
        await session.CompleteAsync(
            4,
            0,
            records.Sum(item => item.SizeBytes),
            0,
            CancellationToken.None);
        var detector = new DuplicateDetectionService(fixture.Repository);
        var hashed = await detector.ComputeHashesAsync(
            session.ScanId,
            groupBatchSize: 1);

        Assert.Equal(4, hashed);
        var candidates = await fixture.Repository.GetCleanupCandidatesAsync(
            session.ScanId,
            CleanupPolicyId.LargeDuplicates,
            DateTimeOffset.Now,
            100,
            0);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public async Task DuplicateDetection_HonorsTheForegroundWorkBudget()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session = await fixture.Repository.BeginScanAsync(fixture.Drive);
        var now = DateTimeOffset.UtcNow.AddDays(-2);
        var records = new List<StorageFileRecord>();
        foreach (var name in new[] { "budget-a.bin", "budget-b.bin" })
        {
            var path = await fixture.WriteRepeatedFileAsync(name, 1_100_000, 61);
            records.Add(new StorageFileRecord
            {
                ScanId = session.ScanId,
                Name = name,
                FullPath = path,
                ParentPath = fixture.ScanRoot,
                SizeBytes = 1_100_000,
                CreatedUtc = now,
                ModifiedUtc = now,
                AccessedUtc = now,
                Attributes = (long)FileAttributes.Normal,
                Extension = ".bin",
                Category = "Other",
                Tag = "File",
            });
        }

        await session.InsertFilesAsync(records, CancellationToken.None);
        await session.CompleteAsync(
            2,
            0,
            2_200_000,
            0,
            CancellationToken.None);
        var detector = new DuplicateDetectionService(fixture.Repository);

        Assert.Equal(
            0,
            await detector.ComputeHashesAsync(
                session.ScanId,
                maxFiles: 1));
        await using var connection = new SqliteConnection(
            $"Data Source={fixture.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM PhysicalFiles
            WHERE RunKey = $run AND HashState = 1
            """;
        command.Parameters.AddWithValue("$run", session.RunKey);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DuplicateDiscovery_LimitsMaterializedCandidates()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session = await fixture.Repository.BeginScanAsync(
            fixture.Drive);
        var records = Enumerable.Range(0, 20)
            .Select(index => CreateRecord(
                session.ScanId,
                fixture.ScanRoot,
                Path.Combine(fixture.ScanRoot, $"same-size-{index:D2}.bin"),
                1_100_000))
            .ToList();
        await session.InsertFilesAsync(records, CancellationToken.None);
        await session.CompleteAsync(
            records.Count,
            0,
            records.Sum(record => record.SizeBytes),
            0,
            CancellationToken.None);

        var groups =
            await fixture.Repository.GetUnsampledDuplicateSizeGroupsAsync(
                session.ScanId,
                candidateLimit: 3);

        Assert.Equal(3, groups.Sum(group => group.Paths.Count));
    }

    [Fact]
    public async Task DuplicateDetection_MarksMissingFilesSoTheGroupDoesNotLoop()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var existingPath = await fixture.WriteRepeatedFileAsync(
            "existing.bin",
            1_100_000,
            29);
        var missingPath = Path.Combine(fixture.ScanRoot, "missing.bin");
        var now = DateTimeOffset.UtcNow.AddDays(-2);
        await using var session = await fixture.Repository.BeginScanAsync(fixture.Drive);
        await session.InsertFilesAsync(
            [
                new StorageFileRecord
                {
                    ScanId = session.ScanId,
                    Name = Path.GetFileName(existingPath),
                    FullPath = existingPath,
                    ParentPath = fixture.ScanRoot,
                    SizeBytes = 1_100_000,
                    CreatedUtc = now,
                    ModifiedUtc = now,
                    AccessedUtc = now,
                    Attributes = (long)FileAttributes.Normal,
                    Extension = ".bin",
                    Category = "Other",
                    Tag = "File",
                },
                new StorageFileRecord
                {
                    ScanId = session.ScanId,
                    Name = Path.GetFileName(missingPath),
                    FullPath = missingPath,
                    ParentPath = fixture.ScanRoot,
                    SizeBytes = 1_100_000,
                    CreatedUtc = now,
                    ModifiedUtc = now,
                    AccessedUtc = now,
                    Attributes = (long)FileAttributes.Normal,
                    Extension = ".bin",
                    Category = "Other",
                    Tag = "File",
                },
            ],
            CancellationToken.None);
        await session.CompleteAsync(2, 0, 2_200_000, 0, CancellationToken.None);
        var detector = new DuplicateDetectionService(fixture.Repository);

        Assert.Equal(
            0,
            await detector.ComputeHashesAsync(
                session.ScanId,
                groupBatchSize: 1));
        Assert.Equal(
            0,
            await detector.ComputeHashesAsync(
                session.ScanId,
                groupBatchSize: 1));
        var candidates = await fixture.Repository.GetCleanupCandidatesAsync(
            session.ScanId,
            CleanupPolicyId.LargeDuplicates,
            DateTimeOffset.Now,
            100,
            0);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Repository_EnqueuesDirectoriesAsAnIdempotentBatch()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session =
            await fixture.Repository.BeginScanAsync(fixture.Drive);
        var metadata = new ScanFolderMetadata(
            (long)FileAttributes.Directory,
            0,
            DateTimeOffset.UtcNow,
            string.Empty,
            false);
        await session.EnqueueDirectoryAsync(
            fixture.ScanRoot,
            0,
            metadata,
            CancellationToken.None);
        var rootWork = Assert.Single(await session.ClaimDirectoriesAsync(
            1,
            CancellationToken.None));
        var initialTotals =
            await session.GetScanTotalsAsync(CancellationToken.None);
        var firstChild = Path.Combine(fixture.ScanRoot, "first");
        var secondChild = Path.Combine(fixture.ScanRoot, "second");
        var excludedChild = Path.Combine(fixture.ScanRoot, "excluded");

        var insertedFolders = await session.EnqueueDirectoriesAsync(
            [
                new ScanDirectoryEntry(firstChild, 1, metadata, true),
                new ScanDirectoryEntry(firstChild, 1, metadata, true),
                new ScanDirectoryEntry(secondChild, 1, metadata, true),
                new ScanDirectoryEntry(excludedChild, 1, metadata, false),
            ],
            CancellationToken.None);
        Assert.Equal(3, insertedFolders);

        var queued = await session.ClaimDirectoriesAsync(
            10,
            CancellationToken.None);
        Assert.Equal(2, queued.Count);
        Assert.Contains(queued, item =>
            string.Equals(
                item.FullPath,
                firstChild,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queued, item =>
            string.Equals(
                item.FullPath,
                secondChild,
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queued, item =>
            string.Equals(
                item.FullPath,
                excludedChild,
                StringComparison.OrdinalIgnoreCase));

        var totals = await session.GetScanTotalsAsync(CancellationToken.None);
        Assert.Equal(initialTotals.Folders + 3, totals.Folders);
        await session.CompleteDirectoryAsync(
            rootWork,
            CancellationToken.None);
        foreach (var item in queued)
        {
            await session.CompleteDirectoryAsync(
                item,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Repository_ClaimsQueuedDirectoriesInIndexedFifoOrder()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session =
            await fixture.Repository.BeginScanAsync(fixture.Drive);
        var metadata = new ScanFolderMetadata(
            (long)FileAttributes.Directory,
            0,
            DateTimeOffset.UtcNow,
            string.Empty,
            false);
        var firstPath = Path.Combine(fixture.ScanRoot, "deep", "first");
        var secondPath = Path.Combine(fixture.ScanRoot, "second");

        await session.EnqueueDirectoryAsync(
            firstPath,
            2,
            metadata,
            CancellationToken.None);
        await Task.Delay(20);
        await session.EnqueueDirectoryAsync(
            secondPath,
            1,
            metadata,
            CancellationToken.None);

        var claimed = await session.ClaimDirectoriesAsync(
            2,
            CancellationToken.None);

        Assert.Equal(
            [firstPath, secondPath],
            claimed.Select(item => item.FullPath).ToArray());
    }

    [Fact]
    public async Task Repository_FinalizesDirectoriesAndIssuesAsABatch()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session =
            await fixture.Repository.BeginScanAsync(fixture.Drive);
        await session.EnqueueDirectoryAsync(
            fixture.ScanRoot,
            0,
            new ScanFolderMetadata(
                (long)FileAttributes.Directory,
                0,
                DateTimeOffset.UtcNow,
                string.Empty,
                false),
            CancellationToken.None);
        var claimed = await session.ClaimDirectoriesAsync(
            1,
            CancellationToken.None);

        await session.FinalizeDirectoriesAsync(
            claimed,
            [fixture.ScanRoot],
            [
                new ScanIssue
                {
                    Path = fixture.ScanRoot,
                    Type = ScanIssueType.AccessDenied,
                    ErrorCode = 5,
                    Message = "Access denied.",
                    DetectedUtc = DateTimeOffset.UtcNow,
                },
            ],
            CancellationToken.None);

        Assert.Empty(await session.ClaimDirectoriesAsync(
            10,
            CancellationToken.None));
        var issue = Assert.Single(
            await fixture.Repository.GetScanIssuesAsync(session.ScanId));
        Assert.Equal(fixture.ScanRoot, issue.Path);
        Assert.Equal(ScanIssueType.AccessDenied, issue.Type);
        Assert.Equal(5, issue.ErrorCode);
    }

    [Fact]
    public async Task Repository_MaterializesPreviousWindowsCandidatesInBulk()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session =
            await fixture.Repository.BeginScanAsync(fixture.Drive);
        const string previousWindowsPath = @"C:\Windows.old";
        await session.EnqueueDirectoryAsync(
            previousWindowsPath,
            0,
            new ScanFolderMetadata(
                (long)FileAttributes.Directory,
                0,
                DateTimeOffset.UtcNow,
                string.Empty,
                false),
            CancellationToken.None);
        var nestedPath = Path.Combine(
            previousWindowsPath,
            "Windows",
            "System32");
        await session.EnqueueDirectoryAsync(
            nestedPath,
            1,
            new ScanFolderMetadata(
                (long)FileAttributes.Directory,
                0,
                DateTimeOffset.UtcNow,
                string.Empty,
                false),
            CancellationToken.None);
        var filePath = Path.Combine(nestedPath, "example.dll");
        await session.InsertFilesAsync(
            [
                CreateRecord(
                    session.ScanId,
                    nestedPath,
                    filePath,
                    1024),
            ],
            CancellationToken.None);

        await session.MaterializePreviousWindowsInstallationCandidatesAsync(
            CancellationToken.None);

        var candidate = Assert.Single(
            await fixture.Repository.GetCleanupCandidatesAsync(
                session.ScanId,
                CleanupPolicyId.PreviousWindowsInstallation,
                DateTimeOffset.UtcNow));
        Assert.Equal(filePath, candidate.FullPath);
    }

    [Fact]
    public async Task Repository_DisposingIncompleteScanPreservesSavedProgress()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var session =
            await fixture.Repository.BeginScanAsync(fixture.Drive);
        await session.SaveProgressAsync(
            24,
            8,
            12_345,
            16_384,
            2,
            CancellationToken.None);

        await session.DisposeAsync();

        var scan = await fixture.Repository.GetScanAsync(session.ScanId);
        Assert.NotNull(scan);
        Assert.Equal(StorageScanStatus.Failed, scan.Status);
        Assert.Equal(24, scan.FilesScanned);
        Assert.Equal(8, scan.FoldersScanned);
        Assert.Equal(12_345, scan.BytesScanned);
        Assert.Equal(16_384, scan.AllocatedBytesScanned);
        Assert.Equal(2, scan.InaccessibleItems);
        Assert.Equal(
            "Scan session closed before completion.",
            scan.ErrorMessage);
    }

    [Fact]
    public async Task Scanner_CancelledScanPersistsCancelledStatus()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("cancel.txt", 10);
        using var cancellation = new CancellationTokenSource();
        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot,
            new CallbackProgress<StorageScanProgress>(
                _ => cancellation.Cancel()),
            cancellation.Token);

        var scan = await fixture.Repository.GetScanAsync(result.ScanId);
        Assert.NotNull(scan);
        Assert.Equal(StorageScanStatus.Cancelled, result.Status);
        Assert.Equal(StorageScanStatus.Cancelled, scan.Status);
        Assert.Null(scan.ErrorMessage);
    }

    [Fact]
    public async Task Scanner_CancelledFinalizationResumesCompletedEnumeration()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync(@"Nested\resume.txt", 10);
        using var cancellation = new CancellationTokenSource();
        string? finalizationStage = null;
        double finalizationRate = -1;
        var first = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot,
            new CallbackProgress<StorageScanProgress>(
                update =>
                {
                    if (update.Phase == "Finalizing")
                    {
                        finalizationStage = update.CurrentPath;
                        finalizationRate = update.ItemsPerSecond;
                        cancellation.Cancel();
                    }
                }),
            cancellation.Token);

        var cancelled = await fixture.Repository.GetScanAsync(first.ScanId);
        Assert.NotNull(cancelled);
        Assert.Equal(StorageScanStatus.Cancelled, first.Status);
        Assert.Equal(StorageScanStatus.Cancelled, cancelled.Status);
        Assert.NotNull(finalizationStage);
        Assert.Contains("Resolving hard links", finalizationStage);
        Assert.Equal(0, finalizationRate);

        var resumed = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        Assert.Equal(first.ScanId, resumed.ScanId);
        Assert.Equal(StorageScanStatus.Completed, resumed.Status);
        Assert.Equal(1, resumed.FilesScanned);
        Assert.Equal(10, resumed.BytesScanned);
        var scan = await fixture.Repository.GetScanAsync(resumed.ScanId);
        Assert.NotNull(scan);
        Assert.Equal(StorageScanStatus.Completed, scan.Status);
        await using (var connection = new SqliteConnection(
                         $"Data Source={fixture.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM FolderNodes child
                INNER JOIN ScanRuns run ON run.RunKey = child.RunKey
                LEFT JOIN FolderNodes parent
                    ON parent.Id = child.ParentId
                   AND parent.RunKey = child.RunKey
                WHERE run.Id = $scan
                  AND child.Depth <> CASE
                      WHEN parent.Id IS NULL THEN 0
                      ELSE parent.Depth + 1
                  END
                """;
            command.Parameters.AddWithValue("$scan", scan.Id.ToString("N"));
            Assert.Equal(
                0,
                Convert.ToInt64(
                    await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task Repository_ResumesAClaimedDirectoryWithoutDuplicatingRows()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var firstPath = await fixture.WriteFileAsync("one.txt", 10);
        var secondPath = await fixture.WriteFileAsync("two.txt", 20);
        var original = await fixture.Repository.BeginScanAsync(fixture.Drive);
        await original.EnqueueDirectoryAsync(
            fixture.ScanRoot,
            0,
            new ScanFolderMetadata(
                (long)FileAttributes.Directory,
                0,
                DateTimeOffset.UtcNow,
                string.Empty,
                false),
            CancellationToken.None);
        var claimed = Assert.Single(await original.ClaimDirectoriesAsync(
            1,
            CancellationToken.None));
        var first = CreateRecord(
            original.ScanId,
            fixture.ScanRoot,
            firstPath,
            10);
        await original.InsertFilesAsync([first], CancellationToken.None);
        await original.DisposeAsync();

        await using var resumed =
            await fixture.Repository.BeginScanAsync(fixture.Drive);
        Assert.True(resumed.IsResumed);
        Assert.Equal(original.ScanId, resumed.ScanId);
        var repairedProgress =
            await fixture.Repository.GetScanAsync(resumed.ScanId);
        Assert.NotNull(repairedProgress);
        Assert.Equal(1, repairedProgress.FilesScanned);
        Assert.Equal(10, repairedProgress.BytesScanned);
        var resumedClaim = Assert.Single(await resumed.ClaimDirectoriesAsync(
            1,
            CancellationToken.None));
        Assert.Equal(claimed.PathHash, resumedClaim.PathHash);
        var inserted = await resumed.InsertFilesAsync(
            [
                first,
                CreateRecord(
                    resumed.ScanId,
                    fixture.ScanRoot,
                    secondPath,
                    20),
            ],
            CancellationToken.None);
        Assert.Equal(1, inserted.Files);
        Assert.Equal(20, inserted.LogicalBytes);
        await resumed.CompleteDirectoryAsync(
            resumedClaim,
            CancellationToken.None);
        await resumed.ClearCompletedWorkItemsAsync(CancellationToken.None);
        await resumed.RecomputeFolderTotalsAsync(CancellationToken.None);
        var totals = await resumed.GetScanTotalsAsync(CancellationToken.None);
        var indexedNames = new List<string>();
        await using (var connection = new SqliteConnection(
                         $"Data Source={fixture.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT n.Name, n.ParentId
                FROM FileNames n
                INNER JOIN ScanRuns r ON r.RunKey = n.RunKey
                WHERE r.Id = $scan
                ORDER BY n.Name, n.ParentId
                """;
            command.Parameters.AddWithValue("$scan", resumed.ScanId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexedNames.Add($"{reader.GetString(0)}:{reader.GetInt64(1)}");
            }
        }

        Assert.True(
            totals.Files == 2,
            string.Join(", ", indexedNames));
        await resumed.CompleteAsync(
            totals.Files,
            totals.Folders,
            totals.LogicalBytes,
            0,
            CancellationToken.None,
            totals.AllocatedBytes);

        var children = await fixture.Repository.GetChildrenAsync(
            resumed.ScanId,
            fixture.ScanRoot);
        Assert.True(
            children.Count == 2,
            string.Join(", ", children.Select(item =>
                $"{(item.IsFolder ? "Folder" : "File")}:{item.Path}")));
    }

    [Fact]
    public async Task Repository_RefusesToPublishAfterLeaseLoss()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await using var session =
            await fixture.Repository.BeginScanAsync(fixture.Drive);
        await using (var connection = new SqliteConnection(
                         $"Data Source={fixture.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ScanRuns
                SET OwnerProcessId = -1,
                    OwnerProcessStartedUtc = 0
                WHERE Id = $id
                """;
            command.Parameters.AddWithValue("$id", session.ScanId.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.CompleteAsync(
                0,
                0,
                0,
                0,
                CancellationToken.None));
    }

    [Fact]
    public async Task Repository_ReconcilesADeadScanOwner()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var session = await fixture.Repository.BeginScanAsync(fixture.Drive);
        await using (var connection = new SqliteConnection(
                         $"Data Source={fixture.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ScanRuns
                SET OwnerProcessId = 2147483647,
                    OwnerProcessStartedUtc = 1
                WHERE Id = $id
                """;
            command.Parameters.AddWithValue("$id", session.ScanId.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        await session.DisposeAsync();
        var reopened = new StorageRepository(fixture.Repository.DatabasePath);
        await reopened.InitializeAsync();

        var run = await reopened.GetScanAsync(session.ScanId);
        Assert.NotNull(run);
        Assert.Equal(StorageScanStatus.Failed, run.Status);
    }

    [Fact]
    public async Task NativeReader_MatchesFileIdentityAndAllocationMetadata()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("native.bin", 10);
        var identity = FileIdentityService.TryGetIdentity(path);

        var entry = Assert.Single(
            NativeDirectoryReader.Enumerate(fixture.ScanRoot),
            item => item.Name == "native.bin");

        Assert.NotNull(identity);
        Assert.Equal(identity.FileId128, entry.FileId, ignoreCase: true);
        Assert.Equal(10, entry.LogicalSize);
        Assert.True(entry.AllocatedSize >= entry.LogicalSize);
        Assert.NotEqual(0, entry.ChangeTime);
    }

    [Fact]
    public async Task NativeReader_EnumeratesMoreThanOneBuffer()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        const int fileCount = 1200;
        for (var index = 0; index < fileCount; index++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(fixture.ScanRoot, $"native-{index:D4}.bin"),
                []);
        }

        var entries = NativeDirectoryReader.Enumerate(fixture.ScanRoot);

        Assert.Equal(fileCount, entries.Count);
        Assert.Equal(fileCount, entries.Select(item => item.Name).Distinct().Count());
        Assert.Contains(entries, item => item.Name == "native-0000.bin");
        Assert.Contains(entries, item => item.Name == "native-1199.bin");
    }

    [Fact]
    public async Task Repository_RefreshesIncrementalFileAddUpdateAndDelete()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var firstPath = await fixture.WriteFileAsync("first.bin", 10);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var run = await fixture.Repository.GetScanAsync(scan.ScanId);
        Assert.NotNull(run);

        var secondPath = await fixture.WriteFileAsync("second.bin", 20);
        var added = await fixture.Repository.RefreshDirectorySnapshotAsync(
            run.RunKey,
            fixture.ScanRoot,
            NativeDirectoryReader.Enumerate(fixture.ScanRoot));
        Assert.True(added.Applied, added.Reason);
        Assert.Equal(
            2,
            (await fixture.Repository.GetChildrenAsync(
                scan.ScanId,
                fixture.ScanRoot)).Count);

        await File.WriteAllBytesAsync(secondPath, new byte[30]);
        var updated = await fixture.Repository.RefreshDirectorySnapshotAsync(
            run.RunKey,
            fixture.ScanRoot,
            NativeDirectoryReader.Enumerate(fixture.ScanRoot));
        Assert.True(updated.Applied, updated.Reason);
        Assert.Equal(
            30,
            (await fixture.Repository.GetItemByPathAsync(
                scan.ScanId,
                secondPath))?.SizeBytes);

        File.Delete(firstPath);
        var deleted = await fixture.Repository.RefreshDirectorySnapshotAsync(
            run.RunKey,
            fixture.ScanRoot,
            NativeDirectoryReader.Enumerate(fixture.ScanRoot));
        Assert.True(deleted.Applied, deleted.Reason);
        var totals = await fixture.Repository.FinalizeIncrementalSyncAsync(
            run.RunKey,
            CancellationToken.None);
        Assert.Equal(1, totals.Files);
        Assert.Equal(30, totals.LogicalBytes);
        Assert.Null(await fixture.Repository.GetItemByPathAsync(
            scan.ScanId,
            firstPath));
    }

    [Fact]
    public async Task Scanner_CountsHardLinkStorageOnce()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var original = await fixture.WriteFileAsync("original.bin", 10);
        var alias = Path.Combine(fixture.ScanRoot, "alias.bin");
        CreateHardLink(alias, original);

        var result = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        Assert.Equal(StorageScanStatus.Completed, result.Status);
        Assert.Equal(2, result.FilesScanned);
        var entries = await fixture.Repository.GetChildrenAsync(
            result.ScanId,
            fixture.ScanRoot);
        Assert.Single(entries, item => item.IsHardLinkAlias);
        Assert.Single(
            entries,
            item => !item.IsHardLinkAlias &&
                    item.AllocatedSizeBytes > 0);
        Assert.True(result.BytesScanned <= 10);
        Assert.True(result.AllocatedBytesScanned <= 4L * 1024);
    }

    [Fact]
    public async Task CleanupCandidates_ExcludeHardLinkAliases()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var original = await fixture.WriteFileAsync(
            "canonical.tmp",
            10,
            setOldTimestamp: true);
        var alias = Path.Combine(fixture.ScanRoot, "alias.tmp");
        CreateHardLink(alias, original);
        File.SetLastWriteTimeUtc(alias, DateTime.UtcNow.AddDays(-2));

        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var candidates = await fixture.Candidates.GetCandidatesAsync(
            fixture.Drive,
            CleanupPolicyId.TemporaryFiles);

        var candidate = Assert.Single(candidates);
        Assert.False(candidate.FileRecord.IsHardLinkAlias);
    }

    [Fact]
    public async Task Repository_PassesIntegrityAndForeignKeyChecks()
    {
        await using var fixture = await ScanFixture.CreateAsync();

        var health = await fixture.Repository.CheckHealthAsync();

        Assert.True(health.IsHealthy, health.QuickCheckResult);
        Assert.Equal(0, health.ForeignKeyViolations);
    }

    [Fact]
    public async Task Repository_MapsNativeFolderIdentityToIndexedPath()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var folderPath = Path.Combine(fixture.ScanRoot, "Mapped");
        Directory.CreateDirectory(folderPath);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var folderEntry = Assert.Single(
            NativeDirectoryReader.Enumerate(fixture.ScanRoot),
            item => item.Name == "Mapped");
        var fileReference = ulong.Parse(
            folderEntry.FileId[..16],
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        var run = await fixture.Repository.GetScanAsync(scan.ScanId);
        Assert.NotNull(run);

        var path = await fixture.Repository.GetFolderPathByFileIdAsync(
            run.RunKey,
            fileReference);

        Assert.NotNull(path);
        Assert.Equal(folderPath.TrimEnd('\\'), path.TrimEnd('\\'));
    }

    [Fact]
    public async Task Repository_SearchBuildsPathsAfterClosingResultReaders()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.WriteFileAsync("nested\\search-target.txt", 10);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);

        var results = await fixture.Repository.SearchAsync(
            scan.ScanId,
            "search-target");

        var item = Assert.Single(results);
        Assert.EndsWith(
            @"nested\search-target.txt",
            item.Path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncrementalRefresh_InvalidatesHashOnChangeTime()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var path = await fixture.WriteRepeatedFileAsync(
            "changed.bin",
            1_100_000,
            71);
        var scan = await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var run = await fixture.Repository.GetScanAsync(scan.ScanId);
        Assert.NotNull(run);
        var detector = new DuplicateDetectionService(fixture.Repository);
        await detector.ComputeHashesAsync(scan.ScanId);
        var originalTime = File.GetLastWriteTimeUtc(path);
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.ReadWrite))
        {
            stream.Position = 0;
            stream.WriteByte(72);
        }

        File.SetLastWriteTimeUtc(path, originalTime);
        var outcome = await fixture.Repository.RefreshDirectorySnapshotAsync(
            run.RunKey,
            fixture.ScanRoot,
            NativeDirectoryReader.Enumerate(fixture.ScanRoot));
        Assert.True(outcome.Applied, outcome.Reason);
        await using var connection = new SqliteConnection(
            $"Data Source={fixture.Repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT HashState
            FROM PhysicalFiles
            WHERE RunKey = $run
            """;
        command.Parameters.AddWithValue("$run", run.RunKey);
        Assert.NotEqual(
            2,
            Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Repository_RejectsIncrementalDirectoryStructureChanges()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        await fixture.Scanner.ScanTestRootAsync(
            fixture.Drive,
            fixture.ScanRoot);
        var run = await fixture.Repository.GetLatestCompletedScanAsync(
            fixture.Drive.Letter);
        Assert.NotNull(run);
        Directory.CreateDirectory(Path.Combine(fixture.ScanRoot, "new-folder"));

        var outcome = await fixture.Repository.RefreshDirectorySnapshotAsync(
            run.RunKey,
            fixture.ScanRoot,
            NativeDirectoryReader.Enumerate(fixture.ScanRoot));

        Assert.False(outcome.Applied);
        Assert.Contains("verification scan", outcome.Reason);
    }

    [Fact]
    public async Task FileIdentity_HandlesLongPaths()
    {
        await using var fixture = await ScanFixture.CreateAsync();
        var directory = fixture.ScanRoot;
        for (var index = 0; index < 6; index++)
        {
            directory = Path.Combine(directory, new string((char)('a' + index), 45));
            Directory.CreateDirectory(directory);
        }

        var path = Path.Combine(directory, "long-path.txt");
        await File.WriteAllTextAsync(path, "Storage Lens");

        Assert.True(path.Length > 260);
        var identity = FileIdentityService.TryGetIdentity(path);
        Assert.NotNull(identity);
        Assert.Equal(32, identity.FileId128.Length);
    }

    private static async Task<FolderBaselineState?> ReadFolderBaselineAsync(
        StorageRepository repository,
        Guid scanId,
        string folderName)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={repository.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT folder.PathHash, baseline.FirstSeenUtc,
                   baseline.BaselineAllocatedSize, folder.AllocatedSize
            FROM ScanRuns run
            INNER JOIN FolderNodes folder ON folder.RunKey = run.RunKey
            INNER JOIN FolderGrowthBaselines baseline
                ON baseline.VolumeKey = COALESCE(
                       NULLIF(run.VolumeSerialNumber, ''),
                       run.DriveLetter
                   )
               AND baseline.PathHash = folder.PathHash
            WHERE run.Id = $scan AND folder.Name = $name
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$scan", scanId.ToString("N"));
        command.Parameters.AddWithValue("$name", folderName);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new FolderBaselineState(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3))
            : null;
    }

    private sealed record FolderBaselineState(
        string PathHash,
        long FirstSeenUtc,
        long BaselineAllocatedSize,
        long AllocatedSize);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class ScanFixture : IAsyncDisposable
    {
        private ScanFixture(string root, StorageRepository repository)
        {
            Root = root;
            ScanRoot = Path.Combine(root, "scan");
            Directory.CreateDirectory(ScanRoot);
            Repository = repository;
            Scanner = new StorageScanService(repository);
            Candidates = new CleanupCandidateService(repository);
            Drive = new DriveSummary
            {
                Name = "Test drive",
                Letter = "C:",
                FileSystem = "NTFS",
                TotalBytes = 1L << 30,
                UsedBytes = 1000,
            };
        }

        public string Root { get; }

        public string ScanRoot { get; }

        public StorageRepository Repository { get; }

        public StorageScanService Scanner { get; }

        public CleanupCandidateService Candidates { get; }

        public DriveSummary Drive { get; }

        public static async Task<ScanFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "StorageLensTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repository = new StorageRepository(Path.Combine(root, "index", "storage.db"));
            await repository.InitializeAsync();
            return new ScanFixture(root, repository);
        }

        public async Task<string> WriteFileAsync(
            string relativePath,
            int length,
            bool setOldTimestamp = false)
        {
            var path = Path.Combine(ScanRoot, relativePath.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)7, length).ToArray());
            if (setOldTimestamp)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            }

            return path;
        }

        public async Task<string> WriteRepeatedFileAsync(
            string relativePath,
            int length,
            byte value)
        {
            var path = Path.Combine(ScanRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Enumerable.Repeat(value, length).ToArray());
            return path;
        }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }

    }

    private static IEnumerable<CleanupCandidateFolderNode>
        FlattenCandidateFolders(CleanupCandidateFolderNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in FlattenCandidateFolders(child))
            {
                yield return descendant;
            }
        }
    }

    private static StorageFolderNode CreateUsageFolder(
        string path,
        long bytes,
        bool hasChildren = false) =>
        new()
        {
            Path = path,
            Name = Path.GetFileName(path),
            SizeBytes = bytes,
            AllocatedSizeBytes = bytes,
            HasChildren = hasChildren,
            Tag = "Folder",
        };

    private static DriveSummary CreateIndexDrive(string letter) =>
        new()
        {
            Name = "Test drive",
            Letter = letter,
            FileSystem = "NTFS",
            TotalBytes = 1L << 30,
            UsedBytes = 0,
        };

    private static FolderGrowthInsight CreateGrowthInsight(
        string path,
        long addedBytes,
        long sizeBytes) =>
        new()
        {
            Name = Path.GetFileName(path.TrimEnd('\\')) switch
            {
                "" => path,
                var name => name,
            },
            Path = path,
            SizeBytes = sizeBytes,
            AddedBytes = addedBytes,
            GrowthPercentage = 10,
            WindowDays = 1,
        };

    private static CleanupCandidate CreateCleanupCandidate(
        Guid scanId,
        CleanupPolicyId policy,
        string path,
        long sizeBytes)
    {
        var name = Path.GetFileName(path);
        var parentPath = Path.GetDirectoryName(path) ??
                         Path.GetPathRoot(path) ??
                         path;
        var record = new StorageFileRecord
        {
            ScanId = scanId,
            Name = name,
            FullPath = path,
            ParentPath = parentPath,
            SizeBytes = sizeBytes,
            AllocatedSizeBytes = sizeBytes,
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow,
            AccessedUtc = DateTimeOffset.UtcNow,
            Attributes = (long)FileAttributes.Normal,
            Extension = Path.GetExtension(name),
            Category = "Cache & temporary",
            Tag = "Temporary",
        };
        return new CleanupCandidate
        {
            ScanId = scanId,
            PolicyId = policy,
            FileRecord = record,
            Item = CleanupCandidateService.ToStorageItem(record),
        };
    }

    private static CleanupRecommendation CreateCleanupRecommendation(
        CleanupPolicyId policy,
        Guid scanId = default,
        int itemCount = 0,
        long reclaimableBytes = 0,
        IReadOnlyList<CleanupScanSource>? sources = null) =>
        new()
        {
            ScanId = scanId,
            Sources = sources ?? [],
            Name = "Temporary files",
            Description = "Test",
            Glyph = "\uE74D",
            Safety = CleanupSafety.Safe,
            PolicyId = policy,
            ReclaimableBytes = reclaimableBytes,
            ItemCount = itemCount,
        };

    private static StorageFileRecord CreateRecord(
        Guid scanId,
        string parentPath,
        string path,
        long size,
        DateTimeOffset? modifiedUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new StorageFileRecord
        {
            ScanId = scanId,
            Name = Path.GetFileName(path),
            FullPath = path,
            ParentPath = parentPath,
            SizeBytes = size,
            AllocatedSizeBytes = size,
            CreatedUtc = now,
            ModifiedUtc = modifiedUtc ?? now,
            AccessedUtc = now,
            Attributes = (long)FileAttributes.Normal,
            Extension = Path.GetExtension(path),
            Category = "Other",
            Tag = "File",
        };
    }

    private static void CreateHardLink(
        string linkPath,
        string existingPath)
    {
        if (!CreateHardLinkW(linkPath, existingPath, 0))
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static void RestoreFromRecycleBin(string fileName)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application") ??
                        throw new InvalidOperationException("Shell.Application is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType) ??
                        throw new InvalidOperationException("Shell.Application could not be created.");
        dynamic recycleBin = shell.NameSpace(10);
        foreach (dynamic item in recycleBin.Items())
        {
            if (!string.Equals((string)item.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (dynamic verb in item.Verbs())
            {
                var name = (string)verb.Name;
                if (name.Replace("&", string.Empty, StringComparison.Ordinal)
                    .Contains("Restore", StringComparison.OrdinalIgnoreCase))
                {
                    verb.DoIt();
                    return;
                }
            }
        }

        throw new InvalidOperationException("The recycled test file could not be restored.");
    }
}
