// Root recapture: rebuilds the native CombatState for one frozen D1 root, imports it with the current
// BattleImporter, and assembles the same SREQ sidecar ConfigCompare checks. The result is written as a
// raw GOLD record with the roots and an empty K1/K2 section plus a Brotli-compressed config.br; the
// Golden tool reads that payload, replaces the frozen root state, and re-records K1/K2 with Core only.
using System.IO.Compression;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Golden;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal static class RecaptureRoot
{
    internal static int Run(string data, string corpora, string output, string root)
    {
        if (!Directory.Exists(output))
            Directory.CreateDirectory(output);
        string sourceDirectory = Path.Combine(data, "d1", root);
        GoldenRootRecord oldRecord = GoldenData.Decode(
            ReadBrotli(Path.Combine(sourceDirectory, "golden.br")));
        byte[] oldConfig = ReadBrotli(Path.Combine(sourceDirectory, "config.br"));

        OfflineRequest request = Program.LoadRequest(data, corpora, root);
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();

        bool hasPrefix = request.FlashPrefixActions.Length != 0 || request.FlashPrefixPlan.Length != 0;
        List<GoldenBattleRoot> roots;
        byte[] configBytes;
        if (request.FlashTurnSetupRootForTest)
        {
            (roots, configBytes) = CaptureSetup(loop, request, oldRecord);
        }
        else
        {
            if (oldRecord.Roots.Count != 1)
                throw new InvalidDataException(
                    $"非回合准备的根 {root} 有 {oldRecord.Roots.Count} 个冻结根，预期 1 个。");
            CombatState state = Build(loop, request, root);
            (roots, configBytes) = hasPrefix
                ? CapturePrefix(state, request)
                : CaptureOrdinary(state, request, oldConfig);
        }

        string targetDirectory = Path.Combine(output, root);
        Directory.CreateDirectory(targetDirectory);
        GoldenRootRecord recapture = new()
        {
            InputJson = oldRecord.InputJson,
            RulesSha256 = oldRecord.RulesSha256,
            Roots = roots,
            K1 = [],
            Walks = [],
        };
        File.WriteAllBytes(Path.Combine(targetDirectory, "roots.gold"), GoldenData.Encode(recapture));
        WriteBrotliSmallest(Path.Combine(targetDirectory, "config.br"), configBytes);

        bool rootsSame = RootsEqual(oldRecord.Roots, roots);
        bool configSame = oldConfig.AsSpan().SequenceEqual(configBytes);
        string first = "none";
        if (!rootsSame)
        {
            for (int index = 0; index < Math.Min(oldRecord.Roots.Count, roots.Count); index++)
            {
                if (SameRoot(oldRecord.Roots[index], roots[index]))
                    continue;
                List<string> diff = BattleDump.Diff(oldRecord.Roots[index].Battle,
                    roots[index].Battle, "golden", "recapture");
                first = diff.Count == 0 ? "<bytes>" : FieldOf(diff[0]);
                break;
            }
        }
        Console.WriteLine($"RECAPTURE root={root} roots={roots.Count} "
            + $"rootBytes={(rootsSame ? "same" : "changed")} "
            + $"configBytes={(configSame ? "same" : "changed")} first={first}");
        return 0;
    }

    private static CombatState Build(MainLoopContext loop, OfflineRequest request, string root)
    {
        Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request);
        loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), $"重新捕获建局 {root}");
        return build.GetAwaiter().GetResult();
    }

    private static (List<GoldenBattleRoot> Roots, byte[] Config) CaptureOrdinary(
        CombatState state, OfflineRequest request, byte[] oldConfig)
    {
        Player player = PlayerOf(state);
        if (state.RunState is not RunState runState)
            throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
        Rules rules = RulesCompiler.Compile(state, player, runState);
        Battle battle = BattleImporter.Import(state, player, rules, ImportOptions.Default);
        SearchPolicy policy = SearchPolicyOverrides.ApplyOverrides(
            SearchPolicyCapture.Capture(state, player), request, player);
        SearchCase expected = SearchCodec.DecodeRequest(oldConfig);
        int actionCount = expected.Options.InitialActionCount;
        EvaluationRootFacts facts = EvaluationRootFacts.Default with { InitialActionCount = actionCount };
        Scorer scorer = EvaluationConfigCompiler.CompileScorer(
            state, player, rules, battle, policy, facts);
        BoundFeatures bound = EvaluationConfigCompiler.CompileBound(
            state, player, battle, policy, facts);
        SearchCase actual = EvaluationConfigCompiler.CompileRequest(
            state, player, rules, battle, policy, facts);
        return ([MakeRoot(battle, "", scorer, bound, actionCount)], SearchCodec.EncodeRequest(actual));
    }

    private static (List<GoldenBattleRoot> Roots, byte[] Config) CapturePrefix(
        CombatState state, OfflineRequest request)
    {
        Player player = PlayerOf(state);
        if (state.RunState is not RunState runState)
            throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
        Rules preRules = RulesCompiler.Compile(state, player, runState);
        Battle pre = BattleImporter.Import(state, player, preRules, ImportOptions.Default);
        PrefixAction[] actions = PrefixPlan.Parse(request);
        PrefixReplayResult replay = PrefixReplay.Replay(pre, preRules, actions);
        Rules rules = RulesCompiler.CompileAfterPrefix(state, player, runState, replay.Battle);
        SearchPolicy policy = SearchPolicyOverrides.ApplyOverrides(
            SearchPolicyCapture.Capture(state, player), request, player);
        EvaluationRootFacts facts = EvaluationRootFacts.Default with
        {
            InitialActionCount = actions.Length,
        };
        Scorer scorer = EvaluationConfigCompiler.CompileScorer(state, player, rules,
            replay.Battle, policy, facts, rankBaselineRoot: pre, rankBaselineRules: preRules);
        BoundFeatures bound = EvaluationConfigCompiler.CompileBound(
            state, player, replay.Battle, policy, facts);
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Scorer = scorer,
            Bound = bound,
            InitialActionCount = actions.Length,
        };
        SearchCase actual = new(rules, [replay.Battle], options);
        return ([MakeRoot(replay.Battle, "", scorer, bound, actions.Length)],
            SearchCodec.EncodeRequest(actual));
    }

    private static (List<GoldenBattleRoot> Roots, byte[] Config) CaptureSetup(
        MainLoopContext loop, OfflineRequest request, GoldenRootRecord oldRecord)
    {
        SetupRootSet set = SetupRootSet.Build(loop, request, needConfig: true);
        if (set.Score is not { } score || set.Bound is not { } boundConfig)
            throw new InvalidDataException("回合准备根的评分/下界配置没有编译。");
        if (set.Roots.Count < oldRecord.Roots.Count)
        {
            throw new InvalidDataException(
                $"回合准备根数量 {set.Roots.Count} 少于冻结的 {oldRecord.Roots.Count}。");
        }
        Scorer scorer = new(score);
        // SetupRootSet may enumerate more options than the frozen record kept. Match each frozen root
        // to a distinct current root by battle bytes, and keep the frozen breadcrumb (which is richer
        // than SetupRootSet's card+upgrade key) so unchanged roots reproduce golden.br byte for byte.
        bool[] used = new bool[set.Roots.Count];
        List<GoldenBattleRoot> roots = new(oldRecord.Roots.Count);
        List<Battle> battles = new(oldRecord.Roots.Count);
        for (int index = 0; index < oldRecord.Roots.Count; index++)
        {
            int match = -1;
            for (int candidate = 0; candidate < set.Roots.Count; candidate++)
            {
                if (!used[candidate]
                    && ImportCompare.SameBytes(oldRecord.Roots[index].Battle, set.Roots[candidate]))
                {
                    match = candidate;
                    break;
                }
            }
            if (match < 0)
            {
                match = index < set.Roots.Count && !used[index]
                    ? index
                    : Array.IndexOf(used, false);
                if (match < 0)
                    throw new InvalidDataException("回合准备根匹配不足。");
            }
            used[match] = true;
            roots.Add(MakeRoot(set.Roots[match], oldRecord.Roots[index].Choices,
                scorer, boundConfig, 0));
            battles.Add(set.Roots[match]);
        }
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Scorer = scorer,
            Bound = boundConfig,
            InitialActionCount = 0,
        };
        SearchCase actual = new(set.Rules, [.. battles], options);
        return (roots, SearchCodec.EncodeRequest(actual));
    }

    private static GoldenBattleRoot MakeRoot(in Battle battle, string choices, Scorer scorer,
        BoundFeatures bound, int actionCount)
    {
        ScoreFeatures features = scorer.Features(battle, actionCount);
        byte boundStatus;
        ulong boundHash = 0;
        if (!bound.Enabled)
        {
            boundStatus = 2;
        }
        else
        {
            boundStatus = 1;
            BoundValues values = bound.Evaluate(battle, features.ExplicitPotionStrategicCost,
                includeSmartPotionLowerBound: true);
            boundHash = GoldenHash.Bound(values);
        }
        return new GoldenBattleRoot
        {
            Battle = battle,
            Choices = choices,
            ScoreHash = GoldenHash.Score(features),
            BoundStatus = boundStatus,
            BoundHash = boundHash,
        };
    }

    private static Player PlayerOf(CombatState state)
        => LocalContext.GetMe(state) ?? throw new InvalidOperationException("找不到本地玩家。");

    private static bool RootsEqual(List<GoldenBattleRoot> left, List<GoldenBattleRoot> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!SameRoot(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static bool SameRoot(GoldenBattleRoot left, GoldenBattleRoot right)
    {
        Battle leftBattle = left.Battle;
        Battle rightBattle = right.Battle;
        return left.ScoreHash == right.ScoreHash
            && left.BoundStatus == right.BoundStatus
            && left.BoundHash == right.BoundHash
            && string.Equals(left.Choices, right.Choices, StringComparison.Ordinal)
            && Fingerprint.Equal(in leftBattle, in rightBattle);
    }

    private static string FieldOf(string line)
    {
        int space = line.IndexOf(' ');
        if (space < 0)
            return line;
        string rest = line[(space + 1)..];
        int colon = rest.IndexOf(':');
        return colon < 0 ? rest : rest[..colon];
    }

    private static byte[] ReadBrotli(string path)
    {
        using FileStream file = File.OpenRead(path);
        using BrotliStream brotli = new(file, CompressionMode.Decompress);
        using MemoryStream buffer = new();
        brotli.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteBrotliSmallest(string path, byte[] bytes)
    {
        using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BrotliStream brotli = new(file, CompressionLevel.SmallestSize, leaveOpen: false);
        brotli.Write(bytes);
    }
}
