// One combat search, from an already imported root to a replayed route. This is the reusable
// "search once" half of the old CombatPilotHost; the controller owns lifecycle and UI publication.
// Capture/compile/portfolio scheduling follows CombatSolver src/Runtime/SolverController.cs at
// 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;

namespace Sts2Solver;

internal static class CombatSearchRunner
{
    internal static async Task<CombatSearchRun> RunAsync(
        NGame host,
        string root,
        Rules rules,
        LiveCombatTracker tracker,
        CombatState combat,
        Player player,
        Battle searchRoot,
        int turn,
        CombatSearchReason reason,
        SearchPerformanceTier tier,
        SearchPolicyConfiguration policyConfiguration,
        Func<SearchPolicy, Player, SearchPolicy>? configurePolicy,
        CancellationToken token)
    {
        int threads = SearchThreadCount.Value;
        SearchPerformanceProfile profile = SearchPerformanceProfile.For(tier);
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.ForceSearchFailure?.Invoke(turn) is { } forcedFailure)
            return Failed(searchRoot, turn, tier, profile, threads, forcedFailure, "forced_by_test");
#endif
        SearchCase searchCase;
        try
        {
            searchCase = SearchSession.CompileCase(
                combat, player, rules, searchRoot, policyConfiguration, configurePolicy,
                deterministic: false, tier);
            searchCase = searchCase with
            {
                Options = searchCase.Options with { SearchThreads = threads },
            };
        }
        catch (UnsupportedImportException error)
        {
            return Failed(searchRoot, turn, tier, profile, threads, "import_failed",
                $"reason={error.Reason} {error.Detail}");
        }
        catch (UnsupportedException error)
        {
            return Failed(searchRoot, turn, tier, profile, threads, "import_unsupported", error.Message);
        }
        catch (CapacityException error)
        {
            return Failed(searchRoot, turn, tier, profile, threads, "import_capacity",
                $"region={error.Region} {error.Message}");
        }
        catch (InvalidDataException error)
        {
            return Failed(searchRoot, turn, tier, profile, threads, "request_invalid", error.Message);
        }
        catch (InvalidOperationException error)
        {
            return Failed(searchRoot, turn, tier, profile, threads, "request_invalid", error.Message);
        }

        SearchPlan plan = profile.ToSearchPlan(searchCase.Options.SearchMemoryBudgetBytes,
            combat.Encounter?.RoomType == RoomType.Boss,
            combat.Encounter?.RoomType == RoomType.Elite, combat.RunState.CurrentActIndex);
        SearchStorage storage = new();
        List<SearchMemberRunUi> memberRuns = [];
        SearchProgressChannel? progress = SearchSession.BeginProgress();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_PROGRESS turn={turn} subscribed={SearchSession.ProgressSubscribed} " +
            $"channel={progress != null}");
        CombatSearchResult outcome;
        try
        {
            outcome = await CombatSearchCoordinator.RunAsync(
                new(searchCase, plan) { Progress = progress }, storage,
                RunMember, token);
        }
        finally
        {
            SearchSession.EndProgress(progress);
        }

        SearchProgressSnapshot? finalProgress = SearchSession.LatestProgress;
        if (outcome.Beam is not { } beam)
        {
            string status = outcome.FailureStatus.Length > 0 ? outcome.FailureStatus : "failed";
            string error = outcome.FailureError ?? "搜索没有返回路线";
            return new CombatSearchRun(searchRoot, searchCase, outcome, null, null, null,
                finalProgress, Environment.TickCount64, tier, profile, threads, [.. memberRuns],
                outcome.ForcedPotionsUnsatisfied ? "forced_potion_unusable" : "search_failed",
                $"search {status}: {error}");
        }
        ReplayedRoute route;
        try
        {
            route = ReplayedRoute.Build(searchRoot, rules, beam.BestRoute, beam.BestIsVictory);
        }
        catch (InvalidDataException error)
        {
            return RouteFailed(error.Message, "route_invalid");
        }
        catch (InvalidOperationException error)
        {
            return RouteFailed(error.Message, "route_invalid");
        }
        catch (UnsupportedException error)
        {
            return RouteFailed(error.Message, "route_unsupported");
        }
        catch (CapacityException error)
        {
            return RouteFailed($"region={error.Region} {error.Message}", "route_capacity");
        }
        tracker.Observe(searchRoot);
        CombatRoutePlan planResult = CombatRoutePlan.FromReplay(route,
            finalProgress?.Route?.OnlyDeathRoutes ?? false);
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH turn={turn} reason={reason} ms={outcome.ElapsedMs} " +
            $"expanded={outcome.TotalExpandedNodes} route={beam.BestRoute.Length} " +
            $"victory={beam.BestIsVictory} score={beam.BestScore} members={outcome.Members.Length} " +
            $"selected_member={outcome.SelectedMember} threads={threads} " +
            $"soft_time_risk={outcome.SoftTimeRisk} skipped={outcome.MembersSkippedByTime} " +
            $"planned_ms={outcome.PlannedBudgetMilliseconds} completion={outcome.Completion}");
        return new CombatSearchRun(searchRoot, searchCase, outcome, beam, route, planResult,
            finalProgress, Environment.TickCount64, tier, profile, threads, [.. memberRuns],
            null, null);

        CombatSearchRun RouteFailed(string detail, string code)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_ROUTE_INVALID turn={turn} reason={reason} {detail}");
            return new CombatSearchRun(searchRoot, searchCase, outcome, beam, null, null,
                finalProgress, Environment.TickCount64, tier, profile, threads, [.. memberRuns],
                code, detail);
        }

        async ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage shared,
            CancellationToken memberToken)
        {
            memberToken.ThrowIfCancellationRequested();
            SearchRunResult result = await SearchSession.RunAsync(
                host, root, member, deterministic: false, tier, resultDirectory: null,
                cancelAfterMilliseconds: null, shared, memberToken);
            memberRuns.Add(new SearchMemberRunUi
            {
                Index = memberRuns.Count,
                Status = result.Status,
                ErrorKind = result.ErrorKind.Length == 0 ? null : result.ErrorKind,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
                ExpandedNodes = result.ExpandedNodes,
                Transitions = result.Transitions,
                ManagedHeapPeakBytes = result.ManagedHeapPeakBytes,
                WorkingSetPeakBytes = result.WorkingSetPeakBytes,
                AllocatedBytes = result.AllocatedBytes,
                PacerYields = result.PacerYields,
                PacerFrameRecoveryWaits = result.PacerFrameRecoveryWaits,
                PacerFrameRecoveryWaitMilliseconds = result.PacerFrameRecoveryWaitMilliseconds,
            });
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_SEARCH_MEMBER turn={turn} reason={reason} " +
                $"width={member.Options.BeamWidth} status={result.Status} " +
                $"ms={result.ElapsedMilliseconds} expanded={result.ExpandedNodes}");
            return new SearchMemberMeasurement(result.Beam, result.Status, result.Error,
                result.ElapsedMilliseconds, result.ManagedHeapPeakBytes,
                result.WorkingSetPeakBytes);
        }
    }

    /// <summary>
    /// Runs one multi-root setup request. The search root is the enumerated Play root selected by
    /// <see cref="BeamResult.BestRootIndex"/>; the route is replayed from that root with the plan's
    /// selections, and the exact Play stamp is handed back for the later live comparison.
    /// </summary>
    internal static async Task<CombatSearchRun> RunSetupAsync(
        NGame host,
        string root,
        Rules rules,
        LiveCombatTracker tracker,
        CombatState combat,
        Battle startRoot,
        SearchCase setupCase,
        int turn,
        SearchPerformanceTier tier,
        CancellationToken token)
    {
        int threads = SearchThreadCount.Value;
        SearchPerformanceProfile profile = SearchPerformanceProfile.For(tier);
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.ForceSearchFailure?.Invoke(turn) is { } forcedFailure)
        {
            return SetupFailed(startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", $"forced_by_test:{forcedFailure}");
        }
#endif
        if (setupCase.TurnSetupPlans == null
            || setupCase.TurnSetupPlans.Count != setupCase.Battles.Length)
        {
            return SetupFailed(startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", "准备请求缺少与根数量一致的计划。");
        }
        SearchCase searchCase = setupCase with
        {
            Options = setupCase.Options with { SearchThreads = threads },
        };
        SearchPlan plan = profile.ToSearchPlan(searchCase.Options.SearchMemoryBudgetBytes,
            combat.Encounter?.RoomType == RoomType.Boss,
            combat.Encounter?.RoomType == RoomType.Elite, combat.RunState.CurrentActIndex);
        SearchStorage storage = new();
        List<SearchMemberRunUi> memberRuns = [];
        SearchProgressChannel? progress = SearchSession.BeginProgress();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_PROGRESS turn={turn} subscribed={SearchSession.ProgressSubscribed} " +
            $"channel={progress != null} setup=true");
        CombatSearchResult outcome;
        try
        {
            outcome = await CombatSearchCoordinator.RunAsync(
                new(searchCase, plan) { Progress = progress }, storage,
                RunMember, token);
        }
        finally
        {
            SearchSession.EndProgress(progress);
        }

        SearchProgressSnapshot? finalProgress = SearchSession.LatestProgress;
        if (outcome.Beam is not { } beam)
        {
            string status = outcome.FailureStatus.Length > 0 ? outcome.FailureStatus : "failed";
            string error = outcome.FailureError ?? "搜索没有返回路线";
            return SetupFailed(startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", $"search {status}: {error}");
        }
        if ((uint)beam.BestRootIndex >= (uint)searchCase.Battles.Length)
        {
            return SetupFailed(startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", $"最优根下标 {beam.BestRootIndex} 超出 {searchCase.Battles.Length}。");
        }
        Battle playRoot = searchCase.Battles[beam.BestRootIndex];
        PlannedChoice[] choices = searchCase.TurnSetupPlans![beam.BestRootIndex];
        ReplayedRoute route;
        try
        {
            route = ReplayedRoute.Build(playRoot, rules, beam.BestRoute, beam.BestIsVictory);
        }
        catch (InvalidDataException error)
        {
            return SetupFailed(startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线无法回放：{error.Message}");
        }
        catch (InvalidOperationException error)
        {
            return SetupFailed(startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线无法回放：{error.Message}");
        }
        catch (UnsupportedException error)
        {
            return SetupFailed(startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线含有尚未支持的效果：{error.Message}");
        }
        catch (CapacityException error)
        {
            return SetupFailed(startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线回放超出容量：region={error.Region} {error.Message}");
        }
        tracker.Observe(playRoot);
        CombatRoutePlan planResult = CombatRoutePlan.FromReplay(route,
            finalProgress?.Route?.OnlyDeathRoutes ?? false, choices);
        CombatStateStamp playStamp = CombatStateStamp.Capture(playRoot);
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH turn={turn} reason={CombatSearchReason.TurnSetup} " +
            $"ms={outcome.ElapsedMs} expanded={outcome.TotalExpandedNodes} " +
            $"root_index={beam.BestRootIndex}/{searchCase.Battles.Length} " +
            $"route={beam.BestRoute.Length} victory={beam.BestIsVictory} score={beam.BestScore} " +
            $"choices={choices.Length} members={outcome.Members.Length} " +
            $"selected_member={outcome.SelectedMember} threads={threads} " +
            $"soft_time_risk={outcome.SoftTimeRisk} skipped={outcome.MembersSkippedByTime} " +
            $"planned_ms={outcome.PlannedBudgetMilliseconds} completion={outcome.Completion}");
        return new CombatSearchRun(playRoot, searchCase, outcome, beam, route, planResult,
            finalProgress, Environment.TickCount64, tier, profile, threads, [.. memberRuns],
            null, null)
        {
            SetupStartRoot = startRoot,
            TurnSetupChoices = choices,
            SetupPlayStamp = playStamp,
        };

        async ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage shared,
            CancellationToken memberToken)
        {
            memberToken.ThrowIfCancellationRequested();
            SearchRunResult result = await SearchSession.RunAsync(
                host, root, member, deterministic: false, tier, resultDirectory: null,
                cancelAfterMilliseconds: null, shared, memberToken);
            memberRuns.Add(new SearchMemberRunUi
            {
                Index = memberRuns.Count,
                Status = result.Status,
                ErrorKind = result.ErrorKind.Length == 0 ? null : result.ErrorKind,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
                ExpandedNodes = result.ExpandedNodes,
                Transitions = result.Transitions,
                ManagedHeapPeakBytes = result.ManagedHeapPeakBytes,
                WorkingSetPeakBytes = result.WorkingSetPeakBytes,
                AllocatedBytes = result.AllocatedBytes,
                PacerYields = result.PacerYields,
                PacerFrameRecoveryWaits = result.PacerFrameRecoveryWaits,
                PacerFrameRecoveryWaitMilliseconds = result.PacerFrameRecoveryWaitMilliseconds,
            });
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_SEARCH_MEMBER turn={turn} reason=turn_setup " +
                $"width={member.Options.BeamWidth} status={result.Status} " +
                $"ms={result.ElapsedMilliseconds} expanded={result.ExpandedNodes}");
            return new SearchMemberMeasurement(result.Beam, result.Status, result.Error,
                result.ElapsedMilliseconds, result.ManagedHeapPeakBytes,
                result.WorkingSetPeakBytes);
        }
    }

    private static CombatSearchRun SetupFailed(Battle startRoot, SearchCase? setupCase, int turn,
        SearchPerformanceTier tier, SearchPerformanceProfile profile, int threads, string code,
        string detail)
    {
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_FAILED turn={turn} code={code} setup=true {detail}");
        CombatSearchResult outcome = new(CombatSearchStatus.Invalid, null, -1, 0, 0, [], detail);
        return new CombatSearchRun(startRoot, setupCase, outcome, null, null, null, null,
            Environment.TickCount64, tier, profile, threads, [], code, detail)
        {
            SetupStartRoot = startRoot,
            TurnSetupChoices = [],
            SetupPlayStamp = null,
        };
    }

    private static CombatSearchRun Failed(Battle root, int turn, SearchPerformanceTier tier,
        SearchPerformanceProfile profile, int threads, string code, string detail)
    {
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_FAILED turn={turn} code={code} {detail}");
        CombatSearchResult outcome = new(CombatSearchStatus.Invalid, null, -1, 0, 0, [], detail);
        return new CombatSearchRun(root, null, outcome, null, null, null, null,
            Environment.TickCount64, tier, profile, threads, [], code, detail);
    }
}
