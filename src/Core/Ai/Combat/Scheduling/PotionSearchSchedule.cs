using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>Intersect member limits with the request's original slot and count constraints.</summary>
internal static class PotionSearchSchedule
{
    internal static bool HasAllowedUse(IReadOnlyList<Battle> roots, Rules rules, BeamOptions options)
    {
        PotionPolicy policy = options.Potions;
        if (policy.ForceAllDisabled || policy.MaximumUses is { } maximum
            && maximum <= options.InitialExplicitPotionUses)
            return false;
        foreach (Battle root in roots)
        {
            for (int slot = 0; slot < root.PotionSlotCount; slot++)
            {
                ushort stored = root.Potions[slot];
                if (stored != 0 && policy.AllowsExplicitUse(slot, (ushort)(stored - 1)))
                    return true;
                foreach (ushort potion in rules.PotionPool)
                    if (policy.AllowsExplicitUse(slot, potion)) return true;
                foreach (ushort potion in rules.OutOfCombatPotionPool)
                    if (policy.AllowsExplicitUse(slot, potion)) return true;
            }
        }
        return false;
    }

    internal static PotionPolicy Limit(PotionPolicy original, SearchPotionLimit limit, int initialUses = 0) => limit switch
    {
        SearchPotionLimit.None => original with { ForceAllDisabled = true, MaximumUses = Math.Min(original.MaximumUses ?? initialUses, initialUses) },
        SearchPotionLimit.One => original with { MaximumUses = Math.Min(original.MaximumUses ?? 1, 1) },
        SearchPotionLimit.Allowed => original,
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };
}
