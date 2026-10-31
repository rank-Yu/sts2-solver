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

    internal static bool IsRunning => Volatile.Read(ref _running) != 0;

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
#if STS2_SOLVER_TESTING
        if (TestSession.Active)
        {
            ModLog.Info("[Sts2Solver] AUTO_REJECTED reason=test_session_active");
            return;
        }
#endif
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        if (combat == null || !CombatManager.Instance.IsInProgress)
        {
            ModLog.Info("[Sts2Solver] AUTO_REJECTED reason=no_active_combat");
            return;
        }
        Player? player = LocalContext.GetMe(combat);
        if (player?.PlayerCombatState is null
            || combat.CurrentSide != CombatSide.Player
            || player.PlayerCombatState.Phase != PlayerTurnPhase.Play)
        {
            ModLog.Info("[Sts2Solver] AUTO_REJECTED reason=not_playable");
            return;
        }
        _ = RunDetachedAsync(host, combat);
    }

    private static async Task RunDetachedAsync(NGame host, CombatState combat)
    {
        try
        {
            CombatPilotResult result = await RunAsync(host, combat, configurePolicy: null, root: "manual",
                SearchPerformanceTier.Medium, CancellationToken.None);
            ModLog.Info(
                $"[Sts2Solver] AUTO_DONE root=manual status={result.Status} outcome={result.Outcome} " +
                $"turns={result.Turns} hp={result.PlayerHp}/{result.PlayerMaxHp} " +
                $"searches={result.Searches} reused={result.ReusedTurns} " +
                $"deviations={result.Deviations} failures={result.DeploymentFailures} " +
                $"elapsed_ms={result.ElapsedMs}");
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
    }

    internal static async Task<CombatPilotResult> RunAsync(NGame host, CombatState combat,
        Func<SearchPolicy, Player, SearchPolicy>? configurePolicy, string root, SearchPerformanceTier tier, CancellationToken external,
        int? cancelAfterMilliseconds = null)
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
            ModLog.Info(
                $"[Sts2Solver] AUTO_START root={root} turn={lastTurn} hp={lastHp}/{lastMaxHp} " +
                $"enemies={tracker.RootEnemyCount} tier={tier}");
            CombatPilotHost combatHost = new(host, rules, tracker, configurePolicy, tier, root, combat, player);
            using Timer? cancelTimer = cancelAfterMilliseconds is { } cancelLimit
                ? new Timer(static _ => Cancel("test_cancel_after_ms"), null, cancelLimit,
                    Timeout.Infinite)
                : null;
            CombatPilotResult result = await CombatPilot.RunAsync(
                combatHost, root, cancellation.Token);
            total.Stop();
            result = result with { ElapsedMs = total.ElapsedMilliseconds };
            ModLog.Info(
                $"[Sts2Solver] AUTO_END root={root} status={result.Status} outcome={result.Outcome} " +
                $"turn={result.Turns} hp={result.PlayerHp}/{result.PlayerMaxHp} " +
                $"searches={result.Searches} reused={result.ReusedTurns} " +
                $"deviations={result.Deviations} deployment_failures={result.DeploymentFailures} " +
                $"elapsed_ms={result.ElapsedMs} reason={Reason() ?? result.PauseReason ?? "-"}");
            return result;
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

    internal static void OnCombatEnded()
    {
        // The loop observes IsInProgress itself; cancelling a running search is SearchSession's job.
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

    private static void LogFailure(string detail)
        => ModLog.Error($"[Sts2Solver] AUTO_FAILED root=manual {detail}");

    private static string? Reason()
    {
        lock (Gate)
            return _cancelReason;
    }
}
