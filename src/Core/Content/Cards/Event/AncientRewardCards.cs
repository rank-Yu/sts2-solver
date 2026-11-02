using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Event;

[ContentId("ABUNDANCE")]
public sealed class Abundance : CardDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterPowers];

    // CardGenerationCardMirrors.AbundanceOnPlay: three distinct unlocked character Power cards,
    // each upgraded once, recorded as generated options; CardChoiceSupport.GetSpec then asks for
    // minCount 1, maxCount 1 GenerateToHand (CardChoiceResolution adds it free this turn).
    public override void OnPlay(ref Exec x)
    {
        Span<Card> options = stackalloc Card[3];
        int count = CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.Power, 3, options,
            CardPools.CharacterPowers);
        if (count == 0)
            return;
        for (int index = 0; index < count; index++)
        {
            Kernel.UpgradeCard(x.R, ref options[index]);
            x.GeneratedOptions[index] = options[index];
        }
        x.GeneratedOptionCount = count;
    }

    public override void OwnChoice(ref Exec x)
    {
        int count = x.GeneratedOptionCount;
        if (count == 0)
            return;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseGeneratedCardsInRange(ref x, ChoiceEffect.GenerateToHand,
                x.GeneratedOptions[..count], minCount: 1, maxCount: 1, selected, out int selectedCount))
        {
            return;
        }
        if (selectedCount == 0)
            return;
        Card card = x.GeneratedOptions[selected[0]];
        Kernel.MakeFreeThisTurn(x.R, ref card);
        Kernel.AddGeneratedCard(ref x, card, Pile.Hand);
    }
}

[ContentId("APOTHEOSIS")]
public sealed class Apotheosis : CardDef
{
    // CardPileOnPlaySupport.UpgradeAllOtherCards: every other card in the combat piles (AllCards
    // order: hand, draw, discard, exhaust, play) that is upgradable is upgraded once.
    public override void OnPlay(ref Exec x)
    {
        int playStart = Piles.Start(x.B, Pile.Play);
        int skip = playStart + x.PlayPosition;
        int cardCount = x.B.CardCount;
        for (int position = 0; position < cardCount; position++)
        {
            if (position == skip)
                continue;
            Kernel.UpgradeCard(x.R, ref x.B.Cards[position]);
        }
    }
}

[ContentId("APPARITION")]
public sealed class Apparition : CardDef
{
    public override IReadOnlyList<string> VarNames => ["IntangiblePower"];

    // CorePowerSupport.ApplyCardPowers: WraithForm or Apparition applies IntangiblePower.
    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.IntangiblePower>(), 0);
}

[ContentId("BRIGHTEST_FLAME")]
public sealed class BrightestFlame : CardDef
{
    public override IReadOnlyList<string> VarNames => ["MaxHp", "Energy", "Cards"];

    // Existing engine: CardOnPlayInferrer's draw recipe runs first, then the CardEffectSpecRegistry
    // BrightestFlame case (GainEnergy, then the isFromCard max-HP loss). The native OnPlay gains
    // energy before drawing; the existing engine's order is authoritative here.
    public override void OnPlay(ref Exec x)
    {
        Kernel.Draw(ref x, CardEffects.Var(x, 2), fromHandDraw: false);
        if (x.NeedsChoice)
            return;
        int maxHpLoss = (int)CardEffects.Var(x, 0);
        // Mirrors CombatSolver's CardEffectSpecRegistry BrightestFlame case: record the card's
        // dynamic MaxHp before applying it, so the search can enforce the per-combat cap.
        x.BrightestFlameMaxHpSpent = checked(x.BrightestFlameMaxHpSpent + maxHpLoss);
        Kernel.GainEnergy(ref x, CardEffects.Var(x, 1));
        if (x.NeedsChoice)
            return;
        Kernel.LoseMaxHp(ref x, Limits.PlayerOwner, maxHpLoss,
            ValueProps.Unblockable | ValueProps.Unpowered | ValueProps.Move);
    }
}

[ContentId("FORBIDDEN_GRIMOIRE")]
public sealed class ForbiddenGrimoire : CardDef
{
    public override IReadOnlyList<string> AppliedPowers => ["FORBIDDEN_GRIMOIRE_POWER"];

    // CardPowerOnPlaySupport.ForbiddenGrimoire: Apply<ForbiddenGrimoirePower>(owner, 1, owner),
    // RecordLongTermResource(50) and a growth reward.
    public override void OnPlay(ref Exec x)
    {
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.ForbiddenGrimoirePower>(), 1, Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        x.B.AddCounter(Counter.LongTermResourceValue, 50);
        GrowthRewards.Add(ref x.B, GrowthRewardSource.ForbiddenGrimoire);
    }
}

[ContentId("MAUL")]
public sealed class Maul : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Increase"];

    // BespokeCardMirrors.MaulOnPlay: two attack hits, then every Maul card in AllCards gets
    // DynamicVars.Damage += Increase and _extraDamageFromMaulPlays += Increase. Sts2Solver keeps the
    // Damage offset in Var0 and the private accumulator in Private.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hitCount: 2);
        if (x.NeedsChoice)
            return;
        int increase = (int)CardEffects.Var(x, 1);
        if (increase < 0)
            throw new UnsupportedException($"Maul 的 Increase 变量 {increase} 为负。");
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref Card card = ref x.B.Cards[position];
            if (card.Id != x.Card.Id)
                continue;
            if (card.Private > short.MaxValue - increase || card.Var0 > short.MaxValue - increase)
                throw new CapacityException("Maul.Increase", short.MaxValue);
            card.Var0 = (short)(card.Var0 + increase);
            card.Private = (short)(card.Private + increase);
        }
    }

    // Native AfterDowngraded re-adds the private accumulator after DowngradeInternal replaced the
    // dynamic variables with a fresh canonical set (Damage = 5 + _extraDamageFromMaulPlays).
    public override void AfterDowngraded(ref Battle battle, int position)
        => battle.Cards[position].Var0 = battle.Cards[position].Private;

    // The existing engine's Maul counter is only ever changed together with the Damage variable, so
    // the imported variable delta is the accumulator (as for Claw/GeneticAlgorithm).
    public override void ProjectPrivate(ref Card card) => card.Private = card.Var0;
}

[ContentId("NEOWS_FURY")]
public sealed class NeowsFury : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    // Inferred attack; CardChoiceSupport.GetSpec's NeowsFury case asks for up to Cards cards from
    // the discard pile (min 0) limited by the remaining hand capacity, then moves them to hand.
    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void OwnChoice(ref Exec x)
    {
        int room = x.B.MaxHandSize - x.B.HandCount;
        int maximum = Math.Min((int)CardEffects.Var(x, 1), room);
        if (maximum <= 0)
            return;
        Span<int> options = stackalloc int[x.B.DiscardCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[maximum];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.MoveToHand, Pile.Discard,
                options, minCount: 0, maxCount: maximum, selected, out int selectedCount))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Discard, selected[..selectedCount], Pile.Hand,
            PilePosition.Bottom);
    }
}

[ContentId("RELAX")]
public sealed class Relax : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Cards", "Energy"];

    // Inferred block recipe, then CardEffectSpecRegistry.Relax's DrawCardsNextTurnPower and
    // EnergyNextTurnPower.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.DrawCardsNextTurnPower>(), (int)CardEffects.Var(x, 1),
            Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.EnergyNextTurnPower>(), (int)CardEffects.Var(x, 2),
            Limits.PlayerOwner);
    }
}

[ContentId("SUPPRESS")]
public sealed class Suppress : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "WeakPower"];

    // Inferred attack; CorePowerSupport.ApplyCardPowers applies Weak after the enemy-death pass.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 1);
    }
}

[ContentId("WHISTLE")]
public sealed class Whistle : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // Inferred attack; CardEffectSpecRegistry.Whistle calls combat.ForceStunnedMove(target) with the
    // default follow-up (the most recent logged move, or the current node).
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        if (x.Target == Limits.NoOwner)
            throw new UnsupportedException("Whistle 需要目标才能眩晕。");
        Kernel.ForceStunnedMove(ref x, x.Target, -1);
    }
}

[ContentId("WISH")]
public sealed class Wish : CardDef
{
    // CardChoiceSupport.GetSpec's Wish case: MoveToHand, Draw, exactly one of every draw-pile card.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.DrawCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[Math.Max(1, options.Length)];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToHand, Pile.Draw, options, 1,
                selected, out int chosen))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Draw, selected[..chosen], Pile.Hand,
            PilePosition.Bottom);
    }
}

[ContentId("WRAITH_FORM")]
public sealed class WraithForm : CardDef
{
    public override IReadOnlyList<string> VarNames => ["IntangiblePower", "WraithFormPower"];

    // CorePowerSupport.ApplyCardPowers: IntangiblePower then WraithFormPower.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.IntangiblePower>(), 0);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.WraithFormPower>(), 1);
    }
}
