namespace Sts2Solver.Ai.Combat;

/// <summary>Supplemental methods follow the main portfolio, paired by potion policy.</summary>
internal static class SupplementPortfolio
{
    internal static bool IsSupplement(SearchMember member)
        => member.Evaluation is SearchEvaluation.Setup or SearchEvaluation.ContinuousSetup
            || member.Retention == SearchRetention.QueenKillTiming;

    internal static SearchMember[] Append(SearchMember[] members, SearchPlan plan, bool queen = true)
    {
        List<SearchMember> result = new(members.Length + 5);
        result.AddRange(members);
        if (plan.SetupStrategies)
        {
            result.Add(new(plan.WideWidth, SearchEvaluation.Setup, SearchRetention.Standard, SearchPotionLimit.None));
            result.Add(new(plan.WideWidth, SearchEvaluation.Setup, SearchRetention.Standard, SearchPotionLimit.Allowed));
        }
        if (plan.SetupStrategies && plan.ContinuousSetupStrategies)
            result.Add(new(plan.WideWidth, SearchEvaluation.ContinuousSetup, SearchRetention.Standard, SearchPotionLimit.Allowed));
        if (queen)
        {
            result.Add(new(plan.NarrowWidth, SearchEvaluation.Standard, SearchRetention.QueenKillTiming, SearchPotionLimit.None));
            result.Add(new(plan.NarrowWidth, SearchEvaluation.Standard, SearchRetention.QueenKillTiming, SearchPotionLimit.Allowed));
        }
        return [.. result];
    }
}
