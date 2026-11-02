// Visible-page turn-setup takeover. The transaction shape, gate, first-visible-surface start and
// cleanup follow CombatSolver src/Runtime/PlayerTurnSetupPatches.cs at commit 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). Search, request compilation and
// result storage use Sts2Solver's own SearchSession/CombatSearchRunner.
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

/// <summary>
/// Main-thread owner of the visible turn-setup page and its search transaction. The controller owns
/// the search session and UI; this type only observes the native page, starts the setup search once
/// the first visible request exists, and drives the page when asked. A replacement
/// <c>SetupPlayerTurn</c> task stays alive until the original setup task has completed, so a player
/// selection or a stop always returns control to the native flow.
/// </summary>
internal static class PlayerTurnSetupRuntime
{
    private static readonly MethodInfo SetupPlayerTurnMethod = AccessTools.Method(
        typeof(CombatManager),
        "SetupPlayerTurn",
        [typeof(CombatTurnState), typeof(Player), typeof(HookPlayerChoiceContext)])
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "SetupPlayerTurn");

    [ThreadStatic]
    private static bool _invokingOriginalSetup;
    private static TurnSetupTransaction? _active;
    private static int _lastTurn;
    private static bool _lastDroveSelection;
    private static bool _lastDeployAfterSetup;

#if STS2_SOLVER_TESTING
    /// <summary>Test builds default the production takeover off; a request opens it explicitly.</summary>
    internal static bool TestEnabled { get; set; }
#endif

    internal static bool HasActivePage => _active is { PageOpen: true };

    internal static bool IsDriving => _active is { Driving: true };

    internal static int ChoiceCount => _active?.Result?.SetupChoices.Length ?? 0;

    internal static bool SearchStarted => _active is { SearchStarted: true };

    internal static bool IsBusy => _active != null;

    private static bool TakeoverEnabled
    {
        get
        {
#if STS2_SOLVER_TESTING
            // Headless tests default the takeover off and open it per request. A test build
            // started without the test directory behaves like the production build.
            return TestEnabled || !ModEntry.TestMode;
#else
            return !ModEntry.TestMode;
#endif
        }
    }

    internal static bool TryInterceptSetup(CombatManager manager, CombatTurnState turnState,
        Player player, HookPlayerChoiceContext choiceContext, out Task? task)
    {
        task = null;
        if (_invokingOriginalSetup
            || _active != null
            || !ModEntry.Enabled
            || !TakeoverEnabled
            || !CombatSessionController.TurnSetupInterceptionAllowed
            || !ReferenceEquals(LocalContext.GetMe(manager.DebugOnlyGetState()), player)
            || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Start })
        {
            return false;
        }
        if (CardSelectCmd.Selector != null || CardSelectCmd.LocalSelector != null)
        {
            ModLog.Info("[Sts2Solver] TURN_SETUP_SKIP reason=selector_active");
            return false;
        }
        if (turnState.PlayersTakingExtraTurn.Count > 0)
        {
            ModLog.Info("[Sts2Solver] TURN_SETUP_SKIP reason=extra_turn");
            return false;
        }
        if (!RequiresSolverChoice(player))
            return false;
        if (!CombatSessionController.TryGetTurnSetupContext(out CombatState? combatContext,
                out Rules? rulesContext, out LiveCombatTracker? trackerContext))
        {
            ModLog.Info("[Sts2Solver] TURN_SETUP_SKIP reason=controller_context");
            return false;
        }
        CombatState combat = combatContext!;
        Rules rules = rulesContext!;
        LiveCombatTracker tracker = trackerContext!;
        Battle startRoot;
        try
        {
            startRoot = tracker.ImportSetupRoot(combat, player, rules);
        }
        catch (UnsupportedImportException error)
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SKIP reason=import_failed {error.Reason} {error.Detail}");
            return false;
        }
        catch (UnsupportedException error)
        {
            ModLog.Info($"[Sts2Solver] TURN_SETUP_SKIP reason=import_unsupported {error.Message}");
            return false;
        }
        catch (CapacityException error)
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SKIP reason=import_capacity region={error.Region} {error.Message}");
            return false;
        }
        catch (InvalidDataException error)
        {
            LogSetupInvariantFailure("import", error);
            return false;
        }
        catch (InvalidOperationException error)
        {
            LogSetupInvariantFailure("import", error);
            return false;
        }
        if (startRoot.Phase != PlayerPhase.Start || !Kernel.IsInProgress(startRoot))
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SKIP reason=start_root_not_active " +
                $"phase={startRoot.Phase} outcome={startRoot.Outcome}");
            return false;
        }
        NativeChoiceSession choices = NativeChoiceRuntime.Begin(
            combat, player, $"turn_setup:{player.PlayerCombatState!.TurnNumber}");
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(CombatSessionController.CombatToken);
        TurnSetupTransaction transaction = new(
            combat, player, rules, tracker, turnState, choiceContext, startRoot, choices,
            cancellation, player.PlayerCombatState.TurnNumber);
        _active = transaction;
        _lastTurn = transaction.Turn;
        _lastDroveSelection = false;
        _lastDeployAfterSetup = false;
        task = RunTurnSetupAsync(transaction);
        return true;
    }

    /// <summary>
    /// Starts driving the visible page from the current plan. The controller has already cleared
    /// any user-stop flag when this is called for an explicit "execute current turn".
    /// </summary>
    internal static bool TryStartDriving(bool deployAfterSetup)
    {
        if (_active is not { PageOpen: true, PlanReady: true, Driving: false, Result: not null } t)
            return false;
        NGame? host = NGame.Instance;
        if (host == null)
            return false;
        t.DeployAfterSetup |= deployAfterSetup;
        t.Driving = true;
        t.DroveSelection = true;
        CombatSessionController.OnTurnSetupDriveStarted();
        try
        {
            t.Choices.SetPlanAndStartDriving(host, t.Result.SetupChoices, t.Cancellation.Token);
        }
        catch (InvalidOperationException error)
        {
            t.Driving = false;
            HandleDriveFailure(t, error);
            return false;
        }
        ModLog.Info(
            $"[Sts2Solver] TURN_SETUP_TAKEOVER turn={t.Turn} " +
            $"mode={(deployAfterSetup ? "execute" : "route_only")} choices={t.Result.SetupChoices.Length}");
        return true;
    }

    internal static bool StopDriving()
    {
        if (_active is not { Driving: true } t)
            return false;
        t.Driving = false;
        t.Choices.StopDriving();
        CombatSessionController.OnTurnSetupDriveStopped();
        ModLog.Info($"[Sts2Solver] TURN_SETUP_DRIVE_STOP turn={t.Turn}");
        return true;
    }

    internal static bool TryRecalculate()
    {
        if (_active is not { PageOpen: true, Driving: false, SearchStarted: true, Case: not null } t)
            return false;
        t.Result = null;
        t.PlanReady = false;
        t.Stopped = false;
        t.DeployAfterSetup = false;
        return CombatSessionController.StartTurnSetupSearch(t.Case, t.StartRoot, t.Turn);
    }

    internal static void OnPlanReady(CombatControllerResult result, bool stopped)
    {
        if (_active is not { } t || t.Completed)
            return;
        t.Result = result;
        t.PlanReady = true;
        t.Stopped = stopped;
        t.Choices.RecordPlanReady();
        ModLog.Info(
            $"[Sts2Solver] TURN_SETUP_PLAN turn={t.Turn} choices={result.SetupChoices.Length} " +
            $"stopped={stopped} page={t.PageOpen}");
        if (stopped)
        {
            t.Choices.ReleaseVisibleSurface();
            return;
        }
        if (t.Driving)
            return;
        if (CombatSessionController.FullAutoEnabled
            && !CombatSessionController.AutomaticSearchPaused)
        {
            if (CombatSessionController.TryPauseTurnSetupFullAuto(result, out string pauseCode,
                    out string pauseReason))
            {
                t.Choices.ReleaseVisibleSurface();
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_FULL_AUTO_PAUSE turn={t.Turn} code={pauseCode} " +
                    $"{pauseReason}");
                return;
            }
            TryStartDriving(deployAfterSetup: true);
        }
    }

    internal static void OnSearchStopped(CombatControllerResult? result)
    {
        if (_active is not { } t || t.Completed)
            return;
        t.Stopped = true;
        t.Driving = false;
        t.Choices.ReleaseVisibleSurface();
        if (result == null)
        {
            t.Result = null;
            t.PlanReady = false;
            t.DeployAfterSetup = false;
            ModLog.Info($"[Sts2Solver] TURN_SETUP_STOP_DROP turn={t.Turn}");
            return;
        }
        t.Result = result;
        t.PlanReady = true;
        ModLog.Info(
            $"[Sts2Solver] TURN_SETUP_STOP_KEEP turn={t.Turn} choices={result.SetupChoices.Length}");
    }

    internal static void OnSearchFailed()
    {
        if (_active is not { } t || t.Completed)
            return;
        t.Driving = false;
        t.Result = null;
        t.PlanReady = false;
        t.DeployAfterSetup = false;
        t.Choices.ReleaseVisibleSurface();
    }

    private static void OnPlayerAdvanced(TurnSetupTransaction t)
    {
        if (!ReferenceEquals(_active, t) || t.Completed || t.PlayerAdvanced)
            return;
        t.PlayerAdvanced = true;
        t.Result = null;
        t.PlanReady = false;
        t.DeployAfterSetup = false;
        t.Driving = false;
        t.Choices.StopDriving();
        t.Choices.ReleaseVisibleSurface();
        CombatSessionController.OnTurnSetupPlayerAdvanced(t.Turn);
    }

    internal static void OnFullAutoResumed(bool skipPause = false)
    {
        if (_active is not { PageOpen: true, PlanReady: true, Driving: false, Stopped: false,
                Result: not null } t)
        {
            return;
        }
        if (!skipPause
            && CombatSessionController.TryPauseTurnSetupFullAuto(t.Result, out string pauseCode,
                out string pauseReason))
        {
            t.Choices.ReleaseVisibleSurface();
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_FULL_AUTO_PAUSE turn={t.Turn} code={pauseCode} " +
                $"{pauseReason}");
            return;
        }
        TryStartDriving(deployAfterSetup: true);
    }

    internal static bool LastTurnSetupDroveSelection(int turn)
        => _lastTurn == turn && _lastDroveSelection;

    internal static bool LastTurnSetupDeployAfterSetup(int turn)
        => _lastTurn == turn && _lastDeployAfterSetup;

    internal static void ConsumeLastTurnSetup()
    {
        _lastDroveSelection = false;
        _lastDeployAfterSetup = false;
    }

#if STS2_SOLVER_TESTING
    internal static bool TestPageVisible => _active is { PageOpen: true };

    internal static bool TestPlanReady => _active is { PlanReady: true };

    internal static bool TestDriving => _active is { Driving: true };

    internal static bool TestBlockerActive => _active?.Choices.BlockerActive == true;

    internal static int TestBlockerAcquisitionCount
        => _active?.Choices.BlockerAcquisitionCount ?? 0;

    internal static bool TestDroveSelection
        => _active?.DroveSelection ?? _lastDroveSelection;

    internal static int TestChoiceCount => _active?.Result?.SetupChoices.Length ?? 0;

    internal static int TestFirstVisibleOptionCount
        => _active?.Choices.FirstVisibleRequest?.LiveOptions.Count ?? 0;

    internal static bool TestFirstVisibleCanSkip
        => _active?.Choices.FirstVisibleRequest?.CanSkip ?? false;

    internal static async Task SelectFirstVisibleForTesting(NGame host, int count,
        CancellationToken token)
    {
        NativeChoiceSession choices = _active?.Choices
            ?? throw new InvalidOperationException("没有活动的回合准备选牌会话。");
        NativeChoiceRequest request = choices.FirstVisibleRequest
            ?? throw new InvalidOperationException("回合准备页面还没有出现。");
        IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> selected = request.Options
            .Take(Math.Min(count, request.MaxSelect))
            .ToArray();
        await choices.SelectVisibleCardsForTesting(host, selected, token);
    }

    internal static Task SelectPlannedOrDifferentForTesting(NGame host, bool different,
        CancellationToken token)
    {
        if (_active is not { } t || t.Result == null || t.Result.SetupChoices.Length == 0)
            throw new InvalidOperationException("回合准备计划还没有就绪。");
        return t.Choices.SelectPlannedOrDifferentVisibleCardsForTesting(
            host, t.Result.SetupChoices[0], different, token);
    }
#endif

    internal static void PrepareForSceneExit()
    {
        NativeChoiceRuntime.CancelActiveHandSelectionForSceneExit();
        Reset("scene_exit");
    }

    internal static void Reset(string reason)
    {
        TurnSetupTransaction? active = _active;
        _active = null;
        _lastDroveSelection = false;
        _lastDeployAfterSetup = false;
        if (active == null)
            return;
        active.Completed = true;
        active.Cancellation.Cancel();
        active.Choices.Dispose();
        active.PageOpen = false;
        active.PlanReady = false;
        active.Driving = false;
        ModLog.Info($"[Sts2Solver] TURN_SETUP_RESET reason={reason} turn={active.Turn}");
        CombatSessionController.CancelTurnSetupSearch(reason);
    }

    private static async Task RunTurnSetupAsync(TurnSetupTransaction t)
    {
        NGame? host = NGame.Instance;
        if (host == null)
        {
            Reset("host_missing");
            return;
        }
        Task originalSetup = InvokeOriginalSetupAsync(t);
        Task<TurnSetupEnumeration> enumerationTask = Task.Run(
            () => TurnSetupRootEnumerator.Enumerate(t.StartRoot, t.Rules), t.Cancellation.Token);
        try
        {
            bool visible = await t.Choices.WaitForFirstVisibleSurfaceAsync(
                host, originalSetup, t.Cancellation.Token);
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_FIRST_VISIBLE turn={t.Turn} visible={visible} " +
                $"original_done={originalSetup.IsCompleted} enum_done={enumerationTask.IsCompleted}");
            if (!visible)
            {
                _ = await AwaitEnumerationAsync(t, enumerationTask, "no_visible_surface");
                return;
            }
            t.PageOpen = true;
            CombatSessionController.NotifyTurnSetupChanged();
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_PAGE turn={t.Turn} sequence={t.Choices.FirstVisibleSequence}");
            while (!enumerationTask.IsCompleted && !originalSetup.IsCompleted)
            {
                ObservePlayerChoice(t, originalSetup);
                if (t.PageOpen && t.Choices.DriverFaulted)
                    HandleDriveFailure(t, t.Choices.DriverTask?.Exception?.GetBaseException());
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            if (originalSetup.IsCompleted)
            {
                _ = await AwaitEnumerationAsync(t, enumerationTask, "original_completed");
                return;
            }
            (EnumerationWait wait, TurnSetupEnumeration? enumerationValue) =
                await AwaitEnumerationAsync(t, enumerationTask, "visible_choice");
            if (wait != EnumerationWait.Ok || enumerationValue == null)
                return;
            TurnSetupEnumeration enumeration = enumerationValue;
            LogEnumeration(t, enumeration);
            if (enumeration.Status is TurnSetupEnumerationStatus.NoChoice
                or TurnSetupEnumerationStatus.Unsupported
                or TurnSetupEnumerationStatus.Terminal)
            {
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_SKIP reason=enumeration_" +
                    $"{enumeration.Status.ToString().ToLowerInvariant()} turn={t.Turn} " +
                    $"detail={enumeration.Reason ?? "-"}");
                return;
            }
            if (enumeration.Branches.Any(static branch => branch.Choices.Any(
                    static choice => choice.Request.Timing != ChoiceTiming.PlayerTurnStart)))
            {
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_SKIP reason=autopreplay_choice turn={t.Turn} " +
                    "detail=plan_contains_auto_preplay_stage");
                return;
            }
            TurnSetupBranch[] branches = [.. enumeration.Branches.Where(static branch => !branch.Terminal)];
            if (branches.Length == 0)
            {
                ModLog.Info($"[Sts2Solver] TURN_SETUP_SKIP reason=all_terminal turn={t.Turn}");
                return;
            }
            if (!CombatSessionController.TryCompileTurnSetupCase(t.Combat, t.Player, t.Rules,
                    t.StartRoot, branches, out SearchCase? setupCase, out string compileDetail,
                    out bool fatalCompile))
            {
                if (fatalCompile)
                {
                    ModLog.Warn(
                        $"[Sts2Solver] TURN_SETUP_INVARIANT_FAILED stage=compile turn={t.Turn} " +
                        $"{compileDetail}");
                    CombatSessionController.FailTurnSetup($"compile {compileDetail}");
                }
                else
                {
                    ModLog.Info(
                        $"[Sts2Solver] TURN_SETUP_SKIP reason=compile_boundary turn={t.Turn} " +
                        $"detail={compileDetail}");
                }
                return;
            }
            t.Case = setupCase!;
            t.SearchStarted = true;
            t.Choices.RecordSearchStarted();
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SEARCH_START turn={t.Turn} roots={branches.Length} " +
                $"dropped={enumeration.DroppedByBudget} after_native_choice_visible=true");
            if (!CombatSessionController.StartTurnSetupSearch(setupCase!, t.StartRoot, t.Turn))
            {
                ModLog.Info($"[Sts2Solver] TURN_SETUP_SEARCH_SKIP turn={t.Turn}");
                return;
            }
            while (!originalSetup.IsCompleted)
            {
                ObservePlayerChoice(t, originalSetup);
                if (t.Driving && t.Choices.DriverFaulted)
                    HandleDriveFailure(t, t.Choices.DriverTask?.Exception?.GetBaseException());
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            ObservePlayerChoice(t, originalSetup);
        }
        catch (OperationCanceledException) when (t.Cancellation.IsCancellationRequested)
        {
            ModLog.Info($"[Sts2Solver] TURN_SETUP_CANCELLED turn={t.Turn}");
        }
        catch (NativeChoiceSurfaceTimeoutException error)
        {
            CombatSessionController.FailTurnSetup(error.Message);
        }
        catch (NativeChoiceSurfaceMismatchException error)
        {
            CombatSessionController.FailTurnSetup(error.Message);
        }
        catch (NativeChoicePlanMismatchException error)
        {
            CombatSessionController.FailTurnSetup(error.Message);
        }
        catch (UnsupportedImportException error)
        {
            CombatSessionController.FailTurnSetup($"reason={error.Reason} {error.Detail}");
        }
        catch (UnsupportedException error)
        {
            CombatSessionController.FailTurnSetup(error.Message);
        }
        catch (CapacityException error)
        {
            CombatSessionController.FailTurnSetup($"region={error.Region} {error.Message}");
        }
        catch (InvalidDataException error)
        {
            LogSetupInvariantFailure("runtime", error);
        }
        catch (InvalidOperationException error)
        {
            LogSetupInvariantFailure("runtime", error);
        }
        catch (TimeoutException error)
        {
            CombatSessionController.FailTurnSetup(error.Message);
        }
        finally
        {
            await originalSetup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!t.EnumerationObserved)
                _ = await AwaitEnumerationAsync(t, enumerationTask, "cleanup");
            t.PageOpen = false;
            t.Driving = false;
            if (t.SearchStarted && !t.PlanReady && !t.PlayerAdvanced)
                CombatSessionController.CancelTurnSetupSearch("setup_completed");
            await t.Choices.CompleteAsync();
            t.Choices.Dispose();
            t.Completed = true;
            if (ReferenceEquals(_active, t))
                _active = null;
            _lastTurn = t.Turn;
            _lastDroveSelection = t.DroveSelection;
            _lastDeployAfterSetup = t.DeployAfterSetup;
            CombatSessionController.NotifyTurnSetupChanged();
            CombatSessionController.OnTurnSetupCompleted();
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_DONE turn={t.Turn} plan={t.PlanReady} " +
                $"drove={t.DroveSelection} advanced={t.PlayerAdvanced} deploy={t.DeployAfterSetup}");
        }
        if (originalSetup.IsFaulted)
            await originalSetup;
    }

    private static void ObservePlayerChoice(TurnSetupTransaction t, Task phaseTask)
    {
        if (t.Completed || t.Driving || t.PlayerAdvanced || !t.Choices.HasVisibleRequest)
            return;
        if (!phaseTask.IsCompleted
            && t.Choices.IsVisibleChoicePending
            && t.Choices.LatestVisibleSequence == t.Choices.FirstVisibleSequence)
        {
            return;
        }
        OnPlayerAdvanced(t);
    }

    private static void HandleDriveFailure(TurnSetupTransaction t, Exception? error)
    {
        if (t.Completed || t.PlayerAdvanced)
            return;
        t.Driving = false;
        t.Result = null;
        t.PlanReady = false;
        t.DeployAfterSetup = false;
        t.Choices.ReleaseVisibleSurface();
        string detail = error?.Message ?? "选牌驱动失败。";
        CombatSessionController.FailTurnSetup(detail);
        ModLog.Warn($"[Sts2Solver] TURN_SETUP_DRIVE_FAILED turn={t.Turn} {detail}");
    }

    private enum EnumerationWait
    {
        Ok,
        Cancelled,
        Skipped,
        Failed,
    }

    private static async Task<(EnumerationWait Status, TurnSetupEnumeration? Value)>
        AwaitEnumerationAsync(TurnSetupTransaction t, Task<TurnSetupEnumeration> task, string stage)
    {
        if (t.EnumerationObserved)
            return (EnumerationWait.Cancelled, null);
        t.EnumerationObserved = true;
        try
        {
            return (EnumerationWait.Ok, await task);
        }
        catch (OperationCanceledException) when (t.Cancellation.IsCancellationRequested)
        {
            return (EnumerationWait.Cancelled, null);
        }
        catch (UnsupportedImportException error)
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SKIP reason=enumeration_import stage={stage} " +
                $"turn={t.Turn} detail={error.Reason} {error.Detail}");
            return (EnumerationWait.Skipped, null);
        }
        catch (UnsupportedException error)
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SKIP reason=enumeration_unsupported stage={stage} " +
                $"turn={t.Turn} detail={error.Message}");
            return (EnumerationWait.Skipped, null);
        }
        catch (CapacityException error)
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SKIP reason=enumeration_capacity stage={stage} " +
                $"turn={t.Turn} region={error.Region} detail={error.Message}");
            return (EnumerationWait.Skipped, null);
        }
        catch (InvalidDataException error)
        {
            LogSetupInvariantFailure($"enumeration:{stage}", error);
            return (EnumerationWait.Failed, null);
        }
        catch (InvalidOperationException error)
        {
            LogSetupInvariantFailure($"enumeration:{stage}", error);
            return (EnumerationWait.Failed, null);
        }
    }

    private static void LogSetupInvariantFailure(string stage, Exception error)
    {
        ModLog.Warn(
            $"[Sts2Solver] TURN_SETUP_INVARIANT_FAILED stage={stage} " +
            $"type={error.GetType().Name} detail={error.Message}");
        CombatSessionController.FailTurnSetup($"{stage}: {error.Message}");
    }

    private static async Task InvokeOriginalSetupAsync(TurnSetupTransaction t)
    {
        _invokingOriginalSetup = true;
        try
        {
            Task original = (Task)(SetupPlayerTurnMethod.Invoke(
                CombatManager.Instance,
                [t.TurnState, t.Player, t.ChoiceContext])
                ?? throw new InvalidOperationException("SetupPlayerTurn 没有返回任务。"));
            _invokingOriginalSetup = false;
            await original;
        }
        finally
        {
            _invokingOriginalSetup = false;
        }
    }

    private static void LogEnumeration(TurnSetupTransaction t, TurnSetupEnumeration enumeration)
    {
        ModLog.Info(
            $"[Sts2Solver] TURN_SETUP_ENUM turn={t.Turn} status={enumeration.Status} " +
            $"branches={enumeration.Branches.Length} dropped={enumeration.DroppedByBudget} " +
            $"reason={enumeration.Reason ?? "-"}");
    }

    private static bool RequiresSolverChoice(Player player)
    {
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        if (player.Relics.Any(relic => !relic.IsMelted
                && (relic is ToastyMittens
                    || turn <= 1 && relic is Toolbox or ChoicesParadox or GamblingChip)))
        {
            return true;
        }
        if (player.Creature.Powers.Any(power => power.Amount > 0
                && power is ForegoneConclusionPower
                    or EntropyPower
                    or ToolsOfTheTradePower
                    or TyrannyPower
                    or MayhemPower
                    or StratagemPower))
        {
            return true;
        }
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("回合准备选牌检测时玩家没有战斗牌堆。");
        return state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Concat(state.ExhaustPile.Cards)
            .Concat(state.PlayPile.Cards)
            .Any(static card => card.Enchantment is Imbued);
    }

    private sealed class TurnSetupTransaction(
        CombatState combat,
        Player player,
        Rules rules,
        LiveCombatTracker tracker,
        CombatTurnState turnState,
        HookPlayerChoiceContext choiceContext,
        Battle startRoot,
        NativeChoiceSession choices,
        CancellationTokenSource cancellation,
        int turn)
    {
        internal CombatState Combat { get; } = combat;
        internal Player Player { get; } = player;
        internal Rules Rules { get; } = rules;
        internal LiveCombatTracker Tracker { get; } = tracker;
        internal CombatTurnState TurnState { get; } = turnState;
        internal HookPlayerChoiceContext ChoiceContext { get; } = choiceContext;
        internal Battle StartRoot { get; } = startRoot;
        internal NativeChoiceSession Choices { get; } = choices;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal int Turn { get; } = turn;
        internal bool PageOpen { get; set; }
        internal bool SearchStarted { get; set; }
        internal bool PlanReady { get; set; }
        internal bool Driving { get; set; }
        internal bool DroveSelection { get; set; }
        internal bool PlayerAdvanced { get; set; }
        internal bool Stopped { get; set; }
        internal bool DeployAfterSetup { get; set; }
        internal bool Completed { get; set; }
        internal bool EnumerationObserved { get; set; }
        internal SearchCase? Case { get; set; }
        internal CombatControllerResult? Result { get; set; }
    }
}
