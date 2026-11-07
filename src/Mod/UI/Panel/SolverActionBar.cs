// First ported from CombatSolver src/UI/SolverActionBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Owns footer layout only: the overlay keeps command bindings and the stage rules. One row holds the
// auto-search switch, the search keys (one key, or 继续搜索 + 重新搜索) and the execute keys (执行本回合 +
// 执行本场战斗, or 停止执行); the memory bar sits below.
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

    public SolverActionBar(Control autoSearch, Button search, Button restart, Button executeTurn,
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
        HFlowContainer actions = CreateFlow("CombatActions");
        actions.AddChild(autoSearch);
        actions.AddChild(CreateGroup("SearchKeys", search, restart));
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
        HBoxContainer group = new() { Name = name, MouseFilter = MouseFilterEnum.Pass };
        group.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        foreach (Control key in keys)
            group.AddChild(key);
        return group;
    }

    private static HFlowContainer CreateFlow(string name)
    {
        HFlowContainer flow = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        flow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Lg);
        flow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        return flow;
    }
}
