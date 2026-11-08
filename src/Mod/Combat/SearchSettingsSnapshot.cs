// Value snapshot of the search inputs that live outside the frozen root. The stop-continuation
// display check compares two snapshots without compiling a request.
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>
/// The tier, thread count, memory ceiling and captured policy configuration a search request was
/// built from. Two snapshots are equal when every input that enters the request is equal; the
/// policy directive list is compared element by element because it is a list, not a value.
/// </summary>
internal sealed record SearchSettingsSnapshot(
    SearchPerformanceTier Tier,
    int Threads,
    long MemoryBudgetBytes,
    SearchPolicyConfiguration Policy)
{
    internal static SearchSettingsSnapshot Capture(SearchPerformanceTier tier,
        SearchTheftPolicy? theftPolicy)
        => new(tier, SearchThreadCount.Value, SearchMemoryBudget.Bytes,
            SolverSearchPolicy.Capture(theftPolicy));

    internal bool Matches(SearchSettingsSnapshot other)
        => Tier == other.Tier
            && Threads == other.Threads
            && MemoryBudgetBytes == other.MemoryBudgetBytes
            && PolicyMatches(Policy, other.Policy);

    private static bool PolicyMatches(SearchPolicyConfiguration left,
        SearchPolicyConfiguration right)
    {
        if (left.GrowthBudgets != right.GrowthBudgets
            || left.IgnoreLongTermRewards != right.IgnoreLongTermRewards
            || left.BrightestFlameMaxHpLossLimit != right.BrightestFlameMaxHpLossLimit
            || left.ActTransitionBossHpStrategy != right.ActTransitionBossHpStrategy
            || left.FinalBossHpStrategy != right.FinalBossHpStrategy
            || left.PotionDefaultMode != right.PotionDefaultMode
            || left.TheftPolicy != right.TheftPolicy
            || left.PotionDirectives.Count != right.PotionDirectives.Count
            || left.RelicCounterAllowances.Count != right.RelicCounterAllowances.Count)
        {
            return false;
        }
        foreach ((string relicId, int hp) in left.RelicCounterAllowances)
            if (!right.RelicCounterAllowances.TryGetValue(relicId, out int other) || other != hp)
                return false;
        for (int index = 0; index < left.PotionDirectives.Count; index++)
            if (left.PotionDirectives[index] != right.PotionDirectives[index])
                return false;
        return true;
    }
}
