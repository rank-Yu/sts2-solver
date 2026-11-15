// Semantics audited against game v0.113.1 Models/Powers/GodByrdPower.cs.
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

[ContentId("GOD_BYRD_POWER")]
public sealed class GodByrdPower : PowerDef
{
    public override bool UsesAmountOnTurnStart => true;
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        Power power = x.B.Powers[self.Index];
        if (power.AmountOnTurnStart == 0) return;
        CardEffects.DamageAllEnemies(ref x, power.Amount, ValueProps.Unpowered, self.Owner);
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

// Wuppy's target redirection is in Kernel.TryDamageTarget before block.
[ContentId("WUPPYS_LOYALTY_POWER")]
public sealed class WuppysLoyaltyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;
}
