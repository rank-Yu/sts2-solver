using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Necrobinder;

/// <summary>
/// <c>Protector</c>: Osty attack whose calculated damage is CalculationBase + ExtraDamage × Osty's max HP
/// (<c>CalculatedVarSpecRegistry.Protector</c>).
/// </summary>
[ContentId("PROTECTOR")]
public sealed class Protector : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedDamageVar.Calculate: CalculationBase + ExtraDamage * the multiplier below; Thrash reads
    // it with a null target. OnPlay uses the same formula.
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        damage = CardEffects.Var(in x.B, x.R, in self, 0) + CardEffects.Var(in x.B, x.R, in self, 1) * (NecrobinderContent.HasOsty(x.B) ? x.B.Osty.MaxHp : 0);
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        if (!NecrobinderContent.HasOsty(x.B))
            return;
        TryGetCalculatedDamage(ref x, x.Card, x.Target, selfInHand: false, out decimal damage);
        NecrobinderContent.OstyAttack(ref x, damage);
    }
}
