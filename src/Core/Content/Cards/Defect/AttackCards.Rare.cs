using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Defect;

[ContentId("ICE_LANCE")]
public sealed class IceLance : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("FROST_ORB"), (int)CardEffects.Var(x, 1));
    }
}

[ContentId("SHATTER")]
public sealed class Shatter : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int orbs = x.B.OrbCount;
        for (int index = 0; index < orbs; index++)
        {
            Kernel.EvokeNext(ref x, repeat: 2);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("HYPERBEAM")]
public sealed class Hyperbeam : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "FocusPower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CardEffectSpecRegistry's Hyperbeam → ApplyTemporaryFocusLoss<HyperbeamFocusDownPower>.
        Kernel.ApplyTemporaryFocus(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.HyperbeamFocusDownPower>(), (int)CardEffects.Var(x, 1),
            Limits.PlayerOwner, sign: -1);
    }
}

[ContentId("METEOR_STRIKE")]
public sealed class MeteorStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // OrbCardMirrors.MeteorStrikeOnPlay: AttackSingle, then channel three Plasma orbs.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("PLASMA_ORB"), 3);
    }
}

[ContentId("HELIX_DRILL")]
public sealed class HelixDrill : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CalculatedVarSpecRegistry.HelixDrill: energy spent this turn minus this card's current cost.
    public override void OnPlay(ref Exec x)
    {
        int hits = Math.Max(0, x.B.Counter(Counter.EnergySpentThisTurn)
            - Kernel.EnergyCostWithModifiers(x.B, x.R, x.Card, Pile.Play));
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}

[ContentId("FLAK_CANNON")]
public sealed class FlakCannon : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        // RandomTargetAttackCardMirrors.FlakCannonOnPlay: exhaust every Status card (outside the
        // exhaust pile), then a random-target attack with one hit per status.
        int hits = CardEffects.CountStatusCards(x.B, x.R);
        CardEffects.ExhaustAllStatusCards(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}

[ContentId("ADAPTIVE_STRIKE")]
public sealed class AdaptiveStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardEffectSpecRegistry.AdaptiveStrike: attack, then a clone with cost 0 this combat to discard.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Card clone = Kernel.CloneCard(x.Card);
        Kernel.AddEnergyCostModifier(ref clone, 0, CostType.Absolute, CostExpiration.EndOfCombat,
            reduceOnly: false);
        Kernel.AddGeneratedCard(ref x, clone, Pile.Discard);
    }
}

[ContentId("ALL_FOR_ONE")]
public sealed class AllForOne : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardEffectSpecRegistry.AllForOne: attack, then return every zero-cost non-X Attack/Skill/Power
    // from the discard pile to hand in discard order.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int count = x.B.DiscardCount;
        int start = Piles.Start(x.B, Pile.Discard);
        Span<Card> chosen = stackalloc Card[count];
        int selected = 0;
        for (int offset = 0; offset < count; offset++)
        {
            Card card = x.B.Cards[start + offset];
            CardStats stats = x.R.Card(card.Id, card.Upgrade);
            if (stats.Type is not (CardType.Attack or CardType.Skill or CardType.Power))
                continue;
            if ((card.Flags & CardFlags.CostsX) != 0)
                continue;
            if (Kernel.EnergyCostWithModifiers(x.B, x.R, card, Pile.Discard) != 0)
                continue;
            chosen[selected++] = card;
        }
        for (int item = 0; item < selected; item++)
        {
            int position = FindDiscard(x.B, chosen[item]);
            if (position < 0)
                throw new UnsupportedException("AllForOne 选中的弃牌在结算前离开了弃牌堆。");
            if (!Kernel.MoveCard(ref x.B, Pile.Discard, position, Pile.Hand))
                return;
        }
    }

    private static int FindDiscard(in Battle battle, in Card card)
    {
        ReadOnlySpan<Card> discard = Piles.ReadCards(battle, Pile.Discard);
        for (int index = 0; index < discard.Length; index++)
        {
            if (Fingerprint.Equal(discard[index], card))
                return index;
        }
        return -1;
    }
}
