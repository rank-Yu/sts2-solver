using Sts2Solver.Content.Afflictions;
using Sts2Solver.Content.Monsters.Act1;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// <c>PlowPower</c> (Ceremonial Beast's STAMP_MOVE): when the owner takes unblocked damage and its
/// current HP drops to this power's amount or below, it loses Strength (permanent and temporary),
/// is stunned into BEAST_CRY_MOVE and removes the power. Mirrors
/// <c>AfterDamageReceivedMirrors.HandlePlowPower</c>; the stun is the existing engine's
/// <c>ForceStunnedMove</c> (not the native <c>CreatureCmd.Stun</c> overload).
/// </summary>
[ContentId("PLOW_POWER")]
public sealed class PlowPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || result.UnblockedDamage <= 0)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (x.B.Creature(self.Owner).CurrentHp > amount)
            return;
        ushort strength = Registry.TryPower("STRENGTH_POWER", out ushort strengthId)
            ? strengthId
            : ushort.MaxValue;
        for (int index = x.B.PowerCount - 1; index >= 0; index--)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Owner != self.Owner)
                continue;
            // AfterDamageReceivedMirrors.HandlePlowPower removes every TemporaryStrengthPower
            // subclass (Mangle, SetupStrike, PiercingWail, ...) plus StrengthPower itself. The
            // temporary family has no single native id; the definition records the stat it feeds.
            PowerDef def = Registry.Power(power.Id);
            bool temporaryStrength = def.IsTemporaryPower
                && def.InternallyAppliedPowerId == "STRENGTH_POWER";
            if (power.Id == strength || temporaryStrength)
                Kernel.SetPowerAmount(ref x.B, self.Owner, power.Id, 0);
        }
        int nextNode = -1;
        if (x.B.Enemies[self.Owner - Limits.FirstEnemyOwner].MonsterId
            == Registry.MonsterId<CeremonialBeast>())
        {
            MonsterRules rules = Kernel.MonsterRules(ref x, self.Owner);
            nextNode = rules.NodeIndex("BEAST_CRY_MOVE");
            if (rules.StaticInts[3] > 0)
                Kernel.ApplyPowerWithoutCardSource(ref x, self.Owner,
                    Registry.PowerId<RitualPower>(), rules.StaticInts[3], self.Owner);
        }
        Kernel.ForceStunnedMove(ref x, self.Owner, nextNode);
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

/// <summary>
/// <c>RingingPower</c> (Ceremonial Beast's BEAST_CRY_MOVE): afflicts every card with Ringing, and a
/// Ringing card may not be played as the second or later card of the same turn
/// (<c>ShouldPlayMirrors.HandleRingingPower</c>). Removed at the player's turn end
/// (<c>CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects</c>), after which the normalization
/// clears the afflictions.
/// </summary>
[ContentId("RINGING_POWER")]
public sealed class RingingPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card)
    {
        if (card.AfflictionId != Registry.AfflictionId<RingingAffliction>() + 1)
            return true;
        return battle.Counter(Counter.CardPlaySeriesStartedThisTurn) == 0;
    }

    public override void PlayerSideTurnEnd(ref Exec x, Listener self)
        => Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
}

/// <summary>
/// <c>TangledPower</c> (Vine Shambler's GRASPING_VINES_MOVE): afflicts Attacks with Entangled and adds
/// the power's amount to their energy cost (<c>TryModifyEnergyCostInCombat</c>). Removed at the
/// player's turn end; the normalization clears Entangled afterwards.
/// </summary>
[ContentId("TANGLED_POWER")]
public sealed class TangledPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombat(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        if (card.AfflictionId != Registry.AfflictionId<EntangledAffliction>() + 1)
            return amount;
        return amount + battle.Powers[self.Index].Amount;
    }

    public override void PlayerSideTurnEnd(ref Exec x, Listener self)
        => Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
}

/// <summary>
/// <c>SmoggyPower</c> (Living Fog's ADVANCED_GAS_MOVE): Skills are unplayable while Smog-afflicted;
/// after the player plays a Skill every other Skill card gets Smog, and the player's turn end clears
/// them. Mirrors <c>ShouldPlayMirrors</c>, <c>AfterCardPlayedMirrors.HandleSmoggyPower</c> and
/// <c>EndTurnPowerSupport.TriggerRegular</c>'s SmoggyPower case.
/// </summary>
[ContentId("SMOGGY_POWER")]
public sealed class SmoggyPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card)
        => card.AfflictionId != Registry.AfflictionId<SmogAffliction>() + 1;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (play.Position < 0 || play.Position >= x.B.CardCount)
            return;
        ref readonly Card played = ref x.B.Cards[play.Position];
        if (Kernel.ResolveCardType(x.B, x.R, played) != CardType.Skill)
            return;
        ushort smog = Registry.AfflictionId<SmogAffliction>();
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref readonly Card card = ref x.B.Cards[position];
            if (card.AfflictionId != 0 || Kernel.ResolveCardType(x.B, x.R, card) != CardType.Skill)
                continue;
            Kernel.ApplyAffliction(ref x, position, smog, 1);
            if (x.NeedsChoice)
                return;
        }
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.ClearAfflictions(ref x.B, Registry.AfflictionId<SmogAffliction>());
    }
}

/// <summary>
/// <c>VitalSparkPower</c> (Infested Prism's PULSATE_MOVE and entrance): Skills carry Tainted. Playing
/// one applies TaintedPower to its owner with the Vital Spark amount; Tainted layers are kept in sync
/// by the normalization pass. The native <c>AfterPowerAmountChanged</c> body is not mirrored by the
/// existing engine, which normalizes instead.
/// </summary>
[ContentId("VITAL_SPARK_POWER")]
public sealed class VitalSparkPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;


    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (play.Position < 0 || play.Position >= x.B.CardCount
            || x.B.Cards[play.Position].AfflictionId != Registry.AfflictionId<TaintedAffliction>() + 1)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<TaintedPower>(),
            x.B.Powers[self.Index].Amount, Limits.NoOwner);
    }
}

/// <summary>
/// <c>TaintedPower</c>: adds its amount to the owner's powered attacks and is removed at the end of
/// the enemy side. Mirrors <c>TaintedPower.ModifyDamageAdditive</c> and the EndTurnPowerSupport case.
/// </summary>
[ContentId("TAINTED_POWER")]
public sealed class TaintedPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        if (damage.Target != self.Owner || !damage.Props.IsPoweredAttack())
            return 0m;
        return x.B.Powers[self.Index].Amount;
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side == Side.Enemy)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

/// <summary>
/// <c>HexPower</c>: afflicts all player cards with Hexed; when its applier dies the power is removed
/// and the normalization clears Hexed. Mirrors <c>CorePowerSupport.ApplyEnemyDeathPowers</c>'s
/// <c>RemoveHexPower</c>.
/// </summary>
[ContentId("HEX_POWER")]
public sealed class HexPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void ApplierDied(ref Exec x, Listener self)
        => Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
}
