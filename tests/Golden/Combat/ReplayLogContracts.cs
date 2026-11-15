using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// A controlled coordinator clock reaches each member's deadline at a fixed solver poll.
/// Replaying the recorded polls must reproduce both an early stop without a candidate and a
/// selected result, independent of machine speed. Single-threaded: parallel polls interleave.
/// </summary>
internal static class ReplayLogContracts
{

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidDataException("replay-log: " + message);
    }

    internal static void Run()
    {
        SearchCase searchCase = GoldenFixtures.Search("quality-NECROBINDER-001");
        SearchPlan plan = new(60, 40, 90, 30, 2_000_000_000)
        {
            MillisecondsPerWidth = 1,
            NodesPerWidth = 1024,
        };
        Check(searchCase, plan, deadlinePoll: 1, expectResult: false);
        Check(searchCase, plan, deadlinePoll: 20_000, expectResult: true);
    }

    private static void Check(SearchCase searchCase, SearchPlan plan, long deadlinePoll, bool expectResult)
    {
        long now = 0;
        long memberDeadline = 0;
        BeamSolver? active = null;
        long ReadClock()
        {
            if (active != null && active.DeadlinePolls >= deadlinePoll)
                now = memberDeadline;
            return now;
        }

        List<long> livePolls = [];
        ValueTask<SearchMemberMeasurement> RunLive(SearchCase member, SearchStorage storage,
            CancellationToken token)
        {
            memberDeadline = checked(now + (long)member.Options.SoftTimeBudget.TotalMilliseconds);
            active = new BeamSolver(member.Rules, member.Options with { SearchThreads = 1 });
            BeamResult beam = active.Solve(member.Battles, storage, token);
            now = ReadClock();
            active = null;
            livePolls.Add(beam.DeadlineExpiredAtPoll);
            return ValueTask.FromResult(new SearchMemberMeasurement(beam, "ok", null, 0));
        }
        CombatSearchResult live = CombatSearchCoordinator.RunAsync(
                new CombatSearchRequest(searchCase, plan), new SearchStorage(), RunLive,
                CancellationToken.None, clock: ReadClock)
            .AsTask().GetAwaiter().GetResult();

        SearchMemberResult[] ran = [.. live.Members.Where(static member => member.Ran)];
        int cuts = livePolls.Count(static poll => poll > 0);
        Assert(cuts > 0, "the controlled clock cuts at least one member on the deadline");
        Assert(livePolls.All(poll => poll == 0 || poll == deadlinePoll),
            "each deadline is recorded at the scheduled poll");

        (CombatSearchResult replayed, List<long> expanded) = ReplayLogCommand.Replay(searchCase, plan, livePolls, 1);
        Assert(expanded.SequenceEqual(ran.Select(static member => member.ExpandedNodes)),
            $"member expanded nodes {string.Join(",", ran.Select(static m => m.ExpandedNodes))} "
            + $"replayed as {string.Join(",", expanded)}");
        Assert(replayed.SelectedMember == live.SelectedMember,
            $"selected member {live.SelectedMember} replayed as {replayed.SelectedMember}");
        Assert((live.Beam != null) == expectResult && (replayed.Beam != null) == expectResult,
            $"both runs have result={expectResult}");
        Assert(live.Status == replayed.Status && live.Completion == replayed.Completion,
            "search status and completion match");
        if (expectResult)
        {
            Assert(ran.Sum(static member => member.ExpandedNodes) > 0, "search expands before the deadline");
            Assert(SearchCodec.EncodeResult(live.Beam!).AsSpan().SequenceEqual(SearchCodec.EncodeResult(replayed.Beam!)),
                "selected result " + (SearchVerifier.FirstDifference(live.Beam!, replayed.Beam!) ?? "<bytes>"));
        }
        else
        {
            Assert(ran.All(static member => member.ExpandedNodes == 0) && live.SelectedMember == -1,
                "the first deadline poll stops before expansion without selecting a member");
        }
        Console.WriteLine($"REPLAY_LOG_CONTRACT poll={deadlinePoll} members={ran.Length} "
            + $"deadline_cuts={cuts} selected={live.SelectedMember} result={expectResult}");
    }
}
