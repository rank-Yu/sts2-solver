namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>First byte of the dynamic 64..255 transient identity range.</summary>
    internal const int DynamicTransientMarkStart = 64;

    /// <summary>Number of bytes in the dynamic 64..255 transient identity range.</summary>
    internal const int DynamicTransientMarkCapacity = 256 - DynamicTransientMarkStart;

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive identity bytes above every enclosing batch and
    /// returns the previous base. <paramref name="markStart"/> is the first byte of the new range.
    /// The caller must restore the base with <see cref="ReleaseTransientMarks"/> in <c>finally</c>.
    /// </summary>
    internal static int ReserveTransientMarks(ref Exec x, int count, out int markStart)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "瞬态标记数量不能为负。");
        int markBase = x.TransientMarkBase;
        if (markBase + count > DynamicTransientMarkCapacity)
            throw new CapacityException("TransientMarks", DynamicTransientMarkCapacity);
        x.TransientMarkBase = checked((byte)(markBase + count));
        markStart = DynamicTransientMarkStart + markBase;
        return markBase;
    }

    /// <summary>
    /// Clears the identity bytes in the reserved range from both live combat cards and detached
    /// cards, then restores the enclosing batch's base.
    /// </summary>
    internal static void ReleaseTransientMarks(ref Exec x, int markBase, int markStart, int count)
    {
        ClearTransientMarks(ref x.B, markStart, count);
        x.TransientMarkBase = checked((byte)markBase);
    }

    /// <summary>Clears <c>[markStart, markStart + count)</c> from every live and detached card value.</summary>
    internal static void ClearTransientMarks(ref Battle battle, int markStart, int count)
    {
        for (int position = 0; position < battle.CardCount; position++)
        {
            if (battle.Cards[position].TransientMark >= markStart
                && battle.Cards[position].TransientMark < markStart + count)
            {
                battle.Cards[position].TransientMark = 0;
            }
        }
        for (int slot = 0; slot < Limits.DetachedCards; slot++)
        {
            if (battle.DetachedCards[slot].TransientMark >= markStart
                && battle.DetachedCards[slot].TransientMark < markStart + count)
            {
                battle.DetachedCards[slot].TransientMark = 0;
            }
        }
    }

    /// <summary>
    /// Stamps each selected card of one pile with a distinct identity byte in
    /// <paramref name="marks"/> (starting at <paramref name="markStart"/>), remembering the outer
    /// byte in <paramref name="outerMarks"/>. Selected indexes must be distinct and ascending is not
    /// required; duplicates by value remain distinct occurrences.
    /// </summary>
    internal static void MarkPileCards(ref Battle battle, Pile pile, scoped ReadOnlySpan<int> selected,
        scoped Span<byte> marks, scoped Span<byte> outerMarks, int markStart)
    {
        if (selected.Length != marks.Length || selected.Length != outerMarks.Length)
            throw new ArgumentException("选择、身份标记和外层标记的容量必须一致。");
        int start = Piles.Start(battle, pile);
        int count = Piles.Count(battle, pile);
        for (int item = 0; item < selected.Length; item++)
        {
            if ((uint)selected[item] >= (uint)count)
                throw new UnsupportedException("身份标记选中的卡不在原牌堆。");
            ref Card card = ref battle.Cards[start + selected[item]];
            marks[item] = checked((byte)(markStart + item));
            outerMarks[item] = card.TransientMark;
            card.TransientMark = marks[item];
        }
    }

    /// <summary>
    /// Puts the enclosing batch's identity byte back on every marked card that is still tracked.
    /// Cards that left every combat pile have no value left to restore.
    /// </summary>
    internal static void RestoreMarkedCards(ref Battle battle, scoped ReadOnlySpan<byte> marks,
        scoped ReadOnlySpan<byte> outerMarks)
    {
        if (marks.Length != outerMarks.Length)
            throw new ArgumentException("身份标记和外层标记的容量必须一致。");
        for (int item = 0; item < marks.Length; item++)
            RestoreTransientMark(ref battle, marks[item], outerMarks[item]);
    }

    /// <summary>
    /// Restores the enclosing batch's identity byte on the card carrying <paramref name="inner"/>.
    /// A missing inner mark means the card left every tracked pile; there is no value left to
    /// restore. Two cards carrying the inner mark are an explicit unsupported state.
    /// </summary>
    internal static void RestoreTransientMark(ref Battle battle, byte inner, byte outer)
    {
        if (inner == outer)
            return;
        bool found = false;
        for (int position = 0; position < battle.CardCount; position++)
        {
            if (battle.Cards[position].TransientMark != inner)
                continue;
            if (found)
                throw new UnsupportedException($"瞬态标记 {inner} 在战斗牌堆中出现重复。");
            battle.Cards[position].TransientMark = outer;
            found = true;
        }
        for (int slot = 0; slot < Limits.DetachedCards; slot++)
        {
            if (battle.DetachedCards[slot].TransientMark != inner)
                continue;
            if (found)
                throw new UnsupportedException($"瞬态标记 {inner} 在战斗牌堆和 detached 区中出现重复。");
            battle.DetachedCards[slot].TransientMark = outer;
            found = true;
        }
    }

    /// <summary>
    /// Restores the enclosing batch's mark for every drawn occurrence whose inner mark is still
    /// present. <paramref name="marks"/> and <paramref name="outerMarks"/> are the parallel arrays
    /// produced by <see cref="DrawIntoMarked"/>.
    /// </summary>
    internal static void RestoreDrawnTransientMarks(ref Battle battle,
        scoped ReadOnlySpan<byte> marks, scoped ReadOnlySpan<byte> outerMarks)
    {
        if (marks.Length != outerMarks.Length)
            throw new ArgumentException("抽牌身份标记与外层标记的容量必须一致。");
        for (int item = 0; item < marks.Length; item++)
            RestoreTransientMark(ref battle, marks[item], outerMarks[item]);
    }

    /// <summary>
    /// Finds the single combat-pile card carrying <paramref name="mark"/> and its current pile
    /// position. The transient mark is an identity byte, so two cards carrying the same mark is an
    /// explicit unsupported state rather than a choice of copy.
    /// </summary>
    internal static bool TryFindUniqueTransientMark(in Battle battle, byte mark,
        out Pile pile, out int index)
    {
        if (mark == 0)
            throw new InvalidOperationException("瞬态标记 0 不是有效身份。");
        bool found = false;
        pile = Pile.Hand;
        index = -1;
        for (int absolute = 0; absolute < battle.CardCount; absolute++)
        {
            if (battle.Cards[absolute].TransientMark != mark)
                continue;
            if (found)
                throw new UnsupportedException($"瞬态标记 {mark} 在战斗牌堆中出现重复。");
            pile = Piles.PileOf(battle, absolute, out index);
            found = true;
        }
        return found;
    }
}
