// Native card-choice driver for route deployment. Setting CardSelectCmd's automated selector is the
// same surface CombatSolver's headless PlannedCardSelector uses at a62f1850 (MIT, Copyright (c)
// 2026 Torch1230 and contributors); this version additionally binds every option to the route's
// frozen card identities and pile before returning a selection.
//
// An unplanned request signals the deployer immediately and hands the selection to the player.
// The selector scope is released by the deployer; the native selection completes independently.
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

/// <summary>
/// Answers native card-selection requests from one action's frozen choice plan. Every request is
/// matched to the plan's next explicit choice by option count and card identity; a missing or
/// shifted request stops deployment and hands the pending choice to a native selection surface.
/// </summary>
internal sealed class PlannedNativeSelector(
    IReadOnlyList<PlannedChoice> choices,
    Player player,
    string owner) : ICardSelector
{
    private readonly IReadOnlyList<PlannedChoice> _choices = choices;
    private int _index;
    private readonly TaskCompletionSource<string> _failure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Player OwnerPlayer => player;

    internal int PendingCount => _choices.Count - _index;

    internal Task<string> Failure => _failure.Task;

    internal string? FailureDetail => Failure.IsCompletedSuccessfully ? Failure.Result : null;

    internal static IDisposable Activate(PlannedNativeSelector selector)
        => MegaCrit.Sts2.Core.Runs.RunManager.Instance.CardSelectorStack.PushSelector(selector.OwnerPlayer, selector);

    /// <summary>Returns the unused-plan diagnostic, or null when every planned choice was consumed.</summary>
    internal string? UnconsumedDetail()
    {
        if (_index == _choices.Count)
            return null;
        return $"{owner} 仍有 {_choices.Count - _index} 个计划选择没有被游戏请求。" +
            $"下一个={Describe(_choices[_index])}";
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options,
        int minSelect, int maxSelect)
    {
        List<CardModel> live = options.ToList();
        if (FailureDetail != null)
        {
            return Fail(live, minSelect, maxSelect,
                $"{owner} 在计划外选牌后继续请求选牌：options={live.Count} " +
                $"bounds={minSelect}..{maxSelect} cards=[{string.Join(',', live.Select(CardName))}]");
        }
        if (_index >= _choices.Count)
        {
            return Fail(live, minSelect, maxSelect,
                $"{owner} 请求了计划外的选牌：options={live.Count} " +
                $"bounds={minSelect}..{maxSelect} cards=[{string.Join(',', live.Select(CardName))}]");
        }
        PlannedChoice choice = _choices[_index];
        if (!TryMapOptions(choice, live, out int[] map, out string error))
            return Fail(live, minSelect, maxSelect, error);
        int expected = choice.SelectedIndexes().Count();
        int actualMin = Math.Min(minSelect, live.Count);
        int actualMax = Math.Min(maxSelect, live.Count);
        if (expected < actualMin || expected > actualMax)
        {
            return Fail(live, minSelect, maxSelect,
                $"{owner} 计划选择 {expected} 张，原生界面要求 {actualMin}..{actualMax} 张。" +
                $"choice={Describe(choice)}");
        }
        List<CardModel> selected = new(expected);
        foreach (int option in choice.SelectedIndexes())
            selected.Add(live[map[option]]);
        ModLog.Info(
            $"[Sts2Solver] DEPLOY_CHOICE {owner} index={_index + 1}/{_choices.Count} " +
            $"source={choice.Request.Source} effect={choice.Request.Effect} " +
            $"plan_bounds={choice.Request.MinCount}..{choice.Request.MaxCount} " +
            $"native_bounds={minSelect}..{maxSelect} options={live.Count} " +
            $"chosen=[{string.Join(',', selected.Select(CardName))}] " +
            $"identities=[{string.Join(',', live.Select(CardName))}]");
        _index++;
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        RecordFailure($"{owner} 请求了战斗内计划外的卡牌奖励选择。");
        throw new NativeChoicePlanMismatchException(FailureDetail!);
    }

    private Task<IEnumerable<CardModel>> Fail(List<CardModel> live, int minSelect, int maxSelect,
        string message)
    {
        RecordFailure(message);
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.SelectUnplannedCardsForTest is { } select)
            return select(live, minSelect);
#endif
        return NativeChoiceHandoff.SelectAsync(player, live, minSelect, maxSelect, owner);
    }

    private void RecordFailure(string message)
    {
        if (_failure.TrySetResult(message))
            ModLog.Error($"[Sts2Solver] DEPLOY_CHOICE_MISMATCH {message}");
    }

    private bool TryMapOptions(PlannedChoice choice, List<CardModel> live, out int[] map,
        out string error)
    {
        map = [];
        int count = choice.Request.OptionCount;
        if (live.Count != count)
        {
            error = $"{owner} 选项数 {live.Count} 与计划 {count} 不符。" +
                $"choice={Describe(choice)} native=[{string.Join(',', live.Select(CardName))}]";
            return false;
        }
        if (!choice.OptionsAreGenerated && !choice.Request.SourceIsNone)
        {
            PlayerCombatState? state = player.PlayerCombatState;
            if (state == null)
            {
                error = $"{owner} 玩家没有战斗牌堆。";
                return false;
            }
            IReadOnlyList<CardModel>? source = PileCards(state, choice.Request.Source);
            if (source == null)
            {
                error = $"{owner} 选择来源牌堆 {choice.Request.Source} 无效。";
                return false;
            }
            for (int index = 0; index < live.Count; index++)
            {
                if (!ContainsReference(source, live[index]))
                {
                    error = $"{owner} 选项 {CardName(live[index])} 不在计划的牌堆 " +
                        $"{choice.Request.Source} 中。choice={Describe(choice)}";
                    return false;
                }
            }
        }
        bool[] used = new bool[count];
        int[] result = new int[count];
        for (int option = 0; option < count; option++)
        {
            Card predicted = choice.OptionsAreGenerated
                ? choice.GeneratedOptions[option]
                : choice.PileOptions[option];
            if (!TryIdentity(predicted, out CardIdentity key))
            {
                error = $"{owner} 计划选项 {option} 的卡牌不在注册表中。choice={Describe(choice)}";
                return false;
            }
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
                error = $"{owner} 找不到选项 {option} 对应的原生卡 {key}。" +
                    $"choice={Describe(choice)} native=[{string.Join(',', live.Select(CardName))}]";
                return false;
            }
            used[match] = true;
            result[option] = match;
        }
        map = result;
        error = "";
        return true;
    }

    private static IReadOnlyList<CardModel>? PileCards(PlayerCombatState state, Pile pile) => pile switch
    {
        Pile.Hand => state.Hand.Cards,
        Pile.Draw => state.DrawPile.Cards,
        Pile.Discard => state.DiscardPile.Cards,
        Pile.Exhaust => state.ExhaustPile.Cards,
        Pile.Play => state.PlayPile.Cards,
        _ => null,
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

    private static bool TryIdentity(in Card card, out CardIdentity identity)
    {
        try
        {
            identity = new CardIdentity(Registry.Card(card.Id).NativeId, card.Upgrade);
            return true;
        }
        catch (UnsupportedException)
        {
            identity = default;
            return false;
        }
    }

    private static CardIdentity Identity(CardModel card)
        => new(card.Id.Entry, card.CurrentUpgradeLevel);

    private static string CardName(CardModel card) => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}";

    private static string NativeName(in Card card)
    {
        try
        {
            return Registry.Card(card.Id).NativeId;
        }
        catch (UnsupportedException)
        {
            return $"#{card.Id}";
        }
    }

    private static string Describe(PlannedChoice choice)
    {
        string[] cards = choice.OptionsAreGenerated
            ? [.. Enumerable.Range(0, choice.OptionCount)
                .Select(index => $"{NativeName(choice.GeneratedOptions[index])}+{choice.GeneratedOptions[index].Upgrade}#gen")]
            : [.. Enumerable.Range(0, choice.OptionCount)
                .Select(index => $"{NativeName(choice.PileOptions[index])}+{choice.PileOptions[index].Upgrade}#{choice.Request.Source}")];
        return $"{choice.Request.Kind}/{choice.Request.Effect} source={choice.Request.Source} " +
            $"count={choice.OptionCount} bounds={choice.Request.MinCount}..{choice.Request.MaxCount} " +
            $"selected=0x{choice.Selection:X} options=[{string.Join(',', cards)}]";
    }

    private readonly record struct CardIdentity(string Id, int Upgrade);
}
