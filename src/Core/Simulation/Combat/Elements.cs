using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sts2Solver.Simulation.Combat;

/// <summary>Per-card state flags. Values never encode presentation.</summary>
[Flags]
public enum CardFlags : byte
{
    None = 0,
    ExhaustOnNextPlay = 1 << 0,
    SlyThisTurn = 1 << 1,
    RetainThisTurn = 1 << 2,
    DeckCard = 1 << 3,
    RemovedFromState = 1 << 4,
    CostsX = 1 << 5,
    StarCostsX = 1 << 6,

    /// <summary>
    /// The existing engine's <c>PredictedCard.HasCheckedPowerAfflictionEntry</c>: the one-time
    /// <c>VitalSparkPower</c>/<c>SmoggyPower</c>/<c>GalvanicPower</c> card-entry check has run. Root
    /// cards are marked at import (they were present at the snapshot); cards created after the root
    /// start unmarked, so the next normalization afflicts them once.
    /// </summary>
    PowerAfflictionChecked = 1 << 7,
}

/// <summary>Current keyword set of a card, one bit per native <c>CardKeyword</c>.</summary>
[Flags]
public enum Keywords : byte
{
    None = 0,
    Exhaust = 1 << 0,
    Ethereal = 1 << 1,
    Innate = 1 << 2,
    Unplayable = 1 << 3,
    Retain = 1 << 4,
    Sly = 1 << 5,
    Eternal = 1 << 6,

    /// <summary>
    /// Not a native keyword: this card entered combat after the root snapshot. The existing engine
    /// distinguishes such cards from root cards with <c>SimulatedCombatState._liveCardsAtSnapshot</c>;
    /// the one-time Vital Spark/Smog/Galvanic entry check applies only to these cards.
    /// </summary>
    CreatedAfterRoot = 1 << 7,
}

/// <summary>Native <c>LocalCostType</c>.</summary>
public enum CostType : byte
{
    None = 0,
    Absolute = 1,
    Relative = 2,
}

/// <summary>Native <c>LocalCostModifierExpiration</c>; values match the native enum.</summary>
public enum CostExpiration : byte
{
    EndOfCombat = 0,
    EndOfTurn = 2,
    WhenPlayed = 4,

    /// <summary><c>LocalCostModifierExpiration.EndOfTurn | WhenPlayed</c> (the native "this turn or until played").</summary>
    EndOfTurnOrWhenPlayed = EndOfTurn | WhenPlayed,
}

/// <summary>One native <c>LocalCostModifier</c> packed into four bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CostModifier
{
    public short Amount;
    public CostType Type;

    /// <summary>Low three bits: <see cref="CostExpiration"/>; bit 7: reduce-only.</summary>
    public byte Packed;

    public readonly CostExpiration Expiration => (CostExpiration)(Packed & 0x07);

    public readonly bool IsReduceOnly => (Packed & 0x80) != 0;

    public static CostModifier Create(int amount, CostType type, CostExpiration expiration, bool reduceOnly)
        => new()
        {
            Amount = checked((short)amount),
            Type = type,
            Packed = (byte)((byte)expiration | (reduceOnly ? 0x80 : 0)),
        };
}

[InlineArray(Limits.CardEnergyCostModifiers)]
public struct CostModifiers
{
    private CostModifier _first;
}

/// <summary>
/// The complete value of one card in a combat pile. Cards have no instance identity: two
/// cards with equal bytes are interchangeable. Size is asserted to be 92 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Card
{
    private const byte TemporaryStarPresentBit = 0x01;
    private const byte TemporaryStarClearsAtTurnEndBit = 0x02;
    private const byte TemporaryStarClearsWhenPlayedBit = 0x04;
    private const byte CloneBit = 0x08;
    private const byte ReturnToHandNextTurnBit = 0x10;
    private const int EnchantmentStatusShift = 5;

    public ushort Id;
    public byte Upgrade;
    public CardFlags Flags;
    public Keywords Keywords;
    public sbyte BaseCost;
    public sbyte BaseStarCost;
    public byte ReplayCount;
    public CostModifiers EnergyModifiers;

    /// <summary>
    /// Bit 0: temporary star cost present; bit 1: it clears at turn end; bit 2: it clears when
    /// played; bit 3: <see cref="IsClone"/>; bit 4: <see cref="ReturnToHandNextTurn"/>; bits 5-7:
    /// native enchantment status. The energy modifier count has its own byte so the capacity is not
    /// limited to the two bits the packed state used to have.
    /// </summary>
    public byte CostState;

    /// <summary>Low five bits: energy modifier count. Bits 5-6: HistoryCourse source membership; bit 7: native IsDupe.</summary>
    public byte ModifierCount;

    public sbyte TemporaryStarCost;

    /// <summary>
    /// The stack of temporary star-cost entries older than the current one. Bits 0-1 hold the older
    /// entry count (0..2); each older entry uses two bits starting at bit 2 for its clear flags
    /// (bit 0 = clears at turn end, bit 1 = clears when played). All entries share
    /// <see cref="TemporaryStarCost"/>: the native bookkeeping keeps a list, but the only vanilla
    /// sources in scope set the same cost (normally 0) and differ only in expiry. A different cost on
    /// top of an existing entry is an explicit capacity boundary, as before.
    /// </summary>
    public byte TemporaryStarCostStack;

    public byte EnchantmentId;
    public byte AfflictionId;
    public short EnchantmentAmount;
    public short EnchantmentPrivate;
    public short AfflictionAmount;

    /// <summary>Semantic dynamic-variable deltas relative to the rule table's base values.</summary>
    public short Var0;
    public short Var1;

    /// <summary>Card-private counter (Claw, Rampage, GeneticAlgorithm and similar).</summary>
    public short Private;

    /// <summary>
    /// While MagiKnight's DampenPower has the card downgraded, the upgrade level before the downgrade
    /// plus one (0 = not dampened). Restoring the caster's death re-applies that many upgrades.
    /// </summary>
    public byte DampenOriginalUpgrade;

    /// <summary>
    /// One-action identity mark used by BeatDown to follow a card object after an earlier auto-play
    /// moves it out of the discard pile. 0 means unmarked. The field occupies existing tail padding;
    /// card size stays 92 bytes. BeatDown clears every mark before its OnPlay returns, so a stable
    /// boundary never contains a nonzero value. Gameplay clones and generated cards start at 0.
    /// </summary>
    public byte TransientMark;

    /// <summary>
    /// Whether this card occupies a <see cref="Battle.DetachedCards"/> slot. Card id 0 is a valid
    /// content row, so the native table cannot use <see cref="Id"/> alone as the empty sentinel.
    /// A detached card has passed the one-time power-affliction entry check; the kernel keeps that
    /// bit set on the detached value (imported cards and <c>CloneCard</c> snapshots both reach the
    /// slot through that fact), while an unused slot is all zero.
    /// </summary>
    public readonly bool IsDetachedCardPresent
        => Id != 0 || (Flags & CardFlags.PowerAfflictionChecked) != 0;

    /// <summary>1 = current turn's last attack; 2 = previous turn during AutoPrePlay; 3 = both.</summary>
    public byte HistoryCourseRole
    {
        readonly get => (byte)((ModifierCount >> 5) & 3);
        set => ModifierCount = (byte)((ModifierCount & 0x9f) | (value << 5));
    }

    /// <summary>Native IsDupe; CreateClone preserves it, including AdaptiveStrike's generated clone.</summary>
    public bool IsDupe
    {
        readonly get => (ModifierCount & 0x80) != 0;
        set => ModifierCount = (byte)((ModifierCount & 0x7f) | (value ? 0x80 : 0));
    }

    public int EnergyModifierCount
    {
        readonly get => ModifierCount & 0x1f;
        set
        {
            if ((uint)value > Limits.CardEnergyCostModifiers)
                throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
            ModifierCount = (byte)((ModifierCount & 0xe0) | value);
        }
    }

    /// <summary>
    /// Native <c>CardModel.IsClone</c>. Clones skip the card-specific part of
    /// <c>AfterCardEnteredCombat</c> (Stomp's cost reduction and similar); the bit lives in the spare
    /// bit 3 of <c>CostState</c>, which the temporary-star and enchantment setters preserve.
    /// </summary>
    public bool IsClone
    {
        readonly get => (CostState & CloneBit) != 0;
        set => CostState = (byte)((CostState & ~CloneBit) | (value ? CloneBit : 0));
    }

    /// <summary>
    /// Native <c>SimulatedCombatState._returnToHandNextTurn</c> membership (Bolas and ThrummingHatchet):
    /// the card goes back to hand at the next player turn's BeforeHandDraw. The existing engine keeps
    /// the played card object in a set; Sts2Solver carries the same membership on the card value.
    /// </summary>
    public bool ReturnToHandNextTurn
    {
        readonly get => (CostState & ReturnToHandNextTurnBit) != 0;
        set => CostState = (byte)((CostState & ~ReturnToHandNextTurnBit) | (value ? ReturnToHandNextTurnBit : 0));
    }

    private const int TemporaryStarHistoryShift = 2;
    private const byte TemporaryStarHistoryMask = 0x03;

    /// <summary>Number of temporary star-cost entries, current plus older ones (0..3).</summary>
    public readonly int TemporaryStarCostCount
        => HasTemporaryStarCost ? 1 + (TemporaryStarCostStack & TemporaryStarHistoryMask) : 0;

    public readonly bool HasTemporaryStarCost => (CostState & TemporaryStarPresentBit) != 0;

    public readonly bool TemporaryStarCostClearsAtTurnEnd => (CostState & TemporaryStarClearsAtTurnEndBit) != 0;

    public readonly bool TemporaryStarCostClearsWhenPlayed => (CostState & TemporaryStarClearsWhenPlayedBit) != 0;

    /// <summary>
    /// Native <c>CardModel.SetTemporaryStarCost</c>: append 0/short-lived entries. CurrentStarCost
    /// reads the last one; the earlier entries stay until their own cleanup removes them.
    /// </summary>
    public void SetTemporaryStarCost(int cost, bool clearsAtTurnEnd, bool clearsWhenPlayed)
    {
        int history = HasTemporaryStarCost ? TemporaryStarCostStack & TemporaryStarHistoryMask : 0;
        int count = HasTemporaryStarCost ? 1 + history : 0;
        if (count > 0 && TemporaryStarCost != checked((sbyte)cost))
        {
            throw new CapacityException("Card.TemporaryStarCosts", 2);
        }
        // Identical entries behave exactly like one entry: CurrentStarCost reads the last one and
        // both cleanup passes remove the whole expiration class. Collapse them so a stack of the
        // same free effect cannot consume the small history.
        if (count > 0 && TemporaryStarCostClearsAtTurnEnd == clearsAtTurnEnd
            && TemporaryStarCostClearsWhenPlayed == clearsWhenPlayed)
        {
            TemporaryStarCost = checked((sbyte)cost);
            return;
        }
        if (count >= 3)
            throw new CapacityException("Card.TemporaryStarCosts", 3);
        if (count > 0)
        {
            byte flags = (byte)((TemporaryStarCostClearsAtTurnEnd ? 1 : 0)
                | (TemporaryStarCostClearsWhenPlayed ? 2 : 0));
            int shift = TemporaryStarHistoryShift + history * 2;
            TemporaryStarCostStack = (byte)((TemporaryStarCostStack & ~(3 << shift)) | (flags << shift));
            TemporaryStarCostStack = (byte)((TemporaryStarCostStack & ~TemporaryStarHistoryMask)
                | (history + 1));
        }
        TemporaryStarCost = checked((sbyte)cost);
        CostState = (byte)((CostState & ~(TemporaryStarPresentBit | TemporaryStarClearsAtTurnEndBit | TemporaryStarClearsWhenPlayedBit))
            | TemporaryStarPresentBit
            | (clearsAtTurnEnd ? TemporaryStarClearsAtTurnEndBit : 0)
            | (clearsWhenPlayed ? TemporaryStarClearsWhenPlayedBit : 0));
    }

    /// <summary>
    /// Mirrors <c>CardModel.EndOfTurnCleanup</c>/<c>AfterCardPlayedCleanup</c> for the temporary star
    /// cost list: remove every entry whose clear flag matches and preserve the rest in order.
    /// </summary>
    public void RemoveTemporaryStarCosts(bool atTurnEnd)
    {
        if (!HasTemporaryStarCost)
            return;
        Span<byte> flags = stackalloc byte[3];
        int count = TemporaryStarCostCount;
        flags[0] = (byte)((TemporaryStarCostClearsAtTurnEnd ? 1 : 0)
            | (TemporaryStarCostClearsWhenPlayed ? 2 : 0));
        for (int index = 1; index < count; index++)
        {
            int shift = TemporaryStarHistoryShift + (index - 1) * 2;
            flags[index] = (byte)((TemporaryStarCostStack >> shift) & 0x03);
        }
        byte mask = atTurnEnd ? (byte)1 : (byte)2;
        int kept = 0;
        for (int index = 0; index < count; index++)
        {
            if ((flags[index] & mask) == 0)
                flags[kept++] = flags[index];
        }
        if (kept == 0)
        {
            ClearTemporaryStarCost();
            return;
        }
        CostState = (byte)((CostState & ~(TemporaryStarClearsAtTurnEndBit | TemporaryStarClearsWhenPlayedBit))
            | ((flags[0] & 1) != 0 ? TemporaryStarClearsAtTurnEndBit : 0)
            | ((flags[0] & 2) != 0 ? TemporaryStarClearsWhenPlayedBit : 0));
        TemporaryStarCostStack = (byte)(kept - 1);
        for (int index = 1; index < kept; index++)
        {
            int shift = TemporaryStarHistoryShift + (index - 1) * 2;
            TemporaryStarCostStack = (byte)((TemporaryStarCostStack & ~(3 << shift)) | (flags[index] << shift));
        }
    }

    public void ClearTemporaryStarCost()
    {
        TemporaryStarCost = 0;
        TemporaryStarCostStack = 0;
        CostState = (byte)(CostState & ~(TemporaryStarPresentBit | TemporaryStarClearsAtTurnEndBit | TemporaryStarClearsWhenPlayedBit));
    }

    public byte EnchantmentStatus
    {
        readonly get => (byte)(CostState >> EnchantmentStatusShift);
        set
        {
            if (value > 7)
                throw new UnsupportedException($"附魔状态值 {value} 超出 3 位编码。");
            CostState = (byte)((CostState & 0x1F) | (value << EnchantmentStatusShift));
        }
    }

    public readonly bool HasKeyword(Keywords keyword) => (Keywords & keyword) != 0;
}

/// <summary>Creature vitals shared by the player, Osty and enemies.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Creature
{
    public int CurrentHp;
    public int MaxHp;
    public int Block;
    public CreatureFlags Flags;
    private byte _padding0;
    private short _padding1;

    public readonly bool IsAlive => CurrentHp > 0;

    public readonly bool IsDead => CurrentHp <= 0;
}

[Flags]
public enum CreatureFlags : byte
{
    None = 0,
    Present = 1 << 0,

    /// <summary>Took unblocked damage in the current side turn (<c>SimulatedCombatState._unblockedDamageThisTurn</c>).</summary>
    LostHpThisTurn = 1 << 1,
}

/// <summary>Native <c>SimulatedCombatState</c> death phase of an enemy.</summary>
public enum DeathPhase : byte
{
    None = 0,
    Reviving = 1,
    PermanentlyDead = 2,
}

/// <summary>
/// Waterfall Giant's <c>SimulatedCombatState._steamEruptionPhases</c> entry. Zero means no forced
/// Steam Eruption action; the two values are the forced <c>ABOUT_TO_BLOW_MOVE</c> and
/// <c>EXPLODE_MOVE</c> phases (<c>SimulatedCombatState.TryConsumeForcedMonsterMove</c>).
/// </summary>
public enum SteamPhase : byte
{
    None = 0,
    AboutToBlow = 1,
    Explode = 2,
}

[Flags]
public enum EnemyFlags : byte
{
    None = 0,
    InCombat = 1 << 0,
    NeedsInitialRoll = 1 << 1,
    SkipNextMove = 1 << 2,
    Escaped = 1 << 3,
    PerformedMoveThisRound = 1 << 4,

    /// <summary>Its death was handled by the deferred enemy-death pass (solver <c>processedEnemyDeaths</c>).</summary>
    DeathProcessed = 1 << 5,

    /// <summary>
    /// A forced must-perform-once node replaced the current move after this enemy already performed this
    /// round (<c>SimulatedCombatState.MonsterAi.cs:122-126</c>: <c>performedMove</c> is the old move, so
    /// the machine must not advance). Consumed and cleared by
    /// <see cref="Kernel.PrepareMonsterMovesForNextRound"/>; zero at every stable boundary.
    /// </summary>
    ForcedMustPerformAfterMove = 1 << 6,

    /// <summary>
    /// <see cref="Kernel.ExecuteMonsterMove"/> is running. A forced must-perform node set while this
    /// is on replaced the move currently being performed; the external performed flag is only set after
    /// the action returns (<c>CombatBeamSolver.Expansion.Replay.cs:1164</c>). Zero at every stable boundary.
    /// </summary>
    ActingThisRound = 1 << 7,
}

[InlineArray(Limits.EnemyRecentMoves)]
public struct RecentMoves
{
    private byte _first;
}

[InlineArray(Limits.EnemyAiRegisters)]
public struct AiRegisters
{
    private int _first;
}

/// <summary>
/// One known enemy. Enemies are never removed from the array; <see cref="EnemyFlags.InCombat"/>
/// tracks roster membership so power owners stay stable.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Enemy
{
    public Creature Creature;
    public ushort MonsterId;
    public byte Slot;
    public DeathPhase DeathPhase;
    public uint CombatId;

    /// <summary>Current move node in the compiled state machine of this enemy.</summary>
    public byte Move;

    public EnemyFlags Flags;

    /// <summary>Total moves logged, saturating at 255.</summary>
    public byte LoggedMoves;

    /// <summary>
    /// Dynamic follow-up of the synthetic STUNNED node (the <c>FollowUpStateId</c> of the native
    /// <c>MoveState("STUNNED")</c> created by <c>SimulatedCombatState.ForceStunnedMove</c>), or 255 when
    /// <see cref="Move"/> is not the STUNNED node. Reusing this slot keeps the layout unchanged.
    /// </summary>
    public byte ForcedMove;

    /// <summary>Index into <see cref="Rules"/>'s monster table (<c>Rules.MonsterRule</c>).</summary>
    public byte Rules;

    /// <summary>Low byte of <see cref="PoweredAttackHitsThisTurn"/>.</summary>
    internal byte PoweredAttackHitsLow;

    /// <summary>
    /// Waterfall Giant's forced Steam Eruption phase (<c>SimulatedCombatState._steamEruptionPhases</c>).
    /// Stored in the former padding byte; zero for every other monster and while no eruption is armed.
    /// </summary>
    public SteamPhase SteamPhase;

    /// <summary>
    /// High byte of <see cref="PoweredAttackHitsThisTurn"/>, stored in the former padding byte
    /// (zero in data written before the counter was widened, so those values decode unchanged).
    /// </summary>
    internal byte PoweredAttackHitsHigh;

    /// <summary>
    /// Powered attack hits dealt to this enemy by the player this turn
    /// (<c>SimulatedCombatState.GetPoweredAttackHitsThisTurn(player, enemy)</c>): BeatIntoShape's
    /// calculated-forge multiplier. Imported for the root turn and reset for every enemy in
    /// <c>BeginSideTurn(player)</c>. The game's counter is an int; a damage loop the turn-loop
    /// guard lets run until the enemy dies can exceed 255 hits, so it is kept in 16 bits across
    /// the two bytes above, and a hit beyond 65535 is an explicit boundary.
    /// </summary>
    public int PoweredAttackHitsThisTurn
    {
        readonly get => PoweredAttackHitsLow | (PoweredAttackHitsHigh << 8);
        set
        {
            ushort hits = checked((ushort)value);
            PoweredAttackHitsLow = (byte)hits;
            PoweredAttackHitsHigh = (byte)(hits >> 8);
        }
    }

    /// <summary>Bit i set when move node i has ever been logged (native "use only once" rule).</summary>
    public ulong EverUsedMoves;

    /// <summary>Most recent logged move nodes, newest last; see <see cref="LoggedMoves"/>.</summary>
    public RecentMoves RecentMoves;

    /// <summary>Monster-private integers, numbered by each monster definition.</summary>
    public AiRegisters Ai;
}

[Flags]
public enum PowerFlags : byte
{
    None = 0,
    SkipNextDurationTick = 1 << 0,
    WasJustAppliedByEnemy = 1 << 1,

    /// <summary>Solver-side skip set by <c>ApplyFromMonster</c> for powers without the native duration skip.</summary>
    SkipNextTickFromMonster = 1 << 2,

    /// <summary>
    /// The native mirror read this power's amount through <c>PowerAmountPredictionState</c> during the
    /// current action. <c>SynchronizePowerAmountPredictionStates</c> at the action boundary restores
    /// the model amount from that entry, so a death cleanup that zeroed the model is undone. Transient:
    /// <c>Kernel.Step</c> clears it at every stable boundary.
    /// </summary>
    PendingAmountState = 1 << 3,
}

/// <summary>
/// One power instance. The power table is kept grouped by <see cref="Owner"/> in ascending
/// owner order; within an owner, entries are in acquisition order.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Power
{
    public ushort Id;
    public byte Owner;
    public byte Applier;
    public byte Target;
    public PowerFlags Flags;
    public short Aux2;
    public int Amount;
    public int AmountOnTurnStart;
    public int Aux0;
    public int Aux1;
}

[Flags]
public enum RelicFlags : byte
{
    None = 0,
    Melted = 1 << 0,
    UsedThisCombat = 1 << 1,
}

[StructLayout(LayoutKind.Sequential)]
public struct Relic
{
    public ushort Id;
    public RelicFlags Flags;
    private byte _padding;
    public int Counter;
    public int Aux;
}

[StructLayout(LayoutKind.Sequential)]
public struct Orb
{
    public byte Id;
    private byte _padding0;
    private short _padding1;
    public int Aux;
}

[InlineArray(Limits.Cards)]
public struct CardArray
{
    private Card _first;
}

/// <summary>Selected-card values of instanced powers (NightmarePower); a zeroed slot is free.</summary>
[InlineArray(Limits.DetachedCards)]
public struct DetachedCardArray
{
    private Card _first;
}

/// <summary>
/// Exact decimal card-variable deltas referenced by <see cref="Card.Private"/> (slot + 1).
/// Canonicalized in detached-card then card storage order at every stable boundary.
/// </summary>
[InlineArray(Limits.DecimalCardVars)]
public struct DecimalVarArray
{
    private decimal _first;
}

[InlineArray(Limits.Enemies)]
public struct EnemyArray
{
    private Enemy _first;
}

[InlineArray(Limits.Enemies)]
public struct Roster
{
    private byte _first;
}

[InlineArray(Limits.Powers)]
public struct PowerArray
{
    private Power _first;
}

[InlineArray(Limits.Relics)]
public struct RelicArray
{
    private Relic _first;
}

[InlineArray(Limits.PotionSlots)]
public struct PotionSlots
{
    private ushort _first;
}

[InlineArray(Limits.Orbs)]
public struct OrbArray
{
    private Orb _first;
}

[InlineArray(Limits.Counters)]
public struct CounterArray
{
    private int _first;
}
