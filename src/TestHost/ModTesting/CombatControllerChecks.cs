// Headless controller scenarios. They drive the real ICombatUiCommands surface, wait on the
// published snapshots and write a compact assertion record for controller.json. No scenario
// bypasses the controller's command validation.
using System.Diagnostics;
using Sts2Solver.Autoplay;
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
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2Solver;

internal static class CombatControllerChecks
{
    internal static async Task<CombatControllerScenarioResult> RunAsync(NGame host,
        TestHost.OfflineRequest request, string scenario, string root, string resultDirectory)
    {
        _ = resultDirectory;
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
                case "stop_then_adopt":
                    await StopThenAdoptAsync(host, Check, Action);
                    break;
                case "stop_then_recalc":
                    await StopThenRecalcAsync(host, Check, Action);
                    break;
                case "adopt":
                    await AdoptAsync(host, Check, Action);
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
                case "full_auto_arm_solver_toggle":
                    await FullAutoArmSolverToggleAsync(host, Check, Action);
                    break;
                case "repeated_failure":
                    await RepeatedFailureAsync(host, Check, Action);
                    break;
                case "turn_setup_wait":
                    await TurnSetupWaitAsync(host, Check, Action);
                    break;
                case "pause_death_turn":
                    await PauseDeathTurnAsync(host, Check, Action);
                    break;
                case "pause_worse_recalculation":
                    await PauseWorseRecalculationAsync(host, Check, Action);
                    break;
                case "solver_toggle":
                    await SolverToggleAsync(host, Check, Action);
                    break;
                case "combat_end":
                    await CombatEndAsync(host, Check, Action);
                    break;
                case "multiplayer_gate":
                    await MultiplayerGateAsync(host, Check, Action);
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
            CombatSessionController.ForceMultiplayerForTest = false;
        }
        CombatPilotResult summary = CombatSessionController.LastCompletedResultForTesting
            ?? CombatSessionController.CapturePilotResultForTest();
        string status = failure == null ? "ok" : "failed";
        return new CombatControllerScenarioResult(
            1, scenario, root, status, failure, [.. checks], [.. actions],
            [.. CombatUiStore.History.Select(SnapshotRecord)],
            summary.Turns, summary.PlayerHp, summary.PlayerMaxHp, summary.Searches,
            summary.ReusedTurns, summary.Deviations, summary.DeploymentFailures,
            summary.Status, summary.Outcome);
    }

    private static async Task ManualStartStopAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "set manual");
        action("set_mode:manual");
        await WaitIdleAsync(host, "manual idle");
        CombatUiSnapshot idle = Snapshot();
        check("manual_auto_search_off", !idle.AutomaticCalculationEnabled && !idle.FullAutoEnabled);
        check("can_start", idle.CanStartSearch, "can start");
        check("start", CombatUiCommands.Instance.StartOrRecalculateSearch(), "manual search");
        action("start_search");
        CombatUiSnapshot searching = await WaitAsync(host,
            static s => s.IsSearching, 240, "manual search started");
        check("search_reason", searching.Search?.Reason == "manual", searching.Search?.Reason);
        check("search_mode", searching.Mode == SolverMode.Manual, searching.Mode.ToString());
        check("stop", CombatUiCommands.Instance.StopSearch(), "stop search");
        action("stop_search");
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
        await StartManualSearchAsync(host, action, waitCandidate: true);
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

    private static async Task StopThenAdoptAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: true);
        check("stop", CombatUiCommands.Instance.StopSearch(), "stop");
        action("stop_search");
        await WaitAsync(host, static s => !s.IsSearching, 300, "search stopped");
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => s.StoppedRouteAvailable, 240, "stopped route available");
        check("stopped_available", stopped.StoppedRouteAvailable);
        check("adopt", CombatUiCommands.Instance.AdoptCurrentRoute(), "adopt stopped route");
        action("adopt_stopped");
        CombatUiSnapshot adopted = await WaitAsync(host,
            static s => s.Result != null && !s.StoppedRouteAvailable, 240, "stopped route adopted");
        check("result_present", adopted.Result != null);
        check("can_execute", adopted.CanExecuteCurrentTurn);
        check("no_new_search", adopted.Searches == stopped.Searches, "no new search");
    }

    private static async Task StopThenRecalcAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: true);
        check("stop", CombatUiCommands.Instance.StopSearch(), "stop");
        action("stop_search");
        await WaitAsync(host, static s => !s.IsSearching, 300, "search stopped");
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => s.StoppedRouteAvailable, 240, "stopped route available");
        check("recalc", CombatUiCommands.Instance.StartOrRecalculateSearch(), "recalculate");
        action("recalculate");
        CombatUiSnapshot started = await WaitAsync(host, static s => s.IsSearching,
            240, "recalculate started");
        check("stopped_cleared", !started.StoppedRouteAvailable);
        check("reason_manual", started.Search?.Reason == "manual", started.Search?.Reason);
        await WaitIdleAsync(host, "recalculate done");
        check("searches_increased", Snapshot().Searches > stopped.Searches);
        check("no_reuse", Snapshot().ReusedTurns == stopped.ReusedTurns);
    }

    private static async Task AdoptAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: true);
        check("adopt", CombatUiCommands.Instance.AdoptCurrentRoute(), "adopt");
        action("adopt");
        await WaitIdleAsync(host, "adopt done");
        CombatUiSnapshot adopted = Snapshot();
        check("result_present", adopted.Result != null);
        check("can_execute", adopted.CanExecuteCurrentTurn);
        bool deployed = CombatUiStore.History.Any(static s => s.IsDeploying);
        check("no_deploy", !deployed);
    }

    private static async Task ExecuteHasResultAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await WaitAsync(host, static s => s.Result != null, 900, "latest result");
        await WaitIdleAsync(host, "idle with result");
        CombatUiSnapshot ready = Snapshot();
        check("has_result", ready.Result != null, "latest result");
        check("can_execute", ready.CanExecuteCurrentTurn);
        check("execute", CombatUiCommands.Instance.ExecuteOrApplyCurrentTurn(), "execute");
        action("execute");
        await WaitAsync(host, static s => s.IsDeploying, 120, "deploying");
        CombatUiSnapshot deploying = Snapshot();
        check("result_cleared", deploying.Result == null, "result cleared on deployment");
        await WaitIdleAsync(host, "deployment done");
        check("last_deployed", Snapshot().LastSolverDeployedTurn is not null);
    }

    private static async Task ExecuteWhileSearchingAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        bool applied = false;
        for (int attempt = 0; attempt < 3 && !applied; attempt++)
        {
            await StartManualSearchAsync(host, action, waitCandidate: false);
            await WaitAsync(host, static s => s.CanApplyCurrentTurn, 600,
                "current turn preview");
            CombatUiSnapshot preview = Snapshot();
            check("preview", preview.Search?.CurrentTurnPreview != null);
            if (!CombatUiCommands.Instance.ExecuteOrApplyCurrentTurn())
            {
                await WaitIdleAsync(host, "search finished before apply");
                continue;
            }
            action("apply_current_turn");
            applied = await WaitOptionalAsync(host, static s => s.IsDeploying, 120);
        }
        check("applied", applied, "apply current turn command accepted");
        await WaitIdleAsync(host, "apply deployment done");
    }

    private static async Task ExecuteWithoutResultAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.Manual), "manual");
        action("set_mode:manual");
        check("disable", CombatUiCommands.Instance.SetSolverEnabled(false), "disable");
        action("solver_off");
        check("enable", CombatUiCommands.Instance.SetSolverEnabled(true), "enable");
        action("solver_on");
        await WaitIdleAsync(host, "idle without result");
        CombatUiSnapshot idle = Snapshot();
        check("no_result", idle.Result == null, "result cleared");
        check("execute", CombatUiCommands.Instance.ExecuteOrApplyCurrentTurn(), "execute without result");
        action("execute_without_result");
        CombatUiSnapshot started = await WaitAsync(host, static s => s.IsSearching, 240,
            "deploy search started");
        check("deploy_when_ready", started.DeployWhenReady);
        await WaitAsync(host, static s => s.IsDeploying, 600, "deploy search deployment");
        await WaitIdleAsync(host, "deployment done");
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

    private static async Task FullAutoArmSolverToggleAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await WaitIdleAsync(host, "auto search idle");
        check("disable", CombatUiCommands.Instance.SetSolverEnabled(false), "disable");
        action("solver_off");
        CombatUiSnapshot disabled = Snapshot();
        check("disabled", disabled.SolverDisabled);
        check("set_full_auto", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        CombatUiSnapshot stillDisabled = Snapshot();
        check("not_armed_while_disabled", !stillDisabled.FullAutoEnabled);
        check("enable", CombatUiCommands.Instance.SetSolverEnabled(true), "enable");
        action("solver_on");
        CombatUiSnapshot armed = Snapshot();
        check("armed_after_enable", armed.FullAutoEnabled, "full auto armed after enable");
        await WaitAsync(host, static s => s.IsSearching, 600, "full auto search after enable");
        await WaitAsync(host, static s => s.IsDeploying, 900, "full auto deployment after enable");
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
        check("mode_auto_search", after.Mode == SolverMode.AutoSearch, after.Mode.ToString());
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

    private static async Task PauseDeathTurnAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.FullAuto), "full auto");
        action("set_mode:full_auto");
        CombatUiSnapshot paused = await WaitAsync(host,
            static s => s.Phase == CombatUiPhase.Paused, 900, "death pause");
        check("pause_code", paused.PauseCode == "death_turn", paused.PauseCode);
        check("full_auto_off", !paused.FullAutoEnabled);
        check("mode_auto_search", paused.Mode == SolverMode.AutoSearch, paused.Mode.ToString());
        check("result_death_turn", paused.Result?.DeathTurn == paused.Result?.StartTurn,
            $"{paused.Result?.DeathTurn}/{paused.Result?.StartTurn}");
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
        check("mode_auto_search", paused.Mode == SolverMode.AutoSearch, paused.Mode.ToString());
        check("increase", paused.Result?.ProjectedBattleHpLossIncrease > 0,
            paused.Result?.ProjectedBattleHpLossIncrease.ToString());
    }

    private static async Task SolverToggleAsync(NGame host, Check check, Action action)
    {
        check("mode_set", CombatUiCommands.Instance.SetMode(SolverMode.AutoSearch), "auto search");
        action("set_mode:auto_search");
        await StartManualSearchAsync(host, action, waitCandidate: false);
        check("disable", CombatUiCommands.Instance.SetSolverEnabled(false), "disable");
        action("solver_off");
        CombatUiSnapshot disabled = await WaitAsync(host, static s => !s.IsSearching, 240,
            "disabled idle");
        check("disabled", disabled.SolverDisabled);
        check("result_cleared", disabled.Result == null);
        check("full_auto_off", !disabled.FullAutoEnabled);
        check("enable", CombatUiCommands.Instance.SetSolverEnabled(true), "enable");
        action("solver_on");
        CombatUiSnapshot enabled = await WaitAsync(host, static s => s.IsSearching, 300,
            "auto search after enable");
        check("enabled", !enabled.SolverDisabled);
        check("auto_search", enabled.Search?.Reason == "auto_turn_start", enabled.Search?.Reason);
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

    private static async Task MultiplayerGateAsync(NGame host, Check check, Action action)
    {
        CombatSessionController.ForceMultiplayerForTest = true;
        action("force_multiplayer");
        await Frames(host, 3);
        check("set_mode", !CombatSessionController.SetFullAuto(true), "full auto rejected");
        check("start", !CombatUiCommands.Instance.StartOrRecalculateSearch(), "search rejected");
        check("execute", !CombatUiCommands.Instance.ExecuteOrApplyCurrentTurn(), "execute rejected");
        check("adopt", !CombatUiCommands.Instance.AdoptCurrentRoute(), "adopt rejected");
        check("stop", !CombatUiCommands.Instance.StopSearch(), "stop rejected");
        check("not_searching", !Snapshot().IsSearching);
        check("not_deploying", !Snapshot().IsDeploying);
        check("full_auto_off", !Snapshot().FullAutoEnabled);
        SolverPanelNode? panel = SolverPanelNode.Instance;
        if (panel != null)
        {
            await Frames(host, 3);
            check("panel_hidden", !panel.ProbeForTest().Visible);
        }
    }

    private static async Task StartManualSearchAsync(NGame host, Action action, bool waitCandidate)
    {
        if (Snapshot().IsSearching)
        {
            await WaitAsync(host, static s => !s.IsSearching, 900, "existing search done");
        }
        if (!CombatUiCommands.Instance.StartOrRecalculateSearch())
            throw new InvalidDataException("manual search rejected");
        action("start_search");
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
        snapshot.FullAutoEnabled, snapshot.AutomaticCalculationEnabled,
        snapshot.AutomaticSearchPaused, snapshot.AutomaticSearchPausedTurn,
        snapshot.IsSearching, snapshot.IsDeploying,
        snapshot.CanStartSearch, snapshot.CanStopSearch, snapshot.CanExecuteCurrentTurn,
        snapshot.CanApplyCurrentTurn, snapshot.CanAdoptCurrentRoute,
        snapshot.StoppedRouteAvailable, snapshot.HasRetainedRoute,
        snapshot.PauseCode, snapshot.ErrorCode, snapshot.Searches, snapshot.ReusedTurns,
        snapshot.Deviations, snapshot.DeploymentFailures,
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
    bool AutomaticCalculationEnabled,
    bool AutomaticSearchPaused,
    int? AutomaticSearchPausedTurn,
    bool IsSearching,
    bool IsDeploying,
    bool CanStartSearch,
    bool CanStopSearch,
    bool CanExecuteCurrentTurn,
    bool CanApplyCurrentTurn,
    bool CanAdoptCurrentRoute,
    bool StoppedRouteAvailable,
    bool HasRetainedRoute,
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
    string Outcome);
