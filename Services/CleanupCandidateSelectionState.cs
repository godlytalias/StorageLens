using StorageLens.Models;

namespace StorageLens.Services;

public readonly record struct CleanupCandidateSelectionSummary(
    long SelectedCount,
    long SelectedBytes);

public sealed class CleanupCandidateSelectionState
{
    private readonly Dictionary<SelectionScope, ScopeState> _scopes = [];
    private long _sequence;

    public bool IsSelected(CleanupCandidate candidate)
    {
        if (!_scopes.TryGetValue(GetScope(candidate), out var scope))
        {
            return true;
        }

        var hasFileOverride = scope.Files.TryGetValue(
            GetCandidateKey(candidate),
            out var fileOverride);
        var folderOverride = GetFolderOverride(scope, candidate.Item.Path);
        if (!hasFileOverride)
        {
            return folderOverride?.IsSelected ?? true;
        }

        return fileOverride.Sequence > (folderOverride?.Sequence ?? 0)
            ? fileOverride.IsSelected
            : folderOverride?.IsSelected ?? true;
    }

    public void SetFileSelected(
        CleanupCandidate candidate,
        bool isSelected)
    {
        var scope = GetOrCreateScope(candidate);
        var path = NormalizePath(candidate.Item.Path);
        scope.Files[GetCandidateKey(candidate)] = new FileOverride(
            path,
            GetParentPath(path),
            candidate.Item.DisplaySizeBytes,
            isSelected,
            ++_sequence);
    }

    public void SetFolderSelected(
        CleanupRecommendation recommendation,
        Guid scanId,
        string folderPath,
        bool isSelected)
    {
        var scope = GetOrCreateScope(scanId, recommendation.PolicyId);
        scope.Folders[NormalizeFolder(folderPath)] = new SelectionOverride(
            isSelected,
            ++_sequence);
    }

    public void RegisterFolderTree(
        Guid scanId,
        CleanupPolicyId policy,
        IReadOnlyList<CleanupCandidateFolderNode> roots)
    {
        GetOrCreateScope(scanId, policy).Roots = roots.ToArray();
    }

    public CleanupCandidateSelectionState CreateSnapshot()
    {
        var snapshot = new CleanupCandidateSelectionState
        {
            _sequence = _sequence,
        };
        foreach (var pair in _scopes)
        {
            var scope = snapshot.GetOrCreateScope(
                pair.Key.ScanId,
                pair.Key.Policy);
            scope.Roots = pair.Value.Roots.ToArray();
            foreach (var file in pair.Value.Files)
            {
                scope.Files.Add(file.Key, file.Value);
            }

            foreach (var folder in pair.Value.Folders)
            {
                scope.Folders.Add(folder.Key, folder.Value);
            }
        }

        return snapshot;
    }

    public void SetAllSelected(
        CleanupRecommendation recommendation,
        bool isSelected)
    {
        foreach (var source in recommendation.Sources)
        {
            SetFolderSelected(
                recommendation,
                source.ScanId,
                source.DriveLetter.TrimEnd('\\') + "\\",
                isSelected);
        }
    }

    public CleanupCandidateSelectionSummary GetPolicySelectionSummary(
        CleanupRecommendation recommendation)
    {
        if (recommendation.Sources.Count > 0)
        {
            var summaries = recommendation.Sources.Select(source =>
                GetSourceSelectionSummary(
                    source,
                    recommendation.PolicyId));
            var selectedCount = 0L;
            var selectedBytes = 0L;
            foreach (var summary in summaries)
            {
                selectedCount += summary.SelectedCount;
                selectedBytes += summary.SelectedBytes;
            }

            return new CleanupCandidateSelectionSummary(
                selectedCount,
                selectedBytes);
        }

        return GetSourceSelectionSummary(
            new CleanupScanSource(
                recommendation.ScanId,
                string.Empty,
                DateTimeOffset.MinValue,
                recommendation.ItemCount,
                recommendation.ReclaimableBytes),
            recommendation.PolicyId);
    }

    private CleanupCandidateSelectionSummary GetSourceSelectionSummary(
        CleanupScanSource source,
        CleanupPolicyId policy)
    {
        if (!_scopes.TryGetValue(
                new SelectionScope(source.ScanId, policy),
                out var scope))
        {
            return new CleanupCandidateSelectionSummary(
                source.ItemCount,
                source.SizeBytes);
        }

        if (scope.Roots.Count == 0)
        {
            var included = GetFolderOverride(
                scope,
                source.DriveLetter.TrimEnd('\\') + "\\")
                ?.IsSelected ?? true;
            return new CleanupCandidateSelectionSummary(
                included ? source.ItemCount : 0,
                included ? source.SizeBytes : 0);
        }

        var selectedCount = 0L;
        var selectedBytes = 0L;
        foreach (var root in scope.Roots)
        {
            var summary = GetFolderSelectionSummary(scope, root);
            selectedCount += summary.SelectedCount;
            selectedBytes += summary.SelectedBytes;
        }

        return new CleanupCandidateSelectionSummary(
            Math.Min(source.ItemCount, selectedCount),
            Math.Min(source.SizeBytes, selectedBytes));
    }

    public CleanupCandidateSelectionSummary GetFolderSelectionSummary(
        CleanupRecommendation recommendation,
        Guid scanId,
        CleanupCandidateFolderNode folder)
    {
        if (!_scopes.TryGetValue(
                new SelectionScope(
                    scanId,
                    recommendation.PolicyId),
                out var scope))
        {
            return new CleanupCandidateSelectionSummary(
                folder.CandidateCount,
                folder.CandidateBytes);
        }

        return GetFolderSelectionSummary(scope, folder);
    }

    private static CleanupCandidateSelectionSummary
        GetFolderSelectionSummary(
            ScopeState scope,
            CleanupCandidateFolderNode folder)
    {
        var folderPath = NormalizeFolder(folder.Path);
        var baseOverride = GetFolderOverride(scope, folderPath);
        var baseSelected = baseOverride?.IsSelected ?? true;
        var selectedCount = baseSelected
            ? folder.DirectCandidateCount
            : 0;
        var selectedBytes = baseSelected
            ? folder.DirectCandidateBytes
            : 0;

        foreach (var fileOverride in scope.Files.Values)
        {
            if (!string.Equals(
                    fileOverride.ParentPath,
                    folderPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inheritedSelection =
                GetFolderOverride(scope, fileOverride.Path);
            var fileSelected = fileOverride.Sequence >
                               (inheritedSelection?.Sequence ?? 0)
                ? fileOverride.IsSelected
                : inheritedSelection?.IsSelected ?? true;
            if (fileSelected == baseSelected)
            {
                continue;
            }

            selectedCount += fileSelected ? 1 : -1;
            selectedBytes += fileSelected
                ? fileOverride.SizeBytes
                : -fileOverride.SizeBytes;
        }

        foreach (var child in folder.Children)
        {
            var childSummary = GetFolderSelectionSummary(scope, child);
            selectedCount += childSummary.SelectedCount;
            selectedBytes += childSummary.SelectedBytes;
        }

        return new CleanupCandidateSelectionSummary(
            Math.Max(0, selectedCount),
            Math.Max(0, selectedBytes));
    }

    public IReadOnlyList<CleanupCandidate> FilterSelected(
        IReadOnlyList<CleanupCandidate> candidates)
    {
        return candidates
            .Where(IsSelected)
            .ToList();
    }

    private ScopeState GetOrCreateScope(CleanupCandidate candidate) =>
        GetOrCreateScope(candidate.ScanId, candidate.PolicyId);

    private ScopeState GetOrCreateScope(
        Guid scanId,
        CleanupPolicyId policy)
    {
        var key = new SelectionScope(
            scanId,
            policy);
        return _scopes.TryGetValue(key, out var scope)
            ? scope
            : _scopes[key] = new ScopeState();
    }

    private static SelectionScope GetScope(CleanupCandidate candidate) =>
        new(
            candidate.ScanId,
            candidate.PolicyId);

    private static string GetCandidateKey(
        CleanupCandidate candidate)
    {
        var pathKey = NormalizePath(candidate.Item.Path);
        var identity = string.IsNullOrWhiteSpace(
            candidate.FileRecord.FileId128)
            ? candidate.FileRecord.FileIndexHigh
                .ToString(System.Globalization.CultureInfo.InvariantCulture) +
              ":" +
              candidate.FileRecord.FileIndexLow
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
            : candidate.FileRecord.FileId128;
        return identity == "0:0"
            ? pathKey
            : $"{candidate.FileRecord.VolumeSerialNumber}:{identity}:{pathKey}";
    }

    private static SelectionOverride? GetFolderOverride(
        ScopeState scope,
        string path)
    {
        SelectionOverride? result = null;
        foreach (var pair in scope.Folders)
        {
            if (!IsWithinFolder(path, pair.Key) ||
                (result is not null &&
                 pair.Value.Sequence <= result.Value.Sequence))
            {
                continue;
            }

            result = pair.Value;
        }

        return result;
    }

    private static bool IsWithinFolder(string path, string folderPath) =>
        string.Equals(
            path,
            folderPath,
            StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(
            GetFolderPrefix(folderPath),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        path.Trim().ToUpperInvariant();

    private static string NormalizeFolder(string path)
    {
        var normalized = NormalizePath(path);
        return normalized.Length == 3 && normalized[2] == '\\'
            ? normalized
            : normalized.TrimEnd('\\');
    }

    private static string GetParentPath(string path)
    {
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        return string.IsNullOrWhiteSpace(parent)
            ? NormalizePath(path)
            : NormalizeFolder(parent);
    }

    private static string GetFolderPrefix(string folderPath)
    {
        var normalized = NormalizeFolder(folderPath);
        return normalized.EndsWith('\\')
            ? normalized
            : normalized + "\\";
    }

    private readonly record struct SelectionScope(
        Guid ScanId,
        CleanupPolicyId Policy);

    private sealed class ScopeState
    {
        public IReadOnlyList<CleanupCandidateFolderNode> Roots { get; set; } = [];

        public Dictionary<string, FileOverride> Files { get; } = new(
            StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, SelectionOverride> Folders { get; } = new(
            StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct SelectionOverride(
        bool IsSelected,
        long Sequence);

    private readonly record struct FileOverride(
        string Path,
        string ParentPath,
        long SizeBytes,
        bool IsSelected,
        long Sequence);
}
