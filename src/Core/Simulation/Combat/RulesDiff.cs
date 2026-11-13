using System;
using System.Collections.Generic;
using System.Linq;

namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Field-level diagnostics for two <see cref="Rules"/> objects. The golden comparison is
/// byte-for-byte, but a first difference is easier to act on when it names the table and item.
/// </summary>
public static class RulesDiff
{
    public static IReadOnlyList<string> Compare(Rules expected, Rules actual, int limit = 24)
    {
        List<string> differences = [];
        void Add(string text)
        {
            if (differences.Count < limit)
                differences.Add(text);
        }

        CardTable(expected, actual, Add);
        DecimalTable("powerVars", expected.PowerVarTable, actual.PowerVarTable,
            id => $"power[{Registry.Power((ushort)id).NativeId}]", Add);
        PotionTable(expected, actual, Add);
        DecimalTable("relicVars", expected.RelicVarTable, actual.RelicVarTable,
            id => $"relic[{Registry.Relic((ushort)id).NativeId}]", Add);
        MonsterTable(expected, actual, Add);
        SpawnTable(expected, actual, Add);
        PoolTable(expected, actual, Add);
        UShortTable("potionPool", expected.PotionPoolTable, actual.PotionPoolTable, Add);
        UShortTable("outOfCombatPotionPool", expected.OutOfCombatPotionPoolTable,
            actual.OutOfCombatPotionPoolTable, Add);
        UShortTable("rootPotions", expected.RootPotionTable, actual.RootPotionTable, Add);
        BoolTable("powerDeathHistoryRisk", expected.PowerDeathHistoryRiskTable,
            actual.PowerDeathHistoryRiskTable,
            id => $"power[{Registry.Power((ushort)id).NativeId}]", Add);

        Scalar("hasEncounter", expected.HasEncounter, actual.HasEncounter, Add);
        StringArray("encounterSlots", expected.EncounterSlotTable, actual.EncounterSlotTable, Add);
        Scalar("rootEnemyCount", expected.RootEnemyCount, actual.RootEnemyCount, Add);
        Scalar("baseMaxEnergy", expected.BaseMaxEnergy, actual.BaseMaxEnergy, Add);
        Scalar("playerSlotIndex", expected.PlayerSlotIndex, actual.PlayerSlotIndex, Add);
        Scalar("baseOrbSlotCount", expected.BaseOrbSlotCount, actual.BaseOrbSlotCount, Add);
        Scalar("rootSwordSageAmount", expected.RootSwordSageAmount, actual.RootSwordSageAmount, Add);
        Scalar("unsettlingLampRelicId", expected.UnsettlingLampRelicId,
            actual.UnsettlingLampRelicId, Add);
        Scalar("hasIdentityContent", expected.HasIdentityContent, actual.HasIdentityContent, Add);
        Scalar("hasShuffleOrderEnchantment", expected.HasShuffleOrderEnchantment,
            actual.HasShuffleOrderEnchantment, Add);
        Scalar("hasBeforeFlushEnchantment", expected.HasBeforeFlushEnchantment,
            actual.HasBeforeFlushEnchantment, Add);
        Scalar("hasTheftContent", expected.HasTheftContent, actual.HasTheftContent, Add);
        Scalar("hasDecimalCardVars", expected.HasDecimalCardVars, actual.HasDecimalCardVars, Add);
        Scalar("hasInfiniteHpDisplayMonster", expected.HasInfiniteHpDisplayMonster,
            actual.HasInfiniteHpDisplayMonster, Add);
        Scalar("hasPaelsLegion", expected.HasPaelsLegion, actual.HasPaelsLegion, Add);
        Scalar("furCoatSpawnHpOne", expected.FurCoatSpawnHpOne, actual.FurCoatSpawnHpOne, Add);
        Scalar("rootLostHpMask", expected.RootLostHpMask, actual.RootLostHpMask, Add);
        Scalar("hasHistoryCourse", expected.HasHistoryCourse, actual.HasHistoryCourse, Add);
        return differences;
    }

    private static void CardTable(Rules expected, Rules actual, Action<string> add)
    {
        CardStats?[][] left = expected.CardTable;
        CardStats?[][] right = actual.CardTable;
        int count = Math.Min(left.Length, right.Length);
        for (int id = 0; id < count; id++)
        {
            string name = id < Registry.CardCount ? Registry.Card((ushort)id).NativeId : id.ToString();
            CardStats?[] leftLevels = left[id];
            CardStats?[] rightLevels = right[id];
            int levels = Math.Min(leftLevels.Length, rightLevels.Length);
            for (int upgrade = 0; upgrade < levels; upgrade++)
            {
                CardStats? a = leftLevels[upgrade];
                CardStats? b = rightLevels[upgrade];
                if (a == null || b == null)
                {
                    if (a != b)
                        add($"cards[{name}][{upgrade}] golden={(a == null ? "null" : "stats")} actual={(b == null ? "null" : "stats")}");
                    continue;
                }
                CompareCard(name, upgrade, a, b, add);
            }
            if (leftLevels.Length != rightLevels.Length)
                add($"cards[{name}].levels golden={leftLevels.Length} actual={rightLevels.Length}");
        }
        if (left.Length != right.Length)
            add($"cards.count golden={left.Length} actual={right.Length}");
    }

    private static void CompareCard(string name, int upgrade, CardStats a, CardStats b, Action<string> add)
    {
        string prefix = $"cards[{name}][{upgrade}]";
        Diff(nameof(CardStats.Type), a.Type, b.Type, prefix, add);
        Diff(nameof(CardStats.Target), a.Target, b.Target, prefix, add);
        Diff(nameof(CardStats.Rarity), a.Rarity, b.Rarity, prefix, add);
        Diff(nameof(CardStats.Pool), a.Pool, b.Pool, prefix, add);
        Diff(nameof(CardStats.EnergyCost), a.EnergyCost, b.EnergyCost, prefix, add);
        Diff(nameof(CardStats.CostsX), a.CostsX, b.CostsX, prefix, add);
        Diff(nameof(CardStats.StarCost), a.StarCost, b.StarCost, prefix, add);
        Diff(nameof(CardStats.StarCostsX), a.StarCostsX, b.StarCostsX, prefix, add);
        Diff(nameof(CardStats.Keywords), a.Keywords, b.Keywords, prefix, add);
        Diff(nameof(CardStats.HasDynamicType), a.HasDynamicType, b.HasDynamicType, prefix, add);
        Diff(nameof(CardStats.MaxUpgradeLevel), a.MaxUpgradeLevel, b.MaxUpgradeLevel, prefix, add);
        Diff(nameof(CardStats.Tags), a.Tags, b.Tags, prefix, add);
        Diff(nameof(CardStats.IsSoul), a.IsSoul, b.IsSoul, prefix, add);
        Diff(nameof(CardStats.IsColorless), a.IsColorless, b.IsColorless, prefix, add);
        Diff(nameof(CardStats.PlaysRecordRisk), a.PlaysRecordRisk, b.PlaysRecordRisk, prefix, add);
        DiffDecimals(nameof(CardStats.Vars), a.Vars, b.Vars, prefix, add);
    }

    private static void PotionTable(Rules expected, Rules actual, Action<string> add)
    {
        PotionStats?[] left = expected.PotionTable;
        PotionStats?[] right = actual.PotionTable;
        int count = Math.Min(left.Length, right.Length);
        for (int id = 0; id < count; id++)
        {
            string name = id < Registry.PotionCount ? Registry.Potion((ushort)id).NativeId : id.ToString();
            PotionStats? a = left[id];
            PotionStats? b = right[id];
            if (a == null || b == null)
            {
                if (a != b)
                    add($"potions[{name}] golden={(a == null ? "null" : "stats")} actual={(b == null ? "null" : "stats")}");
                continue;
            }
            string prefix = $"potions[{name}]";
            Diff(nameof(PotionStats.Target), a.Target, b.Target, prefix, add);
            Diff(nameof(PotionStats.Rarity), a.Rarity, b.Rarity, prefix, add);
            Diff(nameof(PotionStats.StrategicCost), a.StrategicCost, b.StrategicCost, prefix, add);
            DiffDecimals(nameof(PotionStats.Vars), a.Vars, b.Vars, prefix, add);
            if (!a.VarProps.AsSpan().SequenceEqual(b.VarProps))
                add($"{prefix}.VarProps golden={string.Join(',', a.VarProps)} actual={string.Join(',', b.VarProps)}");
        }
        if (left.Length != right.Length)
            add($"potions.count golden={left.Length} actual={right.Length}");
    }

    private static void DecimalTable(string table, decimal[][] left, decimal[][] right,
        Func<int, string> name, Action<string> add)
    {
        int count = Math.Min(left.Length, right.Length);
        for (int id = 0; id < count; id++)
        {
            if (left[id].Length != right[id].Length)
            {
                add($"{table}[{name(id)}].count golden={left[id].Length} actual={right[id].Length}");
                continue;
            }
            for (int index = 0; index < left[id].Length; index++)
            {
                if (!SameDecimal(left[id][index], right[id][index]))
                {
                    add($"{table}[{name(id)}][{index}] golden={left[id][index]} actual={right[id][index]}");
                    break;
                }
            }
        }
        if (left.Length != right.Length)
            add($"{table}.count golden={left.Length} actual={right.Length}");
    }

    private static void MonsterTable(Rules expected, Rules actual, Action<string> add)
    {
        MonsterRules?[] left = expected.MonsterTable;
        MonsterRules?[] right = actual.MonsterTable;
        int count = Math.Min(left.Length, right.Length);
        for (int id = 0; id < count; id++)
        {
            MonsterRules? a = left[id];
            MonsterRules? b = right[id];
            if (a == null || b == null)
            {
                if (a != b)
                    add($"monsters[{id}] golden={(a == null ? "null" : "rules")} actual={(b == null ? "null" : "rules")}");
                continue;
            }
            string monsterName = id < Registry.MonsterCount
                ? Registry.Monster(a.MonsterId).NativeId
                : a.MonsterId.ToString();
            string prefix = $"monsters[{id}:{monsterName}]";
            if (a.MonsterId != b.MonsterId)
                add($"{prefix}.MonsterId golden={a.MonsterId} actual={b.MonsterId}");
            Diff(nameof(MonsterRules.InitialNode), a.InitialNode, b.InitialNode, prefix, add);
            Diff(nameof(MonsterRules.PendingInitialMoveNode), a.PendingInitialMoveNode,
                b.PendingInitialMoveNode, prefix, add);
            Diff(nameof(MonsterRules.InitialMoveLogged), a.InitialMoveLogged, b.InitialMoveLogged,
                prefix, add);
            Diff(nameof(MonsterRules.StunnedNode), a.StunnedNode, b.StunnedNode, prefix, add);
            Diff(nameof(MonsterRules.ReviveNode), a.ReviveNode, b.ReviveNode, prefix, add);
            Diff(nameof(MonsterRules.InitialHpMin), a.InitialHpMin, b.InitialHpMin, prefix, add);
            Diff(nameof(MonsterRules.InitialHpMax), a.InitialHpMax, b.InitialHpMax, prefix, add);
            DiffInts(nameof(MonsterRules.StaticInts), a.StaticInts, b.StaticInts, prefix, add);
            DiffInts(nameof(MonsterRules.RootPowerAmounts), a.RootPowerAmounts,
                b.RootPowerAmounts, prefix, add);
            DiffInts(nameof(MonsterRules.InitialAiRegisters), a.InitialAiRegisters,
                b.InitialAiRegisters, prefix, add);
            if (a.Nodes.Length != b.Nodes.Length)
            {
                add($"{prefix}.Nodes.count golden={a.Nodes.Length} actual={b.Nodes.Length}");
                continue;
            }
            for (int node = 0; node < a.Nodes.Length; node++)
                CompareNode(prefix, node, a.Nodes[node], b.Nodes[node], add);
        }
        if (left.Length != right.Length)
            add($"monsters.count golden={left.Length} actual={right.Length}");
    }

    private static void CompareNode(string prefix, int id, MonsterNode a, MonsterNode b,
        Action<string> add)
    {
        string node = $"{prefix}.Nodes[{id}:{a.NativeId}]";
        Diff(nameof(MonsterNode.NativeId), a.NativeId, b.NativeId, node, add);
        Diff(nameof(MonsterNode.Kind), a.Kind, b.Kind, node, add);
        Diff(nameof(MonsterNode.FollowUp), a.FollowUp, b.FollowUp, node, add);
        Diff(nameof(MonsterNode.MustPerformOnceBeforeTransitioning),
            a.MustPerformOnceBeforeTransitioning, b.MustPerformOnceBeforeTransitioning, node, add);
        Diff(nameof(MonsterNode.ConditionalTarget), a.ConditionalTarget, b.ConditionalTarget, node, add);
        if (a.Attacks.Length != b.Attacks.Length)
        {
            add($"{node}.Attacks.count golden={a.Attacks.Length} actual={b.Attacks.Length}");
        }
        else
        {
            for (int attack = 0; attack < a.Attacks.Length; attack++)
            {
                if (a.Attacks[attack] != b.Attacks[attack])
                    add($"{node}.Attacks[{attack}] golden={a.Attacks[attack]} actual={b.Attacks[attack]}");
            }
        }
        if (a.Options.Length != b.Options.Length)
        {
            add($"{node}.Options.count golden={a.Options.Length} actual={b.Options.Length}");
        }
        else
        {
            for (int option = 0; option < a.Options.Length; option++)
            {
                RandomBranchOption left = a.Options[option];
                RandomBranchOption right = b.Options[option];
                if (left.Target != right.Target
                    || BitConverter.SingleToInt32Bits(left.BaseWeight)
                        != BitConverter.SingleToInt32Bits(right.BaseWeight)
                    || left.RepeatType != right.RepeatType
                    || left.MaxTimes != right.MaxTimes
                    || left.Cooldown != right.Cooldown)
                {
                    add($"{node}.Options[{option}] golden={left} actual={right}");
                }
            }
        }
    }

    private static void SpawnTable(Rules expected, Rules actual, Action<string> add)
    {
        KeyValuePair<ushort, int>[] left = [.. expected.SpawnRuleTable];
        KeyValuePair<ushort, int>[] right = [.. actual.SpawnRuleTable];
        Array.Sort(left, static (a, b) => a.Key.CompareTo(b.Key));
        Array.Sort(right, static (a, b) => a.Key.CompareTo(b.Key));
        if (left.Length != right.Length)
        {
            add($"spawnRules.count golden={left.Length} actual={right.Length}");
            return;
        }
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index].Key != right[index].Key || left[index].Value != right[index].Value)
            {
                add($"spawnRules[{index}] golden={left[index].Key}->{left[index].Value} actual={right[index].Key}->{right[index].Value}");
                return;
            }
        }
    }

    private static void PoolTable(Rules expected, Rules actual, Action<string> add)
    {
        string[] leftKeys = [.. expected.CardPoolTable.Keys];
        string[] rightKeys = [.. actual.CardPoolTable.Keys];
        Array.Sort(leftKeys, StringComparer.Ordinal);
        Array.Sort(rightKeys, StringComparer.Ordinal);
        if (!leftKeys.AsSpan().SequenceEqual(rightKeys))
        {
            add($"cardPools.keys golden=[{string.Join(',', leftKeys)}] actual=[{string.Join(',', rightKeys)}]");
            return;
        }
        foreach (string key in leftKeys)
        {
            ushort[] left = expected.CardPoolTable[key].Cards;
            ushort[] right = actual.CardPoolTable[key].Cards;
            if (!left.AsSpan().SequenceEqual(right))
                add($"cardPools[{key}] golden=[{string.Join(',', left.Select(id => Registry.Card(id).NativeId))}] actual=[{string.Join(',', right.Select(id => Registry.Card(id).NativeId))}]");
        }
    }

    private static void UShortTable(string table, ushort[] left, ushort[] right, Action<string> add)
    {
        if (!left.AsSpan().SequenceEqual(right))
            add($"{table} golden=[{string.Join(',', left)}] actual=[{string.Join(',', right)}]");
    }

    private static void BoolTable(string table, bool[] left, bool[] right, Func<int, string> name,
        Action<string> add)
    {
        if (left.Length != right.Length)
        {
            add($"{table}.count golden={left.Length} actual={right.Length}");
            return;
        }
        for (int id = 0; id < left.Length; id++)
        {
            if (left[id] != right[id])
            {
                add($"{table}[{name(id)}] golden={left[id]} actual={right[id]}");
                return;
            }
        }
    }

    private static void StringArray(string table, string[] left, string[] right, Action<string> add)
    {
        if (!left.AsSpan().SequenceEqual(right))
            add($"{table} golden=[{string.Join('\u001f', left)}] actual=[{string.Join('\u001f', right)}]");
    }

    private static void DiffInts(string field, int[] left, int[] right, string prefix, Action<string> add)
    {
        if (!left.AsSpan().SequenceEqual(right))
            add($"{prefix}.{field} golden=[{string.Join(',', left)}] actual=[{string.Join(',', right)}]");
    }

    private static void DiffDecimals(string field, decimal[] left, decimal[] right, string prefix,
        Action<string> add)
    {
        if (left.Length != right.Length)
        {
            add($"{prefix}.{field}.count golden={left.Length} actual={right.Length}");
            return;
        }
        for (int index = 0; index < left.Length; index++)
        {
            if (!SameDecimal(left[index], right[index]))
            {
                add($"{prefix}.{field}[{index}] golden={left[index]} actual={right[index]}");
                return;
            }
        }
    }

    private static bool SameDecimal(decimal left, decimal right)
    {
        Span<int> leftBits = stackalloc int[4];
        Span<int> rightBits = stackalloc int[4];
        decimal.GetBits(left, leftBits);
        decimal.GetBits(right, rightBits);
        return leftBits.SequenceEqual(rightBits);
    }

    private static void Diff<T>(string field, T left, T right, string prefix, Action<string> add)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
            add($"{prefix}.{field} golden={left} actual={right}");
    }

    private static void Scalar<T>(string field, T left, T right, Action<string> add)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
            add($"{field} golden={left} actual={right}");
    }
}
