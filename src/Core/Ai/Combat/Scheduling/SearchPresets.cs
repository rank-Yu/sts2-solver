namespace Sts2Solver.Ai.Combat;

internal enum CombatSearchTier { Low, Medium, High, ExtraHigh }

internal static class SearchPresets
{
    /// <summary>
    /// Production node ceiling per beam slot; the same value as the offline reproducible budget
    /// (<c>OfflineCombat.ReproducibleNodesPerWidth</c>). In the 11-06 headless real-game runs 98.6%
    /// of members finished their frontier below a fifth of it.
    /// </summary>
    internal const int NodesPerWidth = 512;

    internal static SearchPlan For(CombatSearchTier tier, long memoryBytes) => tier switch
    {
        CombatSearchTier.Low => new(225, 150, 338, 90, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        CombatSearchTier.Medium => new(300, 200, 450, 120, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        CombatSearchTier.High => new(450, 300, 675, 180, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        CombatSearchTier.ExtraHigh => new(675, 450, 1013, 270, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
}
