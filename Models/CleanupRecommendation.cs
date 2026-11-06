using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageLens.Localization;

namespace StorageLens.Models;

public enum CleanupSafety
{
    Safe,
    Review,
    Caution,
}

public sealed partial class CleanupRecommendation : ObservableObject
{
    private bool _isSelected;
    private long _selectedItemCount = -1;
    private long _selectedReclaimableBytes = -1;
    private long _reclaimableBytes;
    private int _itemCount;

    public required string Name { get; init; }

    public required string Description { get; init; }

    public string DisplayName =>
        StorageText.PolicyName(PolicyId, Name);

    public string DisplayDescription =>
        StorageText.PolicyDescription(PolicyId, Description);

    public required string Glyph { get; init; }

    public required CleanupSafety Safety { get; init; }

    public CleanupPolicyId PolicyId { get; init; }

    public Guid ScanId { get; init; }

    public IReadOnlyList<CleanupScanSource> Sources { get; init; } = [];

    public required long ReclaimableBytes
    {
        get => _reclaimableBytes;
        init => _reclaimableBytes = value;
    }

    public required int ItemCount
    {
        get => _itemCount;
        init => _itemCount = value;
    }

    public long SelectedItemCount =>
        _selectedItemCount < 0 ? ItemCount : _selectedItemCount;

    public long SelectedReclaimableBytes =>
        _selectedReclaimableBytes < 0
            ? ReclaimableBytes
            : _selectedReclaimableBytes;

    public bool HasExclusions =>
        SelectedItemCount < ItemCount ||
        SelectedReclaimableBytes < ReclaimableBytes;

    public IReadOnlyList<StorageItem> Candidates { get; init; } = [];

    public bool IsAutomaticEligible { get; init; }

    public string SafetyLabel => AppText.Get(Safety switch
    {
        CleanupSafety.Safe => "SafetySafe",
        CleanupSafety.Caution => "SafetyCaution",
        _ => "SafetyReview",
    });

    public string ItemCountText =>
        StorageFormatter.FormatItemCount(SelectedItemCount);

    public string AccessibleSummary =>
        $"{DisplayName}, {SafetyLabel}, {ItemCountText}, {StorageFormatter.FormatBytes(SelectedReclaimableBytes)}";

    public void UpdateSelection(long selectedCount, long selectedBytes)
    {
        var countChanged = _selectedItemCount != selectedCount;
        var bytesChanged = _selectedReclaimableBytes != selectedBytes;
        _selectedItemCount = Math.Clamp(selectedCount, 0, ItemCount);
        _selectedReclaimableBytes = Math.Clamp(selectedBytes, 0, ReclaimableBytes);
        if (countChanged)
        {
            OnPropertyChanged(nameof(SelectedItemCount));
            OnPropertyChanged(nameof(ItemCountText));
            OnPropertyChanged(nameof(AccessibleSummary));
            OnPropertyChanged(nameof(HasExclusions));
        }

        if (bytesChanged)
        {
            OnPropertyChanged(nameof(SelectedReclaimableBytes));
            OnPropertyChanged(nameof(HasExclusions));
            OnPropertyChanged(nameof(AccessibleSummary));
        }
    }

    public void UpdateCandidateTotals(long candidateCount, long bytes)
    {
        var updatedCount = checked((int)Math.Min(candidateCount, int.MaxValue));
        var totalsChanged =
            _itemCount != updatedCount ||
            _reclaimableBytes != bytes;
        _itemCount = Math.Max(0, updatedCount);
        _reclaimableBytes = Math.Max(0, bytes);
        if (!totalsChanged)
        {
            return;
        }

        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(ReclaimableBytes));
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(HasExclusions));
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedReclaimableBytes));
        OnPropertyChanged(nameof(AccessibleSummary));
    }

    public CleanupRecommendation CreateReviewSnapshot()
    {
        var snapshot = new CleanupRecommendation
        {
            Name = Name,
            Description = Description,
            Glyph = Glyph,
            Safety = Safety,
            PolicyId = PolicyId,
            ScanId = ScanId,
            Sources = Sources.ToArray(),
            ReclaimableBytes = ReclaimableBytes,
            ItemCount = ItemCount,
            Candidates = Candidates,
            IsAutomaticEligible = IsAutomaticEligible,
            IsSelected = IsSelected,
        };
        snapshot.UpdateSelection(
            SelectedItemCount,
            SelectedReclaimableBytes);
        return snapshot;
    }

    public bool IsSafe => Safety == CleanupSafety.Safe;

    public bool IsReview => Safety == CleanupSafety.Review;

    public bool IsCaution => Safety == CleanupSafety.Caution;

    public bool IsActionable => !IsCaution;

    public bool IsWindowsManaged =>
        PolicyId == CleanupPolicyId.PreviousWindowsInstallation;

    public string SelectionAutomationId => $"CleanupPolicy_{PolicyId}";

    public string ActionLabel => AppText.Get(
        PolicyId switch
        {
            CleanupPolicyId.PreviousWindowsInstallation =>
                "CleanupActionManagedWindows",
            _ => "CleanupActionInspectFiles",
        });

    public string SpaceLabel => AppText.Get(
        PolicyId switch
        {
            CleanupPolicyId.PreviousWindowsInstallation =>
                "CleanupWindowsManagedSpaceLabel",
            _ => "CleanupReclaimableSpaceLabel",
        });

    [RelayCommand]
    private void SetSelection(bool? selected)
    {
        if (IsActionable)
        {
            IsSelected = selected == true;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
