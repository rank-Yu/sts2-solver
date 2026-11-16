// Ported from CombatSolver src/Search/PotionUsePolicy.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Policy;

internal static class PotionUsePolicy
{
    /// <summary>
    /// The strategic cost a route's optional potion uses must justify once the expected post-combat reward is
    /// counted. See <see cref="PotionRewardOutlook"/>: the credit stands for the one reward a freed slot can
    /// receive, so it is taken off the route total once, never per potion.
    /// </summary>
    public static int ApplyReplacementCredit(
        int optionalPotionStrategicCost,
        int optionalPotionCount,
        int replacementHpCredit)
        => optionalPotionStrategicCost > 0 && optionalPotionCount > 0 && replacementHpCredit > 0
            ? Math.Max(1, optionalPotionStrategicCost - replacementHpCredit)
            : optionalPotionStrategicCost;

    /// <summary>
    /// Persistent buffs whose use is normally restricted to the search's opening turn.
    /// Relic interactions can make a later use more valuable; resolve those once per root slot.
    /// Decaying, consumed and turn-local buffs are not in this set.
    /// </summary>
    private static readonly IReadOnlySet<string> PersistentBuffPotionIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "STRENGTH_POTION", "DEXTERITY_POTION", "FOCUS_POTION", "FYSH_OIL", "MAZALETHS_GIFT", "LIQUID_BRONZE",
    };

    internal static bool RestrictToOpeningTurn(string potionId, Rules rules)
    {
        if (!PersistentBuffPotionIds.Contains(potionId))
            return false;
        foreach (ushort index in rules.Relics.Active)
        {
            string relicId = Registry.Relic(rules.Relics.Id(index)).NativeId;
            // Its temporary Strength is worth saving for an attack-heavy turn.
            if (relicId == "REPTILE_TRINKET")
                return false;
            // Let a larger Strength gain consume the first-gain multiplier before the potion
            // or its Ritual trigger.
            if (relicId == "RUINED_HELMET" && potionId is "STRENGTH_POTION" or "FYSH_OIL" or "MAZALETHS_GIFT")
                return false;
        }
        return true;
    }

    /// <summary>Share of its strategic cost each optional potion beyond the dearest one is priced at.</summary>
    public const decimal AdditionalPotionPriceFactor = 0.5m;

    /// <summary>
    /// The strategic price of a route's optional potions: the dearest at its full cost, every other one at
    /// <see cref="AdditionalPotionPriceFactor"/>. A single potion keeps its full price; further potions
    /// cost less, so a second potion pays off once it saves half its own price. The price never falls as
    /// a route drinks more, and it does not depend on the drinking order.
    /// </summary>
    public static int OptionalPotionPrice(int explicitPotionStrategicCost, int dearestPotionStrategicCost)
    {
        int total = Math.Max(0, explicitPotionStrategicCost);
        int dearest = Math.Clamp(dearestPotionStrategicCost, 0, total);
        return checked(dearest + (int)Math.Ceiling((total - dearest) * AdditionalPotionPriceFactor));
    }

    /// <summary>Convert a strategic potion price to the route ordering's HP units.</summary>
    public static int HpEquivalentCost(int strategicHpCost, BossHpRelief bossHpRelief = BossHpRelief.None)
        => ActEndingBossPolicy.RawHpRequiredForPersistentValue(strategicHpCost, bossHpRelief);
}
