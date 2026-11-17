using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Silent;

[ContentId("DAGGER_THROW")]
public sealed class DaggerThrow : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers settles newly dead enemies after the mirror attack and
        // before the card-specific draw compensation; HittableEnemies/Hellraiser read that phase.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        Kernel.Draw(ref x, 1, fromHandDraw: false);
    }

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: DaggerThrow → Discard, Hand, 1, every hand card.
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Discard, Pile.Hand, options, 1, selected,
                out int chosen))
        {
            return;
        }
        Kernel.DiscardAndDraw(ref x, Pile.Hand, selected[..chosen], 0);
    }
}

[ContentId("FLICK_FLACK")]
public sealed class FlickFlack : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("POISONED_STAB")]
public sealed class PoisonedStab : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "PoisonPower"];

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
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.PoisonPower>(), 1);
    }
}

[ContentId("RICOCHET")]
public sealed class Ricochet : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    // RandomTargetAttackCardMirrors.RicochetOnPlay: Repeat randomly aimed hits.
    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x, CardEffects.Var(x, 0), (int)CardEffects.Var(x, 1));
}

[ContentId("LEADING_STRIKE")]
public sealed class LeadingStrike : ShivGeneratingCardDef
{
    public override IReadOnlyList<string> VarNames => ["Shivs", "Damage"];

    public override void OnPlay(ref Exec x)
    {
        // BespokeCardMirrors.LeadingStrikeOnPlay: attack, then Shivs Shivs to hand.
        CardEffects.GeneralAttack(ref x, 1);
        if (x.NeedsChoice)
            return;
        SilentContent.GenerateShivs(ref x, (int)CardEffects.Var(x, 0), upgraded: false);
    }
}

[ContentId("SUCKER_PUNCH")]
public sealed class SuckerPunch : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "WeakPower"];

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
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 1);
    }
}

[ContentId("SLICE")]
public sealed class Slice : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("DAGGER_SPRAY")]
public sealed class DaggerSpray : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // BespokeCardMirrors.DaggerSprayOnPlay: two hits to all opponents.
    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x, CardEffects.Var(x, 0), hitCount: 2);
}

[ContentId("PREDATOR")]
public sealed class Predator : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardEffectSpecRegistry.Predator: DrawCardsNextTurnPower with the fixed amount 2.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.DrawCardsNextTurnPower>(), 2, Limits.PlayerOwner);
    }
}
