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
            "tests/Golden/data/d2/requests/191031ddb26d3e8e2b820fdd1e567a47e5ff0225107a25c0d77fede8f76b32dd.request.br"))
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
            && Equal(current.Actions, start.Actions)
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
