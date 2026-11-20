using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Regent;

/// <summary>
/// <c>LunarBlast</c>: CalculatedHits = CalculationBase + CalculationExtra × Skill cards played this
/// turn (<c>CalculatedVarSpecRegistry.LunarBlast</c> → <c>GetSkillCardsPlayedThisTurn</c>), used as the
/// general attack's hit count.
/// </summary>
[ContentId("LUNAR_BLAST")]
public sealed class LunarBlast : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];

    public override void OnPlay(ref Exec x)
    {
        int hits = (int)(CardEffects.Var(x, 1)
            + CardEffects.Var(x, 2) * x.B.Counter(Counter.SkillCardsPlayedThisTurn));
        CardEffects.GeneralAttack(ref x, 0, hitCount: hits);
    }
}

/// <summary>
/// <c>ParticleWall</c> (2 star cost): the general block, then CardResultLocationMirrors sends a discard
/// result to the hand instead.
/// </summary>
[ContentId("PARTICLE_WALL")]
public sealed class ParticleWall : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralBlock(ref x, 0);

    public override Pile ResultLocation(in Battle battle, Rules rules, in Card card,
        Pile baseLocation, out PilePosition position)
    {
        position = PilePosition.Bottom;
        return baseLocation == Pile.Discard ? Pile.Hand : baseLocation;
    }
}

/// <summary><c>Prophesize</c>: the general draw-card recipe with the Cards variable.</summary>
[ContentId("PROPHESIZE")]
public sealed class Prophesize : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);
}

/// <summary>
/// <c>Resonance</c> (2 star cost): CorePowerSupport gains the Strength variable for the owner, then
/// applies -1 Strength to every hittable enemy in roster order.
/// </summary>
[ContentId("RESONANCE")]
public sealed class Resonance : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];

    public override void OnPlay(ref Exec x)
    {
        ushort strength = Registry.PowerId<Powers.StrengthPower>();
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, strength, (int)CardEffects.Var(x, 0),
            Limits.PlayerOwner);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            Kernel.ApplyPower(ref x, enemy, strength, -1, Limits.PlayerOwner);
        }
    }
}

/// <summary><c>Terraforming</c>: CardPowerOnPlaySupport's late pass applies VigorPower.</summary>
[ContentId("TERRAFORMING")]
public sealed class Terraforming : CardDef
{
    public override IReadOnlyList<string> VarNames => ["VigorPower"];

    public override IReadOnlyList<string> AppliedPowers => ["VIGOR_POWER"];

    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.VigorPower>(),
            (int)CardEffects.Var(x, 0), Limits.PlayerOwner);
}

/// <summary>
/// <c>Glimmer</c>: draw the Cards variable, then choose one hand card and put it on top of the draw
/// pile.
/// </summary>
[ContentId("GLIMMER")]
public sealed class Glimmer : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "PutBack"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToDrawTop, Pile.Hand, options, 1,
                selected, out int chosen))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Hand, selected[..chosen], Pile.Draw,
            PilePosition.Top);
    }
}

/// <summary>
/// <c>Charge</c>: choose the Cards number of draw-pile cards and transform each into the canonical
/// Minion Dive Bomb, upgraded when Charge is upgraded
/// (<c>CardChoiceResolution.TransformSelectedCards</c>).
/// </summary>
[ContentId("CHARGE")]
public sealed class Charge : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("MINION_DIVE_BOMB", 0)];

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        int count = Math.Min((int)CardEffects.Var(x, 0), x.B.DrawCount);
        if (count <= 0)
            return;
        Span<int> options = stackalloc int[x.B.DrawCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[count];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Transform, Pile.Draw, options, count,
                selected, out int chosen))
        {
            return;
        }
        TransformSelected(ref x, Pile.Draw, selected[..chosen], "MINION_DIVE_BOMB");
    }

    internal static void TransformSelected(ref Exec x, Pile pile, scoped ReadOnlySpan<int> selected,
        string cardId)
    {
        if (selected.IsEmpty || Kernel.IsOverOrEnding(x.B))
            return;
        if (!Registry.TryCard(cardId, out ushort replacementId))
            throw new UnsupportedException($"变化需要 {cardId} 的 Sts2Solver 定义。");
        Kernel.TransformSelectedTo(ref x, pile, selected, replacementId, x.Card.Upgrade > 0);
    }
}

/// <summary>
/// Native <c>Monologue</c>: apply an instanced MonologuePower and copy the card's Power variable into
/// that instance. Existing engine: <c>CardOnPlaySupport.Batch043</c>'s Monologue case.
/// </summary>
[ContentId("MONOLOGUE")]
public sealed class Monologue : CardDef
{
    public override bool IsIdentityContent => true;

    public override IReadOnlyList<string> VarNames => ["Power"];

    public override IReadOnlyList<string> AppliedPowers => ["MONOLOGUE_POWER"];

    public override void OnPlay(ref Exec x)
    {
        if (!Registry.TryPower("MONOLOGUE_POWER", out ushort power))
            throw new UnsupportedException("Monologue 需要 MONOLOGUE_POWER 定义。");
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, power, 1, Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        int row = Kernel.FindLastPower(x.B, Limits.PlayerOwner, power);
        if (row < 0)
            return;
        // Monologue.OnPlay sets DynamicVars.Strength after PowerCmd.Apply returns.
        x.B.Powers[row].Aux0 = (int)CardEffects.Var(x, 0);
    }
}

/// <summary>
/// Native <c>SummonForth</c>: move every Sovereign Blade outside the hand to the hand bottom, then
/// forge. Existing engine: <c>CardPileOnPlaySupport.MoveSovereignBladesToHand</c> followed by
/// <c>CardOnPlaySupport</c>'s SummonForth forge.
/// </summary>
[ContentId("SUMMON_FORTH")]
public sealed class SummonForth : ForgeCardDef
{
    public override IReadOnlyList<string> VarNames => ["Forge"];

    public override void OnPlay(ref Exec x)
    {
        if (!Registry.TryCard(Kernel.SovereignBladeId, out ushort blade))
            throw new UnsupportedException("SummonForth 需要 SOVEREIGN_BLADE 定义。");
        Pile[] piles = [Pile.Draw, Pile.Discard, Pile.Exhaust, Pile.Play];
        // Native SummonForth snapshots AllCards before CardPileCmd.Add. Capture each pile's original
        // count first: a Draw blade diverted into the discard pile is appended after the original
        // discard cards and must not be visited again when the discard pile is processed.
        Span<int> originalCounts = stackalloc int[piles.Length];
        for (int index = 0; index < piles.Length; index++)
            originalCounts[index] = Piles.Count(x.B, piles[index]);
        bool stopped = false;
        for (int pileIndex = 0; pileIndex < piles.Length && !stopped; pileIndex++)
        {
            Pile pile = piles[pileIndex];
            int inspected = 0;
            int index = 0;
            while (inspected < originalCounts[pileIndex])
            {
                if (index >= Piles.Count(x.B, pile))
                    break;
                Card card = x.B.Cards[Piles.Start(x.B, pile) + index];
                if (card.Id != blade)
                {
                    index++;
                    inspected++;
                    continue;
                }
                // Native CardPileCmd.Add moves each card to the hand bottom and diverts it to the
                // discard pile when the hand is full; MoveCard implements exactly that rule.
                if (!Kernel.MoveCardToHand(ref x, pile, index))
                {
                    stopped = true;
                    break;
                }
                inspected++;
            }
        }
        if (x.NeedsChoice)
            return;
        Kernel.Forge(ref x, (int)CardEffects.Var(x, 0));
    }
}

/// <summary><c>Bulwark</c>: the inferred recipe gains Block, then CardEffectSpecRegistry forges.</summary>
[ContentId("BULWARK")]
public sealed class Bulwark : ForgeCardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Forge"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.Forge(ref x, (int)CardEffects.Var(x, 1));
    }
}

/// <summary>
/// <c>Conqueror</c>: CardOnPlaySupport forges, then applies ConquerorPower 1 to the chosen enemy.
/// </summary>
[ContentId("CONQUEROR")]
public sealed class Conqueror : ForgeCardDef
{
    public override IReadOnlyList<string> VarNames => ["Forge"];

    public override void OnPlay(ref Exec x)
    {
        if (!Kernel.Forge(ref x, (int)CardEffects.Var(x, 0)))
            return;
        if (x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.ConquerorPower>(), 1,
            Limits.PlayerOwner);
    }
}

/// <summary><c>Reflect</c> (3 star cost): the inferred block, then CardEffectSpecRegistry's ReflectPower 1.</summary>
[ContentId("REFLECT")]
public sealed class Reflect : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override IReadOnlyList<string> AppliedPowers => ["REFLECT_POWER"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.ReflectPower>(), 1,
            Limits.PlayerOwner);
    }
}

/// <summary><c>RoyalGamble</c>: CardOnPlaySupport's <c>RoyalGamble</c> case gains Stars.</summary>
[ContentId("ROYAL_GAMBLE")]
public sealed class RoyalGamble : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Stars"];

    public override void OnPlay(ref Exec x)
        => Kernel.GainStars(ref x, (int)CardEffects.Var(x, 0));
}

/// <summary><c>Alignment</c> (2 star cost): CardOnPlaySupport's <c>Alignment</c> case gains Energy.</summary>
[ContentId("ALIGNMENT")]
public sealed class Alignment : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void OnPlay(ref Exec x)
        => Kernel.GainEnergy(ref x, CardEffects.Var(x, 0));
}

/// <summary>
/// <c>Convergence</c>: CardOnPlaySupport applies RetainHandPower 1, EnergyNextTurnPower (Energy) and
/// StarNextTurnPower (Stars), in that order.
/// </summary>
[ContentId("CONVERGENCE")]
public sealed class Convergence : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy", "Stars"];

    public override void OnPlay(ref Exec x)
    {
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.RetainHandPower>(),
            1, Limits.PlayerOwner);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.EnergyNextTurnPower>(),
            (int)CardEffects.Var(x, 0), Limits.PlayerOwner);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.StarNextTurnPower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}
