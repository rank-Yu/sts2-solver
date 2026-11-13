using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// L1/L2 contracts for the BrightestFlame combat-cumulative max HP cap: the ported policy rule,
/// expansion pruning on a real BrightestFlame root, root-spent accounting, two-turn blocking and
/// the optional SREQ extension.
/// </summary>
internal static class BrightestFlameContracts
{
    private const string FlameRoot = "ec-c-ancient";

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("brightest-flame: " + message); }

    internal static void Run()
    {
        PolicyRule();
        SreqRoundTrip();
        ExpansionCap();
        Console.WriteLine("BRIGHTEST_FLAME policy/roundtrip/expand/turns=pass");
    }

    private static void PolicyRule()
    {
        Assert(GrowthCostPolicy.AllowsBrightestFlame(null, 0, 100), "null is unlimited");
        Assert(!GrowthCostPolicy.AllowsBrightestFlame(0, 0, 2), "zero rejects the first play");
        Assert(GrowthCostPolicy.AllowsBrightestFlame(2, 0, 2), "limit 2 allows one play");
        Assert(!GrowthCostPolicy.AllowsBrightestFlame(2, 0, 4), "limit 2 rejects the second play");
        Assert(!GrowthCostPolicy.AllowsBrightestFlame(2, 2, 4), "root spent counts toward the total");
        Assert(GrowthCostPolicy.AllowsBrightestFlame(2, 4, 4), "manual overage raises the ceiling");
        Assert(!GrowthCostPolicy.AllowsBrightestFlame(2, 4, 6), "manual overage grants no new spend");
    }

    private static SearchCase Read()
    {
        using FileStream file = File.OpenRead(GoldenFixtures.PathOf($"d1/{FlameRoot}/config.br"));
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new(); zip.CopyTo(bytes);
        return SearchCodec.DecodeRequest(bytes.ToArray());
    }

    private static byte[] ReadBytes()
    {
        using FileStream file = File.OpenRead(GoldenFixtures.PathOf($"d1/{FlameRoot}/config.br"));
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new(); zip.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void SreqRoundTrip()
    {
        SearchCase item = Read();
        byte[] original = ReadBytes();
        Assert(SearchCodec.EncodeRequest(item).AsSpan().SequenceEqual(original),
            "default request bytes stay unchanged");
        SearchCase withCap = item with
        {
            Options = item.Options with
            {
                BrightestFlameMaxHpLossLimit = 2,
                InitialBrightestFlameMaxHpSpent = 4,
            },
        };
        byte[] encoded = SearchCodec.EncodeRequest(withCap);
        SearchCase decoded = SearchCodec.DecodeRequest(encoded);
        Assert(decoded.Options.BrightestFlameMaxHpLossLimit == 2, "limit survives SREQ");
        Assert(decoded.Options.InitialBrightestFlameMaxHpSpent == 4, "root spent survives SREQ");
        Assert(!encoded.AsSpan().SequenceEqual(original), "non-default cap changes the request bytes");
    }

    private readonly record struct FlameRun(int PlayCards, int MaxPlayCount, int LaterPlayFromSpent);

    private static FlameRun ExpandedFlamePlays(int? limit, int rootSpent, int turns)
    {
        SearchCase item = Read();
        Battle root = item.Battles[0];
        Assert(Registry.TryCard("BRIGHTEST_FLAME", out ushort flameId), "BrightestFlame registered");
        Card flame = default;
        bool found = false;
        for (int index = 0; index < root.CardCount; index++)
        {
            if (root.Cards[index].Id != flameId)
                continue;
            flame = root.Cards[index];
            found = true;
            break;
        }
        Assert(found, "fixture root contains BrightestFlame");
        root.HandCount = 0;
        root.DrawCount = 0;
        root.DiscardCount = 0;
        root.ExhaustCount = 0;
        root.PlayCount = 0;
        Piles.Append(ref root, Pile.Hand, flame);
        Piles.Append(ref root, Pile.Hand, flame);
        root.Energy = 9;
        root.Player.CurrentHp = root.Player.MaxHp = 3000;

        FlameTrace trace = new();
        BeamOptions options = SearchBudgetPolicy.ApplyRegression(item.Options) with
        {
            BeamWidth = 64,
            MaxExpandedNodes = 20_000,
            MaxTurns = turns,
            UnlimitedNodes = false,
            SoftTimeBudget = TimeSpan.MaxValue,
            SearchThreads = 1,
            DeadlineExpired = null,
            SecondRankBand = false,
            Bound = null,
            Trace = trace,
            Ordering = new RouteOrdering(3000, BossHpRelief.None,
                PostCombatRelicHealProfile.None),
            BrightestFlameMaxHpLossLimit = limit,
            InitialBrightestFlameMaxHpSpent = rootSpent,
        };
        _ = new BeamSolver(item.Rules, options).Solve([root], CancellationToken.None);
        return new FlameRun(trace.PlayCards, trace.MaxPlayCount, trace.LaterPlayFromSpent);
    }

    private static void ExpansionCap()
    {
        FlameRun unlimited = ExpandedFlamePlays(null, 0, 1);
        FlameRun first = ExpandedFlamePlays(2, 0, 1);
        FlameRun blocked = ExpandedFlamePlays(0, 0, 1);
        FlameRun alreadySpent = ExpandedFlamePlays(2, 2, 1);
        FlameRun unlimitedTwoTurns = ExpandedFlamePlays(null, 0, 2);
        FlameRun cappedTwoTurns = ExpandedFlamePlays(2, 0, 2);
        Assert(unlimited.PlayCards > 0, "unlimited expands the play");
        Assert(first.PlayCards > 0 && first.MaxPlayCount == 1,
            $"limit 2 allows exactly one play per route: {first}");
        Assert(blocked.PlayCards == 0, $"limit 0 expands no play: {blocked}");
        Assert(alreadySpent.PlayCards == 0,
            $"root spent 2 leaves no allowance under limit 2: {alreadySpent}");
        Assert(unlimitedTwoTurns.MaxPlayCount >= 2,
            $"unlimited expands a second play on one route: {unlimitedTwoTurns}");
        Assert(unlimitedTwoTurns.LaterPlayFromSpent > 0,
            $"unlimited plays again after a spent turn: {unlimitedTwoTurns}");
        Assert(cappedTwoTurns.MaxPlayCount == 1 && cappedTwoTurns.LaterPlayFromSpent == 0,
            $"limit 2 blocks the later play after a spent turn: {cappedTwoTurns}");
    }

    private sealed class FlameTrace : BeamTrace
    {
        private readonly Dictionary<int, int> _playCounts = [];
        private readonly Dictionary<int, int> _turns = [];
        private int _parent;
        private PlayerAction _action;
        internal int PlayCards;
        internal int MaxPlayCount;
        internal int LaterPlayFromSpent;

        internal override int Match(int parentNode, in PlayerAction action)
        {
            _parent = parentNode;
            _action = action;
            if (action.Kind == PlayerActionKind.PlayCard)
            {
                PlayCards++;
                int turn = _turns.GetValueOrDefault(parentNode, 1);
                if (turn > 1 && _playCounts.GetValueOrDefault(parentNode) > 0)
                    LaterPlayFromSpent++;
            }
            return 1;
        }

        internal override bool IsWatched(int node) => node > 0;

        internal override void NodeCreated(int node, int step)
        {
            int plays = _playCounts.GetValueOrDefault(_parent);
            if (_action.Kind == PlayerActionKind.PlayCard)
                plays++;
            _playCounts[node] = plays;
            if (plays > MaxPlayCount)
                MaxPlayCount = plays;
        }

        internal override void NodeInserted(int node, bool added, int existingNode, bool incomingWon,
            double rank, double existingRank, double score, double existingScore,
            int existingPotionCount, int existingTurn, int existingDepth, int existingEnemyHp,
            int existingProjectedHp, in TraceRouteInfo targetInfo, in TraceRouteInfo existingInfo)
        { _turns[node] = targetInfo.Turn; }
        internal override void SetupLeader(string lane, in TraceSetupLeader target,
            in TraceSetupLeader leader, bool targetBeatsLeader, string comparison) { }
        internal override void NodeRetained(int node, string phase, bool selected, bool required,
            int rankPosition, int count, int width, string lanes) { }
        internal override void NodePruned(int node, string phase, string reason) { }
        internal override void StandPatProbe(int node, bool selected, bool available,
            bool allEnemiesDead, int delayedDamage, int projectedHp, int resourceValue, int laneValue,
            double score, bool chosen) { }
        internal override void Finish(string reason, int turns, int expanded) { }
    }
}
