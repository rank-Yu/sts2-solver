// Host wiring follows CombatSolver src/Runtime/SolverController.cs and SolverDispatcher.cs
// at 9f83bea77127830d91dd47db4bac6323414bbbf4 (MIT, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Ai.Combat;

namespace Sts2Solver;

/// <summary>主线程持续采集空闲帧基线；每个搜索成员只重置压力与统计，不丢弃基线。</summary>
internal sealed class SearchFrameMonitor
{
    internal SearchFramePressureSignal Signal { get; } = new();
    internal int LongFrames { get; private set; }
    internal double MaxFrameMilliseconds { get; private set; }
    private bool _measureFrames;

    internal void BeginSearch(bool recoveryEnabled, bool measureFrames)
    {
        Signal.ResetPressure(recoveryEnabled);
        LongFrames = 0;
        MaxFrameMilliseconds = 0d;
        _measureFrames = measureFrames;
    }

    internal void ObserveFrame(double milliseconds, bool searchActive, bool recoveryAllowed)
    {
        Signal.ObserveFrame(milliseconds, searchActive, recoveryAllowed);
        if (!searchActive || !_measureFrames)
            return;
        if (milliseconds >= Signal.PressureFrameGapMilliseconds)
            LongFrames++;
        MaxFrameMilliseconds = Math.Max(MaxFrameMilliseconds, milliseconds);
    }
}
