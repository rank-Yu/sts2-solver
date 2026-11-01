// Deterministic answer to the native turn-setup page. The harness builds the same rooted scenario
// once per option; this selector records the option list and picks the requested occurrence.
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Solver.TestHost;

internal sealed class TurnSetupCardSelector(int requestedIndex, Action? beforeSelect = null)
    : ICardSelector
{
    public int RequestedIndex { get; } = requestedIndex;
    public List<(string CardId, int Upgrade)> LastOptions { get; } = [];
    public (string CardId, int Upgrade)? LastSelected { get; private set; }
    public int Calls { get; private set; }

    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        beforeSelect?.Invoke();
        List<CardModel> remaining = options.ToList();
        if (Calls++ == 0)
        {
            foreach (CardModel card in remaining)
                LastOptions.Add((card.Id.Entry, card.CurrentUpgradeLevel));
        }
        if (remaining.Count == 0)
            return Task.FromResult<IEnumerable<CardModel>>([]);
        if ((uint)RequestedIndex >= (uint)remaining.Count)
        {
            throw new InvalidDataException(
                $"回合准备选项只有 {remaining.Count} 个，请求下标 {RequestedIndex}。");
        }
        CardModel selected = remaining[RequestedIndex];
        LastSelected = (selected.Id.Entry, selected.CurrentUpgradeLevel);
        if (minSelect > 1 || maxSelect < 1)
            throw new InvalidDataException($"回合准备选择数量 {minSelect}..{maxSelect} 不受支持。");
        return Task.FromResult<IEnumerable<CardModel>>([selected]);
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        CardModel? selected = options.FirstOrDefault()?.Card;
        return selected == null ? default : new CardRewardSelection { card = selected };
    }
}
