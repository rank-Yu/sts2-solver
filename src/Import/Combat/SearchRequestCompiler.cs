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

/// <summary>
/// Frozen widths, time per beam slot and expanded-node ceiling per beam slot for independent
/// search members.
/// </summary>
public readonly record struct SearchPerformanceProfile(
    int BaselineWidth, int NarrowWidth, int WideWidth, int OffensiveWidth,
    int MillisecondsPerWidth)
{
    /// <summary>Expanded-node ceiling per beam slot (<see cref="SearchPlan.NodesPerWidth"/>).</summary>
    public int NodesPerWidth { get; init; }

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
            preset.OffensiveWidth, preset.MillisecondsPerWidth)
        {
            NodesPerWidth = preset.NodesPerWidth,
        };
    }

    internal SearchPlan ToSearchPlan(long memoryBytes, bool isBoss, bool isElite, int actIndex)
    {
        bool longEncounter = SearchPlan.IsLongEncounter(isBoss, isElite, actIndex);
        return new SearchPlan(BaselineWidth, NarrowWidth, WideWidth, OffensiveWidth, memoryBytes)
            { QueenStrategies = true, SetupStrategies = longEncounter, ContinuousSetupStrategies = longEncounter,
                MillisecondsPerWidth = MillisecondsPerWidth, NodesPerWidth = NodesPerWidth };
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
        => CompileCase(state, player, rules, root, [root], plans: null, policy, facts, profile,
            deterministic, searchMemoryBudgetBytes);

    /// <summary>
    /// Compiles a multi-root turn-setup request: the Start root supplies the frozen evaluation
    /// (TurnSetupRoot, score table, bound and route ordering) and the enumerated Play roots are the
    /// search roots. The host-only plans stay on <see cref="SearchCase.TurnSetupPlans"/>; they are
    /// not part of SREQ.
    /// </summary>
    public static SearchCase CompileSetupRequest(
        CombatState state,
        Player player,
        Rules rules,
        Battle startRoot,
        IReadOnlyList<TurnSetupBranch> branches,
        SearchPolicy policy,
        EvaluationRootFacts facts,
        SearchPerformanceProfile profile,
        bool deterministic,
        long searchMemoryBudgetBytes)
    {
        if (startRoot.Phase != PlayerPhase.Start)
            throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                $"准备请求的 Start 根相位是 {startRoot.Phase}，不是 Start。");
        if (branches.Count == 0)
            throw new UnsupportedImportException(ImportReason.TurnSetupRoot, "准备枚举没有分支。");
        Battle[] roots = new Battle[branches.Count];
        PlannedChoice[][] plans = new PlannedChoice[branches.Count][];
        for (int index = 0; index < branches.Count; index++)
        {
            TurnSetupBranch branch = branches[index];
            if (branch.Terminal)
            {
                throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                    $"准备分支 {index} 在准备过程中结束了战斗；搜索请求不能表示终局准备根。");
            }
            ValidateSetupPlayRoot(startRoot, branch.Root, index);
            roots[index] = branch.Root;
            plans[index] = branch.Choices;
        }
        // Every Play root must agree on the facts the request freezes once: the search layer's
        // ordering, initial HP and potion ledger are root-global.
        for (int index = 1; index < roots.Length; index++)
        {
            if (roots[index].PlayerTurn != roots[0].PlayerTurn
                || roots[index].Round != roots[0].Round)
            {
                throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                    $"准备根 {index} 的回合 {roots[index].PlayerTurn}/轮 {roots[index].Round} "
                    + $"与根 0 的 {roots[0].PlayerTurn}/{roots[0].Round} 不一致。");
            }
            if (roots[index].Player.MaxHp != roots[0].Player.MaxHp
                || roots[index].PotionSlotCount != roots[0].PotionSlotCount
                || roots[index].EnemyCount != roots[0].EnemyCount)
            {
                throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                    $"准备根 {index} 的最大生命/药水槽/敌人数与根 0 不一致。");
            }
            if (!PotionSlotsEqual(in roots[index], in roots[0]))
            {
                throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                    $"准备根 {index} 的药水槽内容与根 0 不一致。");
            }
        }
        EvaluationRootFacts setupFacts = facts with
        {
            TurnSetupRoot = true,
        };
        return CompileCase(state, player, rules, startRoot, roots, plans, policy, setupFacts,
            profile, deterministic, searchMemoryBudgetBytes);
    }

    private static void ValidateSetupPlayRoot(in Battle start, in Battle root, int index)
    {
        if (root.Phase != PlayerPhase.Play)
        {
            throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                $"准备根 {index} 的相位是 {root.Phase}，不是 Play。");
        }
        if (root.Outcome != Outcome.None)
        {
            throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                $"准备根 {index} 已经终局（{root.Outcome}）。");
        }
        if (root.Player.MaxHp != start.Player.MaxHp
            || root.PotionSlotCount != start.PotionSlotCount
            || root.EnemyCount != start.EnemyCount)
        {
            throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                $"准备根 {index} 的最大生命/药水槽/敌人数与 Start 根不一致。");
        }
        if (root.PlayerTurn != start.PlayerTurn || root.Round != start.Round)
        {
            throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                $"准备根 {index} 的回合 {root.PlayerTurn}/轮 {root.Round} "
                + $"与 Start 根的 {start.PlayerTurn}/{start.Round} 不一致。");
        }
        if (!PotionSlotsEqual(in root, in start))
        {
            throw new UnsupportedImportException(ImportReason.TurnSetupRoot,
                $"准备根 {index} 的药水槽内容与 Start 根不一致。");
        }
    }

    private static bool PotionSlotsEqual(in Battle left, in Battle right)
    {
        for (int slot = 0; slot < Limits.PotionSlots; slot++)
        {
            if (left.Potions[slot] != right.Potions[slot])
                return false;
        }
        return true;
    }

    private static SearchCase CompileCase(
        CombatState state,
        Player player,
        Rules rules,
        Battle evaluationRoot,
        Battle[] battles,
        IReadOnlyList<PlannedChoice[]>? plans,
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
        if (policy.BrightestFlameMaxHpLossLimit is < 0 or > 1000)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"至亮之焰最大生命消耗上限必须在 0..1000 或 null：{policy.BrightestFlameMaxHpLossLimit}");
        }

        Scorer scorer = EvaluationConfigCompiler.CompileScorer(
            state, player, rules, evaluationRoot, policy, facts);
        BoundFeatures bound = EvaluationConfigCompiler.CompileBound(
            state, player, evaluationRoot, policy, facts);
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
            softTime)
        {
            Scorer = scorer,
            Bound = bound,
            Ordering = new RouteOrdering(
                evaluationRoot.Player.CurrentHp,
                score.BossHpRelief,
                score.PostCombatRelicHeal),
            Potions = potions,
            EffectivePotionPolicy = ToUseMode(policy.PotionDefaultMode),
            TheftPolicy = policy.TheftPolicy is { } theft ? (TheftPolicy)(int)theft : null,
            Act3BossStrategy = policy.Act3BossStrategy,
            InitialPlayerTurn = battles[0].PlayerTurn,
            PreserveReplayAllocatorOpening = HasUnusedCardReplayAllocator(player),
            SearchMemoryBudgetBytes = searchMemoryBudgetBytes,
            PowerCommitments = PowerCatalogBuilder.Build(evaluationRoot, rules, scorer,
                state.RunState.TotalFloor),
            HasUnboundedGrowthTargets = policy.HasUnboundedGrowthTargets && !policy.IgnoreGrowthRewards,
            BrightestFlameMaxHpLossLimit = policy.BrightestFlameMaxHpLossLimit,
            InitialBrightestFlameMaxHpSpent = policy.BrightestFlameMaxHpLossLimit.HasValue
                ? NativeHistory.BrightestFlameMaxHpSpent() : 0,
        };
        return new SearchCase(rules, battles, options) { TurnSetupPlans = plans };
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
        _ => throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
            $"未知药水策略：{mode}"),
    };

    private static PotionPolicy DefaultPotionPolicy()
        => new(
            int.MinValue,
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
