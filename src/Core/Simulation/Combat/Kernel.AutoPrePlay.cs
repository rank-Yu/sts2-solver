namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// The AutoPrePlay phase at the player's turn start, mirroring
/// <c>SimulatedCombatState.TriggerAutoPrePlayEarly</c> / <c>ContinueAutoPrePlay</c>: the exhaust pile's
/// Bombardment cards first, then the scheduled auto-plays (Mayhem's amount of draw-pile top cards moved
/// to the play pile, then HistoryCourse's clone of the previous player turn's last attack), then the
/// Imbued enchantment's auto-plays. WhisperingEarring remains a scheduled source with no combat effect
/// (its only first-turn auto-play is a relic trigger with no state in either engine).
/// </summary>
public static partial class Kernel
{
    internal const string BombardmentId = "BOMBARDMENT";
    internal const string ImbuedEnchantmentId = "IMBUED";

    internal static void AutoPrePlay(ref Exec x)
    {
        if (IsOverOrEnding(x.B) || x.B.Player.IsDead)
            return;
        // Turn-start draws and hooks may already have played this turn's first attack.
        // The previous turn was separated before those hooks, not when this phase starts.
        AutoPlayBombardments(ref x);
        if (x.NeedsChoice)
            return;
        AutoPlayScheduled(ref x);
        if (x.NeedsChoice)
            return;
        if (x.HasPreviousHistoryCourseCard)
        {
            AutoPlayHistoryCourse(ref x, x.PreviousHistoryCourseCard);
            if (x.NeedsChoice)
                return;
        }
        if (x.R.HasHistoryCourse)
            for (int i = 0; i < x.B.CardCount; i++)
                x.B.Cards[i].HistoryCourseRole &= 1;
        AutoPlayImbued(ref x);
    }

    /// <summary>
    /// <c>ContinueAutoPrePlay</c>'s Bombardments stage: the Bombardment instances that were in the
    /// exhaust pile when the phase started, in exhaust order. A played Bombardment returns to the
    /// exhaust pile (its result is Exhaust), so the pile still holds exactly the captured count; the
    /// next unplayed copy of a value is always its first copy in the pile, because every played copy
    /// was appended after the unplayed ones.
    /// </summary>
    private static void AutoPlayBombardments(ref Exec x)
    {
        if (!Registry.TryCard(BombardmentId, out ushort bombardment))
            return;
        Span<Card> captured = stackalloc Card[x.B.ExhaustCount];
        int count = 0;
        int exhaustStart = Piles.Start(x.B, Pile.Exhaust);
        for (int offset = 0; offset < x.B.ExhaustCount; offset++)
        {
            Card card = x.B.Cards[exhaustStart + offset];
            if (card.Id == bombardment)
                captured[count++] = card;
        }
        for (int index = 0; index < count; index++)
        {
            if (IsOverOrEnding(x.B) || x.B.Player.IsDead)
                return;
            int start = Piles.Start(x.B, Pile.Exhaust);
            int present = 0;
            for (int offset = 0; offset < x.B.ExhaustCount; offset++)
            {
                if (x.B.Cards[start + offset].Id == bombardment)
                    present++;
            }
            if (present != count)
                throw new UnsupportedException("自动预打出期间消耗堆的轰击数量发生变化。");
            int position = -1;
            for (int offset = 0; offset < x.B.ExhaustCount; offset++)
            {
                Card candidate = x.B.Cards[start + offset];
                if (candidate.Id == captured[index].Id && candidate.Upgrade == captured[index].Upgrade)
                {
                    position = offset;
                    break;
                }
            }
            if (position < 0)
                throw new UnsupportedException("自动预打出的轰击已不在消耗堆。");
            AutoPlay(ref x, Pile.Exhaust, position);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// <c>ContinueScheduledAutoPlays</c>' Mayhem stage: MayhemPower's amount of draw-pile top cards
    /// are moved to the play pile first (<c>MoveCardsForAutoPlay</c>, shuffling when the draw pile is
    /// empty) and then auto-played in that order.
    /// </summary>
    private static void AutoPlayScheduled(ref Exec x)
    {
        if (!Registry.TryPower("MAYHEM_POWER", out ushort mayhem))
            return;
        int count = GetAmount(x.B, Limits.PlayerOwner, mayhem);
        if (count <= 0)
            return;
        if (count > Limits.Cards)
            throw new CapacityException("AutoPlayCards", Limits.Cards);
        Span<Card> captured = stackalloc Card[count];
        int moved = 0;
        for (int index = 0; index < count; index++)
        {
            ShuffleIfNecessary(ref x);
            if (x.NeedsChoice)
                return;
            if (x.B.DrawCount == 0)
                break;
            Card card = Piles.At(ref x.B, Pile.Draw, 0);
            Piles.MoveToEnd(ref x.B, Pile.Draw, 0, Pile.Play);
            captured[moved++] = card;
        }
        if (moved == 0)
            return;
        // MoveCardsForAutoPlay keeps the live CardModels: an earlier auto-play can move a later queued
        // card to another pile (SummonForth moves a queued Sovereign Blade to hand) or remove it from
        // combat. Mark each queued instance with the dynamic 64..255 identity range shared through
        // TransientMarkBase (Sly/Stoke/Decisions also allocate there); the mark travels with the value
        // and is cleared in finally.
        int markBase = x.TransientMarkBase;
        if (64 + markBase + moved > 256)
            throw new CapacityException("TransientMarks", 256 - 64);
        x.TransientMarkBase = checked((byte)(markBase + moved));
        int markStart = 64 + markBase;
        int playStart = Piles.Start(x.B, Pile.Play);
        for (int index = 0; index < moved; index++)
            x.B.Cards[playStart + index].TransientMark = checked((byte)(markStart + index));
        try
        {
            for (int index = 0; index < moved; index++)
            {
                if (IsOverOrEnding(x.B) || x.B.Player.IsDead)
                    return;
                byte mark = checked((byte)(markStart + index));
                if (TryFindUniqueTransientMark(x.B, mark, out Pile pile, out int position))
                {
                    AutoPlay(ref x, pile, position);
                }
                else
                {
                    // The existing engine's AutoPlay re-adds a live card whose GetPile(State) is null
                    // (a queued card that left combat) and plays it again.
                    AutoPlay(ref x, captured[index], Limits.NoOwner, skipXCapture: false,
                        historyCourseDupe: false, out _, out _, out _);
                }
                if (x.NeedsChoice)
                    return;
            }
        }
        finally
        {
            for (int position = 0; position < x.B.CardCount; position++)
            {
                if (x.B.Cards[position].TransientMark >= markStart
                    && x.B.Cards[position].TransientMark < markStart + moved)
                {
                    x.B.Cards[position].TransientMark = 0;
                }
            }
            for (int slot = 0; slot < Limits.DetachedCards; slot++)
            {
                if (x.B.DetachedCards[slot].TransientMark >= markStart
                    && x.B.DetachedCards[slot].TransientMark < markStart + moved)
                {
                    x.B.DetachedCards[slot].TransientMark = 0;
                }
            }
            x.TransientMarkBase = checked((byte)markBase);
        }
    }

    /// <summary>
    /// <c>SimulatedCombatState.ContinueScheduledAutoPlays</c>' HistoryCourse stage (after Mayhem's
    /// cards, before the Imbued stage): for every non-melted HistoryCourse relic, clone the previous
    /// player turn's last attack and auto-play the clone. <c>CreateDupeForPlayer</c> is a clone with
    /// <c>IsDupe = true</c> and the Exhaust keyword removed; the kernel's dupe flag makes the play
    /// leave combat instead of moving to a result pile and keeps it out of the recorded last attack.
    /// </summary>
    private static void AutoPlayHistoryCourse(ref Exec x, in Card previous)
    {
        if (x.B.PlayerTurn <= 1 || !Registry.TryRelic("HISTORY_COURSE", out ushort historyCourse))
            return;
        for (int relic = 0; relic < x.R.Relics.Count; relic++)
        {
            Relic row = x.B.GetRelic(x.R, relic);
            if (row.Id != historyCourse || (row.Flags & RelicFlags.Melted) != 0)
                continue;
            Card copy = CloneCard(PreviousHistoryCourse(x.B, previous));
            copy.IsDupe = true;
            copy.RemoveKeyword(Keywords.Exhaust);
            // The clone is a new object: it is not in the existing engine's _returnToHandNextTurn set.
            copy.ReturnToHandNextTurn = false;
            AutoPlay(ref x, copy, Limits.NoOwner, skipXCapture: false, historyCourseDupe: true,
                out _, out _, out _);
            if (x.NeedsChoice)
                return;
            if (IsOverOrEnding(x.B) || x.B.Player.IsDead)
                return;
        }
    }

    /// <summary>
    /// <c>EnchantmentLifecycleSupport.TriggerAutoPrePlay</c>: on the first player turn, auto-play every
    /// card carrying the Imbued enchantment in <c>AllCards</c> order (hand, draw, discard, exhaust,
    /// play). The existing engine snapshots the card objects before the loop and keeps playing those
    /// objects. Sts2Solver has no card identity, so it snapshots each card's absolute position plus a stable
    /// key (id, enchantment, affliction, clone) and tracks the position through the net move of every
    /// auto-play. A position whose key no longer matches, a changed card count (generated/removed cards)
    /// or a played card whose final position cannot be captured is an explicit boundary: the kernel
    /// must not guess which copy the existing engine would play. Cost modifiers, upgrades and the
    /// one-time affliction-normalization bit may change in place without moving the card and are read
    /// from the current value.
    /// </summary>
    private static void AutoPlayImbued(ref Exec x)
    {
        if (x.B.PlayerTurn > 1 || !Registry.TryEnchantment(ImbuedEnchantmentId, out ushort imbued))
            return;
        int count = 0;
        Span<ImbuedCardKey> keys = stackalloc ImbuedCardKey[x.B.CardCount];
        Span<int> positions = stackalloc int[x.B.CardCount];
        byte enchantment = (byte)(imbued + 1);
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref readonly Card card = ref x.B.Cards[position];
            if (card.EnchantmentId != enchantment)
                continue;
            keys[count] = ImbuedCardKey.Of(card);
            positions[count] = position;
            count++;
        }
        for (int index = 0; index < count; index++)
        {
            if (IsOverOrEnding(x.B) || x.B.Player.IsDead)
                return;
            int position = positions[index];
            if ((uint)position >= (uint)x.B.CardCount || !keys[index].Matches(x.B.Cards[position]))
                throw new UnsupportedException(
                    $"Imbued 自动预打出的卡位置已变化：{Registry.Card(keys[index].Id).NativeId}");
            int before = x.B.CardCount;
            Card sourceCard = x.B.Cards[position];
            Pile sourcePile = Piles.PileOf(x.B, position, out int sourceIndex);
            bool played = AutoPlay(ref x, sourcePile, sourceIndex, Limits.NoOwner, skipXCapture: false,
                out _, out Pile resultPile, out int resultIndex);
            if (x.NeedsChoice)
                return;
            int destination;
            if (!played)
            {
                // MoveToResultPileWithoutPlaying does not run play hooks; the card goes to the exhaust
                // pile when it would exhaust, otherwise to the discard pile, at the end.
                if (x.B.CardCount != before)
                    throw new UnsupportedException("Imbued 未打出的卡改变了战斗牌数。");
                bool exhaust = (sourceCard.Flags & CardFlags.ExhaustOnNextPlay) != 0
                    || HasKeyword(x.B, sourceCard, Keywords.Exhaust);
                Pile target = exhaust ? Pile.Exhaust : Pile.Discard;
                destination = Piles.Start(x.B, target) + Piles.Count(x.B, target) - 1;
                if (!CardEffects.Equal(x.B.Cards[destination], sourceCard))
                    throw new UnsupportedException("Imbued 未打出的卡去向无法捕获。");
            }
            else if (resultIndex < 0)
            {
                if (x.B.CardCount != before - 1)
                    throw new UnsupportedException("Imbued 打出的卡离场时战斗牌数不一致。");
                destination = -1;
            }
            else
            {
                if (x.B.CardCount != before)
                    throw new UnsupportedException("Imbued 打出的卡改变了战斗牌数。");
                destination = Piles.Start(x.B, resultPile) + resultIndex;
            }
            for (int later = index + 1; later < count; later++)
                positions[later] = MoveCardPosition(positions[later], position, destination);
        }
    }

    /// <summary>Net position of an untouched card after one card moved from <paramref name="source"/> to <paramref name="destination"/> (-1 = left combat).</summary>
    private static int MoveCardPosition(int position, int source, int destination)
    {
        int shifted = position > source ? position - 1 : position;
        if (destination >= 0 && shifted >= destination)
            shifted++;
        return shifted;
    }

    /// <summary>
    /// The stable part of a card's identity for the Imbued snapshot: the fields an in-place effect does
    /// not change except through a transform/upgrade (upgrade is intentionally excluded so Dampen's
    /// re-leveling still matches the same object).
    /// </summary>
    private readonly struct ImbuedCardKey
    {
        private readonly ushort _id;
        private readonly byte _enchantment;
        private readonly byte _affliction;
        private readonly bool _clone;

        private ImbuedCardKey(in Card card)
        {
            _id = card.Id;
            _enchantment = card.EnchantmentId;
            _affliction = card.AfflictionId;
            _clone = card.IsClone;
        }

        public ushort Id => _id;

        public static ImbuedCardKey Of(in Card card) => new(card);

        public bool Matches(in Card card)
            => card.Id == _id && card.EnchantmentId == _enchantment && card.AfflictionId == _affliction
                && card.IsClone == _clone;
    }
}
