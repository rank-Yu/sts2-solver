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
        CancellationToken token,
        SearchResumeState? resume = null,
        SearchPlan? resumePlan = null)
    {
        int threads = SearchThreadCount.Value;
        SearchPerformanceProfile profile = SearchPerformanceProfile.For(tier);
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.ForceSearchFailure?.Invoke(turn) is { } forcedFailure)
            return Failed(in searchRoot, turn, tier, profile, threads, forcedFailure, "forced_by_test");
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
            return Failed(in searchRoot, turn, tier, profile, threads, "import_failed",
                $"reason={error.Reason} {error.Detail}");
        }
        catch (UnsupportedException error)
        {
            return Failed(in searchRoot, turn, tier, profile, threads, "import_unsupported", error.Message);
        }
        catch (CapacityException error)
        {
            return Failed(in searchRoot, turn, tier, profile, threads, "import_capacity",
                CombatFailureDetail.Capacity(error,
                    error.Region == "Cards" ? CountCombatCards(player) : null));
        }
        catch (InvalidDataException error)
        {
            return Failed(in searchRoot, turn, tier, profile, threads, "request_invalid", error.Message);
        }
        catch (InvalidOperationException error)
        {
            return Failed(in searchRoot, turn, tier, profile, threads, "request_invalid", error.Message);
        }

        SearchPlan plan = resumePlan
            ?? profile.ToSearchPlan(searchCase.Options.SearchMemoryBudgetBytes,
                combat.Encounter?.RoomType == RoomType.Boss,
                combat.Encounter?.RoomType == RoomType.Elite, combat.RunState.CurrentActIndex);
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.SearchMillisecondsPerWidthForTest is { } fastMilliseconds)
            plan = plan with { MillisecondsPerWidth = fastMilliseconds };
#endif
        CombatLogRecorder.BeginSearch(searchCase, plan, setup: false, turn,
            CombatSessionController.ReasonName(reason), resumed: resume != null, tier, threads);
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
                new(searchCase, plan) { Progress = progress, Resume = resume }, storage,
                RunMember, token);
        }
        finally
        {
            SearchSession.EndProgress(progress);
        }

#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.ForceCapacityDropForTest is { Count: > 0 } forcedDrops
            && outcome.Beam != null)
        {
            outcome = outcome with
            {
                CapacityDroppedRegions =
                    new SortedDictionary<string, int>(forcedDrops, StringComparer.Ordinal),
            };
        }
#endif

        if (outcome.ResumeMerge is { } merge)
        {
            if (merge.Compared)
            {
                ModLog.Info(
                    $"[Sts2Solver] CONTROLLER_SEARCH_CONTINUE_RESULT turn={turn} " +
                    $"kept_stopped={merge.KeptStopped} reason={merge.Reason}");
            }
            else
            {
                ModLog.Warn(
                    $"[Sts2Solver] CONTROLLER_SEARCH_CONTINUE_COMPARE_FAILED turn={turn} " +
                    $"reason={merge.Reason} keeping=stopped");
            }
        }

        SearchProgressSnapshot? finalProgress = SearchSession.LatestProgress;
        if (outcome.Beam is not { } beam)
        {
            string status = outcome.FailureStatus.Length > 0 ? outcome.FailureStatus : "failed";
            string error = outcome.FailureError ?? "搜索没有返回路线";
            return new CombatSearchRun(in searchRoot, searchCase, outcome, null, null, null,
                finalProgress, Environment.TickCount64, tier, profile, threads, [.. memberRuns],
                "search_failed",
                $"search {status}: {error}")
            {
                RequestPlan = plan,
            };
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
            return RouteFailed(CombatFailureDetail.Capacity(error), "route_capacity");
        }
        tracker.Observe(searchRoot);
        CombatRoutePlan planResult = CombatRoutePlan.FromReplay(route, outcome.OnlyDeathRoutes,
            postCombatRelicHeal: searchCase.Options.Ordering?.PostCombatRelicHeal ?? default,
            relicTargets: searchCase.Options.Scorer?.RulesForCodec.RelicCounterTargets);
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH turn={turn} reason={reason} ms={outcome.ElapsedMs} " +
            $"expanded={outcome.TotalExpandedNodes} route={beam.BestRoute.Length} " +
            $"victory={beam.BestIsVictory} score={beam.BestScore} members={outcome.Members.Length} " +
            $"relic_heal={planResult.Outcome.PostCombatRelicHeal} " +
            $"selected_member={outcome.SelectedMember} threads={threads} " +
            $"soft_time_risk={outcome.SoftTimeRisk} skipped={outcome.MembersSkippedByTime} " +
            $"completion={outcome.Completion}");
        return new CombatSearchRun(in searchRoot, searchCase, outcome, beam, route, planResult,
            finalProgress, Environment.TickCount64, tier, profile, threads, [.. memberRuns],
            null, null)
        {
            RequestPlan = plan,
        };

        CombatSearchRun RouteFailed(string detail, string code)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_ROUTE_INVALID turn={turn} reason={reason} {detail}");
            return new CombatSearchRun(in searchRoot, searchCase, outcome, beam, null, null,
                finalProgress, Environment.TickCount64, tier, profile, threads, [.. memberRuns],
                code, detail)
            {
                RequestPlan = plan,
            };
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
                DeadlineExpiredAtPoll = result.Beam?.DeadlineExpiredAtPoll ?? 0,
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

    /// <summary>Card count across the player's combat piles, used only to explain a Cards capacity.</summary>
    private static int? CountCombatCards(Player player)
    {
        if (player.PlayerCombatState is not { } state)
            return null;
        return state.Hand.Cards.Count + state.DrawPile.Cards.Count
            + state.DiscardPile.Cards.Count + state.ExhaustPile.Cards.Count
            + state.PlayPile.Cards.Count;
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
            return SetupFailed(in startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", $"forced_by_test:{forcedFailure}");
        }
#endif
        if (setupCase.TurnSetupPlans == null
            || setupCase.TurnSetupPlans.Count != setupCase.Battles.Length)
        {
            return SetupFailed(in startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", "准备请求缺少与根数量一致的计划。");
        }
        SearchCase searchCase = setupCase with
        {
            Options = setupCase.Options with { SearchThreads = threads },
        };
        SearchPlan plan = profile.ToSearchPlan(searchCase.Options.SearchMemoryBudgetBytes,
            combat.Encounter?.RoomType == RoomType.Boss,
            combat.Encounter?.RoomType == RoomType.Elite, combat.RunState.CurrentActIndex);
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.SearchMillisecondsPerWidthForTest is { } fastMilliseconds)
            plan = plan with { MillisecondsPerWidth = fastMilliseconds };
#endif
        CombatLogRecorder.BeginSearch(searchCase, plan, setup: true, turn, "turn_setup",
            resumed: false, tier, threads);
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
            return SetupFailed(in startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", $"search {status}: {error}");
        }
        if ((uint)beam.BestRootIndex >= (uint)searchCase.Battles.Length)
        {
            return SetupFailed(in startRoot, setupCase, turn, tier, profile, threads,
                "turn_setup_failed", $"最优根下标 {beam.BestRootIndex} 超出 {searchCase.Battles.Length}。");
        }
        PlannedChoice[] choices = searchCase.TurnSetupPlans![beam.BestRootIndex];
        ReplayedRoute route;
        try
        {
            route = ReplayedRoute.Build(in searchCase.Battles[beam.BestRootIndex], rules,
                beam.BestRoute, beam.BestIsVictory);
        }
        catch (InvalidDataException error)
        {
            return SetupFailed(in startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线无法回放：{error.Message}");
        }
        catch (InvalidOperationException error)
        {
            return SetupFailed(in startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线无法回放：{error.Message}");
        }
        catch (UnsupportedException error)
        {
            return SetupFailed(in startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线含有尚未支持的效果：{error.Message}");
        }
        catch (CapacityException error)
        {
            return SetupFailed(in startRoot, searchCase, turn, tier, profile, threads,
                "turn_setup_failed", $"准备路线回放超出容量：region={error.Region} {error.Message}");
        }
        tracker.Observe(in searchCase.Battles[beam.BestRootIndex]);
        CombatRoutePlan planResult = CombatRoutePlan.FromReplay(route, outcome.OnlyDeathRoutes,
            choices, searchCase.Options.Ordering?.PostCombatRelicHeal ?? default,
            searchCase.Options.Scorer?.RulesForCodec.RelicCounterTargets);
        CombatStateStamp playStamp = CombatStateStamp.Capture(in searchCase.Battles[beam.BestRootIndex]);
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH turn={turn} reason={CombatSearchReason.TurnSetup} " +
            $"ms={outcome.ElapsedMs} expanded={outcome.TotalExpandedNodes} " +
            $"root_index={beam.BestRootIndex}/{searchCase.Battles.Length} " +
            $"route={beam.BestRoute.Length} victory={beam.BestIsVictory} score={beam.BestScore} " +
            $"choices={choices.Length} members={outcome.Members.Length} " +
            $"selected_member={outcome.SelectedMember} threads={threads} " +
            $"soft_time_risk={outcome.SoftTimeRisk} skipped={outcome.MembersSkippedByTime} " +
            $"completion={outcome.Completion}");
        return new CombatSearchRun(in searchCase.Battles[beam.BestRootIndex], searchCase, outcome, beam, route, planResult,
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
                DeadlineExpiredAtPoll = result.Beam?.DeadlineExpiredAtPoll ?? 0,
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

    private static CombatSearchRun SetupFailed(in Battle startRoot, SearchCase? setupCase, int turn,
        SearchPerformanceTier tier, SearchPerformanceProfile profile, int threads, string code,
        string detail)
    {
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_FAILED turn={turn} code={code} setup=true {detail}");
        CombatSearchResult outcome = new(CombatSearchStatus.Invalid, null, -1, 0, 0, [], detail);
        return new CombatSearchRun(in startRoot, setupCase, outcome, null, null, null, null,
            Environment.TickCount64, tier, profile, threads, [], code, detail)
        {
            SetupStartRoot = startRoot,
            TurnSetupChoices = [],
            SetupPlayStamp = null,
        };
    }

    private static CombatSearchRun Failed(in Battle root, int turn, SearchPerformanceTier tier,
        SearchPerformanceProfile profile, int threads, string code, string detail)
    {
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_FAILED turn={turn} code={code} {detail}");
        CombatSearchStatus status = CombatSearchStatus.Invalid;
        SearchMemberResult[] members = [];
#if STS2_SOLVER_TESTING
        status = CombatControllerTestHooks.ForceSearchFailureStatus ?? status;
        if (CombatControllerTestHooks.ForceSearchFailureMemoryExceeded)
        {
            members =
            [
                new SearchMemberResult(
                    new SearchMember(1, SearchEvaluation.Standard, SearchRetention.Standard,
                        SearchPotionLimit.None),
                    Ran: true, SkippedReason: null, ElapsedMilliseconds: 0, ExpandedNodes: 0,
                    StopReason: "memory", Victory: false, HpLost: null, PotionUses: null,
                    Adopted: false, MemoryPeakBytes: 0, EndedCompactions: 0)
                {
                    MemoryBudgetExceeded = true,
                },
            ];
        }
#endif
        CombatSearchResult outcome = new(status, null, -1, 0, 0, members, detail);
        return new CombatSearchRun(in root, null, outcome, null, null, null, null,
            Environment.TickCount64, tier, profile, threads, [], code, detail);
    }
}
