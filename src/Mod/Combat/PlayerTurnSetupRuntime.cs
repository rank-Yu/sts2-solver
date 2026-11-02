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

    private static readonly MethodInfo RunAutoPrePlayPhaseMethod = AccessTools.Method(
        typeof(CombatManager),
        "RunAutoPrePlayPhase",
        [typeof(CombatTurnState), typeof(HookPlayerChoiceContext), typeof(Task), typeof(Player)])
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "RunAutoPrePlayPhase");

    [ThreadStatic]
    private static bool _invokingOriginalSetup;
    [ThreadStatic]
    private static bool _invokingOriginalAutoPrePlay;
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
        int setupTurn = player.PlayerCombatState!.TurnNumber;
        Battle liveStart;
        try
        {
            liveStart = tracker.ImportSetupRoot(combat, player, rules);
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
        NormalizeSetupStartRoot(ref liveStart, tracker);
        Battle startRoot = liveStart;
        bool compared = false;
        bool matched = false;
        if (CombatSessionController.TryGetVerifiedEndTurn(setupTurn - 1,
                out Battle endTurnBefore, out PlayerAction endTurnAction))
        {
            compared = true;
            try
            {
                if (Kernel.TryAdvanceToPlayerSetupStart(endTurnBefore, rules, endTurnAction,
                        out Battle predicted))
                {
                    NormalizeSetupStartRoot(ref predicted, tracker);
                    matched = Fingerprint.Equal(predicted, liveStart);
                    if (matched)
                    {
                        startRoot = predicted;
                    }
                    else
                    {
                        List<string> diff = BattleDump.Diff(predicted, liveStart,
                            "predicted", "live");
                        ModLog.Warn(
                            $"[Sts2Solver] TURN_SETUP_START_PREDICTION_MISMATCH " +
                            $"turn={setupTurn} " +
                            $"field={(diff.Count == 0 ? "<bytes>" : diff[0])}");
                        foreach (string line in diff.Take(8))
                            ModLog.Warn($"[Sts2Solver]   {line}");
                    }
                }
                else
                {
                    ModLog.Info(
                        $"[Sts2Solver] TURN_SETUP_START_PREDICTION_SKIPPED " +
                        $"turn={setupTurn} reason=end_turn_prefix");
                }
            }
            catch (UnsupportedException error)
            {
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_START_PREDICTION_SKIPPED turn={setupTurn} " +
                    $"reason={error.Message}");
            }
            catch (CapacityException error)
            {
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_START_PREDICTION_SKIPPED turn={setupTurn} " +
                    $"region={error.Region} {error.Message}");
            }
            catch (InvalidDataException error)
            {
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_START_PREDICTION_SKIPPED turn={setupTurn} " +
                    $"reason={error.Message}");
            }
            catch (InvalidOperationException error)
            {
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_START_PREDICTION_SKIPPED turn={setupTurn} " +
                    $"reason={error.Message}");
            }
        }
        if (tracker.HasPredictedBoundary)
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_CARRY turn={setupTurn} " +
                $"compared={compared} matched={matched} " +
                $"recovered={startRoot.Counter(Counter.RecoveredHp)}");
        }
#if STS2_SOLVER_TESTING
        TestPredictedStartCompared = compared;
        TestPredictedStartMatched = matched;
        TestSetupStartRecoveredHp = startRoot.Counter(Counter.RecoveredHp);
#endif
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
            cancellation, setupTurn);
        _active = transaction;
        _lastTurn = setupTurn;
        _lastDroveSelection = false;
        _lastDeployAfterSetup = false;
#if STS2_SOLVER_TESTING
        TestInterceptCount++;
        TestLastInterceptTurn = setupTurn;
        TestSelectorNullAtLastIntercept = CardSelectCmd.Selector == null;
#endif
        CombatSessionController.OnTurnSetupIntercepted(setupTurn);
        Task operation = RunTurnSetupAsync(transaction);
        transaction.SetupTask = operation;
        task = operation;
        return true;
    }

    /// <summary>
    /// Intercepts the native <c>RunAutoPrePlayPhase</c> so the same page session and plan continue
    /// into the auto-pre-play selections. The method is invoked while the setup task is still
    /// paused, so the replacement task first awaits the setup replacement and only then decides
    /// whether a new search must start at the first auto-pre-play page.
    /// </summary>
    internal static bool TryInterceptAutoPrePlay(CombatManager manager, CombatTurnState turnState,
        HookPlayerChoiceContext choiceContext, Task setupTask, Player player, out Task? task)
    {
        task = null;
        if (_invokingOriginalAutoPrePlay
            || _active is not { Completed: false, AutoPrePlayIntercepted: false } transaction
            || !ReferenceEquals(transaction.Player, player)
            || !ReferenceEquals(transaction.Combat, manager.DebugOnlyGetState())
            || !ReferenceEquals(transaction.SetupTask, setupTask))
        {
            return false;
        }
        transaction.AutoPrePlayIntercepted = true;
#if STS2_SOLVER_TESTING
        TestAutoPrePlayInterceptCount++;
#endif
        task = RunAutoPrePlayAsync(transaction, manager, turnState, choiceContext, setupTask, player);
        return true;
    }

    /// <summary>
    /// Applies the turn-setup root facts on top of a native or predicted root: Vital Spark is
    /// normalized to zero at the native setup entry, there are no action-local shuffle events yet,
    /// and ShufflesCrossed/risk come from the tracker's last verified boundary.
    /// </summary>
    private static void NormalizeSetupStartRoot(ref Battle root, LiveCombatTracker tracker)
    {
        root.SetCounter(Counter.VitalSparkNormalizedAmount, 0);
        root.ShuffleEventCount = 0;
        root.ShufflesCrossed = tracker.ShufflesCrossed;
        root.Flags = (root.Flags & ~BattleFlags.HasRisk)
            | (tracker.HasRisk ? BattleFlags.HasRisk : 0);
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
#if STS2_SOLVER_TESTING
        TestSetupPlayRecoveredHp = result.Plan.Root.Counter(Counter.RecoveredHp);
#endif
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

    internal static int TestInterceptCount { get; private set; }

    internal static int TestLastInterceptTurn { get; private set; }

    internal static bool TestSelectorNullAtLastIntercept { get; private set; }

    internal static int TestAutoPrePlayInterceptCount { get; private set; }

    internal static bool TestLastSearchStartedInAutoPrePlay { get; private set; }

    internal static bool TestPredictedStartCompared { get; private set; }

    internal static bool TestPredictedStartMatched { get; private set; }

    internal static int TestSetupStartRecoveredHp { get; private set; } = -1;

    internal static int TestSetupPlayRecoveredHp { get; private set; } = -1;

    internal static void ResetTestState()
    {
        TestInterceptCount = 0;
        TestLastInterceptTurn = 0;
        TestSelectorNullAtLastIntercept = false;
        TestAutoPrePlayInterceptCount = 0;
        TestLastSearchStartedInAutoPrePlay = false;
        TestPredictedStartCompared = false;
        TestPredictedStartMatched = false;
        TestSetupStartRecoveredHp = -1;
        TestSetupPlayRecoveredHp = -1;
    }

    internal static async Task SelectFirstVisibleForTesting(NGame host, int count,
        CancellationToken token)
    {
        NativeChoiceSession choices = _active?.Choices
            ?? throw new InvalidOperationException("没有活动的回合准备选牌会话。");
        NativeChoiceRequest request = choices.FirstVisibleRequest
            ?? throw new InvalidOperationException("回合准备页面还没有出现。");
        IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> selected = request.LiveOptions
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
        t.EnumerationTask = enumerationTask;
        try
        {
            bool visible = await t.Choices.WaitForFirstVisibleSurfaceAsync(
                host, originalSetup, t.Cancellation.Token);
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_FIRST_VISIBLE turn={t.Turn} visible={visible} " +
                $"original_done={originalSetup.IsCompleted} enum_done={enumerationTask.IsCompleted}");
            if (visible)
            {
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
                if (!originalSetup.IsCompleted)
                {
                    (EnumerationWait wait, TurnSetupEnumeration? enumerationValue) =
                        await AwaitEnumerationAsync(t, enumerationTask, "visible_choice");
                    if (wait == EnumerationWait.Ok && enumerationValue != null)
                        StartSearchFromEnumeration(t, enumerationValue, "setup");
                }
            }
            // The setup task stays alive until the native setup body completes, even after the
            // search has started: the page driver consumes the remaining setup choices and the
            // AutoPrePlay prefix later continues the same session.
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
            await originalSetup.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
            if (ReferenceEquals(_active, t) && !t.Completed)
            {
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_SETUP_DONE turn={t.Turn} search={t.SearchStarted} " +
                    $"plan={t.PlanReady} auto_pre_play={t.AutoPrePlayIntercepted}");
            }
        }
        if (originalSetup.IsFaulted)
            await originalSetup;
    }

    /// <summary>
    /// Starts the one setup search from a completed enumeration if the plan is representable. The
    /// setup phase and the auto-pre-play phase share this method, so at most one search starts; the
    /// caller waits for the first visible native page before calling it.
    /// </summary>
    private static bool StartSearchFromEnumeration(TurnSetupTransaction t,
        TurnSetupEnumeration enumeration, string phase)
    {
        LogEnumeration(t, enumeration);
        if (enumeration.Status is TurnSetupEnumerationStatus.NoChoice
            or TurnSetupEnumerationStatus.Unsupported
            or TurnSetupEnumerationStatus.Terminal)
        {
            ModLog.Info(
                $"[Sts2Solver] TURN_SETUP_SKIP reason=enumeration_" +
                $"{enumeration.Status.ToString().ToLowerInvariant()} turn={t.Turn} phase={phase} " +
                $"detail={enumeration.Reason ?? "-"}");
            return false;
        }
        TurnSetupBranch[] branches = [.. enumeration.Branches.Where(static branch => !branch.Terminal)];
        if (branches.Length == 0)
        {
            ModLog.Info($"[Sts2Solver] TURN_SETUP_SKIP reason=all_terminal turn={t.Turn} phase={phase}");
            return false;
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
                    $"phase={phase} detail={compileDetail}");
            }
            return false;
        }
        t.Case = setupCase!;
        t.SearchStarted = true;
        t.Choices.RecordSearchStarted();
#if STS2_SOLVER_TESTING
        if (phase == "auto_pre_play")
            TestLastSearchStartedInAutoPrePlay = true;
#endif
        ModLog.Info(
            $"[Sts2Solver] TURN_SETUP_SEARCH_START turn={t.Turn} phase={phase} " +
            $"roots={branches.Length} dropped={enumeration.DroppedByBudget} " +
            "after_native_choice_visible=true");
        if (!CombatSessionController.StartTurnSetupSearch(setupCase!, t.StartRoot, t.Turn))
        {
            ModLog.Info($"[Sts2Solver] TURN_SETUP_SEARCH_SKIP turn={t.Turn} phase={phase}");
            return false;
        }
        return true;
    }

    private static async Task RunAutoPrePlayAsync(TurnSetupTransaction t, CombatManager manager,
        CombatTurnState turnState, HookPlayerChoiceContext choiceContext, Task setupTask, Player player)
    {
        Task original = Task.CompletedTask;
        try
        {
            original = InvokeOriginalAutoPrePlayAsync(
                manager, turnState, choiceContext, setupTask, player);
            // The native AutoPrePlay wrapper is invoked while the setup task is still paused; its
            // body awaits that task. Wait for our setup replacement first, then continue the same
            // session into the auto-pre-play pages.
            await t.SetupTask.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
            if (!ReferenceEquals(_active, t) || t.Completed)
                return;
            NGame? host = NGame.Instance
                ?? throw new InvalidOperationException("回合准备自动出牌缺少游戏节点。");
            if (!t.Choices.HasVisibleRequest)
            {
                bool visible = await t.Choices.WaitForFirstVisibleSurfaceAsync(
                    host, original, t.Cancellation.Token);
                ModLog.Info(
                    $"[Sts2Solver] TURN_SETUP_AUTO_PRE_PLAY_FIRST_VISIBLE turn={t.Turn} " +
                    $"visible={visible} original_done={original.IsCompleted} " +
                    $"enum_done={t.EnumerationTask?.IsCompleted.ToString() ?? "-"}");
                if (visible)
                {
                    t.PageOpen = true;
                    CombatSessionController.NotifyTurnSetupChanged();
                    ModLog.Info(
                        $"[Sts2Solver] TURN_SETUP_AUTO_PRE_PLAY_PAGE turn={t.Turn} " +
                        $"sequence={t.Choices.FirstVisibleSequence}");
                    if (t.EnumerationTask != null)
                    {
                        (EnumerationWait wait, TurnSetupEnumeration? enumerationValue) =
                            await AwaitEnumerationAsync(t, t.EnumerationTask, "auto_pre_play");
                        if (wait == EnumerationWait.Ok && enumerationValue != null)
                            StartSearchFromEnumeration(t, enumerationValue, "auto_pre_play");
                    }
                }
            }
            while (!original.IsCompleted)
            {
                ObservePlayerChoice(t, original);
                if (t.Driving && t.Choices.DriverFaulted)
                    HandleDriveFailure(t, t.Choices.DriverTask?.Exception?.GetBaseException());
                await CombatFrameWaiter.NextFrameAsync(host);
            }
            ObservePlayerChoice(t, original);
        }
        catch (OperationCanceledException) when (t.Cancellation.IsCancellationRequested)
        {
            ModLog.Info($"[Sts2Solver] TURN_SETUP_AUTO_PRE_PLAY_CANCELLED turn={t.Turn}");
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
            LogSetupInvariantFailure("auto_pre_play", error);
        }
        catch (InvalidOperationException error)
        {
            LogSetupInvariantFailure("auto_pre_play", error);
        }
        catch (TimeoutException error)
        {
            CombatSessionController.FailTurnSetup(error.Message);
        }
        finally
        {
            await original.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
            await CompleteTurnSetupAsync(t);
        }
        if (original.IsFaulted)
            await original;
    }

    /// <summary>
    /// Releases the single page session and the controller's setup result after the native
    /// AutoPrePlay body has finished (its Play phase is set). The controller consumes the plan from
    /// the subsequent <c>TurnStarted</c> event.
    /// </summary>
    private static async Task CompleteTurnSetupAsync(TurnSetupTransaction t)
    {
        if (t.Completed)
            return;
        try
        {
            if (t.EnumerationTask != null && !t.EnumerationObserved)
                _ = await AwaitEnumerationAsync(t, t.EnumerationTask, "cleanup");
            await t.Choices.CompleteAsync().ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (!t.PlayerAdvanced && t.Choices.DriverFaulted)
                HandleDriveFailure(t, t.Choices.DriverTask?.Exception?.GetBaseException());
        }
        catch (OperationCanceledException) when (t.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            t.PageOpen = false;
            t.Driving = false;
            if (t.SearchStarted && !t.PlanReady && !t.PlayerAdvanced)
                CombatSessionController.CancelTurnSetupSearch("setup_completed");
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
                $"drove={t.DroveSelection} advanced={t.PlayerAdvanced} " +
                $"deploy={t.DeployAfterSetup} auto_pre_play={t.AutoPrePlayIntercepted}");
        }
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

    private static Task InvokeOriginalAutoPrePlayAsync(CombatManager manager,
        CombatTurnState turnState, HookPlayerChoiceContext choiceContext, Task setupTask,
        Player player)
    {
        _invokingOriginalAutoPrePlay = true;
        try
        {
            return (Task)(RunAutoPrePlayPhaseMethod.Invoke(
                manager,
                [turnState, choiceContext, setupTask, player])
                ?? throw new InvalidOperationException("RunAutoPrePlayPhase 没有返回任务。"));
        }
        finally
        {
            _invokingOriginalAutoPrePlay = false;
        }
    }

    private static void LogEnumeration(TurnSetupTransaction t, TurnSetupEnumeration enumeration)
    {
        ModLog.Info(
            $"[Sts2Solver] TURN_SETUP_ENUM turn={t.Turn} status={enumeration.Status} " +
            $"branches={enumeration.Branches.Length} dropped={enumeration.DroppedByBudget} " +
            $"reason={enumeration.Reason ?? "-"}");
    }

    /// <summary>Whether the next player turn could request a solver-visible setup selection.</summary>
    private static bool RequiresSolverChoice(Player player)
        => RequiresSolverChoice(player, player.PlayerCombatState?.TurnNumber ?? 0);

    private static bool RequiresSolverChoice(Player player, int turn)
    {
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

    /// <summary>
    /// Fast gate for releasing the EndTurn <see cref="PlannedNativeSelector"/> before the next turn
    /// boundary. It mirrors the setup interception gate (same automatic modes, same content
    /// heuristic for the next turn number) so a combat without setup content keeps the selector
    /// until Play exactly as before.
    /// </summary>
    internal static bool MayNeedNextTurnSetup(Player player)
    {
        if (!ModEntry.Enabled
            || !TakeoverEnabled
            || !CombatSessionController.NextTurnSetupTakeoverAllowed
            || player.PlayerCombatState is not { } state)
        {
            return false;
        }
        return RequiresSolverChoice(player, state.TurnNumber + 1);
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
        internal bool AutoPrePlayIntercepted { get; set; }
        internal Task SetupTask { get; set; } = Task.CompletedTask;
        internal Task<TurnSetupEnumeration>? EnumerationTask { get; set; }
        internal SearchCase? Case { get; set; }
        internal CombatControllerResult? Result { get; set; }
    }
}
