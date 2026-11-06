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
        RemovalOrder();
    }

    /// <summary>
    /// Purity-style removal (exhaust 0..3 of 6 hand cards, two copies of the cheapest): the cheapest
    /// prefix of every size first, then the remaining class vectors from the cheapest class, capped.
    /// </summary>
    private static void RemovalOrder()
    {
        // Options 0..5: ids 7,3,3,9,5,8; priorities make id 3 cheapest, then 5, 7, 8, 9.
        Sts2Solver.Simulation.Combat.Card[] cards =
        [
            new() { Id = 7 }, new() { Id = 3 }, new() { Id = 3 }, new() { Id = 9 }, new() { Id = 5 },
            new() { Id = 8 },
        ];
        int[] priority = [30, 10, 10, 50, 20, 40];
        Sts2Solver.Simulation.Combat.ChoiceRequest request = new()
        {
            Kind = Sts2Solver.Simulation.Combat.ChoiceKind.SelectCards,
            Effect = Sts2Solver.Simulation.Combat.ChoiceEffect.Exhaust,
            Source = Sts2Solver.Simulation.Combat.Pile.Hand,
            OptionCount = 6,
            MinCount = 0,
            MaxCount = 3,
        };
        Assert(RemovalChoicePriority.Applies(in request), "removal applies to a free-count hand exhaust");
        Span<ulong> output = stackalloc ulong[RemovalChoicePriority.Branches];
        int count = ChoiceEnumerator.EnumerateRemoval(in request, cards, priority, output);
        static ulong Bits(params int[] options) => options.Aggregate(0UL, (bits, o) => bits | 1UL << o);
        Assert(count == RemovalChoicePriority.Branches, $"removal count {count}");
        Assert(output[0] == 0 && output[1] == Bits(1) && output[2] == Bits(1, 2)
            && output[3] == Bits(1, 2, 4), "removal prefixes of every size");
        Assert(output[4] == Bits(4) && output[5] == Bits(0) && output[6] == Bits(5) && output[7] == Bits(3),
            "removal singles from the cheapest class");
        Assert(output[8] == Bits(1, 4), "removal pairs follow the singles");
        Assert(output[..count].ToArray().Distinct().Count() == count, "removal selections distinct");
        Console.WriteLine("SEARCH removal-order=pass");
    }
}
