// Main-thread deployment of one planned turn. The controller owns mode/pause policy; this class
// only trims the route to the current turn, executes each action through ActionDeployer and
// reports drift. The boundary comparison is shared with the offline loop through
// CombatRouteDecision. Deployment speed and inter-action waits follow CombatSolver
// src/Runtime/SolverController.cs at 5c773caa (MIT, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Settings;

namespace Sts2Solver;

internal sealed record CombatTurnDeployment(
    bool EndedTurn,
    bool Terminal,
    string? FailureReason,
    string? FailureDetail,
    string[] Diff,
    bool PlanExhausted,
    CombatPause? Pause,
    bool Stopped = false,
    bool SupersededByTurnSetup = false);

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
        Func<bool>? stopRequested,
        Func<bool>? endTurnSuperseded,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CombatState live = CombatManager.Instance.DebugOnlyGetState() ?? combat;
        Player livePlayer = LocalContext.GetMe(live) ?? player;
        _deployer = new ActionDeployer(host, live, livePlayer, rules, tracker);
        ReplayedAction[] actions = CombatRouteDecision.CurrentTurnActions(plan.Actions, turn);
        int playableCount = actions.Count(static action =>
            action.Action.Kind != PlayerActionKind.EndTurn);
        int playableIndex = 0;
#if STS2_SOLVER_TESTING
        CombatControllerTestHooks.DeploymentPlayableActionCount = playableCount;
#endif
        ReplayedAction? endTurn = plan.EndTurnAction(turn);
        SolverAutomationSettings automation = SolverSettings.Current.Automation;
        FastModeType originalFastMode = CombatDeploymentTiming.CaptureFastMode();
        CombatDeploymentTiming.ApplyFastMode(
            CombatDeploymentTiming.ResolveFastMode(automation.DeploymentSpeed));
        try
        {
        for (int actionIndex = 0; actionIndex < actions.Length; actionIndex++)
        {
            ReplayedAction action = actions[actionIndex];
            token.ThrowIfCancellationRequested();
            if (stopRequested?.Invoke() == true)
                return StoppedDeployment();
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
#if STS2_SOLVER_TESTING
            if (turn == 2)
                CombatControllerTestHooks.RecordTurnSetupBoundary(actual);
#endif
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
#if STS2_SOLVER_TESTING
            CombatControllerTestHooks.RecordDeploymentAction();
#endif
            if (action.After.Outcome != Outcome.None)
                return new CombatTurnDeployment(false, true, null, null, [], false, null);
            if (playableIndex + 1 < playableCount && automation.InterActionDelaySeconds > 0d)
            {
#if STS2_SOLVER_TESTING
                CombatControllerTestHooks.RecordDeploymentWaitStart(Environment.TickCount64);
#endif
                await CombatDeploymentTiming.WaitAsync(host, automation.InterActionDelaySeconds, token);
#if STS2_SOLVER_TESTING
                CombatControllerTestHooks.RecordDeploymentWaitEnd();
#endif
            }
            playableIndex++;
        }

        if (!CombatManager.Instance.IsInProgress || !IsSamePlayableTurn(turn))
            return new CombatTurnDeployment(false, false, null, null, [], false, null);
        if (stopRequested?.Invoke() == true)
            return StoppedDeployment();
        if (endTurn == null)
            return new CombatTurnDeployment(false, false, "turn_plan_exhausted",
                $"第 {turn} 回合路线没有结束回合动作。", [], true, null);
        if (beforeEndTurn != null)
        {
            CombatPause? pause = await beforeEndTurn(endTurn, plan.Outcome.HpLostByTurn);
            if (pause != null)
                return new CombatTurnDeployment(false, false, null, null, [], false, pause);
        }
        if (stopRequested?.Invoke() == true)
            return StoppedDeployment();

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
#if STS2_SOLVER_TESTING
        if (turn == 2)
            CombatControllerTestHooks.RecordTurnSetupBoundary(ended);
#endif
        // The next turn's setup page took over while this EndTurn was waiting for its boundary.
        // The old route's Play prediction no longer describes the actual turn, so the end-turn
        // comparison is skipped and the setup result owns the next turn.
        if (endTurnSuperseded?.Invoke() == true)
        {
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_DEPLOY_SUPERSEDED turn={turn} " +
                $"action={CombatPilotAction.Describe(endTurn)}");
            return new CombatTurnDeployment(false, false, null, null, [], false, null,
                SupersededByTurnSetup: true);
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
#if STS2_SOLVER_TESTING
        CombatControllerTestHooks.RecordDeploymentAction();
#endif
        return new CombatTurnDeployment(true, endTurn.After.Outcome != Outcome.None, null, null,
            [], false, null);
        }
        finally
        {
            CombatDeploymentTiming.RestoreFastMode(originalFastMode);
        }

        static CombatTurnDeployment Failure(string reason, string detail)
            => new(false, false, reason, detail, [], false, null);

        static CombatTurnDeployment StoppedDeployment()
            => new(false, false, null, null, [], false, null, Stopped: true);
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
