using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Regent;

/// <summary><c>MinionDiveBomb</c>: the inferred single-target attack (13 damage, +3 on upgrade).</summary>
[ContentId("MINION_DIVE_BOMB")]
public sealed class MinionDiveBomb : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

/// <summary><c>MinionSacrifice</c>: the inferred self block (7, +3 on upgrade).</summary>
[ContentId("MINION_SACRIFICE")]
public sealed class MinionSacrifice : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralBlock(ref x, 0);
}

/// <summary><c>MinionStrike</c>: the inferred attack (6 damage) then draw the Cards variable (1).</summary>
[ContentId("MINION_STRIKE")]
public sealed class MinionStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

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
        Kernel.Draw(ref x, (int)CardEffects.Var(x, 1), fromHandDraw: false);
    }
}

/// <summary>
/// <c>SovereignBlade</c> (a token): BespokeCardMirrors attacks once per Repeat with the card's Damage
/// (SeekingEdgePower switches the target to every enemy), then gains block equal to ParryPower when
/// its owner has one. Repeat is always 1: no vanilla effect calls <c>SetRepeats</c>.
/// </summary>
[ContentId("SOVEREIGN_BLADE")]
public sealed class SovereignBlade : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    public override bool UsesIdentityLedger => true;

    public override TargetType ResolveTargetType(in Battle battle, Rules rules, in Card card)
        => RegentContent.HasSeekingEdge(battle) ? TargetType.AllEnemies : TargetType.AnyEnemy;

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0, hitCount: (int)CardEffects.Var(x, 1));
        if (x.NeedsChoice)
            return;
        int parry = RegentContent.ParryAmount(x.B);
        if (parry > 0)
            Kernel.GainBlock(ref x, Limits.PlayerOwner, parry, ValueProps.Move, fromCard: true);
    }
}
