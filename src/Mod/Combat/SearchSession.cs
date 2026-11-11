// In-game worker for a single member of the combat search portfolio. The main-thread capture / background worker / main-thread completion
// split and the cancellation lifecycle are adapted from CombatSolver src/Runtime/SolverController.cs
// at commit a62f1850 (MIT, Copyright (c) 2026 Torch1230 and contributors); each call runs one search member; CombatSearchRunner owns portfolio scheduling and the controller owns deployment.
using System.Diagnostics;
using System.Text.Json;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

/// <summary>
/// The single-Beam driver. Capture/import/request assembly happen on the game main thread; the
/// solver then owns only the copied <see cref="SearchCase"/> and runs on a worker thread. Nothing in
/// this type reads or writes a live model after the capture returns.
/// </summary>
internal static class SearchSession
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
    private static CancellationTokenSource? _cancellation;
    private static Task<BeamResult>? _worker;
    private static string? _cancelReason;
    private static int _running;
    private static string _root = "";
    private static int _progressSubscribed;
    private static int _progressThrottleMilliseconds = 200;
    private static SearchProgressChannel? _activeProgress;
    private static SearchProgressSnapshot? _latestProgress;

    static SearchSession()
    {
#if STS2_SOLVER_TESTING
        // The testing build can force progress on (throttle 0 = every checkpoint) for headless runs.
        if (Environment.GetEnvironmentVariable("STS2_SOLVER_SEARCH_PROGRESS") == "1")
            _progressSubscribed = 1;
        if (int.TryParse(Environment.GetEnvironmentVariable(
                "STS2_SOLVER_SEARCH_PROGRESS_THROTTLE_MS"), out int throttle) && throttle >= 0)
            _progressThrottleMilliseconds = throttle;
#endif
    }

    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    internal static bool ProgressSubscribed => Volatile.Read(ref _progressSubscribed) != 0;

    /// <summary>The newest immutable search snapshot; the main thread may poll it every frame.</summary>
    internal static SearchProgressSnapshot? LatestProgress => Volatile.Read(ref _latestProgress);

    /// <summary>Latest published candidate version, or zero when no search is running.</summary>
    internal static int LatestCandidateVersion
        => Volatile.Read(ref _activeProgress)?.LatestCandidateVersion ?? 0;

    /// <summary>
    /// Enables or disables progress publication for subsequent searches. The panel calls this when
    /// its visible state changes; it does not affect the running solve.
    /// </summary>
    internal static void ConfigureProgress(bool subscribed, int throttleMilliseconds = 200)
    {
        Volatile.Write(ref _progressSubscribed, subscribed ? 1 : 0);
        Volatile.Write(ref _progressThrottleMilliseconds, Math.Max(0, throttleMilliseconds));
    }

    /// <summary>Creates the per-request channel; null means the search does no progress work.</summary>
    internal static SearchProgressChannel? BeginProgress()
    {
        if (Volatile.Read(ref _progressSubscribed) == 0)
        {
            Volatile.Write(ref _latestProgress, null);
            Volatile.Write(ref _activeProgress, null);
            return null;
        }
        SearchProgressChannel channel = new(Volatile.Read(ref _progressThrottleMilliseconds))
        {
            Published = static snapshot => Volatile.Write(ref _latestProgress, snapshot),
        };
        Volatile.Write(ref _latestProgress, null);
        Volatile.Write(ref _activeProgress, channel);
        return channel;
    }

    internal static void EndProgress(SearchProgressChannel? channel)
    {
        if (channel != null)
            Interlocked.CompareExchange(ref _activeProgress, null, channel);
    }

    /// <summary>Stops the search; a published candidate is preserved, otherwise it is cancelled.</summary>
    internal static bool RequestStop()
        => Volatile.Read(ref _activeProgress)?.RequestStop() == true;

    /// <summary>
    /// Compiles a request for an already imported root and an already frozen rule table. Auto
    /// combat reuses the combat's first <see cref="Rules"/>: the native enemy list no longer holds
    /// dead monsters, but the frozen rule table still describes the root's full monster closure.
    /// </summary>
    public static SearchCase CompileCase(CombatState combat, Player player, Rules rules, Battle battle,
        SearchPolicyConfiguration policyConfiguration,
        Func<SearchPolicy, Player, SearchPolicy>? configurePolicy, bool deterministic, SearchPerformanceTier tier)
    {
        SearchPolicy policy = SearchPolicyCapture.Capture(combat, player, policyConfiguration);
        if (configurePolicy != null)
            policy = configurePolicy(policy, player);
        return SearchRequestCompiler.CompileRequest(
            combat, player, rules, battle, policy, EvaluationRootFacts.Default,
            SearchPerformanceProfile.For(tier), deterministic, SearchMemoryBudget.Bytes);
    }

    /// <summary>
    /// Compiles the multi-root setup request from the native Start root and the enumerated Play
    /// branches. The evaluation facts match TS1's setup request: <c>TurnSetupRoot=true</c> and no
    /// actions have been replayed yet.
    /// </summary>
    public static SearchCase CompileSetupCase(CombatState combat, Player player, Rules rules,
        Battle startRoot, IReadOnlyList<TurnSetupBranch> branches,
        SearchPolicyConfiguration policyConfiguration,
        Func<SearchPolicy, Player, SearchPolicy>? configurePolicy, bool deterministic,
        SearchPerformanceTier tier)
    {
        SearchPolicy policy = SearchPolicyCapture.Capture(combat, player, policyConfiguration);
        if (configurePolicy != null)
            policy = configurePolicy(policy, player);
        return SearchRequestCompiler.CompileSetupRequest(
            combat, player, rules, startRoot, branches, policy, EvaluationRootFacts.Default,
            SearchPerformanceProfile.For(tier), deterministic, SearchMemoryBudget.Bytes);
    }

    /// <summary>
    /// Runs one already-assembled request. The request options must not contain host callbacks; the
    /// pacer is added only to the in-memory solve copy, never to the encoded SREQ.
    /// </summary>
    public static async Task<SearchRunResult> RunAsync(
        NGame host,
        string root,
        SearchCase searchCase,
        bool deterministic,
        SearchPerformanceTier tier,
        string? resultDirectory,
        int? cancelAfterMilliseconds,
        SearchStorage? storage = null, CancellationToken token = default)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("搜索必须从游戏主线程启动。");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return SearchRunResult.Busy(root);
        if (searchCase.Options.Pacer != null
            || searchCase.Options.Trace != null)
        {
            Volatile.Write(ref _running, 0);
            throw new InvalidOperationException("搜索请求不得包含宿主回调。");
        }
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<BeamResult>? worker = null;
        BeamSolver? solver = null;
        _root = root;
        lock (Gate)
        {
            _cancellation = cancellation;
            _cancelReason = null;
        }

        try
        {
            SearchFramePressureSignal signal = new();
            SearchWorkPacer? pacer = deterministic ? null : new SearchWorkPacer(signal);
            BeamOptions solveOptions = pacer == null
                ? searchCase.Options
                : searchCase.Options with { Pacer = pacer };
            byte[] requestBytes = SearchCodec.EncodeRequest(searchCase);
            if (resultDirectory != null && deterministic)
                WriteBytes(Path.Combine(resultDirectory, "SREQ"), requestBytes);
            ModLog.Info(
                $"[Sts2Solver] SEARCH_STARTED root={root} tier={tier} deterministic={deterministic} "
                + $"threads={searchCase.Options.SearchThreads} "
                + $"beam={searchCase.Options.BeamWidth} "
                + (searchCase.Options.UnlimitedNodes ? "nodes=unlimited " : $"nodes={searchCase.Options.MaxExpandedNodes} ")
                + $"soft_ms={(deterministic ? -1 : (long)searchCase.Options.SoftTimeBudget.TotalMilliseconds)} "
                + $"turn={searchCase.Options.InitialPlayerTurn} root_bytes={requestBytes.Length}");

            Stopwatch elapsed = Stopwatch.StartNew();
            long managedPeak = 0;
            long workingSetPeak = 0;
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            // Process-wide like the allocation count: another mod's collections stop these workers too.
            GcActivity gcBefore = GcActivity.Capture();
            int longFrames = 0;
            double maxFrameMilliseconds = 0d;
            long lastFrame = Stopwatch.GetTimestamp();
            worker = Task.Run(() =>
            {
                solver = new BeamSolver(searchCase.Rules, solveOptions);
                return solver.Solve(searchCase.Battles, storage ?? new SearchStorage(),
                    cancellation.Token);
            });
            lock (Gate)
                _worker = worker;

            while (!worker.IsCompleted)
            {
                await CombatFrameWaiter.NextFrameAsync(host);
                long now = Stopwatch.GetTimestamp();
                if (!deterministic)
                {
                    double frameMilliseconds = Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds;
                    signal.ObserveFrame(frameMilliseconds, searchActive: true);
                    if (frameMilliseconds >= signal.PressureFrameGapMilliseconds)
                        longFrames++;
                    maxFrameMilliseconds = Math.Max(maxFrameMilliseconds, frameMilliseconds);
                }
                lastFrame = now;
                SampleMemory(ref managedPeak, ref workingSetPeak);
                if (cancelAfterMilliseconds is { } limit
                    && elapsed.ElapsedMilliseconds >= limit
                    && !cancellation.IsCancellationRequested)
                {
                    Cancel("test_cancel_after_ms");
                }
            }
            if (!NGame.IsMainThread())
                throw new InvalidOperationException("搜索结果没有回到游戏主线程。");
            elapsed.Stop();
            SampleMemory(ref managedPeak, ref workingSetPeak);
            long allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
            int frameWaits = pacer?.FrameRecoveryWaitCount ?? 0;
            long frameWaitMilliseconds = (long)(pacer?.FrameRecoveryWaitDuration.TotalMilliseconds ?? 0d);
            GcActivity gc = GcActivity.Capture().Since(gcBefore);
            string runtime = $"gc_pause_ms={gc.PauseMilliseconds} gc_counts={gc.Gen0}/{gc.Gen1}/{gc.Gen2} "
                + $"memory_load_pct={gc.MemoryLoadPercent} long_frames={longFrames} "
                + $"max_frame_ms={maxFrameMilliseconds:0}";

            bool workerExited = worker.IsCompleted;
            SearchRunResult result;
            if (cancellation.IsCancellationRequested || worker.IsCanceled)
            {
                result = SearchRunResult.Cancelled(root, elapsed.ElapsedMilliseconds,
                    solver?.ExpandedNodes ?? 0, solver?.TransitionCount ?? 0,
                    managedPeak, workingSetPeak, allocated, workerExited, pacer?.YieldCount ?? 0,
                    Reason()) with
                {
                    SearchThreads = searchCase.Options.SearchThreads,
                    PacerFrameRecoveryWaits = frameWaits,
                    PacerFrameRecoveryWaitMilliseconds = frameWaitMilliseconds,
                };
                ModLog.Info(
                    $"[Sts2Solver] SEARCH_CANCELLED root={root} reason={result.Error} "
                    + $"threads={result.SearchThreads} ms={elapsed.ElapsedMilliseconds} "
                    + $"worker_exited={workerExited} yields={result.PacerYields} "
                    + $"frame_waits={frameWaits} frame_wait_ms={frameWaitMilliseconds} {runtime}");
            }
            else if (worker.IsFaulted)
            {
                Exception? error = worker.Exception?.GetBaseException();
                (string status, string reason) = Classify(error);
                result = SearchRunResult.Failed(root, elapsed.ElapsedMilliseconds,
                    managedPeak, workingSetPeak, allocated, workerExited, pacer?.YieldCount ?? 0,
                    status, reason) with
                {
                    SearchThreads = searchCase.Options.SearchThreads,
                    PacerFrameRecoveryWaits = frameWaits,
                    PacerFrameRecoveryWaitMilliseconds = frameWaitMilliseconds,
                };
                ModLog.Error(
                    $"[Sts2Solver] SEARCH_FAILED root={root} stage=solve kind={result.ErrorKind} "
                    + $"reason={result.Error} ms={elapsed.ElapsedMilliseconds} {runtime}");
            }
            else
            {
                BeamResult beam = worker.GetAwaiter().GetResult();
                if (beam.StopWithoutCandidate)
                {
                    result = SearchRunResult.Cancelled(root, elapsed.ElapsedMilliseconds,
                        beam.ExpandedNodes, beam.Transitions, managedPeak, workingSetPeak, allocated,
                        workerExited, pacer?.YieldCount ?? 0, "stopped") with
                    {
                        SearchThreads = searchCase.Options.SearchThreads,
                        PacerFrameRecoveryWaits = frameWaits,
                        PacerFrameRecoveryWaitMilliseconds = frameWaitMilliseconds,
                    };
                    ModLog.Info(
                        $"[Sts2Solver] SEARCH_STOPPED root={root} reason=no_candidate "
                        + $"ms={elapsed.ElapsedMilliseconds} worker_exited={workerExited} {runtime}");
                }
                else
                {
                    result = SearchRunResult.Ok(root, beam, elapsed.ElapsedMilliseconds,
                        managedPeak, workingSetPeak, allocated, workerExited, pacer?.YieldCount ?? 0,
                        searchCase) with
                    {
                        PacerFrameRecoveryWaits = frameWaits,
                        PacerFrameRecoveryWaitMilliseconds = frameWaitMilliseconds,
                    };
                    if (resultDirectory != null && deterministic)
                        WriteBytes(Path.Combine(resultDirectory, "SRES"), SearchCodec.EncodeResult(beam));
                    ModLog.Info(
                        $"[Sts2Solver] SEARCH_DONE root={root} ms={elapsed.ElapsedMilliseconds} "
                        + $"threads={result.SearchThreads} "
                        + $"expanded={beam.ExpandedNodes} transitions={beam.Transitions} turns={beam.TurnsSearched} "
                        + $"victory={beam.BestIsVictory} score={beam.BestScore} actions={beam.BestRoute.Length} "
                        + $"duplicates={beam.Duplicates} pruned={beam.PrunedIncumbents} "
                        + $"capacity_pruned={beam.CapacityPruned} managed_peak_mb={ToMegabytes(managedPeak):0.0} "
                        + $"working_set_peak_mb={ToMegabytes(workingSetPeak):0.0} "
                        + $"allocated_mb={ToMegabytes(allocated):0.0} yields={pacer?.YieldCount ?? 0} "
                        + $"frame_waits={frameWaits} frame_wait_ms={frameWaitMilliseconds} "
                        + $"workspace_overflow_parents={beam.WorkspaceOverflowParents} "
                        + $"turn_loop_guarded={beam.TurnLoopGuarded} "
                        + $"cross_turn_no_progress_pruned={beam.CrossTurnNoProgressPruned} "
                        + $"workspace_growable_peak_mb={ToMegabytes(beam.SearchWorkspaceGrowablePeakBytes):0.0} "
                        + $"workspace_peak_mb={ToMegabytes(beam.SearchWorkspacePeakBytes):0.0} {runtime}");
                }
            }

            result = result with
            {
                GcPauseMilliseconds = gc.PauseMilliseconds,
                LongFrames = longFrames,
                MaxFrameMilliseconds = maxFrameMilliseconds,
            };
            if (resultDirectory != null)
                WriteJson(Path.Combine(resultDirectory, "search.json"),
                    result.ToMetrics(tier, deterministic));
            return result;
        }
        finally
        {
            if (worker is { IsCompleted: false })
            {
                cancellation.Cancel();
                ModLog.Error($"[Sts2Solver] SEARCH_ABANDONED root={root} reason=driver_exception");
            }
            lock (Gate)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                    _worker = null;
                    _cancelReason = null;
                }
            }
            cancellation.Dispose();
            Volatile.Write(ref _running, 0);
        }
    }

    public static void OnCombatEnded()
    {
        if (IsRunning)
            Cancel("combat_ended");
    }

    public static void OnCombatStarting()
    {
        if (IsRunning)
            Cancel("combat_started");
    }

    public static void Cancel(string reason)
    {
        CancellationTokenSource? cancellation;
        lock (Gate)
        {
            cancellation = _cancellation;
            if (cancellation == null || cancellation.IsCancellationRequested)
                return;
            _cancelReason = reason;
        }
        ModLog.Info($"[Sts2Solver] SEARCH_CANCEL_REQUESTED root={_root} reason={reason}");
        cancellation.Cancel();
    }

    public static async Task<bool> CancelAndWaitAsync(NGame host, string reason, int timeoutMilliseconds)
    {
        Cancel(reason);
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (IsRunning)
        {
            if (Environment.TickCount64 >= deadline)
            {
                ModLog.Error(
                    $"[Sts2Solver] SEARCH_CANCEL_TIMEOUT root={_root} reason={reason} "
                    + $"running={IsRunning}");
                return false;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        return true;
    }

    private static string? Reason()
    {
        lock (Gate)
            return _cancelReason;
    }

    private static (string Status, string Reason) Classify(Exception? error)
    {
        if (error is UnsupportedImportException import)
            return ("unsupported", $"reason={import.Reason} {import.Detail}");
        if (error is UnsupportedException unsupported)
            return ("unsupported", unsupported.Message);
        if (error is CapacityException capacity)
            return ("capacity", $"region={capacity.Region} {capacity.Message}");
        if (error is NotSupportedException notSupported)
            return ("unsupported", notSupported.Message);
        if (error is InvalidDataException invalidData)
            return ("invalid_data", invalidData.Message);
        if (error is InvalidOperationException invalidOperation)
            return ("invalid_operation", invalidOperation.Message);
        if (error is OperationCanceledException canceled)
            return ("cancelled", canceled.Message);
        return (error?.GetType().FullName ?? "unknown", error?.Message ?? "搜索任务没有异常对象");
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

    private static double ToMegabytes(long bytes) => bytes / (1024d * 1024d);

    private static void WriteBytes(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }

    private static void WriteJson<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, ResultJson));
        File.Move(temporary, path, true);
    }
}

internal sealed record SearchRunResult(
    string Root,
    string Status,
    string Stage,
    string? Error,
    string ErrorKind,
    long ElapsedMilliseconds,
    int ExpandedNodes,
    int Transitions,
    int Duplicates,
    int TurnsSearched,
    bool Victory,
    double Score,
    int RouteActionCount,
    string RouteSummary,
    int CapacityPruned,
    int PrunedIncumbents,
    long ManagedHeapPeakBytes,
    long WorkingSetPeakBytes,
    long AllocatedBytes,
    bool WorkerExited,
    int PacerYields,
    SearchCase? Case = null,
    BeamResult? Beam = null)
{
    public int SearchThreads { get; init; } = 1;
    public int WorkspaceOverflowParents { get; init; }
    public long SearchWorkspaceGrowablePeakBytes { get; init; }
    public long SearchWorkspacePeakBytes { get; init; }
    public int PacerFrameRecoveryWaits { get; init; }
    public long PacerFrameRecoveryWaitMilliseconds { get; init; }

    /// <summary>Process-wide GC pause time while the member ran (other mods included).</summary>
    public long GcPauseMilliseconds { get; init; }

    /// <summary>Game frames at or above the frame-pressure gap while the member ran.</summary>
    public int LongFrames { get; init; }

    public double MaxFrameMilliseconds { get; init; }

    /// <summary>How this member ended; adoption and stop are never reported as a plain completion.</summary>
    public SearchCompletionKind Completion { get; init; } = SearchCompletionKind.Completed;

    /// <summary>The adopted candidate's provenance, when <see cref="Completion"/> is an adoption.</summary>
    public SearchAdoptionInfo? Adoption { get; init; }

    public static SearchRunResult Busy(string root) => new(
        root, "failed", "busy", "已有搜索在运行", "busy", 0, 0, 0, 0, 0,
        false, 0, 0, "", 0, 0, 0, 0, 0, false, 0)
    {
        Completion = SearchCompletionKind.Failed,
    };

    public static SearchRunResult Cancelled(
        string root, long elapsed, int expanded, int transitions, long managedPeak,
        long workingSetPeak, long allocated, bool workerExited, int yields, string? reason)
        => new(root, "cancelled", "solve", reason ?? "canceled", "cancelled", elapsed,
            expanded, transitions, 0, 0, false, 0, 0, "", 0, 0, managedPeak, workingSetPeak,
            allocated, workerExited, yields)
        {
            Completion = SearchCompletionKind.Cancelled,
        };

    public static SearchRunResult Failed(
        string root, long elapsed, long managedPeak, long workingSetPeak, long allocated,
        bool workerExited, int yields, string kind, string reason)
        => new(root, "failed", "solve", reason, kind, elapsed,
            0, 0, 0, 0, false, 0, 0, "", 0, 0, managedPeak, workingSetPeak, allocated,
            workerExited, yields)
        {
            Completion = SearchCompletionKind.Failed,
        };

    public static SearchRunResult Ok(
        string root, BeamResult result, long elapsed, long managedPeak, long workingSetPeak,
        long allocated, bool workerExited, int yields, SearchCase searchCase)
        => new(root, "ok", "solve", null, "", elapsed, result.ExpandedNodes,
            result.Transitions, result.Duplicates, result.TurnsSearched, result.BestIsVictory,
            result.BestScore, result.BestRoute.Length, Summarize(result.BestRoute),
            result.CapacityPruned, result.PrunedIncumbents, managedPeak, workingSetPeak,
            allocated, workerExited, yields, searchCase, result)
        {
            SearchThreads = searchCase.Options.SearchThreads,
            WorkspaceOverflowParents = result.WorkspaceOverflowParents,
            SearchWorkspaceGrowablePeakBytes = result.SearchWorkspaceGrowablePeakBytes,
            SearchWorkspacePeakBytes = result.SearchWorkspacePeakBytes,
            Completion = result.Adoption is { Kind: SearchAdoptionKind.Stopped }
                ? SearchCompletionKind.StoppedWithCandidate
                : result.Adoption != null ? SearchCompletionKind.Adopted
                : SearchCompletionKind.Completed,
            Adoption = result.Adoption,
        };

    public SearchMetrics ToMetrics(SearchPerformanceTier tier, bool deterministic) => new(
        1, Root, Status, Stage, Error, ErrorKind, tier.ToString(), deterministic,
        ElapsedMilliseconds, ExpandedNodes, Transitions, Duplicates, TurnsSearched, Victory,
        Score, RouteActionCount, CapacityPruned, PrunedIncumbents, ManagedHeapPeakBytes,
        WorkingSetPeakBytes, AllocatedBytes, WorkerExited, PacerYields,
        PacerFrameRecoveryWaits, PacerFrameRecoveryWaitMilliseconds, SearchThreads,
        WorkspaceOverflowParents, SearchWorkspaceGrowablePeakBytes, SearchWorkspacePeakBytes);

    private static string Summarize(PlayerAction[] route)
    {
        const int limit = 32;
        string summary = string.Join(", ", route.Take(limit).Select(static action => action.ToString()));
        return route.Length > limit ? summary + $", ... ({route.Length} total)" : summary;
    }
}

internal sealed record SearchMetrics(
    int SchemaVersion,
    string Root,
    string Status,
    string Stage,
    string? Error,
    string ErrorKind,
    string Tier,
    bool Deterministic,
    long ElapsedMs,
    int ExpandedNodes,
    int Transitions,
    int Duplicates,
    int TurnsSearched,
    bool Victory,
    double Score,
    int RouteActions,
    int CapacityPruned,
    int PrunedIncumbents,
    long ManagedHeapPeakBytes,
    long WorkingSetPeakBytes,
    long AllocatedBytes,
    bool WorkerExited,
    int PacerYields,
    int PacerFrameRecoveryWaits,
    long PacerFrameRecoveryWaitMilliseconds,
    int SearchThreads,
    int WorkspaceOverflowParents,
    long SearchWorkspaceGrowablePeakBytes,
    long SearchWorkspacePeakBytes);

/// <summary>
/// Process-wide GC activity for the search log: total pause time and collection counts, plus the
/// memory load (percent of the memory available to the GC) as of the last collection.
/// </summary>
internal readonly record struct GcActivity(long PauseMilliseconds, int Gen0, int Gen1, int Gen2,
    int MemoryLoadPercent)
{
    internal static GcActivity Capture()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        int load = info.TotalAvailableMemoryBytes > 0
            ? (int)(info.MemoryLoadBytes * 100 / info.TotalAvailableMemoryBytes)
            : 0;
        return new((long)GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(0),
            GC.CollectionCount(1), GC.CollectionCount(2), load);
    }

    /// <summary>The pauses and collections since <paramref name="before"/>; the load stays current.</summary>
    internal GcActivity Since(GcActivity before) => this with
    {
        PauseMilliseconds = PauseMilliseconds - before.PauseMilliseconds,
        Gen0 = Gen0 - before.Gen0,
        Gen1 = Gen1 - before.Gen1,
        Gen2 = Gen2 - before.Gen2,
    };
}
