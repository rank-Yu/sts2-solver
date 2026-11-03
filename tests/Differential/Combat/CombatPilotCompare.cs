// 原版战斗动作与终局对照：只通过原版建战斗，不依赖局外状态或开战预测。
using System.Diagnostics;
using System.Globalization;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.TestHost;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using NativeRunState = MegaCrit.Sts2.Core.Runs.RunState;

namespace Differential;

internal static class CombatPilotCompare
{
    public static int Run(string[] args)
    {
        string data = "tests/Golden/data";
        string corpora = "tests/corpora";
        int jobs = 8;
        int shard = 0;
        int shards = 1;
        string? root = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--data" when index + 1 < args.Length:
                    data = args[++index];
                    break;
                case "--corpora" when index + 1 < args.Length:
                    corpora = args[++index];
                    break;
                case "--pilot-jobs" when index + 1 < args.Length:
                    jobs = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--pilot-shard" when index + 2 < args.Length:
                    shard = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    shards = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--pilot-root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                default:
                    Console.Error.WriteLine($"未知参数：{args[index]}");
                    return 2;
            }
        }
        if (jobs is < 1 or > 8 || shards < 1 || shard < 0 || shard >= shards)
        {
            Console.Error.WriteLine("--pilot-jobs 必须在 1 到 8 之间，分片参数必须合法。");
            return 2;
        }
        if (root != null)
            return RunChild(data, corpora, root);
        return RunParent(data, corpora, jobs, shard, shards);
    }

    private static int RunChild(string data, string corpora, string root)
    {
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        TerminalBattleCapture.Install();
        return ComparePilot(loop, data, corpora, root);
    }

    private static int RunParent(string data, string corpora, int jobs, int shard, int shards)
    {
        List<string> cases = [];
        foreach (string root in EnumeratePilotRoots(data, corpora))
            cases.Add(root);
        if (shards > 1)
            cases = cases.Where((_, index) => index % shards == shard).ToList();
        if (cases.Count == 0)
        {
            Console.Error.WriteLine("没有 pilot 用例。");
            return 2;
        }

        object gate = new();
        int same = 0, diff = 0, reject = 0, crash = 0;
        int victories = 0, defeats = 0, otherOutcomes = 0;
        int actions = 0;
        Dictionary<string, int> rejects = new(StringComparer.Ordinal);
        Dictionary<string, int> actionCoverage = new(StringComparer.Ordinal);
        Dictionary<string, int> effectCoverage = new(StringComparer.Ordinal);
        Console.WriteLine($"PILOT cases={cases.Count} jobs={jobs} shard={shard}/{shards}");
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = jobs }, root =>
        {
            (int code, string stdout, string stderr) = RunChildProcess(data, corpora,
                ["--pilot-root", root]);
            List<string> lines = stdout.Split('\n')
                .Where(static line => line.StartsWith("PILOT ", StringComparison.Ordinal)
                    || line.StartsWith("PILOT-COVER ", StringComparison.Ordinal)
                    || line.StartsWith("  ", StringComparison.Ordinal))
                .ToList();
            lock (gate)
            {
                string? status = lines.FirstOrDefault(
                    static line => line.StartsWith("PILOT ", StringComparison.Ordinal));
                if (status == null)
                {
                    crash++;
                    Console.WriteLine($"PILOT-CRASH root={root} code={code}");
                    string first = stderr.Split('\n').FirstOrDefault(static line => line.Length != 0)
                        ?? "no stderr";
                    Console.WriteLine("  " + first);
                    return;
                }
                if (status.Contains(" status=same", StringComparison.Ordinal))
                {
                    if (code != 0)
                    {
                        crash++;
                        Console.WriteLine($"PILOT-CRASH root={root} code={code} status=same");
                        return;
                    }
                    same++;
                }
                else if (status.Contains(" status=reject", StringComparison.Ordinal))
                {
                    if (code != 0)
                    {
                        crash++;
                        Console.WriteLine(
                            $"PILOT-CRASH root={root} code={code} status=reject");
                        return;
                    }
                    reject++;
                    string reason = Field(status, "reason", "?");
                    rejects[reason] = rejects.GetValueOrDefault(reason) + 1;
                    Console.WriteLine($"PILOT-REJECT-CASE {root} {reason}");
                }
                else
                {
                    diff++;
                    Console.WriteLine(status);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("  ", StringComparison.Ordinal))
                            Console.WriteLine(line);
                    }
                    if (code != 0)
                        Console.WriteLine($"PILOT-CHILD-EXIT root={root} code={code}");
                }
                string outcome = Field(status, "outcome", "-");
                if (outcome is "victory")
                    victories++;
                else if (outcome is "defeat")
                    defeats++;
                else if (outcome is not ("reject" or "diff" or "executor-diff"))
                    otherOutcomes++;
                actions += int.TryParse(Field(status, "actions", "0"), out int count) ? count : 0;
                foreach (string line in lines)
                {
                    if (!line.StartsWith("PILOT-COVER ", StringComparison.Ordinal))
                        continue;
                    Accumulate(line, "actions=", actionCoverage);
                    Accumulate(line, "effects=", effectCoverage);
                }
            }
        });
        Console.WriteLine(
            $"PILOT-SUMMARY cases={cases.Count} same={same} diff={diff} " +
            $"reject={reject} crash={crash} victory={victories} defeat={defeats} " +
            $"other={otherOutcomes} actions={actions}");
        foreach ((string reason, int count) in rejects.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"PILOT-REJECT {reason}={count}");
        foreach ((string action, int count) in actionCoverage.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"PILOT-ACTION {action}={count}");
        foreach ((string effect, int count) in effectCoverage.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"PILOT-EFFECT {effect}={count}");
        return diff == 0 && crash == 0 ? 0 : 1;
    }

    private static void Accumulate(string line, string field, Dictionary<string, int> counts)
    {
        int start = line.IndexOf(field, StringComparison.Ordinal);
        if (start < 0)
            return;
        start += field.Length;
        int end = line.IndexOf(' ', start);
        string value = end < 0 ? line[start..] : line[start..end];
        if (value.Length == 0)
            return;
        foreach (string entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(entry[(colon + 1)..], out int count))
                continue;
            string key = entry[..colon];
            counts[key] = counts.GetValueOrDefault(key) + count;
        }
    }

    private static string Field(string line, string name, string fallback)
    {
        string marker = name + "=";
        int start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return fallback;
        start += marker.Length;
        int end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static int ComparePilot(MainLoopContext loop, string data, string corpora, string root)
    {
        OfflineRequest request = Program.LoadRequest(data, corpora, root);
        try
        {
            OfflineCombatWaiter waiter = new(loop);
            // The opening SetupPlayerTurn can ask for a turn-start choice (Toasty Mittens,
            // Gambling Chip, Tyranny, ...). Supply the same local selector surface the setup
            // enumeration uses so the comparison builds through the native pause to Play; the
            // selector is unused when the opening asks nothing.
            Task<CombatState> build = ScenarioBuilder.BuildAsync(waiter, request,
                turnSetupSelector: new TurnSetupCardSelector(0));
            loop.RunUntilCompleted(build, TimeSpan.FromSeconds(120), "pilot 战斗场景");
            CombatState combat = build.GetAwaiter().GetResult();
            string encounterId = combat.Encounter?.Id.Entry
                ?? throw new InvalidDataException("原生战斗没有遭遇。");
            Player combatPlayer = LocalContext.GetMe(combat)
                ?? throw new InvalidDataException("原生战斗找不到玩家。");
            NativeRunState run = combat.RunState as NativeRunState
                ?? throw new InvalidDataException("原生战斗的 RunState 类型不对。");
            Rules nativeRules = RulesCompiler.Compile(combat, combatPlayer, run);
            LiveCombatTracker tracker = new(combat);
            Battle opening = tracker.ImportRoot(combat, combatPlayer, nativeRules);
            tracker.Observe(opening);
            SearchPolicy policy = SearchPolicyOverrides.ApplyOverrides(
                SearchPolicyCapture.Capture(combat, combatPlayer), request, combatPlayer);
            NativePilotHost host = new(loop, combat, combatPlayer, nativeRules, tracker, opening,
                policy, SearchPerformanceProfile.For(SearchPerformanceTier.Low), OfflineCombatSettings.DefaultMemoryBudgetBytes);
            CombatPilotResult result = CombatPilot.RunAsync(host, root, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (result.DeploymentFailures != 0 || result.Deviations != 0
                || result.Status is not ("victory" or "defeat" or "ended"))
            {
                string reason = result.Deviations != 0 ? "boundary_diff"
                    : result.FailureLog.Length != 0 ? result.FailureLog[0].Reason
                    : "pilot_" + result.Status;
                string detail = result.Deviations != 0
                    ? result.DeviationLog[0].Action + ": " + string.Join(" | ", result.DeviationLog[0].Fields)
                    : result.FailureLog.Length != 0 ? result.FailureLog[0].Detail
                    : result.PauseReason ?? "";
                string status = result.Status == "rejected" ? "reject" : "executor-diff";
                Console.WriteLine(
                    $"PILOT root={root} status={status} reason={reason} " +
                    $"outcome={result.Outcome} actions={DeployedActions(host.Coverage)} " +
                    $"turns={result.Turns} encounter={encounterId}");
                Console.WriteLine("  " + detail);
                WriteCoverage(host.Coverage);
                return status == "reject" ? 0 : 1;
            }
            string outcome = OutcomeName(host.Current.Outcome);
            Console.WriteLine(
                $"PILOT root={root} status=same outcome={outcome} " +
                $"actions={DeployedActions(host.Coverage)} turns={host.Current.PlayerTurn} " +
                $"encounter={encounterId}");
            WriteCoverage(host.Coverage);
            return 0;
        }
        catch (UnsupportedImportException error)
        {
            Console.WriteLine(
                $"PILOT root={root} status=reject reason={error.Reason} " +
                "outcome=reject actions=0");
            Console.WriteLine("  " + error.Detail);
            return 0;
        }
        catch (Sts2Solver.Simulation.Combat.UnsupportedException error)
        {
            Console.WriteLine(
                $"PILOT root={root} status=reject reason=core-unsupported " +
                "outcome=reject actions=0");
            Console.WriteLine("  " + error.Message);
            return 0;
        }
        catch (CapacityException error)
        {
            Console.WriteLine(
                $"PILOT root={root} status=reject reason=capacity " +
                "outcome=reject actions=0");
            Console.WriteLine("  " + error.Message);
            return 0;
        }
    }

    private static string OutcomeName(Outcome outcome) => outcome switch
    {
        Outcome.Victory => "victory",
        Outcome.Defeat => "defeat",
        Outcome.EventDefeat => "ended",
        _ => "in_progress",
    };

    private static int DeployedActions(CombatActionCoverage coverage)
    {
        int total = 0;
        foreach ((string _, int count) in coverage.Actions)
            total += count;
        return total;
    }

    private static void WriteCoverage(CombatActionCoverage coverage)
    {
        Console.WriteLine(
            $"PILOT-COVER actions={string.Join(',', coverage.Actions.Select(static pair => $"{pair.Key}:{pair.Count}"))} " +
            $"effects={string.Join(',', coverage.Effects.Select(static pair => $"{pair.Key}:{pair.Count}"))}");
    }

    private static IEnumerable<string> EnumeratePilotRoots(string data, string corpora)
        => OfflineCombat.EnumerateQualityRoots(corpora);

    private static (int Code, string Stdout, string Stderr) RunChildProcess(string data,
        string corpora, string[] caseArgs)
    {
        string assembly = typeof(CombatPilotCompare).Assembly.Location;
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--combat-pilot-compare");
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(data);
        start.ArgumentList.Add("--corpora");
        start.ArgumentList.Add(corpora);
        foreach (string argument in caseArgs)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动对照子进程。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
