using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>RitualPower</c>: at the end of its owner's side it applies Strength equal to its amount
/// to the owner (<c>EndTurnPowerSupport.TriggerRegular</c> case <c>RitualPower when ownerParticipates</c>
/// → <c>PersistentPowerSupport.TriggerRitual</c>), unless the enemy has just gained it:
/// <c>ConsumeRitualApplicationDelay</c> clears <c>_wasJustAppliedByEnemy</c> and skips that trigger.
/// </summary>
[ContentId("RITUAL_POWER")]
public sealed class RitualPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool MarksJustAppliedByEnemy => true;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Amount <= 0)
            return;
        if ((power.Flags & PowerFlags.WasJustAppliedByEnemy) != 0)
        {
            power.Flags &= ~PowerFlags.WasJustAppliedByEnemy;
            return;
        }
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(), power.Amount, self.Owner);
    }
}

/// <summary>
/// Native <c>PlatingPower</c>: at its owner's side start it loses <c>Decrement</c> layers from round
/// two on (<c>SimulatedCombatState.TriggerBaseSideTurnStart</c>, <c>decrementPlating</c>) and at the end
/// of its owner's side it gains that many points of Unpowered block
/// (<c>CorePowerSupport.TriggerEnemySideTurnEndEffects</c>, case <c>PlatingPower</c>). The first-turn
/// block is applied before the root and imported. For a single player <c>Decrement</c> is 1; it is read
/// from the compiled power variables (`PlatingPower.AfterApplied` sets it to the player count).
/// </summary>
[ContentId("PLATING_POWER")]
public sealed class PlatingPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<string> VarNames => ["Decrement"];

    public override void BaseSideTurnStart(ref Exec x, Listener self, bool decrementPlating)
    {
        if (!decrementPlating)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, amount - (int)x.R.PowerVar(Id, 0));
    }

    public override void EnemySideTurnEnd(ref Exec x, Listener self)
    {
        // EndTurnPowerSupport.TriggerVeryEarly runs before the per-enemy Plating block gain and removes
        // Plating when an enemy's AsleepPower amount is already <= 1 (its last decrement happens later,
        // in TriggerRegular). This is that removal at the same point in the sequence.
        if (Registry.TryPower("ASLEEP_POWER", out ushort asleep)
            && Kernel.GetAmount(x.B, self.Owner, asleep) is > 0 and <= 1)
        {
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
            return;
        }
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.GainBlock(ref x, self.Owner, amount, ValueProps.Unpowered, fromCard: false);
    }

    // BeforeSideTurnEndMirrors.HandlePlatingPower (Early pass): a participating owner gains its amount
    // as Unpowered block — the player-side Plating at the player's turn end.
    public override void BeforeSideTurnEndEarly(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (Kernel.Participates(participants, self.Owner))
            Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount, ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// Native <c>SuckPower</c> (FossilStalker, three stacks applied in <c>AfterAddedToRoom</c>): after its
/// owner's powered attack, the owner gains <c>Amount</c> Strength once per hit list that dealt
/// unblocked damage to the other side (<c>SuckPower.AfterAttack</c>). The native method first removes
/// every pet hit and the paired result for that pet's owner from each hit list, then counts a list
/// when any remaining result has unblocked damage. <see cref="Exec.AttackSuckHitLists"/> keeps that
/// per-hit-list count for the current attack context; it is zero at every stable boundary.
/// </summary>
[ContentId("SUCK_POWER")]
public sealed class SuckPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        if (attacker != self.Owner || !x.AttackProps.IsPoweredAttack())
            return;
        int hits = x.AttackSuckHitLists;
        if (hits == 0)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(), amount * hits, self.Owner);
    }
}

/// <summary>
/// Native <c>HardenedShellPower</c> (SkulkingColony, 20 stacks applied in <c>AfterAddedToRoom</c>):
/// each hit against the owner loses at most the shell's remaining points for the turn
/// (<c>ModifyHpLostMirrors.HandleHardenedShellPower</c> through <c>HardenedShellPredictionState</c>),
/// unblocked damage is accumulated (<c>AfterDamageReceivedMirrors.HandleHardenedShellPower</c>), and
/// the accumulator resets at every side start (<c>HardenedShellPower.BeforeSideTurnStart</c>).
/// Aux0 = damageReceivedThisTurn; the bridge projects it from the prediction state, falling back to
/// the model's live internal data.
/// </summary>
[ContentId("HARDENED_SHELL_POWER")]
public sealed class HardenedShellPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHpLostBeforeOstyLate(ref Exec x, Listener self, in HpLossContext loss,
        decimal amount)
    {
        if (loss.Target != self.Owner || amount == 0m)
            return amount;
        ref Power power = ref x.B.Powers[self.Index];
        return Math.Min(amount, power.Amount - power.Aux0);
    }

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || result.WasFullyBlocked)
            return;
        x.B.Powers[self.Index].Aux0 += result.UnblockedDamage;
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
        => x.B.Powers[self.Index].Aux0 = 0;
}

/// <summary>
/// Native <c>ThornsPower</c>: before its owner receives a powered attack (or an Omnislice hit), damages the
/// dealer for its amount as Unpowered damage with the owner as dealer
/// (<c>M/Hooks/Damage/BeforeDamageReceivedMirrors.HandleThornsPower</c>).
/// </summary>
[ContentId("THORNS_POWER")]
public sealed class ThornsPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeDamageReceived(ref Exec x, Listener self, byte target, decimal amount,
        ValueProps props, byte dealer, bool hasCardSource, in Card cardSource)
    {
        if (target != self.Owner || dealer == Limits.NoOwner)
            return;
        bool omnislice = hasCardSource && Registry.TryCard("OMNISLICE", out ushort omnisliceId) && cardSource.Id == omnisliceId;
        if (!props.IsPoweredAttack() && !omnislice)
            return;
        Span<byte> targets = stackalloc byte[1];
        targets[0] = dealer;
        Kernel.Damage(ref x, targets, x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered | ValueProps.SkipHurtAnim, self.Owner, hasCardSource: false, default);
    }
}
