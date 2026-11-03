namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Card selections and the effects applied to the selected cards, mirroring
/// <c>SimulatedCombatState.ResolveActionCardChoice</c> and <c>CardChoiceSupport.Apply</c>.
/// </summary>
public static partial class Kernel
{
    /// <summary>
    /// A choice built by <c>CardChoiceSupport.Spec</c>: select exactly <paramref name="count"/> of
    /// <paramref name="options"/> (all of them when fewer). With no options the existing engine builds
    /// no spec and asks nothing, so this returns true with nothing selected.
    /// </summary>
    public static bool ChooseCards(ref Exec x, ChoiceEffect effect, Pile pile, scoped ReadOnlySpan<int> options,
        int count, scoped Span<int> selected, out int selectedCount)
    {
        if (options.IsEmpty)
        {
            selectedCount = 0;
            return true;
        }
        return ChooseCardsInRange(ref x, effect, pile, options, count, count, selected, out selectedCount);
    }

    /// <summary>
    /// A choice built by <c>CardChoiceSupport.RangeSpec</c>: requests a selection among
    /// <paramref name="options"/> (indexes into <paramref name="pile"/>, in the existing engine's option
    /// order). Bounds are clamped to the option count as <c>CardChoiceSupport.BuildChoices</c> does; an
    /// empty option list still asks for an (empty) selection. Returns false when the action carried no
    /// choice for it; the caller must unwind. Selected indexes are written in option order.
    /// </summary>
    public static bool ChooseCardsInRange(ref Exec x, ChoiceEffect effect, Pile pile, scoped ReadOnlySpan<int> options,
        int minCount, int maxCount, scoped Span<int> selected, out int selectedCount)
    {
        bool indexSelection = x.B.CardCount > Limits.LegacyCards
            && options.Length > Limits.MaxBitSetChoiceOptions
            && minCount == 1 && maxCount == 1;
        int optionCapacity = indexSelection ? Limits.MaxChoiceOptions : Limits.MaxBitSetChoiceOptions;
        if (options.Length > optionCapacity)
            throw new CapacityException("Choice.Options", optionCapacity);
        ChoiceRequest request = new()
        {
            Kind = ChoiceKind.SelectCards,
            Effect = effect,
            Timing = x.ChoiceTiming,
            Source = pile,
            OptionCount = checked((ushort)options.Length),
            MinCount = (byte)Math.Min(minCount, options.Length),
            MaxCount = (byte)Math.Min(maxCount, options.Length),
            IndexSelection = indexSelection,
            Origin = x.ChoiceOrigin,
        };
        int start = Piles.Start(x.B, pile);
        for (int index = 0; index < options.Length; index++)
            request.Options[index] = checked((ushort)(start + options[index]));
        selectedCount = 0;
        if (!x.TryTakeChoice(request, out ulong selection))
            return false;
        if (request.UsesIndexSelection)
        {
            if (selection >= (ulong)options.Length)
                throw new InvalidOperationException($"索引选择 {selection} 超过选项数 {options.Length}。");
            selected[selectedCount++] = options[(int)selection];
            return true;
        }
        for (int index = 0; index < options.Length; index++)
        {
            if ((selection & (1UL << index)) != 0)
                selected[selectedCount++] = options[index];
        }
        return true;
    }

    /// <summary>
    /// A fixed option list (Knowledge Demon's curse cards, Toolbox/ChoicesParadox): one of the
    /// options is selected. Options are carried by value in
    /// <see cref="ChoiceRequest.GeneratedOptions"/> because they never enter a pile. Returns false
    /// when the action carried no choice for it; selected indexes are written in option order.
    /// </summary>
    public static bool ChooseOptionsInRange(ref Exec x, ChoiceEffect effect,
        scoped ReadOnlySpan<Card> options, int minCount, int maxCount, scoped Span<int> selected,
        out int selectedCount)
    {
        if (options.IsEmpty)
            throw new UnsupportedException("固定选项选择没有候选。");
        if (options.Length > Limits.MaxGeneratedChoiceOptions)
            throw new CapacityException("Choice.GeneratedOptions", Limits.MaxGeneratedChoiceOptions);
        ChoiceRequest request = new()
        {
            Kind = ChoiceKind.SelectOption,
            Effect = effect,
            Timing = x.ChoiceTiming,
            SourceIsNone = true,
            OptionsAreGenerated = true,
            OptionCount = checked((ushort)options.Length),
            MinCount = (byte)Math.Min(minCount, options.Length),
            MaxCount = (byte)Math.Min(maxCount, options.Length),
            Origin = x.ChoiceOrigin,
        };
        for (int index = 0; index < options.Length; index++)
            request.GeneratedOptions[index] = options[index];
        selectedCount = 0;
        if (!x.TryTakeChoice(request, out ulong selection))
            return false;
        for (int index = 0; index < options.Length; index++)
        {
            if ((selection & (1UL << index)) != 0)
                selected[selectedCount++] = index;
        }
        return true;
    }

    /// <summary>Exactly one option from a fixed generated list.</summary>
    public static bool ChooseOption(ref Exec x, ChoiceEffect effect, scoped ReadOnlySpan<Card> options,
        out int selected)
    {
        Span<int> selectedIndexes = stackalloc int[1];
        if (!ChooseOptionsInRange(ref x, effect, options, 1, 1, selectedIndexes, out int count))
        {
            selected = 0;
            return false;
        }
        if (count != 1)
            throw new InvalidOperationException("固定选项选择没有选中一张。");
        selected = selectedIndexes[0];
        return true;
    }

    /// <summary>
    /// Host-only choice origin of a listener snapshot. Relics, powers, cards and potions map to
    /// their registry id; other listener kinds have no displayable source and stay empty.
    /// </summary>
    internal static ChoiceSource ListenerChoiceSource(in Battle battle, in Listener listener)
        => listener.Kind switch
        {
            ListenerKind.Relic => new ChoiceSource(ChoiceSourceKind.Relic,
                battle.Relics[listener.Index].Id),
            ListenerKind.Power => new ChoiceSource(ChoiceSourceKind.Power,
                battle.Powers[listener.Index].Id),
            ListenerKind.Card => new ChoiceSource(ChoiceSourceKind.Card,
                battle.Cards[listener.Index].Id, battle.Cards[listener.Index].Upgrade),
            ListenerKind.Potion => new ChoiceSource(ChoiceSourceKind.Potion,
                checked((ushort)(battle.Potions[listener.Index] - 1))),
            _ => default,
        };

    /// <summary>
    /// A selected card remembered by value and by occurrence: it is the <see cref="Occurrence"/>-th card
    /// byte-equal to <see cref="Card"/> in its pile. Equal cards at different positions are not
    /// interchangeable (removing one or the other leaves the pile in a different order), so the
    /// occurrence plays the role of the existing engine's card identity.
    /// </summary>
    private struct SelectedCard
    {
        public Card Card;
        public int Occurrence;
        public int Position;
    }

    /// <summary>Records the selected pile indexes (ascending) by value and occurrence before any effect runs.</summary>
    private static void SnapshotSelected(in Battle battle, Pile pile, scoped ReadOnlySpan<int> selected,
        Span<SelectedCard> cards)
    {
        int start = Piles.Start(battle, pile);
        for (int index = 0; index < selected.Length; index++)
        {
            Card card = battle.Cards[start + selected[index]];
            int occurrence = 0;
            for (int prior = 0; prior < selected[index]; prior++)
            {
                if (Fingerprint.Equal(battle.Cards[start + prior], card))
                    occurrence++;
            }
            cards[index] = new SelectedCard { Card = card, Occurrence = occurrence, Position = selected[index] };
        }
    }

    /// <summary>
    /// Current pile index of the <paramref name="item"/>-th selected card. A previously moved card
    /// equal to it lowers its occurrence only when that card was originally before it; selections need
    /// not be ascending (Anointed takes its cards in the shuffled order).
    /// </summary>
    private static int LocateSelected(in Battle battle, Pile pile, scoped ReadOnlySpan<SelectedCard> cards, int item)
    {
        int occurrence = cards[item].Occurrence;
        for (int prior = 0; prior < item; prior++)
        {
            if (cards[prior].Position < cards[item].Position
                && Fingerprint.Equal(cards[prior].Card, cards[item].Card))
                occurrence--;
        }
        int start = Piles.Start(battle, pile);
        int count = Piles.Count(battle, pile);
        for (int index = 0; index < count; index++)
        {
            if (!Fingerprint.Equal(battle.Cards[start + index], cards[item].Card))
                continue;
            if (occurrence-- == 0)
                return index;
        }
        throw new UnsupportedException("所选卡牌在结算前已离开原牌堆。");
    }

    /// <summary>
    /// <c>CardChoiceResolution.TransformSelectedCards</c>: replace each selected card with a canonical
    /// card of <paramref name="cardId"/> (upgraded once when <paramref name="upgrade"/> is set) at the
    /// same position. Transform hooks run between cards, so each card is located again
    /// (<see cref="LocateSelected"/>).
    /// </summary>
    public static void TransformSelectedTo(ref Exec x, Pile pile, scoped ReadOnlySpan<int> selected,
        ushort cardId, bool upgrade)
    {
        if (selected.IsEmpty || IsOverOrEnding(x.B))
            return;
        Span<SelectedCard> cards = stackalloc SelectedCard[selected.Length];
        SnapshotSelected(x.B, pile, selected, cards);
        for (int index = 0; index < cards.Length; index++)
        {
            int position = LocateSelected(x.B, pile, cards, index);
            Card replacement = CreateCard(x.R, cardId);
            if (upgrade)
                UpgradeCard(x.R, ref replacement);
            ReplaceCard(ref x, pile, position, replacement);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>TurnStartChoiceSupport.ContinueExhaustSelection</c>: exhausts the selected cards in
    /// selection order. Exhaust hooks run between cards, so each card is located again (<see cref="LocateSelected"/>).
    /// </summary>
    public static void ExhaustSelected(ref Exec x, Pile pile, scoped ReadOnlySpan<int> selected)
    {
        Span<SelectedCard> cards = stackalloc SelectedCard[selected.Length];
        SnapshotSelected(x.B, pile, selected, cards);
        for (int index = 0; index < cards.Length; index++)
        {
            Exhaust(ref x, pile, LocateSelected(x.B, pile, cards, index));
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors the multi-card <c>CombatPredictionSimulator.AddToPile</c> for cards already in a combat
    /// pile (<c>MoveToHand</c>, <c>MoveToDrawTop</c>): each card moves in selection order, with no hook
    /// in between; a Top insertion leaves the last selected card on top.
    /// </summary>
    public static void MoveSelected(ref Battle battle, Pile from, scoped ReadOnlySpan<int> selected, Pile to,
        PilePosition position)
    {
        if (selected.IsEmpty || IsEnding(battle) || battle.Player.IsDead)
            return;
        Span<SelectedCard> cards = stackalloc SelectedCard[selected.Length];
        SnapshotSelected(battle, from, selected, cards);
        for (int index = 0; index < cards.Length; index++)
            MoveCard(ref battle, from, LocateSelected(battle, from, cards, index), to, position);
    }

    /// <summary>
    /// A generated-card choice (<c>CardChoiceSupport.GetSpec</c>'s <c>PlanChoiceEffect.GenerateToHand</c>
    /// branch): the options are card values that are not in any pile, so they travel by value in the
    /// request. The existing engine asks even when the option list is empty (<c>RangeSpec</c>).
    /// </summary>
    public static bool ChooseGeneratedCardsInRange(ref Exec x, ChoiceEffect effect,
        scoped ReadOnlySpan<Card> options, int minCount, int maxCount,
        scoped Span<int> selected, out int selectedCount)
    {
        if (options.Length > Limits.MaxGeneratedChoiceOptions)
            throw new CapacityException("Choice.GeneratedOptions", Limits.MaxGeneratedChoiceOptions);
        ChoiceRequest request = new()
        {
            Kind = ChoiceKind.SelectCards,
            Effect = effect,
            Timing = x.ChoiceTiming,
            SourceIsNone = true,
            OptionsAreGenerated = true,
            OptionCount = checked((ushort)options.Length),
            MinCount = (byte)Math.Min(minCount, options.Length),
            MaxCount = (byte)Math.Min(maxCount, options.Length),
            Origin = x.ChoiceOrigin,
        };
        for (int index = 0; index < options.Length; index++)
            request.GeneratedOptions[index] = options[index];
        selectedCount = 0;
        if (!x.TryTakeChoice(request, out ulong selection))
            return false;
        for (int index = 0; index < options.Length; index++)
        {
            if ((selection & (1UL << index)) != 0)
                selected[selectedCount++] = index;
        }
        return true;
    }

    /// <summary>
    /// Mirrors <c>PredictedCardExtensions.Upgrade</c> → <c>PredictionUtils.UpgradeCard</c>: no change when
    /// the card is at its maximum level; otherwise one level up. The per-level rule stats give the
    /// native <c>OnUpgrade</c> deltas: <c>EnergyCost.UpgradeBy</c> (base floored at zero, absolute local
    /// modifiers capped at a lowered base), the star cost and keyword changes. Dynamic-variable deltas
    /// relative to the rule base carry over unchanged.
    /// </summary>
    public static void UpgradeCard(Rules rules, ref Card card)
    {
        CardStats before = rules.Card(card.Id, card.Upgrade);
        if (card.Upgrade >= before.MaxUpgradeLevel)
            return;
        CardStats after = rules.Card(card.Id, card.Upgrade + 1);
        if (before.CostsX != after.CostsX || before.StarCostsX != after.StarCostsX)
            throw new UnsupportedException("升级改变 X 费用的卡尚未支持。");
        card.Upgrade++;
        int addend = after.EnergyCost - before.EnergyCost;
        if (!before.CostsX && addend != 0)
        {
            int previous = card.BaseCost;
            int lowered = Math.Max(previous + addend, 0);
            if (lowered < previous)
            {
                for (int index = 0; index < card.EnergyModifierCount; index++)
                {
                    ref CostModifier modifier = ref card.EnergyModifiers[index];
                    if (modifier.Type == CostType.Absolute && modifier.Amount > lowered)
                        modifier.Amount = (short)lowered;
                }
            }
            card.BaseCost = checked((sbyte)lowered);
        }
        int starAddend = after.StarCost - before.StarCost;
        if (!before.StarCostsX && starAddend != 0)
            card.BaseStarCost = checked((sbyte)Math.Max(card.BaseStarCost + starAddend, 0));
        Keywords added = after.Keywords & ~before.Keywords;
        Keywords removed = before.Keywords & ~after.Keywords;
        card.Keywords = (card.Keywords | added) & ~removed;
    }

    /// <summary>
    /// Mirrors <c>CardCmd.Downgrade</c> → <c>CardModel.DowngradeInternal</c>: the original upgrade
    /// level is recorded for DampenPower's restore, then the card returns to its level-0 dynamic
    /// variables, base energy/star costs and canonical local keywords. Local cost modifiers,
    /// replay count, card flags, enchantment and affliction stay as they are.
    /// </summary>
    public static void DowngradeCard(ref Exec x, int position)
    {
        if (IsOverOrEnding(x.B))
            return;
        if ((uint)position >= (uint)x.B.CardCount)
            throw new ArgumentOutOfRangeException(nameof(position), position, "卡牌位置越界。");
        ref Card card = ref x.B.Cards[position];
        if (card.Upgrade == 0)
            return;
        card.DampenOriginalUpgrade = checked((byte)(card.Upgrade + 1));
        CardStats stats = x.R.Card(card.Id, 0);
        short previousVar0 = card.Var0;
        card.Upgrade = 0;
        card.Var0 = 0;
        card.Var1 = 0;
        card.BaseCost = checked((sbyte)stats.EnergyCost);
        card.BaseStarCost = checked((sbyte)stats.StarCost);
        card.Keywords = stats.Keywords | (card.Keywords & Keywords.CreatedAfterRoot);
        CardDef def = Registry.Card(card.Id);
        def.AfterDowngraded(ref x.B, position);
        if (def.PreservesVar0OnDowngrade)
        {
            card.Var0 = previousVar0;
        }
    }

    /// <summary>
    /// <c>SimulatedCombatState.RestoreDampenedCards</c>: every card DampenPower downgraded is upgraded
    /// back to its recorded level, in storage order.
    /// </summary>
    public static void RestoreDampenedCards(ref Exec x)
    {
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref Card card = ref x.B.Cards[position];
            if (card.DampenOriginalUpgrade == 0)
                continue;
            int level = card.DampenOriginalUpgrade - 1;
            card.DampenOriginalUpgrade = 0;
            while (card.Upgrade < level)
                UpgradeCard(x.R, ref card);
        }
    }

    /// <summary>Upgrades the selected cards in selection order (no hook runs in between).</summary>
    public static void UpgradeSelected(ref Exec x, Pile pile, scoped ReadOnlySpan<int> selected)
    {
        int start = Piles.Start(x.B, pile);
        foreach (int index in selected)
            UpgradeCard(x.R, ref x.B.Cards[start + index]);
    }
}
