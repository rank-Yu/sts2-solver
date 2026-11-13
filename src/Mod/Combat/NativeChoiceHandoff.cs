// Native hand/grid selection and cancellation follow game 0.113.0 (16ac0fe3):
// CardSelectCmd, NPlayerHand.CancelHandSelectionIfNecessary and NCardGridSelectionScreen._ExitTree.
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace Sts2Solver;

/// <summary>Hands an unplanned choice to the player; the game owns the resulting selection task.</summary>
internal static class NativeChoiceHandoff
{
    private static Action? _cancelSelection;

    internal static async Task<IEnumerable<CardModel>> SelectAsync(Player player,
        IReadOnlyList<CardModel> options, int minSelect, int maxSelect, string owner)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("原生选牌交还必须在主线程执行。");
        if (NCombatRoom.Instance is not { } room || !room.IsInsideTree())
        {
            ModLog.Info($"[Sts2Solver] NATIVE_CHOICE_CANCELED owner={owner} reason=no_combat_surface");
            throw new OperationCanceledException("战斗选牌界面已退出。", new CancellationToken(true));
        }

        CardSelectorPrefs prefs = new(CardSelectorPrefs.ExhaustSelectionPrompt,
            Math.Min(minSelect, options.Count), Math.Min(maxSelect, options.Count));
        Task<IEnumerable<CardModel>> selection;
        Action cancel;
        NativeChoiceSurfaceKind surface;
        if (options.Count > 0 && options.All(card => player.PlayerCombatState!.Hand.Cards
                .Any(inHand => ReferenceEquals(card, inHand))))
        {
            surface = NativeChoiceSurfaceKind.Hand;
            NPlayerHand hand = room.Ui.Hand;
            selection = hand.SelectCards(prefs,
                card => options.Any(option => ReferenceEquals(option, card)), null);
            cancel = () =>
            {
                if (!selection.IsCompleted)
                    NativeChoiceRuntime.CancelHandSelectionForExit(hand);
            };
        }
        else
        {
            surface = NativeChoiceSurfaceKind.SimpleGrid;
            NOverlayStack overlays = NOverlayStack.Instance
                ?? throw new OperationCanceledException("战斗选牌界面已退出。", new CancellationToken(true));
            room.Ui.Hand.CancelAllCardPlay();
            NSimpleCardSelectScreen screen = NSimpleCardSelectScreen.Create(options, prefs);
            overlays.Push(screen);
            selection = screen.CardsSelected();
            cancel = () =>
            {
                if (!selection.IsCompleted)
                    overlays.Remove(screen);
            };
        }

        _cancelSelection = cancel;
        ModLog.Warn($"[Sts2Solver] NATIVE_CHOICE_HANDOFF owner={owner} surface={surface} options={options.Count} bounds={minSelect}..{maxSelect}");
        try
        {
#if STS2_SOLVER_TESTING
            CombatControllerTestHooks.ChoiceHandedOffForTest?.Invoke(
                new NativeChoiceRequest(0, surface, player, options, null,
                    prefs.MinSelect, prefs.MaxSelect, true, false, prefs.RequireManualConfirmation, owner)
                { Completion = selection });
#endif
            return await selection;
        }
        finally
        {
            // A completed/canceled request must not clear a later request opened by a continuation.
            if (ReferenceEquals(_cancelSelection, cancel))
                _cancelSelection = null;
        }
    }

    internal static void CancelForSceneExit()
    {
        Action? cancel = _cancelSelection;
        _cancelSelection = null;
        cancel?.Invoke();
    }
}
