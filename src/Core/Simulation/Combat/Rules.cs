namespace Sts2Solver.Simulation.Combat;

public enum CardType : byte
{
    None = 0,
    Attack = 1,
    Skill = 2,
    Power = 3,
    Status = 4,
    Curse = 5,
    Quest = 6,
}

public enum TargetType : byte
{
    None = 0,
    Self = 1,
    AnyEnemy = 2,
    AllEnemies = 3,
    RandomEnemy = 4,
    AnyPlayer = 5,
    AnyAlly = 6,
    AllAllies = 7,
    TargetedNoCreature = 8,
    Osty = 9,
}

/// <summary>The canonical card pool a card belongs to, used by transformation's pool choice.</summary>
public enum TransformPool : byte
{
    Character = 0,
    Colorless = 1,
    Status = 2,
    Curse = 3,
}

public enum CardRarity : byte
{
    None = 0,
    Basic = 1,
    Common = 2,
    Uncommon = 3,
    Rare = 4,
    Ancient = 5,
    Event = 6,
    Token = 7,
    Status = 8,
    Curse = 9,
    Quest = 10,
}

/// <summary>Native <c>CardTag</c> set of a card, one bit per tag.</summary>
[Flags]
public enum CardTag : ushort
{
    None = 0,
    Strike = 1 << 0,
    Defend = 1 << 1,
    Minion = 1 << 2,
    OstyAttack = 1 << 3,
    Shiv = 1 << 4,
    Shoot = 1 << 5,
    Wah = 1 << 6,
    Cho = 1 << 7,
    Ki = 1 << 8,
}

/// <summary>Root-frozen data of one card id at one upgrade level.</summary>
public sealed class CardStats
{
    public required CardType Type { get; init; }
    public required TargetType Target { get; init; }
    public required CardRarity Rarity { get; init; }

    /// <summary><c>CardModel.Pool</c> category for transformation; defaults to the character pool.</summary>
    public TransformPool Pool { get; init; } = TransformPool.Character;
    public required int EnergyCost { get; init; }
    public required bool CostsX { get; init; }
    public required int StarCost { get; init; }
    public required bool StarCostsX { get; init; }
    public required Keywords Keywords { get; init; }

    /// <summary>
    /// True when <see cref="CardDef.ResolveCardType"/> can differ from <see cref="Type"/> for this id.
    /// Roots without such cards skip the per-card virtual dispatch in the evaluation.
    /// </summary>
    public bool HasDynamicType { get; init; }

    /// <summary>Native <c>MaxUpgradeLevel</c>; the card is upgradable while its level is below it.</summary>
    public required int MaxUpgradeLevel { get; init; }

    /// <summary>Canonical native tags (Strike, Defend, Minion, Osty attack, Shiv).</summary>
    public CardTag Tags { get; init; }

    /// <summary>Native <c>Soul</c> token: the existing engine's <c>persistentBuffValue</c> counts these cards.</summary>
    public bool IsSoul { get; init; }

    public bool GainsBlock { get; init; }

    /// <summary>
    /// Native <c>CardModel.VisualCardPool.IsColorless</c> (HeirloomHammer's hand filter). Frozen
    /// separately from pool membership because token colorless cards are not in the generation pool.
    /// </summary>
    public bool IsColorless { get; init; }

    /// <summary>
    /// The existing engine's OnPlay mirror dispatch is inferred or unsupported for this card, so every
    /// play records a prediction risk history entry (<c>MethodMirrorIncomplete</c>/<c>MethodNotMirrored</c>).
    /// The routing trait's <c>pure</c> input is false for such a play.
    /// </summary>
    public bool PlaysRecordRisk { get; init; }

    /// <summary>Base values of the native dynamic variables named by the card definition, in order.</summary>
    public required decimal[] Vars { get; init; }
}

public enum MonsterNodeKind : byte
{
    Move = 0,
    RandomBranch = 1,
    ConditionalBranch = 2,
}

/// <summary>Native <c>MoveRepeatType</c>.</summary>
public enum MoveRepeatType : byte
{
    CanRepeatForever = 0,
    CanRepeatXTimes = 1,
    CannotRepeat = 2,
    UseOnlyOnce = 3,
}

public readonly record struct MonsterAttack(int BaseDamage, int Repeats);

public readonly record struct RandomBranchOption(
    int Target,
    float BaseWeight,
    MoveRepeatType RepeatType,
    int MaxTimes,
    int Cooldown);

/// <summary>One node of a compiled native monster move state machine.</summary>
public sealed class MonsterNode
{
    public required string NativeId { get; init; }
    public required MonsterNodeKind Kind { get; init; }

    /// <summary>Follow-up node of a move node.</summary>
    public int FollowUp { get; init; } = -1;

    /// <summary>
    /// Native <c>MoveState.MustPerformOnceBeforeTransitioning</c>: the machine does not advance while
    /// the round ended without this move being performed (<c>SimulatedCombatState.MonsterAi.cs:122</c>).
    /// The synthetic STUNNED node carries it too; its follow-up lives in <see cref="Enemy.ForcedMove"/>.
    /// </summary>
    public bool MustPerformOnceBeforeTransitioning { get; init; }

    public MonsterAttack[] Attacks { get; init; } = [];
    public RandomBranchOption[] Options { get; init; } = [];

    /// <summary>Root-resolved target of a conditional branch, or -1 when unresolved.</summary>
    public int ConditionalTarget { get; init; } = -1;
}

/// <summary>Root-frozen data of one known enemy: its compiled state machine and static values.</summary>
public sealed class MonsterRules
{
    /// <summary>Internal id of the monster type (<see cref="Registry.Monster"/>).</summary>
    public required ushort MonsterId { get; init; }

    public required MonsterNode[] Nodes { get; init; }

    public required int InitialNode { get; init; }

    /// <summary>
    /// The native <c>MoveState</c> a freshly created monster starts from before its initial roll
    /// (<c>SimulatedCombatState.MonsterAi.cs:148-166</c>: the first registered <c>MoveState</c> when
    /// the model's state log is empty). Only observable while a creature spawned during the enemy
    /// turn waits for <see cref="EnemyFlags.NeedsInitialRoll"/>.
    /// </summary>
    public int PendingInitialMoveNode { get; init; }

    /// <summary>
    /// The state-machine constructor already appended <see cref="InitialNode"/> to the native
    /// <c>MoveStateMachine.StateLog</c> (the initial state is a <c>MoveState</c>). A monster created
    /// during the enemy turn then adopts that log entry instead of waiting for
    /// <see cref="EnemyFlags.NeedsInitialRoll"/>
    /// (<c>SimulatedCombatState.RegisterPendingInitialMonsterAi</c>).
    /// </summary>
    public bool InitialMoveLogged { get; init; }

    /// <summary>
    /// The synthetic STUNNED node appended after every native state (and the unregistered initial
    /// branch). It has no attacks or effects, is must-perform-once, and its follow-up is the dynamic
    /// <see cref="Enemy.ForcedMove"/> (<c>SimulatedCombatState.MonsterAi.cs:76-86,102-128</c>).
    /// </summary>
    public required int StunnedNode { get; init; }

    /// <summary>
    /// The synthetic REVIVE_MOVE node of a monster with an illusion revive
    /// (<c>SimulatedCombatState.DeathLifecycle.cs:100-119</c>), or -1. Its follow-up is the dynamic
    /// <see cref="Enemy.ForcedMove"/>.
    /// </summary>
    public int ReviveNode { get; init; } = -1;

    /// <summary>Native <c>MinInitialHp</c>/<c>MaxInitialHp</c> of a newly created monster.</summary>
    public required int InitialHpMin { get; init; }

    public required int InitialHpMax { get; init; }

    /// <summary>Static integers named by the monster definition, in order.</summary>
    public required int[] StaticInts { get; init; }

    /// <summary>
    /// Root power amounts named by <see cref="MonsterDef.RootPowerAmountNames"/>, in order. They
    /// are frozen at root import, matching the existing engine's <c>_rootPowerAmounts</c> baseline for
    /// dynamic monster damage (<c>AdjustMonsterMoveDamage</c>).
    /// </summary>
    public int[] RootPowerAmounts { get; init; } = [];

    /// <summary>
    /// Values of the monster-private integers named by <see cref="MonsterDef.AiRegisterNames"/>
    /// on a freshly created monster, in order (captured through the same <c>GetMonsterInt</c> path).
    /// </summary>
    public int[] InitialAiRegisters { get; init; } = [];

    public int NodeIndex(string nativeId)
    {
        for (int index = 0; index < Nodes.Length; index++)
        {
            if (Nodes[index].NativeId == nativeId)
                return index;
        }
        throw new UnsupportedException($"怪物状态机没有节点 {nativeId}。");
    }
}

/// <summary>Native <c>PotionRarity</c>; values match the native enum.</summary>
public enum PotionRarity : byte
{
    None = 0,
    Common = 1,
    Uncommon = 2,
    Rare = 3,
    Event = 4,
    Token = 5,
}

/// <summary>Canonical values of a potion: its target type and the dynamic variables its definition names.</summary>
public sealed class PotionStats
{
    public required TargetType Target { get; init; }

    public required PotionRarity Rarity { get; init; }

    /// <summary><c>PotionUsePolicy.StrategicHpCost</c> of the canonical potion.</summary>
    public required int StrategicCost { get; init; }

    public required decimal[] Vars { get; init; }

    /// <summary>Value props of each named variable (damage and block variables carry them; others are None).</summary>
    public required ValueProps[] VarProps { get; init; }
}

/// <summary>Root-frozen candidate list of one card-generation pool, in the existing engine's order.</summary>
public sealed class CardPool
{
    public required ushort[] Cards { get; init; }
}

/// <summary>
/// Immutable per-root rule tables shared by every branch of one Sts2Solver solve. Values come from
/// native models at root import; content definitions read numbers from here instead of
/// hard-coding them.
/// </summary>
public sealed class Rules
{
    // Bound from the battle snapshot, not the content RULE table; shared by all nodes of a request.
    private RelicRoster _rootRelics = RelicRoster.Empty;
    public RelicRoster Relics => _rootRelics;
    public Rules WithRelics(RelicRoster relics)
    {
        Rules copy = (Rules)MemberwiseClone();
        copy._rootRelics = relics;
        return copy;
    }

    /// <summary>
    /// Number of constructor inputs that form the serialized schema. The K0 codec contract checks
    /// this against the writer's field count and mutates each field to prove it reaches the bytes.
    /// </summary>
    public const int SchemaFieldCount = 32;

    private readonly CardStats?[][] _cards;
    private readonly decimal[][] _powerVars;
    private readonly PotionStats?[] _potions;
    private readonly decimal[][] _relicVars;
    private readonly MonsterRules?[] _monsters;
    private readonly Dictionary<ushort, int> _spawnRules;
    private readonly Dictionary<string, CardPool> _cardPools;
    private readonly ushort[] _potionPool;
    private readonly ushort[] _outOfCombatPotionPool;
    private readonly ushort[] _rootPotions;
    private readonly string[] _encounterSlots;
    private readonly bool[] _powerDeathHistoryRisk;

    internal Rules(
        CardStats?[][] cards,
        decimal[][] powerVars,
        PotionStats?[] potions,
        decimal[][] relicVars,
        MonsterRules?[] monsters,
        Dictionary<ushort, int> spawnRules,
        Dictionary<string, CardPool> cardPools,
        ushort[] potionPool,
        ushort[] outOfCombatPotionPool,
        ushort[] rootPotions,
        bool[] powerDeathHistoryRisk,
        bool hasEncounter,
        string[] encounterSlots,
        int rootEnemyCount,
        int baseMaxEnergy,
        int playerSlotIndex,
        int baseOrbSlotCount,
        int rootSwordSageAmount,
        ushort unsettlingLampRelicId,
        bool hasIdentityContent,
        bool hasShuffleOrderEnchantment,
        bool hasBeforeFlushEnchantment,
        bool hasTheftContent,
        bool hasDecimalCardVars,
        bool hasInfiniteHpDisplayMonster,
        bool hasPaelsLegion,
        bool furCoatSpawnHpOne,
        ushort rootLostHpMask,
        bool hasHistoryCourse,
        int totalFloor = 0, DominionRules dominion = default, CombatMode mode = CombatMode.Standard)
    {
        _cards = cards;
        // Show Off can retain a card reference even in a root with no arrows or melodies.
        HasFoolMechanics = cards.Where((levels, id) => levels.Any(card => card is not null
            && ((card.Tags & (CardTag.Shoot | CardTag.Wah | CardTag.Cho | CardTag.Ki)) != 0
                || Registry.Card((ushort)id).NativeId == "SHOW_OFF"))).Any();
        bool hasTurnEndInHandCards = false;
        int cardLimit = Math.Min(cards.Length, Registry.CardCount);
        for (int id = 0; id < cardLimit && !hasTurnEndInHandCards; id++)
        {
            if (cards[id].Length == 0 || !Registry.Card((ushort)id).HasTurnEndInHandEffect)
                continue;
            for (int upgrade = 0; upgrade < cards[id].Length; upgrade++)
            {
                if (cards[id][upgrade] is not null)
                {
                    hasTurnEndInHandCards = true;
                    break;
                }
            }
        }
        HasTurnEndInHandCards = hasTurnEndInHandCards;
        _powerVars = powerVars;
        _potions = potions;
        _relicVars = relicVars;
        _monsters = monsters;
        _spawnRules = spawnRules;
        _cardPools = cardPools;
        _potionPool = potionPool;
        _outOfCombatPotionPool = outOfCombatPotionPool;
        _rootPotions = rootPotions;
        _powerDeathHistoryRisk = powerDeathHistoryRisk;
        HasEncounter = hasEncounter;
        _encounterSlots = encounterSlots;
        RootEnemyCount = rootEnemyCount;
        BaseMaxEnergy = baseMaxEnergy;
        PlayerSlotIndex = playerSlotIndex;
        BaseOrbSlotCount = baseOrbSlotCount;
        RootSwordSageAmount = rootSwordSageAmount;
        UnsettlingLampRelicId = unsettlingLampRelicId;
        HasIdentityContent = hasIdentityContent;
        HasShuffleOrderEnchantment = hasShuffleOrderEnchantment;
        HasBeforeFlushEnchantment = hasBeforeFlushEnchantment;
        HasTheftContent = hasTheftContent;
        HasDecimalCardVars = hasDecimalCardVars;
        HasInfiniteHpDisplayMonster = hasInfiniteHpDisplayMonster;
        HasPaelsLegion = hasPaelsLegion;
        FurCoatSpawnHpOne = furCoatSpawnHpOne;
        RootLostHpMask = rootLostHpMask;
        HasHistoryCourse = hasHistoryCourse;
        TotalFloor = totalFloor;
        Dominion = dominion;
        Mode = mode;
    }

    public int BaseMaxEnergy { get; }
    // Codec accessors: the rule tables are private so callers cannot mutate them, but the golden
    // writer lives in the same assembly and needs the complete frozen table.
    internal CardStats?[][] CardTable => _cards;
    internal decimal[][] PowerVarTable => _powerVars;
    internal PotionStats?[] PotionTable => _potions;
    internal decimal[][] RelicVarTable => _relicVars;
    internal MonsterRules?[] MonsterTable => _monsters;
    internal Dictionary<ushort, int> SpawnRuleTable => _spawnRules;
    internal Dictionary<string, CardPool> CardPoolTable => _cardPools;
    internal ushort[] PotionPoolTable => _potionPool;
    internal ushort[] OutOfCombatPotionPoolTable => _outOfCombatPotionPool;
    internal ushort[] RootPotionTable => _rootPotions;
    internal string[] EncounterSlotTable => _encounterSlots;
    internal bool[] PowerDeathHistoryRiskTable => _powerDeathHistoryRisk;

    /// <summary>
    /// Player's SwordSagePower amount at root capture (<c>_rootPowerAmounts</c> baseline of
    /// <c>NormalizeSwordSageReplays</c>). Newly generated non-clone blades start their applied replay
    /// bonus from this value.
    /// </summary>
    public int RootSwordSageAmount { get; }

    /// <summary>
    /// Root's UnsettlingLamp relic id, or <see cref="ushort.MaxValue"/> when the relic is absent. The
    /// relic is the only listener of the two power-amount hooks, so a root without it skips both
    /// dispatches entirely (the bench's Ironclad roots otherwise pay two listener scans per applied
    /// power for nothing).
    /// </summary>
    public ushort UnsettlingLampRelicId { get; }

    /// <summary>
    /// The root can reach one of the ID batch's per-object ledgers or instance powers. Conservative:
    /// true for a native identity card/power/relic, a card/relic that can generate one, or the
    /// SwordSage root amount. Roots without it skip the identity scans entirely.
    /// </summary>
    public bool HasIdentityContent { get; }

    public bool HasFoolMechanics { get; }

    /// <summary>
    /// The root's combat card values include an enchantment implementing <c>ModifyShuffleOrder</c>
    /// (PerfectFit). Shuffles skip the listener scan when false; enchantments only enter combat from the
    /// root (no in-combat card receives these two enchantments), so root cards are the complete source.
    /// </summary>
    public bool HasShuffleOrderEnchantment { get; }

    /// <summary>The root's combat card values include an enchantment implementing <c>BeforeFlush</c> (SlumberingEssence).</summary>
    public bool HasBeforeFlushEnchantment { get; }

    /// <summary>
    /// Some card in this root's rule table has <see cref="CardDef.HasTurnEndInHandEffect"/>. The
    /// threat projection scans the hand for declared turn-end self-damage only when this is true.
    /// The flag is derived from the decoded rule table, so it stays outside the RULE schema.
    /// </summary>
    public bool HasTurnEndInHandCards { get; }

    /// <summary>
    /// The root is a theft encounter or holds a theft power/monster (the two thief encounters).
    /// The outstanding-stolen-resource counters and the evaluation's theft fields are zero for every
    /// other root, so those per-node scans are skipped there.
    /// </summary>
    public bool HasTheftContent { get; }

    /// <summary>
    /// The root or a reachable generated/transform card uses <see cref="CardDef.UsesDecimalDamageVar"/>.
    /// When false, the kernel skips decimal-table canonicalization at every stable boundary.
    /// </summary>
    public bool HasDecimalCardVars { get; }

    /// <summary>
    /// A known or spawnable monster can expose <c>HpDisplay.InfiniteWithoutNumbers</c>
    /// (only the Waterfall Giant's steam-eruption rewrite). <c>HellraiserPower</c> reads it to choose
    /// the capped infinite-life auto-play branch; roots without one skip that scan.
    /// </summary>
    public bool HasInfiniteHpDisplayMonster { get; }

    /// <summary>
    /// The root owns PaelsLegion, the only block hook that needs the per-play ledger. GainBlock
    /// otherwise skips the changed-listener collection (identity content covers Vambrace only).
    /// </summary>
    public bool HasPaelsLegion { get; }

    /// <summary>
    /// The root's FurCoat mark covers the current map coordinate: every enemy created during this
    /// combat starts at 1 current HP (<c>MonsterSpawnSupport.ApplyCreatureAddedRelics</c>).
    /// </summary>
    public bool FurCoatSpawnHpOne { get; }

    /// <summary>
    /// Creatures whose <c>HasLostHpThisTurn</c> was true at root capture, one bit per owner
    /// (<see cref="Limits.PlayerOwner"/>, <see cref="Limits.OstyOwner"/> and enemy owners
    /// 2..). The engine's history query after <c>AdvancePlayerTurn</c> falls back to the immutable root
    /// history (prediction-time damage lives only in the cleared cache), so an extra turn restores
    /// exactly these flags; a normal turn advances the round and clears them.
    /// </summary>
    public ushort RootLostHpMask { get; }

    /// <summary>
    /// The root owns a non-melted <c>HistoryCourse</c>. Only such roots record the per-turn last attack
    /// (<see cref="BattleFlags.HistoryCourseCardPresent"/>) or run the AutoPrePlay clone; every
    /// other root skips both without scanning the relic table.
    /// </summary>
    public bool HasHistoryCourse { get; }

    /// <summary>
    /// Row of the root's PenNib relic, or -1. The doubled-attack latch is transient; a play that ends
    /// before its AfterCardPlayed (player death) leaves a value no later settlement can read, and the
    /// importer projects Aux as 0. Step clears it with one indexed write at every stable boundary.
    /// </summary>
    public int PenNibRelicIndex => Relics.PenNibIndex;

    /// <summary>Root-cached active relic listeners; ordinary roots skip the hit-count query.</summary>
    public bool HasAttackHitCountRelics => !Relics.Listeners(HookMaskExt.ModifyAttackHitCount).IsEmpty;
    public int TotalFloor { get; }
    public DominionRules Dominion { get; }
    public CombatMode Mode { get; }

    /// <summary>Native <c>CharacterModel.BaseOrbSlotCount</c>: a channel adds the first slot when both are zero.</summary>
    public int BaseOrbSlotCount { get; }

    public int PlayerSlotIndex { get; }

    /// <summary>Whether the battle has an encounter (<c>SimulatedCombatState.Encounter != null</c>).</summary>
    public bool HasEncounter { get; }

    /// <summary>Native encounter slot names, in native order (<c>SimulatedCombatState._encounterSlots</c>).</summary>
    public IReadOnlyList<string> EncounterSlots => _encounterSlots;

    /// <summary>
    /// Number of monster rule entries that belong to the root's known enemies. Known enemies are
    /// appended in order, so indices at or above this one are spawned monsters and use
    /// <see cref="SpawnRulesId"/>.
    /// </summary>
    public int RootEnemyCount { get; }

    /// <summary>
    /// Shuffle sort rank. Card ids are assigned in ordinal order of native ids, and all cards
    /// share one model category, so the id itself is the native <c>ModelId</c> order.
    /// </summary>
    public int CardSortRank(ushort id) => id;

    public CardStats Card(ushort id, int upgrade)
    {
        CardStats?[]? levels = id < _cards.Length ? _cards[id] : null;
        if (levels == null || (uint)upgrade >= (uint)levels.Length || levels[upgrade] is not { } stats)
            throw new UnsupportedException($"规则表缺少卡牌 {Registry.Card(id).NativeId}+{upgrade}。");
        return stats;
    }

    /// <summary>
    /// <c>AfterDeathMirrors</c> dispatch for this power type is Inferred/Unsupported: every death
    /// records a MethodNotMirrored history entry while it is in combat.
    /// </summary>
    public bool PowerRecordsDeathHistoryRisk(ushort powerId)
        => powerId < _powerDeathHistoryRisk.Length && _powerDeathHistoryRisk[powerId];

    public decimal PowerVar(ushort powerId, int index)
    {
        decimal[] vars = powerId < _powerVars.Length ? _powerVars[powerId] : [];
        if ((uint)index >= (uint)vars.Length)
            throw new UnsupportedException($"规则表缺少 Power {Registry.Power(powerId).NativeId} 的第 {index} 个变量。");
        return vars[index];
    }

    public PotionStats Potion(ushort potionId)
        => potionId < _potions.Length && _potions[potionId] is { } stats
            ? stats
            : throw new UnsupportedException($"规则表缺少药水 {Registry.Potion(potionId).NativeId}。");

    /// <summary>Root-frozen random-potion candidates, in the existing engine's option order.</summary>
    public IReadOnlyList<ushort> PotionPool => _potionPool;

    /// <summary>
    /// <c>PotionFactory.CreateRandomPotionOutOfCombat</c>'s candidates: every unlocked character and
    /// shared potion (no <c>CanBeGeneratedInCombat</c> filter), in the same order.
    /// </summary>
    public IReadOnlyList<ushort> OutOfCombatPotionPool => _outOfCombatPotionPool;

    /// <summary>
    /// Root potion ids by slot, encoded as internal id + 1 (0 = empty). Used by the kernel to tell a
    /// generated potion from a root potion when accounting <c>PotionUseCount</c>.
    /// </summary>
    public ushort RootPotion(int slot)
        => (uint)slot < (uint)_rootPotions.Length ? _rootPotions[slot] : (ushort)0;

    /// <summary>A canonical dynamic variable of a relic, in the order its definition names them.</summary>
    public decimal RelicVar(ushort relicId, int index)
    {
        decimal[] vars = relicId < _relicVars.Length ? _relicVars[relicId] : [];
        if ((uint)index >= (uint)vars.Length)
            throw new UnsupportedException($"规则表缺少遗物 {Registry.Relic(relicId).NativeId} 的第 {index} 个变量。");
        return vars[index];
    }

    public MonsterRules MonsterRule(byte rulesId)
    {
        if (rulesId >= _monsters.Length || _monsters[rulesId] is not { } rules)
            throw new UnsupportedException($"规则表缺少怪物规则 {rulesId}。");
        return rules;
    }

    /// <summary>Rule entry of a spawnable monster type, or a rejection when the root's closure lacks it.</summary>
    public int SpawnRulesId(ushort monsterId)
        => TrySpawnRulesId(monsterId, out int rulesId)
            ? rulesId
            : throw new UnsupportedException(
                $"规则表没有可生成怪物 {Registry.Monster(monsterId).NativeId} 的条目。");

    public bool TrySpawnRulesId(ushort monsterId, out int rulesId)
        => _spawnRules.TryGetValue(monsterId, out rulesId);

    /// <summary>Frozen candidate list of a generation pool declared by a content definition.</summary>
    public CardPool CardPool(string key)
        => _cardPools.TryGetValue(key, out CardPool? pool)
            ? pool
            : throw new UnsupportedException($"规则表没有卡池 {key}。");

    public bool TryCardPool(string key, out CardPool pool)
        => _cardPools.TryGetValue(key, out pool!);
}

/// <summary>Collects root data from the bridge and freezes it into <see cref="Rules"/>.</summary>
public sealed class RulesDraft
{
    private readonly List<CardStats?>[] _cards = new List<CardStats?>[Registry.CardCount];
    private readonly decimal[]?[] _powerVars = new decimal[]?[Registry.PowerCount];
    private readonly bool[] _powerDeathHistoryRisk = new bool[Registry.PowerCount];
    private readonly PotionStats?[] _potions = new PotionStats?[Registry.PotionCount];
    private readonly decimal[]?[] _relicVars = new decimal[]?[Registry.RelicCount];
    private readonly MonsterRules?[] _monsters = new MonsterRules?[Limits.MonsterRules];
    private readonly Dictionary<ushort, int> _spawnRules = [];
    private readonly Dictionary<string, CardPool> _cardPools = new(StringComparer.Ordinal);
    private ushort[] _potionPool = [];
    private ushort[] _outOfCombatPotionPool = [];
    private readonly ushort[] _rootPotions = new ushort[Limits.PotionSlots];
    private int _monsterRuleCount;

    public int BaseMaxEnergy { get; set; }

    public int PlayerSlotIndex { get; set; }

    public bool HasEncounter { get; set; }

    public string[] EncounterSlots { get; set; } = [];

    public int RootEnemyCount { get; set; }

    public int BaseOrbSlotCount { get; set; }

    public int RootSwordSageAmount { get; set; }

    public ushort UnsettlingLampRelicId { get; set; } = ushort.MaxValue;


    public bool HasIdentityContent { get; set; } = true;

    public bool HasShuffleOrderEnchantment { get; set; }

    public bool HasBeforeFlushEnchantment { get; set; }

    public bool HasTheftContent { get; set; }

    public bool HasDecimalCardVars { get; set; }

    public bool HasInfiniteHpDisplayMonster { get; set; }

    public bool HasPaelsLegion { get; set; }

    public bool FurCoatSpawnHpOne { get; set; }

    public bool HasHistoryCourse { get; set; }

    public int TotalFloor { get; set; }
    public DominionRules Dominion { get; set; }
    public CombatMode Mode { get; set; }

    public ushort RootLostHpMask { get; set; }

    public void SetCard(ushort id, int upgrade, CardStats stats)
    {
        List<CardStats?> levels = _cards[id] ??= [];
        while (levels.Count <= upgrade)
            levels.Add(null);
        if (levels[upgrade] is { } existing && !SameStats(existing, stats))
            throw new InvalidOperationException($"卡牌 {Registry.Card(id).NativeId}+{upgrade} 的规则数据前后不一致。");
        levels[upgrade] = stats;
    }

    public bool HasCard(ushort id, int upgrade)
        => _cards[id] is { } levels && upgrade < levels.Count && levels[upgrade] != null;

    public void SetPowerVars(ushort id, decimal[] vars)
    {
        if (_powerVars[id] is { } existing && !existing.AsSpan().SequenceEqual(vars))
            throw new InvalidOperationException($"Power {Registry.Power(id).NativeId} 的规则数据前后不一致。");
        _powerVars[id] = vars;
    }

    public bool HasPowerVars(ushort id) => _powerVars[id] != null;

    public void SetPowerDeathHistoryRisk(ushort id, bool value) => _powerDeathHistoryRisk[id] = value;

    public void SetPotion(ushort id, PotionStats stats) => _potions[id] = stats;

    public void SetCardPool(string key, ushort[] cards)
    {
        if (!_cardPools.TryAdd(key, new CardPool { Cards = cards }))
            throw new InvalidOperationException($"卡池 {key} 重复登记。");
    }

    public bool HasCardPool(string key) => _cardPools.ContainsKey(key);

    public void SetPotionPool(ushort[] potions) => _potionPool = potions;

    public void SetOutOfCombatPotionPool(ushort[] potions) => _outOfCombatPotionPool = potions;

    public void SetRootPotion(int slot, ushort id)
    {
        if ((uint)slot >= (uint)Limits.PotionSlots)
            throw new CapacityException("PotionSlots", Limits.PotionSlots);
        _rootPotions[slot] = checked((ushort)(id + 1));
    }

    public void SetRelicVars(ushort id, decimal[] vars) => _relicVars[id] = vars;

    public void SetMonster(int enemyIndex, MonsterRules rules)
    {
        if ((uint)enemyIndex >= Limits.MonsterRules)
            throw new CapacityException("MonsterRules", Limits.MonsterRules);
        _monsters[enemyIndex] = rules;
        if (enemyIndex >= _monsterRuleCount)
            _monsterRuleCount = enemyIndex + 1;
    }

    /// <summary>
    /// Appends one entry for a monster type reachable through the spawn closure and returns its rules
    /// index. Each spawnable type gets one entry, independent of the root's same-typed enemies.
    /// </summary>
    public int AddSpawnableMonster(MonsterRules rules)
    {
        if (_monsterRuleCount >= Limits.MonsterRules)
            throw new CapacityException("MonsterRules", Limits.MonsterRules);
        if (!_spawnRules.TryAdd(rules.MonsterId, _monsterRuleCount))
            throw new InvalidOperationException(
                $"可生成怪物 {Registry.Monster(rules.MonsterId).NativeId} 的规则条目重复。");
        int index = _monsterRuleCount++;
        _monsters[index] = rules;
        return index;
    }


    public Rules Build()
    {
        CardStats?[][] cards = new CardStats?[_cards.Length][];
        for (int id = 0; id < _cards.Length; id++)
            cards[id] = _cards[id]?.ToArray() ?? [];
        decimal[][] powerVars = new decimal[_powerVars.Length][];
        for (int id = 0; id < _powerVars.Length; id++)
            powerVars[id] = _powerVars[id] ?? [];
        decimal[][] relicVars = new decimal[_relicVars.Length][];
        for (int id = 0; id < _relicVars.Length; id++)
            relicVars[id] = _relicVars[id] ?? [];
        return new Rules(cards, powerVars, (PotionStats?[])_potions.Clone(), relicVars,
            (MonsterRules?[])_monsters.Clone(), new Dictionary<ushort, int>(_spawnRules),
            new Dictionary<string, CardPool>(_cardPools, StringComparer.Ordinal),
            (ushort[])_potionPool.Clone(),
            (ushort[])_outOfCombatPotionPool.Clone(),
            (ushort[])_rootPotions.Clone(),
            (bool[])_powerDeathHistoryRisk.Clone(),
            HasEncounter, EncounterSlots, RootEnemyCount, BaseMaxEnergy, PlayerSlotIndex, BaseOrbSlotCount,
            RootSwordSageAmount, UnsettlingLampRelicId, HasIdentityContent, HasShuffleOrderEnchantment,
            HasBeforeFlushEnchantment, HasTheftContent, HasDecimalCardVars, HasInfiniteHpDisplayMonster,
            HasPaelsLegion, FurCoatSpawnHpOne, RootLostHpMask, HasHistoryCourse, TotalFloor, Dominion, Mode);
    }

    private static bool SameStats(CardStats left, CardStats right)
        => left.Type == right.Type
            && left.Target == right.Target
            && left.Rarity == right.Rarity
            && left.EnergyCost == right.EnergyCost
            && left.CostsX == right.CostsX
            && left.StarCost == right.StarCost
            && left.StarCostsX == right.StarCostsX
            && left.Keywords == right.Keywords
            && left.Pool == right.Pool
            && left.Tags == right.Tags
            && left.IsSoul == right.IsSoul
            && left.Vars.AsSpan().SequenceEqual(right.Vars);
}
