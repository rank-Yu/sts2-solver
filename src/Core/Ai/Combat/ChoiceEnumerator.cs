using System.Numerics;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Enumerates the selections a <see cref="ChoiceRequest"/> allows. <see cref="Enumerate"/> is the
/// original option-index enumeration (sizes from MinCount to MaxCount, lexicographic within a size).
/// <see cref="EnumerateByClass"/> merges content-equal options into classes and enumerates one
/// multiset of class counts per selection, using the lowest option index of each class as the
/// representative. The cap therefore spends every slot on a different content selection.
/// </summary>
internal static class ChoiceEnumerator
{
    public static int Enumerate(in ChoiceRequest request, Span<ulong> output)
    {
        if (request.UsesIndexSelection)
        {
            int selectedCount = Math.Min((int)request.OptionCount, output.Length);
            for (int index = 0; index < selectedCount; index++)
                output[index] = (ulong)index;
            return selectedCount;
        }
        int options = request.OptionCount;
        int count = 0;
        Span<int> indexes = stackalloc int[Limits.MaxChoiceOptions];
        for (int size = request.MinCount; size <= request.MaxCount && count < output.Length; size++)
        {
            for (int index = 0; index < size; index++)
                indexes[index] = index;
            while (count < output.Length)
            {
                ulong bits = 0;
                for (int index = 0; index < size; index++)
                    bits |= 1UL << indexes[index];
                output[count++] = bits;
                int position = size - 1;
                while (position >= 0 && indexes[position] == options - size + position)
                    position--;
                if (position < 0)
                    break;
                indexes[position]++;
                for (int index = position + 1; index < size; index++)
                    indexes[index] = indexes[index - 1] + 1;
            }
        }
        return count;
    }

    /// <summary>
    /// Enumerates one representative per content multiset: for each size, class-count vectors in
    /// descending count order of the earliest class, each mapped to the lowest option indexes of the
    /// classes. Options whose cards are content-equal (see <see cref="CardEquivalence"/>) form one
    /// class. A request that must select every option yields the single all-options selection.
    /// </summary>
    public static int EnumerateByClass(in ChoiceRequest request, scoped ReadOnlySpan<Card> cards,
        Span<ulong> output)
    {
        if (request.UsesIndexSelection)
            return Enumerate(in request, output);
        if (output.IsEmpty)
            return 0;
        int options = request.OptionCount;
        if (cards.Length < options)
            throw new ArgumentException("选项卡牌数少于选择请求的选项数。", nameof(cards));
        if (options == 0)
        {
            if (request.MinCount > 0)
                return 0;
            output[0] = 0;
            return 1;
        }
        int minTake = Math.Min((int)request.MinCount, options);
        int maxTake = Math.Min((int)request.MaxCount, options);
        if (minTake > maxTake)
            return 0;
        if (minTake == options && maxTake == options)
        {
            output[0] = options == 64 ? ulong.MaxValue : (1UL << options) - 1;
            return 1;
        }

        Span<int> classOf = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> rankInClass = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classSize = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classFirst = stackalloc int[Limits.MaxChoiceOptions];
        int classCount = BuildClasses(cards, classOf, rankInClass, classSize, classFirst);
        Span<int> counts = stackalloc int[Limits.MaxChoiceOptions];
        counts.Clear();
        int written = 0;
        for (int size = minTake; size <= maxTake && written < output.Length; size++)
        {
            EnumerateCounts(0, size, classSize[..classCount], counts, classOf, rankInClass,
                options, output, ref written);
        }
        return written;
    }

    /// <summary>
    /// Class-first enumeration with position fill: first emits all distinct class-vector
    /// representatives in <see cref="EnumerateByClass"/> order, then continues in the original
    /// lexicographic option-index order and appends every physical selection not already emitted,
    /// until the output is full or every selection has been seen. Callers should use the original
    /// <see cref="Enumerate"/> when the request has at most <c>output.Length</c> selections so that
    /// the untruncated order is exactly the pre-M3b order.
    /// </summary>
    public static int EnumerateByClassFilled(in ChoiceRequest request, scoped ReadOnlySpan<Card> cards,
        Span<ulong> output)
    {
        if (request.UsesIndexSelection)
            return Enumerate(in request, output);
        int written = EnumerateByClass(request, cards, output);
        if (written >= output.Length)
            return written;
        int options = request.OptionCount;
        if (options == 0)
            return written;
        int minTake = Math.Min((int)request.MinCount, options);
        int maxTake = Math.Min((int)request.MaxCount, options);
        Span<int> indexes = stackalloc int[Limits.MaxChoiceOptions];
        for (int size = minTake; size <= maxTake && written < output.Length; size++)
        {
            for (int index = 0; index < size; index++)
                indexes[index] = index;
            while (written < output.Length)
            {
                ulong bits = 0;
                for (int index = 0; index < size; index++)
                    bits |= 1UL << indexes[index];
                if (!Contains(output[..written], bits))
                    output[written++] = bits;
                int position = size - 1;
                while (position >= 0 && indexes[position] == options - size + position)
                    position--;
                if (position < 0)
                    break;
                indexes[position]++;
                for (int index = position + 1; index < size; index++)
                    indexes[index] = indexes[index - 1] + 1;
            }
        }
        return written;
    }

    /// <summary>
    /// Removal order for <see cref="RemovalChoicePriority"/>: content classes sorted by ascending
    /// <paramref name="priority"/> (ties keep the first option index), then the cheapest prefix of
    /// every size from MinCount to MaxCount, then the remaining class vectors in
    /// <see cref="EnumerateByClass"/> order over the sorted classes. No physical position variants.
    /// </summary>
    public static int EnumerateRemoval(in ChoiceRequest request, scoped ReadOnlySpan<Card> cards,
        scoped ReadOnlySpan<int> priority, Span<ulong> output)
    {
        if (request.UsesIndexSelection)
            return Enumerate(in request, output);
        int options = request.OptionCount;
        if (cards.Length < options || priority.Length < options)
            throw new ArgumentException("选项卡牌数或优先级数少于选择请求的选项数。", nameof(cards));
        int minTake = Math.Min((int)request.MinCount, options);
        int maxTake = Math.Min((int)request.MaxCount, options);
        if (output.IsEmpty || options == 0 || minTake > maxTake)
            return EnumerateByClass(in request, cards, output);

        Span<int> classOf = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> rankInClass = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classSize = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classFirst = stackalloc int[Limits.MaxChoiceOptions];
        int classCount = BuildClasses(cards[..options], classOf, rankInClass, classSize, classFirst);
        // Stable insertion sort of the classes by the priority of their first option.
        Span<int> order = stackalloc int[classCount];
        for (int cls = 0; cls < classCount; cls++)
        {
            int at = cls;
            while (at > 0 && priority[classFirst[order[at - 1]]] > priority[classFirst[cls]])
            {
                order[at] = order[at - 1];
                at--;
            }
            order[at] = cls;
        }
        Span<int> sortedOf = stackalloc int[classCount];
        Span<int> sortedSize = stackalloc int[classCount];
        for (int position = 0; position < classCount; position++)
        {
            sortedOf[order[position]] = position;
            sortedSize[position] = classSize[order[position]];
        }
        for (int option = 0; option < options; option++)
            classOf[option] = sortedOf[classOf[option]];

        int written = 0;
        for (int size = minTake; size <= maxTake && written < output.Length; size++)
        {
            ulong prefix = 0;
            int left = size;
            for (int position = 0; position < classCount && left > 0; position++)
            {
                int take = Math.Min(sortedSize[position], left);
                for (int option = 0; option < options; option++)
                {
                    if (classOf[option] == position && rankInClass[option] < take)
                        prefix |= 1UL << option;
                }
                left -= take;
            }
            output[written++] = prefix;
        }
        int prefixes = written;
        Span<ulong> rest = stackalloc ulong[output.Length + prefixes];
        Span<int> counts = stackalloc int[Limits.MaxChoiceOptions];
        counts.Clear();
        int generated = 0;
        for (int size = minTake; size <= maxTake && generated < rest.Length; size++)
        {
            EnumerateCounts(0, size, sortedSize, counts, classOf, rankInClass, options, rest,
                ref generated);
        }
        for (int index = 0; index < generated && written < output.Length; index++)
        {
            if (!Contains(output[..prefixes], rest[index]))
                output[written++] = rest[index];
        }
        return written;
    }

    private static bool Contains(scoped ReadOnlySpan<ulong> selections, ulong mask)
    {
        for (int index = 0; index < selections.Length; index++)
        {
            if (selections[index] == mask)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when every content class in <paramref name="selection"/> selects every copy of that
    /// content in the source pile, or when the options are generated values with no pile order.
    /// Selecting a strict subset of a pile class is always treated as approximate: whether the
    /// remaining order matters depends on the effect (a Transform replaces the chosen position in
    /// place, while a MoveToHand removes it), so the predicate never claims exactness there. The
    /// verification switch still measures how often those approximate cases happen to coincide.
    /// </summary>
    public static bool IsExactByClass(in Battle battle, in ChoiceRequest request,
        scoped ReadOnlySpan<Card> cards, ulong selection)
    {
        if (request.UsesIndexSelection)
            return false;
        if (request.OptionsAreGenerated || request.SourceIsNone)
            return true;
        if (request.OptionCount == 0)
            return true;
        int options = request.OptionCount;
        Span<int> classOf = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> rankInClass = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classSize = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classFirst = stackalloc int[Limits.MaxChoiceOptions];
        int classCount = BuildClasses(cards, classOf, rankInClass, classSize, classFirst);
        Span<int> selectedCount = stackalloc int[Limits.MaxChoiceOptions];
        selectedCount.Clear();
        for (int option = 0; option < options; option++)
        {
            if ((selection & (1UL << option)) != 0)
                selectedCount[classOf[option]]++;
        }
        int start = Piles.Start(battle, request.Source);
        int pileCount = Piles.Count(battle, request.Source);
        for (int cls = 0; cls < classCount; cls++)
        {
            int selected = selectedCount[cls];
            if (selected == 0)
                continue;
            Card card = cards[classFirst[cls]];
            int total = 0;
            int previous = -2;
            bool contiguous = true;
            for (int position = 0; position < pileCount; position++)
            {
                if (!CardEquivalence.ContentEquals(in card, in battle.Cards[start + position]))
                    continue;
                total++;
                if (previous >= 0 && position != previous + 1)
                    contiguous = false;
                previous = position;
            }
            if (total == 0)
                return false;
            if (selected == total)
                continue;
            // A position-preserving effect (upgrade, transform, apply keyword, ...) leaves the two
            // copies at different places, so only selecting the whole class is exact there. A
            // removal/move effect that takes every selected card out of the source pile is exact
            // when the class is one contiguous run: any subset leaves the same remainder.
            if (!IsRemovalEffect(request.Effect) || !contiguous)
                return false;
        }
        return true;
    }

    private static bool IsRemovalEffect(ChoiceEffect effect)
        => effect is ChoiceEffect.MoveToHand
            or ChoiceEffect.MoveToDrawTop
            or ChoiceEffect.Discard
            or ChoiceEffect.Exhaust
            or ChoiceEffect.DiscardAndDraw
            or ChoiceEffect.MoveToHandFreeThisTurn;

    /// <summary>
    /// Builds a different physical selection with the same class counts as
    /// <paramref name="selection"/> by taking the last occurrence of each class instead of the first.
    /// Returns false when every class is already selecting all of its options (there is no distinct
    /// physical selection with these counts).
    /// </summary>
    public static bool TryBuildLastOccurrenceSelection(in ChoiceRequest request,
        scoped ReadOnlySpan<Card> cards, ulong selection, out ulong alternative)
    {
        alternative = 0;
        if (request.UsesIndexSelection)
            return false;
        int options = request.OptionCount;
        if (options == 0)
            return false;
        Span<int> classOf = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> rankInClass = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classSize = stackalloc int[Limits.MaxChoiceOptions];
        Span<int> classFirst = stackalloc int[Limits.MaxChoiceOptions];
        BuildClasses(cards, classOf, rankInClass, classSize, classFirst);
        Span<int> remaining = stackalloc int[Limits.MaxChoiceOptions];
        remaining.Clear();
        for (int option = 0; option < options; option++)
        {
            if ((selection & (1UL << option)) != 0)
                remaining[classOf[option]]++;
        }
        for (int option = options - 1; option >= 0; option--)
        {
            int cls = classOf[option];
            if (remaining[cls] == 0)
                continue;
            alternative |= 1UL << option;
            remaining[cls]--;
        }
        return alternative != selection
            && BitOperations.PopCount(alternative) == BitOperations.PopCount(selection);
    }

    /// <summary>Number of legal selections, saturating at <paramref name="cap"/>.</summary>
    public static int Count(in ChoiceRequest request, int cap) => (int)Count(request, (long)cap);

    /// <summary>
    /// Number of legal selections, saturating at <paramref name="cap"/>. Uses 128-bit intermediates
    /// so a large option set saturates instead of overflowing.
    /// </summary>
    public static long Count(in ChoiceRequest request, long cap)
    {
        if (cap <= 0)
            return 0;
        if (request.UsesIndexSelection)
            return Math.Min(request.OptionCount, cap);
        long total = 0;
        for (int size = request.MinCount; size <= request.MaxCount; size++)
        {
            Int128 combinations = 1;
            for (int index = 0; index < size; index++)
            {
                combinations = combinations * (request.OptionCount - index) / (index + 1);
                if (combinations > cap)
                    return cap;
            }
            if (total > cap - (long)combinations)
                return cap;
            total += (long)combinations;
            if (total >= cap)
                return cap;
        }
        return total;
    }

    private static int BuildClasses(scoped ReadOnlySpan<Card> cards, Span<int> classOf,
        Span<int> rankInClass, Span<int> classSize, Span<int> classFirst)
    {
        int count = 0;
        for (int option = 0; option < cards.Length; option++)
        {
            int found = -1;
            for (int cls = 0; cls < count; cls++)
            {
                if (CardEquivalence.ContentEquals(in cards[option], in cards[classFirst[cls]]))
                {
                    found = cls;
                    break;
                }
            }
            if (found < 0)
            {
                found = count++;
                classFirst[found] = option;
                classSize[found] = 0;
            }
            classOf[option] = found;
            rankInClass[option] = classSize[found];
            classSize[found]++;
        }
        return count;
    }

    private static void EnumerateCounts(int cls, int remaining, scoped ReadOnlySpan<int> classSize,
        Span<int> counts, scoped ReadOnlySpan<int> classOf, scoped ReadOnlySpan<int> rankInClass,
        int optionCount, Span<ulong> output, ref int written)
    {
        if (written >= output.Length)
            return;
        if (cls == classSize.Length)
        {
            if (remaining != 0)
                return;
            ulong mask = 0;
            for (int option = 0; option < optionCount; option++)
            {
                if (rankInClass[option] < counts[classOf[option]])
                    mask |= 1UL << option;
            }
            output[written++] = mask;
            return;
        }
        int maximum = Math.Min(classSize[cls], remaining);
        for (int take = maximum; take >= 0; take--)
        {
            counts[cls] = take;
            EnumerateCounts(cls + 1, remaining - take, classSize, counts, classOf, rankInClass,
                optionCount, output, ref written);
            if (written >= output.Length)
                return;
        }
    }
}
