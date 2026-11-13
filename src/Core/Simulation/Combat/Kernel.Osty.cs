namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Osty, the Necrobinder pet. Semantics follow <c>SimulatedCombatState.CardLifecycle</c>'s
/// <c>SummonOsty</c>/<c>HealOsty</c>/<c>GetOstyMaxHp</c> and <c>SimulatedCombatState.Doom</c>'s
/// <c>DoomKill</c>, which are the existing engine's authoritative implementations.
/// </summary>
public static partial class Kernel
{
    /// <summary>Mirrors <c>SimulatedCombatState.SummonOsty</c> (<c>OstyCmd.Summon</c>).</summary>
    public static void SummonOsty(ref Exec x, int amount)
    {
        if (amount <= 0)
            return;
        if (!Registry.TryPower("DIE_FOR_YOU_POWER", out ushort dieForYou))
            throw new UnsupportedException("Osty 需要 DieForYouPower 的 Sts2Solver 定义。");
        bool created = (x.B.Flags & BattleFlags.OstyPresent) == 0;
        if (created)
        {
            // CreatePredictedMonster consumes one CombatId before any HP is assigned. The native model
            // starts at 1/1 HP; the existing engine overwrites both before anything can observe them
            // (the DieForYouPower application has no HP reader), so Sts2Solver initializes the target HP
            // directly instead of hard-coding the model's initial value.
            x.B.NextCombatId = unchecked(x.B.NextCombatId + 1);
            x.B.Flags |= BattleFlags.OstyPresent | BattleFlags.OstyEverPresent;
            x.B.Osty = new Creature
            {
                MaxHp = amount,
                CurrentHp = amount,
                Flags = CreatureFlags.Present,
            };
            // OstyCmd.Summon applies DieForYouPower 1 with no applier for a fresh pet, after AddPet and
            // before SetMaxHp(amount).
            ApplyPower(ref x, Limits.OstyOwner, dieForYou, 1, Limits.NoOwner);
            if (x.NeedsChoice)
                return;
            return;
        }
        ref Creature osty = ref x.B.Osty;
        if (osty.IsAlive)
        {
            int newMax = Math.Clamp((int)Math.Min((long)osty.MaxHp + amount, Limits.ValueClamp), 1,
                Limits.ValueClamp);
            osty.MaxHp = newMax;
            osty.CurrentHp = Math.Min(newMax, (int)Math.Min((long)osty.CurrentHp + amount, Limits.ValueClamp));
        }
        else
        {
            // A dead pet keeps its object; the existing engine revives it and resets both HP values.
            osty.MaxHp = Math.Clamp(amount, 1, Limits.ValueClamp);
            osty.CurrentHp = amount;
        }
    }

    /// <summary>Mirrors <c>SimulatedCombatState.HealOsty</c>.</summary>
    public static void HealOsty(ref Exec x, int amount)
    {
        if ((x.B.Flags & BattleFlags.OstyPresent) == 0)
            throw new UnsupportedException("治疗 Osty 时它不存在。");
        ref Creature osty = ref x.B.Osty;
        osty.CurrentHp = Math.Min(osty.MaxHp,
            (int)Math.Min((long)osty.CurrentHp + Math.Max(0, amount), Limits.ValueClamp));
    }

    /// <summary>Mirrors <c>SimulatedCombatState.GetOstyMaxHp</c> for the single-player Sts2Solver state.</summary>
    public static int OstyMaxHp(in Battle battle)
        => (battle.Flags & BattleFlags.OstyPresent) == 0 ? 0 : battle.Osty.MaxHp;

    /// <summary>
    /// The creature holds a positive power that native keeps on it after its death
    /// (<c>ShouldPowerBeRemovedAfterOwnerDeath</c> false) and that refuses a fatal death (MinionPower).
    /// </summary>
    private static bool KeptPowerRefusesFatal(in Battle battle, byte owner)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != owner || power.Amount <= 0)
                continue;
            PowerDef def = Registry.Power(power.Id);
            if (!def.ShouldPowerBeRemovedAfterOwnerDeath
                && !def.ShouldOwnerDeathTriggerFatal(battle, new Listener(ListenerKind.Power, (byte)index, owner)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Mirrors <c>SimulatedCombatState.DoomKill</c>: kill the doomed creatures one at a time in
    /// order, complete each native death cleanup, then heal the player for every fatal kill through a
    /// supported <c>BookRepairKnife</c>.</summary>
    public static void DoomKill(ref Exec x, scoped ReadOnlySpan<byte> creatures)
    {
        if (creatures.IsEmpty)
            return;
        // Native DoomPower.DoomKill executes CreatureCmd.Kill for every creature and only then calls
        // Hook.AfterDiedToDoom; CreatureCmd.Kill has already run the creature's AfterDeath hook and
        // power removal by then. Complete the deferred death pass after each kill, exactly like the
        // existing engine's DoomKill, before the fatal count below reads the power table.
        // Native AfterDiedToDoom (BookRepairKnife) reads each dead creature's remaining powers. A creature
        // that leaves combat keeps the powers whose ShouldPowerBeRemovedAfterOwnerDeath is false on the
        // native object (MinionPower: a doomed minion is not a fatal kill), while this power table drops
        // every row of an enemy outside the roster. Record before the kills whether such a kept power
        // refuses a fatal death; an enemy still in the roster (Reattach) is read after the kills.
        Span<bool> keptNonFatal = stackalloc bool[creatures.Length];
        for (int index = 0; index < creatures.Length; index++)
            keptNonFatal[index] = IsEnemyOwner(creatures[index]) && KeptPowerRefusesFatal(x.B, creatures[index]);
        Span<byte> one = stackalloc byte[1];
        foreach (byte creature in creatures)
        {
            one[0] = creature;
            Kill(ref x, one, force: false);
            if (x.NeedsChoice)
                return;
            ApplyEnemyDeathPowers(ref x);
            if (x.NeedsChoice)
                return;
        }
        int fatal = 0;
        for (int index = 0; index < creatures.Length; index++)
        {
            byte creature = creatures[index];
            if (creature == Limits.PlayerOwner)
                continue;
            if (IsEnemyOwner(creature) && !InRoster(x.B, creature) && keptNonFatal[index])
                continue;
            if (TargetDeathTriggersFatal(x.B, creature))
                fatal++;
        }
        if (fatal <= 0 || !Registry.TryRelic("BOOK_REPAIR_KNIFE", out ushort knife))
            return;
        // Only a present, unmelted relic reads its Heal variable; a root without the relic does not
        // need the rule-table entry (K0 battles build partial tables).
        decimal heal = 0m;
        bool present = false;
        for (int index = 0; index < x.R.Relics.Count; index++)
        {
            Relic relic = x.B.GetRelic(x.R, index);
            if (relic.Id != knife || (relic.Flags & RelicFlags.Melted) != 0)
                continue;
            heal = x.R.RelicVar(knife, 0) * fatal;
            present = true;
            break;
        }
        if (!present)
            return;
        for (int index = 0; index < x.R.Relics.Count; index++)
        {
            Relic relic = x.B.GetRelic(x.R, index);
            if (relic.Id != knife || (relic.Flags & RelicFlags.Melted) != 0)
                continue;
            Heal(ref x, Limits.PlayerOwner, heal);
            if (x.NeedsChoice)
                return;
        }
    }
}

