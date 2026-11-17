using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Regent;

/// <summary>
/// <c>CollisionCourse</c>: the inferred attack, then CardEffectSpecRegistry adds one Debris to hand.
/// </summary>
[ContentId("COLLISION_COURSE")]
public sealed class CollisionCourse : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("DEBRIS", 0)];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        if (!Registry.TryCard("DEBRIS", out ushort debris))
            throw new UnsupportedException("CollisionCourse 需要 DEBRIS 的 Sts2Solver 定义。");
        Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, debris), Pile.Hand);
    }
}

/// <summary>
/// <c>GuidingStar</c> (1 star cost): the inferred attack, then CardEffectSpecRegistry's
/// <c>GuidingStar</c> entry applies DrawCardsNextTurnPower with the Cards variable.
/// </summary>
[ContentId("GUIDING_STAR")]
public sealed class GuidingStar : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override IReadOnlyList<string> AppliedPowers => ["DRAW_CARDS_NEXT_TURN_POWER"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.DrawCardsNextTurnPower>(), (int)CardEffects.Var(x, 1),
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>CrushUnder</c>: attack every enemy, then CardEffectSpecRegistry applies CrushUnderPower (a
/// temporary Strength loss) to every hittable enemy in roster order.
/// </summary>
[ContentId("CRUSH_UNDER")]
public sealed class CrushUnder : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "StrengthLoss"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        ushort power = Registry.PowerId<Powers.CrushUnderPower>();
        int amount = (int)CardEffects.Var(x, 1);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            Kernel.ApplyTemporaryStrength(ref x, enemy, power, amount, Limits.PlayerOwner, -1);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary><c>CelestialMight</c>: the general attack with the Repeat variable as its hit count.</summary>
[ContentId("CELESTIAL_MIGHT")]
public sealed class CelestialMight : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    public override void OnPlay(ref Exec x)
        => CardEffects.GeneralAttack(ref x, 0, hitCount: (int)CardEffects.Var(x, 1));
}

/// <summary>
/// <c>CrescentSpear</c> (1 star cost): CalculatedDamage = CalculationBase + ExtraDamage × the number
/// of cards in the combat piles whose canonical star cost is non-negative or X
/// (<c>CalculatedVarSpecRegistry.CrescentSpear</c>).
/// </summary>
[ContentId("CRESCENT_SPEAR")]
public sealed class CrescentSpear : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedDamageVar.Calculate: CalculationBase + ExtraDamage * the multiplier below; Thrash reads
    // it with a null target. OnPlay uses the same formula.
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        damage = CardEffects.Var(in x.B, x.R, in self, 0) + CardEffects.Var(in x.B, x.R, in self, 1) * (StarCards(in x.B, x.R));
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, selfInHand: false, out decimal damage);
        CardEffects.Attack(ref x, damage);
    }

    /// <summary>Every combat card with a star cost (<c>CanonicalStarCost >= 0 || HasStarCostX</c>).</summary>
    private static int StarCards(in Battle battle, Rules rules)
    {
        int starCards = 0;
        for (int position = 0; position < battle.CardCount; position++)
        {
            ref readonly Card candidate = ref battle.Cards[position];
            CardStats stats = rules.Card(candidate.Id, candidate.Upgrade);
            if (stats.StarCost >= 0 || stats.StarCostsX)
                starCards++;
        }
        return starCards;
    }
}

/// <summary>
/// <c>PhotonCut</c>: attack, draw the Cards variable, then choose the PutBack number of hand cards and
/// put them on top of the draw pile. The played card is in the play pile, so every hand card is an
/// option.
/// </summary>
[ContentId("PHOTON_CUT")]
public sealed class PhotonCut : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards", "PutBack"];

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

    public override void OwnChoice(ref Exec x)
    {
        int count = Math.Min((int)CardEffects.Var(x, 2), x.B.HandCount);
        if (count <= 0)
            return;
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[count];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToDrawTop, Pile.Hand, options, count,
                selected, out int chosen))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Hand, selected[..chosen], Pile.Draw,
            PilePosition.Top);
    }
}

/// <summary><c>WroughtInWar</c>: the inferred attack, then CardEffectSpecRegistry forges.</summary>
[ContentId("WROUGHT_IN_WAR")]
public sealed class WroughtInWar : ForgeCardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Forge"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.Forge(ref x, (int)CardEffects.Var(x, 1));
    }
}

/// <summary><c>SolarStrike</c>: attack, then CardEffectSpecRegistry's <c>SolarStrike</c> gains Stars.</summary>
[ContentId("SOLAR_STRIKE")]
public sealed class SolarStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Stars"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.GainStars(ref x, (int)CardEffects.Var(x, 1));
    }
}

/// <summary><c>AstralPulse</c> (3 star cost): BespokeCardMirrors attacks every enemy twice.</summary>
[ContentId("ASTRAL_PULSE")]
public sealed class AstralPulse : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0, hitCount: 2);
}
