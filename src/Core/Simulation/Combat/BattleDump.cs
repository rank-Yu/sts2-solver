using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Field-level text dump of a <see cref="Battle"/> for differential reports. Only BCL types are
/// used; the dump includes the byte offset of every field so a mismatch can be mapped back to the
/// state layout. Ported from CombatSolver <c>src/Flash/Bridge/FlashBattleDump.cs</c> at
/// <c>a62f1850</c> (MIT, Copyright (c) 2026 Torch1230 and contributors), extended with offsets and
/// keyed differences.
/// </summary>
public static class BattleDump
{
    /// <summary>All field records in layout order, rendered as <c>offset key=value</c>.</summary>
    public static List<string> Lines(in Battle battle)
        => [.. Records(in battle).Select(record => $"{record.Offset:00000} {record.Key}={record.Value}")];

    /// <summary>
    /// Compares two states field by field. The result names the differing fields with their byte
    /// offsets; when every named field is equal but the byte images differ, the first differing byte
    /// is reported as an undumped field or padding.
    /// </summary>
    public static List<string> Diff(in Battle expected, in Battle actual, string expectedName, string actualName)
    {
        List<Record> left = Records(in expected);
        List<Record> right = Records(in actual);
        Dictionary<string, Record> rightByKey = new(right.Count, StringComparer.Ordinal);
        foreach (Record record in right)
            rightByKey[record.Key] = record;
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Record record in left)
        {
            seen.Add(record.Key);
            if (!rightByKey.TryGetValue(record.Key, out Record other))
            {
                result.Add($"+{record.Offset:00000} {record.Key}: {expectedName}=[{record.Value}] {actualName}=<missing>");
                continue;
            }
            if (!string.Equals(record.Value, other.Value, StringComparison.Ordinal))
                result.Add($"+{Math.Min(record.Offset, other.Offset):00000} {record.Key}: {expectedName}=[{record.Value}] {actualName}=[{other.Value}]");
        }
        foreach (Record record in right)
        {
            if (!seen.Contains(record.Key))
                result.Add($"+{record.Offset:00000} {record.Key}: {expectedName}=<missing> {actualName}=[{record.Value}]");
        }
        if (result.Count != 0)
            return result;

        ReadOnlySpan<byte> leftBytes = Fingerprint.Bytes(in expected);
        ReadOnlySpan<byte> rightBytes = Fingerprint.Bytes(in actual);
        for (int index = 0; index < leftBytes.Length; index++)
        {
            if (leftBytes[index] == rightBytes[index])
                continue;
            return [$"+{index:00000} <byte>: {expectedName}=[{leftBytes[index]:X2}] {actualName}=[{rightBytes[index]:X2}]（未转储字段或 padding）"];
        }
        return result;
    }

    private readonly record struct Record(int Offset, string Key, string Value);

    private static List<Record> Records(in Battle battle)
    {
        List<Record> records = [];
        Add(records, nameof(Battle.Round), battle.Round);
        Add(records, nameof(Battle.PlayerTurn), battle.PlayerTurn);
        Add(records, nameof(Battle.ShuffleEventCount), battle.ShuffleEventCount);
        Add(records, nameof(Battle.NextCombatId), battle.NextCombatId);
        Add(records, nameof(Battle.Energy), battle.Energy);
        Add(records, nameof(Battle.Stars), battle.Stars);
        Add(records, nameof(Battle.Gold), battle.Gold);
        Add(records, nameof(Battle.Side), battle.Side);
        Add(records, nameof(Battle.Phase), battle.Phase);
        Add(records, nameof(Battle.Outcome), battle.Outcome);
        Add(records, nameof(Battle.Flags), battle.Flags);
        Add(records, nameof(Battle.HandCount), battle.HandCount);
        Add(records, nameof(Battle.DrawCount), battle.DrawCount);
        Add(records, nameof(Battle.DiscardCount), battle.DiscardCount);
        Add(records, nameof(Battle.ExhaustCount), battle.ExhaustCount);
        Add(records, nameof(Battle.PlayCount), battle.PlayCount);
        Add(records, nameof(Battle.MaxHandSize), battle.MaxHandSize);
        Add(records, nameof(Battle.EnemyCount), battle.EnemyCount);
        Add(records, nameof(Battle.RosterCount), battle.RosterCount);
        Add(records, nameof(Battle.PowerCount), battle.PowerCount);
        Add(records, nameof(Battle.RelicStateCount), battle.RelicStateCount);
        Add(records, nameof(Battle.OrbCount), battle.OrbCount);
        Add(records, nameof(Battle.OrbCapacity), battle.OrbCapacity);
        Add(records, nameof(Battle.PotionSlotCount), battle.PotionSlotCount);
        Add(records, nameof(Battle.DecimalVarCount), battle.DecimalVarCount);
        AddCreature(records, nameof(Battle.Player), battle.Player);
        AddCreature(records, nameof(Battle.Osty), battle.Osty);

        int counterOffset = FieldOffset(nameof(Battle.Counters));
        for (int counter = 0; counter < Limits.Counters; counter++)
        {
            int value = battle.Counters[counter];
            if (value != 0)
                records.Add(new(counterOffset + counter * sizeof(int), $"Counter[{(Counter)counter}]", value.ToString()));
        }

        int rngOffset = FieldOffset(nameof(Battle.Rng));
        int rngSize = Marshal.SizeOf<RngState>();
        for (int stream = 0; stream < Limits.RngStreams; stream++)
        {
            RngState rng = battle.Rng[stream];
            records.Add(new(rngOffset + stream * rngSize, $"Rng[{(RngStream)stream}]",
                $"{rng.Counter}:{rng.S0:X}:{rng.S1:X}:{rng.S2:X}:{rng.S3:X}"));
        }

        for (int slot = 0; slot < Limits.DetachedCards; slot++)
        {
            Card card = battle.DetachedCards[slot];
            if (!card.IsDetachedCardPresent)
                continue;
            AddCard(records, Offset(nameof(Battle.DetachedCards), slot * Marshal.SizeOf<Card>()), $"Detached[{slot}]", card);
        }
        for (int slot = 0; slot < battle.DecimalVarCount && slot < Limits.DecimalCardVars; slot++)
            records.Add(new(FieldOffset(nameof(Battle.DecimalVars)) + slot * sizeof(decimal),
                $"DecimalVar[{slot}]", battle.DecimalVars[slot].ToString()));

        int rosterOffset = FieldOffset(nameof(Battle.Roster));
        for (int position = 0; position < battle.RosterCount; position++)
            records.Add(new(rosterOffset + position, $"Roster[{position}]", battle.Roster[position].ToString()));
        int potionOffset = FieldOffset(nameof(Battle.Potions));
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            records.Add(new(potionOffset + slot * sizeof(ushort), $"Potion[{slot}]",
                potion == 0 ? "-" : Registry.Potion((ushort)(potion - 1)).NativeId));
        }

        int enemyOffset = FieldOffset(nameof(Battle.Enemies));
        int enemySize = Marshal.SizeOf<Enemy>();
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            Enemy enemy = battle.Enemies[index];
            string prefix = $"Enemy[{index}]";
            int offset = enemyOffset + index * enemySize;
            AddCreature(records, prefix + ".Creature",
                enemy.Creature,
                offset + FieldOffset<Enemy>(nameof(Enemy.Creature)));
            Add(records, prefix + ".MonsterId", $"{Registry.Monster(enemy.MonsterId).NativeId}({enemy.MonsterId})",
                offset + FieldOffset<Enemy>(nameof(Enemy.MonsterId)));
            Add(records, prefix + ".Slot", enemy.Slot, offset + FieldOffset<Enemy>(nameof(Enemy.Slot)));
            Add(records, prefix + ".DeathPhase", enemy.DeathPhase, offset + FieldOffset<Enemy>(nameof(Enemy.DeathPhase)));
            Add(records, prefix + ".CombatId", enemy.CombatId, offset + FieldOffset<Enemy>(nameof(Enemy.CombatId)));
            Add(records, prefix + ".Move", enemy.Move, offset + FieldOffset<Enemy>(nameof(Enemy.Move)));
            Add(records, prefix + ".Flags", enemy.Flags, offset + FieldOffset<Enemy>(nameof(Enemy.Flags)));
            Add(records, prefix + ".LoggedMoves", enemy.LoggedMoves, offset + FieldOffset<Enemy>(nameof(Enemy.LoggedMoves)));
            Add(records, prefix + ".ForcedMove", enemy.ForcedMove, offset + FieldOffset<Enemy>(nameof(Enemy.ForcedMove)));
            Add(records, prefix + ".Rules", enemy.Rules, offset + FieldOffset<Enemy>(nameof(Enemy.Rules)));
            Add(records, prefix + ".PoweredAttackHitsThisTurn", enemy.PoweredAttackHitsThisTurn,
                offset + FieldOffset<Enemy>(nameof(Enemy.PoweredAttackHitsLow)));
            Add(records, prefix + ".EverUsedMoves", enemy.EverUsedMoves,
                offset + FieldOffset<Enemy>(nameof(Enemy.EverUsedMoves)));
            Add(records, prefix + ".SteamPhase", enemy.SteamPhase, offset + FieldOffset<Enemy>(nameof(Enemy.SteamPhase)));
            int recentOffset = offset + FieldOffset<Enemy>(nameof(Enemy.RecentMoves));
            int logged = Math.Min((int)enemy.LoggedMoves, Limits.EnemyRecentMoves);
            for (int position = 0; position < logged; position++)
                records.Add(new(recentOffset + position, $"{prefix}.RecentMoves[{position}]", enemy.RecentMoves[position].ToString()));
            int aiOffset = offset + FieldOffset<Enemy>(nameof(Enemy.Ai));
            for (int register = 0; register < Limits.EnemyAiRegisters; register++)
                records.Add(new(aiOffset + register * sizeof(short), $"{prefix}.Ai[{register}]", enemy.Ai[register].ToString()));
        }

        int powerOffset = FieldOffset(nameof(Battle.Powers));
        int powerSize = Marshal.SizeOf<Power>();
        for (int index = 0; index < battle.PowerCount; index++)
        {
            Power power = battle.Powers[index];
            string prefix = $"Power[{index}]";
            int offset = powerOffset + index * powerSize;
            Add(records, prefix + ".Id", $"{Registry.Power(power.Id).NativeId}({power.Id})", offset + FieldOffset<Power>(nameof(Power.Id)));
            Add(records, prefix + ".Owner", power.Owner, offset + FieldOffset<Power>(nameof(Power.Owner)));
            Add(records, prefix + ".Applier", power.Applier, offset + FieldOffset<Power>(nameof(Power.Applier)));
            Add(records, prefix + ".Target", power.Target, offset + FieldOffset<Power>(nameof(Power.Target)));
            Add(records, prefix + ".Flags", power.Flags, offset + FieldOffset<Power>(nameof(Power.Flags)));
            Add(records, prefix + ".Aux2", power.Aux2, offset + FieldOffset<Power>(nameof(Power.Aux2)));
            Add(records, prefix + ".Amount", power.Amount, offset + FieldOffset<Power>(nameof(Power.Amount)));
            Add(records, prefix + ".AmountOnTurnStart", power.AmountOnTurnStart,
                offset + FieldOffset<Power>(nameof(Power.AmountOnTurnStart)));
            Add(records, prefix + ".Aux0", power.Aux0, offset + FieldOffset<Power>(nameof(Power.Aux0)));
            Add(records, prefix + ".Aux1", power.Aux1, offset + FieldOffset<Power>(nameof(Power.Aux1)));
        }

        int relicOffset = FieldOffset(nameof(Battle.RelicStates));
        for (int index = 0; index < battle.RelicStateCount; index++)
        {
            RelicState state = battle.RelicStates[index];
            records.Add(new Record(relicOffset + index * 8, $"RelicStates[{index}].Counter", state.Counter.ToString()));
            records.Add(new Record(relicOffset + index * 8 + 4, $"RelicStates[{index}].Aux", state.Aux.ToString()));
            records.Add(new Record(FieldOffset(nameof(Battle.RelicStateFlags)) + index,
                $"RelicStates[{index}].Flags", battle.RelicStateFlags[index].ToString()));
        }

        int orbOffset = FieldOffset(nameof(Battle.Orbs));
        int orbSize = Marshal.SizeOf<Orb>();
        for (int index = 0; index < battle.OrbCount; index++)
        {
            Orb orb = battle.Orbs[index];
            string prefix = $"Orb[{index}]";
            int offset = orbOffset + index * orbSize;
            Add(records, prefix + ".Id", $"{Registry.Orb(orb.Id).NativeId}({orb.Id})", offset + FieldOffset<Orb>(nameof(Orb.Id)));
            Add(records, prefix + ".Aux", orb.Aux, offset + FieldOffset<Orb>(nameof(Orb.Aux)));
        }

        int cardOffset = FieldOffset(nameof(Battle.Cards));
        int cardSize = Marshal.SizeOf<Card>();
        foreach (Pile pile in Enum.GetValues<Pile>())
        {
            ReadOnlySpan<Card> cards = Piles.ReadCards(in battle, pile);
            int start = Piles.Start(in battle, pile);
            for (int index = 0; index < cards.Length; index++)
                AddCard(records, cardOffset + (start + index) * cardSize, $"{pile}[{index}]", cards[index]);
        }

        if ((battle.Flags & BattleFlags.HistoryCourseCardPresent) != 0)
        {
            AddCard(records, FieldOffset(nameof(Battle.HistoryCourseCard)), "HistoryCourseCard",
                battle.HistoryCourseCard);
        }
        return records;
    }

    private static void AddCreature(List<Record> records, string key, in Creature creature, int offset = -1)
    {
        if (offset < 0)
            offset = FieldOffset(key);
        Add(records, key + ".CurrentHp", creature.CurrentHp, offset + FieldOffset<Creature>(nameof(Creature.CurrentHp)));
        Add(records, key + ".MaxHp", creature.MaxHp, offset + FieldOffset<Creature>(nameof(Creature.MaxHp)));
        Add(records, key + ".Block", creature.Block, offset + FieldOffset<Creature>(nameof(Creature.Block)));
        Add(records, key + ".Flags", creature.Flags, offset + FieldOffset<Creature>(nameof(Creature.Flags)));
    }

    private static void AddCard(List<Record> records, int offset, string key, in Card card)
    {
        Add(records, key + ".Id", $"{Registry.Card(card.Id).NativeId}({card.Id})", offset + FieldOffset<Card>(nameof(Card.Id)));
        Add(records, key + ".Upgrade", card.Upgrade, offset + FieldOffset<Card>(nameof(Card.Upgrade)));
        Add(records, key + ".Flags", card.Flags, offset + FieldOffset<Card>(nameof(Card.Flags)));
        Add(records, key + ".Keywords", card.Keywords, offset + FieldOffset<Card>(nameof(Card.Keywords)));
        Add(records, key + ".KeywordsAddedFromOutside", card.KeywordsAddedFromOutside, offset + FieldOffset<Card>(nameof(Card.KeywordsAddedFromOutside)));
        Add(records, key + ".KeywordsRemovedFromOutside", card.KeywordsRemovedFromOutside, offset + FieldOffset<Card>(nameof(Card.KeywordsRemovedFromOutside)));
        Add(records, key + ".BaseCost", card.BaseCost, offset + FieldOffset<Card>(nameof(Card.BaseCost)));
        Add(records, key + ".BaseStarCost", card.BaseStarCost, offset + FieldOffset<Card>(nameof(Card.BaseStarCost)));
        Add(records, key + ".ReplayCount", card.ReplayCount, offset + FieldOffset<Card>(nameof(Card.ReplayCount)));
        Add(records, key + ".CostState", card.CostState, offset + FieldOffset<Card>(nameof(Card.CostState)));
        Add(records, key + ".ModifierCount", card.EnergyModifierCount, offset + FieldOffset<Card>(nameof(Card.ModifierCount)));
        Add(records, key + ".TemporaryStarCost", card.TemporaryStarCost, offset + FieldOffset<Card>(nameof(Card.TemporaryStarCost)));
        Add(records, key + ".TemporaryStarCostStack", card.TemporaryStarCostStack,
            offset + FieldOffset<Card>(nameof(Card.TemporaryStarCostStack)));
        Add(records, key + ".EnchantmentId", card.EnchantmentId, offset + FieldOffset<Card>(nameof(Card.EnchantmentId)));
        Add(records, key + ".AfflictionId", card.AfflictionId, offset + FieldOffset<Card>(nameof(Card.AfflictionId)));
        Add(records, key + ".EnchantmentAmount", card.EnchantmentAmount, offset + FieldOffset<Card>(nameof(Card.EnchantmentAmount)));
        Add(records, key + ".EnchantmentPrivate", card.EnchantmentPrivate, offset + FieldOffset<Card>(nameof(Card.EnchantmentPrivate)));
        Add(records, key + ".AfflictionAmount", card.AfflictionAmount, offset + FieldOffset<Card>(nameof(Card.AfflictionAmount)));
        Add(records, key + ".Var0", card.Var0, offset + FieldOffset<Card>(nameof(Card.Var0)));
        Add(records, key + ".Var1", card.Var1, offset + FieldOffset<Card>(nameof(Card.Var1)));
        Add(records, key + ".Private", card.Private, offset + FieldOffset<Card>(nameof(Card.Private)));
        Add(records, key + ".DampenOriginalUpgrade", card.DampenOriginalUpgrade,
            offset + FieldOffset<Card>(nameof(Card.DampenOriginalUpgrade)));
        Add(records, key + ".TransientMark", card.TransientMark, offset + FieldOffset<Card>(nameof(Card.TransientMark)));
        int modifierOffset = offset + FieldOffset<Card>(nameof(Card.EnergyModifiers));
        int modifierSize = Marshal.SizeOf<CostModifier>();
        Add(records, key + ".IsDupe", card.IsDupe, offset + FieldOffset<Card>(nameof(Card.ModifierCount)));
        Add(records, key + ".HistoryCourseRole", card.HistoryCourseRole, offset + FieldOffset<Card>(nameof(Card.ModifierCount)));
        for (int index = 0; index < card.EnergyModifierCount; index++)
        {
            CostModifier modifier = card.EnergyModifiers[index];
            records.Add(new(modifierOffset + index * modifierSize, $"{key}.EnergyModifiers[{index}].Amount", modifier.Amount.ToString()));
            records.Add(new(modifierOffset + index * modifierSize + FieldOffset<CostModifier>(nameof(CostModifier.Packed)),
                $"{key}.EnergyModifiers[{index}].Type", modifier.Type.ToString()));
            records.Add(new(modifierOffset + index * modifierSize + FieldOffset<CostModifier>(nameof(CostModifier.Packed)),
                $"{key}.EnergyModifiers[{index}].Packed", modifier.Packed.ToString()));
        }
    }

    private static void Add<T>(List<Record> records, string key, T value)
        => records.Add(new(FieldOffset(key), key, value?.ToString() ?? "null"));

    private static void Add<T>(List<Record> records, string key, T value, int offset)
        => records.Add(new(offset, key, value?.ToString() ?? "null"));

    private static void Add(List<Record> records, string key, string value)
        => records.Add(new(FieldOffset(key), key, value));

    private static void Add(List<Record> records, string key, string value, int offset)
        => records.Add(new(offset, key, value));

    private static int FieldOffset(string key)
    {
        string name = key.Split('.')[0];
        return Marshal.OffsetOf<Battle>(name).ToInt32();
    }

    private static int FieldOffset<T>(string name) where T : struct
        => Marshal.OffsetOf<T>(name).ToInt32();

    private static int Offset(string key, int relative) => FieldOffset(key) + relative;
}
