// Native fallback for an out-of-plan card selection. The deployment selector must never fault the
// game's async selection task; when the plan cannot answer a request, the current request is handed
// back to a native selection surface (or a deterministic first-N fallback when no surface exists,
// which keeps headless test flows alive) and the controller stops the deployment.
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace Sts2Solver;

internal static class NativeChoiceFallback
{
    /// <summary>
    /// Opens a native card-selection surface for the live options. Returns a completed deterministic
    /// selection when no native surface is available (headless test mode or a non-main-thread call),
    /// so the native async flow can finish and the controller can pause afterwards.
    /// </summary>
    internal static Task<IEnumerable<CardModel>> SelectAsync(Player player,
        IReadOnlyList<CardModel> options, int minSelect, int maxSelect, string owner)
    {
        try
        {
#if STS2_SOLVER_TESTING
            if (ModEntry.TestMode)
                return Task.FromResult(Deterministic(options, minSelect));
#endif
            if (!NGame.IsMainThread() || NCombatRoom.Instance is not { } room)
                return Task.FromResult(Deterministic(options, minSelect));
            int min = Math.Min(minSelect, options.Count);
            int max = Math.Min(maxSelect, options.Count);
            CardSelectorPrefs prefs = new(CardSelectorPrefs.ExhaustSelectionPrompt, min, max);
            if (options.Count > 0 && AllInHand(player, options))
            {
                Func<CardModel, bool> filter = card => ContainsReference(options, card);
                return AwaitNativeAsync(
                    room.Ui.Hand.SelectCards(prefs, filter, null), options, minSelect, owner);
            }
            NPlayerHand.Instance?.CancelAllCardPlay();
            NSimpleCardSelectScreen screen = NSimpleCardSelectScreen.Create(options, prefs);
            NOverlayStack? overlays = NOverlayStack.Instance;
            if (overlays == null)
                return Task.FromResult(Deterministic(options, minSelect));
            overlays.Push(screen);
            return AwaitNativeAsync(screen.CardsSelected(), options, minSelect, owner);
        }
        catch (Exception error)
        {
            ModLog.Error(
                $"[Sts2Solver] NATIVE_CHOICE_FALLBACK_FAILED owner={owner} " +
                $"{error.GetType().Name}: {error.Message}");
            return Task.FromResult(Deterministic(options, minSelect));
        }
    }

    private static async Task<IEnumerable<CardModel>> AwaitNativeAsync(
        Task<IEnumerable<CardModel>> task, IReadOnlyList<CardModel> options, int minSelect,
        string owner)
    {
        try
        {
            return await task;
        }
        catch (Exception error)
        {
            ModLog.Error(
                $"[Sts2Solver] NATIVE_CHOICE_FALLBACK_FAILED owner={owner} " +
                $"{error.GetType().Name}: {error.Message}");
            return Deterministic(options, minSelect);
        }
    }

    private static IEnumerable<CardModel> Deterministic(IReadOnlyList<CardModel> options, int minSelect)
    {
        int count = Math.Min(minSelect, options.Count);
        if (count <= 0)
            return [];
        CardModel[] selected = new CardModel[count];
        for (int index = 0; index < count; index++)
            selected[index] = options[index];
        return selected;
    }

    private static bool AllInHand(Player player, IReadOnlyList<CardModel> options)
    {
        if (player.PlayerCombatState is not { } state)
            return false;
        IReadOnlyList<CardModel> hand = state.Hand.Cards;
        for (int index = 0; index < options.Count; index++)
        {
            if (!ContainsReference(hand, options[index]))
                return false;
        }
        return true;
    }

    private static bool ContainsReference(IReadOnlyList<CardModel> cards, CardModel card)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (ReferenceEquals(cards[index], card))
                return true;
        }
        return false;
    }
}
