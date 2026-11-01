#if STS2_SOLVER_TESTING
// Test-only hooks used by the controller scenarios. Production builds do not compile this file,
// so the controller has no test-host dependency.
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;

namespace Sts2Solver;

internal static class CombatControllerTestHooks
{
    internal static Func<CombatState, int, CancellationToken, Task>? BeforeAutoSearch { get; set; }

    /// <summary>Returns a deployment failure reason for the action, or null to execute it.</summary>
    internal static Func<ReplayedAction, int, string?>? ForceDeploymentFailure { get; set; }

    /// <summary>Search count observed when the native turn-setup selector was asked to choose.</summary>
    internal static int TurnSetupSearchCountAtSelection { get; set; } = -1;

    internal static async Task BeforeAutoSearchAsync(CombatState state, int turn,
        CancellationToken token)
    {
        if (BeforeAutoSearch is { } hook)
            await hook(state, turn, token);
    }
}
#endif
