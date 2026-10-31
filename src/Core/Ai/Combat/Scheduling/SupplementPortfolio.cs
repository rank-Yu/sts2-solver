namespace Sts2Solver.Ai.Combat;

/// <summary>Supplemental members appended to each existing potion stage.</summary>
internal static class SupplementPortfolio
{
    internal static bool IsSupplement(SearchMember member)
        => member.Evaluation is SearchEvaluation.QueenSetup or SearchEvaluation.Setup or SearchEvaluation.ContinuousSetup
            || member.Retention == SearchRetention.QueenKillTiming;

    internal static SearchMember[] AppendToStages(SearchMember[] members, SearchPlan plan, bool queen = true)
    {
        List<SearchMember> result = new(members.Length + 6);
        for (int i = 0; i < members.Length; i++)
        {
            SearchMember member = members[i];
            result.Add(member);
            if (i + 1 < members.Length && members[i + 1].Potions == member.Potions) continue;
            if (plan.SetupStrategies)
                result.Add(new(plan.WideWidth, SearchEvaluation.Setup, SearchRetention.Standard, member.Potions));
            else if (queen)
                result.Add(new(plan.BaselineWidth, SearchEvaluation.QueenSetup, SearchRetention.Standard, member.Potions));
            if (plan.SetupStrategies && plan.ContinuousSetupStrategies && member.Potions == SearchPotionLimit.Allowed)
                result.Add(new(plan.WideWidth, SearchEvaluation.ContinuousSetup, SearchRetention.Standard, member.Potions));
            if (queen)
                result.Add(new(plan.NarrowWidth, SearchEvaluation.Standard, SearchRetention.QueenKillTiming, member.Potions));
        }
        return [.. result];
    }
}
