using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Quality;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class SearchContracts
{
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("combat-search: " + message); }

    private static void CheckUnifiedMemory(SearchCase original, BeamOptions basis)
    {
        foreach (long limit in new[] { 8192L, 12288L })
        {
            SearchStorage arrays = new();
            arrays.BeginSolve(limit);
            int[] values = arrays.RentArray<int>(1024);
            values[0] = 73;
            bool rejected = false;
            try { arrays.ResizeArray(ref values, 2048); }
            catch (SearchMemoryBudgetException) { rejected = true; }
            Assert(rejected == (limit == 8192), "growth budgets old and new arrays together");
            Assert(values[0] == 73 && values.Length == (rejected ? 1024 : 2048),
                "rejected growth preserves the existing array");
            Assert(arrays.Memory.UsedBytes == values.Length * sizeof(int)
                && arrays.Memory.PeakBytes <= limit, "replaced storage is released after the copy");
        }
        BeamResult? single = null;
        long requestBytes = 0;
        foreach (int threads in new[] { 1, 8 })
        {
            SearchStorage storage = new();
            BeamOptions options = basis with
            {
                SearchThreads = threads, MaxExpandedNodes = 64,
                SearchMemoryBudgetBytes = 512L << 20,
            };
            BeamResult result = new BeamSolver(original.Rules, options)
                .Solve(original.Battles, storage, CancellationToken.None);
            var regions = storage.Memory.AllocatedByRegion;
            Assert(regions.Values.Sum() == storage.Memory.UsedBytes, "all regions share one ledger");
            Assert(regions["workspaces"] >= result.SearchWorkspacePeakBytes,
                "workspace reservation covers all live buffers");
            Assert(regions["threads"] == threads * (long)SearchMemoryBudget.ThreadStackReserveBytes,
                "worker stacks come from the configured budget");
            Assert(regions["runtime"] > 0 && regions["route-output"] > 0,
                "runtime and route publication are reserved");
            Assert(result.SearchMemoryPeakBytes <= options.SearchMemoryBudgetBytes,
                "all reservations and pools stay within one ceiling");
            if (threads == 1)
            {
                single = result;
                requestBytes = regions["request"];
            }
            else
            {
                Assert(regions["request"] == requestBytes, "shared root/rules are not counted per worker");
                Assert(regions["workspaces"] >= 16L * BeamSolver.DefaultWorkspaceBufferLimitBytes,
                    "eight threads' sixteen buffers are inside the total");
                Assert(single != null && SearchCodec.EncodeResult(single).AsSpan()
                    .SequenceEqual(SearchCodec.EncodeResult(result)), "ample-memory routes are thread invariant");
            }
            long previous = 0;
            for (int member = 0; member < 2; member++)
            {
                BeamResult repeated = new BeamSolver(original.Rules, options)
                    .Solve(original.Battles, storage, CancellationToken.None);
                Assert(SearchCodec.EncodeResult(result).AsSpan().SequenceEqual(SearchCodec.EncodeResult(repeated)),
                    "storage reuse preserves the selected route");
                if (member > 0)
                    Assert(storage.Memory.UsedBytes == previous, "member reservations do not accumulate");
                previous = storage.Memory.UsedBytes;
            }
            // A budget that could formerly fit the pools must reject before starting workers when
            // it cannot also hold their buffers and stacks.
            if (threads == 8)
            {
                bool rejected = false;
                try
                {
                    _ = new BeamSolver(original.Rules, options with { SearchMemoryBudgetBytes = 64L << 20 })
                        .Solve(original.Battles, CancellationToken.None);
                }
                catch (SearchMemoryBudgetException) { rejected = true; }
                Assert(rejected, "workers cannot allocate outside an insufficient total budget");
            }
        }
    }

    private static void CheckChunkedExpansion()
    {
        int chunks = 0;
        foreach (string root in new[] { "qf3-decisions-repeat", "qf-nightmare-detached",
            "dx7-droplet-fetched-power", "quality-NECROBINDER-001" })
        {
            SearchCase fixture = GoldenFixtures.Search(root);
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(fixture.Options) with
            {
                MaxExpandedNodes = 256, NodeBudgetActive = true, UnlimitedNodes = true,
                SoftTimeBudget = TimeSpan.MaxValue, SearchMemoryBudgetBytes = 512L << 20,
                SearchThreads = 1,
            };
            byte[] expected = SearchCodec.EncodeResult(new BeamSolver(fixture.Rules, options)
                .Solve(fixture.Battles, CancellationToken.None));
            foreach (int threads in new[] { 2, 3, 8 })
            {
                Task<BeamResult> task = Task.Run(() => new BeamSolver(fixture.Rules,
                    options with { SearchThreads = threads, WorkspaceBufferLimitBytes = 64 << 10 })
                    .Solve(fixture.Battles, CancellationToken.None));
                Assert(task.Wait(TimeSpan.FromSeconds(60)), "chunk consumer/producer must make progress");
                BeamResult result = task.Result;
                Assert(expected.AsSpan().SequenceEqual(SearchCodec.EncodeResult(result)),
                    $"{root}: chunk boundaries preserve choices, fetched successors and ordered commits");
                Assert(result.WorkspaceOverflowParents == 0, "full chunks never restart their parent");
                Assert(result.SearchMemoryPeakBytes <= options.SearchMemoryBudgetBytes,
                    "chunk continuation storage stays inside the shared budget");
                chunks += result.WorkspaceChunks;
            }
        }
        Assert(chunks > 0, "small buffers actually exercise continuation across chunks");
        Console.WriteLine($"COMBAT_SEARCH chunks={chunks}");
        SearchCase stopping = GoldenFixtures.Search("qf3-decisions-repeat");
        foreach (string stop in new[] { "cancel", "deadline", "failure" })
        {
            using CancellationTokenSource cancellation = new();
            int expired = 0, parents = 0;
            InvalidOperationException injected = new("chunk consumer failure contract");
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(stopping.Options) with
            {
                SearchThreads = 3, WorkspaceBufferLimitBytes = 64 << 10,
                SearchMemoryBudgetBytes = 512L << 20,
                UnlimitedNodes = true, NodeBudgetActive = true, MaxExpandedNodes = 10000,
                SoftTimeBudget = TimeSpan.MaxValue, DeadlineExpired = () => Volatile.Read(ref expired) != 0,
            };
            BeamSolver.ParentWorkspaceObserver = _ =>
            {
                if (++parents != 5) return;
                if (stop == "cancel") cancellation.Cancel();
                else if (stop == "deadline") Volatile.Write(ref expired, 1);
                else throw injected;
            };
            try
            {
                Task<bool> task = Task.Run(() =>
                {
                    try
                    {
                        BeamResult result = new BeamSolver(stopping.Rules, options)
                            .Solve(stopping.Battles, cancellation.Token);
                        return stop == "deadline" && result.DeadlineExpiredAtPoll > 0;
                    }
                    catch (OperationCanceledException)
                    { return stop == "cancel" && cancellation.IsCancellationRequested; }
                    catch (InvalidOperationException error)
                    { return stop == "failure" && ReferenceEquals(error, injected); }
                });
                Assert(task.Wait(TimeSpan.FromSeconds(60)) && task.Result && parents >= 5,
                    $"{stop}: stopping the consumer releases and joins all paused producers");
            }
            finally { BeamSolver.ParentWorkspaceObserver = null; }
        }
        Console.WriteLine("COMBAT_SEARCH chunk_stop=cancel/deadline/failure");

    }

    internal static void Run()
    {
        SearchCase original = GoldenFixtures.Search("quality-NECROBINDER-001");
        SearchPlan plan = new(12, 8, 16, 5, 100_000_000) { MillisecondsPerWidth = 125 };
        Assert(SearchCodec.EncodeRequest(original).AsSpan()
            .SequenceEqual(SearchCodec.EncodeRequest(SearchCodec.DecodeRequest(SearchCodec.EncodeRequest(original)))),
            "request round trip");
        // The starting pruning bound a coordinator hands a member is the optional INC1 extension:
        // absent from every frozen request, carried exactly when set.
        Assert(original.Options.IncumbentSeed is null, "frozen request has no incumbent seed");
        SearchIncumbent seed = new(23, 4, 3, 1, GoalsComplete: false);
        byte[] seeded = SearchCodec.EncodeRequest(original with
        {
            Options = original.Options with { IncumbentSeed = seed },
        });
        Assert(SearchCodec.DecodeRequest(seeded).Options.IncumbentSeed == seed
            && SearchCodec.EncodeRequest(SearchCodec.DecodeRequest(seeded)).AsSpan().SequenceEqual(seeded)
            && !seeded.AsSpan().SequenceEqual(SearchCodec.EncodeRequest(original)),
            "incumbent seed survives SREQ");

        foreach (VictoryStopTarget target in new[] { new VictoryStopTarget(-7, -12), VictoryStopTarget.Unknown })
        {
            byte[] encoded = SearchCodec.EncodeRequest(original with
            {
                Options = original.Options with { VictoryTarget = target },
            });
            SearchCase decoded = SearchCodec.DecodeRequest(encoded);
            Assert(decoded.Options.VictoryTarget == target
                && SearchCodec.EncodeRequest(decoded).AsSpan().SequenceEqual(encoded),
                "known and unknown shared victory targets survive SREQ");
        }

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
        SearchMemberResult nextRun = validated.Members.Skip(1).First(m => m.Ran);
        Assert(validated.Members[0].StopReason == "deadline" && !validated.Members[0].Adopted
            && nextRun.HpLost.HasValue && nextRun.StopReason != "deadline",
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
        CheckUnifiedMemory(original, legacy);
        CheckChunkedExpansion();
        BeamResult largePool = new BeamSolver(original.Rules, batched with
        {
            MaxExpandedNodes = 1, StatePoolBudgetBytes = 3_000_000_000,
        }).Solve(original.Battles, CancellationToken.None);
        Assert(largePool.ExpandedNodesTotal > 0, "actual beam supports long pool budget above 2 GiB");

        Assert(first.BestRoute.All(a => a.Kind != PlayerActionKind.UsePotion), "fixture has a potion-free route");
        bool fallback = false;
        for (long limit = 1_000_000; limit <= 64_000_000 && !fallback; limit += 250_000)
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
