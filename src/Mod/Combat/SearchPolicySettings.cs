// The mapping from persisted player settings to the neutral Import policy configuration follows
// CombatSolver src/Runtime/SolverController.cs (CaptureSearchPolicy / CapturePotionStrategy) at
// 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors). Potion directives are
// reconciled against the live belt by SearchPolicyCapture, not here.
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>Freezes the settings and per-combat state read by one search request.</summary>
internal static class SolverSearchPolicy
{
    internal static SearchPolicyConfiguration Capture(SearchTheftPolicy? theftPolicy)
    {
        SolverSearchSettings search = SolverSettings.Current.Search;
        return new SearchPolicyConfiguration
        {
            GrowthBudgets = search.GrowthBudgets.ToPolicy(),
            IgnoreLongTermRewards = search.IgnoreLongTermRewards,
            BrightestFlameMaxHpLossLimit = search.BrightestFlameMaxHpLossLimit,
            ActTransitionBossHpStrategy = search.ActTransitionBossHpStrategy,
            FinalBossHpStrategy = search.FinalBossHpStrategy,
            PotionDefaultMode = ToPolicy(search.Potions.DefaultMode),
            PotionDirectives = MapPotionDirectives(search.Potions.Directives),
            TheftPolicy = theftPolicy,
            RelicCounterAllowances = new Dictionary<string, int>(search.RelicCounterAllowances,
                StringComparer.Ordinal),
        };
    }

    private static SearchPotionMode ToPolicy(PotionUseMode mode) => mode switch
    {
        PotionUseMode.Smart => SearchPotionMode.Smart,
        PotionUseMode.Disabled => SearchPotionMode.Disabled,
        _ => throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
            $"未知药水默认策略：{mode}"),
    };

    private static SearchPotionDirective[] MapPotionDirectives(
        IReadOnlyList<PotionSlotDirective> directives)
    {
        SearchPotionDirective[] mapped = new SearchPotionDirective[directives.Count];
        for (int index = 0; index < directives.Count; index++)
        {
            PotionSlotDirective directive = directives[index];
            mapped[index] = new SearchPotionDirective(
                directive.Slot, directive.PotionId, ToPolicy(directive.Directive));
        }
        return mapped;
    }

    private static SearchPotionMode ToPolicy(PotionDirective directive) => directive switch
    {
        PotionDirective.Smart => SearchPotionMode.Smart,
        PotionDirective.Disabled => SearchPotionMode.Disabled,
        _ => throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
            $"未知药水槽策略：{directive}"),
    };
}
