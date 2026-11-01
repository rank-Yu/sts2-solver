// Adapted from CombatSolver src/UI/SolverOverlayMotionDriver.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The overlay owns the per-frame easing entry point, so the driver takes it as a delegate instead
// of calling CombatSolver's static overlay type.
using Godot;

namespace Sts2Solver;

// Lives under the overlay layer so it is freed with it; drives per-frame easing of the search
// readouts that otherwise only change when a progress report arrives.
internal sealed partial class SolverOverlayMotionDriver : Node
{
    internal Action<double>? AdvanceReadouts { get; set; }

    public override void _Process(double delta)
        => AdvanceReadouts?.Invoke(delta);
}
