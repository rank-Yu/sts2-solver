// Whole-combat automation: the shared decision loop lives in Core (CombatPilot); this file owns
// the Mod session lifecycle, native setup and cancellation. The turn loop and failure policy follow
// CombatSolver src/Runtime/SolverController.cs at a62f1850 (MIT, Copyright (c) 2026 Torch1230 and
// contributors), reduced to one search and one deployment at a time.
using System.Diagnostics;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Autoplay;
using Sts2Solver.Ai.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver;

internal enum AutoCombatStartSource
{
    Manual,
    Auto,
    Command,
}

/// <summary>
/// The single full-auto session. Ordinary F8 toggles it; the test protocol awaits it. All game
/// reads/writes happen on the main thread; the only background work is <see cref="SearchSession"/>'s
/// solver worker.
/// </summary>
internal static class AutoCombatSession
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _cancellation;
    private static string? _cancelReason;
    private static int _running;
    private static int _autoStartLatched;
    private static int _lastStartSource = -1;
    private static Task<CombatPilotResult?>? _lastRun;

    internal static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>False after a session has started or stopped in this combat; reset at combat start.</summary>
    internal static bool CanAutoStart => Volatile.Read(ref _autoStartLatched) == 0;

    /// <summary>The last detached run, kept for the headless auto-start scenario.</summary>
    internal static Task<CombatPilotResult?>? LastRunForTesting => Volatile.Read(ref _lastRun);

    internal static void ClearLastRunForTesting()
    {
        Volatile.Write(ref _lastRun, null);
        Interlocked.Exchange(ref _lastStartSource, -1);
        Interlocked.Exchange(ref _autoStartLatched, 0);
    }

    internal static AutoCombatStartSource? LastStartSourceForTesting
    {
        get
        {
            int source = Volatile.Read(ref _lastStartSource);
            return source < 0 ? null : (AutoCombatStartSource)source;
        }
    }

    /// <summary>F8 entry: start full auto when idle, stop it when running.</summary>
    internal static void Toggle(NGame host)
    {
        if (!NGame.IsMainThread())
        {
            ModLog.Error("[Sts2Solver] AUTO_REJECTED reason=not_main_thread");
            return;
        }
        if (IsRunning)
        {
            Cancel("hotkey");
            ModLog.Info("[Sts2Solver] AUTO_STOP_REQUESTED reason=hotkey");
            return;
        }
        StartAuto(host, AutoCombatStartSource.Manual);
    }

    /// <summary>Shared entry for F8, the settings-driven starter and the UI command surface.</summary>
    internal static bool StartAuto(NGame host, AutoCombatStartSource source)
    {
        if (!NGame.IsMainThread())
        {
            ModLog.Error("[Sts2Solver] AUTO_REJECTED reason=not_main_thread");
            return false;
        }
        if (IsRunning)
            return false;
        if (source == AutoCombatStartSource.Auto && !CanAutoStart)
        {
            ModLog.Info("[Sts2Solver] AUTO_REJECTED reason=already_started_this_combat");
            return false;
        }
#if STS2_SOLVER_TESTING
        if (TestSession.Active && !TestSession.AllowAutoStartForTest)
        {
            ModLog.Info("[Sts2Solver] AUTO_REJECTED reason=test_session_active");
            return false;
        }
#endif
        if (!IsPlayableForAutoStart(host, out CombatState? combat))
        {
            ModLog.Info("[Sts2Solver] AUTO_REJECTED reason=not_playable");
            return false;
        }
        Interlocked.Exchange(ref _autoStartLatched, 1);
        Interlocked.Exchange(ref _lastStartSource, (int)source);
        Task<CombatPilotResult?> run = RunDetachedAsync(host, combat!);
        Volatile.Write(ref _lastRun, run);
        return true;
    }

    internal static bool IsPlayableForAutoStart(NGame host, out CombatState? combat)
    {
        _ = host;
        combat = CombatManager.Instance.DebugOnlyGetState();
        if (combat == null || !CombatManager.Instance.IsInProgress || combat.Players.Count != 1)
            return false;
        Player? player = LocalContext.GetMe(combat);
        return player?.PlayerCombatState is { Phase: PlayerTurnPhase.Play }
            && combat.CurrentSide == CombatSide.Player;
    }

    internal static SearchPerformanceTier ResolveSearchTier(SearchPerformanceTier? tierOverride)
        => tierOverride ?? SolverSettings.Current.Search.Tier;

    private static async Task<CombatPilotResult?> RunDetachedAsync(NGame host, CombatState combat)
    {
        try
        {
            CombatPilotResult result = await RunAsync(host, combat, configurePolicy: null, root: "manual",
                tierOverride: null, CancellationToken.None);
            ModLog.Info(
                $"[Sts2Solver] AUTO_DONE root=manual status={result.Status} outcome={result.Outcome} " +
                $"turns={result.Turns} hp={result.PlayerHp}/{result.PlayerMaxHp} " +
                $"searches={result.Searches} reused={result.ReusedTurns} " +
                $"deviations={result.Deviations} failures={result.DeploymentFailures} " +
                $"elapsed_ms={result.ElapsedMs}");
            return result;
        }
        catch (UnsupportedImportException error) { LogFailure($"reason={error.Reason} {error.Detail}"); }
        catch (UnsupportedException error) { LogFailure($"unsupported {error.Message}"); }
        catch (CapacityException error) { LogFailure($"capacity region={error.Region} {error.Message}"); }
        catch (NotSupportedException error) { LogFailure($"unsupported {error.Message}"); }
        catch (InvalidDataException error) { LogFailure($"invalid_data {error.Message}"); }
        catch (InvalidOperationException error) { LogFailure($"invalid_operation {error.Message}"); }
        catch (TimeoutException error) { LogFailure($"timeout {error.Message}"); }
        catch (OperationCanceledException)
        {
            ModLog.Info($"[Sts2Solver] AUTO_CANCELLED root=manual reason={Reason() ?? "cancel"}");
        }
        return null;
    }

    internal static async Task<CombatPilotResult> RunAsync(NGame host, CombatState combat,
        Func<SearchPolicy, Player, SearchPolicy>? configurePolicy, string root, SearchPerformanceTier? tierOverride,
        CancellationToken external, int? cancelAfterMilliseconds = null)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("全自动必须从游戏主线程启动。");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("已有全自动会话在运行。");
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
        lock (Gate)
        {
            _cancellation = cancellation;
            _cancelReason = null;
        }
        Stopwatch total = Stopwatch.StartNew();
        try
        {
            Player player = LocalContext.GetMe(combat)
                ?? throw new InvalidOperationException("找不到本地玩家。");
            if (combat.RunState is not RunState runState)
                throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
            Rules rules = RulesCompiler.Compile(combat, player, runState);
            LiveCombatTracker tracker = new(combat);
            Battle initial = tracker.ImportRoot(combat, player, rules);
            tracker.Observe(initial);
            int lastTurn = player.PlayerCombatState?.TurnNumber ?? 0;
            int lastHp = player.Creature.CurrentHp;
            int lastMaxHp = player.Creature.MaxHp;
            SearchPerformanceTier startupTier = ResolveSearchTier(tierOverride);
            CombatUiStore.Reset(CombatUiProjection.SessionStarted(root, lastTurn, lastHp, lastMaxHp,
                SearchMemoryBudget.Resolve()));
            ModLog.Info(
                $"[Sts2Solver] AUTO_START root={root} turn={lastTurn} hp={lastHp}/{lastMaxHp} " +
                $"enemies={tracker.RootEnemyCount} tier={startupTier}");
            CombatPilotHost combatHost = new(host, rules, tracker, configurePolicy, tierOverride, root,
                combat, player);
            using Timer? cancelTimer = cancelAfterMilliseconds is { } cancelLimit
                ? new Timer(static _ => Cancel("test_cancel_after_ms"), null, cancelLimit,
                    Timeout.Infinite)
                : null;
            CombatPilotResult result = await CombatPilot.RunAsync(
                combatHost, root, cancellation.Token);
            total.Stop();
            result = result with { ElapsedMs = total.ElapsedMilliseconds };
            PublishResult(result);
            ModLog.Info(
                $"[Sts2Solver] AUTO_END root={root} status={result.Status} outcome={result.Outcome} " +
                $"turn={result.Turns} hp={result.PlayerHp}/{result.PlayerMaxHp} " +
                $"searches={result.Searches} reused={result.ReusedTurns} " +
                $"deviations={result.Deviations} deployment_failures={result.DeploymentFailures} " +
                $"elapsed_ms={result.ElapsedMs} reason={Reason() ?? result.PauseReason ?? "-"}");
            return result;
        }
        catch (UnsupportedImportException)
        {
            PublishFailure("unsupported_import");
            throw;
        }
        catch (UnsupportedException)
        {
            PublishFailure("unsupported");
            throw;
        }
        catch (CapacityException)
        {
            PublishFailure("capacity");
            throw;
        }
        catch (NotSupportedException)
        {
            PublishFailure("not_supported");
            throw;
        }
        catch (InvalidDataException)
        {
            PublishFailure("invalid_data");
            throw;
        }
        catch (InvalidOperationException)
        {
            PublishFailure("invalid_operation");
            throw;
        }
        catch (TimeoutException)
        {
            PublishFailure("timeout");
            throw;
        }
        catch (OperationCanceledException)
        {
            PublishCancelled();
            throw;
        }
        finally
        {
            total.Stop();
            lock (Gate)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                    _cancelReason = null;
                }
            }
            cancellation.Dispose();
            Volatile.Write(ref _running, 0);
        }
    }

    internal static void OnCombatStarting()
    {
        Interlocked.Exchange(ref _autoStartLatched, 0);
        Interlocked.Exchange(ref _lastStartSource, -1);
        Volatile.Write(ref _lastRun, null);
        if (CombatUiStore.Initialized && NGame.IsMainThread())
            CombatUiStore.Reset(CombatUiProjection.Idle(SearchMemoryBudget.Resolve()));
    }

    internal static void OnCombatEnded()
    {
        // The loop observes IsInProgress itself; cancelling a running search is SearchSession's job.
        Interlocked.Exchange(ref _autoStartLatched, 0);
        lock (Gate)
            _cancelReason ??= "combat_ended";
    }

    internal static void Cancel(string reason)
    {
        CancellationTokenSource? cancellation;
        lock (Gate)
        {
            cancellation = _cancellation;
            _cancelReason ??= reason;
        }
        if (cancellation != null && !cancellation.IsCancellationRequested)
        {
            ModLog.Info($"[Sts2Solver] AUTO_CANCEL_REQUESTED reason={reason}");
            cancellation.Cancel();
            SearchSession.Cancel(reason);
        }
        if (CombatUiStore.Initialized && NGame.IsMainThread())
        {
            CombatUiStore.Update(snapshot => snapshot with
            {
                PauseReason = reason,
            });
        }
    }

    internal static async Task<bool> CancelAndWaitAsync(NGame host, string reason, int timeoutMilliseconds)
    {
        Cancel(reason);
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (IsRunning)
        {
            if (Environment.TickCount64 >= deadline)
            {
                ModLog.Error($"[Sts2Solver] AUTO_CANCEL_TIMEOUT reason={reason}");
                return false;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        return true;
    }

    private static void PublishResult(CombatPilotResult result)
    {
        CombatUiPhase phase = result.Status switch
        {
            "victory" or "defeat" or "ended" => CombatUiPhase.Completed,
            "paused" => CombatUiPhase.Paused,
            "cancelled" => CombatUiPhase.Cancelled,
            _ => CombatUiPhase.Failed,
        };
        SearchMemoryBudgetResolution budget = SearchMemoryBudget.Resolve();
        CombatUiStore.Update(snapshot => snapshot with
        {
            Phase = phase,
            Root = result.Root,
            Turn = result.Turns,
            PlayerHp = result.PlayerHp,
            PlayerMaxHp = result.PlayerMaxHp,
            EndReason = result.Status,
            PauseReason = result.PauseReason,
            PauseCode = PauseCodeFor(result),
            ErrorCode = phase == CombatUiPhase.Failed ? "pilot_failed" : null,
            ErrorDetail = null,
            Searches = result.Searches,
            ReusedTurns = result.ReusedTurns,
            Deviations = result.Deviations,
            DeploymentFailures = result.DeploymentFailures,
            Memory = CombatUiProjection.Memory(budget),
            Settings = CombatUiProjection.Settings(budget),
        });
    }

    /// <summary>
    /// Stable code for the panel: why the pilot paused (from its failure and search logs), or the
    /// cancel reason for a stop. Victory, defeat and failures have no pause code.
    /// </summary>
    private static string? PauseCodeFor(CombatPilotResult result)
    {
        if (result.Status == "cancelled")
            return Reason() ?? "cancel";
        if (result.Status != "paused")
            return null;
        if (result.FailureLog.Length > 0 && result.FailureLog[^1].Reason == "turn_limit")
            return "turn_limit";
        if (result.SearchLog.Length > 0
            && result.SearchLog[^1] is { SearchStatus: CombatSearchStatus.CapacityBounded } last
            && last.Status != "ok")
        {
            return "capacity_bounded";
        }
        return "repeated_failures";
    }

    private static void PublishFailure(string code)
    {
        if (!CombatUiStore.Initialized)
            return;
        CombatUiStore.Update(snapshot => snapshot with
        {
            Phase = CombatUiPhase.Failed,
            EndReason = code,
            PauseCode = null,
            ErrorCode = code,
            ErrorDetail = null,
        });
    }

    private static void PublishCancelled()
    {
        if (!CombatUiStore.Initialized)
            return;
        string reason = Reason() ?? "cancel";
        CombatUiStore.Update(snapshot => snapshot with
        {
            Phase = CombatUiPhase.Cancelled,
            EndReason = "cancelled",
            PauseReason = reason,
            PauseCode = reason,
            ErrorCode = null,
            ErrorDetail = null,
        });
    }

    private static void LogFailure(string detail)
        => ModLog.Error($"[Sts2Solver] AUTO_FAILED root=manual {detail}");

    private static string? Reason()
    {
        lock (Gate)
            return _cancelReason;
    }
}
