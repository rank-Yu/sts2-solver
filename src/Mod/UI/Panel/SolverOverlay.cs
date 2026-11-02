// Shell ported from CombatSolver src/UI/SolverOverlay.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The big floating-panel layout is kept: CanvasLayer 120, draggable header, right/bottom/corner
// resize, summary, route rows, details and action footer. The presentation is driven by
// CombatUiSnapshot and every command goes through ICombatUiCommands; the frozen route, shortcuts,
// online/bug-report surfaces, potion/growth/relic sidebars and the memory-release button are not
// ported. Theme is always dark.
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2Solver;

/// <summary>
/// The in-combat panel. Shown during single-player combat and hidden otherwise; it can only be
/// collapsed, never hidden. Main thread only: it never touches search or game actions.
/// </summary>
internal sealed partial class SolverOverlay : CanvasLayer
{
    internal const string NodeName = "Sts2SolverPanel";
    private const string LayerName = "Sts2SolverOverlay";
    private const long TickIntervalMilliseconds = 100;

    private static SolverOverlay? _instance;

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
    private VBoxContainer? _mainStack;
    private VBoxContainer? _body;
    private ColorRect? _footerDivider;
    private Button? _solverEnabledButton;
    private Button? _modeButton;
    private Button? _settingsButton;
    private Button? _collapseButton;
    private SolverDetailsPanel? _details;
    private SolverSettingsPanel? _settings;
    private CombatUiSnapshot? _lastSnapshot;
    private SolverOverlaySnapshot? _lastOverlay;
    private long _appliedSequence = -1;
    private long _nextTick;
    private bool _collapsed;
    private bool _settingsVisible;
    private bool _detailsVisible;
    private bool _rebuildQueued;
    private string? _builtLanguage;
    private int _buildCount;

    internal static SolverOverlay? Instance
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
        SolverLocaleRefresh.Bind(_localeAnchor, OnLocaleChanged);
        AddChild(_localeAnchor);
        LoadGeometry();
        _collapsed = SolverSettings.Current.Ui.Collapsed;
        Build();
        GetViewport().SizeChanged += QueueLayout;
        ModLog.Info("[Sts2Solver] UI_PANEL_READY shortcut=none layer=120");
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    public override void _Process(double delta)
    {
        bool show = InSinglePlayerCombat();
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
                _settingsVisible = false;
                _detailsVisible = false;
                _presentation = SolverOverlayPresentation.Ready;
                _waitingForNextTurnPlan = false;
                _deploymentOverlay = null;
                _lastResultCandidate = -1;
                _lastSearchingTurn = 0;
                _lastSearchStartedAt = 0;
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
        RefreshControls(snapshot);
    }

    private static bool InSinglePlayerCombat()
    {
        CombatManager manager = CombatManager.Instance;
        if (manager == null || !manager.IsInProgress)
            return false;
        CombatState? state = manager.DebugOnlyGetState();
        return state != null && state.Players.Count == 1
            && !CombatSessionController.IsMultiplayerSession;
    }

    private void Build()
    {
        SolverUiTokens.Configure(SolverUiTheme.Dark);
        _builtLanguage = LocManager.Instance?.Language;
        _buildCount++;

        _panelBox = SolverUiTokens.CreatePanelBox();
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
        root.AddChild(CreateSearchLimitHint());
        root.AddChild(CreatePerformanceHint());

        HBoxContainer contentColumns = new()
        {
            Name = "ContentColumns",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        contentColumns.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        root.AddChild(contentColumns);
        VBoxContainer primaryColumn = new()
        {
            Name = "PrimaryColumn",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        primaryColumn.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        contentColumns.AddChild(primaryColumn);
        primaryColumn.AddChild(SolverUiTokens.CreateDivider());

        _mainStack = new VBoxContainer
        {
            Name = "ContentAndActions",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _mainStack.AddThemeConstantOverride("separation", 0);
        primaryColumn.AddChild(_mainStack);

        _body = new VBoxContainer
        {
            Name = "Body",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _body.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _mainStack.AddChild(_body);
        BuildRouteSection();
        _body.AddChild(CreateSummarySection());
        _body.MoveChild(_summaryPanel!, 1);
        _details = new SolverDetailsPanel { Visible = false };
        _body.AddChild(_details);

        _footerDivider = SolverUiTokens.CreateDivider();
        _mainStack.AddChild(_footerDivider);
        MarginContainer footerArea = new()
        {
            Name = "FooterArea",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        footerArea.AddThemeConstantOverride("margin_top", SolverUiTokens.Spacing.Sm);
        footerArea.AddChild(CreateFooter());
        _mainStack.AddChild(footerArea);

        _settings = new SolverSettingsPanel { Visible = false };
        _settings.OpacityPreview += PreviewOpacity;
        _settings.DefaultsRestored += OnDefaultsRestored;
        primaryColumn.AddChild(_settings);

        AddChild(panel);
        _panel = panel;
        BuildResizeHandles();
        PreviewOpacity(SolverSettings.Current.Ui.Opacity);
        panel.MinimumSizeChanged += QueueLayout;
        if (_settingsVisible)
            _settings.Reload();
        ApplyContentVisibility();
        _appliedSequence = -1;
        _lastSnapshot = null;
        _lastOverlay = null;
        ModLog.Info(
            $"[Sts2Solver] UI_PANEL_BUILT theme=Dark language={_builtLanguage ?? "-"} " +
            $"collapsed={_collapsed} builds={_buildCount}");
    }

    private Control BuildHeader()
    {
        HBoxContainer header = new()
        {
            Name = "Header",
            CustomMinimumSize = new Vector2(0, 32f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        header.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        header.GuiInput += OnHeaderGuiInput;
        Label title = SolverUiTokens.CreateLabel(SolverText.Get("尖塔求解器"), SolverUiTokens.Type.Title,
            SolverUiTokens.Palette.TextPrimary);
        title.Name = "Title";
        title.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        header.AddChild(title);

        _solverEnabledButton = CreateHeaderButton("", 90);
        _solverEnabledButton.Name = "SolverEnabledButton";
        _solverEnabledButton.Pressed += OnSolverEnabledPressed;
        header.AddChild(_solverEnabledButton);

        _modeButton = CreateHeaderButton("", 96);
        _modeButton.Name = "ModeButton";
        _modeButton.TooltipText = SolverText.Get("切换手动、自动搜索和全自动；收起时也可用。");
        _modeButton.Pressed += OnModePressed;
        header.AddChild(_modeButton);

        header.AddChild(new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });

        _settingsButton = CreateHeaderButton(SolverText.Get("设置"), 54);
        _settingsButton.Name = "SettingsButton";
        _settingsButton.Pressed += ToggleSettings;
        header.AddChild(_settingsButton);
        _collapseButton = CreateHeaderButton(SolverText.Get("−  收起"), 64);
        _collapseButton.Name = "CollapseButton";
        _collapseButton.TooltipText = SolverText.Get("收起路线内容；标题栏保持可见。");
        _collapseButton.Pressed += ToggleCollapsed;
        header.AddChild(_collapseButton);
        return header;
    }

    private static Button CreateHeaderButton(string text, float minimumWidth)
    {
        Button button = SolverUiTokens.CreateButton(text, SolverButtonStyle.Secondary);
        button.CustomMinimumSize = new Vector2(minimumWidth, SolverUiTokens.Size.ButtonHeight);
        return button;
    }

    private void ApplySnapshot(CombatUiSnapshot snapshot, long now)
    {
        if (_summaryText == null || _actionBar == null || _details == null)
            return;
        CombatUiSnapshot? previous = _lastSnapshot;
        _lastSnapshot = snapshot;
        _appliedSequence = snapshot.Sequence;
        _nextTick = now + TickIntervalMilliseconds;

        CombatDisplayNames names = CaptureNames();
        SolverOverlaySnapshot overlay = SolverOverlaySnapshot.Build(snapshot, names);
        _lastOverlay = overlay;
        Present(overlay, snapshot, now);
        _details.Apply(snapshot);
        if (_settingsVisible)
            _settings?.ApplySnapshot(snapshot);
        if (previous == null || !ReferenceEquals(previous.Route, snapshot.Route)
            || !ReferenceEquals(previous.Search, snapshot.Search) || previous.Phase != snapshot.Phase)
        {
            QueueLayout();
        }
    }

    private static CombatDisplayNames CaptureNames()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        return state == null
            ? CombatDisplayNames.CaptureEmpty()
            : CombatDisplayNames.Capture(state);
    }

    private void Tick(long now)
    {
        _nextTick = now + TickIntervalMilliseconds;
        TickSummary(now);
        if (_lastSnapshot is { } snapshot)
            RefreshControls(snapshot);
    }

    private void Advance(double delta)
    {
        if (!Visible)
            return;
        AdvanceSearchReadouts(delta);
        _memoryBar?.Advance(delta);
    }

    private void OnSolverEnabledPressed()
    {
        CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
        bool enable = snapshot.SolverDisabled;
        bool accepted = CombatUiCommands.Instance.SetSolverEnabled(enable);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=solver_toggle enabled={enable} accepted={accepted}");
    }

    private void OnModePressed()
    {
        bool accepted = CombatUiCommands.Instance.CycleMode();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=cycle_mode mode={CombatSessionController.Mode} accepted={accepted}");
    }

    private void ToggleDetails()
    {
        SetDetailsVisible(!_detailsVisible);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=details visible={_detailsVisible}");
    }

    private void SetDetailsVisible(bool visible)
    {
        _detailsVisible = visible;
        if (_details != null)
        {
            _details.Visible = !_collapsed && visible;
            if (visible && _lastSnapshot != null)
                _details.Apply(_lastSnapshot);
        }
        _detailsButton?.SetExpanded(visible);
        ApplyContentVisibility();
    }

    private void ToggleSettings()
    {
        if (_settingsVisible && _settings?.CommitPending() == false)
            return;
        if (_collapsed)
            SetCollapsed(false);
        _settingsVisible = !_settingsVisible;
        if (_settingsVisible)
            _settings?.Reload();
        ApplyContentVisibility();
        SetPerformanceHintVisible(!_settingsVisible && _lastOverlay is { } overlay
            && overlay.ProjectedBattleHpLossKnown
            && IsSignificantBattleHpLoss(true, overlay.ProjectedBattleHpLost));
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
        if (_collapseButton != null)
            _collapseButton.Text = collapsed ? SolverText.Get("+  展开") : SolverText.Get("−  收起");
        if (_lastSnapshot != null && _lastOverlay != null)
            PopulateRoute(_lastOverlay, _lastSnapshot);
        ApplyContentVisibility();
        QueueLayout();
    }

    private void ApplyContentVisibility()
    {
        if (_panel == null)
            return;
        bool settings = _settingsVisible && !_collapsed;
        if (_mainStack != null)
            _mainStack.Visible = !settings || _collapsed;
        if (_body != null)
            _body.Visible = !_collapsed && !_settingsVisible;
        if (_settings != null)
            _settings.Visible = settings;
        if (_details != null)
            _details.Visible = !_collapsed && !_settingsVisible && _detailsVisible;
        if (_footerDivider != null)
            _footerDivider.Visible = !_collapsed && !_settingsVisible;
        if (_actionBar != null && _lastSnapshot != null)
            _actionBar.Refresh(new SolverActionBarState(_collapsed, _lastSnapshot.IsSearching,
                _lastSnapshot.CanAdoptCurrentRoute || _lastSnapshot.IsAdoptingRoute));
        if (_routeOutcomePanel != null && _mainStack != null && _body != null)
        {
            Node parent = _collapsed ? _mainStack : _body;
            if (_routeOutcomePanel.GetParent() != parent)
            {
                _routeOutcomePanel.Reparent(parent);
                parent.MoveChild(_routeOutcomePanel, 0);
            }
        }
        if (_settingsButton != null)
        {
            _settingsButton.Visible = !_collapsed;
            _settingsButton.Text = settings ? SolverText.Get("返回") : SolverText.Get("设置");
            _settingsButton.AddThemeColorOverride("font_color",
                settings ? SolverUiTokens.Palette.Accent : SolverUiTokens.Palette.TextSecondary);
        }
        SetResizeHandlesVisible(!_collapsed);
        QueueLayout();
    }

    private void PreviewOpacity(float opacity)
    {
        float alpha = Math.Clamp(opacity, 0.25f, 1f);
        if (_panel != null)
            _panel.Modulate = new Color(1f, 1f, 1f, alpha);
        foreach (Control? handle in new[] { _rightResizeHandle, _bottomResizeHandle, _cornerResizeHandle })
        {
            if (handle != null)
                handle.Modulate = new Color(1f, 1f, 1f, alpha);
        }
    }

    private void OnDefaultsRestored()
    {
        ResetOverlayPosition();
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
        _settings = null;
        _summaryText = null;
        _summaryPanel = null;
        _summaryStateLabel = null;
        _summaryContextLabel = null;
        _progressText = null;
        _reviewText = null;
        _searchProgressBar = null;
        _detailsButton = null;
        _routeScroll = null;
        _routeOutcomePanel = null;
        _routeHeadingLabel = null;
        _deathOutcomeLabel = null;
        _hpOutcomeLabel = null;
        _hpRecoveredOutcomeLabel = null;
        _actionBar = null;
        _memoryBar = null;
        _renderedMode = null;
        _presentation = SolverOverlayPresentation.Searching;
        _lastResultCandidate = -1;
        _lastSearchingTurn = 0;
        _lastSearchStartedAt = 0;
        _executeButton = null;
        _recalculateButton = null;
        _stopSearchButton = null;
        _adoptRouteButton = null;
        _footerDivider = null;
        _mainStack = null;
        _body = null;
        _details = null;
        FreeResizeHandles();
        Build();
    }

#if STS2_SOLVER_TESTING
    internal SolverOverlayProbe ProbeForTest() => new(
        _panel != null,
        Visible && _panel != null && _panel.Visible,
        false,
        _appliedSequence,
        _lastSnapshot?.Phase.ToString() ?? "",
        _collapsed,
        _settingsVisible && !_collapsed,
        _detailsVisible && !_collapsed,
        SolverUiTokens.ActiveTheme.ToString(),
        _builtLanguage ?? "",
        _buildCount,
        ProbeRowsForTest());

    internal Button? SolverEnabledButtonForTest => _solverEnabledButton;

    internal Button? ModeButtonForTest => _modeButton;

    internal Button? SettingsButtonForTest => _settingsButton;

    internal Button? CollapseButtonForTest => _collapseButton;

    internal Button? DetailsButtonForTest => _detailsButton;

    internal Button? RecalculateButtonForTest => _recalculateButton;

    internal Button? StopSearchButtonForTest => _stopSearchButton;

    internal Button? AdoptRouteButtonForTest => _adoptRouteButton;

    internal Button? ExecuteButtonForTest => _executeButton;

    internal SolverSettingsPanel? SettingsPanelForTest => _settings;

    internal SolverOverlayPresentation PresentationForTest => _presentation;

    internal string? StatusTextForTest => _summaryStateLabel?.Text;

    internal string? ContextTextForTest => _summaryContextLabel?.Text;

    internal string? SummaryTextForTest => _summaryText?.Text;

    internal int FontOverrideCountForTest => _panel != null ? SolverUiTokens.FontOverrideCount(_panel) : 0;

    internal string? RouteHeadingForTest => _routeHeadingLabel?.Text;

    internal SolverMemoryBar? MemoryBarForTest => _memoryBar;

    internal bool MemoryRowVisibleForTest => _actionBar?.MemoryRowVisible ?? false;

    internal SolverPanelInputBridge InputBridgeForTest => _inputBridge;
#endif
}

#if STS2_SOLVER_TESTING
internal sealed record SolverOverlayProbe(
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
