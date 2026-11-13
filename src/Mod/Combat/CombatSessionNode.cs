// Main-thread frame sampling follows CombatSolver src/Runtime/SolverDispatcher.cs at
// 9f83bea77127830d91dd47db4bac6323414bbbf4 (MIT, Copyright (c) 2026 Torch1230 and contributors).
using System.Diagnostics;
using Godot;

namespace Sts2Solver;

/// <summary>
/// Main-thread pump for search progress and live elapsed time. It does not poll combat readiness:
/// turn starts arrive through <see cref="CombatSessionController.OnTurnStarted"/>.
/// </summary>
internal sealed partial class CombatSessionNode : Node
{
    private long _lastFrameTimestamp;

    public override void _Process(double delta)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp != 0)
            SearchSession.ObserveMainThreadFrame(
                Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalMilliseconds);
        _lastFrameTimestamp = now;
        CombatSessionController.Pump();
    }
}
