namespace Sts2Solver;

/// <summary>
/// The UI's only command surface. The panel never calls <see cref="SearchSession"/> or the game
/// action executor directly; every command is validated and executed on the main thread by
/// <see cref="CombatSessionController"/>.
/// </summary>
internal interface ICombatUiCommands
{
    /// <summary>Cycles Manual → AutoSearch → FullAuto. The new mode is published in the snapshot.</summary>
    bool CycleMode();

    bool SetMode(SolverMode mode);

    bool SetSolverEnabled(bool enabled);

    bool StartOrRecalculateSearch();

    bool StopSearch();

    bool AdoptCurrentRoute();

    bool ExecuteOrApplyCurrentTurn();

    bool SetFullAuto(bool enabled);
}

internal static class CombatUiCommands
{
    internal static ICombatUiCommands Instance { get; } = new Commands();

    private sealed class Commands : ICombatUiCommands
    {
        public bool CycleMode()
        {
            CombatSessionController.CycleMode();
            return true;
        }

        public bool SetMode(SolverMode mode) => CombatSessionController.SetMode(mode);

        public bool SetSolverEnabled(bool enabled)
            => CombatSessionController.SetSolverEnabled(enabled);

        public bool StartOrRecalculateSearch()
            => CombatSessionController.StartOrRecalculateSearch();

        public bool StopSearch() => CombatSessionController.StopSearch();

        public bool AdoptCurrentRoute() => CombatSessionController.AdoptCurrentRoute();

        public bool ExecuteOrApplyCurrentTurn()
            => CombatSessionController.ExecuteOrApplyCurrentTurn();

        public bool SetFullAuto(bool enabled) => CombatSessionController.SetFullAuto(enabled);
    }
}
