using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.TestHost;

namespace Differential;

/// <summary>从原版根检查回血估算、停搜目标，并逐动作核对生成的胜利路线。</summary>
internal static class HealingBoundChecks
{
    internal static int Run(string requestPath, string expectedHealing, string? loadoutPath)
    {
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        TerminalBattleCapture.Install();
        OfflineRequest request = JsonSerializer.Deserialize<OfflineRequest>(
            File.ReadAllText(requestPath), OfflineJson.Options)!;
        Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
            turnSetupSelector: new TurnSetupCardSelector(0));
        loop.RunUntilCompleted(build, TimeSpan.FromSeconds(120), "healing root");
        CombatState state = build.GetAwaiter().GetResult();
        if (PrefixPlan.Parse(request).Length != 0)
            RecaptureRoot.VerifyNativePrefix(loop, state, request);
        var player = LocalContext.GetMe(state)!;
        Rules rules = RulesCompiler.Compile(state, player, (RunState)state.RunState);
        LiveCombatTracker tracker = new(state);
        Battle root = tracker.ImportRoot(state, player, rules);
        tracker.Observe(root);
        SearchPolicy policy = SearchPolicyOverrides.ApplyOverrides(
            SearchPolicyCapture.Capture(state, player), request, player);

        AbstractModel[] external = [];
        ModHelper.SubscribeForCombatStateHooks("Sts2Solver.HealingBoundChecks", _ => external);
        SearchCase Compile() => SearchRequestCompiler.CompileRequest(state, player, rules, root,
            policy, EvaluationRootFacts.Default, SearchPerformanceProfile.For(SearchPerformanceTier.Medium),
            deterministic: true, searchMemoryBudgetBytes: 256 * 1024 * 1024);
        SearchCase native = Compile();
        void ExpectNativeEstimate(string label)
        {
            SearchCase actual = Compile();
            if (actual.Options.Bound!.ConfigForCodec.Certificate != native.Options.Bound!.ConfigForCodec.Certificate
                || actual.Options.Bound.EvaluatePruning(root, rules) != native.Options.Bound.EvaluatePruning(root, rules)
                || new BeamSolver(rules, actual.Options).PrepareVictoryTarget([root])
                    != new BeamSolver(rules, native.Options).PrepareVictoryTarget([root]))
                throw new InvalidDataException(label + " changed the native healing estimate.");
        }

        external = [new UnknownSubscriber()];
        ExpectNativeEstimate("external subscriber");
        external = [];
        if (loadoutPath is not null)
        {
            Assembly loadout = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(loadoutPath));
            string[] names =
            [
                "Loadout.Services.Morphing.BottledMonsterMorphCombatSnapshotHook",
                "Loadout.Services.PowerGiver.PowerGiverCombatStartHook",
                "Loadout.Services.TildeKey.LoadoutKillAllMonstersCombatHook",
                "Loadout.Services.TildeKey.LoadoutEveryCardFreeCombatHook",
                "Loadout.Services.PowerGiver.PowerGiverSummonHook",
            ];
            external = names.Select(name => (AbstractModel)Activator.CreateInstance(loadout.GetType(name)!)!).ToArray();
            ExpectNativeEstimate("Loadout subscribers");
            Console.WriteLine($"LOADOUT_SOURCE_OK mvid={loadout.ManifestModule.ModuleVersionId} callbacks={external.Length}");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type service = loadout.GetType("Loadout.Services.PowerGiver.PowerGiverStateService")!;
            object run = service.GetField("_run", flags)!.GetValue(null)!;
            IDictionary counters = (IDictionary)run.GetType().GetProperty("MonsterCounters")!.GetValue(run)!;
            counters.Add("REGEN_POWER", 1);
            try { ExpectNativeEstimate("configured summon power"); }
            finally { counters.Remove("REGEN_POWER"); }
        }

        SearchCase search = Compile();
        var bound = search.Options.Bound!;
        bool known = bound.TryVictoryHealing(root, rules, out long healing, out long maxHpGain);
        VictoryStopTarget target = new BeamSolver(rules, search.Options).PrepareVictoryTarget([root]);
        if (expectedHealing == "unknown")
        {
            if (known || target.EffectiveHpDeficit is not null)
                throw new InvalidDataException("Expected the healing target to be unavailable.");
            Console.WriteLine($"HEALING_BOUND_OK scenario={request.ScenarioId} mode={rules.Mode} healing=unknown reason={bound.DisabledReason}");
            return 0;
        }
        bool estimateOnly = expectedHealing.StartsWith("estimate:", StringComparison.Ordinal);
        string[] expectedParts = (estimateOnly ? expectedHealing["estimate:".Length..] : expectedHealing).Split('/');
        if (expectedParts.Length > 2)
            throw new InvalidDataException("Expected healing[/maxHpGain].");
        long expected = long.Parse(expectedParts[0], System.Globalization.CultureInfo.InvariantCulture);
        long expectedGain = expectedParts.Length == 2
            ? long.Parse(expectedParts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (!known || healing != expected || maxHpGain != expectedGain
            || bound.EvaluatePruning(root, rules).RemainingUnknown
            || !estimateOnly && target.EffectiveHpDeficit is null)
            throw new InvalidDataException($"healing={known}/{healing}/{maxHpGain}, expected={expected}/{expectedGain}; target={target}; disabled={bound.DisabledReason}");
        if (estimateOnly)
        {
            Console.WriteLine($"HEALING_ESTIMATE_OK scenario={request.ScenarioId} healing={healing} maxHpGain={maxHpGain} target={target.EffectiveHpDeficit}");
            return 0;
        }

        ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage storage, CancellationToken token)
        {
            BeamResult beam = new BeamSolver(member.Rules, member.Options with
                { SearchThreads = 1, SoftTimeBudget = TimeSpan.MaxValue, DeadlineExpired = null })
                .Solve(member.Battles, storage, token);
            return ValueTask.FromResult(new SearchMemberMeasurement(beam, "ok", null, 0));
        }
        SearchPlan plan = new(300, 200, 450, 120, 256 * 1024 * 1024)
            { MillisecondsPerWidth = 50, NodesPerWidth = 1024 };
        CombatSearchResult outcome = CombatSearchCoordinator.RunAsync(
            new(search, plan), new SearchStorage(), RunMember, CancellationToken.None, clock: () => 0)
            .AsTask().GetAwaiter().GetResult();
        BeamResult result = outcome.Beam ?? throw new InvalidDataException("No route.");
        int membersRun = outcome.Members.Count(member => member.Ran);
        if (!result.BestIsVictory || expected == 0 && membersRun != 1)
            throw new InvalidDataException($"Expected a healing-target victory; victory={result.BestIsVictory}, members={membersRun}.");
        if (result.StopReason != "healing-target")
            throw new InvalidDataException("Victory did not stop on its healing target.");
        ReplayedRoute route = ReplayedRoute.Build(root, rules, result.BestRoute, result.BestIsVictory);
        NativeActionExecutor executor = new(loop, state, player, rules, tracker);
        foreach (ReplayedAction action in route.Actions)
        {
            Battle actual = executor.Execute(action);
            string[] differences = CombatRouteDecision.BoundaryDiff(action.After, actual);
            if (differences.Length != 0)
                throw new InvalidDataException(string.Join("\n", differences));
        }
        Console.WriteLine($"HEALING_BOUND_OK scenario={request.ScenarioId} mode={rules.Mode} " +
            $"certificate={bound.ConfigForCodec.Certificate} healing={healing} target={target.EffectiveHpDeficit} " +
            $"members={membersRun} nodes={result.ExpandedNodesTotal} actions={route.Actions.Length} " +
            $"end_hp={route.Actions[^1].After.Player.CurrentHp} loadout={loadoutPath is not null}");
        return 0;
    }

    private sealed class UnknownSubscriber : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => true;
    }
}
