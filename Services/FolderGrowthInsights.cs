using StorageLens.Models;

namespace StorageLens.Services;

public static class FolderGrowthInsights
{
    private const decimal SignificantShare = 0.6m;

    public static IReadOnlyList<FolderGrowthInsight> SelectLargest(
        IReadOnlyList<FolderGrowthInsight> candidates,
        int limit)
    {
        if (limit <= 0 || candidates.Count == 0)
        {
            return [];
        }

        var distinct = candidates
            .Where(candidate => candidate.AddedBytes > 0)
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nodes = distinct
            .Select(candidate => new GrowthNode(candidate))
            .ToList();
        foreach (var node in nodes)
        {
            node.Parent = nodes
                .Where(parent =>
                    parent != node &&
                    IsAncestor(parent.Insight.Path, node.Insight.Path))
                .OrderByDescending(parent =>
                    parent.Insight.Path.TrimEnd('\\').Length)
                .FirstOrDefault();
            node.Parent?.Children.Add(node);
        }

        var selected = new List<FolderGrowthInsight>();
        foreach (var root in nodes.Where(node => node.Parent is null))
        {
            Select(root, selected);
        }

        return selected
            .OrderByDescending(insight => insight.AddedBytes)
            .ThenBy(insight => insight.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static void Select(
        GrowthNode node,
        ICollection<FolderGrowthInsight> selected)
    {
        var depth = GetDepth(node.Insight.Path);
        if (depth == 0)
        {
            foreach (var child in node.Children)
            {
                Select(child, selected);
            }

            return;
        }

        if (node.Children.Count == 0)
        {
            selected.Add(node.Insight);
            return;
        }

        var children = node.Children
            .OrderByDescending(child => child.Insight.AddedBytes)
            .ToList();
        var representedBytes = children.Sum(
            child => (decimal)child.Insight.AddedBytes);
        var significantBytes =
            node.Insight.AddedBytes * SignificantShare;
        var shouldDrillDown =
            depth < 2 ||
            IsBroadContainer(node.Insight) ||
            children[0].Insight.AddedBytes >= significantBytes;
        if (shouldDrillDown && representedBytes >= significantBytes)
        {
            foreach (var child in children)
            {
                Select(child, selected);
            }

            return;
        }

        selected.Add(node.Insight);
    }

    private static bool IsAncestor(string parent, string child)
    {
        var prefix = parent.TrimEnd('\\') + "\\";
        return !string.Equals(
                   parent.TrimEnd('\\'),
                   child.TrimEnd('\\'),
                   StringComparison.OrdinalIgnoreCase) &&
               child.StartsWith(
                   prefix,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static int GetDepth(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var relative = path[root.Length..].Trim('\\');
        return string.IsNullOrEmpty(relative)
            ? 0
            : relative.Split(
                '\\',
                StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static bool IsBroadContainer(FolderGrowthInsight folder)
    {
        var trimmed = folder.Path.TrimEnd('\\');
        var parent = Path.GetDirectoryName(trimmed);
        var parentName = Path.GetFileName(parent);
        if (folder.Name.Equals(
                "AppData",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                parentName,
                "AppData",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return folder.Name.Equals(
                   "Microsoft",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   Path.GetFileName(Path.GetDirectoryName(parent)),
                   "AppData",
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GrowthNode(FolderGrowthInsight insight)
    {
        public FolderGrowthInsight Insight { get; } = insight;

        public GrowthNode? Parent { get; set; }

        public List<GrowthNode> Children { get; } = [];
    }
}
