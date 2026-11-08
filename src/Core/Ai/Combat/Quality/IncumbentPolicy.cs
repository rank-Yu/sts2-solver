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
        => strategicHpLowerBound > incumbent.StrategicHpDeficit
            || pruneEqualHp && strategicHpLowerBound == incumbent.StrategicHpDeficit
            || allowTurnTieBound && strategicHpLowerBound == incumbent.StrategicHpDeficit
                && turn > incumbent.CombatEndedTurn;

    internal static bool TryTightenIncumbent(
        bool candidateCompleteVictory,
        bool candidateSatisfiesHardRules,
        int candidateExplicitPotionUses,
        int candidateStrategicHpDeficit,
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
            || candidateExplicitPotionUses != 0
            || candidateCombatEndedTurn is not { } combatEndedTurn)
        {
            return false;
        }

        // A complete, hard-policy-compliant potion-free victory is eligible under Disabled/Smart.
        if (effectivePotionPolicy is not (PotionUseMode.Disabled or PotionUseMode.Smart))
            return false;

        SearchIncumbent candidate = new(candidateStrategicHpDeficit, combatEndedTurn,
            candidateGoalHpCredit, candidateGoalCount, candidateGoalsComplete);
        if (incumbent is { } current
            && InterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidate.StrategicHpDeficit,
                candidate.CombatEndedTurn,
                currentCompleteVictory: true,
                currentStrategicHpDeficit: current.StrategicHpDeficit,
                currentCombatEndedTurn: current.CombatEndedTurn,
                candidateGoalHpCredit: candidate.GoalHpCredit,
                currentGoalHpCredit: current.GoalHpCredit,
                candidateGoalCount: candidate.GoalCount,
                currentGoalCount: current.GoalCount) >= 0)
        {
            return false;
        }

        incumbent = candidate;
        return true;
    }
}
