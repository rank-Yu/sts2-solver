// Fixed-prefix replay semantics adapted from CombatSolver src/Search/Probes/SearchReplayCore.cs,
// src/Search/Probes/SearchProbeHelpers.cs and src/Search/CardChoiceSupport.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). The existing engine's SimulatedCombatState
// is replaced by the imported Battle and the Sts2Solver kernel's PlayerAction/choice requests.

using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Import;

public enum PrefixActionKind : byte
{
    PlayCard,
    UsePotion,
    EndTurn,
}

/// <summary>One selected card of a frozen prefix plan, as CombatSolver records it.</summary>
public sealed record PrefixCardToken(
    string CardId,
    int UpgradeLevel,
    int SourceOccurrence,
    int OptionOccurrence);

/// <summary>One card choice of a frozen prefix plan.</summary>
public sealed record PrefixChoice(
    ChoiceEffect Effect,
    Pile SourcePile,
    bool SourceIsNone,
    IReadOnlyList<PrefixCardToken> Cards);

/// <summary>One fixed-prefix action translated from <c>flashPrefixActions</c>/<c>flashPrefixPlan</c>.</summary>
public sealed record PrefixAction(
    PrefixActionKind Kind,
    int Turn,
    string CardId = "",
    int CardOccurrence = 0,
    string CardStateKey = "",
    int CardStateOccurrence = 0,
    uint? TargetCombatId = null,
    int PotionSlot = -1,
    string PotionId = "",
    PrefixChoice? Choice = null,
    IReadOnlyList<PrefixChoice>? NestedChoices = null,
    int NestedChoicesBeforePrimary = 0);

/// <summary>Replay result and the cumulative potion facts the evaluation configuration freezes.</summary>
public sealed record PrefixReplayResult(
    Battle Battle,
    int ActionCount,
    int PotionUseCount,
    int ExplicitPotionUseCount,
    int AutomaticPotionUseCount,
    int PotionStrategicCost,
    int ExplicitPotionStrategicCost);

/// <summary>
/// Executes the fixed prefix on the Sts2Solver kernel from the imported pre-prefix root. The actions are
/// the same <c>PlanAction</c>s the existing engine replays: a card/target occurrence plus the plan's
/// card choices in execution order. Any action the kernel cannot resolve with the recorded plan is
/// rejected instead of being approximated.
/// </summary>
public static class PrefixReplay
{
    public static PrefixReplayResult Replay(in Battle root, Rules rules,
        IReadOnlyList<PrefixAction> actions, int initialPotionUseCount = 0, Action<PlayerAction>? onResolvedAction = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(actions);
        Battle battle = root;
        foreach (PrefixAction action in actions)
        {
            if (action.Turn != battle.PlayerTurn)
            {
                throw Reject(
                    $"动作回合 {action.Turn} 与根回合 {battle.PlayerTurn} 不符（{Describe(action)}）。");
            }
            PlayerAction baseAction = BuildAction(battle, action);
            List<PrefixChoice> choices = OrderChoices(action);
            PlayerAction carrying = baseAction;
            int choiceIndex = 0;
            while (true)
            {
                Battle trial = battle;
                StepResult result = Kernel.Step(ref trial, carrying, rules, out ChoiceRequest request);
                if (result == StepResult.Completed)
                {
                    if (choiceIndex != choices.Count)
                    {
                        throw Reject(
                            $"动作 {Describe(action)} 只请求了 {choiceIndex} 次选择，" +
                            $"计划有 {choices.Count} 次。");
                    }
                    battle = trial;
                    onResolvedAction?.Invoke(carrying);
                    break;
                }
                if (choiceIndex >= choices.Count)
                {
                    throw Reject(
                        $"动作 {Describe(action)} 请求第 {choiceIndex + 1} 次选择 " +
                        $"({request.Effect}/{request.Source}/{request.OptionCount})，计划没有对应选择。");
                }
                ulong selection = MapChoice(trial, request, choices[choiceIndex], action);
                carrying = carrying.WithChoice(selection);
                choiceIndex++;
            }
        }

        // The post-prefix rule table is compiled from the post-prefix known-enemy order: every known
        // enemy gets the entry at its own position, then the spawnable closure follows. The pre-prefix
        // replay started from the pre-prefix table, so re-project the rule indices onto that layout.
        for (int index = 0; index < battle.EnemyCount; index++)
            battle.Enemies[index].Rules = checked((byte)index);

        (int slotUses, int slotCost, int explicitCost, int fairyUses) = CountSlotPotionUses(root, battle, rules);
        int generatedUses = battle.Counter(Counter.GeneratedPotionUseCount);
        int generatedCosts = battle.Counter(Counter.GeneratedPotionStrategicCost);
        int generatedExplicitCost = generatedCosts & 0xFFFF;
        int generatedAutomaticCost = (int)((uint)generatedCosts >> 16);
        // AutomaticPotionUseCount already includes a Fairy in a Bottle root slot (the same slot diff
        // also sees it), so it is the authoritative automatic count for the prefix.
        int automatic = battle.Counter(Counter.AutomaticPotionUseCount);
        int explicitUses = slotUses + generatedUses - automatic;
        if (explicitUses < 0)
            throw Reject($"前缀药水计数为负：slot={slotUses} generated={generatedUses} automatic={automatic}。");
        return new PrefixReplayResult(
            battle,
            actions.Count,
            checked(initialPotionUseCount + slotUses + generatedUses),
            checked(initialPotionUseCount + explicitUses),
            automatic,
            checked(slotCost + generatedExplicitCost + generatedAutomaticCost),
            checked(explicitCost + generatedExplicitCost));
    }

    private static (int Uses, int Cost, int ExplicitCost, int FairyUses) CountSlotPotionUses(
        in Battle root, in Battle end, Rules rules)
    {
        int uses = 0, cost = 0, explicitCost = 0, fairy = 0;
        for (int slot = 0; slot < root.PotionSlotCount; slot++)
        {
            ushort before = root.Potions[slot];
            if (before == 0)
                continue;
            ushort after = slot < end.PotionSlotCount ? end.Potions[slot] : (ushort)0;
            if (after == before)
                continue;
            int strategic = rules.Potion((ushort)(before - 1)).StrategicCost;
            uses++;
            cost = checked(cost + strategic);
            if (IsFairy((ushort)(before - 1)))
                fairy++;
            else
                explicitCost = checked(explicitCost + strategic);
        }
        return (uses, cost, explicitCost, fairy);
    }

    private static bool IsFairy(ushort potionId)
        => Registry.TryPotion("FAIRY_IN_A_BOTTLE", out ushort fairy) && fairy == potionId;

    private static PlayerAction BuildAction(in Battle battle, PrefixAction action)
    {
        switch (action.Kind)
        {
            case PrefixActionKind.EndTurn:
                return PlayerAction.EndTurn();
            case PrefixActionKind.UsePotion:
            {
                if ((uint)action.PotionSlot >= (uint)battle.PotionSlotCount)
                    throw Reject($"药水槽 {action.PotionSlot} 越界（{Describe(action)}）。");
                ushort stored = battle.Potions[action.PotionSlot];
                if (stored == 0)
                    throw Reject($"药水槽 {action.PotionSlot} 为空（{Describe(action)}）。");
                ushort potionId = (ushort)(stored - 1);
                string expected = action.PotionId;
                if (expected.Length > 0 && Registry.Potion(potionId).NativeId != expected)
                {
                    throw Reject(
                        $"药水槽 {action.PotionSlot} 是 {Registry.Potion(potionId).NativeId}，计划是 {expected}。");
                }
                return PlayerAction.UsePotion(action.PotionSlot, TargetOwner(battle, action));
            }
            case PrefixActionKind.PlayCard:
            {
                int hand = FindHandCard(battle, action);
                return PlayerAction.PlayCard(hand, TargetOwner(battle, action));
            }
            default:
                throw Reject($"未知前缀动作 {(byte)action.Kind}。");
        }
    }

    private static byte TargetOwner(in Battle battle, PrefixAction action)
    {
        if (action.TargetCombatId is not uint combatId)
            return Limits.NoOwner;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (battle.Enemies[index].CombatId == combatId)
                return checked((byte)(Limits.FirstEnemyOwner + index));
        }
        throw Reject($"目标 CombatId={combatId} 不在根中（{Describe(action)}）。");
    }

    private static int FindHandCard(in Battle battle, PrefixAction action)
    {
        bool exactState = action.CardStateKey.Length != 0;
        int upgrade = exactState ? UpgradeFromStateKey(action.CardStateKey, action) : -1;
        int occurrence = exactState ? action.CardStateOccurrence : action.CardOccurrence;
        for (int hand = 0; hand < battle.HandCount; hand++)
        {
            ref readonly Card card = ref battle.Cards[hand];
            if (Registry.Card(card.Id).NativeId != action.CardId)
                continue;
            if (exactState && card.Upgrade != upgrade)
                continue;
            if (occurrence-- != 0)
                continue;
            return hand;
        }
        throw Reject(
            $"手牌中找不到 {action.CardId} occurrence={occurrence} （{Describe(action)}）。");
    }

    private static int UpgradeFromStateKey(string stateKey, PrefixAction action)
    {
        int plus = stateKey.IndexOf('+');
        if (plus < 0)
            throw Reject($"卡片状态键没有升级等级（{Describe(action)}）。");
        int end = plus + 1;
        while (end < stateKey.Length && char.IsAsciiDigit(stateKey[end]))
            end++;
        if (end == plus + 1 || !int.TryParse(stateKey.AsSpan(plus + 1, end - plus - 1), out int upgrade))
            throw Reject($"卡片状态键的升级等级无法解析（{Describe(action)}）。");
        return upgrade;
    }

    private static List<PrefixChoice> OrderChoices(PrefixAction action)
    {
        List<PrefixChoice> choices = [];
        IReadOnlyList<PrefixChoice> nested = action.NestedChoices ?? [];
        if (action.NestedChoicesBeforePrimary < 0 || action.NestedChoicesBeforePrimary > nested.Count)
            throw Reject($"嵌套选择数量越界（{Describe(action)}）。");
        for (int index = 0; index < action.NestedChoicesBeforePrimary; index++)
            choices.Add(nested[index]);
        if (action.Choice is { } primary)
            choices.Add(primary);
        for (int index = action.NestedChoicesBeforePrimary; index < nested.Count; index++)
            choices.Add(nested[index]);
        return choices;
    }

    private static ulong MapChoice(in Battle battle, in ChoiceRequest request,
        PrefixChoice choice, PrefixAction action)
    {
        if (request.Effect != choice.Effect)
        {
            throw Reject(
                $"选择效果 {choice.Effect} 与请求 {request.Effect} 不符（{Describe(action)}）。");
        }
        ulong selection = 0;
        int selected = 0;
        foreach (PrefixCardToken token in choice.Cards)
        {
            int index = FindOptionIndex(battle, request, choice, token, action);
            ulong bit = 1UL << index;
            if ((selection & bit) != 0)
                throw Reject($"同一选项被选择两次：{token.CardId}（{Describe(action)}）。");
            selection |= bit;
            selected++;
        }
        if (selected < request.MinCount || selected > request.MaxCount)
        {
            throw Reject(
                $"选择 {selected} 张，请求要求 {request.MinCount}..{request.MaxCount} 张" +
                $"（{Describe(action)}）。");
        }
        return selection;
    }

    private static int FindOptionIndex(in Battle battle, in ChoiceRequest request,
        PrefixChoice choice, PrefixCardToken token, PrefixAction action)
    {
        if (request.OptionsAreGenerated)
        {
            int occurrence = token.OptionOccurrence;
            for (int index = 0; index < request.OptionCount; index++)
            {
                Card card = request.GeneratedOptions[index];
                if (Registry.Card(card.Id).NativeId != token.CardId
                    || card.Upgrade != token.UpgradeLevel)
                {
                    continue;
                }
                if (occurrence-- == 0)
                    return index;
            }
            throw Reject(
                $"生成选项中找不到 {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}" +
                $"（{Describe(action)}）。");
        }

        int start = Piles.Start(battle, request.Source);
        int count = Piles.Count(battle, request.Source);
        int sourceOccurrence = token.SourceOccurrence;
        int position = -1;
        for (int index = 0; index < count; index++)
        {
            ref readonly Card card = ref battle.Cards[start + index];
            if (Registry.Card(card.Id).NativeId != token.CardId
                || card.Upgrade != token.UpgradeLevel)
            {
                continue;
            }
            if (sourceOccurrence-- == 0)
            {
                position = start + index;
                break;
            }
        }
        if (position < 0)
        {
            throw Reject(
                $"牌堆 {request.Source} 中找不到 {token.CardId}+{token.UpgradeLevel}" +
                $"#{token.SourceOccurrence}（{Describe(action)}）。");
        }
        for (int index = 0; index < request.OptionCount; index++)
        {
            if (request.Options[index] == position)
                return index;
        }
        throw Reject(
            $"所选 {token.CardId} 不在请求的选项中（{Describe(action)}）。");
    }

    private static string Describe(PrefixAction action)
        => action.Kind switch
        {
            PrefixActionKind.PlayCard => $"PlayCard {action.CardId}#{action.CardOccurrence}",
            PrefixActionKind.UsePotion => $"UsePotion slot={action.PotionSlot} {action.PotionId}",
            _ => "EndTurn",
        };

    private static UnsupportedImportException Reject(string detail)
        => new(ImportReason.PrefixPlan, detail);
}
