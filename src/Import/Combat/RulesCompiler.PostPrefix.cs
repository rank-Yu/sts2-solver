// Adapted from CombatSolver src/Flash/Bridge/FlashRulesCompiler.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). The reference bridge compiles rules from
// its post-prefix shadow simulator; this port reads the replayed Battle for the content-dependent
// inputs and the pre-prefix native state for the request-root inputs.

// Post-prefix rule compilation: the fixed prefix runs on the Sts2Solver kernel, so the native state only
// exists before it. The static/run-derived inputs come from that native root; every combat-content
// input (piles, powers, relics, potions, detached cards, known enemies and their live AI registers)
// comes from the replayed Battle. The reference bridge compiles the same way from its post-prefix
// shadow simulator.
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

using Creature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Solver.Import;

public static partial class RulesCompiler
{
    /// <summary>Compiles the rule table of the state after a fixed prefix replayed on the kernel.</summary>
    public static Rules CompileAfterPrefix(CombatState state, Player player, RunState runState,
        in Battle root)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(runState);
        if (!ReferenceEquals(state.RunState, runState))
            throw new UnsupportedImportException(ImportReason.RulesMismatch, "传入的 RunState 不是战斗状态的 RunState");
        if (state.Players.Count != 1)
            throw new UnsupportedImportException(ImportReason.MultiPlayer, $"玩家数 {state.Players.Count}");
        if (!player.Creature.IsAlive)
            throw new UnsupportedImportException(ImportReason.Unrepresentable, "玩家已死亡");
        if (root.EnemyCount == 0)
            throw new UnsupportedImportException(ImportReason.UnsupportedMonster, "前缀之后没有已知敌人");
        for (int index = 0; index < root.EnemyCount; index++)
        {
            ushort monsterId = root.Enemies[index].MonsterId;
            MonsterDef def = Registry.Monster(monsterId);
            if (!CanonicalMonsters.Value.ContainsKey(def.NativeId))
                throw new UnsupportedImportException(ImportReason.UnsupportedMonster, def.NativeId);
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

        RulesDraft builder = new()
        {
            BaseMaxEnergy = player.MaxEnergy,
            BaseOrbSlotCount = player.Character.BaseOrbSlotCount,
            RootSwordSageAmount = PowerAmount(root, Limits.PlayerOwner, "SWORD_SAGE_POWER"),
        };
        bool hasIdentityContent = builder.RootSwordSageAmount != 0
            || player.Character is Regent or Necrobinder or Silent;
        bool hasShuffleOrderEnchantment = false;
        bool hasBeforeFlushEnchantment = false;
        for (int index = 0; index < root.CardCount; index++)
        {
            ref readonly Card card = ref root.Cards[index];
            CardDef def = Registry.Card(card.Id);
            CardModel canonical = CanonicalCard(def);
            AddCard(builder, card.Id, card.Upgrade, canonical, player);
            hasIdentityContent |= def.UsesIdentityLedger || def.TracksFetchPlay
                || def.MayGenerateIdentityContent || def.IsIdentityContent;
            if (card.EnchantmentId != 0)
            {
                EnchantmentDef enchantment = Registry.Enchantment((ushort)(card.EnchantmentId - 1));
                hasShuffleOrderEnchantment |= (enchantment.HooksExt & HookMaskExt.ModifyShuffleOrder) != 0;
                hasBeforeFlushEnchantment |= (enchantment.HooksExt & HookMaskExt.BeforeFlush) != 0;
            }
        }
        builder.HasShuffleOrderEnchantment = hasShuffleOrderEnchantment;
        builder.HasBeforeFlushEnchantment = hasBeforeFlushEnchantment;

        for (int index = 0; index < root.PowerCount; index++)
        {
            ref readonly Power power = ref root.Powers[index];
            if (power.Amount == 0)
                continue;
            PowerDef def = Registry.Power(power.Id);
            hasIdentityContent |= def.IsIdentityContent;
        }

        ushort lampId = ushort.MaxValue;
        bool hasPaelsLegion = false;
        bool furCoatSpawnHpOne = false;
        bool hasHistoryCourse = false;
        sbyte penNibRelicIndex = -1;
        for (int index = 0; index < root.RelicCount; index++)
        {
            ref readonly Relic relic = ref root.Relics[index];
            RelicDef def = Registry.Relic(relic.Id);
            hasIdentityContent |= def.MayGenerateIdentityContent || def.IsIdentityContent;
            if (lampId == ushort.MaxValue && def.IsUnsettlingLamp)
                lampId = relic.Id;
            if ((relic.Flags & RelicFlags.Melted) != 0)
                continue;
            if (def.NativeId == "HISTORY_COURSE")
                hasHistoryCourse = true;
            if (def.NativeId == "PEN_NIB")
                penNibRelicIndex = checked((sbyte)index);
            if (def.IsPaelsLegion)
                hasPaelsLegion = true;
            if (def.NativeId == "FUR_COAT"
                && state.RunState.CurrentMapCoord is { } mapCoord
                && player.Relics.OfType<FurCoat>()
                    .FirstOrDefault(relicModel => relicModel.GetMarkedCoords()?.Contains(mapCoord) == true)
                    is not null)
            {
                furCoatSpawnHpOne = true;
            }
        }
        builder.HasIdentityContent = hasIdentityContent;
        builder.HasPaelsLegion = hasPaelsLegion;
        builder.FurCoatSpawnHpOne = furCoatSpawnHpOne;
        builder.HasHistoryCourse = hasHistoryCourse;
        builder.PenNibRelicIndex = penNibRelicIndex;
        builder.HasTheftContent = HasTheftContent(root, state);

        foreach (string nativeId in PowerIdsWithVars())
            AddPowerVars(builder, nativeId);
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
        if (root.EnemyCount > Limits.Enemies)
            throw new UnsupportedImportException(ImportReason.Capacity, $"敌人数 {root.EnemyCount}");

        ushort rootLostHpMask = 0;
        if ((root.Player.Flags & CreatureFlags.LostHpThisTurn) != 0)
            rootLostHpMask |= 1 << Limits.PlayerOwner;
        if ((root.Flags & BattleFlags.OstyPresent) != 0
            && (root.Osty.Flags & CreatureFlags.LostHpThisTurn) != 0)
        {
            rootLostHpMask |= 1 << Limits.OstyOwner;
        }
        for (int index = 0; index < root.EnemyCount; index++)
        {
            if ((root.Enemies[index].Creature.Flags & CreatureFlags.LostHpThisTurn) != 0)
                rootLostHpMask |= (ushort)(1 << (Limits.FirstEnemyOwner + index));
        }
        builder.RootLostHpMask = rootLostHpMask;

        List<ushort> spawnClosure = SpawnClosure(root);
        if (root.EnemyCount + spawnClosure.Count > Limits.MonsterRules)
            throw new UnsupportedImportException(ImportReason.Capacity,
                $"怪物规则 {root.EnemyCount}+{spawnClosure.Count}");

        List<(string CardId, int Upgrade)> detachedCards = [];
        for (int index = 0; index < Limits.DetachedCards; index++)
        {
            ref readonly Card card = ref root.DetachedCards[index];
            if (card.Id != 0)
                detachedCards.Add((Registry.Card(card.Id).NativeId, card.Upgrade));
        }
        AddGeneratedCardClosure(builder, root, player, spawnClosure, detachedCards);
        builder.HasInfiniteHpDisplayMonster = HasInfiniteHpDisplayMonster(root, spawnClosure);
        builder.RootEnemyCount = root.EnemyCount;
        builder.HasEncounter = state.Encounter != null;
        builder.EncounterSlots = state.Encounter?.Slots.ToArray() ?? [];

        for (int index = 0; index < root.EnemyCount; index++)
            builder.SetMonster(index, CompilePostPrefixMonster(state, runState, root, index));
        foreach (ushort monsterId in spawnClosure)
            CompileSpawnedMonster(builder, state, runState, monsterId);
        return builder.Build();
    }

    private static CardModel CanonicalCard(CardDef def)
        => CanonicalCards.Value.TryGetValue(def.NativeId, out CardModel? card)
            ? card
            : throw new UnsupportedImportException(ImportReason.UnsupportedCard, def.NativeId);

    private static int PowerAmount(in Battle root, byte owner, string nativeId)
    {
        if (!Registry.TryPower(nativeId, out ushort id))
            return 0;
        for (int index = 0; index < root.PowerCount; index++)
        {
            ref readonly Power power = ref root.Powers[index];
            if (power.Owner == owner && power.Id == id && power.Amount > 0)
                return power.Amount;
        }
        return 0;
    }

    private static bool HasTheftContent(in Battle root, CombatState state)
    {
        if (state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK")
            return true;
        for (int index = 0; index < root.EnemyCount; index++)
        {
            string nativeId = Registry.Monster(root.Enemies[index].MonsterId).NativeId;
            if (CanonicalMonsters.Value.TryGetValue(nativeId, out MonsterModel? monster)
                && monster is GremlinMerc or ThievingHopper or FatGremlin)
            {
                return true;
            }
        }
        for (int index = 0; index < root.PowerCount; index++)
        {
            if (root.Powers[index].Amount == 0)
                continue;
            string nativeId = Registry.Power(root.Powers[index].Id).NativeId;
            if (CanonicalPowers.Value.TryGetValue(nativeId, out PowerModel? power)
                && power is ThieveryPower or HeistPower or SwipePower or SurprisePower)
            {
                return true;
            }
        }
        return false;
    }

    private static List<ushort> SpawnClosure(in Battle root)
    {
        List<ushort> closure = [];
        HashSet<ushort> seen = [];
        Queue<string> pending = new();
        void Declare(MonsterDef def)
        {
            foreach (string nativeId in def.SpawnedMonsterIds)
                pending.Enqueue(nativeId);
        }
        for (int index = 0; index < root.EnemyCount; index++)
            Declare(Registry.Monster(root.Enemies[index].MonsterId));
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

    private static bool HasInfiniteHpDisplayMonster(in Battle root, IReadOnlyList<ushort> spawnClosure)
    {
        for (int index = 0; index < root.EnemyCount; index++)
        {
            if (Registry.Monster(root.Enemies[index].MonsterId).HasInfiniteHpDisplay)
                return true;
        }
        foreach (ushort monsterId in spawnClosure)
        {
            if (Registry.Monster(monsterId).HasInfiniteHpDisplay)
                return true;
        }
        return false;
    }

    private static MonsterRules CompilePostPrefixMonster(CombatState state, RunState runState,
        in Battle root, int index)
    {
        ref readonly Enemy enemy = ref root.Enemies[index];
        MonsterDef def = Registry.Monster(enemy.MonsterId);
        int[] ai = new int[def.AiRegisterNames.Count];
        for (int register = 0; register < ai.Length; register++)
            ai[register] = enemy.Ai[register];
        // The reference compiler reads these baselines from the request root (pre-prefix) by
        // CombatId; an enemy summoned by the prefix has no baseline and contributes zero.
        int[] rootPowerAmounts = new int[def.RootPowerAmountNames.Count];
        Creature? nativeCreature = FindNativeCreature(state, enemy.CombatId);
        if (nativeCreature != null)
        {
            for (int position = 0; position < rootPowerAmounts.Length; position++)
            {
                string nativePower = def.RootPowerAmountNames[position];
                foreach (PowerModel power in nativeCreature.Powers)
                {
                    if (power.Amount != 0 && power.Id.Entry == nativePower)
                    {
                        rootPowerAmounts[position] = power.Amount;
                        break;
                    }
                }
            }
        }

        if (nativeCreature?.Monster is { } nativeMonster
            && Registry.TryMonster(nativeMonster.Id.Entry, out ushort nativeId)
            && nativeId == enemy.MonsterId)
        {
            return CompileMonster(nativeMonster, nativeCreature,
                rootPowerAmountsFromCreature: false, ai, rootPowerAmounts);
        }

        MonsterModel canonical = CanonicalMonsters.Value.TryGetValue(def.NativeId, out MonsterModel? found)
            ? found
            : throw new UnsupportedImportException(ImportReason.UnsupportedMonster, def.NativeId);
        MonsterModel mutable = canonical.ToMutable();
        mutable.RunRng = runState.Rng;
        mutable.Rng = new Rng(0);
        string? slot = enemy.Slot < (state.Encounter?.Slots.Count ?? 0)
            ? state.Encounter!.Slots[enemy.Slot]
            : null;
        CombatState scratch = new(encounter: state.Encounter, runState);
        Creature creature = new(mutable, CombatSide.Enemy, slot)
        {
            CombatState = scratch,
            CombatId = enemy.CombatId,
        };
        scratch.AddCreature(creature);
        mutable.SetUpForCombat();
        creature.SetMaxHpInternal(enemy.Creature.MaxHp);
        creature.SetCurrentHpInternal(enemy.Creature.CurrentHp);
        creature.GainBlockInternal(enemy.Creature.Block);
        return CompileMonster(mutable, creature,
            rootPowerAmountsFromCreature: false, ai, rootPowerAmounts);
    }

    private static Creature? FindNativeCreature(CombatState state, uint combatId)
    {
        foreach (Creature creature in state.Enemies)
        {
            if (creature.CombatId == combatId)
                return creature;
        }
        return null;
    }

    private static void AddGeneratedCardClosure(RulesDraft builder, in Battle root, Player player,
        IReadOnlyList<ushort> spawnClosure, IReadOnlyList<(string CardId, int Upgrade)> detachedCards)
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
        for (int index = 0; index < root.CardCount; index++)
            Declare(Registry.Card(root.Cards[index].Id));
        for (int index = 0; index < root.PowerCount; index++)
        {
            if (root.Powers[index].Amount != 0)
                Declare(Registry.Power(root.Powers[index].Id));
        }
        for (int index = 0; index < root.RelicCount; index++)
            Declare(Registry.Relic(root.Relics[index].Id));
        for (int slot = 0; slot < root.PotionSlotCount && slot < Limits.PotionSlots; slot++)
        {
            ushort stored = root.Potions[slot];
            if (stored != 0)
                Declare(Registry.Potion((ushort)(stored - 1)));
        }
        for (int index = 0; index < root.EnemyCount; index++)
            Declare(Registry.Monster(root.Enemies[index].MonsterId));
        foreach (ushort monsterId in spawnClosure)
            Declare(Registry.Monster(monsterId));

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

        for (int slot = 0; slot < root.PotionSlotCount && slot < Limits.PotionSlots; slot++)
        {
            ushort stored = root.Potions[slot];
            if (stored != 0)
                builder.SetRootPotion(slot, (ushort)(stored - 1));
        }
    }
}
