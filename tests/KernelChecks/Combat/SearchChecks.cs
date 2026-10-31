using Sts2Solver.Ai.Combat;

namespace Sts2Solver.KernelChecks;

internal static class SearchChecks
{
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("search-checks: " + message); }

    internal static void Check()
    {
        SearchPlan low = SearchPresets.For(CombatSearchTier.Low, 10_000);
        SearchPlan medium = SearchPresets.For(CombatSearchTier.Medium, 10_000);
        SearchPlan high = SearchPresets.For(CombatSearchTier.High, 10_000);
        SearchPlan extra = SearchPresets.For(CombatSearchTier.ExtraHigh, 10_000);
        Assert((low.BaselineWidth, low.NarrowWidth, low.WideWidth, low.OffensiveWidth, low.MillisecondsPerWidth)
                == (225, 150, 338, 90, 50)
            && (medium.BaselineWidth, medium.NarrowWidth, medium.WideWidth, medium.OffensiveWidth, medium.MillisecondsPerWidth)
                == (300, 200, 450, 120, 50)
            && (high.BaselineWidth, high.NarrowWidth, high.WideWidth, high.OffensiveWidth, high.MillisecondsPerWidth)
                == (450, 300, 675, 180, 50)
            && (extra.BaselineWidth, extra.NarrowWidth, extra.WideWidth, extra.OffensiveWidth, extra.MillisecondsPerWidth)
                == (675, 450, 1013, 270, 50),
            "preset widths and independent time");
        Assert(SearchPlan.IsLongEncounter(true, false, 0)
            && !SearchPlan.IsLongEncounter(false, true, 1)
            && SearchPlan.IsLongEncounter(false, true, 2),
            "long encounter classification");
        SearchPlan plan = new(12, 8, 16, 5, 100_000_000) { MillisecondsPerWidth = 50 };
        SearchMember[] powered = PortfolioBuilder.Build(plan, power: true);
        SearchMember[] ordinary = PortfolioBuilder.Build(plan, power: false);
        Assert(powered.Length == 13 && ordinary.Length == 10, "compact portfolio sizes");
        Assert(powered.Take(6).All(m => m.Potions == SearchPotionLimit.None)
            && powered.Skip(6).Take(2).All(m => m.Potions == SearchPotionLimit.One)
            && powered.Skip(8).All(m => m.Potions == SearchPotionLimit.Allowed)
            && powered.Skip(8).All(m => m.Width != plan.NarrowWidth)
            && powered[9].Width == plan.BaselineWidth && powered[10].Width == plan.WideWidth,
            "compact portfolio stages and widths");
        Console.WriteLine("SEARCH presets/portfolios=pass");
    }
}
