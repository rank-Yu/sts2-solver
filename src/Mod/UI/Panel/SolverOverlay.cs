// Shell ported from CombatSolver src/UI/SolverOverlay.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The big floating-panel layout is kept: CanvasLayer 120, draggable header, right/bottom/corner
// resize, summary, route rows, details and action footer. The presentation is driven by
// CombatUiSnapshot and every command goes through ICombatUiCommands. The header's 药水 / 成长 open
// the strategy sidebar docked beside the panel and 设置 replaces the panel body with the settings
// page (ToggleSettings, TogglePotionStrategy, ApplyContentVisibility); the frozen route, shortcuts,
// online/bug-report surfaces, the relic sidebar and the memory-release button are not ported. Theme
// is always dark.
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
    private Button? _potionButton;
    private Button? _growthButton;
    private Button? _settingsButton;
    private Button? _collapseButton;
    private SolverSidebar? _sidebar;
    private SolverPotionStrategyPage? _potionPage;
    private SolverGrowthStrategyPage? _growthPage;
    private SolverSettingsPage? _settingsPage;
    private CombatUiSnapshot? _lastSnapshot;
    private SolverOverlaySnapshot? _lastOverlay;
    private long _appliedSequence = -1;
    private long _nextTick;
    private bool _collapsed;
    private SolverStrategyPageKind? _openStrategyPage;
    private bool _settingsVisible;
    private bool _rebuildQueued;
    private string? _builtLanguage;
    private int _buildCount;
    private bool _initialized;
    private bool _deferLogged;

    internal static SolverOverlay? Instance
        => _instance != null && IsInstanceValid(_instance) ? _instance : null;

    public override void _Ready()
    {
        Name = NodeName;
        Layer = 120;
        Visible = false;
        TryInitialize();
    }

    /// <summary>
    /// Builds the panel once the game's localization exists. A Steam launch runs mod initializers
    /// inside <c>OneTimeInitialization.ExecuteVeryEarly</c> after the Steam branch check, when
    /// <c>NGame</c> is already ready (so this node's <c>_Ready</c> runs at once) but
    /// <c>ExecuteEssential</c> has not created <see cref="LocManager"/> or loaded the essential
    /// assets yet; the build then waits for <see cref="_Process"/>. ExecuteEssential runs
    /// synchronously, so the first frame that sees <see cref="LocManager.Instance"/> also sees the
    /// rest of the essential initialization.
    /// </summary>
    private void TryInitialize()
    {
        if (_initialized)
            return;
        if (LocManager.Instance == null)
        {
            if (!_deferLogged)
            {
                _deferLogged = true;
                ModLog.Info("[Sts2Solver] UI_PANEL_DEFERRED reason=localization_not_ready");
            }
            return;
        }
        _initialized = true;
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
        if (!_initialized)
        {
            TryInitialize();
            if (!_initialized)
                return;
        }
        bool show = InSinglePlayerCombat();
        if (Visible != show)
        {
            Visible = show;
            if (show)
            {
                _appliedSequence = -1;
                _openStrategyPage = null;
                _settingsVisible = false;
                ApplyContentVisibility();
            }
            else
            {
                CancelPointerGesture();
                _presentation = SolverOverlayPresentation.Ready;
                _routeMotionKey = null;
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
        root.AddChild(CreateTheftNotice());
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

        _settingsPage = new SolverSettingsPage();
        _settingsPage.OpacityPreview += PreviewOpacity;
        _settingsPage.DefaultsRestored += OnDefaultsRestored;
        primaryColumn.AddChild(_settingsPage);

        AddChild(panel);
        _panel = panel;
        // Labels that swap keep the width of the wider one, so the header and notice never jump.
        SolverUiTokens.FitWidestText(_settingsButton!, SolverText.Get("设置"), SolverText.Get("返回"));
        SolverUiTokens.FitWidestText(_theftNoticeButton!, SolverText.Get("改为放走"), SolverText.Get("改为保住"));
        _potionPage = new SolverPotionStrategyPage();
        _growthPage = new SolverGrowthStrategyPage();
        _sidebar = new SolverSidebar(_potionPage, _growthPage);
        _sidebar.CloseRequested += CloseSidebar;
        AddChild(_sidebar);
        BuildResizeHandles();
        PreviewOpacity(SolverSettings.Current.Ui.Opacity);
        panel.MinimumSizeChanged += QueueLayout;
        if (_openStrategyPage is { } page)
            _sidebar.Open(page);
        if (_settingsVisible)
            _settingsPage.Opened();
        ApplyContentVisibility();
        _appliedSequence = -1;
        _lastSnapshot = null;
        _lastOverlay = null;
        _rendered.Clear();
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

        header.AddChild(new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });

        _potionButton = CreateStrategyButton(SolverText.Get("药水"), "PotionStrategyButton",
            SolverStrategyPageKind.Potions);
        header.AddChild(_potionButton);
        _growthButton = CreateStrategyButton(SolverText.Get("成长"), "GrowthStrategyButton",
            SolverStrategyPageKind.Growth);
        header.AddChild(_growthButton);
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

    /// <summary>A header toggle that opens the sidebar on its page; pressed while that page is open.</summary>
    private Button CreateStrategyButton(string text, string name, SolverStrategyPageKind page)
    {
        Button button = CreateHeaderButton(text, 48);
        button.Name = name;
        button.ToggleMode = true;
        button.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextSecondary);
        button.AddThemeColorOverride("font_pressed_color", SolverUiTokens.Palette.Accent);
        button.AddThemeColorOverride("font_hover_pressed_color", SolverUiTokens.Palette.Accent);
        button.Pressed += () => ToggleStrategyPage(page);
        return button;
    }

    private void ApplySnapshot(CombatUiSnapshot snapshot, long now)
    {
        if (_summaryText == null || _actionBar == null)
            return;
        CombatUiSnapshot? previous = _lastSnapshot;
        _lastSnapshot = snapshot;
        _appliedSequence = snapshot.Sequence;
        _nextTick = now + TickIntervalMilliseconds;

        CombatDisplayNames names = CaptureNames();
        SolverOverlaySnapshot overlay = SolverOverlaySnapshot.Build(snapshot, names);
        _lastOverlay = overlay;
        Present(overlay, snapshot, now);
        if (_openStrategyPage != null)
            _sidebar?.Apply(snapshot);
        if (_settingsVisible)
            _settingsPage?.Apply(snapshot);
        ApplyTheftNotice(snapshot.Strategy);
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

    /// <summary>
    /// 设置 / 返回: swaps the panel body for the settings page and closes the strategy sidebar.
    /// Nothing changes while a page holds an invalid input.
    /// </summary>
    private void ToggleSettings()
    {
        if (_settingsVisible)
        {
            if (_settingsPage?.CommitPending() == false)
                return;
            _settingsVisible = false;
        }
        else
        {
            CloseSidebar();
            if (_openStrategyPage != null)
                return;
            _settingsVisible = true;
            _settingsPage?.Opened();
        }
        ApplyContentVisibility();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=settings visible={_settingsVisible}");
    }

    /// <summary>
    /// 药水 / 成长: opens the sidebar on that page, switches pages, or closes the sidebar when the page
    /// is already open. Open settings are closed first, like the original.
    /// </summary>
    private void ToggleStrategyPage(SolverStrategyPageKind page)
    {
        if (_settingsVisible)
        {
            if (_settingsPage?.CommitPending() == false)
            {
                ApplyContentVisibility();
                return;
            }
            _settingsVisible = false;
        }
        if (_openStrategyPage == page)
        {
            CloseSidebar();
            ApplyContentVisibility();
            return;
        }
        if (_openStrategyPage != null && _sidebar?.CommitPending() == false)
        {
            ApplyContentVisibility();
            return;
        }
        _openStrategyPage = page;
        _sidebar?.Open(page);
        ApplyContentVisibility();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=sidebar page={page}");
    }

    /// <summary>Closes the sidebar unless its page holds an invalid input.</summary>
    private void CloseSidebar()
    {
        if (_openStrategyPage == null || _sidebar?.CommitPending() == false)
            return;
        _openStrategyPage = null;
        ApplyContentVisibility();
        ModLog.Info("[Sts2Solver] UI_ACTION action=sidebar page=none");
    }

    /// <summary>Collapsing closes the sidebar and keeps the settings state, like the original.</summary>
    private void ToggleCollapsed()
    {
        if (!_collapsed)
        {
            if (_settingsVisible && _settingsPage?.CommitPending() == false)
                return;
            CloseSidebar();
            if (_openStrategyPage != null)
                return;
        }
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
        bool settingsShown = _settingsVisible && !_collapsed;
        if (_mainStack != null)
            _mainStack.Visible = !settingsShown;
        if (_body != null)
            _body.Visible = !_collapsed && !_settingsVisible;
        if (_settingsPage != null)
            _settingsPage.Visible = settingsShown;
        if (_sidebar != null)
            _sidebar.Visible = _openStrategyPage != null && !_collapsed;
        if (_footerDivider != null)
            _footerDivider.Visible = !_collapsed && !_settingsVisible;
        if (_actionBar != null)
        {
            if (_lastSnapshot != null)
                RefreshControls(_lastSnapshot);
            else
                _actionBar.Refresh(new SolverActionBarState(_collapsed, true, false, false, false, false));
        }
        if (_routeOutcomePanel != null && _mainStack != null && _body != null)
        {
            Node parent = _collapsed ? _mainStack : _body;
            if (_routeOutcomePanel.GetParent() != parent)
            {
                _routeOutcomePanel.Reparent(parent);
                parent.MoveChild(_routeOutcomePanel, 0);
            }
        }
        ApplyStrategyButton(_potionButton, SolverStrategyPageKind.Potions);
        ApplyStrategyButton(_growthButton, SolverStrategyPageKind.Growth);
        if (_settingsButton != null)
        {
            _settingsButton.Visible = !_collapsed;
            _settingsButton.Text = SolverText.Get(_settingsVisible ? "返回" : "设置");
            _settingsButton.AddThemeColorOverride("font_color",
                _settingsVisible ? SolverUiTokens.Palette.Accent : SolverUiTokens.Palette.TextSecondary);
        }
        ApplyNoticeVisibility();
        SetResizeHandlesVisible(!_collapsed);
        QueueLayout();
    }

    private void ApplyStrategyButton(Button? button, SolverStrategyPageKind page)
    {
        if (button == null)
            return;
        button.Visible = !_collapsed;
        button.SetPressedNoSignal(_openStrategyPage == page);
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
        SetPerformanceHintVisible(_presentation == SolverOverlayPresentation.Ready
            && _lastOverlay is { ProjectedBattleHpLossKnown: true } overlay
            && IsSignificantBattleHpLoss(true, overlay.ProjectedBattleHpLost));
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
        if (_sidebar != null)
        {
            RemoveChild(_sidebar);
            _sidebar.QueueFree();
            _sidebar = null;
        }
        _settingsPage = null;
        _potionPage = null;
        _growthPage = null;
        _summaryText = null;
        _summaryPanel = null;
        _summaryStateLabel = null;
        _summaryContextLabel = null;
        _progressText = null;
        _reviewText = null;
        _routeScroll = null;
        _routeOutcomePanel = null;
        _routeHeadingLabel = null;
        _deathOutcomeLabel = null;
        _hpOutcomeLabel = null;
        _hpRecoveredOutcomeLabel = null;
        _actionBar = null;
        _memoryBar = null;
        _rendered.Clear();
        _presentation = SolverOverlayPresentation.Searching;
        _lastResultCandidate = -1;
        _lastSearchingTurn = 0;
        _lastSearchStartedAt = 0;
        _autoSearchSwitch = null;
        _searchButton = null;
        _restartButton = null;
        _executeTurnButton = null;
        _executeCombatButton = null;
        _stopButton = null;
        _footerDivider = null;
        _mainStack = null;
        _body = null;
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
        _openStrategyPage != null && !_collapsed ? _openStrategyPage.Value.ToString() : "",
        _builtLanguage ?? "",
        _buildCount,
        _presentation.ToString(),
        ProbeRowsForTest());

    internal int LastOverlayTurnCountForTest => _lastOverlay?.Turns.Count ?? -1;

    internal CheckButton? AutoSearchSwitchForTest => _autoSearchSwitch;

    internal Button? PotionButtonForTest => _potionButton;

    internal Button? GrowthButtonForTest => _growthButton;

    internal Button? SettingsButtonForTest => _settingsButton;

    internal bool SettingsOpenForTest => _settingsVisible;

    internal Button? CollapseButtonForTest => _collapseButton;

    internal Button? SearchButtonForTest => _searchButton;

    internal Button? RestartSearchButtonForTest => _restartButton;

    internal Button? ExecuteTurnButtonForTest => _executeTurnButton;

    internal Button? ExecuteCombatButtonForTest => _executeCombatButton;

    internal Button? StopExecutionButtonForTest => _stopButton;

    internal SolverSidebar? SidebarForTest => _sidebar;

    internal SolverSettingsPage? SettingsPageForTest => _settingsPage;

    internal SolverPotionStrategyPage? PotionPageForTest => _potionPage;

    internal SolverGrowthStrategyPage? GrowthPageForTest => _growthPage;

    internal PanelContainer? TheftNoticeForTest => _theftNotice;

    internal Label? TheftNoticeLabelForTest => _theftNoticeLabel;

    internal Button? TheftNoticeButtonForTest => _theftNoticeButton;

    internal Control? MainStackForTest => _mainStack;

    internal PanelContainer? PanelForTest => _panel;

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
    string SidebarPage,
    string Language,
    int BuildCount,
    string Presentation,
    SolverRowProbe[] Rows);
#endif
