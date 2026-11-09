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
            ShowMessage(SolverText.Get("求解器消息"),
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
    }

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
        SetStatus(SolverText.Get("后台搜索中"), SolverUiTokens.Palette.Accent,
            SolverText.Format($"第 {search.Turn} 回合"));
        SetRouteHeading(SolverText.Get("求解器当前考虑（尚未验证）"));
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format(
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在搜索当前回合，等待可存活候选…[/color]");
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
            RouteRows[0].TurnLabel.Text = SolverText.Format($"第 {search.Turn} 回合");
            RouteRows[0].ShowStatus(SolverText.Get("等待当前回合候选…"));
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
            ? SolverText.Format($"已规划至第 {best.Turns[^1].Turn} 回合")
            : SolverText.Get("等待候选路线");
        SetStatus(SolverText.Get("后台搜索中"), SolverUiTokens.Palette.Accent, routeContext);
        SetRouteHeading(SolverText.Get("求解器当前考虑（尚未验证）"));
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
            ? SolverText.Format($"已查阅 {shown:N0} 条世界线（{speed:N0} 条/s）")
            : SolverText.Format($"已查阅 {shown:N0} 条世界线");
        string prefix = _searchBestSnapshot is { Turns.Count: > 0 } best
            ? FormatResultSummary(best.Turns.Count,
                best.Turns[^1].CombatEnded && best.Turns[^1].Turn == best.Turns[0].Turn)
            : SolverText.Format(
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在搜索当前回合，等待可存活候选…[/color]");
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
        string status = noSurvival ? SolverText.Get("未找到生还路线")
            : result.ProjectedBattleHpLossIncrease > 0
                ? SolverText.Get("重算后战损上升")
                : result.WasReused ? SolverText.Get("方案已复用") : SolverText.Get("方案就绪");
        SolverOverlayTone tone = noSurvival || result.ProjectedBattleHpLossIncrease > 0
            ? SolverOverlayTone.Danger
            : result.WasReused ? SolverOverlayTone.Success : SolverOverlayTone.Accent;
        SetStatus(status, ToneColor(tone));
        SetRouteHeading(SolverText.Get("推荐路线"));
        string summary = result.DeathTurn is { } deathTurn
            ? FormatDeathSummary(deathTurn, result.StartTurn)
            : FormatResultSummary(overlay.Turns.Count, result.CombatEndedTurn == result.StartTurn);
        if (overlay.Turns.Count > 16)
            summary += "\n" + SolverText.Format($"另有 {overlay.Turns.Count - 16} 回合未展开显示");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = AppendCapacityDropHint(summary, result);
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
        SetStatus(SolverText.Get("正在执行"), SolverUiTokens.Palette.Warning,
            SolverText.Format($"第 {snapshot.Turn} 回合"));
        SetRouteHeading(SolverText.Get("正在执行的路线"));
        SetReviewText(null);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format(
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]按推荐顺序执行 [b]{_lastDeploymentActionCount}[/b] 张牌，完成后结束本回合。[/color]");
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
                            SolverText.Format(
                                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在执行：[/color]")
                            + SolverText.Format(
                                $"[color={SolverUiTokens.Palette.WarningHex}] 结束回合[/color]");
                    }
                    if (RouteRows[0] != null)
                        RouteRows[0].SetEndTurnDeploymentState(active: true, completed: false);
                    return;
                }
            }
            if (active != null && _summaryText != null)
            {
                _summaryText.Text = SolverText.Format(
                    $"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在执行 [b]{route.CompletedActions + 1}/{_lastDeploymentActionCount}[/b]：[/color][color={SolverUiTokens.Palette.WarningHex}] {active.Title}[/color]");
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
        SetStatus(SolverText.Get("执行完成"), SolverUiTokens.Palette.Accent,
            SolverText.Format($"第 {turn} 回合"));
        SetRouteHeading(SolverText.Get("已执行路线（后续回合待校验）"));
        SetReviewText(null);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = _lastDeploymentEndedTurn
                ? SolverText.Format($"已按推荐路线打出 [b]{actions}[/b] 张牌，并提交结束回合动作。")
                : SolverText.Format($"已打出 [b]{actions}[/b] 张牌；战斗或当前回合已在执行期间结束。");
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
        SetStatus(SolverText.Get("等待下一回合方案"), SolverUiTokens.Palette.TextMuted,
            SolverText.Format($"第 {_lastDeploymentTurn} 回合已执行"));
        SetRouteHeading(SolverText.Get("已执行路线（后续回合待校验）"));
        SetReviewText(null);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format(
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]当前路线已经执行；等待下一回合方案就绪。[/color]");
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
            SetStatus(SolverText.Get("已暂停执行"), SolverUiTokens.Palette.Danger,
                SolverText.Format($"第 {snapshot.Turn} 回合    预计死亡"));
            SetSummary(SolverText.Format(
                $"[color={SolverUiTokens.Palette.DangerHex}]当前方案预计本回合死亡，操作权已交还。[/color]"));
        }
        else if (code == "no_survival")
        {
            SetStatus(SolverText.Get("已暂停执行"), SolverUiTokens.Palette.Danger,
                SolverText.Format($"第 {snapshot.Turn} 回合    未找到生还路线"));
            string death = snapshot.Result?.DeathTurn is { } deathTurn
                ? SolverText.Format($"当前方案预计第 {deathTurn} 回合死亡，操作权已交还。")
                : SolverText.Get("未找到生还路线，操作权已交还。");
            SetSummary($"[color={SolverUiTokens.Palette.DangerHex}]{death}\n"
                + SolverText.Get("可在 设置 中提高搜索档位后重新搜索，或点“执行本场战斗”继续（本场不再因此暂停）。")
                + "[/color]");
        }
        else if (CombatSessionController.IsTurnStopPause(code))
        {
            SetStatus(SolverText.Get("已暂停执行"), SolverUiTokens.Palette.Danger);
            string title = SolverPanelText.Pause(code, snapshot.Turn);
            string reason = SolverPanelText.ErrorReason(snapshot.PauseDetailReason);
            SetSummary(
                $"[color={SolverUiTokens.Palette.DangerHex}]{title}\n{reason}[/color]");
        }
        else if (code == "live_end_turn_risk")
        {
            SetStatus(SolverText.Get("已暂停执行"), SolverUiTokens.Palette.Danger,
                snapshot.PausePlayerDead
                    ? SolverText.Format($"第 {snapshot.Turn} 回合    实机复核将死亡")
                    : SolverText.Format($"第 {snapshot.Turn} 回合    实机复核战损上升"));
            SetSummary(
                SolverText.Format(
                    $"[color={SolverUiTokens.Palette.DangerHex}]路线预计掉血 {snapshot.PausePlannedHpLost ?? 0} HP，")
                + SolverText.Format(
                    $"结束回合前实机复核为 {snapshot.PauseLiveHpLost ?? 0} HP；没有提交结束回合。[/color]\n"));
        }
        else if (code == "choice_mismatch")
        {
            ShowMessage(SolverText.Get("求解器消息"),
                SolverText.Get("自动选牌未完成，已暂停执行。当前选择交还手动操作，完成后点击“重新搜索”。"),
                SolverPanelText.ErrorReason(snapshot.PauseDetailReason));
        }
        else
        {
            ShowMessage(SolverText.Get("求解器消息"),
                SolverPanelText.Pause(code, snapshot.Turn),
                SolverPanelText.ErrorReason(snapshot.PauseDetailReason));
        }
        SetRouteHeading(SolverText.Get("推荐路线"));
        PopulateRoute(overlay, snapshot);
        RefreshControls(snapshot);
    }

    private void ShowSearchStopped(CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.Ready;
        _lastResultCandidate = snapshot.Result?.CandidateVersion ?? -1;
        SetStatus(SolverText.Get("搜索已停止"), SolverUiTokens.Palette.Danger);
        if (snapshot.CurrentTurnRouteReady)
        {
            SetSummary(AppendCapacityDropHint(
                SolverText.Format(
                    $"[color={SolverUiTokens.Palette.WarningHex}]搜索已停止；下方保留停止时已经得到的当前候选路线。[/color]"),
                snapshot.Result));
        }
        else
        {
            SetSummary(SolverText.Format(
                $"[color={SolverUiTokens.Palette.DangerHex}]本回合搜索已停止。可手动开始搜索；进入下一回合后是否自动搜索由设置决定。[/color]"));
            SetRouteVisibility(false);
            ApplyContentVisibility();
        }
        RefreshControls(snapshot);
    }

    private static string AppendCapacityDropHint(string text, CombatResultUiSnapshot? result)
    {
        if (result?.CapacityDroppedRegions is not { Count: > 0 } regions)
            return text;
        string hint = SolverPanelText.CapacityDropHint(regions);
        return hint.Length == 0
            ? text
            : text + "\n" + $"[color={SolverUiTokens.Palette.WarningHex}]{hint}[/color]";
    }

    private void ShowManualCalculationReady(CombatUiSnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.Ready;
        SetStatus(SolverText.Get("等待手动搜索"), SolverUiTokens.Palette.TextMuted);
        string action = SolverText.Get("开始搜索");
        SetSummary(SolverText.Format(
            $"[color={SolverUiTokens.Palette.TextSecondaryHex}]自动搜索已关闭。点击“{action}”生成当前回合路线。[/color]"));
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
        SetStatus(SolverText.Get("等待回合开始选择"), SolverUiTokens.Palette.Accent,
            SolverText.Format($"第 {snapshot.Turn} 回合"));
        SetSummary(SolverText.Get("选牌页面出现后自动开始搜索"));
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
        SetStatus(SolverText.Get("等待回合开始选择"), SolverUiTokens.Palette.Accent,
            SolverText.Format($"第 {snapshot.Turn} 回合"));
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
        SetStatus(SolverText.Get("正在执行"), SolverUiTokens.Palette.Warning,
            SolverText.Format($"第 {snapshot.Turn} 回合"));
        SetSummary(SolverText.Get("正在执行回合开始选择"));
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

    private void SetRouteHeading(string heading)
    {
        if (_routeHeadingLabel != null)
            _routeHeadingLabel.Text = heading;
    }

    /// <summary>
    /// The summary of a route that ends in death: no surviving route was found. The best route is
    /// still shown and can still be executed; the hint points at a stronger search.
    /// </summary>
    internal static string FormatDeathSummary(int deathTurn, int startTurn)
        => (deathTurn == startTurn
                ? SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}]未找到生还路线：预计本回合死亡[/color]")
                : SolverText.Format(
                    $"[color={SolverUiTokens.Palette.DangerHex}]未找到生还路线：预计第 {deathTurn} 回合死亡[/color]"))
            + "\n"
            + SolverText.Format(
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]可在 设置 中提高搜索档位后重新搜索。[/color]");

    internal static string FormatResultSummary(int turnCount, bool endsThisTurn)
        => endsThisTurn
            ? SolverText.Format($"[color={SolverUiTokens.Palette.SuccessHex}]本回合结束战斗[/color]")
            : SolverText.Format(
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]预计路线 [b]{turnCount}[/b] 回合[/color]");

    internal static string FormatSearchReview(long reviewedWorldlines, long requestExpandedNodes,
        long requestElapsedMilliseconds, bool reused)
    {
        if (reused)
            return SolverText.Format($"路线已复用，共查阅了 {reviewedWorldlines:N0} 条世界线");
        double seconds = requestElapsedMilliseconds / 1000d;
        long speed = seconds > 0d
            ? (long)Math.Round(requestExpandedNodes / seconds)
            : 0L;
        return speed > 0
            ? SolverText.Format(
                $"花费了 {seconds:F1} 秒，共查阅了 {reviewedWorldlines:N0} 条世界线（{speed:N0} 条/s）")
            : SolverText.Format(
                $"花费了 {seconds:F1} 秒，共查阅了 {reviewedWorldlines:N0} 条世界线");
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
