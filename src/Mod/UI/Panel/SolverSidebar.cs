// Sidebar shell adapted from CombatSolver src/UI/SolverPotionStrategyPanel.cs and
// src/UI/SolverGrowthStrategyPanel.cs (box, heading row and close button) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). One container hosts the potion and
// growth pages; the heading shows the open page's title and a × instead of the original 收起.
using Godot;

namespace Sts2Solver;

internal enum SolverStrategyPageKind
{
    Potions,
    Growth,
}

/// <summary>A page hosted by <see cref="SolverSidebar"/>.</summary>
internal interface ISolverSidebarPage
{
    SolverStrategyPageKind Kind { get; }

    string Title { get; }

    Control Root { get; }

    /// <summary>Reloads the page from the current snapshot; called each time the page opens.</summary>
    void Opened();

    void Apply(CombatUiSnapshot snapshot);

    /// <summary>Saves inputs still being edited; false keeps the page open on an invalid value.</summary>
    bool CommitPending();
}

/// <summary>
/// The strategy sidebar docked beside the panel. The overlay owns its placement, visibility and
/// which page is open; the sidebar only shows that page and reports the close button.
/// </summary>
internal sealed partial class SolverSidebar : PanelContainer
{
    internal const float PreferredWidth = 390f;

    private readonly ISolverSidebarPage[] _pages;
    private readonly Label _title;
    private readonly Button _close;
    private ISolverSidebarPage _active;

    public event Action? CloseRequested;

    public SolverSidebar(params ISolverSidebarPage[] pages)
    {
        if (pages.Length == 0)
            throw new ArgumentException("侧栏至少要有一页。", nameof(pages));
        _pages = pages;
        _active = pages[0];
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
        _close = SolverUiTokens.CreateButton("×", SolverButtonStyle.Secondary);
        _close.Name = "CloseSidebar";
        _close.CustomMinimumSize = new Vector2(SolverUiTokens.Size.ButtonHeight, SolverUiTokens.Size.ButtonHeight);
        _close.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Title);
        _close.Pressed += () => CloseRequested?.Invoke();
        heading.AddChild(_close);
        layout.AddChild(heading);

        foreach (ISolverSidebarPage page in pages)
        {
            page.Root.SizeFlagsVertical = SizeFlags.ExpandFill;
            page.Root.Visible = false;
            layout.AddChild(page.Root);
        }
    }

    /// <summary>Shows the page and reloads it from the current snapshot.</summary>
    internal void Open(SolverStrategyPageKind kind)
    {
        _active = Array.Find(_pages, page => page.Kind == kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "侧栏没有这一页。");
        foreach (ISolverSidebarPage page in _pages)
            page.Root.Visible = ReferenceEquals(page, _active);
        _title.Text = _active.Title;
        _active.Opened();
    }

    internal void Apply(CombatUiSnapshot snapshot) => _active.Apply(snapshot);

    internal bool CommitPending() => _active.CommitPending();

#if STS2_SOLVER_TESTING
    internal Button CloseButtonForTest => _close;

    internal string TitleForTest => _title.Text;

    internal bool PageVisibleForTest(SolverStrategyPageKind kind)
        => Array.Find(_pages, page => page.Kind == kind) is { } page && page.Root.IsVisibleInTree();
#endif
}
