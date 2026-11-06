namespace Sts2Solver.Ai.Combat;

internal enum CombatSearchTier { Low, Medium, High, ExtraHigh }

internal static class SearchPresets
{
    /// <summary>
    /// Production node ceiling per beam slot, twice the offline reproducible budget
    /// (<c>OfflineCombat.ReproducibleNodesPerWidth</c>, 512). Members stop at width times this many
    /// expanded nodes or at their time deadline, whichever comes first. It leaves hard positions room
    /// while bounding searches that do not converge: without a ceiling, quality root IRONCLAD-000 (an
    /// act 1 hallway fight won at full HP) searched its opening for 62 s and chose the same route it
    /// found in 7 s at 512 per width; a Silent Purity/discard deck against Gremlin Merc took 124 s at
    /// 2048 and 41 s at 512 for the same route.
    /// </summary>
    internal const int NodesPerWidth = 1024;

    internal static SearchPlan For(CombatSearchTier tier, long memoryBytes) => tier switch
    {
        CombatSearchTier.Low => new(225, 150, 338, 90, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        CombatSearchTier.Medium => new(300, 200, 450, 120, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        CombatSearchTier.High => new(450, 300, 675, 180, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        CombatSearchTier.ExtraHigh => new(675, 450, 1013, 270, memoryBytes) { MillisecondsPerWidth = 50, NodesPerWidth = NodesPerWidth },
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
}
