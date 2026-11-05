// --offline-combat: whole-combat replay inside the Sts2Solver kernel. The decision loop is the same
// CombatPilot the Mod runs; this host supplies the imported opening root, deterministic search
// and kernel stepping instead of native deployment.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Autoplay;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.TestHost;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal sealed record OfflineCombatSettings(
    string Data,
    string Corpora,
    string Output,
    SearchPerformanceTier Tier,
    SearchPerformanceProfile Profile,
    long MemoryBudgetBytes,
    int WarmupNodes,
    int SearchThreads,
    int WorkspaceBufferBytes,
    bool WorkspaceStats)
{
    /// <summary>List of exported request files to run instead of quality roots; null for quality mode.</summary>
    internal string? RequestsFile { get; init; }

    /// <summary>Capture each first-turn production member request instead of running the combat.</summary>
    internal string? CaptureD2 { get; init; }

    /// <summary>Production search setting applied to captured requests; null leaves it unlimited.</summary>
    internal int? BrightestFlameMaxHpLossLimit { get; init; }

    /// <summary>
    /// Run members on the production wall-clock deadline instead of the reproducible node budget.
    /// Timing studies only: results can depend on machine load.
    /// </summary>
    internal bool WallClock { get; init; }

    /// <summary>0.15 x 16 GiB, the fixed machine size the offline comparison assumes.</summary>
    internal static long DefaultMemoryBudgetBytes => (16L << 30) / 100 * 15 + (16L << 30) % 100 * 15 / 100;
}

/// <summary>
/// The member budget recorded in <c>summary.json</c> and every <c>full-combat.json</c>.
/// Reproducible mode replaces the production soft-time deadline with a fixed per-width node cap and
/// gives the coordinator a clock that never advances; wall-clock mode keeps the production timing.
/// </summary>
internal sealed record OfflineSearchBudget(
    string Mode,
    bool WallClock,
    int SearchThreads,
    int? MillisecondsPerWidth,
    int? MaxExpandedNodesPerWidth);

/// <summary>One exported request file and the output directory name used for its result.</summary>
internal sealed record OfflineRequestEntry(string Root, string Path);

internal sealed record OfflineCombatRootResult(
    string Root,
    string Status,
    string Outcome,
    int Turns,
    int PlayerHp,
    int Searches,
    long ElapsedMs,
    string? Error,
    int ExitCode,
    string? ExceptionType);

internal static class OfflineCombat
{
    /// <summary>
    /// Reproducible member cap in expanded nodes per point of beam width. The wall-clock baseline's
    /// weighted expansion rate was about 11.3 nodes/ms; 50 ms/width therefore corresponds to roughly
    /// 565 nodes/width. 512 is the nearest power of two and stays above every member that completed
    /// before its deadline in the Medium baseline, so only members that used to stop on time change.
    /// </summary>
    internal const int ReproducibleNodesPerWidth = 512;

    public static int Run(OfflineCombatSettings settings, string? root, string? rootsFile, int jobs)
    {
        Directory.CreateDirectory(settings.Output);
        if (settings.CaptureD2 != null)
            return CaptureD2.Run(settings, root, rootsFile, jobs);
        if (settings.RequestsFile != null)
            return RunRequestFiles(settings, root, jobs);
        if (root != null)
        {
            if (rootsFile != null)
            {
                Console.Error.WriteLine("--offline-combat 的 --root 与 --roots-file 不能同时使用。");
                return 2;
            }
            return RunRoot(settings, root);
        }

        string[] roots;
        if (rootsFile != null)
        {
            roots = File.ReadAllLines(rootsFile)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            roots = EnumerateQualityRoots(settings.Corpora);
        }
        if (roots.Length == 0)
        {
            Console.Error.WriteLine("--offline-combat 没有找到任何根。");
            return 2;
        }
        Console.WriteLine(
            $"offline-combat roots={roots.Length} jobs={jobs} tier={settings.Tier} " +
            $"widths={settings.Profile.BaselineWidth}/{settings.Profile.NarrowWidth}/{settings.Profile.WideWidth}/{settings.Profile.OffensiveWidth} " +
            $"time_ms_per_width={settings.Profile.MillisecondsPerWidth} " +
            $"budget={BudgetDescription(settings).Mode} out={settings.Output}");
        Stopwatch wall = Stopwatch.StartNew();
        List<OfflineCombatRootResult> results = [];
        object gate = new();
        int failed = 0;
        Parallel.ForEach(
            roots,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            name =>
            {
                (int code, string stdout, string stderr) = RunChild(settings, name, null);
                OfflineCombatRootResult? entry = ReadSummary(settings.Output, name);
                entry = entry is null
                    ? FailedRoot(name, code, stderr)
                    : entry with { ExitCode = code };
                lock (gate)
                {
                    results.Add(entry);
                    if (code != 0 || entry.Status is "failed" or "error" or "crashed")
                        failed++;
                }
                Console.WriteLine(
                    $"root={name} exit={code} status={entry.Status} outcome={entry.Outcome} " +
                    $"turns={entry.Turns} hp={entry.PlayerHp} searches={entry.Searches} " +
                    $"ms={entry.ElapsedMs}" +
                    (entry.Error is { Length: > 0 } error ? $" error={error}" : ""));
                if (stdout.Length > 0)
                    Console.Write(stdout);
                if (code != 0 && stderr.Length > 0)
                    Console.Error.Write(stderr);
            });
        wall.Stop();
        results.Sort(static (left, right) => string.CompareOrdinal(left.Root, right.Root));
        WriteSummary(settings, roots.Length, failed, wall.ElapsedMilliseconds, results);
        Console.WriteLine(
            $"offline-combat done roots={roots.Length} failed={failed} " +
            $"wall_ms={wall.ElapsedMilliseconds}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Runs exported TestHost request files. A single <paramref name="root"/> selects one entry from
    /// the list (used by the child process); null runs every entry in parallel.
    /// </summary>
    private static int RunRequestFiles(OfflineCombatSettings settings, string? root, int jobs)
    {
        OfflineRequestEntry[] entries = ReadRequestEntries(
            settings.RequestsFile ?? throw new InvalidOperationException("缺少 --requests-file。"));
        if (root != null)
        {
            OfflineRequestEntry? selected = entries.FirstOrDefault(
                entry => string.Equals(entry.Root, root, StringComparison.Ordinal));
            if (selected is null)
            {
                Console.Error.WriteLine($"--requests-file 中没有名为 {root} 的请求。");
                return 2;
            }
            return RunRoot(settings, selected.Root, selected.Path);
        }
        if (entries.Length == 0)
        {
            Console.Error.WriteLine("--requests-file 没有找到任何请求。");
            return 2;
        }
        Console.WriteLine(
            $"offline-combat requests={entries.Length} jobs={jobs} tier={settings.Tier} " +
            $"widths={settings.Profile.BaselineWidth}/{settings.Profile.NarrowWidth}/{settings.Profile.WideWidth}/{settings.Profile.OffensiveWidth} " +
            $"time_ms_per_width={settings.Profile.MillisecondsPerWidth} " +
            $"budget={BudgetDescription(settings).Mode} out={settings.Output}");
        Stopwatch wall = Stopwatch.StartNew();
        List<OfflineCombatRootResult> results = [];
        object gate = new();
        int failed = 0;
        Parallel.ForEach(
            entries,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            entry =>
            {
                (int code, string stdout, string stderr) = RunChild(settings, entry.Root, entry.Path);
                OfflineCombatRootResult? summary = ReadSummary(settings.Output, entry.Root);
                summary = summary is null
                    ? FailedRoot(entry.Root, code, stderr)
                    : summary with { ExitCode = code };
                lock (gate)
                {
                    results.Add(summary);
                    if (code != 0 || summary.Status is "failed" or "error" or "crashed")
                        failed++;
                }
                Console.WriteLine(
                    $"root={entry.Root} exit={code} status={summary.Status} outcome={summary.Outcome} " +
                    $"turns={summary.Turns} hp={summary.PlayerHp} searches={summary.Searches} " +
                    $"ms={summary.ElapsedMs}" +
                    (summary.Error is { Length: > 0 } error ? $" error={error}" : ""));
                if (stdout.Length > 0)
                    Console.Write(stdout);
                if (code != 0 && stderr.Length > 0)
                    Console.Error.Write(stderr);
            });
        wall.Stop();
        results.Sort(static (left, right) => string.CompareOrdinal(left.Root, right.Root));
        WriteSummary(settings, entries.Length, failed, wall.ElapsedMilliseconds, results);
        Console.WriteLine(
            $"offline-combat done roots={entries.Length} failed={failed} " +
            $"wall_ms={wall.ElapsedMilliseconds}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Reads <c>--requests-file</c> lines. Each line is <c>path</c> or <c>name=path</c>; relative
    /// paths are resolved against the list file. Names become result directory names, so they must
    /// be unique and valid single path components.
    /// </summary>
    internal static OfflineRequestEntry[] ReadRequestEntries(string listPath)
    {
        string fullList = Path.GetFullPath(listPath);
        if (!File.Exists(fullList))
            throw new InvalidDataException($"--requests-file 不存在：{fullList}");
        string baseDirectory = Path.GetDirectoryName(fullList)
            ?? throw new InvalidDataException($"--requests-file 没有目录：{fullList}");
        List<OfflineRequestEntry> entries = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string raw in File.ReadAllLines(fullList))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int separator = line.IndexOf('=');
            string name = separator < 0
                ? Path.GetFileNameWithoutExtension(line)
                : line[..separator].Trim();
            string path = separator < 0 ? line : line[(separator + 1)..].Trim();
            if (name.Length == 0 || name is "." or ".."
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Path.GetFileName(name) != name)
                throw new InvalidDataException(
                    $"--requests-file 第 {lineNumber} 行的名称不是合法目录名：{name}");
            if (path.Length == 0)
                throw new InvalidDataException($"--requests-file 第 {lineNumber} 行缺少路径。");
            string fullPath = Path.GetFullPath(
                Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
            if (!File.Exists(fullPath))
                throw new InvalidDataException(
                    $"--requests-file 第 {lineNumber} 行的请求不存在：{fullPath}");
            if (!names.Add(name))
                throw new InvalidDataException(
                    $"--requests-file 第 {lineNumber} 行的名称重复：{name}");
            entries.Add(new OfflineRequestEntry(name, fullPath));
        }
        return entries.OrderBy(static entry => entry.Root, StringComparer.Ordinal).ToArray();
    }

    private static void CheckQualityExpectation(string corpora, string root, SearchPerformanceTier tier, CombatPilotResult result)
    {
        if (!root.StartsWith("quality-", StringComparison.Ordinal)) return;
        string path = Path.Combine(corpora, "quality", root["quality-".Length..], "expectations.json");
        if (!File.Exists(path)) return;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement expected = document.RootElement;
        if (expected.GetProperty("tier").GetString() != tier.ToString()) return;
        if (result.Outcome != expected.GetProperty("outcome").GetString()
            || result.PlayerHp < expected.GetProperty("minimumFinalHp").GetInt32()
            || result.Deviations != 0 || result.DeploymentFailures != 0)
            throw new InvalidDataException($"质量回归失败：{root}，outcome={result.Outcome} hp={result.PlayerHp}。");
    }

    internal static string[] EnumerateQualityRoots(string corpora)
    {
        string quality = Path.Combine(corpora, "quality");
        if (!Directory.Exists(quality))
            return [];
        // The quality directory is the list: every child with a request is one root.
        return Directory.EnumerateDirectories(quality)
            .Where(directory => File.Exists(Path.Combine(directory, "request.json")))
            .Select(directory => "quality-" + Path.GetFileName(directory))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static int RunRoot(OfflineCombatSettings settings, string root,
        string? requestPath = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string directory = Path.Combine(settings.Output, root);
        string stale = Path.Combine(directory, "full-combat.json");
        if (File.Exists(stale))
            File.Delete(stale);
        string staleFailure = Path.Combine(directory, "failure.json");
        if (File.Exists(staleFailure))
            File.Delete(staleFailure);
        try
        {
            (CombatState combat, OfflineRequest request) = Build(settings, root, requestPath);
            Player player = LocalContext.GetMe(combat)
                ?? throw new InvalidOperationException("找不到本地玩家。");
            if (combat.RunState is not RunState runState)
                throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
            Rules rules = RulesCompiler.Compile(combat, player, runState);
            LiveCombatTracker tracker = new(combat);
            Battle opening = tracker.ImportRoot(combat, player, rules);
            tracker.Observe(opening);
            SearchPolicy policy = SearchPolicyOverrides.ApplyOverrides(
                SearchPolicyCapture.Capture(combat, player), request, player);
            DifferentialCombat host = new(combat, player, rules, policy, settings.Profile,
                settings.MemoryBudgetBytes, settings.WarmupNodes, settings.SearchThreads,
                settings.WorkspaceBufferBytes, opening, settings.WallClock);
            WorkspaceStatsCollector? stats = settings.WorkspaceStats
                ? new WorkspaceStatsCollector() : null;
            BeamSolver.ParentWorkspaceObserver = stats is null ? null : stats.Add;
            CombatPilotResult result;
            try
            {
                result = CombatPilot.RunAsync(host, root, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                BeamSolver.ParentWorkspaceObserver = null;
            }
            stats?.Print(root);
            long wallMs = stopwatch.ElapsedMilliseconds;
            CheckQualityExpectation(settings.Corpora, root, settings.Tier, result);
            WriteResult(settings.Output, root, result, BudgetDescription(settings));
            Console.WriteLine(
                $"root={root} status={result.Status} outcome={result.Outcome} turns={result.Turns} " +
                $"hp={result.PlayerHp}/{result.PlayerMaxHp} searches={result.Searches} " +
                $"reused={result.ReusedTurns} deviations={result.Deviations} " +
                $"failures={result.DeploymentFailures} combat_ms={result.ElapsedMs} wall_ms={wallMs}");
            return result.Outcome is "victory" or "defeat" or "ended" ? 0 : 5;
        }
        catch (UnsupportedImportException error)
        {
            WriteFailure(settings.Output, root, $"UNSUPPORTED reason={error.Reason} {error.Detail}");
            Console.Error.WriteLine($"root={root} UNSUPPORTED reason={error.Reason} {error.Detail}");
            return 3;
        }
        catch (UnsupportedException error)
        {
            WriteFailure(settings.Output, root, $"UNSUPPORTED {error.Message}");
            Console.Error.WriteLine($"root={root} UNSUPPORTED {error.Message}");
            return 3;
        }
        catch (NotSupportedException error)
        {
            WriteFailure(settings.Output, root, $"UNSUPPORTED {error.Message}");
            Console.Error.WriteLine($"root={root} UNSUPPORTED {error.Message}");
            return 3;
        }
        catch (CapacityException error)
        {
            WriteFailure(settings.Output, root, $"CAPACITY region={error.Region} {error.Message}");
            Console.Error.WriteLine($"root={root} CAPACITY region={error.Region} {error.Message}");
            return 3;
        }
        catch (InvalidDataException error)
        {
            WriteFailure(settings.Output, root, $"INVALID_DATA {error.Message}");
            Console.Error.WriteLine($"root={root} INVALID_DATA {error.Message}");
            return 2;
        }
        catch (InvalidOperationException error)
        {
            WriteFailure(settings.Output, root, $"FAILED {error.Message}");
            Console.Error.WriteLine($"root={root} FAILED {error.Message}");
            return 5;
        }
        catch (TimeoutException error)
        {
            WriteFailure(settings.Output, root, $"TIMEOUT {error.Message}");
            Console.Error.WriteLine($"root={root} TIMEOUT {error.Message}");
            return 4;
        }
    }

    internal static (CombatState Combat, OfflineRequest Request) Build(
        OfflineCombatSettings settings, string root, string? requestPath = null)
    {
        OfflineRequest request = requestPath is null
            ? Program.LoadRequest(settings.Data, settings.Corpora, root)
            : Program.LoadRequestFile(requestPath);
        if (request.FlashPrefixActions.Length != 0 || request.FlashPrefixPlan.Length != 0)
            throw new NotSupportedException("离线整场不支持前缀根。");
        if (request.FlashTurnSetupRootForTest)
            throw new NotSupportedException("离线整场不支持回合准备根。");
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request);
        loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), $"建局 {root}");
        return (build.GetAwaiter().GetResult(), request);
    }

    private static (int Code, string Stdout, string Stderr) RunChild(
        OfflineCombatSettings settings, string root, string? requestPath)
    {
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--offline-combat");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        if (requestPath != null)
        {
            start.ArgumentList.Add("--requests-file");
            start.ArgumentList.Add(settings.RequestsFile
                ?? throw new InvalidOperationException("缺少 --requests-file。"));
        }
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(settings.Data);
        start.ArgumentList.Add("--corpora");
        start.ArgumentList.Add(settings.Corpora);
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(settings.Output);
        start.ArgumentList.Add("--search-tier");
        start.ArgumentList.Add(settings.Tier.ToString());
        start.ArgumentList.Add("--memory-budget-bytes");
        start.ArgumentList.Add(settings.MemoryBudgetBytes.ToString());
        start.ArgumentList.Add("--jobs");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("--search-threads");
        start.ArgumentList.Add(settings.SearchThreads.ToString());
        start.ArgumentList.Add("--workspace-buffer-bytes");
        start.ArgumentList.Add(settings.WorkspaceBufferBytes.ToString());
        if (settings.WorkspaceStats)
            start.ArgumentList.Add("--workspace-stats");
        if (settings.WallClock)
            start.ArgumentList.Add("--wall-clock");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动离线整场子进程。");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }

    private static OfflineCombatRootResult? ReadSummary(string output, string root)
    {
        string path = Path.Combine(output, root, "full-combat.json");
        if (!File.Exists(path))
            return null;
        try
        {
            CombatPilotResult? result = JsonSerializer.Deserialize<CombatPilotResult>(
                File.ReadAllText(path), OfflineJson.Options);
            if (result == null)
                return null;
            return new OfflineCombatRootResult(result.Root, result.Status, result.Outcome,
                result.Turns, result.PlayerHp, result.Searches, result.ElapsedMs, result.PauseReason,
                0, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteSummary(OfflineCombatSettings settings, int roots, int failed,
        long wallMs, List<OfflineCombatRootResult> results)
    {
        object summary = new
        {
            schemaVersion = 1,
            tier = settings.Tier.ToString(),
            baselineWidth = settings.Profile.BaselineWidth,
            narrowWidth = settings.Profile.NarrowWidth,
            wideWidth = settings.Profile.WideWidth,
            offensiveWidth = settings.Profile.OffensiveWidth,
            millisecondsPerWidth = settings.Profile.MillisecondsPerWidth,
            searchBudget = BudgetDescription(settings),
            memoryBudgetBytes = settings.MemoryBudgetBytes,
            searchThreads = settings.SearchThreads,
            workspaceBufferBytes = settings.WorkspaceBufferBytes,
            workspaceStats = settings.WorkspaceStats,
            roots,
            failed,
            wallMs,
            results,
        };
        string path = Path.Combine(settings.Output, "summary.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(summary, OfflineJson.Options));
        File.Move(temporary, path, true);
    }

    private static void WriteResult(string output, string root, CombatPilotResult result,
        OfflineSearchBudget budget)
    {
        string directory = Path.Combine(output, root);
        Directory.CreateDirectory(directory);
        // Keep the production result shape at the top level and add the offline budget as one
        // extra property; readers that deserialize CombatPilotResult simply ignore it.
        JsonObject json = JsonSerializer.SerializeToNode(result, OfflineJson.Options)!.AsObject();
        json["searchBudget"] = JsonSerializer.SerializeToNode(budget, OfflineJson.Options);
        string path = Path.Combine(directory, "full-combat.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString(OfflineJson.Options));
        File.Move(temporary, path, true);
    }

    private static OfflineSearchBudget BudgetDescription(OfflineCombatSettings settings)
        => settings.WallClock
            ? new OfflineSearchBudget(
                Mode: "wall-clock",
                WallClock: true,
                SearchThreads: settings.SearchThreads,
                MillisecondsPerWidth: settings.Profile.MillisecondsPerWidth,
                MaxExpandedNodesPerWidth: null)
            : new OfflineSearchBudget(
                Mode: "reproducible",
                WallClock: false,
                SearchThreads: 1,
                MillisecondsPerWidth: null,
                MaxExpandedNodesPerWidth: ReproducibleNodesPerWidth);

    private static void WriteFailure(string output, string root, string error)
    {
        string directory = Path.Combine(output, root);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "failure.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { root, error }, OfflineJson.Options));
    }

    /// <summary>
    /// A child that died before writing full-combat.json. Its exit code is always recorded, and the
    /// unhandled exception type/message is extracted from stderr when the runtime printed one. The
    /// scenario trace lines ("[scenario] ...") are diagnostics from the last completed stage, not an
    /// error reason, so they are never used as the summary's error text.
    /// </summary>
    private static OfflineCombatRootResult FailedRoot(string root, int exitCode, string stderr)
    {
        (string? type, string? message) = ParseChildException(stderr);
        string error;
        if (type is not null)
            error = message is { Length: > 0 } ? $"{type}: {message}" : type;
        else
            error = exitCode == 0
                ? "没有写出 full-combat.json"
                : $"exit={exitCode} 没有写出 full-combat.json";
        return new OfflineCombatRootResult(root, exitCode == 0 ? "failed" : "crashed", "",
            0, 0, 0, 0, error, exitCode, type);
    }

    /// <summary>Reads the runtime's unhandled-exception banner, e.g. "Unhandled exception. System.X: msg".</summary>
    private static (string? Type, string? Message) ParseChildException(string stderr)
    {
        string[] lines = stderr.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            const string marker = "Unhandled exception";
            if (!line.StartsWith(marker, StringComparison.Ordinal))
                continue;
            string rest = line[marker.Length..].TrimStart('.', ':', ' ').Trim();
            if (rest.Length == 0 && index + 1 < lines.Length)
                rest = lines[index + 1].Trim();
            return SplitException(rest);
        }
        return (null, null);
    }

    private static (string? Type, string? Message) SplitException(string text)
    {
        if (text.Length == 0)
            return (null, null);
        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            string type = text[..colon].Trim();
            if (type.Length > 0 && type.IndexOf(' ') < 0)
                return (type, text[(colon + 1)..].Trim());
        }
        return (text, null);
    }

    /// <summary>Offline host for <see cref="CombatPilot"/>: kernel-only root and deployment.</summary>
    private sealed class DifferentialCombat(
        CombatState combat,
        Player player,
        Rules rules,
        SearchPolicy policy,
        SearchPerformanceProfile profile,
        long MemoryBudgetBytes,
        int WarmupNodes,
        int SearchThreads,
        int WorkspaceBufferBytes,
        Battle opening,
        bool wallClock) : ICombatPilotHost
    {
        private readonly List<string> _potionsUsed = [];
        private bool _warmedUp;
        private bool _storageChecked;
        private readonly int _combatStartHp = opening.Player.CurrentHp;
        private readonly int _combatStartMaxHp = opening.Player.MaxHp;
        private Battle _current = opening;
        private ReplayedAction? _lastAction;
        private readonly PostCombatRelicHealProfile _postCombatHeal =
            ScoreCompiler.CompilePostCombatRelicHeal(player);

        /// <summary>
        /// Runs one short solve with the same code paths before the measured solve. Each offline root
        /// is a fresh process; this gives tiered JIT the call counts a warm game process already has.
        /// </summary>
        private void Warmup(SearchCase searchCase, CancellationToken token)
        {
            if (WarmupNodes <= 0)
                return;
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(searchCase.Options with
            {
                BeamWidth = searchCase.Options.BeamWidth,
                MaxExpandedNodes = WarmupNodes,
                SoftTimeBudget = TimeSpan.MaxValue,
                Pacer = null,
                Trace = null,
            });
            try
            {
                _ = new BeamSolver(searchCase.Rules, options)
                    .Solve(searchCase.Battles, token);
            }
            catch (CapacityException)
            {
                // A warm-up is best effort; the measured solve reports its own outcome.
            }
            catch (UnsupportedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        public ValueTask<CombatPilotTurn?> WaitForTurnAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_current.Outcome != Outcome.None)
                return ValueTask.FromResult<CombatPilotTurn?>(null);
            return ValueTask.FromResult<CombatPilotTurn?>(new CombatPilotTurn(
                _current.PlayerTurn, _current.Player.CurrentHp, _current.Player.MaxHp));
        }

        public ValueTask<CombatPilotSearch> SearchAsync(int turn, string reason,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SearchCase searchCase;
            try
            {
                searchCase = SearchRequestCompiler.CompileRequest(
                    combat, player, rules, _current, policy, EvaluationRootFacts.Default,
                    profile, deterministic: true, MemoryBudgetBytes);
            }
            catch (UnsupportedImportException error)
            {
                string detail = $"reason={error.Reason} {error.Detail}";
                return ValueTask.FromResult(Failed("rejected", detail, "import_failed", detail));
            }
            catch (UnsupportedException error)
            {
                return ValueTask.FromResult(Failed("rejected", error.Message,
                    "import_unsupported", error.Message));
            }
            catch (CapacityException error)
            {
                string detail = $"region={error.Region} {error.Message}";
                return ValueTask.FromResult(Failed("rejected", detail, "import_capacity", detail));
            }
            catch (InvalidDataException error)
            {
                return ValueTask.FromResult(Failed("rejected", error.Message,
                    "request_invalid", error.Message));
            }
            catch (InvalidOperationException error)
            {
                return ValueTask.FromResult(Failed("rejected", error.Message,
                    "request_invalid", error.Message));
            }

            searchCase = searchCase with
            {
                Options = searchCase.Options with
                {
                    SearchThreads = SearchThreads,
                    WorkspaceBufferLimitBytes = WorkspaceBufferBytes,
                },
            };
            if (!_warmedUp)
            {
                _warmedUp = true;
                Warmup(searchCase, token);
            }
            if (!_storageChecked && Environment.GetEnvironmentVariable("STS2_SOLVER_STORAGE_CHECK") == "1")
            {
                _storageChecked = true;
                StorageCheck(searchCase, token);
            }

            SearchPlan plan = profile.ToSearchPlan(MemoryBudgetBytes, combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Boss,
            combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Elite, combat.RunState.CurrentActIndex);
            SearchStorage storage = new();
            // A constant coordinator clock keeps member and validation deadlines from firing in
            // reproducible mode. RunMember additionally removes the soft-time option and replaces
            // it with the fixed per-width node cap.
            Func<long>? coordinatorClock = wallClock ? null : static () => 0L;
            CombatSearchResult outcome;
            try
            {
                outcome = CombatSearchCoordinator.RunAsync(new(searchCase, plan), storage, RunMember,
                    token, coordinatorClock).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CapacityException error)
            {
                string detail = $"region={error.Region} {error.Message}";
                return ValueTask.FromResult(Failed("capacity", detail, "search_failed", detail));
            }
            catch (UnsupportedException error)
            {
                return ValueTask.FromResult(Failed("unsupported", error.Message, "search_failed",
                    error.Message));
            }
            catch (NotSupportedException error)
            {
                return ValueTask.FromResult(Failed("unsupported", error.Message, "search_failed",
                    error.Message));
            }
            catch (InvalidDataException error)
            {
                return ValueTask.FromResult(Failed("invalid_data", error.Message, "search_failed",
                    error.Message));
            }
            catch (InvalidOperationException error)
            {
                return ValueTask.FromResult(Failed("invalid_operation", error.Message,
                    "search_failed", error.Message));
            }

            if (outcome.Beam is not { } beam)
            {
                string status = outcome.FailureStatus.Length > 0 ? outcome.FailureStatus : "failed";
                string error = outcome.FailureError ?? "搜索没有返回路线";
                return ValueTask.FromResult(new CombatPilotSearch(null, status,
                    outcome.ElapsedMs, outcome.TotalExpandedNodes, 0, false, error,
                    "search_failed", error, outcome.ManagedHeapPeakBytes,
                    outcome.WorkingSetPeakBytes, outcome.SoftTimeRisk,
                    outcome.MemoryBudgetExceeded, outcome.MemoryExceededAtBytes,
                    outcome.PoolCapacityDropped, outcome.SearchMemoryUsedBytes,
                    outcome.SearchMemoryPeakBytes, outcome.RouteNodeStorageBytes,
                    outcome.RouteNodePeakCount)
                {
                    SelectedMember = outcome.SelectedMember, SearchStatus = outcome.Status,
                    Members = outcome.Members, MembersRun = outcome.MembersRun,
                    MembersSkippedByTime = outcome.MembersSkippedByTime,
                });
            }

            ReplayedRoute route;
            try
            {
                route = ReplayedRoute.Build(_current, rules, beam.BestRoute, beam.BestIsVictory);
            }
            catch (InvalidDataException error)
            {
                return ValueTask.FromResult(InvalidRoute(outcome, beam, "invalid", "route_invalid",
                    error.Message));
            }
            catch (InvalidOperationException error)
            {
                return ValueTask.FromResult(InvalidRoute(outcome, beam, "invalid", "route_invalid",
                    error.Message));
            }
            catch (UnsupportedException error)
            {
                return ValueTask.FromResult(InvalidRoute(outcome, beam, "invalid",
                    "route_unsupported", error.Message));
            }
            catch (CapacityException error)
            {
                string detail = $"region={error.Region} {error.Message}";
                return ValueTask.FromResult(InvalidRoute(outcome, beam, "invalid",
                    "route_capacity", detail));
            }

            return ValueTask.FromResult(new CombatPilotSearch(route, "ok", outcome.ElapsedMs,
                outcome.TotalExpandedNodes, beam.BestRoute.Length, beam.BestIsVictory, null,
                null, null, outcome.ManagedHeapPeakBytes, outcome.WorkingSetPeakBytes,
                outcome.SoftTimeRisk, outcome.MemoryBudgetExceeded,
                outcome.MemoryExceededAtBytes, outcome.PoolCapacityDropped,
                outcome.SearchMemoryUsedBytes, outcome.SearchMemoryPeakBytes,
                outcome.RouteNodeStorageBytes, outcome.RouteNodePeakCount)
            {
                SelectedMember = outcome.SelectedMember, SearchStatus = outcome.Status,
                Members = outcome.Members, MembersRun = outcome.MembersRun,
                MembersSkippedByTime = outcome.MembersSkippedByTime,
            });
        }

        private static CombatPilotSearch InvalidRoute(CombatSearchResult outcome, BeamResult beam,
            string status, string reason, string detail)
            => new(null, status, outcome.ElapsedMs, outcome.TotalExpandedNodes,
                beam.BestRoute.Length, beam.BestIsVictory, detail, reason, detail,
                outcome.ManagedHeapPeakBytes, outcome.WorkingSetPeakBytes, outcome.SoftTimeRisk,
                outcome.MemoryBudgetExceeded, outcome.MemoryExceededAtBytes,
                outcome.PoolCapacityDropped, outcome.SearchMemoryUsedBytes,
                outcome.SearchMemoryPeakBytes, outcome.RouteNodeStorageBytes,
                outcome.RouteNodePeakCount)
            {
                SelectedMember = outcome.SelectedMember, SearchStatus = outcome.Status,
                Members = outcome.Members, MembersRun = outcome.MembersRun,
                MembersSkippedByTime = outcome.MembersSkippedByTime,
            };

        private ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage storage, CancellationToken token)
        {
            Stopwatch roundWatch = Stopwatch.StartNew();
            long managedPeak = 0;
            long workingSetPeak = 0;
            BeamResult beam;
            try
            {
                BeamOptions options = member.Options;
                if (!wallClock)
                {
                    options = options with
                    {
                        MaxExpandedNodes = checked(options.BeamWidth * ReproducibleNodesPerWidth),
                        // Keep production's zero-damage-stop and cancellation semantics; only the
                        // budget source changes from the soft-time deadline to the node cap.
                        UnlimitedNodes = true,
                        NodeBudgetActive = true,
                        SoftTimeBudget = TimeSpan.MaxValue,
                        DeadlineExpired = null,
                        SearchThreads = 1,
                        Pacer = null,
                        Trace = null,
                        Progress = null,
                    };
                }
                Task<BeamResult> solve = Task.Run(() =>
                    new BeamSolver(member.Rules, options)
                        .Solve(member.Battles, storage, token));
                while (!solve.IsCompleted)
                {
                    SampleMemory(ref managedPeak, ref workingSetPeak);
                    Thread.Sleep(2);
                }
                SampleMemory(ref managedPeak, ref workingSetPeak);
                beam = solve.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CapacityException error)
            {
                roundWatch.Stop();
                string detail = $"region={error.Region} {error.Message}";
                return ValueTask.FromResult(new SearchMemberMeasurement(null, "capacity", detail,
                    roundWatch.ElapsedMilliseconds, managedPeak, workingSetPeak));
            }
            catch (UnsupportedException error)
            {
                roundWatch.Stop();
                return ValueTask.FromResult(new SearchMemberMeasurement(null, "unsupported",
                    error.Message, roundWatch.ElapsedMilliseconds, managedPeak, workingSetPeak));
            }
            catch (NotSupportedException error)
            {
                roundWatch.Stop();
                return ValueTask.FromResult(new SearchMemberMeasurement(null, "unsupported",
                    error.Message, roundWatch.ElapsedMilliseconds, managedPeak, workingSetPeak));
            }
            catch (InvalidDataException error)
            {
                roundWatch.Stop();
                return ValueTask.FromResult(new SearchMemberMeasurement(null, "invalid_data",
                    error.Message, roundWatch.ElapsedMilliseconds, managedPeak, workingSetPeak));
            }
            catch (InvalidOperationException error)
            {
                roundWatch.Stop();
                return ValueTask.FromResult(new SearchMemberMeasurement(null,
                    "invalid_operation", error.Message, roundWatch.ElapsedMilliseconds, managedPeak,
                    workingSetPeak));
            }
            roundWatch.Stop();
            return ValueTask.FromResult(new SearchMemberMeasurement(beam, "ok", null,
                roundWatch.ElapsedMilliseconds, managedPeak, workingSetPeak));
        }

        private void StorageCheck(SearchCase searchCase, CancellationToken token)
        {
            SearchStorage storage = new();
            long gcStart = GC.GetAllocatedBytesForCurrentThread();
            BeamOptions storageOptions = SearchBudgetPolicy.ApplyRegression(searchCase.Options with
                { SoftTimeBudget = TimeSpan.MaxValue, Pacer = null, Trace = null });
            BeamResult first = new BeamSolver(searchCase.Rules, storageOptions)
                .Solve(searchCase.Battles, storage, token);
            long firstBytes = storage.AllocatedBytes;
            string regionsFirst = RegionDump(storage);
            long gcFirst = GC.GetAllocatedBytesForCurrentThread();
            BeamResult second = new BeamSolver(searchCase.Rules, storageOptions)
                .Solve(searchCase.Battles, storage, token);
            long secondBytes = storage.AllocatedBytes;
            string regionsSecond = RegionDump(storage);
            long gcSecond = GC.GetAllocatedBytesForCurrentThread();
            bool same = SearchCodec.EncodeResult(first)
                .AsSpan().SequenceEqual(SearchCodec.EncodeResult(second));
            Console.WriteLine(
                $"STORAGE_CHECK same={same} firstBytes={firstBytes} " +
                $"secondBytes={secondBytes} secondDelta={secondBytes - firstBytes} " +
                $"nodesFirst={first.RouteNodePeakCount} nodesSecond={second.RouteNodePeakCount} " +
                $"gcFirst={gcFirst - gcStart} gcSecond={gcSecond - gcFirst} " +
                $"regionsFirst=[{regionsFirst}] regionsSecond=[{regionsSecond}]");
            if (!same)
                throw new InvalidDataException("同一请求复用存储两次的结果不同。");
        }

        private static string RegionDump(SearchStorage storage)
            => string.Join(',', storage.Memory.AllocatedByRegion
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}"));

        public ValueTask<CombatPilotBoundary> CheckBoundaryAsync(ReplayedAction expected, int turn,
            int index, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CombatPilotBoundary(true,
                CombatPilotAction.Describe(expected), []));
        }

        public void BeginDeployment(int turn)
        {
        }

        public ValueTask<CombatPilotDeployment> DeployAsync(ReplayedAction action, int turn,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _current = action.After;
            return ValueTask.FromResult(new CombatPilotDeployment(true, true, null, null,
                CombatPilotAction.Describe(action), []));
        }

        public void OnActionDeployed(ReplayedAction action)
        {
            _lastAction = action;
            if (action.Action.Kind == PlayerActionKind.UsePotion)
            {
                ushort stored = action.Before.Potions[action.Action.PotionSlot];
                if (stored != 0)
                    _potionsUsed.Add(Registry.Potion((ushort)(stored - 1)).NativeId);
            }
        }

        public void OnRouteAccepted(ReplayedRoute route)
        {
        }

        public CombatPilotFinal Final(bool cancelled)
        {
            string outcome;
            string status;
            if (_current.Outcome == Outcome.Victory)
            {
                outcome = "victory";
                status = "victory";
            }
            else if (_current.Outcome == Outcome.Defeat)
            {
                outcome = "defeat";
                status = "defeat";
            }
            else if (_current.Outcome == Outcome.EventDefeat)
            {
                outcome = _current.Player.IsDead ? "defeat" : "ended";
                status = outcome == "ended" ? "failed" : "defeat";
            }
            else
            {
                outcome = "in_progress";
                status = cancelled ? "cancelled" : "paused";
            }
            int playerHp = _current.Player.CurrentHp;
            if (_current.Outcome == Outcome.Victory && playerHp > 0)
            {
                // The native AfterCombatVictory relic pass heals the player before the final
                // full-combat snapshot. The kernel Battle is the pre-heal route boundary.
                playerHp += _postCombatHeal.HealFor(playerHp, _current.Player.MaxHp);
            }
            int turns = _current.PlayerTurn;
            // The native side switch increments PlayerCombatState.TurnNumber before the deferred
            // death check finishes, so a victory caused by an enemy-side turn-end effect (Doom and
            // similar) reports one more player turn than the kernel boundary. The flag comes from
            // the same kernel step, so the rule is not a per-root special case.
            if (_current.Outcome == Outcome.Victory
                && _lastAction is { EnemySideTurnEndReached: true })
            {
                turns++;
            }
            return new CombatPilotFinal(outcome, status, turns,
                playerHp, _current.Player.MaxHp, [.. _potionsUsed]);
        }

        private CombatPilotSearch Failed(string status, string error, string reason, string detail)
            => new(null, status, 0, 0, 0, false, error, reason, detail, 0, 0);
    }

    private static void SampleMemory(ref long managedPeak, ref long workingSetPeak)
    {
        long managed = GC.GetTotalMemory(forceFullCollection: false);
        if (managed > managedPeak)
            managedPeak = managed;
        long workingSet = Environment.WorkingSet;
        if (workingSet > workingSetPeak)
            workingSetPeak = workingSet;
    }
}

internal sealed class WorkspaceStatsCollector
{
    private readonly List<ParentWorkspaceMeasurement> _measurements = [];
    private readonly object _gate = new();
    private ParentWorkspaceMeasurement _largest;
    private int _direct;

    internal void Add(ParentWorkspaceMeasurement measurement)
    {
        lock (_gate)
        {
            if (measurement.Direct)
            {
                _direct++;
                return;
            }
            _measurements.Add(measurement);
            if (measurement.PeakUsedBytes > _largest.PeakUsedBytes)
                _largest = measurement;
        }
    }

    internal void Print(string root)
    {
        ParentWorkspaceMeasurement[] all;
        ParentWorkspaceMeasurement largest;
        int direct;
        lock (_gate)
        {
            all = [.. _measurements];
            largest = _largest;
            direct = _direct;
        }
        if (all.Length == 0)
        {
            Console.WriteLine($"WORKSPACE_STATS root={root} parents=0 direct={direct}");
            return;
        }
        long[] sorted = [.. all.Select(static measurement => measurement.PeakUsedBytes)];
        Array.Sort(sorted);
        static long Percentile(long[] values, double percentile)
        {
            int rank = Math.Max(0, (int)Math.Ceiling(percentile / 100d * values.Length) - 1);
            return values[Math.Min(rank, values.Length - 1)];
        }
        static int Over(long[] values, long threshold)
        {
            int low = 0, high = values.Length;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (values[middle] <= threshold)
                    low = middle + 1;
                else
                    high = middle;
            }
            return values.Length - low;
        }
        double Percent(int count) => 100d * count / sorted.Length;
        Console.WriteLine(
            $"WORKSPACE_STATS root={root} parents={sorted.Length} direct={direct} "
            + $"p50={Percentile(sorted, 50)} p99={Percentile(sorted, 99)} "
            + $"p999={Percentile(sorted, 99.9)} max={sorted[^1]}");
        Console.WriteLine(
            $"WORKSPACE_STATS root={root} over1m={Over(sorted, 1 << 20)} "
            + $"({Percent(Over(sorted, 1 << 20)):F3}%) "
            + $"over2m={Over(sorted, 2 << 20)} ({Percent(Over(sorted, 2 << 20)):F3}%) "
            + $"over4m={Over(sorted, 4 << 20)} ({Percent(Over(sorted, 4 << 20)):F3}%) "
            + $"over8m={Over(sorted, 8 << 20)} ({Percent(Over(sorted, 8 << 20)):F3}%) "
            + $"over16m={Over(sorted, 16 << 20)} ({Percent(Over(sorted, 16 << 20)):F3}%)");
        Console.WriteLine(
            $"WORKSPACE_STATS root={root} largest children={largest.Children} "
            + $"terminals={largest.Terminals} choices={largest.Choices} "
            + $"duplicates={largest.Duplicates} peakUsed={largest.PeakUsedBytes} "
            + $"peakAllocated={largest.PeakAllocatedBytes}");
    }
}
