// Sidebar shell adapted from CombatSolver src/UI/SolverGrowthStrategyPanel.cs (box, heading row and
// close button) and src/UI/SolverSettingsPanel.cs (page tab buttons) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). One container hosts every sidebar
// page; the tab bar is shown only when more than one page is registered.
using Godot;

namespace Sts2Solver;

internal enum SolverSidebarPageKind
{
    Settings,
}

/// <summary>A page hosted by <see cref="SolverSidebar"/>.</summary>
internal interface ISolverSidebarPage
{
    SolverSidebarPageKind Kind { get; }

    string Title { get; }

    Control Root { get; }

    /// <summary>Buttons shown in the heading row while this page is active.</summary>
    Control HeaderActions { get; }

    /// <summary>Reloads the page from the current settings; called each time the page opens.</summary>
    void Opened();

    void Apply(CombatUiSnapshot snapshot);

    /// <summary>Saves inputs still being edited; false keeps the page open on an invalid value.</summary>
    bool CommitPending();
}

/// <summary>
/// The sidebar docked beside the panel. The overlay owns its placement and visibility; the sidebar
/// only switches pages and reports the close button.
/// </summary>
internal sealed partial class SolverSidebar : PanelContainer
{
    internal const float PreferredWidth = 390f;

    private readonly ISolverSidebarPage[] _pages;
    private readonly HBoxContainer _tabBar;
    private readonly Button[] _tabs;
    private readonly Label _title;
    private readonly Button _close;
    private int _active;

    public event Action? CloseRequested;

    public SolverSidebar(params ISolverSidebarPage[] pages)
    {
        if (pages.Length == 0)
            throw new ArgumentException("侧栏至少要有一页。", nameof(pages));
        _pages = pages;
        Name = "Sidebar";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Sm));
        VBoxContainer layout = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        AddChild(layout);

        HBoxContainer heading = new()
        {
            Name = "SidebarHeading",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        heading.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _title = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Title,
            SolverUiTokens.Palette.TextPrimary);
        _title.Name = "SidebarTitle";
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(_title);
        foreach (ISolverSidebarPage page in pages)
            heading.AddChild(page.HeaderActions);
        _close = SolverUiTokens.CreateButton("×", SolverButtonStyle.Secondary);
        _close.Name = "CloseSidebar";
        _close.CustomMinimumSize = new Vector2(SolverUiTokens.Size.ButtonHeight, SolverUiTokens.Size.ButtonHeight);
        _close.Pressed += () => CloseRequested?.Invoke();
        heading.AddChild(_close);
        layout.AddChild(heading);

        _tabBar = new HBoxContainer
        {
            Name = "SidebarTabs",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = pages.Length > 1,
        };
        _tabBar.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        _tabs = new Button[pages.Length];
        for (int index = 0; index < pages.Length; index++)
        {
            int page = index;
            Button tab = SolverUiTokens.CreateButton(pages[index].Title, SolverButtonStyle.Secondary);
            tab.Name = $"{pages[index].Kind}Tab";
            tab.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            tab.Pressed += () => Select(page);
            _tabBar.AddChild(tab);
            _tabs[index] = tab;
        }
        layout.AddChild(_tabBar);

        foreach (ISolverSidebarPage page in pages)
        {
            page.Root.SizeFlagsVertical = SizeFlags.ExpandFill;
            layout.AddChild(page.Root);
        }
        Select(0);
    }

    internal ISolverSidebarPage ActivePage => _pages[_active];

    /// <summary>Shows the page and reloads it from the current settings.</summary>
    internal void Open(SolverSidebarPageKind kind)
    {
        int index = Array.FindIndex(_pages, page => page.Kind == kind);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "侧栏没有这一页。");
        Select(index);
        ActivePage.Opened();
    }

    internal void Apply(CombatUiSnapshot snapshot) => ActivePage.Apply(snapshot);

    internal bool CommitPending() => ActivePage.CommitPending();

    private void Select(int index)
    {
        _active = index;
        for (int page = 0; page < _pages.Length; page++)
        {
            bool active = page == index;
            _pages[page].Root.Visible = active;
            _pages[page].HeaderActions.Visible = active;
            SolverUiTokens.ApplyButtonStyle(_tabs[page],
                active ? SolverButtonStyle.Primary : SolverButtonStyle.Secondary);
        }
        _title.Text = ActivePage.Title;
    }

#if STS2_SOLVER_TESTING
    internal Button CloseButtonForTest => _close;

    internal bool TabBarVisibleForTest => _tabBar.Visible;
#endif
}
