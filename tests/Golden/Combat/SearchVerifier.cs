using System.Collections.Concurrent;
using System.Security.Cryptography;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Golden;

internal sealed record SearchSummary(int Selected, int Equal, int Mismatch, long Expanded, long PrefixChecks,
    long ThreatChecks, long PrefixShared, long PrefixUnshared, long PrefixReuse, long PrefixChoiceUnshared,
    long PrefixExtraTurnUnshared, long PrefixNotInProgress, long PlayDeduped, long PlayVerifiedExact,
    long PlayVerifiedApprox, long PlayVerifiedApproxEqual, long PlayVerifiedUnresolved,
    long ChoiceByClassRequests, long ChoiceVerifiedExact, long ChoiceVerifiedApprox,
    long ChoiceVerifiedApproxEqual, long ChoiceVerifiedUnresolved, long WorkspacePeak,
    long WorkspaceGrowablePeak, long WorkspaceParents, long WorkspaceOverflowParents,
    long WorkspaceDirectMilliseconds);

internal sealed record SearchRequestEntry(string Hash, string Path, int Weight);

internal sealed record SearchShardPlan(
    SearchRequestEntry[] Selected,
    long SelectedWeight);

internal static class SearchVerifier
{
    /// <summary>
    /// Loads every D2 request, takes its deterministic weight from the expected result's
    /// expanded-node count, and assigns requests to shards with longest-processing-time first:
    /// descending weight (ties by request hash ordinal), always to the currently lightest shard
    /// (ties by lower shard index). Each plan's Selected array stays in descending weight order.
    /// </summary>
    internal static SearchShardPlan[] BuildPlans(string dataDirectory, int shards)
    {
        string requestDirectory = Path.Combine(dataDirectory, "d2", "requests");
        string expectedDirectory = Path.Combine(dataDirectory, "d2", "expected");
        if (!Directory.Exists(requestDirectory) || !Directory.Exists(expectedDirectory))
            throw new InvalidDataException("搜索回归 requests/expected 目录不完整。");

        string[] paths = Directory.EnumerateFiles(requestDirectory, "*.request.br")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        SearchRequestEntry[] requests = new SearchRequestEntry[paths.Length];
        for (int index = 0; index < paths.Length; index++)
        {
            string path = paths[index];
            string hash = Path.GetFileName(path)[..^".request.br".Length];
            string expectedPath = Path.Combine(expectedDirectory, hash + ".result.br");
            if (!File.Exists(expectedPath))
                throw new InvalidDataException($"搜索回归请求 {hash} 缺少期望结果。");
            BeamResult expected = SearchCodec.DecodeResult(
                GoldenFiles.ReadCompressed(expectedPath));
            if (expected.ExpandedNodes < 0)
                throw new InvalidDataException($"搜索回归请求 {hash} 的展开节点数为负。");
            requests[index] = new SearchRequestEntry(hash, path, expected.ExpandedNodes);
        }

        SearchRequestEntry[] ordered = requests
            .OrderByDescending(static request => request.Weight)
            .ThenBy(static request => request.Hash, StringComparer.Ordinal)
            .ToArray();
        List<SearchRequestEntry>[] buckets = new List<SearchRequestEntry>[shards];
        long[] weights = new long[shards];
        for (int index = 0; index < shards; index++)
            buckets[index] = [];
        foreach (SearchRequestEntry request in ordered)
        {
            int target = 0;
            for (int candidate = 1; candidate < shards; candidate++)
            {
                if (weights[candidate] < weights[target])
                    target = candidate;
            }
            buckets[target].Add(request);
            weights[target] += request.Weight;
        }

        SearchShardPlan[] plans = new SearchShardPlan[shards];
        for (int index = 0; index < shards; index++)
            plans[index] = new SearchShardPlan([.. buckets[index]], weights[index]);
        return plans;
    }

    internal static SearchSummary Run(string dataDirectory, int jobs, int shard, int shards,
        int searchThreads, int workspaceBufferBytes, bool workspaceStats, bool progress,
        int progressThrottleMilliseconds)
    {
        WorkspaceStatsCollector? stats = workspaceStats ? new WorkspaceStatsCollector() : null;
        BeamSolver.ParentWorkspaceObserver = stats is null ? null : stats.Add;
        try
        {
            return RunCore(dataDirectory, jobs, shard, shards, searchThreads,
                workspaceBufferBytes, stats, progress, progressThrottleMilliseconds);
        }
        finally
        {
            BeamSolver.ParentWorkspaceObserver = null;
        }
    }

    private static SearchSummary RunCore(string dataDirectory, int jobs, int shard, int shards,
        int searchThreads, int workspaceBufferBytes, WorkspaceStatsCollector? stats,
        bool progress, int progressThrottleMilliseconds)
    {
        SearchShardPlan plan = BuildPlans(dataDirectory, shards)[shard];
        SearchRequestEntry[] selected = plan.Selected;
        if (selected.Length == 0)
            throw new InvalidDataException($"--shard {shard}/{shards} 没有选到 搜索回归请求。");
        Console.WriteLine($"SEARCH_SHARD shard={shard}/{shards} requests={selected.Length} "
            + $"weight={plan.SelectedWeight} jobs={jobs} searchThreads={searchThreads} "
            + $"progress={(progress ? progressThrottleMilliseconds.ToString() : "off")}");

        int equal = 0, completed = 0;
        long expanded = 0;
        long prefixChecks = 0;
        long threatChecks = 0;
        long prefixShared = 0, prefixUnshared = 0, prefixReuse = 0;
        long prefixChoiceUnshared = 0, prefixExtraTurnUnshared = 0, prefixNotInProgress = 0;
        long playDeduped = 0, playVerifiedExact = 0, playVerifiedApprox = 0;
        long playVerifiedApproxEqual = 0, playVerifiedUnresolved = 0;
        long choiceByClassRequests = 0, choiceVerifiedExact = 0, choiceVerifiedApprox = 0;
        long choiceVerifiedApproxEqual = 0, choiceVerifiedUnresolved = 0;
        long workspacePeak = 0;
        long workspaceGrowablePeak = 0;
        long workspaceParents = 0;
        long workspaceOverflowParents = 0;
        long workspaceDirectMilliseconds = 0;
        object workspaceGate = new();
        void VerifyOne(SearchRequestEntry request)
        {
            string path = request.Path;
            string hash = request.Hash;
            string expectedPath = Path.Combine(dataDirectory, "d2", "expected", hash + ".result.br");
            if (!File.Exists(expectedPath))
                throw new InvalidDataException($"搜索回归请求 {hash} 缺少期望结果。");

            byte[] requestBytes = GoldenFiles.ReadCompressed(path);
            string requestHash = Convert.ToHexString(SHA256.HashData(requestBytes)).ToLowerInvariant();
            if (requestHash != hash)
                throw new InvalidDataException($"搜索回归请求 {hash} 的 SHA-256 是 {requestHash}。");

            SearchCase search = SearchCodec.DecodeRequest(requestBytes);
            SearchProgressChannel? progressChannel = progress
                ? new SearchProgressChannel(progressThrottleMilliseconds)
                {
                    Published = static _ => { },
                }
                : null;
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(search.Options with
            {
                SoftTimeBudget = TimeSpan.MaxValue,
                Pacer = null,
                Trace = null,
                SearchThreads = searchThreads,
                WorkspaceBufferLimitBytes = workspaceBufferBytes,
                Progress = progressChannel,
            });
            BeamResult actual = new BeamSolver(search.Rules, options)
                .Solve(search.Battles, CancellationToken.None);
            BeamResult expected = SearchCodec.DecodeResult(
                GoldenFiles.ReadCompressed(expectedPath));
            string? difference = FirstDifference(expected, actual);
            if (difference != null)
            {
                throw new InvalidDataException(
                    $"搜索回归请求 {hash} 的期望结果不一致：{difference}");
            }
            Interlocked.Increment(ref equal);
            Interlocked.Add(ref expanded, actual.ExpandedNodes);
            Interlocked.Add(ref prefixChecks, actual.EndTurnPrefixVerified);
            Interlocked.Add(ref threatChecks, actual.ThreatProjectionVerified);
            Interlocked.Add(ref prefixShared, actual.EndTurnPrefixShared);
            Interlocked.Add(ref prefixUnshared, actual.EndTurnPrefixUnshared);
            Interlocked.Add(ref prefixReuse, actual.EndTurnPrefixReusedLeaves);
            Interlocked.Add(ref prefixChoiceUnshared, actual.EndTurnPrefixChoiceUnshared);
            Interlocked.Add(ref prefixExtraTurnUnshared, actual.EndTurnPrefixExtraTurnUnshared);
            Interlocked.Add(ref prefixNotInProgress, actual.EndTurnPrefixNotInProgress);
            SearchEquivalenceDiagnostics equivalence = actual.Equivalence;
            Interlocked.Add(ref playDeduped, equivalence.PlayDeduped);
            Interlocked.Add(ref playVerifiedExact, equivalence.PlayVerifiedExact);
            Interlocked.Add(ref playVerifiedApprox, equivalence.PlayVerifiedApprox);
            Interlocked.Add(ref playVerifiedApproxEqual, equivalence.PlayVerifiedApproxEqual);
            Interlocked.Add(ref playVerifiedUnresolved, equivalence.PlayVerifiedUnresolved);
            Interlocked.Add(ref choiceByClassRequests, equivalence.ChoiceByClassRequests);
            Interlocked.Add(ref choiceVerifiedExact, equivalence.ChoiceVerifiedExact);
            Interlocked.Add(ref choiceVerifiedApprox, equivalence.ChoiceVerifiedApprox);
            Interlocked.Add(ref choiceVerifiedApproxEqual, equivalence.ChoiceVerifiedApproxEqual);
            Interlocked.Add(ref choiceVerifiedUnresolved, equivalence.ChoiceVerifiedUnresolved);
            if (actual.SearchWorkspacePeakBytes > 0)
            {
                lock (workspaceGate)
                {
                    if (actual.SearchWorkspacePeakBytes > workspacePeak)
                        workspacePeak = actual.SearchWorkspacePeakBytes;
                    if (actual.SearchWorkspaceGrowablePeakBytes > workspaceGrowablePeak)
                        workspaceGrowablePeak = actual.SearchWorkspaceGrowablePeakBytes;
                }
            }
            Interlocked.Add(ref workspaceParents, actual.WorkspaceParents);
            Interlocked.Add(ref workspaceOverflowParents, actual.WorkspaceOverflowParents);
            Interlocked.Add(ref workspaceDirectMilliseconds,
                (long)actual.WorkspaceDirectDuration.TotalMilliseconds);
            int done = Interlocked.Increment(ref completed);
            if (done % 100 == 0)
                Console.WriteLine($"SEARCH progress={done}/{selected.Length}");
        }

        // NoBuffering makes the orderable partitioner hand out one item at a time in enumeration
        // order, so the heaviest request in the shard starts first.
        Parallel.ForEach(
            Partitioner.Create(selected, EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            VerifyOne);
        stats?.Print();
        return new SearchSummary(selected.Length, equal, selected.Length - equal, expanded, prefixChecks,
            threatChecks, prefixShared, prefixUnshared, prefixReuse, prefixChoiceUnshared,
            prefixExtraTurnUnshared, prefixNotInProgress, playDeduped, playVerifiedExact,
            playVerifiedApprox, playVerifiedApproxEqual, playVerifiedUnresolved,
            choiceByClassRequests, choiceVerifiedExact, choiceVerifiedApprox,
            choiceVerifiedApproxEqual, choiceVerifiedUnresolved, workspacePeak,
            workspaceGrowablePeak, workspaceParents, workspaceOverflowParents,
            workspaceDirectMilliseconds);
    }

    private static string? FirstDifference(in BeamResult expected, in BeamResult actual)
    {
        string? difference = Difference("BestRoute", expected.BestRoute, actual.BestRoute);
        if (difference != null)
            return difference;
        if (expected.TopRoutes.Count != actual.TopRoutes.Count)
            return $"TopRoutes count expected={expected.TopRoutes.Count} actual={actual.TopRoutes.Count}";
        for (int index = 0; index < expected.TopRoutes.Count; index++)
        {
            difference = Difference($"TopRoutes[{index}]", expected.TopRoutes[index], actual.TopRoutes[index]);
            if (difference != null)
                return difference;
        }
        if (expected.BestIsVictory != actual.BestIsVictory)
            return $"BestIsVictory expected={expected.BestIsVictory} actual={actual.BestIsVictory}";
        if (BitConverter.DoubleToInt64Bits(expected.BestScore) != BitConverter.DoubleToInt64Bits(actual.BestScore))
            return $"BestScore expected={expected.BestScore:R} actual={actual.BestScore:R}";
        if (expected.ExpandedNodes != actual.ExpandedNodes)
            return $"ExpandedNodes expected={expected.ExpandedNodes} actual={actual.ExpandedNodes}";
        if (expected.Transitions != actual.Transitions)
            return $"Transitions expected={expected.Transitions} actual={actual.Transitions}";
        if (expected.Duplicates != actual.Duplicates)
            return $"Duplicates expected={expected.Duplicates} actual={actual.Duplicates}";
        if (expected.TurnsSearched != actual.TurnsSearched)
            return $"TurnsSearched expected={expected.TurnsSearched} actual={actual.TurnsSearched}";
        if (expected.PrunedIncumbents != actual.PrunedIncumbents)
            return $"PrunedIncumbents expected={expected.PrunedIncumbents} actual={actual.PrunedIncumbents}";
        if (expected.CapacityPruned != actual.CapacityPruned)
            return $"CapacityPruned expected={expected.CapacityPruned} actual={actual.CapacityPruned}";
        difference = Difference(expected.CapacityDroppedChildren, actual.CapacityDroppedChildren);
        if (difference != null)
            return difference;
        if (expected.EndedCompactions != actual.EndedCompactions)
            return $"EndedCompactions expected={expected.EndedCompactions} actual={actual.EndedCompactions}";
        if (expected.EndedCompressedStates != actual.EndedCompressedStates)
            return $"EndedCompressedStates expected={expected.EndedCompressedStates} actual={actual.EndedCompressedStates}";
        if (expected.BestRootIndex != actual.BestRootIndex)
            return $"BestRootIndex expected={expected.BestRootIndex} actual={actual.BestRootIndex}";
        if (expected.PotionPolicySatisfied != actual.PotionPolicySatisfied)
            return $"PotionPolicySatisfied expected={expected.PotionPolicySatisfied} actual={actual.PotionPolicySatisfied}";
        return null;
    }

    private static string? Difference(string label, PlayerAction[] expected, PlayerAction[] actual)
    {
        if (expected.Length != actual.Length)
            return $"{label} count expected={expected.Length} actual={actual.Length}";
        for (int index = 0; index < expected.Length; index++)
        {
            if (!GoldenFiles.ActionEquals(expected[index], actual[index]))
                return $"{label}[{index}] expected={GoldenFiles.Describe(expected[index])} "
                    + $"actual={GoldenFiles.Describe(actual[index])}";
        }
        return null;
    }

    private static string? Difference(IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual)
    {
        if (expected.Count != actual.Count)
            return $"CapacityDroppedChildren count expected={expected.Count} actual={actual.Count}";
        foreach (string key in expected.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(key, out int value))
                return $"CapacityDroppedChildren missing key {key}";
            if (value != expected[key])
                return $"CapacityDroppedChildren[{key}] expected={expected[key]} actual={value}";
        }
        return null;
    }
}

internal sealed class WorkspaceStatsCollector
{
    private readonly ConcurrentBag<ParentWorkspaceMeasurement> _measurements = new();
    private readonly object _gate = new();
    private ParentWorkspaceMeasurement _largest;
    private int _direct;

    internal void Add(ParentWorkspaceMeasurement measurement)
    {
        if (measurement.Direct)
        {
            Interlocked.Increment(ref _direct);
            return;
        }
        _measurements.Add(measurement);
        lock (_gate)
        {
            if (measurement.PeakUsedBytes > _largest.PeakUsedBytes)
                _largest = measurement;
        }
    }

    internal void Print()
    {
        ParentWorkspaceMeasurement[] all = [.. _measurements];
        if (all.Length == 0)
        {
            Console.WriteLine($"WORKSPACE_STATS parents=0 direct={_direct}");
            return;
        }
        long[] sorted = [.. all.Select(static measurement => measurement.PeakUsedBytes)];
        Array.Sort(sorted);
        static long Percentile(long[] sorted, double percentile)
        {
            int rank = Math.Max(0, (int)Math.Ceiling(percentile / 100d * sorted.Length) - 1);
            return sorted[Math.Min(rank, sorted.Length - 1)];
        }
        static int Over(long[] sorted, long threshold)
            => sorted.Length - LowerBound(sorted, threshold);
        static int LowerBound(long[] sorted, long value)
        {
            int low = 0, high = sorted.Length;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (sorted[middle] <= value)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
        double Percent(int count) => 100d * count / sorted.Length;
        Console.WriteLine(
            $"WORKSPACE_STATS parents={sorted.Length} direct={_direct} "
            + $"p50={Percentile(sorted, 50)} p99={Percentile(sorted, 99)} "
            + $"p999={Percentile(sorted, 99.9)} max={sorted[^1]}");
        Console.WriteLine(
            $"WORKSPACE_STATS over1m={Over(sorted, 1 << 20)} ({Percent(Over(sorted, 1 << 20)):F3}%) "
            + $"over2m={Over(sorted, 2 << 20)} ({Percent(Over(sorted, 2 << 20)):F3}%) "
            + $"over4m={Over(sorted, 4 << 20)} ({Percent(Over(sorted, 4 << 20)):F3}%) "
            + $"over8m={Over(sorted, 8 << 20)} ({Percent(Over(sorted, 8 << 20)):F3}%) "
            + $"over16m={Over(sorted, 16 << 20)} ({Percent(Over(sorted, 16 << 20)):F3}%)");
        ParentWorkspaceMeasurement largest = _largest;
        Console.WriteLine(
            $"WORKSPACE_STATS largest children={largest.Children} terminals={largest.Terminals} "
            + $"choices={largest.Choices} duplicates={largest.Duplicates} "
            + $"peakUsed={largest.PeakUsedBytes} peakAllocated={largest.PeakAllocatedBytes}");
    }
}
