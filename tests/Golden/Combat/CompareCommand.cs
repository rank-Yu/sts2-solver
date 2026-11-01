using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Report-only A/B comparison of the current solver against the frozen D2 results. This command
/// never fails on a quality mismatch: it re-solves every request and reports the solver's own final
/// route quality (better/same/worse), expanded nodes, transitions and wall time. The default
/// <c>verify</c> command is unchanged and remains strict.
/// </summary>
internal static class CompareCommand
{
    private enum Quality
    {
        Better,
        Same,
        Worse,
        Unknown,
    }

    private sealed record Row(
        string Hash,
        Quality Quality,
        bool RouteEqual,
        int OldExpanded,
        int NewExpanded,
        int OldTransitions,
        int NewTransitions,
        double OldScore,
        double NewScore,
        bool OldVictory,
        bool NewVictory,
        double Milliseconds,
        long ManagedHeapPeak,
        long SearchMemoryUsed,
        long RouteNodeStorage,
        int RouteNodePeak,
        SearchEquivalenceDiagnostics Equivalence);

    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("缺少数据目录。");
            return 2;
        }
        string dataDirectory = args[0];
        int jobs = Environment.ProcessorCount;
        int searchThreads = 1;
        string config = "all";
        string? output = null;
        string? only = null;
        bool verify = false;
        bool measureMemory = false;
        int shard = 0, shards = 1;
        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--jobs":
                    jobs = ParsePositiveInt(Next(args, ref index, "--jobs"), "--jobs");
                    break;
                case "--search-threads":
                    searchThreads = ParsePositiveInt(
                        Next(args, ref index, "--search-threads"), "--search-threads");
                    break;
                case "--config":
                    config = Next(args, ref index, "--config");
                    if (config is not ("none" or "play" or "choice" or "all"
                        or "choice-fill" or "all-fill"))
                        throw new InvalidDataException(
                            $"--config 只能是 none、play、choice、all、choice-fill 或 all-fill，"
                            + $"实际为 {config}。");
                    break;
                case "--out":
                    output = Next(args, ref index, "--out");
                    break;
                case "--only":
                    only = Next(args, ref index, "--only");
                    break;
                case "--shard":
                    (shard, shards) = ParseShard(Next(args, ref index, "--shard"));
                    break;
                case "--verify":
                    verify = true;
                    break;
                case "--memory":
                    measureMemory = true;
                    break;
                default:
                    throw new InvalidDataException($"未知参数：{args[index]}");
            }
        }
        string requestDirectory = Path.Combine(dataDirectory, "d2", "requests");
        string expectedDirectory = Path.Combine(dataDirectory, "d2", "expected");
        if (!Directory.Exists(requestDirectory) || !Directory.Exists(expectedDirectory))
            throw new InvalidDataException("D2 requests/expected 目录不完整。");

        // Product switches. `play` is the retained exact hand-run deduplication; the evaluated
        // approximate variant is not in the product and is not selectable here.
        SearchVerificationHooks.DedupeExactPlayActions = config is "play" or "all" or "all-fill";
        SearchVerificationHooks.DedupeEquivalentChoicesByClass =
            config is "choice" or "all" or "choice-fill" or "all-fill";
        SearchVerificationHooks.ChoiceFillPhysicalVariants =
            config is "choice-fill" or "all-fill";
        SearchVerificationHooks.VerifySearchEquivalence = verify;

        string[] paths = shards > 1
            ? SearchVerifier.BuildPlans(dataDirectory, shards)[shard].Selected
                .Select(static request => request.Path)
                .Where(path => only == null
                    || Path.GetFileName(path).StartsWith(only, StringComparison.Ordinal))
                .ToArray()
            : Directory.EnumerateFiles(requestDirectory, "*.request.br")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Where(path => only == null
                    || Path.GetFileName(path).StartsWith(only, StringComparison.Ordinal))
                .ToArray();
        Console.WriteLine($"COMPARE_START config={config} requests={paths.Length} jobs={jobs} "
            + $"searchThreads={searchThreads} verify={verify} shard={shard}/{shards}");
        ConcurrentBag<Row> rows = new();
        Stopwatch watch = Stopwatch.StartNew();
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = jobs }, path =>
        {
            string hash = Path.GetFileName(path)[..^".request.br".Length];
            SearchCase search = SearchCodec.DecodeRequest(GoldenFiles.ReadCompressed(path));
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(search.Options with
            {
                SoftTimeBudget = TimeSpan.MaxValue,
                Pacer = null,
                Trace = null,
                SearchThreads = searchThreads,
            });
            BeamResult expected = SearchCodec.DecodeResult(
                GoldenFiles.ReadCompressed(Path.Combine(expectedDirectory, hash + ".result.br")));
            BeamSolver solver = new(search.Rules, options);
            Stopwatch solveWatch = Stopwatch.StartNew();
            long managedPeak = 0;
            BeamResult actual;
            if (measureMemory)
            {
                Task<BeamResult> solve = Task.Run(
                    () => solver.Solve(search.Battles, CancellationToken.None));
                while (!solve.IsCompleted)
                {
                    SampleHeap(ref managedPeak);
                    Thread.Sleep(2);
                }
                SampleHeap(ref managedPeak);
                actual = solve.GetAwaiter().GetResult();
            }
            else
            {
                actual = solver.Solve(search.Battles, CancellationToken.None);
            }
            solveWatch.Stop();
            Quality quality = Quality.Unknown;
            if (solver.TryCompareResultQuality(search.Battles, expected, actual, out int comparison))
            {
                quality = comparison < 0 ? Quality.Worse
                    : comparison > 0 ? Quality.Better
                    : Quality.Same;
            }
            rows.Add(new Row(hash, quality, RouteEquals(expected.BestRoute, actual.BestRoute),
                expected.ExpandedNodes, actual.ExpandedNodes, expected.Transitions, actual.Transitions,
                expected.BestScore, actual.BestScore, expected.BestIsVictory, actual.BestIsVictory,
                solveWatch.Elapsed.TotalMilliseconds, managedPeak,
                actual.SearchMemoryUsedBytes, actual.RouteNodeStorageBytes,
                actual.RouteNodePeakCount, actual.Equivalence));
        });
        watch.Stop();
        Row[] ordered = [.. rows.OrderBy(static row => row.Hash, StringComparer.Ordinal)];
        if (output != null)
        {
            using StreamWriter writer = new(output, false, new UTF8Encoding(false));
            writer.WriteLine("config,hash,quality,routeEqual,oldExpanded,newExpanded,oldTransitions,"
                + "newTransitions,oldScore,newScore,oldVictory,newVictory,ms,managedHeapPeak,"
                + "searchMemoryUsed,routeNodeStorage,routeNodePeak,playDeduped,"
                + "playVerifiedExact,playVerifiedApprox,playVerifiedApproxEqual,playVerifiedUnresolved,"
                + "choiceByClassRequests,choiceVerifiedExact,choiceVerifiedApprox,"
                + "choiceVerifiedApproxEqual,choiceVerifiedUnresolved");
            foreach (Row row in ordered)
            {
                SearchEquivalenceDiagnostics e = row.Equivalence;
                writer.WriteLine(string.Join(',', new[]
                {
                    config, row.Hash, row.Quality.ToString().ToLowerInvariant(),
                    row.RouteEqual ? "1" : "0",
                    row.OldExpanded.ToString(CultureInfo.InvariantCulture),
                    row.NewExpanded.ToString(CultureInfo.InvariantCulture),
                    row.OldTransitions.ToString(CultureInfo.InvariantCulture),
                    row.NewTransitions.ToString(CultureInfo.InvariantCulture),
                    row.OldScore.ToString("R", CultureInfo.InvariantCulture),
                    row.NewScore.ToString("R", CultureInfo.InvariantCulture),
                    row.OldVictory ? "1" : "0", row.NewVictory ? "1" : "0",
                    row.Milliseconds.ToString("F1", CultureInfo.InvariantCulture),
                    row.ManagedHeapPeak.ToString(CultureInfo.InvariantCulture),
                    row.SearchMemoryUsed.ToString(CultureInfo.InvariantCulture),
                    row.RouteNodeStorage.ToString(CultureInfo.InvariantCulture),
                    row.RouteNodePeak.ToString(CultureInfo.InvariantCulture),
                    e.PlayDeduped.ToString(CultureInfo.InvariantCulture),
                    e.PlayVerifiedExact.ToString(CultureInfo.InvariantCulture),
                    e.PlayVerifiedApprox.ToString(CultureInfo.InvariantCulture),
                    e.PlayVerifiedApproxEqual.ToString(CultureInfo.InvariantCulture),
                    e.PlayVerifiedUnresolved.ToString(CultureInfo.InvariantCulture),
                    e.ChoiceByClassRequests.ToString(CultureInfo.InvariantCulture),
                    e.ChoiceVerifiedExact.ToString(CultureInfo.InvariantCulture),
                    e.ChoiceVerifiedApprox.ToString(CultureInfo.InvariantCulture),
                    e.ChoiceVerifiedApproxEqual.ToString(CultureInfo.InvariantCulture),
                    e.ChoiceVerifiedUnresolved.ToString(CultureInfo.InvariantCulture),
                }));
            }
            Console.WriteLine($"COMPARE_WROTE {output} rows={ordered.Length}");
        }

        int better = ordered.Count(static row => row.Quality == Quality.Better);
        int same = ordered.Count(static row => row.Quality == Quality.Same);
        int worse = ordered.Count(static row => row.Quality == Quality.Worse);
        int unknown = ordered.Count(static row => row.Quality == Quality.Unknown);
        int routeEqual = ordered.Count(static row => row.RouteEqual);
        long oldExpanded = ordered.Sum(static row => (long)row.OldExpanded);
        long newExpanded = ordered.Sum(static row => (long)row.NewExpanded);
        long oldTransitions = ordered.Sum(static row => (long)row.OldTransitions);
        long newTransitions = ordered.Sum(static row => (long)row.NewTransitions);
        double sumMs = ordered.Sum(static row => row.Milliseconds);
        SearchEquivalenceDiagnostics equivalence = ordered
            .Select(static row => row.Equivalence)
            .Aggregate(SearchEquivalenceDiagnostics.Empty,
                static (total, value) => total.Add(value));
        Console.WriteLine($"COMPARE_CONFIG config={config} requests={ordered.Length} "
            + $"better={better} same={same} worse={worse} unknown={unknown} routeEqual={routeEqual} "
            + $"oldExpanded={oldExpanded} newExpanded={newExpanded} "
            + $"expandedPct={Percent(newExpanded - oldExpanded, oldExpanded):F3} "
            + $"oldTransitions={oldTransitions} newTransitions={newTransitions} "
            + $"transitionsPct={Percent(newTransitions - oldTransitions, oldTransitions):F3} "
            + $"solveMsSum={sumMs:F0} wall={watch.Elapsed.TotalSeconds:F1}s jobs={jobs} "
            + $"searchThreads={searchThreads}");
        Console.WriteLine($"COMPARE_EQUIVALENCE config={config} playDeduped={equivalence.PlayDeduped} "
            + $"playVerifiedExact={equivalence.PlayVerifiedExact} "
            + $"playVerifiedApprox={equivalence.PlayVerifiedApprox} "
            + $"playVerifiedApproxEqual={equivalence.PlayVerifiedApproxEqual} "
            + $"playVerifiedUnresolved={equivalence.PlayVerifiedUnresolved} "
            + $"choiceByClassRequests={equivalence.ChoiceByClassRequests} "
            + $"choiceVerifiedExact={equivalence.ChoiceVerifiedExact} "
            + $"choiceVerifiedApprox={equivalence.ChoiceVerifiedApprox} "
            + $"choiceVerifiedApproxEqual={equivalence.ChoiceVerifiedApproxEqual} "
            + $"choiceVerifiedUnresolved={equivalence.ChoiceVerifiedUnresolved}");
        foreach (Row row in ordered.Where(static row => row.Quality is Quality.Worse or Quality.Unknown))
        {
            Console.WriteLine($"COMPARE_{row.Quality.ToString().ToUpperInvariant()} config={config} "
                + $"hash={row.Hash} oldExpanded={row.OldExpanded} newExpanded={row.NewExpanded} "
                + $"oldScore={row.OldScore:R} newScore={row.NewScore:R} "
                + $"oldVictory={row.OldVictory} newVictory={row.NewVictory} "
                + $"routeEqual={row.RouteEqual} oldTransitions={row.OldTransitions} "
                + $"newTransitions={row.NewTransitions} ms={row.Milliseconds:F0}");
        }
        return 0;
    }

    private static bool RouteEquals(PlayerAction[] left, PlayerAction[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            if (!GoldenFiles.ActionEquals(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static void SampleHeap(ref long peak)
    {
        long current = GC.GetTotalMemory(forceFullCollection: false);
        if (current > peak)
            peak = current;
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (++index >= args.Length)
            throw new InvalidDataException($"{name} 缺少参数值。");
        return args[index];
    }

    private static (int Shard, int Shards) ParseShard(string text)
    {
        string[] parts = text.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int shard)
            || !int.TryParse(parts[1], out int shards)
            || shards <= 0 || shard < 0 || shard >= shards)
            throw new InvalidDataException($"--shard 需要 i/n 且 0 <= i < n，实际为 {text}。");
        return (shard, shards);
    }

    private static int ParsePositiveInt(string text, string name)
    {
        if (!int.TryParse(text, out int value) || value <= 0)
            throw new InvalidDataException($"{name} 需要正整数，实际为 {text}。");
        return value;
    }

    private static double Percent(long delta, long baseline)
        => baseline == 0 ? 0 : 100.0 * delta / baseline;
}
