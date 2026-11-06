using StorageLens.Services;

namespace StorageLens.Models;

public sealed class CleanupCandidateReviewRequest
{
    public required CleanupRecommendation Recommendation { get; init; }

    public required CleanupCandidateSelectionState SelectionState { get; init; }
}
