using StorageLens.Models;

namespace StorageLens.Services;

internal sealed class ScheduledCleanupRunner
{
    private const int MaximumCandidatesPerRun = 5_000;
    private const int PageSize = 1_000;
    private readonly Func<IReadOnlyList<DriveSummary>> _getAvailableDrives;
    private readonly Func<
        DriveSummary,
        CleanupPolicyId,
        int,
        int,
        CancellationToken,
        Task<IReadOnlyList<CleanupCandidate>>> _getCandidates;
    private readonly Func<
        CleanupRequest,
        CancellationToken,
        Task<CleanupResult>> _recycle;

    public ScheduledCleanupRunner(
        Func<IReadOnlyList<DriveSummary>> getAvailableDrives,
        Func<
            DriveSummary,
            CleanupPolicyId,
            int,
            int,
            CancellationToken,
            Task<IReadOnlyList<CleanupCandidate>>> getCandidates,
        Func<
            CleanupRequest,
            CancellationToken,
            Task<CleanupResult>> recycle)
    {
        _getAvailableDrives = getAvailableDrives;
        _getCandidates = getCandidates;
        _recycle = recycle;
    }

    public async Task<CleanupResult> RunAsync(
        ScheduledCleanupTaskSnapshot schedule,
        CancellationToken cancellationToken = default)
    {
        if (!schedule.IsEnabled || schedule.Mode != CleanupMode.RecycleSafeItems)
        {
            return new CleanupResult();
        }

        var policies = schedule.Categories
            .Select(CleanupPolicyService.ParsePolicy)
            .Where(policy => policy.HasValue)
            .Select(policy => policy!.Value)
            .Where(policy =>
                CleanupPolicyService.GetDefinition(policy).AutomaticEligible)
            .Distinct()
            .ToList();
        if (policies.Count == 0)
        {
            return new CleanupResult();
        }

        var candidates = new List<CleanupCandidate>();
        foreach (var drive in _getAvailableDrives())
        {
            foreach (var policy in policies)
            {
                var offset = 0;
                while (candidates.Count < MaximumCandidatesPerRun)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await _getCandidates(
                        drive,
                        policy,
                        PageSize,
                        offset,
                        cancellationToken);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    candidates.AddRange(
                        page.Where(item =>
                                item.IsEligibleForAutomaticCleanup)
                            .Take(MaximumCandidatesPerRun - candidates.Count));
                    offset += page.Count;
                    if (page.Count < PageSize)
                    {
                        break;
                    }
                }

                if (candidates.Count >= MaximumCandidatesPerRun)
                {
                    break;
                }
            }

            if (candidates.Count >= MaximumCandidatesPerRun)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            return new CleanupResult();
        }

        return await _recycle(
            new CleanupRequest
            {
                Candidates = candidates,
                IsAutomatic = true,
            },
            cancellationToken);
    }
}
