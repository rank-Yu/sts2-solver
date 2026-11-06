using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Ironclad;

[ContentId("BURNING_PACT")]
public sealed class BurningPact : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // CardOnPlayMirrors registers an empty OnPlay; everything happens in the choice stage.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: Exhaust, Hand, 1, every hand card. ApplyPostChoiceEffects then draws; its
        // "source already in discard" branch needs an effect that moves the played card out of the
        // play pile, which no supported content has.
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Exhaust, Pile.Hand, options, 1, selected, out int chosen))
            return;
        Kernel.ExhaustSelected(ref x, Pile.Hand, selected[..chosen]);
        if (x.NeedsChoice)
            return;
        Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);
    }
}

[ContentId("INFERNAL_BLADE")]
public sealed class InfernalBlade : CardDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAttacks];

    public override void OnPlay(ref Exec x)
    {
        Span<Card> generated = stackalloc Card[1];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAttacks, 1,
            distinct: true, RngStream.CombatCardGeneration, generated);
        for (int index = 0; index < count; index++)
        {
            Kernel.MakeFreeThisTurn(x.R, ref generated[index]);
            Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand);
        }
    }
}

[ContentId("DISCOVERY")]
public sealed class Discovery : CardDef
{
    public override bool MayGenerateIdentityContent => true;

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    public override void OnPlay(ref Exec x)
    {
        Span<Card> options = stackalloc Card[3];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAll, 3,
            distinct: true, RngStream.CombatCardGeneration, options);
        if (count == 0)
            return;
        for (int index = 0; index < count; index++)
            x.GeneratedOptions[index] = options[index];
        x.GeneratedOptionCount = count;
        // CardGenerationOptions + RecordRisk(UnresolvedPlayerChoice) are compensated for Discovery
        // by CardOnPlayCompensationCatalog, so snapshot.HasRisk remains false; see the Colorless
        // Splash definition for the same reasoning.
    }

    public override void OwnChoice(ref Exec x)
    {
        int count = x.GeneratedOptionCount;
        if (count == 0)
            return;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseGeneratedCardsInRange(ref x, ChoiceEffect.GenerateToHand,
                x.GeneratedOptions[..count], minCount: 0, maxCount: 1, selected, out int selectedCount))
            return;
        if (selectedCount == 0)
            return;
        // CardChoiceResolution.AddGeneratedSelectionToHand uses PredictedCard.Clone(), which clones
        // the prediction wrapper only and leaves IsClone false; it is not a gameplay clone.
        Card card = x.GeneratedOptions[selected[0]];
        Kernel.MakeFreeThisTurn(x.R, ref card);
        Kernel.AddGeneratedCard(ref x, card, Pile.Hand);
    }
}

[ContentId("FLAME_BARRIER")]
public sealed class FlameBarrier : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "DamageBack"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.FlameBarrierPower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}

[ContentId("RAGE")]
public sealed class Rage : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Power"];

    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.RagePower>(),
            (int)CardEffects.Var(x, 0), Limits.PlayerOwner);
}

[ContentId("BLOODLETTING")]
public sealed class Bloodletting : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HpLoss", "Energy"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.LoseHpFromCard(ref x, CardEffects.Var(x, 0));
        if (x.NeedsChoice)
            return;
        Kernel.GainEnergy(ref x, CardEffects.Var(x, 1));
    }
}

[ContentId("SECOND_WIND")]
public sealed class SecondWind : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        // The native loop walks a snapshot of the non-Attack hand cards. Exhausting only removes
        // from the hand and cards entering the hand are appended, so walking the live hand and
        // stopping after the snapshot count visits the same cards.
        int remaining = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            Card card = x.B.Cards[index];
            if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Attack)
                remaining++;
        }
        for (int index = 0; index < x.B.HandCount && remaining > 0;)
        {
            // Once combat is ending the exhaust and block calls are no-ops, so stopping here leaves
            // the same final state.
            if (Kernel.IsOverOrEnding(x.B))
                return;
            Card candidate = x.B.Cards[index];
            if (Kernel.ResolveCardType(x.B, x.R, candidate) == CardType.Attack)
            {
                index++;
                continue;
            }
            // Exhausting may also append cards to the hand (BurningSticks copies the first exhausted Skill);
            // appended cards come after every snapshot card, so the walk still stops before them. The
            // candidate itself must have reached the exhaust pile.
            int exhaustBefore = x.B.ExhaustCount;
            remaining--;
            Kernel.Exhaust(ref x, Pile.Hand, index);
            if (x.B.ExhaustCount != exhaustBefore + 1)
                throw new UnsupportedException("SecondWind 的消耗没有把手牌移入消耗堆。");
            if (x.NeedsChoice)
                return;
            Kernel.GainBlock(ref x, Limits.PlayerOwner, CardEffects.Var(x, 0),
                ValueProps.Move, fromCard: true);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("EXPECT_A_FIGHT")]
public sealed class ExpectAFight : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "CalculationExtra"];

    public override void OnPlay(ref Exec x)
    {
        int strength = Kernel.GetAmount(x.B, Limits.PlayerOwner, Registry.PowerId<Powers.StrengthPower>());
        decimal block = CardEffects.Var(x, 0)
            + CardEffects.Var(x, 1) * Math.Max(0, strength);
        Kernel.GainBlock(ref x, Limits.PlayerOwner, block, ValueProps.Move, fromCard: true);
    }

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        int strength = Kernel.GetAmount(x.B, Limits.PlayerOwner, Registry.PowerId<Powers.StrengthPower>());
        damage = BodySlam.SelfVar(x, self, 0)
            + BodySlam.SelfVar(x, self, 1) * Math.Max(0, strength);
        return true;
    }
}

[ContentId("COLOSSUS")]
public sealed class Colossus : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Colossus"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.ColossusPower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}

[ContentId("EVIL_EYE")]
public sealed class EvilEye : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        if (x.B.Counter(Counter.CardsExhaustedThisTurn) > 0)
            CardEffects.GeneralBlock(ref x, 0);
    }
}

[ContentId("FORGOTTEN_RITUAL")]
public sealed class ForgottenRitual : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void OnPlay(ref Exec x)
        => Kernel.GainEnergy(ref x, CardEffects.Var(x, 0));
}

[ContentId("DRUM_OF_BATTLE")]
public sealed class DrumOfBattle : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);

    public override void AfterCardExhausted(ref Exec x, Listener self, in Card card, bool causedByEthereal)
    {
        // The listener's card slot is the exhausted copy only when that copy is the last card moved
        // into the exhaust pile; other identical copies must not trigger.
        if (x.B.ExhaustCount == 0)
            return;
        int exhaustedPosition = Piles.Start(x.B, Pile.Exhaust) + x.B.ExhaustCount - 1;
        if (self.Index != exhaustedPosition || x.B.Cards[exhaustedPosition].Id != Id)
            return;
        CardStats stats = x.R.Card(card.Id, card.Upgrade);
        // GeneratePlayCount has no supported ModifyCardPlayCount listeners, so it is one.
        Kernel.GainEnergy(ref x, stats.Vars[1] + card.Var1);
    }
}

[ContentId("BATTLE_TRANCE")]
public sealed class BattleTrance : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // BattleTrance: the inferred owner draw (Cards) runs first, then
    // CorePowerSupport.ApplyCardPowers applies NoDrawPower 1 to the owner.
    public override void OnPlay(ref Exec x)
    {
        Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.NoDrawPower>(),
            1, Limits.PlayerOwner);
    }
}
