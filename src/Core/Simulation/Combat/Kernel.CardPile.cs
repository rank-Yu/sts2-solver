namespace Sts2Solver.Simulation.Combat;

/// <summary>Native <c>CardPilePosition</c>.</summary>
public enum PilePosition : byte
{
    Bottom = 0,
    Top = 1,
    Random = 2,
}

public static partial class Kernel
{
    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.AddToPile</c> for a card already in a combat pile.
    /// Returns false when the move is refused (combat ending or owner dead).
    /// </summary>
    public static bool MoveCard(ref Battle battle, Pile from, int index, Pile to,
        PilePosition position = PilePosition.Bottom)
        => MoveCard(ref battle, from, index, to, position, out _, out _);

    /// <summary>
    /// The same move, also reporting the final pile (a full hand diverts to the discard pile) and the
    /// inserted pile-local index. <paramref name="toIndex"/> is -1 when the move is refused.
    /// </summary>
    public static bool MoveCard(ref Battle battle, Pile from, int index, Pile to,
        PilePosition position, out Pile toPile, out int toIndex)
    {
        toPile = to;
        toIndex = -1;
        if (IsEnding(battle) || battle.Player.IsDead)
            return false;
        int sourceCount = Piles.Count(battle, from);
        if ((uint)index >= (uint)sourceCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"{from} 牌堆没有这个位置。");
        Pile target = to == Pile.Hand && battle.HandCount >= battle.MaxHandSize ? Pile.Discard : to;
        // The target count and the Random insertion index are read after the source removal, as the
        // RemoveAt/Insert pair did. The move itself is a single rotation.
        int count = Piles.Count(battle, target) - (target == from ? 1 : 0);
        int insert = position switch
        {
            PilePosition.Bottom => count,
            PilePosition.Top => 0,
            PilePosition.Random => battle.RngStream(RngStream.Shuffle).NextInt(count + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
        };
        Piles.Move(ref battle, from, index, target, insert);
        toPile = target;
        toIndex = insert;
        return true;
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.Draw</c> / <c>DrawCore</c>.</summary>
    public static void Draw(ref Exec x, int count, bool fromHandDraw)
    {
        if (IsOverOrEnding(x.B) || !ShouldDraw(ref x, fromHandDraw))
            return;
        for (int index = 0; index < count; index++)
        {
            if (!DrawOneCore(ref x, fromHandDraw, out _) || x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.Draw</c> while recording each drawn card value in order and
    /// stamping it with a caller-reserved transient identity byte before <c>AfterCardDrawn</c> runs.
    /// <c>ShouldDraw</c> is asked once for the whole draw. Each occurrence gets
    /// <c>markOf[i]</c>; a card that this same batch already marked and that is drawn again (a draw
    /// hook put it back into the draw pile) keeps its existing inner mark, because the existing engine
    /// returns the same <c>CardModel</c> object for every occurrence. <c>outerMarks[i]</c> is the
    /// enclosing batch's mark to restore in <c>finally</c>. Returns the number of occurrences written.
    /// </summary>
    public static int DrawIntoMarked(ref Exec x, int count, bool fromHandDraw, scoped Span<Card> drawn,
        scoped Span<byte> outerMarks, scoped Span<byte> markOf, int markStart)
    {
        if (drawn.Length != outerMarks.Length || drawn.Length != markOf.Length)
            throw new ArgumentException("抽牌记录、外层标记和身份标记的容量必须一致。");
        if (IsOverOrEnding(x.B) || !ShouldDraw(ref x, fromHandDraw))
            return 0;
        Span<byte> outerByMark = stackalloc byte[drawn.Length];
        int written = 0;
        for (int index = 0; index < count; index++)
        {
            if (written >= drawn.Length)
                throw new CapacityException("DrawnCards", drawn.Length);
            if (!TryDrawToHand(ref x, out Card card, out int position))
                return written;
            int currentMark = x.B.Cards[position].TransientMark;
            byte inner;
            byte outer;
            if (currentMark >= markStart && currentMark < markStart + drawn.Length)
            {
                int offset = currentMark - markStart;
                inner = checked((byte)currentMark);
                outer = outerByMark[offset];
            }
            else
            {
                if (markStart < DynamicTransientMarkStart
                    || markStart + written >= DynamicTransientMarkStart + DynamicTransientMarkCapacity)
                {
                    throw new CapacityException("TransientMarks", DynamicTransientMarkCapacity);
                }
                inner = checked((byte)(markStart + written));
                outer = checked((byte)currentMark);
                outerByMark[written] = outer;
            }
            x.B.Cards[position].TransientMark = inner;
            drawn[written] = card;
            outerMarks[written] = outer;
            markOf[written] = inner;
            written++;
            bool completed = CompleteDraw(ref x, card, position, fromHandDraw);
            if (!completed || x.NeedsChoice)
                return written;
        }
        return written;
    }

    /// <summary>
    /// One <c>Draw(..., 1, ...)</c> call that also reports the card value at draw time. Pillage uses the
    /// drawn card's type even when the draw hook auto-played and removed that card (Hellraiser), so a
    /// hand-position check is not enough.
    /// </summary>
    public static bool TryDrawOne(ref Exec x, bool fromHandDraw, out Card card)
    {
        card = default;
        if (IsOverOrEnding(x.B) || !ShouldDraw(ref x, fromHandDraw))
            return false;
        return DrawOneCore(ref x, fromHandDraw, out card) && !x.NeedsChoice;
    }

    /// <summary>Draws one unmarked card and runs its after-draw hooks.</summary>
    private static bool DrawOneCore(ref Exec x, bool fromHandDraw, out Card card)
    {
        card = default;
        if (!TryDrawToHand(ref x, out card, out int position))
            return false;
        return CompleteDraw(ref x, card, position, fromHandDraw);
    }

    /// <summary>
    /// Moves the top draw-pile card into the hand and returns its pre-hook value. No draw history or
    /// hook has run yet, so a caller can stamp an identity byte first.
    /// </summary>
    private static bool TryDrawToHand(ref Exec x, out Card card, out int position)
    {
        card = default;
        position = -1;
        if (IsOverOrEnding(x.B) || x.B.HandCount >= x.B.MaxHandSize)
            return false;
        ShuffleIfNecessary(ref x);
        if (x.NeedsChoice)
            return false;
        if (x.B.DrawCount == 0 || x.B.HandCount >= x.B.MaxHandSize)
            return false;
        // AddToPile(top draw card, Hand, Bottom): the draw segment directly follows the hand.
        Piles.DrawTopToHand(ref x.B);
        position = x.B.HandCount - 1;
        card = x.B.Cards[position];
        return true;
    }

    /// <summary><c>RecordCardDrawn</c> and the three <c>AfterCardDrawn</c> passes for a card now in hand.</summary>
    private static bool CompleteDraw(ref Exec x, in Card card, int position, bool fromHandDraw)
    {
        RecordCardDrawn(ref x, card, fromHandDraw);
        AfterCardDrawn(ref x, position, fromHandDraw);
        return !x.NeedsChoice;
    }

    /// <summary>Native <c>Hook.ShouldDraw</c> through <c>HookMirrors.ShouldDraw</c>: any combat listener may prevent the draw.</summary>
    private static bool ShouldDraw(ref Exec x, bool fromHandDraw)
    {
        if ((Registry.AnyHooks & HookMask.ShouldDraw) == 0 || IsOverOrEnding(x.B))
            return true;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R)];
        int count = HookDispatch.CollectListeners(x.B, x.R, HookMask.ShouldDraw, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!HookDispatch.Definition(x.B, x.R, listeners[index]).ShouldDraw(ref x, listeners[index], fromHandDraw))
                return false;
        }
        return true;
    }

    /// <summary>Mirrors <c>SimulatedCombatState.RecordCardDrawn</c>.</summary>
    private static void RecordCardDrawn(ref Exec x, in Card card, bool fromHandDraw)
    {
        x.B.AddCounter(Counter.CardsDrawn, 1);
        if (!fromHandDraw)
            x.B.AddCounter(Counter.NonHandDrawsThisTurn, 1);
        if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Status)
            x.B.AddCounter(Counter.StatusCardsDrawnThisTurn, 1);
    }

    /// <summary>
    /// Mirrors <c>HookMirrors.AfterCardDrawn</c>: the Early and normal passes over the combat listeners (gated by
    /// combat ending), then the drawn card's own handler when its listener visit did not claim it.
    /// </summary>
    private static void AfterCardDrawn(ref Exec x, int position, bool fromHandDraw)
    {
        int savedPosition = x.DrawnCardPosition;
        bool savedHandled = x.IntrinsicDrawHandled;
        x.DrawnCardPosition = position;
        x.IntrinsicDrawHandled = false;
        AfterCardDrawnEarlyHook early = new() { DrawnPosition = position, FromHandDraw = fromHandDraw };
        HookDispatch.Dispatch(ref x, ref early, gatedByEnding: true);
        if (x.NeedsChoice)
            return;
        AfterCardDrawnHook normal = new() { DrawnPosition = position, FromHandDraw = fromHandDraw };
        HookDispatch.Dispatch(ref x, ref normal, gatedByEnding: true);
        if (x.NeedsChoice)
            return;
        if (!x.IntrinsicDrawHandled)
        {
            CardDef def = Registry.Card(x.B.Cards[position].Id);
            if ((def.Hooks & HookMask.AfterCardDrawn) != 0)
            {
                Listener self = new(ListenerKind.Card, (ushort)position, Limits.PlayerOwner);
                ChoiceSource savedOrigin = x.ChoiceOrigin;
                x.ChoiceOrigin = ListenerChoiceSource(x.B, x.R, self);
                try
                {
                    def.AfterCardDrawn(ref x, self, position, fromHandDraw);
                }
                finally
                {
                    x.ChoiceOrigin = savedOrigin;
                }
            }
            if (x.NeedsChoice)
                return;
        }
        x.DrawnCardPosition = savedPosition;
        x.IntrinsicDrawHandled = savedHandled;
    }

    /// <summary>
    /// For a card listener: whether this visit is the drawn card's own handler, claimed once per draw
    /// (<c>AfterCardDrawnMirrorContext.IntrinsicCardHandled</c> with <c>Card.References</c>).
    /// </summary>
    public static bool TakeIntrinsicDraw(ref Exec x, Listener self, int drawnPosition)
    {
        if (x.IntrinsicDrawHandled || self.Kind != ListenerKind.Card || self.Index != drawnPosition)
            return false;
        x.IntrinsicDrawHandled = true;
        return true;
    }

    /// <summary>Decimal overload: rounds up like <c>CombatPredictionSimulator.Draw(Player, decimal, bool)</c>.</summary>
    public static void Draw(ref Exec x, decimal count, bool fromHandDraw)
        => Draw(ref x, count > 0m ? (int)Math.Ceiling(count) : 0, fromHandDraw);

    internal static void ShuffleIfNecessary(ref Exec x)
    {
        if (x.B.DrawCount == 0 && x.B.DiscardCount != 0)
            Shuffle(ref x);
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.Shuffle</c>: discard (in order) followed by the draw pile,
    /// stable-shuffled with the Shuffle stream, all placed into the draw pile.
    /// </summary>
    public static void Shuffle(ref Exec x)
    {
        if (IsOverOrEnding(x.B))
            return;
        int drawCount = x.B.DrawCount;
        int discardCount = x.B.DiscardCount;
        int total = drawCount + discardCount;
        Span<Card> list = stackalloc Card[total];
        Piles.ReadCards(x.B, Pile.Discard).CopyTo(list);
        Piles.ReadCards(x.B, Pile.Draw).CopyTo(list[discardCount..]);

        // ListExtensions.StableShuffle: sort, then UnstableShuffle (Fisher-Yates from the end).
        CardSort.Sort(list, x.R, in x.B, Pile.Draw);
        RngState rng = x.B.RngStream(RngStream.Shuffle).CreateShuffleRng();
        for (int n = total; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
        // HookMirrors.ModifyShuffleOrder runs after the stable shuffle and before AddToPile
        // (PerfectFit moves its own card to the front of the merged list).
        ModifyShuffleOrder(ref x, list, total);

        // Draw and discard are adjacent segments: the region becomes the new draw pile.
        list.CopyTo(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref x.B.Cards[x.B.HandCount], total));
        x.B.DrawCount = checked((ushort)total);
        x.B.DiscardCount = 0;
        x.B.ShuffleEventCount++;
        // CombatPredictionSimulator.Shuffle calls HookMirrors.AfterShuffle only while the combat is
        // not over or ending. The hook then iterates the combat listeners without its own gate.
        if (IsOverOrEnding(x.B))
            return;
        AfterShuffleHook after = new() { Shuffler = Limits.PlayerOwner };
        HookDispatch.Dispatch(ref x, ref after, gatedByEnding: false);
    }

    /// <summary>
    /// <c>HookMirrors.ModifyShuffleOrder</c> over the combat listeners in dispatch order. The existing
    /// engine always passes <c>isInitialShuffle: false</c> from <c>CombatPredictionSimulator.Shuffle</c>
    /// (the only shuffle call site), so Sts2Solver passes false as well.
    /// </summary>
    private static void ModifyShuffleOrder(ref Exec x, scoped Span<Card> cards, int count)
    {
        if (!x.R.HasShuffleOrderEnchantment
            || (Registry.AnyHooksExt & HookMaskExt.ModifyShuffleOrder) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R)];
        int listenersCount = HookDispatch.CollectListeners(x.B, x.R, HookMaskExt.ModifyShuffleOrder, listeners);
        for (int index = 0; index < listenersCount; index++)
            HookDispatch.Definition(x.B, x.R, listeners[index]).ModifyShuffleOrder(
                ref x, listeners[index], cards, count, isInitialShuffle: false);
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.Exhaust</c>.</summary>
    public static void Exhaust(ref Exec x, Pile from, int index, bool causedByEthereal = false)
    {
        if (IsOverOrEnding(x.B))
            return;
        Card card = Piles.At(ref x.B, from, index);
        MoveCard(ref x.B, from, index, Pile.Exhaust);
        x.B.AddCounter(Counter.CardsExhaustedThisTurn, 1);
        AfterCardExhaustedHook after = new() { Card = card, CausedByEthereal = causedByEthereal };
        HookDispatch.Dispatch(ref x, ref after, gatedByEnding: true);
    }
}
