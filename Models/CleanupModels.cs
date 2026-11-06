using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StorageLens.Models;

public enum CleanupPolicyId
{
    TemporaryFiles,
    OldDownloads,
    PackageCaches,
    LargeDuplicates,
    PreviousWindowsInstallation,
}

public sealed partial class CleanupCandidate : ObservableObject
{
    public required Guid ScanId { get; init; }

    public required CleanupPolicyId PolicyId { get; init; }

    public required StorageItem Item { get; init; }

    public required StorageFileRecord FileRecord { get; init; }

    public string EligibilityReason { get; init; } = string.Empty;

    public bool IsEligibleForAutomaticCleanup { get; init; }

    public bool IsSelectionEnabled { get; init; } = true;

    public bool IsInspectionOnly => !IsSelectionEnabled;

    public string AccessibleSummary =>
        $"{Item.Name}, {StorageFormatter.FormatBytes(Item.DisplaySizeBytes)}, {Item.DisplayPath}";

    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    [RelayCommand]
    private void SetSelection(bool? selected)
    {
        if (IsSelectionEnabled)
        {
            IsSelected = selected == true;
        }
    }
}

public sealed class CleanupRequest
{
    public required IReadOnlyList<CleanupCandidate> Candidates { get; init; }

    public bool IsAutomatic { get; init; }
}

public sealed class CleanupProgress
{
    public int Processed { get; init; }

    public int Total { get; init; }

    public long BytesRecycled { get; init; }

    public string CurrentPath { get; init; } = string.Empty;
}

public sealed class CleanupFailure
{
    public required string Path { get; init; }

    public required string Error { get; init; }
}

public sealed class CleanupResult
{
    public int SucceededCount { get; init; }

    public int FailedCount { get; init; }

    public int SkippedCount { get; init; }

    public int ExcludedCount { get; init; }

    public long BytesRecycled { get; init; }

    public int RemainingIndexedCount { get; init; }

    public IReadOnlyList<CleanupFailure> Failures { get; init; } = [];
}

public sealed class CleanupHistoryItem
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    public required CleanupPolicyId Policy { get; init; }

    public required bool Automatic { get; init; }

    public required string Result { get; init; }

    public string? Error { get; init; }
}
