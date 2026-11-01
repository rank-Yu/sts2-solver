// Layout adapted from CreateSummarySection and SetStatus in CombatSolver src/UI/SolverOverlay.cs at
// 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors). The state machine is
// replaced by a projection of Sts2Solver's CombatUiSnapshot; nothing here reads the session.
using Godot;

namespace Sts2Solver;

/// <summary>Status badge, turn/HP context, the search line, the end/pause reason and a thin progress bar.</summary>
internal sealed partial class SolverStatusSection : PanelContainer
{
    private const double ProgressTimeConstantSeconds = 0.15d;

    private readonly PanelContainer _badge;
    private readonly Label _badgeLabel;
    private readonly Label _context;
    private readonly Label _searchLine;
    private readonly Label _reasonLine;
    private readonly ProgressBar _progress;
    private readonly StyleBoxFlat _progressFill;
    private CombatUiSnapshot? _snapshot;
    private double _progressTarget;
    private double _progressShown;
    private bool _progressInitialized;

    public SolverDetailsButton DetailsButton { get; }

    /// <summary>Stable phase key of the last applied snapshot (the enum name), for tests.</summary>
    public string PhaseKey { get; private set; } = "";

    public SolverStatusSection()
    {
        Name = "StatusSection";
        MouseFilter = MouseFilterEnum.Pass;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Sm));

        VBoxContainer layout = new() { MouseFilter = MouseFilterEnum.Pass };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs + 2);
        AddChild(layout);

        HBoxContainer top = new() { MouseFilter = MouseFilterEnum.Pass };
        top.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        (_badge, _badgeLabel) = CreateBadge();
        top.AddChild(_badge);
        _context = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Metric, SolverUiTokens.Palette.TextPrimary);
        _context.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _context.ClipText = true;
        top.AddChild(_context);
        DetailsButton = new SolverDetailsButton { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        top.AddChild(DetailsButton);
        layout.AddChild(top);

        _searchLine = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary, SolverFontWeight.Normal);
        _searchLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _searchLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        layout.AddChild(_searchLine);

        _reasonLine = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption, SolverUiTokens.Palette.Warning);
        _reasonLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _reasonLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        layout.AddChild(_reasonLine);

        _progress = new ProgressBar
        {
            Name = "Progress",
            MinValue = 0,
            MaxValue = 1,
            // Range rounds to Step; a 0.01 grid turns the eased fill back into visible steps.
            Step = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 4),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _progress.AddThemeStyleboxOverride("background", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.ProgressBackground, Colors.Transparent, 2, 0, 0, borderWidth: 0));
        _progressFill = SolverUiTokens.CreateBox(SolverUiTokens.Palette.Accent, Colors.Transparent, 2, 0, 0, borderWidth: 0);
        _progress.AddThemeStyleboxOverride("fill", _progressFill);
        layout.AddChild(_progress);
    }

    /// <summary>A rounded phase badge; also used in the collapsed header.</summary>
    internal static (PanelContainer Badge, Label Label) CreateBadge()
    {
        PanelContainer badge = new()
        {
            Name = "StatusBadge",
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        Label label = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption, SolverUiTokens.Palette.TextMuted);
        badge.AddChild(label);
        return (badge, label);
    }

    internal static void ApplyBadge(PanelContainer badge, Label label, CombatUiSnapshot snapshot)
    {
        Color tone = PhaseColor(snapshot);
        string text = SolverPanelText.Phase(snapshot.Phase, snapshot.EndReason);
        if (label.Text != text)
            label.Text = text;
        label.AddThemeColorOverride("font_color", tone);
        badge.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            new Color(tone, SolverUiTokens.IsLight ? 0.12f : 0.16f),
            new Color(tone, 0.5f),
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            1));
    }

    internal static Color PhaseColor(CombatUiSnapshot snapshot) => snapshot.Phase switch
    {
        CombatUiPhase.Idle => SolverUiTokens.Palette.TextMuted,
        CombatUiPhase.WaitingTurn => SolverUiTokens.Palette.TextSecondary,
        CombatUiPhase.Searching => SolverUiTokens.Palette.Info,
        CombatUiPhase.Deploying => SolverUiTokens.Palette.Accent,
        CombatUiPhase.Paused => SolverUiTokens.Palette.Warning,
        CombatUiPhase.Completed => snapshot.EndReason switch
        {
            "victory" => SolverUiTokens.Palette.Success,
            "defeat" => SolverUiTokens.Palette.Danger,
            _ => SolverUiTokens.Palette.TextSecondary,
        },
        CombatUiPhase.Cancelled => SolverUiTokens.Palette.TextMuted,
        CombatUiPhase.Failed => SolverUiTokens.Palette.Danger,
        _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.Phase, null),
    };

    public void Apply(CombatUiSnapshot snapshot, long nowMilliseconds)
    {
        CombatUiSnapshot? previous = _snapshot;
        _snapshot = snapshot;
        PhaseKey = snapshot.Phase.ToString();
        if (previous == null || previous.Phase != snapshot.Phase || previous.EndReason != snapshot.EndReason)
            ApplyBadge(_badge, _badgeLabel, snapshot);
        SetText(_context, Context(snapshot));
        (string? reason, Color tone) = Reason(snapshot);
        SetText(_reasonLine, reason ?? "");
        _reasonLine.Visible = reason != null;
        _reasonLine.AddThemeColorOverride("font_color", tone);
        Tick(nowMilliseconds);
    }

    /// <summary>Refreshes the live parts (elapsed time, member progress) between snapshots.</summary>
    public void Tick(long nowMilliseconds)
    {
        if (_snapshot is not { } snapshot)
            return;
        SearchUiSnapshot? search = snapshot.Search;
        string? line = search == null ? null : SearchLine(search, nowMilliseconds);
        SetText(_searchLine, line ?? "");
        _searchLine.Visible = line != null;

        double? target = null;
        Color fill = SolverUiTokens.Palette.Accent;
        if (snapshot.Phase == CombatUiPhase.Searching && search is { Phase: SearchUiPhase.Running })
        {
            fill = SolverUiTokens.Palette.Info;
            target = search.ActiveMemberRun >= 0 && search.ActiveMemberBudgetMilliseconds > 0
                ? Math.Clamp((nowMilliseconds - search.ActiveMemberStartedAtMilliseconds)
                    / (double)search.ActiveMemberBudgetMilliseconds, 0d, 1d)
                : 0d;
        }
        else if (snapshot.Route is { ActionCount: > 0 } route)
        {
            target = Math.Clamp(route.CompletedActions / (double)route.ActionCount, 0d, 1d);
        }
        _progress.Visible = target != null;
        if (target is { } value)
        {
            if (_progressFill.BgColor != fill)
                _progressFill.BgColor = fill;
            if (!_progressInitialized || value < _progressTarget - 0.5d)
            {
                _progressShown = value;
                _progressInitialized = true;
            }
            _progressTarget = value;
        }
    }

    public void Advance(double delta)
    {
        if (!_progress.Visible)
            return;
        double blend = SolverUiMotion.Blend(delta, ProgressTimeConstantSeconds);
        _progressShown = SolverUiMotion.Approach(_progressShown, _progressTarget, blend, 0.0005d);
        if (Math.Abs(_progress.Value - _progressShown) > 1e-6)
            _progress.Value = _progressShown;
    }

    private static string Context(CombatUiSnapshot snapshot)
    {
        if (snapshot.Turn > 0 && snapshot.PlayerMaxHp > 0)
            return SolverText.Format($"第 {snapshot.Turn} 回合 · 生命 {snapshot.PlayerHp}/{snapshot.PlayerMaxHp}");
        return snapshot.Phase == CombatUiPhase.Idle
            ? SolverText.Get("用底栏按钮选择手动、自动搜索或全自动")
            : "";
    }

    private static string SearchLine(SearchUiSnapshot search, long now)
    {
        string tier = SolverPanelText.Tier(search.Tier);
        if (search.Phase == SearchUiPhase.Running)
        {
            string total = SolverPanelText.Seconds(Math.Max(0, now - search.StartedAtMilliseconds));
            if (search.ActiveMemberRun >= 0)
            {
                string member = SolverPanelText.Seconds(Math.Max(0, now - search.ActiveMemberStartedAtMilliseconds));
                string budget = SolverPanelText.Seconds(search.ActiveMemberBudgetMilliseconds);
                return SolverText.Format(
                    $"{tier} · 第 {search.ActiveMemberRun + 1} 轮 · 宽度 {search.ActiveMemberWidth} · {member} / {budget} · 共 {total}");
            }
            return search.MemberRuns.Length == 0
                ? SolverText.Format($"{tier} · 正在准备搜索 · 共 {total}")
                : SolverText.Format($"{tier} · 已完成 {search.MemberRuns.Length} 轮 · 共 {total}");
        }
        string elapsed = SolverPanelText.Seconds(search.ElapsedMilliseconds);
        return search.Status switch
        {
            "ok" => SolverText.Format(
                $"{tier} · 搜索完成 · 用时 {elapsed} · 运行 {search.MembersRun}/{search.Members.Length} 轮 · 展开 {SolverPanelText.Count(search.ExpandedNodes)} 个节点"),
            "cancelled" => SolverText.Format($"{tier} · 搜索已取消 · 用时 {elapsed}"),
            _ => SolverText.Format($"{tier} · 搜索失败 · 用时 {elapsed}"),
        };
    }

    private static (string? Text, Color Tone) Reason(CombatUiSnapshot snapshot)
    {
        switch (snapshot.Phase)
        {
            case CombatUiPhase.Paused:
                return (SolverPanelText.Pause(snapshot.PauseCode, snapshot.Turn), SolverUiTokens.Palette.Warning);
            case CombatUiPhase.Cancelled:
                return (SolverPanelText.Cancel(snapshot.PauseCode), SolverUiTokens.Palette.TextMuted);
            case CombatUiPhase.Failed:
                return (SolverPanelText.Error(snapshot.ErrorCode ?? snapshot.EndReason ?? "pilot_failed"),
                    SolverUiTokens.Palette.Danger);
            case CombatUiPhase.Completed:
                return snapshot.EndReason switch
                {
                    "victory" => (SolverText.Get("战斗胜利，全自动已结束"), SolverUiTokens.Palette.Success),
                    "defeat" => (SolverText.Get("战斗失败"), SolverUiTokens.Palette.Danger),
                    _ => (SolverText.Get("战斗已结束"), SolverUiTokens.Palette.TextSecondary),
                };
        }
        if (SolverPanelText.IsRunning(snapshot.Phase) && snapshot.PauseReason != null)
            return (SolverText.Get("正在停止…"), SolverUiTokens.Palette.TextMuted);
        if (snapshot.ErrorCode is { } code)
        {
            return (SolverPanelText.Error(code), code == "cancelled"
                ? SolverUiTokens.Palette.TextMuted
                : SolverUiTokens.Palette.Warning);
        }
        return (null, SolverUiTokens.Palette.TextMuted);
    }

    private static void SetText(Label label, string text)
    {
        if (label.Text != text)
            label.Text = text;
    }
}
