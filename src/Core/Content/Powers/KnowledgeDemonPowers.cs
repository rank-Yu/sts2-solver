using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>Native <c>DisintegrationPower</c>: at its owner's side turn end late, take its amount as
/// unpowered damage dealt by itself.</summary>
[ContentId("DISINTEGRATION_POWER")]
public sealed class DisintegrationPower : PowerDef
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
        damage = TurnEndSelfDamage.FromPower(TurnEndSelfDamageTiming.AfterSideTurnEndLate,
            power.Amount, ValueProps.Unpowered, power.Owner);
        return true;
    }


    public override void AfterSideTurnEndLate(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates || x.B.Powers[self.Index].Amount <= 0 || !x.B.Creature(self.Owner).IsAlive)
            return;
        Span<byte> targets = stackalloc byte[1];
        targets[0] = self.Owner;
        Kernel.Damage(ref x, targets, x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, self.Owner, hasCardSource: false, default);
    }
}

/// <summary>Native <c>MindRotPower</c>: the owner draws <c>max(0, count - amount)</c> cards.</summary>
[ContentId("MIND_ROT_POWER")]
public sealed class MindRotPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner
            ? Math.Max(0m, amount - x.B.Powers[self.Index].Amount)
            : amount;
}

/// <summary>
/// Native <c>SlothPower</c>: the owner may play at most <c>Amount</c> cards each side turn. The
/// native private counter increments in <c>BeforeCardPlayed</c> and resets at the owner's
/// <c>BeforeSideTurnStart</c>; it is read by <c>ShouldPlay</c>.
/// </summary>
[ContentId("SLOTH_POWER")]
public sealed class SlothPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card)
        => battle.Powers[self.Index].Aux0 < battle.Powers[self.Index].Amount;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
        => x.B.Powers[self.Index].Aux0++;

    public override void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
    {
        if (participates)
            x.B.Powers[self.Index].Aux0 = 0;
    }
}

/// <summary>Native <c>WasteAwayPower</c>: the owner's maximum energy is reduced by its amount.</summary>
[ContentId("WASTE_AWAY_POWER")]
public sealed class WasteAwayPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner
            ? amount - x.B.Powers[self.Index].Amount
            : amount;
}
