// --capture-d2: runs the production search coordinator on the opening request of one root and
// writes every member SearchCase the coordinator issues, using the current SREQ encoding. Member
// solves use the D2 regression budget so a repeated capture produces the same requests; the
// coordinator plan (portfolio, queen/setup supplements, potion stages) is the production plan.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.TestHost;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal sealed record CaptureD2Member(
    int Index,
    string RequestSha256,
    int Width,
    string Evaluation,
    string Retention,
    string Potions,
    string? TheftPolicy,
    bool PreserveReplayAllocatorOpening,
    int? BrightestFlameMaxHpLossLimit,
    int InitialBrightestFlameMaxHpSpent,
    bool QueenPolicy,
    bool SetupPolicy,
    bool ContinuousSetup,
    bool PotionFreeBaseline,
    long ElapsedMilliseconds,
    int ExpandedNodes,
    int TurnsSearched,
    string StopReason,
    bool Victory);

internal sealed record CaptureD2Root(
    string Root,
    string ScenarioId,
    string Encounter,
    int PlayerTurn,
    int Calls,
    int UniqueRequests,
    long ElapsedMilliseconds,
    CaptureD2Member[] Members);

internal sealed record CaptureD2Summary(
    string[] Roots,
    int Calls,
    int UniqueRequests,
    long ElapsedMilliseconds);

internal static class CaptureD2
{
    internal static int Run(OfflineCombatSettings settings, string? root, string? rootsFile, int jobs)
    {
        if (root != null && rootsFile != null)
        {
            Console.Error.WriteLine("--capture-d2 的 --root 与 --roots-file 不能同时使用。");
            return 2;
        }
        if (jobs is < 1 or > 8)
        {
            Console.Error.WriteLine("--jobs 必须在 1 到 8 之间。");
            return 2;
        }
        Directory.CreateDirectory(settings.CaptureD2!);
        if (root != null)
        {
            int code = CaptureRoot(settings, root);
            if (code == 0 && Environment.GetEnvironmentVariable(
                    "STS2_SOLVER_CAPTURE_D2_CHILD") != "1")
                WriteSummary(settings);
            return code;
        }

        string[] roots = File.ReadAllLines(rootsFile!)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
        {
            Console.Error.WriteLine("--capture-d2 的 --roots-file 没有根。");
            return 2;
        }

        Console.WriteLine($"capture-d2 roots={roots.Length} jobs={jobs} out={settings.CaptureD2}");
        Stopwatch wall = Stopwatch.StartNew();
        List<(string Root, int Code, string Stdout, string Stderr)> results = [];
        object gate = new();
        Parallel.ForEach(
            roots,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            name =>
            {
                (int code, string stdout, string stderr) = RunChild(settings, name);
                lock (gate)
                    results.Add((name, code, stdout, stderr));
                Console.WriteLine($"capture-d2 root={name} exit={code}");
                if (stdout.Length > 0)
                    Console.Write(stdout);
                if (code != 0 && stderr.Length > 0)
                    Console.Error.Write(stderr);
            });
        wall.Stop();
        results.Sort(static (left, right) => string.CompareOrdinal(left.Root, right.Root));
        int failed = results.Count(static item => item.Code != 0);
        int capturedRoots = WriteSummary(settings, wall.ElapsedMilliseconds);
        Console.WriteLine($"capture-d2 done roots={capturedRoots} failed={failed} "
            + $"wall_ms={wall.ElapsedMilliseconds}");
        return failed == 0 ? 0 : 1;
    }

    private static int CaptureRoot(OfflineCombatSettings settings, string root)
    {
        Stopwatch wall = Stopwatch.StartNew();
        string directory = Path.Combine(settings.CaptureD2!, root);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        string requestDirectory = Path.Combine(directory, "requests");
        Directory.CreateDirectory(requestDirectory);

        (CombatState combat, OfflineRequest request) = OfflineCombat.Build(settings, root);
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        if (combat.RunState is not RunState runState)
            throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
        Rules rules = RulesCompiler.Compile(combat, player, runState);
        LiveCombatTracker tracker = new(combat);
        Battle opening = tracker.ImportRoot(combat, player, rules);
        if (opening.Phase != PlayerPhase.Play)
            throw new InvalidDataException($"capture-d2 根 {root} 的首个搜索不是 Play 根。");
        if (opening.PlayerTurn != 1)
            throw new InvalidDataException(
                $"capture-d2 根 {root} 的回合是 {opening.PlayerTurn}，不是第 1 回合。");

        SearchPolicyConfiguration configuration = new()
        {
            BrightestFlameMaxHpLossLimit = settings.BrightestFlameMaxHpLossLimit,
        };
        SearchPolicy policy = SearchPolicyCapture.Capture(combat, player, configuration);
        SearchCase initial = SearchRequestCompiler.CompileRequest(
            combat, player, rules, opening, policy, EvaluationRootFacts.Default,
            settings.Profile, deterministic: false, settings.MemoryBudgetBytes);
        SearchPlan plan = settings.Profile.ToSearchPlan(settings.MemoryBudgetBytes,
            combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Boss,
            combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Elite,
            combat.RunState.CurrentActIndex);

        List<CaptureD2Member> members = [];
        HashSet<string> unique = new(StringComparer.Ordinal);
        SearchStorage storage = new();
        ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage shared,
            CancellationToken token)
        {
            byte[] bytes = SearchCodec.EncodeRequest(member);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string path = Path.Combine(requestDirectory, hash + ".request.br");
            if (unique.Add(hash))
                WriteCompressedSmallest(path, bytes);

            Stopwatch memberWatch = Stopwatch.StartNew();
            BeamResult? beam = null;
            string status = "ok";
            string? error = null;
            try
            {
                BeamOptions options = SearchBudgetPolicy.ApplyRegression(member.Options with
                {
                    SoftTimeBudget = TimeSpan.MaxValue,
                    DeadlineExpired = null,
                    UnlimitedNodes = false,
                    SearchThreads = 1,
                    WorkspaceBufferLimitBytes = 0,
                    Pacer = null,
                    Trace = null,
                    Progress = null,
                });
                beam = new BeamSolver(member.Rules, options).Solve(member.Battles, shared, token);
            }
            catch (CapacityException exception)
            {
                status = "capacity";
                error = $"region={exception.Region} {exception.Message}";
            }
            catch (UnsupportedException exception)
            {
                status = "unsupported";
                error = exception.Message;
            }
            catch (NotSupportedException exception)
            {
                status = "unsupported";
                error = exception.Message;
            }
            catch (InvalidDataException exception)
            {
                status = "invalid";
                error = exception.Message;
            }
            catch (InvalidOperationException exception)
            {
                status = "invalid";
                error = exception.Message;
            }
            memberWatch.Stop();

            QueenSearchPolicy? queen = member.Options.Scorer?.QueenPolicy;
            bool setup = member.Options.Scorer?.SetupPreference == true;
            bool continuous = member.Options.Scorer?.SetupPolicy?.Continuous == true;
            members.Add(new CaptureD2Member(
                members.Count,
                hash,
                member.Options.BeamWidth,
                setup ? continuous ? "ContinuousSetup" : "Setup"
                    : member.Options.Scorer?.BaseOnly == true ? "Base"
                    : member.Options.Scorer?.Offensive == true ? "Offensive" : "Standard",
                member.Options.SecondRankBand ? "SecondBand"
                    : member.Options.AggressivePowerCommitment ? "Power"
                    : queen is { PreserveKillTiming: true } ? "QueenKillTiming" : "Standard",
                member.Options.Potions.ForceAllDisabled ? "None" : "Allowed",
                member.Options.TheftPolicy?.ToString(),
                member.Options.PreserveReplayAllocatorOpening,
                member.Options.BrightestFlameMaxHpLossLimit,
                member.Options.InitialBrightestFlameMaxHpSpent,
                queen != null,
                setup,
                continuous,
                member.Options.PotionFreeBaseline != null,
                memberWatch.ElapsedMilliseconds,
                beam?.ExpandedNodes ?? 0,
                beam?.TurnsSearched ?? 0,
                beam?.StopReason ?? status,
                beam?.BestIsVictory ?? false));
            return ValueTask.FromResult(new SearchMemberMeasurement(beam, status, error,
                memberWatch.ElapsedMilliseconds));
        }

        CombatSearchResult result;
        try
        {
            result = CombatSearchCoordinator.RunAsync(new(initial, plan), storage, RunMember,
                CancellationToken.None, clock: static () => 0L).AsTask().GetAwaiter().GetResult();
        }
        catch (CapacityException exception)
        {
            throw new InvalidDataException(
                $"capture-d2 根 {root} 容量不足：region={exception.Region} {exception.Message}");
        }
        catch (UnsupportedException exception)
        {
            throw new InvalidDataException($"capture-d2 根 {root} 不支持：{exception.Message}");
        }
        if (result.FailureError is { Length: > 0 } failure)
            throw new InvalidDataException($"capture-d2 根 {root} 搜索失败：{failure}");

        CaptureD2Root capture = new(
            root,
            request.ScenarioId,
            combat.Encounter?.Id.Entry ?? "-",
            opening.PlayerTurn,
            members.Count,
            unique.Count,
            wall.ElapsedMilliseconds,
            [.. members]);
        WriteJson(Path.Combine(directory, "capture.json"), capture);
        Console.WriteLine($"capture-d2 root={root} scenario={capture.ScenarioId} "
            + $"encounter={capture.Encounter} turn={capture.PlayerTurn} calls={capture.Calls} "
            + $"unique={capture.UniqueRequests} wall_ms={capture.ElapsedMilliseconds}");
        foreach (CaptureD2Member member in members)
            Console.WriteLine($"  member={member.Index} hash={member.RequestSha256} "
                + $"width={member.Width} eval={member.Evaluation} retention={member.Retention} "
                + $"potions={member.Potions} theft={member.TheftPolicy ?? "-"} "
                + $"axe={member.PreserveReplayAllocatorOpening} "
                + $"flame={member.BrightestFlameMaxHpLossLimit?.ToString() ?? "-"} "
                + $"queen={member.QueenPolicy} setup={member.SetupPolicy} "
                + $"continuous={member.ContinuousSetup} baseline={member.PotionFreeBaseline} "
                + $"ms={member.ElapsedMilliseconds} expanded={member.ExpandedNodes} "
                + $"turns={member.TurnsSearched} stop={member.StopReason} win={member.Victory}");
        return 0;
    }

    /// <summary>Rewrites the cumulative summary from every capture.json already in the output.</summary>
    private static int WriteSummary(OfflineCombatSettings settings, long wallMilliseconds = 0)
    {
        string[] paths = Directory.EnumerateFiles(settings.CaptureD2!, "capture.json",
                SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        List<CaptureD2Root> captures = [];
        foreach (string path in paths)
            captures.Add(JsonSerializer.Deserialize<CaptureD2Root>(File.ReadAllText(path),
                OfflineJson.Options)
                ?? throw new InvalidDataException($"capture.json 为空：{path}"));
        int calls = captures.Sum(static item => item.Calls);
        int unique = captures.SelectMany(static item => item.Members)
            .Select(static item => item.RequestSha256)
            .Distinct(StringComparer.Ordinal)
            .Count();
        long elapsed = wallMilliseconds > 0
            ? wallMilliseconds
            : captures.Sum(static item => item.ElapsedMilliseconds);
        CaptureD2Summary summary = new([.. captures.Select(static item => item.Root)], calls, unique,
            elapsed);
        WriteJson(Path.Combine(settings.CaptureD2!, "capture-summary.json"), summary);
        return captures.Count;
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
        start.ArgumentList.Add("--capture-d2");
        start.ArgumentList.Add(settings.CaptureD2!);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(settings.Data);
        start.ArgumentList.Add("--corpora");
        start.ArgumentList.Add(settings.Corpora);
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(settings.Output);
        start.Environment["STS2_SOLVER_CAPTURE_D2_CHILD"] = "1";
        start.ArgumentList.Add("--search-tier");
        start.ArgumentList.Add(settings.Tier.ToString());
        start.ArgumentList.Add("--memory-budget-bytes");
        start.ArgumentList.Add(settings.MemoryBudgetBytes.ToString());
        if (settings.BrightestFlameMaxHpLossLimit is { } flame)
        {
            start.ArgumentList.Add("--brightest-flame-max-hp-loss-limit");
            start.ArgumentList.Add(flame.ToString());
        }
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 capture-d2 子进程。");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }

    private static void WriteJson(string path, object value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, OfflineJson.Options));

    private static void WriteCompressedSmallest(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BrotliStream brotli = new(file, CompressionLevel.SmallestSize, leaveOpen: false))
            brotli.Write(bytes);
        File.Move(temporary, path, overwrite: true);
    }
}
