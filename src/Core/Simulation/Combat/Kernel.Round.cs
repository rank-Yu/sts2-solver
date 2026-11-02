using System.Runtime.InteropServices;

namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    private const int BaseHandDrawCount = 5;

    /// <summary>
    /// Mirrors <c>CombatBeamSolver.AdvanceRound</c>: player turn end, the enemy turn, and the next
    /// player turn start. Branches for unsupported powers and relics are omitted; admission rejects
    /// roots that could reach them.
    /// </summary>
    public static void AdvanceRound(ref Exec x)
    {
        if (!AdvanceRoundToPlayerStart(ref x, out bool takingExtraTurn))
            return;
        AdvanceRoundPlayerStart(ref x, takingExtraTurn);
    }

    /// <summary>
    /// Runs the player turn end and the enemy turn, stopping immediately before
    /// <c>AdvanceRoundPlayerStart</c>. Returns false when the round stopped earlier (a choice, an
    /// ending, or an unsupported boundary); <paramref name="takingExtraTurn"/> is only meaningful
    /// when true.
    /// </summary>
    internal static bool AdvanceRoundToPlayerStart(ref Exec x, out bool takingExtraTurn)
    {
        takingExtraTurn = false;
        if (!IsInProgress(x.B))
            return false;

        x.ChoiceTiming = ChoiceTiming.PlayerTurnEnd;
        // PlayerTurnEndLifecycle.RunPhaseOne.
        x.B.Phase = PlayerPhase.End;
        // EndTurnPowerSupport.TriggerVeryEarly: no supported sources.
        RelicPass(ref x, RelicPhase.PrepareBeforeSideTurnEnd, Side.Player);
        if (x.NeedsChoice)
            return false;
        // SimulateEndPlayerTurnBeforeOrbPassives.
        x.B.Phase = PlayerPhase.AutoPostPlay;
        AfterAutoPostPlayPhaseEnteredHook afterAutoPostPlay = new();
        HookDispatch.Dispatch(ref x, ref afterAutoPostPlay, gatedByEnding: true);
        if (x.NeedsChoice)
            return false;
        x.B.Phase = PlayerPhase.End;
        BeforeSideTurnEnd(ref x, Side.Player, ParticipantMask(Limits.PlayerOwner));
        if (x.NeedsChoice)
            return false;
        CheckWinCondition(ref x.B);
        if (!IsOverOrEnding(x.B))
        {
            // OrbQueue.BeforeTurnEnd: every orb in the queue snapshot, each passive resolving its
            // pending enemy deaths; a passive that ends the combat skips the turn-end hand effects.
            TriggerBeforeTurnEndOrbs(ref x);
            if (x.NeedsChoice)
                return false;
            // OrbLifecycleSupport.TriggerBeforeTurnEnd compensates the block breaks of the orb pass.
            ApplyBlockBreakCompensations(ref x);
            if (x.NeedsChoice)
                return false;
            if (!IsOverOrEnding(x.B))
            {
                DoTurnEnd(ref x);
                if (x.NeedsChoice)
                    return false;
                CheckWinCondition(ref x.B);
            }
            CompletePlayerEarlySideTurnEnd(ref x);
            if (!x.NeedsChoice)
                NormalizeCardAfflictions(ref x);
        }
        ApplyEnemyDeathPowers(ref x);
        if (!IsInProgress(x.B))
            return false;
        FlushPlayerHandAtTurnEnd(ref x);
        int turnEndShuffleEvents = x.B.ShuffleEventCount;
        // PlayerTurnEndLifecycle.RunPhaseTwo.
        PlayerTurnEndPhaseTwo(ref x);
        if (x.NeedsChoice)
            return false;
        x.B.Phase = PlayerPhase.None;
        x.B.ShufflesCrossed += x.B.ShuffleEventCount - turnEndShuffleEvents;
        ApplyEnemyDeathPowers(ref x);

        // HookMirrors.ShouldTakeExtraTurn after the player turn-end phases, before the enemy turn.
        bool extraTurn = ShouldTakeExtraTurnPass(ref x);
        if (x.NeedsChoice)
            return false;
        if (extraTurn)
        {
            // An extra turn advances the player's turn number, so damage from the just-finished turn
            // becomes Emotion Chip's "previous turn" window (recorded only when that relic is present).
            AfterRelicRoundPass(ref x);
            if (x.NeedsChoice)
                return false;
            AfterTakingExtraTurnPass(ref x);
            if (x.NeedsChoice)
                return false;
        }
        else
        {
            x.ChoiceTiming = ChoiceTiming.EnemyTurn;
            // Block breaks from the player's turn end were settled at their own compensation points;
            // an enemy-turn break is never compensated (the existing engine only replays card, orb,
            // potion and reactive-relic history).
            x.PendingBlockBreakOwners = 0;
            RunEnemyTurn(ref x);
        }
        // The performed-move marks only carry information within one enemy turn. The
        // forced-must-perform mark is the same kind of in-turn kernel state
        // (SimulatedCombatState.MonsterAi.cs:122-126): PrepareMonsterMovesForNextRound consumes it when
        // the turn reaches that pass, but an early return (the player died during the enemy turn or a
        // choice was requested) skips the pass, and it must still be zero at every stable boundary.
        for (int index = 0; index < x.B.EnemyCount; index++)
        {
            x.B.Enemies[index].Flags &= ~(EnemyFlags.PerformedMoveThisRound | EnemyFlags.ForcedMustPerformAfterMove);
        }
        if (x.NeedsChoice || !IsInProgress(x.B))
            return false;
        takingExtraTurn = extraTurn;
        return true;
    }

    /// <summary>Mirrors the enemy-turn block of <c>CombatBeamSolver.AdvanceRound</c>.</summary>
    private static void RunEnemyTurn(ref Exec x)
    {
        Span<uint> acting = stackalloc uint[Limits.Enemies];
        Span<byte> steamPlayer = stackalloc byte[1];
        Span<byte> steamSelf = stackalloc byte[1];
        steamPlayer[0] = Limits.PlayerOwner;
        int actingCount = x.B.RosterCount;
        for (int position = 0; position < actingCount; position++)
            acting[position] = x.B.Enemies[x.B.Roster[position]].CombatId;

        SetSide(ref x.B, Side.Enemy);
        // BeginSideTurn for the enemies: their per-turn counters are not tracked, but
        // ResetCardLifecycleTurn clears the single shared _fetchCardsPlayedThisTurn set.
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref Card card = ref x.B.Cards[position];
            if (Registry.Card(card.Id).TracksFetchPlay)
                card.Private &= ~FetchPlayedBit;
        }
        ushort enemyOwners = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
            enemyOwners |= ParticipantMask(EnemyOwner(x.B.Roster[position]));
        SnapshotPowerAmountsAtTurnStart(ref x.B, enemyOwners);
        // Each phase reads the live enemy list, as the existing engine passes simulatedCombat.Enemies.
        Span<byte> participants = stackalloc byte[Limits.Enemies];
        BeforeSideTurnStart(ref x, Side.Enemy, participants[..RosterOwners(x.B, participants)]);
        if (x.NeedsChoice)
            return;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            ClearBlockAtSideStart(ref x, EnemyOwner(x.B.Roster[position]));
            if (x.NeedsChoice)
                return;
        }
        TriggerSideTurnStart(ref x, Side.Enemy, participants[..RosterOwners(x.B, participants)], decrementPlating: x.B.Round > 1);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.TriggerPoison over the current enemies, before the deferred death pass.
        if (!TriggerPoison(ref x, participants[..RosterOwners(x.B, participants)]))
            return;
        ApplyEnemyDeathPowers(ref x);
        if (CheckWinCondition(ref x.B))
            return;

        for (int index = 0; index < actingCount; index++)
        {
            // The acting snapshot is by CombatId: an escaped creature compacts the known array, and a
            // stored owner index would otherwise name a different enemy for every later slot.
            int actingRow = FindEnemyIndex(x.B, acting[index]);
            if (actingRow < 0)
                continue;
            byte owner = EnemyOwner(actingRow);
            ref Enemy enemy = ref x.B.Enemies[actingRow];
            if (!CanPerformMonsterMove(enemy))
                continue;
            if ((enemy.Flags & EnemyFlags.SkipNextMove) != 0)
            {
                enemy.Flags &= ~EnemyFlags.SkipNextMove;
                enemy.Flags |= EnemyFlags.PerformedMoveThisRound;
                if (CheckWinCondition(ref x.B))
                    return;
                continue;
            }
            // CombatBeamSolver.Expansion.Replay: the Steam Eruption forced phase is consumed after
            // the stun skip and before the normal move. It replaces the move entirely; the performed
            // mark still advances the state machine at the end of the turn.
            if (TryConsumeSteamForcedMove(ref x, owner, out bool steamExploded, out int steamDamage))
            {
                // The performed move is the one captured before the forced phase replaces it.
                x.B.Enemy(owner).Flags |= EnemyFlags.PerformedMoveThisRound;
                if (steamExploded)
                {
                    DamagePlayerFromMonster(ref x, owner, steamPlayer, steamDamage);
                    if (x.NeedsChoice)
                        return;
                    steamSelf[0] = owner;
                    Kill(ref x, steamSelf, force: true);
                    if (x.NeedsChoice)
                        return;
                    ApplyEnemyDeathPowers(ref x);
                    if (x.NeedsChoice)
                        return;
                }
                if (CheckWinCondition(ref x.B))
                    return;
                continue;
            }
            uint actingCombatId = enemy.CombatId;
            bool playerDied = ExecuteMonsterMove(ref x, owner);
            int performedRow = FindEnemyIndex(x.B, actingCombatId);
            if (performedRow >= 0)
                x.B.Enemies[performedRow].Flags |= EnemyFlags.PerformedMoveThisRound;
            if (x.NeedsChoice)
                return;
            if (CheckWinCondition(ref x.B) || playerDied)
                return;
        }

        TriggerEnemySideTurnEndEffects(ref x, participants[..RosterOwners(x.B, participants)]);
        if (x.NeedsChoice)
            return;
        // CombatBeamSolver.Expansion.Replay checks BattlewornDummyTimedOut immediately after
        // TriggerEnemySideTurnEndEffects and returns SearchBoundaryReason.EventDefeat before the
        // enemy-death, poison, affliction-normalization and next-round preparation passes.
        if (x.B.Outcome == Outcome.EventDefeat)
            return;
        ApplyEnemyDeathPowers(ref x);
        // CorePowerSupport.TriggerPoison over the player, then ClearNoDraw (no supported sources).
        Span<byte> players = stackalloc byte[1];
        players[0] = Limits.PlayerOwner;
        if (!TriggerPoison(ref x, players))
            return;
        if (!x.NeedsChoice)
            NormalizeCardAfflictions(ref x);
        AfterRelicRoundPass(ref x);
        if (x.NeedsChoice)
            return;
        if (CheckWinCondition(ref x.B))
            return;
        PrepareMonsterMovesForNextRound(ref x);
    }

    /// <summary>Mirrors <c>HookMirrors.BeforeSideTurnEnd</c>: the VeryEarly, Early and normal passes.</summary>
    private static void BeforeSideTurnEnd(ref Exec x, Side side, ushort participants)
    {
        BeforeSideTurnEndVeryEarlyHook veryEarly = new() { Side = side, Participants = participants };
        HookDispatch.Dispatch(ref x, ref veryEarly, gatedByEnding: true);
        if (x.NeedsChoice)
            return;
        BeforeSideTurnEndEarlyHook early = new() { Side = side, Participants = participants };
        HookDispatch.Dispatch(ref x, ref early, gatedByEnding: true);
        if (x.NeedsChoice)
            return;
        BeforeSideTurnEndHook normal = new() { Side = side, Participants = participants };
        HookDispatch.Dispatch(ref x, ref normal, gatedByEnding: true);
    }

    public static ushort ParticipantMask(byte owner) => (ushort)(1 << owner);

    /// <summary>Whether <paramref name="owner"/> is in a participant bit set.</summary>
    public static bool Participates(ushort participants, byte owner) => (participants & (1 << owner)) != 0;

    /// <summary><c>SimulatedCombatState.CurrentSide</c> setter: a side change clears the lost-HP marks.</summary>
    private static void SetSide(ref Battle battle, Side side)
    {
        if (battle.Side != side)
            ClearLostHpThisTurn(ref battle);
        battle.Side = side;
    }

    /// <summary>
    /// <c>HasLostHpThisTurn</c> after <c>AdvancePlayerTurn</c>'s cache reset: only the root history's
    /// per-creature answer remains (<see cref="Rules.RootLostHpMask"/>).
    /// </summary>
    private static void RestoreRootLostHp(ref Battle battle, ushort mask)
    {
        SetLostHpFlag(ref battle.Player, (mask & (1 << Limits.PlayerOwner)) != 0);
        SetLostHpFlag(ref battle.Osty, (mask & (1 << Limits.OstyOwner)) != 0);
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            SetLostHpFlag(ref battle.Enemies[index].Creature,
                (mask & (1 << EnemyOwner(index))) != 0);
        }
    }

    private static void SetLostHpFlag(ref Creature creature, bool lost)
    {
        if (lost)
            creature.Flags |= CreatureFlags.LostHpThisTurn;
        else
            creature.Flags &= ~CreatureFlags.LostHpThisTurn;
    }

    private static void ClearLostHpThisTurn(ref Battle battle)
    {
        battle.Player.Flags &= ~CreatureFlags.LostHpThisTurn;
        battle.Osty.Flags &= ~CreatureFlags.LostHpThisTurn;
        for (int index = 0; index < battle.EnemyCount; index++)
            battle.Enemies[index].Creature.Flags &= ~CreatureFlags.LostHpThisTurn;
    }

    /// <summary><c>SimulatedCombatState.BeginSideTurn</c> for the player: resets the per-turn counters.</summary>
    private static void BeginPlayerSideTurn(ref Battle battle)
    {
        for (Counter counter = Counter.CardsPlayedThisTurn; counter <= Counter.EnergySpentThisTurn; counter++)
            battle.SetCounter(counter, 0);
        // BeginSideTurn's ResetTurnCounter(_starsGainedThisTurn) for the player.
        battle.SetCounter(Counter.StarsGainedThisTurn, 0);
        // SimulatedCombatState.BeginSideTurn(player): Osty's per-turn counters are reset through the
        // native `ownerPlayer.Osty` pet field, which only exists for a pet that was already present when
        // the root was captured. A pet created during the search keeps accumulating (existing behavior).
        if ((battle.Flags & BattleFlags.OstyWasRootPet) != 0)
            battle.SetCounter(Counter.OstyAttacksThisTurn, 0);
        battle.SetCounter(Counter.DoomAppliedThisTurn, 0);
        // BeginSideTurn -> ResetCardLifecycleTurn: _fetchCardsPlayedThisTurn is cleared for this side.
        for (int position = 0; position < battle.CardCount; position++)
        {
            ref Card card = ref battle.Cards[position];
            if (Registry.Card(card.Id).TracksFetchPlay)
                card.Private &= ~Kernel.FetchPlayedBit;
        }
        // SimulatedCombatState.BeginSideTurn(player) -> RemovePoweredAttackHitsDealtBy(player):
        // BeatIntoShape's per-target hit count is a this-turn value keyed by the dealer's side.
        for (int index = 0; index < battle.EnemyCount; index++)
            battle.Enemies[index].PoweredAttackHitsThisTurn = 0;
    }

    /// <summary>Owners of the roster enemies in roster order.</summary>
    internal static int RosterOwners(in Battle battle, Span<byte> owners)
    {
        for (int position = 0; position < battle.RosterCount; position++)
            owners[position] = EnemyOwner(battle.Roster[position]);
        return battle.RosterCount;
    }

    /// <summary>
    /// Mirrors <c>CorePowerSupport.TickDurations</c>: Weak, Vulnerable, Frail and Intangible, then NoBlock,
    /// for every creature. Sts2Solver marks those powers with <see cref="PowerDef.TicksAtEnemySideEnd"/>.
    /// </summary>
    private static void TickDurations(ref Battle battle)
    {
        Span<byte> creatures = stackalloc byte[Limits.Enemies + 2];
        int count = 0;
        creatures[count++] = Limits.PlayerOwner;
        if ((battle.Flags & BattleFlags.OstyPresent) != 0)
            creatures[count++] = Limits.OstyOwner;
        for (int position = 0; position < battle.RosterCount; position++)
            creatures[count++] = EnemyOwner(battle.Roster[position]);

        // One pass buckets every ticking power by its creature in power-array order, then ticks the
        // buckets in the original creature order. A previous creature's tick only removes powers of
        // that creature, so the surviving (owner, id) sequence seen by every later bucket is exactly
        // the sequence the per-creature rescans would produce.
        Span<int> bucketOfOwner = stackalloc int[16];
        bucketOfOwner.Fill(-1);
        for (int index = 0; index < count; index++)
            bucketOfOwner[creatures[index]] = index;
        Span<int> counts = stackalloc int[count];
        counts.Clear();
        Span<int> offsets = stackalloc int[count + 1];
        int total = 0;
        for (int power = 0; power < battle.PowerCount; power++)
        {
            ref readonly Power row = ref battle.Powers[power];
            if (row.Owner >= bucketOfOwner.Length)
                continue;
            int bucket = bucketOfOwner[row.Owner];
            if (bucket < 0 || !Registry.Power(row.Id).TicksAtEnemySideEnd)
                continue;
            counts[bucket]++;
            total++;
        }
        int running = 0;
        for (int index = 0; index < count; index++)
        {
            offsets[index] = running;
            running += counts[index];
        }
        offsets[count] = running;
        Span<ushort> ticking = stackalloc ushort[total];
        Span<int> cursor = stackalloc int[count];
        offsets[..count].CopyTo(cursor);
        for (int power = 0; power < battle.PowerCount; power++)
        {
            ref readonly Power row = ref battle.Powers[power];
            if (row.Owner >= bucketOfOwner.Length)
                continue;
            int bucket = bucketOfOwner[row.Owner];
            if (bucket < 0 || !Registry.Power(row.Id).TicksAtEnemySideEnd)
                continue;
            ticking[cursor[bucket]++] = row.Id;
        }
        for (int index = 0; index < count; index++)
        {
            byte owner = creatures[index];
            for (int tick = offsets[index]; tick < offsets[index + 1]; tick++)
                TickDuration(ref battle, owner, ticking[tick]);
        }
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.DoTurnEnd</c>: the hand is split once into turn-end-in-hand cards
    /// and the remaining Ethereal cards (<c>ShouldEtherealTrigger</c> has no supported listeners); the
    /// Ethereal ones exhaust first, then <c>DoTurnEndCards</c> moves each turn-end card to the play pile,
    /// runs its effect, and exhausts it when Ethereal (without the hook check) or discards it. Cards are
    /// located again before each step, since exhaust and effect hooks may change the hand in between.
    /// </summary>
    private static void DoTurnEnd(ref Exec x)
    {
        if (IsOverOrEnding(x.B))
            return;
        int handCount = x.B.HandCount;
        Span<int> ethereal = stackalloc int[handCount];
        Span<int> turnEnd = stackalloc int[handCount];
        int etherealCount = 0, turnEndCount = 0;
        for (int index = 0; index < handCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (Registry.Card(card.Id).HasTurnEndInHandEffect)
                turnEnd[turnEndCount++] = index;
            else if (HasKeyword(x.B, card, Keywords.Ethereal))
                ethereal[etherealCount++] = index;
        }
        Span<SelectedCard> etherealCards = stackalloc SelectedCard[etherealCount];
        SnapshotSelected(x.B, Pile.Hand, ethereal[..etherealCount], etherealCards);
        Span<SelectedCard> turnEndCards = stackalloc SelectedCard[turnEndCount];
        SnapshotSelected(x.B, Pile.Hand, turnEnd[..turnEndCount], turnEndCards);
        for (int item = 0; item < etherealCount; item++)
        {
            Exhaust(ref x, Pile.Hand, LocateSelected(x.B, Pile.Hand, etherealCards, item), causedByEthereal: true);
            if (x.NeedsChoice)
                return;
        }
        for (int item = 0; item < turnEndCount; item++)
        {
            int index = LocateSelected(x.B, Pile.Hand, turnEndCards, item);
            Card card = turnEndCards[item].Card;
            MoveCard(ref x.B, Pile.Hand, index, Pile.Play);
            x.Card = card;
            x.Stats = x.R.Card(card.Id, card.Upgrade);
            x.Target = Limits.NoOwner;
            Registry.Card(card.Id).OnTurnEndInHand(ref x);
            if (x.NeedsChoice)
                return;
            // An in-hand turn-end effect (Infection) may kill the player; the existing engine stops
            // the lifecycle there, so the card is no longer required to sit in the play pile.
            if (x.B.Player.IsDead)
                return;
            // The effect may change the card's own state (a private counter), never its identity or place.
            int position = x.B.PlayCount - 1;
            if (position < 0 || Piles.At(ref x.B, Pile.Play, position).Id != card.Id
                || Piles.At(ref x.B, Pile.Play, position).Upgrade != card.Upgrade)
                throw new UnsupportedException("回合末在手效果移动了打出区的卡。");
            if (HasKeyword(x.B, Piles.At(ref x.B, Pile.Play, position), Keywords.Ethereal))
            {
                Exhaust(ref x, Pile.Play, position, causedByEthereal: true);
                if (x.NeedsChoice)
                    return;
            }
            else
            {
                MoveCard(ref x.B, Pile.Play, position, Pile.Discard);
            }
        }
    }

    /// <summary>Mirrors <c>CorePowerSupport.FlushPlayerHandAtTurnEnd</c>.</summary>
    private static void FlushPlayerHandAtTurnEnd(ref Exec x)
    {
        // EnchantmentLifecycleSupport.BeforeFlush runs before ShouldFlush and before any card moves.
        BeforeFlush(ref x);
        // A refused flush keeps the whole hand.
        bool flush = ShouldFlush(ref x);
        if (flush && !IsEnding(x.B) && !x.B.Player.IsDead)
            FlushNonRetainedHand(ref x);
        // CorePowerSupport.FlushPlayerHandAtTurnEnd runs the AfterFlush relics after the non-retained
        // hand moved and before the end-of-turn card cleanup (Bookmark).
        AfterFlushPass(ref x);
        if (x.NeedsChoice)
            return;
        // End-of-turn cleanup for every combat card:
        int total = x.B.CardCount;
        for (int position = 0; position < total; position++)
            EndOfTurnCleanup(ref x.B.Cards[position]);
    }

    /// <summary>
    /// Appends every non-retained hand card to the discard pile in hand order with one compaction:
    /// the retained hand and the draw pile move left, the old discard follows them, and the flushed
    /// cards fill the gap before the exhaust/play tail, which stays in place. Equivalent to removing
    /// each card and appending it, without the per-card suffix shift.
    /// </summary>
    private static void FlushNonRetainedHand(ref Exec x)
    {
        int handCount = x.B.HandCount;
        if (handCount == 0)
            return;
        Span<Card> flushed = stackalloc Card[handCount];
        int keep = 0;
        int count = 0;
        for (int index = 0; index < handCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (HasKeyword(x.B, card, Keywords.Retain) || (card.Flags & CardFlags.RetainThisTurn) != 0)
            {
                if (keep != index)
                    x.B.Cards[keep] = card;
                keep++;
            }
            else
            {
                flushed[count++] = card;
            }
        }
        if (count == 0)
            return;
        int drawCount = x.B.DrawCount;
        int discardCount = x.B.DiscardCount;
        Span<Card> all = MemoryMarshal.CreateSpan(ref x.B.Cards[0], x.B.CardCount);
        if (keep != handCount)
            all.Slice(handCount, drawCount).CopyTo(all.Slice(keep));
        int discardStart = keep + drawCount;
        int oldDiscardStart = handCount + drawCount;
        all.Slice(oldDiscardStart, discardCount).CopyTo(all.Slice(discardStart));
        flushed[..count].CopyTo(all.Slice(discardStart + discardCount));
        x.B.HandCount = checked((byte)keep);
        x.B.DiscardCount = checked((byte)(discardCount + count));
    }

    /// <summary>
    /// <c>EnchantmentLifecycleSupport.BeforeFlush</c>: every hand card's enchantment gets the callback
    /// before <c>ShouldFlush</c>. The existing engine iterates the hand directly (SlumberingEssence adds
    /// one <c>WhenPlayed</c> −1 modifier per copy), so the kernel does the same instead of a generic
    /// listener scan.
    /// </summary>
    internal static void BeforeFlush(ref Exec x)
    {
        if (!x.R.HasBeforeFlushEnchantment
            || (Registry.AnyHooksExt & HookMaskExt.BeforeFlush) == 0)
            return;
        int handCount = x.B.HandCount;
        for (int index = 0; index < handCount; index++)
        {
            ref Card card = ref x.B.Cards[index];
            if (card.EnchantmentId == 0)
                continue;
            EnchantmentDef def = Registry.Enchantment((ushort)(card.EnchantmentId - 1));
            if ((def.HooksExt & HookMaskExt.BeforeFlush) == 0)
                continue;
            def.BeforeFlush(ref x, new Listener(ListenerKind.Enchantment, (byte)index, Limits.PlayerOwner));
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>Mirrors <c>PersistentRelicSupport.ShouldFlush</c>: every combat listener, without the ending gate.</summary>
    private static bool ShouldFlush(ref Exec x)
    {
        if ((Registry.AnyHooks & HookMask.ShouldFlush) == 0)
            return true;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount];
        int count = HookDispatch.CollectListeners(x.B, HookMask.ShouldFlush, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!HookDispatch.Definition(x.B, listeners[index]).ShouldFlush(ref x, listeners[index]))
                return false;
        }
        return true;
    }

    /// <summary>Mirrors <c>CardModel.EndOfTurnCleanup</c>.</summary>
    private static void EndOfTurnCleanup(ref Card card)
    {
        card.Flags &= ~(CardFlags.ExhaustOnNextPlay | CardFlags.RetainThisTurn | CardFlags.SlyThisTurn);
        int kept = 0;
        for (int index = 0; index < card.EnergyModifierCount; index++)
        {
            CostModifier modifier = card.EnergyModifiers[index];
            if (((byte)modifier.Expiration & (byte)CostExpiration.EndOfTurn) != 0)
                continue;
            card.EnergyModifiers[kept++] = modifier;
        }
        for (int index = kept; index < Limits.CardEnergyCostModifiers; index++)
            card.EnergyModifiers[index] = default;
        card.EnergyModifierCount = kept;
        if (card.HasTemporaryStarCost)
            card.RemoveTemporaryStarCosts(atTurnEnd: true);
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.SnapshotPowerAmountsAtTurnStart</c> for one creature, limited
    /// to the powers whose <c>AmountOnTurnStart</c> any settlement reads
    /// (<see cref="PowerDef.UsesAmountOnTurnStart"/>). The native snapshot writes the model
    /// amount, which lags a mirrored decrement by one action boundary; Sts2Solver has no such lag, so the
    /// remaining powers keep this inert field at zero instead of reproducing the stale model value.
    /// </summary>
    private static void SnapshotPowerAmountsAtTurnStart(ref Battle battle, ushort owners)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref Power power = ref battle.Powers[index];
            if (power.Owner >= 16 || (owners & ParticipantMask(power.Owner)) == 0)
                continue;
            power.AmountOnTurnStart = Registry.Power(power.Id).UsesAmountOnTurnStart
                ? power.Amount
                : 0;
        }
    }

    /// <summary>
    /// Mirrors <c>CombatBeamSolver.AdvanceRoundPlayerStart</c> and <c>ContinuePlayerStart</c> for a
    /// round without turn-start choices.
    /// </summary>
    private static void AdvanceRoundPlayerStart(ref Exec x, bool takingExtraTurn = false)
    {
        // Enemy-turn damage is not part of any compensated history window.
        x.PendingBlockBreakOwners = 0;
        x.ChoiceTiming = ChoiceTiming.PlayerTurnStart;
        SetSide(ref x.B, Side.Player);
        if (!takingExtraTurn)
            x.B.Round++;
        x.B.PlayerTurn++;
        // RoundNumber and CurrentSide only clear the lost-HP marks when they change. An extra turn
        // keeps the same round and side, and AdvancePlayerTurn only drops the prediction cache: the
        // engine then answers HasLostHpThisTurn from the immutable root history. Restore exactly the
        // root answer for an extra turn; a normal turn advances the round and clears everything.
        if (takingExtraTurn)
            RestoreRootLostHp(ref x.B, x.R.RootLostHpMask);
        else
            ClearLostHpThisTurn(ref x.B);
        BeginPlayerSideTurn(ref x.B);
        SnapshotPowerAmountsAtTurnStart(ref x.B, ParticipantMask(Limits.PlayerOwner));
        ReadOnlySpan<byte> player = [Limits.PlayerOwner];
        BeforeSideTurnStart(ref x, Side.Player, player);
        if (x.NeedsChoice)
            return;
        ClearBlockAtSideStart(ref x, Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;

        // ContinuePlayerStart. The TurnExecutionContinuation stages from BeforeHand to Finish. The
        // EndTurn path keeps its historical behavior (no relic BeforeHandDraw, no
        // CheckForEmptyHand, ShufflesCrossed accounting on); PreparePlayerPlayPhase passes
        // rootSetup=true for the native SetupPlayerTurn semantics. [GAME CombatManager.cs:903-925]
        x.TakingExtraTurn = takingExtraTurn;
        ContinuePlayerPlayPhase(ref x, firstTurn: false, rootSetup: false);
    }

    /// <summary>
    /// The <c>CombatBeamSolver.TurnExecutionContinuation.ContinuePlayerStart</c> stages from
    /// <c>BeforeHand</c> through <c>Finish</c>: hand-draw preparation, the draw, the
    /// <c>AfterPlayerTurnStart</c> pass, the player side start, deferred enemy deaths, orbs, the
    /// auto-pre-play phase, then <c>Phase = Play</c>.
    /// <paramref name="rootSetup"/> marks a turn-setup preamble built from a native Start root: the
    /// native <c>Hook.BeforeHandDraw</c> relic stage runs on every turn [GAME CombatManager.cs:903],
    /// <c>CheckForEmptyHand</c> runs after <c>Phase = AutoPrePlay</c> and before the auto-plays
    /// [GAME CombatManager.cs:862-869], and <c>ShufflesCrossed</c> stays at the imported root value
    /// (the original engine's <c>rootSetup</c> flag). The default EndTurn path leaves all three
    /// unchanged.
    /// </summary>
    internal static void ContinuePlayerPlayPhase(ref Exec x, bool firstTurn, bool rootSetup,
        SetupPauseState? setup = null)
    {
        // GAME CombatManager.cs:888-900: reset energy (or keep it when a listener says so), then
        // Hook.AfterEnergyReset before Hook.BeforeHandDraw.
        if (HookDispatch.ShouldPlayerResetEnergy(x.B, x.R, Limits.PlayerOwner))
            x.B.Energy = 0;
        // CombatBeamSolver.RoundTransition: GainEnergy(modified max energy + ConsumeEnergyNextTurn).
        int energyNextTurn = ConsumeEnergyNextTurn(ref x.B);
        x.B.Energy = (int)Math.Clamp((long)x.B.Energy + Math.Max(0, ModifiedMaxEnergy(ref x)) + energyNextTurn,
            0, Limits.ValueClamp);
        PowersAfterEnergyReset(ref x);
        if (x.NeedsChoice)
            return;
        RelicPass(ref x, RelicPhase.AfterEnergyReset, Side.Player, snapshot: rootSetup);
        if (x.NeedsChoice)
            return;
        RelicPass(ref x, RelicPhase.AfterEnergyResetLate, Side.Player, snapshot: rootSetup);
        if (x.NeedsChoice)
            return;

        int beforeHandShuffles = x.B.ShuffleEventCount;
        x.B.Phase = PlayerPhase.Start;
        if (rootSetup)
        {
            // GAME Hook.BeforeHandDraw iterates powers, then relics (CombatState.IterateHookListeners).
            PowerBeforeHandDraw(ref x);
            if (x.NeedsChoice)
                return;
            RelicBeforeHandDraw(ref x);
            if (x.NeedsChoice)
                return;
            ReturnCardsToHand(ref x);
            if (x.NeedsChoice)
                return;
        }
        else
        {
            BeforeHandDraw(ref x);
            if (x.NeedsChoice)
                return;
        }
        // CombatBeamSolver.TurnExecutionContinuation resolves BeforeHandDraw's power changes before
        // the pre-draw checkpoint.
        ResolvePowerAmountChanges(ref x);
        if (x.NeedsChoice)
            return;
        if (!rootSetup)
            x.B.ShufflesCrossed += x.B.ShuffleEventCount - beforeHandShuffles;
        // ConsumeModifiedHandDraw: base + the not-yet-snapshotted DrawCardsNextTurn amount, then the
        // listener passes (DrawCardsNextTurnPower.ModifyHandDraw adds its snapshotted amount).
        int drawCount = ModifiedHandDraw(ref x, BaseHandDrawCount + DrawNextTurnNotYetSnapshotted(x.B));
        if (firstTurn)
        {
            // GAME CombatManager.cs:908-924: on turn 1, enchantments that start at the bottom move
            // first, then Innate cards move to the top; the draw is at least the Innate count and at
            // most the hand size.
            drawCount = ApplyFirstTurnDrawOrder(ref x.B, drawCount);
        }
        int effectiveDraw = Math.Min(drawCount, x.B.MaxHandSize - x.B.HandCount);
        bool willShuffle = effectiveDraw > x.B.DrawCount && x.B.DiscardCount != 0;
        Draw(ref x, drawCount, fromHandDraw: true);
        if (willShuffle && !rootSetup)
            x.B.ShufflesCrossed++;
        if (x.NeedsChoice)
            return;
        // Draw history compensation: no supported sources.
        AfterPlayerTurnStart(ref x, snapshotRelics: rootSetup);
        if (x.NeedsChoice)
            return;
        // GAME CombatManager.cs:777-790: StartTurn runs Hook.AfterSideTurnStart after the setup
        // task pauses or completes. When it pauses, the callback in Exec already ran the side start
        // plus the orb turn-start pass at that pause; otherwise run the side start here (the
        // task-completed order). The EndTurn path keeps its fixed position and orb position.
        if (setup != null)
        {
            if (!setup.SideStarted)
                StartSetupSide(ref x, setup, runOrbs: false);
            if (x.NeedsChoice)
                return;
        }
        else
        {
            StartPlayerSideTurn(ref x);
            if (x.NeedsChoice)
                return;
        }
        ApplyEnemyDeathPowers(ref x);
        if (!x.NeedsChoice)
            NormalizeCardAfflictions(ref x);
        // EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs: Plasma's AfterTurnStartOrbTrigger
        // (through TriggerPassive, so ModifyOrbPassiveTriggerCounts applies). A paused setup already
        // ran it at the pause; an unpaused setup keeps the original position after the deferred
        // enemy-death pass and affliction normalization.
        if (setup == null || !setup.OrbsStarted)
        {
            TriggerAfterTurnStartOrbs(ref x);
            if (x.NeedsChoice)
                return;
        }
        // GAME CombatManager.cs:862-869: the native loop sets AutoPrePlay, checks the empty hand,
        // then fires AfterAutoPrePlayPhaseEntered. Sts2Solver has no AfterAutoPrePlayPhaseEntered
        // listener, so the phase transition itself is the only remaining step.
        x.B.Phase = PlayerPhase.AutoPrePlay;
        if (rootSetup)
        {
            // CombatManager.CheckForEmptyHand: in progress, not inside a card/potion effect, and an
            // empty hand. UnceasingTop is the only supported AfterHandEmptied listener.
            AfterHandEmptied(ref x);
            if (x.NeedsChoice)
                return;
        }
        // TriggerAutoPrePlayEarly: Bombardment cards in the exhaust pile, then the scheduled Mayhem
        // auto-plays. The Imbued, HistoryCourse and WhisperingEarring definitions live in Content.
        AutoPrePlay(ref x);
        if (x.NeedsChoice)
            return;
        x.B.Phase = PlayerPhase.Play;
        CheckWinCondition(ref x.B);
    }

    /// <summary>
    /// State of the rootSetup pause callback. The callback runs the player side start and, at a
    /// pause, the orb turn-start pass in the native order before the setup listener resumes.
    /// </summary>
    internal sealed class SetupPauseState
    {
        internal bool SideStarted;
        internal bool OrbsStarted;
        internal bool PauseOccurred;

        internal void OnPause(ref Exec x)
        {
            PauseOccurred = true;
            StartSetupSide(ref x, this, runOrbs: true);
        }
    }

    /// <summary>
    /// Runs the player side start (and optionally the orb turn-start pass) at the native setup
    /// pause. The pending choice request is saved while the side/orb hooks run and restored
    /// afterwards. A side start or orb pass that itself requests a choice cannot be represented by
    /// the single-prefix preamble and is an explicit unsupported boundary.
    /// </summary>
    private static void StartSetupSide(ref Exec x, SetupPauseState state, bool runOrbs)
    {
        if (state.SideStarted)
            return;
        ChoiceRequest pending = x.Request;
        bool needsChoice = x.NeedsChoice;
        int choicesTaken = x.ChoicesTaken;
        SetupPauseHandler? savedPause = x.SetupPause;
        x.NeedsChoice = false;
        x.Request = default;
        x.SetupPause = null;
        state.SideStarted = true;
        try
        {
            StartPlayerSideTurn(ref x);
            if (x.NeedsChoice || x.ChoicesTaken != choicesTaken)
            {
                throw new UnsupportedException(
                    "回合准备的侧回合开始阶段也请求选择；当前前奏不支持并发选择。");
            }
            if (runOrbs)
            {
                state.OrbsStarted = true;
                TriggerAfterTurnStartOrbs(ref x);
                if (x.NeedsChoice || x.ChoicesTaken != choicesTaken)
                {
                    throw new UnsupportedException(
                        "回合准备的球的回合开始触发也请求选择；当前前奏不支持并发选择。");
                }
            }
        }
        finally
        {
            x.SetupPause = savedPause;
        }
        if (needsChoice)
        {
            x.Request = pending;
            x.NeedsChoice = true;
        }
    }

    /// <summary>
    /// Runs the native <c>CombatManager.SetupPlayerTurn</c> and <c>RunAutoPrePlayPhase</c> sequence
    /// from a root captured at the <c>SetupPlayerTurn</c> entry (<c>PlayerTurnPhase.Start</c>).
    /// Energy reset, the relic/power <c>BeforeHandDraw</c> stage, the hand draw, the
    /// <c>AfterPlayerTurnStart</c> pass, the player side start, orbs and the auto-pre-play phase all
    /// match the native order; <paramref name="choices"/> is the prefix of selections for the
    /// requests the preamble asks. Returns <see cref="StepResult.NeedsChoice"/> with the partial
    /// battle when another selection is needed; the caller must discard it and replay from the Start
    /// root with one more selection.
    /// <para>GAME CombatManager.cs:880-925 (SetupPlayerTurn), 862-869 (RunAutoPrePlayPhase).</para>
    /// <para>Taking an extra turn is not supported in this batch: the native turn loop skips part of
    /// the start for <c>PlayersTakingExtraTurn</c>, and guessing the skipped pieces would produce a
    /// different Play root.</para>
    /// </summary>
    public static StepResult PreparePlayerPlayPhase(ref Battle battle, Rules rules,
        ReadOnlySpan<ulong> choices, out ChoiceRequest request, bool takingExtraTurn = false)
    {
        if (takingExtraTurn)
            throw new UnsupportedException("回合准备前奏不支持额外回合。");
        if (!IsInProgress(battle))
            throw new InvalidOperationException("战斗已经结束，不能再准备玩家出牌阶段。");
        if (battle.Phase != PlayerPhase.Start)
            throw new InvalidOperationException(
                $"回合准备前奏要求 Phase=Start，实际 {battle.Phase}。");
        if (choices.Length > Limits.MaxChoices)
            throw new CapacityException("Action.Choices", Limits.MaxChoices);
        PlayerAction action = PlayerAction.EndTurn();
        foreach (ulong selection in choices)
            action = action.WithChoice(selection);
        Exec x = new(ref battle, rules, action);
        x.ChoiceTiming = ChoiceTiming.PlayerTurnStart;
        x.TakingExtraTurn = false;
        SetupPauseState setup = new();
        x.SetupPause = setup.OnPause;
        // GAME CombatManager.cs:906-908: the native turn-one draw order keys off the player's turn
        // number, which is already 1 in the captured Start root.
        ContinuePlayerPlayPhase(ref x, firstTurn: battle.PlayerTurn == 1, rootSetup: true, setup);
        x.SetupPause = null;
        return FinishStep(ref x, action, out request, out _);
    }

    /// <summary>
    /// The player side start, after the hand draw and <c>AfterPlayerTurnStart</c>. Runs once per
    /// action execution at the two call sites that own the normal start position.
    /// </summary>
    internal static void StartPlayerSideTurn(ref Exec x)
    {
        ReadOnlySpan<byte> player = [Limits.PlayerOwner];
        TriggerSideTurnStart(ref x, Side.Player, player, decrementPlating: x.B.PlayerTurn != 1,
            isExtraTurn: x.TakingExtraTurn);
    }

// This batch's turn-boundary relic hooks (HookMaskExt bits 3-6). Each is dispatched at the
// existing engine's position and over the same listener order as the action-hook dispatcher.

/// <summary><c>CorePowerSupport.FlushPlayerHandAtTurnEnd</c>: <c>AfterFlush</c> over the combat listeners.</summary>
internal static void AfterFlushPass(ref Exec x)
{
    if ((Registry.AnyHooksExt & HookMaskExt.AfterFlush) == 0)
        return;
    Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount];
    int count = HookDispatch.CollectListeners(x.B, HookMaskExt.AfterFlush, listeners);
    for (int index = 0; index < count; index++)
    {
        Listener self = listeners[index];
        HookDispatch.Definition(x.B, self).AfterFlush(ref x, self);
        if (x.NeedsChoice)
            return;
    }
}

/// <summary><c>HookMirrors.ShouldTakeExtraTurn</c>: the first listener answering true wins.</summary>
internal static bool ShouldTakeExtraTurnPass(ref Exec x)
{
    if ((Registry.AnyHooksExt & HookMaskExt.ShouldTakeExtraTurn) == 0)
        return false;
    Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount];
    int count = HookDispatch.CollectListeners(x.B, HookMaskExt.ShouldTakeExtraTurn, listeners);
    for (int index = 0; index < count; index++)
    {
        Listener self = listeners[index];
        if (HookDispatch.Definition(x.B, self).ShouldTakeExtraTurn(ref x, self))
            return true;
        if (x.NeedsChoice)
            return false;
    }
    return false;
}

/// <summary><c>HookMirrors.AfterTakingExtraTurn</c>: a fixed listener snapshot, in dispatch order.</summary>
internal static void AfterTakingExtraTurnPass(ref Exec x)
{
    if ((Registry.AnyHooksExt & HookMaskExt.AfterTakingExtraTurn) == 0)
        return;
    Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount];
    int count = HookDispatch.CollectListeners(x.B, HookMaskExt.AfterTakingExtraTurn, listeners);
    for (int index = 0; index < count; index++)
    {
        Listener self = listeners[index];
        HookDispatch.Definition(x.B, self).AfterTakingExtraTurn(ref x, self);
        if (x.NeedsChoice)
            return;
    }
}

/// <summary><c>SimulatedCombatState.RecordRelicRoundDamage</c>: the per-round relic bookkeeping.</summary>
internal static void AfterRelicRoundPass(ref Exec x)
{
    if ((Registry.AnyHooksExt & HookMaskExt.AfterRelicRound) == 0)
        return;
    Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount];
    int count = HookDispatch.CollectListeners(x.B, HookMaskExt.AfterRelicRound, listeners);
    for (int index = 0; index < count; index++)
    {
        Listener self = listeners[index];
        HookDispatch.Definition(x.B, self).AfterRelicRound(ref x, self);
        if (x.NeedsChoice)
            return;
    }
}
}
