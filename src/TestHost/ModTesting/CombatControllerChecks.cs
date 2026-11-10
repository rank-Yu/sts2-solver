// Headless controller scenarios. They drive the real ICombatUiCommands surface, wait on the
// published snapshots and write a compact assertion record for controller.json. No scenario
// bypasses the controller's command validation.
using System.Diagnostics;
using Button = Godot.Button;
using BaseButton = Godot.BaseButton;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using PlayerAction = Sts2Solver.Simulation.Combat.PlayerAction;
using PlayerActionKind = Sts2Solver.Simulation.Combat.PlayerActionKind;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2Solver;

internal static class CombatControllerChecks
{
    internal static async Task<CombatControllerScenarioResult> RunAsync(NGame host,
        TestHost.OfflineRequest request, string scenario, string root, string resultDirectory)
    {
        List<CombatControllerCheck> checks = [];
        List<string> actions = [];
        string? failure = null;
        void Check(string name, bool condition, string? detail = null)
        {
            checks.Add(new CombatControllerCheck(name, condition ? "ok" : "failed", detail));
            if (condition)
                return;
            failure = detail == null ? name : $"{name}:{detail}";
            throw new InvalidDataException(failure);
        }
        void Action(string text)
        {
            actions.Add(text);
            ModLog.Info($"[Sts2Solver] CONTROLLER_SCENARIO action={text}");
        }
        try
        {
            await WaitControllerBeginAsync(host);
            switch (scenario)
            {
                case "manual_start_stop":
                    await ManualStartStopAsync(host, Check, Action);
                    break;
                case "auto_search_turn":
                    await AutoSearchTurnAsync(host, Check, Action);
                    break;
                case "manual_no_auto_search":
                    await ManualNoAutoSearchAsync(host, Check, Action);
                    break;
                case "manual_switch_at_turn_start":
                    await ManualSwitchAtTurnStartAsync(host, Check, Action);
                    break;
                case "auto_turn_start_once":
                    await AutoTurnStartOnceAsync(host, Check, Action);
                    break;
                case "auto_search_full":
                    await AutoSearchFullAsync(host, Check, Action);
                    break;
                case "manual_full":
                    await ManualFullAsync(host, Check, Action);
                    break;
                case "stop_then_next_turn":
                    await StopThenNextTurnAsync(host, Check, Action);
                    break;
                case "stop_search_keep_route":
                    await StopSearchKeepRouteAsync(host, Check, Action);
                    break;
                case "stop_then_recalc":
                    await StopThenRecalcAsync(host, Check, Action);
                    break;
                case "continue_search_middle":
                    await ContinueSearchMiddleAsync(host, Check, Action);
                    break;
                case "continue_search_settings_changed":
                    await ContinueSearchSettingsChangedAsync(host, Check, Action);
                    break;
                case "continue_search_state_changed":
                    await ContinueSearchStateChangedAsync(host, Check, Action);
                    break;
                case "continue_search_last_member":
                    await ContinueSearchLastMemberAsync(host, Check, Action);
                    break;
                case "continue_search_twice":
                    await ContinueSearchTwiceAsync(host, Check, Action);
                    break;
                case "continue_search_request_mismatch":
                    await ContinueSearchRequestMismatchAsync(host, Check, Action);
                    break;
                case "execute_has_result":
                    await ExecuteHasResultAsync(host, Check, Action);
                    break;
                case "execute_after_stop":
                    await ExecuteAfterStopAsync(host, Check, Action);
                    break;
                case "execute_without_result":
                    await ExecuteWithoutResultAsync(host, Check, Action);
                    break;
                case "mode_switch":
                    await ModeSwitchAsync(host, Check, Action);
                    break;
                case "full_auto_start":
                    await FullAutoStartAsync(host, Check, Action);
                    break;
                case "full_auto_arm_after_turn":
                    await FullAutoArmAfterTurnAsync(host, Check, Action);
                    break;
                case "repeated_failure":
                    await RepeatedFailureAsync(host, Check, Action);
                    break;
                case "turn_setup_wait":
                    await TurnSetupWaitAsync(host, Check, Action);
                    break;
                case "turn_setup_search_only":
                    await TurnSetupSearchOnlyAsync(host, Check, Action);
                    break;
                case "turn_setup_search_only_execute":
                    await TurnSetupSearchOnlyExecuteAsync(host, Check, Action);
                    break;
                case "turn_setup_auto_execute":
                    await TurnSetupAutoExecuteAsync(host, Check, Action);
                    break;
                case "turn_setup_auto_manual":
                    await TurnSetupAutoManualAsync(host, Check, Action);
                    break;
                case "turn_setup_full":
                    await TurnSetupFullAsync(host, Check, Action);
                    break;
                case "turn_setup_stop_keep":
                    await TurnSetupStopKeepAsync(host, Check, Action);
                    break;
                case "turn_setup_stop_drop":
                    await TurnSetupStopDropAsync(host, Check, Action, forcedFailure: false);
                    break;
                case "turn_setup_fail":
                    await TurnSetupStopDropAsync(host, Check, Action, forcedFailure: true);
                    break;
                case "turn_setup_stop_drive":
                    await TurnSetupStopDriveAsync(host, Check, Action);
                    break;
                case "turn_setup_pause":
                    await TurnSetupPauseAsync(host, Check, Action);
                    break;
                case "turn_setup_scene_exit":
                    await TurnSetupSceneExitAsync(host, Check, Action);
                    break;
                case "turn_setup_implicit":
                    await TurnSetupImplicitAsync(host, Check, Action);
                    break;
                case "turn_setup_manual":
                    await TurnSetupManualAsync(host, Check, Action);
                    break;
                case "turn_setup_turn2":
                    await TurnSetupTurnTwoAsync(host, Check, Action, fullAuto: true);
                    break;
                case "turn_setup_extra_turn":
                    await TurnSetupExtraTurnAsync(host, Check, Action);
                    break;
                case "turn_setup_extra_turn_search":
                    await TurnSetupExtraTurnSearchAsync(host, Check, Action);
                    break;
                case "turn_setup_autopreplay":
                    await TurnSetupAutoPrePlayAsync(host, Check, Action);
                    break;
                case "turn_setup_plasma":
                    await TurnSetupPlasmaAsync(host, Check, Action);
                    break;
                case "turn_setup_carry":
                    await TurnSetupCarryAsync(host, Check, Action);
                    break;
                case "turn_setup_continuation_auto":
                    await TurnSetupContinuationAutoAsync(host, Check, Action);
                    break;
                case "turn_setup_continuation_player":
                    await TurnSetupContinuationPlayerAsync(host, Check, Action);
                    break;
                case "turn_setup_continuation_mismatch":
                    await TurnSetupContinuationMismatchAsync(host, Check, Action);
                    break;
                case "turn_setup_continuation_pause":
                    await TurnSetupContinuationPauseAsync(host, Check, Action);
                    break;
                case "turn_setup_continuation_full":
                    await TurnSetupContinuationFullAsync(host, Check, Action);
                    break;
                case "turn_setup_capsule_source":
                    await TurnSetupCapsuleSourceAsync(host, Check, Action, request.ScenarioId);
                    break;
                case "turn_setup_capsule_source_turn2":
                    await TurnSetupCapsuleSourceTurnTwoAsync(host, Check, Action, request.ScenarioId);
                    break;
                case "turn_setup_continuation_probe":
                    await TurnSetupContinuationProbeAsync(host, Check, Action);
                    break;
                case "turn_setup_continuation_validation":
                    await TurnSetupContinuationValidationAsync(host, Check, Action);
                    break;
                case "turn_setup_continuation_gap":
                    await TurnSetupContinuationGapAsync(host, Check, Action);
                    break;
                case "turn_setup_manual_end":
                    await TurnSetupManualEndAsync(host, Check, Action);
                    break;
                case "turn_setup_multi":
                    await TurnSetupMultiAsync(host, Check, Action);
                    break;
                case "turn_setup_crossbow":
                    await TurnSetupCrossbowAsync(host, Check, Action, expectedOptions: 2);
                    break;
                case "turn_setup_crossbow_many":
                    await TurnSetupCrossbowAsync(host, Check, Action, expectedOptions: 4);
                    break;
                case "deployment_choice_mismatch":
                    await DeploymentChoiceMismatchAsync(host, Check, Action);
                    break;
                case "deviation_action_full_auto":
                    await DeviationActionFullAutoAsync(host, Check, Action);
                    break;
                case "deviation_action_auto_search":
                    await DeviationActionAutoSearchAsync(host, Check, Action);
                    break;
                case "deviation_deployment_drift":
                    await DeviationDeploymentDriftAsync(host, Check, Action);
                    break;
                case "deviation_boundary":
                case "deviation_boundary_setup":
                    await DeviationBoundaryAsync(host, Check, Action);
                    break;
                case "deviation_setup_stamp":
                    await DeviationSetupStampAsync(host, Check, Action);
                    break;
                case "plan_exhausted":
                    await PlanExhaustedAsync(host, Check, Action);
                    break;
                case "combat_log_files":
                    await CombatLogFilesAsync(host, Check, Action);
                    break;
                case "main_menu_stops_search":
                    await MainMenuStopsSearchAsync(host, Check, Action);
                    break;
                case "pause_death_turn_deployment":
                    await PauseDeathTurnDeploymentAsync(host, Check, Action);
                    break;
                case "pause_no_survival_deployment":
                    await PauseNoSurvivalDeploymentAsync(host, Check, Action);
                    break;
                case "combat_end":
                    await CombatEndAsync(host, Check, Action);
                    break;
                case "multiplayer_gate":
                    await MultiplayerGateAsync(host, Check, Action);
                    break;
                case "deployment_speed_follow":
                    await DeploymentSpeedAsync(host, Check, Action, SolverDeploymentSpeed.FollowGame, 0d);
                    break;
                case "deployment_speed_normal":
                    await DeploymentSpeedAsync(host, Check, Action, SolverDeploymentSpeed.Normal, 0d);
                    break;
                case "deployment_speed_fast":
                    await DeploymentSpeedAsync(host, Check, Action, SolverDeploymentSpeed.Fast, 0d);
                    break;
                case "deployment_delay":
                    await DeploymentSpeedAsync(host, Check, Action, SolverDeploymentSpeed.FollowGame, 0.6d);
                    break;
                case "thread_setting":
                    await ThreadSettingAsync(host, Check, Action);
                    break;
                case "panel_texts":
                    await PanelTextsAsync(host, Check, Action);
                    break;
                case "search_failures":
                    await SearchFailuresAsync(host, Check, Action);
                    break;
                case "import_capacity":
                    await ImportCapacityAsync(host, Check, Action);
                    break;
                case "import_failure":
                    await ImportFailureAsync(host, Check, Action);
                    break;
                case "search_status_failures":
                    await SearchStatusFailuresAsync(host, Check, Action);
                    break;
                case "capacity_dropped_route":
                    await CapacityDroppedRouteAsync(host, Check, Action);
                    break;
                case "manual_reuses_route":
                    await ManualReusesRouteAsync(host, Check, Action);
                    break;
                case "panel_run_flow":
                    await PanelRunFlowAsync(host, Check, Action);
                    break;
                case "mode_switch_mid_turn":
                    await ModeSwitchMidTurnAsync(host, Check, Action);
                    break;
                case "full_auto_pause_combat":
                    await FullAutoPauseCombatAsync(host, Check, Action);
                    break;
                case "stop_deployment":
                    await StopDeploymentAsync(host, Check, Action);
                    break;
                case "search_block_reasons":
                    await SearchBlockReasonsAsync(host, Check, Action);
                    break;
                case "deployment_drift_research":
                    await DeploymentDriftResearchAsync(host, Check, Action);
                    break;
                case "live_end_turn_risk_retained":
                    await LiveEndTurnRiskRetainedAsync(host, Check, Action);
                    break;
                case "strategy_commands":
                    await StrategyCommandsAsync(host, Check, Action);
                    break;
                case "forced_potion":
                    await ForcedPotionAsync(host, Check, Action);
                    break;
                case "search_character_pool":
                    await SearchRulePoolsAsync(host, Check, Action, "search_character_pool",
                        [Sts2Solver.Simulation.Combat.CardPools.CharacterAll], []);
                    break;
                case "search_colorless_pool":
                    await SearchRulePoolsAsync(host, Check, Action, "search_colorless_pool",
                        [Sts2Solver.Simulation.Combat.CardPools.ColorlessAll], []);
                    break;
                case "search_dazed_root":
                    await SearchRulePoolsAsync(host, Check, Action, "search_dazed_root",
                        [], ["DAZED"]);
                    break;
                case "panel_strategy":
                    await PanelStrategyAsync(host, Check, Action);
                    break;
                default:
                    throw new NotSupportedException($"未知控制器场景：{scenario}");
            }
        }
        catch (TimeoutException error)
        {
            failure = "timeout:" + error.Message;
            checks.Add(new CombatControllerCheck("scenario_completed", "failed", failure));
        }
        catch (InvalidDataException error)
        {
            failure = error.Message;
        }
        catch (InvalidOperationException error)
        {
            failure = error.Message;
            checks.Add(new CombatControllerCheck("scenario_completed", "failed", failure));
        }
        catch (OperationCanceledException error)
        {
            failure = "cancelled:" + error.Message;
            checks.Add(new CombatControllerCheck("scenario_completed", "failed", failure));
        }
        finally
        {
            CombatControllerTestHooks.BeforeAutoSearch = null;
            CombatControllerTestHooks.ForceDeploymentFailure = null;
            CombatControllerTestHooks.ForceActionsRunningForTest = false;
            CombatControllerTestHooks.ForceTurnStartForTest = false;
            CombatControllerTestHooks.BeforeLiveEndTurnCheck = null;
            CombatControllerTestHooks.ForceSearchFailure = null;
            CombatControllerTestHooks.ForceSearchFailureStatus = null;
            CombatControllerTestHooks.ForceSearchFailureMemoryExceeded = false;
            CombatControllerTestHooks.ForceCapacityDropForTest = null;
            CombatControllerTestHooks.AutoStopOnCandidate = false;
            CombatControllerTestHooks.TamperTurnSetupContinuationPlanForTest = false;
            CombatControllerTestHooks.TamperTurnSetupContinuationBeforeValidationForTest = false;
            CombatControllerTestHooks.ForceProjectedDeathTurn = false;
            CombatControllerTestHooks.ForceProjectedLaterDeathTurn = false;
            CombatControllerTestHooks.EnableNoSurvivalPause = false;
            CombatControllerTestHooks.AllowRepeatedAutoTurnStartForTest = false;
            CombatControllerTestHooks.ForceProjectedDeathTurnSetup = false;
            CombatControllerTestHooks.ForcePlanExhaustedOnce = false;
            CombatControllerTestHooks.BeforeTurnSetupPlayStamp = null;
            CombatControllerTestHooks.SetupStampDamageInjected = false;
            CombatControllerTestHooks.StopAtMemberForTest = null;
            CombatControllerTestHooks.SearchMillisecondsPerWidthForTest = null;
            CombatControllerTestHooks.TamperContinuationCompileForTest = null;
            CombatSessionController.ForceMultiplayerForTest = false;
        }
        CombatPilotResult summary = CombatSessionController.LastCompletedResultForTesting
            ?? CombatSessionController.CapturePilotResultForTest();
        string status = failure == null ? "ok" : "failed";
        CombatUiSnapshot final = Snapshot();
        return new CombatControllerScenarioResult(
            1, scenario, root, status, failure, [.. checks], [.. actions],
            [.. CombatUiStore.History.Select(SnapshotRecord)],
            summary.Turns, summary.PlayerHp, summary.PlayerMaxHp, summary.Searches,
            summary.ReusedTurns, summary.Deviations, summary.DeploymentFailures,
            summary.Status, summary.Outcome,
            final.TurnSetupChoiceCount, final.TurnSetupDriving, final.TurnSetupPageOpen,
            final.TurnSetupPlanReady, final.TurnSetupSearchStarted,
            final.TurnSetupContinuation,
            CombatSessionController.ContinuationReuseMissesForTest,
            CombatSessionController.ContinuationReuseMissDetailForTest,
            final.ErrorCode == "turn_setup_failed" ? final.ErrorDetail : null);
    }

    private static async Task ManualStartStopAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "set manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        CombatUiSnapshot idle = Snapshot();
        check("manual_auto_search_off", !idle.AutomaticCalculationEnabled && !idle.CombatRunActive);
        check("can_start", idle.CanStartSearch, "can start");
        await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
        check("start", true, "manual search via panel button");
        action("press_recalculate");
        CombatUiSnapshot searching = await WaitAsync(host,
            static s => s.IsSearching, 240, "manual search started");
        check("search_reason", searching.Search?.Reason == "manual", searching.Search?.Reason);
        check("search_mode", (!searching.AutoSearchEnabled && !searching.CombatRunActive), ModeText(searching));
        await WaitAsync(host, static s => s.IsSearching, 120, "manual search started");
        await PressPanelButtonAsync(host, PanelButton.Search, "stop_search");
        check("stop", true, "stop search via panel button");
        action("press_stop_search");
        CombatUiSnapshot stopped = await WaitAsync(host, static s => !s.IsSearching, 240,
            "search stopped");
        check("paused", stopped.AutomaticSearchPaused, "automatic paused");
        check("full_auto_off", !stopped.CombatRunActive, "full auto off");
        check("pause_code", stopped.PauseCode == "stopped", stopped.PauseCode);
    }

    private static async Task AutoSearchTurnAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitIdleAsync(host, "auto search idle");
        int before = Snapshot().Searches;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.IsSearching && s.Search?.Reason == "auto_turn_start", 600,
            "auto search next turn");
        check("searches_increased", next.Searches > before, $"{before}->{next.Searches}");
        check("full_auto_off", !next.CombatRunActive);
        await WaitIdleAsync(host, "auto search done");
        bool deployed = CombatUiStore.History.Any(static s => s.IsDeploying);
        check("no_deploy", !deployed, "auto search never deploys");
    }

    private static async Task ManualNoAutoSearchAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        // A route searched this turn and then left behind by the turn ending stays as a marked reference.
        await PressPanelButtonAsync(host, PanelButton.Search, "search");
        action("press_search");
        CombatUiSnapshot ready = await WaitAsync(host,
            static s => s.Result != null && !s.IsSearching, 900, "route ready");
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("fresh_route_not_stale", ready.CurrentTurnRouteReady && panel?.RouteStaleForTest == false,
            $"ready={ready.CurrentTurnRouteReady} heading={panel?.RouteHeadingForTest}");
        int before = Snapshot().Searches;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        await Frames(host, 120);
        CombatUiSnapshot after = Snapshot();
        check("no_search", after.Searches == before, $"{before}->{after.Searches}");
        check("not_searching", !after.IsSearching);
        check("auto_search_off", !after.AutomaticCalculationEnabled);
        check("stale_route_kept", after.Result != null && !after.CurrentTurnRouteReady,
            $"result={after.Result != null} ready={after.CurrentTurnRouteReady}");
        check("stale_route_marked", panel?.RouteStaleForTest == true
            && panel.StatusTextForTest == SolverText.Get("历史路线（仅供参考）")
            && panel.RouteHeadingForTest == SolverText.Get("已保存路线（已过期）"),
            $"status={panel?.StatusTextForTest} heading={panel?.RouteHeadingForTest}");
        // The turn ended without a card played, so the enemies hit; a new search shows that loss as the
        // battle's loss so far, and its projection adds the route's own.
        int lost = NativeHistory.HpLostSoFar(LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState())!.Creature);
        await PressPanelButtonAsync(host, PanelButton.Search, "search");
        action("press_search");
        CombatUiSnapshot second = await WaitAsync(host,
            s => s.Result != null && !s.IsSearching && s.Searches > after.Searches, 900, "second route ready");
        await Frames(host, 4);
        CombatResultUiSnapshot result = second.Result!;
        int routeLost = second.Route?.Turns.Sum(static turn => turn.HpLoss) ?? 0;
        check("battle_hp_lost_so_far", lost > 0 && result.BattleHpLostSoFar == lost
            && (result.CombatEndedTurn == null || result.ProjectedBattleHpLost == lost + routeLost),
            $"history={lost} shown={result.BattleHpLostSoFar} projected={result.ProjectedBattleHpLost} route={routeLost}");
        string expected = SolverText.Format($"本局扣血  已 {lost}    预计 {result.ProjectedBattleHpLost} HP");
        check("battle_hp_text", result.CombatEndedTurn == null || panel?.HpOutcomeTextForTest == expected,
            $"text={panel?.HpOutcomeTextForTest} expected={expected}");
    }

    /// <summary>
    /// Delivers the turn-start event, then switches to manual before the scheduled automatic
    /// search task gets to run. The scheduled search must observe the current mode and disappear,
    /// without incrementing Searches or leaving the controller searching.
    /// </summary>
    private static async Task ManualSwitchAtTurnStartAsync(NGame host, Check check, Action action)
    {
        CombatSessionController.ResumeAutoSearchForTest();
        action("resume_turn_start");
        check("mode_before", (Snapshot().AutoSearchEnabled && !Snapshot().CombatRunActive),
            ModeText(Snapshot()));
        int before = Snapshot().Searches;
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await Frames(host, 180);
        CombatUiSnapshot after = Snapshot();
        check("no_auto_search", after.Searches == before, $"{before}->{after.Searches}");
        check("not_searching", !after.IsSearching, after.Phase.ToString());
        check("mode_manual", (!after.AutoSearchEnabled && !after.CombatRunActive), ModeText(after));
        check("auto_search_off", !after.AutomaticCalculationEnabled, "automatic search enabled");
    }

    /// <summary>
    /// Replays the same turn's TurnStarted event after the first automatic search has already
    /// failed. The controller must keep the failed state and must not issue a second automatic
    /// search for that turn; only the player or the next turn may try again.
    /// </summary>
    private static async Task AutoTurnStartOnceAsync(NGame host, Check check, Action action)
    {
        CombatControllerTestHooks.ForceSearchFailure = static _ => "search_failed";
        CombatSessionController.ResumeAutoSearchForTest();
        action("resume_turn_start");
        CombatUiSnapshot failed = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Failed && !s.IsSearching, 1800,
            "first automatic turn-start failure");
        int searches = failed.Searches;
        check("first_search", searches == 1, searches.ToString());
        check("failure_code", failed.ErrorCode == "search_failed", failed.ErrorCode ?? "-");
        CombatSessionController.ResumeAutoSearchForTest();
        action("repeat_turn_start");
        await Frames(host, 120);
        CombatUiSnapshot after = Snapshot();
        check("no_second_search", after.Searches == searches, $"{searches}->{after.Searches}");
        check("not_searching", !after.IsSearching, after.Phase.ToString());
        check("failure_retained", after.Phase == CombatUiPhase.Failed
            && after.ErrorCode == "search_failed", $"{after.Phase}/{after.ErrorCode ?? "-"}");
        CombatControllerTestHooks.ForceSearchFailure = null;
    }

    private static async Task AutoSearchFullAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatUiSnapshot start = await WaitAsync(host,
            static s => s.Search is { Phase: SearchUiPhase.Completed } && !s.IsSearching,
            900, "first auto search");
        int searches = start.Searches;
        for (int turn = 1; turn <= 3 && CombatManager.Instance.IsInProgress; turn++)
        {
            await WaitIdleAsync(host, $"auto search turn {turn}");
            check("no_deploy", !CombatUiStore.History.Any(static s => s.IsDeploying),
                "auto search must not deploy");
            if (!CombatManager.Instance.IsInProgress)
                break;
            await ForceEndTurnAsync(host);
            action($"force_end_turn:{turn}");
            CombatUiSnapshot next = await WaitAsync(host,
                s => s.Searches > searches, 900, $"auto search turn {turn + 1}");
            searches = next.Searches;
        }
        check("multiple_searches", searches >= 2, $"searches={searches}");
    }

    private static async Task ManualFullAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int searches = Snapshot().Searches;
        for (int turn = 1; turn <= 3 && CombatManager.Instance.IsInProgress; turn++)
        {
            check("no_search", Snapshot().Searches == searches, "manual must not search");
            await ForceEndTurnAsync(host);
            action($"force_end_turn:{turn}");
            await Frames(host, 30);
        }
        check("no_search_after_turns", Snapshot().Searches == searches,
            $"{searches}->{Snapshot().Searches}");
        check("not_searching", !Snapshot().IsSearching);
    }

    private static async Task StopThenNextTurnAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: false);
        check("stop", CombatUiCommands.Instance.StopSearch(), "stop");
        action("stop_search");
        await WaitAsync(host, static s => !s.IsSearching, 240, "search stopped");
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.IsSearching && s.Search?.Reason == "auto_turn_start", 600,
            "auto search resumed next turn");
        check("pause_cleared", !next.AutomaticSearchPaused, "pause cleared");
        check("searching", next.IsSearching);
        await WaitIdleAsync(host, "next turn search done");
        bool deployed = CombatUiStore.History.Any(static s => s.IsDeploying);
        check("no_deploy", !deployed, "auto search does not deploy");
    }

    private static async Task StopSearchKeepRouteAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatControllerTestHooks.AutoStopOnCandidate = true;
        try
        {
            await StartManualSearchAsync(host, action, waitCandidate: true);
            CombatUiSnapshot candidate = Snapshot();
            check("candidate_preview", candidate.Search?.StopCandidateAvailable == true,
                candidate.Search?.CandidateVersion.ToString() ?? "-");
        }
        finally
        {
            CombatControllerTestHooks.AutoStopOnCandidate = false;
        }
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => s.AutomaticSearchPaused && !s.IsSearching, 300, "search stopped");
        check("stopped_this_turn", stopped.AutomationStoppedThisTurn);
        check("route_retained", stopped.Result != null && stopped.CurrentTurnRouteReady);
        check("mode_unchanged", stopped.AutoSearchEnabled && !stopped.CombatRunActive, ModeText(stopped));
        check("run_not_paused", !stopped.CombatRunPaused);
        check("stage_ended", stopped.SearchStage == TurnSearchStage.Ended, stopped.SearchStage.ToString());
        check("not_deploying", !stopped.IsDeploying);
        int searches = stopped.Searches;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.Searches > searches || s.IsDeploying, 900, "next turn auto work");
        check("resumed_next_turn", next.Searches > searches || next.IsDeploying);
        check("pause_cleared", !next.AutomationStoppedThisTurn);
    }

    private static async Task StopThenRecalcAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: true);
        await PressPanelButtonAsync(host, PanelButton.Search, "stop_search");
        check("stop", true, "stop via panel button");
        action("press_stop_search");
        await WaitAsync(host, static s => !s.IsSearching, 300, "search stopped");
        CombatUiSnapshot stopped = Snapshot();
        action(stopped.Result != null && stopped.CurrentTurnRouteReady
            ? "stopped_with_candidate"
            : "stopped_without_candidate");
        check("stop_can_continue", stopped.CanContinueSearch,
            "an unchanged stop offers the continuation");
        // A settings change makes the same key restart from scratch, which is this scenario's
        // subject; the continuation path itself is covered by the continue_search scenarios.
        PotionSlotUi? slot = stopped.Strategy.Potions.Length > 0 ? stopped.Strategy.Potions[0] : null;
        check("potion_available", slot != null, "the root has a searchable potion");
        SolverPotionMode originalMode = slot!.Mode switch
        {
            "disabled" => SolverPotionMode.Disabled,
            "force" => SolverPotionMode.Force,
            _ => SolverPotionMode.Smart,
        };
        try
        {
            check("settings_change", CombatUiCommands.Instance.SetPotionDirective(
                slot.Slot, slot.PotionId, SolverPotionMode.Disabled), "change a search setting");
            CombatSessionController.RefreshSnapshotForTest();
            await Frames(host, 2);
            check("cannot_continue", !Snapshot().CanContinueSearch,
                "the changed request restarts instead of continuing");
            await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
            check("recalc", true, "recalculate via panel button");
            action("press_recalculate");
            CombatUiSnapshot started = await WaitAsync(host, static s => s.IsSearching,
                240, "recalculate started");
            check("stopped_cleared", started.Result == null);
            check("reason_manual", started.Search?.Reason == "manual", started.Search?.Reason);
            await WaitIdleAsync(host, "recalculate done");
            check("searches_increased", Snapshot().Searches > stopped.Searches);
            check("no_reuse", Snapshot().ReusedTurns == stopped.ReusedTurns);
        }
        finally
        {
            CombatUiCommands.Instance.SetPotionDirective(slot.Slot, slot.PotionId, originalMode);
        }
    }

    /// <summary>
    /// Stops a search at a chosen member with the same panel button a player uses. The member slots
    /// are shortened so the remaining portfolio also finishes inside the scenario budget.
    /// </summary>
    private static async Task<CombatUiSnapshot> StopAtMemberAsync(NGame host, Action action,
        int member, string description)
    {
        CombatControllerTestHooks.SearchMillisecondsPerWidthForTest = FastMillisecondsPerWidth;
        CombatControllerTestHooks.StopAtMemberForTest = member;
        await PressPanelButtonAsync(host, PanelButton.Search, description);
        action($"press_search_stop_member:{member}");
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => !s.IsSearching && s.PauseCode == "stopped", 1800, description);
        CombatControllerTestHooks.StopAtMemberForTest = null;
        return stopped;
    }

    private static async Task ContinueSearchMiddleAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        using SettingsOverride singleThread = new(SolverSettings.Current with
        {
            Search = SolverSettings.Current.Search with { Threads = 1 },
        });
        CombatUiSnapshot stopped = await StopAtMemberAsync(host, action, 2, "stop at middle member");
        check("stopped_paused", stopped.AutomaticSearchPaused && stopped.PauseCode == "stopped",
            stopped.PauseCode ?? "-");
        check("can_continue", stopped.CanContinueSearch, "continue available");
        string continueText = await SearchButtonTextAsync(host);
        check("continue_text", continueText == SolverText.Get("继续搜索"), continueText);
        check("route_retained", stopped.Result != null && stopped.CurrentTurnRouteReady);
        int stoppedIndex = FindStoppedMember(stopped);
        check("stopped_middle", stoppedIndex == 2, $"stopped_member={stoppedIndex}");
        SearchMemberUi[] stoppedMembers = stopped.Search?.Members ?? [];
        check("stopped_member_logged",
            stoppedIndex >= 0 && stoppedIndex < stoppedMembers.Length
                && stoppedMembers[stoppedIndex].Ran
                && stoppedMembers[stoppedIndex].StopReason == "stopped",
            $"members={stoppedMembers.Length}");
        PlayerAction[] retainedRoute = CombatSessionController.LastSearchRouteForTest;
        int adopted = stopped.Search?.SelectedMember ?? -1;
        int searches = stopped.Searches;

        await PressPanelButtonAsync(host, PanelButton.Search, "continue_search");
        action("press_continue_search");
        CombatUiSnapshot continuing = await WaitAsync(host, static s => s.IsSearching,
            240, "continuation started");
        check("continuation_reason", continuing.Search?.Reason == "manual",
            continuing.Search?.Reason);
        check("continuation_route_visible", continuing.Result != null && continuing.Route != null,
            "route stays visible while continuing");
        check("continuation_route_same", RouteEquals(
            CombatSessionController.LastSearchRouteForTest, retainedRoute),
            "retained route is unchanged while continuing");
        CombatUiSnapshot done = await WaitAsync(host,
            static s => !s.IsSearching && s.Search?.Phase == SearchUiPhase.Completed,
            1800, "continuation done");
        check("searches_increased", done.Searches == searches + 1,
            $"{searches}->{done.Searches}");
        check("result_ready", done.Result != null && done.CurrentTurnRouteReady);
        SearchMemberUi[] members = done.Search?.Members ?? [];
        check("stopped_member_kept", stoppedIndex >= 0 && stoppedIndex < members.Length
            && members[stoppedIndex].StopReason == "stopped" && members[stoppedIndex].Ran,
            $"members={members.Length}");
        check("later_members_processed", ProcessedAfter(members, stoppedIndex),
            "every later member ran or was skipped");
        int expectedRuns = 0;
        for (int index = stoppedIndex + 1; index < members.Length; index++)
            if (members[index].Ran)
                expectedRuns++;
        check("runs_match", done.Search?.MemberRuns.Length == expectedRuns,
            $"runs={done.Search?.MemberRuns.Length} expected={expectedRuns}");
        int selected = done.Search?.SelectedMember ?? -1;
        check("selected_consistent",
            selected == adopted
                ? RouteEquals(CombatSessionController.LastSearchRouteForTest, retainedRoute)
                : selected > stoppedIndex,
            $"selected={selected} stopped={stoppedIndex} adopted={adopted}");
        check("not_worse",
            done.Result != null && stopped.Result != null
                && (!stopped.Result.Victory || done.Result.Victory),
            $"stopped_victory={stopped.Result?.Victory} final_victory={done.Result?.Victory}");
    }

    private static async Task ContinueSearchSettingsChangedAsync(NGame host, Check check,
        Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        CombatUiSnapshot stopped = await StopAtMemberAsync(host, action, 1, "stop before settings change");
        check("can_continue", stopped.CanContinueSearch, "continue available before the change");
        PotionSlotUi? slot = stopped.Strategy.Potions.Length > 0 ? stopped.Strategy.Potions[0] : null;
        check("potion_available", slot != null, "the root has a searchable potion");
        SolverPotionMode originalMode = slot!.Mode switch
        {
            "disabled" => SolverPotionMode.Disabled,
            "force" => SolverPotionMode.Force,
            _ => SolverPotionMode.Smart,
        };
        try
        {
            check("settings_command", CombatUiCommands.Instance.SetPotionDirective(
                slot.Slot, slot.PotionId, SolverPotionMode.Disabled), "disable the first potion");
            action($"disable_potion:{slot.Slot}");
            CombatSessionController.RefreshSnapshotForTest();
            CombatUiSnapshot changed = Snapshot();
            check("no_continue", !changed.CanContinueSearch,
                "continue rejected after a settings change");
            string restartText = await SearchButtonTextAsync(host);
            check("restart_text", restartText == SolverText.Get("重新搜索"), restartText);
            int searches = changed.Searches;
            await PressPanelButtonAsync(host, PanelButton.Search, "restart_after_settings");
            action("press_restart_after_settings");
            CombatUiSnapshot done = await WaitAsync(host, static s => !s.IsSearching
                && s.Search?.Phase == SearchUiPhase.Completed, 1800, "restart after settings");
            check("searches_increased", done.Searches == searches + 1,
                $"{searches}->{done.Searches}");
            SearchMemberUi[] members = done.Search?.Members ?? [];
            check("fresh_from_zero", members.Length > 0 && members[0].Ran
                && members[0].StopReason != "stopped", "first member reran from scratch");
            check("fresh_runs_match", done.Search?.MemberRuns.Length == members.Count(
                static member => member.Ran), "a fresh search runs every reported member");
        }
        finally
        {
            check("settings_restored", CombatUiCommands.Instance.SetPotionDirective(
                slot.Slot, slot.PotionId, originalMode), "restore the potion policy");
        }
    }

    private static async Task ContinueSearchStateChangedAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        CombatUiSnapshot stopped = await StopAtMemberAsync(host, action, 1, "stop before state change");
        check("can_continue", stopped.CanContinueSearch, "continue available before the change");
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        Player? player = state == null ? null : LocalContext.GetMe(state);
        check("player_available", player != null, "live player");
        player!.Creature.SetCurrentHpInternal(player.Creature.CurrentHp - 1);
        action("inject_damage:1");
        CombatSessionController.RefreshSnapshotForTest();
        CombatUiSnapshot changed = Snapshot();
        check("no_continue", !changed.CanContinueSearch, "continue rejected after a state change");
        string restartText = await SearchButtonTextAsync(host);
        check("restart_text", restartText == SolverText.Get("重新搜索"), restartText);
        int searches = changed.Searches;
        await PressPanelButtonAsync(host, PanelButton.Search, "restart_after_state");
        action("press_restart_after_state");
        CombatUiSnapshot done = await WaitAsync(host, static s => !s.IsSearching
            && s.Search?.Phase == SearchUiPhase.Completed, 1800, "restart after state");
        check("searches_increased", done.Searches == searches + 1, $"{searches}->{done.Searches}");
        SearchMemberUi[] members = done.Search?.Members ?? [];
        check("fresh_from_zero", members.Length > 0 && members[0].Ran
            && members[0].StopReason != "stopped", "first member reran from scratch");
    }

    private static async Task ContinueSearchLastMemberAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        CombatControllerTestHooks.SearchMillisecondsPerWidthForTest = FastMillisecondsPerWidth;
        await PressPanelButtonAsync(host, PanelButton.Search, "learn_member_count");
        action("press_search_learn_count");
        CombatUiSnapshot complete = await WaitAsync(host, static s => !s.IsSearching
            && s.Search?.Phase == SearchUiPhase.Completed, 3600, "first full search");
        SearchMemberUi[] planned = complete.Search?.Members ?? [];
        check("member_count", planned.Length > 1, $"members={planned.Length}");
        int last = planned.Length - 1;
        CombatControllerTestHooks.StopAtMemberForTest = last;
        await PressPanelButtonAsync(host, PanelButton.Search, "stop_at_last_member");
        action("press_search_stop_last_member");
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => !s.IsSearching && s.PauseCode == "stopped", 3600,
            "stop at the last member");
        CombatControllerTestHooks.StopAtMemberForTest = null;
        SearchMemberUi[] members = stopped.Search?.Members ?? [];
        check("last_member_stopped", members.Length == planned.Length
            && members[last].Ran && members[last].StopReason == "stopped",
            $"members={members.Length}");
        check("no_continue", !stopped.CanContinueSearch, "the last member has nothing to continue");
        string restartText = await SearchButtonTextAsync(host);
        check("restart_text", restartText == SolverText.Get("重新搜索"), restartText);
    }

    private static async Task ContinueSearchTwiceAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        CombatControllerTestHooks.SearchMillisecondsPerWidthForTest = FastMillisecondsPerWidth;
        CombatUiSnapshot first = await StopAtMemberAsync(host, action, 1, "first stop");
        int firstStopped = FindStoppedMember(first);
        check("first_stop", firstStopped == 1 && first.CanContinueSearch,
            $"stopped={firstStopped}");
        PlayerAction[] retained = CombatSessionController.LastSearchRouteForTest;
        int firstAdopted = first.Search?.SelectedMember ?? -1;

        CombatControllerTestHooks.StopAtMemberForTest = 4;
        await PressPanelButtonAsync(host, PanelButton.Search, "continue_then_stop");
        action("press_continue_then_stop");
        CombatUiSnapshot second = await WaitAsync(host,
            static s => !s.IsSearching && s.PauseCode == "stopped", 1800,
            "second stop during the continuation");
        CombatControllerTestHooks.StopAtMemberForTest = null;
        int secondStopped = FindStoppedMember(second);
        PlayerAction[] retainedSecond = CombatSessionController.LastSearchRouteForTest;
        int secondAdopted = second.Search?.SelectedMember ?? -1;
        check("second_stop", secondStopped == 4, $"stopped={secondStopped}");
        check("second_can_continue", second.CanContinueSearch, "second stop can continue again");
        SearchMemberUi[] secondMembers = second.Search?.Members ?? [];
        check("first_stop_carried", firstStopped >= 0 && firstStopped < secondMembers.Length
            && secondMembers[firstStopped].StopReason == "stopped",
            "the first stopped member stays carried");
        check("between_ran", secondStopped > firstStopped
            && secondMembers[firstStopped + 1].Ran && secondMembers[secondStopped - 1].Ran,
            "members between the two stops ran");

        await PressPanelButtonAsync(host, PanelButton.Search, "continue_again");
        action("press_continue_again");
        CombatUiSnapshot done = await WaitAsync(host, static s => !s.IsSearching
            && s.Search?.Phase == SearchUiPhase.Completed, 1800, "second continuation");
        SearchMemberUi[] members = done.Search?.Members ?? [];
        check("both_stops_kept", firstStopped >= 0 && secondStopped < members.Length
            && members[firstStopped].StopReason == "stopped"
            && members[secondStopped].StopReason == "stopped",
            "neither stopped member reran");
        check("after_second_processed", ProcessedAfter(members, secondStopped),
            "members after the second stop ran or were skipped");
        int expectedRuns = 0;
        for (int index = secondStopped + 1; index < members.Length; index++)
            if (members[index].Ran)
                expectedRuns++;
        check("third_runs_match", done.Search?.MemberRuns.Length == expectedRuns,
            $"runs={done.Search?.MemberRuns.Length} expected={expectedRuns}");
        int selected = done.Search?.SelectedMember ?? -1;
        check("not_worse", done.Result != null
            && (selected == secondAdopted
                ? RouteEquals(CombatSessionController.LastSearchRouteForTest, retainedSecond)
                : selected > secondStopped),
            $"selected={selected} second_adopted={secondAdopted}");
        check("first_route_still_owned",
            secondAdopted == firstAdopted && RouteEquals(retainedSecond, retained),
            "the better first-stop route is still the route after the second stop");
    }

    private static async Task ContinueSearchRequestMismatchAsync(NGame host, Check check,
        Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        CombatUiSnapshot stopped = await StopAtMemberAsync(host, action, 1, "stop before tamper");
        check("can_continue", stopped.CanContinueSearch, "continue available before the tamper");
        CombatControllerTestHooks.TamperContinuationCompileForTest =
            static searchCase => searchCase with
            {
                Options = searchCase.Options with { MaxTurns = searchCase.Options.MaxTurns + 1 },
            };
        int searches = stopped.Searches;
        await PressPanelButtonAsync(host, PanelButton.Search, "continue_with_tampered_request");
        action("press_continue_tampered");
        CombatUiSnapshot done = await WaitAsync(host, static s => !s.IsSearching
            && s.Search?.Phase == SearchUiPhase.Completed, 1800, "tampered request restart");
        check("mismatch_logged", CombatSessionController.LastContinuationMismatchForTest
            == "options.max_turns",
            CombatSessionController.LastContinuationMismatchForTest ?? "-");
        check("searches_increased", done.Searches == searches + 1, $"{searches}->{done.Searches}");
        SearchMemberUi[] members = done.Search?.Members ?? [];
        check("fresh_from_zero", members.Length > 0 && members[0].Ran
            && members[0].StopReason != "stopped", "first member reran from scratch");
        check("fresh_runs_match", done.Search?.MemberRuns.Length == members.Count(
            static member => member.Ran), "a fresh search runs every reported member");
        CombatControllerTestHooks.TamperContinuationCompileForTest = null;
    }

    private const int FastMillisecondsPerWidth = 6;

    private static int FindStoppedMember(CombatUiSnapshot snapshot)
    {
        SearchMemberUi[] members = snapshot.Search?.Members ?? [];
        for (int index = members.Length - 1; index >= 0; index--)
            if (members[index].StopReason == "stopped")
                return index;
        return -1;
    }

    private static bool ProcessedAfter(SearchMemberUi[] members, int stoppedIndex)
    {
        if (stoppedIndex < 0 || members.Length <= stoppedIndex + 1)
            return false;
        bool ran = false;
        for (int index = stoppedIndex + 1; index < members.Length; index++)
        {
            if (!members[index].Ran && members[index].SkippedReason == null)
                return false;
            ran |= members[index].Ran;
        }
        return ran;
    }

    private static async Task<string> SearchButtonTextAsync(NGame host)
    {
        await Frames(host, 2);
        // The old single key read 重新搜索 whenever continuing was impossible; the new footer shows a
        // greyed 继续搜索 next to 重新搜索 then.
        SolverOverlay? panel = SolverOverlay.Instance;
        if (panel?.SearchButtonForTest is { Disabled: true } left
            && left.Text == SolverText.Get("继续搜索") && Usable(panel.RestartSearchButtonForTest))
        {
            return SolverText.Get("重新搜索");
        }
        return panel?.SearchButtonForTest?.Text ?? "";
    }

    private static bool RouteEquals(PlayerAction[] left, PlayerAction[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            PlayerAction a = left[index], b = right[index];
            if (a.Kind != b.Kind || a.HandIndex != b.HandIndex || a.Target != b.Target
                || a.PotionSlot != b.PotionSlot || a.ChoiceCount != b.ChoiceCount)
            {
                return false;
            }
            for (int choice = 0; choice < a.ChoiceCount; choice++)
                if (a.Choices[choice] != b.Choices[choice])
                    return false;
        }
        return true;
    }

    private static async Task ExecuteHasResultAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null, 900, "latest result");
        await WaitIdleAsync(host, "idle with result");
        CombatUiSnapshot ready = Snapshot();
        check("has_result", ready.Result != null, "latest result");
        check("can_execute", ready.CurrentTurnRouteReady);
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        check("execute", true, "execute via panel button");
        action("press_execute");
        await WaitAsync(host, static s => s.IsDeploying, 120, "deploying");
        CombatUiSnapshot deploying = Snapshot();
        check("result_cleared", deploying.Result == null, "result cleared on deployment");
        check("route_result_kept", deploying.RouteResult is { } kept
            && kept.ProjectedBattleHpLost == ready.Result!.ProjectedBattleHpLost
            && kept.CombatEndedTurn == ready.Result.CombatEndedTurn,
            $"lost={deploying.RouteResult?.ProjectedBattleHpLost} ended={deploying.RouteResult?.CombatEndedTurn}");
        CombatDisplayNames names = CombatDisplayNames.Capture(CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("no combat state while deploying"));
        string readyOutcome = SolverOverlaySnapshot.Build(ready, names).HpOutcomeText;
        string deployingOutcome = SolverOverlaySnapshot.Build(deploying, names).HpOutcomeText;
        check("panel_outcome_kept", deployingOutcome == readyOutcome,
            $"ready={readyOutcome} deploying={deployingOutcome}");
        await WaitIdleAsync(host, "deployment done");
        check("last_deployed", Snapshot().LastSolverDeployedTurn is not null);
    }

    private static async Task ExecuteAfterStopAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        bool stoppedWithRoute = false;
        for (int attempt = 0; attempt < 3 && !stoppedWithRoute; attempt++)
        {
            await StartManualSearchAsync(host, action, waitCandidate: false);
            if (!await WaitOptionalAsync(host,
                    static s => s.Search?.StopCandidateAvailable == true
                        && s.Search?.CurrentTurnPreview != null
                        && !s.SearchCandidateAllDeath, 600))
            {
                await WaitIdleAsync(host, "search finished before stop");
                continue;
            }
            await Frames(host, 5);
            if (!Snapshot().IsSearching)
            {
                await WaitIdleAsync(host, "search finished before stop");
                continue;
            }
            Button? execute = SolverOverlay.Instance?.ExecuteTurnButtonForTest;
            check("execute_hidden_while_searching", execute != null && !execute.Visible,
                $"visible={execute?.Visible}");
            check("execute_rejected_while_searching", !CombatUiCommands.Instance.ExecuteCurrentTurn(),
                "a running search offers no execute");
            await PressPanelButtonAsync(host, PanelButton.Search, "stop_search");
            action("press_stop_search");
            CombatUiSnapshot stopped = await WaitAsync(host, static s => !s.IsSearching, 300,
                "search stopped");
            stoppedWithRoute = stopped.CurrentTurnRouteReady;
        }
        check("route_retained", stoppedWithRoute, "the stop keeps the best route");
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_after_stop");
        action("press_execute_after_stop");
        check("applied", await WaitOptionalAsync(host, static s => s.IsDeploying, 240),
            "execute after the stop deploys the kept route");
        await WaitIdleAsync(host, "execute deployment done");
    }

    /// <summary>
    /// One search writes its request, result and summary to the combat folder next to combat.json
    /// and events.log.
    /// </summary>
    private static async Task CombatLogFilesAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null, 900, "latest result");
        await WaitIdleAsync(host, "idle with result");
        string? folder = CombatLogRecorder.CurrentFolderForTest;
        check("combat_folder", folder != null, folder);
        if (folder == null)
            return;
        Task flushed = CombatLogRecorder.FlushForTest();
        for (int frame = 0; frame < 600 && !flushed.IsCompleted; frame++)
            await Frames(host, 1);
        check("writes_flushed", flushed.IsCompletedSuccessfully, flushed.Status.ToString());
        HashSet<string> names = [.. Directory.EnumerateFiles(folder)
            .Select(static path => Path.GetFileName(path)!)];
        foreach (string file in new[] { "combat.json", "events.log", "search-001.json",
                     "search-001.sreq.br", "search-001.sres.br" })
            check("file_" + file, names.Contains(file), string.Join(",", names.Order()));
    }

    /// <summary>
    /// Returning to the main menu during a search stops it right away instead of leaving it running
    /// in the menu until the next combat starts, and leaves the controller idle.
    /// </summary>
    private static async Task MainMenuStopsSearchAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.IsSearching, 900, "search running");
        check("search_running", SearchSession.IsRunning, "before main menu");
        action("return_to_main_menu");
        await host.ReturnToMainMenu();
        for (int frame = 0; frame < 60 && SearchSession.IsRunning; frame++)
            await Frames(host, 1);
        CombatUiSnapshot after = Snapshot();
        check("search_stopped", !SearchSession.IsRunning && !after.IsSearching,
            $"running={SearchSession.IsRunning} searching={after.IsSearching}");
        check("controller_idle", after.Phase == CombatUiPhase.Idle, after.Phase.ToString());
        check("not_in_progress", !RunManager.Instance.IsInProgress);
    }

    private static async Task ExecuteWithoutResultAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        CombatUiSnapshot idle = Snapshot();
        check("no_result", idle.Result == null, "result cleared");
        check("not_searching", !idle.IsSearching);
        check("execute_rejected", !CombatUiCommands.Instance.ExecuteCurrentTurn(),
            "execute without a route");
        action("execute_without_result");
        await Frames(host, 20);
        CombatUiSnapshot after = Snapshot();
        check("still_idle", !after.IsSearching && !after.IsDeploying);
        check("searches_unchanged", after.Searches == idle.Searches,
            $"{idle.Searches}->{after.Searches}");
    }

    private static async Task ModeSwitchAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: false);
        int generation = Snapshot().Searches;
        check("to_full_auto", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        check("still_searching", Snapshot().IsSearching, "search not interrupted");
        check("to_auto_search", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        check("still_searching_2", Snapshot().IsSearching, "search not interrupted");
        await WaitIdleAsync(host, "mode switch search done");
        check("no_extra_search", Snapshot().Searches == generation, "mode switch did not search again");
        bool deployed = CombatUiStore.History.Any(static s => s.IsDeploying);
        check("no_deploy", !deployed, "auto search mode does not deploy");
    }

    private static async Task FullAutoStartAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot armed = Snapshot();
        check("full_auto_armed", armed.CombatRunActive, "full auto armed");
        await WaitAsync(host, static s => s.IsSearching || s.IsDeploying, 600,
            "full auto search");
        await WaitAsync(host, static s => s.IsDeploying, 900, "full auto deployment");
        action("full_auto_deploying");
        await WaitIdleAsync(host, "full auto turn done");
        check("last_deployed", Snapshot().LastSolverDeployedTurn is not null);
    }

    private static async Task FullAutoArmAfterTurnAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitIdleAsync(host, "auto search idle");
        CombatSessionController.HoldAutoSearchForTest = true;
        action("hold_auto_search");
        bool switched = false;
        bool wasPlayable = true;
        void OnEnemyTurn(CombatState state)
        {
            if (switched)
                return;
            switched = true;
            if (!CombatSessionController.ArmRunForTest())
                return;
            wasPlayable = CombatUiStore.Snapshot.CanStartSearch;
        }
        CombatManager.Instance.AboutToSwitchToEnemyTurn += OnEnemyTurn;
        try
        {
            await ForceEndTurnAsync(host, waitForPlay: false);
        }
        finally
        {
            CombatManager.Instance.AboutToSwitchToEnemyTurn -= OnEnemyTurn;
        }
        action("force_end_turn");
        check("switched_during_enemy_turn", switched, "mode switched before enemy turn");
        check("armed_not_playable", !wasPlayable, "full auto armed outside play phase");
        CombatUiSnapshot armed = Snapshot();
        check("armed", armed.CombatRunActive, "full auto armed while not playable");
        CombatSessionController.HoldAutoSearchForTest = false;
        CombatSessionController.ResumeAutoSearchForTest();
        action("resume_auto_search");
        CombatUiSnapshot searching = await WaitAsync(host, static s => s.IsSearching, 900,
            "full auto search after turn start");
        check("search_started", searching.Searches >= 1);
        await WaitAsync(host, static s => s.IsDeploying, 1200, "full auto deployment");
        await WaitIdleAsync(host, "full auto turn done");
        check("deployed", Snapshot().LastSolverDeployedTurn is not null);
    }

    private static async Task RepeatedFailureAsync(NGame host, Check check, Action action)
    {
        // This scenario deliberately replays the automatic turn-start request to reach the
        // failure limit; production only allows one automatic turn-start search per turn.
        CombatControllerTestHooks.AllowRepeatedAutoTurnStartForTest = true;
        CombatControllerTestHooks.ForceSearchFailure = static _ => "search_failed";
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot failed = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Failed, 1800, "first search failure");
        check("first_failure", failed.ErrorCode == "search_failed", failed.ErrorCode);
        for (int attempt = 2; attempt <= 3; attempt++)
        {
            CombatSessionController.ResumeAutoSearchForTest();
            action($"resume_search:{attempt}");
            if (attempt < 3)
            {
                failed = await WaitAsync(host, static s => s.Phase == CombatUiPhase.Failed,
                    1800, $"search failure {attempt}");
            }
        }
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "repeated_failure",
            1800, "repeated search failure pause");
        check("pause_code", paused.PauseCode == "repeated_failure", paused.PauseCode);
        check("reason_kind", paused.PauseDetailReason?.Kind == CombatReasonKind.RepeatedFailure,
            paused.PauseDetailReason?.Kind ?? "-");
        check("reason_inner", paused.PauseDetailReason?.Inner?.Kind == CombatReasonKind.SearchFailed,
            paused.PauseDetailReason?.Inner?.Kind ?? "-");
        await WaitStatusAsync(host, SolverText.Get("求解器消息"));
        await Frames(host, 4);
        check("summary", Summary() == SolverPanelText.Pause(paused.PauseCode, paused.Turn)
            + "\n" + SolverPanelText.ErrorReason(paused.PauseDetailReason), Summary());
        check("searches", paused.Searches >= 3, paused.Searches.ToString());
        check("no_deployment_failures", paused.DeploymentFailures == 0,
            paused.DeploymentFailures.ToString());
        check("mode_full_auto", (paused.CombatRunActive || paused.CombatRunPaused), ModeText(paused));
        int searches = paused.Searches;
        await Frames(host, 180);
        CombatUiSnapshot after = Snapshot();
        check("no_fourth_search", after.Searches == searches, $"{searches}->{after.Searches}");
        check("mode_unchanged", after.AutoSearchEnabled == paused.AutoSearchEnabled, ModeText(after));
        check("paused_this_combat", after.CombatRunPaused);
        check("not_armed", !after.CombatRunActive);
        CombatControllerTestHooks.ForceSearchFailure = null;
    }

    private static async Task TurnSetupWaitAsync(NGame host, Check check, Action action)
    {
        int atSelection = CombatControllerTestHooks.TurnSetupSearchCountAtSelection;
        check("no_search_during_setup", atSelection == 0, atSelection.ToString());
        CombatUiSnapshot after = await WaitAsync(host, static s => s.Searches >= 1, 900,
            "search after setup");
        action("setup_completed");
        check("searched_after_setup", after.Searches >= 1, after.Searches.ToString());
        await WaitIdleAsync(host, "setup search done");
    }

    /// <summary>
    /// CombatSolver loaded and only auto search on: the page is searched and its plan published, but
    /// the setup task is not replaced (no AutoPrePlay replacement) and nothing blocks the page. The
    /// player's own pick drops the plan; Play then searches the turn as usual and the route becomes
    /// executable.
    /// </summary>
    private static async Task TurnSetupSearchOnlyAsync(NGame host, Check check, Action action)
    {
        check("combat_solver_loaded", ModEntry.CombatSolverLoaded, "test override");
        await WaitSetupPageAsync(host);
        check("search_only_page", PlayerTurnSetupRuntime.TestLastSetupSearchOnly);
        await WaitSetupSearchAsync(host);
        action("setup_search_started");
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_published", plan.TurnSetupPlanReady && plan.Result != null,
            $"plan={plan.TurnSetupPlanReady} result={plan.Result != null}");
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("route_ready", plan.CurrentTurnRouteReady, "execute keys usable on the page");
        check("not_driving", !Snapshot().TurnSetupDriving && !PlayerTurnSetupRuntime.TestDriving);
        await PlayerTurnSetupRuntime.SelectFirstVisibleForTesting(host, 1, CancellationToken.None);
        action("player_selected");
        await WaitNativePlayAsync(host);
        CombatUiSnapshot after = await WaitAsync(host,
            static s => s.Searches >= 2 && !s.IsSearching && s.Result != null, 2400,
            "play search after the player's pick");
        check("searched_at_play", after.Searches >= 2 && after.CurrentTurnRouteReady,
            $"searches={after.Searches} ready={after.CurrentTurnRouteReady}");
        check("auto_pre_play_not_replaced", PlayerTurnSetupRuntime.TestAutoPrePlayInterceptCount == 0,
            PlayerTurnSetupRuntime.TestAutoPrePlayInterceptCount.ToString());
    }

    /// <summary>
    /// CombatSolver loaded and only auto search on: 执行本回合 on the search-only page drives it like
    /// a player's clicks and then deploys the turn from the same plan, without a second search and
    /// without replacing the setup or AutoPrePlay task.
    /// </summary>
    private static async Task TurnSetupSearchOnlyExecuteAsync(NGame host, Check check, Action action)
    {
        check("combat_solver_loaded", ModEntry.CombatSolverLoaded, "test override");
        await WaitSetupPageAsync(host);
        check("search_only_page", PlayerTurnSetupRuntime.TestLastSetupSearchOnly);
        await WaitSetupSearchAsync(host);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("route_ready", plan.TurnSetupPlanReady && plan.CurrentTurnRouteReady,
            $"plan={plan.TurnSetupPlanReady} ready={plan.CurrentTurnRouteReady}");
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        int searches = plan.Searches;
        int reused = plan.ReusedTurns;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_setup");
        action("press_execute");
        CombatUiSnapshot driving = await WaitAsync(host,
            static s => s.TurnSetupDriving, 600, "turn setup driving");
        check("driving", driving.TurnSetupDriving);
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying, 2400, "turn setup activated");
        check("one_search", activated.Searches == searches, $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > reused, $"{reused}->{activated.ReusedTurns}");
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
        check("auto_pre_play_not_replaced", PlayerTurnSetupRuntime.TestAutoPrePlayInterceptCount == 0,
            PlayerTurnSetupRuntime.TestAutoPrePlayInterceptCount.ToString());
        await WaitIdleAsync(host, "search-only execute done");
    }

    private static async Task<CombatUiSnapshot> WaitSetupPageAsync(NGame host)
        => await WaitAsync(host, static s => s.TurnSetupPageOpen, 2400, "turn setup page");

    private static async Task<CombatUiSnapshot> WaitSetupSearchAsync(NGame host)
        => await WaitAsync(host,
            static s => s.TurnSetupSearchStarted && s.Search?.Reason == "turn_setup",
            2400, "turn setup search");

    private static async Task<CombatUiSnapshot> WaitSetupPlanAsync(NGame host)
        => await WaitAsync(host,
            static s => s.TurnSetupPlanReady && !s.IsSearching, 3600, "turn setup plan");

    private static async Task WaitNativePlayAsync(NGame host, int frames = 2400)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (player?.PlayerCombatState is { Phase: PlayerTurnPhase.Play })
                return;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException("等原生出牌阶段超时。");
    }

    private static async Task<CombatUiSnapshot> WaitSearchPlanAsync(NGame host)
        => await WaitAsync(host,
            static s => s.Result != null && !s.IsSearching && !s.IsDeploying,
            3600, "turn one search plan");

    private static async Task<CombatUiSnapshot> WaitTurnTwoSetupPlanAsync(NGame host)
    {
        await WaitAsync(host,
            static s => s.Turn >= 2 && s.TurnSetupPageOpen, 3600, "turn two setup page");
        return await WaitSetupPlanAsync(host);
    }

    /// <summary>Auto search reuses turn two's plan from the solver's EndTurn instead of searching.</summary>
    private static async Task TurnSetupContinuationAutoAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatUiSnapshot first = await WaitSearchPlanAsync(host);
        check("turn_one_plan", first.CurrentTurnRouteReady, first.CurrentTurnRouteReady.ToString());
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_one");
        action("press_execute_turn_one");
        CombatUiSnapshot plan = await WaitTurnTwoSetupPlanAsync(host);
        int searches = plan.Searches;
        check("continuation", plan.TurnSetupContinuation, plan.TurnSetupContinuation.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("continuation_plan", PlayerTurnSetupRuntime.TestContinuationPlanCount == 1,
            PlayerTurnSetupRuntime.TestContinuationPlanCount.ToString());
        check("choice_count", plan.TurnSetupChoiceCount > 0, plan.TurnSetupChoiceCount.ToString());
        check("no_extra_search", searches == 1, searches.ToString());
        check("page_no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        string[] texts = SetupChoiceTexts(host);
        check("capsule_present", texts.Length > 0, string.Join(" | ", texts));
        await Frames(host, 4);
        check("capsule_highlighted",
            SolverOverlay.Instance?.TurnStartChoicesActiveForTest(plan.Turn) == true,
            $"turn={plan.Turn}");
        int reused = plan.ReusedTurns;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_two_setup");
        action("press_execute_turn_two_setup");
        await WaitAsync(host, static s => s.TurnSetupDriving, 600, "continuation driving");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "continuation activated");
        check("one_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > reused,
            $"{reused}->{activated.ReusedTurns}");
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_deviation", activated.Deviations == 0, activated.Deviations.ToString());
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    /// <summary>A player selection different from the reused plan retires it and searches normally.</summary>
    private static async Task TurnSetupContinuationPlayerAsync(NGame host, Check check,
        Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitSearchPlanAsync(host);
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_one");
        action("press_execute_turn_one");
        CombatUiSnapshot plan = await WaitTurnTwoSetupPlanAsync(host);
        int searches = plan.Searches;
        int reused = plan.ReusedTurns;
        check("continuation", plan.TurnSetupContinuation, plan.TurnSetupContinuation.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        await PlayerTurnSetupRuntime.SelectPlannedOrDifferentForTesting(host, different: true,
            CancellationToken.None);
        action("select_different_card");
        CombatUiSnapshot advanced = await WaitAsync(host,
            static s => !s.TurnSetupPageOpen, 1200, "player advanced from continuation");
        check("stale_plan", !advanced.TurnSetupPlanReady, "plan discarded");
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        await WaitNativePlayAsync(host);
        CombatUiSnapshot after = await WaitAsync(host,
            s => s.Searches > searches, 3600, "normal search after player choice");
        check("searched_again", after.Searches > searches, $"{searches}->{after.Searches}");
        check("not_reused", after.ReusedTurns == reused,
            $"{reused}->{after.ReusedTurns}");
        check("no_error", after.ErrorCode == null, after.ErrorCode);
    }

    /// <summary>A reused plan whose card is absent from the page returns the page and pauses full auto.</summary>
    private static async Task TurnSetupContinuationMismatchAsync(NGame host, Check check,
        Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        check("tamper", CombatControllerTestHooks.TamperTurnSetupContinuationPlanForTest);
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600, "turn two mismatch page");
        action("wait_continuation_mismatch");
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            PlayerTurnSetupRuntime.TestContinuationValidationOk.ToString());
        CombatControllerTestHooks.TamperTurnSetupContinuationPlanForTest = false;
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "choice_mismatch",
            2400, "choice mismatch pause");
        check("pause_code", paused.PauseCode == "choice_mismatch", paused.PauseCode);
        check("page_open", paused.TurnSetupPageOpen);
        check("not_driving", !paused.TurnSetupDriving);
        check("not_plan_ready", !paused.TurnSetupPlanReady);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("full_auto_off", !paused.CombatRunActive);
        check("paused_this_combat", paused.CombatRunPaused);
        check("no_error", paused.ErrorCode == null, paused.ErrorCode);
    }

    /// <summary>The pre-drive full-auto pause also applies to a reused plan.</summary>
    private static async Task TurnSetupContinuationPauseAsync(NGame host, Check check, Action action)
    {
        CombatControllerTestHooks.ForceProjectedDeathTurnSetup = true;
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        action("force_full_auto_pause");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2
                && s.Phase == CombatUiPhase.Paused && s.PauseCode == "death_turn",
            3600, "continuation full auto pause");
        CombatControllerTestHooks.ForceProjectedDeathTurnSetup = false;
        check("pause_code", paused.PauseCode == "death_turn", paused.PauseCode);
        check("page_open", paused.TurnSetupPageOpen);
        check("plan_ready", paused.TurnSetupPlanReady);
        check("continuation", paused.TurnSetupContinuation, paused.TurnSetupContinuation.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("not_driving", !paused.TurnSetupDriving);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("full_auto_off", !paused.CombatRunActive);
        check("paused_this_combat", paused.CombatRunPaused);
    }

    /// <summary>At least three player turns from one search: turns 2 and 3 reuse the EndTurn plan.</summary>
    private static async Task TurnSetupContinuationFullAsync(NGame host, Check check, Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        action("wait_three_turns");
        CombatUiSnapshot third = await WaitAsync(host,
            static s => s.Turn >= 3 && s.ReusedTurns >= 2, 7200, "third player turn");
        check("turn_three", third.Turn >= 3, third.Turn.ToString());
        check("searches_one", third.Searches == 1, third.Searches.ToString());
        check("reused_two", third.ReusedTurns >= 2, third.ReusedTurns.ToString());
        check("zero_deviations", third.Deviations == 0, third.Deviations.ToString());
        check("zero_deployment_failures", third.DeploymentFailures == 0,
            third.DeploymentFailures.ToString());
        check("continuation_plans",
            PlayerTurnSetupRuntime.TestContinuationPlanCount >= 2,
            PlayerTurnSetupRuntime.TestContinuationPlanCount.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 2,
            PlayerTurnSetupRuntime.TestContinuationValidationOk.ToString());
        check("no_error", third.ErrorCode == null, third.ErrorCode);
        // The kept route is shown again on turn 3: the battle's loss so far is what turns 1 and 2 lost,
        // not the turn-1 search root's value.
        CombatState state = CombatManager.Instance.DebugOnlyGetState()!;
        Creature me = LocalContext.GetMe(state)!.Creature;
        int lostBefore = CombatManager.Instance.History.Entries.OfType<MegaCrit.Sts2.Core.Combat.History.Entries.DamageReceivedEntry>()
            .Where(entry => ReferenceEquals(entry.Receiver, me) && !entry.HappenedThisTurn(state))
            .Sum(static entry => Math.Max(0, entry.Result.UnblockedDamage));
        CombatResultUiSnapshot? shown = third.Result ?? third.RouteResult;
        check("kept_route_hp_lost_so_far", lostBefore > 0 && shown?.BattleHpLostSoFar == lostBefore,
            $"history_before_turn={lostBefore} shown={shown?.BattleHpLostSoFar} reused={shown?.WasReused}");
    }

    /// <summary>Source titles come from the localized relic/power/card names, never ids.</summary>
    private static async Task TurnSetupCapsuleSourceAsync(NGame host, Check check, Action action,
        string scenarioId)
    {
        if (!(Snapshot().AutoSearchEnabled && !Snapshot().CombatRunActive))
        {
            check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true),
                "auto search");
            action("set_mode:auto_search");
        }
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        string[] texts = SetupChoiceTexts(host);
        check("capsule_present", texts.Length > 0, string.Join(" | ", texts));
        bool english = SolverText.IsEnglish;
        string expected = (english, scenarioId) switch
        {
            (false, "FLASH-TS-A-TOASTY") => "烘焙手套",
            (true, "FLASH-TS-A-TOASTY") => "Toasty Mittens",
            (false, "FLASH-TS-B-TOOLBOX") => "工具箱",
            (true, "FLASH-TS-B-TOOLBOX") => "Toolbox",
            (false, "TS-G-MULTI-CHOICE") => "暴政",
            (true, "TS-G-MULTI-CHOICE") => "Tyranny",
            (false, "TS-D-AUTOPREPLAY-CHOICE") => "发现",
            (true, "TS-D-AUTOPREPLAY-CHOICE") => "Discovery",
            _ => throw new InvalidOperationException(
                $"没有定义场景 {scenarioId} 的胶囊来源断言。"),
        };
        check("source_title", texts.Any(text => text.Contains(expected, StringComparison.Ordinal)),
            $"{expected}: {string.Join(" | ", texts)}");
        check("no_choice_fallback", texts.All(
            text => !text.Contains("起手选择", StringComparison.Ordinal)
                && !text.Contains("Turn-start choice", StringComparison.Ordinal)),
            string.Join(" | ", texts));
    }

    /// <summary>Turn-two source titles: the reused EndTurn plan must keep its relic/power/card origin.</summary>
    private static async Task TurnSetupCapsuleSourceTurnTwoAsync(NGame host, Check check,
        Action action, string scenarioId)
    {
        check("mode_auto_search", (Snapshot().AutoSearchEnabled && !Snapshot().CombatRunActive),
            ModeText(Snapshot()));
        CombatUiSnapshot first = await WaitSearchPlanAsync(host);
        check("turn_one_plan", first.CurrentTurnRouteReady, first.CurrentTurnRouteReady.ToString());
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_one");
        action("press_execute_turn_one");
        CombatUiSnapshot plan = await WaitTurnTwoSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        // The furnace now pauses the EndTurn setup at the same point as the native page, so the
        // carried plan already contains the paused-in Sovereign Blade candidate and is reused.
        check("continuation", plan.TurnSetupContinuation, plan.TurnSetupContinuation.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("no_validation_fallback",
            PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch == 0,
            PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch.ToString());
        string[] texts = SetupChoiceTexts(host);
        check("capsule_present", texts.Length > 0, string.Join(" | ", texts));
        bool english = SolverText.IsEnglish;
        string expected = (english, scenarioId) switch
        {
            (false, "TS-G-MULTI-CHOICE") => "暴政",
            (true, "TS-G-MULTI-CHOICE") => "Tyranny",
            (false, "TS-D-AUTOPREPLAY-CHOICE") => "发现",
            (true, "TS-D-AUTOPREPLAY-CHOICE") => "Discovery",
            _ => throw new InvalidOperationException(
                $"没有定义场景 {scenarioId} 的后续回合胶囊来源断言。"),
        };
        check("source_title", texts.Any(text => text.Contains(expected, StringComparison.Ordinal)),
            $"{expected}: {string.Join(" | ", texts)}");
        check("no_choice_fallback", texts.All(
            text => !text.Contains("起手选择", StringComparison.Ordinal)
                && !text.Contains("Turn-start choice", StringComparison.Ordinal)),
            string.Join(" | ", texts));
    }

    /// <summary>
    /// Research probe: reuses the previous EndTurn plan when possible and records whether the exact
    /// Play-state comparison accepted or rejected it. A miss is not a scenario failure.
    /// </summary>
    private static async Task TurnSetupContinuationProbeAsync(NGame host, Check check,
        Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600, "continuation probe page");
        action("wait_continuation_probe");
        check("page_open", page.TurnSetupPageOpen);
        for (int frame = 0; frame < 600; frame++)
        {
            if (PlayerTurnSetupRuntime.TestContinuationPlanCount > 0
                || PlayerTurnSetupRuntime.TestContinuationValidationOk > 0
                || PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch > 0
                || PlayerTurnSetupRuntime.TestContinuationValidationPlayMismatch > 0
                || PlayerTurnSetupRuntime.TestContinuationValidationReplayFailed > 0)
            {
                break;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        string outcome = "none";
        string outcomeDetail = "";
        if (PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch > 0)
        {
            outcome = "validation_candidate";
            outcomeDetail = PlayerTurnSetupRuntime.TestLastContinuationValidationDetail ?? "";
        }
        else if (PlayerTurnSetupRuntime.TestContinuationValidationPlayMismatch > 0)
        {
            outcome = "validation_play";
            outcomeDetail = PlayerTurnSetupRuntime.TestLastContinuationValidationDetail ?? "";
        }
        else if (PlayerTurnSetupRuntime.TestContinuationValidationReplayFailed > 0)
        {
            outcome = "validation_replay";
            outcomeDetail = PlayerTurnSetupRuntime.TestLastContinuationValidationDetail ?? "";
        }
        for (int frame = 0; frame < 3600 && outcome == "none"; frame++)
        {
            int successes = CombatSessionController.ContinuationReuseSuccessesForTest;
            int misses = CombatSessionController.ContinuationReuseMissesForTest;
            CombatUiSnapshot current = Snapshot();
            if (successes > 0)
            {
                outcome = "reuse_ok";
                break;
            }
            if (misses > 0)
            {
                outcome = "reuse_miss";
                outcomeDetail = CombatSessionController.ContinuationReuseMissDetailForTest ?? "";
                break;
            }
            if (current.PauseCode == "choice_mismatch")
            {
                outcome = "plan_mismatch";
                outcomeDetail = current.PauseReason ?? "";
                break;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        check("probe_outcome", outcome != "none", $"{outcome} {outcomeDetail}");
        check("reuse_outcome", outcome is "reuse_ok" or "reuse_miss" or "plan_mismatch"
                or "validation_candidate" or "validation_play" or "validation_replay",
            $"{outcome} {outcomeDetail}");
        CombatUiSnapshot after = Snapshot();
        check("searches", after.Searches is 1 or 2, after.Searches.ToString());
        check("no_error", after.ErrorCode == null, after.ErrorCode);
        action($"probe={outcome} detail={outcomeDetail}");
    }

    /// <summary>Pre-validation tamper rejects the plan before the page is touched; a new search runs.</summary>
    private static async Task TurnSetupContinuationValidationAsync(NGame host, Check check,
        Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        await WaitAsync(host, static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600,
            "validation reject page");
        action("wait_validation_reject");
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("validation_candidate",
            PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch == 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("no_continuation_plan", PlayerTurnSetupRuntime.TestContinuationPlanCount == 0,
            PlayerTurnSetupRuntime.TestContinuationPlanCount.ToString());
        check("searches_two", plan.Searches == 2, plan.Searches.ToString());
        check("not_continuation", !plan.TurnSetupContinuation,
            plan.TurnSetupContinuation.ToString());
        check("no_pause", plan.PauseCode == null, plan.PauseCode);
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "validation reject activated");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_deviation", activated.Deviations == 0, activated.Deviations.ToString());
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    /// <summary>
    /// Furnace pause regression for ts-g-multi-choice. The kernel used to answer the turn-start
    /// choices before the side start, so the carried plan had one fewer candidate than the native
    /// page and validation had to fall back to a new setup search. EndTurn now pauses at the first
    /// turn-start choice exactly like PreparePlayerPlayPhase, so the plan validates and is reused
    /// without a second search.
    /// </summary>
    private static async Task TurnSetupContinuationGapAsync(NGame host, Check check, Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600, "furnace pause page");
        action("wait_furnace_pause");
        check("page_open", page.TurnSetupPageOpen);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk == 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("no_validation_candidate",
            PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch == 0,
            PlayerTurnSetupRuntime.TestContinuationValidationCandidateMismatch.ToString());
        check("continuation_plan", PlayerTurnSetupRuntime.TestContinuationPlanCount == 1,
            PlayerTurnSetupRuntime.TestContinuationPlanCount.ToString());
        check("searches_one", plan.Searches == 1, plan.Searches.ToString());
        check("continuation", plan.TurnSetupContinuation, plan.TurnSetupContinuation.ToString());
        check("not_paused", Snapshot().Phase != CombatUiPhase.Paused, Snapshot().PauseCode);
        check("no_pause_code", plan.PauseCode == null, plan.PauseCode);
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "furnace reused plan activated");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        check("searches_still_one", activated.Searches == 1, activated.Searches.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_deviation", activated.Deviations == 0, activated.Deviations.ToString());
        check("zero_deployment_failures", activated.DeploymentFailures == 0,
            activated.DeploymentFailures.ToString());
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    private static string[] SetupChoiceTexts(NGame host)
    {
        CombatState state = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("截图胶囊时没有战斗状态。");
        CombatUiSnapshot snapshot = Snapshot();
        SolverOverlaySnapshot overlay = SolverOverlaySnapshot.Build(
            snapshot, CombatDisplayNames.Capture(state));
        return [.. overlay.Turns.SelectMany(static turn => turn.TurnStartChoices)];
    }

    private static async Task TurnSetupAutoExecuteAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatUiSnapshot page = await WaitSetupPageAsync(host);
        check("page_open", page.TurnSetupPageOpen);
        check("page_no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        CombatUiSnapshot searching = await WaitSetupSearchAsync(host);
        check("search_reason", searching.Search?.Reason == "turn_setup", searching.Search?.Reason);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("choice_count", plan.TurnSetupChoiceCount > 0, plan.TurnSetupChoiceCount.ToString());
        check("route_ready", plan.CurrentTurnRouteReady);
        check("route_capsule",
            plan.Route?.Turns.Length > 0
            && plan.Route.Turns[0].TurnStartChoices.Length > 0);
        if (CombatControllerTestHooks.ExpectedSetupOptionCount >= 0)
        {
            check("option_count",
                PlayerTurnSetupRuntime.TestFirstVisibleOptionCount
                    == CombatControllerTestHooks.ExpectedSetupOptionCount,
                PlayerTurnSetupRuntime.TestFirstVisibleOptionCount.ToString());
            check("can_skip", !PlayerTurnSetupRuntime.TestFirstVisibleCanSkip);
        }
        check("no_pause", plan.PauseCode == null, plan.PauseCode);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        int searches = plan.Searches;
        int reused = plan.ReusedTurns;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_setup");
        action("press_execute");
        CombatUiSnapshot driving = await WaitAsync(host,
            static s => s.TurnSetupDriving, 600, "turn setup driving");
        check("driving", driving.TurnSetupDriving);
        check("driving_blocker",
            PlayerTurnSetupRuntime.TestBlockerActive
            || PlayerTurnSetupRuntime.TestBlockerAcquisitionCount > 0,
            $"active={PlayerTurnSetupRuntime.TestBlockerActive} " +
            $"count={PlayerTurnSetupRuntime.TestBlockerAcquisitionCount}");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying, 2400, "turn setup activated");
        check("one_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > reused,
            $"{reused}->{activated.ReusedTurns}");
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    private static async Task TurnSetupAutoManualAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitSetupPageAsync(host);
        await WaitSetupSearchAsync(host);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        int searches = plan.Searches;
        int reused = plan.ReusedTurns;
        await PlayerTurnSetupRuntime.SelectPlannedOrDifferentForTesting(host, different: true,
            CancellationToken.None);
        action("select_different_card");
        CombatUiSnapshot advanced = await WaitAsync(host,
            static s => !s.TurnSetupPageOpen, 1200, "player advanced");
        check("stale_plan", !advanced.TurnSetupPlanReady, "plan discarded");
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        await WaitNativePlayAsync(host);
        CombatUiSnapshot after = await WaitAsync(host,
            s => s.Searches > searches, 3600, "normal search after player choice");
        check("searched_again", after.Searches > searches, $"{searches}->{after.Searches}");
        check("not_reused", after.ReusedTurns == reused,
            $"{reused}->{after.ReusedTurns}");
        check("no_error", after.ErrorCode == null, after.ErrorCode);
    }

    private static async Task TurnSetupFullAsync(NGame host, Check check, Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying, 3600,
            "full auto setup activated");
        check("one_search", activated.Searches == 1, activated.Searches.ToString());
        check("reused", activated.ReusedTurns == 1, activated.ReusedTurns.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        bool visible = NativeChoiceRuntime.TraceSnapshotForTesting.Any(static trace =>
            trace.Owner.StartsWith("turn_setup:", StringComparison.Ordinal)
            && trace.Stage == "Visible");
        bool searchStarted = NativeChoiceRuntime.TraceSnapshotForTesting.Any(static trace =>
            trace.Owner.StartsWith("turn_setup:", StringComparison.Ordinal)
            && trace.Stage == "SearchStarted");
        bool selected = NativeChoiceRuntime.TraceSnapshotForTesting.Any(static trace =>
            trace.Owner.StartsWith("turn_setup:", StringComparison.Ordinal)
            && trace.Stage == "Selected");
        check("native_visible", visible, "no Visible trace");
        check("native_search_started", searchStarted, "no SearchStarted trace");
        check("native_selected", selected, "no Selected trace");
        action("wait_full_auto_setup");
    }

    private static async Task TurnSetupStopKeepAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitSetupPageAsync(host);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        CombatControllerTestHooks.AutoStopOnCandidate = false;
        action("auto_stop_on_candidate");
        check("stopped", plan.PauseCode == "stopped", plan.PauseCode);
        check("page_open", plan.TurnSetupPageOpen);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("choice_count", plan.TurnSetupChoiceCount > 0, plan.TurnSetupChoiceCount.ToString());
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("not_driving", !plan.TurnSetupDriving);
        int searches = plan.Searches;
        int reused = plan.ReusedTurns;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_retained_plan");
        action("press_execute_after_stop");
        await WaitAsync(host, static s => s.TurnSetupDriving, 600, "retained plan driving");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying, 2400, "retained plan activated");
        check("one_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > reused,
            $"{reused}->{activated.ReusedTurns}");
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
    }

    private static async Task TurnSetupStopDropAsync(NGame host, Check check, Action action,
        bool forcedFailure)
    {
        if (!forcedFailure)
        {
            check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true),
                "auto search");
            action("set_mode:auto_search");
        }
        else
        {
            action("force_search_failure");
        }
        await WaitSetupPageAsync(host);
        if (forcedFailure)
        {
            CombatUiSnapshot failed = await WaitAsync(host,
                static s => s.ErrorCode == "turn_setup_failed", 2400, "turn setup failure");
            check("error_code", failed.ErrorCode == "turn_setup_failed", failed.ErrorCode);
            check("reason_kind",
                failed.ErrorReason?.Kind == CombatReasonKind.TurnSetupFailed,
                failed.ErrorReason?.Kind ?? "-");
            await WaitStatusAsync(host, SolverText.Get("求解器消息"));
            check("summary_title",
                Summary().Contains(SolverText.Get("回合准备选牌失败"), StringComparison.Ordinal),
                Summary());
            check("summary_reason",
                Summary().Contains(SolverText.Get("没有找到可执行的回合开始选牌方案"),
                    StringComparison.Ordinal), Summary());
            check("page_open", failed.TurnSetupPageOpen);
            check("no_plan", !failed.TurnSetupPlanReady);
            check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
            check("not_faulted", true, "replacement task completed");
            CombatControllerTestHooks.ForceSearchFailure = null;
        }
        else
        {
            await WaitSetupPageAsync(host);
            CombatUiSnapshot stopped = await WaitAsync(host,
                static s => !s.IsSearching, 2400, "turn setup stopped");
            check("stopped", stopped.PauseCode == "stopped", stopped.PauseCode);
            check("page_open", stopped.TurnSetupPageOpen);
            check("no_plan", !stopped.TurnSetupPlanReady);
            check("choice_count", stopped.TurnSetupChoiceCount == 0,
                stopped.TurnSetupChoiceCount.ToString());
            check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        }
        int before = Snapshot().Searches;
        await PlayerTurnSetupRuntime.SelectFirstVisibleForTesting(host, 1, CancellationToken.None);
        action("select_first_card");
        await WaitNativePlayAsync(host);
        // AutoPrePlay is the last native phase before Play; the setup transaction releases the
        // page in its finally, one frame after the phase becomes Play.
        CombatUiSnapshot played = await WaitAsync(host,
            static s => !s.TurnSetupPageOpen, 300, "setup page closed after player choice");
        check("page_closed", !played.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        await PressPanelButtonAsync(host, PanelButton.Search, "search_after_setup");
        action("press_search_after_setup");
        CombatUiSnapshot searched = await WaitAsync(host,
            s => s.Searches > before, 1200, "search after setup page");
        check("normal_search", searched.Searches > before,
            $"{before}->{searched.Searches}");
        check("no_setup_error", searched.ErrorCode == null, searched.ErrorCode);
    }

    private static async Task TurnSetupStopDriveAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        await WaitSetupPageAsync(host);
        await WaitSetupSearchAsync(host);
        await WaitSetupPlanAsync(host);
        await WaitAsync(host, static s => s.TurnSetupDriving, 1200, "setup driving");
        await PressPanelButtonAsync(host, PanelButton.Execute, "stop_turn_setup_drive");
        action("press_stop_drive");
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => !s.TurnSetupDriving, 240, "turn setup drive stopped");
        check("stopped", stopped.PauseCode == "stopped", stopped.PauseCode);
        check("page_open", stopped.TurnSetupPageOpen);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        int searches = stopped.Searches;
        int reused = stopped.ReusedTurns;
        await PlayerTurnSetupRuntime.SelectFirstVisibleForTesting(host, 1, CancellationToken.None);
        action("select_after_stop");
        await WaitNativePlayAsync(host);
        await Frames(host, 120);
        CombatUiSnapshot after = Snapshot();
        check("not_reused", after.ReusedTurns == reused,
            $"{reused}->{after.ReusedTurns}");
        check("no_deploy", !after.IsDeploying, "stopped drive must not deploy");
        check("no_extra_search", after.Searches == searches,
            $"{searches}->{after.Searches}");
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
    }

    private static async Task TurnSetupPauseAsync(NGame host, Check check, Action action)
    {
        CombatControllerTestHooks.ForceProjectedDeathTurnSetup = true;
        check("mode_set", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused)
            || CombatSessionController.ArmRunForTest(), "full auto");
        action("force_full_auto_pause");
        await WaitSetupPageAsync(host);
        await WaitSetupSearchAsync(host);
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "death_turn",
            2400, "setup full auto pause");
        check("page_open", paused.TurnSetupPageOpen);
        check("plan_ready", paused.TurnSetupPlanReady);
        check("not_driving", !paused.TurnSetupDriving);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("full_auto_off", !paused.CombatRunActive);
        check("paused_this_combat", paused.CombatRunPaused);
        CombatControllerTestHooks.ForceProjectedDeathTurnSetup = false;
        int reused = paused.ReusedTurns;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_after_setup_pause");
        action("press_execute_after_pause");
        await WaitAsync(host, static s => s.TurnSetupDriving, 600, "pause override driving");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying, 2400, "pause override activated");
        check("reused", activated.ReusedTurns > reused,
            $"{reused}->{activated.ReusedTurns}");
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
    }

    private static async Task TurnSetupSceneExitAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitSetupPageAsync(host);
        await WaitSetupSearchAsync(host);
        action("return_to_main_menu");
        await host.ReturnToMainMenu();
        await Frames(host, 30);
        check("page_closed", !PlayerTurnSetupRuntime.TestPageVisible);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("not_in_progress", !RunManager.Instance.IsInProgress);
    }

    private static async Task TurnSetupImplicitAsync(NGame host, Check check, Action action)
    {
        check("no_setup_page", !PlayerTurnSetupRuntime.TestPageVisible);
        check("no_setup_search", Snapshot().Searches == 0, Snapshot().Searches.ToString());
        bool implicitRequested = NativeChoiceRuntime.TraceSnapshotForTesting.Any(static trace =>
            trace.Surface == NativeChoiceSurfaceKind.Hand && trace.Stage == "Requested");
        bool visible = NativeChoiceRuntime.TraceSnapshotForTesting.Any(static trace =>
            trace.Stage == "Visible");
        check("implicit_requested", implicitRequested, "no Hand request");
        check("implicit_no_page", !visible, "implicit selection must not open a page");
        bool selected = NativeChoiceRuntime.TraceSnapshotForTesting.Any(static trace =>
            trace.Stage == "Selected");
        check("no_driver", !selected);
        CombatSessionController.ResumeAutoSearchForTest();
        action("resume_auto_search");
        CombatUiSnapshot searched = await WaitAsync(host,
            static s => s.Searches >= 1, 1200, "play search after implicit setup");
        check("play_search", searched.Searches >= 1, searched.Searches.ToString());
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
    }

    private static async Task TurnSetupManualAsync(NGame host, Check check, Action action)
    {
        int atSelection = CombatControllerTestHooks.TurnSetupSearchCountAtSelection;
        check("no_setup_search_at_selection", atSelection == 0, atSelection.ToString());
        check("no_setup_search", Snapshot().Searches == 0, Snapshot().Searches.ToString());
        check("no_setup_page", !Snapshot().TurnSetupPageOpen);
        CombatSessionController.ResumeAutoSearchForTest();
        await Frames(host, 120);
        check("still_manual", Snapshot().Searches == 0, Snapshot().Searches.ToString());
        await PressPanelButtonAsync(host, PanelButton.Search, "manual_search");
        action("press_search");
        CombatUiSnapshot searching = await WaitAsync(host,
            static s => s.IsSearching, 600, "manual search after setup");
        check("manual_search_started", searching.Search?.Reason == "manual",
            searching.Search?.Reason);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
    }

    /// <summary>
    /// Full-auto scenario for a turn whose setup page appears after the solver ended the previous
    /// turn. The request is built to turn-one Play and the takeover is enabled before turn two.
    /// </summary>
    /// <summary>
    /// Same takeover/continuation path as turn two, but the native turn loop is taking an extra
    /// turn (PaelsEye with no cards played on turn one). The setup page, its plan and the first
    /// deployment boundary must all belong to that extra turn.
    /// </summary>
    private static async Task TurnSetupExtraTurnAsync(NGame host, Check check, Action action)
    {
        await TurnSetupTurnTwoAsync(host, check, action, fullAuto: true);
        check("extra_turn_setup", PlayerTurnSetupRuntime.TestLastInterceptTakingExtraTurn,
            "the intercepted SetupPlayerTurn is an extra turn");
    }

    /// <summary>
    /// The player ends turn one manually, so no EndTurn setup plan exists; the extra turn's
    /// PaelsEye/Toasty setup page must be enumerated and searched from the live Start root.
    /// </summary>
    private static async Task TurnSetupExtraTurnSearchAsync(NGame host, Check check, Action action)
    {
        check("mode_manual", (!Snapshot().AutoSearchEnabled && !Snapshot().CombatRunActive), ModeText(Snapshot()));
        check("no_turn_one_search", Snapshot().Searches == 0, Snapshot().Searches.ToString());
        CombatSessionController.HoldAutoSearchForTest = true;
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await StartEndTurnAsync(host);
        action("end_turn_one");
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600,
            "extra turn setup page");
        check("turn_two", page.Turn == 2, page.Turn.ToString());
        check("intercept_once", PlayerTurnSetupRuntime.TestInterceptCount == 1,
            PlayerTurnSetupRuntime.TestInterceptCount.ToString());
        check("selector_null_at_intercept", PlayerTurnSetupRuntime.TestSelectorNullAtLastIntercept);
        check("extra_turn_setup", PlayerTurnSetupRuntime.TestLastInterceptTakingExtraTurn,
            "the intercepted SetupPlayerTurn is an extra turn");
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("choice_count", plan.TurnSetupChoiceCount > 0, plan.TurnSetupChoiceCount.ToString());
        check("no_continuation", !plan.TurnSetupContinuation,
            plan.TurnSetupContinuation.ToString());
        check("no_continuation_plan", PlayerTurnSetupRuntime.TestContinuationPlanCount == 0,
            PlayerTurnSetupRuntime.TestContinuationPlanCount.ToString());
        check("setup_search_started", plan.TurnSetupSearchStarted,
            plan.TurnSetupSearchStarted.ToString());
        check("no_pause", plan.PauseCode == null, plan.PauseCode);
        int searches = plan.Searches;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_two_setup");
        action("press_execute_turn_two_setup");
        CombatSessionController.HoldAutoSearchForTest = false;
        CombatSessionController.ResumeAutoSearchForTest();
        action("resume_auto_search");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "extra turn search setup activated");
        check("one_setup_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_deviation", activated.Deviations == 0, activated.Deviations.ToString());
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    private static async Task TurnSetupTurnTwoAsync(NGame host, Check check, Action action,
        bool fullAuto)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused) || !fullAuto,
            ModeText(Snapshot()));
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600, "turn two setup page");
        action("wait_turn_two_setup");
        check("turn_two", page.Turn == 2, page.Turn.ToString());
        check("intercept_once", PlayerTurnSetupRuntime.TestInterceptCount == 1,
            PlayerTurnSetupRuntime.TestInterceptCount.ToString());
        check("intercept_turn", PlayerTurnSetupRuntime.TestLastInterceptTurn == 2,
            PlayerTurnSetupRuntime.TestLastInterceptTurn.ToString());
        check("selector_null_at_intercept", PlayerTurnSetupRuntime.TestSelectorNullAtLastIntercept);
        check("page_blocker_or_driving",
            !PlayerTurnSetupRuntime.TestBlockerActive
                || PlayerTurnSetupRuntime.TestBlockerAcquisitionCount > 0,
            $"active={PlayerTurnSetupRuntime.TestBlockerActive} " +
            $"count={PlayerTurnSetupRuntime.TestBlockerAcquisitionCount}");
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("choice_count", plan.TurnSetupChoiceCount > 0, plan.TurnSetupChoiceCount.ToString());
        check("continuation", plan.TurnSetupContinuation, plan.TurnSetupContinuation.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("continuation_plan", PlayerTurnSetupRuntime.TestContinuationPlanCount == 1,
            PlayerTurnSetupRuntime.TestContinuationPlanCount.ToString());
        check("searches_one", plan.Searches == 1, plan.Searches.ToString());
        check("no_setup_search", !plan.TurnSetupSearchStarted,
            plan.TurnSetupSearchStarted.ToString());
        check("superseded_deployment",
            CombatSessionController.SetupSupersededDeploymentCount == 1,
            CombatSessionController.SetupSupersededDeploymentCount.ToString());
        check("predicted_start_compared", PlayerTurnSetupRuntime.TestPredictedStartCompared);
        check("predicted_start_matched", PlayerTurnSetupRuntime.TestPredictedStartMatched);
        check("no_pause", plan.PauseCode == null, plan.PauseCode);
        int searches = plan.Searches;
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "turn two setup activated");
        check("one_setup_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_deviation", activated.Deviations == 0, activated.Deviations.ToString());
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
        check("continuation_reuse",
            CombatSessionController.ContinuationReuseSuccessesForTest
                + CombatSessionController.ContinuationReuseMissesForTest == 1,
            $"ok={CombatSessionController.ContinuationReuseSuccessesForTest} " +
            $"miss={CombatSessionController.ContinuationReuseMissesForTest} " +
            $"{CombatSessionController.ContinuationReuseMissDetailForTest}");
    }

    /// <summary>
    /// The setup search must start at the first visible AutoPrePlay page and must not search a
    /// second time; the same plan includes the AutoPrePlay selection.
    /// </summary>
    private static async Task TurnSetupAutoPrePlayAsync(NGame host, Check check, Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600, "auto pre play setup page");
        action("wait_auto_pre_play_page");
        check("turn_two", page.Turn == 2, page.Turn.ToString());
        check("intercept_once", PlayerTurnSetupRuntime.TestInterceptCount == 1,
            PlayerTurnSetupRuntime.TestInterceptCount.ToString());
        check("auto_pre_play_intercept", PlayerTurnSetupRuntime.TestAutoPrePlayInterceptCount == 1,
            PlayerTurnSetupRuntime.TestAutoPrePlayInterceptCount.ToString());
        check("no_auto_pre_play_search",
            !PlayerTurnSetupRuntime.TestLastSearchStartedInAutoPrePlay,
            PlayerTurnSetupRuntime.TestLastSearchStartedInAutoPrePlay.ToString());
        check("selector_null_at_intercept", PlayerTurnSetupRuntime.TestSelectorNullAtLastIntercept);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("plan_has_choice", plan.TurnSetupChoiceCount > 0,
            plan.TurnSetupChoiceCount.ToString());
        check("continuation", plan.TurnSetupContinuation, plan.TurnSetupContinuation.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("continuation_plan", PlayerTurnSetupRuntime.TestContinuationPlanCount == 1,
            PlayerTurnSetupRuntime.TestContinuationPlanCount.ToString());
        check("searches_one", plan.Searches == 1, plan.Searches.ToString());
        check("no_pause", plan.PauseCode == null, plan.PauseCode);
        int searches = plan.Searches;
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "auto pre play setup activated");
        check("one_setup_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_deviation", activated.Deviations == 0, activated.Deviations.ToString());
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    private static async Task TurnSetupPlasmaAsync(NGame host, Check check, Action action)
        => await TurnSetupTurnTwoAsync(host, check, action, fullAuto: true);

    /// <summary>
    /// Turn one uses a forced Blood Potion, so RecoveredHp is non-zero in the verified EndTurn
    /// boundary. Turn two's setup root, selected Play root and first deployment boundary must all
    /// carry the same value, and the predicted Start root must match the live import.
    /// Request: <c>ts-a-toasty</c>, FullAuto takeover, <c>BLOOD_POTION</c> injected and forced,
    /// <c>initialPlayerHp=1</c> of 80, <c>enemyCurrentHp=120</c>. On the e527fdd3 baseline this
    /// exact request passes with RecoveredHp=16; 40/80 (even with enemy 120) heals 0 on both the
    /// baseline and this branch, so it is a route/config property, not a regression.
    /// </summary>
    private static async Task TurnSetupCarryAsync(NGame host, Check check, Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600, "turn two carry setup page");
        action("wait_turn_two_carry_setup");
        check("turn_two", page.Turn == 2, page.Turn.ToString());
        check("intercept_once", PlayerTurnSetupRuntime.TestInterceptCount == 1,
            PlayerTurnSetupRuntime.TestInterceptCount.ToString());
        check("selector_null_at_intercept", PlayerTurnSetupRuntime.TestSelectorNullAtLastIntercept);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("choice_count", plan.TurnSetupChoiceCount > 0, plan.TurnSetupChoiceCount.ToString());
        check("forced_directive",
            CombatSessionController.LastSearchCaseForTest?.Options.Potions.Strategy
                .HasForcedDirectives == true,
            "turn-one request carries the forced potion directive");
        check("forced_route_uses_potion",
            CombatSessionController.LastSearchRouteForTest.Any(
                static action => action.Kind == PlayerActionKind.UsePotion),
            "turn-one route uses the forced potion");
        check("continuation", plan.TurnSetupContinuation, plan.TurnSetupContinuation.ToString());
        check("validation_ok", PlayerTurnSetupRuntime.TestContinuationValidationOk >= 1,
            $"{PlayerTurnSetupRuntime.TestContinuationValidationOk} " +
            $"{PlayerTurnSetupRuntime.TestLastContinuationValidationDetail}");
        check("searches_one", plan.Searches == 1, plan.Searches.ToString());
        check("predicted_start_compared", PlayerTurnSetupRuntime.TestPredictedStartCompared);
        check("predicted_start_matched", PlayerTurnSetupRuntime.TestPredictedStartMatched);
        int carried = PlayerTurnSetupRuntime.TestSetupStartRecoveredHp;
        check("carried_ledger_nonzero", carried > 0, carried.ToString());
        check("play_ledger", PlayerTurnSetupRuntime.TestSetupPlayRecoveredHp == carried,
            $"{carried}->{PlayerTurnSetupRuntime.TestSetupPlayRecoveredHp}");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "turn two carry activated");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        bool observed = false;
        for (int frame = 0; frame < 2400; frame++)
        {
            if (CombatControllerTestHooks.TurnSetupDeployedRecoveredHp >= 0)
            {
                observed = true;
                break;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        check("deployment_observed", observed,
            observed ? "observed" : "no turn-two deployment boundary");
        check("deployment_ledger",
            CombatControllerTestHooks.TurnSetupDeployedRecoveredHp == carried,
            $"{carried}->{CombatControllerTestHooks.TurnSetupDeployedRecoveredHp}");
        check("no_deviation", Snapshot().Deviations == 0, Snapshot().Deviations.ToString());
        check("no_error", Snapshot().ErrorCode == null, Snapshot().ErrorCode);
    }

    /// <summary>
    /// Auto-search mode, player ends turn one with the native end-turn action, turn two's setup
    /// page is searched and then driven by the panel's execute command.
    /// </summary>
    private static async Task TurnSetupManualEndAsync(NGame host, Check check, Action action)
    {
        check("mode_manual", (!Snapshot().AutoSearchEnabled && !Snapshot().CombatRunActive), ModeText(Snapshot()));
        check("no_turn_one_search", Snapshot().Searches == 0, Snapshot().Searches.ToString());
        await StartEndTurnAsync(host);
        action("end_turn_one");
        await WaitNativeSideAsync(host, CombatSide.Enemy);
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatUiSnapshot page = await WaitAsync(host,
            static s => s.TurnSetupPageOpen && s.Turn >= 2, 3600, "manual end turn two setup page");
        check("turn_two", page.Turn == 2, page.Turn.ToString());
        check("intercept_once", PlayerTurnSetupRuntime.TestInterceptCount == 1,
            PlayerTurnSetupRuntime.TestInterceptCount.ToString());
        check("selector_null_at_intercept", PlayerTurnSetupRuntime.TestSelectorNullAtLastIntercept);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("choice_count", plan.TurnSetupChoiceCount > 0, plan.TurnSetupChoiceCount.ToString());
        check("one_search", plan.Searches == 1, plan.Searches.ToString());
        check("no_pause", plan.PauseCode == null, plan.PauseCode);
        int searches = plan.Searches;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_turn_two_setup");
        action("press_execute_turn_two_setup");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying && s.Turn >= 2, 3600,
            "manual end setup activated");
        check("one_setup_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_deviation", activated.Deviations == 0, activated.Deviations.ToString());
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    /// <summary>
    /// Two PlayerTurnStart pages in the same setup phase (ToastyMittens then GamblingChip). The
    /// plan must contain both selections and the driver must consume both in order.
    /// </summary>
    private static async Task TurnSetupMultiAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatUiSnapshot page = await WaitSetupPageAsync(host);
        check("page_open", page.TurnSetupPageOpen);
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("two_choices", plan.TurnSetupChoiceCount == 2,
            plan.TurnSetupChoiceCount.ToString());
        check("one_search", plan.Searches == 1, plan.Searches.ToString());
        int searches = plan.Searches;
        int reused = plan.ReusedTurns;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_multi_stage_setup");
        action("press_execute_multi_stage");
        await WaitAsync(host, static s => s.TurnSetupDriving, 600, "multi stage driving");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying, 2400, "multi stage activated");
        check("one_setup_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > reused,
            $"{reused}->{activated.ReusedTurns}");
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
        int selected = NativeChoiceRuntime.TraceSnapshotForTesting.Count(static trace =>
            trace.Owner.StartsWith("turn_setup:", StringComparison.Ordinal)
            && trace.Stage == "Selected");
        check("two_pages_consumed", selected >= 2, selected.ToString());
    }

    private static async Task TurnSetupCrossbowAsync(NGame host, Check check, Action action,
        int expectedOptions)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatUiSnapshot page = await WaitSetupPageAsync(host);
        check("page_open", page.TurnSetupPageOpen);
        check("option_count",
            PlayerTurnSetupRuntime.TestFirstVisibleOptionCount == expectedOptions,
            PlayerTurnSetupRuntime.TestFirstVisibleOptionCount.ToString());
        CombatUiSnapshot plan = await WaitSetupPlanAsync(host);
        check("plan_ready", plan.TurnSetupPlanReady);
        check("one_choice", plan.TurnSetupChoiceCount == 1,
            plan.TurnSetupChoiceCount.ToString());
        check("one_search", plan.Searches == 1, plan.Searches.ToString());
        int searches = plan.Searches;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_crossbow_setup");
        action("press_execute_crossbow");
        await WaitAsync(host, static s => s.TurnSetupDriving, 600, "crossbow driving");
        CombatUiSnapshot activated = await WaitAsync(host,
            static s => s.ReusedTurns > 0 || s.IsDeploying, 2400, "crossbow activated");
        check("one_setup_search", activated.Searches == searches,
            $"{searches}->{activated.Searches}");
        check("reused", activated.ReusedTurns > 0, activated.ReusedTurns.ToString());
        check("page_closed", !activated.TurnSetupPageOpen);
        check("no_blocker_after", !PlayerTurnSetupRuntime.TestBlockerActive);
        check("no_error", activated.ErrorCode == null, activated.ErrorCode);
    }

    private static async Task StartEndTurnAsync(NGame host)
    {
        if (!CombatManager.Instance.IsInProgress)
            throw new InvalidDataException("结束回合时战斗已结束。");
        CombatState state = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidDataException("结束回合找不到战斗。");
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidDataException("结束回合找不到玩家。");
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidDataException("结束回合没有玩家回合。");
        TaskCompletionSource<GameAction> captured = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBeforeActionExecuted(GameAction action)
        {
            if (action is EndPlayerTurnAction endTurn && endTurn.OwnerId == player.NetId)
                captured.TrySetResult(action);
        }
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        executor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new EndPlayerTurnAction(player, turn));
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
    }

    private static async Task WaitNativeSideAsync(NGame host, CombatSide side)
    {
        for (int frame = 0; frame < 1800; frame++)
        {
            CombatState? live = CombatManager.Instance.DebugOnlyGetState();
            if (live != null && live.CurrentSide == side)
                return;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException($"等待原生 {side} 回合超时。");
    }

    /// <summary>
    /// Drops the EndTurn plan's choices once. The native selection must fall back without faulting
    /// the game's async setup task, the controller must stop with choice_mismatch, and the native
    /// turn must still reach the next Play phase.
    /// </summary>
    private static async Task DeploymentChoiceMismatchAsync(NGame host, Check check, Action action)
    {
        CombatControllerTestHooks.ForceChoicePlanUnderflowOnce = true;
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "choice_mismatch",
            2400, "choice mismatch pause");
        check("pause_code", paused.PauseCode == "choice_mismatch", paused.PauseCode);
        check("deployment_failure", paused.DeploymentFailures >= 1,
            paused.DeploymentFailures.ToString());
        check("deviations_zero", paused.Deviations == 0, paused.Deviations.ToString());
        check("full_auto_off", !paused.CombatRunActive, paused.CombatRunActive.ToString());
        check("paused_this_combat", paused.CombatRunPaused);
        CombatState? live = CombatManager.Instance.DebugOnlyGetState();
        check("native_in_progress", live != null && CombatManager.Instance.IsInProgress,
            $"in_progress={CombatManager.Instance.IsInProgress}");
        Player? player = live == null ? null : LocalContext.GetMe(live);
        check("native_play", player?.PlayerCombatState is { Phase: PlayerTurnPhase.Play },
            player?.PlayerCombatState?.Phase.ToString() ?? "-");
        check("native_turn_advanced", (player?.PlayerCombatState?.TurnNumber ?? 0) >= 2,
            (player?.PlayerCombatState?.TurnNumber ?? 0).ToString());
    }

    private static async Task DeviationActionFullAutoAsync(NGame host, Check check, Action action)
    {
        int forcedAt = 0;
        CombatControllerTestHooks.ForceDeploymentFailure = (deployed, turn) =>
            turn == 1 && ++forcedAt == 2 ? "action_deviation" : null;
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "action_deviation",
            2400, "action deviation pause");
        check("forced_second_action", forcedAt >= 2, forcedAt.ToString());
        check("pause_code", paused.PauseCode == "action_deviation", paused.PauseCode);
        check("deviations", paused.Deviations == 1, paused.Deviations.ToString());
        check("full_auto_off", !paused.CombatRunActive);
        check("mode_full_auto", (paused.CombatRunActive || paused.CombatRunPaused), ModeText(paused));
        check("paused_this_combat", paused.CombatRunPaused);
        check("route_dropped", paused.Result == null && !paused.CurrentTurnRouteReady);
        int searches = paused.Searches;
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("left_text", SearchAvailable(panel), FooterText(panel));
        check("left_enabled", SearchAvailable(panel), FooterText(panel));
        check("right_hidden", ExecuteUnavailable(panel), FooterText(panel));
        await WaitStatusAsync(host, SolverText.Get("已暂停执行"));
        check("summary", Summary().Contains("动作结果与预测不同，已停止", StringComparison.Ordinal),
            Summary());
        check("summary_reason", Summary().Contains(
            SolverPanelText.ErrorReason(paused.PauseDetailReason), StringComparison.Ordinal),
            Summary());
        await Frames(host, 60);
        check("no_research", Snapshot().Searches == searches, $"{searches}->{Snapshot().Searches}");
        CombatControllerTestHooks.ForceDeploymentFailure = null;
        // The paused route was dropped, so the execute keys are greyed: the player searches again.
        await PressPanelButtonAsync(host, PanelButton.Search, "search_after_pause");
        action("press_search_after_pause");
        CombatUiSnapshot resumed = await WaitAsync(host,
            s => s.Searches > searches || s.IsSearching, 240, "resume search after deviation");
        check("resume_clears_pause", resumed.PauseCode == null, resumed.PauseCode ?? "-");
        check("resume_not_paused", !resumed.CombatRunPaused);
        check("resume_search", resumed.Searches > searches || resumed.IsSearching,
            $"{searches}->{resumed.Searches}");
    }

    private static async Task DeviationActionAutoSearchAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching && !s.IsDeploying,
            1800, "route ready");
        int searches = Snapshot().Searches;
        CombatControllerTestHooks.ForceDeploymentFailure = (deployed, _) =>
            deployed.Action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.PlayCard
                ? "action_deviation"
                : null;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        action("press_execute");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "action_deviation",
            1800, "auto search deviation pause");
        check("pause_code", paused.PauseCode == "action_deviation", paused.PauseCode);
        check("mode_auto_search", (paused.AutoSearchEnabled && !paused.CombatRunActive), ModeText(paused));
        check("not_full_auto_paused", !paused.CombatRunPaused);
        check("route_dropped", paused.Result == null && !paused.CurrentTurnRouteReady);
        check("searches_unchanged", paused.Searches == searches,
            $"{searches}->{paused.Searches}");
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("left_text", SearchAvailable(panel), FooterText(panel));
        check("left_enabled", SearchAvailable(panel), FooterText(panel));
        check("right_hidden", ExecuteUnavailable(panel), FooterText(panel));
        await Frames(host, 60);
        check("no_research", Snapshot().Searches == searches, $"{searches}->{Snapshot().Searches}");
    }

    private static async Task DeviationDeploymentDriftAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching && !s.IsDeploying,
            1800, "route ready");
        int searches = Snapshot().Searches;
        CombatControllerTestHooks.ForceDeploymentFailure = (deployed, _) =>
            deployed.Action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.PlayCard
                ? "hand_drift"
                : null;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        action("press_execute");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused
                && s.PauseCode == "deployment_deviation", 1800, "deployment drift pause");
        check("pause_code", paused.PauseCode == "deployment_deviation", paused.PauseCode);
        check("detail", paused.PauseReason?.Contains("hand_drift", StringComparison.Ordinal) == true,
            paused.PauseReason ?? "-");
        check("reason_kind", paused.PauseDetailReason?.Kind == CombatReasonKind.Deviation,
            paused.PauseDetailReason?.Kind ?? "-");
        check("reason_text",
            SolverPanelText.ErrorReason(paused.PauseDetailReason)
                == SolverText.Get("手牌和路线对不上"),
            SolverPanelText.ErrorReason(paused.PauseDetailReason));
        await WaitStatusAsync(host, SolverText.Get("已暂停执行"));
        check("summary_reason",
            Summary().Contains(SolverText.Get("手牌和路线对不上"), StringComparison.Ordinal),
            Summary());
        if (SolverText.IsEnglish)
        {
            check("english_reason",
                Summary().Contains("The hand does not match the route.",
                    StringComparison.Ordinal), Summary());
        }
        check("searches_unchanged", paused.Searches == searches,
            $"{searches}->{paused.Searches}");
        check("route_dropped", paused.Result == null && !paused.CurrentTurnRouteReady);
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("left_enabled", SearchAvailable(panel), FooterText(panel));
        check("right_hidden", ExecuteUnavailable(panel), FooterText(panel));
        await Frames(host, 60);
        check("no_research", Snapshot().Searches == searches, $"{searches}->{Snapshot().Searches}");
    }

    private static async Task DeviationBoundaryAsync(NGame host, Check check, Action action)
    {
        int searchesAtTurnTwo = -1;
        bool damaged = false;
        CombatControllerTestHooks.BeforeAutoSearch = async (state, turn, token) =>
        {
            if (damaged || turn < 2)
                return;
            damaged = true;
            searchesAtTurnTwo = CombatSessionController.SearchCountForTest;
            Player player = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("回合边界伤害注入找不到玩家。");
            await DamagePlayerAsync(state, player, 1);
            action($"inject_boundary_damage:turn={turn}");
            token.ThrowIfCancellationRequested();
        };
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused
                && s.PauseCode == "boundary_deviation", 3600, "boundary deviation pause");
        check("damaged", damaged, "turn two damage injected");
        check("at_turn_two", paused.Turn >= 2, paused.Turn.ToString());
        check("searches_unchanged", searchesAtTurnTwo >= 0
            && paused.Searches == searchesAtTurnTwo,
            $"{searchesAtTurnTwo}->{paused.Searches}");
        check("full_auto_off", !paused.CombatRunActive);
        check("mode_full_auto", (paused.CombatRunActive || paused.CombatRunPaused), ModeText(paused));
        check("paused_this_combat", paused.CombatRunPaused);
        check("route_dropped", paused.Result == null && !paused.CurrentTurnRouteReady);
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("left_text", SearchAvailable(panel), FooterText(panel));
        check("left_enabled", SearchAvailable(panel), FooterText(panel));
        check("right_hidden", ExecuteUnavailable(panel), FooterText(panel));
        await WaitStatusAsync(host, SolverText.Get("已暂停执行"));
        check("summary",
            Summary().Contains("回合开始时实际状态与预测不同，已停止", StringComparison.Ordinal),
            Summary());
        check("summary_reason",
            Summary().Contains("和路线对不上", StringComparison.Ordinal), Summary());
        CombatControllerTestHooks.BeforeAutoSearch = null;
        int searches = paused.Searches;
        // The paused route was dropped, so the execute keys are greyed: the player searches again.
        await PressPanelButtonAsync(host, PanelButton.Search, "search_after_pause");
        action("press_search_after_pause");
        CombatUiSnapshot resumed = await WaitAsync(host,
            s => s.Searches > searches || s.IsSearching, 240, "resume after boundary deviation");
        check("resume_clears_pause", resumed.PauseCode == null, resumed.PauseCode ?? "-");
        check("resume_not_paused", !resumed.CombatRunPaused);
        check("resume_search", resumed.Searches > searches || resumed.IsSearching,
            $"{searches}->{resumed.Searches}");
    }

    private static async Task DeviationSetupStampAsync(NGame host, Check check, Action action)
    {
        check("mode_full_auto", (Snapshot().CombatRunActive || Snapshot().CombatRunPaused),
            ModeText(Snapshot()));
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused
                && s.PauseCode == "boundary_deviation", 2400, "setup stamp deviation pause");
        check("damaged", CombatControllerTestHooks.SetupStampDamageInjected,
            "setup Play stamp damage injected");
        check("pause_code", paused.PauseCode == "boundary_deviation", paused.PauseCode);
        check("full_auto_off", !paused.CombatRunActive);
        check("mode_full_auto", (paused.CombatRunActive || paused.CombatRunPaused), ModeText(paused));
        check("paused_this_combat", paused.CombatRunPaused);
        check("route_dropped", paused.Result == null && !paused.CurrentTurnRouteReady);
        check("no_blocker", !PlayerTurnSetupRuntime.TestBlockerActive);
        int searches = paused.Searches;
        await Frames(host, 60);
        check("no_research", Snapshot().Searches == searches,
            $"{searches}->{Snapshot().Searches}");
        await WaitStatusAsync(host, SolverText.Get("已暂停执行"));
        check("summary",
            Summary().Contains("回合开始时实际状态与预测不同，已停止", StringComparison.Ordinal),
            Summary());
        SolverOverlay? panel = SolverOverlay.Instance;
        check("left_text", SearchAvailable(panel), FooterText(panel));
        check("left_enabled", SearchAvailable(panel), FooterText(panel));
        check("right_hidden", ExecuteUnavailable(panel), FooterText(panel));
    }

    private static async Task PlanExhaustedAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching && !s.IsDeploying,
            1800, "route ready");
        int searches = Snapshot().Searches;
        CombatControllerTestHooks.ForcePlanExhaustedOnce = true;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        action("press_execute");
        CombatUiSnapshot searching = await WaitAsync(host,
            static s => s.IsSearching && s.Search?.Reason == "plan_exhausted",
            1200, "plan exhausted re-search");
        check("reason", searching.Search?.Reason == "plan_exhausted", searching.Search?.Reason);
        check("deploy_when_ready", searching.DeployWhenReady);
        check("not_paused", searching.Phase != CombatUiPhase.Paused, searching.Phase.ToString());
        await WaitIdleAsync(host, "plan exhausted deployment done");
        check("searches_increased", Snapshot().Searches > searches,
            $"{searches}->{Snapshot().Searches}");
    }

    private static async Task PauseDeathTurnDeploymentAsync(NGame host, Check check, Action action)
    {
        CombatControllerTestHooks.ForceProjectedDeathTurn = true;
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "death_turn",
            2400, "death turn deployment pause");
        check("pause_code", paused.PauseCode == "death_turn", paused.PauseCode);
        check("full_auto_off", !paused.CombatRunActive);
        check("paused_this_combat", paused.CombatRunPaused);
        check("result_retained", paused.Result != null);
        check("route_ready", paused.CurrentTurnRouteReady);
        await WaitPanelAppliedAsync(host);
        SolverOverlay? panel = SolverOverlay.Instance;
        // After the run's own pause both execute keys are back; 执行本场战斗 confirms this turn's
        // route and runs again.
        check("right_visible", Usable(panel?.ExecuteCombatButtonForTest)
            && Usable(panel?.ExecuteTurnButtonForTest), FooterText(panel));
        check("right_text", panel?.ExecuteCombatButtonForTest?.Text == SolverText.Get("执行本场战斗"),
            FooterText(panel));
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute_after_death_pause");
        action("press_execute_after_death_pause");
        CombatUiSnapshot resumed = await WaitAsync(host, static s => s.IsDeploying, 600,
            "deployment after death pause");
        check("resume_deploying", resumed.IsDeploying, resumed.Phase.ToString());
        check("resume_full_auto_on", resumed.CombatRunActive);
        check("resume_not_paused", !resumed.CombatRunPaused);
        check("resume_pause_cleared", resumed.PauseCode == null, resumed.PauseCode ?? "-");
        CombatControllerTestHooks.ForceProjectedDeathTurn = false;
        await WaitIdleAsync(host, "deployment after death pause done");
    }

    /// <summary>
    /// A route that dies two turns later pauses full auto once (<c>no_survival</c>) with the route
    /// kept; resuming deploys it and the same far-death outcome does not pause again this combat.
    /// </summary>
    private static async Task PauseNoSurvivalDeploymentAsync(NGame host, Check check, Action action)
    {
        CombatControllerTestHooks.ForceProjectedLaterDeathTurn = true;
        CombatControllerTestHooks.EnableNoSurvivalPause = true;
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "no_survival",
            2400, "no survival pause");
        check("pause_code", paused.PauseCode == "no_survival", paused.PauseCode);
        check("full_auto_off", !paused.CombatRunActive);
        check("paused_this_combat", paused.CombatRunPaused);
        check("result_retained", paused.Result != null);
        check("route_ready", paused.CurrentTurnRouteReady);
        check("not_deploying", !paused.IsDeploying);
        await PressPanelButtonAsync(host, PanelButton.ExecuteCombat, "resume_full_auto");
        action("press_resume_full_auto");
        CombatUiSnapshot resumed = await WaitAsync(host,
            static s => s.IsDeploying || (s.Phase == CombatUiPhase.Paused && s.PauseCode == "no_survival"),
            600, "resumed deployment");
        check("resumed_deploying", resumed.IsDeploying, resumed.Phase.ToString());
        check("no_second_pause", resumed.PauseCode != "no_survival", resumed.PauseCode ?? "-");
        await WaitIdleAsync(host, "resumed deployment done");
        check("not_paused_again", Snapshot().PauseCode != "no_survival", Snapshot().PauseCode ?? "-");
        CombatControllerTestHooks.ForceProjectedLaterDeathTurn = false;
        CombatControllerTestHooks.EnableNoSurvivalPause = false;
    }

    private static async Task CombatEndAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        long deadline = Environment.TickCount64 + 480_000;
        while (CombatManager.Instance.IsInProgress && Environment.TickCount64 < deadline)
        {
            if (CombatSessionController.IsPaused)
                throw new InvalidDataException($"combat paused: {Snapshot().PauseCode}");
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        if (CombatManager.Instance.IsInProgress)
            throw new TimeoutException("combat did not finish");
        await Frames(host, 5);
        CombatUiSnapshot ended = Snapshot();
        check("idle", ended.Phase == CombatUiPhase.Idle, ended.Phase.ToString());
        check("result_cleared", ended.Result == null);
        check("not_busy", !ended.IsSearching && !ended.IsDeploying);
        CombatPilotResult summary = CombatSessionController.LastCompletedResultForTesting
            ?? throw new InvalidDataException("controller summary missing");
        check("terminal", summary.Status is "victory" or "defeat", summary.Status);
    }

    private static string Status() => SolverOverlay.Instance?.StatusTextForTest ?? "";

    private static async Task WaitStatusAsync(NGame host, params string[] statuses)
    {
        for (int frame = 0; frame < 240; frame++)
        {
            SolverOverlay? panel = SolverOverlay.Instance;
            // Status text alone can still be the previous combat's last message when a request
            // starts after another one in the same process. Only accept it after the panel has
            // applied the current snapshot, so the following Summary() assertions read the new
            // combat's text too.
            bool applied = panel != null
                && panel.ProbeForTest().AppliedSequence == CombatUiStore.Snapshot.Sequence;
            if (applied && statuses.Contains(Status(), StringComparer.Ordinal))
                return;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
    }
    /// <summary>Waits until the panel has applied the current snapshot (it renders one frame later).</summary>
    private static async Task WaitPanelAppliedAsync(NGame host)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            SolverOverlay? panel = SolverOverlay.Instance;
            if (panel != null && panel.ProbeForTest().AppliedSequence == CombatUiStore.Snapshot.Sequence)
                return;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException("panel did not apply the current snapshot");
    }

    private static string Context() => SolverOverlay.Instance?.ContextTextForTest ?? "";
    private static string Summary() => SolverOverlay.Instance?.SummaryTextForTest ?? "";
    private static string Heading() => SolverOverlay.Instance?.RouteHeadingForTest ?? "";

    private static async Task PanelTextsAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        await WaitIdleAsync(host, "manual idle");
        await WaitStatusAsync(host, SolverText.Get("等待手动搜索"));
        check("manual_status", Status() == SolverText.Get("等待手动搜索"), Status());
        check("manual_summary", Summary().Contains("自动搜索已关闭", StringComparison.Ordinal)
            && Summary().Contains("开始搜索", StringComparison.Ordinal), Summary());
        await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
        await WaitAsync(host, static s => s.IsSearching, 240, "search started");
        await WaitStatusAsync(host, SolverText.Get("后台搜索中"));
        check("searching_status", Status() == SolverText.Get("后台搜索中"), Status());
        check("searching_heading", Heading() == SolverText.Get("求解器当前考虑（尚未验证）"), Heading());
        check("searching_summary", Summary().Contains("正在搜索当前回合", StringComparison.Ordinal)
            || Summary().Contains("已查阅", StringComparison.Ordinal), Summary());
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching, 900, "result ready");
        await WaitStatusAsync(host, SolverText.Get("方案就绪"), SolverText.Get("方案已复用"));
        check("ready_status", Status() is "方案就绪" or "方案已复用", Status());
        check("ready_heading", Heading() == SolverText.Get("推荐路线"), Heading());
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        await WaitAsync(host, static s => s.IsDeploying, 240, "deploying");
        await WaitStatusAsync(host, SolverText.Get("正在执行"));
        check("deploying_status", Status() == SolverText.Get("正在执行"), Status());
        check("deploying_heading", Heading() == SolverText.Get("正在执行的路线"), Heading());
        check("deploying_summary", Summary().Contains("按推荐顺序执行", StringComparison.Ordinal)
            || Summary().Contains("正在执行", StringComparison.Ordinal), Summary());
        await WaitAsync(host, static s => !s.IsDeploying, 1800, "deployment done");
        await WaitStatusAsync(host,
            SolverText.Get("执行完成"), SolverText.Get("等待下一回合方案"));
        check("complete_status", Status() == SolverText.Get("执行完成")
            || Status() == SolverText.Get("等待下一回合方案"), Status());
        await WaitStatusAsync(host, SolverText.Get("等待下一回合方案"));
        check("waiting_status", Status() == SolverText.Get("等待下一回合方案"), Status());
        check("waiting_context", Context().Contains("回合已执行", StringComparison.Ordinal), Context());
        check("waiting_summary", Summary().Contains("等待下一回合方案就绪", StringComparison.Ordinal), Summary());

        await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
        await WaitAsync(host, static s => s.IsSearching, 240, "search for stop");
        await PressPanelButtonAsync(host, PanelButton.Search, "stop");
        await WaitStatusAsync(host, SolverText.Get("搜索已停止"));
        check("stop_status", Status() == SolverText.Get("搜索已停止"), Status());
        check("stop_summary_no_candidate",
            Summary().Contains("本回合搜索已停止", StringComparison.Ordinal), Summary());

        bool candidateStopped;
        CombatControllerTestHooks.AutoStopOnCandidate = true;
        try
        {
            await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
            CombatUiSnapshot stoppedCandidate = await WaitAsync(host,
                static s => s.AutomaticSearchPaused && s.Result != null
                    && s.CurrentTurnRouteReady, 600, "candidate stopped");
            candidateStopped = stoppedCandidate.Result != null
                && stoppedCandidate.CurrentTurnRouteReady;
        }
        finally
        {
            CombatControllerTestHooks.AutoStopOnCandidate = false;
        }
        check("stop_candidate_stopped", candidateStopped,
            Snapshot().CurrentTurnRouteReady.ToString());
        await WaitStatusAsync(host, SolverText.Get("搜索已停止"));
        check("stop_candidate_summary",
            Summary().Contains("下方保留停止时已经得到的当前候选路线", StringComparison.Ordinal), Summary());
    }

    /// <summary>
    /// Forces a search failure through the real completion path and checks the main panel. A retry
    /// clears the state; a turn change also clears a later forced-potion failure.
    /// </summary>
    private static async Task SearchFailuresAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int turn = Snapshot().Turn;

        CombatControllerTestHooks.ForceSearchFailure = static _ => "search_failed";
        await PressPanelButtonAsync(host, PanelButton.Search, "search_failed");
        action("press_search_failed");
        CombatUiSnapshot failed = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Failed && !s.IsSearching, 240,
            "search_failed phase");
        check("search_failed_phase", failed.Phase == CombatUiPhase.Failed, failed.Phase.ToString());
        check("search_failed_code", failed.ErrorCode == "search_failed", failed.ErrorCode);
        check("search_failed_turn", failed.Turn == turn, $"{turn}->{failed.Turn}");
        check("search_failed_can_retry", failed.CanStartSearch);
        check("search_failed_no_route", failed.Result == null && !failed.CurrentTurnRouteReady);
        string? terminalReason = CombatSessionController.NonProgressingReasonForTest;
        check("search_failed_terminal_reason",
            terminalReason?.StartsWith("controller_failed code=search_failed",
                StringComparison.Ordinal) == true, terminalReason ?? "-");
        await WaitStatusAsync(host, SolverText.Get("求解器消息"));
        check("search_failed_status", Status() == SolverText.Get("求解器消息"), Status());
        check("search_failed_reason_kind",
            failed.ErrorReason?.Kind == CombatReasonKind.SearchFailed,
            failed.ErrorReason?.Kind ?? "-");
        check("search_failed_summary",
            Summary() == SolverPanelText.Error("search_failed") + "\n"
                + SolverPanelText.ErrorReason(failed.ErrorReason), Summary());
        if (SolverText.IsEnglish)
        {
            check("search_failed_english_reason",
                Summary().Contains("The search failed before returning an executable route.",
                    StringComparison.Ordinal), Summary());
        }
        SolverOverlay? panel = SolverOverlay.Instance;
        check("search_failed_button", SearchAvailable(panel), FooterText(panel));
        check("search_failed_no_execute", ExecuteUnavailable(panel), FooterText(panel));

        CombatControllerTestHooks.ForceSearchFailure = null;
        await PressPanelButtonAsync(host, PanelButton.Search, "retry");
        action("press_retry");
        CombatUiSnapshot retried = await WaitAsync(host,
            s => s.ErrorCode == null && s.Phase != CombatUiPhase.Failed && !s.IsSearching,
            900, "search failure cleared by retry");
        check("retry_phase", retried.Phase == CombatUiPhase.WaitingTurn, retried.Phase.ToString());
        check("retry_error_cleared", retried.ErrorCode == null, retried.ErrorCode);
        check("retry_terminal_reason_cleared",
            CombatSessionController.NonProgressingReasonForTest == null,
            CombatSessionController.NonProgressingReasonForTest ?? "-");
        await WaitIdleAsync(host, "retry search done");

        CombatControllerTestHooks.ForceSearchFailure = static _ => "forced_potion_unusable";
        int failedTurn = Snapshot().Turn;
        await PressPanelButtonAsync(host, PanelButton.Search, "forced_potion_unusable");
        action("press_forced_potion_unusable");
        CombatUiSnapshot potion = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Failed && !s.IsSearching, 240,
            "forced_potion_unusable phase");
        check("potion_failed_phase", potion.Phase == CombatUiPhase.Failed, potion.Phase.ToString());
        check("potion_failed_code", potion.ErrorCode == "forced_potion_unusable",
            potion.ErrorCode);
        check("potion_failed_can_retry", potion.CanStartSearch);
        await WaitStatusAsync(host, SolverText.Get("求解器消息"));
        check("potion_failed_status", Status() == SolverText.Get("求解器消息"), Status());
        check("potion_failed_reason_kind",
            potion.ErrorReason?.Kind == CombatReasonKind.ForcedPotion,
            potion.ErrorReason?.Kind ?? "-");
        check("potion_failed_summary",
            Summary() == SolverPanelText.Error("forced_potion_unusable") + "\n"
                + SolverPanelText.ErrorReason(potion.ErrorReason), Summary());
        if (SolverText.IsEnglish)
        {
            check("potion_failed_english_reason",
                Summary().Contains("No route used every potion set to Always use.",
                    StringComparison.Ordinal), Summary());
        }
        check("potion_failed_button", SearchAvailable(panel), FooterText(panel));

        CombatControllerTestHooks.ForceSearchFailure = null;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.Turn > failedTurn && s.ErrorCode == null && s.Phase != CombatUiPhase.Failed,
            900, "failure cleared on next turn");
        check("next_turn_phase", next.Phase == CombatUiPhase.WaitingTurn, next.Phase.ToString());
        check("next_turn_error_cleared", next.ErrorCode == null, next.ErrorCode);
    }

    private static async Task ImportCapacityAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int turn = Snapshot().Turn;

        await PressPanelButtonAsync(host, PanelButton.Search, "import_capacity");
        action("press_search_import_capacity");
        CombatUiSnapshot failed = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Failed && !s.IsSearching, 1800,
            "import capacity failure");
        check("phase", failed.Phase == CombatUiPhase.Failed, failed.Phase.ToString());
        check("code", failed.ErrorCode == "import_capacity", failed.ErrorCode ?? "-");
        check("turn", failed.Turn == turn, $"{turn}->{failed.Turn}");
        check("reason_kind", failed.ErrorReason?.Kind == CombatReasonKind.ImportCapacity,
            failed.ErrorReason?.Kind ?? "-");
        int limit = Sts2Solver.Simulation.Combat.Limits.Cards;
        check("reason_capacity", failed.ErrorReason?.Capacity == limit,
            failed.ErrorReason?.Capacity?.ToString() ?? "-");
        check("reason_actual", failed.ErrorReason?.Actual == limit + 1,
            failed.ErrorReason?.Actual?.ToString() ?? "-");
        string expected = SolverText.Format($"牌组共 {limit + 1} 张，超过求解器上限 {limit} 张");
        check("reason_text", SolverPanelText.ErrorReason(failed.ErrorReason) == expected,
            SolverPanelText.ErrorReason(failed.ErrorReason));
        check("no_route", failed.Result == null && !failed.CurrentTurnRouteReady);
        check("can_retry", failed.CanStartSearch);
        string? terminal = CombatSessionController.NonProgressingReasonForTest;
        check("terminal_reason",
            terminal?.StartsWith("controller_failed code=import_capacity",
                StringComparison.Ordinal) == true, terminal ?? "-");
        await WaitStatusAsync(host, SolverText.Get("求解器消息"));
        check("status", Status() == SolverText.Get("求解器消息"), Status());
        check("title", Summary().Contains(SolverText.Get("搜索初始化失败"), StringComparison.Ordinal),
            Summary());
        check("summary_reason", Summary().Contains(expected, StringComparison.Ordinal), Summary());
        SolverOverlay? panel = SolverOverlay.Instance;
        check("button", SearchAvailable(panel), FooterText(panel));
        check("no_execute", ExecuteUnavailable(panel), FooterText(panel));
        // Mode switches clear the visible failure state. The whole-combat test must still see the
        // unrecovered import failure afterwards.
        check("mode_clear", CombatSessionController.LeaveRunForTest(autoSearch: false));
        action("set_mode_manual_after_failure");
        await Frames(host, 4);
        string? terminalAfterMode = CombatSessionController.NonProgressingReasonForTest;
        check("terminal_reason_after_mode_clear",
            terminalAfterMode?.StartsWith("controller_failed code=import_capacity",
                StringComparison.Ordinal) == true, terminalAfterMode ?? "-");
    }

    private static async Task ImportFailureAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        await PressPanelButtonAsync(host, PanelButton.Search, "import_failure");
        action("press_search_import_failure");
        CombatUiSnapshot failed = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Failed && !s.IsSearching, 1800,
            "import failure");
        check("phase", failed.Phase == CombatUiPhase.Failed, failed.Phase.ToString());
        check("import_code",
            failed.ErrorCode is "import_failed" or "import_unsupported" or "import_capacity",
            failed.ErrorCode ?? "-");
        check("reason_kind", failed.ErrorReason?.Kind is CombatReasonKind.Import
                or CombatReasonKind.ImportUnsupported or CombatReasonKind.ImportCapacity,
            failed.ErrorReason?.Kind ?? "-");
        check("no_route", failed.Result == null && !failed.CurrentTurnRouteReady);
        string? terminal = CombatSessionController.NonProgressingReasonForTest;
        check("terminal_reason",
            terminal?.StartsWith("controller_failed code=import_", StringComparison.Ordinal) == true,
            terminal ?? "-");
        await WaitStatusAsync(host, SolverText.Get("求解器消息"));
        string title = SolverPanelText.Error(failed.ErrorCode ?? "search_failed");
        string reason = SolverPanelText.ErrorReason(failed.ErrorReason);
        check("summary_title", Summary().Contains(title, StringComparison.Ordinal), Summary());
        check("summary_reason", Summary().Contains(reason, StringComparison.Ordinal), Summary());
        check("summary_two_lines", Summary().Contains('\n'), Summary());
        SolverOverlay? panel = SolverOverlay.Instance;
        check("button", SearchAvailable(panel), FooterText(panel));
        check("no_execute", ExecuteUnavailable(panel), FooterText(panel));
    }

    private static async Task SearchStatusFailuresAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        (string Name, CombatSearchStatus Status, bool Memory, string Kind,
            string Reason)[] cases =
        [
            ("capacity_bounded", CombatSearchStatus.CapacityBounded, false,
                CombatReasonKind.SearchCapacityBounded,
                "生成的牌太多，超出求解器容量，没有找到能赢的路线"),
            ("memory_limit", CombatSearchStatus.Incomplete, true,
                CombatReasonKind.SearchMemoryLimit,
                "达到内存上限，没有找到可执行路线"),
            ("no_route", CombatSearchStatus.Incomplete, false,
                CombatReasonKind.SearchNoRoute,
                "没有找到任何可执行路线"),
        ];
        try
        {
            foreach ((string name, CombatSearchStatus status, bool memory,
                         string kind, string reasonSource) in cases)
            {
                CombatControllerTestHooks.ForceSearchFailure = static _ => "search_failed";
                CombatControllerTestHooks.ForceSearchFailureStatus = status;
                CombatControllerTestHooks.ForceSearchFailureMemoryExceeded = memory;
                await PressPanelButtonAsync(host, PanelButton.Search, name);
                action($"press_search:{name}");
                CombatUiSnapshot failed = await WaitAsync(host,
                    static s => s.Phase == CombatUiPhase.Failed && !s.IsSearching, 600,
                    $"{name} failure");
                check($"{name}_code", failed.ErrorCode == "search_failed",
                    failed.ErrorCode ?? "-");
                check($"{name}_kind", failed.ErrorReason?.Kind == kind,
                    failed.ErrorReason?.Kind ?? "-");
                string title = SolverPanelText.Error("search_failed");
                string reason = SolverPanelText.ErrorReason(failed.ErrorReason);
                check($"{name}_reason", reason == SolverText.Get(reasonSource), reason);
                await WaitStatusAsync(host, SolverText.Get("求解器消息"));
                await Frames(host, 4);
                check($"{name}_summary", Summary() == title + "\n" + reason, Summary());
                if (SolverText.IsEnglish)
                {
                    check($"{name}_english_line",
                        Summary().Contains("\n", StringComparison.Ordinal)
                        && !Summary().EndsWith('\n'), Summary());
                }
            }
        }
        finally
        {
            CombatControllerTestHooks.ForceSearchFailure = null;
            CombatControllerTestHooks.ForceSearchFailureStatus = null;
            CombatControllerTestHooks.ForceSearchFailureMemoryExceeded = false;
        }
    }

    private static async Task CapacityDroppedRouteAsync(NGame host, Check check, Action action)
    {
        check("mode_auto_search",
            CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        CombatUiSnapshot ready = await WaitAsync(host,
            static s => s.Result != null && !s.IsSearching && !s.IsDeploying
                && s.Result.CapacityDroppedRegions is { Count: > 0 },
            1800, "capacity-dropped route");
        check("phase", ready.Phase == CombatUiPhase.WaitingTurn, ready.Phase.ToString());
        check("no_error", ready.ErrorCode == null, ready.ErrorCode ?? "-");
        check("has_route", ready.Result != null && ready.CurrentTurnRouteReady);
        check("drop_region", ready.Result?.CapacityDroppedRegions is { } regions
            && regions.TryGetValue("Cards", out int dropped) && dropped == 1,
            ready.Result?.CapacityDroppedRegions is { } r
                ? string.Join(",", r.Select(static pair => $"{pair.Key}={pair.Value}"))
                : "-");
        await WaitStatusAsync(host, SolverText.Get("方案就绪"), SolverText.Get("方案已复用"));
        await Frames(host, 4);
        string hint = SolverPanelText.CapacityDropHint(
            new SortedDictionary<string, int>(StringComparer.Ordinal) { ["Cards"] = 1 });
        check("summary_hint", Summary().Contains(hint, StringComparison.Ordinal), Summary());
        SolverOverlay? panel = SolverOverlay.Instance;
        check("left_text", SearchAvailable(panel), FooterText(panel));
        check("left_enabled", SearchAvailable(panel), FooterText(panel));
        check("right_visible", panel?.ExecuteTurnButtonForTest?.Visible == true, FooterText(panel));
        check("right_text", panel?.ExecuteTurnButtonForTest?.Text == SolverText.Get("执行本回合"),
            FooterText(panel));
        check("right_enabled", panel?.ExecuteTurnButtonForTest?.Disabled == false, FooterText(panel));
    }

    private static async Task ThreadSettingAsync(NGame host, Check check, Action action)
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
            await WaitIdleAsync(host, "manual idle");
            foreach (int threads in new[] { 2, 3 })
            {
                check("settings_saved",
                    SolverSettings.Update(current => current with
                    {
                        Search = current.Search with { Threads = threads },
                    }), "thread setting saved");
                await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
                CombatUiSnapshot searching = await WaitAsync(host, static s => s.IsSearching, 240,
                    "search started");
                check($"search_threads_{threads}", searching.Search?.SearchThreads == threads,
                    searching.Search?.SearchThreads.ToString() ?? "-");
                await WaitIdleAsync(host, "search done");
                action($"search_threads:{threads}");
            }
        }
        finally
        {
            SolverSettings.Update(_ => original);
        }
    }

    private static async Task DeploymentSpeedAsync(NGame host, Check check, Action action,
        SolverDeploymentSpeed speed, double delaySeconds)
    {
        SolverSettingsData original = SolverSettings.Current;
        FastModeType originalFastMode = SaveManager.Instance.PrefsSave.FastMode;
        try
        {
            check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
            action("set_mode:auto_search");
            await WaitAsync(host, static s => s.Result != null && !s.IsSearching
                && !s.IsDeploying, 900, "result ready");
            bool saved = SolverSettings.Update(current => current with
            {
                Automation = current.Automation with
                {
                    DeploymentSpeed = speed,
                    InterActionDelaySeconds = delaySeconds,
                },
            });
            check("settings_saved", saved, "deployment settings saved");
            CombatControllerTestHooks.DeploymentCompletionTimes.Clear();
            CombatControllerTestHooks.DeploymentWaits.Clear();
            CombatControllerTestHooks.DeploymentPlayableActionCount = 0;
            FastModeType preset = speed == SolverDeploymentSpeed.Fast
                ? FastModeType.Normal
                : FastModeType.Fast;
            FastModeType expected = speed == SolverDeploymentSpeed.FollowGame
                ? preset
                : speed == SolverDeploymentSpeed.Normal ? FastModeType.Normal : FastModeType.Fast;
            SaveManager.Instance.PrefsSave.FastMode = preset;
            await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
            action($"execute:speed={speed}:delay={delaySeconds}");
            await WaitAsync(host, static s => s.IsDeploying, 240, "deploying");
            List<FastModeType> seen = [];
            for (int frame = 0; frame < 600 && Snapshot().IsDeploying; frame++)
            {
                FastModeType mode = SaveManager.Instance.PrefsSave.FastMode;
                if (seen.Count == 0 || seen[^1] != mode)
                    seen.Add(mode);
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            await WaitIdleAsync(host, "deployment done");
            check("speed_active", seen.Count > 0 && seen.All(mode => mode == expected),
                $"expected={expected} seen={string.Join(",", seen)}");
            if (delaySeconds <= 0d)
            {
                check("delay_none", CombatControllerTestHooks.DeploymentWaits.Count == 0,
                    CombatControllerTestHooks.DeploymentWaits.Count.ToString());
            }
            check("speed_restored", SaveManager.Instance.PrefsSave.FastMode == preset,
                SaveManager.Instance.PrefsSave.FastMode.ToString());
            if (delaySeconds > 0d)
            {
                int playable = CombatControllerTestHooks.DeploymentPlayableActionCount;
                (long Start, long End)[] waits = [.. CombatControllerTestHooks.DeploymentWaits];
                check("delay_wait_count", waits.Length == Math.Max(0, playable - 1),
                    $"waits={waits.Length} playable={playable}");
                long minimum = (long)(delaySeconds * 1000d) - 34L;
                check("delay_measured", waits.All(wait => wait.End - wait.Start >= minimum),
                    string.Join(",", waits.Select(static wait => wait.End - wait.Start)) + " min=" + minimum);
            }
        }
        finally
        {
            SolverSettings.Update(_ => original);
            SaveManager.Instance.PrefsSave.FastMode = originalFastMode;
        }
    }

    private static async Task MultiplayerGateAsync(NGame host, Check check, Action action)
    {
        CombatSessionController.ForceMultiplayerForTest = true;
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("force_multiplayer");
        await Frames(host, 3);
        check("set_full_auto", !CombatSessionController.ArmRunForTest(),
            "full auto rejected");
        check("execute_combat", !CombatUiCommands.Instance.ExecuteCombat(), "execute combat rejected");
        check("continue", !CombatUiCommands.Instance.ContinueSearch(), "continue rejected");
        check("start", !CombatUiCommands.Instance.StartSearch(), "search rejected");
        check("execute", !CombatUiCommands.Instance.ExecuteCurrentTurn(), "execute rejected");
        check("stop_search", !CombatUiCommands.Instance.StopSearch(), "stop search rejected");
        check("stop_deployment", !CombatUiCommands.Instance.StopExecution(),
            "stop deployment rejected");
        check("not_searching", !Snapshot().IsSearching);
        check("not_deploying", !Snapshot().IsDeploying);
        check("full_auto_off", !Snapshot().CombatRunActive);
        SolverOverlay? panel = SolverOverlay.Instance;
        if (panel != null)
        {
            await Frames(host, 3);
            check("panel_hidden", !panel.ProbeForTest().Visible);
        }
    }

    /// <summary>
    /// The footer and the run as the player drives them (user design 2026-11-07). Before the turn's
    /// first search only 开始搜索 is usable and both execute keys sit greyed; during the search no
    /// execute key shows; after it 继续搜索 + 重新搜索 and both execute keys. Switching auto search with
    /// a current route never searches again. 执行本场战斗 deploys this turn and carries on into the next
    /// turn by itself, showing only 停止执行; pressing it ends the run and hands control back.
    /// </summary>
    private static async Task PanelRunFlowAsync(NGame host, Check check, Action action)
    {
        check("auto_search_off", CombatSessionController.LeaveRunForTest(autoSearch: false), "auto search off");
        action("set_auto_search:off");
        await WaitIdleAsync(host, "idle");
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        CombatUiSnapshot idle = Snapshot();
        check("stage_not_started", idle.SearchStage == TurnSearchStage.NotStarted, idle.SearchStage.ToString());
        check("start_key", panel?.SearchButtonForTest is { Visible: true, Disabled: false } start
            && start.Text == SolverText.Get("开始搜索"), FooterText(panel));
        check("restart_hidden_before_search", panel?.RestartSearchButtonForTest?.Visible == false,
            FooterText(panel));
        check("execute_greyed_before_search",
            panel?.ExecuteTurnButtonForTest is { Visible: true, Disabled: true }
            && panel.ExecuteCombatButtonForTest is { Visible: true, Disabled: true }, FooterText(panel));
        await PressPanelButtonAsync(host, PanelButton.Search, "start_search");
        action("press_start_search");
        await WaitAsync(host, static s => s.IsSearching, 240, "search started");
        await Frames(host, 2);
        check("no_execute_while_searching", ExecuteUnavailable(panel)
            && panel?.ExecuteTurnButtonForTest?.Visible == false
            && panel.ExecuteCombatButtonForTest?.Visible == false, FooterText(panel));
        CombatUiSnapshot ready = await WaitAsync(host,
            static s => !s.IsSearching && s.CurrentTurnRouteReady, 1800, "route ready");
        await Frames(host, 4);
        check("stage_ended", Snapshot().SearchStage == TurnSearchStage.Ended, Snapshot().SearchStage.ToString());
        check("continue_restart_pair", panel?.SearchButtonForTest?.Text == SolverText.Get("继续搜索")
            && panel.RestartSearchButtonForTest is { Visible: true, Disabled: false }, FooterText(panel));
        check("execute_ready", Usable(panel?.ExecuteTurnButtonForTest)
            && Usable(panel?.ExecuteCombatButtonForTest), FooterText(panel));
        int searches = ready.Searches;
        await PressPanelButtonAsync(host, PanelButton.AutoSearchOn, "auto_search_on");
        action("toggle_auto_search_on");
        await Frames(host, 30);
        check("switch_on_no_search", Snapshot().Searches == searches && !Snapshot().IsSearching,
            $"{searches}->{Snapshot().Searches}");
        check("switch_on_saved", SolverSettings.Current.Automation.AutoSearch && Snapshot().AutoSearchEnabled);
        await PressPanelButtonAsync(host, PanelButton.AutoSearchOff, "auto_search_off");
        action("toggle_auto_search_off");
        await Frames(host, 30);
        check("switch_off_no_search", Snapshot().Searches == searches && Snapshot().CurrentTurnRouteReady,
            $"{searches}->{Snapshot().Searches}");
        int turn = Snapshot().Turn;
        await PressPanelButtonAsync(host, PanelButton.ExecuteCombat, "execute_combat");
        action("press_execute_combat");
        await WaitAsync(host, static s => s.IsDeploying, 240, "run deploying");
        await Frames(host, 2);
        check("run_active", Snapshot().CombatRunActive, ModeText(Snapshot()));
        check("only_stop_key", panel?.StopExecutionButtonForTest is { Visible: true }
            && panel.ExecuteTurnButtonForTest?.Visible == false
            && panel.ExecuteCombatButtonForTest?.Visible == false
            && panel.SearchButtonForTest?.Disabled == true, FooterText(panel));
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.Turn > turn && (s.IsDeploying || s.IsSearching) || !CombatManager.Instance.IsInProgress,
            2400, "run reaches the next turn");
        if (!CombatManager.Instance.IsInProgress)
        {
            check("combat_continues", false, "combat ended during the first turn");
            return;
        }
        check("run_next_turn", next.CombatRunActive && (next.IsDeploying || next.IsSearching), ModeText(next));
        await PressPanelButtonAsync(host, PanelButton.Execute, "stop_execution");
        action("press_stop_execution");
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => !s.IsDeploying && !s.IsSearching && !s.CombatRunActive, 1800, "run stopped");
        check("run_ended", !stopped.CombatRunActive && !stopped.CombatRunPaused, ModeText(stopped));
        await Frames(host, 4);
        check("stop_key_hidden", panel?.StopExecutionButtonForTest?.Visible == false, FooterText(panel));
        check("search_available_after_stop", SearchAvailable(panel), FooterText(panel));
    }

    private static async Task ManualReusesRouteAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int reused = Snapshot().ReusedTurns;
        await PressPanelButtonAsync(host, PanelButton.Search, "search");
        CombatUiSnapshot ready = await WaitAsync(host,
            static s => s.Result != null && !s.IsSearching, 900, "route ready");
        int searches = ready.Searches;
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        action("press_execute");
        await WaitAsync(host, static s => s.IsDeploying, 240, "deploying");
        await WaitIdleAsync(host, "deployment done");
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.ReusedTurns > reused || !CombatManager.Instance.IsInProgress, 1200,
            "manual route reused");
        check("combat_continues", CombatManager.Instance.IsInProgress);
        check("reused", next.ReusedTurns > reused, $"{reused}->{next.ReusedTurns}");
        check("no_new_search", next.Searches == searches, $"{searches}->{next.Searches}");
        check("reused_result", next.Result?.WasReused == true);
        CombatUiSnapshot reusedReady = await WaitAsync(host,
            static s => s.CurrentTurnRouteReady, 240, "reused route ready");
        check("route_ready", reusedReady.CurrentTurnRouteReady);
    }

    private static async Task ModeSwitchMidTurnAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int searches = Snapshot().Searches;
        await PressPanelButtonAsync(host, PanelButton.AutoSearchOn, "auto_search_on");
        action("press_auto_search_on");
        CombatUiSnapshot searching = await WaitAsync(host, static s => s.IsSearching, 240,
            "switch started search");
        check("search_reason", searching.Search?.Reason == "auto_search_switch",
            searching.Search?.Reason);
        await PressPanelButtonAsync(host, PanelButton.AutoSearchOff, "auto_search_off");
        action("press_auto_search_off");
        await Frames(host, 4);
        check("not_interrupted", Snapshot().IsSearching);
        await WaitIdleAsync(host, "mode switch search done");
        check("one_search", Snapshot().Searches == searches + 1,
            $"{searches}->{Snapshot().Searches}");
        check("no_deploy", !CombatUiStore.History.Any(static s => s.IsDeploying));
    }

    private static async Task FullAutoPauseCombatAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching
            && !s.IsDeploying, 900, "result ready");
        CombatControllerTestHooks.ForceDeploymentFailure = static (deployed, _) =>
            deployed.Action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.PlayCard
                ? "forced_failure"
                : null;
        action("force_deployment_failure");
        check("set_full_auto", CombatSessionController.ArmRunForTest(), "full auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused
                && s.PauseCode == "deploy_failed", 2400, "deploy failure pause");
        check("pause_code", paused.PauseCode == "deploy_failed", paused.PauseCode);
        check("failures", paused.DeploymentFailures >= 1, paused.DeploymentFailures.ToString());
        check("mode_full_auto", (paused.CombatRunActive || paused.CombatRunPaused), ModeText(paused));
        check("paused_this_combat", paused.CombatRunPaused);
        check("not_armed", !paused.CombatRunActive);
        CombatControllerTestHooks.ForceDeploymentFailure = null;
        int searches = paused.Searches;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot searching = await WaitAsync(host,
            s => s.IsSearching && s.Search?.Reason == "auto_turn_start", 900,
            "auto search after pause");
        check("auto_search_resumed", searching.Searches > searches,
            $"{searches}->{searching.Searches}");
        check("run_still_off", !searching.CombatRunActive, ModeText(searching));
        check("auto_search_still_on", searching.AutoSearchEnabled, ModeText(searching));
        await WaitIdleAsync(host, "post-pause search done");
        check("no_auto_execute", !Snapshot().IsDeploying);
        await PressPanelButtonAsync(host, PanelButton.ExecuteCombat, "resume_full_auto");
        action("press_resume_full_auto");
        CombatUiSnapshot resumed = await WaitAsync(host,
            static s => !s.CombatRunPaused, 120, "full auto resumed");
        check("resume_flag_cleared", !resumed.CombatRunPaused);
        check("resume_armed", resumed.CombatRunActive || resumed.IsDeploying || resumed.IsSearching);
        await WaitAsync(host, static s => s.IsDeploying || s.IsSearching, 240,
            "full auto resumed work");
        if (Snapshot().IsSearching)
            await WaitAsync(host, static s => s.IsDeploying, 1200, "resume deployment");
        await WaitIdleAsync(host, "resumed deployment done");
    }

    private static async Task StrategyCommandsAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        check("reset_settings", SolverSettings.Update(settings => settings with
        {
            Search = settings.Search with
            {
                Potions = settings.Search.Potions with { Directives = [] },
                GrowthBudgets = new SolverGrowthBudgets(),
                IgnoreLongTermRewards = false,
                BrightestFlameMaxHpLossLimit = null,
                ActTransitionBossHpStrategy = SearchBossHpStrategy.ProgressionFirst,
                FinalBossHpStrategy = SearchBossHpStrategy.ProgressionFirst,
            },
        }), "strategy settings reset");
        await Frames(host, 4);

        if (Snapshot().Strategy.TheftApplicable)
        {
            StrategyUiSnapshot theftStrategy = Snapshot().Strategy;
            check("theft_default", theftStrategy.TheftPolicy == "preserve_resources",
                theftStrategy.TheftPolicy ?? "-");
            int before = Snapshot().Searches;
            check("theft_cmd", CombatUiCommands.Instance.SetTheftPolicy(SearchTheftPolicy.LetEscape));
            action("set_theft:let_escape");
            await Frames(host, 30);
            CombatUiSnapshot after = Snapshot();
            check("theft_no_search", after.Searches == before && !after.IsSearching,
                $"{before}->{after.Searches}");
            SearchCase theft = CombatSessionController.CompileRequestForTest();
            check("theft_request", theft.Options.TheftPolicy == TheftPolicy.LetEscape,
                theft.Options.TheftPolicy?.ToString() ?? "-");
            check("theft_snapshot", Snapshot().Strategy.TheftPolicy == "let_escape",
                Snapshot().Strategy.TheftPolicy ?? "-");
            return;
        }

        async Task<SearchCase> SearchAsync(string name)
        {
            int before = Snapshot().Searches;
            check(name + "_start", CombatUiCommands.Instance.StartSearch(), name);
            CombatUiSnapshot done = await WaitAsync(host,
                s => s.Searches > before && !s.IsSearching, 2400, name);
            check(name + "_done", done.Searches == before + 1, $"{before}->{done.Searches}");
            return CombatSessionController.LastSearchCaseForTest
                ?? throw new InvalidDataException(name + ": request missing");
        }

        async Task NoSearchAsync(string name)
        {
            int before = Snapshot().Searches;
            await Frames(host, 30);
            CombatUiSnapshot after = Snapshot();
            check(name, after.Searches == before && !after.IsSearching,
                $"{before}->{after.Searches}");
        }

        SearchCase baseline = await SearchAsync("baseline");
        StrategyUiSnapshot strategy = Snapshot().Strategy;
        if (strategy.Potions.Length != 0)
        {
            PotionSlotUi? potion = strategy.Potions.FirstOrDefault(static item => item.CanAssignPolicy);
            if (potion == null)
            {
                check("potion_searchable", false, "no searchable potion");
            }
            else
            {
                check("potion_single_cmd", CombatUiCommands.Instance.SetPotionDirective(
                    potion.Slot, potion.PotionId, SolverPotionMode.Disabled));
                action($"set_potion:{potion.Slot}:{potion.PotionId}:disabled");
                await NoSearchAsync("potion_single_no_search");
                check("potion_single_snapshot",
                    Snapshot().Strategy.Potions.Any(item => item.Slot == potion.Slot
                        && item.Mode == "disabled"), "snapshot mode");
                SearchCase single = await SearchAsync("potion_single");
                check("potion_single_request",
                    single.Options.Potions.Strategy.Directives.Any(item =>
                        item.Slot == potion.Slot && item.PotionId == potion.PotionId
                        && item.Directive == PotionDirective.Disabled), "disabled directive");
                check("potion_single_effective",
                    single.Options.EffectivePotionPolicy == PotionUseMode.Smart, "effective smart");
                check("potion_single_route",
                    !CombatSessionController.LastSearchRouteForTest.Any(action =>
                        action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.UsePotion
                        && action.PotionSlot == potion.Slot), "disabled potion used");

                check("potion_all_disabled_cmd",
                    CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Disabled));
                action("set_all_potions:disabled");
                await NoSearchAsync("potion_all_disabled_no_search");
                SearchCase allDisabled = await SearchAsync("potion_all_disabled");
                int searchable = Snapshot().Strategy.Potions.Count(static item => item.CanAssignPolicy);
                check("potion_all_disabled_request",
                    allDisabled.Options.Potions.Strategy.Directives.Count == searchable,
                    $"{allDisabled.Options.Potions.Strategy.Directives.Count}!={searchable}");

                check("potion_all_smart_cmd",
                    CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Smart));
                action("set_all_potions:smart");
                await NoSearchAsync("potion_all_smart_no_search");
                SearchCase allSmart = await SearchAsync("potion_all_smart");
                check("potion_all_smart_request",
                    allSmart.Options.Potions.Strategy.Directives.Count == 0,
                    allSmart.Options.Potions.Strategy.Directives.Count.ToString());

                check("potion_all_force_cmd",
                    CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Force));
                action("set_all_potions:force");
                await NoSearchAsync("potion_all_force_no_search");
                int forceCount = Snapshot().Strategy.Potions.Count(static item => item.CanAssignPolicy);
                SearchCase allForce = CombatSessionController.CompileRequestForTest();
                check("potion_all_force_request",
                    allForce.Options.Potions.Strategy.Directives.Count == forceCount
                    && allForce.Options.Potions.Strategy.Directives.All(static item =>
                        item.Directive == PotionDirective.Force),
                    allForce.Options.Potions.Strategy.Directives.Count.ToString());
                check("potion_all_force_snapshot",
                    Snapshot().Strategy.Potions.Where(static item => item.CanAssignPolicy)
                        .All(static item => item.Mode == "force"), "snapshot force");

                check("potion_all_smart_again",
                    CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Smart));
            }
        }

        check("growth_cmd", CombatUiCommands.Instance.SetGrowthBudget(GrowthSource.Feed, 7));
        action("set_growth:feed:7");
        await NoSearchAsync("growth_no_search");
        check("growth_snapshot", Snapshot().Strategy.GrowthBudgets.Feed == 7, "snapshot budget");
        SearchCase growth = await SearchAsync("growth");
        check("growth_request", growth.Options.Scorer?.RulesForCodec.GrowthBudgets.Feed == 7,
            growth.Options.Scorer?.RulesForCodec.GrowthBudgets.Feed.ToString() ?? "-");
        check("ignore_cmd", CombatUiCommands.Instance.SetIgnoreLongTermRewards(true));
        action("set_ignore_long_term:true");
        await NoSearchAsync("ignore_no_search");
        check("ignore_snapshot", Snapshot().Strategy.IgnoreLongTermRewards, "snapshot ignore");
        SearchCase ignore = await SearchAsync("ignore");
        check("ignore_request", ignore.Options.Scorer?.RulesForCodec.IgnoreLongTermRewards == true,
            "ignore flag");
        check("ignore_effective_budget",
            ignore.Options.Scorer?.RulesForCodec.GrowthBudgets.Total == 0,
            ignore.Options.Scorer?.RulesForCodec.GrowthBudgets.Total.ToString() ?? "-");
        check("ignore_reset", CombatUiCommands.Instance.SetIgnoreLongTermRewards(false));
        check("growth_reset", CombatUiCommands.Instance.SetGrowthBudget(GrowthSource.Feed, 0));

        RelicCounterUi[] relics = Snapshot().Strategy.RelicCounters;
        bool TargetsMatch(SearchCase request, int firstAllowance, out string detail)
        {
            RelicCounterTarget[] targets = request.Options.Scorer?.RulesForCodec.RelicCounterTargets ?? [];
            detail = string.Join(",", targets.Select(static target =>
                $"{target.Slot}:{target.Goal}/{target.Period}+{target.HpAllowance}"));
            return targets.Length == relics.Length
                && targets.Select((target, index) => target.Slot == relics[index].Slot
                    && target.Period == relics[index].Period && target.Goal == relics[index].Period - 1
                    && target.HpAllowance == (index == 0 ? firstAllowance : 0)).All(static ok => ok);
        }
        check("relic_targets_default",
            TargetsMatch(CombatSessionController.CompileRequestForTest(), 0, out string relicDefault),
            relicDefault);
        if (relics.Length != 0)
        {
            string relicId = relics[0].RelicId;
            check("relic_cmd", CombatUiCommands.Instance.SetRelicCounterAllowance(relicId, 4));
            action($"set_relic:{relicId}:4");
            await NoSearchAsync("relic_no_search");
            check("relic_snapshot",
                Snapshot().Strategy.RelicCounterAllowances.TryGetValue(relicId, out int saved) && saved == 4,
                "snapshot allowance");
            check("relic_request",
                TargetsMatch(CombatSessionController.CompileRequestForTest(), 4, out string relicSet), relicSet);
            check("relic_ignore_cmd", CombatUiCommands.Instance.SetIgnoreLongTermRewards(true));
            RelicCounterTarget[] ignored = CombatSessionController.CompileRequestForTest()
                .Options.Scorer?.RulesForCodec.RelicCounterTargets ?? [];
            check("relic_ignore_request", ignored.Length == 0, ignored.Length.ToString());
            check("relic_ignore_reset", CombatUiCommands.Instance.SetIgnoreLongTermRewards(false));
            check("relic_reset", CombatUiCommands.Instance.SetRelicCounterAllowance(relicId, 0)
                && !SolverSettings.Current.Search.RelicCounterAllowances.ContainsKey(relicId), "allowance kept");
        }
        check("relic_unknown_rejected", !CombatUiCommands.Instance.SetRelicCounterAllowance("BURNING_BLOOD", 3)
            && !CombatUiCommands.Instance.SetRelicCounterAllowance("PEN_NIB", 1001), "accepted");

        check("flame_set_cmd", CombatUiCommands.Instance.SetBrightestFlameLimit(2));
        action("set_brightest_flame:2");
        await NoSearchAsync("flame_no_search");
        check("flame_snapshot", Snapshot().Strategy.BrightestFlameMaxHpLossLimit == 2,
            Snapshot().Strategy.BrightestFlameMaxHpLossLimit?.ToString() ?? "null");
        SearchCase flame = CombatSessionController.CompileRequestForTest();
        check("flame_request", flame.Options.BrightestFlameMaxHpLossLimit == 2,
            flame.Options.BrightestFlameMaxHpLossLimit?.ToString() ?? "null");
        check("flame_root_spent",
            flame.Options.InitialBrightestFlameMaxHpSpent == Snapshot().Strategy.BrightestFlameMaxHpSpent,
            $"{flame.Options.InitialBrightestFlameMaxHpSpent}!={Snapshot().Strategy.BrightestFlameMaxHpSpent}");

        check("flame_zero_cmd", CombatUiCommands.Instance.SetBrightestFlameLimit(0));
        action("set_brightest_flame:0");
        await NoSearchAsync("flame_zero_no_search");
        SearchCase flameZero = CombatSessionController.CompileRequestForTest();
        check("flame_zero_request", flameZero.Options.BrightestFlameMaxHpLossLimit == 0,
            flameZero.Options.BrightestFlameMaxHpLossLimit?.ToString() ?? "null");
        check("flame_zero_snapshot", Snapshot().Strategy.BrightestFlameMaxHpLossLimit == 0,
            Snapshot().Strategy.BrightestFlameMaxHpLossLimit?.ToString() ?? "null");

        check("flame_invalid", !CombatUiCommands.Instance.SetBrightestFlameLimit(1001));
        check("flame_clear_cmd", CombatUiCommands.Instance.SetBrightestFlameLimit(null));
        action("set_brightest_flame:none");
        await NoSearchAsync("flame_clear_no_search");
        check("flame_clear_snapshot", Snapshot().Strategy.BrightestFlameMaxHpLossLimit is null,
            Snapshot().Strategy.BrightestFlameMaxHpLossLimit?.ToString() ?? "null");
        SearchCase flameClear = CombatSessionController.CompileRequestForTest();
        check("flame_clear_request", flameClear.Options.BrightestFlameMaxHpLossLimit is null,
            flameClear.Options.BrightestFlameMaxHpLossLimit?.ToString() ?? "null");

        if (strategy.IsActEndingBoss)
        {
            check("boss_baseline_has_relief",
                baseline.Options.Ordering?.BossHpRelief is BossHpRelief.ActClearHeal
                    or BossHpRelief.RunEnding,
                baseline.Options.Ordering?.BossHpRelief.ToString() ?? "-");
            check("boss_snapshot_relief",
                Snapshot().Strategy.BossHpRelief is "act_clear_heal" or "run_ending",
                Snapshot().Strategy.BossHpRelief);
            check("act_boss_cmd",
                CombatUiCommands.Instance.SetActTransitionBossHpStrategy(
                    SearchBossHpStrategy.MinimizeHpLoss));
            check("final_boss_cmd",
                CombatUiCommands.Instance.SetFinalBossHpStrategy(SearchBossHpStrategy.MinimizeHpLoss));
            action("set_boss_hp:minimize");
            await NoSearchAsync("boss_no_search");
            check("boss_snapshot_strategy",
                Snapshot().Strategy.ActTransitionBossHpStrategy
                    == SearchBossHpStrategy.MinimizeHpLoss
                && Snapshot().Strategy.FinalBossHpStrategy
                    == SearchBossHpStrategy.MinimizeHpLoss, "snapshot strategy");
            SearchCase boss = await SearchAsync("boss");
            check("boss_request", boss.Options.Ordering?.BossHpRelief == BossHpRelief.None,
                boss.Options.Ordering?.BossHpRelief.ToString() ?? "-");
        }
        else
        {
            check("boss_not_applicable", !strategy.IsActEndingBoss);
        }

        check("theft_reject", !CombatUiCommands.Instance.SetTheftPolicy(
            SearchTheftPolicy.LetEscape));
        check("theft_absent", strategy.TheftPolicy == null, strategy.TheftPolicy ?? "-");
    }

    /// <summary>The strategy panel driven through its controls, in manual mode so no search runs.</summary>
    private static async Task PanelStrategyAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        bool reset = CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Smart)
            && CombatUiCommands.Instance.SetIgnoreLongTermRewards(false)
            && CombatUiCommands.Instance.SetBrightestFlameLimit(null)
            && CombatUiCommands.Instance.SetActTransitionBossHpStrategy(SearchBossHpStrategy.ProgressionFirst)
            && CombatUiCommands.Instance.SetFinalBossHpStrategy(SearchBossHpStrategy.ProgressionFirst)
            && Enum.GetValues<GrowthSource>().All(source => CombatUiCommands.Instance.SetGrowthBudget(source, 0));
        check("reset_strategy", reset, "strategy commands");
        await Frames(host, 4);
        await UiPanelChecks.StrategyAsync(host, check, action);
    }

    /// <summary>
    /// Regression for content that appears before the first play-phase root (injected potions or
    /// turn-one generation): the controller must recompile its rules from the live state, so the
    /// search request contains every pool/card the root can generate. Before that refresh a
    /// potion-enabled member could fail at solve time or the root import could reject a status card.
    /// </summary>
    private static async Task SearchRulePoolsAsync(NGame host, Check check, Action action,
        string name, string[] expectedPools, string[] expectedCards)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int before = Snapshot().Searches;
        check("search_start", CombatUiCommands.Instance.StartSearch());
        action(name);
        CombatUiSnapshot done = await WaitAsync(host,
            s => s.Searches > before && !s.IsSearching && !s.IsDeploying, 1800,
            name);
        SearchCase request = CombatSessionController.LastSearchCaseForTest
            ?? throw new InvalidDataException($"{name} search request missing");
        foreach (string pool in expectedPools)
        {
            check("pool_" + pool, request.Rules.TryCardPool(pool, out _), pool);
        }
        foreach (string card in expectedCards)
            check("card_" + card, HasRuleCard(request.Rules, card), card);
        check("no_error", done.ErrorCode == null, done.ErrorCode ?? "-");
    }

    private static bool HasRuleCard(Sts2Solver.Simulation.Combat.Rules rules, string nativeId)
    {
        if (!Sts2Solver.Simulation.Combat.Registry.TryCard(nativeId, out ushort id))
            return false;
        try
        {
            _ = rules.Card(id, 0);
            return true;
        }
        catch (Sts2Solver.Simulation.Combat.UnsupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Forces one potion, protects another, and executes the current turn. The forced potion must be
    /// planned and then actually consumed by the live deployment.
    /// </summary>
    private static async Task ForcedPotionAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        check("reset_settings", SolverSettings.Update(settings => settings with
        {
            Search = settings.Search with
            {
                Potions = settings.Search.Potions with
                {
                    Directives = [],
                    Preset = SolverPotionPreset.Smart,
                },
            },
        }), "potion settings reset");
        await Frames(host, 4);
        PotionSlotUi[] searchable = Snapshot().Strategy.Potions
            .Where(static item => item.CanAssignPolicy)
            .ToArray();
        check("potion_count", searchable.Length >= 1, searchable.Length.ToString());
        PotionSlotUi forced = searchable[0];
        PotionSlotUi? disabled = searchable.Skip(1).FirstOrDefault();
        if (disabled is { } other)
            check("disable_other", CombatUiCommands.Instance.SetPotionDirective(
                other.Slot, other.PotionId, SolverPotionMode.Disabled));
        check("force_cmd", CombatUiCommands.Instance.SetPotionDirective(
            forced.Slot, forced.PotionId, SolverPotionMode.Force));
        action($"set_forced_potion:{forced.Slot}:{forced.PotionId}");
        int before = Snapshot().Searches;
        await Frames(host, 30);
        CombatUiSnapshot changed = Snapshot();
        check("no_search", changed.Searches == before && !changed.IsSearching,
            $"{before}->{changed.Searches}");
        check("snapshot_force", changed.Strategy.Potions.Any(item =>
            item.Slot == forced.Slot && item.PotionId == forced.PotionId && item.Mode == "force"),
            "snapshot force");
        if (disabled is { } protectedSlot)
            check("snapshot_disabled", changed.Strategy.Potions.Any(item =>
                item.Slot == protectedSlot.Slot && item.PotionId == protectedSlot.PotionId
                && item.Mode == "disabled"), "snapshot disabled");

        check("search_start", CombatUiCommands.Instance.StartSearch());
        action("search_forced_potion");
        CombatUiSnapshot done = await WaitAsync(host,
            s => s.Searches > before && !s.IsSearching && !s.IsDeploying, 1800,
            "forced potion search");
        SearchCase request = CombatSessionController.LastSearchCaseForTest
            ?? throw new InvalidDataException("forced potion search request missing");
        check("request_force", request.Options.Potions.Strategy.Directives.Any(item =>
            item.Slot == forced.Slot && item.PotionId == forced.PotionId
            && item.Directive == PotionDirective.Force), "force directive");
        if (disabled is { } requestedDisabled)
            check("request_disabled", request.Options.Potions.Strategy.Directives.Any(item =>
                item.Slot == requestedDisabled.Slot && item.PotionId == requestedDisabled.PotionId
                && item.Directive == PotionDirective.Disabled), "disabled directive");
        Sts2Solver.Simulation.Combat.PlayerAction[] route =
            CombatSessionController.LastSearchRouteForTest;
        check("route_force", route.Any(action =>
            action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.UsePotion
            && action.PotionSlot == forced.Slot), "route uses forced potion");
        if (disabled is { } plannedDisabled)
            check("route_disabled_absent", !route.Any(action =>
                action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.UsePotion
                && action.PotionSlot == plannedDisabled.Slot), "route omits disabled potion");

        RouteUiSnapshot plan = done.Route
            ?? throw new InvalidDataException("forced potion route missing");
        RouteTurnUi firstTurn = plan.Turns.FirstOrDefault()
            ?? throw new InvalidDataException("forced potion route has no turns");
        check("first_turn_force", firstTurn.Actions.Any(item =>
            item.Kind == "use_potion" && item.PotionId == forced.PotionId),
            "forced potion is scheduled in the current turn");
        check("execute", CombatUiCommands.Instance.ExecuteCurrentTurn());
        action("execute_forced_potion");
        await WaitAsync(host, static s => s.IsDeploying, 240, "forced potion deployment");
        await WaitIdleAsync(host, "forced potion deployment done");
        CombatUiSnapshot after = Snapshot();
        check("potion_consumed", after.Strategy.Potions.All(item =>
            item.Slot != forced.Slot || item.PotionId != forced.PotionId),
            "live belt no longer holds the forced potion");
        check("no_deviation", after.Deviations == changed.Deviations,
            $"{changed.Deviations}->{after.Deviations}");
    }

    private static async Task StopDeploymentAsync(NGame host, Check check, Action action)
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
            action("set_mode:auto_search");
            await WaitAsync(host, static s => s.Result != null && !s.IsSearching
                && !s.IsDeploying, 900, "result ready");
            check("settings_saved", SolverSettings.Update(current => current with
            {
                Automation = current.Automation with { InterActionDelaySeconds = 0.6d },
            }), "delay saved");
            CombatControllerTestHooks.DeploymentCompletionTimes.Clear();
            CombatControllerTestHooks.DeploymentWaits.Clear();
            await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
            action("press_execute");
            await WaitAsync(host, static s => s.IsDeploying, 240, "deploying");
            for (int frame = 0; frame < 600; frame++)
            {
                if (CombatControllerTestHooks.DeploymentCompletionTimes.Count > 0
                    && CombatControllerTestHooks.DeploymentWaits.Count > 0
                    && CombatControllerTestHooks.DeploymentWaits[^1].End == 0L)
                    break;
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            int completedAtStop = CombatControllerTestHooks.DeploymentCompletionTimes.Count;
            await PressPanelButtonAsync(host, PanelButton.Execute, "stop_deployment");
            action("press_stop_deployment");
            CombatUiSnapshot stopping = await WaitAsync(host,
                static s => s.IsDeploying && s.IsStoppingDeployment, 120, "stopping deployment");
            check("deployment_retained", stopping.IsDeploying);
            check("stopping_flag", stopping.IsStoppingDeployment);
            SolverOverlay? panel = SolverOverlay.Instance;
            check("left_disabled", panel?.SearchButtonForTest?.Disabled == true);
            check("left_reason", stopping.SearchBlockReason == "executing",
                stopping.SearchBlockReason ?? "-");
            await Frames(host, 4);
            check("stop_button_disabled", panel?.StopExecutionButtonForTest?.Disabled == true,
                FooterText(panel));
            await WaitAsync(host, static s => !s.IsDeploying, 1800, "deployment stopped");
            int completedAfter = CombatControllerTestHooks.DeploymentCompletionTimes.Count;
            check("no_new_actions", completedAfter <= completedAtStop + 1,
                $"{completedAtStop}->{completedAfter}");
            await Frames(host, 30);
            check("no_late_actions",
                CombatControllerTestHooks.DeploymentCompletionTimes.Count == completedAfter);
            CombatUiSnapshot stopped = Snapshot();
            check("stopped_this_turn", stopped.AutomationStoppedThisTurn);
            check("pause_code", stopped.PauseCode == "stopped", stopped.PauseCode);
            check("not_searching", !stopped.IsSearching);
            check("left_enabled", SearchAvailable(panel), FooterText(panel));
        }
        finally
        {
            SolverSettings.Update(_ => original);
        }
    }

    private static async Task SearchBlockReasonsAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        CombatUiSnapshot idle = Snapshot();
        check("can_start", idle.CanStartSearch && idle.SearchBlockReason == null,
            idle.SearchBlockReason ?? "-");
        await ForceEndTurnAsync(host, waitForPlay: false);
        action("force_end_turn");
        CombatUiSnapshot enemy = await WaitAsync(host,
            static s => s.SearchBlockReason == "enemy_turn", 240, "enemy turn reason");
        check("enemy_block", enemy.SearchBlockReason == "enemy_turn", enemy.SearchBlockReason);
        check("enemy_cannot_start", !enemy.CanStartSearch);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("enemy_left_disabled", panel?.SearchButtonForTest?.Disabled == true);
        check("enemy_tooltip",
            panel?.SearchButtonForTest?.TooltipText == SolverText.Get("敌方回合，轮到你时再搜索。"),
            panel?.SearchButtonForTest?.TooltipText ?? "-");
        await WaitAsync(host, static s => s.CanStartSearch, 900, "player turn restored");
        CombatControllerTestHooks.ForceActionsRunningForTest = true;
        action("force_actions_running");
        try
        {
            CombatUiSnapshot running = await WaitAsync(host,
                static s => s.SearchBlockReason == "actions_running", 120,
                "actions running reason");
            check("actions_block", running.SearchBlockReason == "actions_running",
                running.SearchBlockReason);
            check("actions_cannot_start", !running.CanStartSearch);
            await Frames(host, 4);
            check("actions_left_disabled", panel?.SearchButtonForTest?.Disabled == true);
            check("actions_tooltip",
                panel?.SearchButtonForTest?.TooltipText
                    == SolverText.Get("动作结算中（含选牌界面），结算完再搜索。"),
                panel?.SearchButtonForTest?.TooltipText ?? "-");
            check("actions_rejected", !CombatUiCommands.Instance.StartSearch());
        }
        finally
        {
            CombatControllerTestHooks.ForceActionsRunningForTest = false;
        }
        _ = CombatSessionController.LeaveRunForTest(autoSearch: false);
        await WaitAsync(host, static s => s.CanStartSearch, 120, "actions settled");
        CombatControllerTestHooks.ForceTurnStartForTest = true;
        action("force_turn_start");
        _ = CombatSessionController.LeaveRunForTest(autoSearch: false);
        try
        {
            CombatUiSnapshot start = await WaitAsync(host,
                static s => s.SearchBlockReason == "turn_start", 120, "turn start reason");
            check("turn_start_block", start.SearchBlockReason == "turn_start",
                start.SearchBlockReason);
            check("turn_start_cannot_start", !start.CanStartSearch);
            await Frames(host, 4);
            check("turn_start_left_disabled", panel?.SearchButtonForTest?.Disabled == true);
            check("turn_start_tooltip",
                panel?.SearchButtonForTest?.TooltipText
                    == SolverText.Get("回合开始结算中，完成开局选择后再搜索。"),
                panel?.SearchButtonForTest?.TooltipText ?? "-");
        }
        finally
        {
            CombatControllerTestHooks.ForceTurnStartForTest = false;
        }
        _ = CombatSessionController.LeaveRunForTest(autoSearch: false);
        await WaitAsync(host, static s => s.CanStartSearch, 120, "turn start settled");
    }

    private static async Task DeploymentDriftResearchAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: false), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        await PressPanelButtonAsync(host, PanelButton.Search, "search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching, 1200,
            "route ready");
        int searches = Snapshot().Searches;
        int forced = 0;
        CombatControllerTestHooks.ForceDeploymentFailure = (deployed, _) =>
        {
            if (deployed.Action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.PlayCard
                && forced++ == 0)
            {
                return "action_failed";
            }
            return null;
        };
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        action("press_execute");
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused && s.PauseCode == "deploy_failed",
            1800, "deploy failed stop");
        check("pause_code", stopped.PauseCode == "deploy_failed", stopped.PauseCode);
        check("route_dropped", stopped.Result == null && !stopped.CurrentTurnRouteReady);
        check("no_research", stopped.Searches == searches, $"{searches}->{stopped.Searches}");
        check("failures", stopped.DeploymentFailures == 1, stopped.DeploymentFailures.ToString());
        check("deviations", stopped.Deviations == 0, stopped.Deviations.ToString());
        check("reason_kind", stopped.PauseDetailReason?.Kind == CombatReasonKind.Deployment,
            stopped.PauseDetailReason?.Kind ?? "-");
        await WaitStatusAsync(host, SolverText.Get("已暂停执行"));
        check("summary_title",
            Summary().Contains("自动执行中止", StringComparison.Ordinal), Summary());
        check("summary_reason",
            Summary().Contains("原生动作执行出错", StringComparison.Ordinal), Summary());
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("left_text", SearchAvailable(panel), FooterText(panel));
        check("left_enabled", SearchAvailable(panel), FooterText(panel));
        check("right_hidden", ExecuteUnavailable(panel), FooterText(panel));
        await Frames(host, 60);
        check("still_no_research", Snapshot().Searches == searches,
            $"{searches}->{Snapshot().Searches}");
        CombatControllerTestHooks.ForceDeploymentFailure = null;
    }

    private static async Task LiveEndTurnRiskRetainedAsync(NGame host, Check check, Action action)
    {
        bool damaged = false;
        CombatControllerTestHooks.BeforeLiveEndTurnCheck = async (state, player) =>
        {
            if (damaged)
                return;
            damaged = true;
            await CreatureCmd.LoseBlock(new BlockingPlayerChoiceContext(), player.Creature,
                player.Creature.Block, null);
            await DamagePlayerAsync(state, player, 60);
        };
        check("mode_set", CombatSessionController.LeaveRunForTest(autoSearch: true), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching
            && !s.IsDeploying, 900, "result ready");
        check("set_full_auto", CombatSessionController.ArmRunForTest(), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused
                && s.PauseCode == "live_end_turn_risk", 2400, "live end turn pause");
        check("damaged", damaged, "live damage injected");
        check("paused_this_combat", paused.CombatRunPaused);
        check("not_armed", !paused.CombatRunActive);
        check("result_retained", paused.Result != null);
        check("route_ready", paused.CurrentTurnRouteReady);
        check("only_end_turn",
            paused.Route?.Turns.Length == 1
            && paused.Route.Turns[0].Actions.Length == 1
            && paused.Route.Turns[0].Actions[0].EndsTurn,
            $"{paused.Route?.Turns.Length}/{paused.Route?.Turns.FirstOrDefault()?.Actions.Length}");
        CombatControllerTestHooks.BeforeLiveEndTurnCheck = null;
        await PressPanelButtonAsync(host, PanelButton.Execute, "force_end_turn");
        action("press_execute_after_live_pause");
        await WaitAsync(host, static s => s.IsDeploying, 240, "forced deployment");
        await WaitIdleAsync(host, "forced end turn done");
    }

    private enum PanelButton
    {
        Search,
        Execute,
        ExecuteCombat,
        AutoSearchOn,
        AutoSearchOff,
    }

    private sealed class SettingsOverride : IDisposable
    {
        private readonly SolverSettingsData _original;

        internal SettingsOverride(SolverSettingsData replacement)
        {
            _original = SolverSettings.Current;
            SolverSettings.Update(_ => replacement);
        }

        public void Dispose() => SolverSettings.Update(_ => _original);
    }

    private static SettingsOverride SlowSearchTier()
        => new(SolverSettings.Current with
        {
            Search = SolverSettings.Current.Search with { Tier = SearchPerformanceTier.ExtraHigh },
        });

    /// <summary>
    /// The old single search key's intent on the new footer: stop a running search, start the turn's
    /// first search, continue a stopped one when possible, otherwise search again.
    /// </summary>
    private static Button? SearchKey(SolverOverlay panel, CombatUiSnapshot snapshot)
        => snapshot.IsSearching || snapshot.SearchStage == TurnSearchStage.NotStarted
            || snapshot.CanContinueSearch
            ? panel.SearchButtonForTest
            : panel.RestartSearchButtonForTest;

    /// <summary>
    /// The old single execute key's intent: stop an execution, confirm a paused run (执行本场战斗),
    /// otherwise play this turn.
    /// </summary>
    private static Button? ExecuteKey(SolverOverlay panel, CombatUiSnapshot snapshot)
        => snapshot.IsDeploying || snapshot.TurnSetupDriving || snapshot.CombatRunActive
            || snapshot.IsSearching && snapshot.DeployWhenReady
            ? panel.StopExecutionButtonForTest
            : snapshot.CombatRunPaused
                ? panel.ExecuteCombatButtonForTest
                : panel.ExecuteTurnButtonForTest;

    private static string ModeText(CombatUiSnapshot snapshot)
        => $"auto_search={snapshot.AutoSearchEnabled} run={snapshot.CombatRunActive} " +
            $"paused={snapshot.CombatRunPaused}";

    private static bool Usable(Button? button)
        => button is { Disabled: false } && button.IsVisibleInTree();

    /// <summary>A search key the player can press: 开始搜索/继续搜索 on the left or 重新搜索.</summary>
    private static bool SearchAvailable(SolverOverlay? panel)
        => panel != null
            && (Usable(panel.SearchButtonForTest)
                    && panel.SearchButtonForTest!.Text != SolverText.Get("停止搜索")
                || Usable(panel.RestartSearchButtonForTest));

    /// <summary>No execute-area key can be pressed (hidden or greyed).</summary>
    private static bool ExecuteUnavailable(SolverOverlay? panel)
        => panel != null && !Usable(panel.ExecuteTurnButtonForTest)
            && !Usable(panel.ExecuteCombatButtonForTest) && !Usable(panel.StopExecutionButtonForTest);

    private static string FooterText(SolverOverlay? panel)
        => panel == null ? "-" : string.Join(" | ", new[]
        {
            panel.SearchButtonForTest, panel.RestartSearchButtonForTest, panel.ExecuteTurnButtonForTest,
            panel.ExecuteCombatButtonForTest, panel.StopExecutionButtonForTest,
        }.Select(static button => button == null ? "null"
            : $"{button.Name}:{button.Text}:{(button.Visible ? "v" : "h")}{(button.Disabled ? "d" : "e")}"));

    /// <summary>The visible execute-area key: 停止执行 while executing, else 执行本回合.</summary>
    private static Button? ExecuteKeyForTest(SolverOverlay? panel)
        => panel == null ? null
            : panel.StopExecutionButtonForTest is { Visible: true } stop ? stop : panel.ExecuteTurnButtonForTest;

    private static async Task PressPanelButtonAsync(NGame host, PanelButton which, string name)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            SolverOverlay? panel = SolverOverlay.Instance;
            Button? button = panel == null
                ? null
                : which switch
                {
                    PanelButton.Search => SearchKey(panel, Snapshot()),
                    PanelButton.Execute => ExecuteKey(panel, Snapshot()),
                    PanelButton.ExecuteCombat => panel.ExecuteCombatButtonForTest,
                    PanelButton.AutoSearchOn or PanelButton.AutoSearchOff => panel.AutoSearchSwitchForTest,
                    _ => throw new ArgumentOutOfRangeException(nameof(which)),
                };
            if (button is Godot.CheckButton toggle && toggle.IsVisibleInTree() && !toggle.Disabled)
            {
                toggle.ButtonPressed = which == PanelButton.AutoSearchOn;
                return;
            }
            if (button != null && button.IsVisibleInTree() && !button.Disabled)
            {
                button.EmitSignal(BaseButton.SignalName.Pressed);
                return;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new InvalidDataException($"panel_button_unavailable:{name}");
    }

    private static async Task StartManualSearchAsync(NGame host, Action action, bool waitCandidate)
    {
        if (Snapshot().IsSearching)
        {
            await WaitAsync(host, static s => !s.IsSearching, 900, "existing search done");
        }
        await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
        action("press_recalculate");
        await WaitAsync(host, static s => s.IsSearching, 240, "search started");
        if (waitCandidate)
        {
            await WaitAsync(host, static s => s.Search?.StopCandidateAvailable == true,
                600, "candidate rendered");
        }
    }

    private static async Task WaitControllerBeginAsync(NGame host)
    {
        for (int frame = 0; frame < 120; frame++)
        {
            if (CombatSessionController.IsActiveForTest)
                return;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException("controller did not begin the combat");
    }

    private static async Task WaitIdleAsync(NGame host, string description)
    {
        await WaitAsync(host, static s => !s.IsSearching && !s.IsDeploying, 1800, description);
    }

    private static async Task<CombatUiSnapshot> WaitAsync(NGame host,
        Func<CombatUiSnapshot, bool> predicate, int frames, string description)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            CombatUiSnapshot snapshot = Snapshot();
            if (predicate(snapshot))
                return snapshot;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        CombatUiSnapshot last = Snapshot();
        throw new TimeoutException(
            $"{description} frames={frames} phase={last.Phase} searching={last.IsSearching} " +
            $"deploying={last.IsDeploying} pause={last.PauseCode} error={last.ErrorCode} " +
            $"searches={last.Searches} search={last.Search?.Phase}");
    }

    private static async Task<bool> WaitOptionalAsync(NGame host,
        Func<CombatUiSnapshot, bool> predicate, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            if (predicate(Snapshot()))
                return true;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        return predicate(Snapshot());
    }

    private static async Task Frames(NGame host, int count)
    {
        for (int frame = 0; frame < count; frame++)
            await CombatFrameWaiter.NextFrameAsync(host);
    }

    private static CombatUiSnapshot Snapshot() => CombatUiStore.Snapshot;

    private static async Task ForceEndTurnAsync(NGame host, bool waitForPlay = true)
    {
        if (!CombatManager.Instance.IsInProgress)
            return;
        CombatState state = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidDataException("强制结束回合找不到战斗。");
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidDataException("强制结束回合找不到玩家。");
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidDataException("强制结束回合没有玩家回合。");
        TaskCompletionSource<GameAction> captured = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBeforeActionExecuted(GameAction action)
        {
            if (action is EndPlayerTurnAction endTurn && endTurn.OwnerId == player.NetId)
                captured.TrySetResult(action);
        }
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        executor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new EndPlayerTurnAction(player, turn));
            GameAction action = await captured.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await action.CompletionTask.WaitAsync(TimeSpan.FromSeconds(120));
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
        for (int frame = 0; frame < 600; frame++)
        {
            if (!CombatManager.Instance.IsInProgress)
                return;
            CombatState? live = CombatManager.Instance.DebugOnlyGetState();
            if (!waitForPlay && live != null && live.CurrentSide != CombatSide.Player)
                return;
            Player? livePlayer = live == null ? null : LocalContext.GetMe(live);
            if (livePlayer?.PlayerCombatState is { } phase && phase.TurnNumber > turn)
            {
                if (waitForPlay && phase.Phase == PlayerTurnPhase.Play)
                    return;
                if (!waitForPlay && live != null && (live.CurrentSide == CombatSide.Enemy
                    || phase.Phase != PlayerTurnPhase.Play))
                {
                    return;
                }
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException("强制结束回合后没有进入下一回合。");
    }

    private static async Task DamagePlayerAsync(CombatState state, Player player, int amount)
    {
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), player.Creature, amount,
            ValueProp.Unblockable | ValueProp.Unpowered, (Creature?)null, null, null);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (!CombatManager.Instance.IsInProgress || !player.Creature.IsAlive)
            throw new InvalidDataException("伤害注入后战斗意外结束。");
    }

    private static ControllerSnapshotRecord SnapshotRecord(CombatUiSnapshot snapshot) => new(
        snapshot.Sequence, snapshot.Phase.ToString(), snapshot.SearchStage.ToString(),
        snapshot.AutoSearchEnabled, snapshot.CombatRunActive, snapshot.CombatRunPaused,
        snapshot.AutomaticCalculationEnabled, snapshot.AutomaticSearchPaused,
        snapshot.AutomaticSearchPausedTurn, snapshot.AutomationStoppedThisTurn,
        snapshot.IsSearching, snapshot.IsDeploying, snapshot.IsStoppingDeployment,
        snapshot.CanStartSearch, snapshot.CanStopSearch, snapshot.SearchBlockReason,
        snapshot.CurrentTurnRouteReady, snapshot.SearchCandidateAllDeath,
        snapshot.DeployWhenReady, snapshot.PauseCode, snapshot.ErrorCode,
        snapshot.Searches, snapshot.ReusedTurns, snapshot.Deviations, snapshot.DeploymentFailures,
        snapshot.Result?.StartTurn, snapshot.Result?.DeathTurn,
        snapshot.Result?.CombatEndedTurn, snapshot.Result?.ProjectedBattleHpLost,
        snapshot.Result?.ProjectedBattleHpLossIncrease,
        snapshot.Search?.Phase.ToString(), snapshot.Search?.Reason, snapshot.Search?.CandidateVersion,
        snapshot.Search?.CurrentTurnPreview?.Turn, snapshot.Route?.StartTurn,
        snapshot.Route?.ActionCount, snapshot.Route?.Reused);

    internal delegate void Check(string name, bool condition, string? detail = null);

    internal delegate void Action(string text);
}

internal sealed record CombatControllerCheck(string Name, string Status, string? Detail);

internal sealed record ControllerSnapshotRecord(
    long Sequence,
    string Phase,
    string SearchStage,
    bool AutoSearchEnabled,
    bool CombatRunActive,
    bool CombatRunPaused,
    bool AutomaticCalculationEnabled,
    bool AutomaticSearchPaused,
    int? AutomaticSearchPausedTurn,
    bool AutomationStoppedThisTurn,
    bool IsSearching,
    bool IsDeploying,
    bool IsStoppingDeployment,
    bool CanStartSearch,
    bool CanStopSearch,
    string? SearchBlockReason,
    bool CurrentTurnRouteReady,
    bool SearchCandidateAllDeath,
    bool DeployWhenReady,
    string? PauseCode,
    string? ErrorCode,
    int Searches,
    int ReusedTurns,
    int Deviations,
    int DeploymentFailures,
    int? ResultStartTurn,
    int? DeathTurn,
    int? CombatEndedTurn,
    int? ProjectedBattleHpLost,
    int? ProjectedBattleHpLossIncrease,
    string? SearchPhase,
    string? SearchReason,
    int? CandidateVersion,
    int? CurrentTurnPreview,
    int? RouteStartTurn,
    int? RouteActionCount,
    bool? RouteReused);

internal sealed record CombatControllerScenarioResult(
    int SchemaVersion,
    string Scenario,
    string Root,
    string Status,
    string? Error,
    CombatControllerCheck[] Checks,
    string[] Actions,
    ControllerSnapshotRecord[] Snapshots,
    int Turns,
    int PlayerHp,
    int PlayerMaxHp,
    int Searches,
    int ReusedTurns,
    int Deviations,
    int DeploymentFailures,
    string TerminalStatus,
    string Outcome,
    int TurnSetupChoiceCount = 0,
    bool TurnSetupDriven = false,
    bool TurnSetupPageVisible = false,
    bool TurnSetupPlanReady = false,
    bool TurnSetupSearchStarted = false,
    bool TurnSetupContinuation = false,
    int ContinuationReuseMisses = 0,
    string? ContinuationReuseMissDetail = null,
    string? TurnSetupError = null);
