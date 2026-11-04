namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// The card-effect discard pipeline, mirroring <c>CombatPredictionSimulator.Discard</c> /
/// <c>DiscardAndDraw</c> (<c>CardCmd.Discard</c>). The turn-end hand flush is separate: the existing
/// engine moves those cards with <c>AddToPile</c> directly, so no discard hook or Sly auto-play runs
/// there (<c>CorePowerSupport.FlushPlayerHandAtTurnEnd</c>).
/// </summary>
public static partial class Kernel
{
    /// <summary>Native <c>CardModel.IsSlyThisTurn</c>: the Sly keyword or the single-turn flag.</summary>
    public static bool IsSlyThisTurn(in Battle battle, in Card card)
        => HasKeyword(battle, card, Keywords.Sly) || (card.Flags & CardFlags.SlyThisTurn) != 0;

    /// <summary>Mirrors <c>CombatPredictionSimulator.Discard(PredictedCard)</c>: one card, its hook, then Sly auto-play.</summary>
    public static void Discard(ref Exec x, Pile pile, int index)
    {
        if (IsOverOrEnding(x.B))
            return;
        Card card = Piles.At(ref x.B, pile, index);
        int occurrence = CountEqualInPile(x.B, Pile.Discard, card);
        bool moved = DiscardCore(ref x, pile, index, out _, out bool sly);
        if (x.NeedsChoice || !moved || !sly)
            return;
        int position = LocateOccurrence(x.B, Pile.Discard, card, occurrence);
        AutoPlay(ref x, Pile.Discard, position);
    }

    /// <summary>
    /// The shared single-card discard step: move to the bottom of the discard pile, count it, and
    /// dispatch <c>AfterCardDiscarded</c>. Returns whether the move was accepted; the existing
    /// engine still records and dispatches after a refused move, which the callers reproduce.
    /// </summary>
    private static bool DiscardCore(ref Exec x, Pile pile, int index, out Card card, out bool sly)
    {
        card = Piles.At(ref x.B, pile, index);
        sly = IsSlyThisTurn(x.B, card);
        bool moved = MoveCard(ref x.B, pile, index, Pile.Discard);
        x.B.AddCounter(Counter.CardsDiscardedThisTurn, 1);
        AfterCardDiscardedHook hook = new() { Card = card };
        HookDispatch.Dispatch(ref x, ref hook, gatedByEnding: true);
        return moved;
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.DiscardAndDraw</c> / <c>ContinueDiscardAndDrawExecution</c>:
    /// discard every selected card in selection order (counter and <c>AfterCardDiscarded</c> after each),
    /// then draw, then auto-play the cards that were Sly at discard time in that same order.
    /// <paramref name="selected"/> holds indexes into <paramref name="pile"/>, in the existing engine's
    /// option order.
    /// </summary>
    /// <remarks>
    /// The auto-play happens after the draw, so a shuffle may have moved the discarded card into the
    /// draw pile. Sts2Solver has no card object identity, so each Sly card is located by a transient
    /// identity byte that travels with its value. <see cref="DiscardDrawn"/> uses the same mechanism
    /// for cards that were drawn before they were discarded.
    /// </remarks>
    public static void DiscardAndDraw(ref Exec x, Pile pile, scoped ReadOnlySpan<int> selected, int drawCount)
    {
        if (IsOverOrEnding(x.B) || selected.IsEmpty && drawCount == 0)
            return;
        Span<SelectedCard> cards = stackalloc SelectedCard[selected.Length];
        SnapshotSelected(x.B, pile, selected, cards);
        Span<SelectedCard> sly = stackalloc SelectedCard[selected.Length];
        int slyCount = 0;
        // The existing engine keeps the discarded CardModel and auto-plays it from whichever pile the
        // draw moved it to. Two byte-equal copies are not interchangeable: playing a copy that is still
        // in hand (or that stayed in the discard pile) leaves a different pile distribution, so each
        // discarded Sly card gets a one-action identity byte that travels with its value through the
        // shuffle and draw. Marks 64..255 are reserved for these batches; BeatDown uses 1..63.
        int markBase = ReserveTransientMarks(ref x, selected.Length, out int markStart);
        // A nested discard (Acrobatics discarding a card that the enclosing Calculated Gamble
        // already marked) reuses the same card value. Each batch needs its own identity byte, so
        // remember the outer mark and put it back before the inner auto-play moves the card on.
        Span<byte> outerMarks = stackalloc byte[selected.Length];
        try
        {
            for (int item = 0; item < cards.Length; item++)
            {
                int index = LocateSelected(x.B, pile, cards, item);
                // AddToPile(card, Discard) appends at the bottom; its result is ignored by the existing
                // loop, which still records the discard and dispatches the hook.
                DiscardCore(ref x, pile, index, out Card card, out bool isSly);
                if (x.NeedsChoice)
                    return;
                if (!isSly)
                    continue;
                // AddToPile appended the card at the bottom of the discard pile. A hook may have
                // removed or moved it; if the same copy is still there, its mark travels with it
                // through the draw. Otherwise no mark exists and the auto-play reports unsupported.
                byte mark = checked((byte)(markStart + slyCount));
                sly[slyCount++] = new SelectedCard { Card = card, Position = mark };
                if (x.B.DiscardCount > 0
                    && Fingerprint.Equal(Piles.At(ref x.B, Pile.Discard, x.B.DiscardCount - 1), card))
                {
                    ref Card discarded = ref Piles.At(ref x.B, Pile.Discard, x.B.DiscardCount - 1);
                    outerMarks[slyCount - 1] = discarded.TransientMark;
                    discarded.TransientMark = mark;
                }
            }
            Draw(ref x, drawCount, fromHandDraw: false);
            if (x.NeedsChoice)
                return;
            for (int item = 0; item < slyCount; item++)
            {
                if (IsOverOrEnding(x.B) || x.B.Player.IsDead)
                    return;
                if (!TryFindUniqueTransientMark(x.B, (byte)sly[item].Position, out Pile currentPile, out int currentIndex))
                {
                    // A hook cleared the identity byte or removed the card from every combat pile;
                    // the object identity the existing engine holds cannot be reproduced.
                    throw new UnsupportedException(
                        $"弃牌 {Registry.Card(sly[item].Card.Id).NativeId}+{sly[item].Card.Upgrade} " +
                        $"在自动打出前失去瞬态标记（cards={x.B.CardCount}）。");
                }
                x.B.Cards[Piles.Start(x.B, currentPile) + currentIndex].TransientMark = outerMarks[item];
                AutoPlay(ref x, currentPile, currentIndex);
                if (x.NeedsChoice)
                    return;
            }
        }
        finally
        {
            ReleaseTransientMarks(ref x, markBase, markStart, slyCount);
        }
    }

    /// <summary>
    /// Mirrors Scrape's <c>CardCmd.Discard(choiceContext, cards)</c>: each drawn occurrence carries the
    /// identity byte <c>markOf[i]</c> from before its <c>AfterCardDrawn</c> hooks. The selected
    /// occurrences are discarded in draw order by that identity from whichever pile they now occupy;
    /// the Sly occurrences are auto-played after every discard. Equal copies elsewhere are never
    /// substituted, and a card drawn more than once in this batch (the same value re-entering the draw
    /// pile after an auto-play) is discarded once per occurrence, as the existing engine does with the
    /// same <c>CardModel</c> reference. Outer marks are restored by the caller's <c>finally</c>.
    /// </summary>
    public static void DiscardDrawn(ref Exec x, scoped ReadOnlySpan<Card> drawn,
        scoped ReadOnlySpan<byte> outerMarks, scoped ReadOnlySpan<byte> markOf, int markStart,
        scoped ReadOnlySpan<int> selected)
    {
        if (IsOverOrEnding(x.B) || selected.IsEmpty)
            return;
        if (drawn.Length != outerMarks.Length || drawn.Length != markOf.Length)
            throw new ArgumentException("抽牌记录、外层标记和身份标记的容量必须一致。");
        if (drawn.Length > DynamicTransientMarkCapacity)
            throw new CapacityException("TransientMarks", DynamicTransientMarkCapacity);
        Span<bool> isSelected = stackalloc bool[drawn.Length];
        Span<bool> selectedMark = stackalloc bool[drawn.Length];
        foreach (int item in selected)
        {
            if ((uint)item >= (uint)drawn.Length)
                throw new ArgumentOutOfRangeException(nameof(selected), item, "抽到的牌下标越界。");
            isSelected[item] = true;
            int offset = markOf[item] - markStart;
            if ((uint)offset >= (uint)drawn.Length)
                throw new InvalidOperationException($"抽牌身份标记 {markOf[item]} 不在本批区间内。");
            selectedMark[offset] = true;
        }
        // Restore an unselected occurrence only when no selected occurrence shares its identity: the
        // same physical card can be drawn several times in one batch.
        for (int item = 0; item < drawn.Length; item++)
        {
            int offset = markOf[item] - markStart;
            if (!isSelected[item] && !selectedMark[offset])
                RestoreTransientMark(ref x.B, markOf[item], outerMarks[item]);
        }

        Span<byte> slyMarks = stackalloc byte[selected.Length];
        int slyCount = 0;
        foreach (int drawnIndex in selected)
        {
            if (IsOverOrEnding(x.B))
                return;
            byte mark = markOf[drawnIndex];
            if (!TryFindUniqueTransientMark(x.B, mark, out Pile pile, out int index))
                throw LostDrawnMark(mark, x.B.CardCount);
            bool moved = DiscardCore(ref x, pile, index, out _, out bool isSly);
            if (x.NeedsChoice)
                return;
            if (!moved)
                return;
            if (isSly)
                slyMarks[slyCount++] = mark;
        }
        for (int item = 0; item < slyCount; item++)
        {
            if (IsOverOrEnding(x.B) || x.B.Player.IsDead)
                return;
            byte mark = slyMarks[item];
            if (!TryFindUniqueTransientMark(x.B, mark, out Pile pile, out int index))
                throw LostDrawnMark(mark, x.B.CardCount);
            AutoPlay(ref x, pile, index);
            if (x.NeedsChoice)
                return;
        }
    }

    private static UnsupportedException LostDrawnMark(byte mark, int cardCount)
        => new($"抓牌后被抽牌钩子移动的牌失去瞬态标记（mark={mark}, cards={cardCount}）。");

    /// <summary>Discards every card in the hand, then draws <paramref name="drawCount"/> (CalculatedGamble, ShadowStep).</summary>
    public static void DiscardAllInHand(ref Exec x, int drawCount)
    {
        Span<int> selected = stackalloc int[x.B.HandCount];
        for (int index = 0; index < selected.Length; index++)
            selected[index] = index;
        DiscardAndDraw(ref x, Pile.Hand, selected, drawCount);
    }

    /// <summary>Number of cards byte-equal to <paramref name="card"/> currently in <paramref name="pile"/>.</summary>
    private static int CountEqualInPile(in Battle battle, Pile pile, in Card card)
    {
        ReadOnlySpan<Card> cards = Piles.ReadCards(battle, pile);
        int count = 0;
        foreach (Card candidate in cards)
        {
            if (Fingerprint.Equal(candidate, card))
                count++;
        }
        return count;
    }

    /// <summary>Number of cards byte-equal to <paramref name="card"/> before absolute position <paramref name="position"/>.</summary>
    private static int CountEqualBefore(in Battle battle, int position, in Card card)
    {
        int count = 0;
        for (int index = 0; index < position; index++)
        {
            if (Fingerprint.Equal(battle.Cards[index], card))
                count++;
        }
        return count;
    }

    /// <summary>Finds the <paramref name="occurrence"/>-th byte-equal card over the whole battle and its pile.</summary>
    private static bool TryLocateAnywhere(in Battle battle, in Card card, int occurrence,
        out Pile pile, out int index)
    {
        int found = -1;
        for (int position = 0; position < battle.CardCount; position++)
        {
            if (!Fingerprint.Equal(battle.Cards[position], card))
                continue;
            if (occurrence-- == 0)
            {
                found = position;
                break;
            }
        }
        if (found < 0)
        {
            pile = Pile.Hand;
            index = -1;
            return false;
        }
        ReadOnlySpan<Pile> piles = [Pile.Hand, Pile.Draw, Pile.Discard, Pile.Exhaust, Pile.Play];
        foreach (Pile candidate in piles)
        {
            int start = Piles.Start(battle, candidate);
            int count = Piles.Count(battle, candidate);
            if (found < start || found >= start + count)
                continue;
            pile = candidate;
            index = found - start;
            return true;
        }
        pile = Pile.Hand;
        index = -1;
        return false;
    }

    /// <summary>
    /// Pile index of the <paramref name="occurrence"/>-th (0-based) card byte-equal to
    /// <paramref name="card"/>. Returns -1 when that card is no longer in the pile.
    /// </summary>
    private static int LocateOccurrence(in Battle battle, Pile pile, in Card card, int occurrence)
    {
        ReadOnlySpan<Card> cards = Piles.ReadCards(battle, pile);
        for (int index = 0; index < cards.Length; index++)
        {
            if (!Fingerprint.Equal(cards[index], card))
                continue;
            if (occurrence-- == 0)
                return index;
        }
        throw new UnsupportedException("弃牌在结算前已离开弃牌堆。");
    }
}
