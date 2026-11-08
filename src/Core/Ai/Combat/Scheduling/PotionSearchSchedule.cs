using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>Applies a member's potion stage to the request's potion policy.</summary>
internal static class PotionSearchSchedule
{
    internal static bool HasAllowedUse(IReadOnlyList<Battle> roots, Rules rules, BeamOptions options)
    {
        PotionPolicy policy = options.Potions;
        if (policy.ForceAllDisabled)
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

    internal static PotionPolicy Limit(PotionPolicy original, SearchPotionLimit limit) => limit switch
    {
        SearchPotionLimit.None => original with { ForceAllDisabled = true },
        SearchPotionLimit.Allowed => original,
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };
}
