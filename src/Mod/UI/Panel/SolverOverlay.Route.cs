// Route section ported from CombatSolver src/UI/SolverOverlay.cs (PopulateRoute, route heading and
// outcome labels) at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Data comes from SolverOverlaySnapshot; the route window and row states are unchanged.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverOverlay
{
    private const int TurnRows = 16;

    private readonly SolverRouteRow[] RouteRows = new SolverRouteRow[TurnRows];
    private ScrollContainer? _routeScroll;
    private PanelContainer? _routeOutcomePanel;
    private Label? _routeHeadingLabel;
    private Label? _deathOutcomeLabel;
    private HFlowContainer? _strategyOutcomeRow;
    private Label? _usedPotionOutcomeLabel;
    private Label? _unrecoveredLootLabel;
    private string? _strategyOutcomeKey;
    private bool _statusQueued;
    private Label? _potionOutcomeLabel;
    private Label? _hpOutcomeLabel;
    private Label? _hpRecoveredOutcomeLabel;
    private int _lastRouteCount = -1;
    private (SolverOverlayPresentation Presentation, int DeploymentTurn)? _routeMotionKey;

    private void BuildRouteSection()
    {
        _routeOutcomePanel = SolverUiTokens.CreateSectionPanel("RouteOutcomePanel");
        HBoxContainer routeSummary = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        routeSummary.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        routeSummary.AddChild(SolverUiTokens.CreateLabel(SolverText.Get("当前路线摘要"),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary));
        HFlowContainer headingRow = new()
        {
            Alignment = FlowContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        headingRow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        headingRow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        _routeHeadingLabel = SolverUiTokens.CreateLabel(SolverText.Get("推荐路线"),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        _routeHeadingLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headingRow.AddChild(_routeHeadingLabel);
        _deathOutcomeLabel = SolverUiTokens.CreateLabel(SolverText.Get("未找到生还路线"),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.Danger);
        _deathOutcomeLabel.Visible = false;
        _deathOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        headingRow.AddChild(_deathOutcomeLabel);
        _usedPotionOutcomeLabel = SolverUiTokens.CreateLabel(string.Empty,
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.Warning);
        _usedPotionOutcomeLabel.Visible = false;
        _usedPotionOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        headingRow.AddChild(_usedPotionOutcomeLabel);
        _potionOutcomeLabel = SolverUiTokens.CreateLabel(SolverText.Get("预计用1瓶药"),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.Warning);
        _potionOutcomeLabel.Visible = false;
        _potionOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        headingRow.AddChild(_potionOutcomeLabel);
        _unrecoveredLootLabel = SolverUiTokens.CreateLabel(string.Empty,
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.Danger);
        _unrecoveredLootLabel.Visible = false;
        _unrecoveredLootLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        headingRow.AddChild(_unrecoveredLootLabel);
        _strategyOutcomeRow = new HFlowContainer
        {
            Alignment = FlowContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _strategyOutcomeRow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        headingRow.AddChild(_strategyOutcomeRow);
        _strategyOutcomeKey = null;
        _hpOutcomeLabel = SolverUiTokens.CreateLabel(SolverText.Get("本局扣血  0 HP"),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.Success);
        _hpOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _hpOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        headingRow.AddChild(_hpOutcomeLabel);
        _hpRecoveredOutcomeLabel = SolverUiTokens.CreateLabel(SolverText.Get("路线回血  0 HP"),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.Success);
        _hpRecoveredOutcomeLabel.Visible = false;
        _hpRecoveredOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _hpRecoveredOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        headingRow.AddChild(_hpRecoveredOutcomeLabel);
        routeSummary.AddChild(headingRow);
        _routeOutcomePanel.AddChild(routeSummary);
        _body!.AddChild(_routeOutcomePanel);

        VBoxContainer routes = new()
        {
            Name = "Routes",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        routes.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        for (int index = 0; index < TurnRows; index++)
        {
            SolverRouteRow row = new(index);
            RouteRows[index] = row;
            routes.AddChild(row);
        }
        _routeScroll = new ScrollContainer
        {
            Name = "RouteScroll",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.RouteViewportHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _routeScroll.AddChild(routes);
        _body.AddChild(_routeScroll);
    }

    /// <summary>
    /// Presentations that show a route being executed: the deployment itself and the turn-setup page
    /// a run reaches after it (waiting with a carried plan, or driving the page).
    /// </summary>
    private bool ShowsDeployedRoute => _presentation is SolverOverlayPresentation.Deploying
        or SolverOverlayPresentation.TurnSetupContinuation
        or SolverOverlayPresentation.TurnSetupDriving;

    /// <summary>Rebuilds the strategy outcome labels only when their text changes (every refresh calls this).</summary>
    private void PopulateStrategyOutcomes(IReadOnlyList<SolverStrategyOutcome> outcomes)
    {
        if (_strategyOutcomeRow == null)
            return;
        string key = string.Join("\n", outcomes.Select(static outcome => $"{outcome.Satisfied}|{outcome.Text}"));
        if (key == _strategyOutcomeKey)
            return;
        _strategyOutcomeKey = key;
        foreach (Node child in _strategyOutcomeRow.GetChildren())
        {
            _strategyOutcomeRow.RemoveChild(child);
            child.QueueFree();
        }
        foreach (SolverStrategyOutcome outcome in outcomes)
        {
            Label label = SolverUiTokens.CreateLabel(outcome.Text, SolverUiTokens.Type.Body,
                outcome.Satisfied ? SolverUiTokens.Palette.Success : SolverUiTokens.Palette.Warning);
            label.TooltipText = outcome.Text;
            label.MouseFilter = Control.MouseFilterEnum.Pass;
            label.HorizontalAlignment = HorizontalAlignment.Right;
            _strategyOutcomeRow.AddChild(label);
        }
        _strategyOutcomeRow.Visible = outcomes.Count > 0;
    }

#if STS2_SOLVER_TESTING
    /// <summary>
    /// Whether the route heading row is on screen, and the strategy outcome texts its row shows in order
    /// (empty when the row is hidden).
    /// </summary>
    /// <summary>Whether the used-potion and unrecovered-loot labels are shown.</summary>
    internal (bool UsedPotions, bool UnrecoveredLoot) OutcomeExtrasForTest => (
        _usedPotionOutcomeLabel?.Visible == true, _unrecoveredLootLabel?.Visible == true);

    /// <summary>
    /// The route area shows the overlay kept from the deployment (<see cref="ShowDeploymentComplete"/>
    /// and <see cref="ShowWaitingForNextTurnPlan"/> when the snapshot has no route any more), so its
    /// outcome labels describe the executed result rather than the snapshot's.
    /// </summary>
    internal bool RouteShowsKeptDeploymentForTest => _shownRouteOverlay != null
        && !ReferenceEquals(_shownRouteOverlay, _lastOverlay);

    private SolverOverlaySnapshot? _shownRouteOverlay;

    internal (bool HeadingVisible, string[] Texts) StrategyOutcomesForTest => (
        _strategyOutcomeRow?.GetParent() is Control heading && heading.IsVisibleInTree(),
        _strategyOutcomeRow is { Visible: true } row
            ? [.. row.GetChildren().OfType<Label>().Where(static label => !label.IsQueuedForDeletion())
                .Select(static label => label.Text)]
            : []);
#endif

    private void PopulateRoute(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
#if STS2_SOLVER_TESTING
        _shownRouteOverlay = overlay;
#endif
        bool hasTurns = overlay.Turns.Count > 0;
        SetRouteVisibility(hasTurns || snapshot.Phase == CombatUiPhase.Searching);
        if (_deathOutcomeLabel != null)
            _deathOutcomeLabel.Visible = overlay.OnlyDeathRoutesFound;
        PopulateStrategyOutcomes(overlay.StrategyOutcomes);
        if (_usedPotionOutcomeLabel != null)
        {
            _usedPotionOutcomeLabel.Visible = overlay.UsedPotionOutcomeText != null;
            _usedPotionOutcomeLabel.Text = overlay.UsedPotionOutcomeText ?? string.Empty;
        }
        if (_potionOutcomeLabel != null)
        {
            // CombatSolver's rule: the named route potions when the result has them, otherwise the bare
            // count (a search preview, or uses the route makes without an explicit potion action).
            _potionOutcomeLabel.Visible = overlay.PlannedPotionOutcomeText != null
                || overlay.ProjectedBattlePotionCount > 0 && overlay.UsedPotionOutcomeText == null;
            _potionOutcomeLabel.Text = overlay.PlannedPotionOutcomeText
                ?? SolverText.Format($"预计用{overlay.ProjectedBattlePotionCount}瓶药");
        }
        if (_unrecoveredLootLabel != null)
        {
            _unrecoveredLootLabel.Visible = overlay.UnrecoveredLootText != null;
            _unrecoveredLootLabel.Text = overlay.UnrecoveredLootText ?? string.Empty;
        }
        if (_hpOutcomeLabel != null)
        {
            _hpOutcomeLabel.Text = overlay.HpOutcomeText;
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

        // Pill colors restart only when the presentation changes (a new deployment turn, the result,
        // the executed history); a refresh inside one presentation keeps the running fades.
        (SolverOverlayPresentation, int) motionKey = (_presentation,
            _presentation == SolverOverlayPresentation.Deploying ? _lastDeploymentTurn : 0);
        bool restartMotion = _routeMotionKey != motionKey;
        _routeMotionKey = motionKey;
        int length = overlay.Turns.Count;
        int count = Math.Min(length, TurnRows);
        for (int index = 0; index < TurnRows; index++)
        {
            SetRouteRowVisible(index, index < count);
            if (index >= count)
                continue;
            SolverOverlayTurnSnapshot turn = overlay.Turns[index];
            SolverRouteRow row = RouteRows[index];
            row.Populate(turn, restartMotion);
            int netHpChange = turn.HpRecovered - turn.HpLoss;
            string outcome = turn.CombatEnded
                ? SolverText.Get("战斗结束")
                : netHpChange != 0
                    ? $"{HpChangeText.Signed(netHpChange)} HP"
                    : "0 HP";
            row.SetOutcome(
                outcome,
                turn.CombatEnded ? SolverUiTokens.Palette.Success
                    : netHpChange < 0 ? SolverUiTokens.Palette.Danger
                    : netHpChange > 0 ? SolverUiTokens.Palette.Success
                    : SolverUiTokens.Palette.TextMuted,
                energyText: SolverText.Format($"余 {turn.EnergyLeft} 费"),
                enemyDamageText: turn.EnemyHpDamageLost is { } damage
                    ? SolverText.Format($"对敌伤害 {damage}")
                    : string.Empty);
            bool current = ShowsDeployedRoute
                ? turn.Turn == snapshot.Turn
                : index == 0;
            row.SetRowState(current, !current && turn.Turn < snapshot.Turn);
        }
        if (!hasTurns && snapshot.Phase == CombatUiPhase.Searching)
        {
            SetRouteRowVisible(0, true);
            RouteRows[0].TurnLabel.Text = SolverText.Format($"第 {snapshot.Turn} 回合");
            RouteRows[0].ShowStatus(SolverText.Get("等待当前回合候选…"));
            RouteRows[0].SetOutcome(string.Empty, SolverUiTokens.Palette.TextMuted);
            RouteRows[0].SetRowState(current: true, completed: false);
        }
        if (snapshot.Phase == CombatUiPhase.Deploying && snapshot.Route is { } route)
        {
            // CombatSolver passes a non-null currentCardTitle only while an action runs and
            // passes null for the completion boundary (src/Runtime/SolverController.cs at
            // 5c773caa). ActionRunning carries that distinction; current == completed alone also
            // holds while an action is running, so it cannot identify the boundary frame.
            int? active = route.ActionRunning
                && route.CurrentActionIndex >= route.CompletedActions
                && route.CurrentActionIndex < route.ActionCount
                    ? route.CurrentActionIndex
                    : null;
            foreach (SolverRouteRow row in RouteRows)
            {
                if (!row.Visible)
                    continue;
                int completedLocal = Math.Clamp(
                    route.CompletedActions - row.StartActionIndex, 0, row.DeploymentActionCount);
                int? activeLocal = active is { } activeIndex
                    && activeIndex >= row.StartActionIndex
                    && activeIndex <= row.StartActionIndex + row.DeploymentActionCount
                        ? activeIndex - row.StartActionIndex
                        : null;
                if (activeLocal == row.DeploymentActionCount)
                {
                    row.SetDeploymentProgress(completedLocal, null);
                    row.SetEndTurnDeploymentState(active: true, completed: false);
                }
                else
                {
                    row.SetDeploymentProgress(completedLocal, activeLocal);
                }
            }
        }
        else if (snapshot.Route is { CompletedActions: > 0 } setupRoute)
        {
            // The projection passes deployment progress only for the deployed route. Outside the
            // Deploying phase (a run reaching the next turn's setup page, or the WaitingTurn frames
            // right after a turn's deployment finished) its executed actions stay completed instead
            // of being redrawn as pending when the rows are rebuilt.
            foreach (SolverRouteRow row in RouteRows)
            {
                if (!row.Visible || row.StartActionIndex >= setupRoute.CompletedActions)
                    continue;
                row.SetDeploymentProgress(Math.Clamp(
                    setupRoute.CompletedActions - row.StartActionIndex, 0, row.DeploymentActionCount), null);
            }
        }
        if (_routeScroll != null
            && (count != _lastRouteCount))
        {
            _routeScroll.ScrollVertical = 0;
            _lastRouteCount = count;
        }
    }

    /// <summary>
    /// CombatSolver SolverOverlay.ApplyOutcomeLabels: post-combat relic healing is reported next to
    /// route healing rather than folded into it, because it lands after the last action.
    /// </summary>
    private static string RecoveredOutcomeText(SolverOverlaySnapshot overlay)
        => (overlay.RouteHpRecovered, overlay.RoutePostCombatRelicHeal) switch
        {
            ( > 0, > 0) => SolverText.Format($"路线回血  {overlay.RouteHpRecovered} HP")
                + SolverText.Format($"　战后遗物  {overlay.RoutePostCombatRelicHeal} HP"),
            ( > 0, _) => SolverText.Format($"路线回血  {overlay.RouteHpRecovered} HP"),
            (_, > 0) => SolverText.Format($"战后遗物回血  {overlay.RoutePostCombatRelicHeal} HP"),
            _ => string.Empty,
        };

    private void SetRouteVisibility(bool visible)
    {
        if (_routeOutcomePanel != null)
            _routeOutcomePanel.Visible = visible;
        for (int index = 0; index < TurnRows; index++)
            SetRouteRowVisible(index, visible);
        QueueLayout();
    }

    private void SetRouteRowVisible(int index, bool visible)
    {
        if (RouteRows[index] != null)
            RouteRows[index].Visible = visible;
    }

#if STS2_SOLVER_TESTING
    internal SolverRowProbe[] ProbeRowsForTest()
        => RouteRows.Where(static row => row.Visible).Select(static row => row.ProbeForTest()).ToArray();
#endif
}

/// <summary>Signed HP text shared by the route row summary; no sign for zero.</summary>
internal static class HpChangeText
{
    internal static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString();
}
