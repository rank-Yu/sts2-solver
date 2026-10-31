// Adapted from CombatSolver src/Search/BeamWidthPortfolio.cs at a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
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

        if (power)
            members.Add(new(plan.BaselineWidth, SearchEvaluation.Standard, SearchRetention.Power,
                SearchPotionLimit.One));
        members.Add(new(plan.WideWidth, SearchEvaluation.Standard, SearchRetention.Standard,
            SearchPotionLimit.One));
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
