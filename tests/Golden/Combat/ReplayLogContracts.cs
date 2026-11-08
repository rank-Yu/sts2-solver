using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// The combat-log replay reproduces a wall-clock search. A real-clock coordinator run with 1 ms
/// per width cuts its members on the deadline; replaying with the recorded deadline polls must expand the
/// same nodes, select the same member and encode the same result. Single-threaded: the polls of a
/// parallel solve interleave, so its replay may differ.
/// </summary>
internal static class ReplayLogContracts
{
    private const string Fixture =
        "tests/Golden/data/d2/requests/18fa46c9cb20c477d67d2e4090053db081b3f6513f9352fdae85cfbc3bc5bafd.request.br";

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
        List<long> livePolls = [];
        ValueTask<SearchMemberMeasurement> RunLive(SearchCase member, SearchStorage storage,
            CancellationToken token)
        {
            BeamResult beam = new BeamSolver(member.Rules, member.Options with { SearchThreads = 1 })
                .Solve(member.Battles, storage, token);
            livePolls.Add(beam.DeadlineExpiredAtPoll);
            return ValueTask.FromResult(new SearchMemberMeasurement(beam, "ok", null, 0));
        }
        CombatSearchResult live = CombatSearchCoordinator.RunAsync(
                new CombatSearchRequest(searchCase, plan), new SearchStorage(), RunLive,
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        SearchMemberResult[] ran = [.. live.Members.Where(static member => member.Ran)];
        int cuts = livePolls.Count(static poll => poll > 0);
        Assert(cuts > 0, "1 ms per width cuts at least one member on the deadline");

        (CombatSearchResult replayed, List<long> expanded) = ReplayLogCommand.Replay(searchCase, plan, livePolls, 1);
        Assert(expanded.SequenceEqual(ran.Select(static member => member.ExpandedNodes)),
            $"member expanded nodes {string.Join(",", ran.Select(static m => m.ExpandedNodes))} "
            + $"replayed as {string.Join(",", expanded)}");
        Assert(replayed.SelectedMember == live.SelectedMember,
            $"selected member {live.SelectedMember} replayed as {replayed.SelectedMember}");
        Assert(live.Beam != null && replayed.Beam != null, "both runs select a result");
        Assert(SearchCodec.EncodeResult(live.Beam!).AsSpan().SequenceEqual(SearchCodec.EncodeResult(replayed.Beam!)),
            "selected result " + (SearchVerifier.FirstDifference(live.Beam!, replayed.Beam!) ?? "<bytes>"));
        Console.WriteLine($"REPLAY_LOG_CONTRACT members={ran.Length} deadline_cuts={cuts} selected={live.SelectedMember}");
    }
}
