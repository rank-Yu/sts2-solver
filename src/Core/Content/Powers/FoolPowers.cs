// Checked against native 0.113.0 (16ac0fe3) and CombatSolver's
// src/Engine/InCombat/Mirrors/Hooks/Attack/AimPowerMirrors.cs at
// f48b69c590cb8b689ec7fdf033c2f62e625e0f1e (MIT, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Content.Powers;

[ContentId("AIM_POWER")]
public sealed class AimPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeAttack(ref Exec x, Listener self, byte attacker, ValueProps props)
    {
        if (attacker != self.Owner || !props.IsPoweredAttack() || (props & ValueProps.FromArrow) == 0)
            return;
        if (x.B.Powers[self.Index].Aux0 != 0) return;
        ushort identity = Kernel.EnsureFoolCardIdentity(ref x);
        ref Power power = ref x.B.Powers[self.Index];
        power.Aux0 = identity;
        power.Aux1 = power.Amount;
        power.AmountOnTurnStart = x.B.Fool.ActiveAttack;
    }

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (damage.Dealer != self.Owner || !damage.Props.IsPoweredAttack()
            || (damage.Props & ValueProps.FromArrow) == 0) return 0;
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 != 0 && damage.HasCardSource
            && power.Aux0 != damage.CardSource.FoolIdentity) return 0;
        decimal value = power.Amount;
        if (damage.HasCardSource && Registry.Card(damage.CardSource.Id).NativeId == "HEAVY_SHOT")
            value *= CardEffects.Var(x.B, x.R, damage.CardSource, 1);
        return value;
    }

    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        ref Power power = ref x.B.Powers[self.Index];
        if (power.AmountOnTurnStart == 0 || power.AmountOnTurnStart != x.B.Fool.ActiveAttack) return;
        int next = power.Amount - power.Aux1;
        power.AmountOnTurnStart = 0;
        power.Aux1 = 0;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, next);
    }
}

[ContentId("IMPALED_POWER")]
public sealed class ImpaledPower : PowerDef
{
    public override PowerType Type => PowerType.None;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override IReadOnlyList<string> VarNames => ["HitCounter"];

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || dealer != x.B.Powers[self.Index].Applier
            || !result.Props.IsPoweredAttack()) return;
        if (--x.B.Powers[self.Index].Aux0 > 0) return;
        x.B.Powers[self.Index].Aux0 = (int)x.R.PowerVar(Id, 0);
        Kernel.DislodgeArrow(ref x, self.Owner, result.HasCardSource, result.CardSource);
    }

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (creature == self.Owner && x.B.Player.IsAlive)
            Kernel.DislodgeAllArrows(ref x, self.Owner, hasCardSource, cardSource);
    }
}
