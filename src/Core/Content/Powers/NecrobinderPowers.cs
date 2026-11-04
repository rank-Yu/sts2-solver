using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// <c>DieForYouPower</c>: while Osty is alive, a powered attack on its owner redirects the unblocked HP
/// loss to Osty (<c>Hook.ModifyUnblockedDamageTarget</c>; existing engine path
/// <c>CombatPredictionSimulator.TryDamageTarget</c>). The pet is created with one layer and keeps the
/// power through death (<c>ShouldPowerBeRemovedAfterOwnerDeath</c> false).
/// </summary>
[ContentId("DIE_FOR_YOU_POWER")]
public sealed class DieForYouPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;

    public override byte ModifyUnblockedDamageTarget(ref Exec x, Listener self, byte target,
        decimal amount, ValueProps props, byte dealer)
    {
        if (target != Limits.PlayerOwner || x.B.Osty.IsDead || !props.IsPoweredAttack())
            return target;
        return Limits.OstyOwner;
    }
}

/// <summary>
/// <c>DoomPower</c>: the enemy-side turn end kills every living participant whose HP is at or below the
/// total Doom on it (<c>BeforeSideTurnEndMirrors.HandleDoomPower</c>,
/// <c>EndTurnPowerSupport.TriggerEnemyDoom</c>); the player-side turn end kills its own owner through
/// the per-listener <c>TriggerRegular</c> case. Doom is applied by cards and counted by the evaluation.
/// </summary>
[ContentId("DOOM_POWER")]
public sealed class DoomPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        // BeforeSideTurnEndMirrors.HandleDoomPower: enemy side only, the owner must participate and be
        // alive, and only the first doomed owner in creature order starts the shared kill.
        if (Kernel.IsOverOrEnding(x.B) || side == Side.Player
            || !Kernel.Participates(participants, self.Owner)
            || x.B.Creature(self.Owner).IsDead)
        {
            return;
        }
        bool ownerDoomed = false;
        bool ownerIsFirst = false;
        Span<byte> doomed = stackalloc byte[Limits.Enemies + 2];
        int doomedCount = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            int doom = DoomFor(x.B, owner);
            bool isDoomed = x.B.Creature(owner).IsAlive && doom > 0 && x.B.Creature(owner).CurrentHp <= doom;
            if (isDoomed)
            {
                if (doomedCount == 0)
                    ownerIsFirst = owner == self.Owner;
                doomed[doomedCount++] = owner;
            }
            if (owner == self.Owner)
                ownerDoomed = isDoomed;
        }
        if (!ownerDoomed || !ownerIsFirst)
            return;
        Kernel.DoomKill(ref x, doomed[..doomedCount]);
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side == Side.Enemy || !participates)
            return;
        ref readonly Power power = ref x.B.Powers[self.Index];
        ref readonly Creature creature = ref x.B.Creature(self.Owner);
        if (power.Amount <= 0 || creature.IsDead || creature.CurrentHp > power.Amount)
            return;
        Span<byte> one = stackalloc byte[1];
        one[0] = self.Owner;
        Kernel.Kill(ref x, one, force: false);
    }

    /// <summary>The total positive Doom on one creature (the existing engine sums listener amounts).</summary>
    internal static int DoomFor(in Battle battle, byte owner)
    {
        if (!Registry.TryPower("DOOM_POWER", out ushort id))
            return 0;
        return Kernel.GetAmount(battle, owner, id);
    }
}

/// <summary>
/// <c>NecroMasteryPower</c>: HP lost by the pet deals that much times the layer count to every hittable
/// enemy (<c>AfterCurrentHpChangedMirrors.HandleNecroMasteryPower</c>).
/// </summary>
[ContentId("NECRO_MASTERY_POWER")]
public sealed class NecroMasteryPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCurrentHpChanged(ref Exec x, Listener self, byte creature, decimal delta)
    {
        if (delta >= 0m || creature != Limits.OstyOwner || self.Owner != Limits.PlayerOwner)
            return;
        decimal amount = -delta * x.B.Powers[self.Index].Amount;
        CardEffects.DamageAllEnemies(ref x, amount,
            ValueProps.Unblockable | ValueProps.Unpowered, self.Owner);
    }
}

/// <summary>
/// <c>CalcifyPower</c>: adds its amount to a powered attack made by the owner's Osty
/// (<c>ModifyDamageMirrors</c> invokes <c>CalcifyPower.ModifyDamageAdditive</c>).
/// </summary>
[ContentId("CALCIFY_POWER")]
public sealed class CalcifyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        if (damage.Dealer != Limits.OstyOwner || self.Owner != Limits.PlayerOwner
            || !damage.Props.IsPoweredAttack())
        {
            return 0m;
        }
        return x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// <c>CountdownPower</c>: at its owner's side turn start, apply its amount as Doom to a random hittable
/// opponent (<c>TurnStartPowerSupport.TriggerAfterSideTurnStart</c>, the COUNTDOWN case; the kernel's
/// fixed <c>GlobalAfterSideTurnStart</c> sequence calls it in listener order).
/// </summary>
[ContentId("COUNTDOWN_POWER")]
public sealed class CountdownPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void GlobalAfterSideTurnStart(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates || self.Owner != Limits.PlayerOwner || x.B.Powers[self.Index].Amount <= 0)
            return;
        Span<byte> candidates = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (Kernel.IsHittable(x.B, enemy))
                candidates[count++] = enemy;
        }
        if (count == 0)
            return;
        byte target = candidates[x.B.RngStream(RngStream.CombatTargets).NextInt(0, count)];
        Kernel.ApplyPower(ref x, target, DoomId(), x.B.Powers[self.Index].Amount, self.Owner);
    }

    internal static ushort DoomId()
        => Registry.TryPower("DOOM_POWER", out ushort id)
            ? id
            : throw new UnsupportedException("倒计时需要 DoomPower 的 Sts2Solver 定义。");
}

/// <summary>
/// <c>NeurosurgePower</c>: at its owner's side turn start, apply its amount as Doom to its owner
/// (<c>PersistentPowerSupport.TriggerOwnerAfterSideTurnStart</c>, the NEUROSURGE case; the kernel's
/// fixed owner sequence calls it).
/// </summary>
[ContentId("NEUROSURGE_POWER")]
public sealed class NeurosurgePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.ApplyPower(ref x, self.Owner, CountdownPower.DoomId(), amount, self.Owner);
    }
}

/// <summary>
/// <c>HauntPower</c>: after its owner plays a Soul, deal its amount to a random hittable enemy as
/// unblockable unpowered damage with no dealer (<c>AfterCardPlayedMirrors.HandleHauntPower</c>).
/// </summary>
[ContentId("HAUNT_POWER")]
public sealed class HauntPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner
            || !Registry.TryCard("SOUL", out ushort soul) || play.Card.Id != soul)
        {
            return;
        }
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (Kernel.IsHittable(x.B, enemy))
                targets[count++] = enemy;
        }
        if (count == 0)
            return;
        byte target = targets[x.B.RngStream(RngStream.CombatTargets).NextInt(0, count)];
        Span<byte> one = stackalloc byte[1];
        one[0] = target;
        Kernel.Damage(ref x, one, x.B.Powers[self.Index].Amount,
            ValueProps.Unblockable | ValueProps.Unpowered, Limits.NoOwner,
            hasCardSource: false, default);
    }
}

/// <summary>
/// <c>LethalityPower</c>: multiplies the first attack card played each turn by 1 + amount / 100
/// (<c>ModifyDamageMirrors.HandleLethalityPower</c>). The attack's play index must be zero and the
/// card's owner must be the power owner.
/// </summary>
[ContentId("LETHALITY_POWER")]
public sealed class LethalityPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        if (!damage.Props.IsPoweredAttack() || !damage.HasCardSource || self.Owner != Limits.PlayerOwner)
            return 1m;
        bool inPlay = x.EffectDepth > 0;
        if (inPlay && x.PlayIndex > 0)
            return 1m;
        int started = x.B.Counter(Counter.AttackPlayStartsThisTurn);
        return started <= (inPlay ? 1 : 0)
            ? 1m + (decimal)x.B.Powers[self.Index].Amount / 100m
            : 1m;
    }
}

/// <summary>
/// <c>ShroudPower</c>: gain block equal to its amount whenever its owner applies Doom
/// (<c>PowerLifecycleSupport.ResolvePowerAmountChanges</c>, the Shroud case).
/// </summary>
[ContentId("SHROUD_POWER")]
public sealed class ShroudPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target)
    {
        if (applier != self.Owner || powerId != CountdownPower.DoomId())
            return;
        Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>SicEmPower</c>: when the applier's Osty damages the owner, summon that much HP for the pet
/// (<c>AfterDamageGivenMirrors.HandleSicEmPower</c>); removed at its owner's side turn end
/// (<c>EndTurnPowerSupport.TriggerBatch048</c>).
/// </summary>
[ContentId("SIC_EM_POWER")]
public sealed class SicEmPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageGiven(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (dealer == Limits.OstyOwner && result.Receiver == self.Owner
            && self.Owner != Limits.PlayerOwner
            && x.B.Powers[self.Index].Amount > 0)
        {
            Kernel.SummonOsty(ref x, x.B.Powers[self.Index].Amount);
        }
    }

    public override void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// <c>OblivionPower</c>: records its amount at each play start (the kernel's transient play stack, the
/// existing engine's <c>CardPlayPairPredictionState</c>) and applies that amount as Doom after the play;
/// removed at the player's side turn end (<c>EndTurnPowerSupport.TriggerBatch048</c>).
/// </summary>
[ContentId("OBLIVION_POWER")]
public sealed class OblivionPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (power.Applier != Limits.PlayerOwner)
            return;
        x.PushCardPlayPair(CardPlayPairKind.OblivionDoom, self.Owner, power.Id, power.Amount);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (!x.TryTakeCardPlayPair(CardPlayPairKind.OblivionDoom, self.Owner, power.Id, out int amount))
            return;
        Kernel.ApplyPower(ref x, self.Owner, CountdownPower.DoomId(), amount, power.Applier);
    }

    public override void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side == Side.Player)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// <c>VeilpiercerPower</c>: Ethereal cards in hand or in play cost zero (the Late cost pass) and
/// decrement the power when played (<c>ModifyEnergyCostInCombatMirrors</c>,
/// <c>BeforeCardPlayedMirrors.HandleVeilpiercerPower</c>).
/// </summary>
[ContentId("VEILPIERCER_POWER")]
public sealed class VeilpiercerPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules,
        Listener self, in Card card, Pile pile, decimal amount)
    {
        // CardModel.Keywords runs Hook.ModifyKeywords: HexPower grants Ethereal to a Hexed card.
        if (self.Owner != Limits.PlayerOwner || !Kernel.HasKeyword(battle, card, Keywords.Ethereal)
            || pile is not (Pile.Hand or Pile.Play))
        {
            return amount;
        }
        return 0m;
    }

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (self.Owner != Limits.PlayerOwner || power.Amount <= 0
            || !Kernel.HasKeyword(x.B, play.Card, Keywords.Ethereal))
        {
            return;
        }
        Kernel.SetPowerAmount(ref x.B, self.Owner, power.Id, power.Amount - 1);
    }
}

/// <summary>
/// <c>DebilitatePower</c>: the owner takes double Vulnerable damage and deals half Weak damage
/// (<c>ModifyDamageMirrors</c> folds this into the Vulnerable and Weak multipliers); decrements at its
/// owner's side turn end (<c>EndTurnPowerSupport.TriggerBatch048</c>).
/// </summary>
[ContentId("DEBILITATE_POWER")]
public sealed class DebilitatePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id,
                x.B.Powers[self.Index].Amount - 1);
    }
}

/// <summary>
/// <c>HangPower</c>: the next Hang card played against its owner deals damage times this amount
/// (<c>HangPower.ModifyDamageMultiplicative</c> through <c>ModifyDamageMirrors</c>).
/// </summary>
[ContentId("HANG_POWER")]
public sealed class HangPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        if (damage.Target != self.Owner || !damage.HasCardSource
            || !Registry.TryCard("HANG", out ushort hang) || damage.CardSource.Id != hang)
        {
            return 1m;
        }
        return x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// <c>BorrowedTimePower</c>: every card costs its amount more until its owner's side turn end
/// (<c>BorrowedTimePower.TryModifyEnergyCostInCombat</c>; the kernel's transient removal pass clears it).
/// </summary>
[ContentId("BORROWED_TIME_POWER")]
public sealed class BorrowedTimePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombat(in Battle battle, Rules rules,
        Listener self, in Card card, Pile pile, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + battle.Powers[self.Index].Amount : amount;
}

/// <summary>
/// <c>FriendshipPower</c>: each layer adds one maximum energy (<c>FriendshipPower.ModifyMaxEnergy</c>
/// through <c>PersistentPowerSupport.GetModifiedMaxEnergy</c>).
/// </summary>
[ContentId("FRIENDSHIP_POWER")]
public sealed class FriendshipPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + x.B.Powers[self.Index].Amount : amount;
}

/// <summary>
/// <c>EnfeeblingTouchPower</c> (a negative <c>TemporaryStrengthPower</c>): application changes Strength
/// by -amount and its owner's side turn end retires it, restoring Strength.
/// </summary>
[ContentId("ENFEEBLING_TOUCH_POWER")]
public sealed class EnfeeblingTouchPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override string? InternallyAppliedPowerId => "STRENGTH_POWER";

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsTemporaryPower => true;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryStrength(ref x, self);
    }
}

/// <summary>
/// <c>SummonNextTurnPower</c>: at the player's turn start, summon that much HP for Osty and consume the
/// power (<c>TurnStartPowerSupport</c>'s SUMMON_NEXT_TURN case).
/// </summary>
[ContentId("SUMMON_NEXT_TURN_POWER")]
public sealed class SummonNextTurnPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool UsesAmountOnTurnStart => true;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.SummonOsty(ref x, amount);
        if (!x.NeedsChoice)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// <c>SleightOfFleshPower</c>: when its owner applies a non-temporary debuff to an enemy, deal its amount
/// to that enemy as unpowered damage (<c>PowerLifecycleSupport.ResolvePowerAmountChanges</c>).
/// </summary>
[ContentId("SLEIGHT_OF_FLESH_POWER")]
public sealed class SleightOfFleshPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target)
    {
        if (delta == 0 || applier != self.Owner || !Kernel.IsEnemyOwner(target)
            || Registry.Power(powerId).TypeForAmount(delta) != PowerType.Debuff
            || Registry.Power(powerId).IsTemporaryPower)
        {
            return;
        }
        Span<byte> one = stackalloc byte[1];
        one[0] = target;
        Kernel.Damage(ref x, one, x.B.Powers[self.Index].Amount, ValueProps.Unpowered, self.Owner,
            hasCardSource: false, default);
    }
}

/// <summary>
/// <c>SpiritOfAshPower</c>: gain its amount as unpowered block before its owner plays an Ethereal card
/// (<c>BeforeCardPlayedMirrors.HandleSpiritOfAshPower</c>).
/// </summary>
[ContentId("SPIRIT_OF_ASH_POWER")]
public sealed class SpiritOfAshPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner
            || !Kernel.HasKeyword(x.B, play.Card, Keywords.Ethereal))
        {
            return;
        }
        Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount, ValueProps.Unpowered,
            fromCard: false);
    }
}

/// <summary>
/// <c>DevourLifePower</c>: after its owner plays a Soul, summon its amount of Osty HP
/// (<c>AfterCardPlayedMirrors.HandleDevourLifePower</c>).
/// </summary>
[ContentId("DEVOUR_LIFE_POWER")]
public sealed class DevourLifePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner
            || !Registry.TryCard("SOUL", out ushort soul) || play.Card.Id != soul)
        {
            return;
        }
        Kernel.SummonOsty(ref x, x.B.Powers[self.Index].Amount);
    }
}

/// <summary>
/// <c>DemesnePower</c>: its amount adds both hand draw and maximum energy
/// (<c>PersistentPowerSupport.GetModifiedHandDraw</c>/<c>GetModifiedMaxEnergy</c> invoke the native methods).
/// </summary>
[ContentId("DEMESNE_POWER")]
public sealed class DemesnePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + x.B.Powers[self.Index].Amount : amount;

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + x.B.Powers[self.Index].Amount : amount;
}
