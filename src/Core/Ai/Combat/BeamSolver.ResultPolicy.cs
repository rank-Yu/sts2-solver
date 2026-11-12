using System.Runtime.CompilerServices;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Quality;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>Replays and validates a member result against the request's original policy.</summary>
    internal Candidate AssessResult(IReadOnlyList<Battle> roots, BeamResult result,
        CancellationToken token, Func<bool> expired, out Outcome outcome, out int hpLost, out bool goalReached)
        => AssessResult(roots, result, token, expired, out outcome, out hpLost, out goalReached, out _);

    /// <summary>
    /// As above; <paramref name="incumbent"/> is the pruning bound the route sets for later members (a
    /// victory without death save or risk, valued by its effective deficit), or null. Whether the
    /// potion policy admits the route is the caller's check.
    /// </summary>
    internal Candidate AssessResult(IReadOnlyList<Battle> roots, BeamResult result,
        CancellationToken token, Func<bool> expired, out Outcome outcome, out int hpLost, out bool goalReached,
        out SearchIncumbent? incumbent)
    {
        if ((uint)result.BestRootIndex >= (uint)roots.Count)
            throw new InvalidDataException("搜索路线的根索引无效。");
        token.ThrowIfCancellationRequested();
        if (expired()) throw new TimeoutException();
        Battle root = roots[result.BestRootIndex];
        Unsafe.SkipInit(out Battle end);
        Battle.CopyPacked(in root, ref end);
        int brightestFlameSpent = _options.InitialBrightestFlameMaxHpSpent;
        for (int i = 0; i < result.BestRoute.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (expired()) throw new TimeoutException();
            PlayerAction action = result.BestRoute[i];
            Unsafe.SkipInit(out Battle before);
            Battle.CopyPacked(in end, ref before);
            if (action.Kind == PlayerActionKind.UsePotion)
            {
                int slot = action.PotionSlot;
                if ((uint)slot >= before.PotionSlotCount || before.Potions[slot] == 0)
                    throw new InvalidDataException("路线包含不允许使用的药水。");
                ushort potion = (ushort)(before.Potions[slot] - 1);
                if (!_options.Potions.AllowsExplicitUse(slot, potion))
                    throw new InvalidDataException("路线违反药水保护指令。");
            }
            for (int prefix = 0; prefix <= action.ChoiceCount; prefix++)
            {
                token.ThrowIfCancellationRequested();
                if (expired()) throw new TimeoutException();
                Unsafe.SkipInit(out Battle partial);
                Battle.CopyPacked(in before, ref partial);
                PlayerAction partialAction = action;
                partialAction.ChoiceCount = (byte)prefix;
                StepResult step = Kernel.Step(ref partial, partialAction, _rules, out _,
                    out _, out _, out int actionBrightestFlameSpent);
                if (prefix < action.ChoiceCount && step != StepResult.NeedsChoice
                    || prefix == action.ChoiceCount && step != StepResult.Completed)
                    throw new InvalidDataException("路线的选择无法回放。");
                if (prefix == action.ChoiceCount)
                {
                    if (_options.BrightestFlameMaxHpLossLimit.HasValue)
                    {
                        brightestFlameSpent = checked(brightestFlameSpent + actionBrightestFlameSpent);
                        if (!GrowthCostPolicy.AllowsBrightestFlame(
                                _options.BrightestFlameMaxHpLossLimit,
                                _options.InitialBrightestFlameMaxHpSpent, brightestFlameSpent))
                        {
                            throw new InvalidDataException("路线超过至亮之焰最大生命消耗上限。");
                        }
                    }
                    Battle.CopyPacked(in partial, ref end);
                }
            }
        }
        outcome = end.Outcome;
        if ((outcome == Outcome.Victory) != result.BestIsVictory)
            throw new InvalidDataException("路线结局与搜索结果不一致。");
        int actionCount = result.BestRoute.Length;
        ScoreFeatures features = _scorer.Features(end, actionCount, includeRetentionOnly: false);
        hpLost = features.CumulativePlayerHpLost;
        RouteKey key = Key(end, features, actionCount);
        incumbent = _bound is not null && key.Won && features.ProjectedDeathSaveUseCount == 0
            && !_bound.HasRisk(end)
                ? new SearchIncumbent(key.EffectiveHpDeficit, end.PlayerTurn, features.StrategyGoalHpCredit,
                    features.StrategyGoalCount, _scorer.RelicGoalsMet(features))
                : null;
        bool goalsSatisfied = _scorer.GrowthTargetsSatisfied(features) && _scorer.RelicGoalsMet(features);
        bool safeVictory = key.Won && features.ProjectedDeathSaveUseCount == 0 && goalsSatisfied;
        goalReached = safeVictory && !_options.HasUnboundedGrowthTargets
            && _scorer.RulesForCodec.GrowthBudgets.Total == 0
            && features.ExplicitPotionUseCount == 0
            && features.CumulativePlayerHpLost == 0
            && features.PlayerHp >= _ordering.InitialPlayerHp
            && features.PlayerMaxHp >= _scorer.RulesForCodec.InitialPlayerMaxHp
            && !_ordering.PostCombatRelicHeal.ThresholdHealPaysOff(features.PlayerHp, features.PlayerMaxHp)
            && key.OutstandingStolenResource == 0;
        return new Candidate(0, key, features.ExplicitPotionUseCount,
            features.ProjectedDeathSaveUseCount);
    }

    // Per-bottle directives are enforced while expanding and replaying actions.
    internal bool IsPolicyEligible(in Candidate candidate)
        => !_options.Potions.ForceAllDisabled || candidate.ExplicitPotionUseCount == 0;

    internal int ComparePolicyResults(in Candidate left, in Candidate right) => CompareAudited(left, right);

    /// <summary>The last player turn a replayable result reaches; false when it cannot be scored.</summary>
    internal bool TryGetResultThroughTurn(IReadOnlyList<Battle> roots, in BeamResult result,
        out int throughTurn)
    {
        throughTurn = 0;
        return TryEvaluateResultQuality(roots, result, out _, out throughTurn);
    }
}
