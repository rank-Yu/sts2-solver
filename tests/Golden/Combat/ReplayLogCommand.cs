using System.IO.Compression;
using System.Text.Json;
using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Reruns the searches of an exported combat log (zip or folder) through the production coordinator.
/// The coordinator clock never advances; a member the game stopped on its deadline is stopped here at the
/// recorded deadline poll, and a member stopped by its node cap or by finishing runs to the same end. Each
/// search reports whether the selected result (SRES) and the members' node counts match the recording.
/// </summary>
internal static class ReplayLogCommand
{
    private sealed record Recorded(string Combat, string Name, JsonElement Log, byte[]? Request, byte[]? Result);

    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：Golden replay-log <zip|folder> [--threads N] [--search NNN]");
            return 2;
        }
        string input = Path.GetFullPath(args[0]);
        int? threads = null;
        string? only = null;
        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--threads":
                    if (++index >= args.Length || !int.TryParse(args[index], out int value) || value <= 0)
                        throw new InvalidDataException("--threads 需要正整数。");
                    threads = value;
                    break;
                case "--search":
                    if (++index >= args.Length)
                        throw new InvalidDataException("--search 需要搜索编号。");
                    only = args[index];
                    break;
                default:
                    throw new InvalidDataException($"未知参数：{args[index]}");
            }
        }

        List<Recorded> searches = Load(input);
        if (only != null)
            searches = [.. searches.Where(search => search.Name == $"search-{only}")];
        if (searches.Count == 0)
            throw new InvalidDataException($"{input} 里没有搜索记录。");
        int same = 0, different = 0, skipped = 0;
        foreach (Recorded search in searches)
        {
            string verdict = ReplayOne(search, threads);
            if (verdict == "same") same++;
            else if (verdict == "different") different++;
            else skipped++;
        }
        Console.WriteLine($"REPLAY_LOG searches={searches.Count} same={same} different={different} skipped={skipped}");
        return different == 0 ? 0 : 1;
    }

    private static string ReplayOne(Recorded search, int? threadOverride)
    {
        JsonElement log = search.Log;
        string kind = log.GetProperty("kind").GetString() ?? "?";
        int turn = log.GetProperty("turn").GetInt32();
        bool resumed = log.GetProperty("resumed").GetBoolean();
        string label = $"combat={search.Combat} {search.Name} kind={kind} turn={turn} resumed={resumed}";
        if (search.Request == null || log.GetProperty("plan").ValueKind != JsonValueKind.Object)
        {
            Console.WriteLine($"REPLAY {label} verdict=skipped reason=no_request");
            return "skipped";
        }
        SearchCase searchCase = SearchCodec.DecodeRequest(search.Request);
        SearchPlan plan = ReadPlan(log.GetProperty("plan"));
        int threads = threadOverride ?? Math.Max(1, log.GetProperty("threads").GetInt32());
        List<long> polls = [];
        List<long> recordedExpanded = [];
        foreach (JsonElement member in log.GetProperty("members").EnumerateArray())
        {
            if (!member.GetProperty("ran").GetBoolean())
                continue;
            recordedExpanded.Add(member.GetProperty("expandedNodes").GetInt64());
            polls.Add(member.GetProperty("deadlinePoll").GetInt64());
        }

        (CombatSearchResult outcome, List<long> replayedExpanded) = Replay(searchCase, plan, polls, threads);
        byte[]? replayed = outcome.Beam == null ? null : SearchCodec.EncodeResult(outcome.Beam);
        bool resultSame = replayed == null
            ? search.Result == null
            : search.Result != null && replayed.AsSpan().SequenceEqual(search.Result);
        int recordedSelected = log.GetProperty("selectedMember").GetInt32();
        int matchingMembers = recordedExpanded.Zip(replayedExpanded).Count(static pair => pair.First == pair.Second);
        bool membersSame = matchingMembers == recordedExpanded.Count
            && replayedExpanded.Count == recordedExpanded.Count;
        bool same = resultSame && outcome.SelectedMember == recordedSelected;
        string? difference = resultSame || replayed == null || search.Result == null
            ? null
            : SearchVerifier.FirstDifference(SearchCodec.DecodeResult(search.Result), outcome.Beam!);
        Console.WriteLine($"REPLAY {label} verdict={(same ? "same" : "different")} "
            + $"result={(resultSame ? "same" : "different")} "
            + $"selected={recordedSelected}->{outcome.SelectedMember} "
            + $"members_ran={recordedExpanded.Count}->{replayedExpanded.Count} "
            + $"members_expanded_same={matchingMembers} "
            + $"victory={outcome.Beam?.BestIsVictory.ToString() ?? "-"} "
            + $"route={outcome.Beam?.BestRoute.Length.ToString() ?? "-"}"
            + (membersSame ? "" : " note=member_counts_differ")
            + (difference == null ? "" : $" first_difference={difference}"));
        return same ? "same" : "different";
    }

    /// <summary>
    /// Runs the production coordinator on a recorded request with a clock that never advances. The
    /// k-th member that runs finds its deadline expired from deadline poll <c>deadlinePolls[k]</c> on,
    /// the poll that stopped it live; a member whose deadline never expired (0) ends on its node cap or
    /// naturally, as it did live. Polls of a multi-threaded solve interleave, so only a single-threaded
    /// replay is exact.
    /// </summary>
    internal static (CombatSearchResult Outcome, List<long> Expanded) Replay(SearchCase searchCase,
        SearchPlan plan, IReadOnlyList<long> deadlinePolls, int threads)
    {
        List<long> replayedExpanded = [];
        int runIndex = 0;
        ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage storage,
            CancellationToken token)
        {
            long poll = runIndex < deadlinePolls.Count ? deadlinePolls[runIndex] : 0;
            runIndex++;
            BeamSolver? solver = null;
            BeamOptions options = member.Options with
            {
                SearchThreads = threads,
                SoftTimeBudget = TimeSpan.MaxValue,
                Pacer = null,
                Trace = null,
                Progress = null,
                DeadlineExpired = poll > 0
                    ? () => solver!.DeadlinePolls >= poll
                    : member.Options.DeadlineExpired,
            };
            solver = new BeamSolver(member.Rules, options);
            BeamResult beam = solver.Solve(member.Battles, storage, token);
            replayedExpanded.Add(beam.ExpandedNodesTotal);
            return ValueTask.FromResult(new SearchMemberMeasurement(beam, "ok", null, 0));
        }

        CombatSearchResult outcome = CombatSearchCoordinator.RunAsync(
                new CombatSearchRequest(searchCase, plan), new SearchStorage(), RunMember,
                CancellationToken.None, clock: static () => 0)
            .AsTask().GetAwaiter().GetResult();
        return (outcome, replayedExpanded);
    }

    private static SearchPlan ReadPlan(JsonElement plan)
        => new(plan.GetProperty("baselineWidth").GetInt32(), plan.GetProperty("narrowWidth").GetInt32(),
            plan.GetProperty("wideWidth").GetInt32(), plan.GetProperty("offensiveWidth").GetInt32(),
            plan.GetProperty("memoryBytes").GetInt64())
        {
            MillisecondsPerWidth = plan.GetProperty("millisecondsPerWidth").GetInt32(),
            NodesPerWidth = plan.GetProperty("nodesPerWidth").GetInt32(),
            QueenStrategies = plan.GetProperty("queenStrategies").GetBoolean(),
            SetupStrategies = plan.GetProperty("setupStrategies").GetBoolean(),
            ContinuousSetupStrategies = plan.GetProperty("continuousSetupStrategies").GetBoolean(),
        };

    /// <summary>Reads every search-NNN record of a zip or folder, ordered by combat and index.</summary>
    private static List<Recorded> Load(string input)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        if (File.Exists(input))
        {
            using ZipArchive archive = ZipFile.OpenRead(input);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Length == 0 && entry.FullName.EndsWith('/'))
                    continue;
                using Stream stream = entry.Open();
                using MemoryStream bytes = new();
                stream.CopyTo(bytes);
                files[entry.FullName.Replace('\\', '/')] = bytes.ToArray();
            }
        }
        else if (Directory.Exists(input))
        {
            foreach (string path in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                files[Path.GetRelativePath(input, path).Replace('\\', '/')] = File.ReadAllBytes(path);
        }
        else
        {
            throw new InvalidDataException($"找不到 {input}。");
        }

        List<Recorded> searches = [];
        foreach (string key in files.Keys.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(key);
            if (!name.StartsWith("search-", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
                continue;
            string folder = key.Contains('/') ? key[..key.LastIndexOf('/')] : "";
            string stem = key[..^".json".Length];
            using JsonDocument document = JsonDocument.Parse(files[key]);
            searches.Add(new Recorded(folder.Length == 0 ? "." : Path.GetFileName(folder), name[..^".json".Length],
                document.RootElement.Clone(),
                files.TryGetValue(stem + ".sreq.br", out byte[]? request) ? Decompress(request) : null,
                files.TryGetValue(stem + ".sres.br", out byte[]? result) ? Decompress(result) : null));
        }
        return searches;
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using MemoryStream input = new(compressed);
        using BrotliStream brotli = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        brotli.CopyTo(output);
        return output.ToArray();
    }
}
