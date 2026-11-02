using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>Replays and validates a member result against the request's original policy.</summary>
    internal Candidate AssessResult(IReadOnlyList<Battle> roots, BeamResult result,
        CancellationToken token, Func<bool> expired, out Outcome outcome, out int hpLost, out bool goalReached)
    {
        if ((uint)result.BestRootIndex >= (uint)roots.Count)
            throw new InvalidDataException("搜索路线的根索引无效。");
        token.ThrowIfCancellationRequested();
        if (expired()) throw new TimeoutException();
        Battle root = roots[result.BestRootIndex];
        Battle end = root;
        int uses = _initialExplicitPotionUses;
        int ambergris = _initialAmbergrisUses;
        List<PotionUse> potionUses = [];
        for (int i = 0; i < result.BestRoute.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (expired()) throw new TimeoutException();
            PlayerAction action = result.BestRoute[i];
            Battle before = end;
            if (action.Kind == PlayerActionKind.UsePotion)
            {
                int slot = action.PotionSlot;
                if ((uint)slot >= before.PotionSlotCount || before.Potions[slot] == 0
                    || before.PlayerTurn < _options.Potions.EarliestPlayerTurn)
                    throw new InvalidDataException("路线包含不允许使用的药水。");
                ushort potion = (ushort)(before.Potions[slot] - 1);
                if (!_options.Potions.AllowsExplicitUse(slot, potion))
                    throw new InvalidDataException("路线违反药水保护指令。");
                uses++;
                if (potion == _ambergrisPotionId) ambergris++;
                int cost = potion < _scorer.RulesForCodec.PotionStrategicCost.Length
                    ? _scorer.RulesForCodec.PotionStrategicCost[potion]
                    : 0;
                potionUses.Add(new PotionUse(slot, Registry.Potion(potion).NativeId, cost));
            }
            for (int prefix = 0; prefix <= action.ChoiceCount; prefix++)
            {
                token.ThrowIfCancellationRequested();
                if (expired()) throw new TimeoutException();
                Battle partial = before;
                PlayerAction partialAction = action;
                partialAction.ChoiceCount = (byte)prefix;
                StepResult step = Kernel.Step(ref partial, partialAction, _rules, out _);
                if (prefix < action.ChoiceCount && step != StepResult.NeedsChoice
                    || prefix == action.ChoiceCount && step != StepResult.Completed)
                    throw new InvalidDataException("路线的选择无法回放。");
                if (prefix == action.ChoiceCount) end = partial;
            }
        }
        outcome = end.Outcome;
        if ((outcome == Outcome.Victory) != result.BestIsVictory)
            throw new InvalidDataException("路线结局与搜索结果不一致。");
        if (_options.Potions.MaximumUses is { } max && uses > max)
            throw new InvalidDataException("路线超过药水使用上限。");
        int actionCount = _options.InitialActionCount + result.BestRoute.Length;
        ScoreFeatures features = _scorer.Features(end, actionCount, includeRetentionOnly: false);
        hpLost = features.CumulativePlayerHpLost;
        ForcedPotionUseEvaluation forced = _options.Potions.Strategy.EvaluateForcedUses(potionUses);
        int optional = Math.Max(0, features.ExplicitPotionUseCount - forced.ForcedUseCount);
        int optionalCost = OptionalPotionStrategicCost(
            Math.Max(0, features.ExplicitPotionStrategicCost - forced.ForcedStrategicHpCost),
            optional);
        RouteKey key = Key(end, features, actionCount);
        bool goalsSatisfied = _scorer.GrowthTargetsSatisfied(features) && _scorer.RelicTargetsSatisfied(features);
        bool safeVictory = key.Won && features.ProjectedDeathSaveUseCount == 0 && goalsSatisfied;
        goalReached = safeVictory && !_options.HasUnboundedGrowthTargets
            && _scorer.RulesForCodec.GrowthBudgets.Total == 0
            && features.ExplicitPotionUseCount == 0
            && forced.AllForcedUsesSatisfied
            && features.CumulativePlayerHpLost == 0
            && features.PlayerHp >= _ordering.InitialPlayerHp
            && features.PlayerMaxHp >= _ordering.InitialPlayerMaxHp
            && key.OutstandingStolenResource == 0;
        PotionUseMode mode = _effectivePotionPolicy switch
        {
            PotionUseMode.RequireAtLeastOne when forced.ForcedUseCount > 0 => PotionUseMode.Smart,
            PotionUseMode.Disabled when optional > 0 => PotionUseMode.Smart,
            _ => _effectivePotionPolicy,
        };
        key = key with
        {
            OptionalPotionCount = optional,
            PolicyHpDeficit = mode == PotionUseMode.RequireAtLeastOne
                ? key.StrategicHpDeficit + PotionUsePolicy.AdditionalRequiredUseStrategicHpCost(optionalCost)
                : key.StrategicHpDeficit,
        };
        return new Candidate(0, key, features.ExplicitPotionUseCount, features.PotionUseCount,
            features.ExplicitPotionStrategicCost, optionalCost, Math.Max(0, ambergris - forced.ForcedAmbergrisCount),
            features.PlayerHp, features.ProjectedDeathSaveUseCount, mode,
            forced.AllForcedUsesSatisfied);
    }

    internal bool IsPolicyEligible(in Candidate candidate, Candidate? baseline)
    {
        Candidate free = baseline.GetValueOrDefault();
        return IsAuditedPolicyEligible(candidate, baseline.HasValue, baseline.HasValue && free.Key.Won,
            baseline.HasValue ? free.Key.StrategicHpDeficit : _ordering.InitialPlayerHp,
            baseline.HasValue ? free.PlayerHp : 0,
            baseline.HasValue ? free.ProjectedDeathSaveUseCount : int.MaxValue,
            baseline.HasValue ? free.Key.OutstandingStolenResource : int.MaxValue,
            candidate.Key.Won || baseline.HasValue && free.Key.Won);
    }

    internal int ComparePolicyResults(in Candidate left, in Candidate right) => CompareAudited(left, right);
    internal static PotionFreePolicyBaseline PotionBaseline(in Candidate candidate) => new(
        candidate.Key.Won, candidate.Key.StrategicHpDeficit, candidate.PlayerHp,
        candidate.Key.Won ? candidate.Key.CombatEndedTurn : null)
    { DeathSaveUseCount = candidate.ProjectedDeathSaveUseCount,
      OutstandingStolenResource = candidate.Key.OutstandingStolenResource };
}
