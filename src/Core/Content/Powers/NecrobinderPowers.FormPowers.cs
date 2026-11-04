using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>DanseMacabrePower</c>: when its owner plays a card whose resolved energy cost is at
/// least the power's <c>Energy</c> var (2), gain its amount of unpowered block
/// (<c>BeforeCardPlayedMirrors.HandleDanseMacabrePower</c>).
/// </summary>
[ContentId("DANSE_MACABRE_POWER")]
public sealed class DanseMacabrePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if (x.CurrentPlayEnergyValue < x.R.PowerVar(Id, 0))
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// Native <c>ReaperFormPower</c>: after a powered attack by its owner (or the owner's pet) deals
/// damage, apply Doom to the target equal to TotalDamage × amount, with the power owner as applier
/// and no card source (<c>AfterDamageGivenMirrors.HandleReaperFormPower</c>).
/// </summary>
[ContentId("REAPER_FORM_POWER")]
public sealed class ReaperFormPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageGiven(ref Exec x, Listener self, in DamageResult result,
        byte dealer)
    {
        if (!Kernel.IsOwnerOrOsty(self.Owner, dealer))
            return;
        if (!result.Props.IsPoweredAttack())
            return;
        int total = result.BlockedDamage + result.UnblockedDamage;
        if (total <= 0)
            return;
        if (!Registry.TryPower("DOOM_POWER", out ushort doom))
            throw new UnsupportedException("Doom 没有 Sts2Solver 定义。");
        Kernel.ApplyPowerWithoutCardSource(ref x, result.Receiver, doom,
            total * x.B.Powers[self.Index].Amount, self.Owner);
    }
}
