// Full-auto pause checks and current-turn deployment. The pause predicates live in Core
// (CombatFullAutoPausePolicy); this file supplies the controller's live state and publishes the
// resulting pause.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    private static void StartFullAutoDeployment(CombatControllerResult result)
    {
        CombatRouteOutcome outcome = result.Outcome;
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.ForceProjectedDeathTurn)
            outcome = outcome with { DeathTurn = result.StartTurn };
#endif
        CombatPauseCode? pause =
            CombatFullAutoPausePolicy.BeforeDeployment(result.StartTurn, outcome);
        if (pause is { } code)
        {
            DisableFullAuto();
            _fullAutoPausedThisCombat = true;
            _continuationSource = null;
            _pauseCode = CombatFullAutoPausePolicy.Code(code);
            _pauseReason = code == CombatPauseCode.DeathTurn
                ? $"death_turn={outcome.DeathTurn}"
                : code.ToString();
            _phase = CombatUiPhase.Paused;
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_FULL_AUTO_STOP reason={_pauseCode} " +
                $"turn={result.StartTurn} {_pauseReason}");
            return;
        }
        StartDeployment(result);
    }

    private static void StartDeployment(CombatControllerResult result)
    {
        if (HasBlockingDeployment || _state == null || _player == null || _rules == null
            || _tracker == null)
        {
            return;
        }
        InvalidateSearchContinuation("deployment");
        ReplayedAction[] turnActions =
            CombatRouteDecision.CurrentTurnActions(result.Plan.Actions, result.StartTurn);
#if STS2_SOLVER_TESTING
        bool forcePlanExhausted = CombatControllerTestHooks.ForcePlanExhaustedOnce;
        CombatControllerTestHooks.ForcePlanExhaustedOnce = false;
#else
        const bool forcePlanExhausted = false;
#endif
        if (turnActions.Length == 0 || forcePlanExhausted)
        {
            _continuationSource = null;
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_DEPLOY_REPLAN turn={result.StartTurn} " +
                "reason=turn_plan_exhausted");
            RequestSearch(CombatSearchReason.PlanExhausted, deployWhenReady: !_fullAutoEnabled);
            return;
        }
        if (_latestResult != null)
        {
            _latestResult = null;
            _latestStamp = null;
        }
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_combatCancellation.Token);
        CombatDeploymentSession deployment = new(result.StartTurn, _state, cancellation);
        _deployment = deployment;
        _deploymentResult = result;
        _currentActionIndex = turnActions[0].Index;
        _completedActions = turnActions[0].Index;
        _actionRunning = false;
        _phase = CombatUiPhase.Deploying;
        _errorCode = null;
        _errorDetail = null;
        PublishSnapshot();
        string enemyHp = _state.Enemies == null
            ? "-"
            : string.Join(',', _state.Enemies.Select(static enemy => $"{enemy.CurrentHp}/{enemy.MaxHp}"));
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_DEPLOY_START turn={result.StartTurn} " +
            $"actions={turnActions.Length} full_auto={_fullAutoEnabled} enemy_hp={enemyHp} " +
            $"player_hp={_player.Creature.CurrentHp}");
        deployment.Operation = DeployTurnAsync(deployment, result);
    }

    private static async Task DeployTurnAsync(CombatDeploymentSession deployment,
        CombatControllerResult result)
    {
        NGame? host = NGame.Instance;
        if (host == null)
            return;
        int turn = result.StartTurn;
        bool endedTurn = false;
        try
        {
            CombatTurnDeployer deployer = new(host, _state!, _player!, _rules!, _tracker!);
            CombatTurnDeployment outcome = await deployer.DeployTurnAsync(
                result.Plan, turn, BeforeEndTurnAsync,
                action =>
                {
                    _currentActionIndex = action.Index;
                    _actionRunning = true;
                    PublishSnapshot();
                },
                action =>
                {
                    _completedActions = action.Index + 1;
                    _currentActionIndex = action.Index + 1;
                    _actionRunning = false;
                    if (action.Action.Kind == PlayerActionKind.UsePotion)
                        RecordPotionUsed(action);
                    PublishSnapshot();
                },
                () => deployment.StopRequested,
                () => deployment.SupersededByTurnSetup,
                deployment.Cancellation.Token);
            bool replaced = !ReferenceEquals(_deployment, deployment);
            ReleaseDeployment(deployment);
            if (outcome.Pause is { } pause)
            {
                DisableFullAuto();
                _fullAutoPausedThisCombat = true;
                _continuationSource = null;
                if (pause.CodeName == "live_end_turn_risk")
                    RetainRemainingEndTurnPlan(turn);
                _pauseCode = pause.CodeName;
                _pauseReason = pause.Detail;
                _pausePlannedHpLost = pause.PlannedHpLost;
                _pauseLiveHpLost = pause.LiveHpLost;
                _pausePlayerDead = pause.PlayerDead;
                _phase = CombatUiPhase.Paused;
                PublishSnapshot();
                ModLog.Warn(
                    $"[Sts2Solver] CONTROLLER_FULL_AUTO_STOP reason={_pauseCode} turn={turn} " +
                    $"planned_hp_lost={pause.PlannedHpLost?.ToString() ?? "-"} " +
                    $"live_hp_lost={pause.LiveHpLost?.ToString() ?? "-"} " +
                    $"player_dead={pause.PlayerDead} {pause.Detail}");
                return;
            }
            if (outcome.Stopped)
            {
                _automaticSearchPaused = true;
                _automaticSearchPausedTurn = turn;
                _pauseCode = "stopped";
                _pauseReason = "stopped_by_user";
                _phase = CombatUiPhase.WaitingTurn;
                PublishSnapshot();
                ModLog.Info($"[Sts2Solver] CONTROLLER_DEPLOY_STOPPED turn={turn}");
                return;
            }
            if (outcome.SupersededByTurnSetup)
            {
                // The next turn's setup page already owns the turn; do not compare the old
                // EndTurn prediction, do not count a deviation and do not kick another search.
                _lastSolverDeployedTurn = turn;
                _lastTurnSupersededBySetup = turn;
                if (!replaced)
                {
                    _phase = CombatUiPhase.WaitingTurn;
                    PublishSnapshot();
                }
                ModLog.Info(
                    $"[Sts2Solver] CONTROLLER_DEPLOY_SUPERSEDED turn={turn} " +
                    $"next_setup=true deviation=false replaced={replaced}");
                return;
            }
            if (outcome.FailureReason is { } failure)
            {
                HandleDeploymentFailure(turn, failure, outcome.FailureDetail ?? "",
                    [.. outcome.Diff], outcome.PlanExhausted);
                return;
            }
            if (outcome.Terminal)
            {
                _lastSolverDeployedTurn = turn;
                _phase = CombatUiPhase.WaitingTurn;
                PublishSnapshot();
                return;
            }
            endedTurn = outcome.EndedTurn;
            if (endedTurn)
                ClearConsecutiveFailures(turn);
            _lastSolverDeployedTurn = turn;
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
        }
        catch (OperationCanceledException)
        {
            ModLog.Info($"[Sts2Solver] CONTROLLER_DEPLOY_CANCELLED turn={turn}");
        }
        catch (Exception error)
        {
            NativeCallbackGuard.Report("DeployTurn", error);
        }
        finally
        {
            ReleaseDeployment(deployment);
            if (ReferenceEquals(_deploymentResult, result))
                _deploymentResult = null;
            deployment.Cancellation.Dispose();
            ResumePendingTurnStarted();
            PublishSnapshot();
        }
        if (endedTurn)
            await KickAutoSearchIfNeededAsync();
    }

    private static void ReleaseDeployment(CombatDeploymentSession deployment)
    {
        _actionRunning = false;
        if (ReferenceEquals(_deployment, deployment))
            _deployment = null;
    }

    private static void HandleDeploymentFailure(int turn, string reason, string detail,
        string[] diff, bool planExhausted)
    {
        _deploymentFailures++;
        if ((reason is "combat_over" or "combat_changed" or "combat_ended")
            && !CombatManager.Instance.IsInProgress)
        {
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return;
        }
        if (reason == "turn_plan_exhausted")
        {
            // The route had actions but no EndTurn. This is the plan-exhausted path, not a
            // failed live execution: search again and keep the old pending-execution rule.
            _continuationSource = null;
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_DEPLOY_REPLAN turn={turn} reason={reason} " +
                $"diff={string.Join(" | ", diff)} {detail}");
            RequestSearch(CombatSearchReason.PlanExhausted, deployWhenReady: !_fullAutoEnabled);
            return;
        }
        if (reason == "choice_mismatch")
        {
            DisableFullAuto();
            _fullAutoPausedThisCombat = true;
            _continuationSource = null;
            _automaticSearchPaused = true;
            _automaticSearchPausedTurn = turn;
            _pauseCode = "choice_mismatch";
            _pauseReason = detail;
            _phase = CombatUiPhase.Paused;
            PublishSnapshot();
            ModLog.Warn($"[Sts2Solver] CONTROLLER_DEPLOY_PAUSE turn={turn} reason={reason} {detail}");
            return;
        }
        if (IsDeploymentDeviation(reason))
        {
            string pauseCode = reason == "action_deviation"
                ? "action_deviation"
                : "deployment_deviation";
            string pauseDetail = reason == pauseCode ? detail : $"{reason}: {detail}";
            PauseForTurnStop(turn, pauseCode, pauseDetail, countDeviation: true);
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_DEPLOY_PAUSE turn={turn} reason={reason} " +
                $"pause={pauseCode} plan_exhausted={planExhausted} " +
                $"diff={string.Join(" | ", diff)} {detail}");
            return;
        }

        // Every remaining deployment failure stops the turn: the action could not be executed
        // and the route is no longer trustworthy. Do not search automatically.
        PauseForTurnStop(turn, "deploy_failed", $"{reason}: {detail}", countDeviation: false);
        ModLog.Warn(
            $"[Sts2Solver] CONTROLLER_DEPLOY_FAILED turn={turn} reason={reason} " +
            $"plan_exhausted={planExhausted} diff={string.Join(" | ", diff)} {detail}");
    }

    /// <summary>
    /// Stops the current turn without searching again. Prediction deviations also count in
    /// <c>_deviations</c>; deployment failures already counted in <c>_deploymentFailures</c>.
    /// </summary>
    private static void PauseForTurnStop(int turn, string code, string detail,
        bool countDeviation)
    {
        if (countDeviation)
            _deviations++;
        _latestResult = null;
        _latestStamp = null;
        _deploymentResult = null;
        _turnSetupResult = null;
        _continuationSource = null;
        _continuationSetupTurn = null;
        _pendingCompleteBaseline = null;
        _automaticSearchPaused = true;
        _automaticSearchPausedTurn = turn;
        if (_fullAutoEnabled || _mode == SolverMode.FullAuto)
        {
            DisableFullAuto();
            _fullAutoPausedThisCombat = true;
        }
        _pauseCode = code;
        _pauseReason = detail;
        _pausePlannedHpLost = null;
        _pauseLiveHpLost = null;
        _pausePlayerDead = false;
        _phase = CombatUiPhase.Paused;
        PublishSnapshot();
        ModLog.Warn(
            $"[Sts2Solver] CONTROLLER_TURN_STOP turn={turn} code={code} " +
            $"mode={_mode} deviation={countDeviation} {detail}");
    }

    private static bool IsDeploymentDeviation(string reason) => reason switch
    {
        "action_deviation" or "turn_drift" or "energy_drift" or "stars_drift" or "hand_drift"
            or "potion_slot_drift" or "card_missing" or "card_mismatch" or "card_unplayable"
            or "potion_missing" or "potion_mismatch" or "potion_target" or "target_missing" => true,
        _ => false,
    };

    /// <summary>
    /// Keeps the unplayed tail (the end-turn action) as the current-turn route after a live
    /// end-turn pause, so "Execute current turn" can force the remaining action.
    /// </summary>
    private static void RetainRemainingEndTurnPlan(int turn)
    {
        if (_deploymentResult is not { } result || _state == null || _player == null
            || _rules == null || _tracker == null)
        {
            return;
        }
        ReplayedAction? endTurn = result.Plan.EndTurnAction(turn);
        CombatRoutePlan? remaining = endTurn == null ? null : result.Plan.TrimToAction(endTurn.Index);
        if (remaining == null)
            return;
        try
        {
            _tracker.Refresh(_state);
            Battle live = _tracker.ImportRoot(_state, _player, _rules);
            _tracker.Observe(live);
            CombatControllerResult retained = result with
            {
                Plan = remaining,
                Stamp = CombatStateStamp.Capture(live),
                Reason = "live_end_turn_risk",
            };
            _latestResult = retained;
            _latestStamp = retained.Stamp;
            _currentActionIndex = 0;
            _completedActions = 0;
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_RETAIN_END_TURN turn={turn} " +
                $"actions={remaining.Actions.Length}");
        }
        catch (UnsupportedImportException error)
        {
            ModLog.Error(
                $"[Sts2Solver] CONTROLLER_RETAIN_END_TURN_FAILED reason={error.Reason} {error.Detail}");
        }
        catch (UnsupportedException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_RETAIN_END_TURN_FAILED {error.Message}");
        }
        catch (CapacityException error)
        {
            ModLog.Error(
                $"[Sts2Solver] CONTROLLER_RETAIN_END_TURN_FAILED region={error.Region} {error.Message}");
        }
        catch (InvalidDataException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_RETAIN_END_TURN_FAILED {error.Message}");
        }
        catch (InvalidOperationException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_RETAIN_END_TURN_FAILED {error.Message}");
        }
    }

    private static async ValueTask<CombatPause?> BeforeEndTurnAsync(ReplayedAction endTurn,
        IReadOnlyDictionary<int, int> hpLostByTurn)
    {
#if !STS2_SOLVER_TESTING
        await ValueTask.CompletedTask;
#endif
        if (!_fullAutoEnabled || _state == null || _player == null || _rules == null
            || _tracker == null)
        {
            return null;
        }
        int turn = endTurn.Before.PlayerTurn;
        if (!hpLostByTurn.TryGetValue(turn, out int plannedHpLost))
        {
            return new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, $"missing planned hp loss for turn {turn}");
        }
        try
        {
#if STS2_SOLVER_TESTING
            if (CombatControllerTestHooks.BeforeLiveEndTurnCheck is { } hook)
                await hook(_state, _player);
#endif
            _tracker.Refresh(_state);
            Battle live = _tracker.ImportForComparison(_state, _player, _rules, endTurn.Before);
            LiveEndTurnRiskProjection risk =
                LiveEndTurnRiskEvaluator.Evaluate(live, endTurn, _rules);
            CombatPauseCode? code = CombatFullAutoPausePolicy.LiveEndTurn(
                plannedHpLost, risk.HpLost, risk.PlayerDead);
            if (code == null)
                return null;
            return new CombatPause(code.Value,
                $"planned_hp_lost={plannedHpLost} live_hp_lost={risk.HpLost} " +
                $"hp_before={risk.HpBefore} hp_after={risk.HpAfter} dead={risk.PlayerDead}",
                plannedHpLost, risk.HpLost, risk.PlayerDead);
        }
        catch (InvalidDataException error)
        {
            return new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, error.Message, plannedHpLost, null, false);
        }
        catch (UnsupportedException error)
        {
            return new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, error.Message, plannedHpLost, null, false);
        }
        catch (InvalidOperationException error)
        {
            return new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, error.Message, plannedHpLost, null, false);
        }
        catch (CapacityException error)
        {
            return new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, $"region={error.Region} {error.Message}",
                plannedHpLost, null, false);
        }
    }

    private static void RecordPotionUsed(ReplayedAction action)
    {
        int slot = action.Action.PotionSlot;
        if (slot >= action.Before.PotionSlotCount)
            return;
        ushort stored = action.Before.Potions[slot];
        if (stored == 0)
            return;
        string id = Registry.Potion((ushort)(stored - 1)).NativeId;
        if (!PotionsUsed.Contains(id, StringComparer.Ordinal))
            PotionsUsed.Add(id);
    }
}
