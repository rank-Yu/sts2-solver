namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>Bit 0 of <see cref="Card.Private"/>: this exact Fetch card played this turn.</summary>
    internal const short FetchPlayedBit = 1;

    /// <summary>
    /// A fresh canonical card at <paramref name="upgrade"/>, as the existing engine creates generated
    /// cards (<c>PredictedCard.Create(CanonicalModels.Card&lt;T&gt;())</c>): canonical cost, star cost and
    /// keywords, no deck version, no modifiers.
    /// </summary>
    public static Card CreateCard(Rules rules, ushort cardId, int upgrade = 0)
    {
        CardStats stats = rules.Card(cardId, upgrade);
        Card card = new()
        {
            Id = cardId,
            Upgrade = checked((byte)upgrade),
            Keywords = stats.Keywords | Keywords.CreatedAfterRoot,
            BaseCost = checked((sbyte)(stats.CostsX ? 0 : stats.EnergyCost)),
            BaseStarCost = checked((sbyte)stats.StarCost),
        };
        if (Registry.Card(cardId).UsesIdentityLedger)
            card.Private = Limits.UninitializedLedger;
        if (stats.CostsX)
            card.Flags |= CardFlags.CostsX;
        if (stats.StarCostsX)
            card.Flags |= CardFlags.StarCostsX;
        return card;
    }

    /// <summary>
    /// Samples <paramref name="count"/> cards from a root-frozen generation pool. Distinct selection
    /// mirrors <c>IEnumerableExtensions.TakeRandom</c> (<c>UnstableShuffle</c> over the whole list, then
    /// Take), so it consumes one Shuffle-stream draw per element beyond the first even when fewer cards
    /// are taken. With-replacement selection mirrors <c>Rng.NextItem</c> per card. Cards are created at
    /// upgrade 0, as <c>PredictedCard.Create(canonical, player)</c> does.
    /// </summary>
    public static int SampleCardPool(ref Battle battle, Rules rules, string poolKey, int count,
        bool distinct, RngStream stream, Span<Card> selected)
    {
        CardPool pool = rules.CardPool(poolKey);
        if (pool.Cards.Length == 0)
            return 0;
        if (count > 0 && selected.Length < count)
            throw new CapacityException("GeneratedCards", selected.Length);
        ref RngState rng = ref battle.RngStream(stream);
        if (distinct)
        {
            Span<ushort> order = stackalloc ushort[pool.Cards.Length];
            pool.Cards.AsSpan().CopyTo(order);
            for (int n = order.Length; n > 1;)
            {
                n--;
                int k = rng.NextInt(n + 1);
                (order[k], order[n]) = (order[n], order[k]);
            }
            int take = Math.Min(count, order.Length);
            for (int index = 0; index < take; index++)
                selected[index] = CreateCard(rules, order[index]);
            return take;
        }
        int sampled = Math.Max(0, count);
        for (int index = 0; index < sampled; index++)
            selected[index] = CreateCard(rules, pool.Cards[rng.NextInt(0, pool.Cards.Length)]);
        return sampled;
    }

    /// <summary>Mirrors <c>CardModel.SetToFreeThisTurn</c>: an absolute 0 energy modifier until the end of
    /// the turn or the next play, and a 0 temporary star cost cleared at turn end.</summary>
    public static void MakeFreeThisTurn(Rules rules, ref Card card)
    {
        CardStats stats = rules.Card(card.Id, card.Upgrade);
        // CardEnergyCost.SetThisTurnOrUntilPlayed: the modifier is added when cost != 0 or the
        // canonical cost is >= 0, so X-cost cards (canonical 0) still receive it.
        if (stats.EnergyCost >= 0)
        {
            if (card.EnergyModifierCount >= Limits.CardEnergyCostModifiers)
                throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
            card.EnergyModifiers[card.EnergyModifierCount] = CostModifier.Create(
                0, CostType.Absolute, CostExpiration.EndOfTurnOrWhenPlayed, reduceOnly: false);
            card.EnergyModifierCount++;
        }
        card.SetTemporaryStarCost(0, clearsAtTurnEnd: true, clearsWhenPlayed: true);
    }

    /// <summary>
    /// Mirrors <c>CardModel.SetToFreeThisCombat</c>: an absolute 0 energy modifier until the end of
    /// combat and a 0 temporary star cost that clears neither at turn end nor when played. Only used
    /// on freshly generated cards (Metamorphosis), so the single temporary-star-cost slot is free.
    /// </summary>
    public static void MakeFreeThisCombat(Rules rules, ref Card card)
    {
        CardStats stats = rules.Card(card.Id, card.Upgrade);
        if (stats.EnergyCost >= 0)
        {
            if (card.EnergyModifierCount >= Limits.CardEnergyCostModifiers)
                throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
            card.EnergyModifiers[card.EnergyModifierCount] = CostModifier.Create(
                0, CostType.Absolute, CostExpiration.EndOfCombat, reduceOnly: false);
            card.EnergyModifierCount++;
        }
        card.SetTemporaryStarCost(0, clearsAtTurnEnd: false, clearsWhenPlayed: false);
    }

    /// <summary>
    /// Stores the block an instanced power grants when its owner's block is cleared
    /// (<c>ToricToughnessPower.SetBlock</c>): the native field is a decimal, but the observed value
    /// is the settled card block gain, an integer. A fractional value cannot be represented in
    /// <see cref="Power.Aux0"/> and is rejected as an explicit boundary.
    /// </summary>
    public static void SetInstanceBlockValue(ref Battle battle, int index, decimal block)
    {
        if ((uint)index >= (uint)battle.PowerCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Power 行越界。");
        if (block != decimal.Truncate(block) || block < 0m || block > int.MaxValue)
            throw new UnsupportedException($"实例 Power 的格挡量 {block} 不是可表示的非负整数。");
        battle.Powers[index].Aux0 = (int)block;
    }

    /// <summary>
    /// Replaces the card at <paramref name="index"/> in <paramref name="pile"/> with
    /// <paramref name="replacement"/> at the same position, mirroring <c>CardCmd.Transform</c> as the
    /// existing engine resolves it (<c>CardChoiceResolution.AddTransformedCard</c>). The original has
    /// no instance identity in Sts2Solver, so the position is the identity.
    /// </summary>
    public static void ReplaceCard(ref Exec x, Pile pile, int index, in Card replacement)
    {
        // CardChoiceSupport.TransformCardToGeneratedReplacement has no ending gate: Entropy still
        // transforms a hand card when the draw already killed the last enemy (the turn-start power
        // pass walks the power table directly). Only the card- and relic-listener dispatches stop.
        Card entered = replacement;
        entered.Keywords |= Keywords.CreatedAfterRoot;
        // A replacement is a new native card: Dampen's per-card original upgrade belongs to the object
        // that was downgraded, not a generated/transformed value.
        entered.DampenOriginalUpgrade = 0;
        int markBase = ReserveTransientMarks(ref x, 1, out int markStart);
        byte mark = checked((byte)markStart);
        entered.TransientMark = mark;
        try
        {
            Piles.At(ref x.B, pile, index) = entered;
            int insert = index;
            x.B.AddCounter(Counter.CardsGeneratedThisCombat, 1);
            EnteredCombat(ref x, replacement, pile, insert);
            if (!x.NeedsChoice)
                AfterGeneratedForCombat(ref x, mark, Limits.PlayerOwner);
        }
        finally
        {
            ReleaseTransientMarks(ref x, markBase, markStart, 1);
        }
    }

    /// <summary>
    /// Mirrors <c>CardChoiceSupport.TransformCardBatch</c> / <c>CardCmd.Transform</c>: every selected
    /// card is removed first (recording its index in the shrinking pile), then the replacements are
    /// inserted in (pile, index) order, each entering combat and firing
    /// <c>AfterCardGeneratedForCombat</c>. Used by Compact's Status → Fuel transformation.
    /// </summary>
    public static void TransformCards(ref Exec x, Pile pile, scoped ReadOnlySpan<int> selected,
        Card replacement, int upgrade)
    {
        if (selected.IsEmpty || IsOverOrEnding(x.B))
            return;
        Span<Card> originals = stackalloc Card[selected.Length];
        Span<int> occurrences = stackalloc int[selected.Length];
        Span<int> removal = stackalloc int[selected.Length];
        int count = Piles.Count(x.B, pile);
        ReadOnlySpan<Card> before = Piles.ReadCards(x.B, pile);
        for (int item = 0; item < selected.Length; item++)
        {
            if ((uint)selected[item] >= (uint)count)
                throw new UnsupportedException("变换选中的卡不在原牌堆。");
            originals[item] = before[selected[item]];
            int occurrence = 0;
            for (int prior = 0; prior < selected[item]; prior++)
            {
                if (Fingerprint.Equal(before[prior], originals[item]))
                    occurrence++;
            }
            occurrences[item] = occurrence;
            removal[item] = selected[item];
        }
        // Remove in the selected order; byte-equal cards at other slots are not interchangeable.
        for (int item = 0; item < selected.Length; item++)
        {
            int occurrence = occurrences[item];
            for (int prior = 0; prior < item; prior++)
            {
                if (selected[prior] < selected[item]
                    && Fingerprint.Equal(originals[prior], originals[item]))
                {
                    occurrence--;
                }
            }
            int position = -1;
            ReadOnlySpan<Card> cards = Piles.ReadCards(x.B, pile);
            for (int index = 0; index < cards.Length; index++)
            {
                if (!Fingerprint.Equal(cards[index], originals[item]))
                    continue;
                if (occurrence-- == 0)
                {
                    position = index;
                    break;
                }
            }
            if (position < 0)
                throw new UnsupportedException("变换选中的卡在结算前离开了原牌堆。");
            removal[item] = position;
            Piles.RemoveAt(ref x.B, pile, position);
        }
        // CardCmd.Transform sorts by pile (all equal here) then index, ascending.
        for (int item = 1; item < removal.Length; item++)
        {
            int index = item;
            while (index > 0 && removal[index - 1] > removal[index])
            {
                (removal[index - 1], removal[index]) = (removal[index], removal[index - 1]);
                index--;
            }
        }
        for (int item = 0; item < removal.Length; item++)
        {
            Card card = replacement;
            card.Upgrade = checked((byte)upgrade);
            card.DampenOriginalUpgrade = 0;
            card.BaseCost = checked((sbyte)x.R.Card(card.Id, upgrade).EnergyCost);
            card.BaseStarCost = checked((sbyte)x.R.Card(card.Id, upgrade).StarCost);
            card.Keywords = x.R.Card(card.Id, upgrade).Keywords | Keywords.CreatedAfterRoot;
            int markBase = ReserveTransientMarks(ref x, 1, out int markStart);
            byte mark = checked((byte)markStart);
            card.TransientMark = mark;
            try
            {
                int insert = Math.Min(removal[item], Piles.Count(x.B, pile));
                Piles.Insert(ref x.B, pile, insert, card);
                x.B.AddCounter(Counter.CardsGeneratedThisCombat, 1);
                EnteredCombat(ref x, card, pile, insert);
                if (x.NeedsChoice)
                    return;
                if (!AfterGeneratedForCombat(ref x, mark, Limits.PlayerOwner))
                    return;
            }
            finally
            {
                ReleaseTransientMarks(ref x, markBase, markStart, 1);
            }
        }
    }

    /// <summary>
    /// <c>SimulatedCombatState.CardEventHistory.AfterCardEnteredCombat</c>: the player's relic listeners
    /// (<c>AfterCardEnteredCombat</c>, GhostSeed) run before the card-specific cost reactions
    /// (Stomp, Pinpoint, Banshee's Cry), then <c>CardDef.AfterEnteredCombat</c>.
    /// </summary>
    private static void EnteredCombat(ref Exec x, in Card card, Pile pile, int index)
    {
        AfterCardEnteredCombatHook entered = new() { Pile = pile, Index = index, Card = card };
        HookDispatch.Dispatch(ref x, ref entered, gatedByEnding: false);
        if (x.NeedsChoice)
            return;
        // CardEventHistory.AfterCardEnteredCombat returns before its card-specific switch for clones.
        if (!card.IsClone)
        {
            Registry.Card(card.Id).AfterEnteredCombat(ref x, pile, index);
            if (x.NeedsChoice)
                return;
        }
        // SimulatedCombatState.CardEventHistory.AfterCardEnteredCombat ends with the affliction
        // normalization (the one-time Vital Spark/Smog entry check lives there).
        NormalizeCardAfflictions(ref x);
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.AddGeneratedCardsToCombat</c> for one card: insertion as
    /// <c>AddToPile</c> does for a card entering combat (full hand diverts to discard, random position
    /// from the Shuffle stream), then the entered-combat sink and <c>AfterCardGeneratedForCombat</c>.
    /// Returns false only for a combat that is no longer in progress or a pending choice; an add
    /// refused because the combat is ending still records the generation and returns true so the
    /// caller continues exactly like <c>AddGeneratedCardsToCombat</c>.
    /// </summary>
    public static bool AddGeneratedCard(ref Exec x, in Card card, Pile pile,
        PilePosition position = PilePosition.Bottom, byte creator = Limits.PlayerOwner)
    {
        if (!IsInProgress(x.B))
            return false;
        // CombatPredictionHistory.CardGenerated is recorded before AddToPile; a card refused because the
        // combat is ending still counts as generated (HeirloomHammer clones a card on a killing blow).
        // AddGeneratedCardsToCombat keeps looping and the card's OnPlay keeps running after the refused
        // add, so this reports success without inserting the card: callers must not treat the value as
        // being in a pile, and no generation hook runs for it.
        if (creator == Limits.PlayerOwner)
            x.B.AddCounter(Counter.CardsGeneratedThisCombat, 1);
        if (IsEnding(x.B) || x.B.Player.IsDead)
            return true;
        Pile target = pile == Pile.Hand && x.B.HandCount >= x.B.MaxHandSize ? Pile.Discard : pile;
        int count = Piles.Count(x.B, target);
        int index = position switch
        {
            PilePosition.Bottom => count,
            PilePosition.Top => 0,
            PilePosition.Random => x.B.RngStream(RngStream.Shuffle).NextInt(count + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
        };
        Card added = card;
        added.Keywords |= Keywords.CreatedAfterRoot;
        // Native passes the generated CardModel object to every AfterCardGeneratedForCombat
        // listener. The value has no object identity in Sts2Solver, so stamp it with a transient
        // identity byte for the duration of the entry and generation hooks. A listener can move it
        // (Gremlin Horn's draw shuffles the discard pile, taking a just-generated Slimed along), and
        // the following listeners still receive the card's new absolute position.
        int markBase = ReserveTransientMarks(ref x, 1, out int markStart);
        byte mark = checked((byte)markStart);
        added.TransientMark = mark;
        try
        {
            Piles.Insert(ref x.B, target, index, added);
            EnteredCombat(ref x, added, target, index);
            if (x.NeedsChoice)
                return false;
            return AfterGeneratedForCombat(ref x, mark, creator);
        }
        finally
        {
            ReleaseTransientMarks(ref x, markBase, markStart, 1);
        }
    }

    /// <summary>
    /// <c>HookMirrors.AfterCardGeneratedForCombat</c> after a generated card entered combat. The hook
    /// receives the card's absolute position; <paramref name="mark"/> follows the value if an earlier
    /// listener moved it to another pile.
    /// </summary>
    private static bool AfterGeneratedForCombat(ref Exec x, byte mark, byte creator)
    {
        if ((Registry.AnyHooks & HookMask.AfterCardGeneratedForCombat) != 0)
        {
            Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
            int listenerCount = HookDispatch.CollectListeners(x.B, HookMask.AfterCardGeneratedForCombat, listeners);
            for (int listener = 0; listener < listenerCount; listener++)
            {
                int position = GeneratedCardPosition(ref x, mark);
                HookDispatch.Definition(x.B, listeners[listener])
                    .AfterCardGeneratedForCombat(ref x, listeners[listener], position, creator);
                if (x.NeedsChoice)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Current absolute position of a card carrying <paramref name="mark"/>. Native hands the same
    /// CardModel to every listener even after an earlier listener moves it between combat piles; a
    /// value that left every pile cannot be addressed by position and is an explicit boundary.
    /// </summary>
    private static int GeneratedCardPosition(ref Exec x, byte mark)
    {
        if (!TryFindUniqueTransientMark(x.B, mark, out Pile pile, out int index))
            throw new UnsupportedException("生成牌触发时该牌已不在牌堆。");
        return Piles.Start(x.B, pile) + index;
    }

    /// <summary>
    /// Mirrors <c>CardModel.CreateClone</c>: the clone keeps the card's mutable state but not
    /// ExhaustOnNextPlay, and <c>AfterCloned</c> clears the deck link and the removed-from-state mark.
    /// </summary>
    public static Card CloneCard(in Card card)
    {
        Card clone = card;
        clone.Flags &= ~(CardFlags.ExhaustOnNextPlay | CardFlags.DeckCard
            | CardFlags.RemovedFromState | CardFlags.PowerAfflictionChecked);
        clone.Keywords |= Keywords.CreatedAfterRoot;
        clone.IsClone = true;
        // The clone is a new native object; a BeatDown transient mark and Dampen's per-card original
        // upgrade belong to the original.
        clone.TransientMark = 0;
        clone.DampenOriginalUpgrade = 0;
        // PredictedCard/CardModel clones are new objects: the existing engine's per-card dictionaries
        // have no entry for them until their first normalization/play. Other cards keep Private (the
        // native clone copies Claw/Rampage-style private counters).
        CardDef cloneDef = Registry.Card(card.Id);
        if (cloneDef.UsesIdentityLedger)
            clone.Private = Limits.UninitializedLedger;
        if (cloneDef.TracksFetchPlay)
            clone.Private &= ~FetchPlayedBit;
        return clone;
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.Heal</c>.</summary>
    public static void Heal(ref Exec x, byte owner, decimal amount)
    {
        if (IsEnding(x.B) && owner != Limits.PlayerOwner)
            return;
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "治疗量不能为负。");
        ref Creature creature = ref x.B.Creature(owner);
        int before = creature.CurrentHp;
        creature.CurrentHp = (int)Math.Min(creature.CurrentHp + amount, creature.MaxHp);
        int restored = creature.CurrentHp - before;
        if (restored > 0 && owner == Limits.PlayerOwner)
            x.B.AddCounter(Counter.RecoveredHp, restored);
        if (amount > 0m)
            AfterCurrentHpChanged(ref x, owner, amount);
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.GainMaxHp</c>: the capped max-HP delta is then healed.</summary>
    public static void GainMaxHp(ref Exec x, byte owner, decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "最大生命增加量不能为负。");
        ref Creature creature = ref x.B.Creature(owner);
        int before = creature.MaxHp;
        creature.MaxHp = Math.Clamp((int)(before + amount), 1, Limits.ValueClamp);
        creature.CurrentHp = Math.Min(creature.CurrentHp, creature.MaxHp);
        Heal(ref x, owner, creature.MaxHp - before);
    }

    /// <summary>
    /// Mirrors <c>AfterDamageGivenMirrors.HandlePaperCutsPower</c>'s max-HP loss: when the new maximum
    /// is below the current HP, the difference is dealt as unblockable unpowered damage with no
    /// dealer, then the maximum is clamped to at least 1.
    /// </summary>
    public static void LoseMaxHp(ref Exec x, byte owner, int amount)
        => LoseMaxHp(ref x, owner, amount, ValueProps.Unblockable | ValueProps.Unpowered);

    /// <summary>
    /// <see cref="LoseMaxHp(ref Exec, byte, int)"/> with the native <c>isFromCard</c> damage props.
    /// <c>CreatureCmd.LoseMaxHp(..., isFromCard: true)</c> (BrightestFlame) adds
    /// <c>ValueProp.Move</c> to the unblockable unpowered HP loss.
    /// </summary>
    public static void LoseMaxHp(ref Exec x, byte owner, int amount, ValueProps props)
    {
        if (amount <= 0)
            return;
        ref Creature creature = ref x.B.Creature(owner);
        int newMaxHp = creature.MaxHp - amount;
        if (newMaxHp < creature.CurrentHp)
        {
            Span<byte> targets = stackalloc byte[1];
            targets[0] = owner;
            Damage(ref x, targets, creature.CurrentHp - newMaxHp, props, Limits.NoOwner,
                hasCardSource: false, default);
            if (x.NeedsChoice)
                return;
        }
        ref Creature current = ref x.B.Creature(owner);
        current.MaxHp = Math.Max(1, newMaxHp);
        // Creature.SetMaxHpInternal clamps the current HP to the new maximum.
        current.CurrentHp = Math.Min(current.CurrentHp, current.MaxHp);
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.GainEnergy</c> (no supported energy-gain modifiers yet).</summary>
    public static void GainEnergy(ref Exec x, decimal amount)
    {
        if (IsEnding(x.B) || amount <= 0m)
            return;
        x.B.Energy = (int)Math.Clamp(x.B.Energy + amount, 0m, Limits.ValueClamp);
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.LoseEnergy</c>.</summary>
    public static void LoseEnergy(ref Exec x, decimal amount)
    {
        if (IsEnding(x.B) || amount <= 0m)
            return;
        x.B.Energy = (int)Math.Clamp(x.B.Energy - amount, 0m, Limits.ValueClamp);
    }
}
