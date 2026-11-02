// Command surface of the controller: mode, search, stop and execute. All commands run on the game
// main thread and return false when the button would be disabled.
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
        bool changed = _mode != mode;
        _mode = mode;
        if (mode != SolverMode.FullAuto)
        {
            if (_fullAutoEnabled)
                ModLog.Info($"[Sts2Solver] CONTROLLER_FULL_AUTO_OFF source=mode mode={mode}");
            _fullAutoEnabled = false;
            if (_fullAutoPausedThisCombat)
            {
                _fullAutoPausedThisCombat = false;
                _pauseCode = null;
                _pauseReason = null;
            }
            if (mode == SolverMode.Manual)
                CancelTurnSchedule();
            if (_search == null && _deployment == null && _pauseCode == null)
                _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            if (mode == SolverMode.AutoSearch && changed
                && _search == null && _deployment == null
                && !_automaticSearchPaused)
            {
                TrySearchOnModeSwitch(CombatSearchReason.AutoTurnStart);
            }
            return true;
        }

        bool resumePaused = _fullAutoPausedThisCombat;
        _fullAutoPausedThisCombat = false;
        _pauseCode = null;
        _pauseReason = null;
        _rejectCode = null;
        if (resumePaused)
        {
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
        }
        _fullAutoEnabled = CanArmFullAuto();
        if (!_fullAutoEnabled)
        {
            ModLog.Info("[Sts2Solver] CONTROLLER_FULL_AUTO_NOT_ARMED reason=combat_not_eligible");
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return true;
        }
        if (_search != null || _deployment != null)
        {
            PublishSnapshot();
            return true;
        }
        if (!_automaticSearchPaused
            && _latestResult is { } result && ResultStampMatches(result))
        {
            StartFullAutoDeployment(result);
        }
        else if (!_automaticSearchPaused)
        {
            TrySearchOnModeSwitch(CombatSearchReason.FullAuto);
        }
        if (_search == null && _deployment == null && _phase != CombatUiPhase.Searching
            && _phase != CombatUiPhase.Deploying && _pauseCode == null)
        {
            _phase = CombatUiPhase.WaitingTurn;
        }
        PublishSnapshot();
        return true;
    }

    internal static bool StartOrRecalculateSearch()
    {
        RequireMainThread();
        if (IsMultiplayerSession || _search != null || _deployment != null
            || SearchSession.IsRunning)
            return false;
        if (!CanSolve(out string rejection))
        {
            _rejectCode = rejection;
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_REJECT reason={rejection}");
            return false;
        }
        RequestSearch(CombatSearchReason.Manual, deployWhenReady: false);
        return true;
    }

    internal static bool StopSearch()
    {
        RequireMainThread();
        if (IsMultiplayerSession || _search is not { } search || search.StopRequested)
            return false;
        _automaticSearchPaused = true;
        _automaticSearchPausedTurn = CurrentTurn();
        _pauseCode = "stopped";
        _pauseReason = "stopped_by_user";
        _phase = CombatUiPhase.Searching;
        search.StopRequested = true;
        search.ApplyCurrentTurn = false;
        search.DeployWhenReady = false;
        if (!SearchSession.RequestStop())
        {
            search.Cancellation.Cancel();
            SearchSession.Cancel("user");
        }
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_REQUEST turn={search.StartTurn}");
        return true;
    }

    internal static bool StopDeployment()
    {
        RequireMainThread();
        if (IsMultiplayerSession || _deployment is not { } deployment || deployment.StopRequested)
            return false;
        deployment.StopRequested = true;
        _automaticSearchPaused = true;
        _automaticSearchPausedTurn = CurrentTurn();
        _pauseCode = "stopped";
        _pauseReason = "stopped_by_user";
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_DEPLOYMENT turn={deployment.Turn}");
        return true;
    }

    internal static bool ExecuteCurrentTurn()
    {
        RequireMainThread();
        if (IsMultiplayerSession || _deployment != null)
            return false;
        if (_search is { } search)
        {
            if (search.StopRequested || search.ApplyCurrentTurn
                || search.RenderedCandidateVersion <= 0
                || _lastProgress?.CurrentTurn == null
                || _lastProgress.Route?.OnlyDeathRoutes == true)
            {
                return false;
            }
            _automaticSearchPaused = true;
            _automaticSearchPausedTurn = CurrentTurn();
            search.ApplyCurrentTurn = true;
            search.StopRequested = true;
            search.DeployWhenReady = true;
            _pauseCode = "stopped";
            _pauseReason = "execute_current_turn";
            _phase = CombatUiPhase.Searching;
            if (!SearchSession.RequestStop())
            {
                search.ApplyCurrentTurn = false;
                search.StopRequested = false;
                search.DeployWhenReady = false;
                _automaticSearchPaused = false;
                _automaticSearchPausedTurn = null;
                _pauseCode = null;
                _pauseReason = null;
                return false;
            }
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_EXECUTE_CURRENT_TURN turn={search.StartTurn} " +
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
        ModLog.Info("[Sts2Solver] CONTROLLER_EXECUTE_REJECT reason=no_current_turn_route");
        return false;
    }

    /// <summary>Starts the mode-switch search without replacing a route that is still current.</summary>
    private static void TrySearchOnModeSwitch(CombatSearchReason reason)
    {
        if (_latestResult is { } result && ResultStampMatches(result))
            return;
        if (!CanSolve(out _))
            return;
        RequestSearch(reason, deployWhenReady: false);
    }
}
