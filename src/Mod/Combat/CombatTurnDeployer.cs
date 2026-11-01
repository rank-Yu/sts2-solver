// Main-thread deployment of one planned turn. The controller owns mode/pause policy; this class
// only trims the route to the current turn, executes each action through ActionDeployer and
// reports drift. The boundary comparison is shared with the offline loop through
// CombatRouteDecision.
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

internal sealed record CombatTurnDeployment(
    bool EndedTurn,
    bool Terminal,
    string? FailureReason,
    string? FailureDetail,
    string[] Diff,
    bool PlanExhausted,
    CombatPause? Pause);

internal sealed class CombatTurnDeployer(
    NGame host,
    CombatState combat,
    Player player,
    Rules rules,
    LiveCombatTracker tracker)
{
    private ActionDeployer? _deployer;

    internal async ValueTask<CombatTurnDeployment> DeployTurnAsync(
        CombatRoutePlan plan,
        int turn,
        Func<ReplayedAction, IReadOnlyDictionary<int, int>, ValueTask<CombatPause?>>? beforeEndTurn,
        Action<ReplayedAction>? actionStarted,
        Action<ReplayedAction>? actionCompleted,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CombatState live = CombatManager.Instance.DebugOnlyGetState() ?? combat;
        Player livePlayer = LocalContext.GetMe(live) ?? player;
        _deployer = new ActionDeployer(host, live, livePlayer, rules, tracker);
        ReplayedAction[] actions = CombatRouteDecision.CurrentTurnActions(plan.Actions, turn);
        ReplayedAction? endTurn = plan.EndTurnAction(turn);
        foreach (ReplayedAction action in actions)
        {
            token.ThrowIfCancellationRequested();
            if (action.Action.Kind == PlayerActionKind.EndTurn)
                continue;
            actionStarted?.Invoke(action);
#if STS2_SOLVER_TESTING
            if (CombatControllerTestHooks.ForceDeploymentFailure?.Invoke(action, turn)
                is { } forced)
            {
                return Failure(forced, "forced_by_test");
            }
#endif
            Battle actual;
            try
            {
                actual = await _deployer.ExecuteAsync(action, token);
            }
            catch (DeploymentFailureException error)
            {
                return Failure(error.Reason, error.Message);
            }
            if (action.After.Outcome == Outcome.None
                && !CombatRouteDecision.ActionMatches(action, actual))
            {
                string[] diff = CombatRouteDecision.BoundaryDiff(action.After, actual);
                ModLog.Error(
                    $"[Sts2Solver] CONTROLLER_ACTION_DEVIATION turn={turn} index={action.Index} " +
                    $"action={action.Action} diff={string.Join(" | ", diff)}");
                return new CombatTurnDeployment(false, false, "action_deviation",
                    string.Join(" | ", diff), diff, false, null);
            }
            tracker.Observe(action.After);
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_DEPLOY_OK turn={action.Before.PlayerTurn} " +
                $"index={action.Index} action={CombatPilotAction.Describe(action)} " +
                $"outcome={action.After.Outcome} next_turn={action.After.PlayerTurn}");
            actionCompleted?.Invoke(action);
            if (action.After.Outcome != Outcome.None)
                return new CombatTurnDeployment(false, true, null, null, [], false, null);
        }

        if (!CombatManager.Instance.IsInProgress || !IsSamePlayableTurn(turn))
            return new CombatTurnDeployment(false, false, null, null, [], false, null);
        if (endTurn == null)
            return new CombatTurnDeployment(false, false, "turn_plan_exhausted",
                $"第 {turn} 回合路线没有结束回合动作。", [], true, null);
        if (beforeEndTurn != null)
        {
            CombatPause? pause = await beforeEndTurn(endTurn, plan.Outcome.HpLostByTurn);
            if (pause != null)
                return new CombatTurnDeployment(false, false, null, null, [], false, pause);
        }

        actionStarted?.Invoke(endTurn);
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.ForceDeploymentFailure?.Invoke(endTurn, turn)
            is { } forcedEndTurn)
        {
            return Failure(forcedEndTurn, "forced_by_test");
        }
#endif
        Battle ended;
        try
        {
            ended = await _deployer.ExecuteAsync(endTurn, token);
        }
        catch (DeploymentFailureException error)
        {
            return Failure(error.Reason, error.Message);
        }
        if (endTurn.After.Outcome == Outcome.None
            && !CombatRouteDecision.ActionMatches(endTurn, ended))
        {
            string[] diff = CombatRouteDecision.BoundaryDiff(endTurn.After, ended);
            ModLog.Error(
                $"[Sts2Solver] CONTROLLER_END_TURN_DEVIATION turn={turn} " +
                $"diff={string.Join(" | ", diff)}");
            return new CombatTurnDeployment(true, false, "action_deviation",
                string.Join(" | ", diff), diff, false, null);
        }
        tracker.Observe(endTurn.After);
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_DEPLOY_OK turn={endTurn.Before.PlayerTurn} " +
            $"index={endTurn.Index} action={CombatPilotAction.Describe(endTurn)} " +
            $"outcome={endTurn.After.Outcome} next_turn={endTurn.After.PlayerTurn}");
        actionCompleted?.Invoke(endTurn);
        return new CombatTurnDeployment(true, endTurn.After.Outcome != Outcome.None, null, null,
            [], false, null);

        static CombatTurnDeployment Failure(string reason, string detail)
            => new(false, false, reason, detail, [], false, null);
    }

    private bool IsSamePlayableTurn(int turn)
    {
        CombatState? live = CombatManager.Instance.DebugOnlyGetState();
        if (live == null)
            return false;
        Player? current = LocalContext.GetMe(live);
        return live.CurrentSide == CombatSide.Player
            && current?.PlayerCombatState is { Phase: PlayerTurnPhase.Play } state
            && state.TurnNumber == turn;
    }
}
