// Ported from CombatSolver src/UI/SolverActionBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Owns footer layout only: the overlay keeps command bindings and capability checks. The frozen
// route, the independent full-auto toggle and the memory-release button are not ported.
using Godot;

namespace Sts2Solver;

internal readonly record struct SolverActionBarState(bool Collapsed, bool Searching, bool ShowAdopt);

internal sealed partial class SolverActionBar : VBoxContainer
{
    private readonly HFlowContainer _actions;
    private readonly HBoxContainer _memoryRow;
    private readonly Button _execute;
    private readonly Button _recalculate;
    private readonly Button _stop;
    private readonly Button _adopt;
    private readonly Control _memory;

    public SolverActionBar(Button execute, Button recalculate, Button stop, Button adopt,
        Control memory)
    {
        Name = "Footer";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _execute = execute;
        _recalculate = recalculate;
        _stop = stop;
        _adopt = adopt;
        _memory = memory;
        _actions = CreateFlow("CombatActions");
        HBoxContainer actionRow = new() { Name = "ActionRow", MouseFilter = MouseFilterEnum.Pass };
        actionRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        _actions.AddChild(adopt);
        _actions.AddChild(execute);
        _actions.AddChild(recalculate);
        _actions.AddChild(stop);
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
        _recalculate.Visible = !state.Searching;
        _stop.Visible = state.Searching;
        _adopt.Visible = state.ShowAdopt;
        _execute.Visible = !state.Searching;
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
