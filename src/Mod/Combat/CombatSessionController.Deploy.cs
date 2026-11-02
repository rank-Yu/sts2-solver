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
        CombatPauseCode? pause = CombatFullAutoPausePolicy.BeforeDeployment(
            result.StartTurn, result.Outcome, result.WasReused,
            result.ProjectedBattleHpLossIncrease,
            StopFullAutoOnCombatEnd, StopFullAutoOnDeathTurn, StopFullAutoOnWorseRecalculation);
        if (pause is { } code)
        {
            DisableFullAuto(persistMode: true);
            _continuationSource = null;
            _pauseCode = CombatFullAutoPausePolicy.Code(code);
            _pauseReason = code switch
            {
                CombatPauseCode.WorseRecalculation =>
                    $"projected_hp_lost {result.PreviousProjectedBattleHpLost} -> " +
                    $"{result.Outcome.ProjectedBattleHpLost}",
                CombatPauseCode.DeathTurn => $"death_turn={result.Outcome.DeathTurn}",
                CombatPauseCode.CombatEndTurn => $"combat_ended_turn={result.Outcome.CombatEndedTurn}",
                _ => code.ToString(),
            };
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
        if (_deployment != null || _state == null || _player == null || _rules == null
            || _tracker == null)
        {
            return;
        }
        ReplayedAction[] turnActions =
            CombatRouteDecision.CurrentTurnActions(result.Plan.Actions, result.StartTurn);
        if (turnActions.Length == 0)
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
                    PublishSnapshot();
                },
                action =>
                {
                    _completedActions = action.Index + 1;
                    _currentActionIndex = action.Index + 1;
                    if (action.Action.Kind == PlayerActionKind.UsePotion)
                        RecordPotionUsed(action);
                    PublishSnapshot();
                },
                deployment.Cancellation.Token);
            if (outcome.Pause is { } pause)
            {
                DisableFullAuto(persistMode: true);
                _continuationSource = null;
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
        finally
        {
            if (ReferenceEquals(_deployment, deployment))
                _deployment = null;
            _deploymentResult = null;
            deployment.Cancellation.Dispose();
            PublishSnapshot();
        }
        if (endedTurn)
            await KickAutoSearchIfNeededAsync();
    }

    private static void HandleDeploymentFailure(int turn, string reason, string detail,
        string[] diff, bool planExhausted)
    {
        _deploymentFailures++;
        if (reason == "action_deviation")
            _deviations++;
        if (reason is "combat_over" or "combat_changed" && !CombatManager.Instance.IsInProgress)
        {
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return;
        }
        if (RecordConsecutiveFailure(turn, reason, detail))
            return;
        if (reason == "choice_mismatch")
        {
            DisableFullAuto(persistMode: true);
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
        _continuationSource = null;
        _phase = CombatUiPhase.WaitingTurn;
        PublishSnapshot();
        ModLog.Warn(
            $"[Sts2Solver] CONTROLLER_DEPLOY_DRIFT turn={turn} reason={reason} " +
            $"plan_exhausted={planExhausted} diff={string.Join(" | ", diff)} {detail}");
        RequestSearch(CombatSearchReason.DeploymentDrift, deployWhenReady: !_fullAutoEnabled);
    }

    private static ValueTask<CombatPause?> BeforeEndTurnAsync(ReplayedAction endTurn,
        IReadOnlyDictionary<int, int> hpLostByTurn)
    {
        if (!_fullAutoEnabled
            || (!StopFullAutoOnDeathTurn && !StopFullAutoOnWorseRecalculation)
            || _state == null || _player == null || _rules == null || _tracker == null)
        {
            return ValueTask.FromResult<CombatPause?>(null);
        }
        int turn = endTurn.Before.PlayerTurn;
        if (!hpLostByTurn.TryGetValue(turn, out int plannedHpLost))
        {
            return ValueTask.FromResult<CombatPause?>(new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, $"missing planned hp loss for turn {turn}"));
        }
        try
        {
            _tracker.Refresh(_state);
            Battle live = _tracker.ImportForComparison(_state, _player, _rules, endTurn.Before);
            LiveEndTurnRiskProjection risk =
                LiveEndTurnRiskEvaluator.Evaluate(live, endTurn, _rules);
            CombatPauseCode? code = CombatFullAutoPausePolicy.LiveEndTurn(
                plannedHpLost, risk.HpLost, risk.PlayerDead,
                StopFullAutoOnDeathTurn, StopFullAutoOnWorseRecalculation);
            if (code == null)
                return ValueTask.FromResult<CombatPause?>(null);
            return ValueTask.FromResult<CombatPause?>(new CombatPause(code.Value,
                $"planned_hp_lost={plannedHpLost} live_hp_lost={risk.HpLost} " +
                $"hp_before={risk.HpBefore} hp_after={risk.HpAfter} dead={risk.PlayerDead}",
                plannedHpLost, risk.HpLost, risk.PlayerDead));
        }
        catch (InvalidDataException error)
        {
            return ValueTask.FromResult<CombatPause?>(new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, error.Message, plannedHpLost, null, false));
        }
        catch (UnsupportedException error)
        {
            return ValueTask.FromResult<CombatPause?>(new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, error.Message, plannedHpLost, null, false));
        }
        catch (InvalidOperationException error)
        {
            return ValueTask.FromResult<CombatPause?>(new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, error.Message, plannedHpLost, null, false));
        }
        catch (CapacityException error)
        {
            return ValueTask.FromResult<CombatPause?>(new CombatPause(
                CombatPauseCode.LiveEndTurnRisk, $"region={error.Region} {error.Message}",
                plannedHpLost, null, false));
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
