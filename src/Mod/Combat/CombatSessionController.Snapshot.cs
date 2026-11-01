// Snapshot publication. The panel only ever reads these immutable records; every value is built
// on the game main thread from controller state, a frozen search progress snapshot or a replayed
// route.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    private static void PublishSnapshot()
    {
        if (!NGame.IsMainThread() || !CombatUiStore.Initialized)
            return;
        CombatUiSnapshot current = CombatUiStore.Snapshot;
        SearchMemoryBudgetResolution budget = SearchMemoryBudget.Resolve();
        int turn = CurrentTurn();
        int hp = _player?.Creature.CurrentHp ?? current.PlayerHp;
        int maxHp = _player?.Creature.MaxHp ?? current.PlayerMaxHp;
        bool stoppedAvailable = StoppedRouteAvailable();
        bool canExecute = !_solverDisabled && _deployment == null && _search == null
            && _latestResult != null && ResultStampMatches(_latestResult);
        bool canApply = !_solverDisabled && _search is { } applying
            && !applying.ApplyCurrentTurn
            && applying.RenderedCandidateVersion > 0
            && _lastProgress?.CurrentTurn != null;
        bool canAdopt = !_solverDisabled
            && ((_search is { } adopting && adopting.RenderedCandidateVersion > 0)
                || stoppedAvailable);
        CombatControllerResult? routeSource = _search == null
            ? _latestResult ?? _deploymentResult ?? _stoppedResult
            : null;
        CombatControllerResult? shown = _search == null ? _latestResult ?? _stoppedResult : null;
        CombatUiSnapshot next = new()
        {
            Phase = _phase,
            Root = current.Root.Length == 0 ? _testRoot ?? "" : current.Root,
            Mode = _mode,
            SolverDisabled = _solverDisabled,
            FullAutoEnabled = _fullAutoEnabled,
            AutomaticCalculationEnabled = _mode is SolverMode.AutoSearch or SolverMode.FullAuto,
            AutomaticSearchPaused = _automaticSearchPaused,
            AutomaticSearchPausedTurn = _automaticSearchPausedTurn,
            DeployWhenReady = _search?.DeployWhenReady ?? false,
            IsSearching = _search != null,
            IsDeploying = _deployment != null,
            IsApplyingCurrentTurn = _search?.ApplyCurrentTurn ?? false,
            IsAdoptingRoute = _search?.AdoptRequested ?? false,
            CanStartSearch = !_solverDisabled && _search == null && _deployment == null
                && !SearchSession.IsRunning && CanSolve(out _),
            CanStopSearch = !_solverDisabled && _search != null,
            CanExecuteCurrentTurn = canExecute,
            CanApplyCurrentTurn = canApply,
            CanAdoptCurrentRoute = canAdopt,
            StoppedRouteAvailable = stoppedAvailable,
            HasRetainedRoute = _latestResult != null || _continuationSource != null,
            LastSolverDeployedTurn = _lastSolverDeployedTurn,
            Result = shown == null ? null : ResultUi(shown),
            Turn = turn,
            PlayerHp = hp,
            PlayerMaxHp = maxHp,
            EndReason = EndReason(),
            PauseReason = _pauseReason,
            PauseCode = _pauseCode ?? (_automaticSearchPaused ? "stopped" : null),
            ErrorCode = _errorCode,
            ErrorDetail = _errorDetail,
            RejectCode = _rejectCode,
            Search = BuildSearchUi(),
            Route = BuildRouteUi(routeSource),
            Memory = current.Memory,
            Settings = CombatUiProjection.Settings(budget),
            Searches = _searches,
            ReusedTurns = _reusedTurns,
            Deviations = _deviations,
            DeploymentFailures = _deploymentFailures,
        };
        CombatUiStore.Publish(next);
    }

    private static string? EndReason() => _phase switch
    {
        CombatUiPhase.Completed => TerminalStatus() ?? "ended",
        CombatUiPhase.Paused => "paused",
        CombatUiPhase.Cancelled => "cancelled",
        CombatUiPhase.Failed => "failed",
        _ => null,
    };

    private static CombatResultUiSnapshot ResultUi(CombatControllerResult result) => new()
    {
        StartTurn = result.StartTurn,
        DeathTurn = result.Outcome.DeathTurn,
        CombatEndedTurn = result.Outcome.CombatEndedTurn,
        ProjectedBattleHpLost = result.Outcome.ProjectedBattleHpLost,
        ProjectedBattlePotionCount = result.Outcome.ProjectedBattlePotionCount,
        PreviousProjectedBattleHpLost = result.PreviousProjectedBattleHpLost,
        ProjectedBattleHpLossIncrease = result.ProjectedBattleHpLossIncrease,
        WasReused = result.WasReused,
        Victory = result.Outcome.Victory,
        OnlyDeathRoutes = result.Outcome.OnlyDeathRoutes,
        Outcome = OutcomeName(result.Outcome),
        AdoptionKind = result.Adoption?.Kind,
        CandidateVersion = result.CandidateVersion,
    };

    private static string OutcomeName(CombatRouteOutcome outcome)
        => outcome.Victory ? "victory"
            : outcome.DeathTurn.HasValue ? "defeat"
            : "incomplete";

    private static RouteUiSnapshot? BuildRouteUi(CombatControllerResult? result)
    {
        if (result == null || _names == null || _search != null)
            return null;
        return CombatUiProjection.Route(result.Plan, _names, CurrentTurn(),
            _currentActionIndex, _completedActions, result.WasReused);
    }

    private static SearchUiSnapshot? BuildSearchUi()
    {
        if (_search == null)
            return _finishedSearch;
        SearchProgressSnapshot? progress = _lastProgress;
        SearchUiPhase phase = _search.StopRequested ? SearchUiPhase.Stopping
            : _search.ApplyCurrentTurn ? SearchUiPhase.Adopting
            : SearchUiPhase.Running;
        return new SearchUiSnapshot
        {
            Phase = phase,
            Tier = _searchTier,
            Turn = _search.StartTurn,
            Reason = ReasonName(_search.Reason),
            SearchThreads = _searchThreads,
            StartedAtMilliseconds = _searchStartedAt,
            ActiveMemberRun = _activeMemberRun,
            ActiveMemberWidth = _activeMemberWidth,
            ActiveMemberBudgetMilliseconds = _activeMemberBudget,
            ActiveMemberStartedAtMilliseconds = _activeMemberStartedAt,
            BudgetMilliseconds = checked((long)_searchProfile.BaselineWidth
                * _searchProfile.MillisecondsPerWidth),
            ElapsedMilliseconds = progress?.ElapsedMilliseconds
                ?? Math.Max(0, Environment.TickCount64 - _searchStartedAt),
            ExpandedNodes = progress?.ExpandedNodes ?? 0,
            Transitions = _memberRuns.Sum(static run => (long)run.Transitions),
            CandidateVersion = progress?.CandidateVersion ?? 0,
            RenderedCandidateVersion = _search.RenderedCandidateVersion,
            StopCandidateAvailable = _search.RenderedCandidateVersion > 0,
            RoutePreview = progress?.Route == null ? null : RoutePreview(progress.Route),
            CurrentTurnPreview = progress?.CurrentTurn == null
                ? null
                : CurrentTurnPreview(progress.CurrentTurn),
            Status = "running",
            MemberRuns = _memberRuns,
        };
    }

    private static SearchRoutePreviewUi RoutePreview(SearchRoutePreview preview) => new()
    {
        CandidateVersion = preview.CandidateVersion,
        StartTurn = preview.StartTurn,
        ThroughTurn = preview.ThroughTurn,
        ActionCount = preview.Actions.Length,
        Victory = preview.Victory,
        OnlyDeathRoutes = preview.OnlyDeathRoutes,
        ProjectedBattleHpLost = preview.Turns.Sum(static turn => turn.HpLost),
        Turns = preview.Turns.Select(static turn => new SearchTurnPreviewUi
        {
            Turn = turn.Turn,
            ActionCount = turn.Actions.Length,
            HpLost = turn.HpLost,
            HpRecovered = turn.HpRecovered,
            EnemyHpLost = turn.EnemyHpLost,
            EnergyLeft = turn.EnergyLeft,
            CombatEnded = turn.CombatEnded,
        }).ToArray(),
    };

    private static SearchCurrentTurnPreviewUi CurrentTurnPreview(SearchCurrentTurnPreview preview)
        => new()
        {
            CandidateVersion = preview.CandidateVersion,
            Turn = preview.Turn,
            ActionCount = preview.Actions.Length,
            HpLost = preview.HpLost,
            HpRecovered = preview.HpRecovered,
            EnemyHpLost = preview.EnemyHpLost,
            EnergyLeft = preview.EnergyLeft,
            CombatEnded = preview.CombatEnded,
        };

    private static SearchUiSnapshot BuildFinishedSearch(CombatSearchSession session,
        CombatSearchRun? run, string status, string? error)
    {
        SearchPerformanceProfile profile = run?.Profile ?? _searchProfile;
        SearchPerformanceTier tier = run?.Tier ?? _searchTier;
        SearchUiSnapshot baseSnapshot;
        if (run != null)
        {
            baseSnapshot = CombatUiProjection.SearchFinished(run.Outcome, run.MemberRuns,
                session.StartTurn, ReasonName(session.Reason), tier, profile, error,
                _searchStartedAt);
        }
        else
        {
            baseSnapshot = new SearchUiSnapshot
            {
                Phase = SearchUiPhase.Cancelled,
                Tier = tier,
                Turn = session.StartTurn,
                Reason = ReasonName(session.Reason),
                StartedAtMilliseconds = _searchStartedAt,
                SearchThreads = _searchThreads,
                BudgetMilliseconds = checked((long)profile.BaselineWidth
                    * profile.MillisecondsPerWidth),
                ElapsedMilliseconds = Math.Max(0, Environment.TickCount64 - _searchStartedAt),
                Status = status,
                SearchStatus = status,
                ErrorCode = error,
                Victory = false,
            };
        }
        SearchProgressSnapshot? final = run?.FinalProgress ?? _lastProgress;
        return baseSnapshot with
        {
            Phase = status switch
            {
                "cancelled" => SearchUiPhase.Cancelled,
                "failed" or "stale" => SearchUiPhase.Failed,
                _ => SearchUiPhase.Completed,
            },
            Status = status,
            ErrorCode = error,
            CandidateVersion = final?.CandidateVersion ?? 0,
            RenderedCandidateVersion = session.RenderedCandidateVersion,
            StopCandidateAvailable = session.RenderedCandidateVersion > 0,
            RoutePreview = final?.Route == null ? null : RoutePreview(final.Route),
            CurrentTurnPreview = final?.CurrentTurn == null
                ? null
                : CurrentTurnPreview(final.CurrentTurn),
            MemberRuns = run?.MemberRuns ?? [],
        };
    }

    private static bool StoppedRouteAvailable()
    {
        if (_stoppedResult is not { } stopped || _search != null || _deployment != null)
            return false;
        return ResultStampMatches(stopped);
    }

    private static CombatPilotResult BuildPilotResult()
    {
        string status = TerminalStatus() ?? (_pauseCode != null ? "paused" : "in_progress");
        string outcome = status switch
        {
            "victory" => "victory",
            "defeat" => "defeat",
            "paused" => "in_progress",
            _ => "in_progress",
        };
        int turn = CurrentTurn();
        int hp = _player?.Creature.CurrentHp ?? 0;
        int maxHp = _player?.Creature.MaxHp ?? 0;
        return new CombatPilotResult(
            _testRoot ?? "manual", status, outcome, turn, hp, maxHp, [.. PotionsUsed],
            _searches, _reusedTurns, _deviations, _deploymentFailures,
            Math.Max(0, Environment.TickCount64 - _combatStartedAt), _pauseReason,
            [.. SearchLog], [.. DeviationLog], [.. FailureLog]);
    }
}
