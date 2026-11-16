// Presentation state machine ported from CombatSolver src/UI/SolverOverlay.cs
// (ShowSearching/ShowProgress/ShowResult/ShowDeploying/ShowDeploymentComplete/ShowWaitingForNextTurnPlan/
// ShowSearchStopped/ShowManualCalculationReady and the full-auto pause banners) at
// 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors). The controller methods are
// replaced by a dispatch over CombatUiSnapshot; the texts, tones, contexts and timing are the original ones.
using System.Globalization;

namespace Sts2Solver;

internal enum SolverOverlayPresentation
{
    Ready,
    Deploying,
    ExecutedHistory,
    Searching,
    TurnSetupPending,
    TurnSetupContinuation,
    TurnSetupDriving,
}

internal sealed partial class SolverOverlay
{
    private SolverOverlayPresentation _presentation = SolverOverlayPresentation.Searching;
    private bool _waitingForNextTurnPlan;
    private SolverOverlaySnapshot? _searchBestSnapshot;
    private int _lastSearchingTurn;
    private int _lastSearchStartedAt;
    private int _lastDeploymentTurn;
    private int _lastDeploymentActionCount;
    private bool _lastDeploymentEndedTurn;
    private SolverOverlaySnapshot? _deploymentOverlay;
    private int _lastResultCandidate = -1;
    private bool _routeStale;
    private bool _routeGuided;
    private string? _guideStatusContext;
    private bool _turnStartHighlighted;
    private long _lastProgressReviewedWorldlines;
    private long _lastProgressSampledAt;
    private long _reviewedWorldlinesBeforeSearch;
    private long _reviewedWorldlinesTarget;
    private double _shownReviewedWorldlines;
    private double _targetWorldlinesPerSecond;
    private double _shownWorldlinesPerSecond;

    private void Present(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot, long now)
    {
        ApplySummary(snapshot, now);
        PopulateRoute(overlay, snapshot);
        if (snapshot.Phase == CombatUiPhase.Searching && snapshot.Search is { } search)
        {
            if (_presentation != SolverOverlayPresentation.Searching
                || _lastSearchingTurn != search.Turn
                || _lastSearchStartedAt != (int)search.StartedAtMilliseconds)
            {
                ShowSearching(overlay, snapshot, search);
            }
            else
            {
                ShowProgress(overlay, snapshot, search);
            }
        }
        else if (snapshot.Phase == CombatUiPhase.Deploying)
        {
            if (_presentation != SolverOverlayPresentation.Deploying || _lastDeploymentTurn != snapshot.Turn)
                ShowDeploying(overlay, snapshot);
            else
                ShowDeploymentStep(overlay, snapshot);
        }
        else if (snapshot.Phase == CombatUiPhase.Paused && snapshot.PauseCode == "stopped")
        {
            ShowSearchStopped(snapshot);
        }
        else if (snapshot.Phase == CombatUiPhase.Paused)
        {
            ShowPaused(overlay, snapshot);
        }
        else if (snapshot.Phase == CombatUiPhase.Cancelled
                 || snapshot.AutomaticSearchPaused && !snapshot.IsSearching)
        {
            ShowSearchStopped(snapshot);
        }
        else if (snapshot.Phase == CombatUiPhase.Failed)
        {
            ShowMessage(SolverText.Get("ui.solver_message"),
                SolverPanelText.Error(snapshot.ErrorCode ?? "search_failed"),
                SolverPanelText.ErrorReason(snapshot.ErrorReason));
        }
        else if (snapshot.TurnSetupDriving)
        {
            ShowTurnSetupDriving(overlay, snapshot);
        }
        else if (snapshot.TurnSetupPageOpen && !snapshot.TurnSetupPlanReady)
        {
            ShowTurnSetupPending(snapshot);
        }
        else if (snapshot.TurnSetupContinuation && snapshot.Result is { } continuationResult)
        {
            ShowTurnSetupContinuation(overlay, snapshot, continuationResult);
        }
        else if (snapshot.Result is { } result)
        {
            if (_presentation == SolverOverlayPresentation.Deploying)
            {
                _lastDeploymentEndedTurn = snapshot.Turn > _lastDeploymentTurn
                    || snapshot.Phase == CombatUiPhase.Completed;
                ShowDeploymentComplete(overlay, snapshot, result);
                _waitingForNextTurnPlan = true;
            }
            else if (_presentation == SolverOverlayPresentation.ExecutedHistory && _waitingForNextTurnPlan)
            {
                ShowWaitingForNextTurnPlan(overlay, snapshot);
            }
            else if (_presentation != SolverOverlayPresentation.Ready
                     || _lastResultCandidate != result.CandidateVersion)
            {
                ShowResult(overlay, snapshot, result);
            }
            else if (snapshot.Route == null)
            {
                PopulateRoute(overlay, snapshot);
            }
        }
        else if (_presentation == SolverOverlayPresentation.Deploying)
        {
            _lastDeploymentEndedTurn = snapshot.Turn > _lastDeploymentTurn
                || snapshot.Phase == CombatUiPhase.Completed;
            ShowDeploymentComplete(overlay, snapshot, null);
            _waitingForNextTurnPlan = true;
        }
        else if (_presentation == SolverOverlayPresentation.ExecutedHistory && _waitingForNextTurnPlan)
        {
            ShowWaitingForNextTurnPlan(overlay, snapshot);
        }
        else if (!snapshot.AutomaticCalculationEnabled)
        {
            ShowManualCalculationReady(snapshot);
        }
        ApplyRouteFreshness(overlay, snapshot);
        ApplyTurnStartChoiceHighlight(overlay, snapshot);
    }

    /// <summary>
    /// A shown result whose start no longer matches the live state (the player played on, or a new turn
    /// began without a search) stays as a reference, but only while the player could act: during the
    /// enemy turn, action resolution or a turn-start page the route is waiting for the next turn, not out
    /// of date (<see cref="CombatUiSnapshot.CanStartSearch"/> is false there). It is marked the way
    /// CombatSolver marks a retained route
    /// that can no longer run (<c>ShowRetainedRoute</c> at 5c773caa): status 历史路线（仅供参考）,
    /// rows dimmed. When it matches again the result is shown as usual.
    /// </summary>
    private void ApplyRouteFreshness(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        bool outdated = _presentation == SolverOverlayPresentation.Ready
            && snapshot.Result != null
            && snapshot.Phase is not (CombatUiPhase.Searching or CombatUiPhase.Deploying)
            && !snapshot.CurrentTurnRouteReady
            && snapshot.CanStartSearch
            && _routeOutcomePanel is { Visible: true };
        GuideStepUi? guide = snapshot.CurrentTurnRouteReady
            && snapshot.Guide is { Advanced: true } advanced ? advanced : null;
        bool stale = outdated;
        if (guide != null)
        {
            string status = SolverText.Get("ui.guide_following");
            string context = SolverText.Format(
                "ui.guide_turn_step", snapshot.Turn, guide.TurnStep, guide.TurnStepCount);
            if (_summaryStateLabel?.Text != status || _guideStatusContext != context)
            {
                SetStatus(status, SolverUiTokens.Palette.Accent, context);
                _guideStatusContext = context;
            }
        }
        else
        {
            _guideStatusContext = null;
        }
        if (stale)
        {
            string status = SolverText.Get("ui.previous_route_reference_only");
            if (_summaryStateLabel?.Text != status)
            {
                SetStatus(status, SolverUiTokens.Palette.TextMuted,
                    SolverText.Format("ui.turn", snapshot.Turn));
            }
        }
        else if ((_routeStale || _routeGuided && guide == null)
                 && _presentation == SolverOverlayPresentation.Ready
                 && snapshot.Result is { } result)
        {
            ShowResult(overlay, snapshot, result);
        }
        if (_routeScroll != null)
            _routeScroll.Modulate = stale ? StaleRouteModulate : Godot.Colors.White;
        _routeStale = stale;
        _routeGuided = guide != null;
    }

    private static readonly Godot.Color StaleRouteModulate = new(1f, 1f, 1f, 0.5f);

    /// <summary>
    /// While the game waits on the turn-setup page with a plan and the solver is not driving it, the current
    /// turn's start-choice pills are the step to take now (CombatSolver PopulateRoute, PendingTurnSetup).
    /// </summary>
    private void ApplyTurnStartChoiceHighlight(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        bool waiting = _presentation is SolverOverlayPresentation.Ready
                or SolverOverlayPresentation.TurnSetupContinuation
            && snapshot.TurnSetupPageOpen && snapshot.TurnSetupPlanReady && !snapshot.TurnSetupDriving;
        if (!waiting && !_turnStartHighlighted)
            return;
        for (int index = 0; index < overlay.Turns.Count && index < TurnRows; index++)
        {
            if (overlay.Turns[index].Turn != snapshot.Turn)
                continue;
            RouteRows[index].SetTurnStartChoiceDeploymentState(active: waiting, completed: false);
            break;
        }
        _turnStartHighlighted = waiting;
    }

#if STS2_SOLVER_TESTING
    /// <summary>Whether the shown route is marked as no longer matching the live state.</summary>
    internal bool RouteStaleForTest => _routeStale;

    /// <summary>The heading's battle HP text as shown.</summary>
    internal string? HpOutcomeTextForTest => _hpOutcomeLabel?.Text;

    /// <summary>Whether the row of <paramref name="turn"/> shows start-choice pills, all lit as the current step.</summary>
    internal bool TurnStartChoicesActiveForTest(int turn)
    {
        for (int index = 0; index < TurnRows; index++)
        {
            if (RouteRows[index].Visible && RouteRows[index].Turn == turn)
                return RouteRows[index].TurnStartChoicesActiveForTest;
        }
        return false;
    }
#endif

    private void ShowSearching(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot,
        SearchUiSnapshot search)
    {
        _presentation = SolverOverlayPresentation.Searching;
        _waitingForNextTurnPlan = false;
        SetPerformanceHintVisible(false);
        _searchBestSnapshot = null;
        _lastSearchingTurn = search.Turn;
        _lastSearchStartedAt = (int)search.StartedAtMilliseconds;
        _reviewedWorldlinesBeforeSearch = search.ReviewedWorldlinesBeforeSearch;
        _reviewedWorldlinesTarget = _reviewedWorldlinesBeforeSearch;
        _shownReviewedWorldlines = _reviewedWorldlinesBeforeSearch;
        _targetWorldlinesPerSecond = 0d;
        _shownWorldlinesPerSecond = 0d;
        _lastProgressReviewedWorldlines = 0L;
        _lastProgressSampledAt = 0L;
        _statusQueued = ExecutionQueued(snapshot);
        SetStatus(SolverText.Get("ui.searching"), SolverUiTokens.Palette.Accent,
            _statusQueued
                ? SolverText.Format("ui.turn_execution_queued", search.Turn)
                : SolverText.Format("ui.turn", search.Turn));
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format("ui.searching_this_turn_for_a_survivable_candidate", SolverUiTokens.Palette.TextSecondaryHex);
        }
        if (_progressText != null)
            _progressText.Visible = true;
        SetReviewText(null);
        RenderReviewedWorldlinesSummary();
        if (overlay.Turns.Count > 0)
        {
            PopulateRoute(overlay, snapshot);
        }
        else
        {
            SetRouteVisibility(true);
            SetRouteRowVisible(0, true);
            RouteRows[0].TurnLabel.Text = SolverText.Format("ui.turn", search.Turn);
            RouteRows[0].ShowStatus(SolverText.Get("search.waiting_current_turn_candidate"));
            RouteRows[0].SetOutcome(string.Empty, SolverUiTokens.Palette.TextMuted);
            RouteRows[0].SetRowState(current: true, completed: false);
            for (int index = 1; index < TurnRows; index++)
                SetRouteRowVisible(index, false);
        }
        ApplyContentVisibility();
        RefreshControls(snapshot);
    }

    private void ShowProgress(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot, SearchUiSnapshot search)
    {
        _presentation = SolverOverlayPresentation.Searching;
        if (overlay.Turns.Count > 0)
        {
            _searchBestSnapshot = overlay;
            PopulateRoute(overlay, snapshot);
        }
        string routeContext = _searchBestSnapshot is { Turns.Count: > 0 } best
            ? SolverText.Format("ui.planned_through_turn", best.Turns[^1].Turn)
            : SolverText.Get("search.waiting_candidate");
        _statusQueued = ExecutionQueued(snapshot);
        SetStatus(SolverText.Get("ui.searching"), SolverUiTokens.Palette.Accent,
            _statusQueued ? SolverText.Format("ui.execution_queued", routeContext) : routeContext);
        // ExpandedNodes counts the whole request (finished members plus the running one), so the
        // total only grows; the rate is measured on the main thread's clock between samples, since
        // the snapshot's elapsed time restarts with every member.
        long reviewedNow = search.ReviewedWorldlinesBeforeSearch + search.ExpandedNodes;
        _reviewedWorldlinesTarget = Math.Max(_reviewedWorldlinesTarget, reviewedNow);
        long sampledAt = Environment.TickCount64;
        if (_lastProgressSampledAt == 0L)
        {
            _lastProgressSampledAt = sampledAt;
            _lastProgressReviewedWorldlines = search.ExpandedNodes;
        }
        else if (sampledAt - _lastProgressSampledAt >= 250
            && search.ExpandedNodes >= _lastProgressReviewedWorldlines)
        {
            double instantSpeed = (search.ExpandedNodes - _lastProgressReviewedWorldlines)
                / ((sampledAt - _lastProgressSampledAt) / 1000d);
            _targetWorldlinesPerSecond = _targetWorldlinesPerSecond > 0d
                ? 0.35d * instantSpeed + 0.65d * _targetWorldlinesPerSecond
                : instantSpeed;
            _lastProgressReviewedWorldlines = search.ExpandedNodes;
            _lastProgressSampledAt = sampledAt;
        }
        RenderReviewedWorldlinesSummary();
        SetReviewText(null);
        RefreshControls(snapshot);
    }

    private void RenderReviewedWorldlinesSummary()
    {
        if (_summaryText == null)
            return;
        long shown = (long)Math.Round(_shownReviewedWorldlines);
        long speed = (long)Math.Round(_shownWorldlinesPerSecond);
        _summaryText.Visible = true;
        string reviewed = speed > 0
            ? SolverText.Format("ui.explored_timelines_s", shown, speed)
            : SolverText.Format("ui.explored_timelines", shown);
        string prefix = _searchBestSnapshot is { Turns.Count: > 0 } best
            ? FormatResultSummary(best.Turns.Count,
                best.Turns[^1].CombatEnded && best.Turns[^1].Turn == best.Turns[0].Turn)
            : SolverText.Format("ui.searching_this_turn_for_a_survivable_candidate", SolverUiTokens.Palette.TextSecondaryHex);
        _summaryText.Text = prefix.Length > 0 ? prefix + "  │  " + reviewed : reviewed;
    }

    private void AdvanceSearchReadouts(double delta)
    {
        if (_presentation != SolverOverlayPresentation.Searching
            || _layerVisible == false)
            return;
        if (_shownReviewedWorldlines != _reviewedWorldlinesTarget
            || _shownWorldlinesPerSecond != _targetWorldlinesPerSecond)
        {
            double blend = SolverUiMotion.Blend(delta, SolverUiMotion.ReadoutTimeConstantSeconds);
            _shownReviewedWorldlines = SolverUiMotion.Approach(
                _shownReviewedWorldlines, _reviewedWorldlinesTarget, blend, 0.5d);
            _shownWorldlinesPerSecond = SolverUiMotion.Approach(
                _shownWorldlinesPerSecond, _targetWorldlinesPerSecond, blend, 0.5d);
            RenderReviewedWorldlinesSummary();
        }
    }

    private void ShowResult(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot,
        CombatResultUiSnapshot result)
    {
        _presentation = SolverOverlayPresentation.Ready;
        _waitingForNextTurnPlan = false;
        _lastResultCandidate = result.CandidateVersion;
        bool noSurvival = result.DeathTurn is not null;
        string status = noSurvival ? SolverText.Get("ui.no_surviving_route_found")
            : result.ProjectedBattleHpLossIncrease > 0
                ? SolverText.Get("ui.hp_loss_increased_after_recalculation")
                : result.WasReused ? SolverText.Get("ui.plan_reused") : SolverText.Get("ui.plan_ready");
        SolverOverlayTone tone = noSurvival || result.ProjectedBattleHpLossIncrease > 0
            ? SolverOverlayTone.Danger
            : result.WasReused ? SolverOverlayTone.Success : SolverOverlayTone.Accent;
        SetStatus(status, ToneColor(tone));
        string summary = result.DeathTurn is { } deathTurn
            ? FormatDeathSummary(deathTurn, result.StartTurn)
            : FormatResultSummary(overlay.Turns.Count, result.CombatEndedTurn == result.StartTurn);
        if (overlay.Turns.Count > 16)
            summary += "\n" + SolverText.Format("ui.more_turns_not_shown", overlay.Turns.Count - 16);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = summary;
        }
        string review = FormatSearchReview(
            overlay.ReviewedWorldlines, result.SearchExpandedNodes,
            result.SearchElapsedMilliseconds, result.WasReused);
        SetReviewText(review);
        PopulateRoute(overlay, snapshot);
        SetOutcomeLabels(overlay, result);
        ApplyContentVisibility();
        SetPerformanceHintVisible(overlay.ProjectedBattleHpLossKnown
            && IsSignificantBattleHpLoss(true, overlay.ProjectedBattleHpLost));
        RefreshControls(snapshot);
    }

    private void SetOutcomeLabels(SolverOverlaySnapshot overlay, CombatResultUiSnapshot result)
    {
        if (_hpOutcomeLabel != null)
        {
            _hpOutcomeLabel.Text = overlay.HpOutcomeText;
            _hpOutcomeLabel.Visible = true;
            _hpOutcomeLabel.AddThemeColorOverride("font_color",
                !overlay.ProjectedBattleHpLossKnown
                    ? SolverUiTokens.Palette.TextMuted
                    : overlay.ProjectedBattleHpLost > 0
                        ? SolverUiTokens.Palette.Danger
                        : SolverUiTokens.Palette.Success);
        }
        if (_hpRecoveredOutcomeLabel != null)
        {
            _hpRecoveredOutcomeLabel.Visible = overlay.RouteHpRecovered > 0
                || overlay.RoutePostCombatRelicHeal > 0;
            _hpRecoveredOutcomeLabel.Text = RecoveredOutcomeText(overlay);
        }
        if (_deathOutcomeLabel != null)
            _deathOutcomeLabel.Visible = overlay.OnlyDeathRoutesFound;
    }

    private void ShowDeploying(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.Deploying;
        _waitingForNextTurnPlan = false;
        SetPerformanceHintVisible(false);
        _deploymentOverlay = overlay;
        _lastDeploymentTurn = snapshot.Turn;
        _lastDeploymentActionCount = overlay.Turns.Count > 0
            ? overlay.Turns[0].Actions.Count
            : snapshot.Route?.Turns.FirstOrDefault()?.Actions.Count(static action => !action.EndsTurn) ?? 0;
        _lastDeploymentEndedTurn = false;
        SetStatus(SolverText.Get("ui.executing"), SolverUiTokens.Palette.Warning,
            SolverText.Format("ui.turn", snapshot.Turn));
        SetReviewText(null);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format("ui.play_cards_in_order_then_end_the_turn", SolverUiTokens.Palette.TextSecondaryHex, _lastDeploymentActionCount);
        }
        PopulateRoute(overlay, snapshot);
        ShowDeploymentStep(overlay, snapshot);
        RefreshControls(snapshot);
    }

    private void ShowDeploymentStep(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        if (_presentation != SolverOverlayPresentation.Deploying || snapshot.Route is not { } route)
            return;
        PopulateRoute(overlay, snapshot);
        _lastDeploymentActionCount = Math.Max(_lastDeploymentActionCount,
            route.Turns.FirstOrDefault()?.Actions.Count(static action => !action.EndsTurn) ?? 0);
        if (route.ActionRunning
            && route.CurrentActionIndex >= route.CompletedActions
            && route.CurrentActionIndex < route.ActionCount)
        {
            SolverOverlayActionSnapshot? active = overlay.Turns
                .SelectMany(static turn => turn.Actions)
                .FirstOrDefault(action => action.Index == route.CurrentActionIndex);
            if (active == null)
            {
                SolverOverlayActionSnapshot? endTurn = overlay.Turns
                    .Select(static turn => turn.EndTurnAction)
                    .FirstOrDefault(action => action?.Index == route.CurrentActionIndex);
                if (endTurn != null)
                {
                    if (_summaryText != null)
                    {
                        _summaryText.Text =
                            SolverText.Format("ui.end_turn_colored", SolverUiTokens.Palette.WarningHex);
                    }
                    if (RouteRows[0] != null)
                        RouteRows[0].SetEndTurnDeploymentState(active: true, completed: false);
                    return;
                }
            }
            if (active != null && _summaryText != null)
            {
                _summaryText.Text = SolverText.Format("ui.playing", SolverUiTokens.Palette.TextSecondaryHex, route.CompletedActions + 1, _lastDeploymentActionCount, SolverUiTokens.Palette.WarningHex, active.Title);
            }
        }
    }

    private void ShowDeploymentComplete(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot,
        CombatResultUiSnapshot? result)
    {
        _presentation = SolverOverlayPresentation.ExecutedHistory;
        _waitingForNextTurnPlan = true;
        int turn = result?.StartTurn ?? _lastDeploymentTurn;
        int actions = _lastDeploymentActionCount;
        SetStatus(SolverText.Get("ui.execution_complete"), SolverUiTokens.Palette.Accent,
            SolverText.Format("ui.turn", turn));
        SetReviewText(null);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = _lastDeploymentEndedTurn
                ? SolverText.Format("ui.played_cards_and_submitted_end_turn", actions)
                : SolverText.Format("ui.played_cards_combat_or_the_turn_ended_during_execution", actions);
        }
        SolverOverlaySnapshot display = overlay.Turns.Count > 0
            ? overlay
            : _deploymentOverlay ?? overlay;
        PopulateRoute(display, snapshot);
        if (RouteRows[0] != null)
            RouteRows[0].SetEndTurnDeploymentState(active: false, completed: _lastDeploymentEndedTurn);
        RefreshControls(snapshot);
    }

    private void ShowWaitingForNextTurnPlan(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.ExecutedHistory;
        _waitingForNextTurnPlan = true;
        SetStatus(SolverText.Get("ui.waiting"), SolverUiTokens.Palette.TextMuted,
            SolverText.Format("ui.turn_executed", _lastDeploymentTurn));
        SetReviewText(null);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format("ui.route_executed_waiting_for_next_action", SolverUiTokens.Palette.TextSecondaryHex);
        }
        SolverOverlaySnapshot display = overlay.Turns.Count > 0
            ? overlay
            : _deploymentOverlay ?? overlay;
        PopulateRoute(display, snapshot);
        RefreshControls(snapshot);
    }

    private void ShowPaused(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.Ready;
        string code = snapshot.PauseCode ?? "";
        if (code == "death_turn")
        {
            SetStatus(SolverText.Get("ui.execution_paused"), SolverUiTokens.Palette.Danger,
                SolverText.Format("ui.turn_death_predicted", snapshot.Turn));
            SetSummary(SolverText.Format("ui.this_plan_predicts_death_this_turn_control_returned_to_you", SolverUiTokens.Palette.DangerHex));
        }
        else if (code == "no_survival")
        {
            SetStatus(SolverText.Get("ui.execution_paused"), SolverUiTokens.Palette.Danger,
                SolverText.Format("ui.turn_no_surviving_route", snapshot.Turn));
            string death = snapshot.Result?.DeathTurn is { } deathTurn
                ? SolverText.Format("ui.this_plan_predicts_death_on_turn_control_returned_to_you", deathTurn)
                : SolverText.Get("ui.no_surviving_route_found_control_returned_to_you");
            SetSummary($"[color={SolverUiTokens.Palette.DangerHex}]{death}\n"
                + SolverText.Get("ui.raise_the_search_tier_in_settings_and_search_again_or")
                + "[/color]");
        }
        else if (CombatSessionController.IsTurnStopPause(code))
        {
            SetStatus(SolverText.Get("ui.execution_paused"), SolverUiTokens.Palette.Danger);
            string title = SolverPanelText.Pause(code, snapshot.Turn);
            string reason = SolverPanelText.ErrorReason(snapshot.PauseDetailReason);
            SetSummary(
                $"[color={SolverUiTokens.Palette.DangerHex}]{title}\n{reason}[/color]");
        }
        else if (code == "live_end_turn_risk")
        {
            SetStatus(SolverText.Get("ui.execution_paused"), SolverUiTokens.Palette.Danger,
                snapshot.PausePlayerDead
                    ? SolverText.Format("ui.turn_live_check_predicts_death", snapshot.Turn)
                    : SolverText.Format("ui.turn_live_check_predicts_higher_hp_loss", snapshot.Turn));
            SetSummary(
                SolverText.Format("summary.live_end_turn_risk", SolverUiTokens.Palette.DangerHex,
                    snapshot.PausePlannedHpLost ?? 0, snapshot.PauseLiveHpLost ?? 0));
        }
        else if (code == "choice_mismatch")
        {
            ShowMessage(SolverText.Get("ui.solver_message"),
                SolverText.Get("ui.automatic_selection_could_not_finish_execution_is_paused_and_the"),
                SolverPanelText.ErrorReason(snapshot.PauseDetailReason));
        }
        else
        {
            ShowMessage(SolverText.Get("ui.solver_message"),
                SolverPanelText.Pause(code, snapshot.Turn),
                SolverPanelText.ErrorReason(snapshot.PauseDetailReason));
        }
        PopulateRoute(overlay, snapshot);
        RefreshControls(snapshot);
    }

    /// <summary>The running search deploys its route when it finishes: 执行本场战斗 or a deploy-when-ready recalculation.</summary>
    private static bool ExecutionQueued(CombatUiSnapshot snapshot)
        => snapshot.DeployWhenReady || snapshot.CombatRunActive && !snapshot.CombatRunPaused;

#if STS2_SOLVER_TESTING
    internal bool StatusQueuedForTest => _statusQueued;
#endif

    private void ShowSearchStopped(CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.Ready;
        _lastResultCandidate = snapshot.Result?.CandidateVersion ?? -1;
        bool executionStopped = snapshot.PauseReason == "execution_stopped_by_user";
        SetStatus(SolverText.Get(executionStopped ? "ui.execution_paused" : "ui.search_stopped"),
            SolverUiTokens.Palette.Danger);
        if (executionStopped)
        {
            SetSummary(string.Empty);
            SetRouteVisibility(snapshot.Route != null);
            ApplyContentVisibility();
        }
        else if (snapshot.CurrentTurnRouteReady)
        {
            SetSummary(SolverText.Format("ui.search_stopped_the_current_candidate_route_is_retained_below", SolverUiTokens.Palette.WarningHex));
        }
        else
        {
            SetSummary(SolverText.Format("ui.search_stopped_for_this_turn_start_it_manually_automatic_search", SolverUiTokens.Palette.DangerHex));
            SetRouteVisibility(false);
            ApplyContentVisibility();
        }
        RefreshControls(snapshot);
    }

    private void ShowManualCalculationReady(CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.Ready;
        SetStatus(SolverText.Get("ui.waiting_for_manual_search"), SolverUiTokens.Palette.TextMuted);
        string action = SolverText.Get("action.search");
        SetSummary(SolverText.Format("ui.auto_search_is_off_click_to_plan_this_turn", SolverUiTokens.Palette.TextSecondaryHex, action));
        SetReviewText(null);
        SetRouteVisibility(false);
        ApplyContentVisibility();
        RefreshControls(snapshot);
    }

    private void ShowTurnSetupPending(CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.TurnSetupPending;
        _waitingForNextTurnPlan = false;
        _lastResultCandidate = -1;
        SetStatus(SolverText.Get("ui.waiting_for_turn_start_choice"), SolverUiTokens.Palette.Accent,
            SolverText.Format("ui.turn", snapshot.Turn));
        SetSummary(SolverText.Get("ui.the_search_starts_when_the_choice_page_appears"));
        SetReviewText(null);
        SetRouteVisibility(false);
        ApplyContentVisibility();
        RefreshControls(snapshot);
    }

    private void ShowTurnSetupContinuation(SolverOverlaySnapshot overlay,
        CombatUiSnapshot snapshot, CombatResultUiSnapshot result)
    {
        if (_presentation != SolverOverlayPresentation.TurnSetupContinuation
            || _lastResultCandidate != result.CandidateVersion)
        {
            ShowResult(overlay, snapshot, result);
        }
        _presentation = SolverOverlayPresentation.TurnSetupContinuation;
        _waitingForNextTurnPlan = false;
        SetStatus(SolverText.Get("ui.waiting_for_turn_start_choice"), SolverUiTokens.Palette.Accent,
            SolverText.Format("ui.turn", snapshot.Turn));
        PopulateRoute(overlay, snapshot);
        RefreshControls(snapshot);
    }

    private void ShowTurnSetupDriving(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        if (_presentation != SolverOverlayPresentation.TurnSetupDriving
            && snapshot.Result is { } result)
        {
            ShowResult(overlay, snapshot, result);
        }
        _presentation = SolverOverlayPresentation.TurnSetupDriving;
        _waitingForNextTurnPlan = false;
        SetStatus(SolverText.Get("ui.executing"), SolverUiTokens.Palette.Warning,
            SolverText.Format("ui.turn", snapshot.Turn));
        SetSummary(SolverText.Get("ui.making_the_turn_start_choice"));
        SetReviewText(null);
        PopulateRoute(overlay, snapshot);
        RefreshControls(snapshot);
    }

    private void ShowMessage(string status, string title, string? reason = null)
    {
        _presentation = SolverOverlayPresentation.Ready;
        _waitingForNextTurnPlan = false;
        _lastResultCandidate = -1;
        SetStatus(status, SolverUiTokens.Palette.TextMuted);
        SetSummary(string.IsNullOrEmpty(reason) ? title : title + "\n" + reason);
        SetReviewText(null);
        SetRouteVisibility(false);
        ApplyContentVisibility();
    }

    private void SetSummary(string text)
    {
        if (_summaryText == null)
            return;
        _summaryText.Visible = true;
        _summaryText.Text = text;
        if (_progressText != null)
            _progressText.Visible = false;
    }

    /// <summary>
    /// The summary of a route that ends in death: no surviving route was found. The best route is
    /// still shown and can still be executed; the hint points at a stronger search.
    /// </summary>
    internal static string FormatDeathSummary(int deathTurn, int startTurn)
        => (deathTurn == startTurn
                ? SolverText.Format("ui.no_surviving_route_death_predicted_this_turn", SolverUiTokens.Palette.DangerHex)
                : SolverText.Format("ui.no_surviving_route_death_predicted_on_turn", SolverUiTokens.Palette.DangerHex, deathTurn))
            + "\n"
            + SolverText.Format("ui.raise_the_search_tier_in_settings_and_search_again", SolverUiTokens.Palette.TextSecondaryHex);

    internal static string FormatResultSummary(int turnCount, bool endsThisTurn)
        => endsThisTurn
            ? SolverText.Format("ui.combat_ends_this_turn", SolverUiTokens.Palette.SuccessHex)
            : SolverText.Format("ui.planned_route_turns", SolverUiTokens.Palette.TextSecondaryHex, turnCount);

    internal static string FormatSearchReview(long reviewedWorldlines, long requestExpandedNodes,
        long requestElapsedMilliseconds, bool reused)
    {
        if (reused)
            return SolverText.Format("ui.plan_reused_explored_timelines_in_total", reviewedWorldlines);
        double seconds = requestElapsedMilliseconds / 1000d;
        long speed = seconds > 0d
            ? (long)Math.Round(requestExpandedNodes / seconds)
            : 0L;
        return speed > 0
            ? SolverText.Format("ui.searched_for_s_explored_timelines_s", seconds, reviewedWorldlines, speed)
            : SolverText.Format("ui.searched_for_s_explored_timelines", seconds, reviewedWorldlines);
    }

    private static Godot.Color ToneColor(SolverOverlayTone tone) => tone switch
    {
        SolverOverlayTone.Danger => SolverUiTokens.Palette.Danger,
        SolverOverlayTone.Success => SolverUiTokens.Palette.Success,
        SolverOverlayTone.Muted => SolverUiTokens.Palette.TextMuted,
        _ => SolverUiTokens.Palette.Accent,
    };

    private bool _layerVisible => Visible && _panel is { Visible: true };
}
