// --search-compare: replay the game's deterministic SREQ/SRES byte for byte offline and rebuild the
// same request from the offline host's own native import.
using System.Text.Json;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal sealed record SearchLiveStatus
{
    public int SchemaVersion { get; init; } = 1;
    public string Root { get; init; } = string.Empty;
    public string Status { get; init; } = "failed";
    public string? Error { get; init; }
    public bool TurnSetupRoot { get; init; }
    public string[] SetupSelections { get; init; } = [];
    public string SearchStatus { get; init; } = "none";
    public string SearchTier { get; init; } = "Medium";
    public bool DeterministicSearch { get; init; }
}

internal static class SearchCompare
{
    public static int Run(string data, string corpora, string resultDirectory)
    {
        if (!Directory.Exists(resultDirectory))
        {
            Console.Error.WriteLine($"找不到实机结果目录：{resultDirectory}");
            return 2;
        }
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();

        string[] directories = Directory.EnumerateDirectories(resultDirectory)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        int same = 0, different = 0, rejected = 0, skipped = 0;
        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            SearchLiveStatus? status;
            try
            {
                string statusPath = Path.Combine(directory, "status.json");
                if (!File.Exists(statusPath))
                {
                    Console.Error.WriteLine($"root={name} search=reject reason=missing-status");
                    rejected++;
                    continue;
                }
                status = JsonSerializer.Deserialize<SearchLiveStatus>(
                    File.ReadAllText(statusPath), OfflineJson.Options);
            }
            catch (JsonException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=invalid-status detail={error.Message}");
                rejected++;
                continue;
            }
            if (status == null || !string.Equals(status.Root, name, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"root={name} search=reject reason=status-root-mismatch");
                rejected++;
                continue;
            }
            if (status.TurnSetupRoot || string.Equals(status.SearchStatus, "skipped", StringComparison.Ordinal))
            {
                Console.WriteLine($"root={name} search=skipped reason=turn-setup-root");
                skipped++;
                continue;
            }
            if (!status.DeterministicSearch)
            {
                Console.WriteLine($"root={name} search=skipped reason=measure-only");
                skipped++;
                continue;
            }
            if (!string.Equals(status.Status, "ok", StringComparison.Ordinal)
                || !string.Equals(status.SearchStatus, "ok", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"root={name} search=reject reason=search-not-ok status={status.Status} "
                    + $"search={status.SearchStatus} detail={status.Error}");
                rejected++;
                continue;
            }

            byte[] requestBytes;
            byte[] resultBytes;
            try
            {
                requestBytes = File.ReadAllBytes(Path.Combine(directory, "SREQ"));
                resultBytes = File.ReadAllBytes(Path.Combine(directory, "SRES"));
            }
            catch (FileNotFoundException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=missing-artifact detail={error.FileName}");
                rejected++;
                continue;
            }

            bool requestSame;
            bool resultSame;
            string? detail = null;
            try
            {
                SearchCase searchCase = SearchCodec.DecodeRequest(requestBytes);
                if (searchCase.Options.Pacer != null
                    || searchCase.Options.Trace != null)
                {
                    throw new InvalidDataException("SREQ 含宿主回调，确定性重放会不同。");
                }
                BeamOptions replayOptions = SearchBudgetPolicy.ApplyRegression(searchCase.Options with
                    { SoftTimeBudget = TimeSpan.MaxValue, Pacer = null, Trace = null });
                BeamSolver solver = new(searchCase.Rules, replayOptions);
                BeamResult replayed = solver.Solve(searchCase.Battles, CancellationToken.None);
                byte[] replayedBytes = SearchCodec.EncodeResult(replayed);
                resultSame = replayedBytes.AsSpan().SequenceEqual(resultBytes);
                if (!resultSame)
                    detail = "离线求解结果与 SRES 不同";

                requestSame = AssembleFromNative(
                    loop, data, corpora, name, status, requestBytes, out string assemblyDetail);
                if (!requestSame)
                    detail = assemblyDetail;
            }
            catch (InvalidDataException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=invalid-request detail={error.Message}");
                rejected++;
                continue;
            }
            catch (UnsupportedImportException error)
            {
                Console.Error.WriteLine(
                    $"root={name} search=reject reason={error.Reason} detail={error.Detail}");
                rejected++;
                continue;
            }
            catch (UnsupportedException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=core-unsupported detail={error.Message}");
                rejected++;
                continue;
            }
            catch (CapacityException error)
            {
                Console.Error.WriteLine(
                    $"root={name} search=reject reason=capacity region={error.Region} detail={error.Message}");
                rejected++;
                continue;
            }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=invalid-operation detail={error.Message}");
                rejected++;
                continue;
            }
            catch (TimeoutException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=timeout detail={error.Message}");
                rejected++;
                continue;
            }
            catch (OperationCanceledException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=canceled detail={error.Message}");
                rejected++;
                continue;
            }
            catch (IOException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=io detail={error.Message}");
                rejected++;
                continue;
            }
            catch (JsonException error)
            {
                Console.Error.WriteLine($"root={name} search=reject reason=json detail={error.Message}");
                rejected++;
                continue;
            }

            if (requestSame && resultSame)
            {
                same++;
                Console.WriteLine($"root={name} search=same sreq=same sres=same");
            }
            else
            {
                different++;
                Console.WriteLine(
                    $"root={name} search=diff sreq={(requestSame ? "same" : "diff")} "
                    + $"sres={(resultSame ? "same" : "diff")}");
                if (detail != null)
                    Console.WriteLine(detail);
            }
        }
        Console.WriteLine(
            $"search-compare roots={directories.Length} same={same} diff={different} "
            + $"reject={rejected} skipped={skipped}");
        return different == 0 && rejected == 0 ? 0 : 1;
    }

    private static bool AssembleFromNative(
        MainLoopContext loop,
        string data,
        string corpora,
        string root,
        SearchLiveStatus status,
        byte[] expectedRequest,
        out string detail)
    {
        detail = "";
        OfflineRequest request = Program.LoadRequest(data, corpora, root);
        try
        {
            Task<CombatState> build = ScenarioBuilder.BuildAsync(
                new OfflineCombatWaiter(loop), request);
            loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), $"搜索对照建局 {root}");
            CombatState combat = build.GetAwaiter().GetResult();
            Player player = LocalContext.GetMe(combat)
                ?? throw new InvalidOperationException("找不到本地玩家。");
            if (combat.RunState is not RunState runState)
                throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
            Rules rules = RulesCompiler.Compile(combat, player, runState);
            Battle battle = BattleImporter.Import(combat, player, rules, ImportOptions.Default);
            SearchPolicy policy = SearchPolicyOverrides.ApplyOverrides(
                SearchPolicyCapture.Capture(combat, player), request, player);
            SearchPerformanceTier tier = SearchPerformanceProfile.ParseTier(status.SearchTier);
            SearchCase assembled = SearchRequestCompiler.CompileRequest(
                combat, player, rules, battle, policy, EvaluationRootFacts.Default,
                SearchPerformanceProfile.For(tier), status.DeterministicSearch, 0);
            byte[] actual = SearchCodec.EncodeRequest(assembled);
            bool same = actual.AsSpan().SequenceEqual(expectedRequest);
            if (!same)
                detail = "离线组装的 SREQ 与游戏 SREQ 不同";
            return same;
        }
        finally
        {
            RunManager.Instance.CleanUp(true);
        }
    }
}
