namespace Sts2Solver.Simulation.Combat;

/// <summary>A hook invocation: which mask selects listeners and how to call one of them.</summary>
public interface IActionHook
{
    static abstract HookMask Mask { get; }

    void Invoke(ref Exec x, ListenerDef definition, Listener self);
}

/// <summary>Information about the card play an <c>AfterCardPlayed</c> or <c>BeforeCardPlayed</c> listener observes.</summary>
public readonly struct CardPlayInfo
{
    public CardPlayInfo(in Card card, byte target, int playIndex, int playCount, Pile resultPile,
        int position = -1)
    {
        Card = card;
        Target = target;
        PlayIndex = playIndex;
        PlayCount = playCount;
        ResultPile = resultPile;
        Position = position;
    }

    public Card Card { get; }

    public byte Target { get; }

    public int PlayIndex { get; }

    public int PlayCount { get; }

    public Pile ResultPile { get; }

    /// <summary>Absolute position of the played card in <see cref="Battle.Cards"/> while its tail runs.</summary>
    public int Position { get; }

    /// <summary>
    /// Earlier listeners can generate cards or change the played card. Its play-pile slot remains
    /// stable, but its absolute position and value must be read again for each native hook listener.
    /// </summary>
    internal CardPlayInfo Refresh(in Battle battle, int playPosition)
    {
        if ((uint)playPosition >= (uint)battle.PlayCount)
            return new(Card, Target, PlayIndex, PlayCount, ResultPile, -1);
        int position = Piles.Start(battle, Pile.Play) + playPosition;
        return new(battle.Cards[position], Target, PlayIndex, PlayCount, ResultPile, position);
    }
}

public static partial class HookDispatch
{
    private struct SnapshotEntry
    {
        public Listener Listener;
        public ushort Id;

        /// <summary>Card value of a card-attached listener at snapshot time, for re-location.</summary>
        public Card Card;

        /// <summary>Orb value at snapshot time, for re-location.</summary>
        public Orb Orb;

        /// <summary>Index among byte-equal cards (or orbs) at snapshot time, or -1.</summary>
        public int Occurrence;

        /// <summary>Card pile and pile-local index at snapshot time (card-attached listeners).</summary>
        public Pile CardPile;
        public int CardPileIndex;
    }

    /// <summary>
    /// Dispatches an action hook over a snapshot of the listeners taken at the start, mirroring the
    /// existing engine's listener-list enumeration. Each listener is re-resolved before its call
    /// (power slots move when powers are removed); a listener that left in between is skipped.
    /// Stops as soon as an effect needs a choice.
    /// </summary>
    public static void Dispatch<T>(ref Exec x, ref T hook, bool gatedByEnding) where T : struct, IActionHook
    {
        if ((Registry.AnyHooks & T.Mask) == 0 || gatedByEnding && Kernel.IsOverOrEnding(x.B))
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B)];
        int count = CollectListeners(x.B, T.Mask, listeners);
        if (count == 0)
            return;
        Span<SnapshotEntry> snapshot = stackalloc SnapshotEntry[count];
        for (int index = 0; index < count; index++)
            snapshot[index] = Snapshot(x.B, listeners[index]);
        for (int index = 0; index < count; index++)
        {
            if (!TryResolve(x.B, snapshot[index], out Listener current))
                continue;
            InvokeWithChoiceSource(ref x, Definition(x.B, current), current, ref hook);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Power-only action hook dispatch. The existing engine's <c>AfterPowerAmountChanged</c> loop
    /// iterates the current powers (<c>PowerLifecycleSupport.ResolvePowerAmountChanges</c>), not every
    /// combat listener, so the general listener list is filtered to powers while keeping its order.
    /// </summary>
    public static void DispatchPowerHook<T>(ref Exec x, ref T hook) where T : struct, IActionHook
    {
        if ((Registry.AnyHooks & T.Mask) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B)];
        int count = CollectListeners(x.B, T.Mask, listeners);
        if (count == 0)
            return;
        Span<SnapshotEntry> snapshot = stackalloc SnapshotEntry[count];
        int powers = 0;
        for (int index = 0; index < count; index++)
        {
            if (listeners[index].Kind != ListenerKind.Power)
                continue;
            snapshot[powers++] = Snapshot(x.B, listeners[index]);
        }
        for (int index = 0; index < powers; index++)
        {
            if (!TryResolve(x.B, snapshot[index], out Listener current))
                continue;
            if (current.Kind != ListenerKind.Power || x.B.Powers[current.Index].Amount <= 0)
                continue;
            InvokeWithChoiceSource(ref x, Definition(x.B, current), current, ref hook);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Invokes one action-hook listener with its own choice origin, restoring the caller's origin
    /// afterwards so nested auto-plays and listeners keep the innermost source.
    /// </summary>
    private static void InvokeWithChoiceSource<T>(ref Exec x, ListenerDef definition,
        Listener self, ref T hook) where T : struct, IActionHook
    {
        ChoiceSource savedOrigin = x.ChoiceOrigin;
        x.ChoiceOrigin = Kernel.ListenerChoiceSource(x.B, self);
        try
        {
            hook.Invoke(ref x, definition, self);
        }
        finally
        {
            x.ChoiceOrigin = savedOrigin;
        }
    }

    private static SnapshotEntry Snapshot(in Battle battle, Listener listener)
    {
        SnapshotEntry entry = new() { Listener = listener, Id = ListenerId(battle, listener), Occurrence = -1 };
        if (listener.Kind == ListenerKind.Orb)
        {
            entry.Orb = battle.Orbs[listener.Index];
            int orbOccurrence = 0;
            for (int index = 0; index < listener.Index; index++)
            {
                if (OrbEqual(battle.Orbs[index], entry.Orb))
                    orbOccurrence++;
            }
            entry.Occurrence = orbOccurrence;
            return entry;
        }
        if (listener.Kind is not (ListenerKind.Card or ListenerKind.Affliction or ListenerKind.Enchantment))
            return entry;
        entry.Card = battle.Cards[listener.Index];
        int occurrence = 0;
        for (int position = 0; position < listener.Index; position++)
        {
            if (SameListenerCard(battle.Cards[position], entry.Card))
                occurrence++;
        }
        entry.Occurrence = occurrence;
        entry.CardPileIndex = -1;
        foreach (Pile pile in Enum.GetValues<Pile>())
        {
            int start = Piles.Start(in battle, pile);
            int count = Piles.Count(in battle, pile);
            if ((uint)(listener.Index - start) >= (uint)count)
                continue;
            entry.CardPile = pile;
            entry.CardPileIndex = listener.Index - start;
            break;
        }
        return entry;
    }

    private static bool OrbEqual(in Orb left, in Orb right)
        => left.Id == right.Id && left.Aux == right.Aux;

    /// <summary>
    /// Stable identity markers used to relocate a captured card listener. Hooks can change upgrade,
    /// cost, variables and attachments without replacing the card. Deck membership and clone identity
    /// distinguish original and generated copies while their pile positions move.
    /// </summary>
    private static bool SameListenerCard(in Card left, in Card right)
        => left.Id == right.Id && left.IsClone == right.IsClone
            && (left.Flags & CardFlags.DeckCard) == (right.Flags & CardFlags.DeckCard);

    private static ushort ListenerId(in Battle battle, Listener listener) => listener.Kind switch
    {
        ListenerKind.Power => battle.Powers[listener.Index].Id,
        ListenerKind.Relic => battle.Relics[listener.Index].Id,
        ListenerKind.Potion => battle.Potions[listener.Index],
        ListenerKind.Monster => battle.Enemies[listener.Index].MonsterId,
        ListenerKind.Orb => battle.Orbs[listener.Index].Id,
        ListenerKind.Card => battle.Cards[listener.Index].Id,
        ListenerKind.Affliction => battle.Cards[listener.Index].AfflictionId,
        ListenerKind.Enchantment => battle.Cards[listener.Index].EnchantmentId,
        _ => throw new ArgumentOutOfRangeException(nameof(listener), listener.Kind, null),
    };

    private static bool TryResolve(in Battle battle, in SnapshotEntry entry, out Listener current)
    {
        current = entry.Listener;
        // Native combat listener enumeration re-checks owner.IsActiveForHooks as each item is
        // yielded; after a fatal damage listener deactivates the player's hooks, later relics,
        // potions and cards must not run. Player death prevention/revive keeps the flag set.
        if (!PlayerListenerActive(battle, entry.Listener))
            return false;
        switch (entry.Listener.Kind)
        {
            case ListenerKind.Power:
                if (entry.Listener.Index < battle.PowerCount
                    && battle.Powers[entry.Listener.Index].Id == entry.Id
                    && battle.Powers[entry.Listener.Index].Owner == entry.Listener.Owner)
                {
                    return true;
                }
                int found = Kernel.FindPower(battle, entry.Listener.Owner, entry.Id);
                if (found < 0)
                    return false;
                current = new Listener(ListenerKind.Power, (byte)found, entry.Listener.Owner);
                return true;
            case ListenerKind.Monster:
                // The existing engine still calls a monster captured in the listener snapshot.
                return true;
            case ListenerKind.Orb:
                // The queue shifts when an earlier listener channels/evokes. Re-locate the captured orb
                // value by its occurrence among equal orbs, like cards: two orbs with the same value
                // are interchangeable, but the listener must still point at one of them.
                if (entry.Listener.Index < battle.OrbCount && OrbEqual(battle.Orbs[entry.Listener.Index], entry.Orb))
                    return true;
                int occurrence = entry.Occurrence;
                for (int index = 0; index < battle.OrbCount; index++)
                {
                    if (!OrbEqual(battle.Orbs[index], entry.Orb))
                        continue;
                    if (occurrence-- == 0)
                    {
                        current = new Listener(ListenerKind.Orb, (byte)index, entry.Listener.Owner);
                        return true;
                    }
                }
                return false;
            case ListenerKind.Card:
            case ListenerKind.Affliction:
            case ListenerKind.Enchantment:
                // A played card keeps its Play-local slot while generated cards shift the packed
                // prefix. Its old absolute index can point at an identical exhausted copy.
                // Other piles can move within the same callback: Right Hand Hand moves Discard to
                // Hand, leaving later discard cards at their original absolute indices.
                if (entry.CardPile != Pile.Play && entry.Listener.Index < battle.CardCount
                    && ListenerId(battle, entry.Listener) == entry.Id
                    && SameListenerCard(battle.Cards[entry.Listener.Index], entry.Card))
                {
                    return true;
                }
                int located;
                if (TryLocateCardInPile(battle, entry, out located)
                    || TryLocateCard(battle, entry, out located))
                {
                    current = new Listener(entry.Listener.Kind, (ushort)located, entry.Listener.Owner);
                    return true;
                }
                return false;
            default:
                return ListenerId(battle, entry.Listener) == entry.Id;
        }
    }

    private static bool PlayerListenerActive(in Battle battle, in Listener listener)
    {
        if ((battle.Flags & BattleFlags.PlayerHooksActive) != 0)
            return true;
        return listener.Kind switch
        {
            ListenerKind.Power => listener.Owner is not (Limits.PlayerOwner or Limits.OstyOwner),
            ListenerKind.Relic or ListenerKind.Potion or ListenerKind.Card
                or ListenerKind.Affliction or ListenerKind.Enchantment or ListenerKind.Orb => false,
            _ => true,
        };
    }

    /// <summary>Locates the snapshot card at the same pile-local index after insertions in earlier piles.</summary>
    private static bool TryLocateCardInPile(in Battle battle, in SnapshotEntry entry, out int index)
    {
        index = -1;
        if (entry.CardPileIndex < 0)
            return false;
        int start = Piles.Start(in battle, entry.CardPile);
        int count = Piles.Count(in battle, entry.CardPile);
        if ((uint)entry.CardPileIndex >= (uint)count)
            return false;
        int candidate = start + entry.CardPileIndex;
        if (!SameListenerCard(battle.Cards[candidate], entry.Card))
            return false;
        if (ListenerId(battle, new Listener(entry.Listener.Kind, (ushort)candidate, entry.Listener.Owner))
            != entry.Id)
        {
            return false;
        }
        index = candidate;
        return true;
    }

    /// <summary>Locates the snapshot card by its value and occurrence over the whole battle.</summary>
    private static bool TryLocateCard(in Battle battle, in SnapshotEntry entry, out int index)
    {
        int occurrence = entry.Occurrence;
        for (int position = 0; position < battle.CardCount; position++)
        {
            if (!SameListenerCard(battle.Cards[position], entry.Card))
                continue;
            if (occurrence-- == 0)
            {
                index = position;
                return ListenerId(battle, new Listener(entry.Listener.Kind, (ushort)position, entry.Listener.Owner))
                    == entry.Id;
            }
        }
        index = -1;
        return false;
    }
}

// Hook invokers. Each is a value type so dispatch is specialized per hook without allocation.

public struct AfterCurrentHpChangedHook : IActionHook
{
    public byte Creature;
    public decimal Delta;

    public static HookMask Mask => HookMask.AfterCurrentHpChanged;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCurrentHpChanged(ref x, self, Creature, Delta);
}

public struct BeforeDeathHook : IActionHook
{
    public byte Creature;

    public static HookMask Mask => HookMask.BeforeDeath;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.BeforeDeath(ref x, self, Creature);
}

public struct AfterDeathHook : IActionHook
{
    public byte Creature;
    public bool WasRemovalPrevented;

    public static HookMask Mask => HookMask.AfterDeath;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterDeath(ref x, self, Creature, WasRemovalPrevented);
}

public struct AfterBlockBrokenHook : IActionHook
{
    public byte Target;
    public byte Breaker;

    public static HookMask Mask => HookMask.AfterBlockBroken;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterBlockBroken(ref x, self, Target, Breaker);
}

public struct AfterCardDrawnEarlyHook : IActionHook
{
    public int DrawnPosition;
    public bool FromHandDraw;

    public static HookMask Mask => HookMask.AfterCardDrawnEarly;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCardDrawnEarly(ref x, self, DrawnPosition, FromHandDraw);
}

public struct AfterCardDrawnHook : IActionHook
{
    public int DrawnPosition;
    public bool FromHandDraw;

    public static HookMask Mask => HookMask.AfterCardDrawn;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCardDrawn(ref x, self, DrawnPosition, FromHandDraw);
}

public struct AfterAutoPostPlayPhaseEnteredHook : IActionHook
{
    public static HookMask Mask => HookMask.AfterAutoPostPlayPhaseEntered;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterAutoPostPlayPhaseEntered(ref x, self);
}

public struct BeforeSideTurnEndVeryEarlyHook : IActionHook
{
    public Side Side;
    public ushort Participants;

    public static HookMask Mask => HookMask.BeforeSideTurnEndVeryEarly;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.BeforeSideTurnEndVeryEarly(ref x, self, Side, Participants);
}

public struct BeforeSideTurnEndEarlyHook : IActionHook
{
    public Side Side;
    public ushort Participants;

    public static HookMask Mask => HookMask.BeforeSideTurnEndEarly;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.BeforeSideTurnEndEarly(ref x, self, Side, Participants);
}

public struct BeforeSideTurnEndHook : IActionHook
{
    public Side Side;
    public ushort Participants;

    public static HookMask Mask => HookMask.BeforeSideTurnEnd;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.BeforeSideTurnEnd(ref x, self, Side, Participants);
}

public struct BeforeDamageReceivedHook : IActionHook
{
    public byte Target;
    public decimal Amount;
    public ValueProps Props;
    public byte Dealer;
    public bool HasCardSource;
    public Card CardSource;

    public static HookMask Mask => HookMask.BeforeDamageReceived;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.BeforeDamageReceived(ref x, self, Target, Amount, Props, Dealer, HasCardSource, CardSource);
}

public struct AfterDamageReceivedHook : IActionHook
{
    public DamageResult Result;
    public byte Dealer;

    public static HookMask Mask => HookMask.AfterDamageReceived;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterDamageReceived(ref x, self, Result, Dealer);
}

public struct AfterDamageReceivedLateHook : IActionHook
{
    public DamageResult Result;
    public byte Dealer;

    public static HookMask Mask => HookMask.AfterDamageReceivedLate;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterDamageReceivedLate(ref x, self, Result, Dealer);
}

public struct AfterDamageGivenHook : IActionHook
{
    public DamageResult Result;
    public byte Dealer;

    public static HookMask Mask => HookMask.AfterDamageGiven;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterDamageGiven(ref x, self, Result, Dealer);
}

public struct BeforeCardPlayedHook : IActionHook
{
    public CardPlayInfo Play;

    public static HookMask Mask => HookMask.BeforeCardPlayed;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.BeforeCardPlayed(ref x, self, Play.Refresh(x.B, x.PlayPosition));
}

public struct AfterCardPlayedHook : IActionHook
{
    public CardPlayInfo Play;

    public static HookMask Mask => HookMask.AfterCardPlayed;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCardPlayed(ref x, self, Play.Refresh(x.B, x.PlayPosition));
}

public struct AfterCardPlayedLateHook : IActionHook
{
    public CardPlayInfo Play;

    public static HookMask Mask => HookMask.AfterCardPlayedLate;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCardPlayedLate(ref x, self, Play.Refresh(x.B, x.PlayPosition));
}

public struct AfterCardExhaustedHook : IActionHook
{
    public Card Card;
    public bool CausedByEthereal;

    public static HookMask Mask => HookMask.AfterCardExhausted;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCardExhausted(ref x, self, Card, CausedByEthereal);
}

public struct AfterCardDiscardedHook : IActionHook
{
    public Card Card;

    public static HookMask Mask => HookMask.AfterCardDiscarded;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCardDiscarded(ref x, self, Card);
}

public struct AfterBlockGainedHook : IActionHook
{
    public byte Owner;
    public decimal Amount;
    public ValueProps Props;
    public bool FromCard;

    public static HookMask Mask => HookMask.AfterBlockGained;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterBlockGained(ref x, self, Owner, Amount, Props, FromCard);
}

public struct BeforeAttackHook : IActionHook
{
    public byte Attacker;
    public ValueProps Props;

    public static HookMask Mask => HookMask.BeforeAttack;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.BeforeAttack(ref x, self, Attacker, Props);
}

public struct AfterAttackHook : IActionHook
{
    public byte Attacker;

    public static HookMask Mask => HookMask.AfterAttack;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterAttack(ref x, self, Attacker);
}

public struct AfterPowerAmountChangedHook : IActionHook
{
    public ushort PowerId;
    public int Delta;
    public byte Applier;
    public byte Target;

    public static HookMask Mask => HookMask.AfterPowerAmountChanged;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterPowerAmountChanged(ref x, self, PowerId, Delta, Applier, Target);
}

public struct AfterShuffleHook : IActionHook
{
    public byte Shuffler;

    public static HookMask Mask => HookMask.AfterShuffle;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterShuffle(ref x, self, Shuffler);
}

public struct AfterCardEnteredCombatHook : IActionHook
{
    public Pile Pile;
    public int Index;
    public Card Card;

    public static HookMask Mask => HookMask.AfterCardEnteredCombat;

    public readonly void Invoke(ref Exec x, ListenerDef definition, Listener self)
        => definition.AfterCardEnteredCombat(ref x, self, Pile, Index, Card);
}
