// 原版战斗动作与终局对照：只通过原版建战斗，不依赖局外状态或开战预测。
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private sealed record PilotCase(string Name, string? Root, string? RequestPath);

    public static int Run(string[] args)
    {
        string data = "tests/Golden/data";
        string corpora = "tests/corpora";
        int jobs = 8;
        int shard = 0;
        int shards = 1;
        SearchPerformanceTier? tier = null;
        string? root = null;
        string? fixtureDirectory = null;
        string? requestPath = null;
        string? resultsPath = null;
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
                case "--pilot-dir" when index + 1 < args.Length:
                    fixtureDirectory = args[++index];
                    break;
                case "--pilot-request" when index + 1 < args.Length:
                    requestPath = args[++index];
                    break;
                case "--pilot-results" when index + 1 < args.Length:
                    resultsPath = args[++index];
                    break;
                case "--pilot-tier" when index + 1 < args.Length:
                    tier = SearchPerformanceProfile.ParseTier(args[++index]);
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
        if (requestPath != null)
            return RunChild(data, corpora, root, requestPath, tier);
        if (fixtureDirectory != null && root != null)
        {
            Console.Error.WriteLine("--pilot-dir 和 --pilot-root 不能同时使用。");
            return 2;
        }
        if (root != null)
            return RunChild(data, corpora, root, null, tier);
        return RunParent(data, corpora, jobs, shard, shards, fixtureDirectory, resultsPath,
            tier ?? SearchPerformanceTier.Low);
    }

    private static int RunChild(string data, string corpora, string? root, string? requestPath,
        SearchPerformanceTier? tier)
    {
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        TerminalBattleCapture.Install();
        return ComparePilot(loop, data, corpora, root, requestPath, tier);
    }

    private static int RunParent(string data, string corpora, int jobs, int shard, int shards,
        string? fixtureDirectory, string? resultsPath, SearchPerformanceTier tier)
    {
        List<PilotCase> cases = EnumeratePilotCases(data, corpora, fixtureDirectory);
        if (shards > 1)
            cases = cases.Where((_, index) => index % shards == shard).ToList();
        if (cases.Count == 0)
        {
            Console.Error.WriteLine("没有 pilot 用例。");
            return 2;
        }
        if (resultsPath != null)
        {
            string fullResults = Path.GetFullPath(resultsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullResults)!);
            File.WriteAllText(fullResults,
                "case\tstatus\treason\toutcome\tactions\tturns\tencounter\tchild_exit\t" +
                "first_diff\taction\tdiag\tcards\teffects\n");
            resultsPath = fullResults;
        }

        object gate = new();
        int same = 0, diff = 0, reject = 0, crash = 0;
        int victories = 0, defeats = 0, otherOutcomes = 0;
        int actions = 0;
        Dictionary<string, int> rejects = new(StringComparer.Ordinal);
        Dictionary<string, int> actionCoverage = new(StringComparer.Ordinal);
        Dictionary<string, int> effectCoverage = new(StringComparer.Ordinal);
        Dictionary<string, int> cardCoverage = new(StringComparer.Ordinal);
        Console.WriteLine($"PILOT cases={cases.Count} jobs={jobs} shard={shard}/{shards}");
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = jobs }, item =>
        {
            string[] childArgs = item.RequestPath != null
                ? ["--pilot-request", item.RequestPath, "--pilot-tier", tier.ToString()]
                : ["--pilot-root", item.Root!, "--pilot-tier", tier.ToString()];
            (int code, string stdout, string stderr) =
                RunChildProcess(data, corpora, childArgs);
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
                    Console.WriteLine($"PILOT-CRASH root={item.Name} code={code}");
                    string first = stderr.Split('\n').FirstOrDefault(static line => line.Length != 0)
                        ?? "no stderr";
                    Console.WriteLine("  " + first);
                    WriteResult(resultsPath, item.Name, "crash", "no-status", "-", "0", "0", "-",
                        code, "-", "-", "-");
                    return;
                }
                bool isSame = status.Contains(" status=same", StringComparison.Ordinal);
                bool isReject = status.Contains(" status=reject", StringComparison.Ordinal);
                string resultStatus;
                if (isSame)
                {
                    if (code != 0)
                    {
                        crash++;
                        Console.WriteLine($"PILOT-CRASH root={item.Name} code={code} status=same");
                        resultStatus = "crash";
                    }
                    else
                    {
                        same++;
                        resultStatus = "same";
                    }
                }
                else if (isReject)
                {
                    if (code != 0)
                    {
                        crash++;
                        Console.WriteLine($"PILOT-CRASH root={item.Name} code={code} status=reject");
                        resultStatus = "crash";
                    }
                    else
                    {
                        reject++;
                        resultStatus = "reject";
                        string reason = Field(status, "reason", "?");
                        rejects[reason] = rejects.GetValueOrDefault(reason) + 1;
                        Console.WriteLine($"PILOT-REJECT-CASE {item.Name} {reason}");
                        foreach (string line in lines.Where(static line => line.StartsWith("  ", StringComparison.Ordinal)))
                            Console.WriteLine(line);
                    }
                }
                else
                {
                    diff++;
                    resultStatus = "executor-diff";
                    Console.WriteLine(status);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("  ", StringComparison.Ordinal))
                            Console.WriteLine(line);
                    }
                    if (code != 0)
                        Console.WriteLine($"PILOT-CHILD-EXIT root={item.Name} code={code}");
                }
                string outcome = Field(status, "outcome", "-");
                if (outcome is "victory")
                    victories++;
                else if (outcome is "defeat")
                    defeats++;
                else if (outcome is not ("reject" or "diff" or "executor-diff"))
                    otherOutcomes++;
                int actionCount = int.TryParse(Field(status, "actions", "0"), out int parsed)
                    ? parsed : 0;
                actions += actionCount;
                foreach (string line in lines)
                {
                    if (!line.StartsWith("PILOT-COVER ", StringComparison.Ordinal))
                        continue;
                    Accumulate(line, "actions=", actionCoverage);
                    Accumulate(line, "effects=", effectCoverage);
                    Accumulate(line, "cards=", cardCoverage);
                }
                string firstDiff = lines
                    .Select(static line => line.Trim())
                    .FirstOrDefault(static line => line.StartsWith("+", StringComparison.Ordinal))
                    ?? "-";
                string differenceAction = lines
                    .Select(static line => line.Trim())
                    .FirstOrDefault(static line => line.StartsWith("PILOT-DIAG action=",
                        StringComparison.Ordinal))
                    ?? "-";
                string diag = string.Join("; ", lines
                    .Select(static line => line.Trim())
                    .Where(static line => line.StartsWith("PILOT-DIAG ", StringComparison.Ordinal)));
                if (diag.Length == 0)
                    diag = "-";
                string coverage = lines.FirstOrDefault(static line =>
                    line.StartsWith("PILOT-COVER ", StringComparison.Ordinal)) ?? "";
                WriteResult(resultsPath, item.Name, resultStatus,
                    resultStatus == "same" ? "same" : Field(status, "reason", "-"),
                    outcome, actionCount.ToString(CultureInfo.InvariantCulture),
                    Field(status, "turns", "0"), Field(status, "encounter", "-"), code,
                    firstDiff, differenceAction, diag, Field(coverage, "cards", "-"),
                    Field(coverage, "effects", "-"));
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
        foreach ((string card, int count) in cardCoverage.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"PILOT-CARD {card}={count}");
        foreach ((string effect, int count) in effectCoverage.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"PILOT-EFFECT {effect}={count}");
        return diff == 0 && crash == 0 ? 0 : 1;
    }

    private static void WriteResult(string? path, string name, string status, string reason,
        string outcome, string actions, string turns, string encounter, int exitCode,
        string firstDiff, string differenceAction, string diag, string cards = "-",
        string effects = "-")
    {
        if (path == null)
            return;
        static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        File.AppendAllText(path,
            $"{Clean(name)}\t{Clean(status)}\t{Clean(reason)}\t{Clean(outcome)}\t" +
            $"{Clean(actions)}\t{Clean(turns)}\t{Clean(encounter)}\t" +
            $"{exitCode.ToString(CultureInfo.InvariantCulture)}\t{Clean(firstDiff)}\t" +
            $"{Clean(differenceAction)}\t{Clean(diag)}\t{Clean(cards)}\t{Clean(effects)}\n");
    }

    private static List<PilotCase> EnumeratePilotCases(string data, string corpora,
        string? fixtureDirectory)
    {
        if (fixtureDirectory == null)
            return EnumeratePilotRoots(data, corpora)
                .Select(static root => new PilotCase(root, root, null)).ToList();

        string directory = Path.GetFullPath(fixtureDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"pilot 目录不存在：{directory}");
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(static path => Path.GetFileName(path) is not ("summary.json" or "manifest.json"
                or "selection.json"))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(path => new PilotCase(Path.GetFileNameWithoutExtension(path), null,
                Path.GetFullPath(path)))
            .ToList();
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

    private static int ComparePilot(MainLoopContext loop, string data, string corpora,
        string? root, string? requestPath, SearchPerformanceTier? tier)
    {
        string caseName = root ?? Path.GetFileNameWithoutExtension(requestPath!)
            ?? throw new InvalidDataException("pilot 用例没有名称。");
        OfflineRequest request = requestPath != null
            ? LoadRequestFromPath(requestPath)
            : Program.LoadRequest(data, corpora,
                root ?? throw new InvalidDataException("pilot 用例没有根名。"));
        // A self-contained --pilot-request carries its own search tier; a root fixture keeps
        // the historical Low pilot default. An explicit --pilot-tier overrides both.
        SearchPerformanceTier effectiveTier = tier
            ?? (requestPath != null
                ? SearchPerformanceProfile.ParseTier(request.SearchTier)
                : SearchPerformanceTier.Low);
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
                policy, SearchPerformanceProfile.For(effectiveTier), OfflineCombatSettings.DefaultMemoryBudgetBytes,
                onDifference: (predicted, live) =>
                {
                    if (Environment.GetEnvironmentVariable("STS2_SOLVER_PILOT_EVIDENCE") is not { Length: > 0 } evidence)
                        return;
                    string directory = Path.Combine(evidence, caseName);
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(Path.Combine(directory, "predicted.batl"), BattleCodec.Encode(predicted, nativeRules.Relics));
                    File.WriteAllBytes(Path.Combine(directory, "actual.batl"), BattleCodec.Encode(live, nativeRules.Relics));
                    File.WriteAllBytes(Path.Combine(directory, "rules.rule"), RulesCodec.Encode(nativeRules));
                    File.WriteAllLines(Path.Combine(directory, "diff.txt"),
                        BattleDump.Diff(predicted, live, "predicted", "live"));
                });
            CombatPilotResult result = CombatPilot.RunAsync(host, caseName, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (result.Deviations == 0 && result.FailureLog.Length != 0
                && IsImportFailure(result.FailureLog[0].Reason))
            {
                string importReason = result.FailureLog[0].Reason;
                Console.WriteLine(
                    $"PILOT root={caseName} status=reject reason={importReason} " +
                    $"outcome=reject actions={DeployedActions(host.Coverage)} " +
                    $"turns={result.Turns} encounter={encounterId}");
                Console.WriteLine("  " + result.FailureLog[0].Detail);
                WriteCoverage(host.Coverage);
                return 0;
            }
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
                    $"PILOT root={caseName} status={status} reason={reason} " +
                    $"outcome={result.Outcome} actions={DeployedActions(host.Coverage)} " +
                    $"turns={result.Turns} encounter={encounterId}");
                Console.WriteLine("  " + detail);
                if (host.LastDifferenceAction is { Length: > 0 } differenceAction)
                    Console.WriteLine("  PILOT-DIAG action=" + differenceAction);
                if (result.Deviations != 0)
                    DumpDeviationPile(combatPlayer, result.DeviationLog[0].Fields);
                else if (result.FailureLog.Length != 0)
                    DumpDeviationPile(combatPlayer, result.FailureLog[0].Detail.Split('\n'));
                WriteCoverage(host.Coverage);
                return status == "reject" ? 0 : 1;
            }
            string outcome = OutcomeName(host.Current.Outcome);
            Console.WriteLine(
                $"PILOT root={caseName} status=same outcome={outcome} " +
                $"actions={DeployedActions(host.Coverage)} turns={host.Current.PlayerTurn} " +
                $"encounter={encounterId} hp={host.Current.Player.CurrentHp} maxHp={host.Current.Player.MaxHp} " +
                $"expanded={result.SearchLog.Sum(static search => search.ExpandedNodes)} " +
                $"search_ms={result.SearchLog.Sum(static search => search.ElapsedMs)} " +
                $"healing_stops={result.SearchLog.Sum(static search => search.Members.Count(static member => member.StopReason == "healing-target"))}");
            WriteCoverage(host.Coverage);
            return 0;
        }
        catch (UnsupportedImportException error)
        {
            Console.WriteLine(
                $"PILOT root={caseName} status=reject reason={error.Reason} " +
                "outcome=reject actions=0");
            Console.WriteLine("  " + error.Detail);
            return 0;
        }
        catch (Sts2Solver.Simulation.Combat.UnsupportedException error)
        {
            Console.WriteLine(
                $"PILOT root={caseName} status=reject reason=core-unsupported " +
                "outcome=reject actions=0");
            Console.WriteLine("  " + error.Message);
            return 0;
        }
        catch (CapacityException error)
        {
            Console.WriteLine(
                $"PILOT root={caseName} status=reject reason=capacity " +
                "outcome=reject actions=0");
            Console.WriteLine("  " + error.Message);
            return 0;
        }
    }

    private static void DumpDeviationPile(Player player,
        IReadOnlyList<string> fields)
    {
        foreach (string field in fields)
        {
            Match match = Regex.Match(field, @"(Hand|Draw|Discard|Exhaust|Play)\[(\d+)\]");
            if (!match.Success || player.PlayerCombatState is not { } state)
                continue;
            string pileName = match.Groups[1].Value;
            int index = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> pile = pileName switch
            {
                "Hand" => state.Hand.Cards,
                "Draw" => state.DrawPile.Cards,
                "Discard" => state.DiscardPile.Cards,
                "Exhaust" => state.ExhaustPile.Cards,
                "Play" => state.PlayPile.Cards,
                _ => [],
            };
            if (index < 0 || index >= pile.Count)
            {
                Console.WriteLine($"  PILOT-DIAG pile={pileName} index={index} out-of-range count={pile.Count}");
                continue;
            }
            MegaCrit.Sts2.Core.Models.CardModel card = pile[index];
            string vars = string.Join(",", card.DynamicVars.Values.Select(variable =>
                $"{variable.Name}={variable.BaseValue.ToString(CultureInfo.InvariantCulture)}"));
            string enchantments = card.Enchantment is { } attachedEnchantment
                ? $"{attachedEnchantment.Id.Entry}:{attachedEnchantment.Amount}"
                : "-";
            string affliction = card.Affliction is { } attached
                ? $"{attached.Id.Entry}:{attached.Amount}"
                : "-";
            Console.WriteLine(
                $"  PILOT-DIAG pile={pileName} index={index} card={card.Id.Entry} " +
                $"upgrade={card.CurrentUpgradeLevel} vars=[{vars}] " +
                $"enchant=[{enchantments}] affliction={affliction}");
        }
    }

    private static OfflineRequest LoadRequestFromPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        OfflineRequest request = JsonSerializer.Deserialize<OfflineRequest>(
            File.ReadAllText(fullPath), OfflineJson.Options)
            ?? throw new InvalidDataException($"pilot 请求为空：{fullPath}");
        if (request.GeneratedScenarioPath is { Length: > 0 } generated)
        {
            string specPath = Path.IsPathRooted(generated)
                ? generated
                : Path.Combine(Path.GetDirectoryName(fullPath)!, generated);
            if (!File.Exists(specPath))
                throw new FileNotFoundException(
                    $"pilot 请求 {fullPath} 引用的生成场景不存在：{generated}", specPath);
            request = request with { GeneratedScenarioPath = Path.GetFullPath(specPath) };
        }
        return request;
    }

    private static bool IsImportFailure(string reason) =>
        reason.StartsWith("import_", StringComparison.Ordinal)
        || string.Equals(reason, "request_invalid", StringComparison.Ordinal);

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
            $"effects={string.Join(',', coverage.Effects.Select(static pair => $"{pair.Key}:{pair.Count}"))} " +
            $"cards={string.Join(',', coverage.Cards.Select(static pair => $"{pair.Key}:{pair.Count}"))}");
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
