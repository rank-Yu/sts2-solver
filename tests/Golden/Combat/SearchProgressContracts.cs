using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Progress publication and adoption contracts. Each scenario runs with 1, 4 and 8 expansion
/// threads so the worker/commit split is exercised together with the search-thread-only publisher.
/// </summary>
internal static class SearchProgressContracts
{
    private static readonly int[] ThreadCounts = [1, 4, 8];

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("search-progress: " + message); }

    internal static void Run()
    {
        SearchCase original;
        using (FileStream input = File.OpenRead(
            "tests/Golden/data/d2/requests/6e0cc7806b41874f9203b30010b822a3b93c0b73fb93a7166a803bc61d271294.request.br"))
        using (BrotliStream compressed = new(input, CompressionMode.Decompress))
        using (MemoryStream bytes = new())
        {
            compressed.CopyTo(bytes);
            original = SearchCodec.DecodeRequest(bytes.ToArray());
        }
        BeamOptions baseline = SearchBudgetPolicy.ApplyRegression(original.Options with
        {
            BeamWidth = 8,
            MaxExpandedNodes = 4_000,
            SoftTimeBudget = TimeSpan.MaxValue,
            SearchThreads = 1,
        });
        foreach (int threads in ThreadCounts)
        {
            Adoption(original, baseline, threads);
            StopWithCandidate(original, baseline, threads);
            StopWithoutCandidate(original, baseline, threads);
            StaleEvicted(original, baseline, threads);
            SubscriptionDoesNotChangeResult(original, baseline, threads);
            CoordinatorAdoption(original, baseline, threads);
            BestCompleteSummary(original, baseline, threads);
            StopChoosesBetterCompleted(original, baseline, threads);
            TerminalVictoryPreview(original, baseline, threads);
            CompletedOnlyDeathVerdict(original, baseline, threads);
            FinalResultPublication(threads);
        }
        MixedMemberPreview();
        CounterPublication();
        Console.WriteLine("SEARCH_PROGRESS adoption/stop/stale/terminal/verdict/final/mixed/threads=pass");
    }

    private static void CounterPublication()
    {
        SearchProgressChannel channel = new(200);
        channel.BeginMember(0, default, null);
        channel.Publish(new SearchProgressSnapshot(0, default, 10, 20, null, null, null, 7));
        SearchProgressSnapshot initial = channel.Latest!;
        channel.AddCompletedExpandedNodes(30);
        channel.PublishCounters(40, 60, 100, 120);
        Assert(channel.Latest is { ExpandedNodes: 40, RequestExpandedNodes: 70,
            ElapsedMilliseconds: 60, CandidateVersion: 7, SearchMemoryUsedBytes: 100,
            SearchMemoryPeakBytes: 120 }, "heartbeat refreshes counters without changing candidate");
        Assert(ReferenceEquals(initial.Route, channel.Latest!.Route)
            && ReferenceEquals(initial.CurrentTurn, channel.Latest.CurrentTurn),
            "heartbeat keeps preview objects");
        channel.BeginMember(1, default, null);
        SearchProgressSnapshot previous = channel.Latest;
        channel.PublishCounters(1, 2, 3, 4);
        Assert(ReferenceEquals(previous, channel.Latest),
            "new member does not relabel the previous member preview");
    }

    private static void FinalResultPublication(int threads)
    {
        // One-action target stop and a longer frontier exhaustion from the frozen native corpus.
        foreach (string hash in new[]
        {
            "f0dd390422b700ac3dcf521532d0706045158417a30cc7beaa8c000a36b6b9fb",
            "6e0cc7806b41874f9203b30010b822a3b93c0b73fb93a7166a803bc61d271294",
        })
        foreach (bool audit in new[] { false, true })
        {
            SearchCase search = SearchCodec.DecodeRequest(GoldenFiles.ReadCompressed(
                $"tests/Golden/data/d2/requests/{hash}.request.br"));
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(search.Options with
            {
                SearchThreads = threads, SoftTimeBudget = TimeSpan.MaxValue,
                AuditPotionPolicy = audit, Progress = null,
            });
            BeamResult plain = new BeamSolver(search.Rules, options)
                .Solve(search.Battles, CancellationToken.None);
            // Suppress every ordinary publication after the opening snapshot.
            SearchProgressChannel channel = new(int.MaxValue);
            BeamResult result = new BeamSolver(search.Rules, options with { Progress = channel })
                .Solve(search.Battles, CancellationToken.None);
            Assert(SearchCodec.EncodeResult(plain).AsSpan()
                .SequenceEqual(SearchCodec.EncodeResult(result)),
                $"{threads}t final publication leaves result unchanged audit={audit}");
            Assert(result.BestRoute.Length > 0 && channel.Latest?.Route is not null,
                $"{threads}t final route is published despite throttle audit={audit}");
            SearchRoutePreview route = channel.Latest!.Route!;
            Assert(Equal(route.Actions, result.BestRoute)
                && route.RootIndex == result.BestRootIndex && route.Victory == result.BestIsVictory,
                $"{threads}t final preview is the returned route audit={audit}");
            BeamSolver quality = new(search.Rules, options);
            BeamSolver.Candidate candidate = quality.AssessResult(search.Battles, result,
                CancellationToken.None, static () => false, out _, out _, out _);
            Assert(BeamSolver.CompareRouteKeys(candidate.Key, route.RankKey) == 0,
                $"{threads}t final preview uses actual result quality audit={audit}");
            Assert(channel.FindCandidate(route.CandidateVersion) is not null
                && channel.Latest.RequestExpandedNodes == result.ExpandedNodesTotal,
                $"{threads}t final preview is replayable and has final counters");
        }
    }

    private static void MixedMemberPreview()
    {
        SearchProgressChannel channel = new(200);
        BeamSolver.RouteKey key = new(true, true, 0, 0, 8, 0, 0, 2, 8, 0, 0,
            0, 0, 0, 0, 1);
        int version = 0;
        SearchRoutePreview Publish(int member, int damage, int potionCost, bool victory = true)
        {
            int currentVersion = ++version;
            SearchRoutePreview route = new(currentVersion, 1, 2, 0, [PlayerAction.EndTurn()], [],
                victory, false, key with
                {
                    Won = victory, StrategicHpDeficit = damage, PotionHpCost = potionCost,
                });
            SearchCurrentTurnPreview current = new(currentVersion, 1, [], damage, 0, 0, 0, victory);
            SearchMember profile = new(200, SearchEvaluation.Standard, SearchRetention.Standard,
                potionCost > 0 ? SearchPotionLimit.Allowed : SearchPotionLimit.None);
            channel.BeginMember(member, profile, null);
            channel.Publish(new SearchProgressSnapshot(member, profile, 1, 1,
                route, current, null, currentVersion));
            return route;
        }
        _ = Publish(0, 8, 0);
        SearchRoutePreview better = Publish(1, 2, 3);
        _ = Publish(2, 7, 0);
        _ = Publish(3, 0, 10);
        _ = Publish(4, 0, 0, victory: false);
        // The main thread deliberately reads only after five members have published.
        Assert(ReferenceEquals(channel.Latest!.BestVictoryRoute, better),
            "missed publications retain the cheaper potion victory across members");
        Assert(channel.Latest.BestVictoryCurrentTurn?.CandidateVersion == better.CandidateVersion
            && channel.Latest.Route?.Victory == false && channel.Latest.MemberIndex == 4,
            "best route and turn stay paired while running-member metadata advances");
        SearchProgressSnapshot latest = channel.Latest;
        channel.Publish(latest with { Route = null, CurrentTurn = null });
        Assert(ReferenceEquals(channel.Latest!.BestVictoryRoute, better)
            && channel.Latest.BestVictoryCurrentTurn?.CandidateVersion == better.CandidateVersion,
            "an empty opening snapshot cannot erase the previous member's winning preview");
        SearchRoutePreview best = Publish(5, 4, 0);
        Assert(ReferenceEquals(channel.Latest.BestVictoryRoute, best),
            "a later better no-potion victory replaces the potion route");
    }

    private static BeamOptions ForThreads(BeamOptions baseline, int threads,
        SearchProgressChannel progress)
        => baseline with { SearchThreads = threads, Progress = progress };

    private static void Adoption(SearchCase original, BeamOptions baseline, int threads)
    {
        SearchProgressChannel channel = new(0);
        SearchRoutePreview? published = null;
        channel.Published = snapshot =>
        {
            if (published is not null || snapshot.Route is not { } route)
                return;
            ValidateSnapshot(snapshot, route, threads);
            published = route;
            Assert(channel.RequestAdopt(route.CandidateVersion),
                $"{threads}t adoption request is accepted");
        };
        BeamResult result = new BeamSolver(original.Rules, ForThreads(baseline, threads, channel))
            .Solve(original.Battles, CancellationToken.None);
        Assert(published is not null, $"{threads}t adoption publishes a candidate");
        Assert(result.Adoption is { Kind: SearchAdoptionKind.Requested },
            $"{threads}t adoption result is marked");
        Assert(result.Adoption!.CandidateVersion == published!.CandidateVersion
            && result.Adoption.ThroughTurn == published.ThroughTurn,
            $"{threads}t adoption keeps the candidate version and turn");
        Assert(Equal(result.BestRoute, published.Actions),
            $"{threads}t adopted route equals the published sequence");
        ReplayedRoute replay = ReplayedRoute.Build(original.Battles[result.BestRootIndex],
            original.Rules, result.BestRoute, result.BestIsVictory);
        Assert(replay.Count == published.Actions.Length,
            $"{threads}t adopted route replays from the root");
    }

    private static void StopWithCandidate(SearchCase original, BeamOptions baseline, int threads)
    {
        SearchProgressChannel channel = new(0);
        bool requested = false;
        channel.Published = snapshot =>
        {
            if (requested || snapshot.Route is null)
                return;
            requested = true;
            Assert(channel.RequestStop(), $"{threads}t stop request is accepted");
        };
        BeamResult result = new BeamSolver(original.Rules, ForThreads(baseline, threads, channel))
            .Solve(original.Battles, CancellationToken.None);
        Assert(requested, $"{threads}t stop waits for a published candidate");
        Assert(result.Adoption is { Kind: SearchAdoptionKind.Stopped },
            $"{threads}t stop preserves the latest candidate");
        Assert(!result.StopWithoutCandidate && result.BestRoute.Length > 0,
            $"{threads}t stop returns the candidate route");
        _ = ReplayedRoute.Build(original.Battles[result.BestRootIndex], original.Rules,
            result.BestRoute, result.BestIsVictory);
    }

    private static void StopWithoutCandidate(SearchCase original, BeamOptions baseline, int threads)
    {
        SearchProgressChannel channel = new(0);
        bool requested = false;
        channel.Published = snapshot =>
        {
            if (requested || snapshot.Route is not null)
                return;
            requested = true;
            Assert(channel.RequestStop(), $"{threads}t stop before a candidate is accepted");
        };
        BeamResult result = new BeamSolver(original.Rules, ForThreads(baseline, threads, channel))
            .Solve(original.Battles, CancellationToken.None);
        Assert(requested, $"{threads}t first snapshot has no candidate");
        Assert(result.StopWithoutCandidate && result.Adoption is null
            && result.BestRoute.Length == 0, $"{threads}t stop without a candidate cancels");
    }

    private static void StaleEvicted(SearchCase original, BeamOptions baseline, int threads)
    {
        SearchProgressChannel channel = new(0, retainedCandidates: 1);
        int firstVersion = 0;
        int routeCount = 0;
        channel.Published = snapshot =>
        {
            if (snapshot.Route is not { } route)
                return;
            routeCount++;
            if (routeCount == 1)
                firstVersion = route.CandidateVersion;
            else if (routeCount == 2)
                Assert(channel.RequestAdopt(firstVersion),
                    $"{threads}t stale request is accepted for the checkpoint");
        };
        bool rejected = false;
        try
        {
            _ = new BeamSolver(original.Rules, ForThreads(baseline, threads, channel))
                .Solve(original.Battles, CancellationToken.None);
        }
        catch (InvalidDataException error)
        {
            rejected = true;
            Assert(error.Message.Contains("过期"), $"{threads}t stale candidate is an explicit failure");
        }
        Assert(routeCount >= 2 && rejected, $"{threads}t evicted candidate version is rejected");
    }

    private static void SubscriptionDoesNotChangeResult(SearchCase original, BeamOptions baseline,
        int threads)
    {
        BeamResult plain = new BeamSolver(original.Rules, baseline with { SearchThreads = threads })
            .Solve(original.Battles, CancellationToken.None);
        SearchProgressChannel channel = new(0) { Published = static _ => { } };
        BeamResult subscribed = new BeamSolver(original.Rules,
            ForThreads(baseline, threads, channel)).Solve(original.Battles, CancellationToken.None);
        Assert(SearchCodec.EncodeResult(plain).AsSpan()
                .SequenceEqual(SearchCodec.EncodeResult(subscribed)),
            $"{threads}t subscription does not change the encoded result");
    }

    private static void ValidateSnapshot(SearchProgressSnapshot snapshot,
        SearchRoutePreview route, int threads)
    {
        Assert(snapshot.MemberIndex == 0 && snapshot.Member.Width > 0,
            $"{threads}t snapshot names the active member");
        Assert(snapshot.CandidateVersion == route.CandidateVersion
            && route.CandidateVersion > 0, $"{threads}t snapshot carries the candidate version");
        Assert(snapshot.ExpandedNodes >= 0 && snapshot.ElapsedMilliseconds >= 0,
            $"{threads}t snapshot carries counters");
        Assert(route.Actions.Length > 0 && route.RootIndex >= 0
            && route.ThroughTurn >= route.StartTurn, $"{threads}t route spans at least one action");
        Assert(route.Turns.Length > 0
            && route.Turns.Sum(static turn => turn.Actions.Length) == route.Actions.Length,
            $"{threads}t route turns partition the action sequence");
        Assert(route.ThroughTurn == route.Turns[^1].Turn,
            $"{threads}t route covers through its last turn");
        SearchProgressTurn start = route.Turns.First(turn => turn.Turn == route.StartTurn);
        Assert(snapshot.CurrentTurn is { } current
            && current.Turn == route.StartTurn
            && current.CandidateVersion == route.CandidateVersion
            && EqualPreview(current.Actions, start.Actions)
            && current.HpLost == start.HpLost
            && current.HpRecovered == start.HpRecovered
            && current.EnemyHpLost == start.EnemyHpLost
            && current.EnergyLeft == start.EnergyLeft
            && current.CombatEnded == start.CombatEnded,
            $"{threads}t current-turn preview is the route's first turn");
    }

    private static void CoordinatorAdoption(SearchCase original, BeamOptions baseline, int threads)
    {
        SearchPlan plan = new(8, 8, 8, 8, 2_576_980_377) { MillisecondsPerWidth = 1000 };
        SearchProgressChannel channel = new(0);
        bool requested = false;
        channel.Published = snapshot =>
        {
            if (requested || snapshot.Route is not { } route)
                return;
            requested = true;
            Assert(channel.RequestAdopt(route.CandidateVersion),
                $"{threads}t coordinator adoption request is accepted");
        };
        static ValueTask<SearchMemberMeasurement> RunMember(SearchCase member,
            SearchStorage shared, CancellationToken token)
            => ValueTask.FromResult(new SearchMemberMeasurement(
                new BeamSolver(member.Rules, member.Options).Solve(member.Battles, shared, token),
                "ok", null, 0));
        CombatSearchResult result = CombatSearchCoordinator.RunAsync(
            new(original with { Options = baseline with { SearchThreads = threads } }, plan)
            {
                Progress = channel,
            }, new SearchStorage(), RunMember, CancellationToken.None, static () => 0)
            .AsTask().GetAwaiter().GetResult();
        Assert(requested && result.Completion == SearchCompletionKind.Adopted
            && result.Adoption is { Kind: SearchAdoptionKind.Requested }
            && result.Beam is not null && result.Beam.Adoption is not null,
            $"{threads}t coordinator returns the adopted member result");
        _ = ReplayedRoute.Build(original.Battles[result.Beam!.BestRootIndex], original.Rules,
            result.Beam.BestRoute, result.Beam.BestIsVictory);
    }

    /// <summary>
    /// A victory found while the frontier is still undecided is the preview (CombatSolver publishes
    /// <c>retained ∪ completed</c>), it is not an only-death verdict, and adopting it returns that
    /// victory. Before POF1 the preview held frontier states only, so a won route never showed.
    /// </summary>
    private static void TerminalVictoryPreview(SearchCase original, BeamOptions baseline, int threads)
    {
        BeamResult plain = new BeamSolver(original.Rules, baseline with { SearchThreads = threads })
            .Solve(original.Battles, CancellationToken.None);
        Assert(plain.BestIsVictory, $"{threads}t the contract request is won");
        SearchProgressChannel channel = new(0);
        SearchRoutePreview? victory = null;
        channel.Published = snapshot =>
        {
            if (victory is not null || snapshot.Route is not { Victory: true } route)
                return;
            ValidateSnapshot(snapshot, route, threads);
            Assert(!route.OnlyDeathRoutes, $"{threads}t a victory preview is not an only-death verdict");
            Assert(route.Turns[^1].CombatEnded, $"{threads}t a victory preview ends the combat");
            victory = route;
            Assert(channel.RequestAdopt(route.CandidateVersion),
                $"{threads}t victory preview adoption is accepted");
        };
        BeamResult result = new BeamSolver(original.Rules, ForThreads(baseline, threads, channel))
            .Solve(original.Battles, CancellationToken.None);
        Assert(victory is not null, $"{threads}t a victory found during the search becomes the preview");
        Assert(result.Adoption is { Kind: SearchAdoptionKind.Requested } && result.BestIsVictory
            && Equal(result.BestRoute, victory!.Actions),
            $"{threads}t adopting the victory preview returns that victory");
        _ = ReplayedRoute.Build(original.Battles[result.BestRootIndex], original.Rules,
            result.BestRoute, result.BestIsVictory);
    }

    /// <summary>
    /// A completed request's only-death verdict is the selected route's: a won request is never
    /// reported as only-death, whatever the last member's last preview held.
    /// </summary>
    private static void CompletedOnlyDeathVerdict(SearchCase original, BeamOptions baseline, int threads)
    {
        SearchPlan plan = new(8, 8, 8, 8, 2_576_980_377) { MillisecondsPerWidth = 1000 };
        SearchProgressChannel channel = new(0) { Published = static _ => { } };
        static ValueTask<SearchMemberMeasurement> RunMember(SearchCase member,
            SearchStorage shared, CancellationToken token)
            => ValueTask.FromResult(new SearchMemberMeasurement(
                new BeamSolver(member.Rules, member.Options).Solve(member.Battles, shared, token),
                "ok", null, 0));
        CombatSearchResult result = CombatSearchCoordinator.RunAsync(
            new(original with { Options = baseline with { SearchThreads = threads } }, plan)
            {
                Progress = channel,
            }, new SearchStorage(), RunMember, CancellationToken.None, static () => 0)
            .AsTask().GetAwaiter().GetResult();
        Assert(result.Completion == SearchCompletionKind.Completed
            && result.Status == CombatSearchStatus.Victory && !result.OnlyDeathRoutes,
            $"{threads}t a won completed request is not an only-death verdict");
    }

    private static void StopChoosesBetterCompleted(SearchCase original, BeamOptions baseline,
        int threads)
    {
        BeamSolver quality = new(original.Rules,
            baseline with { SearchThreads = threads, Progress = null, AuditPotionPolicy = true });
        (BeamResult better, BeamResult worse) = FindTwoRoutes(original, quality);
        CombatSearchResult completedBetter = RunStop(original, baseline, threads, better, worse);
        Assert(completedBetter.Completion == SearchCompletionKind.StoppedWithCandidate
            && completedBetter.Beam is not null
            && Equal(completedBetter.Beam.BestRoute, better.BestRoute),
            $"{threads}t stop keeps the better completed member");
        Assert(completedBetter.SelectedMember == 0
            && completedBetter.Members[0].Adopted && !completedBetter.Members.Last(m => m.Ran).Adopted,
            $"{threads}t stop marks the substituted member");
        CombatSearchResult stopBetter = RunStop(original, baseline, threads, worse, better);
        Assert(stopBetter.Completion == SearchCompletionKind.StoppedWithCandidate
            && stopBetter.Beam is not null
            && Equal(stopBetter.Beam.BestRoute, better.BestRoute),
            $"{threads}t stop keeps the better current candidate");

        static CombatSearchResult RunStop(SearchCase original, BeamOptions baseline, int threads,
            BeamResult completed, BeamResult stopped)
        {
            SearchPlan plan = new(8, 8, 8, 8, 2_576_980_377) { MillisecondsPerWidth = 1000 };
            int call = 0;
            ValueTask<SearchMemberMeasurement> RunMember(SearchCase member,
                SearchStorage shared, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (call++ == 0)
                    return ValueTask.FromResult(new SearchMemberMeasurement(completed, "ok", null, 0));
                BeamResult stoppedBeam = stopped with
                {
                    StopReason = "stopped",
                    Adoption = new SearchAdoptionInfo(1, 0, SearchAdoptionKind.Stopped,
                        Outcome.None),
                };
                return ValueTask.FromResult(new SearchMemberMeasurement(stoppedBeam, "stopped",
                    null, 0));
            }
            return CombatSearchCoordinator.RunAsync(
                new(original with { Options = baseline with { SearchThreads = threads } }, plan),
                new SearchStorage(), RunMember, CancellationToken.None, static () => 0)
                .AsTask().GetAwaiter().GetResult();
        }
    }

    private static (BeamResult Better, BeamResult Worse) FindTwoRoutes(SearchCase original,
        BeamSolver quality)
    {
        List<BeamResult> routes = [];
        if (TryRoute(original, []) is { } empty)
            routes.Add(empty);
        if (TryRoute(original, [PlayerAction.EndTurn()]) is { } endTurn)
            routes.Add(endTurn);
        PlayerAction[] legal = new PlayerAction[Kernel.MaxLegalActions];
        int count = Kernel.LegalActions(original.Battles[0], original.Rules, legal);
        for (int index = 0; index < count; index++)
        {
            if (legal[index].Kind == PlayerActionKind.UsePotion)
                continue;
            if (TryRoute(original, [legal[index]]) is { } single)
                routes.Add(single);
            if (TryRoute(original, [legal[index], PlayerAction.EndTurn()]) is { } withEnd)
                routes.Add(withEnd);
        }
        for (int left = 0; left < routes.Count; left++)
            for (int right = left + 1; right < routes.Count; right++)
            {
                int comparison = quality.CompareResultQuality(original.Battles,
                    routes[left], routes[right]);
                if (comparison == 0)
                    continue;
                return comparison < 0
                    ? (routes[left], routes[right])
                    : (routes[right], routes[left]);
            }
        throw new InvalidDataException("search-progress: no two routes with different final ordering");
    }

    private static BeamResult? TryRoute(SearchCase original, PlayerAction[] route)
    {
        Battle state = original.Battles[0];
        try
        {
            foreach (PlayerAction action in route)
            {
                if (Kernel.Step(ref state, action, original.Rules, out _) != StepResult.Completed)
                    return null;
            }
        }
        catch (CapacityException)
        {
            return null;
        }
        return new BeamResult(route, state.Outcome == Outcome.Victory, 0d, 0, 0, 0,
            route.Length, 0, TimeSpan.Zero)
        {
            BestRootIndex = 0,
            PotionPolicySatisfied = true,
            StopReason = "ok",
        };
    }

    private static void BestCompleteSummary(SearchCase original, BeamOptions baseline, int threads)
    {
        SearchProgressChannel channel = new(0);
        SearchInterimResult summary = new(0, true, 4, 1, 6, 1.25, 2);
        channel.BeginMember(3, new SearchMember(8, SearchEvaluation.Standard,
            SearchRetention.Standard, SearchPotionLimit.None), summary);
        bool seen = false;
        channel.Published = snapshot =>
        {
            if (ReferenceEquals(snapshot.BestCompleteResult, summary))
                seen = true;
        };
        _ = new BeamSolver(original.Rules, ForThreads(baseline, threads, channel))
            .Solve(original.Battles, CancellationToken.None);
        Assert(seen, $"{threads}t snapshot carries the coordinator's best complete victory");
    }

    private static bool EqualPreview(SearchProgressAction[] left, SearchProgressAction[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            SearchProgressAction a = left[index];
            SearchProgressAction b = right[index];
            if (a.Kind != b.Kind || a.CardId != b.CardId || a.Upgrade != b.Upgrade
                || a.PotionId != b.PotionId || a.CardType != b.CardType
                || a.TargetKind != b.TargetKind || a.TargetCombatId != b.TargetCombatId
                || a.TargetMonsterId != b.TargetMonsterId || a.TargetOrdinal != b.TargetOrdinal
                || a.HpBefore != b.HpBefore || a.HpAfter != b.HpAfter
                || a.EnergyBefore != b.EnergyBefore || a.EnergyAfter != b.EnergyAfter
                || a.ReplayCount != b.ReplayCount || a.Kills.Length != b.Kills.Length)
                return false;
            for (int kill = 0; kill < a.Kills.Length; kill++)
            {
                if (a.Kills[kill] != b.Kills[kill])
                    return false;
            }
        }
        return true;
    }

    private static bool Equal(PlayerAction[] left, PlayerAction[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
            if (!GoldenFiles.ActionEquals(left[index], right[index]))
                return false;
        return true;
    }
}
