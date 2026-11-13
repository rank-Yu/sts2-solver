// Native card-choice driver for the in-process action executor. The matching rules and the
// card-identity binding are adapted from src/Mod/Combat/PlannedNativeSelector.cs, which in turn
// follows CombatSolver's headless PlannedCardSelector at a62f1850 (MIT, Copyright (c) 2026
// Torch1230 and contributors). This copy only replaces ModLog with the offline host's quiet mode.
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace Differential;

internal sealed class NativeChoiceMismatchException(string message) : InvalidOperationException(message);

/// <summary>
/// Answers the native game's selection requests from one action's frozen choice plan. The option
/// list must have exactly the planned size and every planned card must be present; any extra,
/// missing or shifted request is a mismatch instead of a guess.
/// </summary>
internal sealed class NativeChoiceSelector(
    IReadOnlyList<PlannedChoice> choices,
    Player player,
    string owner) : ICardSelector
{
    private int _index;
    internal NativeChoiceMismatchException? Failure { get; private set; }

    private NativeChoiceMismatchException Mismatch(string message)
        => Failure ??= new NativeChoiceMismatchException(message);

    private Player OwnerPlayer => player;

    internal int PendingCount => choices.Count - _index;

    internal static IDisposable Activate(NativeChoiceSelector selector)
        => MegaCrit.Sts2.Core.Runs.RunManager.Instance.CardSelectorStack.PushSelector(selector.OwnerPlayer, selector);

    internal void AssertConsumed()
    {
        if (_index != choices.Count)
        {
            throw Mismatch(
                $"{owner} 仍有 {choices.Count - _index} 个计划选择没有被游戏请求。" +
                $"下一个={Describe(choices[_index])}");
        }
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options,
        int minSelect, int maxSelect)
    {
        List<CardModel> live = options.ToList();
        if (_index >= choices.Count)
        {
            throw Mismatch(
                $"{owner} 请求了计划外的选牌：options={live.Count} bounds={minSelect}..{maxSelect} " +
                $"cards=[{string.Join(',', live.Select(CardName))}]");
        }
        PlannedChoice choice = choices[_index++];
        int[] map = MapOptions(choice, live);
        int expected = choice.SelectedIndexes().Count();
        int actualMin = Math.Min(minSelect, live.Count);
        int actualMax = Math.Min(maxSelect, live.Count);
        if (expected < actualMin || expected > actualMax)
        {
            throw Mismatch(
                $"{owner} 计划选择 {expected} 张，原生界面要求 {actualMin}..{actualMax} 张。" +
                $"choice={Describe(choice)}");
        }
        List<CardModel> selected = new(expected);
        foreach (int option in choice.SelectedIndexes())
            selected.Add(live[map[option]]);
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => throw Mismatch($"{owner} 请求了战斗内计划外的卡牌奖励选择。");

    private int[] MapOptions(PlannedChoice choice, List<CardModel> live)
    {
        int count = choice.Request.OptionCount;
        if (live.Count != count)
        {
            throw Mismatch(
                $"{owner} 选项数 {live.Count} 与计划 {count} 不符。" +
                $"choice={Describe(choice)} native=[{string.Join(',', live.Select(CardName))}]");
        }
        if (!choice.OptionsAreGenerated && !choice.Request.SourceIsNone)
        {
            PlayerCombatState state = player.PlayerCombatState
                ?? throw Mismatch($"{owner} 玩家没有战斗牌堆。");
            IReadOnlyList<CardModel> source = PileCards(state, choice.Request.Source);
            for (int index = 0; index < live.Count; index++)
            {
                if (!ContainsReference(source, live[index]))
                {
                    throw Mismatch(
                        $"{owner} 选项 {CardName(live[index])} 不在计划的牌堆 {choice.Request.Source} 中。" +
                        $"choice={Describe(choice)}");
                }
            }
        }
        bool[] used = new bool[count];
        int[] map = new int[count];
        for (int option = 0; option < count; option++)
        {
            Card predicted = choice.OptionsAreGenerated
                ? choice.GeneratedOptions[option]
                : choice.PileOptions[option];
            CardIdentity key = Identity(predicted);
            int match = -1;
            for (int candidate = 0; candidate < live.Count; candidate++)
            {
                if (!used[candidate] && Identity(live[candidate]) == key)
                {
                    match = candidate;
                    break;
                }
            }
            if (match < 0)
            {
                throw Mismatch(
                    $"{owner} 找不到选项 {option} 对应的原生卡 {key}。" +
                    $"choice={Describe(choice)} native=[{string.Join(',', live.Select(CardName))}]");
            }
            used[match] = true;
            map[option] = match;
        }
        return map;
    }

    private IReadOnlyList<CardModel> PileCards(PlayerCombatState state, Pile pile) => pile switch
    {
        Pile.Hand => state.Hand.Cards,
        Pile.Draw => state.DrawPile.Cards,
        Pile.Discard => state.DiscardPile.Cards,
        Pile.Exhaust => state.ExhaustPile.Cards,
        Pile.Play => state.PlayPile.Cards,
        _ => throw Mismatch($"选择来源牌堆 {pile} 无效。"),
    };

    private static bool ContainsReference(IReadOnlyList<CardModel> cards, CardModel card)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (ReferenceEquals(cards[index], card))
                return true;
        }
        return false;
    }

    private static CardIdentity Identity(in Card card)
        => new(Registry.Card(card.Id).NativeId, card.Upgrade);

    private static CardIdentity Identity(CardModel card)
        => new(card.Id.Entry, card.CurrentUpgradeLevel);

    private static string CardName(CardModel card) => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}";

    private static string Describe(PlannedChoice choice)
    {
        string[] cards = choice.OptionsAreGenerated
            ? [.. Enumerable.Range(0, choice.OptionCount)
                .Select(index => $"{Registry.Card(choice.GeneratedOptions[index].Id).NativeId}+{choice.GeneratedOptions[index].Upgrade}#gen")]
            : [.. Enumerable.Range(0, choice.OptionCount)
                .Select(index => $"{Registry.Card(choice.PileOptions[index].Id).NativeId}+{choice.PileOptions[index].Upgrade}#{choice.Request.Source}")];
        return $"{choice.Request.Kind}/{choice.Request.Effect} source={choice.Request.Source} " +
            $"count={choice.OptionCount} bounds={choice.Request.MinCount}..{choice.Request.MaxCount} " +
            $"selected=0x{choice.Selection:X} options=[{string.Join(',', cards)}]";
    }

    private readonly record struct CardIdentity(string Id, int Upgrade);
}
