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
    private static void RequestSearch(CombatSearchReason reason, bool deployWhenReady,
        SearchResumeState? resume = null, SearchPlan? resumePlan = null)
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
            && reason is not CombatSearchReason.PlanExhausted)
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
                allowDeployment: reason is CombatSearchReason.PlanExhausted))
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
        bool continuation = resume != null;
        InvalidateSearchContinuation(continuation ? "consumed" : "new_search");
        if (reason == CombatSearchReason.AutoTurnStart && _continuationSource != null
            && TryReuseContinuation(turn, deployWhenReady, out _))
        {
            return;
        }
        if (reason != CombatSearchReason.AutoTurnStart)
            _continuationSource = null;
        if (_search != null)
            CancelSearch();
        _rejectCode = null;
        if (!TryImportSearchRoot(out Battle root, out string? importCode, out string? importDetail))
        {
            PublishSearchSetupFailure(importCode!, importDetail!);
            return;
        }
        CombatStateStamp stamp = CombatStateStamp.Capture(root);
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_combatCancellation.Token);
        _searchTier = SolverSettings.Current.Search.Tier;
        _searchThreads = SearchThreadCount.Value;
        _searchProfile = CaptureSearchProfile();
        SearchSettingsSnapshot settings = SearchSettingsSnapshot.Capture(_searchTier, _theftPolicy);
        CombatSearchSession session = new(++_nextSearchGeneration, _state, in root, stamp, turn,
            reason, SolverSearchPolicy.Capture(_theftPolicy), deployWhenReady, cancellation)
        {
            Settings = settings,
            Resume = resume,
            ResumePlan = resumePlan,
        };
        session.ReviewedWorldlinesBeforeSearch = ReviewedWorldlinesSoFar();
        session.BattleHpLostBeforeSearch = root.Counter(Counter.CumulativeHpLost);
        _search = session;
        _searchedTurn = turn;
        // A new search replaces the route the run paused on; its route gets the normal checks.
        _runPaused = false;
        _displayRoute = null;
        _displayCurrentTurn = null;
        if (!continuation)
        {
            _latestResult = null;
            _latestStamp = null;
        }
        _pendingCompleteBaseline = reason == CombatSearchReason.AutoTurnStart
            ? _pendingCompleteBaseline
            : null;
        _searchStartedAt = Environment.TickCount64;
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
        _finishedSearch = null;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH_REQUEST generation={session.Generation} turn={turn} " +
            $"reason={reason} deploy_when_ready={deployWhenReady} tier={_searchTier} " +
            $"threads={_searchThreads} auto_search={_autoSearch} run={_runCombat} " +
            $"continuation={continuation}" +
            (continuation ? $" pass={resume!.Pass} next_member={resume.NextMemberIndex}" : ""));
        _ = RunSearchAsync(session);
    }

    /// <summary>
    /// Imports the current live root with the same seeding the search request uses. The caller
    /// publishes the returned setup failure code; both the fresh and the continuation path share it.
    /// </summary>
    private static bool TryImportSearchRoot(out Battle root, out string? code, out string? detail)
    {
        root = default;
        code = null;
        detail = null;
        try
        {
            if (!_trackerSeeded)
                SeedTracker();
            _tracker!.Refresh(_state!);
            root = _tracker.ImportRoot(_state!, _player!, _rules!);
            return true;
        }
        catch (UnsupportedImportException error)
        {
            code = "import_failed";
            detail = $"reason={error.Reason} {error.Detail}";
        }
        catch (UnsupportedException error)
        {
            code = "import_unsupported";
            detail = error.Message;
        }
        catch (CapacityException error)
        {
            code = "import_capacity";
            detail = CombatFailureDetail.Capacity(error,
                error.Region == "Cards" ? CountCombatCards() : null);
        }
        catch (InvalidDataException error)
        {
            code = "invalid_data";
            detail = error.Message;
        }
        catch (InvalidOperationException error)
        {
            code = "invalid_operation";
            detail = error.Message;
        }
        return false;
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

    private static void SeedTracker()
    {
        Battle seed = _tracker!.ImportRoot(_state!, _player!, _rules!);
        _tracker.Observe(seed);
        _trackerSeeded = true;
    }

    private static async Task RunSearchAsync(CombatSearchSession session)
    {
        NGame? host = NGame.Instance;
        if (host == null)
            return;
        try
        {
            CombatSearchRun run = await CombatSearchRunner.RunAsync(host, _testRoot ?? "manual",
                _rules!, _tracker!, session.State, _player!, session.Root, session.StartTurn, session.Reason,
                _searchTier, session.PolicyConfiguration,
#if STS2_SOLVER_TESTING
                SearchPolicyOverrideForTest,
#else
                null,
#endif
                session.Cancellation.Token, session.Resume, session.ResumePlan);
            CombatLogRecorder.RecordSearch(run, setup: false, session.StartTurn,
                ReasonName(session.Reason), resumed: session.Resume != null);
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
            CountCancelledSearch();
            InvalidateSearchContinuation("search_cancelled");
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
            FailSearch(session, "capacity", CombatFailureDetail.Capacity(error));
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

    /// <summary>Card count across the live combat piles, used only to explain a Cards capacity.</summary>
    private static int? CountCombatCards()
    {
        if (_player?.PlayerCombatState is not { } state)
            return null;
        return state.Hand.Cards.Count + state.DrawPile.Cards.Count
            + state.DiscardPile.Cards.Count + state.ExhaustPile.Cards.Count
            + state.PlayPile.Cards.Count;
    }

    private static void FailSearch(CombatSearchSession session, string code, string detail)
    {
        if (!ReferenceEquals(_search, session))
            return;
        _search = null;
        InvalidateSearchContinuation("search_failed");
        _finishedSearch = BuildFinishedSearch(session, null, "failed", code);
        _errorCode = code;
        _errorDetail = detail;
#if STS2_SOLVER_TESTING
        RecordUnrecoveredFailureForTest(code, detail);
#endif
        _pendingCompleteBaseline = null;
        if (RecordConsecutiveFailure(session.StartTurn, code, detail))
            return;
        _phase = CombatUiPhase.Failed;
        PublishSnapshot();
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_FAILED turn={session.StartTurn} code={code} {detail}");
    }

    private static bool TryReuseContinuation(int turn, bool deployWhenReady,
        out bool stoppedForDeviation)
    {
        stoppedForDeviation = false;
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
                PotionsUsedSoFar = PotionsUsedSoFar(),
            };
            _latestResult = reused;
            _latestStamp = reuseStamp;
            _currentActionIndex = 0;
            _completedActions = 0;
            _actionRunning = false;
            _reusedTurns++;
            _pauseCode = null;
            _pauseReason = null;
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
            _phase = CombatUiPhase.WaitingTurn;
            _finishedSearch = null;
            PublishSnapshot();
            _searchedTurn = turn;
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_REUSE turn={turn} source_turn={source.StartTurn} " +
                $"remaining={trimmed.Actions.Length}");
            if (_runCombat)
                StartFullAutoDeployment(reused);
            else if (deployWhenReady)
                StartDeployment(reused);
            return true;
        }

        string difference;
        if (expected is { } expectedAction)
        {
            string[] diff = CombatRouteDecision.BoundaryDiff(expectedAction.Before, live);
            CombatLogRecorder.RecordDeviation("boundary", turn, expectedAction.Index, diff,
                expectedAction.Before, live);
            difference = string.Join(" | ", diff);
        }
        else
        {
            difference = "field=continuation expected={cached_turn_missing} actual={live_turn_present}";
        }
        // A normal continuation is not a boundary deviation when the previous deployment was
        // taken over by this turn's setup page: the setup result owns that boundary. A validated
        // setup continuation is different: its Play comparison is the post-validation check the
        // player-visible plan must still pass.
        bool followedBySolver = _lastSolverDeployedTurn == turn - 1
            && (_lastTurnSupersededBySetup != turn - 1 || _continuationSetupTurn == turn);
#if STS2_SOLVER_TESTING
        if (_continuationSetupTurn == turn)
        {
            ContinuationReuseMissesForTest++;
            ContinuationReuseMissDetailForTest = difference;
        }
#endif
        if (expected is not null && followedBySolver)
        {
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_REUSE_MISS turn={turn} source_turn={source.StartTurn} " +
                $"followed_by_solver=true boundary_deviation=true {difference}");
            PauseForTurnStop(turn, "boundary_deviation", difference, countDeviation: true);
            stoppedForDeviation = true;
            return true;
        }
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
            _continuationSetupTurn = null;
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_REUSE_MISS turn={turn} source_turn={source.StartTurn} " +
            $"followed_by_solver={followedBySolver} {difference}");
        return false;
    }

    private static void CompleteSearch(CombatSearchSession session, CombatSearchRun run)
    {
        if (run.Plan == null || run.Beam == null || run.Outcome.Beam == null)
        {
            InvalidateSearchContinuation("search_failed");
            _finishedSearch = BuildFinishedSearch(session, run, "failed",
                run.ErrorCode ?? "search_failed");
            _errorCode = run.ErrorCode ?? "search_failed";
            _errorDetail = run.ErrorDetail;
#if STS2_SOLVER_TESTING
            RecordUnrecoveredFailureForTest(_errorCode, _errorDetail);
#endif
            _phase = session.StopRequested ? CombatUiPhase.Paused : CombatUiPhase.Failed;
            if (session.StopRequested)
                _pauseCode = "stopped";
            AddSearchLog(session, run, "failed");
            ModLog.Error(
                $"[Sts2Solver] CONTROLLER_SEARCH_FAILED turn={session.StartTurn} " +
                $"code={_errorCode} {_errorDetail}");
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
            InvalidateSearchContinuation("search_stale");
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

#if STS2_SOLVER_TESTING
        ClearUnrecoveredFailureForTest();
#endif
        CombatControllerResult result = new(run.Plan, run.Plan.Outcome, session.Stamp,
            run.FinalProgress?.CandidateVersion ?? 0, run.Outcome.Adoption, 0, 0, false,
            run.Tier, run.Outcome.ElapsedMilliseconds, run.Outcome.TotalExpandedNodes,
            run.Outcome, run.MemberRuns, ReasonName(session.Reason),
            run.Plan.Root.Counter(Sts2Solver.Simulation.Combat.Counter.CumulativeHpLost),
            CapacityDroppedRegions: run.Outcome.CapacityDroppedRegions,
            PotionsUsedSoFar: PotionsUsedSoFar());
        if (session.StopRequested)
        {
            _names ??= CombatDisplayNames.Capture(_state!);
            _latestResult = result;
            _latestStamp = result.Stamp;
            _continuationSource = result;
            _searchContinuation = BuildSearchContinuation(session, run, result);
            _finishedSearch = BuildFinishedSearch(session, run, "stopped", null);
            _pauseCode = "stopped";
            _pauseReason = "stopped_by_user";
            _automaticSearchPaused = true;
            _automaticSearchPausedTurn = session.StartTurn;
            _phase = CombatUiPhase.WaitingTurn;
            AddSearchLog(session, run, "stopped");
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_STOPPED_RESULT turn={result.StartTurn} " +
                $"candidate={result.CandidateVersion} actions={result.Plan.Actions.Length} " +
                $"resume={(run.Outcome.Resume != null ? "yes" : "no")}");
            return;
        }

        InvalidateSearchContinuation("search_completed");
#if STS2_SOLVER_TESTING
        LastResumeMergeForTest = run.Outcome.ResumeMerge;
#endif
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
            $"combat_ended_turn={result.Outcome.CombatEndedTurn?.ToString() ?? "-"} " +
            $"projected_hp_lost={result.Outcome.ProjectedBattleHpLost} " +
            $"increase={result.ProjectedBattleHpLossIncrease} adopted={result.Adoption?.Kind.ToString() ?? "-"} " +
            $"resume_merge={run.Outcome.ResumeMerge?.Reason ?? "-"}");
        if (session.DeployWhenReady)
            StartDeployment(result);
        else if (_runCombat)
            StartFullAutoDeployment(result);
    }

    /// <summary>
    /// Keeps the host-only resume point when a stop left members to run. Setup searches and a stop
    /// at the last member of the last pass have nothing to continue.
    /// </summary>
    private static CombatSearchContinuation? BuildSearchContinuation(CombatSearchSession session,
        CombatSearchRun run, CombatControllerResult result)
    {
        if (session.IsTurnSetup || run.Outcome.Resume is not { } resume
            || run.Case is not { } request || run.RequestPlan is not { } plan
            || session.Settings is not { } settings)
        {
            return null;
        }
        byte[] bytes = SearchCodec.EncodeRequest(request);
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_CONTINUATION_READY turn={session.StartTurn} " +
            $"pass={resume.Pass} next_member={resume.NextMemberIndex} " +
            $"stopped_member={resume.NextMemberIndex - 1} adopted_member={resume.StoppedIndex} " +
            $"members={resume.Members.Length} request_bytes={bytes.Length}");
        return new CombatSearchContinuation(resume, bytes, session.Stamp, settings, plan,
            session.StartTurn, result);
    }

    /// <summary>
    /// Cheap display check: the retained result is still the stop result and every captured search
    /// input is unchanged. The state stamp is checked separately by the caller's snapshot refresh.
    /// </summary>
    private static bool ContinuationInputsMatch(CombatSearchContinuation continuation)
        => ReferenceEquals(_latestResult, continuation.Result)
            && continuation.Settings.Matches(SearchSettingsSnapshot.Capture(
                SolverSettings.Current.Search.Tier, _theftPolicy));

    /// <summary>
    /// Exact continuation check: recompile the request from the current root and settings and
    /// compare it byte for byte with the request that stopped. Any difference logs the section that
    /// changed and makes the caller start over from the first member.
    /// </summary>
    private static bool TryContinueSearch(CombatSearchContinuation continuation,
        out SearchResumeState resume, out SearchPlan plan)
    {
        resume = continuation.Resume;
        plan = continuation.Plan;
        if (!ContinuationInputsMatch(continuation))
        {
            LogContinuationReject("inputs_changed");
            return false;
        }
        if (!TryImportSearchRoot(out Battle root, out string? code, out string? detail))
        {
            LogContinuationReject($"import:{code} {detail}");
            return false;
        }
        if (!continuation.Stamp.Matches(root))
        {
            LogContinuationReject("state_changed");
            return false;
        }
        SearchCase current;
        try
        {
            current = CompileContinuationRequest(root);
        }
        catch (UnsupportedImportException error)
        {
            LogContinuationReject($"compile:reason={error.Reason} {error.Detail}");
            return false;
        }
        catch (UnsupportedException error)
        {
            LogContinuationReject($"compile:{error.Message}");
            return false;
        }
        catch (CapacityException error)
        {
            LogContinuationReject($"compile:region={error.Region} {error.Message}");
            return false;
        }
        catch (InvalidDataException error)
        {
            LogContinuationReject($"compile:{error.Message}");
            return false;
        }
        catch (InvalidOperationException error)
        {
            LogContinuationReject($"compile:{error.Message}");
            return false;
        }
        byte[] bytes = SearchCodec.EncodeRequest(current);
        if (bytes.AsSpan().SequenceEqual(continuation.RequestBytes))
            return true;
        string part = DescribeRequestDifference(current, continuation.RequestBytes);
#if STS2_SOLVER_TESTING
        LastContinuationMismatchForTest = part;
#endif
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH_CONTINUE_MISMATCH part={part} " +
            $"turn={CurrentTurn()} restart=from_scratch");
        return false;
    }

    private static SearchCase CompileContinuationRequest(in Battle root)
    {
        SearchCase searchCase = SearchSession.CompileCase(_state!, _player!, _rules!, root,
            SolverSearchPolicy.Capture(_theftPolicy),
#if STS2_SOLVER_TESTING
            SearchPolicyOverrideForTest,
#else
            null,
#endif
            deterministic: false, SolverSettings.Current.Search.Tier);
        searchCase = searchCase with
        {
            Options = searchCase.Options with { SearchThreads = SearchThreadCount.Value },
        };
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.TamperContinuationCompileForTest is { } tamper)
            searchCase = tamper(searchCase);
#endif
        return searchCase;
    }

    /// <summary>Names the request section that differs, for the restart log.</summary>
    private static string DescribeRequestDifference(SearchCase current, byte[] stoppedBytes)
    {
        SearchCase stopped;
        try
        {
            stopped = SearchCodec.DecodeRequest(stoppedBytes);
        }
        catch (InvalidDataException)
        {
            return "stopped_request_undecodable";
        }
        if (!RulesCodec.Encode(current.Rules).AsSpan().SequenceEqual(RulesCodec.Encode(stopped.Rules)))
            return "rules";
        if (current.Battles.Length != stopped.Battles.Length)
            return "roots";
        for (int index = 0; index < current.Battles.Length; index++)
        {
            if (!BattleCodec.Encode(current.Battles[index]).AsSpan()
                    .SequenceEqual(BattleCodec.Encode(stopped.Battles[index])))
                return $"root[{index}]";
        }
        BeamOptions left = current.Options, right = stopped.Options;
        if (left.BeamWidth != right.BeamWidth) return "options.beam_width";
        if (left.MaxExpandedNodes != right.MaxExpandedNodes) return "options.max_expanded_nodes";
        if (left.MaxTurns != right.MaxTurns) return "options.max_turns";
        if (left.SoftTimeBudget != right.SoftTimeBudget) return "options.soft_time";
        if (left.EffectivePotionPolicy != right.EffectivePotionPolicy)
            return "options.potion_policy";
        if (left.AuditPotionPolicy != right.AuditPotionPolicy) return "options.audit_pot";
        if (left.TheftPolicy != right.TheftPolicy) return "options.theft_policy";
        if (left.InitialPlayerTurn != right.InitialPlayerTurn) return "options.initial_turn";
        if (left.Act3BossStrategy != right.Act3BossStrategy) return "options.act3_boss";
        if (left.BrightestFlameMaxHpLossLimit != right.BrightestFlameMaxHpLossLimit)
            return "options.brightest_flame_limit";
        if (left.SearchMemoryBudgetBytes != right.SearchMemoryBudgetBytes)
            return "options.memory_budget";
        return "options";
    }

    private static void LogContinuationReject(string reason)
        => ModLog.Info(
            $"[Sts2Solver] CONTROLLER_SEARCH_CONTINUE_REJECTED reason={reason} turn={CurrentTurn()}");

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
#if STS2_SOLVER_TESTING
        RecordUnrecoveredFailureForTest(code, detail);
#endif
        _phase = CombatUiPhase.Failed;
        _pauseCode = null;
        _pendingCompleteBaseline = null;
        PublishSnapshot();
        ModLog.Error($"[Sts2Solver] CONTROLLER_SEARCH_SETUP_FAILED code={code} {detail}");
    }

    internal static string ReasonName(CombatSearchReason reason) => reason switch
    {
        CombatSearchReason.Manual => "manual",
        CombatSearchReason.AutoTurnStart => "auto_turn_start",
        CombatSearchReason.Deploy => "deploy",
        CombatSearchReason.AutoSearchSwitch => "auto_search_switch",
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
