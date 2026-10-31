// --offline-combat: whole-combat replay inside the Sts2Solver kernel. The decision loop is the same
// CombatPilot the Mod runs; this host supplies the imported opening root, deterministic search
// and kernel stepping instead of native deployment.
using System.Diagnostics;
using System.Text.Json;
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
    int WarmupNodes)
{
    /// <summary>0.15 x 16 GiB, the fixed machine size the offline comparison assumes.</summary>
    internal static long DefaultMemoryBudgetBytes => (16L << 30) / 100 * 15 + (16L << 30) % 100 * 15 / 100;
}

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
    public static int Run(OfflineCombatSettings settings, string? root, string? rootsFile, int jobs)
    {
        Directory.CreateDirectory(settings.Output);
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
            $"time_ms_per_width={settings.Profile.MillisecondsPerWidth} out={settings.Output}");
        Stopwatch wall = Stopwatch.StartNew();
        List<OfflineCombatRootResult> results = [];
        object gate = new();
        int failed = 0;
        Parallel.ForEach(
            roots,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            name =>
            {
                (int code, string stdout, string stderr) = RunChild(settings, name);
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
        string[] available = Directory.EnumerateDirectories(quality)
            .Where(directory => File.Exists(Path.Combine(directory, "request.json")))
            .Select(directory => "quality-" + Path.GetFileName(directory))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string manifest = Path.Combine(quality, "default-roots.txt");
        if (!File.Exists(manifest)) return available;
        string[] selected = File.ReadAllLines(manifest).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length
            || selected.Except(available, StringComparer.Ordinal).Any())
            throw new InvalidDataException("默认质量评测名单包含重复或缺失的请求。");
        return selected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static int RunRoot(OfflineCombatSettings settings, string root)
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
            (CombatState combat, OfflineRequest request) = Build(settings, root);
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
                settings.MemoryBudgetBytes, settings.WarmupNodes, opening);
            CombatPilotResult result = CombatPilot.RunAsync(host, root, CancellationToken.None)
                .GetAwaiter().GetResult();
            long wallMs = stopwatch.ElapsedMilliseconds;
            CheckQualityExpectation(settings.Corpora, root, settings.Tier, result);
            WriteResult(settings.Output, root, result);
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

    private static (CombatState Combat, OfflineRequest Request) Build(
        OfflineCombatSettings settings, string root)
    {
        OfflineRequest request = Program.LoadRequest(settings.Data, settings.Corpora, root);
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
        OfflineCombatSettings settings, string root)
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
            memoryBudgetBytes = settings.MemoryBudgetBytes,
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

    private static void WriteResult(string output, string root, CombatPilotResult result)
    {
        string directory = Path.Combine(output, root);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "full-combat.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, OfflineJson.Options));
        File.Move(temporary, path, true);
    }

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
        Battle opening) : ICombatPilotHost
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
            CombatSearchResult outcome;
            try
            {
                outcome = CombatSearchCoordinator.RunAsync(new(searchCase, plan), storage, RunMember, token)
                    .AsTask().GetAwaiter().GetResult();
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
                Task<BeamResult> solve = Task.Run(() =>
                    new BeamSolver(member.Rules, member.Options)
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
