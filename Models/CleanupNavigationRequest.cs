namespace StorageLens.Models;

public sealed class CleanupNavigationRequest
{
    public string? DriveLetter { get; init; }

    public string? RecommendationName { get; init; }

    public IReadOnlyList<CleanupPolicyId> PolicyIds { get; init; } = [];

    public bool Refresh { get; init; }
}
