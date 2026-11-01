using Godot;

namespace Sts2Solver;

/// <summary>Invisible 4 Hz main-thread sampler for the memory readout.</summary>
internal sealed partial class CombatUiMemorySampler : Node
{
    private const double IntervalSeconds = 0.25;

    private double _elapsed;

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed < IntervalSeconds)
            return;
        _elapsed = 0;
        if (CombatUiStore.Initialized)
            CombatUiStore.SampleMemory();
    }
}
