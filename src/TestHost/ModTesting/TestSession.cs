using System.Diagnostics;
using System.Text.Json;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.TestHost;
using Sts2Solver.Autoplay;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace Sts2Solver;

/// <summary>
/// 无头测试模式：按文件名顺序依次处理 requests/*.json，每个请求建局、注入、等到可出牌，
/// 在主线程导入并把 BATL/RULE/SREQ 与原生状态转储写到 results/&lt;name&gt;/。
/// </summary>
internal static class TestSession
{
    public const string DirectoryVariable = "STS2_SOLVER_TEST_DIR";

    private static bool _started;

    public static bool Active { get; private set; }

    /// <summary>Lets the auto-start node run during the explicit settings-driven scenario.</summary>
    internal static bool AllowAutoStartForTest { get; set; }

    public static void Start()
    {
        if (_started || !ModEntry.Enabled || !ModEntry.TestMode)
            return;
        _started = true;
        string? configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ModLog.Error("[Sts2Solver] TEST_NOT_STARTED reason=missing_STS2_SOLVER_TEST_DIR");
            return;
        }
        string root = Path.GetFullPath(configured);
        string requestDirectory = Path.Combine(root, "requests");
        if (!Directory.Exists(requestDirectory))
        {
            ModLog.Error($"[Sts2Solver] TEST_NOT_STARTED reason=missing_requests directory={requestDirectory}");
            return;
        }
        Directory.CreateDirectory(Path.Combine(root, "results"));
        NGame? host = NGame.Instance;
        if (host == null)
        {
            ModLog.Error("[Sts2Solver] TEST_NOT_STARTED reason=NGame_instance_missing");
            return;
        }
        Active = true;
        ModLog.Info($"[Sts2Solver] TEST_STARTED requests={requestDirectory}");
        _ = RunAsync(host, root, requestDirectory);
    }

    public static void OnCombatEnded()
    {
        if (Active)
            ModLog.Info("[Sts2Solver] COMBAT_ENDED test_session_active=true");
    }

    private static async Task RunAsync(NGame host, string testRoot, string requestDirectory)
    {
        string resultRoot = Path.Combine(testRoot, "results");
        await WaitForMainMenuReadyAsync(host);
        ModLog.Info("[Sts2Solver] TEST_MAIN_MENU_READY");
        string[] files = Directory.GetFiles(requestDirectory, "*.json")
            .Where(path => !string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        List<ResultEntry> results = [];
        Stopwatch total = Stopwatch.StartNew();
        int ok = 0;
        int failed = 0;
        UiLogicChecks.UiLogicCheckResult logic = UiLogicChecks.Run(testRoot);
        WriteJson(Path.Combine(resultRoot, "ui-checks.json"), logic);
        if (logic.Failed > 0)
        {
            failed++;
            results.Add(new ResultEntry("ui-logic-checks", "failed", "ui_checks", 0,
                string.Join("; ", logic.Checks.Where(static check => check.Status != "ok")
                    .Select(static check => check.Name + ":" + check.Error)),
                [], false, "skipped", 0));
        }
        foreach (string file in files)
        {
            string root = Path.GetFileNameWithoutExtension(file);
            string resultDirectory = Path.Combine(resultRoot, root);
            Directory.CreateDirectory(resultDirectory);
            Stopwatch stopwatch = Stopwatch.StartNew();
            string stage = "load";
            SolverSettingsData previousSettings = SolverSettings.Current;
            bool settingsChanged = false;
            bool recordSnapshots = false;
            UiPanelChecks.Session? panelChecks = null;
            void CaptureFailed(string resultDetail, string logDetail)
            {
                failed++;
                results.Add(Failure(root, stage, stopwatch, resultDetail, null, false));
                ModLog.Error($"[Sts2Solver] CAPTURE_FAILED root={root} stage={stage} {logDetail}");
            }
            try
            {
                OfflineRequest request = JsonSerializer.Deserialize<OfflineRequest>(
                    File.ReadAllText(file), OfflineJson.Options)
                    ?? throw new InvalidDataException("请求为空。");
                Validate(request);
                settingsChanged = ApplyTestSettings(request);
                SearchPerformanceTier tier = request.SearchTierFromSettingsForTest
                    ? SolverSettings.Current.Search.Tier
                    : SearchPerformanceProfile.ParseTier(request.SearchTier);
                CombatSessionController.SetTestRoot(root);
                SearchSession.ConfigureProgress(true,
                    request.ControllerScenarioForTest is { Length: > 0 } ? 0 : 200);
                bool turnSetupScenario =
                    request.ControllerScenarioForTest == "turn_setup_wait";
                TurnSetupCardSelector? setupSelector = null;
                if (turnSetupScenario)
                {
                    CombatControllerTestHooks.TurnSetupSearchCountAtSelection = -1;
                    setupSelector = new TurnSetupCardSelector(0, static () =>
                    {
                        if (CombatControllerTestHooks.TurnSetupSearchCountAtSelection < 0)
                        {
                            CombatControllerTestHooks.TurnSetupSearchCountAtSelection =
                                CombatSessionController.SearchCountForTest;
                        }
                    });
                }
                if (request.UiSnapshotForTest || request.ControllerScenarioForTest != null)
                {
                    CombatUiStore.ClearHistory();
                    CombatUiStore.RecordHistory = true;
                    recordSnapshots = true;
                }
                FirstChoiceCardSelector.LastSelections.Clear();
                if (request.FlashTurnSetupRootForTest)
                    TurnSetupRootCapture.BeginBuild();
                stage = "build";
                CombatSessionController.HoldAutoSearchForTest = !turnSetupScenario;
                ModLog.Info($"[Sts2Solver] BUILD_STARTED root={root}");
                CombatState combat = await ScenarioBuilder.BuildAsync(
                    new GodotCombatWaiter(host), request,
                    skipInitialInjection: request.FlashTurnSetupRootForTest,
                    turnSetupSelector: setupSelector);
                ModLog.Info($"[Sts2Solver] BUILD_COMPLETED root={root}");
                stage = "settle";
                await CombatFrameWaiter.SettleAsync(host);
                if (request.FullCombatForTest || request.ControllerScenarioForTest != null)
                    CombatSessionController.ResumeAutoSearchForTest();
                stage = "capture";
                CapturedSearch? captured = request.ControllerScenarioForTest is { Length: > 0 }
                    ? null
                    : Capture(resultDirectory, root, request, combat, tier);
                string? failure = null;
                if (request.ControllerScenarioForTest is { Length: > 0 } scenario)
                {
                    stage = "controller";
                    CombatControllerScenarioResult scenarioResult =
                        await CombatControllerChecks.RunAsync(host, request, scenario, root,
                            resultDirectory);
                    WriteJson(Path.Combine(resultDirectory, "controller.json"), scenarioResult);
                    failure = scenarioResult.Status == "ok" ? null : scenarioResult.Error;
                    stage = "status";
                    WriteJson(Path.Combine(resultDirectory, "status.json"), new StatusEntry(
                        2, root, "ok", stage, stopwatch.ElapsedMilliseconds, failure,
                        [.. FirstChoiceCardSelector.LastSelections],
                        request.FlashTurnSetupRootForTest, 0, "skipped", tier.ToString(), false,
                        0, 0, 0, 0, 0, 0, 0, false, 0, 0, true, null,
                        scenarioResult.Status, scenarioResult.Outcome, scenarioResult.Turns,
                        scenarioResult.PlayerHp, scenarioResult.PlayerMaxHp,
                        scenarioResult.Searches, scenarioResult.ReusedTurns,
                        scenarioResult.Deviations, scenarioResult.DeploymentFailures));
                    if (failure == null)
                    {
                        ok++;
                        results.Add(new ResultEntry(root, "ok", stage,
                            stopwatch.ElapsedMilliseconds, null,
                            [.. FirstChoiceCardSelector.LastSelections],
                            request.FlashTurnSetupRootForTest, scenarioResult.Status, 0));
                    }
                    else
                    {
                        failed++;
                        results.Add(new ResultEntry(root, "failed", stage,
                            stopwatch.ElapsedMilliseconds, failure,
                            [.. FirstChoiceCardSelector.LastSelections],
                            request.FlashTurnSetupRootForTest, "failed", 0));
                        ModLog.Error($"[Sts2Solver] CONTROLLER_SCENARIO_FAILED root={root} {failure}");
                    }
                }
                else if (request.FullCombatForTest)
                {
                    if (captured == null)
                        throw new InvalidDataException("整场测试不支持回合准备根。");
                    stage = "ui_panel";
                    panelChecks = await UiPanelChecks.BeginAsync(host);
                    stage = "full_combat";
                    CombatPilotResult auto = await RunControllerCombatAsync(
                        host, root, request, tier, resultDirectory);
                    WriteJson(Path.Combine(resultDirectory, "full-combat.json"), auto);
                    if (request.UiSnapshotForTest)
                        UiSnapshotChecks.Run(resultDirectory, auto, CombatUiStore.History);
                    stage = "ui_panel";
                    await UiPanelChecks.FinishAsync(host, panelChecks, resultDirectory);
                    panelChecks = null;
                    bool expectedCancel = request.SearchCancelAfterMillisecondsForTest is not null;
                    bool ended = auto.Status is "victory" or "defeat"
                        || expectedCancel && auto.Status == "cancelled";
                    if (!ended)
                    {
                        failure = $"full_combat {auto.Status}: {auto.PauseReason ?? "战斗未结束"}" +
                            $" turn={auto.Turns} hp={auto.PlayerHp}/{auto.PlayerMaxHp} " +
                            $"searches={auto.Searches} reused={auto.ReusedTurns} " +
                            $"deviations={auto.Deviations} failures={auto.DeploymentFailures}";
                    }
                    stage = "status";
                    WriteJson(Path.Combine(resultDirectory, "status.json"), new StatusEntry(
                        2, root, "ok", stage, stopwatch.ElapsedMilliseconds, failure,
                        [.. FirstChoiceCardSelector.LastSelections], request.FlashTurnSetupRootForTest, 0,
                        auto.Status, tier.ToString(), false,
                        auto.ElapsedMs, 0, 0, 0, 0, 0, auto.Turns,
                        auto.Status == "victory", 0, 0, true, null,
                        auto.Status, auto.Outcome, auto.Turns, auto.PlayerHp, auto.PlayerMaxHp,
                        auto.Searches, auto.ReusedTurns, auto.Deviations, auto.DeploymentFailures));
                    if (failure == null)
                    {
                        ok++;
                        results.Add(new ResultEntry(root, "ok", stage, stopwatch.ElapsedMilliseconds, null,
                            [.. FirstChoiceCardSelector.LastSelections], request.FlashTurnSetupRootForTest,
                            auto.Status, auto.ElapsedMs));
                        ModLog.Info(
                            $"[Sts2Solver] FULL_COMBAT_OK root={root} status={auto.Status} " +
                            $"turns={auto.Turns} hp={auto.PlayerHp}/{auto.PlayerMaxHp} " +
                            $"searches={auto.Searches} reused={auto.ReusedTurns} " +
                            $"deviations={auto.Deviations} failures={auto.DeploymentFailures}");
                    }
                    else
                    {
                        failed++;
                        results.Add(new ResultEntry(root, "failed", stage, stopwatch.ElapsedMilliseconds,
                            failure, [.. FirstChoiceCardSelector.LastSelections],
                            request.FlashTurnSetupRootForTest, auto.Status, auto.ElapsedMs));
                        ModLog.Error($"[Sts2Solver] FULL_COMBAT_FAILED root={root} {failure}");
                    }
                }
                else
                {
                    SearchRunResult? search = null;
                    string? searchFailure = null;
                    if (captured != null)
                    {
                        stage = "search";
                        search = await SearchSession.RunAsync(
                            host, root, captured.SearchCase, captured.Deterministic, captured.Tier,
                            resultDirectory, request.SearchCancelAfterMillisecondsForTest);
                        bool expectedCancel = request.SearchCancelAfterMillisecondsForTest is not null;
                        bool searchOk = search.Status == "ok"
                            || expectedCancel && search.Status == "cancelled" && search.WorkerExited;
                        if (!searchOk)
                            searchFailure = $"search {search.Status}: {search.Error}";
                        else if (search.RouteSummary.Length > 0)
                            ModLog.Info($"[Sts2Solver] SEARCH_ROUTE root={root} {search.RouteSummary}");
                    }
                    failure = searchFailure;
                    stage = captured == null ? "capture" : "status";
                    WriteJson(Path.Combine(resultDirectory, "status.json"), new StatusEntry(
                        2, root, "ok", stage, stopwatch.ElapsedMilliseconds, searchFailure,
                        [.. FirstChoiceCardSelector.LastSelections], request.FlashTurnSetupRootForTest, 0,
                        search?.Status ?? (captured == null ? "skipped" : "not_started"),
                        tier.ToString(), request.DeterministicSearchForTest,
                        search?.ElapsedMilliseconds ?? 0,
                        search?.ManagedHeapPeakBytes ?? 0,
                        search?.WorkingSetPeakBytes ?? 0,
                        search?.AllocatedBytes ?? 0,
                        search?.ExpandedNodes ?? 0,
                        search?.Transitions ?? 0,
                        search?.TurnsSearched ?? 0,
                        search?.Victory ?? false,
                        search?.Score ?? 0,
                        search?.RouteActionCount ?? 0,
                        search?.WorkerExited ?? captured == null,
                        search?.Error));
                    if (searchFailure == null)
                    {
                        ok++;
                        results.Add(new ResultEntry(root, "ok", stage, stopwatch.ElapsedMilliseconds, null,
                            [.. FirstChoiceCardSelector.LastSelections], request.FlashTurnSetupRootForTest,
                            search?.Status ?? "skipped", search?.ElapsedMilliseconds ?? 0));
                        ModLog.Info(
                            $"[Sts2Solver] CAPTURE_OK root={root} ms={stopwatch.ElapsedMilliseconds} "
                            + $"search={search?.Status ?? "skipped"}");
                    }
                    else
                    {
                        failed++;
                        results.Add(new ResultEntry(root, "failed", stage, stopwatch.ElapsedMilliseconds,
                            searchFailure, [.. FirstChoiceCardSelector.LastSelections],
                            request.FlashTurnSetupRootForTest, search?.Status ?? "failed",
                            search?.ElapsedMilliseconds ?? 0));
                        ModLog.Error($"[Sts2Solver] SEARCH_FAILED root={root} stage={stage} {searchFailure}");
                    }
                }
            }
            catch (UnsupportedImportException error)
            { CaptureFailed($"unsupported reason={error.Reason} {error.Detail}", $"unsupported reason={error.Reason} {error.Detail}"); }
            catch (UnsupportedException error)
            { CaptureFailed(error.Message, $"unsupported {error.Message}"); }
            catch (CapacityException error)
            { CaptureFailed($"capacity region={error.Region} {error.Message}", $"capacity region={error.Region} {error.Message}"); }
            catch (NotSupportedException error)
            { CaptureFailed(error.Message, $"unsupported {error.Message}"); }
            catch (InvalidDataException error)
            { CaptureFailed(error.Message, $"invalid_data {error.Message}"); }
            catch (InvalidOperationException error)
            { CaptureFailed(error.Message, $"invalid_operation {error.Message}"); }
            catch (TimeoutException error)
            { CaptureFailed(error.Message, $"timeout {error.Message}"); }
            catch (OperationCanceledException error)
            { CaptureFailed(error.Message, $"canceled {error.Message}"); }
            catch (IOException error)
            { CaptureFailed(error.Message, $"io {error.Message}"); }
            catch (JsonException error)
            { CaptureFailed(error.Message, $"json {error.Message}"); }
            catch (UnauthorizedAccessException error)
            { CaptureFailed(error.Message, $"access {error.Message}"); }
            finally
            {
                panelChecks?.Stop();
                AllowAutoStartForTest = false;
                if (recordSnapshots)
                    CombatUiStore.RecordHistory = false;
                CombatSessionController.HoldAutoSearchForTest = false;
                CombatControllerTestHooks.ForceDeploymentFailure = null;
                CombatControllerTestHooks.TurnSetupSearchCountAtSelection = -1;
                if (settingsChanged && !SolverSettings.Update(_ => previousSettings))
                    ModLog.Error($"[Sts2Solver] SETTINGS_RESTORE_FAILED root={root}");
            }

            bool breakAfterCleanup = false;
            try
            {
                if (!await CombatSessionController.CancelAndWaitAsync(host, "request_cleanup", 10_000))
                    throw new TimeoutException("控制器取消后仍未退出。");
                if (!await SearchSession.CancelAndWaitAsync(host, "request_cleanup", 10_000))
                    throw new TimeoutException("搜索取消后仍未退出。");
                CombatSessionController.Reset("request_cleanup");
                await ResetToMainMenuAsync(host);
            }
            catch (TimeoutException error)
            {
                ModLog.Error($"[Sts2Solver] RESET_FAILED root={root} timeout {error.Message}");
                breakAfterCleanup = true;
            }
            catch (InvalidOperationException error)
            {
                ModLog.Error($"[Sts2Solver] RESET_FAILED root={root} invalid_operation {error.Message}");
                breakAfterCleanup = true;
            }
            if (breakAfterCleanup)
            {
                failed++;
                results.Add(Failure(root + "-cleanup", "cleanup", stopwatch,
                    "返回主菜单失败，后续请求不再执行", null, false));
                break;
            }
        }
        if (!await CombatSessionController.CancelAndWaitAsync(host, "test_done", 10_000))
        {
            failed++;
            results.Add(Failure("all-done-controller-cancel", "cleanup", total,
                "测试结束时控制器仍未退出", null, false));
        }
        if (!await SearchSession.CancelAndWaitAsync(host, "test_done", 10_000))
        {
            failed++;
            results.Add(Failure("all-done-cancel", "cleanup", total,
                "测试结束时后台搜索仍未退出", null, false));
        }
        total.Stop();
        DoneEntry done = new(1, files.Length, ok, failed, total.Elapsed.TotalMilliseconds, [.. results]);
        WriteJson(Path.Combine(resultRoot, "done.json"), done);
        ModLog.Info(
            $"[Sts2Solver] TEST_DONE requests={files.Length} ok={ok} failed={failed} " +
            $"total_ms={total.Elapsed.TotalMilliseconds:0}");
        Active = false;
        // Use the game's normal shutdown to release cached text/font resources before Godot exits.
        // NGame.Quit requests exit code 0; preserve a failed test's nonzero status afterwards.
        host.Quit();
        host.GetTree().Quit(failed == 0 ? 0 : 1);
    }

    private static CapturedSearch? Capture(string directory, string root, OfflineRequest request,
        CombatState combat, SearchPerformanceTier tier)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        if (combat.RunState is not RunState runState)
            throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
        Rules rules = RulesCompiler.Compile(combat, player, runState);
        ImportOptions importOptions = request.FlashTurnSetupRootForTest
            ? ImportOptions.Default with
            {
                TurnSetupRoot = true,
                RootCards = TurnSetupRootCapture.StartCards
                    ?? throw new InvalidDataException("回合准备根缺少 Start 卡牌集合。"),
            }
            : ImportOptions.Default;
        Battle battle = BattleImporter.Import(combat, player, rules, importOptions);
        byte[] battleBytes = BattleCodec.Encode(battle);
        byte[] rulesBytes = RulesCodec.Encode(rules);
        SearchPolicy policy = SearchPolicyOverrides.ApplyOverrides(
            SearchPolicyCapture.Capture(combat, player), request, player);
        EvaluationRootFacts facts = EvaluationRootFacts.Default with
        {
            InitialActionCount = 0,
            TurnSetupRoot = request.FlashTurnSetupRootForTest,
        };
        SearchCase captureCase = EvaluationConfigCompiler.CompileRequest(
            combat, player, rules, battle, policy, facts);
        WriteBytes(Path.Combine(directory, "BATL"), battleBytes);
        WriteBytes(Path.Combine(directory, "RULE"), rulesBytes);
        WriteBytes(Path.Combine(directory, "SREQ.capture"), SearchCodec.EncodeRequest(captureCase));
        NativeStateDump.Write(combat, Path.Combine(directory, "native.txt"));
        if (request.FlashTurnSetupRootForTest)
            return null;
        SearchCase searchCase = SearchRequestCompiler.CompileRequest(
            combat, player, rules, battle, policy, facts,
            SearchPerformanceProfile.For(tier), request.DeterministicSearchForTest,
            SearchMemoryBudget.Bytes);
        // SearchSession writes SREQ from this case; the budget and thread fields are host-only and
        // are not encoded, so attaching them keeps the request bytes identical while giving the
        // single search the same pool budget the offline replay uses.
        BeamOptions options = request.DeterministicSearchForTest
            ? SearchBudgetPolicy.ApplyRegression(searchCase.Options)
            : SearchBudgetPolicy.Apply(searchCase.Options,
                SearchBudgetPolicy.RegressionEndedKeepWidths,
                SearchBudgetPolicy.RegressionEndedTriggerWidths, SearchMemoryBudget.Bytes);
        if (request.SearchThreadsForTest is { } searchThreads)
            options = options with { SearchThreads = searchThreads };
        SearchCase runCase = searchCase with { Options = options };
        return new CapturedSearch(runCase, request.DeterministicSearchForTest, tier);
    }

    private static ResultEntry Failure(string root, string stage, Stopwatch stopwatch,
        string error, string[]? selections, bool turnSetupRoot)
    {
        return new ResultEntry(root, "failed", stage, stopwatch.ElapsedMilliseconds, error,
            selections ?? [], turnSetupRoot, "failed", 0);
    }

    private static bool ApplyTestSettings(OfflineRequest request)
    {
        if (request.ControllerScenarioForTest is { Length: > 0 } || request.FullCombatForTest)
        {
            CombatSessionController.SearchPolicyOverrideForTest =
                (policy, player) => SearchPolicyOverrides.ApplyOverrides(policy, request, player);
        }
        if (request.SettingsTierForTest is null && request.SettingsAutoEnableFullAutoForTest is null
            && !request.FullCombatForTest)
        {
            return false;
        }
        SearchPerformanceTier? tier = request.SettingsTierForTest is { } text
            ? SearchPerformanceProfile.ParseTier(text)
            : null;
        SolverSettingsData current = SolverSettings.Current;
        SolverMode mode = request.FullCombatForTest
            ? SolverMode.FullAuto
            : request.SettingsAutoEnableFullAutoForTest is { } auto
                ? auto ? SolverMode.FullAuto : SolverMode.AutoSearch
                : current.Automation.Mode;
        SolverSettingsData next = current with
        {
            Search = tier is { } value ? current.Search with { Tier = value } : current.Search,
            Automation = current.Automation with { Mode = mode },
        };
        if (next == current)
            return false;
        if (!SolverSettings.Update(_ => next))
            throw new InvalidOperationException("无法应用测试设置。");
        CombatSessionController.OnSettingsChanged();
        return true;
    }

    private static async Task<CombatPilotResult> RunControllerCombatAsync(NGame host, string root,
        OfflineRequest request, SearchPerformanceTier tier, string resultDirectory)
    {
        FastModeType original = SaveManager.Instance.PrefsSave.FastMode;
        FastModeType instant = ParseFastMode(request.HeadlessFastModeForTest);
        try
        {
            SaveManager.Instance.PrefsSave.FastMode = instant;
            for (int frame = 0; frame < 120 && !CombatSessionController.IsActiveForTest; frame++)
                await CombatFrameWaiter.NextFrameAsync(host);
            if (!CombatSessionController.IsActiveForTest)
                throw new TimeoutException("控制器没有进入战斗。");
            ModLog.Info(
                $"[Sts2Solver] FULL_COMBAT_START root={root} fast_mode={instant} " +
                $"tier={tier} mode={SolverSettings.Current.Automation.Mode} " +
                $"auto_start={request.AutoStartFullCombatForTest}");
            if (request.AutoStartFullCombatForTest)
            {
                long startDeadline = Environment.TickCount64 + 15_000;
                while (!CombatSessionController.FullAutoEnabled)
                {
                    if (Environment.TickCount64 >= startDeadline)
                        throw new TimeoutException("等待全自动模式武装超时。");
                    await CombatFrameWaiter.NextFrameAsync(host);
                }
                WriteJson(Path.Combine(resultDirectory, "auto-start.json"), new AutoStartEntry(
                    1, "mode", true, tier.ToString(),
                    SolverSettings.Current.Automation.Mode == SolverMode.FullAuto));
            }
            long deadline = Environment.TickCount64
                + (long)Math.Max(request.TimeoutSeconds, 480d) * 1000L;
            while (CombatManager.Instance.IsInProgress && !CombatSessionController.IsPaused)
            {
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("等待控制器整场结束超时。");
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            for (int frame = 0; frame < 120
                && CombatSessionController.LastCompletedResultForTesting == null; frame++)
            {
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            CombatPilotResult result =
                CombatSessionController.LastCompletedResultForTesting
                ?? CombatSessionController.CapturePilotResultForTest();
            ModLog.Info(
                $"[Sts2Solver] FULL_COMBAT_CONTROLLER root={root} status={result.Status} " +
                $"outcome={result.Outcome} turns={result.Turns} hp={result.PlayerHp}/{result.PlayerMaxHp} " +
                $"searches={result.Searches} reused={result.ReusedTurns} " +
                $"deviations={result.Deviations} failures={result.DeploymentFailures} " +
                $"elapsed_ms={result.ElapsedMs}");
            return result;
        }
        finally
        {
            CombatSessionController.SearchPolicyOverrideForTest = null;
            SaveManager.Instance.PrefsSave.FastMode = original;
            ModLog.Info(
                $"[Sts2Solver] FULL_COMBAT_FAST_MODE_RESTORED root={root} mode={original}");
        }
    }

    private static FastModeType ParseFastMode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || string.Equals(text, "FollowGame", StringComparison.OrdinalIgnoreCase))
        {
            return FastModeType.Instant;
        }
        return text.ToLowerInvariant() switch
        {
            "instant" => FastModeType.Instant,
            "fast" => FastModeType.Fast,
            "normal" => FastModeType.Normal,
            _ => throw new NotSupportedException($"不支持的快速模式：{text}"),
        };
    }

    private static void Validate(OfflineRequest request)
    {
        if (request.SchemaVersion != 1)
            throw new NotSupportedException($"不支持的请求 schemaVersion：{request.SchemaVersion}。");
        if (request.FlashPrefixActions.Length != 0 || request.FlashPrefixPlan.Length != 0)
            throw new NotSupportedException("本批实机捕获只支持普通根；前缀根请用离线宿主。");
        if (request.GeneratedScenarioPath is { Length: > 0 } path && !Path.IsPathRooted(path))
            throw new NotSupportedException($"generatedScenarioPath 必须是绝对路径：{path}");
        _ = SearchPerformanceProfile.ParseTier(request.SearchTier);
        if (request.SettingsTierForTest is { } settingsTier)
            _ = SearchPerformanceProfile.ParseTier(settingsTier);
        if (request.SearchCancelAfterMillisecondsForTest is <= 0)
            throw new NotSupportedException(
                $"searchCancelAfterMillisecondsForTest 必须为正：{request.SearchCancelAfterMillisecondsForTest}");
        if (request.SearchThreadsForTest is < 1 or > 8)
            throw new NotSupportedException(
                $"searchThreadsForTest 必须在 1..8：{request.SearchThreadsForTest}");
        if (request.FullCombatForTest && request.FlashTurnSetupRootForTest)
            throw new NotSupportedException("整场测试不支持回合准备根。");
        if (request.UiSnapshotForTest && !request.FullCombatForTest)
            throw new NotSupportedException("uiSnapshotForTest 需要 fullCombatForTest。");
        if (request.AutoStartFullCombatForTest && !request.FullCombatForTest)
            throw new NotSupportedException("autoStartFullCombatForTest 需要 fullCombatForTest。");
        if (request.AutoStartFullCombatForTest && request.FlashTurnSetupRootForTest)
            throw new NotSupportedException("自动开启场景不支持回合准备根。");
        if (request.ControllerScenarioForTest is { } scenario)
        {
            if (scenario.Length == 0)
                throw new NotSupportedException("controllerScenarioForTest 不能为空。");
            if (request.FullCombatForTest)
                throw new NotSupportedException("控制器场景不能与 fullCombatForTest 同时使用。");
        }
        if (request.AutoStartFullCombatForTest && request.SearchCancelAfterMillisecondsForTest is not null)
            throw new NotSupportedException("自动开启场景不支持 searchCancelAfterMillisecondsForTest。");
    }

    private static async Task WaitForMainMenuReadyAsync(NGame host)
    {
        long deadline = Environment.TickCount64 + 120_000;
        while (Environment.TickCount64 < deadline)
        {
            if (host.MainMenu != null && !RunManager.Instance.IsInProgress)
            {
                await CombatFrameWaiter.NextFrameAsync(host);
                await CombatFrameWaiter.NextFrameAsync(host);
                return;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException("等待主菜单就绪超时。");
    }

    private static async Task ResetToMainMenuAsync(NGame host)
    {
        if (RunManager.Instance.IsInProgress)
            await host.ReturnToMainMenu();
        long deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            if (!RunManager.Instance.IsInProgress
                && host.MainMenu != null
                && CombatManager.Instance.DebugOnlyGetState() == null)
            {
                return;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException("返回主菜单超时。");
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }

    private static void WriteJson<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, OfflineJson.Options));
        File.Move(temporary, path, true);
    }

    private sealed record AutoStartEntry(
        int SchemaVersion,
        string Source,
        bool StartedBySetting,
        string Tier,
        bool SettingEnabled);

    private sealed record CapturedSearch(
        SearchCase SearchCase,
        bool Deterministic,
        SearchPerformanceTier Tier);

    private sealed record StatusEntry(
        int SchemaVersion,
        string Root,
        string Status,
        string Stage,
        long ElapsedMs,
        string? Error,
        string[] SetupSelections,
        bool TurnSetupRoot,
        int InitialActionCount,
        string SearchStatus,
        string SearchTier,
        bool DeterministicSearch,
        long SearchElapsedMs,
        long SearchManagedHeapPeakBytes,
        long SearchWorkingSetPeakBytes,
        long SearchAllocatedBytes,
        int SearchExpandedNodes,
        int SearchTransitions,
        int SearchTurns,
        bool SearchVictory,
        double SearchScore,
        int SearchRouteActions,
        bool SearchWorkerExited,
        string? SearchError,
        string FullCombatStatus = "not_requested",
        string FullCombatOutcome = "",
        int FullCombatTurns = 0,
        int FullCombatPlayerHp = 0,
        int FullCombatPlayerMaxHp = 0,
        int FullCombatSearches = 0,
        int FullCombatReusedTurns = 0,
        int FullCombatDeviations = 0,
        int FullCombatDeploymentFailures = 0);

    private sealed record ResultEntry(
        string Root,
        string Status,
        string Stage,
        long ElapsedMs,
        string? Error,
        string[] SetupSelections,
        bool TurnSetupRoot,
        string SearchStatus,
        long SearchElapsedMs);

    private sealed record DoneEntry(
        int SchemaVersion,
        int Requests,
        int Ok,
        int Failed,
        double TotalMs,
        ResultEntry[] Results);
}
