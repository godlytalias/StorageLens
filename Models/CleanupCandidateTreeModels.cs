using System.Collections.ObjectModel;
using StorageLens.Localization;

namespace StorageLens.Models;

public sealed class CleanupCandidateFolderSummary
{
    public required string Path { get; init; }

    public long DirectCandidateCount { get; init; }

    public long DirectCandidateBytes { get; init; }
}

public sealed partial class CleanupCandidateFolderNode
{
    private CleanupCandidateFolderNode(
        string path,
        string name,
        CleanupCandidateFolderNode? parent,
        Guid scanId)
    {
        Path = path;
        Name = name;
        Parent = parent;
        ScanId = scanId;
    }

    public Guid ScanId { get; }

    public string Path { get; }

    public string Name { get; }

    public string DisplayPath => StorageText.IsolatePath(Path);

    public string AccessibleSummary =>
        $"{Name}, {SummaryText}";

    public CleanupCandidateFolderNode? Parent { get; }

    public ObservableCollection<CleanupCandidateFolderNode> Children { get; } = [];

    public long CandidateCount { get; private set; }

    public long CandidateBytes { get; private set; }

    public long DirectCandidateCount { get; private set; }

    public long DirectCandidateBytes { get; private set; }

    public bool HasChildren => Children.Count > 0;

    public string SummaryText =>
        AppText.Get("CleanupFolderSummaryFormat")
            .Replace(
                "{0}",
                CandidateCount.ToString(
                    System.Globalization.CultureInfo.CurrentCulture),
                StringComparison.Ordinal)
            .Replace(
                "{1}",
                StorageFormatter.FormatBytes(CandidateBytes),
                StringComparison.Ordinal);

    public bool IsExpanded { get; set; }

    public bool IsSelected { get; set; }

    public static IReadOnlyList<CleanupCandidateFolderNode> BuildTree(
        IEnumerable<CleanupCandidateFolderSummary> summaries,
        Guid scanId = default)
    {
        var nodes = new Dictionary<string, CleanupCandidateFolderNode>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var summary in summaries)
        {
            if (string.IsNullOrWhiteSpace(summary.Path))
            {
                continue;
            }

            var node = GetOrCreateNode(summary.Path, nodes, scanId);
            node.DirectCandidateCount = summary.DirectCandidateCount;
            node.DirectCandidateBytes = summary.DirectCandidateBytes;
            for (var current = node; current is not null; current = current.Parent)
            {
                current.CandidateCount += summary.DirectCandidateCount;
                current.CandidateBytes += summary.DirectCandidateBytes;
            }
        }

        var roots = nodes.Values
            .Where(node => node.Parent is null)
            .OrderByDescending(node => node.CandidateBytes)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var root in roots)
        {
            SortChildren(root);
        }

        if (roots.Length > 0)
        {
            roots[0].IsExpanded = true;
        }

        return roots;
    }

    private static CleanupCandidateFolderNode GetOrCreateNode(
        string path,
        Dictionary<string, CleanupCandidateFolderNode> nodes,
        Guid scanId)
    {
        var normalized = NormalizePath(path);
        if (nodes.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var parentPath = GetParentPath(normalized);
        var parent = parentPath is null
            ? null
            : GetOrCreateNode(parentPath, nodes, scanId);
        var name = GetDisplayName(normalized);
        var node = new CleanupCandidateFolderNode(
            normalized,
            name,
            parent,
            scanId);
        nodes.Add(normalized, node);
        parent?.Children.Add(node);
        return node;
    }

    private static void SortChildren(CleanupCandidateFolderNode node)
    {
        var children = node.Children
            .OrderByDescending(child => child.CandidateBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        node.Children.Clear();
        foreach (var child in children)
        {
            node.Children.Add(child);
            SortChildren(child);
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = System.IO.Path.GetFullPath(path);
        return normalized.Length == 3 &&
               normalized[1] == ':' &&
               normalized[2] == '\\'
            ? normalized
            : normalized.TrimEnd('\\');
    }

    private static string? GetParentPath(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        if (root is not null &&
            string.Equals(
                NormalizePath(root),
                path,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return System.IO.Path.GetDirectoryName(path) is { } parent
            ? NormalizePath(parent)
            : null;
    }

    private static string GetDisplayName(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        return root is not null &&
               string.Equals(
                   NormalizePath(root),
                   path,
                   StringComparison.OrdinalIgnoreCase)
            ? path
            : System.IO.Path.GetFileName(path);
    }
}
