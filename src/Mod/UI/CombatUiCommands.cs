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
    }
}
