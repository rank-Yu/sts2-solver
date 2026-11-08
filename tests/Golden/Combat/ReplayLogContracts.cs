using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// The combat-log replay reproduces a wall-clock search. A real-clock coordinator run with 1 ms
/// per width cuts its members on the deadline; replaying with the recorded expanded nodes and
/// transitions must select the same member and encode the same result, member by member.
/// Single-threaded: with parallel expansion a deadline that fires during candidate collection
/// cannot be located by counters, and the replay may differ.
/// </summary>
internal static class ReplayLogContracts
{
    private const string Fixture =
        "tests/Golden/data/d2/requests/c8737af7c32d597ccd647037d938e189ddb5d07b8de5d1d2b92ee7aa6de8601f.request.br";

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidDataException("replay-log: " + message);
    }

    internal static void Run()
    {
        SearchCase searchCase = SearchCodec.DecodeRequest(GoldenFiles.ReadCompressed(Fixture));
        SearchPlan plan = new(60, 40, 90, 30, 2_000_000_000)
        {
            MillisecondsPerWidth = 1,
            NodesPerWidth = 1024,
        };
        List<long> liveTransitions = [];
        ValueTask<SearchMemberMeasurement> RunLive(SearchCase member, SearchStorage storage,
            CancellationToken token)
        {
            BeamResult beam = new BeamSolver(member.Rules, member.Options with { SearchThreads = 1 })
                .Solve(member.Battles, storage, token);
            liveTransitions.Add(beam.Transitions);
            return ValueTask.FromResult(new SearchMemberMeasurement(beam, "ok", null, 0));
        }
        CombatSearchResult live = CombatSearchCoordinator.RunAsync(
                new CombatSearchRequest(searchCase, plan), new SearchStorage(), RunLive,
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        SearchMemberResult[] ran = [.. live.Members.Where(static member => member.Ran)];
        List<(long Expanded, long Transitions)?> caps = [];
        for (int index = 0; index < ran.Length; index++)
        {
            caps.Add(ReplayLogCommand.IsBudgetStop(ran[index].StopReason)
                ? (ran[index].ExpandedNodes, liveTransitions[index])
                : null);
        }
        int cuts = caps.Count(static cap => cap != null);
        Assert(cuts > 0, "1 ms per width cuts at least one member on the deadline");

        (CombatSearchResult replayed, List<long> expanded) = ReplayLogCommand.Replay(searchCase, plan, caps, 1);
        Assert(expanded.SequenceEqual(ran.Select(static member => member.ExpandedNodes)),
            $"member expanded nodes {string.Join(",", ran.Select(static m => m.ExpandedNodes))} "
            + $"replayed as {string.Join(",", expanded)}");
        Assert(replayed.SelectedMember == live.SelectedMember,
            $"selected member {live.SelectedMember} replayed as {replayed.SelectedMember}");
        Assert(live.Beam != null && replayed.Beam != null, "both runs select a result");
        Assert(SearchCodec.EncodeResult(live.Beam!).AsSpan().SequenceEqual(SearchCodec.EncodeResult(replayed.Beam!)),
            "selected result " + (SearchVerifier.FirstDifference(live.Beam!, replayed.Beam!) ?? "<bytes>"));
        Console.WriteLine($"REPLAY_LOG_CONTRACT members={ran.Length} budget_cuts={cuts} selected={live.SelectedMember}");
    }
}
