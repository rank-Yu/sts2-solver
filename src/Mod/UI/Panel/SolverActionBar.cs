// Ported from CombatSolver src/UI/SolverActionBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Owns footer layout only: the overlay keeps command bindings and capability checks. The footer
// has one search slot and one execute slot; the second slot is hidden when no current-turn route
// is executable.
using Godot;

namespace Sts2Solver;

internal readonly record struct SolverActionBarState(bool Collapsed, bool ExecuteVisible);

internal sealed partial class SolverActionBar : VBoxContainer
{
    private readonly HFlowContainer _actions;
    private readonly HBoxContainer _memoryRow;
    private readonly Button _search;
    private readonly Button _execute;
    private readonly Control _memory;

    public SolverActionBar(Button search, Button execute, Control memory)
    {
        Name = "Footer";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _search = search;
        _execute = execute;
        _memory = memory;
        _actions = CreateFlow("CombatActions");
        HBoxContainer actionRow = new() { Name = "ActionRow", MouseFilter = MouseFilterEnum.Pass };
        actionRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        _actions.AddChild(search);
        _actions.AddChild(execute);
        actionRow.AddChild(_actions);
        AddChild(actionRow);
        _memoryRow = new HBoxContainer { Name = "MemoryRow", MouseFilter = MouseFilterEnum.Pass };
        _memoryRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _memoryRow.AddChild(memory);
        AddChild(_memoryRow);
    }

    public bool MemoryRowVisible => _memoryRow.Visible;

    public void Refresh(SolverActionBarState state)
    {
        _search.Visible = true;
        _execute.Visible = state.ExecuteVisible;
        _memoryRow.Visible = !state.Collapsed;
        _memory.Visible = !state.Collapsed;
    }

    private static HFlowContainer CreateFlow(string name)
    {
        HFlowContainer flow = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        flow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        flow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        return flow;
    }
}
