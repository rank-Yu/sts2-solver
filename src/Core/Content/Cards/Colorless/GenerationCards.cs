using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Colorless;

[ContentId("BEAT_DOWN")]
public sealed class BeatDown : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
    {
        int count = (int)CardEffects.Var(x, 0);
        int pileCount = x.B.DiscardCount;
        Span<Card> candidates = stackalloc Card[pileCount];
        Span<int> origins = stackalloc int[pileCount];
        int candidateCount = 0;
        for (int index = 0; index < pileCount; index++)
        {
            Card card = x.B.Cards[x.B.HandCount + x.B.DrawCount + index];
            if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Attack
                && !card.HasKeyword(Keywords.Unplayable))
            {
                candidates[candidateCount] = card;
                origins[candidateCount] = index;
                candidateCount++;
            }
        }
        candidates = candidates[..candidateCount];
        origins = origins[..candidateCount];
        BeatDownStableShuffle(candidates, origins, x.R, ref x.B);
        int take = Math.Min(count, candidates.Length);
        if (take == 0)
            return;
        // The existing engine keeps the selected CardModel objects, so a card that an earlier
        // auto-play moves (drawn into hand, shuffled into the draw pile, exhausted, ...) is still
        // played from its new pile. Sts2Solver marks each selected card with a one-action identity byte;
        // the mark travels with the card value through every pile move and is cleared in finally.
        int discardStart = Piles.Start(x.B, Pile.Discard);
        for (int item = 0; item < take; item++)
        {
            ref Card marked = ref x.B.Cards[discardStart + origins[item]];
            marked.TransientMark = checked((byte)(item + 1));
        }
        try
        {
            for (int item = 0; item < take; item++)
            {
                if (Kernel.IsOverOrEnding(x.B))
                    return;
                Card selected = candidates[item];
                CardStats stats = x.R.Card(selected.Id, selected.Upgrade);
                byte target = Limits.NoOwner;
                if (stats.Target == TargetType.AnyEnemy)
                {
                    // BeatDown resolves the target before AutoPlay's playability check.
                    target = Kernel.RandomHittableEnemy(ref x.B);
                }
                if (!TryFindMarked(ref x.B, (byte)(item + 1), out Pile pile, out int position))
                {
                    // The selected CardModel left every combat pile (removed from state, transformed
                    // or detached). The existing engine would still play that object; Sts2Solver has no
                    // pile-free executing card representation, so this is an explicit boundary.
                    throw new UnsupportedException("BeatDown 选中的弃牌在结算前离开了战斗牌堆。");
                }
                Kernel.AutoPlay(ref x, pile, position, target);
                if (x.NeedsChoice)
                    return;
            }
        }
        finally
        {
            ClearTransientMarks(ref x.B);
        }
    }

    private static bool TryFindMarked(ref Battle battle, byte mark, out Pile pile, out int index)
    {
        for (int absolute = 0; absolute < battle.CardCount; absolute++)
        {
            if (battle.Cards[absolute].TransientMark != mark)
                continue;
            pile = Piles.PileOf(battle, absolute, out index);
            return true;
        }
        pile = Pile.Hand;
        index = -1;
        return false;
    }

    private static void ClearTransientMarks(ref Battle battle)
    {
        // BeatDown reserves 1..63. Sly discard batches use 64..255 and may be nested around an
        // auto-played BeatDown, so their marks must survive this cleanup.
        for (int position = 0; position < battle.CardCount; position++)
        {
            if (battle.Cards[position].TransientMark < 64)
                battle.Cards[position].TransientMark = 0;
        }
        for (int slot = 0; slot < Limits.DetachedCards; slot++)
        {
            if (battle.DetachedCards[slot].TransientMark < 64)
                battle.DetachedCards[slot].TransientMark = 0;
        }
    }

    private static void BeatDownStableShuffle(Span<Card> cards, Span<int> origins, Rules rules,
        ref Battle battle)
    {
        // Native List<T>.StableShuffle: sort with CardModel.CompareTo, then Fisher-Yates.
        CardSort.SortWithOrigins(cards, origins, rules, in battle, Pile.Draw);
        RngState rng = battle.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        for (int n = cards.Length; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (cards[k], cards[n]) = (cards[n], cards[k]);
            (origins[k], origins[n]) = (origins[n], origins[k]);
        }
    }
}

[ContentId("CATASTROPHE")]
public sealed class Catastrophe : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
    {
        int count = (int)CardEffects.Var(x, 0);
        Span<Card> buffer = stackalloc Card[x.B.CardCount];
        Span<int> origins = stackalloc int[x.B.CardCount];
        for (int item = 0; item < count; item++)
        {
            int pileCount = x.B.DrawCount;
            int eligibleCount = 0;
            for (int index = 0; index < pileCount; index++)
            {
                Card card = x.B.Cards[x.B.HandCount + index];
                if (!card.HasKeyword(Keywords.Unplayable))
                {
                    buffer[eligibleCount] = card;
                    origins[eligibleCount] = index;
                    eligibleCount++;
                }
            }
            int shuffledCount = eligibleCount;
            if (shuffledCount == 0)
            {
                // The first empty StableShuffle still consumes a seed.
                x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
                for (int index = 0; index < pileCount; index++)
                {
                    buffer[index] = x.B.Cards[x.B.HandCount + index];
                    origins[index] = index;
                }
                shuffledCount = pileCount;
            }
            StableShuffle(buffer[..shuffledCount], origins[..shuffledCount], x.R, ref x.B);
            if (shuffledCount == 0)
                continue;
            Card selected = buffer[0];
            int position = origins[0];
            if (position >= x.B.DrawCount
                || !Fingerprint.Equal(Piles.At(ref x.B, Pile.Draw, position), selected))
                throw new UnsupportedException("Catastrophe 选中的抽牌在结算前离开了抽牌堆。");
            Kernel.AutoPlay(ref x, Pile.Draw, position);
            if (x.NeedsChoice)
                return;
        }
    }

    private static void StableShuffle(Span<Card> cards, Span<int> origins, Rules rules,
        ref Battle battle)
    {
        CardSort.SortWithOrigins(cards, origins, rules, in battle, Pile.Draw);
        RngState rng = battle.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        for (int n = cards.Length; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (cards[k], cards[n]) = (cards[n], cards[k]);
            (origins[k], origins[n]) = (origins[n], origins[k]);
        }
    }
}

[ContentId("JACKPOT")]
public sealed class Jackpot : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterZeroCost];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int count = (int)CardEffects.Var(x, 1);
        if (count > Limits.Cards)
            throw new CapacityException("Cards", Limits.Cards);
        Span<Card> generated = stackalloc Card[Math.Max(0, count)];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterZeroCost, count,
            distinct: false, RngStream.CombatCardGeneration, generated);
        for (int index = 0; index < sampled; index++)
        {
            if (x.Card.Upgrade > 0)
                Kernel.UpgradeCard(x.R, ref generated[index]);
            Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand);
        }
    }
}

[ContentId("JACK_OF_ALL_TRADES")]
public sealed class JackOfAllTrades : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.ColorlessExceptJackOfAllTrades];

    public override void OnPlay(ref Exec x)
    {
        int count = (int)CardEffects.Var(x, 0);
        Span<Card> generated = stackalloc Card[Math.Max(0, count)];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R,
            CardPools.ColorlessExceptJackOfAllTrades, count, distinct: true,
            RngStream.CombatCardGeneration, generated);
        for (int index = 0; index < sampled; index++)
            Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand);
    }
}

[ContentId("SPLASH")]
public sealed class Splash : CardDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.OtherCharacterAttacks];

    public override void OnPlay(ref Exec x)
    {
        Span<Card> options = stackalloc Card[3];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.OtherCharacterAttacks, 3,
            distinct: true, RngStream.CombatCardGeneration, options);
        if (count == 0)
            return;
        for (int index = 0; index < count; index++)
        {
            if (x.Card.Upgrade > 0)
                Kernel.UpgradeCard(x.R, ref options[index]);
            x.GeneratedOptions[index] = options[index];
        }
        x.GeneratedOptionCount = count;
        // RecordOptions records UnresolvedPlayerChoice, but PredictionCoverage treats every OnPlay
        // risk from a compensated card as compensated and snapshot.HasRisk stays false. The kernel
        // therefore does not raise the flag; only the imported root's uncompensated risk does.
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

[ContentId("ALCHEMIZE")]
public sealed class Alchemize : CardDef
{
    public override bool GeneratesRandomPotion => true;

    public override void OnPlay(ref Exec x)
    {
        ushort potion = Kernel.GenerateRandomPotion(ref x.B, x.R);
        if (Kernel.TryProcurePotion(ref x.B, x.R, potion))
        {
            // SimulatedCombatState.RecordLongTermResource(20) after a successful procure.
            x.B.AddCounter(Counter.LongTermResourceValue, 20);
            GrowthRewards.Add(ref x.B, GrowthRewardSource.Alchemize);
        }
    }
}

[ContentId("CALAMITY")]
public sealed class Calamity : CardDef
{
    public override IReadOnlyList<string> AppliedPowers => ["CALAMITY_POWER"];

    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.CalamityPower>(), 1, Limits.PlayerOwner);
}
