using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Relic counter targets on D2 requests that hold a counter relic, solved on the regression budget.
/// Happy Flower and Pen Nib + Joss Paper end on the goal at no extra HP loss when the allowance is 0;
/// Pendulum is reached only by losing 2 more HP, so allowance 0 keeps the HP and 5 buys the goal.
/// The request codec carries the targets.
/// </summary>
internal static class RelicCounterContracts
{
    private const string Requests = "tests/Golden/data/d2/requests/";
    private const string HappyFlower = "294ca14288c38d8ae86d700d36c72f32dd458cb4748b5956510a30ef0189ec52";
    private const string PenNibJossPaper = "86e7fa14365f470e5b90befa58a6d4ef92b1ed707c451aa2279e6de8350cc486";
    private const string Pendulum = "bb32b85c6d55b5c0d381f08643fdd6aa28ed3829e7bb2a79bde5d47f71f11f72";

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
        Outcome free = Solve(flower, 0);
        Assert(free.Targeted.Won && free.Plain.Won, "Happy Flower routes win");
        Assert(free.Targeted.Deficit == free.Plain.Deficit, "free goal costs no HP");
        Assert(free.Plain.Goals == 0 && free.Targeted.Goals == 1, "Happy Flower ends on its goal for free");

        Outcome pair = Solve(Load(PenNibJossPaper), 0);
        Assert(pair.Targeted.Deficit == pair.Plain.Deficit && pair.Targeted.Lost <= pair.Plain.Lost,
            "Pen Nib + Joss Paper goal costs no HP");
        Assert(pair.Plain.Goals == 0 && pair.Targeted.Goals == 1, "one of two counters aligned for free");

        SearchCase pendulum = Load(Pendulum);
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
        battle.Relics[targets[0].Slot].Counter = 2;
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: true) == new RelicCounterGoals(4, 1),
            "goal met on a won state");
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: false) == default, "no credit before the win");
        battle.Relics[targets[0].Slot].Counter = 5;
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: true).Count == 1, "counter read modulo its cycle");
        battle.Relics[targets[0].Slot].Counter = 0;
        Assert(RelicCounterGoals.Evaluate(battle, targets, won: true) == default, "goal missed");
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

    private static RelicCounterTarget[] Targets(SearchCase search, int allowance)
    {
        Battle root = search.Battles[0];
        List<RelicCounterTarget> targets = [];
        for (int index = 0; index < root.RelicCount; index++)
        {
            ushort id = root.Relics[index].Id;
            string relic = Registry.Relic(id).NativeId;
            int period = relic == "PEN_NIB" ? 10
                : PeriodVar.TryGetValue(relic, out int var) ? (int)search.Rules.RelicVar(id, var) : 0;
            if (period != 0)
                targets.Add(new((byte)index, (byte)period, (byte)(period - 1), allowance));
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

    private static SearchCase Load(string hash)
        => SearchCodec.DecodeRequest(GoldenFiles.ReadCompressed(Requests + hash + ".request.br"));
}
