using StorageLens.Models;

namespace StorageLens.Services;

public static class FolderUsageInsights
{
    private const int MaximumExpansions = 64;
    private const int MaximumDepth = 16;
    private const decimal SignificantShare = 0.6m;

    public static async Task<IReadOnlyList<StorageFolderNode>> SelectLargestAsync(
        IReadOnlyList<StorageFolderNode> roots,
        Func<string, int, CancellationToken,
            Task<IReadOnlyList<StorageFolderNode>>> loadChildrenAsync,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0)
        {
            return [];
        }

        var candidates = roots
            .Where(folder => folder.DisplaySizeBytes > 0)
            .DistinctBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .Select(folder => new Candidate(folder, 0))
            .ToList();
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childLimit = (int)Math.Clamp((long)limit * 2, 8, 64);
        for (var expansion = 0; expansion < MaximumExpansions; expansion++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Rank(candidates)
                .Take(limit)
                .FirstOrDefault(item =>
                    item.Folder.HasChildren &&
                    item.Depth < MaximumDepth &&
                    !inspected.Contains(item.Folder.Path));
            if (candidate is null)
            {
                break;
            }

            var folder = candidate.Folder;
            inspected.Add(folder.Path);
            var prefix = folder.Path.TrimEnd('\\') + "\\";
            var children = (await loadChildrenAsync(
                    folder.Path,
                    childLimit,
                    cancellationToken))
                .Where(child =>
                    child.DisplaySizeBytes > 0 &&
                    child.Path.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                .DistinctBy(child => child.Path, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(child => child.DisplaySizeBytes)
                .ThenBy(child => child.Path, StringComparer.OrdinalIgnoreCase)
                .Take(childLimit)
                .ToArray();
            if (children.Length == 0)
            {
                continue;
            }

            var significantBytes = folder.DisplaySizeBytes * SignificantShare;
            var representedBytes = children.Sum(child =>
                (decimal)child.DisplaySizeBytes);
            var shouldDrillDown =
                candidate.Depth < 2 ||
                IsBroadContainer(folder) ||
                children[0].DisplaySizeBytes >= significantBytes;

            // Retain direct-file-heavy folders; never show a parent alongside its children.
            if (shouldDrillDown && representedBytes >= significantBytes)
            {
                candidates.Remove(candidate);
                candidates.AddRange(children.Select(child =>
                    new Candidate(child, candidate.Depth + 1)));
            }
        }

        return Rank(candidates)
            .Take(limit)
            .Select(item => item.Folder)
            .ToArray();
    }

    private static IOrderedEnumerable<Candidate> Rank(
        IEnumerable<Candidate> candidates) =>
        candidates
            .OrderByDescending(item => item.Folder.DisplaySizeBytes)
            .ThenBy(item => item.Folder.Path, StringComparer.OrdinalIgnoreCase);

    private static bool IsBroadContainer(StorageFolderNode folder)
    {
        var parent = Path.GetDirectoryName(folder.Path.TrimEnd('\\'));
        var parentName = Path.GetFileName(parent);
        if (folder.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parentName, "AppData", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return folder.Name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   Path.GetFileName(Path.GetDirectoryName(parent)),
                   "AppData",
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Candidate(StorageFolderNode Folder, int Depth);
}
