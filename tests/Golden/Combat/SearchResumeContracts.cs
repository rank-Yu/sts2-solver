using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Coordinator stop/resume contracts. A deterministic fake member runner proves that stopping at
/// member k and continuing afterwards produces the same route as one full run in which member k is
/// replaced by the result it had when it stopped. The fake never runs a real search, so the
/// comparison isolates the coordinator's bookkeeping.
/// </summary>
internal static class SearchResumeContracts
{
    private const string PlainRoot =
        "cb3f5c80fc3dca8240911e4fee6678465a959c60d6572ed71e3bf0ad776f910e";
    private const string PotionRoot = "79ba7974d0e4";

    private static readonly SearchPlan Plan =
        new(8, 8, 8, 8, 2_576_980_377) { MillisecondsPerWidth = 1000 };

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("search-resume: " + message); }

    internal static void Run()
    {
        SearchCase plain = LoadRequest(PlainRoot + ".request.br");
        SearchCase potions = LoadForcedCase("FIRE_POTION");
        ResumeKeepsStoppedResult(plain);
        ResumeAcceptsBetterRemainingMember(plain);
        ResumeAfterSecondStop(plain);
        TwoPassStopInFirstPass(potions);
        StopAtLastMemberHasNoResume(potions);
        StopWithoutCandidateAdoptsCompleted(plain);
        StopWithoutCandidateWithoutCompletedCancels(plain);
        RequestedAdoptionHasNoResume(plain);
        Console.WriteLine("SEARCH_RESUME stop/middle/last/two-pass/carried/without-candidate=pass");
    }

    private static void ResumeKeepsStoppedResult(SearchCase original)
    {
        BeamSolver quality = new(original.Rules, original.Options with { AuditPotionPolicy = true });
        (BeamResult better, BeamResult worse) = FindTwoRoutes(original, quality);
        const int stopIndex = 1;
        int count = MemberCount(original);
        Assert(count > stopIndex + 1, "plan has a middle member to stop at");

        Script stopScript = new() { Default = BeamStep(worse) };
        stopScript.Set((0, 0), BeamStep(worse));
        stopScript.Set((0, stopIndex), StoppedStep(better));
        SearchProgressChannel stopChannel = new(0);
        FakeRunner stopRunner = new(stopChannel, Plan, stopScript);
        CombatSearchResult stopped = Run(original, stopRunner, stopChannel);
        Assert(stopRunner.Calls.Select(static call => call.Index)
                .SequenceEqual(Enumerable.Range(0, stopIndex + 1)),
            "stop runs members 0..k once");
        Assert(stopped.Completion == SearchCompletionKind.StoppedWithCandidate,
            stopped.Completion.ToString());
        SearchResumeState resume = stopped.Resume
            ?? throw new InvalidDataException("search-resume: stop did not carry a resume point");
        Assert(resume.Pass == 0 && resume.NextMemberIndex == stopIndex + 1
            && resume.StoppedIndex == stopIndex && resume.Members.Length == stopIndex + 1,
            "stop carries pass, next member and member logs");
        Assert(stopped.Beam is not null && Equal(stopped.Beam.BestRoute, better.BestRoute),
            "stop adopts the better stopped candidate");
        Assert(stopped.Adoption is { Kind: SearchAdoptionKind.Stopped }, "stop adoption kind");
        long stopPlanned = stopped.PlannedBudgetMilliseconds;
        long stopSpent = stopRunner.Calls.Sum(static call => call.Configured);

        Script resumeScript = new() { Default = BeamStep(worse) };
        SearchProgressChannel resumeChannel = new(0);
        FakeRunner resumeRunner = new(resumeChannel, Plan, resumeScript);
        CombatSearchResult resumed = Run(original, resumeRunner, resumeChannel, resume);
        Assert(resumeRunner.Calls.All(static call => call.Index > stopIndex),
            "resume never reruns the stopped member");
        for (int call = 1; call < resumeRunner.Calls.Count; call++)
            Assert(resumeRunner.Calls[call - 1].Index < resumeRunner.Calls[call].Index,
                "resume runs the remaining members in order");
        int[] expectedRuns = [.. Enumerable.Range(stopIndex + 1, count - stopIndex - 1)
            .Where(index => resumed.Members[index].Ran)];
        Assert(resumeRunner.Calls.Select(static call => call.Index).SequenceEqual(expectedRuns)
            && resumed.Members.Length == count
            && resumed.Members.Skip(stopIndex + 1)
                .All(static member => member.Ran || member.SkippedReason != null),
            "resume processes every remaining member and only reruns the reported ones");
        Assert(resumed.Completion == SearchCompletionKind.Completed, resumed.Completion.ToString());
        Assert(resumed.ResumeMerge is { Compared: true, KeptStopped: true, Reason: "stopped_not_worse" },
            resumed.ResumeMerge?.ToString() ?? "-");
        Assert(resumed.SelectedMember == stopIndex
            && resumed.Beam is not null && Equal(resumed.Beam.BestRoute, better.BestRoute),
            "resume keeps the stop-time route and marks its member");
        long resumePlanned = resumed.PlannedBudgetMilliseconds;
        Assert(resumePlanned == resumeRunner.Calls.Sum(static call => call.Configured),
            "resumed planned time is the remaining member sum");
        Assert(stopPlanned == stopSpent + resumePlanned,
            $"planned time {stopPlanned} = spent {stopSpent} + remaining {resumePlanned}");
        Assert(resumeChannel.PlannedBudgetMilliseconds == resumePlanned,
            "progress reports the remaining planned time");

        Script fullScript = new() { Default = BeamStep(worse) };
        fullScript.Set((0, 0), BeamStep(worse));
        fullScript.Set((0, stopIndex), BeamStep(better));
        SearchProgressChannel fullChannel = new(0);
        FakeRunner fullRunner = new(fullChannel, Plan, fullScript);
        CombatSearchResult full = Run(original, fullRunner, fullChannel);
        Assert(full.Completion == SearchCompletionKind.Completed, full.Completion.ToString());
        Assert(full.Beam is not null && resumed.Beam is not null
            && Equal(full.Beam.BestRoute, resumed.Beam.BestRoute),
            "resume equals the full run with the stopped result substituted");
        Assert(full.SelectedMember == resumed.SelectedMember, "both runs adopt the same member");
    }

    private static void ResumeAcceptsBetterRemainingMember(SearchCase original)
    {
        BeamSolver quality = new(original.Rules, original.Options with { AuditPotionPolicy = true });
        (BeamResult better, BeamResult worse) = FindTwoRoutes(original, quality);
        const int stopIndex = 1;
        const int betterIndex = 3;

        Script stopScript = new() { Default = BeamStep(worse) };
        stopScript.Set((0, stopIndex), StoppedStep(worse));
        SearchProgressChannel stopChannel = new(0);
        FakeRunner stopRunner = new(stopChannel, Plan, stopScript);
        CombatSearchResult stopped = Run(original, stopRunner, stopChannel);
        SearchResumeState resume = stopped.Resume
            ?? throw new InvalidDataException("search-resume: stop did not carry a resume point");
        Assert(stopped.Beam is not null && Equal(stopped.Beam.BestRoute, worse.BestRoute),
            "stop adopts the stopped candidate");

        Script resumeScript = new() { Default = BeamStep(worse) };
        resumeScript.Set((0, betterIndex), BeamStep(better));
        SearchProgressChannel resumeChannel = new(0);
        FakeRunner resumeRunner = new(resumeChannel, Plan, resumeScript);
        CombatSearchResult resumed = Run(original, resumeRunner, resumeChannel, resume);
        Assert(resumed.ResumeMerge is { Compared: true, KeptStopped: false, Reason: "completed_better" },
            resumed.ResumeMerge?.ToString() ?? "-");
        Assert(resumed.SelectedMember == betterIndex
            && resumed.Beam is not null && Equal(resumed.Beam.BestRoute, better.BestRoute),
            "a better remaining member replaces the stop-time route");

        Script fullScript = new() { Default = BeamStep(worse) };
        fullScript.Set((0, stopIndex), BeamStep(worse));
        fullScript.Set((0, betterIndex), BeamStep(better));
        SearchProgressChannel fullChannel = new(0);
        FakeRunner fullRunner = new(fullChannel, Plan, fullScript);
        CombatSearchResult full = Run(original, fullRunner, fullChannel);
        Assert(full.Beam is not null && resumed.Beam is not null
            && Equal(full.Beam.BestRoute, resumed.Beam.BestRoute),
            "improving resume equals the substituted full run");
        Assert(full.SelectedMember == resumed.SelectedMember, "both runs adopt the better member");
    }

    private static void ResumeAfterSecondStop(SearchCase original)
    {
        BeamSolver quality = new(original.Rules, original.Options with { AuditPotionPolicy = true });
        (BeamResult better, BeamResult worse) = FindTwoRoutes(original, quality);

        Script firstScript = new() { Default = BeamStep(worse) };
        firstScript.Set((0, 1), StoppedStep(better));
        SearchProgressChannel firstChannel = new(0);
        FakeRunner firstRunner = new(firstChannel, Plan, firstScript);
        CombatSearchResult first = Run(original, firstRunner, firstChannel);
        SearchResumeState resume = first.Resume
            ?? throw new InvalidDataException("search-resume: first stop did not carry a resume point");
        Assert(resume.NextMemberIndex == 2, "first stop at member 1");

        Script secondScript = new() { Default = BeamStep(worse) };
        secondScript.Set((0, 4), StoppedStep(worse));
        SearchProgressChannel secondChannel = new(0);
        FakeRunner secondRunner = new(secondChannel, Plan, secondScript);
        CombatSearchResult second = Run(original, secondRunner, secondChannel, resume);
        SearchResumeState secondResume = second.Resume
            ?? throw new InvalidDataException("search-resume: second stop did not carry a resume point");
        Assert(secondResume.NextMemberIndex == 5 && secondResume.StoppedIndex == 1,
            "second stop resumes after its member and keeps the better earlier route");
        Assert(second.Beam is not null && Equal(second.Beam.BestRoute, better.BestRoute),
            "second stop keeps the better earlier route");
        Assert(secondRunner.Calls.All(static call => call.Index is > 1 and <= 4),
            "second stop only ran the members between the stops");

        Script thirdScript = new() { Default = BeamStep(worse) };
        SearchProgressChannel thirdChannel = new(0);
        FakeRunner thirdRunner = new(thirdChannel, Plan, thirdScript);
        CombatSearchResult third = Run(original, thirdRunner, thirdChannel, secondResume);
        Assert(thirdRunner.Calls.All(static call => call.Index > 4),
            "third request starts after the second stopped member");
        Assert(third.Completion == SearchCompletionKind.Completed, third.Completion.ToString());
        Assert(third.Beam is not null && Equal(third.Beam.BestRoute, better.BestRoute),
            "final route keeps the best stop-time result");
    }

    private static void TwoPassStopInFirstPass(SearchCase original)
    {
        BeamSolver quality = new(original.Rules, original.Options with { AuditPotionPolicy = true });
        BeamResult forced = ForcedPotionRoute(original, quality);
        BeamResult other = OtherForcedPotionRoute(original, quality, forced);
        int count = MemberCount(original);
        const int stopIndex = 1;

        Script stopScript = new() { Default = FakeStep.None };
        stopScript.Set((0, 0), BeamStep(forced));
        stopScript.Set((0, stopIndex), StoppedStep(forced));
        SearchProgressChannel stopChannel = new(0);
        FakeRunner stopRunner = new(stopChannel, Plan, stopScript);
        CombatSearchResult stopped = Run(original, stopRunner, stopChannel);
        Assert(stopped.Completion == SearchCompletionKind.StoppedWithCandidate,
            stopped.Completion.ToString());
        SearchResumeState resume = stopped.Resume
            ?? throw new InvalidDataException("search-resume: forced stop did not carry a resume point");
        Assert(resume.Pass == 0 && resume.NextMemberIndex == stopIndex + 1,
            "forced-gradient stop carries pass 0");

        Script resumeScript = new() { Default = FakeStep.None };
        for (int index = 0; index < count; index++)
            resumeScript.Set((1, index), BeamStep(other));
        SearchProgressChannel resumeChannel = new(0);
        FakeRunner resumeRunner = new(resumeChannel, Plan, resumeScript);
        CombatSearchResult resumed = Run(original, resumeRunner, resumeChannel, resume);
        Assert(resumeRunner.Calls[0].Pass == 0 && resumeRunner.Calls[0].Index == stopIndex + 1,
            "resume starts at pass 0 after the stopped member");
        Assert(resumeRunner.Calls.All(static call => call.Pass == 0),
            "a selected pass-0 result stops the fallback pass");
        Assert(resumed.Beam is not null && Equal(resumed.Beam.BestRoute, forced.BestRoute),
            "resume keeps the pass-0 route");

        Script fullScript = new() { Default = FakeStep.None };
        fullScript.Set((0, 0), BeamStep(forced));
        fullScript.Set((0, stopIndex), BeamStep(forced));
        for (int index = 0; index < count; index++)
            fullScript.Set((1, index), BeamStep(other));
        SearchProgressChannel fullChannel = new(0);
        FakeRunner fullRunner = new(fullChannel, Plan, fullScript);
        CombatSearchResult full = Run(original, fullRunner, fullChannel);
        Assert(full.Beam is not null && resumed.Beam is not null
            && Equal(full.Beam.BestRoute, resumed.Beam.BestRoute),
            "forced-gradient resume equals the substituted full run");
    }

    private static void StopAtLastMemberHasNoResume(SearchCase potions)
    {
        BeamSolver quality = new(potions.Rules, potions.Options with { AuditPotionPolicy = true });
        BeamResult forced = ForcedPotionRoute(potions, quality);
        int potionCount = MemberCount(potions);
        Script forcedScript = new() { Default = FakeStep.None };
        forcedScript.Set((1, potionCount - 1), StoppedStep(forced));
        SearchProgressChannel forcedChannel = new(0);
        FakeRunner forcedRunner = new(forcedChannel, Plan, forcedScript);
        CombatSearchResult forcedStop = Run(potions, forcedRunner, forcedChannel);
        Assert(forcedRunner.Calls.Any(static call => call.Pass == 1),
            "forced-gradient run reaches the second pass");
        Assert(forcedStop.Completion == SearchCompletionKind.StoppedWithCandidate,
            forcedStop.Completion.ToString());
        Assert(forcedStop.Resume is null,
            "stop at the last member of the last pass carries no resume point");
    }

    private static void StopWithoutCandidateAdoptsCompleted(SearchCase original)
    {
        BeamSolver quality = new(original.Rules, original.Options with { AuditPotionPolicy = true });
        (BeamResult better, _) = FindTwoRoutes(original, quality);
        Script script = new() { Default = FakeStep.None };
        script.Set((0, 0), BeamStep(better));
        script.Set((0, 1), StopWithoutCandidateStep());
        SearchProgressChannel channel = new(0);
        FakeRunner runner = new(channel, Plan, script);
        CombatSearchResult result = Run(original, runner, channel);
        Assert(result.Completion == SearchCompletionKind.StoppedWithCandidate,
            result.Completion.ToString());
        Assert(result.Beam is not null && Equal(result.Beam.BestRoute, better.BestRoute),
            "a stopped member without a candidate keeps the completed best result");
        Assert(result.Adoption is { Kind: SearchAdoptionKind.Stopped }, "adoption kind is stopped");
        Assert(result.SelectedMember == 0 && result.Members[0].Adopted
            && result.Members[1].StopReason == "stopped",
            "member log marks the completed result and the stopped member");
        SearchResumeState resume = result.Resume
            ?? throw new InvalidDataException("search-resume: completed adoption lost the resume point");
        Assert(resume.NextMemberIndex == 2,
            "the adopted completed result still carries a resume point");
    }

    private static void StopWithoutCandidateWithoutCompletedCancels(SearchCase original)
    {
        Script script = new() { Default = FakeStep.None };
        script.Set((0, 0), StopWithoutCandidateStep());
        SearchProgressChannel channel = new(0);
        FakeRunner runner = new(channel, Plan, script);
        CombatSearchResult result = Run(original, runner, channel);
        Assert(result.Completion == SearchCompletionKind.Cancelled, result.Completion.ToString());
        Assert(result.Beam is null && result.Resume is null && result.SelectedMember == -1,
            "no candidate and no completed result still cancels");
    }

    private static void RequestedAdoptionHasNoResume(SearchCase original)
    {
        Script script = new() { Default = FakeStep.None };
        script.Set((0, 0), RequestedStep(FirstRoute(original)));
        SearchProgressChannel channel = new(0);
        FakeRunner runner = new(channel, Plan, script);
        CombatSearchResult result = Run(original, runner, channel);
        Assert(result.Completion == SearchCompletionKind.Adopted, result.Completion.ToString());
        Assert(result.Beam is not null && result.Resume is null,
            "a requested adoption ends the request without a resume point");
    }

    private static int MemberCount(SearchCase searchCase)
        => PortfolioBuilder.Build(Plan,
            searchCase.Options.PowerCommitments is { Rules.Length: > 0 }).Length;

    private static CombatSearchResult Run(SearchCase searchCase, FakeRunner runner,
        SearchProgressChannel channel, SearchResumeState? resume = null)
        => CombatSearchCoordinator.RunAsync(
            new(searchCase, Plan) { Progress = channel, Resume = resume },
            new SearchStorage(), runner.Run, CancellationToken.None, static () => 0)
            .AsTask().GetAwaiter().GetResult();

    private static SearchCase LoadRequest(string fileName)
        => LoadRequestPath(Path.Combine("tests/Golden/data/d2/requests", fileName));

    private static SearchCase LoadRequestPath(string path)
    {
        using FileStream input = File.OpenRead(path);
        using BrotliStream compressed = new(input, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        compressed.CopyTo(bytes);
        return SearchCodec.DecodeRequest(bytes.ToArray());
    }

    private static SearchCase LoadForcedCase(string forcedPotionId)
    {
        SearchCase original = LoadRequestPath(Directory.GetFiles(
            "tests/Golden/data/d2/requests", PotionRoot + "*.request.br").Single());
        PotionStrategySnapshot strategy = new(PotionUseMode.Smart,
        [
            new PotionSlotDirective(0, "POISON_POTION", PotionDirective.Disabled),
            new PotionSlotDirective(1, forcedPotionId, PotionDirective.Force),
        ]);
        PotionPolicy potions = original.Options.Potions with
        {
            MaximumUses = null,
            Strategy = strategy,
            EffectivePolicy = PotionUseMode.Smart,
            ForceAllDisabled = false,
            EarliestPlayerTurn = int.MinValue,
        };
        BeamOptions options = original.Options with
        {
            BeamWidth = 12,
            MaxExpandedNodes = 100_000,
            MaxTurns = int.MaxValue,
            UnlimitedNodes = false,
            SoftTimeBudget = TimeSpan.MaxValue,
            SearchThreads = 1,
            DeadlineExpired = null,
            SecondRankBand = false,
            Potions = potions,
            EffectivePotionPolicy = PotionUseMode.Smart,
            InitialExplicitPotionUses = 0,
            InitialAutomaticPotionUses = 0,
            PotionFreeBaseline = null,
            AuditPotionPolicy = true,
        };
        options = SearchBudgetPolicy.ApplyRegression(options);
        return original with { Options = options };
    }

    private static BeamResult FirstRoute(SearchCase original)
        => TryRoute(original, [PlayerAction.EndTurn()])
            ?? throw new InvalidDataException("search-resume: fixture cannot end the turn");

    private static BeamResult ForcedPotionRoute(SearchCase original, BeamSolver quality)
    {
        foreach (BeamResult candidate in PotionRoutes(original))
        {
            try
            {
                BeamSolver.Candidate assessed = quality.AssessResult(original.Battles, candidate,
                    CancellationToken.None, static () => false, out _, out _, out _);
                if (assessed.ForcedUsesSatisfied && quality.IsPolicyEligible(assessed, null))
                    return candidate;
            }
            catch (InvalidDataException) { }
            catch (CapacityException) { }
        }
        throw new InvalidDataException("search-resume: no forced-potion route in the fixture");
    }

    private static BeamResult OtherForcedPotionRoute(SearchCase original, BeamSolver quality,
        BeamResult excluded)
    {
        foreach (BeamResult candidate in PotionRoutes(original))
        {
            if (ReferenceEquals(candidate, excluded) || Equal(candidate.BestRoute, excluded.BestRoute))
                continue;
            try
            {
                BeamSolver.Candidate assessed = quality.AssessResult(original.Battles, candidate,
                    CancellationToken.None, static () => false, out _, out _, out _);
                if (assessed.ForcedUsesSatisfied && quality.IsPolicyEligible(assessed, null))
                    return candidate;
            }
            catch (InvalidDataException) { }
            catch (CapacityException) { }
        }
        // A second forced route is not required for the contract; reuse the first one.
        return excluded;
    }

    private static IEnumerable<BeamResult> PotionRoutes(SearchCase original)
    {
        PlayerAction[] legal = new PlayerAction[Kernel.MaxLegalActions];
        int count = Kernel.LegalActions(original.Battles[0], original.Rules, legal);
        for (int index = 0; index < count; index++)
        {
            if (legal[index].Kind != PlayerActionKind.UsePotion)
                continue;
            PlayerAction potion = legal[index];
            if (TryRoute(original, [potion]) is { } single)
                yield return single;
            if (TryRoute(original, [potion, PlayerAction.EndTurn()]) is { } withEnd)
                yield return withEnd;
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
                if (!PolicyComparisonAgrees(original, quality, routes[left], routes[right], comparison))
                    continue;
                return comparison < 0
                    ? (routes[left], routes[right])
                    : (routes[right], routes[left]);
            }
        throw new InvalidDataException("search-resume: no two routes with different final ordering");
    }

    /// <summary>
    /// The stop path compares with the final ordering while a normal member uses the audited policy
    /// comparison; the contract only uses pairs both orderings rank the same way.
    /// </summary>
    private static bool PolicyComparisonAgrees(SearchCase original, BeamSolver quality,
        BeamResult left, BeamResult right, int routeComparison)
    {
        try
        {
            BeamSolver.Candidate leftCandidate = quality.AssessResult(original.Battles, left,
                CancellationToken.None, static () => false, out _, out _, out _);
            BeamSolver.Candidate rightCandidate = quality.AssessResult(original.Battles, right,
                CancellationToken.None, static () => false, out _, out _, out _);
            int policyComparison = quality.ComparePolicyResults(leftCandidate, rightCandidate);
            return policyComparison != 0 && Math.Sign(policyComparison) == Math.Sign(routeComparison);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (CapacityException)
        {
            return false;
        }
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

    private static FakeStep BeamStep(BeamResult beam) => new(FakeKind.Beam, beam);
    private static FakeStep StoppedStep(BeamResult beam) => new(FakeKind.Stopped, beam);
    private static FakeStep StopWithoutCandidateStep() => new(FakeKind.StopWithoutCandidate, null);
    private static FakeStep RequestedStep(BeamResult beam) => new(FakeKind.Requested, beam);

    private enum FakeKind { None, Beam, Stopped, Requested, StopWithoutCandidate }

    private readonly record struct FakeStep(FakeKind Kind, BeamResult? Beam)
    {
        internal static FakeStep None => new(FakeKind.None, null);
    }

    private sealed class Script
    {
        private readonly Dictionary<(int Pass, int Index), FakeStep> _steps = [];

        internal FakeStep Default { get; set; } = FakeStep.None;

        internal void Set((int Pass, int Index) key, FakeStep step) => _steps[key] = step;

        internal FakeStep For(int pass, int index)
            => _steps.TryGetValue((pass, index), out FakeStep step) ? step : Default;
    }

    private sealed class FakeRunner(SearchProgressChannel channel, SearchPlan plan, Script script)
    {
        private int _pass;
        private int _lastIndex = -1;

        internal List<(int Pass, int Index, long Configured)> Calls { get; } = [];

        internal ValueTask<SearchMemberMeasurement> Run(SearchCase member, SearchStorage shared,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int index = channel.MemberIndex;
            if (index <= _lastIndex)
                _pass++;
            _lastIndex = index;
            Calls.Add((_pass, index,
                checked((long)member.Options.BeamWidth * plan.MillisecondsPerWidth)));
            return ValueTask.FromResult(Measure(index));
        }

        private SearchMemberMeasurement Measure(int index)
        {
            FakeStep step = script.For(_pass, index);
            switch (step.Kind)
            {
                case FakeKind.None:
                    return new(null, "none", null, 0);
                case FakeKind.Beam:
                    return new(step.Beam, "ok", null, 0);
                case FakeKind.Stopped:
                    return new(step.Beam! with
                    {
                        StopReason = "stopped",
                        Adoption = new SearchAdoptionInfo(1, 0, SearchAdoptionKind.Stopped,
                            step.Beam.BestIsVictory ? Outcome.Victory : Outcome.None),
                    }, "stopped", null, 0);
                case FakeKind.Requested:
                    return new(step.Beam! with
                    {
                        StopReason = "adopted",
                        Adoption = new SearchAdoptionInfo(1, 0, SearchAdoptionKind.Requested,
                            step.Beam.BestIsVictory ? Outcome.Victory : Outcome.None),
                    }, "ok", null, 0);
                case FakeKind.StopWithoutCandidate:
                    return new(new BeamResult([], [], false, double.NegativeInfinity, 0, 0, 0, 0, 0,
                        TimeSpan.Zero)
                    {
                        StopReason = "stopped",
                        StopWithoutCandidate = true,
                        PotionPolicySatisfied = false,
                    }, "stopped", null, 0);
                default:
                    throw new InvalidDataException($"search-resume: unknown fake step {step.Kind}");
            }
        }
    }
}
