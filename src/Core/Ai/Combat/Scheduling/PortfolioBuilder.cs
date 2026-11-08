// Adapted from CombatSolver src/Search/BeamWidthPortfolio.cs at a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
// The original's middle stage (at most one potion: power-first at baseline width and wide) is not
// kept. On the 108 quality roots it was the selected member 11 times; in 10 of them an Allowed
// member found a route with the same HP loss and no more potions, and dropping it cut expanded
// nodes by 14.6% with one root (DEFECT-008) drinking one more potion.
namespace Sts2Solver.Ai.Combat;

internal static class PortfolioBuilder
{
    /// <summary>Builds the compact staged portfolio used by every production request.</summary>
    internal static SearchMember[] Build(SearchPlan plan, bool power)
    {
        List<SearchMember> members = [];
        if (power)
            members.Add(new(plan.BaselineWidth, SearchEvaluation.Standard, SearchRetention.Power,
                SearchPotionLimit.None));
        int ordinaryIndex = members.Count;
        members.Add(new(plan.NarrowWidth, SearchEvaluation.Standard, SearchRetention.Standard,
            SearchPotionLimit.None));
        members.Add(new(plan.WideWidth, SearchEvaluation.Standard, SearchRetention.Standard,
            SearchPotionLimit.None));
        members.Add(new(plan.BaselineWidth, SearchEvaluation.Standard, SearchRetention.SecondBand,
            SearchPotionLimit.None));
        members.Add(new(plan.BaselineWidth, SearchEvaluation.Base, SearchRetention.Standard,
            SearchPotionLimit.None));
        members.Add(new(plan.OffensiveWidth, SearchEvaluation.Offensive, SearchRetention.Standard,
            SearchPotionLimit.None));
        int primaryCount = members.Count;

        for (int index = 0; index < primaryCount; index++)
        {
            if (members[index].Retention == SearchRetention.SecondBand)
                continue;
            members.Add(members[index] with
            {
                Potions = SearchPotionLimit.Allowed,
                Width = index == ordinaryIndex ? plan.BaselineWidth : members[index].Width,
            });
        }
        return [.. members];
    }
}
