// Command surface of the controller: mode, solver switch, search, stop, adopt and execute. All
// commands run on the game main thread and return false when the button would be disabled.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    internal static bool SetMode(SolverMode mode, bool persist = true)
    {
        RequireMainThread();
        if (!Enum.IsDefined(mode))
            return false;
        if (mode == SolverMode.FullAuto && IsMultiplayerSession)
            return false;
        if (persist && SolverSettings.Current.Automation.Mode != mode
            && !SolverSettings.Update(current => current with
            {
                Automation = current.Automation with { Mode = mode },
            }))
        {
            return false;
        }
        _mode = mode;
        if (mode != SolverMode.FullAuto)
        {
            if (_fullAutoEnabled)
                ModLog.Info($"[Sts2Solver] CONTROLLER_FULL_AUTO_OFF source=mode mode={mode}");
            _fullAutoEnabled = false;
            if (mode == SolverMode.Manual)
                CancelTurnSchedule();
            if (_search == null && _deployment == null && _pauseCode == null)
                _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return true;
        }

        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        _pauseCode = null;
        _pauseReason = null;
        _rejectCode = null;
        _fullAutoEnabled = CanArmFullAuto();
        if (!_fullAutoEnabled)
        {
            ModLog.Info("[Sts2Solver] CONTROLLER_FULL_AUTO_NOT_ARMED reason=combat_not_eligible");
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return true;
        }
        if (_search == null && _deployment == null && CanSolve(out _))
        {
            if (_latestResult != null && ResultStampMatches(_latestResult))
                StartFullAutoDeployment(_latestResult);
            else
                RequestSearch(CombatSearchReason.FullAuto, deployWhenReady: false);
        }
        PublishSnapshot();
        return true;
    }

    internal static bool SetSolverEnabled(bool enabled, bool persist = true)
    {
        RequireMainThread();
        bool disabled = !enabled;
        if (persist && SolverSettings.Current.Automation.SolverDisabled != disabled
            && !SolverSettings.Update(current => current with
            {
                Automation = current.Automation with { SolverDisabled = disabled },
            }))
        {
            return false;
        }
        _solverDisabled = disabled;
        ModLog.Info($"[Sts2Solver] CONTROLLER_SOLVER enabled={enabled}");
        if (disabled)
        {
            CancelTurnSchedule();
            DisableFullAuto(persistMode: true);
            CancelSearch();
            CancelDeployment();
            _latestResult = null;
            _latestStamp = null;
            _stoppedResult = null;
            _continuationSource = null;
            _pendingCompleteBaseline = null;
            _pauseCode = null;
            _pauseReason = null;
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return true;
        }

        _pauseCode = null;
        _pauseReason = null;
        _rejectCode = null;
        _fullAutoEnabled = _mode == SolverMode.FullAuto && CanArmFullAuto();
        if (_fullAutoEnabled && _search == null && _deployment == null && CanSolve(out _))
        {
            if (_latestResult != null && ResultStampMatches(_latestResult))
                StartFullAutoDeployment(_latestResult);
            else
                RequestSearch(CombatSearchReason.FullAuto, deployWhenReady: false);
        }
        else if (!_fullAutoEnabled && _mode == SolverMode.AutoSearch
            && _search == null && _deployment == null && CanSolve(out _))
        {
            _ = RequestSearchWhenIdleAsync(CombatSearchReason.AutoTurnStart);
        }
        _phase = CombatUiPhase.WaitingTurn;
        PublishSnapshot();
        return true;
    }

    internal static bool StartOrRecalculateSearch()
    {
        RequireMainThread();
        if (_search != null || _deployment != null || SearchSession.IsRunning
            || !CanSolve(out _))
        {
            return false;
        }
        RequestSearch(CombatSearchReason.Manual, deployWhenReady: false);
        return true;
    }

    internal static bool StopSearch()
    {
        RequireMainThread();
        if (_search is not { } search || _solverDisabled)
            return false;
        DisableFullAuto(persistMode: true);
        _automaticSearchPaused = true;
        _automaticSearchPausedTurn = CurrentTurn();
        _pauseCode = "stopped";
        _pauseReason = "stopped_by_user";
        _phase = CombatUiPhase.Searching;
        if (search.RenderedCandidateVersion > 0
            && SearchSession.RequestAdopt(search.RenderedCandidateVersion))
        {
            search.StopRequested = true;
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_STOP_PRESERVE turn={search.StartTurn} " +
                $"candidate={search.RenderedCandidateVersion}");
        }
        else
        {
            ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_CANCEL turn={search.StartTurn}");
            search.Cancellation.Cancel();
            SearchSession.Cancel("user");
        }
        PublishSnapshot();
        return true;
    }

    internal static bool AdoptCurrentRoute()
    {
        RequireMainThread();
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        if (_search is { } search)
        {
            if (search.RenderedCandidateVersion <= 0
                || !SearchSession.RequestAdopt(search.RenderedCandidateVersion))
            {
                return false;
            }
            search.StopRequested = false;
            search.DeployWhenReady = false;
            search.AdoptRequested = true;
            _pauseCode = null;
            _pauseReason = null;
            _phase = CombatUiPhase.Searching;
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_ADOPT turn={search.StartTurn} " +
                $"candidate={search.RenderedCandidateVersion}");
            return true;
        }
        return TryAdoptStoppedRoute();
    }

    internal static bool ExecuteOrApplyCurrentTurn()
    {
        RequireMainThread();
        if (_deployment != null || _solverDisabled)
            return false;
        if (_search is { } search)
        {
            if (search.RenderedCandidateVersion <= 0 || _lastProgress?.CurrentTurn == null)
                return false;
            DisableFullAuto(persistMode: true);
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
            if (!SearchSession.RequestAdopt(search.RenderedCandidateVersion))
                return false;
            search.ApplyCurrentTurn = true;
            search.DeployWhenReady = true;
            search.StopRequested = false;
            search.AdoptRequested = false;
            _pauseCode = null;
            _pauseReason = null;
            _phase = CombatUiPhase.Searching;
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_APPLY_CURRENT_TURN turn={search.StartTurn} " +
                $"candidate={search.RenderedCandidateVersion}");
            return true;
        }
        if (_latestResult is { } result && ResultStampMatches(result))
        {
            StartDeployment(result);
            return true;
        }
        if (!CanSolve(out string rejection))
        {
            ModLog.Info($"[Sts2Solver] CONTROLLER_EXECUTE_REJECT reason={rejection}");
            return false;
        }
        RequestSearch(CombatSearchReason.Deploy, deployWhenReady: true);
        return true;
    }

    internal static bool SetFullAuto(bool enabled)
    {
        RequireMainThread();
        if (!enabled)
        {
            DisableFullAuto(persistMode: true);
            PublishSnapshot();
            return true;
        }
        return SetMode(SolverMode.FullAuto);
    }

    private static bool TryAdoptStoppedRoute()
    {
        if (_stoppedResult is not { } stopped || stopped.Plan.Actions.Length == 0
            || _state == null || _player == null || _rules == null || _tracker == null
            || !CanSolve(out _))
        {
            return false;
        }
        Battle live;
        try
        {
            live = _tracker.ImportForComparison(_state, _player, _rules,
                stopped.Plan.Actions[0].Before);
        }
        catch (UnsupportedImportException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_ADOPT_IMPORT_FAILED reason={error.Reason} {error.Detail}");
            return false;
        }
        catch (UnsupportedException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_ADOPT_IMPORT_FAILED {error.Message}");
            return false;
        }
        catch (CapacityException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_ADOPT_IMPORT_FAILED region={error.Region} {error.Message}");
            return false;
        }
        catch (InvalidDataException error)
        {
            ModLog.Error($"[Sts2Solver] CONTROLLER_ADOPT_IMPORT_FAILED {error.Message}");
            return false;
        }
        bool current = stopped.Stamp.Matches(live);
        _stoppedResult = null;
        if (!current)
        {
            ModLog.Info("[Sts2Solver] CONTROLLER_ADOPT_STALE");
            PublishSnapshot();
            return false;
        }
        _tracker.Observe(live);
        _names ??= CombatDisplayNames.Capture(_state);
        _latestResult = stopped;
        _latestStamp = CombatStateStamp.Capture(live);
        _continuationSource = stopped;
        _pauseCode = null;
        _pauseReason = null;
        _phase = CombatUiPhase.WaitingTurn;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_STOPPED_ROUTE_ADOPTED turn={stopped.StartTurn} " +
            $"actions={stopped.Plan.Actions.Length}");
        return true;
    }
}
