// Adapted from CombatSolver/src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs,
// src/Testing/Host/UnattendedTestRunner.GeneratedScenario.cs,
// src/Testing/Support/UnattendedTestRunner.Fixtures.cs and
// src/Testing/Support/UnattendedTestRunner.StateDiff.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
// Carries only the run-creation, loadout injection, initial-state injection and playable wait needed
// to build a native combat root. Checkpoints, replay, Showcase, search and deployment are omitted.
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2Solver.TestHost;

internal static class ScenarioBuilder
{
    private const int MaxBulkCardInjectionCount = 4096;

    internal sealed class PreparedRun : IDisposable
    {
        private readonly IDisposable[] _scopes;

        internal PreparedRun(RunState run, Player player, OfflineRequest request,
            EncounterModel encounter, GeneratedScenarioSetup setup, IDisposable[] scopes)
        {
            Run = run;
            Player = player;
            Request = request;
            Encounter = encounter;
            Setup = setup;
            _scopes = scopes;
        }

        internal RunState Run { get; }
        internal Player Player { get; }
        internal OfflineRequest Request { get; }
        internal EncounterModel Encounter { get; }
        internal GeneratedScenarioSetup Setup { get; }

        public void Dispose()
        {
            for (int index = _scopes.Length - 1; index >= 0; index--)
                _scopes[index].Dispose();
        }
    }

    internal static async Task<PreparedRun> PrepareRunAsync(ICombatHost waiter, OfflineRequest request,
        TurnSetupCardSelector? turnSetupSelector = null, GeneratedScenarioSetup? preparedSetup = null)
    {
        GeneratedScenarioSetup setup = preparedSetup ?? GeneratedScenarioSetup.Prepare(request);
        request = setup.Request;
        ResolvedGeneratedCombatScenario? generated = setup.Resolved;
        GeneratedScenarioCardSelector? generatedSelector = setup.Selector;
        ValidateSupported(request);
        TestHostLog.Trace("scenario.validate");

        CharacterModel character = NativeLookup.ResolveUnique(ModelDb.AllCharacters, request.CharacterId, "角色");
        EncounterModel encounter = NativeLookup.ResolveUnique(
            ModelDb.All.OfType<EncounterModel>(), request.EncounterId, "遭遇");

        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("已经有进行中的跑局。");

        List<IDisposable> scopes = [];
        if (generatedSelector != null)
            scopes.Add(CardSelectCmd.PushSelector(generatedSelector, localOnly: true));
        if (turnSetupSelector != null)
            scopes.Add(CardSelectCmd.PushSelector(turnSetupSelector, localOnly: true));
        else if (request.FlashTurnSetupRootForTest)
            scopes.Add(CardSelectCmd.PushSelector(new FirstChoiceCardSelector(), localOnly: true));
        if (generatedSelector == null
            && request.ScenarioId.StartsWith("FLASH-PR-", StringComparison.Ordinal))
            scopes.Add(CardSelectCmd.PushSelector(new FirstChoiceCardSelector(), localOnly: true));

        TestHostLog.Trace("scenario.start_run");
        RunState runState = await waiter.StartRunAsync(character, request.Ascension, request.Seed);
        TestHostLog.Trace("scenario.run_started");

        if (request.ActIndexForTest != 0)
        {
            if ((uint)request.ActIndexForTest >= (uint)runState.Acts.Count)
                throw new InvalidOperationException($"测试幕索引超出范围：{request.ActIndexForTest}。");
            await RunManager.Instance.SetActInternal(request.ActIndexForTest);
        }

        Player player = LocalContext.GetMe(runState)
            ?? throw new InvalidOperationException("创建跑局后找不到本地玩家。");

        if (generated?.Options.IncludeStartingRelics == false)
        {
            foreach (RelicModel relic in player.Relics.ToArray())
                player.RemoveRelicInternal(relic, silent: true);
        }
        if (request.OwnedLoadoutForTest is null)
            foreach (UnattendedRelicInjection injection in request.Relics)
                await InjectRelicAsync(player, injection);
        if (request.ClearRunDeck)
            ClearRunDeck(runState, player);
        await PrepareAscendersBaneAsync(runState, player, generated);
        foreach (UnattendedCardInjection injection in request.RunCards)
            await InjectRunCardAsync(runState, player, injection);
        if (request.OwnedLoadoutForTest is { } owned)
        {
            foreach (UnattendedRelicInjection injection in request.Relics)
                await InjectRelicAsync(player, injection);
            await CreatureCmd.SetMaxHp(player.Creature, owned.MaxHp);
            player.Creature.SetCurrentHpInternal(owned.Hp);
            player.Gold = owned.Gold;
        }
        PreparePotionSlots(player, request, generated);
        if (request.PreserveNativeCombatStateForTest)
        {
            foreach (UnattendedPotionInjection injection in request.Potions)
                InjectPotionForTest(player, injection.PotionId);
        }
        CaptureGeneratedLoadout(runState, player, request, generated);
        return new PreparedRun(runState, player, request, encounter, setup, [.. scopes]);
    }

    internal static async Task<CombatState> BuildAsync(ICombatHost waiter, OfflineRequest request,
        bool skipInitialInjection = false, TurnSetupCardSelector? turnSetupSelector = null,
        bool waitForTurnSetupPage = false)
    {
        using PreparedRun prepared = await PrepareRunAsync(waiter, request, turnSetupSelector);
        request = prepared.Request;
        ResolvedGeneratedCombatScenario? generated = prepared.Setup.Resolved;
        GeneratedScenarioCardSelector? generatedSelector = prepared.Setup.Selector;
        Player player = prepared.Player;
        EncounterModel mutableEncounter = prepared.Encounter.ToMutable();
        MapPointType targetMapPointType = request.TargetMapPointType != MapPointType.Unassigned
            ? request.TargetMapPointType
            : request.TargetRoomType switch
            {
                RoomType.Monster => MapPointType.Monster,
                RoomType.Elite => MapPointType.Elite,
                RoomType.Boss => MapPointType.Boss,
                _ => throw new InvalidOperationException($"只支持战斗房间，收到 {request.TargetRoomType}。"),
            };
        TestHostLog.Trace("scenario.enter_room");
        await RunManager.Instance.EnterRoomDebug(
            request.TargetRoomType, targetMapPointType, mutableEncounter);
        TestHostLog.Trace("scenario.wait_playable");

        CombatState combat = waitForTurnSetupPage
            ? await waiter.WaitForTurnSetupPageAsync()
            : await waiter.WaitForPlayableCombatAsync();
        TestHostLog.Trace("scenario.playable");
        Player combatPlayer = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("进入战斗后找不到本地玩家。");
        generatedSelector?.AssertConsumed();
        bool atSetupPage = combatPlayer.PlayerCombatState?.Phase == PlayerTurnPhase.Start;
        if (!atSetupPage && !request.PreserveNativeCombatStateForTest && !skipInitialInjection)
        {
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            await InjectInitialStateAsync(combat, combatPlayer, request);
        }
        return combat;
    }

    private static void ValidateSupported(OfflineRequest request)
    {
        if (request.OwnedLoadoutForTest is { } owned
            && (!request.PreserveNativeCombatStateForTest || !request.ClearRunDeck
                || request.GeneratedScenarioPath is not null || request.InitialPlayerHp is not null
                || request.InitialPlayerMaxHp is not null
                || owned.MaxHp <= 0 || owned.Hp <= 0 || owned.Hp > owned.MaxHp || owned.Gold < 0
                || request.Relics.Any(r => !r.AddWithoutObtainedEffects)))
            throw new InvalidDataException("已有牌组恢复需要原版开局、完整牌组、有效生命金币及无获取效果的遗物。");
        if (request.SchemaVersion != 1)
            throw new NotSupportedException($"不支持的请求 schemaVersion：{request.SchemaVersion}。");
        if (!string.IsNullOrWhiteSpace(request.CheckpointArchivePath))
            throw new NotSupportedException(
                "本仓库不支持检查点恢复；checkpointArchivePath 必须为空。");
        if (request.HeadlessFastModeForTest is { Length: > 0 } headless && headless != "Instant")
            throw new NotSupportedException($"不支持的 headlessFastModeForTest：{headless}。");
        if (request.TargetRoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
            throw new NotSupportedException($"不支持的 targetRoomType：{request.TargetRoomType}。");
        if (request.TargetMapPointType is not (MapPointType.Unassigned or MapPointType.Monster
            or MapPointType.Elite or MapPointType.Boss))
            throw new NotSupportedException($"不支持的 targetMapPointType：{request.TargetMapPointType}。");
    }

    private static void ClearRunDeck(RunState runState, Player player)
    {
        CardModel[] cards = player.Deck.Cards.ToArray();
        player.Deck.Clear(silent: true);
        foreach (CardModel card in cards)
            runState.RemoveCard(card);
        if (player.Deck.Cards.Count != 0)
            throw new InvalidOperationException("未能完整清空跑局牌组。");
    }

    private static async Task PrepareAscendersBaneAsync(
        RunState runState, Player player, ResolvedGeneratedCombatScenario? generated)
    {
        if (generated == null)
            return;
        CardModel[] bane = player.Deck.Cards.Where(card => card is AscendersBane).ToArray();
        if (generated.Options.IncludeAscendersBane)
        {
            if (bane.Length == 0)
                await InjectRunCardAsync(runState, player, new() { CardId = "ASCENDERS_BANE", Count = 1 });
            else if (bane.Length != 1)
                throw new InvalidDataException("原生初始牌组出现多张进阶之灾。");
        }
        else
        {
            foreach (CardModel card in bane)
            {
                player.Deck.RemoveInternal(card, silent: true);
                runState.RemoveCard(card);
            }
        }
    }

    private static void PreparePotionSlots(
        Player player, OfflineRequest request, ResolvedGeneratedCombatScenario? generated)
    {
        if (generated == null)
            return;
        int targetSlotCount = generated.Options.PotionSlotCount ?? player.PotionSlots.Count;
        if (request.Potions.Length > targetSlotCount)
            throw new InvalidDataException(
                $"生成场景请求{request.Potions.Length}瓶药水，但A{generated.Options.Ascension}实际只有{targetSlotCount}槽。");
        foreach (PotionModel? potion in player.PotionSlots.ToArray())
            potion?.Discard();
        if (generated.Options.PotionSlotCount is { } slots)
            player.SetMaxPotionCountInternal(slots);
    }

    private static void CaptureGeneratedLoadout(
        RunState state, Player player, OfflineRequest request, ResolvedGeneratedCombatScenario? generated)
    {
        if (generated == null)
            return;
        if (generated.Options.PlayerCurrentHp is { } hp)
        {
            if (hp > player.Creature.MaxHp)
                throw new InvalidDataException("指定生命超过获取遗物后的实际最大生命。");
            player.Creature.SetCurrentHpInternal(hp);
        }
        if (state.Players.Count != 1
            || player.Deck.Cards.Any(card => !GeneratedCombatScenario.IsSingleplayerCard(card))
            || player.Relics.Any(relic => !GeneratedCombatScenario.IsSingleplayerRelic(relic)))
            throw new InvalidDataException("生成场景必须为单人，且实际牌组与遗物不能包含多人专用内容。");
        int expectedBane = generated.Options.IncludeAscendersBane ? 1 : 0;
        if (player.Deck.Cards.Count(card => card is AscendersBane) != expectedBane
            || state.AscensionLevel != generated.Options.Ascension)
            throw new InvalidDataException("生成场景的进阶之灾数量或A等级不符合配置。");
        if (!generated.Options.ApplyRelicObtainEffects)
        {
            int expectedDeck = (generated.Options.IncludeStartingDeck ? player.Character.StartingDeck.Count() : 0)
                + expectedBane + request.RunCards.Sum(card => card.Count);
            int expectedRelics = (generated.Options.IncludeStartingRelics ? player.Character.StartingRelics.Count : 0)
                + request.Relics.Length;
            if (player.Deck.Cards.Count != expectedDeck || player.Relics.Count != expectedRelics)
                throw new InvalidDataException("生成场景的牌组或遗物数量不符合合成装备配置。");
        }
        string[] actualPotions = player.PotionSlots.OfType<PotionModel>().Select(potion => potion.Id.Entry).ToArray();
        if (!actualPotions.SequenceEqual(request.Potions.Select(potion => potion.PotionId)))
            throw new InvalidDataException("生成场景的药水槽顺序与配置不一致。");
    }

    private static async Task InjectInitialStateAsync(
        CombatState combatState, Player player, OfflineRequest request)
    {
        if (request.InitialEnemyMaxHps.Length > 0)
        {
            if (request.InitialEnemyMaxHps.Length != combatState.Enemies.Count)
                throw new InvalidOperationException(
                    $"逐敌最大生命数量 {request.InitialEnemyMaxHps.Length} 与敌人数 {combatState.Enemies.Count} 不同。");
            for (int enemyIndex = 0; enemyIndex < combatState.Enemies.Count; enemyIndex++)
            {
                int maxHp = request.InitialEnemyMaxHps[enemyIndex];
                if (maxHp <= 0)
                    throw new InvalidOperationException(
                        $"逐敌最大生命必须为正数：enemyIndex={enemyIndex}，maxHp={maxHp}。");
                await CreatureCmd.SetMaxHp(combatState.Enemies[enemyIndex], maxHp);
            }
        }
        if (request.InitialEnemyCurrentHps.Length > 0)
        {
            if (request.InitialEnemyCurrentHps.Length != combatState.Enemies.Count)
                throw new InvalidOperationException(
                    $"逐敌生命数量 {request.InitialEnemyCurrentHps.Length} 与敌人数 {combatState.Enemies.Count} 不同。");
            for (int enemyIndex = 0; enemyIndex < combatState.Enemies.Count; enemyIndex++)
            {
                Creature enemy = combatState.Enemies[enemyIndex];
                await CreatureCmd.SetCurrentHp(
                    enemy, Math.Clamp(request.InitialEnemyCurrentHps[enemyIndex], 0, enemy.MaxHp));
            }
        }
        else
        {
            foreach (Creature enemy in combatState.Enemies.Where(enemy => !enemy.IsDead))
                await CreatureCmd.SetCurrentHp(enemy, Math.Min(request.EnemyCurrentHp, enemy.MaxHp));
        }
        if (request.InitialEnemyBlocks.Length > 0)
        {
            if (request.InitialEnemyBlocks.Length != combatState.Enemies.Count)
                throw new InvalidOperationException(
                    $"逐敌格挡数量 {request.InitialEnemyBlocks.Length} 与敌人数 {combatState.Enemies.Count} 不同。");
            for (int enemyIndex = 0; enemyIndex < combatState.Enemies.Count; enemyIndex++)
                await SetBlockAsync(combatState.Enemies[enemyIndex], request.InitialEnemyBlocks[enemyIndex]);
        }
        ForceInitialEnemyMoves(combatState, request);
        ForceInitialEnemyStateLogs(combatState, request);
        ApplyInitialEnemyIntegerMembers(combatState, request);
        if (request.InitialPlayerMaxHp is { } initialPlayerMaxHp)
            await CreatureCmd.SetMaxHp(player.Creature, initialPlayerMaxHp);
        if (request.InitialPlayerHp is { } initialPlayerHp)
        {
            await CreatureCmd.SetCurrentHp(
                player.Creature, Math.Clamp(initialPlayerHp, 1, player.Creature.MaxHp));
        }
        if (request.InitialPlayerBlock is { } initialPlayerBlock)
            await SetBlockAsync(player.Creature, initialPlayerBlock);
        if (request.InitialPlayerEnergy is { } initialPlayerEnergy)
            SetEnergy(player, initialPlayerEnergy);
        if (request.InitialPlayerStars is { } initialPlayerStars)
            SetStars(player, initialPlayerStars);
        if (request.InitialRoundNumber is { } initialRoundNumber)
            combatState.RoundNumber = initialRoundNumber;
        if (request.InitialPlayerTurnNumber is { } initialPlayerTurnNumber)
        {
            PlayerCombatState playerState = player.PlayerCombatState
                ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
            if (initialPlayerTurnNumber < playerState.TurnNumber)
                throw new InvalidOperationException(
                    $"玩家测试回合号不能从 {playerState.TurnNumber} 回退到 {initialPlayerTurnNumber}。");
            while (playerState.TurnNumber < initialPlayerTurnNumber)
                playerState.IncrementTurnNumber();
        }

        if (request.ClearPlayerPiles)
            await ClearPlayerPilesAsync(player);
        else if (request.ClearPlayerHand)
        {
            await CardCmd.Discard(
                new BlockingPlayerChoiceContext(),
                player.PlayerCombatState!.Hand.Cards.ToArray());
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedCardInjection injection in request.Cards)
            await InjectCardAsync(combatState, player, injection);
        foreach (UnattendedOrbInjection injection in request.Orbs)
            await InjectOrbAsync(player, injection);
        foreach (UnattendedPotionInjection injection in request.Potions)
            InjectPotionForTest(player, injection.PotionId);
        foreach (UnattendedRelicInjection injection in request.CombatRelics)
            await InjectRelicAsync(player, injection);
        if (request.ClearAllPowers)
        {
            foreach (PowerModel power in combatState.Creatures
                         .SelectMany(creature => creature.Powers)
                         .ToArray())
                await PowerCmd.Remove(power);
        }
        foreach (UnattendedPowerInjection injection in request.Powers)
            await InjectPowerAsync(combatState, player, injection);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static async Task ClearPlayerPilesAsync(Player player)
    {
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        CardModel[] cards =
        [
            .. playerState.Hand.Cards,
            .. playerState.DrawPile.Cards,
            .. playerState.DiscardPile.Cards,
            .. playerState.ExhaustPile.Cards,
        ];
        await CardPileCmd.RemoveFromCombat(cards, skipVisuals: true);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static void ForceInitialEnemyMoves(CombatState combatState, OfflineRequest request)
    {
        if (request.InitialEnemyMoveIds.Length > combatState.Enemies.Count)
            throw new InvalidOperationException(
                $"配置了 {request.InitialEnemyMoveIds.Length} 个初始行动，但战斗只有 {combatState.Enemies.Count} 个敌人。");
        for (int index = 0; index < request.InitialEnemyMoveIds.Length; index++)
        {
            string moveId = request.InitialEnemyMoveIds[index];
            if (string.IsNullOrWhiteSpace(moveId))
                continue;
            MonsterModel monster = combatState.Enemies[index].Monster
                ?? throw new InvalidOperationException($"敌人 {index} 没有 MonsterModel。");
            MonsterMoveStateMachine machine = monster.MoveStateMachine
                ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
            if (!machine.States.TryGetValue(moveId, out MonsterState? state) || state is not MoveState move)
                throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动 {moveId}。");
            monster.SetMoveImmediate(move, true);
        }
    }

    private static void ForceInitialEnemyStateLogs(CombatState combatState, OfflineRequest request)
    {
        if (request.InitialEnemyStateLogs.Length == 0)
            return;
        if (request.InitialEnemyStateLogs.Length != combatState.Enemies.Count)
            throw new InvalidOperationException(
                $"怪物行动历史数量 {request.InitialEnemyStateLogs.Length} 与敌人数 {combatState.Enemies.Count} 不同。");
        for (int enemyIndex = 0; enemyIndex < combatState.Enemies.Count; enemyIndex++)
        {
            MonsterModel monster = combatState.Enemies[enemyIndex].Monster
                ?? throw new InvalidOperationException($"敌人 {enemyIndex} 没有 MonsterModel。");
            MonsterMoveStateMachine machine = monster.MoveStateMachine
                ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
            machine.StateLog.Clear();
            foreach (string stateId in request.InitialEnemyStateLogs[enemyIndex])
            {
                if (!machine.States.TryGetValue(stateId, out MonsterState? state))
                    throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有历史状态 {stateId}。");
                machine.StateLog.Add(state);
            }
        }
    }

    private static void ApplyInitialEnemyIntegerMembers(CombatState combatState, OfflineRequest request)
    {
        if (request.InitialEnemyIntegerMembers.Length == 0)
            return;
        if (request.InitialEnemyIntegerMembers.Length != combatState.Enemies.Count)
            throw new InvalidOperationException(
                $"怪物整数成员表数量 {request.InitialEnemyIntegerMembers.Length} " +
                $"与敌人数 {combatState.Enemies.Count} 不同。");
        for (int enemyIndex = 0; enemyIndex < combatState.Enemies.Count; enemyIndex++)
        {
            MonsterModel monster = combatState.Enemies[enemyIndex].Monster
                ?? throw new InvalidOperationException($"敌人 {enemyIndex} 没有 MonsterModel。");
            foreach ((string memberName, int value) in request.InitialEnemyIntegerMembers[enemyIndex])
                SetObjectStateMember(monster, monster.Id.Entry, memberName, value);
        }
    }

    private static async Task SetBlockAsync(Creature creature, int targetBlock)
    {
        if (targetBlock < 0)
            throw new InvalidOperationException($"测试格挡不能为负数：{targetBlock}。");
        if (creature.Block > targetBlock)
        {
            await CreatureCmd.LoseBlock(
                new BlockingPlayerChoiceContext(), creature, creature.Block - targetBlock, null);
        }
        else if (creature.Block < targetBlock)
        {
            await CreatureCmd.GainBlock(
                creature, targetBlock - creature.Block, ValueProp.Unpowered, null, fast: true);
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static void SetEnergy(Player player, int targetEnergy)
    {
        if (targetEnergy < 0)
            throw new InvalidOperationException($"测试能量不能为负数：{targetEnergy}。");
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        if (state.Energy > targetEnergy)
            state.LoseEnergy(state.Energy - targetEnergy);
        else if (state.Energy < targetEnergy)
            state.GainEnergy(targetEnergy - state.Energy);
    }

    private static void SetStars(Player player, int targetStars)
    {
        if (targetStars < 0)
            throw new InvalidOperationException($"测试星能不能为负数：{targetStars}。");
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        if (state.Stars > targetStars)
            state.LoseStars(state.Stars - targetStars);
        else if (state.Stars < targetStars)
            state.GainStars(targetStars - state.Stars);
    }

    private static async Task<IReadOnlyList<CardModel>> InjectCardAsync(
        CombatState combatState, Player player, UnattendedCardInjection injection)
    {
        if (!Enum.TryParse(injection.Pile, true, out PileType pileType)
            || pileType is not (PileType.Hand or PileType.Draw or PileType.Discard or PileType.Exhaust))
            throw new InvalidOperationException($"不支持注入牌堆 {injection.Pile}。");
        if (injection.Count is < 1 or > MaxBulkCardInjectionCount)
            throw new InvalidOperationException(
                $"注入卡牌数量 {injection.Count} 超出 1-{MaxBulkCardInjectionCount}。");
        if (injection.UpgradeLevel is < 0 or > 1)
            throw new NotSupportedException($"不支持的卡牌 upgradeLevel：{injection.UpgradeLevel}。");

        CardModel canonical = NativeLookup.ResolveUnique(ModelDb.AllCards, injection.CardId, "卡牌");
        List<CardModel> cards = [];
        for (int index = 0; index < injection.Count; index++)
        {
            CardModel card = combatState.CreateCard(canonical, player);
            if (injection.TreatAsDeckCard)
            {
                CardModel deckVersion = combatState.RunState.CreateCard(canonical, player);
                CardPileAddResult deckResult = await CardPileCmd.Add(deckVersion, PileType.Deck);
                if (!deckResult.success)
                    throw new InvalidOperationException($"游戏拒绝为测试卡牌 {canonical.Id} 建立跑局版本。");
                card.DeckVersion = deckVersion;
            }
            for (int level = 0; level < injection.UpgradeLevels && card.IsUpgradable; level++)
                CardCmd.Upgrade(card, CardPreviewStyle.None);
            foreach ((string key, int value) in injection.DynamicVars)
            {
                if (!card.DynamicVars.TryGetValue(key, out DynamicVar? dynamicVar))
                    throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在动态变量 {key}。");
                dynamicVar.BaseValue = value;
            }
            ApplyCardEnumMembers(card, injection.EnumMembers);
            if (!string.IsNullOrWhiteSpace(injection.EnchantmentId))
            {
                EnchantmentModel enchantment = NativeLookup.ResolveUnique(
                    ModelDb.DebugEnchantments, injection.EnchantmentId, "附魔").ToMutable();
                if (!enchantment.CanEnchant(card))
                    throw new InvalidOperationException(
                        $"附魔 {enchantment.Id} 不能用于测试卡牌 {card.Id}。");
                CardCmd.Enchant(enchantment, card, injection.EnchantmentAmount);
            }
            if (!string.IsNullOrWhiteSpace(injection.AfflictionId))
            {
                AfflictionModel affliction = NativeLookup.ResolveUnique(
                    ModelDb.DebugAfflictions, injection.AfflictionId, "苦难").ToMutable();
                await CardCmd.Afflict(affliction, card, injection.AfflictionAmount);
            }
            CardPileAddResult result = await CardPileCmd.AddGeneratedCardToCombat(card, pileType, player);
            if (!result.success)
                throw new InvalidOperationException($"游戏拒绝把 {canonical.Id} 注入 {pileType}。");
            cards.Add(card);
        }
        return cards;
    }

    private static async Task InjectRunCardAsync(
        RunState runState, Player player, UnattendedCardInjection injection)
    {
        if (injection.Count is < 1 or > MaxBulkCardInjectionCount)
            throw new InvalidOperationException(
                $"注入跑局卡牌数量 {injection.Count} 超出 1-{MaxBulkCardInjectionCount}。");
        if (injection.UpgradeLevel is < 0 or > 1)
            throw new NotSupportedException($"不支持的卡牌 upgradeLevel：{injection.UpgradeLevel}。");
        CardModel canonical = NativeLookup.ResolveUnique(ModelDb.AllCards, injection.CardId, "卡牌");
        for (int index = 0; index < injection.Count; index++)
        {
            CardModel card = runState.CreateCard(canonical, player);
            for (int level = 0; level < injection.UpgradeLevels && card.IsUpgradable; level++)
                CardCmd.Upgrade(card, CardPreviewStyle.None);
            foreach ((string key, int value) in injection.DynamicVars)
            {
                if (!card.DynamicVars.TryGetValue(key, out DynamicVar? dynamicVar))
                    throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在动态变量 {key}。");
                dynamicVar.BaseValue = value;
            }
            ApplyCardEnumMembers(card, injection.EnumMembers);
            if (!string.IsNullOrWhiteSpace(injection.EnchantmentId))
            {
                EnchantmentModel enchantment = NativeLookup.ResolveUnique(
                    ModelDb.DebugEnchantments, injection.EnchantmentId, "附魔").ToMutable();
                if (!enchantment.CanEnchant(card))
                    throw new InvalidOperationException($"附魔 {enchantment.Id} 不能用于测试卡牌 {card.Id}。");
                CardCmd.Enchant(enchantment, card, injection.EnchantmentAmount);
            }
            if (!string.IsNullOrWhiteSpace(injection.AfflictionId))
            {
                AfflictionModel affliction = NativeLookup.ResolveUnique(
                    ModelDb.DebugAfflictions, injection.AfflictionId, "苦难").ToMutable();
                await CardCmd.Afflict(affliction, card, injection.AfflictionAmount);
            }
            CardPileAddResult result = await CardPileCmd.Add(card, PileType.Deck);
            if (!result.success)
                throw new InvalidOperationException($"游戏拒绝把 {canonical.Id} 注入跑局牌组。");
        }
    }

    private static void ApplyCardEnumMembers(CardModel card, IReadOnlyDictionary<string, string> members)
    {
        foreach ((string memberName, string serializedValue) in members)
        {
            bool assigned = false;
            for (Type? type = card.GetType(); type != null && !assigned; type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly;
                if (type.GetProperty(memberName, flags) is { } property)
                {
                    if (!property.PropertyType.IsEnum || property.SetMethod == null)
                        throw new InvalidOperationException($"卡牌 {card.Id.Entry} 成员 {memberName} 不是可写枚举属性。");
                    property.SetValue(card, Enum.Parse(property.PropertyType, serializedValue, ignoreCase: true));
                    assigned = true;
                }
                else if (type.GetField(memberName, flags) is { } field)
                {
                    if (!field.FieldType.IsEnum || field.IsInitOnly)
                        throw new InvalidOperationException($"卡牌 {card.Id.Entry} 成员 {memberName} 不是可写枚举字段。");
                    field.SetValue(card, Enum.Parse(field.FieldType, serializedValue, ignoreCase: true));
                    assigned = true;
                }
            }
            if (!assigned)
                throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在枚举成员 {memberName}。");
        }
    }

    private static async Task InjectOrbAsync(Player player, UnattendedOrbInjection injection)
    {
        if (injection.Count is < 1 or > 20)
            throw new InvalidOperationException($"注入充能球数量 {injection.Count} 超出 1-20。");
        OrbModel canonical = NativeLookup.ResolveUnique(ModelDb.Orbs, injection.OrbId, "充能球");
        var choiceContext = new BlockingPlayerChoiceContext();
        for (int index = 0; index < injection.Count; index++)
        {
            OrbModel orb = canonical.ToMutable();
            foreach ((string memberName, decimal value) in injection.DecimalMembers)
                SetObjectStateMember(orb, orb.Id.Entry, memberName, value);
            await OrbCmd.Channel(choiceContext, orb, player);
        }
    }

    private static async Task InjectRelicAsync(Player player, UnattendedRelicInjection injection)
    {
        RelicModel canonical = NativeLookup.ResolveUnique(ModelDb.AllRelics, injection.RelicId, "遗物");
        RelicModel relic = canonical.ToMutable();
        if (injection.AddWithoutObtainedEffects)
            player.AddRelicInternal(relic);
        else
            relic = await RelicCmd.Obtain(relic, player);
        foreach ((string memberName, int value) in injection.IntegerMembers)
            SetRelicStateMember(relic, memberName, value);
        foreach ((string memberName, bool value) in injection.BooleanMembers)
            SetRelicStateMember(relic, memberName, value);
    }

    private static void SetRelicStateMember<T>(RelicModel relic, string memberName, T value)
    {
        for (Type? type = relic.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly;
            if (type.GetProperty(memberName, flags) is { } property)
            {
                if (property.PropertyType != typeof(T) || property.SetMethod == null)
                    throw new InvalidOperationException(
                        $"遗物 {relic.Id.Entry} 状态属性 {memberName} 不是可写的 {typeof(T).Name}。");
                property.SetValue(relic, value);
                return;
            }
            if (type.GetField(memberName, flags) is { } field)
            {
                if (field.FieldType != typeof(T) || field.IsInitOnly)
                    throw new InvalidOperationException(
                        $"遗物 {relic.Id.Entry} 状态字段 {memberName} 不是可写的 {typeof(T).Name}。");
                field.SetValue(relic, value);
                return;
            }
        }
        throw new InvalidOperationException($"遗物 {relic.Id.Entry} 不存在状态成员 {memberName}。");
    }

    private static async Task InjectPowerAsync(
        CombatState combatState, Player player, UnattendedPowerInjection injection)
    {
        if (injection.Amount is < 1 or > 999_999_999)
            throw new InvalidOperationException($"注入 Power 数值 {injection.Amount} 超出 1-999999999。");

        Creature owner = ResolvePowerInjectionCreature(
            combatState, player, injection.Target, injection.TargetIndex, "所有者");
        Creature powerTarget = string.IsNullOrWhiteSpace(injection.PowerTarget)
            ? owner
            : ResolvePowerInjectionCreature(
                combatState, player, injection.PowerTarget, injection.PowerTargetIndex, "效果目标");
        Creature applier = string.IsNullOrWhiteSpace(injection.Applier)
            ? player.Creature
            : ResolvePowerInjectionCreature(
                combatState, player, injection.Applier, injection.ApplierIndex, "施加者");
        PowerModel canonical = NativeLookup.ResolveUnique(ModelDb.AllPowers, injection.PowerId, "Power");
        PowerModel power = canonical.ToMutable();
        foreach ((string key, int value) in injection.DynamicVars)
        {
            if (!power.DynamicVars.TryGetValue(key, out DynamicVar? dynamicVar))
                throw new InvalidOperationException($"Power {power.Id.Entry} 不存在动态变量 {key}。");
            dynamicVar.BaseValue = value;
        }
        power.Target = powerTarget;
        await PowerCmd.Apply(
            new BlockingPlayerChoiceContext(), power, owner, injection.Amount, applier, null);
        if (injection.InternalIntegerMembers.Count > 0)
        {
            PowerModel appliedPower = owner.Powers.Last(candidate => candidate.Id == canonical.Id);
            object internalData = appliedPower switch
            {
                AutomationPower automation => automation.GetInternalData<AutomationPower.Data>(),
                HellraiserPower hellraiser => hellraiser.GetInternalData<HellraiserPower.Data>(),
                VoidFormPower voidForm => voidForm.GetInternalData<VoidFormPower.Data>(),
                _ => throw new InvalidOperationException(
                    $"测试尚不支持设置 Power {appliedPower.Id.Entry} 的内部整数状态。"),
            };
            foreach ((string memberName, int value) in injection.InternalIntegerMembers)
                SetObjectStateMember(internalData, appliedPower.Id.Entry, memberName, value);
        }
    }

    private static Creature ResolvePowerInjectionCreature(
        CombatState combatState, Player player, string selector, int index, string role)
        => selector switch
        {
            "Player" => player.Creature,
            "Osty" when player.Osty != null => player.Osty,
            "Enemy" when index >= 0 && index < combatState.Enemies.Count => combatState.Enemies[index],
            _ => throw new InvalidOperationException($"无效的 Power 注入{role} {selector}[{index}]。"),
        };

    private static void SetObjectStateMember<T>(object target, string ownerId, string memberName, T value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();
        if (type.GetField(memberName, flags) is { } field)
        {
            if (field.FieldType != typeof(T) || field.IsInitOnly)
                throw new InvalidOperationException($"{ownerId} 内部字段 {memberName} 不是可写的 {typeof(T).Name}。");
            field.SetValue(target, value);
            return;
        }
        if (type.GetProperty(memberName, flags) is { } property)
        {
            if (property.PropertyType != typeof(T) || property.SetMethod == null)
                throw new InvalidOperationException($"{ownerId} 内部属性 {memberName} 不是可写的 {typeof(T).Name}。");
            property.SetValue(target, value);
            return;
        }
        throw new InvalidOperationException($"{ownerId} 不存在内部状态成员 {memberName}。");
    }

    private static void InjectPotionForTest(Player player, string potionId)
    {
        PotionModel canonical = NativeLookup.ResolveUnique(ModelDb.AllPotions, potionId, "药水");
        PotionModel potion = canonical.ToMutable();
        int slot = Enumerable.Range(0, player.PotionSlots.Count)
            .FirstOrDefault(index => player.GetPotionAtSlotIndex(index) == null, -1);
        if (slot < 0)
        {
            player.GetPotionAtSlotIndex(0)!.Discard();
            slot = 0;
        }
        if (!player.AddPotionInternal(potion, slot, silent: false).success)
            throw new InvalidOperationException($"无法把测试药水 {potionId} 注入槽位 {slot}。");
    }
}
