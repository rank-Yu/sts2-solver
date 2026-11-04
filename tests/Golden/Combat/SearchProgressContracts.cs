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
            "tests/Golden/data/d2/requests/21fa57140c860d0758e752c80e115b1385cfc2c5eab2252cc29dd6fa97f50b40.request.br"))
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
        }
        Console.WriteLine("SEARCH_PROGRESS adoption/stop/stale/threads=pass");
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
            && completedBetter.Members[0].Adopted && !completedBetter.Members[1].Adopted,
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
        return new BeamResult(route, [], state.Outcome == Outcome.Victory, 0d, 0, 0, 0,
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
