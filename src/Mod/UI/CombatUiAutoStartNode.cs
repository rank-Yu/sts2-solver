using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

/// <summary>
/// Starts the ordinary full-auto session once per combat when the saved setting is on. The start
/// path is the same one F8 and <see cref="CombatUiCommands"/> use.
/// </summary>
internal sealed partial class CombatUiAutoStartNode : Node
{
    private const double IntervalSeconds = 0.1;

    private double _elapsed;

    public override void _Process(double delta)
    {
        if (!SolverSettings.Current.Automation.AutoEnableFullAuto
            || AutoCombatSession.IsRunning
            || !AutoCombatSession.CanAutoStart)
        {
            return;
        }
#if STS2_SOLVER_TESTING
        if (TestSession.Active && !TestSession.AllowAutoStartForTest)
            return;
#endif
        _elapsed += delta;
        if (_elapsed < IntervalSeconds)
            return;
        _elapsed = 0;
        NGame? host = NGame.Instance;
        if (host == null || !AutoCombatSession.IsPlayableForAutoStart(host, out _))
            return;
        AutoCombatSession.StartAuto(host, AutoCombatStartSource.Auto);
    }
}
