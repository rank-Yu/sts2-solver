// Ported from CombatSolver src/Search/SolverInterimResultOrdering.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Quality;

internal static class InterimResultOrdering
{
    public static bool IsCompleteVictory(
        int actionCount,
        bool allEnemiesDead,
        bool playerDead,
        int projectedPlayerHp)
        => actionCount > 0
            && allEnemiesDead
            && !playerDead
            && projectedPlayerHp > 0;

    /// <summary>
    /// Compares the result-quality prefix shared by in-session selection, final candidate
    /// retention, and cross-session audits. A negative value means <paramref name="candidate"/>
    /// is better. Callers pass strategic HP loss after earned growth and relic goal credit; the goals
    /// break ties before combat duration, including when every budget and allowance is zero.
    /// </summary>
    public static int ComparePrimaryQuality(
        bool candidateCompleteVictory,
        int candidateStrategicHpDeficit,
        int? candidateCombatEndedTurn,
        bool currentCompleteVictory,
        int currentStrategicHpDeficit,
        int? currentCombatEndedTurn,
        int candidateGoalHpCredit = 0,
        int currentGoalHpCredit = 0,
        int candidateGoalCount = 0,
        int currentGoalCount = 0,
        int candidateDeathSaveUseCount = 0,
        int currentDeathSaveUseCount = 0)
        => RouteQualityPolicy.Compare(
            RouteQuality.Primary(candidateCompleteVictory, candidateStrategicHpDeficit,
                candidateCombatEndedTurn, candidateGoalHpCredit, candidateGoalCount,
                candidateDeathSaveUseCount),
            RouteQuality.Primary(currentCompleteVictory, currentStrategicHpDeficit,
                currentCombatEndedTurn, currentGoalHpCredit, currentGoalCount,
                currentDeathSaveUseCount),
            RouteQualityProjection.Primary);

    internal static bool IsResourceTradeImprovement(
        int candidateHpDeficit,
        int candidatePotionCost,
        int currentHpDeficit,
        int currentPotionCost)
    {
        int candidateBurden = checked(candidateHpDeficit + candidatePotionCost);
        int currentBurden = checked(currentHpDeficit + currentPotionCost);
        return candidateBurden < currentBurden
            || candidateBurden == currentBurden && candidateHpDeficit < currentHpDeficit;
    }
}
