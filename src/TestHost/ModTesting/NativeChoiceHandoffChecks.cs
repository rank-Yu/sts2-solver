using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2Solver;

internal static partial class CombatControllerChecks
{
    private static async Task DeploymentChoiceHandoffAsync(NGame host, Check check,
        Action action, string scenario)
    {
        NativeChoiceRequest? request = null;
        CombatControllerTestHooks.ChoiceHandedOffForTest = value => request = value;
        if (scenario == "deployment_card_choice_handoff")
            CombatControllerTestHooks.ForceCardChoicePlanUnderflowOnce = true;
        else
            CombatControllerTestHooks.ForceChoicePlanUnderflowOnce = true;
        check("run_started", CombatSessionController.ArmRunForTest());
        CombatUiSnapshot paused = await WaitAsync(host,
            _ => request != null && Snapshot().PauseCode == "choice_mismatch",
            2400, "native handoff and immediate pause");
        check("full_auto_off", !paused.CombatRunActive);
        check("deployment_released", !paused.IsDeploying);
        check("one_failure", paused.DeploymentFailures == 1, paused.DeploymentFailures.ToString());
        check("no_deviation", paused.Deviations == 0);
        check("selector_released", RunManager.Instance.CardSelectorStack.GetSelector(request!.Player) == null);
        check("manual_pending", !request!.Completion!.IsCompleted);
        await Frames(host, 30);
        check("no_automatic_answer", !request.Completion.IsCompleted);

        if (scenario.EndsWith("_exit", StringComparison.Ordinal))
        {
            action("exit_with_manual_choice");
            await host.ReturnToMainMenu();
            await ExpectChoiceCanceledAsync(request.Completion, check);
            NativeChoiceHandoff.CancelForSceneExit();
            check("run_exited", !RunManager.Instance.IsInProgress);
            // An unavailable surface must cancel, never fabricate an empty/minimum selection.
            await ExpectChoiceCanceledAsync(NativeChoiceHandoff.SelectAsync(request.Player,
                request.Options, request.MinSelect, request.MaxSelect, "after_scene_exit"), check);
        }
        else
        {
            action("choose_last_option_manually");
            await SubmitHandoffAsync(host, request, check);
            if (scenario == "deployment_choice_handoff")
                await WaitAsync(host, _ => request.Player.PlayerCombatState is
                    { Phase: PlayerTurnPhase.Play, TurnNumber: >= 2 }, 1200, "manual choice advances native turn");
            else
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions()
                    .WaitAsync(TimeSpan.FromSeconds(10));
            check("no_second_failure", Snapshot().DeploymentFailures == 1);
            check("full_auto_stays_off", !Snapshot().CombatRunActive);
        }
    }

    private static async Task ChoiceHandoffSurfaceAsync(NGame host, Check check,
        Action action, string scenario)
    {
        CombatSessionController.LeaveRunForTest(autoSearch: false);
        CombatState state = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state)!;
        bool grid = scenario.Contains("_grid", StringComparison.Ordinal);
        CardModel[] options = (grid ? player.PlayerCombatState!.DrawPile.Cards
            : player.PlayerCombatState!.Hand.Cards).Take(3).ToArray();
        check("multiple_options", options.Length >= 2);
        NativeChoiceRequest? request = null;
        CombatControllerTestHooks.ChoiceHandedOffForTest = value => request = value;
        Task<IEnumerable<CardModel>> selection =
            NativeChoiceHandoff.SelectAsync(player, options, 1, 1, scenario);
        check("surface_captured", request != null);
        check("correct_surface", request!.Surface == (grid
            ? NativeChoiceSurfaceKind.SimpleGrid : NativeChoiceSurfaceKind.Hand));
        await Frames(host, 30);
        check("manual_pending", !selection.IsCompleted);

        if (scenario.EndsWith("_exit", StringComparison.Ordinal))
        {
            action("return_to_main_menu");
            await host.ReturnToMainMenu();
            await ExpectChoiceCanceledAsync(selection, check);
            NativeChoiceHandoff.CancelForSceneExit();
            NativeChoiceHandoff.CancelForSceneExit();
            check("no_run", !RunManager.Instance.IsInProgress);
        }
        else if (scenario == "choice_handoff_combat_end")
        {
            action("end_combat_with_pending_choice");
            foreach (Creature enemy in state.Enemies.ToArray())
                await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), enemy, 100000,
                    ValueProp.Unblockable | ValueProp.Unpowered, (Creature?)null, null, null);
            await CombatManager.Instance.CheckWinCondition();
            await ExpectChoiceCanceledAsync(selection, check);
            check("combat_ended", !CombatManager.Instance.IsInProgress);
        }
        else if (scenario == "choice_handoff_reentry")
        {
            NativeChoiceHandoff.CancelForSceneExit();
            // The old cancellation's continuation may still be queued when the next request opens.
            Task<IEnumerable<CardModel>> next =
                NativeChoiceHandoff.SelectAsync(player, options, 1, 1, "next_handoff");
            await ExpectChoiceCanceledAsync(selection, check);
            await Frames(host, 2);
            check("next_still_pending", !next.IsCompleted);
            NativeChoiceHandoff.CancelForSceneExit();
            NativeChoiceHandoff.CancelForSceneExit();
            await ExpectChoiceCanceledAsync(next, check);
        }
        else
        {
            action("select_last_card");
            bool peek = scenario == "choice_handoff_hand_peek";
            if (peek)
            {
                NPlayerHand.Instance!.PeekButton.SetPeeking(true);
                check("peek_input_gate_enabled", NPlayerHand.Instance.PeekButton.IsPeeking);
            }
            await SubmitHandoffAsync(host, request, check);
            if (peek)
                check("peek_input_gate_cleared", !NPlayerHand.Instance!.PeekButton.IsPeeking);
            // A late exit callback must preserve the completed manual selection.
            Task<IEnumerable<CardModel>> unrelated = NativeChoiceHandoff.SelectAsync(
                player, options, 1, 1, "new_manual_choice");
            await SubmitHandoffAsync(host, request!, check);
            await unrelated;
            NativeChoiceHandoff.CancelForSceneExit();
            check("completed_choice_preserved", unrelated.IsCompletedSuccessfully);
        }
    }

    private static async Task SubmitHandoffAsync(NGame host, NativeChoiceRequest request, Check check)
    {
        CardModel[] selected = request.Options.TakeLast(Math.Max(1, request.MinSelect)).ToArray();
        using (NativeChoiceSurfaceLock surface =
            await NativeChoiceSurface.WaitAndLockAsync(host, request, CancellationToken.None))
        {
            await NativeChoiceSurface.SelectAsync(host, surface, request, selected, CancellationToken.None);
        }
        var result = await ((Task<IEnumerable<CardModel>>)request.Completion!)
            .WaitAsync(TimeSpan.FromSeconds(10));
        check("manual_selection_preserved", result.SequenceEqual(selected));
    }

    private static async Task ExpectChoiceCanceledAsync(Task selection, Check check)
    {
        try
        {
            await selection.WaitAsync(TimeSpan.FromSeconds(10));
            check("choice_canceled", false, "request completed with a selection");
        }
        catch (OperationCanceledException)
        {
            check("choice_canceled", selection.IsCanceled);
        }
    }
}
