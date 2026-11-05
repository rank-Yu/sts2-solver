using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;

namespace Sts2Solver.Ai.Combat.Scoring;

/// <summary>Strategic-effect classification of one admitted power (port of <c>StrategicEffectModel.Evaluate</c>).</summary>
internal enum StrategicKind : byte
{
    Scaling = 0,
    Afterimage,
    Buffer,
    FeelNoPain,
    DarkEmbrace,
    Thorns,
    EchoForm,
    Envenom,
    Accuracy,
    SleightOfFlesh,
    Strength,
    Lethality,
    ReaperForm,
    Dexterity,
    DemonForm,
    Curious,
    Corruption,
    CreativeAi,
    Iteration,
    MasterPlanner,
    Focus,
    Furnace,
    Thunder,
    LightningRod,

    /// <summary>Pagestorm under <c>Act3BossInteractions</c>: future ethereal draws.</summary>
    Pagestorm,

    /// <summary>Danse Macabre under <c>Act3BossInteractions</c>: block from high-energy plays.</summary>
    DanseMacabre,

    /// <summary>Demesne under <c>Act3BossInteractions</c>: future energy and draws.</summary>
    Demesne,

    /// <summary>Prep Time under <c>Act3BossInteractions</c>: recurring Vigor damage.</summary>
    PrepTime,

    /// <summary>Automation: recurring energy from drawn cards in the refund window.</summary>
    RecurringEnergy,

    /// <summary>Orbit: recurring energy from the refund window's energy spend plus the remainder.</summary>
    OrbitEnergy,
}

/// <summary>Port of <c>StrategicEffectRequirements</c>; bit values must match the existing enum.</summary>
[Flags]
internal enum StrategicRequirements
{
    None = 0,
    RemainingTurns = 1 << 0,
    UsefulCardPlays = 1 << 1,
    AttackPlays = 1 << 2,
    SkillPlays = 1 << 3,
    BlockSkillPlays = 1 << 4,
    PowerPlays = 1 << 5,
    ExhaustPlays = 1 << 6,
    ShivPlays = 1 << 7,
    DebuffApplications = 1 << 8,
    SkillEnergySpend = 1 << 9,
    PowerEnergySpend = 1 << 10,
    AverageCardValue = 1 << 11,
    BestCardValue = 1 << 12,
    AverageAttackValue = 1 << 13,
    StatusDrawTriggers = 1 << 14,
    AttackHits = 1 << 15,
}

/// <summary>Port of <c>PersistentSetupTraits</c>; bit values must match the existing enum.</summary>
[Flags]
internal enum SetupTraits
{
    None = 0,
    Curious = 1 << 0,
    EchoForm = 1 << 1,
    Buffer = 1 << 2,
    Focus = 1 << 3,
    Thunder = 1 << 4,
    RecurringScaling = 1 << 5,
    OrbEngine = 1 << 6,
}

/// <summary>Which calculated-damage multiplier the card's native spec uses (port of <c>CalculatedVarSpecRegistry</c> for the admitted cards).</summary>
internal enum CalculatedKind : byte
{
    None = 0,
    PlayerBlock,
    StrikeCardCount,
    ExhaustPileCount,
    PlayerStrength,
    TargetVulnerable,
    DrawPileCount,
    TargetDebuffPowerCount,
    FinishedCardPlays,
    CardsDiscardedThisTurn,
    NegativeHandCount,
    CardsDrawn,

    /// <summary>CrescentSpear: cards in the combat piles whose canonical star cost is ≥ 0 or X.</summary>
    StarCardCount,

    /// <summary>Unleash: Osty's current HP.</summary>
    OstyCurrentHp,

    /// <summary>Protector: Osty's max HP.</summary>
    OstyMaxHp,

    /// <summary>Rattle: 1 + Osty's attacks this turn.</summary>
    OstyAttacksPlusOne,

    /// <summary>Squeeze: every other OstyAttack card in the combat piles.</summary>
    OstyAttackCardsMinusSelf,

    /// <summary>SoulStorm: Soul cards in the exhaust pile.</summary>
    ExhaustSoulCount,

    /// <summary>PullFromBelow: Ethereal card plays this combat.</summary>
    EtherealPlays,

    /// <summary>NoEscape: floor(target Doom / DoomThreshold).</summary>
    DoomOverThreshold,

    /// <summary>TimesUp: the target's Doom amount.</summary>
    TargetDoom,

    /// <summary>Supermassive: cards the player generated this combat.</summary>
    GeneratedCardCount,

    /// <summary>DeathMarch: non-hand draws this turn.</summary>
    NonHandDraws,

    /// <summary>A registered calculated-damage card the port does not model; the root is rejected.</summary>
    Unsupported,
}

/// <summary>Port of the per-card attack-hit fact (<c>CardMechanismFacts.AttackHits</c>).</summary>
internal enum AttackHitKind : byte
{
    One = 0,
    Two,
    RepeatVar,
}

/// <summary>Port of the per-card immediate shiv supply fact (<c>CardMechanismFacts.ImmediateShivSupply</c>).</summary>
internal enum ShivSupplyKind : byte
{
    None = 0,
    CardsVar,
    ShivsVar,
}

/// <summary>
/// Root-frozen per-content evaluation values for one card id at one upgrade level. Every admitted card
/// has an entry; the values themselves are read from the existing engine's models when
/// The request compiler builds the table.
/// </summary>
internal sealed class CardScore
{
    /// <summary><c>CardChoiceSupport.DynamicVarBaseValue</c> of the canonical upgraded card's named variables.</summary>
    public double BaseDamage { get; init; }

    public double BaseBlock { get; init; }

    public double BaseCards { get; init; }

    /// <summary>The card is a power; <c>CardChoiceSupport.CardValue</c> adds the flat 8.</summary>
    public bool IsPower { get; init; }

    /// <summary>
    /// <c>CardChoiceSupport</c> gives this played card a primary <c>MoveToHand</c> choice: Hologram,
    /// Graveblast, Secret Weapon/Technique, Wish, Dredge, Neow's Fury and Seeker Strike.
    /// </summary>
    public bool PrimaryMoveToHandChoice { get; init; }

    /// <summary>Index of the named variables inside <see cref="CardStats.Vars"/>, or -1.</summary>
    public sbyte DamageVar { get; init; } = -1;

    public sbyte BlockVar { get; init; } = -1;

    public sbyte CardsVar { get; init; } = -1;

    public sbyte RepeatVar { get; init; } = -1;

    public sbyte ShivCardsVar { get; init; } = -1;

    public sbyte ShivShivsVar { get; init; } = -1;

    public sbyte CalculationBaseVar { get; init; } = -1;

    public sbyte CalculationExtraVar { get; init; } = -1;

    /// <summary>NoEscape's <c>DoomThreshold</c> variable index, or -1.</summary>
    public sbyte DoomThresholdVar { get; init; } = -1;

    public AttackHitKind AttackHits { get; init; }

    public ShivSupplyKind ShivSupply { get; init; }

    /// <summary>The card has any dynamic variable whose key names a block (port of <c>StrategicEffectModel.IsBlockDynamicVar</c>).</summary>
    public bool HasBlockDynamicVar { get; init; }

    /// <summary>The card has any dynamic variable whose key names a debuff (port of <c>StrategicEffectModel.IsDebuffDynamicVar</c>).</summary>
    public bool HasDebuffDynamicVar { get; init; }

    public bool HasTurnEndInHandEffect { get; init; }

    public bool HasOstyAttackTag { get; init; }

    public bool HasShivTag { get; init; }

    public int LatentValue { get; init; }

    public SetupTraits LatentTrait { get; init; }

    /// <summary>
    /// The definition's <see cref="Sts2Solver.Simulation.Combat.CardDef.HasDynamicCardType"/>:
    /// the live type (and MadScience's latent value/trait) come from the card value, not this table.
    /// </summary>
    public bool DynamicCardType { get; init; }

    public CalculatedKind Calculated { get; init; }
}

/// <summary>Root-frozen strategic-effect values of one admitted power.</summary>
internal sealed class PowerScore
{
    public StrategicKind Kind { get; init; }

    public StrategicRequirements Requirements { get; init; }

    public SetupTraits Trait { get; init; }

    public bool IsTemporary { get; init; }

    public PowerType Type { get; init; }

    /// <summary>
    /// <c>DanseMacabrePower.DynamicVars.Energy.IntValue</c>: the resolved-cost threshold that turns a
    /// play into block. Zero for every other power.
    /// </summary>
    public int DanseMacabreEnergyThreshold { get; init; }
}

/// <summary>One configured relic counter goal, with the Sts2Solver relic id that carries its counter.</summary>
internal readonly record struct RelicCounterRule(
    RelicCounterTarget Target,
    ushort RelicId,
    int MeatOnTheBoneThreshold);

/// <summary>
/// Everything <see cref="Scorer"/> reads besides the <see cref="Battle"/> and
/// <see cref="Rules"/>: per-card and per-power evaluation values compiled from the existing
/// engine at the root, plus the request settings the evaluation freezes.
/// </summary>
internal sealed class ScoreRules
{
    public required Rules Rules { get; init; }

    /// <summary>Indexed by card id, then upgrade level; null when the rule table has no such level.</summary>
    public required CardScore?[][] Cards { get; init; }

    /// <summary>Indexed by power id.</summary>
    public required PowerScore[] Powers { get; init; }

    /// <summary>Indexed by potion id; <c>PotionUsePolicy.StrategicHpCost</c> of the canonical potion.</summary>
    public required int[] PotionStrategicCost { get; init; }

    /// <summary>HP credit per realized growth reward.</summary>
    public GrowthValues GrowthBudgets { get; init; }

    /// <summary>
    /// <c>GrowthOpportunityTargets.RequiredRewards</c>: the bounded goals the HP-target stop must
    /// satisfy; empty when the root has no bounded growth target.
    /// </summary>
    public GrowthValues RequiredGrowthRewards { get; init; }

    /// <summary>Configured relic counter goals in the policy order (the captured list is id-sorted).</summary>
    public RelicCounterRule[] RelicCounterRules { get; init; } = [];

    /// <summary>The existing final ordering's post-combat relic heal profile.</summary>
    public PostCombatRelicHealProfile PostCombatRelicHeal { get; init; } = PostCombatRelicHealProfile.None;

    /// <summary>The existing final ordering's strategic boss HP relief.</summary>
    public BossHpRelief BossHpRelief { get; init; }

    public required int InitialPlayerMaxHp { get; init; }

    public required bool IsActEndingBoss { get; init; }

    /// <summary>
    /// The request's Act3 boss strategy, frozen at the root. Gates the reference engine's
    /// three-act boss interaction requirements/evaluation and the fetched-power commitment on every searched turn.
    /// </summary>
    public required bool Act3BossStrategy { get; init; }

    public required bool IgnoreLongTermRewards { get; init; }

    public required TheftPolicy? TheftPolicy { get; init; }

    public required int InitialEnemyCount { get; init; }

    public required int InitialPersistentBuffValue { get; init; }

    public required int InitialEnemyStrengthSuppression { get; init; }

    public required int InitialEnemyWeakTurns { get; init; }

    public required int InitialRetainedAttackValue { get; init; }

    /// <summary>Root potion ids by slot (0 = empty); used to derive the potion-use counters.</summary>
    public required ushort[] RootPotions { get; init; }

    /// <summary>
    /// Cumulative potion counters at the search root. Production has no fixed prefix and passes
    /// zero; D1 configs keep the values that reproduce their recorded score hashes.
    /// </summary>
    public int InitialPotionUseCount { get; init; }

    public int InitialPotionStrategicCost { get; init; }

    public int InitialExplicitPotionStrategicCost { get; init; }

    /// <summary>Power / card ids the port names directly, or <see cref="ushort.MaxValue"/> when unregistered.</summary>
    public ushort StrengthPowerId { get; init; } = ushort.MaxValue;

    public ushort WeakPowerId { get; init; } = ushort.MaxValue;

    public ushort VulnerablePowerId { get; init; } = ushort.MaxValue;

    public ushort PoisonPowerId { get; init; } = ushort.MaxValue;

    public ushort DemisePowerId { get; init; } = ushort.MaxValue;

    public ushort DoomPowerId { get; init; } = ushort.MaxValue;

    /// <summary><c>SandpitPower</c>: the enemy-side turn-start countdown read by the control term.</summary>
    public ushort SandpitPowerId { get; init; } = ushort.MaxValue;

    /// <summary><c>SummonNextTurnPower</c>: its amount is the summon-next-turn future-resource term.</summary>
    public ushort SummonNextTurnPowerId { get; init; } = ushort.MaxValue;

    /// <summary><c>DieForYouPower</c>: the Osty redirect read by the threat projection.</summary>
    public ushort DieForYouPowerId { get; init; } = ushort.MaxValue;

    public ushort SleightOfFleshPowerId { get; init; } = ushort.MaxValue;

    public ushort RetainHandPowerId { get; init; } = ushort.MaxValue;

    public ushort EnergyNextTurnPowerId { get; init; } = ushort.MaxValue;

    public ushort DrawCardsNextTurnPowerId { get; init; } = ushort.MaxValue;

    public ushort StarNextTurnPowerId { get; init; } = ushort.MaxValue;

    public ushort FreeAttackPowerId { get; init; } = ushort.MaxValue;

    /// <summary><c>VoidFormPower</c>: the free-play window and future-resource term.</summary>
    public ushort VoidFormPowerId { get; init; } = ushort.MaxValue;

    public ushort FreeSkillPowerId { get; init; } = ushort.MaxValue;

    public ushort FreePowerPowerId { get; init; } = ushort.MaxValue;

    public ushort IllusionPowerId { get; init; } = ushort.MaxValue;

    public ushort ReattachPowerId { get; init; } = ushort.MaxValue;

    public ushort AdaptablePowerId { get; init; } = ushort.MaxValue;

    public ushort BodySlamCardId { get; init; } = ushort.MaxValue;

    /// <summary><c>Eradicate</c>: first-attack damage counts every payable hit of the X-cost attack.</summary>
    public ushort EradicateCardId { get; init; } = ushort.MaxValue;

    /// <summary><c>Fiddle</c>: while unmelted, future Pagestorm bonus draws are blocked.</summary>
    public ushort FiddleRelicId { get; init; } = ushort.MaxValue;

    /// <summary><c>DropletOfPrecognition</c>: the only potion whose choice effect is <c>MoveToHand</c>.</summary>
    public ushort DropletOfPrecognitionPotionId { get; init; } = ushort.MaxValue;

    public ushort TheHuntCardId { get; init; } = ushort.MaxValue;

    public ushort TheHuntPowerId { get; init; } = ushort.MaxValue;

    public ushort BufferPowerId { get; init; } = ushort.MaxValue;

    public ushort NoDrawPowerId { get; init; } = ushort.MaxValue;

    public ushort TheGambitPowerId { get; init; } = ushort.MaxValue;

    public ushort FairyInABottlePotionId { get; init; } = ushort.MaxValue;

    public ushort AmbergrisPotionId { get; init; } = ushort.MaxValue;

    public ushort LizardTailRelicId { get; init; } = ushort.MaxValue;

    public required int RootPotionSlotCount { get; init; }

    public CardScore Card(ushort id, int upgrade)
    {
        CardScore?[]? levels = id < Cards.Length ? Cards[id] : null;
        return levels != null && (uint)upgrade < (uint)levels.Length && levels[upgrade] is { } score
            ? score
            : throw new UnsupportedException($"评分表缺少卡牌 {Registry.Card(id).NativeId}+{upgrade}。");
    }

    public PowerScore Power(ushort id)
        => id < Powers.Length && Powers[id] is { } score
            ? score
            : throw new UnsupportedException($"评分表缺少 Power {Registry.Power(id).NativeId}。");
}
