using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sts2Solver.Simulation.Combat;

public enum PlayerActionKind : byte
{
    PlayCard = 0,
    UsePotion = 1,
    EndTurn = 2,
}

[InlineArray(Limits.MaxChoices)]
public struct ChoiceSelections
{
    private ulong _first;
}

/// <summary>
/// One player action plus every choice its execution needs, as bit sets over the option list each
/// choice request produces. Cards are referenced by hand position, never by identity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlayerAction
{
    public PlayerActionKind Kind;
    public byte HandIndex;

    /// <summary>Target owner, or <see cref="Limits.NoOwner"/>.</summary>
    public byte Target;

    public byte PotionSlot;
    public byte ChoiceCount;
    private byte _padding0;
    private short _padding1;
    public ChoiceSelections Choices;

    public static PlayerAction PlayCard(int handIndex, byte target)
        => new() { Kind = PlayerActionKind.PlayCard, HandIndex = checked((byte)handIndex), Target = target, PotionSlot = byte.MaxValue };

    public static PlayerAction UsePotion(int slot, byte target)
        => new() { Kind = PlayerActionKind.UsePotion, HandIndex = byte.MaxValue, Target = target, PotionSlot = checked((byte)slot) };

    public static PlayerAction EndTurn()
        => new() { Kind = PlayerActionKind.EndTurn, HandIndex = byte.MaxValue, Target = Limits.NoOwner, PotionSlot = byte.MaxValue };

    public readonly PlayerAction WithChoice(ulong selection)
    {
        if (ChoiceCount >= Limits.MaxChoices)
            throw new CapacityException("Action.Choices", Limits.MaxChoices);
        PlayerAction next = this;
        next.Choices[next.ChoiceCount] = selection;
        next.ChoiceCount++;
        return next;
    }

    public override readonly string ToString() => Kind switch
    {
        PlayerActionKind.PlayCard => $"Play(hand={HandIndex}, target={Target}, choices={ChoiceCount})",
        PlayerActionKind.UsePotion => $"Potion(slot={PotionSlot}, target={Target}, choices={ChoiceCount})",
        _ => $"EndTurn(choices={ChoiceCount})",
    };
}

public enum StepResult : byte
{
    /// <summary>The action ran to a stable boundary; the battle may now be over.</summary>
    Completed = 0,

    /// <summary>
    /// The action needs a choice it did not carry. The battle is left in an unspecified partial
    /// state and must be discarded; re-run the action from the parent with the choice appended.
    /// </summary>
    NeedsChoice = 1,
}

public enum ChoiceKind : byte
{
    None = 0,
    SelectCards = 1,

    /// <summary>Pick one of a fixed option list (Knowledge Demon's curse cards).</summary>
    SelectOption = 2,
}

/// <summary>What the selected cards undergo; mirrors the supported <c>PlanChoiceEffect</c> values.</summary>
public enum ChoiceEffect : byte
{
    MoveToHand,
    MoveToDrawTop,
    Discard,
    Exhaust,
    Upgrade,

    /// <summary>Discard the selected hand cards, then draw as many (GamblersBrew).</summary>
    DiscardAndDraw,

    /// <summary>Move the selected cards to hand and make them free this turn (LiquidMemories).</summary>
    MoveToHandFreeThisTurn,

    /// <summary>Make the selected hand cards free for the rest of combat (TouchOfInsanity).</summary>
    SetFreeThisCombat,

    /// <summary>Pick one generated option and add a free-this-turn clone to hand (Discovery, Splash).</summary>
    GenerateToHand,

    /// <summary>Replace the selected cards with random cards from the transform pool (Entropy).</summary>
    Transform,

    /// <summary>Add copies of the selected card to hand (Dual Wield, Heirloom Hammer).</summary>
    Duplicate,

    /// <summary>Auto-play the selected card a number of times (Decisions, Decisions).</summary>
    AutoPlayRepeated,

    /// <summary>Give the selected cards single-turn Sly (<c>CardCmd.ApplySingleTurnSly</c>, Hand Trick).</summary>
    ApplySly,

    /// <summary>Add the local Retain keyword to the selected cards (<c>CardCmd.ApplyKeyword</c>, Snap).</summary>
    ApplyRetain,

    /// <summary>Add the local Ethereal keyword to the selected cards (<c>CardCmd.ApplyKeyword</c>, SculptingStrike).</summary>
    ApplyEthereal,

    /// <summary>Select one hand card and store a clone as NightmarePower's selected card.</summary>
    Nightmare,

    /// <summary>Modify the selected cards in place (Transfigure's cost and replay count).</summary>
    Modify,

    /// <summary>
    /// Knowledge Demon's CURSE_OF_KNOWLEDGE: the selected card option applies its curse power to the
    /// player and advances the demon's <c>_curseOfKnowledgeCounter</c>.
    /// </summary>
    ApplyKnowledgeCurse,
}

/// <summary>Phase the choice belongs to; mirrors <c>PlanChoiceTiming</c>.</summary>
public enum ChoiceTiming : byte
{
    Action,
    PlayerTurnEnd,
    EnemyTurn,
    PlayerTurnStart,
}

/// <summary>
/// Host-only identity of the relic, power, card or potion that asked for a choice. It is transient
/// display metadata: it never enters <see cref="Battle"/>, the D1 request encoding, SREQ or SRES.
/// Mirrors CombatSolver's <c>PlanCardChoice.SourceId</c>/<c>ChoiceRequest.SourceId</c> at 5c773caa.
/// </summary>
public enum ChoiceSourceKind : byte
{
    None = 0,
    Relic = 1,
    Power = 2,
    Card = 3,
    Potion = 4,
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct ChoiceSource
{
    public ChoiceSource(ChoiceSourceKind kind, ushort id, byte upgrade = 0)
    {
        Kind = kind;
        Id = id;
        Upgrade = upgrade;
    }

    public readonly ChoiceSourceKind Kind;

    /// <summary>Registry id of the source, or zero for <see cref="ChoiceSourceKind.None"/>.</summary>
    public readonly ushort Id;

    /// <summary>Card upgrade level when <see cref="Kind"/> is <see cref="ChoiceSourceKind.Card"/>.</summary>
    public readonly byte Upgrade;

    public readonly bool IsNone => Kind == ChoiceSourceKind.None;
}

[InlineArray(Limits.MaxChoiceOptions)]
public struct ChoiceOptions
{
    private byte _first;
}

[InlineArray(Limits.MaxGeneratedChoiceOptions)]
public struct GeneratedChoiceOptions
{
    private Card _first;
}

/// <summary>
/// Option list of a choice the kernel could not resolve from the action. Options are absolute
/// positions in <see cref="Battle.Cards"/> of the (partial) battle at the time of the request,
/// in the existing engine's option order; selections are bit sets over this list. Generated-card
/// choices (the existing engine's <c>PlanChoiceEffect.GenerateToHand</c>) have no pile, so their
/// options are carried by value in <see cref="GeneratedOptions"/> instead.
/// </summary>
public struct ChoiceRequest
{
    public ChoiceKind Kind;
    public ChoiceEffect Effect;
    public ChoiceTiming Timing;
    public Pile Source;

    /// <summary>True for a generated-card choice, whose existing spec has <c>PileType.None</c>.</summary>
    public bool SourceIsNone;

    /// <summary>True when <see cref="GeneratedOptions"/> holds the options instead of pile positions.</summary>
    public bool OptionsAreGenerated;

    public byte OptionCount;

    /// <summary>Selection size bounds, already clamped to the option count as the existing engine does.</summary>
    public byte MinCount;
    public byte MaxCount;

    /// <summary>
    /// Host-only origin of the request, used by the panel's turn-start capsules. Never encoded and
    /// never part of a search or replay equivalence key.
    /// </summary>
    public ChoiceSource Origin;
    public ChoiceOptions Options;
    public GeneratedChoiceOptions GeneratedOptions;
}

/// <summary>
/// One queued power amount change (<c>SimulatedPowerAmountChange</c>): the changed power id, the
/// applied delta and the applier. Only transient between <see cref="Kernel.ApplyPower"/> and the
/// next <see cref="Kernel.ResolvePowerAmountChanges"/>; a stable boundary has none queued.
/// </summary>
public struct PowerAmountChange
{
    public ushort PowerId;
    public byte Applier;
    public byte Target;
    public int Delta;
}

[InlineArray(Limits.PendingPowerAmountChanges)]
public struct PowerAmountChanges
{
    private PowerAmountChange _first;
}

/// <summary>Which <c>AfterCardPlayed</c> listener snapshotted the amount at <c>BeforeCardPlayed</c>.</summary>
public enum CardPlayPairKind : byte
{
    /// <summary><c>AfterimagePower</c>: the power amount becomes Unpowered block after the play.</summary>
    AfterimageBlock = 0,

    /// <summary><c>StranglePower</c>: the power amount is dealt to its owner after the play.</summary>
    StrangleDamage = 1,

    /// <summary><c>StormPower</c>: the power amount becomes Lightning channels after a Power play.</summary>
    StormLightning = 2,

    /// <summary><c>SubroutinePower</c>: the power amount becomes energy after a Power play.</summary>
    SubroutineEnergy = 3,

    /// <summary><c>OblivionPower</c>: the power amount at play start becomes Doom on its owner after the play.</summary>
    OblivionDoom = 4,

    /// <summary><c>SerpentFormPower</c>: the power amount at play start becomes Unpowered damage after the play.</summary>
    SerpentFormDamage = 5,

    /// <summary><c>CalamityPower</c>: the power amount at play start generates that many Attacks after the play.</summary>
    CalamityAttacks = 6,
}

/// <summary>
/// One <c>BeforeCardPlayed</c> amount snapshot, keyed by the play index it belongs to (the existing
/// engine keys <c>CardPlayPairPredictionState</c> by the <c>CardPlay</c> object).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CardPlayPair
{
    public ushort PowerId;
    public byte Owner;
    public CardPlayPairKind Kind;
    public int Amount;
}

[InlineArray(Limits.CardPlayPairs)]
public struct CardPlayPairs
{
    private CardPlayPair _first;
}

/// <summary>
/// One MonologuePower instance whose Strength is due after the current card play
/// (<c>CorePowerSupport</c>'s <c>CapturePendingMonologues</c>). The occurrence addresses the instance
/// after rows shift; the strength is the value read before the card's effects.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MonologuePending
{
    public byte Occurrence;
    public short Strength;
}

[InlineArray(Limits.MonologuePending)]
public struct MonologuePendingArray
{
    private MonologuePending _first;
}

/// <summary>Temporary Power ids doubled by UnsettlingLamp in the current card source scope.</summary>
[InlineArray(Limits.LampInternalPowerTypes)]
public struct LampInternalPowerTypes
{
    private ushort _first;
}

/// <summary>
/// Stack-only execution context of one kernel step. Holds the battle by reference and the
/// transient facts of the current action; nothing here survives the step.
/// </summary>
public delegate void SetupPauseHandler(ref Exec x);

public ref struct Exec
{
    public ref Battle B;
    public readonly Rules R;

    /// <summary>
    /// Turn-setup preamble callback. It runs at the first choice request (or when the first carried
    /// selection is consumed) so the player side start and the orb turn-start pass happen while the
    /// native setup task is paused. Null for every ordinary action.
    /// </summary>
    internal SetupPauseHandler? SetupPause;

    /// <summary>
    /// This action replays the native <c>SetupPlayerTurn</c> preamble. Unlike an ordinary action or
    /// the EndTurn path, a pile-based choice must run <see cref="RunSetupPause"/> before it reads its
    /// candidate list, because the native <c>CardSelectCmd</c> entry signals the pause first and
    /// only then builds the candidates. <see cref="SetupPause"/> may already be null when a later
    /// choice in the same preamble reads its candidates; this flag keeps the preamble semantics.
    /// </summary>
    internal bool SetupPreamble;

    /// <summary>
    /// Innermost listener/card/potion invoking an effect. A choice request created while this is set
    /// copies it into <see cref="ChoiceRequest.Origin"/>; callers save and restore it around nested
    /// effects so an inner source wins. Transient; empty at every stable boundary.
    /// </summary>
    internal ChoiceSource ChoiceOrigin;

    /// <summary>Unblocked-player hits in the current attack context (PainfulStabs after the attack).</summary>
    public int AttackUnblockedHits;

    /// <summary>Number of carried choices already consumed (the pending request's index).</summary>
    public readonly int ChoicesTaken => _choicesTaken;

    /// <summary>
    /// The step reached the enemy-side turn-end pass. The native turn counter advances after that pass
    /// even when a kill there ends the combat; search does not read this flag.
    /// </summary>
    internal bool EnemySideTurnEndReached;

    /// <summary>
    /// Max HP requested by BrightestFlame plays in this action, including automatic and replayed
    /// plays. Transient per action; never part of the battle state or fingerprint. The existing
    /// engine records the same total in <c>SimulatedCombatState.BrightestFlameMaxHpSpent</c>.
    /// </summary>
    internal int BrightestFlameMaxHpSpent;
    private readonly ChoiceSelections _choices;
    private readonly int _choiceCount;
    private int _choicesTaken;

    public Exec(ref Battle battle, Rules rules, in PlayerAction action)
    {
        B = ref battle;
        R = rules;
        _choices = action.Choices;
        _choiceCount = action.ChoiceCount;
        _choicesTaken = 0;
        NeedsChoice = false;
        HistoryRisk = false;
        EnemySideTurnEndReached = false;
        BrightestFlameMaxHpSpent = 0;
        Request = default;
        Card = default;
        Stats = null;
        Target = Limits.NoOwner;
        XValue = 0;
        StarValue = 0;
        PlayIndex = 0;
        PlayPosition = -1;
        EffectDepth = 0;
        ChoiceTiming = ChoiceTiming.Action;
        DrawnCardPosition = -1;
        IntrinsicDrawHandled = false;
        GeneratedOptions = default;
        GeneratedOptionCount = 0;
        PendingPowerChanges = default;
        PendingPowerChangeCount = 0;
        CardPlayPairs = default;
        CardPlayPairCount = 0;
        MonologuePending = default;
        MonologuePendingCount = 0;
        CurrentMonologueBase = 0;
        MonologuesResolved = false;
        CardPowerSourceActive = false;
        CardPowerSourceSerial = 0;
        TransientMarkBase = 0;
        CardPowerSourceSerialCounter = 0;
        LampTriggerSerial = 0;
        LampInternalTypeCount = 0;
        LampInternalTypes = default;
        VambraceTriggerActive = false;
        VambraceTriggerPosition = -1;
        VambraceTriggerSerial = 0;
        LastCardPowerSourceSerial = 0;
        PlayIdentity = 0;
        LastPlayIdentity = 0;
        PlayIdentityCounter = 0;
        MusicBoxPlayIdentity = 0;
        CurrentPlayIsAutoPlay = false;
        CurrentPlayIsHistoryCourseDupe = false;
        CurrentPlayEnergyValue = 0;
        CardDamageTotal = 0;
        CardKilledTarget = false;
        PendingBlockBreakOwners = 0;
        LastDamageTotal = 0;
        LastDamageOverkill = 0;
        TakingExtraTurn = false;
        SetupPause = null;
        SetupPreamble = false;
        ChoiceOrigin = default;
    }

    /// <summary>
    /// Captures the execution context at the player-turn-start boundary of an end-turn action.
    /// Every value field whose lifetime is not confined to a single effect is copied; the fields
    /// that are structurally empty at this boundary are asserted by
    /// <see cref="AssertPlayerStartBoundary"/>.
    /// </summary>
    internal ExecContinuation CaptureContinuation()
    {
        AssertPlayerStartBoundary();
        return new ExecContinuation(
            HistoryRisk, AttackUnblockedHits, CardDamageTotal, CardKilledTarget,
            LastDamageKills, LastDamageTotal, LastDamageOverkill, Card, Stats, Target, XValue,
            StarValue, PlayIndex, PlayPosition, DrawnCardPosition, IntrinsicDrawHandled,
            GeneratedOptions, GeneratedOptionCount, PendingPowerChanges, PendingPowerChangeCount,
            CardPlayPairs, CardPlayPairCount, MonologuePending, MonologuePendingCount,
            CurrentMonologueBase, MonologuesResolved, CardPowerSourceActive, CardPowerSourceSerial,
            CardPowerSourceSerialCounter, LampTriggerSerial, LampInternalTypeCount,
            LampInternalTypes, VambraceTriggerActive, VambraceTriggerPosition,
            VambraceTriggerSerial, LastCardPowerSourceSerial, PlayIdentity, LastPlayIdentity,
            PlayIdentityCounter, MusicBoxPlayIdentity, CurrentPlayIsAutoPlay,
            CurrentPlayIsHistoryCourseDupe, CurrentPlayEnergyValue, BrightestFlameMaxHpSpent);
    }

    /// <summary>Restores a prefix snapshot into a freshly constructed action execution context.</summary>
    internal void RestoreContinuation(in ExecContinuation continuation)
    {
        HistoryRisk = continuation.HistoryRisk;
        AttackUnblockedHits = continuation.AttackUnblockedHits;
        CardDamageTotal = continuation.CardDamageTotal;
        CardKilledTarget = continuation.CardKilledTarget;
        LastDamageKills = continuation.LastDamageKills;
        LastDamageTotal = continuation.LastDamageTotal;
        LastDamageOverkill = continuation.LastDamageOverkill;
        Card = continuation.Card;
        Stats = continuation.Stats;
        Target = continuation.Target;
        XValue = continuation.XValue;
        StarValue = continuation.StarValue;
        PlayIndex = continuation.PlayIndex;
        PlayPosition = continuation.PlayPosition;
        DrawnCardPosition = continuation.DrawnCardPosition;
        IntrinsicDrawHandled = continuation.IntrinsicDrawHandled;
        GeneratedOptions = continuation.GeneratedOptions;
        GeneratedOptionCount = continuation.GeneratedOptionCount;
        PendingPowerChanges = continuation.PendingPowerChanges;
        PendingPowerChangeCount = continuation.PendingPowerChangeCount;
        CardPlayPairs = continuation.CardPlayPairs;
        CardPlayPairCount = continuation.CardPlayPairCount;
        MonologuePending = continuation.MonologuePending;
        MonologuePendingCount = continuation.MonologuePendingCount;
        CurrentMonologueBase = continuation.CurrentMonologueBase;
        MonologuesResolved = continuation.MonologuesResolved;
        CardPowerSourceActive = continuation.CardPowerSourceActive;
        CardPowerSourceSerial = continuation.CardPowerSourceSerial;
        CardPowerSourceSerialCounter = continuation.CardPowerSourceSerialCounter;
        LampTriggerSerial = continuation.LampTriggerSerial;
        LampInternalTypeCount = continuation.LampInternalTypeCount;
        LampInternalTypes = continuation.LampInternalTypes;
        VambraceTriggerActive = continuation.VambraceTriggerActive;
        VambraceTriggerPosition = continuation.VambraceTriggerPosition;
        VambraceTriggerSerial = continuation.VambraceTriggerSerial;
        LastCardPowerSourceSerial = continuation.LastCardPowerSourceSerial;
        PlayIdentity = continuation.PlayIdentity;
        LastPlayIdentity = continuation.LastPlayIdentity;
        PlayIdentityCounter = continuation.PlayIdentityCounter;
        MusicBoxPlayIdentity = continuation.MusicBoxPlayIdentity;
        CurrentPlayIsAutoPlay = continuation.CurrentPlayIsAutoPlay;
        CurrentPlayIsHistoryCourseDupe = continuation.CurrentPlayIsHistoryCourseDupe;
        CurrentPlayEnergyValue = continuation.CurrentPlayEnergyValue;
        BrightestFlameMaxHpSpent = continuation.BrightestFlameMaxHpSpent;
    }

    /// <summary>
    /// Asserts the execution-context invariants of the split point immediately before
    /// <c>AdvanceRoundPlayerStart</c>. A failure means the first half stopped inside an effect (or
    /// consumed a choice), so the prefix is not a valid continuation point.
    /// </summary>
    private void AssertPlayerStartBoundary()
    {
        if (NeedsChoice)
            throw new InvalidOperationException("EndTurn 前缀停在未决选择处。");
        if (Request.OptionCount != 0 || Request.MinCount != 0 || Request.MaxCount != 0)
            throw new InvalidOperationException("EndTurn 前缀带有未消费的选择请求。");
        if (_choicesTaken != 0)
            throw new InvalidOperationException("EndTurn 前缀已经消费了选择。");
        if (EffectDepth != 0)
            throw new InvalidOperationException($"EndTurn 前缀的 EffectDepth={EffectDepth} 非零。");
        if (TransientMarkBase != 0)
            throw new InvalidOperationException($"EndTurn 前缀的 TransientMarkBase={TransientMarkBase} 非零。");
        if (TakingExtraTurn)
            throw new InvalidOperationException("EndTurn 前缀处于额外回合。");
        if (SetupPause != null)
            throw new InvalidOperationException("EndTurn 前缀带有回合准备暂停回调。");
        if (SetupPreamble)
            throw new InvalidOperationException("EndTurn 前缀处于回合准备前奏。");
        if (!ChoiceOrigin.IsNone)
            throw new InvalidOperationException($"EndTurn 前缀带有选择来源 {ChoiceOrigin.Kind}:{ChoiceOrigin.Id}。");
    }

    /// <summary>Set when an effect asked for a choice the action did not carry; callers unwind immediately.</summary>
    public bool NeedsChoice;

    /// <summary>
    /// Set by content whose existing-engine dispatch records a prediction risk history entry even
    /// when the compensating path applies the same effect (compensated risk, e.g. IllusionPower's
    /// death dispatch). Transient per action; never part of the battle state or fingerprint.
    /// </summary>
    public bool HistoryRisk;

    /// <summary>
    /// Sum of <c>TotalDamage + OverkillDamage</c> over the damage results of the current card play
    /// (the <c>CombatPredictionDamageReceivedEntry</c> history the existing engine's Fisticuffs sums).
    /// Reset when a card starts; transient, never part of the state.
    /// </summary>
    public int CardDamageTotal;

    /// <summary>Whether the current card play killed its chosen target (HandOfGreed's WasFatalKill).</summary>
    public bool CardKilledTarget;

    /// <summary>
    /// Owners whose block was broken since the last deferred compensation point. The existing engine
    /// applies the BurrowedPower block-break compensation from <c>TriggeredPowerSupport.CompensateHistorySince</c>
    /// after the effect's death pass, so a lethal hit has already removed BurrowedPower. Transient;
    /// never part of the battle state or fingerprint.
    /// </summary>
    public ulong PendingBlockBreakOwners;

    /// <summary>The round being advanced is an extra turn (<c>ShouldTakeExtraTurn</c>).</summary>
    public bool TakingExtraTurn;

    /// <summary>
    /// Number of targets killed by the most recent <c>Kernel.Damage</c> call. EchoingSlash uses it
    /// to add one extra attack wave per kill; nested damage overwrites it, so read it immediately.
    /// </summary>
    public int LastDamageKills;

    /// <summary>
    /// <c>TotalDamage + OverkillDamage</c> of the most recent <c>Damage</c> call, captured before its
    /// result hooks run (Omnislice copies this amount to the other enemies).
    /// </summary>
    public int LastDamageTotal;

    public int LastDamageOverkill;

    public ChoiceRequest Request;

    /// <summary>Value of the card currently being played (copy taken when it entered the play pile).</summary>
    public Card Card;

    public CardStats? Stats;

    /// <summary>Target owner of the current card or potion, or <see cref="Limits.NoOwner"/>.</summary>
    public byte Target;

    /// <summary>Energy X value captured by the last card play (<c>EnergyCost.CapturedXValue</c>).</summary>
    public int XValue;

    /// <summary>
    /// Star value captured by the last card play (<c>CardModel.LastStarsSpent</c>): the star cost paid,
    /// or the star cost computed for an auto-play. <c>ResolveStarXValue</c> reads it.
    /// </summary>
    public int StarValue;

    public int PlayIndex;

    /// <summary>Relative play-pile position of the card whose tail/attachment hook is running (-1 outside a play).</summary>
    public int PlayPosition;

    /// <summary>Nesting depth of card/potion effects; <c>AfterHandEmptied</c> fires only at depth zero.</summary>
    public int EffectDepth;

    /// <summary>Timing stamped on choice requests (<c>SimulatedCombatState.SetActionChoiceTiming</c>).</summary>
    public ChoiceTiming ChoiceTiming;

    /// <summary>Absolute position of the card whose <c>AfterCardDrawn</c> is being dispatched, or -1.</summary>
    public int DrawnCardPosition;

    /// <summary><c>AfterCardDrawnMirrorContext.IntrinsicCardHandled</c> for the current drawn card.</summary>
    public bool IntrinsicDrawHandled;

    /// <summary>
    /// Generated-card options produced by the current card's OnPlay, consumed by its OwnChoice
    /// (Discovery/Splash). Transient: a needs-choice unwind discards the whole partial battle and
    /// re-execution regenerates the same options from the same parent RNG state.
    /// </summary>
    public GeneratedChoiceOptions GeneratedOptions;

    public int GeneratedOptionCount;

    /// <summary>Power amount changes recorded since the last resolution (empty at a stable boundary).</summary>
    public PowerAmountChanges PendingPowerChanges;

    public int PendingPowerChangeCount;

    /// <summary>
    /// <c>BeforeCardPlayed</c> amount snapshots waiting for the matching <c>AfterCardPlayed</c>
    /// listener. A nested auto-play pushes above the outer play's entries and consumes its own first;
    /// empty outside a card play, so it never reaches a stable boundary.
    /// </summary>
    public CardPlayPairs CardPlayPairs;

    public int CardPlayPairCount;

    /// <summary>
    /// MonologuePower instances captured when the current card's effects started. Empty at every
    /// stable boundary; nested auto-plays use the same stack with a saved base marker.
    /// </summary>
    public MonologuePendingArray MonologuePending;

    public int MonologuePendingCount;

    /// <summary>Base index of the current card play's pending list (nested plays save/restore it).</summary>
    public int CurrentMonologueBase;

    /// <summary>Whether the current card play already resolved its pending Monologue Strength.</summary>
    public bool MonologuesResolved;

    /// <summary>
    /// True while a card's OnPlay/compensated effects run (the existing engine's
    /// <c>BeginCardPowerApplication</c> scope). Power applications then carry the card as their source.
    /// </summary>
    public bool CardPowerSourceActive;

    /// <summary>Unique identity of the current card power-application scope (0 = no source).</summary>
    public int CardPowerSourceSerial;

    /// <summary>
    /// Base of the transient identity-byte range reserved by nested Sly discard batches
    /// (<see cref="Kernel.DiscardAndDraw"/>). Zero outside them; transient, never part of state.
    /// </summary>
    public byte TransientMarkBase;

    /// <summary>Monotonic serial allocator; saved/restored around nested plays.</summary>
    public int CardPowerSourceSerialCounter;

    /// <summary>UnsettlingLamp's triggering card source serial (0 = not triggered).</summary>
    public int LampTriggerSerial;

    public int LampInternalTypeCount;

    public LampInternalPowerTypes LampInternalTypes;

    /// <summary>
    /// Vambrace's <c>TriggeringCard</c> within the current play: the play's power-source serial plus
    /// relative position. The serial identifies the card play without copying the full card value;
    /// transient, stable boundaries keep only the relic's used bit.
    /// </summary>
    public bool VambraceTriggerActive;

    public int VambraceTriggerPosition;

    public int VambraceTriggerSerial;

    /// <summary>The just-completed play's power-source serial; valid during AfterCardPlayed.</summary>
    public int LastCardPowerSourceSerial;

    /// <summary>
    /// Identity of the card play whose effects are running (one value per native <c>CardPlay</c>).
    /// Unlike <see cref="CardPowerSourceSerial"/> it is assigned for every play, including roots
    /// without identity content, so relic ledgers can pair BeforeCardPlayed/block hooks with the
    /// matching AfterCardPlayed. Transient: stable boundaries keep only the relic's finished flag.
    /// </summary>
    public int PlayIdentity;

    /// <summary>The identity of the play whose AfterCardPlayed is running.</summary>
    public int LastPlayIdentity;

    /// <summary>Monotonic allocator for <see cref="PlayIdentity"/>.</summary>
    public int PlayIdentityCounter;

    /// <summary>MusicBox's remembered attack play (<see cref="PlayIdentity"/>; 0 = none).</summary>
    public int MusicBoxPlayIdentity;

    /// <summary>Records one MonologuePower pending settlement; called at the start of a card's effects.</summary>
    public void PushMonologuePending(byte occurrence, int strength)
    {
        if (MonologuePendingCount >= Limits.MonologuePending)
            throw new CapacityException("MonologuePending", Limits.MonologuePending);
        if (strength is < short.MinValue or > short.MaxValue)
            throw new CapacityException("MonologuePending.Strength", short.MaxValue);
        MonologuePending[MonologuePendingCount++] = new MonologuePending
        {
            Occurrence = occurrence,
            Strength = (short)strength,
        };
    }

    /// <summary>
    /// The play whose <c>OnPlay</c>/<c>OwnChoice</c> is running was started by <c>AutoPlay</c>
    /// (<c>CardPlay.IsAutoPlay</c>). The existing engine's nested-choice sink applies a required
    /// empty choice without asking; a manual play asks it.
    /// </summary>
    public bool CurrentPlayIsAutoPlay;

    /// <summary>
    /// The card being played is a HistoryCourse duplicate (<c>CardModel.IsDupe</c>). It is created from
    /// the cross-turn slot, auto-played, and then removed from combat instead of going to a result pile.
    /// A one-play transient: nested auto-plays clear it and the caller restores it afterwards.
    /// </summary>
    public bool CurrentPlayIsHistoryCourseDupe;

    /// <summary>
    /// <c>CardPlay.Resources.EnergyValue</c> of the play whose hooks are running: the energy the card paid
    /// (<c>CardEnergyCost.GetAmountToSpend</c>), zero for an auto-play.
    /// </summary>
    public int CurrentPlayEnergyValue;

    /// <summary>Records one <c>BeforeCardPlayed</c> amount for its <c>AfterCardPlayed</c> consumer.</summary>
    public void PushCardPlayPair(CardPlayPairKind kind, byte owner, ushort powerId, int amount)
    {
        if (CardPlayPairCount >= Limits.CardPlayPairs)
            throw new CapacityException("CardPlayPairs", Limits.CardPlayPairs);
        CardPlayPairs[CardPlayPairCount++] = new CardPlayPair
        {
            PowerId = powerId,
            Owner = owner,
            Kind = kind,
            Amount = amount,
        };
    }

    /// <summary>
    /// Takes the most recent matching snapshot. Entries are keyed by the play index (the nested play's
    /// entries sit above the outer play's), and there is at most one per (kind, owner, power).
    /// </summary>
    public bool TryTakeCardPlayPair(CardPlayPairKind kind, byte owner, ushort powerId, out int amount)
    {
        for (int index = CardPlayPairCount - 1; index >= 0; index--)
        {
            CardPlayPair pair = CardPlayPairs[index];
            if (pair.Kind != kind || pair.Owner != owner || pair.PowerId != powerId)
                continue;
            amount = pair.Amount;
            for (int move = index + 1; move < CardPlayPairCount; move++)
                CardPlayPairs[move - 1] = CardPlayPairs[move];
            CardPlayPairCount--;
            return true;
        }
        amount = 0;
        return false;
    }

    /// <summary>Queues one applied power amount change for <see cref="Kernel.ResolvePowerAmountChanges"/>.</summary>
    public void RecordPowerAmountChange(ushort powerId, int delta, byte applier, byte target)
    {
        if (delta == 0)
            return;
        if (PendingPowerChangeCount >= Limits.PendingPowerAmountChanges)
            throw new CapacityException("PendingPowerAmountChanges", Limits.PendingPowerAmountChanges);
        PendingPowerChanges[PendingPowerChangeCount++] = new PowerAmountChange
        {
            PowerId = powerId,
            Applier = applier,
            Target = target,
            Delta = delta,
        };
    }

    /// <summary>
    /// Empties the queue after its entries were copied out for dispatch. The existing engine's
    /// <c>DrainPowerAmountChanges</c> clears the list before handlers run, so a nested resolution
    /// (for example Vicious drawing a Strike that Hellraiser auto-plays) only sees changes appended
    /// during the handlers, never the batch currently being dispatched.
    /// </summary>
    public void ClearPowerAmountChanges() => PendingPowerChangeCount = 0;

    /// <summary>
    /// Runs the pending native setup pause, if any, and returns whether this action is a
    /// <c>SetupPlayerTurn</c> preamble. Pile-based choice call sites invoke it before building
    /// candidates, mirroring <c>CardSelectCmd.FromHand</c>/<c>FromCombatPile</c>, which signal the
    /// pause before they read the pile. Running it once clears the callback, so the request itself
    /// and later choices in the same preamble do not run the side start again.
    /// </summary>
    internal bool RunSetupPause()
    {
        SetupPauseHandler? pause = SetupPause;
        if (pause != null)
        {
            SetupPause = null;
            pause(ref this);
        }
        return SetupPreamble;
    }

    /// <summary>
    /// Takes the next carried choice. When none is left, records <paramref name="request"/>, sets
    /// <see cref="NeedsChoice"/> and returns false; the caller must return without further effects.
    /// </summary>
    public bool TryTakeChoice(in ChoiceRequest request, out ulong selection)
    {
        if (_choicesTaken < _choiceCount)
        {
            selection = _choices[_choicesTaken++];
            ulong valid = request.OptionCount >= 64 ? ulong.MaxValue : (1UL << request.OptionCount) - 1;
            int count = System.Numerics.BitOperations.PopCount(selection);
            if ((selection & ~valid) != 0 || count < request.MinCount || count > request.MaxCount)
                throw new InvalidOperationException($"选择 {selection:X} 不符合选项规格（{request.OptionCount} 项，{request.MinCount}-{request.MaxCount} 个；effect={request.Effect} timing={request.Timing} source={request.Source} choices={_choiceCount} taken={_choicesTaken}）。");
            // The first carried selection answers the native setup pause: run the side start and
            // the orb turn-start pass before the listener that asked resumes and applies it.
            if (_choicesTaken == 1)
                RunSetupPause();
            return true;
        }
        selection = 0;
        NeedsChoice = true;
        Request = request;
        // The request itself is the native pause point. Side start and orbs run now, while the
        // choice is pending; the callback restores Request afterwards. A pile-based call site may
        // already have run this before building its candidates.
        RunSetupPause();
        return false;
    }

    public readonly bool AllChoicesConsumed => _choicesTaken == _choiceCount;
}
