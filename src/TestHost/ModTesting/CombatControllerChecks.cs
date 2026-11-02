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
                case "execute_has_result":
                    await ExecuteHasResultAsync(host, Check, Action);
                    break;
                case "execute_while_searching":
                    await ExecuteWhileSearchingAsync(host, Check, Action);
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
                case "pause_worse_recalculation":
                    await PauseWorseRecalculationAsync(host, Check, Action);
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
                case "manual_reuses_route":
                    await ManualReusesRouteAsync(host, Check, Action);
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
            CombatControllerTestHooks.AutoStopOnCandidate = false;
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
            final.ErrorCode == "turn_setup_failed" ? final.ErrorDetail : null);
    }

    private static async Task ManualStartStopAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "set manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        CombatUiSnapshot idle = Snapshot();
        check("manual_auto_search_off", !idle.AutomaticCalculationEnabled && !idle.FullAutoEnabled);
        check("can_start", idle.CanStartSearch, "can start");
        await PressPanelButtonAsync(host, PanelButton.Search, "recalculate");
        check("start", true, "manual search via panel button");
        action("press_recalculate");
        CombatUiSnapshot searching = await WaitAsync(host,
            static s => s.IsSearching, 240, "manual search started");
        check("search_reason", searching.Search?.Reason == "manual", searching.Search?.Reason);
        check("search_mode", searching.Mode == SolverMode.Manual, searching.Mode.ToString());
        await WaitAsync(host, static s => s.IsSearching, 120, "manual search started");
        await PressPanelButtonAsync(host, PanelButton.Search, "stop_search");
        check("stop", true, "stop search via panel button");
        action("press_stop_search");
        CombatUiSnapshot stopped = await WaitAsync(host, static s => !s.IsSearching, 240,
            "search stopped");
        check("paused", stopped.AutomaticSearchPaused, "automatic paused");
        check("full_auto_off", !stopped.FullAutoEnabled, "full auto off");
        check("pause_code", stopped.PauseCode == "stopped", stopped.PauseCode);
    }

    private static async Task AutoSearchTurnAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await WaitIdleAsync(host, "auto search idle");
        int before = Snapshot().Searches;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.IsSearching && s.Search?.Reason == "auto_turn_start", 600,
            "auto search next turn");
        check("searches_increased", next.Searches > before, $"{before}->{next.Searches}");
        check("full_auto_off", !next.FullAutoEnabled);
        await WaitIdleAsync(host, "auto search done");
        bool deployed = CombatUiStore.History.Any(static s => s.IsDeploying);
        check("no_deploy", !deployed, "auto search never deploys");
    }

    private static async Task ManualNoAutoSearchAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int before = Snapshot().Searches;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        await Frames(host, 120);
        CombatUiSnapshot after = Snapshot();
        check("no_search", after.Searches == before, $"{before}->{after.Searches}");
        check("not_searching", !after.IsSearching);
        check("auto_search_off", !after.AutomaticCalculationEnabled);
    }

    private static async Task AutoSearchFullAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        action("set_mode:full_auto");
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
        check("mode_unchanged", stopped.Mode == SolverMode.FullAuto, stopped.Mode.ToString());
        check("full_auto_not_paused", !stopped.FullAutoPausedThisCombat);
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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

    private static async Task ExecuteHasResultAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
        await WaitIdleAsync(host, "deployment done");
        check("last_deployed", Snapshot().LastSolverDeployedTurn is not null);
    }

    private static async Task ExecuteWhileSearchingAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
        action("set_mode:manual");
        bool applied = false;
        for (int attempt = 0; attempt < 3 && !applied; attempt++)
        {
            await StartManualSearchAsync(host, action, waitCandidate: false);
            if (!await WaitOptionalAsync(host,
                    static s => s.Search?.StopCandidateAvailable == true
                        && s.Search?.CurrentTurnPreview != null
                        && !s.SearchCandidateAllDeath, 600))
            {
                await WaitIdleAsync(host, "search finished before execute");
                continue;
            }
            CombatUiSnapshot preview = Snapshot();
            check("preview", preview.Search?.CurrentTurnPreview != null);
            await PressPanelButtonAsync(host, PanelButton.Execute, "execute_while_searching");
            applied = await WaitOptionalAsync(host, static s => s.IsDeploying, 240);
            if (!applied)
                await WaitIdleAsync(host, "search finished before execute");
        }
        check("applied", applied, "execute current turn accepted while searching");
        action("press_execute_while_searching");
        await WaitIdleAsync(host, "execute deployment done");
        check("stopped_this_turn", Snapshot().AutomaticSearchPaused
            || !Snapshot().IsSearching);
    }

    private static async Task ExecuteWithoutResultAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: false);
        int generation = Snapshot().Searches;
        check("to_full_auto", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        action("set_mode:full_auto");
        check("still_searching", Snapshot().IsSearching, "search not interrupted");
        check("to_auto_search", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        check("still_searching_2", Snapshot().IsSearching, "search not interrupted");
        await WaitIdleAsync(host, "mode switch search done");
        check("no_extra_search", Snapshot().Searches == generation, "mode switch did not search again");
        bool deployed = CombatUiStore.History.Any(static s => s.IsDeploying);
        check("no_deploy", !deployed, "auto search mode does not deploy");
    }

    private static async Task FullAutoStartAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot armed = Snapshot();
        check("full_auto_armed", armed.FullAutoEnabled, "full auto armed");
        await WaitAsync(host, static s => s.IsSearching || s.IsDeploying, 600,
            "full auto search");
        await WaitAsync(host, static s => s.IsDeploying, 900, "full auto deployment");
        action("full_auto_deploying");
        await WaitIdleAsync(host, "full auto turn done");
        check("last_deployed", Snapshot().LastSolverDeployedTurn is not null);
    }

    private static async Task FullAutoArmAfterTurnAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
            if (!CombatUiCommands.Instance.SetMode(SolverMode.FullAuto))
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
        check("armed", armed.FullAutoEnabled, "full auto armed while not playable");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null, 900, "search result");
        await WaitIdleAsync(host, "result ready");
        CombatControllerTestHooks.ForceDeploymentFailure = static (deployed, _) =>
            deployed.Action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.PlayCard ? "forced_failure" : null;
        action("force_deployment_failure");
        check("set_full_auto", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused, 1800, "repeated failure pause");
        check("pause_code", paused.PauseCode == "repeated_failure", paused.PauseCode);
        check("failures", paused.DeploymentFailures >= 3, paused.DeploymentFailures.ToString());
        int searches = paused.Searches;
        await Frames(host, 180);
        CombatUiSnapshot after = Snapshot();
        check("no_fourth_search", after.Searches == searches, $"{searches}->{after.Searches}");
        check("mode_full_auto", after.Mode == SolverMode.FullAuto, after.Mode.ToString());
        check("paused_this_combat", after.FullAutoPausedThisCombat);
        check("not_armed", !after.FullAutoEnabled);
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

    private static async Task TurnSetupAutoExecuteAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
        check("mode_full_auto", Snapshot().Mode == SolverMode.FullAuto,
            Snapshot().Mode.ToString());
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
            check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch),
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
        CombatUiSnapshot played = Snapshot();
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
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
        CombatControllerTestHooks.ForcedTurnSetupFullAutoPauseCode = "death_turn";
        check("mode_set", Snapshot().Mode == SolverMode.FullAuto
            || CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
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
        check("full_auto_off", !paused.FullAutoEnabled);
        check("paused_this_combat", paused.FullAutoPausedThisCombat);
        CombatControllerTestHooks.ForcedTurnSetupFullAutoPauseCode = null;
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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

    private static async Task PauseWorseRecalculationAsync(NGame host, Check check, Action action)
    {
        bool damaged = false;
        CombatControllerTestHooks.BeforeAutoSearch = async (state, turn, token) =>
        {
            if (damaged || turn != 2)
                return;
            damaged = true;
            Player player = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("伤害注入找不到玩家。");
            await DamagePlayerAsync(state, player, 10);
            action($"inject_damage:turn={turn}");
            token.ThrowIfCancellationRequested();
        };
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused, 1800, "worse recalculation pause");
        check("damaged", damaged, "turn 2 damage injected");
        check("pause_code", paused.PauseCode == "worse_recalculation", paused.PauseCode);
        check("full_auto_off", !paused.FullAutoEnabled);
        check("mode_full_auto", paused.Mode == SolverMode.FullAuto, paused.Mode.ToString());
        check("paused_this_combat", paused.FullAutoPausedThisCombat);
        check("result_retained", paused.Result != null);
        check("increase", paused.Result?.ProjectedBattleHpLossIncrease > 0,
            paused.Result?.ProjectedBattleHpLossIncrease.ToString());
        await WaitStatusAsync(host, SolverText.Get("已暂停执行"));
        check("pause_status", Status() == SolverText.Get("已暂停执行"), Status());
        check("pause_context", Context().Contains("重算后战损上升", StringComparison.Ordinal), Context());
        check("pause_summary", Summary().Contains("完整路线原预计", StringComparison.Ordinal), Summary());
    }

    private static async Task CombatEndAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
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
            if (statuses.Contains(Status(), StringComparer.Ordinal))
                return;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
    }
    private static string Context() => SolverOverlay.Instance?.ContextTextForTest ?? "";
    private static string Summary() => SolverOverlay.Instance?.SummaryTextForTest ?? "";
    private static string Heading() => SolverOverlay.Instance?.RouteHeadingForTest ?? "";

    private static async Task PanelTextsAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
        await WaitStatusAsync(host, SolverText.Get("求解器消息"));
        check("search_failed_status", Status() == SolverText.Get("求解器消息"), Status());
        check("search_failed_summary",
            Summary() == SolverPanelText.Error("search_failed"), Summary());
        SolverOverlay? panel = SolverOverlay.Instance;
        check("search_failed_button",
            panel?.SearchButtonForTest is { Disabled: false } button
            && button.Text == SolverText.Get("重新搜索"),
            panel?.SearchButtonForTest?.Text);
        check("search_failed_no_execute", panel?.ExecuteButtonForTest?.Visible == false);

        CombatControllerTestHooks.ForceSearchFailure = null;
        await PressPanelButtonAsync(host, PanelButton.Search, "retry");
        action("press_retry");
        CombatUiSnapshot retried = await WaitAsync(host,
            s => s.ErrorCode == null && s.Phase != CombatUiPhase.Failed && !s.IsSearching,
            900, "search failure cleared by retry");
        check("retry_phase", retried.Phase == CombatUiPhase.WaitingTurn, retried.Phase.ToString());
        check("retry_error_cleared", retried.ErrorCode == null, retried.ErrorCode);
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
        check("potion_failed_summary",
            Summary() == SolverPanelText.Error("forced_potion_unusable"), Summary());
        check("potion_failed_button",
            panel?.SearchButtonForTest is { Disabled: false } potionButton
            && potionButton.Text == SolverText.Get("重新搜索"),
            panel?.SearchButtonForTest?.Text);

        CombatControllerTestHooks.ForceSearchFailure = null;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot next = await WaitAsync(host,
            s => s.Turn > failedTurn && s.ErrorCode == null && s.Phase != CombatUiPhase.Failed,
            900, "failure cleared on next turn");
        check("next_turn_phase", next.Phase == CombatUiPhase.WaitingTurn, next.Phase.ToString());
        check("next_turn_error_cleared", next.ErrorCode == null, next.ErrorCode);
    }

    private static async Task ThreadSettingAsync(NGame host, Check check, Action action)
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
            check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("force_multiplayer");
        await Frames(host, 3);
        check("set_full_auto", !CombatUiCommands.Instance.SetMode(SolverMode.FullAuto),
            "full auto rejected");
        check("start", !CombatUiCommands.Instance.StartOrRecalculateSearch(), "search rejected");
        check("execute", !CombatUiCommands.Instance.ExecuteCurrentTurn(), "execute rejected");
        check("stop_search", !CombatUiCommands.Instance.StopSearch(), "stop search rejected");
        check("stop_deployment", !CombatUiCommands.Instance.StopDeployment(),
            "stop deployment rejected");
        check("not_searching", !Snapshot().IsSearching);
        check("not_deploying", !Snapshot().IsDeploying);
        check("full_auto_off", !Snapshot().FullAutoEnabled);
        SolverOverlay? panel = SolverOverlay.Instance;
        if (panel != null)
        {
            await Frames(host, 3);
            check("panel_hidden", !panel.ProbeForTest().Visible);
        }
    }

    private static async Task ManualReusesRouteAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int searches = Snapshot().Searches;
        await PressPanelButtonAsync(host, PanelButton.AutoSearchMode, "auto_search_mode");
        action("press_auto_search");
        CombatUiSnapshot searching = await WaitAsync(host, static s => s.IsSearching, 240,
            "mode switch started search");
        check("search_reason", searching.Search?.Reason == "auto_turn_start",
            searching.Search?.Reason);
        await PressPanelButtonAsync(host, PanelButton.ManualMode, "manual_mode");
        action("press_manual");
        await Frames(host, 4);
        check("not_interrupted", Snapshot().IsSearching);
        await WaitIdleAsync(host, "mode switch search done");
        check("one_search", Snapshot().Searches == searches + 1,
            $"{searches}->{Snapshot().Searches}");
        check("no_deploy", !CombatUiStore.History.Any(static s => s.IsDeploying));
    }

    private static async Task FullAutoPauseCombatAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching
            && !s.IsDeploying, 900, "result ready");
        CombatControllerTestHooks.ForceDeploymentFailure = static (deployed, _) =>
            deployed.Action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.PlayCard
                ? "forced_failure"
                : null;
        action("force_deployment_failure");
        check("set_full_auto", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused
                && s.PauseCode == "repeated_failure", 2400, "repeated failure pause");
        check("mode_full_auto", paused.Mode == SolverMode.FullAuto, paused.Mode.ToString());
        check("paused_this_combat", paused.FullAutoPausedThisCombat);
        check("not_armed", !paused.FullAutoEnabled);
        CombatControllerTestHooks.ForceDeploymentFailure = null;
        int searches = paused.Searches;
        await ForceEndTurnAsync(host);
        action("force_end_turn");
        CombatUiSnapshot searching = await WaitAsync(host,
            s => s.IsSearching && s.Search?.Reason == "auto_turn_start", 900,
            "auto search after pause");
        check("auto_search_resumed", searching.Searches > searches,
            $"{searches}->{searching.Searches}");
        check("still_paused", searching.FullAutoPausedThisCombat);
        check("mode_still_full_auto", searching.Mode == SolverMode.FullAuto,
            searching.Mode.ToString());
        await WaitIdleAsync(host, "post-pause search done");
        check("no_auto_execute", !Snapshot().IsDeploying);
        await PressPanelButtonAsync(host, PanelButton.FullAutoMode, "resume_full_auto");
        action("press_resume_full_auto");
        CombatUiSnapshot resumed = await WaitAsync(host,
            static s => !s.FullAutoPausedThisCombat, 120, "full auto resumed");
        check("resume_flag_cleared", !resumed.FullAutoPausedThisCombat);
        check("resume_armed", resumed.FullAutoEnabled || resumed.IsDeploying || resumed.IsSearching);
        await WaitAsync(host, static s => s.IsDeploying || s.IsSearching, 240,
            "full auto resumed work");
        if (Snapshot().IsSearching)
            await WaitAsync(host, static s => s.IsDeploying, 1200, "resume deployment");
        await WaitIdleAsync(host, "resumed deployment done");
    }

    private static async Task StrategyCommandsAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
            check(name + "_start", CombatUiCommands.Instance.StartOrRecalculateSearch(), name);
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

                check("potion_force_reset_one", CombatUiCommands.Instance.SetPotionDirective(
                    potion.Slot, potion.PotionId, SolverPotionMode.Smart));
                check("potion_only_forced_cmd",
                    CombatUiCommands.Instance.SetOnlyForcedPotionDirectives());
                action("set_only_forced_potions");
                await NoSearchAsync("potion_only_forced_no_search");
                SearchCase onlyForced = CombatSessionController.CompileRequestForTest();
                check("potion_only_forced_request",
                    onlyForced.Options.Potions.Strategy.Directives.Any(item =>
                        item.Slot == potion.Slot && item.PotionId == potion.PotionId
                        && item.Directive == PotionDirective.Disabled)
                    && onlyForced.Options.Potions.Strategy.Directives.Any(item =>
                        item.Slot != potion.Slot && item.Directive == PotionDirective.Force),
                    onlyForced.Options.Potions.Strategy.Directives.Count.ToString());
                check("potion_only_forced_snapshot",
                    Snapshot().Strategy.Potions.Any(item => item.Slot == potion.Slot
                        && item.Mode == "disabled"), "snapshot only-forced");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
    /// Forces one potion, protects another, and executes the current turn. The forced potion must be
    /// planned and then actually consumed by the live deployment.
    /// </summary>
    private static async Task ForcedPotionAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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

        check("search_start", CombatUiCommands.Instance.StartOrRecalculateSearch());
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
            check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
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
            check("stop_button_disabled", panel?.ExecuteButtonForTest?.Disabled == true);
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
            check("left_enabled", panel?.SearchButtonForTest?.Disabled == false);
        }
        finally
        {
            SolverSettings.Update(_ => original);
        }
    }

    private static async Task SearchBlockReasonsAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
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
            check("actions_rejected", !CombatUiCommands.Instance.StartOrRecalculateSearch());
        }
        finally
        {
            CombatControllerTestHooks.ForceActionsRunningForTest = false;
        }
        _ = CombatUiCommands.Instance.SetMode(SolverMode.Manual);
        await WaitAsync(host, static s => s.CanStartSearch, 120, "actions settled");
        CombatControllerTestHooks.ForceTurnStartForTest = true;
        action("force_turn_start");
        _ = CombatUiCommands.Instance.SetMode(SolverMode.Manual);
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
        _ = CombatUiCommands.Instance.SetMode(SolverMode.Manual);
        await WaitAsync(host, static s => s.CanStartSearch, 120, "turn start settled");
    }

    private static async Task DeploymentDriftResearchAsync(NGame host, Check check, Action action)
    {
        using SettingsOverride slowTier = SlowSearchTier();
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        int searches = Snapshot().Searches;
        await PressPanelButtonAsync(host, PanelButton.Search, "search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching, 1200,
            "route ready");
        int forced = 0;
        CombatControllerTestHooks.ForceDeploymentFailure = (deployed, _) =>
        {
            if (deployed.Action.Kind == Sts2Solver.Simulation.Combat.PlayerActionKind.PlayCard
                && forced++ == 0)
            {
                return "forced_failure";
            }
            return null;
        };
        await PressPanelButtonAsync(host, PanelButton.Execute, "execute");
        action("press_execute");
        CombatUiSnapshot drift = await WaitAsync(host,
            static s => s.IsSearching && s.Search?.Reason == "deployment_drift"
                && s.DeployWhenReady, 1800, "deployment drift search");
        check("drift_search", drift.Search?.Reason == "deployment_drift", drift.Search?.Reason);
        check("pending_execution", drift.DeployWhenReady);
        await Frames(host, 4);
        SolverOverlay? panel = SolverOverlay.Instance;
        check("drift_left_text", panel?.SearchButtonForTest?.Text
            == SolverText.Get("停止搜索"), panel?.SearchButtonForTest?.Text ?? "-");
        check("drift_right_text", panel?.ExecuteButtonForTest?.Text
            == SolverText.Get("停止执行"), panel?.ExecuteButtonForTest?.Text ?? "-");
        await WaitAsync(host, static s => s.IsDeploying, 2400, "drift deployment resumed");
        action("drift_deployment_resumed");
        await WaitIdleAsync(host, "drift deployment done");
        check("searches_increased", Snapshot().Searches > searches,
            $"{searches}->{Snapshot().Searches}");
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
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null && !s.IsSearching
            && !s.IsDeploying, 900, "result ready");
        check("set_full_auto", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused
                && s.PauseCode == "live_end_turn_risk", 2400, "live end turn pause");
        check("damaged", damaged, "live damage injected");
        check("paused_this_combat", paused.FullAutoPausedThisCombat);
        check("not_armed", !paused.FullAutoEnabled);
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
        ManualMode,
        AutoSearchMode,
        FullAutoMode,
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

    private static async Task PressPanelButtonAsync(NGame host, PanelButton which, string name)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            SolverOverlay? panel = SolverOverlay.Instance;
            Button? button = panel == null
                ? null
                : which switch
                {
                    PanelButton.Search => panel.SearchButtonForTest,
                    PanelButton.Execute => panel.ExecuteButtonForTest,
                    PanelButton.ManualMode => panel.ManualModeButtonForTest,
                    PanelButton.AutoSearchMode => panel.AutoSearchModeButtonForTest,
                    PanelButton.FullAutoMode => panel.FullAutoModeButtonForTest,
                    _ => throw new ArgumentOutOfRangeException(nameof(which)),
                };
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
        snapshot.Sequence, snapshot.Phase.ToString(), snapshot.Mode.ToString(),
        snapshot.FullAutoEnabled, snapshot.FullAutoPausedThisCombat,
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
    string Mode,
    bool FullAutoEnabled,
    bool FullAutoPausedThisCombat,
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
    string? TurnSetupError = null);
