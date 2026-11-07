using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Defect;

[ContentId("HAILSTORM")]
public sealed class Hailstorm : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HailstormPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.HailstormPower>(), 0);
}

[ContentId("LOOP")]
public sealed class Loop : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Loop"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.LoopPower>(), 0);
}

[ContentId("STORM")]
public sealed class Storm : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StormPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.StormPower>(), 0);
}

[ContentId("ITERATION")]
public sealed class Iteration : CardDef
{
    public override IReadOnlyList<string> VarNames => ["IterationPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.IterationPower>(), 0);
}

[ContentId("FERAL")]
public sealed class Feral : CardDef
{
    public override IReadOnlyList<string> VarNames => ["FeralPower"];

    public override void OnPlay(ref Exec x)
    {
        int amount = (int)CardEffects.Var(x, 0);
        ushort feral = Registry.PowerId<Powers.FeralPower>();
        bool stacked = Kernel.FindPower(x.B, Limits.PlayerOwner, feral) >= 0;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, feral, amount, Limits.PlayerOwner);
        if (x.NeedsChoice || stacked)
            return;
        // FeralPower.AfterApplied runs only for a new instance (PowerCmd.Apply stacks onto an existing
        // one through ModifyAmount): it starts from the zero-cost attacks already played this turn.
        int index = Kernel.FindPower(x.B, Limits.PlayerOwner, feral);
        if (index < 0)
            return;
        x.B.Powers[index].Aux0 = x.B.Counter(Counter.ZeroCostAttackStartsThisTurn);
    }
}

[ContentId("THUNDER")]
public sealed class Thunder : CardDef
{
    public override IReadOnlyList<string> VarNames => ["ThunderPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.ThunderPower>(), 0);
}

[ContentId("SMOKESTACK")]
public sealed class Smokestack : CardDef
{
    public override IReadOnlyList<string> VarNames => ["SmokestackPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.SmokestackPower>(), 0);
}

[ContentId("SUBROUTINE")]
public sealed class Subroutine : CardDef
{
    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.SubroutinePower>(), 1, Limits.PlayerOwner);
}

[ContentId("BULK_UP")]
public sealed class BulkUp : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OrbSlots", "StrengthPower", "DexterityPower"];

    // CorePowerSupport.BulkUp: remove slots, then Strength and Dexterity.
    public override void OnPlay(ref Exec x)
    {
        Kernel.RemoveOrbSlots(ref x.B, (int)CardEffects.Var(x, 0));
        CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.StrengthPower>(), 1);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.DexterityPower>(), 2);
    }
}

[ContentId("CAPACITOR")]
public sealed class Capacitor : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat"];

    public override void OnPlay(ref Exec x)
        => Kernel.AddOrbSlots(ref x.B, (int)CardEffects.Var(x, 0));
}
