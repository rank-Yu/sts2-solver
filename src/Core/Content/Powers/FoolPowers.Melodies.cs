// Native game 0.113.0 (16ac0fe3).
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Content.Powers;

public abstract class FoolBuff : PowerDef
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;
}

[ContentId("STRETCH_POWER")]
public sealed class StretchPower : FoolBuff
{
    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner || !x.R.Card(play.Card.Id, play.Card.Upgrade).GainsBlock
            || x.B.Powers[self.Index].Aux0 != 0) return;
        ref Power power = ref x.B.Powers[self.Index];
        power.Aux0 = x.PlayIdentity;
        power.Aux1 = power.Amount;
        power.AmountOnTurnStart = x.PlayIndex + 1;
    }

    public override decimal ModifyBlockAdditive(ref Exec x, Listener self, in BlockContext block, decimal amount)
    {
        if (!block.HasCardSource || self.Owner != Limits.PlayerOwner
            || !block.Props.IsPoweredAttack()) return 0;
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 == 0) return x.PlayIdentity == 0 ? power.Amount : 0;
        return power.Aux0 == x.PlayIdentity && Math.Abs(power.AmountOnTurnStart) == x.PlayIndex + 1
            ? power.Amount : 0;
    }

    public override void AfterModifyingBlockAmount(ref Exec x, Listener self, decimal modified, in BlockContext block)
    {
        if (x.B.Powers[self.Index].Aux0 == x.PlayIdentity && x.PlayIdentity != 0)
            x.B.Powers[self.Index].AmountOnTurnStart = -Math.Abs(x.B.Powers[self.Index].AmountOnTurnStart);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 != x.PlayIdentity || Math.Abs(power.AmountOnTurnStart) != play.PlayIndex + 1) return;
        int spent = power.AmountOnTurnStart < 0 ? power.Aux1 : 0;
        int next = power.Amount - spent;
        power.Aux0 = power.Aux1 = power.AmountOnTurnStart = 0;
        if (spent != 0) Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, next);
    }
}

[ContentId("ALL_IS_WELL_POWER")]
public sealed class AllIsWellPower : FoolBuff
{
    public override void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position, byte creator)
    {
        if (creator == self.Owner && Registry.Card(x.B.Cards[position].Id).NativeId == "SADNESS")
            Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount, ValueProps.Unpowered, false);
    }
}

[ContentId("ARCHERY_POWER")]
public sealed class ArcheryPower : FoolBuff
{
    public override IReadOnlyList<string> AppliedPowers => ["AIM_POWER"];
    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
        => Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<AimPower>(),
            x.B.Powers[self.Index].Amount, self.Owner);
}

[ContentId("HIDDEN_ROOTS_POWER")]
public sealed class HiddenRootsPower : FoolBuff
{
    public override IReadOnlyList<string> AppliedPowers => ["STRETCH_POWER"];
    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
        => Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StretchPower>(),
            x.B.Powers[self.Index].Amount, self.Owner);
}

[ContentId("HONING_POWER")]
public sealed class HoningPower : FoolBuff
{
    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
        => damage.Dealer == self.Owner && damage.Props.IsPoweredAttack() && (damage.Props & ValueProps.FromArrow) != 0
            ? 1m + x.B.Powers[self.Index].Amount / 100m : 1m;
}

[ContentId("DISLIKE_YOU_POWER")]
public sealed class DislikeYouPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
        => damage.Target == self.Owner && damage.Props.IsPoweredAttack() && (damage.Props & ValueProps.FromArrow) != 0
            ? 1m + x.B.Powers[self.Index].Amount / 100m : 1m;
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side == Side.Player) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
}

[ContentId("SERRATED_ARROWS_POWER")]
public sealed class SerratedArrowsPower : FoolBuff
{
    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
        => damage.Props.IsPoweredAttack() && (damage.Dealer == self.Owner
                || self.Owner == Limits.PlayerOwner && damage.Dealer == Limits.OstyOwner)
            && damage.Target != Limits.NoOwner && Kernel.ImpaledPowerIndex(x.B, damage.Target) >= 0
            ? 1m + x.B.Powers[self.Index].Amount / 100m : 1m;
}

[ContentId("SONG_PATH_POWER")]
public sealed class SongPathPower : FoolBuff
{
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner == Limits.PlayerOwner && Kernel.FirstMelodyPlay(ref x, CardTag.Cho))
            Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount, ValueProps.Unpowered, false);
    }
}

[ContentId("VIOLENT_CHORD_POWER")]
public sealed class ViolentChordPower : FoolBuff
{
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner || !Kernel.FirstMelodyPlay(ref x, CardTag.Wah)) return;
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int i = 0; i < x.B.RosterCount; i++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[i]);
            if (Kernel.IsHittable(x.B, owner)) targets[count++] = owner;
        }
        Kernel.Damage(ref x, targets[..count], x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, self.Owner, false, default);
    }
}

public abstract class FirstMelodyCountPower : FoolBuff
{
    protected static int FirstCount(ref Exec x, in Card card)
    {
        CardTag tags = x.R.Card(card.Id, card.Upgrade).Tags;
        int count = 0;
        for (int i = 0; i < 3; i++)
            if ((tags & (CardTag)((int)CardTag.Wah << i)) != 0 && x.B.Fool.Started[i] == 1) count++;
        return count;
    }
}

[ContentId("MOTIF_POWER")]
public sealed class MotifPower : FirstMelodyCountPower
{
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner == Limits.PlayerOwner)
            Kernel.Draw(ref x, FirstCount(ref x, play.Card) * x.B.Powers[self.Index].Amount, false);
    }
}

[ContentId("LOST_LUTE_POWER")]
public sealed class LostLutePower : FirstMelodyCountPower
{
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner == Limits.PlayerOwner)
            Kernel.AutoPlayFromDrawPile(ref x, FirstCount(ref x, play.Card) * x.B.Powers[self.Index].Amount, false);
    }
}

[ContentId("TUNE_POWER")]
public sealed class TunePower : FoolBuff
{
    public override IReadOnlyList<string> AppliedPowers => ["FREE_CARD_POWER"];
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side != Side.Enemy && self.Owner == Limits.PlayerOwner && Kernel.PlayedMelody(x.B, CardTag.Ki))
            Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<FreeCardPower>(),
                x.B.Powers[self.Index].Amount, self.Owner);
    }
}

[ContentId("CHO_BARBS_POWER")]
public sealed class ChoBarbsPower : FoolBuff
{
    public override bool IsTemporaryPower => true;
    public override string InternallyAppliedPowerId => "THORNS_POWER";
    public override IReadOnlyList<string> AppliedPowers => ["THORNS_POWER"];
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates) return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.RemovePowerAt(ref x.B, self.Index);
        ushort thorns = Registry.PowerId<ThornsPower>();
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, thorns,
            Kernel.GetAmount(x.B, self.Owner, thorns) - amount);
    }
}

[ContentId("FREE_CARD_POWER")]
public sealed class FreeCardPower : FoolBuff
{
    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
        => self.Owner == Limits.PlayerOwner && pile is Pile.Hand or Pile.Play ? 0m : amount;
    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner == Limits.PlayerOwner)
            Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, x.B.Powers[self.Index].Amount - 1);
    }
}
