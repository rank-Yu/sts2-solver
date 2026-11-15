// First ported from CombatSolver src/UI/SolverActionBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Owns footer layout only: search and execution occupy two fixed-height rows. The grid aligns
// their switches and button groups; the memory bar sits below.
using Godot;

namespace Sts2Solver;

internal readonly record struct SolverActionBarState(bool Collapsed, bool SearchVisible,
    bool RestartVisible, bool ExecuteTurnVisible, bool ExecuteCombatVisible, bool StopVisible);

internal sealed partial class SolverActionBar : VBoxContainer
{
    private readonly HBoxContainer _memoryRow;
    private readonly Button _search;
    private readonly Button _restart;
    private readonly Button _executeTurn;
    private readonly Button _executeCombat;
    private readonly Button _stop;
    private readonly Control _memory;

    public SolverActionBar(Control autoSearch, Button search, Button restart, Control autoExecute, Button executeTurn,
        Button executeCombat, Button stop, Control memory)
    {
        Name = "Footer";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _search = search;
        _restart = restart;
        _executeTurn = executeTurn;
        _executeCombat = executeCombat;
        _stop = stop;
        _memory = memory;
        GridContainer actions = new()
        {
            Name = "CombatActions",
            Columns = 2,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        actions.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Lg);
        actions.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        actions.AddChild(autoSearch);
        actions.AddChild(CreateGroup("SearchKeys", search, restart));
        actions.AddChild(autoExecute);
        actions.AddChild(CreateGroup("ExecuteKeys", executeTurn, executeCombat, stop));
        AddChild(actions);
        _memoryRow = new HBoxContainer { Name = "MemoryRow", MouseFilter = MouseFilterEnum.Pass };
        _memoryRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _memoryRow.AddChild(memory);
        AddChild(_memoryRow);
    }

    public bool MemoryRowVisible => _memoryRow.Visible;

    public void Refresh(SolverActionBarState state)
    {
        _search.Visible = state.SearchVisible;
        _restart.Visible = state.RestartVisible;
        _executeTurn.Visible = state.ExecuteTurnVisible;
        _executeCombat.Visible = state.ExecuteCombatVisible;
        _stop.Visible = state.StopVisible;
        _memoryRow.Visible = !state.Collapsed;
        _memory.Visible = !state.Collapsed;
    }

    private static HBoxContainer CreateGroup(string name, params Control[] keys)
    {
        HBoxContainer group = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Pass,
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ButtonHeight),
        };
        group.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        foreach (Control key in keys)
            group.AddChild(key);
        return group;
    }
}
