using System.Reflection;

namespace Sts2Solver.Simulation.Combat;

/// <summary>Declares the native model id implemented by a Sts2Solver content definition.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ContentIdAttribute(string nativeId) : Attribute
{
    public string NativeId { get; } = nativeId;
}

/// <summary>Native <c>ValueProp</c> flags.</summary>
[Flags]
public enum ValueProps : byte
{
    None = 0,
    Unblockable = 2,
    Unpowered = 4,
    Move = 8,
    SkipHurtAnim = 0x10,
    FromArrow = 0x20,
}

public static class ValuePropsExtensions
{
    /// <summary>Native <c>ValueProp.IsPoweredAttack()</c>.</summary>
    public static bool IsPoweredAttack(this ValueProps props)
        => (props & ValueProps.Move) != 0 && (props & ValueProps.Unpowered) == 0;
}

/// <summary>
/// Settlement position of a declared turn-end self-damage effect. Cards resolve in hand order at
/// the player turn end; powers resolve in their kernel phase, and the player's poison ticks at the
/// end of the enemy turn. Scoring uses the position to replay the native order.
/// </summary>
public enum TurnEndSelfDamageTiming : byte
{
    None = 0,
    CardInHand = 1,
    PlayerSideTurnEnd = 2,
    EndTurnRegular = 3,
    AfterSideTurnEndLate = 4,
    EnemySideTurnEnd = 5,
}

/// <summary>
/// One content-declared turn-end self-damage effect. The amount is read from the rule table and the
/// live card or power state; settlement still runs the card's <c>OnTurnEndInHand</c> or the power's
/// phase hook. <see cref="HitCount"/> with <see cref="DecrementPerHit"/> expresses repeating damage
/// whose amount decreases each hit (the player's poison trigger).
/// </summary>
public readonly struct TurnEndSelfDamage
{
    public TurnEndSelfDamageTiming Timing { get; init; }

    public decimal Amount { get; init; }

    public ValueProps Props { get; init; }

    public byte Dealer { get; init; }

    public bool HasCardSource { get; init; }

    /// <summary>Number of separate damage calls; at least one for a declared effect.</summary>
    public int HitCount { get; init; }

    /// <summary>Each later hit uses <c>Amount - hitIndex</c> (poison decrements between triggers).</summary>
    public bool DecrementPerHit { get; init; }

    /// <summary>A card's turn-end effect: unpowered, moved, with the player as dealer and the card as source.</summary>
    public static TurnEndSelfDamage FromCard(decimal amount, bool unblockable)
        => new()
        {
            Timing = TurnEndSelfDamageTiming.CardInHand,
            Amount = amount,
            Props = ValueProps.Unpowered | ValueProps.Move
                | (unblockable ? ValueProps.Unblockable : ValueProps.None),
            Dealer = Limits.PlayerOwner,
            HasCardSource = true,
            HitCount = 1,
        };

    /// <summary>A power's turn-end effect with the exact native damage dealer and value props.</summary>
    public static TurnEndSelfDamage FromPower(TurnEndSelfDamageTiming timing, decimal amount,
        ValueProps props, byte dealer, int hitCount = 1, bool decrementPerHit = false)
        => new()
        {
            Timing = timing,
            Amount = amount,
            Props = props,
            Dealer = dealer,
            HasCardSource = false,
            HitCount = hitCount,
            DecrementPerHit = decrementPerHit,
        };
}

public enum ListenerKind : byte
{
    Power,
    Relic,
    Potion,
    Monster,
    Orb,
    Card,
    Affliction,
    Enchantment,
}

/// <summary>
/// Identity of the listener a hook is being dispatched to. <see cref="Index"/> is a row in the
/// corresponding table; for cards it is the absolute position in <see cref="Battle.Cards"/>, so it
/// is 16-bit like the card capacity.
/// </summary>
public readonly record struct Listener(ListenerKind Kind, ushort Index, byte Owner);

/// <summary>Arguments of the damage modification hooks.</summary>
public readonly struct DamageContext
{
    public DamageContext(byte target, byte dealer, ValueProps props, bool hasCardSource, in Card cardSource, bool sourceInPlay = true)
    {
        Target = target;
        Dealer = dealer;
        Props = props;
        HasCardSource = hasCardSource;
        CardSource = cardSource;
        SourceInPlay = sourceInPlay;
    }

    /// <summary>Target owner, or <see cref="Limits.NoOwner"/>.</summary>
    public byte Target { get; }

    /// <summary>Dealer owner, or <see cref="Limits.NoOwner"/>.</summary>
    public byte Dealer { get; }

    public ValueProps Props { get; }

    public bool HasCardSource { get; }

    public Card CardSource { get; }

    /// <summary>False for damage previews of a card outside the play pile.</summary>
    public bool SourceInPlay { get; }
}

/// <summary>Arguments of the HP loss modification hooks (<c>ModifyHpLostMirrorContext</c>).</summary>
public readonly struct HpLossContext
{
    public HpLossContext(byte target, ValueProps props, byte dealer, bool hasCardSource, in Card cardSource)
    {
        Target = target;
        Props = props;
        Dealer = dealer;
        HasCardSource = hasCardSource;
        CardSource = cardSource;
    }

    public byte Target { get; }

    public ValueProps Props { get; }

    /// <summary>Dealer owner, or <see cref="Limits.NoOwner"/>.</summary>
    public byte Dealer { get; }

    public bool HasCardSource { get; }

    public Card CardSource { get; }
}

/// <summary>Arguments of the block modification hooks.</summary>
public readonly struct BlockContext
{
    public BlockContext(byte target, ValueProps props, bool hasCardSource, in Card cardSource)
    {
        Target = target;
        Props = props;
        HasCardSource = hasCardSource;
        CardSource = cardSource;
    }

    public byte Target { get; }

    public ValueProps Props { get; }

    public bool HasCardSource { get; }

    public Card CardSource { get; }
}

/// <summary>Hooks a definition overrides; computed once by reflection.</summary>
[Flags]
public enum HookMask : ulong
{
    None = 0,
    ModifyDamageAdditive = 1UL << 0,
    ModifyDamageMultiplicative = 1UL << 1,
    ModifyDamageCap = 1UL << 2,
    ModifyBlockAdditive = 1UL << 3,
    ModifyBlockMultiplicative = 1UL << 4,
    AfterDamageReceived = 1UL << 5,
    AfterDamageReceivedLate = 1UL << 6,
    AfterDamageGiven = 1UL << 7,
    BeforeCardPlayed = 1UL << 8,
    AfterCardPlayed = 1UL << 9,
    AfterCardPlayedLate = 1UL << 10,
    AfterCardExhausted = 1UL << 11,
    AfterBlockGained = 1UL << 12,
    AfterAttack = 1UL << 13,
    BeforeAttack = 1UL << 38,
    ShouldClearBlock = 1UL << 14,
    ModifyMaxEnergy = 1UL << 15,
    ModifyHandDraw = 1UL << 16,
    ModifyHandDrawLate = 1UL << 17,
    BeforeDamageReceived = 1UL << 18,
    BeforeSideTurnEndVeryEarly = 1UL << 19,
    BeforeSideTurnEndEarly = 1UL << 20,
    BeforeSideTurnEnd = 1UL << 21,
    ShouldDraw = 1UL << 22,
    AfterCardDrawnEarly = 1UL << 23,
    AfterCardDrawn = 1UL << 24,
    ModifyHpLostBeforeOsty = 1UL << 25,
    ModifyHpLostBeforeOstyLate = 1UL << 26,
    ModifyHpLostAfterOsty = 1UL << 27,
    ModifyHpLostAfterOstyLate = 1UL << 28,
    AfterModifyingHpLostAfterOsty = 1UL << 29,
    AfterCurrentHpChanged = 1UL << 30,
    BeforeDeath = 1UL << 31,
    ShouldDie = 1UL << 32,
    ShouldDieLate = 1UL << 33,
    AfterDeath = 1UL << 34,
    AfterBlockBroken = 1UL << 35,
    ShouldPlay = 1UL << 36,
    ShouldFlush = 1UL << 37,
    AfterAutoPostPlayPhaseEntered = 1UL << 39,
    TryModifyEnergyCostInCombat = 1UL << 40,
    TryModifyEnergyCostInCombatLate = 1UL << 41,
    ModifyCardPlayResultLocation = 1UL << 42,
    AfterModifyingCardPlayResultLocation = 1UL << 43,
    ModifyCardPlayCount = 1UL << 44,
    AfterModifyingCardPlayCount = 1UL << 45,
    AfterPowerAmountChanged = 1UL << 46,
    AfterShuffle = 1UL << 47,
    ModifyXValue = 1UL << 48,
    ShouldPlayerResetEnergy = 1UL << 49,
    TryModifyPowerAmountReceived = 1UL << 50,
    AfterModifyingPowerAmountReceived = 1UL << 51,
    AfterCardEnteredCombat = 1UL << 52,

    /// <summary><c>PersistentPowerSupport.TriggerRampart</c>: player-side turn start, after the owner passes.</summary>
    Rampart = 1UL << 53,

    /// <summary><c>HookMirrors.AfterCardGeneratedForCombat</c> (Aeonglass upgrades generated Withers).</summary>
    AfterCardGeneratedForCombat = 1UL << 54,

    /// <summary><c>HookMirrors.AfterCardDiscarded</c> (<c>CombatPredictionSimulator.Discard</c>).</summary>
    AfterCardDiscarded = 1UL << 55,

    /// <summary><c>HookMirrors.AfterOrbChanneled</c> (<c>CombatPredictionSimulator.OrbChannel</c>).</summary>
    AfterOrbChanneled = 1UL << 56,

    /// <summary><c>HookMirrors.AfterOrbEvoked</c> (<c>CombatPredictionSimulator.OrbEvoke</c>).</summary>
    AfterOrbEvoked = 1UL << 57,

    /// <summary><c>HookMirrors.ModifyOrbPassiveTriggerCount</c> (<c>OrbModel.TriggerPassive</c>).</summary>
    ModifyOrbPassiveTriggerCounts = 1UL << 58,

    /// <summary><c>HookMirrors.ModifyOrbValue</c> (<c>OrbModel.ModifyOrbValue</c>): a pure query.</summary>
    ModifyOrbValue = 1UL << 59,

    /// <summary><c>SimulatedCombatState.AfterPotionUsed</c>'s relic reactions (<c>ReptileTrinket</c>).</summary>
    AfterPotionUsed = 1UL << 60,

    /// <summary>
    /// <c>PowerLifecycleSupport.AfterStarsSpent</c>: <c>TriggerRelicsAfterStarsSpent</c> then the
    /// <c>ChildOfTheStarsPower</c> loop. The existing engine uses a fixed relic-then-power order
    /// instead of listener order, so <c>HookDispatch.AfterStarsSpent</c> dispatches in two passes.
    /// </summary>
    AfterStarsSpent = 1UL << 61,

    /// <summary><c>HookMirrors.AfterStarsGained</c> (BlackHolePower).</summary>
    AfterStarsGained = 1UL << 62,

    /// <summary><c>HookMirrors.ModifyStarCost</c> (<c>AbstractModel.TryModifyStarCost</c>): a pure query.</summary>
    TryModifyStarCost = 1UL << 63,

    /// <summary>
    /// Hooks the existing engine dispatches over the run listeners, which also include the deck versions of
    /// cards outside combat; a root whose deck holds a card implementing one of them is rejected.
    /// </summary>
    RunListenerHooks = ModifyDamageAdditive | ModifyDamageMultiplicative | ModifyDamageCap | BeforeDamageReceived
        | AfterDamageReceived | AfterDamageReceivedLate | AfterDamageGiven | ModifyHpLostBeforeOsty
        | ModifyHpLostBeforeOstyLate | ModifyHpLostAfterOsty | ModifyHpLostAfterOstyLate | AfterCurrentHpChanged
        | BeforeDeath | ShouldDie | ShouldDieLate | AfterDeath,
}

/// <summary>
/// Hooks past the 64 bits of <see cref="HookMask"/>, computed by the same reflection into
/// <see cref="ContentDef.HooksExt"/> and collected by the matching
/// <see cref="HookDispatch.CollectListeners(in Battle, HookMaskExt, Span{Listener})"/> overload.
/// </summary>
[Flags]
public enum HookMaskExt : ulong
{
    None = 0,

    /// <summary>
    /// Native <c>Hook.ModifyUnblockedDamageTarget</c>: a pure query chained over the combat listeners
    /// while damage resolves, without the ending gate. Supports <c>DieForYouPower</c>'s Osty redirect.
    /// </summary>
    ModifyUnblockedDamageTarget = 1UL << 0,

    /// <summary>
    /// Native <c>AbstractModel.AfterSideTurnEndLate</c>: the fresh-listener late pass after the regular
    /// end-of-side hooks (DisintegrationPower). Dispatch is in combat listener order, as
    /// <c>HookMirrors.AfterSideTurnEndLate</c>.
    /// </summary>
    AfterSideTurnEndLate = 1UL << 32,

    /// <summary>
    /// Native <c>AbstractModel.ModifyGoldGained</c> (<c>PlayerCmd.GainGold</c> before the amount is
    /// added). BowlerHat and Ectoplasm multiply/zero the amount; the already-changed listeners are
    /// then notified through <see cref="AfterModifyingGoldGained"/>.
    /// </summary>
    ModifyGoldGained = 1UL << 33,

    /// <summary>
    /// Native <c>AbstractModel.AfterModifyingGoldGained</c>, dispatched only to the listeners whose
    /// truncated amount changed in <see cref="ModifyGoldGained"/>. Both supported listeners only
    /// flash.
    /// </summary>
    AfterModifyingGoldGained = 1UL << 34,

    /// <summary>
    /// Native <c>AbstractModel.AfterGoldGained</c> (<c>PlayerCmd.GainGold</c> after the amount was
    /// added). DragonFruit gains its MaxHp variable and heals the same amount.
    /// </summary>
    AfterGoldGained = 1UL << 35,

    /// <summary>
    /// Native <c>Hook.ModifyPowerAmountGiven</c>: a pure query over the combat listeners before the
    /// received-side pass and the Artifact check (<c>PersistentRelicSupport.ModifyPowerAmountGiven</c>,
    /// SneckoSkull's Poison bonus). Uses the batch's assigned bits 16-31.
    /// </summary>
    ModifyPowerAmountGiven = 1UL << 16,

    /// <summary>
    /// Native <c>ModifyPowerAmountGivenMultiplicative</c>: a pure query after the additive given pass
    /// and before the received-side pass (<c>UnsettlingLamp</c>'s per-card debuff doubling).
    /// </summary>
    ModifyPowerAmountGivenMultiplicative = 1UL << 17,

    /// <summary>
    /// Native <c>BeforePowerAmountChanged</c>: the observation pass at the start of
    /// <c>SimulatedCombatState.ModifyPowerAmountForRelics</c> (UnsettlingLamp records the card and the
    /// temporary power's internal type). Must not change the amount.
    /// </summary>
    BeforePowerAmountChanged = 1UL << 18,

    /// <summary>
    /// Native <c>AfterModifyingBlockAmount</c> for the listeners that changed the settled block,
    /// dispatched before the block is added (<c>HookMirrors.ModifyBlock</c> / <c>GainBlock</c>).
    /// </summary>
    AfterModifyingBlockAmount = 1UL << 19,

    /// <summary>
    /// Native <c>AfterEnergySpent</c> (<c>CardModel.SpendResources</c>, before star spending):
    /// <c>OrbitPower</c> accumulates spent energy and grants energy on each full cycle.
    /// </summary>
    AfterEnergySpent = 1UL << 20,

    /// <summary>
    /// Native <c>EnchantmentModel.BeforeFlush</c> (<c>EnchantmentLifecycleSupport.BeforeFlush</c>):
    /// the player turn end applies it to every hand card before <c>ShouldFlush</c>
    /// (SlumberingEssence's <c>AddUntilPlayed(-1)</c>). Uses the batch's assigned bits 1-15.
    /// </summary>
    BeforeFlush = 1UL << 1,

    /// <summary>
    /// Native <c>AbstractModel.ModifyShuffleOrder</c> (<c>CombatPredictionSimulator.Shuffle</c> after the
    /// stable shuffle, before the cards are placed into the draw pile; PerfectFit moves its card to the
    /// front). Uses the batch's assigned bits 1-15.
    /// </summary>
    ModifyShuffleOrder = 1UL << 2,

    /// <summary>
    /// Native <c>AbstractModel.AfterFlush</c> (<c>CorePowerSupport.FlushPlayerHandAtTurnEnd</c> after the
    /// non-retained hand moved to the discard pile; Bookmark picks a retained card). Uses this batch's
    /// assigned bits 3-15.
    /// </summary>
    AfterFlush = 1UL << 3,

    /// <summary>
    /// Native <c>AbstractModel.ShouldTakeExtraTurn</c> (<c>HookMirrors.ShouldTakeExtraTurn</c>, after the
    /// player turn-end phases and before the enemy turn; ExtraTurnPower and PaelsEye). Uses bits 3-15.
    /// </summary>
    ShouldTakeExtraTurn = 1UL << 4,

    /// <summary>
    /// Native <c>AbstractModel.AfterTakingExtraTurn</c> (<c>HookMirrors.AfterTakingExtraTurn</c>, before
    /// the extra player turn starts; ExtraTurnPower decrements, PaelsEye marks itself used). Bits 3-15.
    /// </summary>
    AfterTakingExtraTurn = 1UL << 5,

    /// <summary>
    /// <c>SimulatedCombatState.RecordRelicRoundDamage</c>: the per-round relic bookkeeping after the
    /// enemy turn (or before an extra turn starts). EmotionChip rotates its lost-HP window here.
    /// Uses bits 3-15.
    /// </summary>
    AfterRelicRound = 1UL << 6,

    /// <summary>
    /// Native <c>Hook.AfterCreatureAddedToCombat</c>: a creature entered combat after the root.
    /// PhilosophersStone applies its enemy Strength here; FurCoat's HP rewrite is handled by
    /// <c>Kernel.AddCreatedMonster</c> because it runs before the monster's first move.
    /// </summary>
    AfterCreatureAddedToCombat = 1UL << 7,

    AfterModifyingHandDraw = 1UL << 8,
    AfterArrowShot = 1UL << 9,
    AfterArrowImpaled = 1UL << 10,
    AfterArrowDislodged = 1UL << 11,

    /// <summary>Native AttackCommand.Execute's hit-count query after BeforeAttack.</summary>
    ModifyAttackHitCount = 1UL << 12,

    /// <summary>
    /// Gold hooks the native <c>PlayerCmd.GainGold</c> dispatches over run listeners, which include
    /// the deck copies of cards outside the combat piles. A root whose deck holds a card implementing
    /// one of them is rejected because Sts2Solver keeps only the combat copies.
    /// </summary>
    RunListenerHooks = ModifyGoldGained | AfterModifyingGoldGained | AfterGoldGained,
}

/// <summary>
/// Candidate lists frozen by the Bridge with the existing engine's own generation queries. A
/// definition declaring one of these ids requires the rule table to contain the list and every
/// candidate to have a Sts2Solver card definition (admission rejects the root otherwise).
/// </summary>
public static class CardPools
{
    public const string Tarot = "tarot";
    public const string OtherCharacterCards = "character.other-all";
    public const string CharacterShots = "character.shots";
    public const string Melodies = "all.melodies";
    public const string Wah = "all.wah";
    public const string Cho = "all.cho";
    public const string Ki = "all.ki";

    /// <summary>Player's unlocked character cards, filtered for combat generation.</summary>
    public const string CharacterAll = "character.all";

    /// <summary>Player's unlocked character Attacks, filtered for combat generation.</summary>
    public const string CharacterAttacks = "character.attacks";

    /// <summary>Player's unlocked character cards with canonical cost 0 and no X cost.</summary>
    public const string CharacterZeroCost = "character.zero-cost";

    /// <summary>Player's unlocked colorless cards, filtered for combat generation.</summary>
    public const string ColorlessAll = "colorless.all";

    /// <summary>Colorless cards except JackOfAllTrades itself.</summary>
    public const string ColorlessExceptJackOfAllTrades = "colorless.except-jack-of-all-trades";

    /// <summary>Attacks from every other unlocked character pool (Splash).</summary>
    public const string OtherCharacterAttacks = "character.other-attacks";

    /// <summary>Player's unlocked character Power cards (WhiteNoise, CreativeAiPower).</summary>
    public const string CharacterPowers = "character.powers";

    /// <summary>Player's unlocked character cards whose canonical keywords include Ethereal (BigHat).</summary>
    public const string CharacterEthereal = "character.ethereal";

    /// <summary>Unlocked status cards (Entropy's transformation of a Status card).</summary>
    public const string StatusAll = "status.all";

    /// <summary>Unlocked curse cards (Entropy's transformation of a Curse card).</summary>
    public const string CurseAll = "curse.all";
}

public abstract class ContentDef
{
    public string NativeId { get; internal set; } = "";

    public ushort Id { get; internal set; }

    public HookMask Hooks { get; internal set; }

    public HookMaskExt HooksExt { get; internal set; }

    /// <summary>Native dynamic-variable names compiled into the rule table, in order.</summary>
    public virtual IReadOnlyList<string> VarNames => [];

    // Cached once by Registry. Sorting must not allocate a fresh VarNames collection per comparison.
    internal byte MutableVarCount { get; set; }
    internal byte FirstMutableVarInSort { get; set; }

    /// <summary>
    /// Native card ids (with upgrade level) this content can put into combat. The rule compiler adds
    /// their canonical stats, and admission rejects the root when any of them has no Sts2Solver definition.
    /// </summary>
    public virtual IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [];

    /// <summary>
    /// Candidate pools (see <see cref="CardPools"/>) this content can generate from. The Bridge
    /// freezes each pool with the existing engine's own query and admission rejects the root when a
    /// candidate has no Sts2Solver definition.
    /// </summary>
    public virtual IReadOnlyList<string> GeneratedCardPools => [];

    /// <summary>
    /// Native power ids this content applies during its effect. The closure follows them so a power
    /// that can generate cards (CalamityPower) is frozen and checked before the root is admitted.
    /// </summary>
    public virtual IReadOnlyList<string> AppliedPowers => [];

    /// <summary>
    /// True when this content can create a random potion in combat (<c>PotionFactory.CreateRandomPotionInCombat</c>).
    /// The Bridge then freezes the potion option pool and admission rejects an undefined candidate.
    /// </summary>
    public virtual bool GeneratesRandomPotion => false;

    /// <summary>
    /// True when this content can create a random potion with
    /// <c>PotionFactory.CreateRandomPotionOutOfCombat</c> (EntropicBrew, DelicateFrond): the rule
    /// compiler freezes the unfiltered character + shared potion pool, including potions that cannot
    /// be generated in combat.
    /// </summary>
    public virtual bool GeneratesOutOfCombatPotion => false;

    /// <summary>
    /// Card stats and pools used only while the opening battle is built. They are frozen into the
    /// provisional rules so first-turn generation can sample them, but not into the play-phase
    /// rules (the generated cards themselves are part of the battle and keep their stats there).
    /// </summary>
    public virtual IReadOnlyList<string> SetupOnlyCardPools => [];

    public virtual bool SetupOnlyOutOfCombatPotionPool => false;

    /// <summary>
    /// Native content that calls <c>PlayerCmd.GainGold</c> while combat is in progress. HandOfGreed
    /// is the only card that does; LuckyFysh is a relic listener that can gain gold when a thief
    /// returns a stolen card to the deck during combat. The pruning bound counts HandOfGreed copies
    /// once each and keeps the result unknown for every other in-combat source.
    /// </summary>
    public virtual bool GainsGoldInCombat => false;
}

/// <summary>
/// Base of every definition that can listen to hooks. Hook methods are named after the native
/// hook and receive the listener identity so the definition can read its own state.
/// Definitions are stateless; every mutable value lives in <see cref="Battle"/>.
/// </summary>
public abstract class ListenerDef : ContentDef
{
    /// <summary>
    /// Native <c>AbstractModel.AfterSideTurnEndLate</c>: the late pass with a fresh combat-listener
    /// snapshot. <paramref name="participates"/> is <c>participants.Contains(self.Owner)</c>.
    /// </summary>
    public virtual void AfterSideTurnEndLate(ref Exec x, Listener self, Side side, bool participates)
    {
    }

    /// <summary>Case in <c>PersistentPowerSupport.TriggerRampart</c> (player-side turn start only).</summary>
    public virtual void Rampart(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// <c>HookMirrors.AfterCardGeneratedForCombat</c> for the card at <paramref name="position"/>.
    /// <paramref name="creator"/> is the player owner, or <see cref="Limits.NoOwner"/> when a
    /// monster generated the card (the existing engine passes a null <c>Player</c> creator there).
    /// </summary>
    public virtual void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position,
        byte creator)
    {
    }

    public virtual decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage, decimal amount) => 0m;

    public virtual decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount) => 1m;

    public virtual decimal ModifyDamageCap(ref Exec x, Listener self, in DamageContext damage) => decimal.MaxValue;

    public virtual decimal ModifyBlockAdditive(ref Exec x, Listener self, in BlockContext block, decimal amount) => 0m;

    public virtual decimal ModifyBlockMultiplicative(ref Exec x, Listener self, in BlockContext block, decimal amount) => 1m;

    /// <summary>
    /// Native <c>AfterModifyingBlockAmount</c> for a listener that changed the settled block, before
    /// the block is added. <paramref name="modified"/> is the final settled amount
    /// (<c>HookMirrors.AfterModifyingBlockAmount</c>).
    /// </summary>
    public virtual void AfterModifyingBlockAmount(ref Exec x, Listener self, decimal modified,
        in BlockContext block)
    {
    }

    // HP loss modification (HookMirrors.ModifyHpLost over the run listeners, no ending gate): before the
    // Osty redirect, then after it, each with a Late pass. A listener that changes the truncated amount in an
    // after-Osty pass is notified by AfterModifyingHpLostAfterOsty.

    public virtual decimal ModifyHpLostBeforeOsty(ref Exec x, Listener self, in HpLossContext loss, decimal amount) => amount;

    public virtual decimal ModifyHpLostBeforeOstyLate(ref Exec x, Listener self, in HpLossContext loss, decimal amount) => amount;

    public virtual decimal ModifyHpLostAfterOsty(ref Exec x, Listener self, in HpLossContext loss, decimal amount) => amount;

    public virtual decimal ModifyHpLostAfterOstyLate(ref Exec x, Listener self, in HpLossContext loss, decimal amount) => amount;

    public virtual void AfterModifyingHpLostAfterOsty(ref Exec x, Listener self)
    {
    }

    // Death (CombatPredictionSimulator.KillWithoutCheckingWinCondition) and HP changes, over the run
    // listeners without the ending gate.

    /// <summary>Native <c>AfterCurrentHpChanged</c>: after damage, a kill or a heal (<paramref name="delta"/> is the requested change).</summary>
    public virtual void AfterCurrentHpChanged(ref Exec x, Listener self, byte creature, decimal delta)
    {
    }

    /// <summary>
    /// Native <c>ModifyGoldGained</c>: return the amount after this listener's modification. The
    /// amount is a decimal until it is truncated for the gold balance. Pure query.
    /// </summary>
    public virtual decimal ModifyGoldGained(ref Exec x, Listener self, decimal amount) => amount;

    /// <summary>Native <c>AfterModifyingGoldGained</c>, only for listeners that changed the truncated amount.</summary>
    public virtual void AfterModifyingGoldGained(ref Exec x, Listener self, decimal amount)
    {
    }

    /// <summary>Native <c>AfterGoldGained</c>: the positive amount was added to the player's gold.</summary>
    public virtual void AfterGoldGained(ref Exec x, Listener self)
    {
    }

    public virtual void BeforeDeath(ref Exec x, Listener self, byte creature)
    {
    }

    /// <summary>Native <c>ShouldDie</c>: return false to prevent the death; the first preventer gets <see cref="AfterPreventingDeath"/>.</summary>
    public virtual bool ShouldDie(ref Exec x, Listener self, byte creature) => true;

    public virtual bool ShouldDieLate(ref Exec x, Listener self, byte creature) => true;

    public virtual void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
    }

    /// <summary>Called on the preventer only (<c>HookMirrors.AfterPreventingDeath</c>).</summary>
    public virtual void AfterPreventingDeath(ref Exec x, Listener self, byte creature)
    {
    }

    /// <summary>Native <c>AfterBlockBroken</c> over the combat listeners (no ending gate), from <c>ProcessDamageResults</c>.</summary>
    public virtual void AfterBlockBroken(ref Exec x, Listener self, byte target, byte breaker)
    {
    }

    // Action hooks. Each dispatch point in the kernel names the existing-engine call it mirrors.

    /// <summary>
    /// Native <c>BeforeDamageReceived</c>, dispatched after damage modification and before block
    /// (<c>CombatPredictionSimulator.TryDamageTarget</c>), over every listener without the ending gate.
    /// </summary>
    public virtual void BeforeDamageReceived(ref Exec x, Listener self, byte target, decimal amount,
        ValueProps props, byte dealer, bool hasCardSource, in Card cardSource)
    {
    }

    /// <summary>
    /// Native <c>ModifyUnblockedDamageTarget</c> (<c>Hook.ModifyUnblockedDamageTarget</c>), chained over the
    /// combat listeners without the ending gate: returns the receiver of the HP loss, which may differ from
    /// <paramref name="target"/> (Osty's <c>DieForYouPower</c>). Pure: prediction and evaluation call it read-only.
    /// </summary>
    public virtual byte ModifyUnblockedDamageTarget(ref Exec x, Listener self, byte target,
        decimal amount, ValueProps props, byte dealer) => target;

    public virtual void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
    }

    /// <summary>
    /// Native <c>ShouldPlay</c> in the playability check (<c>CombatPredictionSimulator.CanPlay</c>, after the costs;
    /// gated by combat ending): return false to forbid playing <paramref name="card"/>. A pure query of the battle.
    /// </summary>
    public virtual bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card) => true;

    /// <summary>
    /// Native <c>ShouldFlush</c> at the player's turn end (<c>PersistentRelicSupport.ShouldFlush</c>, every listener,
    /// no ending gate): return false to keep the whole hand.
    /// </summary>
    public virtual bool ShouldFlush(ref Exec x, Listener self) => true;

    /// <summary>
    /// Native <c>EnchantmentModel.BeforeFlush</c> (<c>EnchantmentLifecycleSupport.BeforeFlush</c>): runs for every
    /// hand card before <c>ShouldFlush</c> at the player turn end. SlumberingEssence adds a
    /// <c>WhenPlayed</c> −1 cost modifier to its own card.
    /// </summary>
    public virtual void BeforeFlush(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// Native <c>AbstractModel.ModifyShuffleOrder</c> (<c>HookMirrors.ModifyShuffleOrder</c>): the existing engine
    /// runs it after the stable shuffle and before the merged cards are placed into the draw pile. The kernel
    /// passes the local <paramref name="cards"/> region (<paramref name="count"/> entries); <paramref name="self"/>
    /// identifies the listener's own card by its absolute position in <see cref="Battle.Cards"/>.
    /// </summary>
    public virtual void ModifyShuffleOrder(ref Exec x, Listener self, scoped Span<Card> cards, int count,
        bool isInitialShuffle)
    {
    }

    /// <summary>Native <c>ShouldDraw</c> at the start of a draw (gated by combat ending): return false to prevent it.</summary>
    public virtual bool ShouldDraw(ref Exec x, Listener self, bool fromHandDraw) => true;

    /// <summary>
    /// <c>HookMirrors.AfterCardDrawn</c> Early pass for the card just drawn to <paramref name="drawnPosition"/>
    /// (absolute position in <see cref="Battle.Cards"/>).
    /// </summary>
    public virtual void AfterCardDrawnEarly(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
    }

    /// <summary>
    /// <c>HookMirrors.AfterCardDrawn</c> normal pass. A card reacting to being drawn itself (Void and similar)
    /// claims that once with <see cref="Kernel.TakeIntrinsicDraw"/>; the kernel calls it after the pass
    /// when its listener visit did not (for example while combat is ending).
    /// </summary>
    public virtual void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
    }

    /// <summary>
    /// Native <c>AfterAutoPostPlayPhaseEntered</c> (<c>HookMirrors.AfterAutoPostPlayPhaseEntered</c>),
    /// dispatched over the combat listeners while the player phase is AutoPostPlay, before it moves to
    /// End. HowlFromBeyond and StampedePower are the supported listeners.
    /// </summary>
    public virtual void AfterAutoPostPlayPhaseEntered(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// Native <c>AfterFlush</c> (<c>CorePowerSupport.FlushPlayerHandAtTurnEnd</c>): after the
    /// non-retained hand moved to the discard pile, before the end-of-turn card cleanup. Only the
    /// player's relics observe it; Bookmark picks a retained non-X card and adds an until-played −1.
    /// </summary>
    public virtual void AfterFlush(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// Native <c>ShouldTakeExtraTurn</c> (<c>HookMirrors.ShouldTakeExtraTurn</c>): whether the player
    /// takes another turn instead of the enemy turn. Dispatched in combat listener order; the first
    /// listener answering true wins.
    /// </summary>
    public virtual bool ExtraTurnPriority => false;

    public virtual bool ShouldTakeExtraTurn(ref Exec x, Listener self) => false;

    /// <summary>
    /// Native <c>AfterTakingExtraTurn</c> (<c>HookMirrors.AfterTakingExtraTurn</c>): settles each
    /// listener that grants an extra turn, on a fixed listener snapshot, before the turn starts.
    /// </summary>
    public virtual void AfterTakingExtraTurn(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// <c>SimulatedCombatState.RecordRelicRoundDamage</c>: the per-round relic bookkeeping the existing
    /// engine runs after the enemy turn or before an extra player turn. EmotionChip rotates its
    /// "lost HP last turn" window here.
    /// </summary>
    public virtual void AfterRelicRound(ref Exec x, Listener self)
    {
    }

    // HookMirrors.BeforeSideTurnEnd at the player's turn end (phase one, after AfterAutoPostPlayPhaseEntered):
    // VeryEarly, Early and normal passes over the combat listeners, each gated by combat ending.
    // participants is a bit set over owner indexes (Kernel.Participates).

    public virtual void BeforeSideTurnEndVeryEarly(ref Exec x, Listener self, Side side, ushort participants)
    {
    }

    public virtual void BeforeSideTurnEndEarly(ref Exec x, Listener self, Side side, ushort participants)
    {
    }

    public virtual void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
    }

    public virtual void AfterDamageReceivedLate(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
    }

    public virtual void AfterDamageGiven(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
    }

    // Energy cost and play-result hooks. The cost hooks are pure queries (no state mutation): the
    // kernel reads them from playability checks and the evaluation as well as from an action.
    // HookMirrors.ModifyEnergyCostInCombat runs the normal pass over every combat listener, then the
    // late pass over every combat listener; the caller clamps the result at zero.

    /// <summary>Native <c>TryModifyEnergyCostInCombat</c> normal pass; <paramref name="pile"/> is the card's pile.</summary>
    public virtual decimal TryModifyEnergyCostInCombat(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount) => amount;

    /// <summary>Native <c>TryModifyEnergyCostInCombatLate</c> late pass (Corruption, FreeAttackPower).</summary>
    public virtual decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount) => amount;

    /// <summary>
    /// Native <c>ModifyCardPlayResultLocation</c> (<c>CardModel.OnPlayWrapper</c>), after the base
    /// result location. <paramref name="energyValue"/> is <c>CardPlay.Resources.EnergyValue</c>;
    /// <paramref name="position"/> is the native <c>CardLocation.position</c>, passed through like the
    /// native struct: a listener that leaves the location alone keeps the incoming position (Shining
    /// Strike's base location is the top of the draw pile), and one that moves the card sets only what
    /// the native modifier sets (Nostalgia returns to the top of the draw pile).
    /// </summary>
    public virtual Pile ModifyCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        bool isAutoPlay, int energyValue, Pile location, ref PilePosition position)
    {
        return location;
    }

    /// <summary>
    /// <c>CardModel.OnPlayWrapper</c>'s direct dispatch over the modifiers that changed the result
    /// location (<c>HookMirrors.AfterModifyingCardPlayResultLocation</c>); not re-read from the listener list.
    /// </summary>
    public virtual void AfterModifyingCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        Pile location)
    {
    }

    /// <summary>
    /// Native <c>ModifyCardPlayCount</c> over every combat listener; the existing engine records the
    /// listeners whose value changed for the after pass.
    /// </summary>
    public virtual int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount) => playCount;

    /// <summary>
    /// <c>Hook.AfterModifyingCardPlayCount</c>: only the modifiers recorded by
    /// <see cref="ModifyCardPlayCount"/>, in listener order (OneTwoPunch, Duplication).
    /// </summary>
    public virtual void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
    {
    }

    /// <summary>
    /// <c>PowerLifecycleSupport.ResolvePowerAmountChanges</c>: a queued power amount change from
    /// <c>ApplyWithBeforeApplied</c>. The existing engine dispatches this over the current powers only.
    /// </summary>
    public virtual void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target)
    {
    }

    /// <summary>Native <c>AfterShuffle</c> over the combat listeners, after the shuffle event count.</summary>
    public virtual void AfterShuffle(ref Exec x, Listener self, byte shuffler)
    {
    }

    /// <summary>Native <c>ModifyXValue</c> over the combat listeners (<c>Hook.ModifyXValue</c>).</summary>
    public virtual int ModifyXValue(in Battle battle, Rules rules, Listener self, in Card card,
        int value) => value;

    /// <summary>
    /// <c>HookMirrors.ModifyStarCost</c> (<c>AbstractModel.TryModifyStarCost</c>): a pure query over
    /// the combat listeners, run only for a non-negative original cost and skipped while the combat is
    /// over/ending. <paramref name="pile"/> is the card's current pile (VoidFormPower/BrilliantScarf
    /// only modify cards in Hand or Play).
    /// </summary>
    public virtual decimal TryModifyStarCost(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount) => amount;

    /// <summary>
    /// <c>PowerLifecycleSupport.AfterStarsSpent</c> (<c>CombatPredictionSimulator.SpendResources</c>):
    /// the spender's relics first (<c>TriggerRelicsAfterStarsSpent</c>: MiniRegent, then GalacticDust),
    /// then every effective power in listener order (ChildOfTheStarsPower). <paramref name="amount"/>
    /// is the star cost actually paid and is always positive here.
    /// </summary>
    public virtual void AfterStarsSpent(ref Exec x, Listener self, int amount)
    {
    }

    /// <summary>
    /// Native <c>AfterEnergySpent</c> (before star spending). The amount is the energy actually paid;
    /// the existing engine calls the sink even for zero, whose <c>OrbitPower</c> case ignores it.
    /// </summary>
    public virtual void AfterEnergySpent(ref Exec x, Listener self, in Card card, int amount)
    {
    }

    /// <summary>
    /// <c>HookMirrors.AfterStarsGained</c> after <c>PlayerCmd.GainStars</c> mutates the player's
    /// stars. <paramref name="amount"/> is the requested gain (BlackHolePower ignores non-positive
    /// amounts).
    /// </summary>
    public virtual void AfterStarsGained(ref Exec x, Listener self, int amount)
    {
    }

    /// <summary>
    /// <c>Hook.ModifyOrbValue</c> over the combat listeners (<c>FocusPower</c>, <c>InfusedCore</c>).
    /// A pure query: prediction and evaluation call it read-only.
    /// </summary>
    public virtual decimal ModifyOrbValue(in Battle battle, Rules rules, Listener self,
        in Orb orb, decimal value) => value;

    /// <summary><c>HookMirrors.AfterOrbChanneled</c> for the orb now at <paramref name="orb"/>.</summary>
    public virtual void AfterOrbChanneled(ref Exec x, Listener self, in Orb orb)
    {
    }

    /// <summary>
    /// <c>HookMirrors.AfterOrbEvoked</c>: the evoked orb value and its result targets (owner indexes,
    /// in <c>OrbModel.Evoke</c> order), dispatched after the evoke.
    /// </summary>
    public virtual void AfterOrbEvoked(ref Exec x, Listener self, in Orb orb,
        scoped ReadOnlySpan<byte> targets)
    {
    }

    /// <summary>
    /// <c>Hook.ModifyOrbPassiveTriggerCount</c>: a pure query over every combat listener; the listeners
    /// that change the count are recorded for <c>AfterModifyingOrbPassiveTriggerCount</c>, whose only
    /// vanilla listener (GoldPlatedCables) is cosmetic and has no Sts2Solver definition.
    /// </summary>
    public virtual int ModifyOrbPassiveTriggerCounts(in Battle battle, Rules rules, Listener self,
        in Orb orb, int orbIndex, int triggerCount) => triggerCount;

    /// <summary>
    /// Native <c>ShouldPlayerResetEnergy</c> over the combat listeners before the player turn's energy
    /// reset (<c>PersistentRelicSupport.ShouldPlayerResetEnergy</c>); false keeps the current energy.
    /// </summary>
    public virtual bool ShouldPlayerResetEnergy(in Battle battle, Rules rules, Listener self,
        byte player) => true;

    /// <summary>
    /// Native <c>TryModifyPowerAmountReceived</c> (RuinedHelmet). This pass must not mutate state; the
    /// kernel records the listeners that changed the amount and calls
    /// <see cref="AfterModifyingPowerAmountReceived"/> on them before the Artifact check, matching
    /// <c>SimulatedCombatState.ModifyPowerAmountForRelics</c>.
    /// </summary>
    /// <summary>
    /// <c>Hook.ModifyPowerAmountGiven</c>: <c>PersistentRelicSupport.ModifyPowerAmountGiven</c> iterates
    /// the combat listeners and passes the already-modified amount, so each listener adds its delta.
    /// A pure query; the received-side pass follows it.
    /// </summary>
    public virtual decimal ModifyPowerAmountGiven(in Battle battle, Rules rules, Listener self,
        ushort powerId, byte giver, byte target, decimal amount) => 0m;

    /// <summary>
    /// Native <c>Hook.ModifyPowerAmountGivenMultiplicative</c>
    /// (<c>UnsettlingLamp.ModifyPowerAmountGivenMultiplicative</c>): a pure query over the combat
    /// listeners after the additive given pass. <paramref name="cardSourceSerial"/> is 0 when the
    /// application has no card source.
    /// </summary>
    public virtual decimal ModifyPowerAmountGivenMultiplicative(ref Exec x, Listener self,
        ushort powerId, byte giver, byte target, decimal amount, int cardSourceSerial) => 1m;

    /// <summary>
    /// Native <c>BeforePowerAmountChanged</c> at the start of
    /// <c>SimulatedCombatState.ModifyPowerAmountForRelics</c>. UnsettlingLamp records the triggering
    /// card and the temporary power's internal type; the amount must not be changed here.
    /// </summary>
    public virtual void BeforePowerAmountChanged(ref Exec x, Listener self, ushort powerId,
        int amount, byte target, byte applier, int cardSourceSerial)
    {
    }

    public virtual bool TryModifyPowerAmountReceived(in Battle battle, Rules rules, Listener self,
        ushort powerId, byte target, int amount, out int modified)
    {
        modified = amount;
        return false;
    }

    /// <summary>Native <c>AfterModifyingPowerAmountReceived</c> for the listeners recorded by the received pass.</summary>
    public virtual void AfterModifyingPowerAmountReceived(ref Exec x, Listener self, ushort powerId,
        byte target)
    {
    }

    /// <summary>Native <c>Hook.AfterCreatureAddedToCombat</c> for a creature added after the root.</summary>
    public virtual void AfterCreatureAddedToCombat(ref Exec x, Listener self, byte creature)
    {
    }

    /// <summary>
    /// Native <c>AfterCardEnteredCombat</c> over the player's relics when a card enters combat from
    /// outside the combat piles (<c>SimulatedCombatState.CardEventHistory.AfterCardEnteredCombat</c>
    /// dispatches the relic before the card-specific cost reactions).
    /// </summary>
    public virtual void AfterCardEnteredCombat(ref Exec x, Listener self, Pile pile, int index,
        in Card card)
    {
    }

    /// <summary>
    /// <c>SimulatedCombatState.AfterPotionUsed</c>'s relic reactions, dispatched while the player lives
    /// after the potion effect and before <c>ResolvePowerAmountChanges</c> (ReptileTrinket).
    /// </summary>
    public virtual void AfterPotionUsed(ref Exec x, Listener self, ushort potionId)
    {
    }

    public virtual void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
    }

    public virtual void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
    }

    public virtual void AfterCardPlayedLate(ref Exec x, Listener self, in CardPlayInfo play)
    {
    }

    public virtual void AfterCardExhausted(ref Exec x, Listener self, in Card card, bool causedByEthereal)
    {
    }

    /// <summary>
    /// Native <c>AfterCardDiscarded</c> (<c>HookMirrors.AfterCardDiscarded</c>), dispatched after a
    /// card effect moved a card to the discard pile (Tingsha, ToughBandages) and before its Sly
    /// auto-play. The turn-end hand flush does not dispatch it, matching
    /// <c>CorePowerSupport.FlushPlayerHandAtTurnEnd</c>.
    /// </summary>
    public virtual void AfterCardDiscarded(ref Exec x, Listener self, in Card card)
    {
    }

    public virtual void AfterBlockGained(ref Exec x, Listener self, byte owner, decimal amount, ValueProps props, bool fromCard)
    {
    }

    /// <summary>
    /// Native <c>BeforeAttack</c> (<c>CombatPredictionSimulator.BeginAttackContext</c>), before the first
    /// hit. Every Sts2Solver attack comes from a card or a monster move; <paramref name="props"/> are its
    /// damage props.
    /// </summary>
    public virtual void AfterArrowShot(ref Exec x, Listener self, int arrow) { }

    public virtual void AfterArrowImpaled(ref Exec x, Listener self, byte target, int arrow) { }

    public virtual void AfterArrowDislodged(ref Exec x, Listener self, byte target, int arrow,
        bool hasCardSource, in Card cardSource) { }

    public virtual void BeforeAttack(ref Exec x, Listener self, byte attacker, ValueProps props)
    {
    }

    /// <summary>Native ModifyAttackHitCount for a command sourced from the current card.</summary>
    public virtual int ModifyAttackHitCount(ref Exec x, Listener self, int hitCount) => hitCount;

    public virtual void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
    }

    /// <summary>Native <c>ShouldClearBlock</c>: return false to keep the owner's block at its side start.</summary>
    public virtual bool ShouldClearBlock(ref Exec x, Listener self, byte owner) => true;

    /// <summary>
    /// Block kept after this listener prevented the clear (<c>PersistentRelicSupport.BlockAfterPreventingClear</c>);
    /// the excess is removed as unpowered block damage.
    /// </summary>
    public virtual int BlockAfterPreventingClear(ref Exec x, Listener self, byte owner, int block) => block;

    public virtual decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount) => amount;

    /// <summary>
    /// <c>PersistentPowerSupport.GetModifiedHandDraw</c> normal pass. Evaluated exactly once per turn-start hand draw
    /// (<c>ConsumeModifiedHandDraw</c>), so a listener may advance its own per-turn state here as the existing engine does.
    /// </summary>
    public virtual decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount) => amount;

    public virtual decimal ModifyHandDrawLate(ref Exec x, Listener self, decimal amount) => amount;

    public virtual void AfterModifyingHandDraw(ref Exec x, Listener self) { }
}

public abstract class CardDef : ListenerDef
{
    /// <summary>
    /// Card effect in the existing engine's order: mirrored attack/block/draw first, then the
    /// compensated power/resource/generation effects.
    /// </summary>
    public abstract void OnPlay(ref Exec x);

    /// <summary>
    /// The card's own choice and its post-choice effects, run after <see cref="OnPlay"/> as the
    /// existing engine's <c>ResolveManualCardChoice</c> stage does (<c>CardChoiceSupport.GetSpec</c>,
    /// <c>Apply</c> and <c>ApplyPostChoiceEffects</c>). Most cards have none.
    /// </summary>
    public virtual void OwnChoice(ref Exec x)
    {
    }

    /// <summary>
    /// <c>SimulatedCombatState.IsCardPlayPrevented</c>: while this card is in hand, no card without this flag can be
    /// played (Enthralled).
    /// </summary>
    public virtual bool LocksOtherHandCards => false;

    /// <summary>Native <c>CardModel.HasTurnEndInHandEffect</c>: the card runs <see cref="OnTurnEndInHand"/> at the turn end.</summary>
    public virtual bool HasTurnEndInHandEffect => false;

    /// <summary>
    /// The damage <see cref="OnTurnEndInHand"/> will deal to the player when this card resolves from
    /// the hand at the turn end, or false when it deals none. Pure query for scoring; settlement keeps
    /// running <see cref="OnTurnEndInHand"/>.
    /// </summary>
    public virtual bool TryGetTurnEndSelfDamage(in Battle battle, Rules rules, in Card card,
        out TurnEndSelfDamage damage)
    {
        damage = default;
        return false;
    }

    /// <summary>
    /// This card uses <see cref="Card.Private"/> as the per-object identity ledger the existing
    /// engine keeps in a dictionary (<c>SwordSagePower</c>'s applied replay bonus; the same field's
    /// bit 0 is Fetch's "this exact card was played this turn" bit).
    /// </summary>
    public virtual bool UsesIdentityLedger => false;

    /// <summary>
    /// Native <c>Fetch</c>: <c>SimulatedCombatState._fetchCardsPlayedThisTurn</c> records this exact
    /// card object when the play completes. The kernel sets bit 0 of <see cref="Card.Private"/>.
    /// </summary>
    public virtual bool TracksFetchPlay => false;

    /// <summary>
    /// This card can create a card whose identity the existing engine keeps in a per-object
    /// dictionary (a random/selected card can be a Sovereign Blade, Fetch, or Nightmare target).
    /// Used only to decide whether a root must enable the identity scans.
    /// </summary>
    public virtual bool MayGenerateIdentityContent => false;

    /// <summary>This card is one of the ID batch's per-instance cards.</summary>
    public virtual bool IsIdentityContent => false;

    /// <summary>
    /// The card's variable 0 is a decimal delta stored in <see cref="Battle.DecimalVars"/>
    /// instead of <see cref="Card.Var0"/> (Thrash's <c>DynamicVars.Damage</c> receives a
    /// decimal <c>ModifyDamage</c> result). The card's <c>Private</c> field holds the slot.
    /// </summary>
    public virtual bool UsesDecimalDamageVar => false;

    /// <summary>
    /// The card's <c>CardOnTurnEndInHandMirrors</c> effect at the player's turn end, while it sits in the play
    /// pile (<c>CombatPredictionSimulator.DoTurnEndCards</c>); <see cref="Exec.Card"/> holds its value.
    /// </summary>
    public virtual void OnTurnEndInHand(ref Exec x)
    {
    }

    /// <summary>Card-specific playability (<c>CardIsPlayableMirrors</c>); most cards are always playable.</summary>
    public virtual bool IsPlayable(in Battle battle, Rules rules, in Card card) => true;

    /// <summary>
    /// Native <c>CardModel.TargetType</c>. The rule table freezes the canonical value; a card whose
    /// native override is dynamic (Shiv while the owner holds <c>FanOfKnivesPower</c>) overrides this.
    /// </summary>
    public virtual TargetType ResolveTargetType(in Battle battle, Rules rules, in Card card)
        => rules.Card(card.Id, card.Upgrade).Target;

    /// <summary>
    /// Native <c>CardModel.Type</c> when it is not the per-id canonical value (MadScience's saved
    /// <c>TinkerTimeType</c>). The rule table's <see cref="CardStats.Type"/> stays canonical for
    /// generation pools and fingerprints; the settlement and evaluation paths that read the native
    /// live type call this override instead.
    /// </summary>
    public virtual CardType ResolveCardType(in Battle battle, Rules rules, in Card card)
        => rules.Card(card.Id, card.Upgrade).Type;

    /// <summary>True for definitions whose <see cref="ResolveCardType"/> reads per-card state.</summary>
    public virtual bool HasDynamicCardType => false;

    /// <summary>
    /// Opaque per-card rider code the evaluation reads for a dynamic-type card (MadScience's
    /// <c>TinkerTimeRider</c>), or -1 when the definition has none. Settlement reads its own code.
    /// </summary>
    public virtual int PrivateRider(in Card card) => -1;

    /// <summary>
    /// Native <c>CardModel.GetResultLocationForCardPlay</c> override (<c>CardResultLocationMirrors</c>).
    /// The base location is Discard, Exhaust or Play (a power removes the card from combat); a card that
    /// returns to the hand or the top of the draw pile rewrites the discard case here. <paramref name="position"/>
    /// is the native <c>CardLocation.position</c> for the returned pile.
    /// </summary>
    public virtual decimal ModifyOwnEnergyCost(in Battle battle, Rules rules, in Card card, decimal amount) => amount;

    public virtual decimal ModifyOwnDamageMultiplier(ref Exec x, in DamageContext damage) => 1m;

    public virtual void AfterPlayCompleted(ref Exec x, bool lastInSeries) { }

    public virtual Pile ResultLocation(in Battle battle, Rules rules, in Card card,
        Pile baseLocation, out PilePosition position)
    {
        position = PilePosition.Bottom;
        return baseLocation;
    }

    /// <summary>
    /// A card's native <c>CalculatedDamage</c> formula for another effect that reads this card's damage
    /// (Thrash). Returns false for cards without a calculated damage variable; the caller then reads
    /// the named <c>Damage</c>/<c>OstyDamage</c> variable. <paramref name="self"/> is this card's value;
    /// <paramref name="selfInHand"/> says whether it sits in the hand (Thrash's pick) rather than in
    /// the play pile (its own play), which matters for multipliers that count the hand.
    /// </summary>
    public virtual bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        damage = 0m;
        return false;
    }

    /// <summary>
    /// True when every <see cref="HookMask.RunListenerHooks"/> or
    /// <see cref="HookMaskExt.RunListenerHooks"/> hook this definition implements is a no-op for the
    /// copy that lives in the player's deck outside combat. The existing engine dispatches those hooks
    /// over the run listeners, which include deck cards; Sts2Solver only has the combat copies.
    /// Melancholy qualifies: its <c>AfterDeath</c> handler resolves the card through the combat state and
    /// returns without effect when the card is not in a combat pile. Admission still rejects every other
    /// deck card implementing a run-listener hook. See admission-and-verification.md.
    /// </summary>
    public virtual bool DeckCopyOutsideCombatIsInert => false;

    /// <summary>
    /// Card-specific adjustment when this card enters combat from outside the combat piles
    /// (<c>SimulatedCombatState.AfterCardEnteredCombat</c>, for example Stomp or Pinpoint cost changes).
    /// </summary>
    public virtual void AfterEnteredCombat(ref Exec x, Pile pile, int index)
    {
    }

    /// <summary>
    /// Card-specific <c>AfterDowngraded</c> (MagiKnight's DampenPower). Called after
    /// <see cref="Kernel.DowngradeCard"/> reset the value to upgrade level 0. Claw and
    /// GeneticAlgorithm re-add their private per-play accumulator to the Damage/Block variable.
    /// </summary>
    public virtual void AfterDowngraded(ref Battle battle, int position)
    {
    }

    /// <summary>
    /// Native <c>AfterDowngraded</c> restores a per-play Damage accumulator that the model keeps
    /// outside its dynamic variables (Rampage's <c>_extraDamageFromPlays</c>, TheScythe's
    /// <c>IncreasedDamage</c>). Sts2Solver keeps that accumulation in <see cref="Card.Var0"/>, so
    /// <see cref="Kernel.DowngradeCard"/> restores the pre-downgrade value after the level-0
    /// reset instead of reading a private field.
    /// </summary>
    public virtual bool PreservesVar0OnDowngrade => false;

    /// <summary>
    /// Fills <see cref="Card.Private"/> for a card value imported from the existing engine, after
    /// the variable deltas were computed. Only cards whose definition reads the private counter may
    /// use it; the existing engine's per-card accumulator is available in the var delta
    /// (Claw/GeneticAlgorithm).
    /// </summary>
    public virtual void ProjectPrivate(ref Card card)
    {
    }
}

public enum PowerType : byte
{
    None = 0,
    Buff = 1,
    Debuff = 2,
}

public enum PowerStackType : byte
{
    None = 0,
    Counter = 1,
    Single = 2,
}

public abstract class PowerDef : ListenerDef
{
    /// <summary>This power is one of the ID batch's per-instance powers.</summary>
    public virtual bool IsIdentityContent => false;

    public abstract PowerType Type { get; }

    public abstract PowerStackType StackType { get; }

    public virtual bool AllowNegative => false;

    /// <summary>Native instanced powers get a new instance per application instead of stacking.</summary>
    public virtual bool IsInstanced => false;

    /// <summary>
    /// Native <c>PowerModel.IsVisible</c>. The single-player visibility test reduces to the hidden
    /// internal override (only <c>ExtraTurnPower</c> sets <c>IsVisibleInternal=false</c>); all visible
    /// powers here return true.
    /// </summary>
    public virtual bool IsVisible => true;

    /// <summary>
    /// The instance carries a selected card value in <see cref="Battle.DetachedCards"/>
    /// (<see cref="Power.Aux0"/> = slot + 1). Removing the power clears the slot.
    /// </summary>
    public virtual bool UsesDetachedCard => false;

    /// <summary>Native <c>GetTypeForAmount</c>: a negative amount of a buff that allows negatives is a debuff.</summary>
    public virtual PowerType TypeForAmount(int amount)
        => AllowNegative && amount < 0 && Type == PowerType.Buff ? PowerType.Debuff : Type;

    /// <summary>A newly applied player-side instance skips its first duration tick (Weak, Vulnerable, Frail).</summary>
    public virtual bool UsesNativeDurationSkip => false;

    /// <summary>
    /// The native <c>PowerModel.AmountOnTurnStart</c> is read for settlement only by
    /// <c>DrawCardsNextTurnPower</c>, <c>SummonNextTurnPower</c> and <c>HelloWorldPower</c>
    /// (<c>PowerLifecycleSupport.SemanticallyRelevantAmountOnTurnStart</c>). Other powers only carry
    /// the value in the native model, where a mirrored decrement (PowerAmountPredictionState) leaves
    /// the model amount one action behind until <c>SynchronizePowerAmountPredictionStates</c>. Sts2Solver
    /// applies mirrored decrements immediately, so for every non-reader the value is projected and
    /// snapshotted as zero instead of trying to reproduce a lagging model field.
    /// </summary>
    public virtual bool UsesAmountOnTurnStart => false;

    /// <summary>Ticks down at the end of the enemy side in <c>CorePowerSupport.TickDurations</c>.</summary>
    public virtual bool TicksAtEnemySideEnd => false;

    /// <summary>Native <c>PowerModel.OwnerIsSecondaryEnemy</c>: a positive amount makes its owner a secondary enemy.</summary>
    public virtual bool OwnerIsSecondaryEnemy => false;

    /// <summary>
    /// Native <c>RitualPower._wasJustAppliedByEnemy</c>: an enemy that gains the power from zero marks it
    /// (<see cref="PowerFlags.WasJustAppliedByEnemy"/>, set in <c>ApplyWithBeforeApplied</c>).
    /// </summary>
    public virtual bool MarksJustAppliedByEnemy => false;

    /// <summary>
    /// <c>ShouldRemoveAfterDeath</c>: a positive amount keeps its dead owner in the enemy roster (Adaptable, Illusion,
    /// Reattach, Steam Eruption).
    /// </summary>
    public virtual bool PreventsRemovalAfterDeath => false;

    /// <summary>
    /// Native <c>ShouldStopCombatFromEnding</c> for a power on a roster creature (Adaptable, Stock).
    /// </summary>
    public virtual bool PreventsCombatEnding => false;

    /// <summary>
    /// Native <c>PowerModel.ShouldPowerBeRemovedAfterOwnerDeath</c>, read by
    /// <c>SimulatedCombatState.RemovePowersAfterDeath</c>.
    /// </summary>
    public virtual bool ShouldPowerBeRemovedAfterOwnerDeath => true;

    /// <summary>
    /// The existing engine's death cleanup removes every power of a creature that already left the
    /// roster (the second <c>RemovePowersAfterDeath</c> loop). A later power-amount synchronization can
    /// still restore a power whose mirrored amount was pending; FlutterPower is the observed case and
    /// keeps its row here. Default false mirrors the cleanup.
    /// </summary>
    public virtual bool PowerRemainsAfterOwnerLeavesRoster => false;

    /// <summary>
    /// Native <c>PowerModel.ShouldOwnerDeathTriggerFatal</c> (true by default; MinionPower and a
    /// ReattachPower with living segments return false). HandOfGreed reads it before gaining gold.
    /// </summary>
    public virtual bool ShouldOwnerDeathTriggerFatal(in Battle battle, Listener self) => true;

    /// <summary>Native <c>ITemporaryPower</c>: a temporary debuff survives an illusion owner's death.</summary>
    public virtual bool IsTemporaryPower => false;

    /// <summary>
    /// Native <c>ITemporaryPower.InternallyAppliedPower</c> as a Sts2Solver native id, used by Misery's
    /// cloned-temporary merge. A null value means the temporary power has no represented internal
    /// stat (no clone call site needs the merge).
    /// </summary>
    public virtual string? InternallyAppliedPowerId => null;

    /// <summary>
    /// Native <c>IllusionPower.ShouldPowerBeRemovedOnDeath</c>: while the dead owner has this power,
    /// the solver keeps its buffs (and temporary debuffs) and removes the other debuffs
    /// (<c>SimulatedCombatState.DeathLifecycle.cs</c>, <c>RemovePowersAfterDeath</c>).
    /// </summary>
    public virtual bool KeepsOwnerBuffsAfterDeath => false;

    /// <summary>
    /// <c>DeathPowerSupport.SpawnsPrimaryEnemyOnDeath</c>: a positive amount means the owner's death
    /// spawns new primary enemies, so <c>SimulatedCombatState.HasUnresolvedSpawningDeath</c> keeps the
    /// combat from ending until the deferred death pass runs (<c>InfestedPower</c>).
    /// </summary>
    public virtual bool SpawnsPrimaryEnemyOnDeath => false;

    /// <summary>
    /// Case in <c>DeathPowerSupport.Trigger</c> for one newly dead enemy, over every positive power in
    /// listener order before that death's power cleanup (<c>Kernel.ApplyEnemyDeathPowers</c>). This
    /// covers the deaths the existing engine settles in that pass (Reattach, Infested, Surrounded's
    /// facing update); the immediate <see cref="AfterDeath"/> hook keeps the native cases that pass
    /// does not move (Ravenous, Illusion).
    /// </summary>
    public virtual void DeathTrigger(ref Exec x, Listener self, byte dead)
    {
    }

    // Turn phases. Each method is the power's case in the named existing-engine pass; the kernel
    // visits only powers with a positive amount, in the pass's order (Kernel.TurnPhases).

    /// <summary>Case in <c>TurnStartPowerSupport.ApplyBeforeSideTurnStartPower</c>.</summary>
    public virtual void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
    {
    }

    /// <summary>Case in <c>SimulatedCombatState.TriggerBaseSideTurnStart</c>.</summary>
    public virtual void BaseSideTurnStart(ref Exec x, Listener self, bool decrementPlating)
    {
    }

    /// <summary>Case in <c>PersistentPowerSupport.TriggerOwnerAfterSideTurnStart</c>.</summary>
    public virtual void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
    }

    /// <summary>Case in <c>TurnStartPowerSupport.TriggerAfterSideTurnStart</c>.</summary>
    public virtual void GlobalAfterSideTurnStart(ref Exec x, Listener self, Side side, bool participates)
    {
    }

    /// <summary>Case in <c>CorePowerSupport.TriggerAfterBlockCleared</c>.</summary>
    public virtual void AfterBlockCleared(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// The damage this power will deal to its owner in its side-turn-end phase, or false when it deals
    /// none. Pure query for scoring; settlement keeps running <see cref="EndTurnRegular"/> and the
    /// other power phase hooks. <paramref name="powerIndex"/> is the live row in <see cref="Battle.Powers"/>.
    /// </summary>
    public virtual bool TryGetTurnEndSelfDamage(in Battle battle, Rules rules, int powerIndex,
        out TurnEndSelfDamage damage)
    {
        damage = default;
        return false;
    }

    /// <summary>Case in <c>EndTurnPowerSupport.TriggerRegular</c>.</summary>
    public virtual void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
    }

    /// <summary>Case in <c>EndTurnPowerSupport.TriggerBatch048</c>.</summary>
    public virtual void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
    }

    /// <summary>Case in <c>CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects</c> before <c>TriggerRegular</c>.</summary>
    public virtual void PlayerSideTurnEnd(ref Exec x, Listener self)
    {
    }

    /// <summary>Per-enemy case in <c>CorePowerSupport.TriggerEnemySideTurnEndEffects</c> before <c>TriggerRegular</c>.</summary>
    public virtual void EnemySideTurnEnd(ref Exec x, Listener self)
    {
    }

    /// <summary>Per-player case in <c>CorePowerSupport.CompletePlayerEarlySideTurnEndEffects</c> (end of turn-end phase one).</summary>
    public virtual void CompletePlayerEarlySideTurnEnd(ref Exec x, Listener self)
    {
    }

    /// <summary>Case in <c>PersistentPowerSupport.TriggerAfterEnergyReset</c>.</summary>
    public virtual void AfterEnergyReset(ref Exec x, Listener self)
    {
    }

    /// <summary>Case in <c>TurnStartPowerSupport.ContinueBeforeHandDraw</c>.</summary>
    public virtual void BeforeHandDraw(ref Exec x, Listener self)
    {
    }

    /// <summary>Case in <c>TurnStartPowerSupport.ContinueAfterPlayerTurnStart</c>.</summary>
    public virtual void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// Case in <c>CorePowerSupport.ApplyEnemyDeathPowers</c> after a newly dead enemy's death powers:
    /// this power's applier is that enemy (Constrict, Hex and Shrink on player creatures, then Magic
    /// Bomb). Called whatever the sign of the amount, as the existing engine reads the power directly.
    /// </summary>
    public virtual void ApplierDied(ref Exec x, Listener self)
    {
    }
}

/// <summary>A relic. Turn-phase methods are its cases in the named <c>TurnStartRelicSupport</c>/<c>PersistentRelicSupport</c> batches.</summary>
public abstract class RelicDef : ListenerDef
{
    /// <summary>Declares branch-mutable instance data, including values initially zero.</summary>
    public virtual bool HasCombatState => false;

    /// <summary>This relic can create a card with per-object identity (Toolbox's colourless choice).</summary>
    public virtual bool MayGenerateIdentityContent => false;

    /// <summary>This relic has per-object identity state (Vambrace/UnsettlingLamp).</summary>
    public virtual bool IsIdentityContent => false;

    /// <summary>Native <c>UnsettlingLamp</c> (the only listener of the two power-amount ext hooks).</summary>
    public virtual bool IsUnsettlingLamp => false;

    /// <summary>Native <c>PaelsLegion</c>: the only block hook that needs the per-play ledger.</summary>
    public virtual bool IsPaelsLegion => false;

    public virtual void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
    }

    public virtual void AfterEnergyReset(ref Exec x, Listener self)
    {
    }

    public virtual void AfterEnergyResetLate(ref Exec x, Listener self)
    {
    }

    public virtual void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
    }

    /// <summary>Native <c>AfterPlayerTurnStartLate</c>, the third pass over the same listeners.</summary>
    public virtual void AfterPlayerTurnStartLate(ref Exec x, Listener self)
    {
    }

    public virtual void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
    }

    /// <summary>
    /// Case in <c>TurnStartRelicSupport.TriggerBeforeSideTurnEnd</c> (<c>PrepareRelicsBeforeSideTurnEnd</c>), the
    /// relic batch at the start of the player's turn end; not the <c>BeforeSideTurnEnd</c> hook passes.
    /// </summary>
    public virtual void PrepareBeforeSideTurnEnd(ref Exec x, Listener self, Side side)
    {
    }

    public virtual void AfterSideTurnEnd(ref Exec x, Listener self, Side side)
    {
    }

    public virtual void AfterBlockCleared(ref Exec x, Listener self)
    {
    }

    /// <summary>Native <c>BeforeHandDraw</c> relic stage on the player's hand draw.</summary>
    public virtual void BeforeHandDraw(ref Exec x, Listener self)
    {
    }

    /// <summary>
    /// Native <c>ShouldProcurePotion</c> (Sozu): returns false to refuse a potion entering the belt.
    /// </summary>
    public virtual bool ShouldProcurePotion(in Battle battle, ushort relicId, ushort potionId) => true;


}

/// <summary>
/// A potion. Its target type and variables come from <see cref="Rules.Potion"/>; targets are
/// enumerated as <c>CombatBeamSolver.TargetsForPotion</c> does.
/// </summary>
public abstract class PotionDef : ListenerDef
{
    /// <summary>
    /// <c>PotionOnUseSupport.CanSearch</c>: whether the existing search offers explicit use. A potion it does not
    /// offer still needs a definition to be held in a slot, but produces no action.
    /// </summary>
    public virtual bool Searchable => true;

    /// <summary>
    /// The potion's case in <c>PotionOnUseSupport.Use</c>. <see cref="Exec.Target"/> holds the chosen
    /// enemy, or <see cref="Limits.NoOwner"/> for self, all-enemy and untargeted potions.
    /// </summary>
    public abstract void OnUse(ref Exec x);
}

/// <summary>
/// A native orb (<c>OrbModel</c>). The orb queue is the player's, in queue order. <see cref="Orb.Aux"/>
/// carries the one private decimal each vanilla orb can accumulate (Dark's <c>_evokeVal</c>, Glass's
/// <c>_passiveVal</c>); the other types ignore it. Base values live in the definition because they are
/// native code constants, not model data.
/// </summary>
public abstract class OrbDef : ListenerDef
{
    /// <summary>
    /// <c>OrbQueue.BeforeTurnEnd</c> → <c>OrbModel.BeforeTurnEndOrbTrigger</c>. The default triggers the
    /// passive through <c>TriggerPassive</c> (which applies <c>ModifyOrbPassiveTriggerCounts</c>);
    /// Plasma overrides it with an empty trigger.
    /// </summary>
    public virtual void BeforeTurnEndOrbTrigger(ref Exec x, int orbIndex)
        => Kernel.TriggerOrbPassive(ref x, orbIndex);

    /// <summary><c>OrbQueue.AfterTurnStart</c> → <c>OrbModel.AfterTurnStartOrbTrigger</c> (Plasma).</summary>
    public virtual void AfterTurnStartOrbTrigger(ref Exec x, int orbIndex)
    {
    }

    /// <summary>
    /// Value of <see cref="Orb.Aux"/> for a newly created orb: Dark starts its evoke accumulation
    /// at 6, Glass its passive at 4 (native <c>DarkOrb._evokeVal</c> / <c>GlassOrb._passiveVal</c>).
    /// </summary>
    public virtual int InitialAux => 0;

    /// <summary><c>OrbModel.PassiveVal</c> before the <c>ModifyOrbValue</c> listeners.</summary>
    public virtual decimal BasePassiveValue(in Battle battle, in Orb orb) => 0m;

    /// <summary><c>OrbModel.EvokeVal</c> before the <c>ModifyOrbValue</c> listeners.</summary>
    public virtual decimal BaseEvokeValue(in Battle battle, in Orb orb) => 0m;

    /// <summary>
    /// Whether <c>EvokeVal</c> runs the <c>ModifyOrbValue</c> listeners. DarkOrb.EvokeVal returns the
    /// raw <c>_evokeVal</c> (Focus only affects its passive accumulation), while the other four evoke
    /// values are modified.
    /// </summary>
    public virtual bool EvokeUsesModifyOrbValue => true;

    /// <summary>
    /// <c>OrbModel.Passive</c>: mutate the battle (or the orb's private value at
    /// <paramref name="orbIndex"/>) for one trigger. The kernel verifies the slot still holds the same
    /// value before calling; a queue change during a passive is an explicit boundary.
    /// </summary>
    public virtual void Passive(ref Exec x, int orbIndex, byte target)
    {
    }

    /// <summary>
    /// <c>OrbModel.Evoke</c>: return the number of owner indexes written to <paramref name="targets"/>
    /// (capacity <see cref="Limits.Enemies"/> + 1). The caller dispatches
    /// <c>AfterOrbEvoked</c> over them afterwards.
    /// </summary>
    public virtual int Evoke(ref Exec x, in Orb orb, scoped Span<byte> targets) => 0;

    /// <summary>
    /// <c>OrbRetentionValue</c>'s per-orb multiplier: Glass counts once per living enemy, the rest once.
    /// </summary>
    public virtual int RetentionMultiplier(in Battle battle, int aliveEnemyCount) => 1;

    /// <summary>
    /// <c>OrbMirrors.GetPassiveValue</c> runs <c>ModifyOrbValue</c> for Lightning, Frost, Dark and
    /// Glass, but returns <c>PlasmaOrb.PassiveVal</c> (1) directly. The evaluation's orb retention
    /// value uses this query, so Plasma must not receive Focus.
    /// </summary>
    public virtual bool PassiveUsesModifyOrbValue => true;
}

/// <summary>
/// A card enchantment. The modification methods mirror <c>EnchantmentModel.EnchantDamage*</c> and
/// <c>EnchantBlock*</c>, which the existing engine calls before every other damage/block modifier
/// (<c>HookMirrors.ModifyDamage</c>/<c>ModifyBlock</c>), and <c>EnchantPlayCount</c>, which
/// <c>CardModel.GetEnchantedReplayCount</c> applies before the play-count hooks. They are pure
/// queries over the card value: prediction and evaluation call them read-only.
/// </summary>
public abstract class EnchantmentDef : ListenerDef
{
    /// <summary>Native <c>EnchantmentModel.ShouldStartAtBottomOfDrawPile</c> (Imbued).</summary>
    public virtual bool StartsAtBottomOfDrawPile => false;

    public virtual decimal EnchantDamageAdditive(in Card card, decimal damage, ValueProps props) => 0m;

    public virtual decimal EnchantDamageMultiplicative(in Card card, decimal damage, ValueProps props) => 1m;

    public virtual decimal EnchantBlockAdditive(in Card card, decimal block) => 0m;

    public virtual decimal EnchantBlockMultiplicative(in Card card, decimal block) => 1m;

    /// <summary>
    /// <c>EnchantmentModel.OnEnchant</c> for the local card mutations the kernel models. Called on
    /// downgrade, where native resets the local keywords and then runs <c>ModifyCard</c> again.
    /// </summary>
    public virtual void OnEnchant(ref Exec x, ref Card card)
    {
    }

    /// <summary><c>CardModel.GetEnchantedReplayCount</c>: the base replay count with this enchantment's modifier.</summary>
    public virtual int EnchantPlayCount(in Card card, int replayCount) => replayCount;

    /// <summary>
    /// <c>EnchantmentModel.OnPlay</c> in the card-play tail, before the affliction's OnPlay and before
    /// <c>AfterCardPlayed</c> (<c>CombatPredictionSimulator.CardTailContinuation</c>).
    /// </summary>
    public virtual void OnPlay(ref Exec x, in Card card, byte target)
    {
    }
}

/// <summary>
/// A card affliction. Vanilla afflictions are markers: their card-type/stacking rules govern
/// <c>CardCmd.Afflict</c>, and the powers that apply them carry the behavior. <c>OnPlay</c> is the
/// empty base callback of <c>AfflictionModel.OnPlay</c>.
/// </summary>
public abstract class AfflictionDef : ListenerDef
{
    /// <summary>Native <c>AfflictionModel.IsStackable</c>: a second application of the same type adds its amount.</summary>
    public virtual bool IsStackable => false;

    /// <summary>Native <c>AfflictionModel.CanAfflictUnplayableCards</c>.</summary>
    public virtual bool CanAfflictUnplayableCards => true;

    /// <summary>Native <c>AfflictionModel.CanAfflictCardType</c>.</summary>
    public virtual bool CanAfflictCardType(CardType type) => true;

    /// <summary><c>AfflictionModel.OnPlay</c> in the card-play tail, after the enchantment's OnPlay.</summary>
    public virtual void OnPlay(ref Exec x, in Card card, byte target)
    {
    }
}

/// <summary>
/// A monster: its non-attack move effects and the special cases the existing engine applies when
/// advancing its state machine.
/// </summary>
public abstract class MonsterDef : ListenerDef
{
    /// <summary>Static integers read from the native monster, compiled into <see cref="MonsterRules.StaticInts"/>.</summary>
    public virtual IReadOnlyList<string> StaticIntNames => [];

    /// <summary>
    /// Root-frozen power amounts (native power ids) compiled into
    /// <see cref="MonsterRules.RootPowerAmounts"/>, for dynamic move damage whose baseline is the
    /// amount at root import (<c>SimulatedCombatState._rootPowerAmounts</c>).
    /// </summary>
    public virtual IReadOnlyList<string> RootPowerAmountNames => [];

    /// <summary>
    /// Native ids of the monster types this monster can summon. The rule compiler closes over them
    /// transitively and compiles one rule entry per type.
    /// </summary>
    public virtual IReadOnlyList<string> SpawnedMonsterIds => [];

    /// <summary>
    /// Monster-private integer names (<c>SimulatedCombatState._monsterIntStates</c>) this monster
    /// reads. Each name maps to a <see cref="Enemy.Ai"/> register in declaration order; the
    /// bridge imports the captured value and the definition keeps the register up to date.
    /// </summary>
    public virtual IReadOnlyList<string> AiRegisterNames => [];

    /// <summary>
    /// Monster-private integer names that exist in the existing engine's fingerprint or continuation
    /// text but that no resolution path reads. The bridge accepts their captured entries without
    /// projecting them; any other unclaimed entry rejects the root.
    /// </summary>
    public virtual IReadOnlyList<string> InertMonsterIntNames => [];

    /// <summary>
    /// True when this monster carries an illusion revive: the rule compiler appends the synthetic
    /// REVIVE_MOVE node, and <see cref="Kernel.BeginIllusionRevive"/> may force it.
    /// </summary>
    public virtual bool IllusionRevive => false;

    /// <summary>
    /// Powers a newly summoned monster receives before its first action, mirroring
    /// <c>MonsterSpawnSupport.ApplyNativeEntrancePowers</c>.
    /// </summary>
    public virtual void ApplyEntrancePowers(ref Exec x, byte owner)
    {
    }

    /// <summary>
    /// <see cref="ApplyEntrancePowers(ref Exec, byte)"/> with the summoning owner, for entrance
    /// values that depend on the source (Axebot's replacement StockPower).
    /// </summary>
    public virtual void ApplyEntrancePowers(ref Exec x, byte owner, byte source)
        => ApplyEntrancePowers(ref x, owner);

    /// <summary>
    /// The monster can set <c>HpDisplay.InfiniteWithoutNumbers</c> (Waterfall Giant's steam-eruption
    /// rewrite). The rule compiler uses this instead of a native-id literal to decide whether
    /// Hellraiser's infinite-life branch needs its scan.
    /// </summary>
    public virtual bool HasInfiniteHpDisplay => false;

    /// <summary>
    /// Native <c>MonsterModel.StockAmount</c> used when the rule compiler creates this type's spawn
    /// rule entry, or -1 to use the canonical model. Axebot's replacement starts from BOOT_UP_MOVE
    /// and a +10 HP range, which requires a non-null StockAmount at compile time.
    /// </summary>
    public virtual int SpawnStockAmount => -1;

    /// <summary>
    /// The part of a move the existing engine runs before its attacks
    /// (<c>MonsterMoveEffects.ApplyBeforeAttack</c>), for example Living Fog's BLOAT_MOVE summon.
    /// </summary>
    public virtual void BeforeMove(ref Exec x, byte owner, int moveNode)
    {
    }

    /// <summary>
    /// Non-attack part of a move, run after its attack hits, mirroring
    /// <c>MonsterMoveEffects.Apply</c>. Moves without effects do nothing.
    /// </summary>
    public virtual void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
    }

    /// <summary>
    /// Test Subject style forms: the scorer's effective HP adds the later forms' HP from the AI
    /// registers while AdaptablePower keeps the body in combat.
    /// </summary>
    public virtual bool HasAdaptableFormHp => false;

    /// <summary>
    /// Special attack damage (<c>SimulatedCombatState.AdjustMonsterMoveDamage</c>): the compiled base
    /// damage plus the move's dynamic adjustment, clamped to zero by the damage pipeline.
    /// </summary>
    public virtual int ModifyAttackDamage(in Battle battle, Rules rules, byte owner, int moveNode,
        int attackIndex, int damage) => damage;

    /// <summary>Special attack hit count (<c>BranchMonsterAi.CurrentMove</c>, Test Subject's MULTI_CLAW).</summary>
    public virtual int ModifyAttackRepeats(in Battle battle, Rules rules, byte owner, int moveNode,
        int attackIndex, int repeats) => repeats;

    /// <summary>
    /// Special conditional-branch resolution (<c>BranchMonsterAi.ResolveConditional</c>); return -1
    /// to use the root-resolved target.
    /// </summary>
    public virtual int ResolveConditional(ref Exec x, byte owner, int branchNode) => -1;

    /// <summary>Special random-branch weights (<c>BranchMonsterAi.GetBranchWeight</c>).</summary>
    public virtual float BranchWeight(ref Exec x, byte owner, int branchNode, int option, float baseWeight) => baseWeight;

    /// <summary>
    /// The encounter slot the bridge uses when compiling this type's spawn rule entry, or null to use
    /// the first free slot. A monster whose native initial state depends on its slot name (Wriggler's
    /// INIT_MOVE) needs a valid fixed name so the private compile never sees an unmatched conditional.
    /// </summary>
    public virtual string? SpawnSlotName => null;

    /// <summary>
    /// The native model is created with <c>StartStunned = true</c>: its initial state machine node is
    /// the SPAWNED_MOVE move state instead of the INIT_MOVE conditional. The bridge applies this while
    /// compiling the spawn rule entry; the kernel spawns from that entry unchanged.
    /// </summary>
    public virtual bool SpawnedStunned => false;

    /// <summary>
    /// The native <c>ResolveReviveMove</c> case of a move, run before <see cref="ApplyMoveEffects"/>
    /// (<c>MonsterMoveEffects.Apply</c> starts with it). The default REVIVE_MOVE case is handled by the
    /// kernel; Reattach and Test Subject override this.
    /// </summary>
    public virtual void ResolveReviveMove(ref Exec x, byte owner, int moveNode)
    {
    }

    /// <summary>
    /// The move that <c>ForceStunnedMove</c> uses after a fully blocked attack while the owner has
    /// ImbalancedPower. <c>MonsterMoveSemantics</c> passes <c>HEADBUTT_MOVE</c> for BowlbugRock and the
    /// native default (most recent logged move) otherwise; -1 selects that default.
    /// </summary>
    public virtual int ImbalancedStunFollowUp(in MonsterRules rules) => -1;


}

/// <summary>
/// One monster the encounter generator produces. <see cref="Slot"/> is an index into
/// <see cref="EncounterDef.Slots"/> or <see cref="Kernel.NoSlot"/>; <see cref="Variant"/> is the
/// monster-specific encounter variant (constructor flags and initial move).
/// </summary>
public readonly record struct EncounterSpawn(ushort MonsterId, byte Slot, byte Variant, int HpReduction = 0);

/// <summary>
/// Deterministic per-encounter RNG. The native encounter RNG is
/// <c>seed + TotalFloor + hash(encounter id)</c> and is not carried past the encounter, so the
/// wrapper owns a private <see cref="RngState"/>.
/// </summary>
public struct EncounterRng(RngState state)
{
    public RngState State = state;

    public int NextInt(int maxExclusive) => State.NextInt(maxExclusive);

    public int NextInt(int minInclusive, int maxExclusive) => State.NextInt(minInclusive, maxExclusive);

    public bool NextBool() => State.NextBool();

    public int NextItem(int count) => State.NextInt(count);

    public int NextItem<T>(IReadOnlyList<T> items) => State.NextInt(items.Count);
}

/// <summary>
/// An encounter definition: its native slot names (the <c>EncounterModel.Slots</c> list the import
/// layer maps creature slot names through) and the ported <c>GenerateMonsters</c> semantics. The
/// generator writes into the caller's fixed span; running out is a capacity failure.
/// </summary>
public abstract class EncounterDef : ContentDef
{
    public virtual IReadOnlyList<string> Slots => [];

    /// <summary>
    /// The native event script ends the room before the player ever acts; the encounter exists for
    /// presentation only and has no combat end condition. The executor must reject it explicitly
    /// instead of driving a combat that cannot finish.
    /// </summary>
    public virtual bool EndsByEventScript => false;

    public abstract void Generate(ref EncounterRng rng, Span<EncounterSpawn> spawns, out int count);

    protected static ushort Monster(string nativeId)
        => Registry.TryMonster(nativeId, out ushort id)
            ? id
            : throw new UnsupportedException($"遭遇生成引用了未知怪物 {nativeId}。");

    protected byte Slot(string? name)
    {
        if (name == null)
            return Kernel.NoSlot;
        for (int index = 0; index < Slots.Count; index++)
        {
            if (string.Equals(Slots[index], name, StringComparison.Ordinal))
                return checked((byte)index);
        }
        throw new UnsupportedException($"遭遇 {NativeId} 没有槽位 {name}。");
    }

    /// <summary>
    /// The import layer maps a native creature slot name through <c>EncounterModel.Slots</c>; a name
    /// outside that list becomes <see cref="Kernel.NoSlot"/>. Encounter generation still uses the
    /// native name (Exoskeleton's conditional branch reads the creature's slot), but the stored row
    /// only keeps the mapped index.
    /// </summary>
    protected byte SlotOrNone(string? name)
    {
        if (name == null)
            return Kernel.NoSlot;
        for (int index = 0; index < Slots.Count; index++)
        {
            if (string.Equals(Slots[index], name, StringComparison.Ordinal))
                return checked((byte)index);
        }
        return Kernel.NoSlot;
    }

    protected static EncounterSpawn Spawn(string nativeId, byte slot = Kernel.NoSlot, byte variant = 0,
        int hpReduction = 0)
        => new(Monster(nativeId), slot, variant, hpReduction);

    protected static EncounterSpawn Spawn(ushort monsterId, byte slot = Kernel.NoSlot, byte variant = 0,
        int hpReduction = 0)
        => new(monsterId, slot, variant, hpReduction);

    protected static void Add(ref int count, Span<EncounterSpawn> spawns, in EncounterSpawn spawn)
    {
        if ((uint)count >= (uint)spawns.Length)
            throw new CapacityException("EncounterSpawns", spawns.Length);
        spawns[count++] = spawn;
    }
}

/// <summary>
/// Registry of all Sts2Solver content definitions in this assembly. Ids are assigned per kind in
/// ordinal order of native ids.
/// </summary>
public static class Registry
{
    private static readonly Lazy<Tables> Data = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object TestAssemblyGate = new();
    private static readonly List<Assembly> TestAssemblies = [];
    private static bool _buildStarted;

    /// <summary>
    /// Adds an assembly whose Sts2Solver content definitions participate in the registry's ordinal id
    /// assignment. Test-only: the core production registry scans its own assembly exactly as before.
    /// </summary>
    internal static void RegisterTestAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (TestAssemblyGate)
        {
            if (_buildStarted)
                throw new InvalidOperationException("Registry 已经构建，不能再登记测试程序集。");
            foreach (Assembly registered in TestAssemblies)
            {
                if (ReferenceEquals(registered, assembly))
                    return;
            }
            TestAssemblies.Add(assembly);
        }
    }

    public static int CardCount => Data.Value.Cards.Length;

    /// <summary>Union of the hooks any registered definition overrides; dispatch skips the rest outright.</summary>
    public static HookMask AnyHooks => Data.Value.AnyHooks;

    /// <summary>Union of the <see cref="HookMaskExt"/> hooks any registered definition overrides.</summary>
    public static HookMaskExt AnyHooksExt => Data.Value.AnyHooksExt;

    /// <summary>Union of the hooks implemented by card, affliction and enchantment definitions.</summary>
    public static HookMask CardHooks => Data.Value.CardHooks;

    public static HookMaskExt CardHooksExt => Data.Value.CardHooksExt;

    public static int PowerCount => Data.Value.Powers.Length;

    public static int RelicCount => Data.Value.Relics.Length;

    public static int MonsterCount => Data.Value.Monsters.Length;

    public static int PotionCount => Data.Value.Potions.Length;

    public static int EnchantmentCount => Data.Value.Enchantments.Length;

    public static int AfflictionCount => Data.Value.Afflictions.Length;

    public static int OrbCount => Data.Value.Orbs.Length;

    public static int EncounterCount => Data.Value.Encounters.Length;

    public static CardDef Card(ushort id) => Data.Value.Cards[id];

    public static PowerDef Power(ushort id) => Data.Value.Powers[id];

    public static RelicDef Relic(ushort id) => Data.Value.Relics[id];

    public static PotionDef Potion(ushort id) => Data.Value.Potions[id];

    public static MonsterDef Monster(ushort id) => Data.Value.Monsters[id];

    public static OrbDef Orb(ushort id) => Data.Value.Orbs[id];

    public static EnchantmentDef Enchantment(ushort id) => Data.Value.Enchantments[id];

    public static AfflictionDef Affliction(ushort id) => Data.Value.Afflictions[id];

    public static EncounterDef Encounter(ushort id) => Data.Value.Encounters[id];

    public static bool TryCard(string nativeId, out ushort id) => Data.Value.CardIds.TryGetValue(nativeId, out id);

    public static bool TryPower(string nativeId, out ushort id) => Data.Value.PowerIds.TryGetValue(nativeId, out id);

    public static bool TryRelic(string nativeId, out ushort id) => Data.Value.RelicIds.TryGetValue(nativeId, out id);

    /// <summary>Canonical id of the route-trait pocketwatch relic (0 when the content is absent).</summary>
    public static ushort PocketwatchRelicId
        => Data.Value.RelicIds.TryGetValue("POCKETWATCH", out ushort id) ? id : (ushort)0;

    /// <summary>Canonical id of the declined-extra-turn relic (0 when the content is absent).</summary>
    public static ushort PaelsEyeRelicId
        => Data.Value.RelicIds.TryGetValue("PAELS_EYE", out ushort id) ? id : (ushort)0;

    /// <summary>
    /// Canonical id of the Art of War relic (0 when the content is absent). <c>CanTriggerArtOfWarNextTurn</c>
    /// reads the same <c>StatefulRelicState.Current == 0</c>; the Sts2Solver definition keeps it in
    /// <see cref="Relic.Counter"/>.
    /// </summary>
    public static ushort ArtOfWarRelicId
        => Data.Value.RelicIds.TryGetValue("ART_OF_WAR", out ushort id) ? id : (ushort)0;

    public static bool TryPotion(string nativeId, out ushort id) => Data.Value.PotionIds.TryGetValue(nativeId, out id);

    public static bool TryMonster(string nativeId, out ushort id) => Data.Value.MonsterIds.TryGetValue(nativeId, out id);

    public static bool TryOrb(string nativeId, out ushort id) => Data.Value.OrbIds.TryGetValue(nativeId, out id);

    public static bool TryEnchantment(string nativeId, out ushort id) => Data.Value.EnchantmentIds.TryGetValue(nativeId, out id);

    public static bool TryAffliction(string nativeId, out ushort id) => Data.Value.AfflictionIds.TryGetValue(nativeId, out id);

    public static bool TryEncounter(string nativeId, out ushort id) => Data.Value.EncounterIds.TryGetValue(nativeId, out id);

    public static ushort PowerId<T>() where T : PowerDef => PowerIdCache<T>.Id;

    public static ushort CardId<T>() where T : CardDef => CardIdCache<T>.Id;

    public static ushort MonsterId<T>() where T : MonsterDef => MonsterIdCache<T>.Id;

    public static ushort AfflictionId<T>() where T : AfflictionDef => AfflictionIdCache<T>.Id;

    public static ushort EnchantmentId<T>() where T : EnchantmentDef => EnchantmentIdCache<T>.Id;

    private static class PowerIdCache<T> where T : PowerDef
    {
        public static readonly ushort Id = Data.Value.ByType.TryGetValue(typeof(T), out ContentDef? def)
            ? def.Id
            : throw new InvalidOperationException($"{typeof(T).Name} 没有登记为 Sts2Solver Power。");
    }

    private static class CardIdCache<T> where T : CardDef
    {
        public static readonly ushort Id = Data.Value.ByType.TryGetValue(typeof(T), out ContentDef? def)
            ? def.Id
            : throw new InvalidOperationException($"{typeof(T).Name} 没有登记为 Sts2Solver 卡牌。");
    }

    private static class MonsterIdCache<T> where T : MonsterDef
    {
        public static readonly ushort Id = Data.Value.ByType.TryGetValue(typeof(T), out ContentDef? def)
            ? def.Id
            : throw new InvalidOperationException($"{typeof(T).Name} 没有登记为 Sts2Solver 怪物。");
    }

    private static class AfflictionIdCache<T> where T : AfflictionDef
    {
        public static readonly ushort Id = Data.Value.ByType.TryGetValue(typeof(T), out ContentDef? def)
            ? def.Id
            : throw new InvalidOperationException($"{typeof(T).Name} 没有登记为 Sts2Solver 苦难。");
    }

    private static class EnchantmentIdCache<T> where T : EnchantmentDef
    {
        public static readonly ushort Id = Data.Value.ByType.TryGetValue(typeof(T), out ContentDef? def)
            ? def.Id
            : throw new InvalidOperationException($"{typeof(T).Name} 没有登记为 Sts2Solver 附魔。");
    }

    private sealed record Tables(
        CardDef[] Cards, Dictionary<string, ushort> CardIds,
        PowerDef[] Powers, Dictionary<string, ushort> PowerIds,
        RelicDef[] Relics, Dictionary<string, ushort> RelicIds,
        PotionDef[] Potions, Dictionary<string, ushort> PotionIds,
        MonsterDef[] Monsters, Dictionary<string, ushort> MonsterIds,
        OrbDef[] Orbs, Dictionary<string, ushort> OrbIds,
        EnchantmentDef[] Enchantments, Dictionary<string, ushort> EnchantmentIds,
        AfflictionDef[] Afflictions, Dictionary<string, ushort> AfflictionIds,
        EncounterDef[] Encounters, Dictionary<string, ushort> EncounterIds,
        Dictionary<Type, ContentDef> ByType)
    {
        public HookMask AnyHooks { get; } = ByType.Values.Aggregate(HookMask.None, (mask, def) => mask | def.Hooks);

        public HookMaskExt AnyHooksExt { get; } = ByType.Values.Aggregate(HookMaskExt.None, (mask, def) => mask | def.HooksExt);

        /// <summary>
        /// Hooks any card, affliction or enchantment definition can answer. The listener scan only
        /// visits the card zone when the requested hook intersects this union; the per-card
        /// <c>Has</c> test would reject every card otherwise.
        /// </summary>
        public HookMask CardHooks { get; } = Cards.Aggregate(HookMask.None, (mask, def) => mask | def.Hooks)
            | Afflictions.Aggregate(HookMask.None, (mask, def) => mask | def.Hooks)
            | Enchantments.Aggregate(HookMask.None, (mask, def) => mask | def.Hooks);

        public HookMaskExt CardHooksExt { get; } =
            Cards.Aggregate(HookMaskExt.None, (mask, def) => mask | def.HooksExt)
            | Afflictions.Aggregate(HookMaskExt.None, (mask, def) => mask | def.HooksExt)
            | Enchantments.Aggregate(HookMaskExt.None, (mask, def) => mask | def.HooksExt);
    }

    private static Tables Build()
    {
        Assembly[] testAssemblies;
        lock (TestAssemblyGate)
        {
            _buildStarted = true;
            testAssemblies = [.. TestAssemblies];
        }
        List<Type> typeList = [.. typeof(Registry).Assembly.GetTypes()];
        foreach (Assembly assembly in testAssemblies)
            typeList.AddRange(assembly.GetTypes());
        Type[] types = [.. typeList];
        Dictionary<Type, ContentDef> byType = [];
        var cards = Collect<CardDef>(types, byType, ushort.MaxValue);
        var powers = Collect<PowerDef>(types, byType, ushort.MaxValue);
        var relics = Collect<RelicDef>(types, byType, ushort.MaxValue);
        var potions = Collect<PotionDef>(types, byType, ushort.MaxValue);
        var monsters = Collect<MonsterDef>(types, byType, ushort.MaxValue);
        var orbs = Collect<OrbDef>(types, byType, byte.MaxValue);
        var enchantments = Collect<EnchantmentDef>(types, byType, byte.MaxValue);
        var afflictions = Collect<AfflictionDef>(types, byType, byte.MaxValue);
        var encounters = Collect<EncounterDef>(types, byType, ushort.MaxValue);
        return new Tables(
            cards.Defs, cards.Ids, powers.Defs, powers.Ids, relics.Defs, relics.Ids,
            potions.Defs, potions.Ids, monsters.Defs, monsters.Ids, orbs.Defs, orbs.Ids,
            enchantments.Defs, enchantments.Ids, afflictions.Defs, afflictions.Ids,
            encounters.Defs, encounters.Ids, byType);
    }

    private static (T[] Defs, Dictionary<string, ushort> Ids) Collect<T>(
        Type[] types, Dictionary<Type, ContentDef> byType, int maxCount)
        where T : ContentDef
    {
        List<(string NativeId, Type Type)> found = [];
        foreach (Type type in types)
        {
            if (!typeof(T).IsAssignableFrom(type) || type.IsAbstract)
                continue;
            ContentIdAttribute attribute = type.GetCustomAttribute<ContentIdAttribute>()
                ?? throw new InvalidOperationException($"Sts2Solver 定义 {type.FullName} 缺少 [ContentId]。");
            found.Add((attribute.NativeId, type));
        }
        found.Sort((left, right) => string.CompareOrdinal(left.NativeId, right.NativeId));
        if (found.Count > maxCount)
            throw new CapacityException(typeof(T).Name, maxCount);
        T[] defs = new T[found.Count];
        Dictionary<string, ushort> ids = new(found.Count, StringComparer.Ordinal);
        for (int index = 0; index < found.Count; index++)
        {
            (string nativeId, Type type) = found[index];
            T def = (T)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"无法创建 Sts2Solver 定义 {type.FullName}。"));
            def.NativeId = nativeId;
            def.Id = (ushort)index;
            IReadOnlyList<string> variableNames = def.VarNames;
            def.MutableVarCount = (byte)Math.Min(2, variableNames.Count);
            def.FirstMutableVarInSort = (byte)(variableNames.Count > 1
                && string.CompareOrdinal(variableNames[0], variableNames[1]) > 0 ? 1 : 0);
            def.Hooks = (HookMask)ComputeHooks<HookMask>(type);
            def.HooksExt = (HookMaskExt)ComputeHooks<HookMaskExt>(type);
            if (!ids.TryAdd(nativeId, (ushort)index))
                throw new InvalidOperationException($"原版 Id {nativeId} 有多个 Sts2Solver {typeof(T).Name} 定义。");
            defs[index] = def;
            byType.Add(type, def);
        }
        return (defs, ids);
    }

    /// <summary>
    /// Hooks <paramref name="type"/> overrides: for each mask name, the virtual declared on
    /// <see cref="ListenerDef"/> and whether the type overrides exactly that method (overloads with
    /// the same name on derived bases, such as phase methods, do not count).
    /// </summary>
    private static ulong ComputeHooks<TMask>(Type type) where TMask : struct, Enum
    {
        ulong mask = 0;
        MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach (TMask hook in Enum.GetValues<TMask>())
        {
            ulong bit = Convert.ToUInt64(hook);
            // Composite masks (RunListenerHooks and similar) name no single hook.
            if (System.Numerics.BitOperations.PopCount(bit) != 1)
                continue;
            MethodInfo baseMethod = typeof(ListenerDef).GetMethod(hook.ToString(), BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"ListenerDef 没有与 Hook {hook} 同名的方法。");
            foreach (MethodInfo method in methods)
            {
                if (method.DeclaringType != typeof(ListenerDef) && method.GetBaseDefinition() == baseMethod)
                {
                    mask |= bit;
                    break;
                }
            }
        }
        return mask;
    }
}
