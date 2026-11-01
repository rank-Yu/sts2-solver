// Layout adapted from the route outcome panel and route rows of CombatSolver
// src/UI/SolverOverlay.cs at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Sts2Solver keeps one whole-combat route, so the rows show a 16-turn window around the current
// turn instead of the searched turns of the latest result.
using Godot;

namespace Sts2Solver;

/// <summary>Route heading, the scrollable turn rows and the empty state. Reads only the snapshot.</summary>
internal sealed partial class SolverRouteSection : PanelContainer
{
    internal const int MaximumRows = 16;

    private readonly SolverRouteRow[] _rows = new SolverRouteRow[MaximumRows];
    private readonly Label _summary;
    private readonly Label _outcome;
    private readonly Label _progress;
    private readonly Label _empty;
    private readonly Label _more;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _rowsBox;
    private RouteUiSnapshot? _lastRoute;
    private CombatUiPhase? _lastPhase;
    private int _lastReused = -1;
    private int _windowStart;
    private int _windowCount;

    public SolverRouteSection()
    {
        Name = "RouteSection";
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Sm));

        VBoxContainer layout = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        AddChild(layout);

        HBoxContainer heading = new() { MouseFilter = MouseFilterEnum.Ignore };
        heading.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        heading.AddChild(SolverUiTokens.CreateLabel(
            SolverText.Get("路线"), SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary));
        _summary = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary, SolverFontWeight.Normal);
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.ClipText = true;
        heading.AddChild(_summary);
        _progress = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary, SolverFontWeight.Normal);
        heading.AddChild(_progress);
        _outcome = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption, SolverUiTokens.Palette.Success);
        heading.AddChild(_outcome);
        layout.AddChild(heading);

        _empty = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextMuted, SolverFontWeight.Normal);
        _empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _empty.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _empty.CustomMinimumSize = new Vector2(0, 40);
        layout.AddChild(_empty);

        _rowsBox = new VBoxContainer
        {
            Name = "Routes",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _rowsBox.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs + 2);
        for (int index = 0; index < MaximumRows; index++)
        {
            SolverRouteRow row = new(index) { Visible = false };
            _rows[index] = row;
            _rowsBox.AddChild(row);
        }
        _more = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextMuted, SolverFontWeight.Normal);
        _more.Visible = false;
        _rowsBox.AddChild(_more);

        _scroll = new ScrollContainer
        {
            Name = "RouteScroll",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.RouteViewportMinHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = MouseFilterEnum.Pass,
        };
        _scroll.AddChild(_rowsBox);
        layout.AddChild(_scroll);
    }

    /// <summary>Natural height of the visible rows, used by the auto-height layout.</summary>
    public float NaturalRowsHeight => _scroll.Visible ? _rowsBox.GetCombinedMinimumSize().Y : 0f;

    public void SetViewportHeight(float height)
    {
        Vector2 size = new(0, height);
        if (_scroll.CustomMinimumSize != size)
            _scroll.CustomMinimumSize = size;
    }

    public void Apply(CombatUiSnapshot snapshot)
    {
        RouteUiSnapshot? route = snapshot.Route;
        if (ReferenceEquals(route, _lastRoute) && snapshot.Phase == _lastPhase
            && snapshot.ReusedTurns == _lastReused)
        {
            return;
        }
        _lastRoute = route;
        _lastPhase = snapshot.Phase;
        _lastReused = snapshot.ReusedTurns;
        if (route == null || route.Turns.Length == 0)
        {
            ShowEmpty(snapshot);
            return;
        }
        _empty.Visible = false;
        _scroll.Visible = true;
        (int start, int count) = Window(route);
        bool windowMoved = start != _windowStart || count != _windowCount;
        _windowStart = start;
        _windowCount = count;
        bool running = SolverPanelText.IsRunning(snapshot.Phase);
        int? active = ActiveIndex(snapshot.Phase, route);
        for (int index = 0; index < MaximumRows; index++)
        {
            SolverRouteRow row = _rows[index];
            if (index >= count)
            {
                row.Visible = false;
                continue;
            }
            RouteTurnUi turn = route.Turns[start + index];
            row.Visible = true;
            row.Populate(turn, route.Victory);
            bool completed = route.CompletedActions >= turn.StartActionIndex + turn.Actions.Length;
            row.SetRowState(running && turn.Turn == route.CurrentTurn, completed);
            row.SetDeploymentProgress(route.CompletedActions, active);
        }
        int hidden = route.Turns.Length - count;
        _more.Visible = hidden > 0;
        if (hidden > 0)
        {
            int before = start;
            int after = route.Turns.Length - start - count;
            _more.Text = SolverText.Format($"另有 {hidden} 个回合未显示（之前 {before}，之后 {after}）");
        }
        if (windowMoved)
            _scroll.ScrollVertical = 0;

        _summary.Text = SolverText.Format($"{route.Turns.Length} 回合 · {route.ActionCount} 个动作")
            + (snapshot.ReusedTurns > 0 ? SolverText.Format($" · 已沿用 {snapshot.ReusedTurns} 回合") : "");
        _progress.Text = SolverText.Format($"已执行 {route.CompletedActions}/{route.ActionCount}");
        _outcome.Text = route.Victory ? SolverText.Get("预计胜利") : SolverText.Get("未找到胜利路线");
        _outcome.AddThemeColorOverride("font_color",
            route.Victory ? SolverUiTokens.Palette.Success : SolverUiTokens.Palette.Danger);
        _outcome.Visible = true;
        _progress.Visible = true;
    }

    /// <summary>The 16-row window: one finished turn of context before the current turn when it fits.</summary>
    internal static (int Start, int Count) Window(RouteUiSnapshot route)
    {
        int length = route.Turns.Length;
        int count = Math.Min(length, MaximumRows);
        int current = Array.FindIndex(route.Turns, turn => turn.Turn == route.CurrentTurn);
        if (current < 0)
            return (0, count);
        return (Math.Clamp(current - 1, 0, length - count), count);
    }

    /// <summary>The action being executed, or null when nothing is in flight.</summary>
    internal static int? ActiveIndex(CombatUiPhase phase, RouteUiSnapshot route)
        => phase == CombatUiPhase.Deploying
            && route.CurrentActionIndex >= route.CompletedActions
            && route.CurrentActionIndex < route.ActionCount
                ? route.CurrentActionIndex
                : null;

#if STS2_SOLVER_TESTING
    internal SolverRowProbe[] ProbeRowsForTest()
        => [.. _rows.Where(static row => row.Visible).Select(static row => row.ProbeForTest())];
#endif

    private void ShowEmpty(CombatUiSnapshot snapshot)
    {
        foreach (SolverRouteRow row in _rows)
            row.Visible = false;
        _more.Visible = false;
        _scroll.Visible = false;
        _summary.Text = "";
        _progress.Visible = false;
        _outcome.Visible = false;
        _empty.Visible = true;
        _windowStart = 0;
        _windowCount = 0;
        _empty.Text = snapshot.Phase switch
        {
            CombatUiPhase.Idle => SolverText.Get("开始全自动后，这里显示求解器规划的整场路线。"),
            CombatUiPhase.WaitingTurn => SolverText.Get("等待出牌阶段…"),
            CombatUiPhase.Searching => SolverText.Get("正在搜索整场路线…"),
            _ => SolverText.Get("没有可显示的路线。"),
        };
    }
}
