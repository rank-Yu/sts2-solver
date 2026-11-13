// 对照游戏 0.113.0 (16ac0fe3) 同名卡牌，以及 CombatSolver
// src/Engine/InCombat/Mirrors/Cards/OnPlay/TarotCardMirrors.cs @ f48b69c5
// MIT, Copyright (c) 2026 Torch1230 and contributors；按 Battle 布局重写。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Powers;
namespace Sts2Solver.Content.Cards.Fool;

[ContentId("THE_HIGH_PRIESTESS")]
public sealed class TheHighPriestess : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void OnPlay(ref Exec x) => Kernel.Draw(ref x, FoolEffects.Var(ref x, 0), false);
}

[ContentId("THE_EMPEROR")]
public sealed class TheEmperor : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];
    public override void OnPlay(ref Exec x) => Kernel.GainEnergy(ref x, FoolEffects.Var(ref x, 0));
}

[ContentId("THE_CHARIOT")]
public sealed class TheChariot : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x) => FoolEffects.Attack(ref x, all: true);
}

[ContentId("JUSTICE")]
public sealed class Justice : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x) => FoolEffects.Attack(ref x);
}

[ContentId("TEMPERANCE")]
public sealed class Temperance : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override void OnPlay(ref Exec x) => FoolEffects.Block(ref x);
}

[ContentId("STRENGTH")]
public sealed class Strength : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];
    public override IReadOnlyList<string> AppliedPowers => ["STRENGTH_POWER"];
    public override void OnPlay(ref Exec x) => FoolEffects.Apply<StrengthPower>(ref x, FoolEffects.Int(ref x, 0));
}

[ContentId("DEATH")]
public sealed class Death : CardDef
{
    public override IReadOnlyList<string> VarNames => ["DoomPower"];
    public override IReadOnlyList<string> AppliedPowers => ["DOOM_POWER"];
    public override void OnPlay(ref Exec x) => FoolEffects.Apply<DoomPower>(ref x, FoolEffects.Int(ref x, 0), true);
}


[ContentId("THE_EMPRESS")]
public sealed class TheEmpress : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Power"];
    public override IReadOnlyList<string> AppliedPowers => ["WEAK_POWER", "VULNERABLE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<WeakPower>(ref x, FoolEffects.Int(ref x, 0), true);
        if (!x.NeedsChoice) FoolEffects.Apply<VulnerablePower>(ref x, FoolEffects.Int(ref x, 0), true);
    }
}

[ContentId("JUDGEMENT")]
public sealed class Judgement : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Threshold"];
    public override void OnPlay(ref Exec x)
    {
        if (x.B.Creature(x.Target).IsAlive && x.B.Creature(x.Target).CurrentHp <= FoolEffects.Int(ref x, 0))
        {
            Span<byte> targets = stackalloc byte[1] { x.Target };
            Kernel.Kill(ref x, targets, false);
        }
    }
}

[ContentId("THE_HANGED_MAN")]
public sealed class TheHangedMan : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HpLoss"];
    public override IReadOnlyList<string> AppliedPowers => ["EXTRA_TURN_POWER"];
    public override void OnPlay(ref Exec x)
    {
        Span<byte> targets = stackalloc byte[1] { Limits.PlayerOwner };
        Kernel.Damage(ref x, targets, FoolEffects.Var(ref x, 0),
            ValueProps.Unblockable | ValueProps.Unpowered | ValueProps.Move, Limits.PlayerOwner, true, x.Card);
        if (!x.NeedsChoice) Kernel.ApplyPowerWithoutCardSource(ref x, Limits.PlayerOwner,
            Registry.PowerId<ExtraTurnPower>(), 1, Limits.PlayerOwner);
    }
}

[ContentId("THE_TOWER")]
public sealed class TheTower : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        damage = CardEffects.Var(x.B, x.R, self, 0) + CardEffects.Var(x.B, x.R, self, 1) * x.R.TotalFloor;
        return true;
    }
    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, false, out decimal damage);
        Kernel.CardAttack(ref x, damage, 1, AttackTargeting.Single, x.Target);
    }
}

[ContentId("THE_STAR")]
public sealed class TheStar : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy"];
    public override IReadOnlyList<string> AppliedPowers => ["ENERGY_NEXT_TURN_POWER", "DRAW_CARDS_NEXT_TURN_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<EnergyNextTurnPower>(ref x, FoolEffects.Int(ref x, 1));
        if (!x.NeedsChoice) FoolEffects.Apply<DrawCardsNextTurnPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("THE_SUN")]
public sealed class TheSun : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOOT", 0)];
    public override void OnPlay(ref Exec x)
    {
        Kernel.Draw(ref x, FoolEffects.Var(ref x, 0), false);
        if (!x.NeedsChoice) Kernel.GainEnergy(ref x, FoolEffects.Var(ref x, 1));
        if (!x.NeedsChoice) Kernel.AddGeneratedCard(ref x,
            Kernel.CreateCard(x.R, Registry.CardId<Status.Soot>()), Pile.Discard);
    }
}

[ContentId("WHEEL_OF_FORTUNE")]
public sealed class WheelOfFortune : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void OnPlay(ref Exec x) => Kernel.DiscardAllInHand(ref x, FoolEffects.Int(ref x, 0));
}

[ContentId("THE_HERMIT")]
public sealed class TheHermit : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void OnPlay(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int i = 0; i < options.Length; i++) options[i] = i;
        Span<int> chosen = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Discard, Pile.Hand, options, 1, chosen, out int count)) return;
        if (count > 0)
        {
            if (Kernel.ResolveCardType(x.B, x.R, x.B.Cards[chosen[0]]) == CardType.Curse)
                Kernel.Exhaust(ref x, Pile.Hand, chosen[0]);
            else Kernel.Discard(ref x, Pile.Hand, chosen[0]);
        }
        if (!x.NeedsChoice) Kernel.Draw(ref x, FoolEffects.Var(ref x, 0), false);
    }
}

[ContentId("THE_MAGICIAN")]
public sealed class TheMagician : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat"];
    public override void OnPlay(ref Exec x)
    {
        int count = FoolEffects.Int(ref x, 0);
        Kernel.AddOrbSlots(ref x.B, count);
        for (int i = 0; i < count && !x.NeedsChoice; i++)
            Kernel.ChannelOrb(ref x, Kernel.RandomOrb(ref x.B));
    }
}

[ContentId("THE_WORLD")]
public sealed class TheWorld : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void OnPlay(ref Exec x)
    {
        int count = x.B.DiscardCount;
        Span<int> order = stackalloc int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        var rng = x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        for (int i = count - 1; i > 0; i--)
        {
            int pick = rng.NextInt(i + 1);
            (order[i], order[pick]) = (order[pick], order[i]);
        }
        int take = Math.Max(0, Math.Min(count, Math.Min(FoolEffects.Int(ref x, 0), x.B.MaxHandSize - x.B.HandCount)));
        Kernel.MoveSelected(ref x.B, Pile.Discard, order[..take], Pile.Hand, PilePosition.Bottom);
    }
}

[ContentId("THE_FOOL")]
public sealed class TheFool : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void OnPlay(ref Exec x)
    {
        int repeats = FoolEffects.Int(ref x, 0);
        Span<Card> cards = stackalloc Card[Limits.MaxHandSize];
        Span<int> order = stackalloc int[Limits.MaxHandSize];
        for (int repeat = 0; repeat < repeats && !x.NeedsChoice; repeat++)
        {
            int count = 0;
            for (int i = 0; i < x.B.HandCount; i++)
                if (!Kernel.HasKeyword(x.B, x.B.Cards[i], Keywords.Unplayable))
                { cards[count] = x.B.Cards[i]; order[count++] = i; }
            // Both native shuffles consume a parent RNG draw, including an empty candidate list.
            int pick = Shuffle(ref x, cards[..count], order[..count]);
            if (pick < 0)
            {
                count = x.B.HandCount;
                for (int i = 0; i < count; i++) { cards[i] = x.B.Cards[i]; order[i] = i; }
                pick = Shuffle(ref x, cards[..count], order[..count]);
            }
            if (pick >= 0) Kernel.AutoPlay(ref x, Pile.Hand, pick);
        }
    }
    private static int Shuffle(ref Exec x, scoped Span<Card> cards, scoped Span<int> order)
    {
        CardSort.SortWithOrigins(cards, order, x.R, x.B, Pile.Hand);
        var rng = x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        for (int i = order.Length - 1; i > 0; i--)
        {
            int pick = rng.NextInt(i + 1);
            (order[i], order[pick]) = (order[pick], order[i]);
        }
        return order.Length > 0 ? order[0] : -1;
    }
}

[ContentId("THE_DEVIL")]
public sealed class TheDevil : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        int count = x.B.HandCount;
        Span<int> selected = stackalloc int[count];
        for (int i = 0; i < count; i++) selected[i] = i;
        Kernel.ExhaustSelected(ref x, Pile.Hand, selected);
        if (!x.NeedsChoice) Kernel.AutoPlayFromDrawPile(ref x, count, false, upgrade: x.Card.Upgrade > 0);
    }
}

[ContentId("THE_HIEROPHANT")]
public sealed class TheHierophant : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.ColorlessAll];
    public override void OnPlay(ref Exec x)
    {
        int requested = FoolEffects.Int(ref x, 0);
        if (requested > Limits.Cards) throw new CapacityException("GeneratedCards", Limits.Cards);
        Span<Card> cards = stackalloc Card[requested];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.ColorlessAll, requested, true,
            RngStream.CombatCardGeneration, cards);
        for (int i = 0; i < count && !x.NeedsChoice; i++)
        {
            Kernel.AddEnergyCostModifier(ref cards[i], -1, CostType.Relative,
                CostExpiration.EndOfTurnOrWhenPlayed, false);
            Kernel.AddGeneratedCard(ref x, cards[i], Pile.Hand);
        }
    }
}

[ContentId("THE_LOVERS")]
public sealed class TheLovers : CardDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.OtherCharacterCards];
    public override void OnPlay(ref Exec x)
    {
        int requested = x.B.MaxHandSize - x.B.HandCount;
        Span<Card> cards = stackalloc Card[requested];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.OtherCharacterCards, requested, false,
            RngStream.CombatCardGeneration, cards);
        for (int i = 0; i < count; i++)
            if (x.Card.Upgrade > 0) Kernel.UpgradeCard(x.R, ref cards[i]);
        for (int i = 0; i < count && !x.NeedsChoice; i++)
            Kernel.AddGeneratedCard(ref x, cards[i], Pile.Hand);
    }
}

[ContentId("THE_MOON")]
public sealed class TheMoon : CardDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SHIV", 0), ("SHIV", 1)];
    public override void OnPlay(ref Exec x)
    {
        int count = x.B.MaxHandSize - x.B.HandCount;
        Span<byte> marks = stackalloc byte[count];
        Span<byte> outer = stackalloc byte[count];
        marks.Clear(); outer.Clear();
        int saved = Kernel.ReserveTransientMarks(ref x, count, out int first);
        try
        {
            for (int i = 0; i < count; i++)
            {
                Card card = Kernel.CreateCard(x.R, Registry.CardId<Silent.Shiv>());
                marks[i] = checked((byte)(first + i));
                card.TransientMark = marks[i];
                Kernel.AddGeneratedCard(ref x, card, Pile.Hand);
                if (x.NeedsChoice) return;
            }
            if (x.Card.Upgrade > 0)
                for (int i = 0; i < count; i++)
                    if (Kernel.TryFindUniqueTransientMark(x.B, marks[i], out Pile pile, out int index))
                        Kernel.UpgradeCard(x.R, ref Piles.At(ref x.B, pile, index));
        }
        finally
        {
            Kernel.RestoreMarkedCards(ref x.B, marks, outer);
            Kernel.ReleaseTransientMarks(ref x, saved, first, count);
        }
    }
}
