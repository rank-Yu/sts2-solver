// Adapted from CombatSolver src/UI/SolverActionBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Only the wired full-auto command is shown. Per-turn commands (execute this turn, adopt, freeze,
// recalculate) get their own row in TurnCommands once ICombatUiCommands exposes them.
using Godot;

namespace Sts2Solver;

/// <summary>Footer layout: the full-auto toggle, a reserved per-turn command row and the memory bar.</summary>
internal sealed partial class SolverActionBar : VBoxContainer
{
    private const long NoticeMilliseconds = 3500;

    private readonly Button _fullAuto;
    private readonly Label _hint;
    private readonly Label _notice;
    private SolverButtonStyle? _renderedStyle;
    private long _noticeUntil;

    /// <summary>Raised when the player presses the full-auto button; the shell sends the command.</summary>
    public event Action? FullAutoPressed;

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
        _fullAuto = SolverUiTokens.CreateButton("", SolverButtonStyle.Positive);
        _fullAuto.Name = "FullAutoButton";
        _fullAuto.CustomMinimumSize = new Vector2(148, SolverUiTokens.Size.ButtonHeight);
        _fullAuto.TooltipText = SolverText.Get("开始或停止本场战斗的全自动；F8 同样可以开关。停止时已经开始的动作会执行完。");
        _fullAuto.Pressed += () => FullAutoPressed?.Invoke();
        actions.AddChild(_fullAuto);
        _hint = SolverUiTokens.CreateLabel("F8", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextMuted, SolverFontWeight.Normal);
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

    public bool FullAutoRunning { get; private set; }

    public void Apply(CombatUiSnapshot snapshot, bool collapsed)
    {
        bool running = SolverPanelText.IsRunning(snapshot.Phase);
        bool stopping = running && snapshot.PauseReason != null;
        FullAutoRunning = running;
        string text = stopping ? SolverText.Get("正在停止…")
            : running ? SolverText.Get("停止全自动")
            : SolverText.Get("开始全自动");
        if (_fullAuto.Text != text)
            _fullAuto.Text = text;
        _fullAuto.Disabled = stopping;
        SolverButtonStyle style = running ? SolverButtonStyle.Danger : SolverButtonStyle.Positive;
        if (_renderedStyle != style)
        {
            _renderedStyle = style;
            SolverUiTokens.ApplyButtonStyle(_fullAuto, style);
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
    internal Button FullAutoButtonForTest => _fullAuto;
#endif
}
