// Ported from CombatSolver src/Search/Probes/PrimaryIncumbentPolicy.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
using Sts2Solver.Ai.Combat.Policy;

namespace Sts2Solver.Ai.Combat.Quality;

/// <summary>
/// The existing primary member's incumbent pruning rule, shared by the Beam retention policy and
/// the Sts2Solver retained-seat lane. Pure functions over frozen request values.
/// </summary>
internal static class IncumbentPolicy
{
    internal static bool ShouldPruneByIncumbent(
        int strategicHpLowerBound,
        int turn,
        SearchIncumbent incumbent,
        bool allowTurnTieBound = true,
        bool pruneEqualHp = false)
        => strategicHpLowerBound > incumbent.EffectiveHpDeficit
            || pruneEqualHp && strategicHpLowerBound == incumbent.EffectiveHpDeficit
            || allowTurnTieBound && strategicHpLowerBound == incumbent.EffectiveHpDeficit
                && turn > incumbent.CombatEndedTurn;

    /// <summary>
    /// Tightens the bound with a complete victory the final ordering could select: no death save, no
    /// risk, and admitted by the request's potion policy (a potion route passes the same filter as the
    /// final selection). Its value is the effective deficit, so potion routes bound every state.
    /// </summary>
    internal static bool TryTightenIncumbent(
        bool candidateCompleteVictory,
        bool candidateSatisfiesHardRules,
        bool candidatePolicyAdmissible,
        int candidateEffectiveHpDeficit,
        int? candidateCombatEndedTurn,
        ref SearchIncumbent? incumbent,
        PotionUseMode? effectivePotionPolicy = null,
        int candidateDeathSaveUseCount = 0,
        int candidateGoalHpCredit = 0,
        int candidateGoalCount = 0,
        bool candidateGoalsComplete = true)
    {
        if (!candidateCompleteVictory
            || !candidateSatisfiesHardRules
            || candidateDeathSaveUseCount > 0
            || !candidatePolicyAdmissible
            || candidateCombatEndedTurn is not { } combatEndedTurn)
        {
            return false;
        }

        if (effectivePotionPolicy is not (PotionUseMode.Disabled or PotionUseMode.Smart))
            return false;

        SearchIncumbent candidate = new(candidateEffectiveHpDeficit, combatEndedTurn,
            candidateGoalHpCredit, candidateGoalCount, candidateGoalsComplete);
        if (!IsTighter(candidate, incumbent))
            return false;
        incumbent = candidate;
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> is a strictly better bound than <paramref name="current"/>.</summary>
    internal static bool IsTighter(SearchIncumbent candidate, SearchIncumbent? current)
        => current is not { } bound
            || InterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidate.EffectiveHpDeficit,
                candidate.CombatEndedTurn,
                currentCompleteVictory: true,
                currentStrategicHpDeficit: bound.EffectiveHpDeficit,
                currentCombatEndedTurn: bound.CombatEndedTurn,
                candidateGoalHpCredit: candidate.GoalHpCredit,
                currentGoalHpCredit: bound.GoalHpCredit,
                candidateGoalCount: candidate.GoalCount,
                currentGoalCount: bound.GoalCount) < 0;
}
