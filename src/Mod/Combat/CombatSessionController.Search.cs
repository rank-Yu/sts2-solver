// Search request, continuation reuse and completion. All of this runs on the game main thread;
// CombatSearchRunner and SearchSession do the only background work.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    private static void RequestSearch(CombatSearchReason reason, bool deployWhenReady)
    {
        RequireMainThread();
        if (!_combatActive || _state == null || _player == null || _rules == null || _tracker == null)
            return;
        if (IsMultiplayerSession)
            return;
        if (reason == CombatSearchReason.Manual)
        {
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
            _pauseCode = null;
            _pauseReason = null;
            _pendingCompleteBaseline = null;
            ClearConsecutiveFailures(CurrentTurn());
        }
        else if (_automaticSearchPaused
            && reason is not (CombatSearchReason.DeploymentDrift or CombatSearchReason.PlanExhausted))
        {
            _pauseCode = "stopped";
            _phase = CombatUiPhase.Paused;
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_REJECT reason=user_stopped request={reason}");
            return;
        }
        if (reason == CombatSearchReason.AutoTurnStart && !ShouldAutomaticallySearchNextTurn)
        {
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return;
        }
        int turn = CurrentTurn();
        if (reason == CombatSearchReason.AutoTurnStart && SearchAlreadyOwned(turn))
        {
            ModLog.Info($"[Sts2Solver] CONTROLLER_AUTO_SEARCH_OWNED turn={turn}");
            return;
        }
        if (!CanSolve(out string rejection,
                allowDeployment: reason is CombatSearchReason.DeploymentDrift
                    or CombatSearchReason.PlanExhausted))
        {
            _rejectCode = rejection;
            _pauseCode = null;
            if (_phase != CombatUiPhase.Failed)
                _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_REJECT reason={rejection}");
            return;
        }
        ClearFailureState();
        if (reason == CombatSearchReason.AutoTurnStart && _continuationSource != null
            && TryReuseContinuation(turn, deployWhenReady))
        {
            return;
        }
        if (reason != CombatSearchReason.AutoTurnStart)
            _continuationSource = null;
        if (_search != null)
            CancelSearch();
        _rejectCode = null;
        Battle root;
        try
        {
            if (!_trackerSeeded)
            {
                Battle seed = _tracker.ImportRoot(_state, _player, _rules);
                _tracker.Observe(seed);
                _trackerSeeded = true;
            }
            _tracker.Refresh(_state);
            root = _tracker.ImportRoot(_state, _player, _rules);
        }
        catch (UnsupportedImportException error)
        {
            PublishSearchSetupFailure("import_failed", $"reason={error.Reason} {error.Detail}");
            return;
        }
        catch (UnsupportedException error)
        {
            PublishSearchSetupFailure("import_unsupported", error.Message);
            return;
        }
        catch (CapacityException error)
        {
            PublishSearchSetupFailure("import_capacity", $"region={error.Region} {error.Message}");
            return;
        }
        catch (InvalidDataException error)
        {
            PublishSearchSetupFailure("invalid_data", error.Message);
            return;
        }
        catch (InvalidOperationException error)
        {
            PublishSearchSetupFailure("invalid_operation", error.Message);
            return;
        }
        CombatStateStamp stamp = CombatStateStamp.Capture(root);
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_combatCancellation.Token);
        CombatSearchSession session = new(++_nextSearchGeneration, _state, root, stamp, turn,
            reason, SolverSearchPolicy.Capture(_theftPolicy), deployWhenReady, cancellation);
        session.ReviewedWorldlinesBeforeSearch = SearchLog.Sum(static entry => entry.ExpandedNodes);
        _search = session;
        _latestResult = null;
        _latestStamp = null;
        _pendingCompleteBaseline = reason == CombatSearchReason.AutoTurnStart
            ? _pendingCompleteBaseline
            : null;
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
        _searches++;
        _phase = CombatUiPhase.Searching;
        _pauseCode = null;
        _pauseReason = null;
        _errorCode = null;
        _errorDetail = null;
        _finishedSearch = null;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH_REQUEST generation={session.Generation} turn={turn} " +
            $"reason={reason} deploy_when_ready={deployWhenReady} tier={_searchTier} " +
            $"threads={_searchThreads} mode={_mode} full_auto={_fullAutoEnabled}");
        _ = RunSearchAsync(session, root);
    }

#if STS2_SOLVER_TESTING
    /// <summary>
    /// Compiles the request the next search would run without starting it. Controller scenarios use
    /// this to assert strategy plumbing on roots whose full search is too expensive.
    /// </summary>
    internal static SearchCase CompileRequestForTest()
    {
        RequireMainThread();
        if (!_combatActive || _state == null || _player == null || _rules == null || _tracker == null)
            throw new InvalidOperationException("没有可编译的战斗根。");
        if (!_trackerSeeded)
        {
            Battle seed = _tracker.ImportRoot(_state, _player, _rules);
            _tracker.Observe(seed);
            _trackerSeeded = true;
        }
        _tracker.Refresh(_state);
        Battle root = _tracker.ImportRoot(_state, _player, _rules);
        return SearchSession.CompileCase(_state, _player, _rules, root,
            SolverSearchPolicy.Capture(_theftPolicy), SearchPolicyOverrideForTest,
            deterministic: false, SolverSettings.Current.Search.Tier);
    }
#endif

    private static async Task RequestSearchWhenIdleAsync(CombatSearchReason reason)
    {
        NGame? host = NGame.Instance;
        if (host == null)
            return;
        for (int frame = 0; frame < 900; frame++)
        {
            if (!SearchSession.IsRunning && _search == null && !HasBlockingDeployment)
                break;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        if (!_combatActive || IsMultiplayerSession || _search != null
            || HasBlockingDeployment || !CanSolve(out _))
        {
            return;
        }
        RequestSearch(reason, deployWhenReady: false);
    }

    private static async Task RunSearchAsync(CombatSearchSession session, Battle root)
    {
        NGame? host = NGame.Instance;
        if (host == null)
            return;
        try
        {
            CombatSearchRun run = await CombatSearchRunner.RunAsync(host, _testRoot ?? "manual",
                _rules!, _tracker!, session.State, _player!, root, session.StartTurn, session.Reason,
                _searchTier, session.PolicyConfiguration,
#if STS2_SOLVER_TESTING
                SearchPolicyOverrideForTest,
#else
                null,
#endif
                session.Cancellation.Token);
            if (!ReferenceEquals(_search, session))
                return;
            _search = null;
#if STS2_SOLVER_TESTING
            LastSearchCaseForTest = run.Case;
            LastSearchRouteForTest = run.Beam?.BestRoute ?? [];
#endif
            CompleteSearch(session, run);
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_search, session))
                return;
            _search = null;
            _finishedSearch = BuildFinishedSearch(session, null, "cancelled", "cancelled");
            _errorCode = "cancelled";
            _errorDetail = null;
            _phase = CombatUiPhase.WaitingTurn;
            if (session.StopRequested)
            {
                _pauseCode = "stopped";
                _automaticSearchPaused = true;
                _automaticSearchPausedTurn = session.StartTurn;
            }
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_CANCELLED turn={session.StartTurn}");
        }
        catch (UnsupportedException error)
        {
            FailSearch(session, "unsupported", error.Message);
        }
        catch (CapacityException error)
        {
            FailSearch(session, "capacity", $"region={error.Region} {error.Message}");
        }
        catch (InvalidDataException error)
        {
            FailSearch(session, "invalid_data", error.Message);
        }
        catch (InvalidOperationException error)
        {
            FailSearch(session, "invalid_operation", error.Message);
        }
        catch (TimeoutException error)
        {
            FailSearch(session, "timeout", error.Message);
        }
        finally
        {
            session.Cancellation.Dispose();
        }
    }

    private static void FailSearch(CombatSearchSession session, string code, string detail)
    {
        if (!ReferenceEquals(_search, session))
            return;
        _search = null;
        _finishedSearch = BuildFinishedSearch(session, null, "failed", code);
        _errorCode = code;
        _errorDetail = detail;
        _pendingCompleteBaseline = null;
        if (RecordConsecutiveFailure(session.StartTurn, code, detail))
            return;
        _phase = CombatUiPhase.Failed;
        PublishSnapshot();
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_FAILED turn={session.StartTurn} code={code} {detail}");
    }

    private static bool TryReuseContinuation(int turn, bool deployWhenReady)
    {
        if (_continuationSource is not { } source || _state == null || _player == null
            || _rules == null || _tracker == null)
        {
            return false;
        }
        int index = CombatRouteDecision.FindTurnIndex(source.Plan.Actions, turn);
        ReplayedAction? expected = index >= 0 ? source.Plan.Actions[index] : null;
        Battle live;
        try
        {
            live = expected is { } action
                ? _tracker.ImportForComparison(_state, _player, _rules, action.Before)
                : _tracker.ImportRoot(_state, _player, _rules);
        }
        catch (UnsupportedImportException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED reason={error.Reason} {error.Detail}");
            _continuationSource = null;
            return false;
        }
        catch (UnsupportedException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED {error.Message}");
            _continuationSource = null;
            return false;
        }
        catch (CapacityException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED region={error.Region} {error.Message}");
            _continuationSource = null;
            return false;
        }
        catch (InvalidDataException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED {error.Message}");
            _continuationSource = null;
            return false;
        }
        if (expected is not null
            && CombatRouteDecision.FindContinuationIndex(source.Plan.Actions, turn, live) >= 0)
        {
            CombatRoutePlan? trimmed = source.Plan.TrimToTurn(turn);
            if (trimmed == null)
                return false;
            // The trimmed plan's root is this turn: its capsules come from the previous EndTurn's
            // PlayerTurnStart choices, not from the source route's root turn.
            PlannedChoice[] currentSetup = source.Plan.EndTurnAction(turn - 1) is { } priorEndTurn
                ? [.. priorEndTurn.Choices.Where(
                    static choice => choice.Request.Timing == ChoiceTiming.PlayerTurnStart)]
                : [];
            trimmed = trimmed.WithRootSetupChoices(currentSetup);
            _tracker.Observe(live);
            Battle stampState;
            try
            {
                stampState = _tracker.ImportRoot(_state, _player, _rules);
            }
            catch (UnsupportedImportException error)
            {
                ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED reason={error.Reason} {error.Detail}");
                _continuationSource = null;
                return false;
            }
            catch (UnsupportedException error)
            {
                ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED {error.Message}");
                _continuationSource = null;
                return false;
            }
            catch (CapacityException error)
            {
                ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED region={error.Region} {error.Message}");
                _continuationSource = null;
                return false;
            }
            catch (InvalidDataException error)
            {
                ModLog.Error($"[Sts2Solver] CONTROLLER_REUSE_IMPORT_FAILED {error.Message}");
                _continuationSource = null;
                return false;
            }
            CombatStateStamp reuseStamp = CombatStateStamp.Capture(stampState);
            if (_continuationSetupTurn == turn)
            {
#if STS2_SOLVER_TESTING
                ContinuationReuseSuccessesForTest++;
#endif
                _continuationSetupTurn = null;
            }
            CombatControllerResult reused = source with
            {
                Plan = trimmed,
                Stamp = reuseStamp,
                PreviousProjectedBattleHpLost = 0,
                ProjectedBattleHpLossIncrease = 0,
                RecalculatedAfterCompleteProjection = false,
                Reason = "reuse",
            };
            _latestResult = reused;
            _latestStamp = reuseStamp;
            _currentActionIndex = 0;
            _completedActions = 0;
            _reusedTurns++;
            _pauseCode = null;
            _pauseReason = null;
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
            _phase = CombatUiPhase.WaitingTurn;
            _finishedSearch = null;
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_REUSE turn={turn} source_turn={source.StartTurn} " +
                $"remaining={trimmed.Actions.Length}");
            if (_fullAutoEnabled)
                StartFullAutoDeployment(reused);
            else if (deployWhenReady)
                StartDeployment(reused);
            return true;
        }

        string difference;
        if (expected is { } expectedAction)
        {
            string[] diff = CombatRouteDecision.BoundaryDiff(expectedAction.Before, live);
            difference = string.Join(" | ", diff);
        }
        else
        {
            difference = "field=continuation expected={cached_turn_missing} actual={live_turn_present}";
        }
        bool followedBySolver = _lastSolverDeployedTurn == turn - 1;
        if (followedBySolver && source.Outcome.CombatEndedTurn.HasValue)
        {
            _pendingCompleteBaseline = new CompleteProjectionBaseline(
                source.StartTurn, source.Outcome.ProjectedBattleHpLost, difference);
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_RECALC_BASELINE previous_turn={source.StartTurn} " +
                $"projected_hp_lost={source.Outcome.ProjectedBattleHpLost} diff={difference}");
        }
        _continuationSource = null;
        if (_continuationSetupTurn == turn)
        {
#if STS2_SOLVER_TESTING
            ContinuationReuseMissesForTest++;
            ContinuationReuseMissDetailForTest = difference;
#endif
            _continuationSetupTurn = null;
        }
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_REUSE_MISS turn={turn} source_turn={source.StartTurn} " +
            $"followed_by_solver={followedBySolver} {difference}");
        return false;
    }

    private static void CompleteSearch(CombatSearchSession session, CombatSearchRun run)
    {
        if (run.Plan == null || run.Beam == null || run.Outcome.Beam == null)
        {
            _finishedSearch = BuildFinishedSearch(session, run, "failed",
                run.ErrorCode ?? "search_failed");
            _errorCode = run.ErrorCode ?? "search_failed";
            _errorDetail = run.ErrorDetail;
            _phase = session.StopRequested ? CombatUiPhase.Paused : CombatUiPhase.Failed;
            if (session.StopRequested)
                _pauseCode = "stopped";
            AddSearchLog(session, run, "failed");
            _pendingCompleteBaseline = null;
            if (!session.StopRequested
                && run.Outcome.Completion != SearchCompletionKind.Cancelled
                && RecordConsecutiveFailure(session.StartTurn,
                    run.ErrorCode ?? "search_failed", run.ErrorDetail ?? ""))
            {
                return;
            }
            PublishSnapshot();
            return;
        }
        if (!SearchStampMatches(session.Stamp))
        {
            _latestResult = null;
            _latestStamp = null;
            _continuationSource = null;
            _pendingCompleteBaseline = null;
            _finishedSearch = BuildFinishedSearch(session, run, "stale", "search_stale");
            _errorCode = "search_stale";
            _errorDetail = "result root no longer matches the live state";
            _phase = CombatUiPhase.WaitingTurn;
            AddSearchLog(session, run, "stale");
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_STALE turn={session.StartTurn}");
            return;
        }

        CombatControllerResult result = new(run.Plan, run.Plan.Outcome, session.Stamp,
            run.FinalProgress?.CandidateVersion ?? 0, run.Outcome.Adoption, 0, 0, false,
            run.Tier, run.Outcome.ElapsedMilliseconds, run.Outcome.TotalExpandedNodes,
            run.Outcome, run.MemberRuns, ReasonName(session.Reason),
            run.Plan.Root.Counter(Sts2Solver.Simulation.Combat.Counter.CumulativeHpLost));
        if (session.StopRequested)
        {
            _names ??= CombatDisplayNames.Capture(_state!);
            _latestResult = result;
            _latestStamp = result.Stamp;
            _continuationSource = result;
            _finishedSearch = BuildFinishedSearch(session, run, "stopped", null);
            _pauseCode = "stopped";
            _pauseReason = session.ApplyCurrentTurn ? "execute_current_turn" : "stopped_by_user";
            _automaticSearchPaused = true;
            _automaticSearchPausedTurn = session.StartTurn;
            _phase = CombatUiPhase.WaitingTurn;
            AddSearchLog(session, run, "stopped");
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_STOPPED_RESULT turn={result.StartTurn} " +
                $"candidate={result.CandidateVersion} actions={result.Plan.Actions.Length} " +
                $"apply={session.ApplyCurrentTurn}");
            if (session.ApplyCurrentTurn)
            {
                _automaticSearchPaused = false;
                _automaticSearchPausedTurn = null;
                _pauseCode = null;
                _pauseReason = null;
                StartDeployment(result);
            }
            return;
        }

        _names ??= CombatDisplayNames.Capture(_state!);
        result = ApplyProjectionBaseline(result);
        _latestResult = result;
        _latestStamp = result.Stamp;
        _continuationSource = result;
        _pauseCode = null;
        _pauseReason = null;
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        _phase = CombatUiPhase.WaitingTurn;
        _finishedSearch = BuildFinishedSearch(session, run, "ok", null);
        AddSearchLog(session, run, "ok");
        _pendingCompleteBaseline = null;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH_DONE turn={result.StartTurn} " +
            $"completion={run.Outcome.Completion} death_turn={result.Outcome.DeathTurn?.ToString() ?? "-"} " +
            $"combat_end_turn={result.Outcome.CombatEndedTurn?.ToString() ?? "-"} " +
            $"projected_hp_lost={result.Outcome.ProjectedBattleHpLost} " +
            $"increase={result.ProjectedBattleHpLossIncrease} adopted={result.Adoption?.Kind.ToString() ?? "-"}");
        if (session.DeployWhenReady)
            StartDeployment(result);
        else if (_fullAutoEnabled)
            StartFullAutoDeployment(result);
    }

    private static CombatControllerResult ApplyProjectionBaseline(CombatControllerResult result)
    {
        if (_pendingCompleteBaseline is not { } baseline
            || result.Outcome.ProjectedBattleHpLost <= baseline.ProjectedBattleHpLost)
        {
            _pendingCompleteBaseline = null;
            return result;
        }
        _pendingCompleteBaseline = null;
        int increase = result.Outcome.ProjectedBattleHpLost - baseline.ProjectedBattleHpLost;
        ModLog.Warn(
            $"[Sts2Solver] CONTROLLER_RECALC_WORSENED original_turn={baseline.StartTurnNumber} " +
            $"current_turn={result.StartTurn} previous={baseline.ProjectedBattleHpLost} " +
            $"current={result.Outcome.ProjectedBattleHpLost} increase={increase} " +
            baseline.StateDifference);
        return result with
        {
            PreviousProjectedBattleHpLost = baseline.ProjectedBattleHpLost,
            ProjectedBattleHpLossIncrease = increase,
            RecalculatedAfterCompleteProjection = true,
        };
    }

    private static bool ResultStampMatches(CombatControllerResult result)
        => SearchStampMatches(result.Stamp);

    private static bool SearchStampMatches(CombatStateStamp stamp)
    {
        if (!CanSolve(out _) || _state == null || _player == null || _rules == null || _tracker == null)
            return false;
        try
        {
            _tracker.Refresh(_state);
            Battle live = _tracker.ImportRoot(_state, _player, _rules);
            return stamp.Matches(live);
        }
        catch (UnsupportedImportException)
        {
            return false;
        }
        catch (UnsupportedException)
        {
            return false;
        }
        catch (CapacityException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool SearchAlreadyOwned(int turn)
    {
        if (_latestResult?.StartTurn == turn || _turnSetupResult?.StartTurn == turn
            || _lastSolverDeployedTurn == turn)
            return true;
        if (_search is { } search && ReferenceEquals(search.State, _state) && search.StartTurn == turn)
            return true;
        if (_deployment is { } deployment && ReferenceEquals(deployment.State, _state)
            && deployment.Turn == turn)
        {
            return true;
        }
        return false;
    }

    private static void PublishSearchSetupFailure(string code, string detail)
    {
        _latestResult = null;
        _latestStamp = null;
        _continuationSource = null;
        _finishedSearch = null;
        _errorCode = code;
        _errorDetail = detail;
        _phase = CombatUiPhase.Failed;
        _pauseCode = null;
        _pendingCompleteBaseline = null;
        PublishSnapshot();
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_SETUP_FAILED code={code} {detail}");
    }

    private static string ReasonName(CombatSearchReason reason) => reason switch
    {
        CombatSearchReason.Manual => "manual",
        CombatSearchReason.AutoTurnStart => "auto_turn_start",
        CombatSearchReason.Deploy => "deploy",
        CombatSearchReason.FullAuto => "full_auto",
        CombatSearchReason.DeploymentDrift => "deployment_drift",
        CombatSearchReason.PlanExhausted => "plan_exhausted",
        CombatSearchReason.TurnSetup => "turn_setup",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static void AddSearchLog(CombatSearchSession session, CombatSearchRun run, string status)
    {
        CombatSearchResult outcome = run.Outcome;
        SearchLog.Add(new CombatPilotSearchEntry(
            session.StartTurn, ReasonName(session.Reason), status,
            outcome.ElapsedMilliseconds, outcome.TotalExpandedNodes,
            run.Beam?.BestRoute.Length ?? 0, run.Beam?.BestIsVictory ?? false,
            run.ErrorDetail, outcome.ManagedHeapPeakBytes, outcome.WorkingSetPeakBytes,
            outcome.SoftTimeRisk, outcome.MemoryBudgetExceeded, outcome.MemoryExceededAtBytes,
            outcome.PoolCapacityDropped, outcome.SearchMemoryUsedBytes,
            outcome.SearchMemoryPeakBytes, outcome.RouteNodeStorageBytes,
            outcome.RouteNodePeakCount)
        {
            SelectedMember = outcome.SelectedMember,
            SearchStatus = outcome.Status,
            Members = outcome.Members,
            MembersRun = outcome.MembersRun,
            MembersSkippedByTime = outcome.MembersSkippedByTime,
        });
    }
}
