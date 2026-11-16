using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using Sts2Solver.Autoplay;
using Sts2Solver.Simulation.Combat;
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;
using PlayerActionKind = Sts2Solver.Simulation.Combat.PlayerActionKind;

namespace Sts2Solver;

internal static partial class CombatControllerChecks
{
    private static async Task ResumeRouteAsync(NGame host, Check check, Action action, string scenario)
    {
        SolverSettingsData original = SolverSettings.Current;
        List<int> deployed = [];
        try
        {
            NModalContainer.Instance?.Clear();
            check("settings", SolverSettings.Update(settings => settings with
            {
                Ui = settings.Ui with { ShowGuide = scenario == "resume_manual_guide" },
                Automation = settings.Automation with
                {
                    AutoSearch = false, AutoExecute = false, InterActionDelaySeconds = 0,
                },
            }));
            check("search", CombatUiCommands.Instance.StartSearch());
            await WaitAsync(host, static s => s.CurrentTurnRouteReady && !s.IsSearching, 2400, "route ready");
            await Frames(host, 3);
            CombatRoutePlan plan = CombatSessionController.RetainedPlanForTest!;
            int turn = Snapshot().Turn;
            int searches = Snapshot().Searches;
            ReplayedAction[] turnActions = CombatRouteDecision.CurrentTurnActions(plan.Actions, turn);
            check("fixture_has_steps", turnActions.Length >= 3, turnActions.Length.ToString());
            check("guide_setting", (Snapshot().Guide != null) == (scenario == "resume_manual_guide"));

            bool stopNext = true;
            CombatControllerTestHooks.ForceDeploymentFailure = (planned, _) =>
            {
                deployed.Add(planned.Index);
                if (stopNext)
                {
                    stopNext = false;
                    check("stop_requested", CombatUiCommands.Instance.StopExecution());
                }
                return null;
            };
            if (scenario == "resume_potion")
            {
                int potionIndex = Array.FindIndex(turnActions,
                    static step => step.Action.Kind == PlayerActionKind.UsePotion);
                check("potion_fixture", potionIndex >= 0, string.Join(",", turnActions.Select(
                    static step => step.Action.Kind.ToString())));
                for (int index = 0; index <= potionIndex; index++)
                    await PlayRouteStepManuallyAsync(host, turnActions[index], check, false, false);
                await Frames(host, 4);
                check("potion_route_ready", Snapshot().CurrentTurnRouteReady);
                check("potion_resume", CombatUiCommands.Instance.ExecuteCurrentTurn());
                await WaitAsync(host, s => !s.IsDeploying && (s.AutomationStoppedThisTurn
                    || s.Turn > turn || s.Phase == CombatUiPhase.Completed), 1200, "potion resume");
                check("potion_next_action", deployed.SequenceEqual(new[] { potionIndex + 1 }),
                    string.Join(",", deployed));
                check("potion_no_deviation", Snapshot().Deviations == 0 && Snapshot().DeploymentFailures == 0);
                check("potion_no_search", Snapshot().Searches == searches);
                action("manual_potion_resume_verified");
                return;
            }
            check("execute", CombatUiCommands.Instance.ExecuteCurrentTurn());
            await WaitAsync(host, static s => !s.IsDeploying && s.AutomationStoppedThisTurn,
                1200, "first action settled");
            check("one_action_executed", deployed.SequenceEqual(new[] { 0 }));
            check("retained_same_plan", ReferenceEquals(plan, CombatSessionController.RetainedPlanForTest));
            check("cursor_after_stop", CombatSessionController.RouteIndexForTest == 1);
            check("ready_after_stop", Snapshot().CurrentTurnRouteReady);
            await Frames(host, 20);
            check("no_automatic_restart", deployed.Count == 1 && Snapshot().Searches == searches);
            await WaitPanelAppliedAsync(host);
            check("resume_button_enabled", SolverOverlay.Instance!.ExecuteTurnButtonForTest is { Disabled: false });
            if (scenario != "resume_manual_guide")
                check("execution_pause_status", Status() == SolverText.Get("ui.execution_paused"), Status());

            int manualCount = scenario switch
            {
                "resume_immediate" => 0,
                "resume_manual" or "resume_manual_guide" => 2,
                "resume_end_turn" => turnActions.Length - 2,
                _ => 1,
            };
            for (int index = 1; index <= manualCount; index++)
            {
                ReplayedAction planned = plan.Actions[index];
                check("manual_step_available", planned.Action.Kind != PlayerActionKind.EndTurn);
                await PlayRouteStepManuallyAsync(host, planned, check,
                    wrongTarget: scenario == "resume_wrong_target",
                    wrongChoice: scenario == "resume_wrong_choice",
                    wrongCard: scenario == "resume_wrong_card");
                await Frames(host, 4);
                if (scenario is "resume_wrong_target" or "resume_wrong_choice" or "resume_wrong_card")
                {
                    check("deviation_disables_resume", !Snapshot().CurrentTurnRouteReady);
                    check("deviation_hides_guide", Snapshot().Guide == null);
                    check("deviation_rejects_execute", !CombatUiCommands.Instance.ExecuteCurrentTurn());
                    check("no_extra_search", Snapshot().Searches == searches);
                    action("manual_deviation_rejected");
                    return;
                }
                check($"manual_progress_{index}", CombatSessionController.RouteIndexForTest == index + 1,
                    CombatSessionController.RouteIndexForTest.ToString());
                check($"manual_ready_{index}", Snapshot().CurrentTurnRouteReady);
                check($"manual_completed_{index}", Snapshot().Route?.CompletedActions == index + 1);
            }
            int next = manualCount + 1;
            stopNext = scenario != "resume_end_turn";
            check("resume", scenario == "resume_end_turn"
                ? CombatUiCommands.Instance.ExecuteCurrentTurn()
                : CombatUiCommands.Instance.ExecuteCombat());
            await WaitAsync(host, s => !s.IsDeploying && (s.AutomationStoppedThisTurn || s.Turn > turn
                || s.Phase == CombatUiPhase.Completed), 1800, "resumed action settled");
            check("correct_next_action", deployed.Count >= 2 && deployed[1] == next,
                string.Join(",", deployed));
            check("no_repeated_actions", deployed.Distinct().Count() == deployed.Count);
            check("same_search", Snapshot().Searches == searches);
            check("no_deviation", Snapshot().Deviations == 0 && Snapshot().DeploymentFailures == 0);
            if (scenario != "resume_end_turn" && Snapshot().Phase != CombatUiPhase.Completed)
            {
                check("repeat_stop_ready", Snapshot().CurrentTurnRouteReady);
                check("repeat_stop_cursor", CombatSessionController.RouteIndexForTest == next + 1);
            }
            if (scenario == "resume_end_turn")
                check("turn_advanced", Snapshot().Turn > turn);
            action($"resume_verified manual={manualCount} deployed={string.Join(",", deployed)}");
        }
        finally
        {
            CombatControllerTestHooks.ForceDeploymentFailure = null;
            SolverSettings.Update(_ => original);
        }
    }

    private static async Task PlayRouteStepManuallyAsync(NGame host, ReplayedAction planned, Check check,
        bool wrongTarget, bool wrongChoice, bool wrongCard = false)
    {
        CombatState combat = CombatManager.Instance.DebugOnlyGetState()!;
        Player player = LocalContext.GetMe(combat)!;
        NativeCreature? target = planned.Action.Target switch
        {
            Limits.NoOwner => null,
            Limits.PlayerOwner => player.Creature,
            Limits.OstyOwner => player.Osty,
            _ => combat.Enemies.Single(enemy =>
                enemy.CombatId == planned.Before.Enemies[planned.Action.Target - Limits.FirstEnemyOwner].CombatId),
        };
        if (wrongTarget)
        {
            check("targeted_fixture", target != null && combat.Enemies.Count > 1);
            target = combat.Enemies.First(enemy => !ReferenceEquals(enemy, target));
        }
        TaskCompletionSource<GameAction> captured = new();
        void Capture(GameAction candidate)
        {
            if (candidate.OwnerId == player.NetId && candidate is PlayCardAction or UsePotionAction)
                captured.TrySetResult(candidate);
        }
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        executor.BeforeActionExecuted += Capture;
        try
        {
            if (planned.Action.Kind == PlayerActionKind.PlayCard)
            {
                CardModel card = player.PlayerCombatState!.Hand.Cards[planned.Action.HandIndex];
                if (wrongCard)
                    card = player.PlayerCombatState.Hand.Cards.First(other => !ReferenceEquals(other, card));
                check("manual_play", card.TryManualPlay(target));
            }
            else
            {
                PotionModel potion = player.GetPotionAtSlotIndex(planned.Action.PotionSlot)!;
                potion.EnqueueManualUse(target);
            }
            for (int frame = 0; frame < 120 && !captured.Task.IsCompleted; frame++)
                await CombatFrameWaiter.NextFrameAsync(host);
            check("native_action_captured", captured.Task.IsCompletedSuccessfully);
            GameAction native = captured.Task.Result;
            long answered = -1;
            bool chose = false;
            for (int frame = 0; frame < 1200 && !native.CompletionTask.IsCompleted; frame++)
            {
                if (CombatSessionController.ManualRouteChoiceForTest is { IsPending: true } request
                    && request.Sequence != answered)
                {
                    check("selection_blocks_execution", !CombatUiCommands.Instance.ExecuteCurrentTurn());
                    await Frames(host, 5);
                    NPlayerHand hand = NPlayerHand.Instance!;
                    check("hand_selection_open", hand.IsInCardSelection);
                    IReadOnlyList<CardModel> selected = CombatSessionController.ManualRouteChoiceCardsForTest!;
                    if (wrongChoice)
                    {
                        check("single_choice_fixture", selected.Count == 1);
                        selected = [request.Options.First(card => !ReferenceEquals(card, selected[0]))];
                    }
                    foreach (CardModel card in selected)
                    {
                        NCardHolder holder = Descendants(hand).OfType<NCardHolder>()
                            .Single(item => ReferenceEquals(item.CardModel, card));
                        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                    }
                    await Frames(host, 3);
                    NClickableControl confirm = hand.GetNode<NClickableControl>("%SelectModeConfirmButton");
                    confirm.EmitSignal(NClickableControl.SignalName.Released, confirm);
                    answered = request.Sequence;
                    chose = true;
                }
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            check("manual_action_settled", native.CompletionTask.IsCompletedSuccessfully);
            if (wrongChoice)
                check("wrong_choice_exercised", chose);
        }
        finally
        {
            executor.BeforeActionExecuted -= Capture;
        }
    }

    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child))
                yield return descendant;
        }
    }
}
