using Godot;

namespace Sts2Solver;

/// <summary>
/// Main-thread pump for search progress and live elapsed time. It does not poll combat readiness:
/// turn starts arrive through <see cref="CombatSessionController.OnTurnStarted"/>.
/// </summary>
internal sealed partial class CombatSessionNode : Node
{
    public override void _Process(double delta) => CombatSessionController.Pump();
}
