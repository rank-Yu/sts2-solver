using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Solver.Autoplay;

namespace Sts2Solver;

internal static partial class CombatControllerChecks
{
    private static async Task AutoExecuteControlsAsync(NGame host, Check check, Action action)
    {
        await Frames(host, 12);
        SolverOverlay panel = SolverOverlay.Instance!;
        check("disabled_without_search", panel.AutoExecuteSwitchForTest is { Disabled: true, ButtonPressed: false });
        check("reject_without_search", !CombatUiCommands.Instance.SetAutoExecute(true));
        check("no_extra_copy", panel.AutoExecuteSwitchForTest!.TooltipText.Length == 0);

        foreach (float width in new[] { 560f, 820f })
        {
            panel.ResizeForTest(new Vector2(width, panel.PanelForTest!.Size.Y));
            await Frames(host, 12);
            Control searchGroup = panel.SearchButtonForTest!.GetParent<Control>();
            Control executeGroup = panel.ExecuteTurnButtonForTest!.GetParent<Control>();
            check($"search_group_{width}", panel.AutoSearchSwitchForTest!.GetParent().GetParent() == searchGroup);
            check($"execute_group_{width}", panel.AutoExecuteSwitchForTest!.GetParent().GetParent() == executeGroup);
            check($"execute_same_row_{width}",
                Math.Abs(panel.ExecuteTurnButtonForTest.GlobalPosition.Y
                    - panel.ExecuteCombatButtonForTest!.GlobalPosition.Y) < 1);
            check($"groups_within_panel_{width}",
                executeGroup.GetGlobalRect().End.X <= panel.PanelForTest.GetGlobalRect().End.X);
            check($"group_wrap_{width}", width == 560
                ? executeGroup.GlobalPosition.Y > searchGroup.GlobalPosition.Y
                : Math.Abs(executeGroup.GlobalPosition.Y - searchGroup.GlobalPosition.Y) < 1,
                $"panel={panel.PanelForTest.Size} search={searchGroup.GetGlobalRect()} execute={executeGroup.GetGlobalRect()}");
        }

        await PressPanelButtonAsync(host, PanelButton.AutoSearchOn, "enable_search");
        await WaitIdleAsync(host, "search complete before enabling execution");
        check("no_automatic_execution", !Snapshot().CombatRunActive && !Snapshot().IsDeploying);
        await PressPanelButtonAsync(host, PanelButton.AutoExecuteOn, "enable_execution");
        await WaitAsync(host, static s => s.IsDeploying, 600, "execute existing result");
        check("persisted_on", SolverSettingsStore.Load(SolverSettings.FilePath!,
            static _ => { }, static _ => { }).Current.Automation.AutoExecute);
        await PressPanelButtonAsync(host, PanelButton.AutoExecuteOff, "disable_execution");
        await WaitAsync(host, static s => !s.IsDeploying && !s.CombatRunActive, 600, "execution stopped");
        check("persisted_off", !SolverSettings.Current.Automation.AutoExecute);
        action("controls_and_layout_verified");
    }

    private static async Task AutoExecuteFullAsync(NGame host, Check check, Action action)
    {
        long deadline = System.Environment.TickCount64 + 180_000;
        while (CombatManager.Instance.IsInProgress && System.Environment.TickCount64 < deadline)
        {
            if (CombatSessionController.IsPaused || Snapshot().ErrorCode != null)
                throw new InvalidDataException($"automatic execution stopped: {Snapshot().PauseCode} {Snapshot().ErrorCode}");
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        await Frames(host, 5);
        CombatPilotResult summary = CombatSessionController.LastCompletedResultForTesting
            ?? throw new InvalidDataException("auto execution did not finish combat");
        check("victory", summary.Status == "victory", summary.Status);
        check("multiple_turns", summary.Turns >= 2, summary.Turns.ToString());
        check("no_deviation", summary.Deviations == 0 && summary.DeploymentFailures == 0);
        check("preference_retained", SolverSettings.Current.Automation.AutoExecute);
        action("combat_completed_without_execute_command");
    }

    private static async Task AutoExecuteStopSearchAsync(NGame host, Check check, Action action, bool setup)
    {
        if (!setup)
        {
            CombatControllerTestHooks.AutoStopOnCandidate = true;
            check("search_on", CombatUiCommands.Instance.SetAutoSearch(true));
            check("execute_on", CombatUiCommands.Instance.SetAutoExecute(true));
        }
        CombatUiSnapshot stopped = await WaitAsync(host,
            static s => !s.IsSearching && s.PauseCode == "stopped", 2400, "automatic search stopped");
        CombatControllerTestHooks.AutoStopOnCandidate = false;
        await Frames(host, 60);
        check("route_retained", stopped.Result != null && stopped.CurrentTurnRouteReady);
        check("preference_still_on", Snapshot().AutoExecuteEnabled);
        check("not_executed", !Snapshot().IsDeploying && !Snapshot().TurnSetupDriving
            && !Snapshot().CombatRunActive && Snapshot().ReusedTurns == 0);
        check("no_deploy_history", !CombatUiStore.History.Any(static s => s.IsDeploying || s.TurnSetupDriving));
        action("stop_keeps_route_without_execution");
    }

    private static async Task AutoExecuteDisableAsync(NGame host, Check check, Action action, string scenario)
    {
        await WaitAsync(host, static s => s.IsDeploying, 2400, "auto deployment");
        PanelButton button = scenario switch
        {
            "auto_execute_disable_search" => PanelButton.AutoSearchOff,
            "auto_execute_stop_execution" => PanelButton.Execute,
            _ => PanelButton.AutoExecuteOff,
        };
        await PressPanelButtonAsync(host, button, scenario);
        await WaitAsync(host, static s => !s.IsDeploying && !s.TurnSetupDriving
            && !s.CombatRunActive && !s.IsSearching, 1200, "soft stop");
        await Frames(host, 60);
        check("auto_execute_off", !Snapshot().AutoExecuteEnabled
            && !SolverSettings.Current.Automation.AutoExecute);
        check("not_restarted", !Snapshot().IsDeploying && !Snapshot().CombatRunActive);
        if (button == PanelButton.AutoSearchOff)
        {
            check("search_off", !Snapshot().AutoSearchEnabled);
            check("switch_disabled", SolverOverlay.Instance!.AutoExecuteSwitchForTest!.Disabled);
        }
        action("stopped_after_current_action");
    }
}
