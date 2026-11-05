using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>ClarityPower</c>: draws one extra card per turn while its owner is the player and loses
/// one stack at its owner's side turn start
/// (<c>PersistentPowerSupport.TriggerOwnerAfterSideTurnStart</c>).
/// </summary>
[ContentId("CLARITY_POWER")]
public sealed class ClarityPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // ClarityPower.ModifyHandDraw: only the player's own clarity adds one, independent of the amount.
    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + 1m : amount;

    // PersistentPowerSupport.TriggerOwnerAfterSideTurnStart: decrement one stack.
    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }
}

/// <summary>
/// Native <c>FlexPotionPower</c> (a positive <c>TemporaryStrengthPower</c>): at its owner's side turn
/// end it is removed and its amount of Strength is taken back
/// (<c>EndTurnPowerSupport.TriggerRegular</c>, <c>SimulatedCombatState.RetireTemporaryStat</c>).
/// </summary>
[ContentId("FLEX_POTION_POWER")]
public sealed class FlexPotionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

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
/// Native <c>ShacklingPotionPower</c> (a negative <c>TemporaryStrengthPower</c>): at its owner's side
/// turn end it is removed and its amount of Strength is restored
/// (<c>EndTurnPowerSupport.TriggerRegular</c>, <c>SimulatedCombatState.RetireTemporaryStat</c>).
/// </summary>
[ContentId("SHACKLING_POTION_POWER")]
public sealed class ShacklingPotionPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryStrength(ref x, self);
    }
}

/// <summary>
/// Native <c>SpeedPotionPower</c> (a positive <c>TemporaryDexterityPower</c>): at its owner's side
/// turn end it is removed and its amount of Dexterity is taken back
/// (<c>EndTurnPowerSupport.TriggerRegular</c>, <c>SimulatedCombatState.RetireTemporaryStat</c>).
/// </summary>
[ContentId("SPEED_POTION_POWER")]
public sealed class SpeedPotionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override string? InternallyAppliedPowerId => "DEXTERITY_POWER";

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsTemporaryPower => true;
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
        if (amount == 0)
            return;
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<DexterityPower>(), -amount, self.Owner);
    }
}

/// <summary>
/// Native <c>RadiancePower</c>: gains <c>Energy</c> energy at the owner's energy reset, then loses one
/// stack (<c>AfterEnergyResetMirrors.HandleRadiancePower</c>). No Energy Gain is not supported yet, so
/// the guard only fires when that power is registered.
/// </summary>
[ContentId("RADIANCE_POWER")]
public sealed class RadiancePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void AfterEnergyReset(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (!Registry.TryPower("NO_ENERGY_GAIN_POWER", out ushort noEnergyGain)
            || Kernel.GetAmount(x.B, self.Owner, noEnergyGain) <= 0)
        {
            Kernel.GainEnergy(ref x, x.R.PowerVar(Id, 0));
        }
        if (amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }
}

/// <summary>
/// Native <c>RegenPower</c>: heals its amount at its owner's side turn end and loses one stack. The
/// existing engine heals in <c>BeforeSideTurnEndMirrors.HandleRegenPower</c> for the player and in
/// <c>CorePowerSupport.TriggerEnemySideTurnEndEffects</c> for enemies; the player's decrement happens
/// in <c>CompletePlayerEarlySideTurnEndEffects</c> after the turn-end hand effects.
/// </summary>
[ContentId("REGEN_POWER")]
public sealed class RegenPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeSideTurnEndEarly(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner) || x.B.Creature(self.Owner).IsDead)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.Heal(ref x, self.Owner, amount);
    }

    public override void EnemySideTurnEnd(ref Exec x, Listener self)
    {
        if (x.B.Creature(self.Owner).IsDead)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.Heal(ref x, self.Owner, amount);
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }

    // CorePowerSupport.CompletePlayerEarlySideTurnEndEffects: the player's regen stack is removed after DoTurnEnd.
    public override void CompletePlayerEarlySideTurnEnd(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }
}

/// <summary>
/// Native <c>RetainHandPower</c>: <c>ShouldFlush</c> keeps every card in the owner's hand at the
/// player's turn end, and the power loses one stack in
/// <c>CorePowerSupport.TriggerTransientSideTurnEndPowers</c>.
/// </summary>
[ContentId("RETAIN_HAND_POWER")]
public sealed class RetainHandPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // RetainHandPower.ShouldFlush: false for its owner's player, so the hand is not flushed.
    public override bool ShouldFlush(ref Exec x, Listener self) => !Kernel.IsPlayerSide(self.Owner);
}

/// <summary>
/// Native <c>BlockNextTurnPower</c>: when its owner's block is cleared, gains its amount as
/// Unpowered block and is removed (<c>CorePowerSupport.TriggerAfterBlockCleared</c>).
/// </summary>
[ContentId("BLOCK_NEXT_TURN_POWER")]
public sealed class BlockNextTurnPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterBlockCleared(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.GainBlock(ref x, self.Owner, amount, ValueProps.Unpowered, fromCard: false);
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

/// <summary>
/// Native <c>DemisePower</c>: at its owner's side turn end, damages the owner for its amount as
/// Unblockable | Unpowered damage with no dealer
/// (<c>EndTurnPowerSupport.TriggerRegular</c>).
/// </summary>
[ContentId("DEMISE_POWER")]
public sealed class DemisePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool TryGetTurnEndSelfDamage(in Battle battle, Rules rules, int powerIndex,
        out TurnEndSelfDamage damage)
    {
        damage = default;
        ref readonly Power power = ref battle.Powers[powerIndex];
        if (power.Amount <= 0 || battle.Creature(power.Owner).IsDead)
            return false;
        damage = TurnEndSelfDamage.FromPower(TurnEndSelfDamageTiming.EndTurnRegular,
            power.Amount, ValueProps.Unblockable | ValueProps.Unpowered, Limits.NoOwner);
        return true;
    }


    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates || x.B.Creature(self.Owner).IsDead)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Span<byte> target = stackalloc byte[1];
        target[0] = self.Owner;
        Kernel.Damage(ref x, target, amount,
            ValueProps.Unblockable | ValueProps.Unpowered, Limits.NoOwner,
            hasCardSource: false, default);
    }
}

/// <summary>
/// Native <c>DuplicationPower</c> (Duplicator potion): each played card gets one extra play;
/// <c>AfterModifyingCardPlayCount</c> decrements the power, and the player turn end removes it
/// (<c>ModifyCardPlayCountMirrors</c>, <c>CorePowerSupport.TriggerTransientSideTurnEndPowers</c>).
/// </summary>
[ContentId("DUPLICATION_POWER")]
public sealed class DuplicationPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount)
    {
        if (self.Owner != Limits.PlayerOwner)
            return playCount;
        return playCount + 1;
    }

    public override void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }
}

/// <summary>
/// Native <c>GigantificationPower</c>: the next powered Attack card play deals triple damage and
/// consumes one stack. BeforeAttack records the armed attack, ModifyDamageMultiplicative applies 3x
/// while the same card's attack runs, AfterAttack decrements. Aux0 is the armed flag; CommandToModify's
/// card identity is unambiguous because Sts2Solver resolves one card attack at a time.
/// </summary>
[ContentId("GIGANTIFICATION_POWER")]
public sealed class GigantificationPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeAttack(ref Exec x, Listener self, byte attacker, ValueProps props)
    {
        // GigantificationPowerMirrors.BeforeAttack calls StateStore.GetPowerAmount(power) before any
        // predicate, for every attack (including the enemy's). That creates the native
        // PowerAmountPredictionState; the action-boundary synchronization restores the model amount
        // from it, so a player death during the same action leaves the power in place. The flag lets
        // Kernel.HandlePlayerDeath keep the row; Step clears it at the stable boundary.
        x.B.Powers[self.Index].Flags |= PowerFlags.PendingAmountState;
        // GigantificationPower.BeforeAttack keys on the card source's owner, not on the attacker:
        // an Osty attack is still the player's Attack card.
        if (self.Owner != Limits.PlayerOwner || !props.IsPoweredAttack())
            return;
        if (x.Card.Id == 0 || x.Stats?.Type != CardType.Attack)
            return;
        if (x.B.Powers[self.Index].Amount > 0)
            x.B.Powers[self.Index].Aux0 = 1;
    }

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        if (self.Owner != Limits.PlayerOwner || x.B.Powers[self.Index].Aux0 == 0)
            return 1m;
        if (!damage.HasCardSource || !damage.Props.IsPoweredAttack())
            return 1m;
        return 3m;
    }

    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 == 0)
            return;
        power.Aux0 = 0;
        if (power.Amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, power.Amount - 1);
    }
}
