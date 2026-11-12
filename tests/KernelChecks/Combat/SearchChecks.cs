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
        Assert(powered.Length == 10 && ordinary.Length == 8, "compact portfolio sizes");
        Assert(powered.Select(m => m.Potions).SequenceEqual(new[]
            {
                SearchPotionLimit.None, SearchPotionLimit.Allowed, SearchPotionLimit.None,
                SearchPotionLimit.None, SearchPotionLimit.Allowed, SearchPotionLimit.None,
                SearchPotionLimit.None, SearchPotionLimit.Allowed,
                SearchPotionLimit.None, SearchPotionLimit.Allowed,
            })
            && powered[2].Width == plan.NarrowWidth && powered[3].Width == plan.WideWidth,
            "main methods are paired without changing ordinary or wide widths");
        foreach (SearchPlan preset in new[] { low, medium, high, extra })
        foreach (bool power in new[] { false, true })
        {
            SearchMember[] members = PortfolioBuilder.Build(preset, power);
            int first = power ? 2 : 0;
            Assert(members[first] == new SearchMember(preset.NarrowWidth,
                SearchEvaluation.Standard, SearchRetention.Standard, SearchPotionLimit.None),
                "ordinary member uses the tier narrow width without potions");
            SearchMember[] allowed = members.Where(m => m.Potions == SearchPotionLimit.Allowed).ToArray();
            Assert(allowed.Count(m => m.Evaluation == SearchEvaluation.Standard
                    && m.Retention == SearchRetention.Standard) == 1
                && allowed.Single(m => m.Evaluation == SearchEvaluation.Standard
                    && m.Retention == SearchRetention.Standard).Width == preset.WideWidth,
                "only the wide standard member remains in the Allowed stage");
        }
        foreach (bool power in new[] { false, true })
        foreach (bool setup in new[] { false, true })
        foreach (bool continuous in new[] { false, true })
        foreach (bool queen in new[] { false, true })
        {
            SearchPlan pairedPlan = medium with
            {
                SetupStrategies = setup, ContinuousSetupStrategies = continuous,
            };
            SearchMember[] main = PortfolioBuilder.Build(pairedPlan, power);
            SearchMember[] all = SupplementPortfolio.Append(main, pairedPlan, queen);
            Assert(all.Take(main.Length).SequenceEqual(main), "supplements follow all main members");
            List<SearchMember> expected = [];
            if (setup)
            {
                expected.Add(new(medium.WideWidth, SearchEvaluation.Setup, SearchRetention.Standard, SearchPotionLimit.None));
                expected.Add(new(medium.WideWidth, SearchEvaluation.Setup, SearchRetention.Standard, SearchPotionLimit.Allowed));
            }
            if (setup && continuous)
                expected.Add(new(medium.WideWidth, SearchEvaluation.ContinuousSetup, SearchRetention.Standard, SearchPotionLimit.Allowed));
            if (queen)
            {
                expected.Add(new(medium.NarrowWidth, SearchEvaluation.Standard, SearchRetention.QueenKillTiming, SearchPotionLimit.None));
                expected.Add(new(medium.NarrowWidth, SearchEvaluation.Standard, SearchRetention.QueenKillTiming, SearchPotionLimit.Allowed));
            }
            Assert(all.Skip(main.Length).SequenceEqual(expected)
                && all.Distinct().Count() == all.Length, "supplement pairs appear once and obey encounter flags");
        }
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
