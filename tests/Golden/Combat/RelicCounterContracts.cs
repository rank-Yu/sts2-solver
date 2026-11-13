using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Relic counter targets on named native roots with explicit cycle positions, solved on the regression budget.
/// Happy Flower and Pen Nib + Joss Paper end on the goal at no extra HP loss when the allowance is 0;
/// Pendulum is reached only by losing 2 more HP, so allowance 0 keeps the HP and 5 buys the goal.
/// The request codec carries the targets.
/// </summary>
internal static class RelicCounterContracts
{
    private const string HappyFlower = "quality-DEFECT-018";
    private const string PenNibJossPaper = "quality-IRONCLAD-006";
    private const string Pendulum = "quality-IRONCLAD-005";

    private static readonly Dictionary<string, int> PeriodVar = new(StringComparer.Ordinal)
    {
        ["HAPPY_FLOWER"] = 1, ["FAKE_HAPPY_FLOWER"] = 1, ["PENDULUM"] = 1, ["POLLINOUS_CORE"] = 1,
        ["NUNCHAKU"] = 0, ["TUNING_FORK"] = 0, ["JOSS_PAPER"] = 0, ["IRON_CLUB"] = 0, ["GALACTIC_DUST"] = 0,
    };

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidDataException("relic-counter: " + message);
    }

    internal static void Run()
    {
        SearchCase flower = Load(HappyFlower);
        EvaluateCounters(flower);
        CodecRoundTrip(flower);
        WideTargetRoundTrip(flower);
        Outcome free = Solve(flower, 0);
        Assert(free.Targeted.Won && free.Plain.Won, "Happy Flower routes win");
        Assert(free.Targeted.Deficit == free.Plain.Deficit, "free goal costs no HP");
        Assert(free.Plain.Goals == 0 && free.Targeted.Goals == 1, "Happy Flower ends on its goal for free");

        Outcome pair = Solve(WithCounter(Load(PenNibJossPaper), "PEN_NIB", 1), 0);
        Assert(pair.Targeted.Deficit == pair.Plain.Deficit && pair.Targeted.Lost <= pair.Plain.Lost,
            "Pen Nib + Joss Paper goal costs no HP");
        Assert(pair.Plain.Goals == 0 && pair.Targeted.Goals == 1, "one of two counters aligned for free");

        SearchCase pendulum = WithCounter(Load(Pendulum), "PENDULUM", 0);
        Outcome kept = Solve(pendulum, 0);
        Assert(kept.Targeted.Lost == kept.Plain.Lost && kept.Targeted.Goals == 0,
            "allowance 0 never pays HP for the goal");
        Outcome paid = Solve(pendulum, 5);
        Assert(paid.Targeted.Goals == 1 && paid.Targeted.Lost > paid.Plain.Lost
            && paid.Targeted.Lost - paid.Plain.Lost <= 5, "allowance 5 buys the goal within 5 HP");
        Assert(paid.Targeted.Deficit == paid.Targeted.Lost - paid.Plain.Lost + paid.Plain.Deficit - 5,
            "the goal earns its allowance once");
        Console.WriteLine($"RELIC_COUNTER_CONTRACT free_hp={free.Targeted.Lost} pair_hp={pair.Targeted.Lost} "
            + $"paid_extra_hp={paid.Targeted.Lost - paid.Plain.Lost}");
    }

    private readonly record struct Route(bool Won, int Deficit, int Goals, int Lost);

    private readonly record struct Outcome(Route Plain, Route Targeted);

    /// <summary>Solves without and with targets and assesses both routes under the targets.</summary>
    private static Outcome Solve(SearchCase search, int allowance)
    {
        BeamOptions plainOptions = Regression(search.Options);
        BeamResult plain = new BeamSolver(search.Rules, plainOptions).Solve(search.Battles, CancellationToken.None);
        BeamOptions targetOptions = Regression(WithTargets(search.Options, Targets(search, allowance)));
        BeamSolver solver = new(search.Rules, targetOptions);
        BeamResult targeted = solver.Solve(search.Battles, CancellationToken.None);
        return new Outcome(Assess(solver, search, plain), Assess(solver, search, targeted));
    }

    private static Route Assess(BeamSolver solver, SearchCase search, BeamResult result)
    {
        BeamSolver.Candidate candidate = solver.AssessResult(search.Battles, result, CancellationToken.None,
            static () => false, out _, out int lost, out _);
        return new Route(candidate.Key.Won, candidate.Key.StrategicHpDeficit, candidate.Key.StrategyGoalCount, lost);
    }

    /// <summary>The goal is one count short of the cycle and is credited on a won state only.</summary>
    private static void EvaluateCounters(SearchCase search)
    {
        RelicCounterTarget[] targets = Targets(search, 4);
        Assert(targets.Length == 1 && targets[0].Period == 3 && targets[0].Goal == 2, "Happy Flower cycle");
        Battle battle = search.Battles[0];
        battle.RelicState(search.Rules, targets[0].Slot).Counter = 2;
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: true, search.Rules) == new RelicCounterGoals(4, 1),
            "goal met on a won state");
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: false, search.Rules) == default, "no credit before the win");
        battle.RelicState(search.Rules, targets[0].Slot).Counter = 5;
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: true, search.Rules).Count == 1, "counter read modulo its cycle");
        battle.RelicState(search.Rules, targets[0].Slot).Counter = 0;
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: true, search.Rules) == default, "goal missed");
    }

    private static void CodecRoundTrip(SearchCase search)
    {
        SearchCase targeted = search with { Options = WithTargets(search.Options, Targets(search, 7)) };
        byte[] bytes = SearchCodec.EncodeRequest(targeted);
        SearchCase decoded = SearchCodec.DecodeRequest(bytes);
        Assert(decoded.Options.Scorer!.RulesForCodec.RelicCounterTargets
                .SequenceEqual(Targets(search, 7)), "targets survive the request codec");
        Assert(SearchCodec.EncodeRequest(decoded).AsSpan().SequenceEqual(bytes), "request round trip");
    }

    private static void WideTargetRoundTrip(SearchCase search)
    {
        Registry.TryRelic("MAW_BANK", out ushort filler);
        Registry.TryRelic("HAPPY_FLOWER", out ushort flower);
        Relic[] rows = Enumerable.Repeat(new Relic { Id = filler }, 301).ToArray();
        rows[300] = new Relic { Id = flower, Counter = 2 };
        RelicRoster roster = RelicRoster.Create(rows);
        Battle battle = search.Battles[0];
        roster.Import(rows, ref battle);
        Rules rules = search.Rules.WithRelics(roster);
        RelicCounterTarget[] targets = [new(300, 3, 2, 7)];
        Scorer old = search.Options.Scorer!;
        Scorer scorer = new(old.RulesForCodec.WithRules(rules).WithRelicCounterTargets(targets),
            old.BaseOnly, old.Offensive, old.QueenPolicy, old.SetupPolicy);
        SearchCase wide = new(rules, [battle], search.Options with { Scorer = scorer });
        byte[] bytes = SearchCodec.EncodeRequest(wide);
        SearchCase decoded = SearchCodec.DecodeRequest(bytes);
        Assert(decoded.Options.Scorer!.RulesForCodec.RelicCounterTargets.SequenceEqual(targets)
            && decoded.Rules.Relics.Count == 301
            && RelicCounterGoals.Evaluate(decoded.Battles[0], targets, true, decoded.Rules)
                == new RelicCounterGoals(7, 1)
            && bytes.AsSpan().SequenceEqual(SearchCodec.EncodeRequest(decoded)),
            "wide relic target and root context survive request codec");
    }

    private static RelicCounterTarget[] Targets(SearchCase search, int allowance)
    {
        Battle root = search.Battles[0];
        List<RelicCounterTarget> targets = [];
        for (int index = 0; index < search.Rules.Relics.Count; index++)
        {
            ushort id = root.GetRelic(search.Rules, index).Id;
            string relic = Registry.Relic(id).NativeId;
            int period = relic == "PEN_NIB" ? 10
                : PeriodVar.TryGetValue(relic, out int var) ? (int)search.Rules.RelicVar(id, var) : 0;
            if (period != 0)
                targets.Add(new((ushort)index, (byte)period, (byte)(period - 1), allowance));
        }
        return [.. targets];
    }

    private static BeamOptions WithTargets(BeamOptions options, RelicCounterTarget[] targets)
    {
        Scorer scorer = options.Scorer ?? throw new InvalidDataException("夹具没有评分器。");
        return options with
        {
            Scorer = new Scorer(scorer.RulesForCodec.WithRelicCounterTargets(targets), scorer.BaseOnly,
                scorer.Offensive, scorer.QueenPolicy, scorer.SetupPolicy),
        };
    }

    private static BeamOptions Regression(BeamOptions options) => SearchBudgetPolicy.ApplyRegression(options with
    {
        SoftTimeBudget = TimeSpan.MaxValue,
        Pacer = null,
        Trace = null,
        SearchThreads = 1,
    });

    // Fix the initial cycle position explicitly: native opening shuffles may change across
    // game versions, while these contracts must retain both free and HP-costing goal routes.
    private static SearchCase WithCounter(SearchCase search, string relic, int counter)
    {
        Battle root = search.Battles[0];
        for (int slot = 0; slot < search.Rules.Relics.Count; slot++)
        {
            if (Registry.Relic(root.GetRelic(search.Rules, slot).Id).NativeId != relic) continue;
            root.RelicState(search.Rules, slot).Counter = counter;
            return search with { Battles = [root] };
        }
        throw new InvalidDataException($"relic-counter: missing {relic}");
    }

    private static SearchCase Load(string root)
        => GoldenFixtures.Search(root);
}
