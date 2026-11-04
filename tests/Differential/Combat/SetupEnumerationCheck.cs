// Offline proof for the turn-setup root enumerator: import the native Start root at the
// SetupPlayerTurn entry, enumerate every choice plan with the Sts2Solver kernel, rebuild the same
// native page once per plan and compare the resulting Play roots byte for byte. Also compiles the
// setup search request and compares its data sections with the frozen config.br.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.TestHost;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal static class SetupEnumerationCheck
{
    private const long SearchMemoryBudgetBytes = 2_576_980_377;
    private static SearchCase? s_setupCase;

    internal static int Run(string data, string corpora, string root, int turn)
    {
        OfflineRequest request = Program.LoadRequest(data, corpora, root);
        if (turn is not (1 or 2))
            throw new NotSupportedException("--setup-turn 目前只支持 1 或 2。");
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        try
        {
            return turn == 1
                ? RunTurnOne(loop, data, root, request)
                : RunTurnTwo(loop, data, root, request);
        }
        finally
        {
            RunManager.Instance.CleanUp(true);
        }
    }

    private static int RunTurnOne(MainLoopContext loop, string data, string root,
        OfflineRequest request)
    {
        bool frozen = request.FlashTurnSetupRootForTest;
        if (!frozen)
        {
            // The Start snapshot is the native state before the test host's initial injection.
            // Keep it through the first playable boundary and suppress the FLASH-PR- fallback
            // selector so a rebuilt Play root is the setup outcome.
            request = request with
            {
                PreserveNativeCombatStateForTest = true,
                ScenarioId = request.ScenarioId.StartsWith("FLASH-PR-", StringComparison.Ordinal)
                    ? "SETUP-" + request.ScenarioId
                    : request.ScenarioId,
            };
        }
        TurnSetupCardSelector firstSelector = new(0);
        TurnSetupRootCapture.BeginBuild(captureStartBattle: true);
        Task<CombatState> firstBuild = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
            skipInitialInjection: frozen, firstSelector);
        loop.RunUntilCompleted(firstBuild, TimeSpan.FromSeconds(600), "建局 setup-start");
        CombatState firstCombat = firstBuild.GetAwaiter().GetResult();
        Player player = LocalContext.GetMe(firstCombat)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        if (firstCombat.RunState is not RunState runState)
            throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
        Battle frozenStart = TurnSetupRootCapture.StartBattle
            ?? throw new InvalidDataException("SetupPlayerTurn 入口没有导入冻结口径 Start Battle。");
        Battle productionStart = TurnSetupRootCapture.ProductionStartBattle
            ?? throw new InvalidDataException("SetupPlayerTurn 入口没有导入生产口径 Start Battle。");
        Rules startRules = TurnSetupRootCapture.StartRules
            ?? throw new InvalidDataException("SetupPlayerTurn 入口没有编译 Start Rules。");
        bool extraTurn = TurnSetupRootCapture.StartTakingExtraTurn;
        int startTurn = TurnSetupRootCapture.StartTurn;
        HashSet<CardModel> startCards = TurnSetupRootCapture.StartCards
            ?? throw new InvalidDataException("SetupPlayerTurn 入口没有捕获 Start 卡牌集合。");
        Rules rules = RulesCompiler.Compile(firstCombat, player, runState);
        bool rulesSame = RulesCodec.Encode(startRules).AsSpan()
            .SequenceEqual(RulesCodec.Encode(rules));
        Battle firstPlay = BattleImporter.Import(firstCombat, player, rules,
            ImportOptions.Default with { TurnSetupRoot = true, RootCards = startCards });
        ReportStartImport(root, frozenStart, productionStart);
        TurnSetupEnumeration enumeration = EnumerateAndCompile(root, turn: 1, startTurn,
            productionStart, rules, rulesSame, extraTurn, firstCombat, player, firstPlay);
        int failures = ReportEnumeration(enumeration, root, turn: 1);
        if (frozen)
        {
            CompareWithFrozenConfig(data, root, firstCombat, player, rules, frozenStart,
                SearchPolicyCapture.Capture(firstCombat, player), enumeration.Branches, firstPlay);
            if (s_setupCase is { } production)
                CompareProductionToFrozen(data, root, production);
            CompareGolden(data, root, enumeration.Branches);
            RunShortSearch(root, enumeration, data);
        }
        RunManager.Instance.CleanUp(true);
        failures += CompareNativePlans(root, enumeration.Branches,
            (branch, pages) => BuildTurnOnePlan(loop, request, rules, branch, pages));
        return failures == 0 ? 0 : 1;
    }

    private static int RunTurnTwo(MainLoopContext loop, string data, string root,
        OfflineRequest request)
    {
        _ = data;
        CombatState combat = BuildToPlay(loop, request, turnOneSelector: 0);
        _ = combat;
        TurnSetupRootCapture.BeginBuild(captureStartBattle: true);
        using (CardSelectCmd.PushSelector(new TurnSetupCardSelector(0), localOnly: true))
            EndTurnAndWait(loop, expectedTurn: 2);
        CombatState secondCombat = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidDataException("第二回合找不到战斗状态。");
        Player secondPlayer = LocalContext.GetMe(secondCombat)
            ?? throw new InvalidOperationException("第二回合找不到本地玩家。");
        if (secondCombat.RunState is not RunState runState)
            throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
        Battle frozenStart = TurnSetupRootCapture.StartBattle
            ?? throw new InvalidDataException("第二回合 SetupPlayerTurn 入口没有导入冻结口径 Start Battle。");
        Battle productionStart = TurnSetupRootCapture.ProductionStartBattle
            ?? throw new InvalidDataException("第二回合 SetupPlayerTurn 入口没有导入生产口径 Start Battle。");
        Rules startRules = TurnSetupRootCapture.StartRules
            ?? throw new InvalidDataException("第二回合 SetupPlayerTurn 入口没有编译 Start Rules。");
        bool extraTurn = TurnSetupRootCapture.StartTakingExtraTurn;
        int startTurn = TurnSetupRootCapture.StartTurn;
        HashSet<CardModel> startCards = TurnSetupRootCapture.StartCards
            ?? throw new InvalidDataException("第二回合 SetupPlayerTurn 入口没有捕获 Start 卡牌集合。");
        Rules rules = RulesCompiler.Compile(secondCombat, secondPlayer, runState);
        bool rulesSame = RulesCodec.Encode(startRules).AsSpan()
            .SequenceEqual(RulesCodec.Encode(rules));
        Battle firstPlay = BattleImporter.Import(secondCombat, secondPlayer, rules,
            ImportOptions.Default with { TurnSetupRoot = true, RootCards = startCards });
        ReportStartImport(root, frozenStart, productionStart);
        TurnSetupEnumeration enumeration = EnumerateAndCompile(root, turn: 2, startTurn,
            productionStart, rules, rulesSame, extraTurn, secondCombat, secondPlayer, firstPlay);
        int failures = ReportEnumeration(enumeration, root, turn: 2);
        RunManager.Instance.CleanUp(true);
        failures += CompareNativePlans(root, enumeration.Branches,
            (branch, pages) => BuildTurnTwoPlan(loop, request, rules, branch, pages));
        return failures == 0 ? 0 : 1;
    }

    private static void ReportStartImport(string root, Battle frozen, Battle production)
    {
        if (Fingerprint.Equal(frozen, production))
        {
            Console.WriteLine($"SETUP_START root={root} frozen=production");
            return;
        }
        List<string> diff = BattleDump.Diff(frozen, production, "frozen", "production");
        Console.WriteLine($"SETUP_START root={root} frozen!=production field={FirstField(diff)}");
        foreach (string line in diff.Take(16))
            Console.WriteLine("  " + line);
    }

    private static TurnSetupEnumeration EnumerateAndCompile(string root, int turn, int startTurn,
        Battle start, Rules rules, bool rulesSame, bool extraTurn, CombatState state, Player player,
        Battle firstPlay)
    {
        TurnSetupEnumeration enumeration = TurnSetupRootEnumerator.Enumerate(start, rules, extraTurn);
        List<TurnSetupBranch> branches = [.. enumeration.Branches];
        Console.WriteLine(
            $"SETUP_ENUM root={root} turn={turn} nativeTurn={startTurn} extraTurn={extraTurn} "
            + $"status={enumeration.Status} "
            + $"branches={branches.Count} play={branches.Count(static b => !b.Terminal)} "
            + $"terminal={branches.Count(static b => b.Terminal)} dropped={enumeration.DroppedByBudget} "
            + $"rules={(rulesSame ? "same" : "diff")} startTurn={start.PlayerTurn} "
            + $"startPhase={start.Phase} startHand={start.HandCount} startDraw={start.DrawCount}");
        if (enumeration.Reason is { } reason)
            Console.WriteLine($"  unsupported={reason}");
        if (enumeration.Status != TurnSetupEnumerationStatus.Unsupported)
        {
            TurnSetupEnumeration budget = TurnSetupRootEnumerator.Enumerate(start, rules, extraTurn,
                maxRoots: 2);
            Console.WriteLine($"SETUP_BUDGET root={root} turn={turn} cap=2 roots={budget.Branches.Length} "
                + $"dropped={budget.DroppedByBudget} status={budget.Status}");
        }
        for (int i = 0; i < branches.Count; i++)
        {
            Console.WriteLine($"ENUM branch={i} choices={ChoiceLabels(branches[i])} "
                + $"allOptions=[{AllOptionLabels(branches[i])}] energy={branches[i].Root.Energy} "
                + $"hand={branches[i].Root.HandCount} draw={branches[i].Root.DrawCount} "
                + $"discard={branches[i].Root.DiscardCount} exhaust={branches[i].Root.ExhaustCount} "
                + $"hp={branches[i].Root.Player.CurrentHp}");
        }
        if (enumeration.Status == TurnSetupEnumerationStatus.Unsupported)
            return enumeration;
        if (branches.Any(static branch => branch.Terminal))
        {
            Console.WriteLine($"SETUP_REQUEST root={root} turn={turn} status=skip reason=terminal-branch");
            return enumeration;
        }
        SearchPolicy policy = SearchPolicyCapture.Capture(state, player);
        EvaluationRootFacts facts = EvaluationRootFacts.Default with
        {
            InitialActionCount = 0,
            TurnSetupRoot = true,
        };
        SearchCase setupCase = SearchRequestCompiler.CompileSetupRequest(
            state, player, rules, start, branches, policy, facts,
            SearchPerformanceProfile.For(SearchPerformanceTier.Medium), deterministic: true,
            SearchMemoryBudgetBytes);
        if (setupCase.TurnSetupPlans is not { } plans || plans.Count != setupCase.Battles.Length)
            throw new InvalidDataException("编译出的 setup 请求没有对齐的 TurnSetupPlans。");
        s_setupCase = setupCase;
        Console.WriteLine(
            $"SETUP_REQUEST root={root} turn={turn} battles={setupCase.Battles.Length} "
            + $"plans={plans.Count} initialTurn={setupCase.Options.InitialPlayerTurn} "
            + $"actionCount={setupCase.Options.InitialActionCount} "
            + $"bound={(setupCase.Options.Bound != null)} firstPlayEnergy={firstPlay.Energy}");
        return enumeration;
    }

    private static int ReportEnumeration(TurnSetupEnumeration enumeration, string root, int turn)
    {
        if (enumeration.Status == TurnSetupEnumerationStatus.Unsupported)
        {
            Console.WriteLine($"SETUP_ENUM root={root} turn={turn} unsupported={enumeration.Reason}");
            return 1;
        }
        // Terminal-only enumerations are a legitimate setup outcome (the combat ended before the
        // play phase); only an unsupported plan is a failure.
        return 0;
    }

    private static (Battle Root, IReadOnlyList<NativeSetupPage> Pages) BuildTurnOnePlan(
        MainLoopContext loop, OfflineRequest request, Rules rules, TurnSetupBranch branch,
        IReadOnlyList<int[]> pages)
    {
        RunManager.Instance.CleanUp(true);
        TurnSetupRootCapture.BeginBuild(captureStartBattle: true);
        TurnSetupCardSelector selector = new(pages);
        Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
            skipInitialInjection: request.FlashTurnSetupRootForTest, selector);
        loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), "建局 plan");
        CombatState combat = build.GetAwaiter().GetResult();
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        LiveCombatTracker tracker = TurnSetupRootCapture.StartTracker
            ?? throw new InvalidDataException("plan 没有捕获 Start tracker。");
        return (tracker.ImportForComparison(combat, player, rules, branch.Root), selector.Pages);
    }

    private static (Battle Root, IReadOnlyList<NativeSetupPage> Pages) BuildTurnTwoPlan(
        MainLoopContext loop, OfflineRequest request, Rules rules, TurnSetupBranch branch,
        IReadOnlyList<int[]> pages)
    {
        RunManager.Instance.CleanUp(true);
        BuildToPlay(loop, request, turnOneSelector: 0);
        TurnSetupRootCapture.BeginBuild(captureStartBattle: true);
        TurnSetupCardSelector selector = new(pages);
        using (CardSelectCmd.PushSelector(selector, localOnly: true))
            EndTurnAndWait(loop, expectedTurn: 2);
        CombatState secondCombat = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidDataException("第二回合找不到战斗状态。");
        Player player = LocalContext.GetMe(secondCombat)
            ?? throw new InvalidOperationException("第二回合找不到本地玩家。");
        LiveCombatTracker tracker = TurnSetupRootCapture.StartTracker
            ?? throw new InvalidDataException("第二回合 plan 没有捕获 Start tracker。");
        return (tracker.ImportForComparison(secondCombat, player, rules, branch.Root), selector.Pages);
    }

    private static CombatState BuildToPlay(MainLoopContext loop, OfflineRequest request,
        int turnOneSelector)
    {
        TurnSetupRootCapture.BeginBuild();
        using (CardSelectCmd.PushSelector(new TurnSetupCardSelector(turnOneSelector), localOnly: true))
        {
            Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
                skipInitialInjection: false);
            loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), "建局 turn-1");
            return build.GetAwaiter().GetResult();
        }
    }

    private static void EndTurnAndWait(MainLoopContext loop, int expectedTurn)
    {
        CombatState state = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidDataException("结束回合找不到战斗状态。");
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("结束回合找不到本地玩家。");
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidDataException("结束回合没有玩家回合。");
        TaskCompletionSource<GameAction> captured = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBeforeActionExecuted(GameAction action)
        {
            if (action is EndPlayerTurnAction endTurn && endTurn.OwnerId == player.NetId)
                captured.TrySetResult(action);
        }
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        executor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new EndPlayerTurnAction(player, turn));
            loop.RunUntilCompleted(captured.Task, TimeSpan.FromSeconds(30), "结束回合");
            loop.RunUntilCompleted(captured.Task.GetAwaiter().GetResult().CompletionTask,
                TimeSpan.FromSeconds(120), "结束回合完成");
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
        for (int frame = 0; frame < 6000; frame++)
        {
            CombatState? live = CombatManager.Instance.DebugOnlyGetState();
            Player? livePlayer = live == null ? null : LocalContext.GetMe(live);
            if (live != null && CombatManager.Instance.IsInProgress
                && live.CurrentSide == CombatSide.Player
                && livePlayer?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && livePlayer.PlayerCombatState.TurnNumber == expectedTurn)
            {
                return;
            }
            loop.Pump(TimeSpan.FromMilliseconds(20));
        }
        throw new TimeoutException($"没有等到第 {expectedTurn} 回合的玩家出牌阶段。");
    }

    private static void CompareWithFrozenConfig(string data, string root, CombatState state,
        Player player, Rules rules, Battle start, SearchPolicy policy,
        IReadOnlyList<TurnSetupBranch> branches, Battle firstPlay)
    {
        byte[] expectedBytes;
        SearchCase expected;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            expectedBytes = ConfigCompare.ReadBrotli(Path.Combine(directory, "config.br"));
            expected = SearchCodec.DecodeRequest(expectedBytes);
        }
        catch (InvalidDataException error)
        {
            Console.WriteLine($"SETUP_CONFIG root={root} status=reject reason=invalid-golden detail={error.Message}");
            return;
        }
        Battle[] ordered = OrderLikeExpected(expected.Battles, branches);
        if (ordered.Length != expected.Battles.Length)
        {
            Console.WriteLine(
                $"SETUP_CONFIG root={root} status=diff field=roots expected={expected.Battles.Length} "
                + $"matched={ordered.Length}");
            return;
        }
        EvaluationRootFacts facts = EvaluationRootFacts.Default with { TurnSetupRoot = true };
        Scorer scorer = EvaluationConfigCompiler.CompileScorer(state, player, rules, start, policy, facts);
        BoundFeatures bound = EvaluationConfigCompiler.CompileBound(state, player, start, policy, facts);
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero, firstPlay.Player.MaxHp)
        {
            Scorer = scorer,
            Bound = bound,
            InitialActionCount = 0,
        };
        SearchCase capture = new(rules, ordered, options);
        byte[] actualBytes = SearchCodec.EncodeRequest(capture);
        if (actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            Console.WriteLine($"SETUP_CONFIG root={root} scope=frozen status=same bytes={actualBytes.Length}");
            SearchCase noBound = capture with { Options = options with { Bound = null } };
            bool noBoundSame = SearchCodec.EncodeRequest(noBound).AsSpan().SequenceEqual(expectedBytes);
            SearchCase withPlayBound = capture with
            {
                Options = options with
                {
                    Scorer = EvaluationConfigCompiler.CompileScorer(state, player, rules, firstPlay,
                        policy, EvaluationRootFacts.Default with { TurnSetupRoot = true }),
                    Bound = EvaluationConfigCompiler.CompileBound(state, player, firstPlay, policy,
                        EvaluationRootFacts.Default with { TurnSetupRoot = true }),
                },
            };
            bool playBoundSame = SearchCodec.EncodeRequest(withPlayBound).AsSpan()
                .SequenceEqual(expectedBytes);
            Console.WriteLine($"SETUP_BOUND root={root} start=same "
                + $"without={(noBoundSame ? "same" : "diff")} "
                + $"playRoot={(playBoundSame ? "same" : "diff")}");
            return;
        }
        string? section = ConfigCompare.DescribeRequestDifference(expected, capture, out string detail);
        Console.WriteLine($"SETUP_CONFIG root={root} scope=frozen status=diff field={section ?? "<bytes>"}");
        if (detail.Length > 0)
            Console.WriteLine("  " + detail.Replace("\n", "\n  ", StringComparison.Ordinal));
    }

    private static void RunShortSearch(string root, TurnSetupEnumeration enumeration, string data)
    {
        if (!enumeration.HasPlayRoots || s_setupCase is not { } searchCase)
        {
            Console.WriteLine($"SETUP_SEARCH root={root} status=skip reason=no-play-root");
            return;
        }
        // Short deterministic solve on the compiled setup request. The production profile is
        // intentionally shrunk here; the point is that Battles[BestRootIndex] replays.
        BeamOptions options = searchCase.Options with
        {
            BeamWidth = 2,
            SecondRankBandWidth = 2,
            MaxExpandedNodes = 4000,
            MaxTurns = 4,
            SoftTimeBudget = TimeSpan.MaxValue,
            DeadlineExpired = null,
            StatePoolBudgetBytes = SearchMemoryBudgetBytes / 2,
        };
        BeamSolver solver = new(searchCase.Rules, options);
        BeamResult result;
        try
        {
            result = solver.Solve(searchCase.Battles, CancellationToken.None);
        }
        catch (InvalidOperationException error)
        {
            Console.WriteLine($"SETUP_SEARCH root={root} status=failed reason={error.Message}");
            return;
        }
        if (result.StopWithoutCandidate || result.BestRoute.Length == 0)
        {
            Console.WriteLine($"SETUP_SEARCH root={root} status=no-route expanded={result.ExpandedNodes} "
                + $"stop={result.StopReason}");
            return;
        }
        ReplayedRoute replay;
        try
        {
            replay = ReplayedRoute.Build(searchCase.Battles[result.BestRootIndex], searchCase.Rules,
                result.BestRoute, result.BestIsVictory);
        }
        catch (InvalidDataException error)
        {
            Console.WriteLine($"SETUP_SEARCH root={root} status=replay-failed reason={error.Message}");
            return;
        }
        GoldenRootRecord golden = GoldenData.Decode(ConfigCompare.ReadBrotli(
            Path.Combine(data, "d1", root, "golden.br")));
        int goldenIndex = -1;
        for (int index = 0; index < golden.Roots.Count; index++)
        {
            if (Fingerprint.Equal(golden.Roots[index].Battle, replay.Root))
            {
                goldenIndex = index;
                break;
            }
        }
        bool routeChoicesValid = true;
        foreach (ReplayedAction action in replay.Actions)
        {
            if (action.Choices.Length != action.Action.ChoiceCount)
                routeChoicesValid = false;
        }
        Console.WriteLine($"SETUP_SEARCH root={root} status=ok bestRoot={result.BestRootIndex} "
            + $"goldenRoot={goldenIndex} victory={replay.Victory} actions={replay.Count} "
            + $"expanded={result.ExpandedNodes} choices={routeChoicesValid} "
            + $"finalHp={replay.Actions.Length switch { > 0 => replay.Actions[^1].After.Player.CurrentHp, _ => replay.Root.Player.CurrentHp }} "
            + $"outcome={replay.Actions.Length switch { > 0 => replay.Actions[^1].After.Outcome, _ => replay.Root.Outcome }}");
    }

    private static void CompareProductionToFrozen(string data, string root, SearchCase production)
    {
        SearchCase expected;
        byte[] expectedBytes;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            expectedBytes = ConfigCompare.ReadBrotli(Path.Combine(directory, "config.br"));
            expected = SearchCodec.DecodeRequest(expectedBytes);
        }
        catch (InvalidDataException error)
        {
            Console.WriteLine($"SETUP_PROD root={root} status=reject reason=invalid-golden detail={error.Message}");
            return;
        }
        bool rulesSame = RulesCodec.Encode(expected.Rules).AsSpan()
            .SequenceEqual(RulesCodec.Encode(production.Rules));
        int battleMatches = 0;
        List<Battle> uniqueConfig = [];
        foreach (Battle battle in expected.Battles)
        {
            if (!uniqueConfig.Any(item => Fingerprint.Equal(item, battle)))
                uniqueConfig.Add(battle);
            if (production.Battles.Any(item => Fingerprint.Equal(item, battle)))
                battleMatches++;
        }
        SearchCase expectedScore = expected with
        {
            Battles = [],
            Options = new BeamOptions(1, 1, 1, TimeSpan.Zero, 1)
            {
                Scorer = expected.Options.Scorer,
                InitialActionCount = expected.Options.InitialActionCount,
            },
        };
        SearchCase actualScore = expectedScore with
        {
            Options = expectedScore.Options with { Scorer = production.Options.Scorer },
        };
        bool scoreSame = SearchCodec.EncodeRequest(expectedScore).AsSpan()
            .SequenceEqual(SearchCodec.EncodeRequest(actualScore));
        SearchCase expectedBound = expected with
        {
            Battles = [],
            Options = new BeamOptions(1, 1, 1, TimeSpan.Zero, 1)
            {
                Bound = expected.Options.Bound,
                InitialActionCount = expected.Options.InitialActionCount,
            },
        };
        SearchCase actualBound = expectedBound with
        {
            Options = expectedBound.Options with { Bound = production.Options.Bound },
        };
        bool boundSame = SearchCodec.EncodeRequest(expectedBound).AsSpan()
            .SequenceEqual(SearchCodec.EncodeRequest(actualBound));
        List<string> optionDiffs = [];
        if (expected.Options.BeamWidth != production.Options.BeamWidth)
            optionDiffs.Add($"beamWidth {expected.Options.BeamWidth}->{production.Options.BeamWidth}");
        if (expected.Options.MaxExpandedNodes != production.Options.MaxExpandedNodes)
            optionDiffs.Add($"maxExpandedNodes {expected.Options.MaxExpandedNodes}->{production.Options.MaxExpandedNodes}");
        if (expected.Options.MaxTurns != production.Options.MaxTurns)
            optionDiffs.Add($"maxTurns {expected.Options.MaxTurns}->{production.Options.MaxTurns}");
        if (expected.Options.SoftTimeBudget != production.Options.SoftTimeBudget)
            optionDiffs.Add("softTimeBudget");
        if (expected.Options.InitialPlayerMaxHp != production.Options.InitialPlayerMaxHp)
            optionDiffs.Add($"initialMaxHp {expected.Options.InitialPlayerMaxHp}->{production.Options.InitialPlayerMaxHp}");
        if (expected.Options.InitialPlayerTurn != production.Options.InitialPlayerTurn)
            optionDiffs.Add($"initialPlayerTurn {expected.Options.InitialPlayerTurn}->{production.Options.InitialPlayerTurn}");
        if ((expected.Options.Ordering != null) != (production.Options.Ordering != null))
            optionDiffs.Add("ordering none->present");
        bool bytesSame = SearchCodec.EncodeRequest(production).AsSpan().SequenceEqual(expectedBytes);
        Console.WriteLine($"SETUP_PROD root={root} status={(bytesSame ? "same" : "diff")} "
            + $"rules={(rulesSame ? "same" : "diff")} "
            + $"battles={production.Battles.Length} config={expected.Battles.Length} "
            + $"configUnique={uniqueConfig.Count} matched={battleMatches} "
            + $"score={(scoreSame ? "same" : "diff")} bound={(boundSame ? "same" : "diff")} "
            + $"options=[{string.Join(", ", optionDiffs)}]");
    }

    private static void CompareGolden(string data, string root,
        IReadOnlyList<TurnSetupBranch> branches)
    {
        GoldenRootRecord golden;
        try
        {
            golden = GoldenData.Decode(ConfigCompare.ReadBrotli(
                Path.Combine(data, "d1", root, "golden.br")));
        }
        catch (InvalidDataException error)
        {
            Console.WriteLine($"SETUP_GOLDEN root={root} status=reject reason={error.Message}");
            return;
        }
        int matched = 0;
        List<Battle> uniqueGolden = [];
        foreach (GoldenBattleRoot expected in golden.Roots)
        {
            if (!uniqueGolden.Any(existing => Fingerprint.Equal(existing, expected.Battle)))
                uniqueGolden.Add(expected.Battle);
            bool found = false;
            foreach (TurnSetupBranch branch in branches)
            {
                if (branch.Terminal || !Fingerprint.Equal(expected.Battle, branch.Root))
                    continue;
                found = true;
                break;
            }
            if (found)
                matched++;
            else
                Console.WriteLine($"  golden-miss index={matched}/{golden.Roots.Count}");
        }
        bool same = matched == golden.Roots.Count && branches.Count == uniqueGolden.Count;
        Console.WriteLine($"SETUP_GOLDEN root={root} status={(same ? "same" : "diff")} "
            + $"golden={golden.Roots.Count} goldenUnique={uniqueGolden.Count} "
            + $"enumerated={branches.Count} matched={matched}");
    }

    private static int CompareNativePlans(string root, IReadOnlyList<TurnSetupBranch> branches,
        Func<TurnSetupBranch, IReadOnlyList<int[]>, (Battle Root, IReadOnlyList<NativeSetupPage> Pages)> rebuild)
    {
        int failures = 0;
        List<Battle> nativeRoots = [];
        for (int index = 0; index < branches.Count; index++)
        {
            TurnSetupBranch branch = branches[index];
            int[][] pages;
            try
            {
                pages = PlanPages(branch);
            }
            catch (InvalidDataException error)
            {
                Console.WriteLine($"PLAN index={index} status=reject reason={error.Message}");
                failures++;
                continue;
            }
            (Battle native, IReadOnlyList<NativeSetupPage> nativePages) = rebuild(branch, pages);
            bool same = Fingerprint.Equal(branch.Root, native);
            bool optionsSame = ComparePlanOptions(branch, nativePages, out string optionDetail);
            if (!same || !optionsSame)
            {
                failures++;
                List<string> diff = same ? [] : BattleDump.Diff(branch.Root, native, "enumerated", "native");
                string field = optionsSame ? FirstField(diff) : "options";
                Console.WriteLine($"PLAN index={index} status=diff field={field} "
                    + $"choices={ChoiceLabels(branch)} indexes={FormatPages(pages)}");
                if (!optionsSame)
                    Console.WriteLine($"  {optionDetail}");
                foreach (string line in diff.Take(12))
                    Console.WriteLine("  " + line);
            }
            else
            {
                Console.WriteLine($"PLAN index={index} status=same choices={ChoiceLabels(branch)} "
                    + $"indexes={FormatPages(pages)} options={nativePages.Count}");
            }
            if (!nativeRoots.Any(existing => Fingerprint.Equal(existing, native)))
                nativeRoots.Add(native);
        }
        if (nativeRoots.Count != branches.Count)
        {
            failures++;
            Console.WriteLine($"SETUP_NATIVE root={root} status=diff nativeUnique={nativeRoots.Count} "
                + $"enumerated={branches.Count}");
        }
        else
        {
            Console.WriteLine($"SETUP_NATIVE root={root} status=same nativeUnique={nativeRoots.Count} "
                + $"enumerated={branches.Count}");
        }
        return failures;
    }

    /// <summary>
    /// Compares every native setup page against the kernel request that produced it: option ids,
    /// upgrade levels and order, plus the effective min/max selection bounds. Implicit choices do
    /// not open a page and are skipped; a page/request count mismatch is reported.
    /// </summary>
    private static bool ComparePlanOptions(TurnSetupBranch branch,
        IReadOnlyList<NativeSetupPage> pages, out string detail)
    {
        PlannedChoice[] choices = [.. branch.Choices.Where(static choice => !choice.Implicit)];
        if (choices.Length != pages.Count)
        {
            detail = $"stages plan={choices.Length} native={pages.Count}";
            return false;
        }
        for (int stage = 0; stage < choices.Length; stage++)
        {
            PlannedChoice planned = choices[stage];
            NativeSetupPage page = pages[stage];
            if (planned.OptionCount != page.Options.Count)
            {
                detail = $"stage={stage} options plan={planned.OptionCount} native={page.Options.Count}";
                return false;
            }
            for (int option = 0; option < planned.OptionCount; option++)
            {
                Card card = planned.OptionsAreGenerated
                    ? planned.GeneratedOptions[option]
                    : planned.PileOptions[option];
                string id = Registry.Card(card.Id).NativeId;
                (string CardId, int Upgrade) nativeOption = page.Options[option];
                if (!string.Equals(id, nativeOption.CardId, StringComparison.Ordinal)
                    || card.Upgrade != nativeOption.Upgrade)
                {
                    detail = $"stage={stage} option={option} plan={id}+{card.Upgrade} " +
                        $"native={nativeOption.CardId}+{nativeOption.Upgrade}";
                    return false;
                }
            }
            // FromChooseACardScreen (Toolbox, Discovery) always calls ICardSelector with 0..1 even
            // when the page's canSkip is false; that flag is not visible through GetSelectedCards.
            // Candidate identities/order are still compared for generated pages, but the native
            // bounds there do not express the kernel's Min/Max.
            if (!planned.OptionsAreGenerated)
            {
                int nativeMin = Math.Min(page.MinSelect, page.Options.Count);
                int nativeMax = Math.Min(page.MaxSelect, page.Options.Count);
                if (planned.Request.MinCount != nativeMin || planned.Request.MaxCount != nativeMax)
                {
                    detail = $"stage={stage} bounds plan={planned.Request.MinCount}..{planned.Request.MaxCount} " +
                        $"native={nativeMin}..{nativeMax} raw={page.MinSelect}..{page.MaxSelect}";
                    return false;
                }
            }
        }
        detail = "";
        return true;
    }

    /// <summary>Maps each plan selection to the native selector's option indexes.</summary>
    private static int[][] PlanPages(TurnSetupBranch branch)
    {
        PlannedChoice[] explicitChoices = [.. branch.Choices.Where(static choice => !choice.Implicit)];
        int[][] pages = new int[explicitChoices.Length][];
        for (int choice = 0; choice < explicitChoices.Length; choice++)
        {
            PlannedChoice planned = explicitChoices[choice];
            List<int> selected = new(planned.Request.OptionCount);
            for (int option = 0; option < planned.Request.OptionCount; option++)
            {
                if ((planned.Selection & (1UL << option)) != 0)
                    selected.Add(option);
            }
            if (selected.Count == 0 && planned.Request.MinCount > 0)
            {
                throw new InvalidDataException(
                    $"回合准备选择 {choice} 没有选中任何卡，但最小选择数是 {planned.Request.MinCount}。");
            }
            if (selected.Count < planned.Request.MinCount
                || selected.Count > planned.Request.MaxCount)
            {
                throw new InvalidDataException(
                    $"回合准备选择 {choice} 的选中数 {selected.Count} 不在 "
                    + $"{planned.Request.MinCount}..{planned.Request.MaxCount} 内。");
            }
            pages[choice] = [.. selected];
        }
        return pages;
    }

    private static string FormatPages(IReadOnlyList<int[]> pages)
        => string.Join(',', pages.Select(static page => $"[{string.Join('|', page)}]"));

    private static Battle[] OrderLikeExpected(Battle[] expected, IReadOnlyList<TurnSetupBranch> branches)
    {
        // A frozen root list may keep two byte-identical roots for two native options; the
        // enumerator deduplicates them. Reuse the equal branch so the data sections can still be
        // compared in the frozen order (the root count difference is reported separately).
        List<Battle> ordered = new(expected.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            int match = -1;
            for (int candidate = 0; candidate < branches.Count; candidate++)
            {
                if (branches[candidate].Terminal)
                    continue;
                if (!Fingerprint.Equal(expected[index], branches[candidate].Root))
                    continue;
                match = candidate;
                break;
            }
            if (match < 0)
                return [];
            ordered.Add(branches[match].Root);
        }
        return [.. ordered];
    }

    private static string ChoiceLabels(TurnSetupBranch branch)
    {
        List<string> labels = new(branch.Choices.Length);
        foreach (PlannedChoice planned in branch.Choices)
        {
            int selected = -1;
            for (int option = 0; option < planned.Request.OptionCount; option++)
            {
                if ((planned.Selection & (1UL << option)) != 0)
                {
                    selected = option;
                    break;
                }
            }
            if (selected < 0)
            {
                labels.Add($"{planned.Request.Effect}:skip");
                continue;
            }
            Card card = planned.Request.OptionsAreGenerated
                ? planned.GeneratedOptions[selected]
                : planned.PileOptions[selected];
            labels.Add($"{planned.Request.Effect}:{Registry.Card(card.Id).NativeId}+{card.Upgrade}");
        }
        return string.Join(',', labels);
    }

    private static string AllOptionLabels(TurnSetupBranch branch)
    {
        List<string> pages = new(branch.Choices.Length);
        foreach (PlannedChoice planned in branch.Choices)
        {
            List<string> values = new(planned.Request.OptionCount);
            for (int option = 0; option < planned.Request.OptionCount; option++)
            {
                Card card = planned.Request.OptionsAreGenerated
                    ? planned.GeneratedOptions[option]
                    : planned.PileOptions[option];
                values.Add($"{Registry.Card(card.Id).NativeId}+{card.Upgrade}");
            }
            pages.Add(string.Join('|', values));
        }
        return string.Join(" / ", pages);
    }

    private static string FirstField(List<string> diff)
    {
        if (diff.Count == 0)
            return "<bytes>";
        string line = diff[0];
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? line[..colon] : line;
    }
}
