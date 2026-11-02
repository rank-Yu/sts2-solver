// Ported semantics from CombatSolver src/Flash/Bridge/FlashRouteMaterializer.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). It assembles the primary Beam request from
// the already imported root, the compiled rules and the captured policy; the difference is that the
// Sts2Solver request uses the kernel's own root/options types instead of the old engine's bridges.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace Sts2Solver.Import;

/// <summary>Performance tiers exposed by the combat search settings.</summary>
public enum SearchPerformanceTier
{
    Low,
    Medium,
    High,
    ExtraHigh,
}

/// <summary>Frozen widths and time per beam slot for independent search members.</summary>
public readonly record struct SearchPerformanceProfile(
    int BaselineWidth, int NarrowWidth, int WideWidth, int OffensiveWidth,
    int MillisecondsPerWidth)
{
    /// <summary>Deterministic capture keeps the regression's fixed turn boundary; production overrides it.</summary>
    public const int MaxTurns = 64;
    private const int CaptureNodeLimit = 512_000;
    internal int StartMaxExpandedNodes => CaptureNodeLimit;

    public static SearchPerformanceProfile For(SearchPerformanceTier tier)
    {
        CombatSearchTier coreTier = tier switch
        {
            SearchPerformanceTier.Low => CombatSearchTier.Low,
            SearchPerformanceTier.Medium => CombatSearchTier.Medium,
            SearchPerformanceTier.High => CombatSearchTier.High,
            SearchPerformanceTier.ExtraHigh => CombatSearchTier.ExtraHigh,
            _ => throw new ArgumentOutOfRangeException(nameof(tier)),
        };
        SearchPlan preset = SearchPresets.For(coreTier, 0);
        return new(preset.BaselineWidth, preset.NarrowWidth, preset.WideWidth,
            preset.OffensiveWidth, preset.MillisecondsPerWidth);
    }

    internal SearchPlan ToSearchPlan(long memoryBytes, bool isBoss, bool isElite, int actIndex)
    {
        bool longEncounter = SearchPlan.IsLongEncounter(isBoss, isElite, actIndex);
        return new SearchPlan(BaselineWidth, NarrowWidth, WideWidth, OffensiveWidth, memoryBytes)
            { QueenStrategies = true, SetupStrategies = longEncounter, ContinuousSetupStrategies = longEncounter,
                MillisecondsPerWidth = MillisecondsPerWidth };
    }

    public static SearchPerformanceTier ParseTier(string text)
    {
        if (!Enum.TryParse(text, ignoreCase: true, out SearchPerformanceTier tier)
            || !Enum.IsDefined(tier))
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"未知搜索性能档位：{text}");
        }
        return tier;
    }
}

/// <summary>
/// Assembles a complete <see cref="SearchCase"/> for the primary Beam member. The offline host and
/// the in-game Mod call this with the same imported root and captured policy, so the encoded request
/// is identical on both sides.
/// </summary>
internal static class SearchRequestCompiler
{
    public static SearchCase CompileRequest(
        CombatState state,
        Player player,
        Rules rules,
        Battle root,
        SearchPolicy policy,
        EvaluationRootFacts facts,
        SearchPerformanceProfile profile,
        bool deterministic,
        long searchMemoryBudgetBytes)
    {
        if (profile.BaselineWidth <= 0 || profile.NarrowWidth <= 0
            || profile.WideWidth <= 0 || profile.OffensiveWidth <= 0)
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy, "搜索宽度必须为正。");
        if (!deterministic && profile.MillisecondsPerWidth <= 0)
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"单位宽度搜索时间必须为正：{profile.MillisecondsPerWidth}");

        Scorer scorer = EvaluationConfigCompiler.CompileScorer(
            state, player, rules, root, policy, facts);
        BoundFeatures bound = EvaluationConfigCompiler.CompileBound(
            state, player, root, policy, facts);
        ScoreRules score = scorer.RulesForCodec;

        // Deterministic test requests run only on the node cap. Zero would stop the solver before
        // the first parent (`OutOfBudget` compares elapsed >= budget); MaxValue disables that clock.
        TimeSpan softTime = deterministic
            ? TimeSpan.MaxValue
            : TimeSpan.FromMilliseconds(checked(profile.BaselineWidth * profile.MillisecondsPerWidth));
        PotionPolicy potions = BuildPotionPolicy(policy);
        BeamOptions options = new(
            profile.BaselineWidth,
            profile.StartMaxExpandedNodes,
            SearchPerformanceProfile.MaxTurns,
            softTime,
            root.Player.MaxHp)
        {
            Scorer = scorer,
            Bound = bound,
            Ordering = new RouteOrdering(
                root.Player.CurrentHp,
                root.Player.MaxHp,
                score.BossHpRelief,
                score.PostCombatRelicHeal),
            Potions = potions,
            EffectivePotionPolicy = ToUseMode(policy.PotionDefaultMode),
            TheftPolicy = policy.TheftPolicy is { } theft ? (TheftPolicy)(int)theft : null,
            InitialActionCount = facts.InitialActionCount,
            InitialExplicitPotionUses = facts.InitialExplicitPotionUses,
            InitialAutomaticPotionUses = facts.InitialAutomaticPotionUses,
            AmbergrisPotionId = score.AmbergrisPotionId,
            Act3BossStrategy = policy.Act3BossStrategy,
            InitialPlayerTurn = root.PlayerTurn,
            InitialSearchTurn = root.PlayerTurn,
            SecondRankBandWidth = profile.BaselineWidth,
            PreserveReplayAllocatorOpening = HasUnusedCardReplayAllocator(player),
            SearchMemoryBudgetBytes = searchMemoryBudgetBytes,
            PowerCommitments = PowerCatalogBuilder.Build(root, rules, scorer, state.RunState.TotalFloor),
            HasUnboundedGrowthTargets = policy.HasUnboundedGrowthTargets && !policy.IgnoreLongTermRewards,
        };
        return new SearchCase(rules, [root], options);
    }

    /// <summary>
    /// The default path keeps the exact object the old compiler built. Non-default potion settings
    /// only change the policy fields; the SREQ layout is untouched.
    /// </summary>
    private static PotionPolicy BuildPotionPolicy(SearchPolicy policy)
    {
        if (policy.PotionDefaultMode == SearchPotionMode.Smart
            && policy.PotionDirectives.Count == 0)
        {
            return DefaultPotionPolicy();
        }
        PotionUseMode defaultMode = ToUseMode(policy.PotionDefaultMode);
        List<PotionSlotDirective> directives = new(policy.PotionDirectives.Count);
        foreach (SearchPotionDirective directive in policy.PotionDirectives)
        {
            if (!Enum.IsDefined(directive.Mode))
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"未知药水策略：{directive.Mode}");
            }
            directives.Add(new PotionSlotDirective(
                directive.Slot, directive.PotionId, ToDirective(directive.Mode)));
        }
        return new PotionPolicy(
            int.MinValue,
            MaximumUses: null,
            new PotionStrategySnapshot(defaultMode, directives),
            defaultMode,
            ForceAllDisabled: false);
    }

    private static PotionUseMode ToUseMode(SearchPotionMode mode) => mode switch
    {
        SearchPotionMode.Smart => PotionUseMode.Smart,
        SearchPotionMode.Disabled => PotionUseMode.Disabled,
        _ => throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
            $"未知药水策略：{mode}"),
    };

    private static PotionDirective ToDirective(SearchPotionMode mode) => mode switch
    {
        SearchPotionMode.Smart => PotionDirective.Smart,
        SearchPotionMode.Disabled => PotionDirective.Disabled,
        SearchPotionMode.Force => PotionDirective.Force,
        _ => throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
            $"未知药水策略：{mode}"),
    };

    private static PotionPolicy DefaultPotionPolicy()
        => new(
            int.MinValue,
            MaximumUses: null,
            new PotionStrategySnapshot(PotionUseMode.Smart, Array.Empty<PotionSlotDirective>()),
            PotionUseMode.Smart,
            ForceAllDisabled: false);

    /// <summary><c>CombatRootSnapshot.HasUnusedCardReplayAllocator</c>.</summary>
    private static bool HasUnusedCardReplayAllocator(Player player)
    {
        foreach (RelicModel relic in player.Relics)
        {
            if (!relic.IsMelted && relic is ThrowingAxe axe)
                return !NativeAccess.ThrowingAxeUsed(axe);
        }
        return false;
    }
}
