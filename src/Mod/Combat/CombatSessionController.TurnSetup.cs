// Turn-setup search lifecycle and the exact Play-state handoff. The search itself is a normal
// portfolio request over the existing search roots; this file only owns the setup-specific result
// shape, stop/failure cleanup and the one run pause decision used before the page is driven.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    internal static bool TryCompileTurnSetupCase(CombatState state, Player player, Rules rules,
        Battle startRoot, IReadOnlyList<TurnSetupBranch> branches, out SearchCase? setupCase,
        out string detail, out bool fatal)
    {
        setupCase = null;
        detail = "";
        fatal = false;
        try
        {
            setupCase = SearchSession.CompileSetupCase(
                state, player, rules, startRoot, branches,
                SolverSearchPolicy.Capture(_theftPolicy),
#if STS2_SOLVER_TESTING
                SearchPolicyOverrideForTest,
#else
                null,
#endif
                deterministic: false, SolverSettings.Current.Search.Tier);
            return true;
        }
        catch (UnsupportedImportException error)
        {
            detail = $"reason={error.Reason} {error.Detail}";
            return false;
        }
        catch (UnsupportedException error)
        {
            detail = error.Message;
            return false;
        }
        catch (CapacityException error)
        {
            detail = CombatFailureDetail.Capacity(error);
            return false;
        }
        catch (InvalidDataException error)
        {
            detail = error.Message;
            fatal = true;
            return false;
        }
        catch (InvalidOperationException error)
        {
            detail = error.Message;
            fatal = true;
            return false;
        }
    }

    internal static bool StartTurnSetupSearch(SearchCase setupCase, Battle startRoot, int turn)
    {
        RequireMainThread();
        if (!_combatActive || _state == null || _player == null || _rules == null
            || _tracker == null || IsMultiplayerSession || _search != null
            || (_deployment != null && !_deployment.SupersededByTurnSetup))
        {
            return false;
        }
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_combatCancellation.Token);
        CombatSearchSession session = new(++_nextSearchGeneration, _state, startRoot,
            CombatStateStamp.Capture(startRoot), turn, CombatSearchReason.TurnSetup,
            SolverSearchPolicy.Capture(_theftPolicy), deployWhenReady: false, cancellation)
        {
            IsTurnSetup = true,
            SetupCase = setupCase,
            SetupStartRoot = startRoot,
        };
        session.ReviewedWorldlinesBeforeSearch = ReviewedWorldlinesSoFar();
        session.BattleHpLostBeforeSearch = startRoot.Counter(Counter.CumulativeHpLost);
        _search = session;
        _displayRoute = null;
        _displayCurrentTurn = null;
        _turnSetupResult = null;
        _latestResult = null;
        _latestStamp = null;
        _continuationSource = null;
        _pendingCompleteBaseline = null;
        _searchStartedAt = Environment.TickCount64;
        _searchTier = SolverSettings.Current.Search.Tier;
        _searchProfile = SearchPerformanceProfile.For(_searchTier);
        _searchThreads = SearchThreadCount.Value;
        _memberRuns = [];
        _lastProgress = null;
        _lastProgressPublishAt = 0;
        _lastCandidateVersion = 0;
        _activeMemberRun = -1;
        _activeMemberWidth = 0;
        _activeMemberBudget = 0;
        _activeMemberStartedAt = _searchStartedAt;
        _currentActionIndex = 0;
        _completedActions = 0;
        _actionRunning = false;
        _searches++;
        _phase = CombatUiPhase.Searching;
        _pauseCode = null;
        _pauseReason = null;
        _errorCode = null;
        _errorDetail = null;
        _rejectCode = null;
        _finishedSearch = null;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_TURN_SETUP_SEARCH generation={session.Generation} turn={turn} " +
            $"roots={setupCase.Battles.Length} tier={_searchTier} threads={_searchThreads} " +
            $"auto_search={_autoSearch} run={_runCombat}");
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.StopTurnSetupSearchOnStart)
        {
            session.StopRequested = true;
            _search = null;
            CombatSearchResult stopped = new(CombatSearchStatus.Invalid, null, -1, 0, 0, [],
                "stopped before candidate");
            CombatSearchRun empty = new(in startRoot, setupCase, stopped, null, null, null, null,
                Environment.TickCount64, _searchTier, _searchProfile, _searchThreads, [],
                null, null);
            CompleteTurnSetupSearch(session, empty);
            return true;
        }
#endif
        _ = RunTurnSetupSearchAsync(session, setupCase, startRoot);
        return true;
    }

    private static async Task RunTurnSetupSearchAsync(CombatSearchSession session,
        SearchCase setupCase, Battle startRoot)
    {
        NGame? host = NGame.Instance;
        if (host == null)
            return;
        try
        {
            CombatSearchRun run = await CombatSearchRunner.RunSetupAsync(host,
                _testRoot ?? "turn_setup", _rules!, _tracker!, session.State, startRoot, setupCase,
                session.StartTurn, _searchTier, session.Cancellation.Token);
            CombatLogRecorder.RecordSearch(run, setup: true, session.StartTurn, "turn_setup",
                resumed: false);
            if (!ReferenceEquals(_search, session))
                return;
            _search = null;
#if STS2_SOLVER_TESTING
            LastSearchCaseForTest = run.Case;
            LastSearchRouteForTest = run.Beam?.BestRoute ?? [];
#endif
            CompleteTurnSetupSearch(session, run);
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_search, session))
                return;
            _search = null;
            CountCancelledSearch();
            _finishedSearch = BuildFinishedSearch(session, null, "cancelled", "cancelled");
            _errorCode = null;
            _errorDetail = null;
            _phase = CombatUiPhase.WaitingTurn;
            if (session.StopRequested)
            {
                _pauseCode = "stopped";
                _pauseReason = "stopped_by_user";
                _automaticSearchPaused = true;
                _automaticSearchPausedTurn = session.StartTurn;
            }
            PlayerTurnSetupRuntime.OnSearchStopped(null);
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_TURN_SETUP_CANCELLED turn={session.StartTurn}");
            ResumePendingTurnStarted();
        }
        catch (UnsupportedException error)
        {
            FailTurnSetupSearch(session, error.Message);
        }
        catch (CapacityException error)
        {
            FailTurnSetupSearch(session, CombatFailureDetail.Capacity(error));
        }
        catch (InvalidDataException error)
        {
            FailTurnSetupSearch(session, error.Message);
        }
        catch (InvalidOperationException error)
        {
            FailTurnSetupSearch(session, error.Message);
        }
        catch (TimeoutException error)
        {
            FailTurnSetupSearch(session, error.Message);
        }
        finally
        {
            session.Cancellation.Dispose();
        }
    }

    private static void CompleteTurnSetupSearch(CombatSearchSession session, CombatSearchRun run)
    {
        bool stopped = session.StopRequested;
        if (run.Plan == null || run.Beam == null || run.Outcome.Beam == null
            || run.SetupPlayStamp == null)
        {
            _finishedSearch = BuildFinishedSearch(session, run, "failed",
                run.ErrorCode ?? "turn_setup_failed");
            if (stopped)
            {
                _errorCode = null;
                _errorDetail = null;
                _phase = CombatUiPhase.WaitingTurn;
                _pauseCode = "stopped";
                _pauseReason = "stopped_by_user";
                _automaticSearchPaused = true;
                _automaticSearchPausedTurn = session.StartTurn;
                PlayerTurnSetupRuntime.OnSearchStopped(null);
                AddSearchLog(session, run, "stopped");
                PublishSnapshot();
                ModLog.Info(
                    $"[Sts2Solver] CONTROLLER_TURN_SETUP_STOP_DROP turn={session.StartTurn}");
                ResumePendingTurnStarted();
                return;
            }
            FailTurnSetup(run.ErrorDetail ?? "准备搜索没有返回可执行计划。");
            AddSearchLog(session, run, "failed");
            ResumePendingTurnStarted();
            return;
        }

        _names ??= CombatDisplayNames.Capture(_state!);
        CombatControllerResult result = new(
            run.Plan,
            run.Plan.Outcome,
            run.SetupPlayStamp!,
            run.FinalProgress?.CandidateVersion ?? 0,
            run.Outcome.Adoption,
            0,
            0,
            false,
            run.Tier,
            run.Outcome.ElapsedMilliseconds,
            run.Outcome.TotalExpandedNodes,
            run.Outcome,
            run.MemberRuns,
            "turn_setup",
            run.Plan.Root.Counter(Counter.CumulativeHpLost),
            run.TurnSetupChoices,
            run.SetupPlayStamp,
            CapacityDroppedRegions: run.Outcome.CapacityDroppedRegions,
            PotionsUsedSoFar: PotionsUsedSoFar());
        _turnSetupResult = result;
        _latestResult = result;
        _latestStamp = result.Stamp;
        // The setup result only becomes a continuation source after its exact Play stamp has been
        // validated. Until then it must not enter the ordinary reuse/deployment path.
        _continuationSource = null;
        _finishedSearch = BuildFinishedSearch(session, run, stopped ? "stopped" : "ok", null);
        _phase = CombatUiPhase.WaitingTurn;
        if (stopped)
        {
            _pauseCode = "stopped";
            _pauseReason = "stopped_by_user";
            _automaticSearchPaused = true;
            _automaticSearchPausedTurn = session.StartTurn;
        }
        else
        {
            _pauseCode = null;
            _pauseReason = null;
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
        }
        AddSearchLog(session, run, stopped ? "stopped" : "ok");
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_TURN_SETUP_RESULT turn={session.StartTurn} " +
            $"choices={run.TurnSetupChoices.Length} stopped={stopped} " +
            $"expanded={run.Outcome.TotalExpandedNodes} root={run.Beam.BestRootIndex}");
        PlayerTurnSetupRuntime.OnPlanReady(result, stopped);
        PublishSnapshot();
        ResumePendingTurnStarted();
    }

    private static void FailTurnSetupSearch(CombatSearchSession session, string detail)
    {
        if (!ReferenceEquals(_search, session))
            return;
        _search = null;
        _finishedSearch = BuildFinishedSearch(session, null, "failed", "turn_setup_failed");
        FailTurnSetup(detail);
    }

    internal static void FailTurnSetup(string detail)
    {
        _turnSetupResult = null;
        if (_latestResult?.IsTurnSetup == true)
        {
            _latestResult = null;
            _latestStamp = null;
        }
        _continuationSource = null;
        _errorCode = "turn_setup_failed";
        _errorDetail = detail;
        _rejectCode = null;
        _phase = CombatUiPhase.Failed;
        PlayerTurnSetupRuntime.OnSearchFailed();
        PublishSnapshot();
        ModLog.Warn($"[Sts2Solver] CONTROLLER_TURN_SETUP_FAILED {detail}");
    }

    internal static void CancelTurnSetupSearch(string reason)
    {
        if (_search is not { IsTurnSetup: true } search)
            return;
        _search = null;
        CountCancelledSearch();
        if (!search.Cancellation.IsCancellationRequested)
            search.Cancellation.Cancel();
        _finishedSearch = BuildFinishedSearch(search, null, "cancelled", "cancelled");
        _errorCode = null;
        _errorDetail = null;
        _phase = _combatActive ? CombatUiPhase.WaitingTurn : CombatUiPhase.Idle;
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_TURN_SETUP_CANCEL reason={reason}");
    }

    internal static void OnTurnSetupPlayerAdvanced(int turn)
    {
        if (_search is { IsTurnSetup: true })
            CancelTurnSetupSearch("player_advanced");
        DiscardTurnSetupPlan("player_advanced");
        _phase = _combatActive ? CombatUiPhase.WaitingTurn : CombatUiPhase.Idle;
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_TURN_SETUP_PLAYER_ADVANCED turn={turn}");
    }

    private static void DiscardTurnSetupPlan(string reason)
    {
        _continuationSetupTurn = null;
        if (_turnSetupResult == null)
            return;
        if (_latestResult != null && ReferenceEquals(_latestResult, _turnSetupResult))
        {
            _latestResult = null;
            _latestStamp = null;
        }
        _turnSetupResult = null;
        _continuationSource = null;
        if (_phase is not (CombatUiPhase.Failed or CombatUiPhase.Paused))
            _phase = _combatActive ? CombatUiPhase.WaitingTurn : CombatUiPhase.Idle;
    }

    /// <summary>
    /// The previous turn's verified EndTurn action carries this turn's <c>PlayerTurnStart</c>
    /// choices. The page can reuse that plan without enumerating or searching; the ordinary
    /// continuation comparison after Play still decides whether the route is exact.
    /// </summary>
    internal static bool TryGetPlannedTurnSetupContinuation(int turn,
        out CombatControllerResult result, out Battle expectedPlayRoot)
    {
        result = null!;
        expectedPlayRoot = default;
        if (_continuationSource is not { } source || !SolverDeployedTurn(turn - 1))
            return false;
        ReplayedAction? endTurn = source.Plan.EndTurnAction(turn - 1);
        if (endTurn == null)
            return false;
        PlannedChoice[] planned = [.. endTurn.Choices.Where(
            static choice => choice.Request.Timing == ChoiceTiming.PlayerTurnStart)];
        if (planned.Length == 0)
            return false;
        CombatRoutePlan? trimmed = source.Plan.TrimToTurn(turn);
        if (trimmed == null || trimmed.Actions.Length == 0
            || trimmed.Actions[0].Before.PlayerTurn != turn)
        {
            return false;
        }
        expectedPlayRoot = endTurn.After;
        result = source with
        {
            Plan = trimmed.WithRootSetupChoices(planned),
            TurnSetupChoices = planned,
            SetupPlayStamp = null,
            IsContinuationSetup = true,
            Reason = "turn_setup_continuation",
        };
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_TURN_SETUP_CONTINUATION turn={turn} " +
            $"choices={planned.Length} remaining={trimmed.Actions.Length}");
        return true;
    }

    private static bool SolverDeployedTurn(int turn)
        => _lastSolverDeployedTurn == turn
            || _deploymentResult is { } result && result.StartTurn == turn
            || _deployment is { } deployment && deployment.Turn == turn;

    /// <summary>Publishes the reused plan while the page waits for the player or the driver.</summary>
    internal static void OnTurnSetupContinuationReady(CombatControllerResult result)
    {
        _turnSetupResult = result;
        _latestResult = result;
        _continuationSetupTurn = result.StartTurn;
        _phase = CombatUiPhase.WaitingTurn;
        PublishSnapshot();
    }

    /// <summary>
    /// The page and the plan disagree (or a planned card is absent). Never guess: return the page
    /// to the player and, while 执行本场战斗 runs, pause the run with <c>choice_mismatch</c>.
    /// </summary>
    internal static void FailTurnSetupChoiceMismatch(string detail)
    {
        DiscardTurnSetupPlan("choice_mismatch");
        _errorCode = null;
        _errorDetail = null;
        if (_runCombat)
        {
            StopRun("pause");
            _runPaused = true;
            _pauseCode = "choice_mismatch";
            _pauseReason = detail;
            _phase = CombatUiPhase.Paused;
            ModLog.Warn($"[Sts2Solver] CONTROLLER_FULL_AUTO_STOP reason=choice_mismatch {detail}");
        }
        else
        {
            _pauseCode = null;
            _pauseReason = null;
            _phase = _combatActive ? CombatUiPhase.WaitingTurn : CombatUiPhase.Idle;
            ModLog.Warn($"[Sts2Solver] CONTROLLER_TURN_SETUP_CHOICE_MISMATCH {detail}");
        }
        PlayerTurnSetupRuntime.OnSearchFailed();
        PublishSnapshot();
    }

    /// <summary>
    /// Consumes a setup plan at the first native Play event. The exact Play stamp must match and
    /// the page must have been driven by the solver: a player selection (even one that happens to
    /// equal the plan) retires the search instead of installing a stale route.
    /// </summary>
    private static bool TryActivateTurnSetup(int turn)
    {
        if (_turnSetupResult is not { } result)
            return false;
        if (result.IsContinuationSetup)
        {
            if (result.StartTurn != turn || !PlayerTurnSetupRuntime.LastTurnSetupDroveSelection(turn))
            {
                ModLog.Info(
                    $"[Sts2Solver] CONTROLLER_TURN_SETUP_DISCARD turn={turn} reason=player_selected");
                DiscardTurnSetupPlan("player_selected");
                PublishSnapshot();
                return false;
            }
            // Drop the display-only preview. The ordinary continuation comparison in
            // RunTurnStartedAsync decides whether the reused route still matches the Play state.
            _turnSetupResult = null;
            if (_latestResult != null && ReferenceEquals(_latestResult, result))
            {
                _latestResult = null;
                _latestStamp = null;
            }
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_TURN_SETUP_CONTINUATION_READY turn={turn}");
            return false;
        }
        if (_player?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play })
            return false;
        if (result.StartTurn != turn
            || !PlayerTurnSetupRuntime.LastTurnSetupDroveSelection(turn))
        {
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_TURN_SETUP_DISCARD turn={turn} reason=player_selected");
            DiscardTurnSetupPlan("player_selected");
            PublishSnapshot();
            return false;
        }
        if (_state == null || _player == null || _rules == null || _tracker == null)
            return false;
#if STS2_SOLVER_TESTING
        CombatControllerTestHooks.BeforeTurnSetupPlayStamp?.Invoke(_state, _player);
#endif
        Battle live;
        try
        {
            _tracker.Refresh(_state);
            live = _tracker.ImportRoot(_state, _player, _rules);
        }
        catch (UnsupportedImportException error)
        {
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_TURN_SETUP_STAMP_FAILED reason={error.Reason} {error.Detail}");
            DiscardTurnSetupPlan("stamp_import_failed");
            PublishSnapshot();
            return false;
        }
        catch (UnsupportedException error)
        {
            ModLog.Warn($"[Sts2Solver] CONTROLLER_TURN_SETUP_STAMP_FAILED {error.Message}");
            DiscardTurnSetupPlan("stamp_import_failed");
            PublishSnapshot();
            return false;
        }
        catch (CapacityException error)
        {
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_TURN_SETUP_STAMP_FAILED region={error.Region} {error.Message}");
            DiscardTurnSetupPlan("stamp_import_failed");
            PublishSnapshot();
            return false;
        }
        catch (InvalidDataException error)
        {
            ModLog.Warn($"[Sts2Solver] CONTROLLER_TURN_SETUP_STAMP_FAILED {error.Message}");
            DiscardTurnSetupPlan("stamp_import_failed");
            PublishSnapshot();
            return false;
        }
        CombatStateStamp? expected = result.SetupPlayStamp;
        if (expected is null || !expected.Matches(live))
        {
            string first = "<missing>";
            if (expected != null)
            {
                List<string> diff = BattleDump.Diff(expected.Normalized,
                    BattleReconciler.Normalize(live), "plan", "live");
                first = diff.Count == 0 ? "<bytes>" : diff[0];
            }
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_TURN_SETUP_STATE_MISMATCH turn={turn} " +
                $"validation=exact_state_text boundary_deviation=true field={first}");
            PauseForTurnStop(turn, "boundary_deviation",
                "turn_setup_live_state_changed", countDeviation: true);
            return true;
        }
        CombatStateStamp liveStamp = CombatStateStamp.Capture(live);
        CombatControllerResult activated = result with { Stamp = liveStamp };
        _turnSetupResult = null;
        _searchedTurn = turn;
        _latestResult = activated;
        _latestStamp = liveStamp;
        _continuationSource = activated;
        _reusedTurns++;
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        _pauseCode = null;
        _pauseReason = null;
        _phase = CombatUiPhase.WaitingTurn;
        bool deploy = PlayerTurnSetupRuntime.LastTurnSetupDeployAfterSetup(turn);
        PlayerTurnSetupRuntime.ConsumeLastTurnSetup();
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_TURN_SETUP_ACTIVATE turn={turn} " +
            $"actions={activated.Plan.Actions.Length} deploy={deploy} reused={_reusedTurns}");
        if (deploy)
            StartDeployment(activated);
        else if (_runCombat)
            StartFullAutoDeployment(activated);
        return true;
    }

    /// <summary>
    /// The single pre-drive pause decision while 执行本场战斗 runs. Returns true when the page must not
    /// be driven and the run is now paused. To follow the original game (drive first and pause after
    /// Play), make this method always return false: OnPlanReady then drives, and
    /// TryActivateTurnSetup -> StartFullAutoDeployment applies the existing pause policy after Play.
    /// </summary>
    internal static bool TryPauseTurnSetupFullAuto(CombatControllerResult result, out string code,
        out string reason)
    {
        code = "";
        reason = "";
        CombatRouteOutcome outcome = result.Outcome;
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.ForceProjectedDeathTurnSetup)
            outcome = outcome with { DeathTurn = result.StartTurn };
#endif
        // The same policy as every other deployment: death this turn always pauses, a later death
        // pauses once per combat. OnlyDeathRoutes is the selected route's survival verdict, so it is
        // not a pause reason of its own (it used to read as death this turn).
        CombatPauseCode? pause = CombatFullAutoPausePolicy.BeforeDeployment(result.StartTurn, outcome,
            noSurvivalPause: NoSurvivalPauseAvailable);
        if (pause is null)
            return false;
        if (pause.Value == CombatPauseCode.NoSurvival)
            _noSurvivalPauseUsed = true;
        code = CombatFullAutoPausePolicy.Code(pause.Value);
        reason = $"death_turn={outcome.DeathTurn}";
        ApplyTurnSetupFullAutoPause(code, reason);
        return true;
    }

    private static void ApplyTurnSetupFullAutoPause(string code, string reason)
    {
        StopRun("pause");
        _runPaused = true;
        _continuationSource = null;
        _pauseCode = code;
        _pauseReason = reason;
        _phase = CombatUiPhase.Paused;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_FULL_AUTO_STOP reason={code} " +
            $"turn={CurrentTurn()} setup=true {reason}");
    }
}
