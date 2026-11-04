// Deterministic answer to the native turn-setup page. The harness builds the same rooted scenario
// once per option (or once per planned option sequence); this selector records the option lists and
// picks the requested occurrences from each page.
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Solver.TestHost;

/// <summary>One native setup page as the selector saw it (candidates in page order).</summary>
internal sealed record NativeSetupPage(
    IReadOnlyList<(string CardId, int Upgrade)> Options,
    int MinSelect,
    int MaxSelect);

internal sealed class TurnSetupCardSelector : ICardSelector
{
    private readonly int _requestedIndex;
    private readonly IReadOnlyList<int[]>? _plan;
    private readonly Action? _beforeSelect;

    public TurnSetupCardSelector(int requestedIndex, Action? beforeSelect = null)
    {
        _requestedIndex = requestedIndex;
        _beforeSelect = beforeSelect;
    }

    /// <summary>One page of selected option indexes per native setup page, in call order.</summary>
    public TurnSetupCardSelector(IReadOnlyList<int[]> plan, Action? beforeSelect = null)
    {
        _plan = plan;
        _beforeSelect = beforeSelect;
    }

    public int RequestedIndex => _plan == null ? _requestedIndex : -1;
    public List<(string CardId, int Upgrade)> LastOptions { get; } = [];
    public (string CardId, int Upgrade)? LastSelected { get; private set; }
    public int Calls { get; private set; }

    /// <summary>Options/min/max of every player-turn-start page, in call order.</summary>
    public List<NativeSetupPage> Pages { get; } = [];

    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        _beforeSelect?.Invoke();
        List<CardModel> remaining = options.ToList();
        bool setupPage = IsPlayerTurnStart();
        if (setupPage && Calls == 0)
        {
            foreach (CardModel card in remaining)
                LastOptions.Add((card.Id.Entry, card.CurrentUpgradeLevel));
        }
        IReadOnlyList<int> page;
        if (!setupPage)
        {
            // End-of-turn or other non-setup selections are not part of the plan; answer with the
            // preferred option and fill any remaining required slots so a multi-select discard
            // (Hidden Daggers, Charge, ...) can reach the turn-setup pages. An optional page still
            // selects nothing, as before.
            page = DefaultOtherPage(remaining.Count, minSelect, _requestedIndex);
        }
        else if (_plan == null)
        {
            page = DefaultSetupPage(remaining.Count, minSelect, _requestedIndex);
        }
        else
        {
            if (Calls >= _plan.Count)
            {
                throw new InvalidDataException(
                    $"回合准备计划只有 {_plan.Count} 段选择，原生请求了第 {Calls + 1} 段。");
            }
            page = FillPage(_plan[Calls], remaining.Count, minSelect);
        }
        if (setupPage)
        {
            Pages.Add(new NativeSetupPage(
                [.. remaining.Select(static card => (card.Id.Entry, card.CurrentUpgradeLevel))],
                minSelect, maxSelect));
            Calls++;
        }
        if (remaining.Count == 0)
            return Task.FromResult<IEnumerable<CardModel>>([]);
        int actualMin = Math.Min(minSelect, remaining.Count);
        int actualMax = Math.Min(maxSelect, remaining.Count);
        if (page.Count < actualMin || page.Count > actualMax)
        {
            throw new InvalidDataException(
                $"回合准备计划选了 {page.Count} 张，原生要求 {actualMin}..{actualMax}。");
        }
        List<CardModel> selected = new(page.Count);
        foreach (int index in page)
        {
            if ((uint)index >= (uint)remaining.Count)
            {
                throw new InvalidDataException(
                    $"回合准备选项只有 {remaining.Count} 个，请求下标 {index}。");
            }
            CardModel card = remaining[index];
            selected.Add(card);
            LastSelected ??= (card.Id.Entry, card.CurrentUpgradeLevel);
        }
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        CardModel? selected = options.FirstOrDefault()?.Card;
        return selected == null ? default : new CardRewardSelection { card = selected };
    }

    /// <summary>
    /// The setup-page single-selector answer: the preferred option followed by ascending unselected
    /// options until the native minimum is met. A setup page keeps the historical "always take one"
    /// choice even when it allows zero. The native multi-select pages appear when an Imbued card
    /// auto-plays a card such as Hidden Daggers during the setup pause.
    /// </summary>
    private static int[] DefaultSetupPage(int optionCount, int minSelect, int preferred)
        => DefaultPage(optionCount, minSelect, preferred, takeOneWhenOptional: true);

    /// <summary>The non-setup answer: empty when the page allows it, otherwise the same fill.</summary>
    private static int[] DefaultOtherPage(int optionCount, int minSelect, int preferred)
        => DefaultPage(optionCount, minSelect, preferred, takeOneWhenOptional: false);

    private static int[] DefaultPage(int optionCount, int minSelect, int preferred,
        bool takeOneWhenOptional)
    {
        if (optionCount == 0 || minSelect == 0 && !takeOneWhenOptional)
            return [];
        List<int> page = [(uint)preferred < (uint)optionCount ? preferred : 0];
        for (int index = 0; page.Count < minSelect && index < optionCount; index++)
        {
            if (!page.Contains(index))
                page.Add(index);
        }
        return [.. page];
    }

    /// <summary>Extends a recorded plan page to the native minimum with unselected options.</summary>
    private static int[] FillPage(int[] page, int optionCount, int minSelect)
    {
        if (page.Length >= minSelect || optionCount == 0)
            return page;
        List<int> filled = [.. page];
        for (int index = 0; filled.Count < minSelect && index < optionCount; index++)
        {
            if (!filled.Contains(index))
                filled.Add(index);
        }
        return [.. filled];
    }

    private static bool IsPlayerTurnStart()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        Player? player = state == null ? null : LocalContext.GetMe(state);
        return player?.PlayerCombatState is { Phase: PlayerTurnPhase.Start or PlayerTurnPhase.AutoPrePlay };
    }
}
