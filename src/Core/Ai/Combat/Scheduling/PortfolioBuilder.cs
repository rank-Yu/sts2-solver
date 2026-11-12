// Adapted from CombatSolver src/Search/BeamWidthPortfolio.cs at a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
// The original's middle stage (at most one potion: power-first at baseline width and wide) is not
// kept. On the 108 quality roots it was the selected member 11 times; in 10 of them an Allowed
// member found a route with the same HP loss and no more potions, and dropping it cut expanded
// nodes by 14.6% with one root (DEFECT-008) drinking one more potion.
namespace Sts2Solver.Ai.Combat;

internal static class PortfolioBuilder
{
    /// <summary>Runs each search method without potions, then with potions when applicable.</summary>
    internal static SearchMember[] Build(SearchPlan plan, bool power)
    {
        List<SearchMember> members = [];
        if (power)
            AddPair(plan.BaselineWidth, SearchEvaluation.Standard, SearchRetention.Power);
        members.Add(new(plan.NarrowWidth, SearchEvaluation.Standard, SearchRetention.Standard,
            SearchPotionLimit.None));
        AddPair(plan.WideWidth, SearchEvaluation.Standard, SearchRetention.Standard);
        members.Add(new(plan.BaselineWidth, SearchEvaluation.Standard, SearchRetention.SecondBand,
            SearchPotionLimit.None));
        AddPair(plan.BaselineWidth, SearchEvaluation.Base, SearchRetention.Standard);
        AddPair(plan.OffensiveWidth, SearchEvaluation.Offensive, SearchRetention.Standard);
        return [.. members];

        void AddPair(int width, SearchEvaluation evaluation, SearchRetention retention)
        {
            members.Add(new(width, evaluation, retention, SearchPotionLimit.None));
            members.Add(new(width, evaluation, retention, SearchPotionLimit.Allowed));
        }
    }
}
