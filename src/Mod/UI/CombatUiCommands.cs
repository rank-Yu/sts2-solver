using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>
/// The UI's only command surface. The panel never calls <see cref="SearchSession"/> or the game
/// action executor directly; every command is validated and executed on the main thread by
/// <see cref="CombatSessionController"/>.
/// </summary>
internal interface ICombatUiCommands
{
    bool SetMode(SolverMode mode);

    bool StartOrRecalculateSearch();

    bool StopSearch();

    bool ExecuteCurrentTurn();

    bool StopDeployment();

    bool SetPotionDirective(int slot, string potionId, SolverPotionMode mode);

    bool SetAllPotionDirectives(SolverPotionMode mode);

    bool SetOnlyForcedPotionDirectives();

    bool SetGrowthBudget(GrowthSource source, int value);

    bool SetIgnoreLongTermRewards(bool ignore);

    bool SetActTransitionBossHpStrategy(SearchBossHpStrategy strategy);

    bool SetFinalBossHpStrategy(SearchBossHpStrategy strategy);

    bool SetTheftPolicy(SearchTheftPolicy policy);
}

internal static class CombatUiCommands
{
    internal static ICombatUiCommands Instance { get; } = new Commands();

    private sealed class Commands : ICombatUiCommands
    {
        public bool SetMode(SolverMode mode) => CombatSessionController.SetMode(mode);

        public bool StartOrRecalculateSearch()
            => CombatSessionController.StartOrRecalculateSearch();

        public bool StopSearch() => CombatSessionController.StopSearch();

        public bool ExecuteCurrentTurn() => CombatSessionController.ExecuteCurrentTurn();

        public bool StopDeployment() => CombatSessionController.StopDeployment();

        public bool SetPotionDirective(int slot, string potionId, SolverPotionMode mode)
            => CombatSessionController.SetPotionDirective(slot, potionId, mode);

        public bool SetAllPotionDirectives(SolverPotionMode mode)
            => CombatSessionController.SetAllPotionDirectives(mode);

        public bool SetOnlyForcedPotionDirectives()
            => CombatSessionController.SetOnlyForcedPotionDirectives();

        public bool SetGrowthBudget(GrowthSource source, int value)
            => CombatSessionController.SetGrowthBudget(source, value);

        public bool SetIgnoreLongTermRewards(bool ignore)
            => CombatSessionController.SetIgnoreLongTermRewards(ignore);

        public bool SetActTransitionBossHpStrategy(SearchBossHpStrategy strategy)
            => CombatSessionController.SetActTransitionBossHpStrategy(strategy);

        public bool SetFinalBossHpStrategy(SearchBossHpStrategy strategy)
            => CombatSessionController.SetFinalBossHpStrategy(strategy);

        public bool SetTheftPolicy(SearchTheftPolicy policy)
            => CombatSessionController.SetTheftPolicy(policy);
    }
}
