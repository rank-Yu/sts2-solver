// Ported from CombatSolver/src/Testing/Host/UnattendedCardSelector.cs
// at commit a62f1850 (MIT, Copyright (c) 2026 Torch1230 and contributors).
// Only the deterministic "__FIRST__" behavior needed to answer the native turn-setup page is kept.
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Solver.TestHost;

internal sealed class FirstChoiceCardSelector : ICardSelector
{
    /// <summary>Card ids answered by this selector; used to identify the ordinary setup root.</summary>
    public static List<string> LastSelections { get; } = [];

    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        // The first option, or the first minSelect options when the page needs more than one
        // (a "choose 2" start-of-combat page).
        List<CardModel> remaining = options.ToList();
        int take = Math.Min(Math.Min(remaining.Count, maxSelect), Math.Max(1, minSelect));
        List<CardModel> selected = remaining.Take(Math.Max(0, take)).ToList();
        foreach (CardModel card in selected)
            LastSelections.Add(card.Id.Entry);
        if (selected.Count < minSelect || selected.Count > maxSelect)
            throw new InvalidOperationException(
                $"原生测试选择 {selected.Count} 张牌，但界面要求 {minSelect}..{maxSelect} 张。");
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        CardModel? selected = options.FirstOrDefault()?.Card;
        return selected == null ? default : new CardRewardSelection { card = selected };
    }
}
