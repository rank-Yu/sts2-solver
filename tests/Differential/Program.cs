// Differential host entry point. Builds the native CombatState for frozen snapshot inputs and writes
// a deterministic state dump; also compares a dump directory against a reference directory.
using System.Diagnostics;
using System.Text.Json;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Combat;

namespace Differential;

internal static class Program
{
    private const string DefaultData = "tests/Golden/data";
    private const string DefaultCorpora = "tests/corpora";
    private const string DefaultOutput = ".local/offlinehost/out";

    private static Task<int> Main(string[] args)
    {
        try
        {
            return Task.FromResult(Run(args));
        }
        catch (UnsupportedException error)
        {
            Console.Error.WriteLine($"UNSUPPORTED: {error.Message}");
            return Task.FromResult(3);
        }
        catch (NotSupportedException error)
        {
            Console.Error.WriteLine($"UNSUPPORTED: {error.Message}");
            return Task.FromResult(3);
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine($"INVALID_INPUT: {error.Message}");
            return Task.FromResult(2);
        }
        catch (TimeoutException error)
        {
            Console.Error.WriteLine($"TIMEOUT: {error.Message}");
            return Task.FromResult(4);
        }
    }

    private static int Run(string[] args)
    {
        if (args is ["--list-quality-roots"] or ["--list-quality-roots", "--corpora", _])
        {
            foreach (string name in OfflineCombat.EnumerateQualityRoots(args.Length == 3 ? args[2] : DefaultCorpora))
                Console.WriteLine(name);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--combat-pilot-compare")
            return CombatPilotCompare.Run(args[1..]);
        string data = DefaultData;
        string corpora = DefaultCorpora;
        string output = DefaultOutput;
        string? root = null;
        string? compareDir = null;
        string? compareOnlyNew = null;
        string? compareOnlyRef = null;
        bool all = false;
        bool importCompare = false;
        bool importNegative = false;
        bool rulesCompare = false;
        bool rulesTableCheck = false;
        bool configCompare = false;
        bool recaptureRoot = false;
        bool compiledRules = false;
        string? liveCompareDir = null;
        string? searchCompareDir = null;
        string? emitLiveRequestsDir = null;
        string? liveRootsFile = null;
        string? searchTier = null;
        int? searchCancelAfterMilliseconds = null;
        bool searchNormal = false;
        bool fullCombat = false;
        bool offlineCombat = false;
        int? beamWidth = null;
        int? maxWidth = null;
        int? maxNodes = null;
        int? nodesPerWidth = null;
        int? poolStateBudgetMb = null;
        int? memoryBudgetMb = null;
        long? memoryBudgetBytes = null;
        int jobs = 1;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--all":
                    all = true;
                    break;
                case "--import-compare":
                    importCompare = true;
                    break;
                case "--import-negative":
                    importNegative = true;
                    break;
                case "--rules-compare":
                    rulesCompare = true;
                    break;
                case "--config-compare":
                    configCompare = true;
                    break;
                case "--recapture-root":
                    recaptureRoot = true;
                    break;
                case "--rules-table-check":
                    rulesTableCheck = true;
                    break;
                case "--compiled-rules":
                    compiledRules = true;
                    break;
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                case "--data" when index + 1 < args.Length:
                    data = args[++index];
                    break;
                case "--corpora" when index + 1 < args.Length:
                    corpora = args[++index];
                    break;
                case "--out" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--jobs" when index + 1 < args.Length:
                    jobs = int.Parse(args[++index]);
                    break;
                case "--compare" when index + 1 < args.Length:
                    compareDir = args[++index];
                    break;
                case "--compare-only" when index + 2 < args.Length:
                    compareOnlyNew = args[++index];
                    compareOnlyRef = args[++index];
                    break;
                case "--live-compare" when index + 1 < args.Length:
                    liveCompareDir = args[++index];
                    break;
                case "--search-compare" when index + 1 < args.Length:
                    searchCompareDir = args[++index];
                    break;
                case "--search-tier" when index + 1 < args.Length:
                    searchTier = args[++index];
                    break;
                case "--search-normal":
                    searchNormal = true;
                    break;
                case "--full-combat":
                    fullCombat = true;
                    break;
                case "--offline-combat":
                    offlineCombat = true;
                    break;
                case "--beam-width" when index + 1 < args.Length:
                    beamWidth = int.Parse(args[++index]);
                    break;
                case "--max-nodes" when index + 1 < args.Length:
                    maxNodes = int.Parse(args[++index]);
                    break;
                case "--max-width" when index + 1 < args.Length:
                    maxWidth = int.Parse(args[++index]);
                    break;
                case "--nodes-per-width" when index + 1 < args.Length:
                    nodesPerWidth = int.Parse(args[++index]);
                    break;
                case "--pool-state-budget-mb" when index + 1 < args.Length:
                    poolStateBudgetMb = int.Parse(args[++index]);
                    break;
                case "--memory-budget-mb" when index + 1 < args.Length:
                    memoryBudgetMb = int.Parse(args[++index]);
                    break;
                case "--memory-budget-bytes" when index + 1 < args.Length:
                    memoryBudgetBytes = long.Parse(args[++index]);
                    break;
                case "--search-cancel-after-ms" when index + 1 < args.Length:
                    searchCancelAfterMilliseconds = int.Parse(args[++index]);
                    break;
                case "--emit-live-requests" when index + 1 < args.Length:
                    emitLiveRequestsDir = args[++index];
                    break;
                case "--roots-file" when index + 1 < args.Length:
                    liveRootsFile = args[++index];
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数：{args[index]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (offlineCombat)
        {
            if (all || importCompare || importNegative || rulesCompare || rulesTableCheck
                || configCompare || recaptureRoot || compiledRules || compareDir != null
                || emitLiveRequestsDir != null || liveCompareDir != null
                || searchCompareDir != null || searchNormal
                || searchCancelAfterMilliseconds != null || compareOnlyNew != null)
            {
                Console.Error.WriteLine("--offline-combat 不能与其它对照/发射模式同时使用。");
                return 2;
            }
            return RunOfflineCombat(data, corpora, output, searchTier, beamWidth, maxWidth,
                maxNodes, nodesPerWidth, poolStateBudgetMb, memoryBudgetMb, memoryBudgetBytes,
                fullCombat, jobs, root, liveRootsFile);
        }
        if (beamWidth != null || maxWidth != null || maxNodes != null || nodesPerWidth != null
            || poolStateBudgetMb != null || memoryBudgetMb != null || memoryBudgetBytes != null)
        {
            Console.Error.WriteLine(
                "--pool-state-budget-mb/"
                + "--memory-budget-mb 只能与 --offline-combat 一起使用。");
            return 2;
        }
        if (compareOnlyNew != null)
            return Compare(compareOnlyNew, compareOnlyRef!, roots: null);

        if (emitLiveRequestsDir != null)
        {
            if (recaptureRoot)
            {
                Console.Error.WriteLine("--emit-live-requests 不能与 --recapture-root 同时使用。");
                return 2;
            }
            if (searchTier != null || searchNormal || searchCancelAfterMilliseconds != null || fullCombat)
                return EmitLiveRequests(data, corpora, emitLiveRequestsDir, liveRootsFile, all, root,
                    searchTier, searchNormal, searchCancelAfterMilliseconds, fullCombat);
            return EmitLiveRequests(data, corpora, emitLiveRequestsDir, liveRootsFile, all, root);
        }
        if (searchCompareDir != null)
        {
            if (all || root != null || importCompare || rulesCompare || configCompare
                || recaptureRoot || emitLiveRequestsDir != null || liveCompareDir != null)
            {
                Console.Error.WriteLine("--search-compare 不能与 --root/--all 或其它对照模式同时使用。");
                return 2;
            }
            return SearchCompare.Run(data, corpora, searchCompareDir);
        }
        if (liveCompareDir != null)
        {
            if (all || root != null || importCompare || rulesCompare || configCompare
                || recaptureRoot || emitLiveRequestsDir != null)
            {
                Console.Error.WriteLine("--live-compare 不能与 --root/--all 或其它对照模式同时使用。");
                return 2;
            }
            return LiveCompare.Run(data, corpora, liveCompareDir);
        }
        if (liveRootsFile != null)
        {
            Console.Error.WriteLine("--roots-file 只能与 --emit-live-requests 一起使用。");
            return 2;
        }
        if (searchTier != null || searchNormal || searchCancelAfterMilliseconds != null || fullCombat)
        {
            Console.Error.WriteLine(
                "--search-tier/--search-normal/--search-cancel-after-ms/--full-combat 只能与 --emit-live-requests 一起使用。");
            return 2;
        }

        if (importNegative)
            return BuildNegative(data, corpora, root ?? "p8-c-rage-juggling-dark-embrace");
        if (rulesTableCheck)
            return BuildRulesTableCheck();
        if (compiledRules && !importCompare)
        {
            Console.Error.WriteLine("--compiled-rules 只能与 --import-compare 一起使用。");
            return 2;
        }
        if (rulesCompare && importCompare)
        {
            Console.Error.WriteLine("--rules-compare 与 --import-compare 不能同时使用。");
            return 2;
        }
        if (configCompare && (importCompare || rulesCompare))
        {
            Console.Error.WriteLine("--config-compare 不能与 --import-compare/--rules-compare 同时使用。");
            return 2;
        }
        if (recaptureRoot)
        {
            if (importCompare || importNegative || rulesCompare || rulesTableCheck || configCompare
                || compiledRules || compareDir != null)
            {
                Console.Error.WriteLine(
                    "--recapture-root 只能与 --root/--all、--out、--data、--corpora 一起使用。");
                return 2;
            }
            if (!all && root == null)
            {
                Console.Error.WriteLine("--recapture-root 需要 --root <name> 或 --all。");
                return 2;
            }
        }
        if (!all && root == null)
        {
            Console.Error.WriteLine("需要 --root <name>、--all、--rules-table-check，或 --compare-only <new> <ref>。");
            return 2;
        }
        if (jobs is < 1 or > 8)
        {
            Console.Error.WriteLine("--jobs 必须在 1 到 8 之间。");
            return 2;
        }
        Directory.CreateDirectory(output);

        if (recaptureRoot)
        {
            if (root != null)
            {
                RecaptureRoot.Run(data, corpora, output, root);
                return 0;
            }
            string[] recaptureRoots = EnumerateRoots(data);
            Console.WriteLine($"recapture roots={recaptureRoots.Length} jobs={jobs} out={output}");
            int recaptureFailures = 0;
            Parallel.ForEach(recaptureRoots,
                new ParallelOptions { MaxDegreeOfParallelism = jobs },
                name =>
                {
                    (int code, _) = BuildChild(data, corpora, output, name, false, false, false,
                        false, recaptureRoot: true);
                    if (code != 0)
                        Interlocked.Increment(ref recaptureFailures);
                });
            Console.WriteLine($"recapture done roots={recaptureRoots.Length} "
                + $"failures={recaptureFailures}");
            return recaptureFailures == 0 ? 0 : 1;
        }

        if (root != null)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int code = BuildInProcess(data, corpora, output, root, importCompare,
                rulesCompare, configCompare, compiledRules);
            stopwatch.Stop();
            if (code != 0)
                return code;
            Console.WriteLine($"root={root} ms={stopwatch.Elapsed.TotalMilliseconds:0}");
            if (rulesCompare)
                return LastRulesResult == RulesStatus.Different ? 1 : 0;
            if (importCompare)
                return LastImportResult == ImportStatus.Different ? 1 : 0;
            if (configCompare)
                return LastConfigResult == ConfigStatus.Different ? 1 : 0;
            return compareDir == null ? 0 : Compare(output, compareDir, [root]);
        }

        string[] roots = EnumerateRoots(data);
        Console.WriteLine($"roots={roots.Length} jobs={jobs} out={output}");
        if (rulesCompare)
            PrintRulesObjectCoverage(data, roots);
        int failures = 0;
        long totalMilliseconds = 0;
        object gate = new();
        int same = 0, different = 0, rejected = 0;
        Dictionary<string, int> diffGroups = new(StringComparer.Ordinal);
        Dictionary<string, int> rejectGroups = new(StringComparer.Ordinal);
        Dictionary<string, int> rulesDiffGroups = new(StringComparer.Ordinal);
        Dictionary<string, int> rulesRejectGroups = new(StringComparer.Ordinal);
        Dictionary<string, int> configDiffGroups = new(StringComparer.Ordinal);
        Dictionary<string, int> configRejectGroups = new(StringComparer.Ordinal);
        int configFull = 0, configSubParts = 0;
        Parallel.ForEach(
            roots,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            name =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                (int code, string stdout) = BuildChild(data, corpora, output, name,
                    importCompare, rulesCompare, configCompare, compiledRules);
                stopwatch.Stop();
                List<string> importLines = stdout.Split('\n')
                    .Where(line => line.StartsWith("IMPORT ", StringComparison.Ordinal)).ToList();
                List<string> rulesLines = stdout.Split('\n')
                    .Where(line => line.StartsWith("RULES ", StringComparison.Ordinal)).ToList();
                List<string> configLines = stdout.Split('\n')
                    .Where(line => line.StartsWith("CONFIG ", StringComparison.Ordinal)).ToList();
                lock (gate)
                {
                    totalMilliseconds += (long)stopwatch.Elapsed.TotalMilliseconds;
                    if (code != 0)
                        failures++;
                    foreach (string line in importLines)
                    {
                        if (line.Contains("status=same", StringComparison.Ordinal)) same++;
                        else if (line.Contains("status=diff", StringComparison.Ordinal))
                        {
                            different++;
                            string field = ValueOf(line, "field=");
                            diffGroups[field] = diffGroups.GetValueOrDefault(field) + 1;
                        }
                        else if (line.Contains("status=reject", StringComparison.Ordinal))
                        {
                            rejected++;
                            string reason = ValueOf(line, "reason=");
                            rejectGroups[reason] = rejectGroups.GetValueOrDefault(reason) + 1;
                        }
                    }
                    foreach (string line in rulesLines)
                    {
                        if (line.Contains("status=same", StringComparison.Ordinal)) same++;
                        else if (line.Contains("status=diff", StringComparison.Ordinal))
                        {
                            different++;
                            string table = ValueOf(line, "table=");
                            rulesDiffGroups[table] = rulesDiffGroups.GetValueOrDefault(table) + 1;
                        }
                        else if (line.Contains("status=reject", StringComparison.Ordinal))
                        {
                            rejected++;
                            string reason = ValueOf(line, "reason=");
                            rulesRejectGroups[reason] = rulesRejectGroups.GetValueOrDefault(reason) + 1;
                        }
                    }
                    foreach (string line in configLines)
                    {
                        if (line.Contains("scope=subparts", StringComparison.Ordinal))
                            configSubParts++;
                        else
                            configFull++;
                        if (line.Contains("status=same", StringComparison.Ordinal)) same++;
                        else if (line.Contains("status=diff", StringComparison.Ordinal))
                        {
                            different++;
                            string field = ValueOf(line, "field=");
                            configDiffGroups[field] = configDiffGroups.GetValueOrDefault(field) + 1;
                        }
                        else if (line.Contains("status=reject", StringComparison.Ordinal))
                        {
                            rejected++;
                            string reason = ValueOf(line, "reason=");
                            configRejectGroups[reason] = configRejectGroups.GetValueOrDefault(reason) + 1;
                        }
                    }
                }
                Console.WriteLine($"root={name} ms={stopwatch.Elapsed.TotalMilliseconds:0} status={(code == 0 ? "ok" : "fail")}");
            });
        Console.WriteLine($"done roots={roots.Length} failures={failures} sum_ms={totalMilliseconds}");
        if (importCompare)
        {
            Console.WriteLine($"import same={same} diff={different} reject={rejected}");
            foreach ((string key, int count) in diffGroups.OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
                Console.WriteLine($"import-diff field={key} count={count}");
            foreach ((string key, int count) in rejectGroups.OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
                Console.WriteLine($"import-reject reason={key} count={count}");
        }
        else if (rulesCompare)
        {
            Console.WriteLine($"rules same={same} diff={different} reject={rejected}");
            foreach ((string key, int count) in rulesDiffGroups.OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
                Console.WriteLine($"rules-diff table={key} count={count}");
            foreach ((string key, int count) in rulesRejectGroups.OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
                Console.WriteLine($"rules-reject reason={key} count={count}");
        }
        else if (configCompare)
        {
            Console.WriteLine($"config same={same} diff={different} reject={rejected} full={configFull} subparts={configSubParts}");
            foreach ((string key, int count) in configDiffGroups.OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
                Console.WriteLine($"config-diff field={key} count={count}");
            foreach ((string key, int count) in configRejectGroups.OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
                Console.WriteLine($"config-reject reason={key} count={count}");
        }
        int compareCode = compareDir == null ? 0 : Compare(output, compareDir, roots);
        return failures == 0 && different == 0 ? compareCode : 1;
    }

    private static string[] EnumerateRoots(string data)
    {
        string d1 = Path.Combine(data, "d1");
        return Directory.EnumerateDirectories(d1)
            .Where(directory => File.Exists(Path.Combine(directory, "meta.json")))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;
    }

    private static (int Code, string Stdout) BuildChild(string data, string corpora, string output,
        string root, bool importCompare, bool rulesCompare, bool configCompare, bool compiledRules,
        bool recaptureRoot = false)
    {
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(data);
        start.ArgumentList.Add("--corpora");
        start.ArgumentList.Add(corpora);
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(output);
        if (recaptureRoot)
            start.ArgumentList.Add("--recapture-root");
        if (importCompare)
            start.ArgumentList.Add("--import-compare");
        if (rulesCompare)
            start.ArgumentList.Add("--rules-compare");
        if (configCompare)
            start.ArgumentList.Add("--config-compare");
        if (compiledRules)
            start.ArgumentList.Add("--compiled-rules");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动子进程。");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"root={root} exit={process.ExitCode}");
            if (stderr.Length > 0)
                Console.Error.WriteLine(stderr.TrimEnd());
        }
        if (stdout.Length > 0)
            Console.WriteLine(stdout.TrimEnd());
        return (process.ExitCode, stdout);
    }

    private static int BuildRulesTableCheck()
    {
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        return RulesTableCheck.Run();
    }

    private static void PrintRulesObjectCoverage(string data, IReadOnlyList<string> roots)
    {
        HashSet<string> objects = new(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            using JsonDocument meta = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(data, "d1", root, "meta.json")));
            if (meta.RootElement.TryGetProperty("rulesSha256", out JsonElement sha)
                && sha.GetString() is { Length: > 0 } value)
            {
                objects.Add(value);
            }
        }
        Console.WriteLine($"rules-objects distinct={objects.Count} roots={roots.Count}");
    }

    private static int BuildNegative(string data, string corpora, string root)
    {
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        OfflineRequest request = LoadRequest(data, corpora, root);
        Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request);
        loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), $"负例建局 {root}");
        return ImportNegative.Run(data, root, build.GetAwaiter().GetResult());
    }

    private static ImportStatus LastImportResult { get; set; }

    private static RulesStatus LastRulesResult { get; set; }

    private static ConfigStatus LastConfigResult { get; set; }

    private static int BuildInProcess(string data, string corpora, string output, string root,
        bool importCompare, bool rulesCompare, bool configCompare, bool compiledRules)
    {
        OfflineRequest request = LoadRequest(data, corpora, root);
        string stage = "init";
        try
        {
            MainLoopContext loop = new();
            SynchronizationContext.SetSynchronizationContext(loop);
            GameBootstrap.ApplyGodotBypasses();
            GameBootstrap.SkipGodotNodeStaticConstructors();
            GameBootstrap.InitializeStaticState();
            stage = "build";
            bool hasPrefix = request.FlashPrefixActions.Length != 0
                || request.FlashPrefixPlan.Length != 0;
            if ((importCompare || rulesCompare || configCompare) && request.FlashTurnSetupRootForTest)
            {
                SetupRootSet set = SetupRootSet.Build(loop, request, needConfig: configCompare);
                Console.WriteLine($"root={root} setup-roots={set.Roots.Count} options={set.OptionIds.Count}");
                if (rulesCompare)
                {
                    RulesResult setupRules = RulesCompare.RunSetup(data, root, set);
                    LastRulesResult = setupRules.Status;
                    Console.WriteLine(setupRules.Line);
                    if (setupRules.Status == RulesStatus.Different)
                        Console.WriteLine(setupRules.Detail);
                    else if (setupRules.Status == RulesStatus.Rejected)
                        Console.WriteLine($"  detail={setupRules.Detail}");
                }
                else if (importCompare)
                {
                    ImportResult setupImport = ImportCompare.RunSetup(data, root, set);
                    LastImportResult = setupImport.Status;
                    Console.WriteLine(setupImport.Line);
                    if (setupImport.Status == ImportStatus.Different)
                        Console.WriteLine(setupImport.Detail);
                    else if (setupImport.Status == ImportStatus.Rejected)
                        Console.WriteLine($"  detail={setupImport.Detail}");
                }
                else
                {
                    ConfigResult setupConfig = ConfigCompare.RunSetup(data, root, set);
                    LastConfigResult = setupConfig.Status;
                    Console.WriteLine(setupConfig.Line);
                    if (setupConfig.Status == ConfigStatus.Different)
                        Console.WriteLine(setupConfig.Detail);
                    else if (setupConfig.Status == ConfigStatus.Rejected)
                        Console.WriteLine($"  detail={setupConfig.Detail}");
                }
                return 0;
            }

            FirstChoiceCardSelector.LastSelections.Clear();
            Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request);
            loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), $"建局 {root}");
            CombatState combat = build.GetAwaiter().GetResult();
            stage = "dump";
            string path = Path.Combine(output, root + ".native.txt");
            NativeStateDump.Write(combat, path);
            Console.WriteLine($"root={root} dump={path}");
            if (rulesCompare)
            {
                RulesResult result = hasPrefix
                    ? RulesCompare.RunPrefix(data, root, combat, request)
                    : RulesCompare.Run(data, root, combat);
                LastRulesResult = result.Status;
                Console.WriteLine(result.Line);
                if (result.Status == RulesStatus.Different)
                    Console.WriteLine(result.Detail);
                else if (result.Status == RulesStatus.Rejected)
                    Console.WriteLine($"  detail={result.Detail}");
            }
            else if (importCompare)
            {
                ImportResult result = hasPrefix
                    ? ImportCompare.RunPrefix(data, root, combat, request)
                    : ImportCompare.Run(data, root, combat, [.. FirstChoiceCardSelector.LastSelections],
                        false, compiledRules);
                LastImportResult = result.Status;
                Console.WriteLine(result.Line);
                if (result.Status == ImportStatus.Different)
                    Console.WriteLine(result.Detail);
                else if (result.Status == ImportStatus.Rejected)
                    Console.WriteLine($"  detail={result.Detail}");
            }
            else if (configCompare)
            {
                ConfigResult result = hasPrefix
                    ? ConfigCompare.RunPrefix(data, root, combat, request)
                    : ConfigCompare.Run(data, root, combat, [.. FirstChoiceCardSelector.LastSelections], false, request);
                LastConfigResult = result.Status;
                Console.WriteLine(result.Line);
                if (result.Status == ConfigStatus.Different)
                    Console.WriteLine(result.Detail);
                else if (result.Status == ConfigStatus.Rejected)
                    Console.WriteLine($"  detail={result.Detail}");
            }
            return 0;
        }
        catch (UnsupportedImportException error)
        {
            Console.Error.WriteLine($"root={root} stage={stage} UNSUPPORTED reason={error.Reason} {error.Detail}");
            return 3;
        }
        catch (UnsupportedException error)
        {
            Console.Error.WriteLine($"root={root} stage={stage} UNSUPPORTED {error.Message}");
            return 3;
        }
        catch (NotSupportedException error)
        {
            Console.Error.WriteLine($"root={root} stage={stage} UNSUPPORTED {error.Message}");
            return 3;
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine($"root={root} stage={stage} INVALID_INPUT {error.Message}");
            return 2;
        }
        catch (TimeoutException error)
        {
            Console.Error.WriteLine($"root={root} stage={stage} TIMEOUT {error.Message}");
            return 4;
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine($"root={root} stage={stage} FAILED {error.Message}");
            return 5;
        }
    }

    internal static OfflineRequest LoadRequest(string data, string corpora, string root)
    {
        if (root.StartsWith("quality-", StringComparison.Ordinal))
        {
            string name = root["quality-".Length..];
            if (name.Length == 0 || Path.GetFileName(name) != name)
                throw new InvalidDataException("质量语料名称无效。");
            string directory = Path.Combine(corpora, "quality", name);
            string requestPath = Path.Combine(directory, "request.json");
            OfflineRequest quality = JsonSerializer.Deserialize<OfflineRequest>(
                File.ReadAllText(requestPath), OfflineJson.Options)
                ?? throw new InvalidDataException($"quality request 为空：{requestPath}。");
            if (quality.GeneratedScenarioPath is not { Length: > 0 })
            {
                if (quality.OwnedLoadoutForTest is null)
                    throw new InvalidDataException("固定质量语料必须声明已有牌组恢复。");
                return quality;
            }
            string spec = Path.Combine(directory, "spec.json");
            if (!File.Exists(spec)) throw new FileNotFoundException("质量语料缺少生成规格。", spec);
            return quality with { GeneratedScenarioPath = spec };
        }
        string metaPath = Path.Combine(data, "d1", root, "meta.json");
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        JsonElement inputElement = meta.RootElement.GetProperty("input");
        string inputText = inputElement.ValueKind == JsonValueKind.String
            ? inputElement.GetString() ?? throw new InvalidDataException($"{metaPath} 的 input 为空。")
            : inputElement.GetRawText();
        using JsonDocument inputDocument = JsonDocument.Parse(inputText);
        JsonElement input = inputDocument.RootElement;
        string kind = input.GetProperty("kind").GetString()
            ?? throw new InvalidDataException($"{metaPath} 的 input.kind 为空。");

        OfflineRequest request;
        if (kind == "fixture")
        {
            string sourcePath = input.GetProperty("fixture").GetString()
                ?? throw new InvalidDataException($"{metaPath} 的 input.fixture 为空。");
            string expectedName = "flash-" + root + ".json";
            if (!string.Equals(Path.GetFileName(sourcePath), expectedName, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"{metaPath} 的 fixture 文件名 {Path.GetFileName(sourcePath)} 与根名 {root} 不对应（预期 {expectedName}）。");
            string fixturePath = Path.Combine(corpora, "fixtures", root + ".json");
            if (!File.Exists(fixturePath))
                throw new FileNotFoundException($"找不到 fixture：{fixturePath}", fixturePath);
            request = JsonSerializer.Deserialize<OfflineRequest>(
                File.ReadAllText(fixturePath), OfflineJson.Options)
                ?? throw new InvalidDataException($"fixture 为空：{fixturePath}。");
            if (request.GeneratedScenarioPath is { Length: > 0 } generatedPath)
            {
                string specName = Path.GetFileName(generatedPath);
                string specPath = Path.Combine(corpora, "fixtures", "specs", specName);
                if (!File.Exists(specPath))
                    throw new FileNotFoundException(
                        $"fixture {root} 引用的生成场景配置不存在：{generatedPath}（本地查找 {specPath}）。",
                        specPath);
                request = request with { GeneratedScenarioPath = specPath };
            }
        }
        else
        {
            throw new InvalidDataException($"不支持的 input.kind：{kind}。");
        }
        return request;
    }

    private static int Compare(string newDirectory, string referenceDirectory, IReadOnlyList<string>? roots)
    {
        string[] names = roots?.ToArray()
            ?? Directory.EnumerateFiles(newDirectory, "*.native.txt")
                .Select(path => Path.GetFileName(path)[..^".native.txt".Length])
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        int mismatches = 0;
        foreach (string name in names)
        {
            string newPath = Path.Combine(newDirectory, name + ".native.txt");
            string referencePath = Path.Combine(referenceDirectory, name + ".native.txt");
            if (!File.Exists(newPath))
            {
                Console.Error.WriteLine($"root={name} MISSING_NEW {newPath}");
                mismatches++;
                continue;
            }
            if (!File.Exists(referencePath))
            {
                Console.Error.WriteLine($"root={name} MISSING_REFERENCE {referencePath}");
                mismatches++;
                continue;
            }
            string[] actual = File.ReadAllText(newPath).Split('\n');
            string[] expected = File.ReadAllText(referencePath).Split('\n');
            int shared = Math.Min(actual.Length, expected.Length);
            int first = -1;
            for (int index = 0; index < shared; index++)
            {
                if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                {
                    first = index;
                    break;
                }
            }
            if (first < 0 && actual.Length != expected.Length)
                first = shared;
            if (first < 0)
                continue;
            mismatches++;
            string expectedLine = first < expected.Length ? expected[first] : "<eof>";
            string actualLine = first < actual.Length ? actual[first] : "<eof>";
            Console.Error.WriteLine(
                $"root={name} MISMATCH line={first + 1} expected=[{expectedLine}] actual=[{actualLine}]");
        }
        Console.WriteLine($"compare roots={names.Length} mismatches={mismatches} reference={referenceDirectory}");
        return mismatches == 0 ? 0 : 1;
    }

    private static string ValueOf(string line, string key)
    {
        int start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
            return "<unknown>";
        start += key.Length;
        int end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static int RunOfflineCombat(string data, string corpora, string output,
        string? searchTier, int? beamWidth, int? maxWidth, int? maxNodes, int? nodesPerWidth,
        int? poolStateBudgetMb, int? memoryBudgetMb, long? memoryBudgetBytesOverride,
        bool fullCombat, int jobs, string? root, string? rootsFile)
    {
        if (fullCombat)
        {
            // fullCombat is the headless request flag; it is not valid in this mode.
            Console.Error.WriteLine("--offline-combat 不能与 --full-combat 同时使用。");
            return 2;
        }
        if (jobs is < 1 or > 8)
        {
            Console.Error.WriteLine("--jobs 必须在 1 到 8 之间。");
            return 2;
        }
        if (beamWidth is not null || maxWidth is not null || maxNodes is not null || nodesPerWidth is not null)
        {
            Console.Error.WriteLine("旧加宽和节点上限参数已移除；请使用 --search-tier。 ");
            return 2;
        }
        if (poolStateBudgetMb is <= 0)
        {
            Console.Error.WriteLine("--pool-state-budget-mb 必须是正整数。");
            return 2;
        }
        if (memoryBudgetMb is <= 0)
        {
            Console.Error.WriteLine("--memory-budget-mb 必须是正整数。");
            return 2;
        }
        if (memoryBudgetBytesOverride is <= 0)
        {
            Console.Error.WriteLine("--memory-budget-bytes 必须是正整数。");
            return 2;
        }
        SearchPerformanceTier tier;
        try
        {
            tier = SearchPerformanceProfile.ParseTier(searchTier ?? "Medium");
        }
        catch (UnsupportedImportException error)
        {
            Console.Error.WriteLine($"INVALID_INPUT {error.Detail}");
            return 2;
        }
        SearchPerformanceProfile profile = SearchPerformanceProfile.For(tier);
        long memoryBudgetBytes = memoryBudgetBytesOverride
            ?? (memoryBudgetMb is { } budgetMb
                ? (long)budgetMb * 1024 * 1024
                : OfflineCombatSettings.DefaultMemoryBudgetBytes);
        int warmupNodes = int.TryParse(
            Environment.GetEnvironmentVariable("STS2_SOLVER_OFFLINE_WARMUP_NODES"), out int warmup)
            ? Math.Max(0, warmup) : 50_000;
        OfflineCombatSettings settings = new(data, corpora, output, tier, profile,
            memoryBudgetBytes, warmupNodes, maxNodes, poolStateBudgetMb);
        return OfflineCombat.Run(settings, root, rootsFile, jobs);
    }

    private static int EmitLiveRequests(string data, string corpora, string output, string? rootsFile,
        bool all, string? root, string? searchTier = null, bool searchNormal = false,
        int? searchCancelAfterMilliseconds = null, bool fullCombat = false)
    {
        if (searchCancelAfterMilliseconds is < 1)
        {
            Console.Error.WriteLine("--search-cancel-after-ms 必须是正整数。");
            return 2;
        }
        if (searchTier != null)
        {
            try
            {
                _ = SearchPerformanceProfile.ParseTier(searchTier);
            }
            catch (UnsupportedImportException error)
            {
                Console.Error.WriteLine($"INVALID_INPUT {error.Detail}");
                return 2;
            }
        }
        string[] roots;
        if (rootsFile != null)
        {
            if (all || root != null)
            {
                Console.Error.WriteLine("--roots-file 不能与 --root/--all 同时使用。");
                return 2;
            }
            roots = File.ReadAllLines(rootsFile)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }
        else if (all)
        {
            if (root != null)
            {
                Console.Error.WriteLine("--all 与 --root 不能同时使用。");
                return 2;
            }
            roots = EnumerateRoots(data);
        }
        else if (root != null)
        {
            roots = [root];
        }
        else
        {
            Console.Error.WriteLine("--emit-live-requests 需要 --root、--roots-file 或 --all。");
            return 2;
        }
        Directory.CreateDirectory(output);
        List<string> emitted = [];
        foreach (string name in roots)
        {
            OfflineRequest request;
            try
            {
                request = LoadRequest(data, corpora, name);
            }
            catch (InvalidDataException error)
            {
                Console.Error.WriteLine($"root={name} INVALID_INPUT {error.Message}");
                return 2;
            }
            if (request.FlashPrefixActions.Length != 0 || request.FlashPrefixPlan.Length != 0)
            {
                Console.WriteLine($"root={name} skipped=prefix");
                continue;
            }
            if (request.GeneratedScenarioPath is { Length: > 0 } scenarioPath)
                request = request with { GeneratedScenarioPath = Path.GetFullPath(scenarioPath) };
            if (searchTier != null)
                request = request with { SearchTier = searchTier };
            if (searchNormal || fullCombat)
                request = request with { DeterministicSearchForTest = false };
            if (fullCombat)
                request = request with { FullCombatForTest = true };
            if (searchCancelAfterMilliseconds is { } cancelAfter)
                request = request with { SearchCancelAfterMillisecondsForTest = cancelAfter };
            string path = Path.Combine(output, name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(request, OfflineJson.Options));
            emitted.Add(name);
            Console.WriteLine($"root={name} request={path}");
        }
        File.WriteAllText(Path.Combine(output, "manifest.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, roots = emitted }, OfflineJson.Options));
        Console.WriteLine($"emit-live-requests roots={emitted.Count} out={output}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            用法：
              dotnet Differential.dll --root <name> [--out <dir>] [--data <dir>] [--corpora <dir>]
              dotnet Differential.dll --all [--jobs 1..8] [--out <dir>]
              dotnet Differential.dll --root <name> --import-compare [--compiled-rules]
              dotnet Differential.dll --all --jobs 1..8 --import-compare [--compiled-rules]
              dotnet Differential.dll --all --jobs 1..8 --rules-compare
              dotnet Differential.dll --root <name> --config-compare
              dotnet Differential.dll --all --jobs 1..8 --config-compare
              dotnet Differential.dll --root <name> --recapture-root [--out <dir>]
              dotnet Differential.dll --all --jobs 1..8 --recapture-root [--out <dir>]
              dotnet Differential.dll --rules-table-check
              dotnet Differential.dll --import-negative [--root <name>]
              dotnet Differential.dll --combat-pilot-compare [--pilot-root <name>]
                  [--pilot-jobs 1..8] [--pilot-shard i n]
              dotnet Differential.dll --compare-only <newDir> <referenceDir>
              dotnet Differential.dll --all --compare <referenceDir>
              dotnet Differential.dll --live-compare <resultDir>
              dotnet Differential.dll --search-compare <resultDir>
              dotnet Differential.dll --emit-live-requests <dir> (--root <name> | --roots-file <file> | --all)
                  [--search-tier Low|Medium|High|ExtraHigh] [--search-normal]
                  [--search-cancel-after-ms N] [--full-combat]
              dotnet Differential.dll --list-quality-roots [--corpora <dir>]
              dotnet Differential.dll --offline-combat [--root <name> | --roots-file <file> | --corpora <dir>]
                  [--search-tier Low|Medium|High|ExtraHigh] [--jobs 1..8] [--out <dir>]
                  [--pool-state-budget-mb N] [--memory-budget-mb N]
            """);
    }
}
