// Adapted from CombatSolver src/UI/SolverOverlayInputBridge.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The bridge talks to the Sts2Solver panel instance instead of the static CombatSolver overlay;
// there is no upload dialog to defer to.
using Godot;

namespace Sts2Solver;

/// <summary>Global input for the panel: a left-button release ends a drag or resize. There is no hide shortcut.</summary>
internal sealed partial class SolverPanelInputBridge : Node
{
    internal SolverPanelNode? Panel { get; set; }

    public override void _Input(InputEvent inputEvent)
    {
        if (Panel == null)
            return;
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            Panel.CompletePointerGesture();
    }
}
