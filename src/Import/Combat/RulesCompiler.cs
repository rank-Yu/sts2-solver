// Ported from CombatSolver src/Flash/Bridge/FlashRulesCompiler.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). The existing engine's simulated state is
// replaced by direct reads of the native CombatState/models; card cloning, monster state machines
// and the spawn creation path are implemented against the native model APIs instead of the
// engine's SimulatedCombatState. Rule table shape and closure ordering are preserved.
using System.Reflection;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

using Creature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;
using EngineCardRarity = Sts2Solver.Simulation.Combat.CardRarity;
using EngineCardTag = Sts2Solver.Simulation.Combat.CardTag;
using EngineCardType = Sts2Solver.Simulation.Combat.CardType;
using EngineMoveRepeatType = Sts2Solver.Simulation.Combat.MoveRepeatType;
using EnginePotionRarity = Sts2Solver.Simulation.Combat.PotionRarity;
using EngineTargetType = Sts2Solver.Simulation.Combat.TargetType;
using NativeCardTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;
using NativePotionRarity = MegaCrit.Sts2.Core.Entities.Potions.PotionRarity;
using NativeCardType = MegaCrit.Sts2.Core.Entities.Cards.CardType;

namespace Sts2Solver.Import;

/// <summary>
/// Compiles a native root into <see cref="Rules"/>. This is the M2a-3 replacement for decoding a
/// frozen rule object: the 30 constructor inputs are read from the native combat state and models.
/// Unsupported or missing native reads reject the root with a stable <see cref="ImportReason"/>.
/// </summary>
public static partial class RulesCompiler
{
    private static readonly Lazy<Dictionary<string, CardModel>> CanonicalCards = new(() =>
        ModelDb.All.OfType<CardModel>().GroupBy(card => card.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    private static readonly Lazy<Dictionary<string, PowerModel>> CanonicalPowers = new(() =>
        ModelDb.All.OfType<PowerModel>().GroupBy(power => power.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    private static readonly Lazy<Dictionary<string, RelicModel>> CanonicalRelics = new(() =>
        ModelDb.All.OfType<RelicModel>().GroupBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    private static readonly Lazy<Dictionary<string, PotionModel>> CanonicalPotions = new(() =>
        ModelDb.All.OfType<PotionModel>().GroupBy(potion => potion.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    private static readonly Lazy<Dictionary<string, MonsterModel>> CanonicalMonsters = new(() =>
        ModelDb.All.OfType<MonsterModel>().GroupBy(monster => monster.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    internal static IReadOnlyDictionary<string, CardModel> CanonicalCardModels
        => CanonicalCards.Value;

    internal static IReadOnlyDictionary<string, PowerModel> CanonicalPowerModels
        => CanonicalPowers.Value;

    internal static IReadOnlyDictionary<string, RelicModel> CanonicalRelicModels
        => CanonicalRelics.Value;

    internal static IReadOnlyDictionary<string, PotionModel> CanonicalPotionModels
        => CanonicalPotions.Value;

    internal static IReadOnlyDictionary<string, MonsterModel> CanonicalMonsterModels
        => CanonicalMonsters.Value;

    private static readonly FieldInfo InitialStateField = typeof(MonsterMoveStateMachine)
        .GetField("_initialState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(MonsterMoveStateMachine).FullName, "_initialState");

    public static Rules Compile(CombatState state, Player player, RunState runState)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(runState);
        if (!ReferenceEquals(state.RunState, runState))
            throw new UnsupportedImportException(ImportReason.RulesMismatch, "传入的 RunState 不是战斗状态的 RunState");
        if (state.Players.Count != 1)
            throw new UnsupportedImportException(ImportReason.MultiPlayer, $"玩家数 {state.Players.Count}");
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new UnsupportedImportException(ImportReason.Unrepresentable, "玩家没有 PlayerCombatState");
        if (!player.Creature.IsAlive)
            throw new UnsupportedImportException(ImportReason.Unrepresentable, "玩家已死亡");
        IReadOnlyList<Creature> known = state.Enemies;
        if (known.Count == 0)
            throw new UnsupportedImportException(ImportReason.KnownEnemyHistory, "根没有敌人");
        foreach (Creature enemy in known)
        {
            if (enemy.Monster == null)
                throw new UnsupportedImportException(ImportReason.UnsupportedMonster, $"{enemy.Name} 没有怪物模型");
            if (!enemy.IsAlive && enemy.Monster.MoveStateMachine == null)
                throw new UnsupportedImportException(ImportReason.KnownEnemyHistory,
                    $"敌人 {enemy.Monster.Id.Entry} 已死亡且没有行动状态机");
            NativeAccess.RequireOriginal(enemy.Monster);
        }
        if (NativeAccess.HasNonVanillaModels(player,
                new NativeAccess.CombatStateView(state.Creatures, state.Enemies, state.Allies)))
        {
            throw new UnsupportedImportException(ImportReason.ThirdPartyContent, "战斗中含非原版程序集的模型");
        }
        NativeAccess.RequireOriginal(player.Character);
        if (state.Encounter != null)
            NativeAccess.RequireOriginal(state.Encounter);
        foreach (ModifierModel modifier in state.Modifiers)
            NativeAccess.RequireOriginal(modifier);

        NativeHistory history = NativeHistory.Capture(state);
        RulesDraft builder = new()
        {
            // SimulatedCombatState captures Player.MaxEnergy, the un-hooked base value.
            BaseMaxEnergy = player.MaxEnergy,
            BaseOrbSlotCount = player.Character.BaseOrbSlotCount,
            RootSwordSageAmount = player.Creature.GetPowerAmount<SwordSagePower>(),
        };

        ushort lampId = ushort.MaxValue;
        bool hasIdentityContent = builder.RootSwordSageAmount != 0
            || player.Character is Regent or Necrobinder or Silent;
        bool hasShuffleOrderEnchantment = false;
        bool hasBeforeFlushEnchantment = false;
        foreach (CardPile pile in EnumeratePiles(playerState))
        {
            foreach (CardModel card in pile.Cards)
            {
                ushort id = CardId(card);
                AddCard(builder, id, card.CurrentUpgradeLevel, card.CanonicalInstance, player);
                CardDef def = Registry.Card(id);
                hasIdentityContent |= def.UsesIdentityLedger || def.TracksFetchPlay
                    || def.MayGenerateIdentityContent || def.IsIdentityContent;
                if (card.Enchantment is { } enchantment
                    && Registry.TryEnchantment(enchantment.Id.Entry, out ushort enchantmentId))
                {
                    HookMaskExt hooks = Registry.Enchantment(enchantmentId).HooksExt;
                    hasShuffleOrderEnchantment |= (hooks & HookMaskExt.ModifyShuffleOrder) != 0;
                    hasBeforeFlushEnchantment |= (hooks & HookMaskExt.BeforeFlush) != 0;
                }
            }
        }
        builder.HasShuffleOrderEnchantment = hasShuffleOrderEnchantment;
        builder.HasBeforeFlushEnchantment = hasBeforeFlushEnchantment;

        foreach (PowerModel power in EffectivePowers(state))
        {
            if (Registry.TryPower(power.Id.Entry, out ushort powerId))
                hasIdentityContent |= Registry.Power(powerId).IsIdentityContent;
        }

        bool hasPaelsLegion = false;
        bool furCoatSpawnHpOne = false;
        bool hasHistoryCourse = false;
        foreach (RelicModel relic in player.Relics)
        {
            if (Registry.TryRelic(relic.Id.Entry, out ushort relicId))
            {
                RelicDef def = Registry.Relic(relicId);
                hasIdentityContent |= def.MayGenerateIdentityContent || def.IsIdentityContent;
                if (def.IsUnsettlingLamp && lampId == ushort.MaxValue)
                    lampId = relicId;
            }
            if (relic.IsMelted)
                continue;
            if (relic is HistoryCourse)
                hasHistoryCourse = true;
            if (Registry.TryRelic(relic.Id.Entry, out ushort ruleId) && Registry.Relic(ruleId).IsPaelsLegion)
                hasPaelsLegion = true;
            if (relic is FurCoat furCoat
                && state.RunState.CurrentMapCoord is { } mapCoord
                && furCoat.GetMarkedCoords()?.Contains(mapCoord) == true)
            {
                furCoatSpawnHpOne = true;
            }
        }
        builder.UnsettlingLampRelicId = lampId;
        builder.HasIdentityContent = hasIdentityContent;
        builder.HasPaelsLegion = hasPaelsLegion;
        builder.FurCoatSpawnHpOne = furCoatSpawnHpOne;
        builder.HasHistoryCourse = hasHistoryCourse;
        builder.HasTheftContent = HasTheftContent(state);
        foreach (string nativeId in PowerIdsWithVars())
            AddPowerVars(builder, nativeId);
        // The reference engine records a prediction-risk history entry for every death while a
        // power whose AfterDeath dispatch is inferred/unsupported is present. The boolean is not
        // native data, so it is frozen in MirrorRiskTables.
        for (ushort id = 0; id < Registry.PowerCount; id++)
        {
            string nativePower = Registry.Power(id).NativeId;
            if (!CanonicalPowers.Value.ContainsKey(nativePower))
                continue;
            builder.SetPowerDeathHistoryRisk(id,
                MirrorRiskTables.PowerRecordsDeathHistoryRisk(nativePower));
        }
        for (ushort id = 0; id < Registry.PotionCount; id++)
            AddPotionStats(builder, id);
        for (ushort id = 0; id < Registry.RelicCount; id++)
            AddRelicVars(builder, id);
        if (known.Count > Limits.Enemies)
            throw new UnsupportedImportException(ImportReason.Capacity, $"敌人数 {known.Count}");

        ushort rootLostHpMask = 0;
        if (history.HasLostHpThisTurn(player.Creature))
            rootLostHpMask |= 1 << Limits.PlayerOwner;
        if (player.Osty is { } osty && history.HasLostHpThisTurn(osty))
            rootLostHpMask |= 1 << Limits.OstyOwner;
        for (int index = 0; index < known.Count && index < Limits.Enemies; index++)
        {
            if (history.HasLostHpThisTurn(known[index]))
                rootLostHpMask |= (ushort)(1 << (Limits.FirstEnemyOwner + index));
        }
        builder.RootLostHpMask = rootLostHpMask;

        List<ushort> spawnClosure = SpawnClosure(known);
        if (known.Count + spawnClosure.Count > Limits.MonsterRules)
            throw new UnsupportedImportException(ImportReason.Capacity,
                $"怪物规则 {known.Count}+{spawnClosure.Count}");

        List<(string CardId, int Upgrade)> detachedCards = [];
        foreach (PowerModel power in EffectivePowers(state))
        {
            if (power is NightmarePower nightmare)
            {
                if (NativeAccess.NightmareSelection(nightmare) is { } selected)
                    detachedCards.Add((selected.Id.Entry, selected.CurrentUpgradeLevel));
            }
            else if (power is SwipePower { StolenCard: { } stolen })
            {
                detachedCards.Add((stolen.Id.Entry, stolen.CurrentUpgradeLevel));
            }
        }
        builder.Dominion = CompileDominion(state);
        AddGeneratedCardClosure(builder, state, player, playerState, known, spawnClosure, detachedCards);
        builder.HasInfiniteHpDisplayMonster = HasInfiniteHpDisplayMonster(known, spawnClosure);
        builder.RootEnemyCount = known.Count;
        builder.TotalFloor = player.RunState.TotalFloor;
        builder.Mode = player.RunState.GameMode switch
        {
            GameMode.Standard => CombatMode.Standard,
            GameMode.Dread => CombatMode.Dread,
            GameMode.Blitz => CombatMode.Blitz,
            _ => throw new UnsupportedException($"未知游戏模式 {player.RunState.GameMode}。"),
        };
        builder.HasEncounter = state.Encounter != null;
        builder.EncounterSlots = state.Encounter?.Slots.ToArray() ?? [];

        // Compile known enemies from their live native models. The reference engine uses a private
        // fork only so that reading cached private integers cannot mutate the caller; the native
        // reader in NativeAccess has no such cache.
        for (int index = 0; index < known.Count; index++)
        {
            builder.SetMonster(index, CompileMonster(known[index].Monster!, known[index],
                rootPowerAmountsFromCreature: true));
        }
        foreach (ushort monsterId in spawnClosure)
            CompileSpawnedMonster(builder, state, runState, monsterId);
        return builder.Build().WithRelics(CompileRelicRoster(player));
    }

    private static IEnumerable<CardPile> EnumeratePiles(PlayerCombatState playerState)
    {
        yield return playerState.Hand;
        yield return playerState.DrawPile;
        yield return playerState.DiscardPile;
        yield return playerState.ExhaustPile;
        yield return playerState.PlayPile;
    }

    /// <summary>
    /// Native listeners in native order, filtered to non-zero powers. For a materialized root this
    /// is the same prefix order the reference engine builds from its root listener snapshot.
    /// </summary>
    private static IEnumerable<PowerModel> EffectivePowers(CombatState state)
    {
        foreach (AbstractModel listener in state.IterateHookListeners())
        {
            if (listener is PowerModel power && power.Amount != 0)
                yield return power;
        }
    }

    private static bool HasInfiniteHpDisplayMonster(IReadOnlyList<Creature> known,
        IReadOnlyList<ushort> spawnClosure)
    {
        foreach (Creature creature in known)
        {
            if (creature.Monster is { } monster
                && Registry.TryMonster(monster.Id.Entry, out ushort id)
                && Registry.Monster(id).HasInfiniteHpDisplay)
            {
                return true;
            }
        }
        foreach (ushort monsterId in spawnClosure)
        {
            if (Registry.Monster(monsterId).HasInfiniteHpDisplay)
                return true;
        }
        return false;
    }

    private static bool HasTheftContent(CombatState state)
    {
        if (state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK")
            return true;
        foreach (Creature creature in state.Enemies)
        {
            if (creature.Monster is GremlinMerc or ThievingHopper or FatGremlin)
                return true;
        }
        foreach (PowerModel power in EffectivePowers(state))
        {
            if (power is ThieveryPower or HeistPower or SwipePower or SurprisePower)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Native ids reachable from the root enemies' spawn declarations, transitively, in first-seen
    /// breadth-first order. The reference engine assigns spawn-rule indices in this order.
    /// </summary>
    private static List<ushort> SpawnClosure(IReadOnlyList<Creature> known)
    {
        List<ushort> closure = [];
        HashSet<ushort> seen = [];
        Queue<string> pending = new();
        void Declare(MonsterDef def)
        {
            foreach (string nativeId in def.SpawnedMonsterIds)
                pending.Enqueue(nativeId);
        }
        foreach (Creature creature in known)
        {
            string nativeId = creature.Monster?.Id.Entry
                ?? throw new UnsupportedImportException(ImportReason.UnsupportedMonster, "敌人没有怪物模型");
            if (!Registry.TryMonster(nativeId, out ushort id))
                throw new UnsupportedImportException(ImportReason.UnsupportedMonster, nativeId);
            Declare(Registry.Monster(id));
        }
        while (pending.Count > 0)
        {
            string nativeId = pending.Dequeue();
            if (!Registry.TryMonster(nativeId, out ushort id))
                throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                    $"生成怪物 {nativeId} 没有 Sts2Solver 定义");
            if (!seen.Add(id))
                continue;
            closure.Add(id);
            Declare(Registry.Monster(id));
        }
        return closure;
    }

    /// <summary>
    /// Compiles one spawnable type on a private scratch creation path: a fresh mutable model, the
    /// definition's spawn configuration, the native <c>SetUpForCombat</c> state-machine generation,
    /// and a throwaway <see cref="CombatState"/> that only supplies creature identity to conditions.
    /// </summary>
    private static void CompileSpawnedMonster(RulesDraft builder, CombatState state,
        RunState runState, ushort monsterId)
    {
        MonsterDef def = Registry.Monster(monsterId);
        string nativeId = def.NativeId;
        MonsterModel canonical = CanonicalMonsters.Value.TryGetValue(nativeId, out MonsterModel? monster)
            ? monster
            : throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                $"原版没有怪物 {nativeId}");
        MonsterModel mutable = canonical.ToMutable();
        if (def.SpawnedStunned && mutable is Wriggler wriggler)
            wriggler.StartStunned = true;
        if (def.SpawnStockAmount >= 0 && mutable is Axebot axebot)
            axebot.StockAmount = def.SpawnStockAmount;
        string? slot = def.SpawnSlotName ?? NextFreeSlot(state);
        if (string.IsNullOrEmpty(slot))
            slot = null;
        Creature created = CreateScratchMonster(mutable, slot, runState);
        builder.AddSpawnableMonster(CompileMonster(mutable, created, rootPowerAmountsFromCreature: false));
    }

    internal static Creature CreateScratchMonster(MonsterModel monster, string? slot,
        IRunState runState)
    {
        CombatState scratch = new(encounter: null, runState);
        monster.RunRng = runState.Rng;
        monster.Rng = new Rng(0);
        Creature creature = new(monster, CombatSide.Enemy, slot);
        creature.CombatState = scratch;
        creature.CombatId = scratch._nextCreatureId++;
        monster.SetUpForCombat();
        scratch.AddCreature(creature);
        return creature;
    }

    private static string? NextFreeSlot(CombatState state)
    {
        if (state.Encounter == null)
            return null;
        foreach (string slot in state.Encounter.Slots)
        {
            bool used = false;
            foreach (Creature creature in state.Enemies)
            {
                if (string.Equals(creature.SlotName, slot, StringComparison.Ordinal))
                {
                    used = true;
                    break;
                }
            }
            if (!used)
                return slot;
        }
        return null;
    }

    public static ushort CardId(CardModel card)
        => Registry.TryCard(card.Id.Entry, out ushort id) ? id : throw UnsupportedContent.Card(card);

    private static void AddCard(RulesDraft builder, ushort id, int upgrade, CardModel canonical,
        Player player)
        => AddStatsWithUpgrade(builder, id, upgrade, canonical, player);

    private static void AddStatsWithUpgrade(RulesDraft builder, ushort id, int upgrade,
        CardModel canonical, Player player)
    {
        builder.HasDecimalCardVars |= Registry.Card(id).UsesDecimalDamageVar;
        if (upgrade > 0 && !builder.HasCard(id, 0))
            builder.SetCard(id, 0, CanonicalStats(canonical, 0, player));
        if (!builder.HasCard(id, upgrade))
            builder.SetCard(id, upgrade, CanonicalStats(canonical, upgrade, player));
        if (upgrade < canonical.MaxUpgradeLevel && !builder.HasCard(id, upgrade + 1))
            builder.SetCard(id, upgrade + 1, CanonicalStats(canonical, upgrade + 1, player));
    }

    internal static CardStats CanonicalStats(CardModel canonical, int upgrade, Player player)
    {
        CardModel copy = CreateUpgradedCard(canonical, upgrade, player);
        CardDef def = Registry.Card(CardId(canonical));
        decimal[] vars = new decimal[def.VarNames.Count];
        for (int index = 0; index < vars.Length; index++)
        {
            string name = def.VarNames[index];
            vars[index] = copy.DynamicVars.TryGetValue(name, out DynamicVar? variable)
                ? variable.BaseValue
                : throw new UnsupportedImportException(ImportReason.UnsupportedCard,
                    $"{canonical.Id.Entry} 没有动态变量 {name}");
        }
        return new CardStats
        {
            Type = (EngineCardType)(byte)copy.Type,
            Target = (EngineTargetType)(byte)copy.TargetType,
            Rarity = (EngineCardRarity)(byte)copy.Rarity,
            Pool = copy.Pool switch
            {
                ColorlessCardPool => TransformPool.Colorless,
                StatusCardPool => TransformPool.Status,
                CurseCardPool => TransformPool.Curse,
                _ => TransformPool.Character,
            },
            EnergyCost = copy.EnergyCost._base,
            CostsX = copy.EnergyCost.CostsX,
            StarCost = copy.BaseStarCost,
            StarCostsX = copy.HasStarCostX,
            Keywords = MapKeywords(copy.Keywords),
            MaxUpgradeLevel = copy.MaxUpgradeLevel,
            Tags = MapTags(copy.Tags),
            IsSoul = copy.Id.Entry == "SOUL",
            GainsBlock = copy.GainsBlock,
            IsColorless = copy.VisualCardPool.IsColorless,
            HasDynamicType = def.HasDynamicCardType,
            PlaysRecordRisk = MirrorRiskTables.CardPlaysRecordRisk(canonical.Id.Entry),
            Vars = vars,
        };
    }

    /// <summary>
    /// Native equivalent of <c>PredictionUtils.CreateCard</c> + <c>UpgradeCard</c>: a mutable model
    /// clone owned by the player, then the native upgrade pair, without reading the root card (which
    /// may carry temporary upgrades or modifiers).
    /// </summary>
    internal static CardModel CreateUpgradedCard(CardModel canonical, int upgrade, Player player)
    {
        CardModel copy = canonical.ToMutable();
        copy.Owner = player;
        for (int level = 0; level < upgrade; level++)
        {
            if (!copy.IsUpgradable)
                continue;
            copy.UpgradeInternal();
            copy.FinalizeUpgradeInternal();
        }
        if (copy.CurrentUpgradeLevel != upgrade)
            throw new UnsupportedImportException(ImportReason.UnsupportedCard,
                $"{canonical.Id.Entry} 无法升级到 {upgrade}");
        return copy;
    }

    private static void AddGeneratedCardClosure(RulesDraft builder, CombatState state, Player player,
        PlayerCombatState playerState, IReadOnlyList<Creature> known, IReadOnlyList<ushort> spawnClosure,
        IReadOnlyList<(string CardId, int Upgrade)> detachedCards)
    {
        Queue<(string CardId, int Upgrade)> pendingCards = new();
        Queue<string> pendingPools = new();
        Queue<string> pendingPowers = new();
        HashSet<(string, int)> seenCards = [];
        foreach ((string cardId, int upgrade) in detachedCards)
        {
            if (seenCards.Add((cardId, upgrade)))
                pendingCards.Enqueue((cardId, upgrade));
        }
        HashSet<string> seenPools = new(StringComparer.Ordinal);
        HashSet<string> seenPowerDefs = new(StringComparer.Ordinal);
        HashSet<ushort> seenPotionDefs = [];
        ushort[]? inCombatPotionPool = null;
        ushort[]? outOfCombatPotionPool = null;
        bool needsPotionPool = false;
        bool needsOutOfCombatPotionPool = false;
        // The root scan above only sees content that already exists. A generated identity-ledger
        // card (Forge's Sovereign Blade) requires the same scans. Only the card that owns a
        // per-object ledger counts here: whether a generated pool contains some card that could
        // generate identity content is not enough (Entropy's pools do), and the root flag is part
        // of the frozen rules encoding.
        bool generatedIdentityContent = false;
        void Declare(ContentDef def)
        {
            generatedIdentityContent |= def is CardDef { UsesIdentityLedger: true };
            needsPotionPool |= def.GeneratesRandomPotion;
            needsOutOfCombatPotionPool |= def.GeneratesOutOfCombatPotion;
            if (def is PotionDef && !seenPotionDefs.Add(def.Id))
                return;
            foreach ((string cardId, int upgrade) in def.GeneratedCards)
            {
                if (seenCards.Add((cardId, upgrade)))
                    pendingCards.Enqueue((cardId, upgrade));
            }
            foreach (string pool in def.GeneratedCardPools)
            {
                if (seenPools.Add(pool))
                    pendingPools.Enqueue(pool);
            }
            foreach (string power in def.AppliedPowers)
                pendingPowers.Enqueue(power);
        }
        foreach (CardPile pile in EnumeratePiles(playerState))
        {
            foreach (CardModel card in pile.Cards)
            {
                if (Registry.TryCard(card.Id.Entry, out ushort id))
                    Declare(Registry.Card(id));
            }
        }
        foreach (PowerModel power in EffectivePowers(state))
        {
            if (Registry.TryPower(power.Id.Entry, out ushort id))
                Declare(Registry.Power(id));
        }
        foreach (RelicModel relic in player.Relics)
        {
            if (Registry.TryRelic(relic.Id.Entry, out ushort id))
                Declare(Registry.Relic(id));
        }
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (player.GetPotionAtSlotIndex(slot) is { } potion
                && Registry.TryPotion(potion.Id.Entry, out ushort id))
            {
                Declare(Registry.Potion(id));
            }
        }
        foreach (Creature enemy in known)
        {
            if (enemy.Monster is { } monster && Registry.TryMonster(monster.Id.Entry, out ushort id))
                Declare(Registry.Monster(id));
        }
        foreach (ushort monsterId in spawnClosure)
            Declare(Registry.Monster(monsterId));

        if (builder.Dominion.SpawnAmount != 0)
            pendingPowers.Enqueue(Registry.Power(builder.Dominion.SpawnPower).NativeId);
        if (builder.Dominion.Kind == DominionKind.KnowledgeDemon)
        {
            foreach (string card in new[] { "SLOTH", "AMNESIA" })
                if (seenCards.Add((card, 0))) pendingCards.Enqueue((card, 0));
            pendingPowers.Enqueue("SLOTH_POWER");
            pendingPowers.Enqueue("AMNESIA_POWER");
        }
        while (true)
        {
            while (pendingCards.Count > 0 || pendingPools.Count > 0 || pendingPowers.Count > 0)
            {
                while (pendingPowers.Count > 0)
                {
                    string powerId = pendingPowers.Dequeue();
                    if (!seenPowerDefs.Add(powerId))
                        continue;
                    if (!Registry.TryPower(powerId, out ushort powerInternalId))
                        throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                            $"生成 Power {powerId} 没有 Sts2Solver 定义");
                    Declare(Registry.Power(powerInternalId));
                }
                while (pendingPools.Count > 0)
                {
                    string poolKey = pendingPools.Dequeue();
                    ushort[] pool = FreezeCardPool(poolKey, player);
                    builder.SetCardPool(poolKey, pool);
                    foreach (ushort id in pool)
                    {
                        Declare(Registry.Card(id));
                        CardModel poolCanonical = CanonicalCards.Value.TryGetValue(
                            Registry.Card(id).NativeId, out CardModel? canonicalCard)
                            ? canonicalCard
                            : throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                                $"原版没有卡牌 {Registry.Card(id).NativeId}");
                        AddStatsWithUpgrade(builder, id, 0, poolCanonical, player);
                    }
                }
                if (pendingCards.Count == 0)
                    break;
                (string cardId, int upgrade) = pendingCards.Dequeue();
                if (!Registry.TryCard(cardId, out ushort cardInternalId))
                    throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                        $"生成卡 {cardId} 没有 Sts2Solver 定义");
                CardModel canonical = CanonicalCards.Value.TryGetValue(cardId, out CardModel? card)
                    ? card
                    : throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                        $"原版没有卡牌 {cardId}");
                AddStatsWithUpgrade(builder, cardInternalId, upgrade, canonical, player);
                Declare(Registry.Card(cardInternalId));
            }
            // A random-potion candidate can itself generate cards or potions (Alchemize-class
            // potions), so declaring the frozen pool must re-run the closure before it is complete.
            if (needsPotionPool && inCombatPotionPool == null)
            {
                inCombatPotionPool = FreezePotionPool(player, inCombat: true);
                builder.SetPotionPool(inCombatPotionPool);
                foreach (ushort id in inCombatPotionPool)
                    Declare(Registry.Potion(id));
                continue;
            }
            if (needsOutOfCombatPotionPool && outOfCombatPotionPool == null)
            {
                outOfCombatPotionPool = FreezePotionPool(player, inCombat: false);
                builder.SetOutOfCombatPotionPool(outOfCombatPotionPool);
                foreach (ushort id in outOfCombatPotionPool)
                    Declare(Registry.Potion(id));
                continue;
            }
            break;
        }
        builder.HasIdentityContent |= generatedIdentityContent;

        // Root potions identify "this slot still holds the potion the search started with". The
        // reference compiler records them inside this closure; the value is independent of order.
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (player.GetPotionAtSlotIndex(slot) is { } potion
                && Registry.TryPotion(potion.Id.Entry, out ushort potionId))
            {
                builder.SetRootPotion(slot, potionId);
            }
        }
    }

    private static ushort[] FreezePotionPool(Player player, bool inCombat)
    {
        List<PotionModel> options = PotionFactory.GetPotionOptions(player)
            .Where(potion => !inCombat || potion.CanBeGeneratedInCombat)
            .ToList();
        ushort[] ids = new ushort[options.Count];
        for (int index = 0; index < options.Count; index++)
        {
            PotionModel potion = options[index];
            if (!Registry.TryPotion(potion.Id.Entry, out ushort id))
                throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                    $"随机药水 {potion.Id.Entry} 没有 Sts2Solver 定义");
            ids[index] = id;
        }
        return ids;
    }

    private static ushort[] FreezeCardPool(string poolKey, Player player)
    {
        CardMultiplayerConstraint constraint = player.RunState.CardMultiplayerConstraint;
        IEnumerable<CardModel> cards = poolKey switch
        {
            CardPools.Tarot => TarotCards.All.Where(card => card.IsAllowedInRun(player.RunState)),
            CardPools.OtherCharacterCards => player.UnlockState.CharacterCardPools
                .Where(pool => player.UnlockState.CharacterCardPools.Count() <= 1 || pool != player.Character.CardPool)
                .SelectMany(pool => pool.GetUnlockedCards(player.UnlockState, constraint)),
            CardPools.CharacterShots => player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, constraint)
                .Where(static card => card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot)),
            CardPools.Melodies or CardPools.Wah or CardPools.Cho or CardPools.Ki => player.UnlockState.CharacterCardPools
                .SelectMany(pool => pool.GetUnlockedCards(player.UnlockState, constraint))
                .Where(card => poolKey == CardPools.Melodies
                    ? card.Tags.Any(tag => tag is MegaCrit.Sts2.Core.Entities.Cards.CardTag.Wah
                        or MegaCrit.Sts2.Core.Entities.Cards.CardTag.Cho or MegaCrit.Sts2.Core.Entities.Cards.CardTag.Ki)
                    : card.Tags.Contains(poolKey == CardPools.Wah ? MegaCrit.Sts2.Core.Entities.Cards.CardTag.Wah
                        : poolKey == CardPools.Cho ? MegaCrit.Sts2.Core.Entities.Cards.CardTag.Cho
                        : MegaCrit.Sts2.Core.Entities.Cards.CardTag.Ki)),
            CardPools.CharacterAll => player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, constraint),
            CardPools.CharacterAttacks => player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, constraint)
                .Where(static card => card.Type == NativeCardType.Attack),
            CardPools.CharacterZeroCost => player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, constraint)
                .Where(static card => card.EnergyCost is { Canonical: 0, CostsX: false }),
            CardPools.ColorlessAll => ModelDb.CardPool<ColorlessCardPool>()
                .GetUnlockedCards(player.UnlockState, constraint),
            CardPools.ColorlessExceptJackOfAllTrades => ModelDb.CardPool<ColorlessCardPool>()
                .GetUnlockedCards(player.UnlockState, constraint)
                .Where(static card => card is not JackOfAllTrades),
            CardPools.OtherCharacterAttacks => OtherCharacterAttacks(player, constraint),
            CardPools.CharacterPowers => player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, constraint)
                .Where(static card => card.Type == NativeCardType.Power),
            CardPools.CharacterEthereal => player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, constraint)
                .Where(static card => card.Keywords.Contains(CardKeyword.Ethereal)),
            CardPools.StatusAll => ModelDb.CardPool<StatusCardPool>()
                .GetUnlockedCards(player.UnlockState, constraint),
            CardPools.CurseAll => ModelDb.CardPool<CurseCardPool>()
                .GetUnlockedCards(player.UnlockState, constraint),
            _ => throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                $"未知卡池 {poolKey}"),
        };
        List<CardModel> candidates = (poolKey == CardPools.Tarot ? cards : FilterForCombatAndPlayerCount(cards, constraint))
            .Where(card => card.IsAllowedInRun(player.RunState)).ToList();
        ushort[] ids = new ushort[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            CardModel candidate = candidates[index];
            if (!Registry.TryCard(candidate.Id.Entry, out ushort id))
                throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                    $"生成卡 {candidate.Id.Entry} 没有 Sts2Solver 定义（卡池 {poolKey}）");
            ids[index] = id;
        }
        return ids;
    }

    private static IEnumerable<CardModel> OtherCharacterAttacks(Player player,
        CardMultiplayerConstraint constraint)
    {
        List<CardPoolModel> pools = player.UnlockState.CharacterCardPools.ToList();
        if (pools.Count > 1)
            pools.Remove(player.Character.CardPool);
        return pools
            .SelectMany(pool => pool.GetUnlockedCards(player.UnlockState, constraint))
            .Where(static card => card.Type == NativeCardType.Attack);
    }

    private static IEnumerable<CardModel> FilterForCombatAndPlayerCount(
        IEnumerable<CardModel> cards, CardMultiplayerConstraint constraint)
    {
        IEnumerable<CardModel> combatCards = CardFactory.FilterForCombat(cards);
        return constraint == CardMultiplayerConstraint.SingleplayerOnly
            ? combatCards.Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            : combatCards.Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.SingleplayerOnly);
    }

    private static IEnumerable<string> PowerIdsWithVars()
    {
        for (ushort id = 0; id < Registry.PowerCount; id++)
        {
            PowerDef def = Registry.Power(id);
            if (def.VarNames.Count > 0)
                yield return def.NativeId;
        }
    }

    private static void AddRelicVars(RulesDraft builder, ushort id)
        => builder.SetRelicVars(id, CompileRelicVars(id));

    internal static decimal[] CompileRelicVars(ushort id)
    {
        RelicDef def = Registry.Relic(id);
        if (def.VarNames.Count == 0)
            return [];
        RelicModel canonical = CanonicalRelics.Value.TryGetValue(def.NativeId, out RelicModel? relic)
            ? relic
            : throw new UnsupportedImportException(ImportReason.UnsupportedRelic,
                $"原版没有遗物 {def.NativeId}");
        decimal[] vars = new decimal[def.VarNames.Count];
        for (int index = 0; index < vars.Length; index++)
        {
            vars[index] = canonical.DynamicVars.TryGetValue(def.VarNames[index], out DynamicVar? variable)
                ? variable.BaseValue
                : throw new UnsupportedImportException(ImportReason.UnsupportedRelic,
                    $"遗物 {def.NativeId} 没有动态变量 {def.VarNames[index]}");
        }
        return vars;
    }

    private static void AddPotionStats(RulesDraft builder, ushort id)
        => builder.SetPotion(id, CompilePotionStats(id));

    internal static PotionStats CompilePotionStats(ushort id, bool inCombat = true)
    {
        PotionDef def = Registry.Potion(id);
        PotionModel canonical = CanonicalPotions.Value.TryGetValue(def.NativeId, out PotionModel? potion)
            ? potion
            : throw new UnsupportedImportException(ImportReason.UnsupportedPotion,
                $"原版没有药水 {def.NativeId}");
        decimal[] vars = new decimal[def.VarNames.Count];
        ValueProps[] props = new ValueProps[vars.Length];
        for (int index = 0; index < vars.Length; index++)
        {
            DynamicVar variable = canonical.DynamicVars.TryGetValue(def.VarNames[index],
                out DynamicVar? found)
                ? found
                : throw new UnsupportedImportException(ImportReason.UnsupportedPotion,
                    $"药水 {def.NativeId} 没有动态变量 {def.VarNames[index]}");
            vars[index] = variable.BaseValue;
            props[index] = variable switch
            {
                DamageVar damage => (ValueProps)(byte)damage.Props,
                BlockVar block => (ValueProps)(byte)block.Props,
                _ => ValueProps.None,
            };
        }
        return new PotionStats
        {
            // FoulPotion's native TargetType switches on CombatManager.IsInProgress; the rules
            // compiler only runs inside a battle, so the table also stores the in-combat value.
            Target = canonical is FoulPotion && inCombat
                ? EngineTargetType.AllEnemies
                : (EngineTargetType)(byte)canonical.TargetType,
            Rarity = (EnginePotionRarity)(byte)canonical.Rarity,
            StrategicCost = PotionStrategicCost(canonical),
            Vars = vars,
            VarProps = props,
        };
    }

    private static void AddPowerVars(RulesDraft builder, string nativeId)
    {
        Registry.TryPower(nativeId, out ushort id);
        builder.SetPowerVars(id, CompilePowerVars(nativeId));
    }

    internal static decimal[] CompilePowerVars(string nativeId)
    {
        Registry.TryPower(nativeId, out ushort id);
        PowerDef def = Registry.Power(id);
        PowerModel canonical = CanonicalPowers.Value.TryGetValue(nativeId, out PowerModel? power)
            ? power
            : throw new UnsupportedImportException(ImportReason.UnsupportedPower,
                $"原版没有 Power {nativeId}");
        decimal[] vars = new decimal[def.VarNames.Count];
        for (int index = 0; index < vars.Length; index++)
        {
            vars[index] = canonical.DynamicVars.TryGetValue(def.VarNames[index], out DynamicVar? variable)
                ? variable.BaseValue
                : throw new UnsupportedImportException(ImportReason.UnsupportedPower,
                    $"Power {nativeId} 没有动态变量 {def.VarNames[index]}");
        }
        return vars;
    }

    /// <summary>
    /// <c>PotionUsePolicy.StrategicHpCost</c> at a62f1850. The cost is a solver policy, not native
    /// data; values are the same frozen weights the reference rules compiler records.
    /// </summary>
    private static int PotionStrategicCost(PotionModel potion)
    {
        if (potion.Rarity == NativePotionRarity.Token)
            return 0;
        return potion.Id.Entry switch
        {
            "GLOWWATER_POTION" or "SWIFT_POTION" or "GAMBLERS_BREW" or "DUPLICATOR"
                or "OROBIC_ACID" or "POT_OF_GHOULS" => 18,
            "DISTILLED_CHAOS" or "CLARITY" or "RADIANT_TINCTURE" or "CURE_ALL"
                or "LIQUID_MEMORIES" or "BOTTLED_POTENTIAL" or "TOUCH_OF_INSANITY" => 14,
            _ => 9,
        };
    }

    internal static MonsterRules CompileMonster(MonsterModel monster, Creature creature,
        bool rootPowerAmountsFromCreature, int[]? initialAiRegistersOverride = null,
        int[]? rootPowerAmountsOverride = null)
    {
        if (!Registry.TryMonster(monster.Id.Entry, out ushort monsterId))
            throw new UnsupportedImportException(ImportReason.UnsupportedMonster, monster.Id.Entry);
        MonsterDef def = Registry.Monster(monsterId);
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                $"{monster.Id.Entry} 没有行动状态机");
        List<MonsterState> registeredStates = machine.States.Values.ToList();
        MonsterStaticSnapshot snapshot = CaptureStaticSnapshot(registeredStates, monster, creature);
        List<MonsterState> states = [.. registeredStates];
        // A native initial branch may live outside States (PhantasmalGardener's INIT_MOVE); it is
        // compiled after the registered states so their indices stay those of States. Its static
        // entry is absent, matching the reference engine's registered-only snapshot: an
        // unregistered conditional has no root-resolved target and an unregistered move has no
        // attacks.
        if (InitialStateField.GetValue(machine) is MonsterState { } unregistered
            && !machine.States.ContainsKey(unregistered.Id))
        {
            states.Add(unregistered);
        }
        List<string> ids = states.Select(static state => state.Id).ToList();
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        for (int position = 0; position < ids.Count; position++)
            index[ids[position]] = position;
        int nodeCount = ids.Count + 1;
        if (def.IllusionRevive)
            nodeCount++;
        if (nodeCount > 64)
            throw new UnsupportedImportException(ImportReason.Capacity,
                $"{monster.Id.Entry} 状态数 {ids.Count}");

        MonsterNode[] nodes = new MonsterNode[nodeCount];
        for (int position = 0; position < ids.Count; position++)
        {
            MonsterState state = states[position];
            nodes[position] = state switch
            {
                MoveState move => CompileMove(move, snapshot, index, monster),
                RandomBranchState random => new MonsterNode
                {
                    NativeId = random.Id,
                    Kind = MonsterNodeKind.RandomBranch,
                    Options = random.States.Select(option => new RandomBranchOption(
                        index.TryGetValue(option.stateId, out int target)
                            ? target
                            : throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                                $"{monster.Id.Entry}.{option.stateId}"),
                        snapshot.RandomBaseWeights.TryGetValue((random.Id, option.stateId),
                            out float weight)
                            ? weight
                            : throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                                $"{monster.Id.Entry} 随机权重 {random.Id}.{option.stateId}"),
                        (EngineMoveRepeatType)(byte)option.repeatType,
                        option.maxTimes,
                        option.cooldown)).ToArray(),
                },
                ConditionalBranchState conditional => new MonsterNode
                {
                    NativeId = conditional.Id,
                    Kind = MonsterNodeKind.ConditionalBranch,
                    ConditionalTarget = snapshot.ConditionalSelections.TryGetValue(conditional.Id,
                        out string? selected)
                        ? index.TryGetValue(selected, out int target) ? target : -1
                        : -1,
                },
                _ => throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                    $"{monster.Id.Entry} 状态 {state.GetType().Name}"),
            };
        }

        MonsterState initialState = InitialStateField.GetValue(machine) as MonsterState
            ?? throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                $"{monster.Id.Entry} 没有初始状态");
        int initial = index.TryGetValue(initialState.Id, out int initialIndex)
            ? initialIndex
            : throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                $"{monster.Id.Entry} 初始状态 {initialState.Id} 不在节点表");
        int stunned = ids.Count;
        nodes[stunned] = new MonsterNode
        {
            NativeId = Kernel.StunnedNodeId,
            Kind = MonsterNodeKind.Move,
            MustPerformOnceBeforeTransitioning = true,
        };
        int revive = -1;
        if (def.IllusionRevive)
        {
            revive = stunned + 1;
            nodes[revive] = new MonsterNode
            {
                NativeId = Kernel.ReviveNodeId,
                Kind = MonsterNodeKind.Move,
                MustPerformOnceBeforeTransitioning = true,
            };
        }
        int pendingInitial = -1;
        for (int position = 0; position < ids.Count; position++)
        {
            if (states[position] is MoveState)
            {
                pendingInitial = position;
                break;
            }
        }
        if (pendingInitial < 0)
            throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                $"{monster.Id.Entry} 没有可用行动状态");
        int[] staticInts = new int[def.StaticIntNames.Count];
        for (int position = 0; position < staticInts.Length; position++)
        {
            staticInts[position] = NativeAccess.MonsterInt(monster, def.StaticIntNames[position]);
        }
        int[] rootPowerAmounts = rootPowerAmountsOverride
            ?? new int[def.RootPowerAmountNames.Count];
        if (rootPowerAmountsOverride == null)
        {
            for (int position = 0; position < rootPowerAmounts.Length; position++)
            {
                string nativePower = def.RootPowerAmountNames[position];
                rootPowerAmounts[position] = 0;
                if (!rootPowerAmountsFromCreature)
                    continue;
                foreach (PowerModel power in creature.Powers)
                {
                    if (power.Amount != 0 && power.Id.Entry == nativePower)
                    {
                        rootPowerAmounts[position] = power.Amount;
                        break;
                    }
                }
            }
        }
        int[] initialAiRegisters = initialAiRegistersOverride
            ?? new int[def.AiRegisterNames.Count];
        if (initialAiRegistersOverride == null)
        {
            for (int position = 0; position < initialAiRegisters.Length; position++)
            {
                initialAiRegisters[position] = NativeAccess.MonsterInt(monster,
                    def.AiRegisterNames[position]);
            }
        }
        return new MonsterRules
        {
            MonsterId = monsterId,
            Nodes = nodes,
            InitialNode = initial,
            PendingInitialMoveNode = pendingInitial,
            InitialMoveLogged = initialState is MoveState,
            StunnedNode = stunned,
            ReviveNode = revive,
            InitialHpMin = monster.MinInitialHp,
            InitialHpMax = monster.MaxInitialHp,
            StaticInts = staticInts,
            RootPowerAmounts = rootPowerAmounts,
            InitialAiRegisters = initialAiRegisters,
        };
    }

    private static MonsterNode CompileMove(MoveState move, MonsterStaticSnapshot snapshot,
        IReadOnlyDictionary<string, int> index, MonsterModel monster)
    {
        string? followUp = move.FollowUpState?.Id ?? move.FollowUpStateId;
        IReadOnlyList<BranchMonsterAttack> attacks =
            snapshot.AttacksByMove.TryGetValue(move.Id, out IReadOnlyList<BranchMonsterAttack>? captured)
                ? captured
                : [];
        // MULTI_CLAW's intent repeats are dynamic (BaseMultiClawCount + _extraMultiClawCount at
        // capture time). The kernel adds the live extra count in TestSubject.ModifyAttackRepeats,
        // so compiling the captured total would add it twice.
        bool fixedTestSubjectMultiClaw = snapshot.TestSubjectBaseMultiClawCount > 0
            && move.Id == "MULTI_CLAW_MOVE";
        return new MonsterNode
        {
            NativeId = move.Id,
            Kind = MonsterNodeKind.Move,
            FollowUp = followUp != null && index.TryGetValue(followUp, out int target) ? target : -1,
            MustPerformOnceBeforeTransitioning = move.MustPerformOnceBeforeTransitioning,
            Attacks = attacks.Select(attack => new MonsterAttack(
                attack.BaseDamage,
                fixedTestSubjectMultiClaw
                    ? snapshot.TestSubjectBaseMultiClawCount
                    : attack.Repeats)).ToArray(),
        };
    }

    private static MonsterStaticSnapshot CaptureStaticSnapshot(IReadOnlyList<MonsterState> states,
        MonsterModel monster, Creature creature)
    {
        Dictionary<string, IReadOnlyList<BranchMonsterAttack>> attacks =
            new(StringComparer.Ordinal);
        Dictionary<(string BranchId, string StateId), float> weights = [];
        Dictionary<string, string> conditionals = new(StringComparer.Ordinal);
        foreach (MonsterState state in states)
        {
            if (state is MoveState move)
            {
                attacks[move.Id] = CaptureAttacks(move, monster);
            }
            else if (state is RandomBranchState random)
            {
                foreach (RandomBranchState.StateWeight weight in random.States)
                    weights.Add((random.Id, weight.stateId), weight.GetWeight());
            }
            else if (state is ConditionalBranchState conditional)
            {
                conditionals.Add(conditional.Id, ResolveConditional(conditional, creature, monster));
            }
        }
        int baseMultiClaw = monster.GetType().Name == "TestSubject"
            ? NativeAccess.MonsterInt(monster, "BaseMultiClawCount")
            : 0;
        return new MonsterStaticSnapshot(attacks, weights, conditionals, baseMultiClaw);
    }

    private static IReadOnlyList<BranchMonsterAttack> CaptureAttacks(MoveState move,
        MonsterModel monster)
    {
        List<BranchMonsterAttack> attacks = [];
        foreach (AbstractIntent intent in move.Intents)
        {
            if (intent is not AttackIntent attack)
                continue;
            Func<decimal> calculate = attack.DamageCalc
                ?? throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                    $"{monster.GetType().FullName}.{move.Id} 的攻击意图没有伤害计算");
            attacks.Add(new BranchMonsterAttack(
                Math.Max(0, (int)calculate()),
                Math.Max(1, attack.Repeats)));
        }
        return attacks;
    }

    private static string ResolveConditional(ConditionalBranchState conditional, Creature creature,
        MonsterModel monster)
    {
        try
        {
            return conditional.GetNextState(creature, new Rng(0));
        }
        catch (InvalidOperationException error)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedMonster,
                $"{monster.Id.Entry}.{conditional.Id} 条件分支无法解析：{error.Message}");
        }
    }

    private sealed record MonsterStaticSnapshot(
        IReadOnlyDictionary<string, IReadOnlyList<BranchMonsterAttack>> AttacksByMove,
        IReadOnlyDictionary<(string BranchId, string StateId), float> RandomBaseWeights,
        IReadOnlyDictionary<string, string> ConditionalSelections,
        int TestSubjectBaseMultiClawCount);

    private readonly record struct BranchMonsterAttack(int BaseDamage, int Repeats);

    private static Keywords MapKeywords(IEnumerable<CardKeyword> keywords)
    {
        Keywords result = Keywords.None;
        foreach (CardKeyword keyword in keywords)
        {
            result |= keyword switch
            {
                CardKeyword.Exhaust => Keywords.Exhaust,
                CardKeyword.Ethereal => Keywords.Ethereal,
                CardKeyword.Innate => Keywords.Innate,
                CardKeyword.Unplayable => Keywords.Unplayable,
                CardKeyword.Retain => Keywords.Retain,
                CardKeyword.Sly => Keywords.Sly,
                CardKeyword.Eternal => Keywords.Eternal,
                _ => throw new UnsupportedImportException(ImportReason.UnsupportedCard,
                    $"关键字 {keyword}"),
            };
        }
        return result;
    }

    private static EngineCardTag MapTags(IEnumerable<NativeCardTag> tags)
    {
        EngineCardTag result = EngineCardTag.None;
        foreach (NativeCardTag tag in tags)
        {
            result |= tag switch
            {
                NativeCardTag.Strike => EngineCardTag.Strike,
                NativeCardTag.Defend => EngineCardTag.Defend,
                NativeCardTag.Minion => EngineCardTag.Minion,
                NativeCardTag.OstyAttack => EngineCardTag.OstyAttack,
                NativeCardTag.Shiv => EngineCardTag.Shiv,
                NativeCardTag.Shoot => EngineCardTag.Shoot,
                NativeCardTag.Wah => EngineCardTag.Wah,
                NativeCardTag.Cho => EngineCardTag.Cho,
                NativeCardTag.Ki => EngineCardTag.Ki,
                _ => throw new UnsupportedImportException(ImportReason.UnsupportedCard,
                    $"卡牌标签 {tag}"),
            };
        }
        return result;
    }
    private static RelicRoster CompileRelicRoster(Player player)
    {
        if (player.Relics.Count > Limits.RelicRoster)
            throw new CapacityException("RelicRoster", Limits.RelicRoster);
        Relic[] rows = new Relic[player.Relics.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            RelicModel native = player.Relics[i];
            if (!Registry.TryRelic(native.Id.Entry, out ushort id))
                throw UnsupportedContent.Relic(native);
            rows[i] = new Relic { Id = id, Flags = native.IsMelted ? RelicFlags.Melted : RelicFlags.None };
            if (native is PhilosophersStone stone) rows[i].Aux = stone.DynamicVars["StrengthPower"].IntValue;
            if (native is PumpkinCandle candle) rows[i].Counter = candle.KindleCount;
        }
        // Melted rows retain imported private values too. The importer supplies their full snapshot.
        // Keep declared state slots even for melted rows so fixed metadata does not need live counters.
        return RelicRoster.Create(rows);
    }

}
