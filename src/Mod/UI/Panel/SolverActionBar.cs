// Adapted from CombatSolver src/UI/SolverActionBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Only the mode switch is shown. Per-turn commands (execute this turn, adopt, recalculate) get
// their own row in TurnCommands once the panel is replaced in P3.
using Godot;

namespace Sts2Solver;

/// <summary>Footer layout: the full-auto toggle, a reserved per-turn command row and the memory bar.</summary>
internal sealed partial class SolverActionBar : VBoxContainer
{
    private const long NoticeMilliseconds = 3500;

    private readonly Button _mode;
    private readonly Label _hint;
    private readonly Label _notice;
    private SolverButtonStyle? _renderedStyle;
    private long _noticeUntil;

    /// <summary>Raised when the player presses the mode button; the shell sends the command.</summary>
    public event Action? ModePressed;

    /// <summary>
    /// Reserved for per-turn command buttons. It stays empty and hidden; buttons are only added
    /// together with the command that performs them, never as disabled placeholders.
    /// </summary>
    public HBoxContainer TurnCommands { get; }

    public SolverMemoryBar Memory { get; }

    public SolverActionBar()
    {
        Name = "Footer";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);

        HBoxContainer actions = new() { Name = "ActionRow", MouseFilter = MouseFilterEnum.Pass };
        actions.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        _mode = SolverUiTokens.CreateButton("", SolverButtonStyle.Positive);
        _mode.Name = "ModeButton";
        _mode.CustomMinimumSize = new Vector2(148, SolverUiTokens.Size.ButtonHeight);
        _mode.TooltipText = SolverText.Get("切换手动、自动搜索和全自动三档；切换不会打断正在进行的搜索或本回合执行。");
        _mode.Pressed += () => ModePressed?.Invoke();
        actions.AddChild(_mode);
        _hint = SolverUiTokens.CreateLabel(SolverText.Get("模式"),
            SolverUiTokens.Type.Caption, SolverUiTokens.Palette.TextMuted,
            SolverFontWeight.Normal);
        actions.AddChild(_hint);
        _notice = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption, SolverUiTokens.Palette.Warning);
        _notice.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _notice.Visible = false;
        actions.AddChild(_notice);
        Control spacer = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        actions.AddChild(spacer);
        TurnCommands = new HBoxContainer
        {
            Name = "TurnCommands",
            Visible = false,
            MouseFilter = MouseFilterEnum.Pass,
        };
        TurnCommands.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        actions.AddChild(TurnCommands);
        AddChild(actions);

        Memory = new SolverMemoryBar();
        AddChild(Memory);
    }

    public void Apply(CombatUiSnapshot snapshot, bool collapsed)
    {
        string text = SolverPanelText.Mode(snapshot.Mode);
        if (_mode.Text != text)
            _mode.Text = text;
        _mode.Disabled = snapshot.SolverDisabled;
        SolverButtonStyle style = snapshot.Mode == SolverMode.FullAuto
            ? SolverButtonStyle.Positive
            : SolverButtonStyle.Secondary;
        if (_renderedStyle != style)
        {
            _renderedStyle = style;
            SolverUiTokens.ApplyButtonStyle(_mode, style);
        }
        _hint.Visible = !collapsed;
        Memory.Visible = !collapsed;
        Memory.Apply(snapshot.Memory, snapshot.Search);
    }

    public void ShowNotice(string text, long nowMilliseconds)
    {
        _notice.Text = text;
        _notice.Visible = true;
        _noticeUntil = nowMilliseconds + NoticeMilliseconds;
    }

    public void Tick(long nowMilliseconds)
    {
        if (_notice.Visible && nowMilliseconds >= _noticeUntil)
            _notice.Visible = false;
    }

#if STS2_SOLVER_TESTING
    internal Button ModeButtonForTest => _mode;
#endif
}
