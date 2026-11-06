using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Silent;

[ContentId("MURDER")]
public sealed class Murder : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedVarSpecRegistry.Murder: CalculationBase + ExtraDamage * cards drawn this combat.
    // CalculatedDamageVar.Calculate: CalculationBase + ExtraDamage * the multiplier below; Thrash reads
    // it with a null target. OnPlay uses the same formula.
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        damage = CardEffects.Var(in x.B, x.R, in self, 0) + CardEffects.Var(in x.B, x.R, in self, 1) * (x.B.Counter(Counter.CardsDrawn));
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, selfInHand: false, out decimal damage);
        CardEffects.Attack(ref x, damage);
    }
}

[ContentId("ASSASSINATE")]
public sealed class Assassinate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack
        // and before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

[ContentId("GRAND_FINALE")]
public sealed class GrandFinale : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // GrandFinale.cs.IsPlayable: only when the draw pile is empty (CardIsPlayableMirrors).
    public override bool IsPlayable(in Battle battle, Rules rules, in Card card)
        => battle.DrawCount == 0;

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

/// <summary>
/// <c>TheHunt</c>: attack, and when the selected target's powers all allow a fatal kill and the hit
/// killed it, gain 1 <c>TheHuntPower</c> and 30 long-term resource. The extra card reward itself
/// lives on the combat room and has no representation in either simulated state; the existing
/// engine likewise only records the power and the long-term resource. Existing engine:
/// <c>CorePowerSupport</c>'s TheHunt case.
/// </summary>
[ContentId("THE_HUNT")]
public sealed class TheHunt : CardDef
{
    private const int LongTermResourceValue = 30;

    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override IReadOnlyList<string> AppliedPowers => ["THE_HUNT_POWER"];

    public override void OnPlay(ref Exec x)
    {
        byte target = x.Target;
        bool fatal = target != Limits.NoOwner && Kernel.TargetDeathTriggersFatal(x.B, target);
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice || target == Limits.NoOwner || x.B.Creature(target).IsAlive || !fatal)
            return;
        // CorePowerSupport: Apply<TheHuntPower>(owner, 1, owner) then RecordLongTermResource(30).
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<TheHuntPower>(), 1,
            Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        x.B.AddCounter(Counter.LongTermResourceValue, LongTermResourceValue);
        GrowthRewards.Add(ref x.B, GrowthRewardSource.TheHunt);
    }
}
