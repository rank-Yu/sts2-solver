namespace Sts2Solver.Ai.Combat;

internal enum CombatSearchTier { Low, Medium, High, ExtraHigh }

internal static class SearchPresets
{
    internal static SearchPlan For(CombatSearchTier tier, long memoryBytes) => tier switch
    {
        CombatSearchTier.Low => new(225, 150, 338, 90, memoryBytes) { MillisecondsPerWidth = 50 },
        CombatSearchTier.Medium => new(300, 200, 450, 120, memoryBytes) { MillisecondsPerWidth = 50 },
        CombatSearchTier.High => new(450, 300, 675, 180, memoryBytes) { MillisecondsPerWidth = 50 },
        CombatSearchTier.ExtraHigh => new(675, 450, 1013, 270, memoryBytes) { MillisecondsPerWidth = 50 },
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
}
