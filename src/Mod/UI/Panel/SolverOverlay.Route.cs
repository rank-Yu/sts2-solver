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
    private Label? _potionOutcomeLabel;
    private Label? _hpOutcomeLabel;
    private Label? _hpRecoveredOutcomeLabel;
    private int _lastRouteCount = -1;

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
        _potionOutcomeLabel = SolverUiTokens.CreateLabel(SolverText.Get("预计用1瓶药"),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.Warning);
        _potionOutcomeLabel.Visible = false;
        _potionOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        headingRow.AddChild(_potionOutcomeLabel);
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

    private void PopulateRoute(SolverOverlaySnapshot overlay, CombatUiSnapshot snapshot)
    {
        bool hasTurns = overlay.Turns.Count > 0;
        SetRouteVisibility(hasTurns || snapshot.Phase == CombatUiPhase.Searching);
        if (_deathOutcomeLabel != null)
            _deathOutcomeLabel.Visible = overlay.OnlyDeathRoutesFound;
        if (_potionOutcomeLabel != null)
        {
            _potionOutcomeLabel.Visible = overlay.ProjectedBattlePotionCount > 0;
            _potionOutcomeLabel.Text = SolverText.Format($"预计用{overlay.ProjectedBattlePotionCount}瓶药");
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

        int length = overlay.Turns.Count;
        int count = Math.Min(length, TurnRows);
        for (int index = 0; index < TurnRows; index++)
        {
            SetRouteRowVisible(index, index < count);
            if (index >= count)
                continue;
            SolverOverlayTurnSnapshot turn = overlay.Turns[index];
            SolverRouteRow row = RouteRows[index];
            row.Populate(turn);
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
            bool current = _presentation == SolverOverlayPresentation.Deploying
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
