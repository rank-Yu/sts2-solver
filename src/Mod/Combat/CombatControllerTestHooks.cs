#if STS2_SOLVER_TESTING
// Test-only hooks used by the controller scenarios. Production builds do not compile this file,
// so the controller has no test-host dependency.
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Solver;

internal static class CombatControllerTestHooks
{
    internal static Func<CombatState, int, CancellationToken, Task>? BeforeAutoSearch { get; set; }

    /// <summary>Returns a deployment failure reason for the action, or null to execute it.</summary>
    internal static Func<ReplayedAction, int, string?>? ForceDeploymentFailure { get; set; }

    /// <summary>Search count observed when the native turn-setup selector was asked to choose.</summary>
    internal static int TurnSetupSearchCountAtSelection { get; set; } = -1;

    /// <summary>Stop the search as soon as a candidate is rendered, for stop-candidate text tests.</summary>
    internal static bool AutoStopOnCandidate { get; set; }

    /// <summary>Pretends the action queue is still resolving, for the search-block scenario.</summary>
    internal static bool ForceActionsRunningForTest { get; set; }

    /// <summary>Pretends the turn-start setup is still running, for the search-block scenario.</summary>
    internal static bool ForceTurnStartForTest { get; set; }

    /// <summary>Returns a search failure code for the request, or null to search normally.</summary>
    internal static Func<int, string?>? ForceSearchFailure { get; set; }

    /// <summary>Runs before the live end-turn risk check, for the forced-execution scenario.</summary>
    internal static Func<CombatState, Player, Task>? BeforeLiveEndTurnCheck { get; set; }

    /// <summary>Completion times of deployed actions in the current scenario, in tick milliseconds.</summary>
    internal static List<long> DeploymentCompletionTimes { get; } = [];

    internal static List<(long Start, long End)> DeploymentWaits { get; } = [];

    internal static int DeploymentPlayableActionCount { get; set; }

    internal static void RecordDeploymentAction()
        => DeploymentCompletionTimes.Add(Environment.TickCount64);

    internal static void RecordDeploymentWaitStart(long startedAt)
        => DeploymentWaits.Add((startedAt, 0L));

    internal static void RecordDeploymentWaitEnd()
    {
        if (DeploymentWaits.Count == 0)
            return;
        (long start, _) = DeploymentWaits[^1];
        DeploymentWaits[^1] = (start, Environment.TickCount64);
    }

    internal static async Task BeforeAutoSearchAsync(CombatState state, int turn,
        CancellationToken token)
    {
        if (BeforeAutoSearch is { } hook)
            await hook(state, turn, token);
    }
}
#endif
