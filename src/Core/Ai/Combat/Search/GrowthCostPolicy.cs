// Ported from CombatSolver src/Search/GrowthCostPolicy.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the member Sts2Solver reads.
namespace Sts2Solver.Ai.Combat;

/// <summary>
/// The existing search's BrightestFlame hard cap: a limit is combat-cumulative, and a manual
/// overspend at the root raises the ceiling to the root's spent amount instead of rejecting the root.
/// </summary>
internal static class GrowthCostPolicy
{
    internal static bool AllowsBrightestFlame(int? limit, int spentAtRoot, int spentAfterAction)
        => !limit.HasValue || spentAfterAction <= Math.Max(limit.Value, spentAtRoot);
}
