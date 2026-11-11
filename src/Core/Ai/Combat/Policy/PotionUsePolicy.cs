// Ported from CombatSolver src/Search/PotionUsePolicy.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat.Policy;

internal static class PotionUsePolicy
{
    public const decimal AmbergrisMinimumHpSavedFraction = 0.40m;

    public static int RequiredHpSaved(int potionCount)
        => potionCount * ScoreWeights.PotionMinimumHpSaved;

    public static int ExplicitUseCount(int potionCount, int automaticPotionCount)
        => potionCount - automaticPotionCount;

    /// <summary>
    /// The strategic cost a route's optional potion uses must justify once the expected post-combat reward is
    /// counted. See <see cref="PotionRewardOutlook"/>: the credit stands for the one reward a freed slot can
    /// receive, so it is taken off the route total once, never per potion.
    /// </summary>
    /// <remarks>
    /// The floor is one HP, not zero. A zero requirement would let <see cref="IsEligible"/> admit a potion route
    /// that saves nothing or even loses more than the potion-free baseline (<see cref="HpSaved"/> clamps at
    /// zero), and a free replacement only makes spending costless, never worth doing for no gain.
    /// </remarks>
    public static int ApplyReplacementCredit(
        int optionalPotionStrategicCost,
        int optionalPotionCount,
        int replacementHpCredit)
        => optionalPotionStrategicCost > 0 && optionalPotionCount > 0 && replacementHpCredit > 0
            ? Math.Max(1, optionalPotionStrategicCost - replacementHpCredit)
            : optionalPotionStrategicCost;

    /// <summary>
    /// Potions whose whole effect is a buff that lasts the combat and never decays: Strength, Dexterity,
    /// Focus, Fysh Oil (Strength and Dexterity), Mazaleth's Gift (Ritual) and Liquid Bronze (Thorns).
    /// Drinking one later than the search's first turn is never better than drinking it then, so the
    /// search offers a root potion of these kinds only on its first turn. Decaying or consumed buffs
    /// (Heart of Iron, Gigantification, Clarity), effects that depend on when they land (Soldier's Stew
    /// on the Strikes present, King's Courage forging, Potion of Capacity changing when orbs evoke) and
    /// one-turn buffs are not listed. No monster in the game reacts to a buff the player applies.
    /// </summary>
    public static readonly IReadOnlySet<string> OpeningOnlyPotionIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "STRENGTH_POTION", "DEXTERITY_POTION", "FOCUS_POTION", "FYSH_OIL", "MAZALETHS_GIFT", "LIQUID_BRONZE",
    };

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

    public static int HpSaved(int potionFreeHpDeficit, int potionRouteHpDeficit)
        => Math.Max(0, potionFreeHpDeficit - potionRouteHpDeficit);

    public static int SmartRequiredHpSaved(
        int strategicHpCost,
        BossHpRelief bossHpRelief = BossHpRelief.None)
        => ActEndingBossPolicy.RawHpRequiredForPersistentValue(
            strategicHpCost,
            bossHpRelief);

    public static int AmbergrisRequiredHpSaved(int maximumHp)
        => (int)Math.Ceiling(maximumHp * AmbergrisMinimumHpSavedFraction);

    public static int EffectiveStrategicHpCost(
        int strategicHpCost,
        int ambergrisCount,
        int maximumHp)
        => strategicHpCost + ambergrisCount
            * (AmbergrisRequiredHpSaved(maximumHp) - ScoreWeights.PotionMinimumHpSaved);

    public static bool MeetsAmbergrisRestriction(
        bool hasPotionFreeBaseline,
        int ambergrisCount,
        int strategicHpCost,
        int maximumHp,
        int potionFreePlayerHp,
        int potionRoutePlayerHp)
    {
        if (ambergrisCount == 0)
            return true;
        if (!hasPotionFreeBaseline)
            return false;
        int required = EffectiveStrategicHpCost(strategicHpCost, ambergrisCount, maximumHp);
        return Math.Max(0, potionRoutePlayerHp - potionFreePlayerHp) >= required;
    }

    public static bool IsEligible(
        PotionUseMode policy,
        int explicitPotionCount,
        int strategicHpCost,
        bool potionFreeWon,
        int potionFreeHpDeficit,
        bool anyRouteWon,
        bool potionRouteWon,
        int potionRouteHpDeficit)
        => policy switch
        {
            PotionUseMode.Disabled => explicitPotionCount == 0,
            PotionUseMode.Smart => explicitPotionCount == 0
                || potionRouteWon && !potionFreeWon
                || HpSaved(potionFreeHpDeficit, potionRouteHpDeficit)
                    >= SmartRequiredHpSaved(strategicHpCost),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
}

internal readonly record struct PotionFreePolicyBaseline(
    bool Won,
    int HpDeficit,
    int PlayerHp,
    int? CombatEndedTurn)
{
    public int DeathSaveUseCount { get; init; }
    public int OutstandingStolenResource { get; init; } = int.MaxValue;
}
