// Shell rewritten for Sts2Solver after CombatSolver src/UI/SolverOverlay.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The big floating-panel layout
// (header, status summary, route rows, details, action bar, settings host), the CanvasLayer and
// the theme rebuild are kept; the panel reads only CombatUiStore snapshots and sends commands only
// through CombatUiCommands, instead of reading and driving CombatSolver's SolverController.
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2Solver;

/// <summary>
/// The in-combat panel. Shown during single-player combat and hidden otherwise; Ctrl+F9 hides it
/// for the rest of the session. Main thread only: it never touches search or game actions.
/// </summary>
internal sealed partial class SolverPanelNode : CanvasLayer
{
    internal const string NodeName = "Sts2SolverPanel";
    private const long TickIntervalMilliseconds = 100;

    private static SolverPanelNode? _instance;

    private readonly SolverPanelInputBridge _inputBridge = new() { Name = "Sts2SolverPanelInput" };
    private readonly SolverOverlayMotionDriver _motion = new() { Name = "Sts2SolverPanelMotion" };
    private readonly Control _localeAnchor = new()
    {
        Name = "LocaleAnchor",
        MouseFilter = Control.MouseFilterEnum.Ignore,
        Visible = false,
    };

    private PanelContainer? _panel;
    private StyleBoxFlat? _panelBox;
    private PanelContainer? _headerBadge;
    private Label? _headerBadgeLabel;
    private Button? _settingsButton;
    private Button? _collapseButton;
    private VBoxContainer? _mainStack;
    private VBoxContainer? _body;
    private ColorRect? _footerDivider;
    private SolverStatusSection? _status;
    private SolverRouteSection? _route;
    private SolverDetailsPanel? _details;
    private SolverActionBar? _actionBar;
    private SolverSettingsPanel? _settings;
    private CombatUiSnapshot? _lastSnapshot;
    private long _appliedSequence = -1;
    private long _nextTick;
    private bool _collapsed;
    private bool _settingsVisible;
    private bool _detailsVisible;
    private bool _hiddenByShortcut;
    private bool _rebuildQueued;
    private string? _builtLanguage;
    private int _buildCount;

    internal static SolverPanelNode? Instance
        => _instance != null && IsInstanceValid(_instance) ? _instance : null;

    public override void _Ready()
    {
        Name = NodeName;
        Layer = 120;
        Visible = false;
        _instance = this;
        _inputBridge.Panel = this;
        AddChild(_inputBridge);
        _motion.AdvanceReadouts = Advance;
        AddChild(_motion);
        // Bind before the anchor enters the tree: the refresher registers on TreeEntered.
        SolverLocaleRefresh.Bind(_localeAnchor, OnLocaleChanged);
        AddChild(_localeAnchor);
        LoadGeometry();
        _collapsed = SolverSettings.Current.Ui.Collapsed;
        Build();
        GetViewport().SizeChanged += QueueLayout;
        ModLog.Info("[Sts2Solver] UI_PANEL_READY toggle=Ctrl+F9 layer=120");
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    public override void _Process(double delta)
    {
        bool show = InSinglePlayerCombat() && !_hiddenByShortcut;
        if (Visible != show)
        {
            Visible = show;
            if (show)
            {
                _appliedSequence = -1;
                QueueLayout();
            }
            else
            {
                CancelPointerGesture();
            }
        }
        if (!show || _panel == null)
            return;
        long now = System.Environment.TickCount64;
        CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
        if (snapshot.Sequence != _appliedSequence)
            ApplySnapshot(snapshot, now);
        else if (now >= _nextTick)
            Tick(now);
    }

    /// <summary>Ctrl+F9. Only acts during single-player combat, like the original shortcut.</summary>
    internal bool ToggleFromShortcut()
    {
        if (!InSinglePlayerCombat())
            return false;
        _hiddenByShortcut = !_hiddenByShortcut;
        ModLog.Info($"[Sts2Solver] UI_PANEL_TOGGLE source=shortcut visible={!_hiddenByShortcut}");
        return true;
    }

    private static bool InSinglePlayerCombat()
    {
        CombatManager manager = CombatManager.Instance;
        if (manager == null || !manager.IsInProgress)
            return false;
        CombatState? state = manager.DebugOnlyGetState();
        return state != null && state.Players.Count == 1;
    }

    private void Build()
    {
        SolverSettingsData settings = SolverSettings.Current;
        SolverUiTokens.Configure(settings.Ui.Theme);
        _builtLanguage = LocManager.Instance?.Language;
        _buildCount++;

        _panelBox = SolverUiTokens.CreatePanelBox(settings.Ui.Opacity);
        PanelContainer panel = new()
        {
            Name = "Panel",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        panel.AddThemeStyleboxOverride("panel", _panelBox);
        VBoxContainer root = new()
        {
            Name = "Layout",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        panel.AddChild(root);
        root.AddChild(BuildHeader());

        _mainStack = new VBoxContainer
        {
            Name = "Main",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _mainStack.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        root.AddChild(_mainStack);

        _status = new SolverStatusSection();
        _status.DetailsButton.Pressed += ToggleDetails;
        _mainStack.AddChild(_status);

        _body = new VBoxContainer
        {
            Name = "Body",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _body.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _route = new SolverRouteSection();
        _body.AddChild(_route);
        _details = new SolverDetailsPanel { Visible = false };
        _body.AddChild(_details);
        _mainStack.AddChild(_body);

        _footerDivider = SolverUiTokens.CreateDivider();
        _mainStack.AddChild(_footerDivider);
        _actionBar = new SolverActionBar();
        _actionBar.FullAutoPressed += OnFullAutoPressed;
        _mainStack.AddChild(_actionBar);

        _settings = new SolverSettingsPanel { Visible = false };
        _settings.OpacityPreview += PreviewOpacity;
        _settings.ThemeSaved += QueueRebuild;
        _settings.DefaultsRestored += OnDefaultsRestored;
        root.AddChild(_settings);

        AddChild(panel);
        _panel = panel;
        BuildResizeHandles();
        panel.MinimumSizeChanged += QueueLayout;
        if (_settingsVisible)
            _settings.Reload();
        ApplyContentVisibility();
        _appliedSequence = -1;
        _lastSnapshot = null;
        ModLog.Info(
            $"[Sts2Solver] UI_PANEL_BUILT theme={SolverUiTokens.ActiveTheme} language={_builtLanguage ?? "-"} " +
            $"collapsed={_collapsed} builds={_buildCount}");
    }

    private Control BuildHeader()
    {
        HBoxContainer header = new()
        {
            Name = "Header",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.HeaderHeight),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        header.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        header.GuiInput += OnHeaderGuiInput;
        header.AddChild(new ColorRect
        {
            Color = SolverUiTokens.Palette.Accent,
            CustomMinimumSize = new Vector2(3, 18),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        Label title = SolverUiTokens.CreateLabel(SolverText.Get("尖塔求解器"), SolverUiTokens.Type.Title,
            SolverUiTokens.Palette.TextPrimary);
        title.Name = "Title";
        header.AddChild(title);
        (_headerBadge, _headerBadgeLabel) = SolverStatusSection.CreateBadge();
        header.AddChild(_headerBadge);
        header.AddChild(new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        _settingsButton = SolverUiTokens.CreateButton("", SolverButtonStyle.Ghost);
        _settingsButton.Name = "SettingsButton";
        _settingsButton.CustomMinimumSize = new Vector2(64, 28);
        _settingsButton.Pressed += ToggleSettings;
        header.AddChild(_settingsButton);
        _collapseButton = SolverUiTokens.CreateButton("", SolverButtonStyle.Ghost);
        _collapseButton.Name = "CollapseButton";
        _collapseButton.CustomMinimumSize = new Vector2(64, 28);
        _collapseButton.TooltipText = SolverText.Get("收起或展开面板内容；Ctrl+F9 显示或隐藏整个面板。");
        _collapseButton.Pressed += ToggleCollapsed;
        header.AddChild(_collapseButton);
        return header;
    }

    private void ApplySnapshot(CombatUiSnapshot snapshot, long now)
    {
        if (_status == null || _route == null || _actionBar == null || _details == null)
            return;
        CombatUiSnapshot? previous = _lastSnapshot;
        _status.Apply(snapshot, now);
        if (_headerBadge != null && _headerBadgeLabel != null
            && (previous == null || previous.Phase != snapshot.Phase || previous.EndReason != snapshot.EndReason))
        {
            SolverStatusSection.ApplyBadge(_headerBadge, _headerBadgeLabel, snapshot);
        }
        _route.Apply(snapshot);
        if (_detailsVisible)
            _details.Apply(snapshot);
        _actionBar.Apply(snapshot, _collapsed);
        if (_settingsVisible)
            _settings?.ApplySnapshot(snapshot);
        _appliedSequence = snapshot.Sequence;
        _lastSnapshot = snapshot;
        _nextTick = now + TickIntervalMilliseconds;
        if (previous == null || !ReferenceEquals(previous.Route, snapshot.Route)
            || !ReferenceEquals(previous.Search, snapshot.Search) || previous.Phase != snapshot.Phase)
        {
            QueueLayout();
        }
    }

    private void Tick(long now)
    {
        _nextTick = now + TickIntervalMilliseconds;
        _status?.Tick(now);
        _actionBar?.Tick(now);
    }

    private void Advance(double delta)
    {
        if (!Visible)
            return;
        _status?.Advance(delta);
        _actionBar?.Memory.Advance(delta);
    }

    private void OnFullAutoPressed()
    {
        if (_actionBar == null)
            return;
        if (_actionBar.FullAutoRunning)
        {
            ModLog.Info("[Sts2Solver] UI_ACTION action=stop_full_auto");
            CombatUiCommands.Instance.StopAuto("ui");
            return;
        }
        ModLog.Info("[Sts2Solver] UI_ACTION action=start_full_auto");
        if (!CombatUiCommands.Instance.StartAuto())
            _actionBar.ShowNotice(SolverText.Get("现在不能开始：请在自己的出牌阶段再试。"), System.Environment.TickCount64);
    }

    private void ToggleDetails()
    {
        _detailsVisible = !_detailsVisible;
        if (_detailsVisible && _lastSnapshot != null)
            _details?.Apply(_lastSnapshot);
        ApplyContentVisibility();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=details visible={_detailsVisible}");
    }

    private void ToggleSettings()
    {
        if (_settingsVisible && _settings?.CommitPending() == false)
            return;
        _settingsVisible = !_settingsVisible;
        if (_settingsVisible)
        {
            if (_collapsed)
                SetCollapsed(false);
            _settings?.Reload();
        }
        ApplyContentVisibility();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=settings visible={_settingsVisible}");
    }

    private void ToggleCollapsed()
    {
        if (_settingsVisible && _settings?.CommitPending() == false)
            return;
        if (!_collapsed)
            _settingsVisible = false;
        SetCollapsed(!_collapsed);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=collapse collapsed={_collapsed}");
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        if (SolverSettings.Current.Ui.Collapsed != collapsed)
            SolverSettings.Update(current => current with { Ui = current.Ui with { Collapsed = collapsed } });
        ApplyContentVisibility();
        if (_lastSnapshot != null)
            _actionBar?.Apply(_lastSnapshot, _collapsed);
    }

    private void ApplyContentVisibility()
    {
        if (_panel == null)
            return;
        bool settings = _settingsVisible && !_collapsed;
        if (_headerBadge != null)
            _headerBadge.Visible = _collapsed;
        if (_settingsButton != null)
        {
            _settingsButton.Visible = !_collapsed;
            _settingsButton.Text = settings ? SolverText.Get("返回") : SolverText.Get("设置");
            _settingsButton.AddThemeColorOverride("font_color",
                settings ? SolverUiTokens.Palette.Accent : SolverUiTokens.Palette.TextSecondary);
        }
        if (_collapseButton != null)
            _collapseButton.Text = _collapsed ? SolverText.Get("展开") : SolverText.Get("收起");
        if (_mainStack != null)
            _mainStack.Visible = !settings;
        if (_status != null)
        {
            _status.Visible = !_collapsed;
            _status.DetailsButton.SetExpanded(_detailsVisible);
        }
        if (_body != null)
            _body.Visible = !_collapsed;
        if (_details != null)
            _details.Visible = !_collapsed && _detailsVisible;
        if (_footerDivider != null)
            _footerDivider.Visible = !_collapsed;
        if (_settings != null)
            _settings.Visible = settings;
        SetResizeHandlesVisible(!_collapsed);
        QueueLayout();
    }

    private void PreviewOpacity(float opacity)
    {
        if (_panelBox != null)
            SolverUiTokens.ApplyPanelOpacity(_panelBox, opacity);
    }

    private void OnDefaultsRestored()
    {
        ResetGeometry();
        if (_collapsed)
            SetCollapsed(false);
        QueueLayout();
    }

    private void OnLocaleChanged()
    {
        if (_panel == null || LocManager.Instance?.Language == _builtLanguage)
            return;
        QueueRebuild();
    }

    /// <summary>Theme and language changes rebuild every control so colors, fonts, texts and tooltips are all fresh.</summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        Callable.From(Rebuild).CallDeferred();
    }

    private void Rebuild()
    {
        _rebuildQueued = false;
        CancelPointerGesture();
        if (_panel != null)
        {
            RemoveChild(_panel);
            _panel.QueueFree();
            _panel = null;
        }
        FreeResizeHandles();
        Build();
    }

#if STS2_SOLVER_TESTING
    internal SolverPanelProbe ProbeForTest() => new(
        _panel != null,
        Visible && _panel != null && _panel.Visible,
        _hiddenByShortcut,
        _appliedSequence,
        _status?.PhaseKey ?? "",
        _collapsed,
        _settingsVisible && !_collapsed,
        _detailsVisible && !_collapsed,
        SolverUiTokens.ActiveTheme.ToString(),
        _builtLanguage ?? "",
        _buildCount,
        _route?.ProbeRowsForTest() ?? []);

    internal Button? SettingsButtonForTest => _settingsButton;

    internal Button? CollapseButtonForTest => _collapseButton;

    internal Button? DetailsButtonForTest => _status?.DetailsButton;

    internal OptionButton? ThemeOptionForTest => _settings?.ThemeOptionForTest;

    internal SolverPanelInputBridge InputBridgeForTest => _inputBridge;
#endif
}

#if STS2_SOLVER_TESTING
internal sealed record SolverPanelProbe(
    bool Exists,
    bool Visible,
    bool HiddenByShortcut,
    long AppliedSequence,
    string PhaseKey,
    bool Collapsed,
    bool SettingsVisible,
    bool DetailsVisible,
    string Theme,
    string Language,
    int BuildCount,
    SolverRowProbe[] Rows);
#endif
