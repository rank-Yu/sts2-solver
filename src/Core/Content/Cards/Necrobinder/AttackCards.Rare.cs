using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Necrobinder;

/// <summary>
/// <c>TimesUp</c>: calculated attack with CalculationBase 0 + ExtraDamage 1 × the target's Doom
/// (<c>CalculatedVarSpecRegistry.TimesUp</c>).
/// </summary>
[ContentId("TIMES_UP")]
public sealed class TimesUp : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        int doom = Kernel.GetAmount(x.B, x.Target, Registry.PowerId<Powers.DoomPower>());
        CardEffects.Attack(ref x, NecrobinderContent.Calculated(ref x, 0, 1, doom));
    }
}

/// <summary>
/// <c>SoulStorm</c>: calculated attack with CalculationBase + ExtraDamage × Soul cards in the exhaust
/// pile (<c>CalculatedVarSpecRegistry.SoulStorm</c>).
/// </summary>
[ContentId("SOUL_STORM")]
public sealed class SoulStorm : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override void OnPlay(ref Exec x)
    {
        int souls = CountExhaustSouls(x.B, x.R);
        CardEffects.Attack(ref x, NecrobinderContent.Calculated(ref x, 0, 1, souls));
    }

    private static int CountExhaustSouls(in Battle battle, Rules rules)
    {
        int start = Piles.Start(battle, Pile.Exhaust);
        int count = Piles.Count(battle, Pile.Exhaust);
        int souls = 0;
        for (int offset = 0; offset < count; offset++)
        {
            ref readonly Card card = ref battle.Cards[start + offset];
            if (rules.Card(card.Id, card.Upgrade).IsSoul)
                souls++;
        }
        return souls;
    }
}

/// <summary>
/// <c>Squeeze</c>: Osty attack whose calculated damage is CalculationBase + ExtraDamage × the number of
/// other OstyAttack cards in the combat piles (<c>CalculatedVarSpecRegistry.Squeeze</c>).
/// </summary>
[ContentId("SQUEEZE")]
public sealed class Squeeze : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    /// <summary>
    /// Native <c>CalculatedDamageVar</c> for Thrash reading this card's damage: CalculationBase +
    /// ExtraDamage × the number of other Osty-attack cards in every combat pile.
    /// </summary>
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target,
        out decimal damage)
    {
        int others = NecrobinderContent.CountOstyAttackCards(x.B, x.R) - 1;
        CardStats stats = x.R.Card(self.Id, self.Upgrade);
        if (stats.Vars.Length < 2)
            throw new UnsupportedException(
                $"SQUEEZE 规则表只有 {stats.Vars.Length} 个动态变量，需要 CalculationBase 和 ExtraDamage。");
        decimal baseValue = stats.Vars[0] + DecimalVars.Delta(in x.B, in self, 0);
        decimal extra = stats.Vars[1] + DecimalVars.Delta(in x.B, in self, 1);
        damage = baseValue + extra * others;
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        if (!NecrobinderContent.HasOsty(x.B))
            return;
        int others = NecrobinderContent.CountOstyAttackCards(x.B, x.R) - 1;
        decimal damage = NecrobinderContent.Calculated(ref x, 0, 1, others);
        NecrobinderContent.OstyAttack(ref x, damage);
    }
}

/// <summary>
/// <c>Misery</c>: deal the attack, then copy every debuff on the selected target to every other
/// hittable enemy. Existing engine: <c>BespokeCardMirrors.MiseryOnPlay</c>. The clone carries the
/// original amount and applier. Temporary powers are the existing engine's <c>ITemporaryPower</c>
/// merge path (temporary → internal power amount); Sts2Solver has no per-instance dynamic-variable copy,
/// so a target carrying one is an explicit unsupported boundary rather than a half-clone.
/// </summary>
[ContentId("MISERY")]
public sealed class Misery : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        Span<ushort> ids = stackalloc ushort[Limits.Powers];
        Span<int> amounts = stackalloc int[Limits.Powers];
        Span<byte> appliers = stackalloc byte[Limits.Powers];
        Span<byte> targets = stackalloc byte[Limits.Powers];
        int count = 0;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Owner != x.Target || power.Amount == 0)
                continue;
            PowerDef def = Registry.Power(power.Id);
            if (def.TypeForAmount(power.Amount) != PowerType.Debuff)
                continue;
            ids[count] = power.Id;
            amounts[count] = power.Amount;
            appliers[count] = power.Applier;
            targets[count] = power.Target;
            count++;
        }
        // BespokeCardMirrors.MiseryOnPlay: a temporary power's amount is also merged into the
        // internally applied stat when that stat is itself among the cloned debuffs. The temporary
        // power row is still cloned (the native ApplyClonedPower skips BeforeApplied), exactly as the
        // existing engine does.
        for (int index = 0; index < count; index++)
        {
            PowerDef def = Registry.Power(ids[index]);
            if (!def.IsTemporaryPower || def.InternallyAppliedPowerId is not { } internalId)
                continue;
            if (!Registry.TryPower(internalId, out ushort internalPower))
                continue;
            for (int targetIndex = 0; targetIndex < count; targetIndex++)
            {
                if (ids[targetIndex] == internalPower)
                {
                    amounts[targetIndex] = checked(amounts[targetIndex] + amounts[index]);
                    break;
                }
            }
        }
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        byte original = x.Target;
        // The existing engine iterates a HittableEnemies snapshot; a cloned debuff can kill its
        // target (Sleight of Flesh) and compact the live roster, which must not skip later enemies.
        Span<byte> enemies = stackalloc byte[Limits.Enemies];
        int rosterCount = x.B.RosterCount;
        for (int position = 0; position < rosterCount; position++)
            enemies[position] = Kernel.EnemyOwner(x.B.Roster[position]);
        for (int position = 0; position < rosterCount; position++)
        {
            byte enemy = enemies[position];
            if (enemy == original || !Kernel.IsHittable(x.B, enemy))
                continue;
            for (int item = 0; item < count; item++)
            {
                Kernel.ApplyPowerPreservingTarget(ref x, enemy, ids[item], amounts[item],
                    appliers[item], targets[item]);
                if (x.NeedsChoice)
                    return;
                Kernel.ResolvePowerAmountChanges(ref x);
                if (x.NeedsChoice)
                    return;
            }
        }
    }
}

/// <summary>
/// <c>Hang</c>: attack, then apply HangPower with max(2, current amount) and the 999,999,999 cap
/// (<c>CorePowerSupport</c>'s Hang case).
/// </summary>
[ContentId("HANG")]
public sealed class Hang : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack
        // and before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        if (x.NeedsChoice || x.Target == Limits.NoOwner)
            return;
        ushort hang = Registry.PowerId<Powers.HangPower>();
        int current = Kernel.GetAmount(x.B, x.Target, hang);
        int amount = Math.Max(2, current);
        if (current + amount > 999_999_999)
            amount = Math.Max(0, 999_999_999 - current);
        Kernel.ApplyPower(ref x, x.Target, hang, amount, Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>Eradicate</c>: an X-cost attack whose hit count is the resolved X value
/// (<c>GeneralCardMirrors</c> uses <c>ResolveEnergyXValue</c> for <c>AttackCommand.WithHitCount</c>).
/// </summary>
[ContentId("ERADICATE")]
public sealed class Eradicate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x, NecrobinderContent.Var(ref x, 0), Kernel.ResolveEnergyXValue(ref x));
}

/// <summary>
/// <c>BansheesCry</c>: attack all enemies; its cost drops by Energy for every Ethereal card played and
/// for every Ethereal play already in this combat when it enters combat
/// (<c>AfterCardPlayedMirrors.HandleBansheesCry</c>, <c>SimulatedCombatState.CardEventHistory</c>).
/// </summary>
[ContentId("BANSHEES_CRY")]
public sealed class BansheesCry : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Energy"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if ((uint)self.Index >= (uint)x.B.CardCount)
            return;
        ref Card card = ref x.B.Cards[self.Index];
        if (card.Id != Id || !Kernel.HasKeyword(x.B, play.Card, Keywords.Ethereal))
            return;
        int energy = (int)x.R.Card(card.Id, card.Upgrade).Vars[1];
        if (energy > 0)
        {
            Kernel.AddEnergyCostModifier(ref card, -energy, CostType.Relative,
                CostExpiration.EndOfCombat, reduceOnly: false);
        }
    }

    public override void AfterEnteredCombat(ref Exec x, Pile pile, int index)
    {
        ref Card card = ref Piles.At(ref x.B, pile, index);
        if (card.Id != Id || card.IsClone)
            return;
        int plays = x.B.Counter(Counter.EtherealPlays);
        int energy = (int)x.R.Card(card.Id, card.Upgrade).Vars[1];
        int reduction = plays * energy;
        if (reduction > 0)
        {
            Kernel.AddEnergyCostModifier(ref card, -reduction, CostType.Relative,
                CostExpiration.EndOfCombat, reduceOnly: false);
        }
    }
}

/// <summary>
/// <c>TheScythe</c>: attack with its accumulated Damage variable, then add the Increase variable to
/// the played card's Damage (and record the deck-version long-term growth:
/// <c>BespokeCardMirrors.TheScytheOnPlay</c>). The accumulated value is the imported Damage-variable
/// delta (Var0), so no private counter is needed.
/// </summary>
[ContentId("THE_SCYTHE")]
public sealed class TheScythe : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Increase"];

    // TheScythe.AfterDowngraded calls UpdateDamage() with IncreasedDamage; Var0 is that accumulator.
    public override bool PreservesVar0OnDowngrade => true;

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // The native OnPlay reads the live card's Increase after the attack (Dampen restore may have
        // upgraded the played copy in the meantime).
        Kernel.SyncCurrentPlayCard(ref x);
        int increase = (int)NecrobinderContent.Var(ref x, 1);
        CardEffects.AddCurrentCardVar(ref x, 0, increase);
        if ((x.Card.Flags & CardFlags.DeckCard) != 0)
        {
            x.B.AddCounter(Counter.LongTermResourceValue, increase);
            GrowthRewards.Add(ref x.B, GrowthRewardSource.TheScythe);
        }
    }
}
