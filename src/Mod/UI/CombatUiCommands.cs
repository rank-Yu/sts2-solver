using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

/// <summary>
/// The UI's only command surface. The overlay never calls <see cref="SearchSession"/> or the game
/// action executor directly; single-turn commands will be added here with their session state machine.
/// </summary>
internal interface ICombatUiCommands
{
    void StartAuto();

    void StopAuto(string reason);
}

internal static class CombatUiCommands
{
    internal static ICombatUiCommands Instance { get; } = new Commands();

    private sealed class Commands : ICombatUiCommands
    {
        public void StartAuto()
        {
            NGame? host = NGame.Instance;
            if (host == null)
            {
                ModLog.Error("[Sts2Solver] AUTO_REJECTED reason=NGame_instance_missing");
                return;
            }
            AutoCombatSession.StartAuto(host, AutoCombatStartSource.Command);
        }

        public void StopAuto(string reason)
            => AutoCombatSession.Cancel(string.IsNullOrEmpty(reason) ? "ui" : reason);
    }
}
