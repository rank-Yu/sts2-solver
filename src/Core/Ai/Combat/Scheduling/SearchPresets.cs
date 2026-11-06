namespace Sts2Solver.Ai.Combat;

internal enum CombatSearchTier { Low, Medium, High, ExtraHigh }

internal static class SearchPresets
{
    /// <summary>
    /// Production budgets. Members stop at their own time deadline (width times
    /// <see cref="SearchPlan.MillisecondsPerWidth"/>); there is no expanded-node ceiling, so a fast
    /// machine searches hard positions further. The offline reproducible evaluation keeps its own
    /// fixed node budget (<c>OfflineCombat.ReproducibleNodesPerWidth</c>).
    /// </summary>
    internal static SearchPlan For(CombatSearchTier tier, long memoryBytes) => tier switch
    {
        CombatSearchTier.Low => new(225, 150, 338, 90, memoryBytes) { MillisecondsPerWidth = 50 },
        CombatSearchTier.Medium => new(300, 200, 450, 120, memoryBytes) { MillisecondsPerWidth = 50 },
        CombatSearchTier.High => new(450, 300, 675, 180, memoryBytes) { MillisecondsPerWidth = 50 },
        CombatSearchTier.ExtraHigh => new(675, 450, 1013, 270, memoryBytes) { MillisecondsPerWidth = 50 },
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
}
