using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Silent;

/// <summary>
/// Native <c>PhantomBlades</c>: apply PhantomBladesPower and, on the first stack, give every Shiv in
/// combat the local Retain keyword (<c>PhantomBladesPowerMirrors.AfterApplied</c>, called when the
/// power transitions from zero). Existing engine: <c>CardPowerOnPlaySupport</c>'s PhantomBlades case.
/// </summary>
[ContentId("PHANTOM_BLADES")]
public sealed class PhantomBlades : CardDef
{
    public override bool IsIdentityContent => true;

    public override IReadOnlyList<string> VarNames => ["PhantomBladesPower"];

    public override IReadOnlyList<string> AppliedPowers => ["PHANTOM_BLADES_POWER"];

    public override void OnPlay(ref Exec x)
    {
        if (!Registry.TryPower("PHANTOM_BLADES_POWER", out ushort power))
            throw new UnsupportedException("PhantomBlades 需要 PHANTOM_BLADES_POWER 定义。");
        bool alreadyPresent = Kernel.GetAmount(x.B, Limits.PlayerOwner, power) > 0;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, power, (int)CardEffects.Var(x, 0),
            Limits.PlayerOwner);
        if (x.NeedsChoice || alreadyPresent)
            return;
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref Card card = ref x.B.Cards[position];
            CardStats stats = x.R.Card(card.Id, card.Upgrade);
            if ((stats.Tags & CardTag.Shiv) != 0)
                card.Keywords |= Keywords.Retain;
        }
    }
}

[ContentId("NOXIOUS_FUMES")]
public sealed class NoxiousFumes : CardDef
{
    public override IReadOnlyList<string> VarNames => ["PoisonPerTurn"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.NoxiousFumesPower>(), 0);
}

[ContentId("ACCELERANT")]
public sealed class Accelerant : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Accelerant"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.AccelerantPower>(), 0);
}

[ContentId("INFINITE_BLADES")]
public sealed class InfiniteBlades : ShivGeneratingCardDef
{
    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.InfiniteBladesPower>(), 1, Limits.PlayerOwner);
}

[ContentId("ACCURACY")]
public sealed class Accuracy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["AccuracyPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.AccuracyPower>(), 0);
}

[ContentId("SPEEDSTER")]
public sealed class Speedster : CardDef
{
    public override IReadOnlyList<string> VarNames => ["SpeedsterPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.SpeedsterPower>(), 0);
}

[ContentId("FOOTWORK")]
public sealed class Footwork : CardDef
{
    public override IReadOnlyList<string> VarNames => ["DexterityPower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.DexterityPower>(), 0);
}
