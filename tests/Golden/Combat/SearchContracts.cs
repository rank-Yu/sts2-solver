using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class SearchContracts
{
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("combat-search: " + message); }

    internal static void Run()
    {
        using FileStream input = File.OpenRead("tests/Golden/data/d2/requests/837a13316751422b9533614c35e14ea07e77fb367818725ab6fe9f5db3303d60.request.br");
        using BrotliStream compressed = new(input, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        compressed.CopyTo(bytes);
        SearchCase original = SearchCodec.DecodeRequest(bytes.ToArray());
        SearchPlan plan = new(12, 8, 16, 5, 100_000_000) { MillisecondsPerWidth = 125 };
        Assert(SearchCodec.EncodeRequest(original).AsSpan()
            .SequenceEqual(SearchCodec.EncodeRequest(SearchCodec.DecodeRequest(SearchCodec.EncodeRequest(original)))),
            "request round trip");

        Scorer standardScorer = original.Options.Scorer ?? throw new InvalidDataException("夹具没有评分器。");
        ScoreFeatures rootFeatures = standardScorer.Features(original.Battles[0], 0);
        double standardRank = standardScorer.BeamRankScore(original.Battles[0], rootFeatures, false, false);
        double baseRank = new Scorer(standardScorer.RulesForCodec, baseOnly: true)
            .BeamRankScore(original.Battles[0], rootFeatures, false, false);
        double attackRank = new Scorer(standardScorer.RulesForCodec, offensive: true)
            .BeamRankScore(original.Battles[0], rootFeatures, false, false);
        Assert(baseRank != standardRank && attackRank != standardRank, "evaluation dimension changes ranking");
        BeamOptions baseline = SearchBudgetPolicy.ApplyRegression(original.Options);
        BeamOptions legacy = baseline with { BeamWidth = 8, MaxExpandedNodes = 1,
            SoftTimeBudget = TimeSpan.MaxValue };
        BeamResult capped = new BeamSolver(original.Rules, legacy).Solve(original.Battles, CancellationToken.None);
        BeamResult uncompressed = new BeamSolver(original.Rules, legacy with { MaxExpandedNodes = 2_000 })
            .Solve(original.Battles, CancellationToken.None);
        _ = ReplayedRoute.Build(original.Battles[uncompressed.BestRootIndex], original.Rules,
            uncompressed.BestRoute, uncompressed.BestIsVictory);
        BeamOptions batched = legacy with { MaxExpandedNodes = 2_000,
            EndedBatchTrigger = 16, EndedBatchTarget = 8,
            SearchMemoryBudgetBytes = 100_000_000 };
        BeamOptions unlimited = batched with { UnlimitedNodes = true, MaxExpandedNodes = 1,
            SoftTimeBudget = TimeSpan.FromSeconds(1) };
        BeamResult unbounded = new BeamSolver(original.Rules, unlimited).Solve(original.Battles, CancellationToken.None);
        SearchStorage storage = new();
        BeamResult first = new BeamSolver(original.Rules, batched).Solve(original.Battles, storage, CancellationToken.None);
        Assert(capped.ExpandedNodes <= 1 && unbounded.ExpandedNodesTotal > 1, "new search has no hidden node cap");
        Assert(first.EndedCompactions > 0, "end-turn batch actually compacts");
        Assert(first.Duplicates > 0, "candidate deduplication is exercised");
        _ = ReplayedRoute.Build(original.Battles[first.BestRootIndex], original.Rules,
            first.BestRoute, first.BestIsVictory);
        BeamResult second = new BeamSolver(original.Rules, batched).Solve(original.Battles, storage, CancellationToken.None);
        Assert(SearchCodec.EncodeResult(first).AsSpan().SequenceEqual(SearchCodec.EncodeResult(second)),
            "compacted pools and route references survive storage reuse");
        BeamResult parallel = new BeamSolver(original.Rules, batched with { SearchThreads = 3 })
            .Solve(original.Battles, CancellationToken.None);
        Assert(SearchCodec.EncodeResult(first).AsSpan().SequenceEqual(SearchCodec.EncodeResult(parallel)),
            "parallel expansion commits in parent order with the same result");
        Assert(first.EndTurnPrefixShared == parallel.EndTurnPrefixShared
            && first.EndTurnPrefixUnshared == parallel.EndTurnPrefixUnshared
            && first.EndTurnPrefixReusedLeaves == parallel.EndTurnPrefixReusedLeaves
            && first.EndTurnPrefixChoiceUnshared == parallel.EndTurnPrefixChoiceUnshared
            && first.EndTurnPrefixExtraTurnUnshared == parallel.EndTurnPrefixExtraTurnUnshared
            && first.EndTurnPrefixNotInProgress == parallel.EndTurnPrefixNotInProgress,
            "parallel prefix diagnostics aggregate to the single-thread totals");
        Assert(first.Equivalence == parallel.Equivalence,
            "parallel equivalence diagnostics aggregate to the single-thread totals");

        // A worker exception inside a layer-boundary selection batch must be handed back to the
        // search thread as the same object; the same worker pool must then run another batch and
        // continue through subsequent action layers. The contract is default-off and self-asserts
        // the exception identity and the recovery batch size.
        BeamOptions recoveryOptions = batched with
        {
            BeamWidth = 24,
            MaxExpandedNodes = 1_500,
            SearchThreads = 4,
        };
        BeamResult recoveryBaseline = new BeamSolver(original.Rules,
            recoveryOptions with { SearchThreads = 1 }).Solve(original.Battles, CancellationToken.None);
        SearchVerificationHooks.VerifySelectionBatchFailureRecovery = true;
        SearchVerificationHooks.SelectionBatchFailure = null;
        SearchVerificationHooks.SelectionBatchFailureObserved = null;
        SearchVerificationHooks.SelectionBatchRecoveryItems = 0;
        BeamResult recovered;
        try
        {
            Task<BeamResult> contract = Task.Run(() => new BeamSolver(original.Rules, recoveryOptions)
                .Solve(original.Battles, CancellationToken.None));
            Assert(contract.Wait(TimeSpan.FromSeconds(60)),
                "selection batch failure contract did not finish");
            recovered = contract.Result;
        }
        finally
        {
            SearchVerificationHooks.VerifySelectionBatchFailureRecovery = false;
        }
        Assert(SearchVerificationHooks.SelectionBatchFailureObserved is InvalidOperationException observed
            && ReferenceEquals(observed, SearchVerificationHooks.SelectionBatchFailure),
            "selection batch failure did not reach the calling thread as the original exception");
        Assert(SearchVerificationHooks.SelectionBatchRecoveryItems == 16,
            "the same worker pool did not complete the next batch after a worker failure");
        Assert(SearchCodec.EncodeResult(recoveryBaseline).AsSpan()
                .SequenceEqual(SearchCodec.EncodeResult(recovered)),
            "the search after a recovered batch failure did not continue to the same result");
        Assert(recovered.ExpandedNodesTotal > 1,
            "the recovered batch failure contract did not reach later action layers");
        Console.WriteLine($"COMBAT_SEARCH batch_failure_recovered expanded={recovered.ExpandedNodesTotal}");

        BeamResult streamed = new BeamSolver(original.Rules,
            batched with { WorkspaceBufferLimitBytes = 1 })
            .Solve(original.Battles, CancellationToken.None);
        Assert(SearchCodec.EncodeResult(first).AsSpan().SequenceEqual(SearchCodec.EncodeResult(streamed)),
            "the straight-through expansion produces the same result");
        BeamResult parallelStreamed = new BeamSolver(original.Rules,
            batched with { SearchThreads = 3, WorkspaceBufferLimitBytes = 1 })
            .Solve(original.Battles, CancellationToken.None);
        Assert(SearchCodec.EncodeResult(first).AsSpan()
                .SequenceEqual(SearchCodec.EncodeResult(parallelStreamed))
            && parallelStreamed.WorkspaceOverflowParents > 0,
            "parallel over-limit parents fall back to the same straight-through expansion");

        long clock = 0;
        int delayedCalls = 0;
        ValueTask<SearchMemberMeasurement> DelayedShare(SearchCase member, SearchStorage shared, CancellationToken token)
        {
            if (delayedCalls++ == 0)
            {
                clock = (long)member.Options.SoftTimeBudget.TotalMilliseconds + 1;
                BeamResult delayed = new BeamSolver(member.Rules, member.Options).Solve(member.Battles, shared, token);
                Assert(delayed.SoftTimeBudgetReached && delayed.ExpandedNodesTotal == 0,
                    "member share includes host queue");
                return ValueTask.FromResult(new SearchMemberMeasurement(delayed, "ok", null, 0));
            }
            return ValueTask.FromResult(new SearchMemberMeasurement(null, "none", null, 0));
        }
        CombatSearchResult delayedShare = CombatSearchCoordinator.RunAsync(new(original, plan),
            new SearchStorage(), DelayedShare, CancellationToken.None, () => clock)
            .AsTask().GetAwaiter().GetResult();
        Assert(delayedShare.Members[0].StopReason == "time" && delayedShare.Members[0].ExpandedNodes == 0,
            "member cutoff is independent of total cutoff");

        clock = 0;
        int independentCalls = 0;
        ValueTask<SearchMemberMeasurement> StaleThenValid(SearchCase member, SearchStorage shared, CancellationToken token)
        {
            Assert(!member.Options.DeadlineExpired!(), "next member starts with a fresh deadline");
            clock += independentCalls++ == 0 ? 1100 : 1;
            return ValueTask.FromResult(new SearchMemberMeasurement(first, "ok", null, 0));
        }
        SearchPlan timedPlan = plan with { BaselineWidth = 8, NarrowWidth = 8, WideWidth = 8, OffensiveWidth = 8 };
        CombatSearchResult validated = CombatSearchCoordinator.RunAsync(new(original, timedPlan),
            new(), StaleThenValid, CancellationToken.None, () => clock).AsTask().GetAwaiter().GetResult();
        Assert(validated.Members[0].StopReason == "deadline" && !validated.Members[0].Adopted
            && validated.Members[1].Ran && validated.Members[1].HpLost.HasValue
            && validated.Members[1].StopReason != "deadline",
            "expired result cannot be adopted; a later member can still validate its real route");

        int retentionChecks = 0;
        bool expired = false;
        BeamOptions interruptedOptions = batched with
        {
            UnlimitedNodes = true,
            DeadlineExpired = () => expired,
            RetentionCheckpoint = () => { if (++retentionChecks == 2) expired = true; },
        };
        BeamResult interrupted = new BeamSolver(original.Rules, interruptedOptions)
            .Solve(original.Battles, CancellationToken.None);
        Assert(retentionChecks >= 2 && interrupted.ExpandedNodesTotal > 0
            && interrupted.SoftTimeBudgetReached && interrupted.StopReason == "time",
            "deadline interrupts retention at a safe checkpoint");
        _ = ReplayedRoute.Build(original.Battles[interrupted.BestRootIndex], original.Rules,
            interrupted.BestRoute, interrupted.BestIsVictory);
        using CancellationTokenSource cancel = new();
        int cancellationChecks = 0;
        BeamOptions cancelOptions = batched with
        {
            UnlimitedNodes = true,
            RetentionCheckpoint = () => { if (++cancellationChecks == 2) cancel.Cancel(); },
        };
        bool cancelled = false;
        try { _ = new BeamSolver(original.Rules, cancelOptions).Solve(original.Battles, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "user cancellation propagates independently of time stops");

        bool smallPool = false;
        try
        {
            BeamOptions tiny = batched with { BeamWidth = 100,
                StatePoolBudgetBytes = SearchMemoryBudget.PlanningStateBytes * 64 };
            _ = new BeamSolver(original.Rules, tiny).Solve(original.Battles, CancellationToken.None);
        }
        catch (SearchMemoryBudgetException error) when (error.Region == "ended-pool-capacity") { smallPool = true; }
        Assert(smallPool, "too-small pool is an explicit budget failure");
        bool zeroBudget = false;
        try { _ = new BeamSolver(original.Rules, batched with { SearchMemoryBudgetBytes = 0 })
            .Solve(original.Battles, CancellationToken.None); }
        catch (InvalidDataException) { zeroBudget = true; }
        Assert(zeroBudget, "zero search-memory budget is rejected at Solve entry");
        BeamResult largePool = new BeamSolver(original.Rules, batched with
        {
            MaxExpandedNodes = 1, StatePoolBudgetBytes = 3_000_000_000,
        }).Solve(original.Battles, CancellationToken.None);
        Assert(largePool.ExpandedNodesTotal > 0, "actual beam supports long pool budget above 2 GiB");

        Assert(first.BestRoute.All(a => a.Kind != PlayerActionKind.UsePotion), "fixture has a potion-free route");
        bool fallback = false;
        for (long limit = 1_000_000; limit <= 20_000_000 && !fallback; limit += 250_000)
        {
            try
            {
                BeamResult limited = new BeamSolver(original.Rules, batched with { SearchMemoryBudgetBytes = limit })
                    .Solve(original.Battles, CancellationToken.None);
                if (!limited.SearchMemoryBudgetExceeded || limited.ExpandedNodesTotal == 0) continue;
                Assert(limited.SearchMemoryUsedBytes <= limit, "low-memory allocation stays within ledger");
                _ = ReplayedRoute.Build(original.Battles[limited.BestRootIndex], original.Rules,
                    limited.BestRoute, limited.BestIsVictory);
                fallback = true;
            }
            catch (SearchMemoryBudgetException) { } // Root setup may not fit this trial budget.
        }
        Assert(fallback, "low-memory search returns a replayable fallback");
        Assert(new BeamSolver(original.Rules, legacy).TryCompareResultQuality(original.Battles,
            first, uncompressed, out int compactionComparison), "batch comparison replays both routes");
        BeamOptions tinyNext = batched with
        {
            MaxExpandedNodes = 20_000,
            StatePoolBudgetBytes = SearchMemoryBudget.PlanningStateBytes * 64,
            EndedBatchTrigger = 1_000_000,
            EndedBatchTarget = 1_000_000,
        };
        BeamResult reserveDropped = new BeamSolver(original.Rules, tinyNext)
            .Solve(original.Battles, CancellationToken.None);
        Assert(reserveDropped.CapacityPruned > 0
            && reserveDropped.CapacityDroppedChildren.TryGetValue("Beam.Pool", out int poolDrops)
            && poolDrops > 0,
            "full per-action pool drops children at Reserve and counts the boundary");

        // A reusable rank scratch array may be longer than the current pool. Selection must only
        // read its live prefix; a stale tail index used to reach the packed arena and throw.
        BeamOptions preserveOpening = batched with
        {
            BeamWidth = 200,
            PreserveReplayAllocatorOpening = true,
            MaxExpandedNodes = 200_000,
            UnlimitedNodes = true,
            SoftTimeBudget = TimeSpan.FromSeconds(10),
            EndedBatchTrigger = 1_000_000,
            EndedBatchTarget = 1_000_000,
        };
        BeamResult preserveResult = new BeamSolver(original.Rules, preserveOpening)
            .Solve(original.Battles, CancellationToken.None);
        Assert(preserveResult.ExpandedNodesTotal > 0 && preserveResult.BestRoute.Length > 0,
            "preserve-opening selection reads only the live rank prefix");
        _ = ReplayedRoute.Build(original.Battles[preserveResult.BestRootIndex], original.Rules,
            preserveResult.BestRoute, preserveResult.BestIsVictory);
        Console.WriteLine($"COMBAT_SEARCH preserve_opening_expanded={preserveResult.ExpandedNodesTotal} " +
            $"route={preserveResult.BestRoute.Length}");
        PotionStageContracts.Run(original, plan);
        Console.WriteLine($"COMBAT_SEARCH compaction comparison={compactionComparison} " +
            $"batched={first.BestIsVictory} uncompressed={uncompressed.BestIsVictory}");
        Console.WriteLine($"COMBAT_SEARCH real expanded={first.ExpandedNodesTotal} compactions={first.EndedCompactions} victory={first.BestIsVictory}");
    }
}
