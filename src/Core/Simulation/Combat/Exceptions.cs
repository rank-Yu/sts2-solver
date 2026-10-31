namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// A fixed capacity of <see cref="Battle"/> was exceeded. A child-state overflow inside the
/// expansion loop is a search boundary handled by <c>BeamSolver</c>; a root-level overflow
/// abandons the whole Sts2Solver solve and is reported as a capacity failure; no partial route is
/// returned.
/// </summary>
public sealed class CapacityException(string region, int capacity)
    : Exception($"Sts2Solver 状态区块 {region} 超出容量 {capacity}。")
{
    public string Region { get; } = region;

    public int Capacity { get; } = capacity;
}

/// <summary>
/// The kernel reached content or a mechanic it does not implement. Admission should have
/// rejected the root; reaching this at run time abandons the whole Sts2Solver solve.
/// </summary>
public class UnsupportedException(string detail)
    : Exception($"Sts2Solver 内核不支持：{detail}");
