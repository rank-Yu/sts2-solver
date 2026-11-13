using System.Runtime.CompilerServices;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>A request-wide optimistic cost, before member-specific potion restrictions.</summary>
internal sealed record VictoryStopTarget(int? EffectiveHpDeficit, int StrategicHpDeficit = 0)
{
    internal static readonly VictoryStopTarget Unknown = new((int?)null);
}

internal sealed partial class BeamSolver
{
    private VictoryStopTarget? _victoryTarget;
    private bool _victoryTargetReached;

    internal VictoryStopTarget PrepareVictoryTarget(IReadOnlyList<Battle> roots)
        => _victoryTarget ??= _options.VictoryTarget ?? CaptureVictoryTarget(roots);

    private VictoryStopTarget CaptureVictoryTarget(IReadOnlyList<Battle> roots)
    {
        BoundFeatures? bound = _options.Bound;
        if (bound is null || !_zeroGrowthBudget || _options.HasUnboundedGrowthTargets
            || !_potionCostMonotone || roots.Count == 0)
            return VictoryStopTarget.Unknown;
        int bestEffective = int.MaxValue;
        int bestStrategic = int.MaxValue;
        foreach (Battle root in roots)
        {
            int allowed = 0;
            for (int slot = 0; slot < root.PotionSlotCount; slot++)
                if (root.Potions[slot] != 0
                    && _options.Potions.AllowsExplicitUse(slot, (ushort)(root.Potions[slot] - 1)))
                    allowed |= 1 << slot;
            // There are at most five slots. Examine every legal subset, including no potion;
            // a member that forbids potions still receives this common portfolio target.
            for (int mask = allowed; ; mask = (mask - 1) & allowed)
            {
                Unsafe.SkipInit(out Battle healingRoot);
                Unsafe.SkipInit(out Battle pricedRoot);
                Battle.CopyPacked(in root, ref healingRoot);
                Battle.CopyPacked(in root, ref pricedRoot);
                for (int slot = 0; slot < root.PotionSlotCount; slot++)
                {
                    if ((mask & (1 << slot)) != 0)
                        pricedRoot.Potions[slot] = 0;
                    else
                        healingRoot.Potions[slot] = 0;
                }
                if (!bound.TryVictoryHealing(healingRoot, _rules, out long healing, out long maxHpGain))
                    return VictoryStopTarget.Unknown;
                long headroom = Math.Max(0, root.Player.MaxHp - root.Player.CurrentHp) + maxHpGain;
                int recovered = (int)Math.Clamp(
                    root.Counter(Counter.RecoveredHp) + Math.Min(headroom, healing + maxHpGain),
                    0L, int.MaxValue);
                int strategic = ActEndingBossPolicy.StrategicHpDeficit(
                    root.Counter(Counter.CumulativeHpLost),
                    (int)Math.Max(0L, _scorer.RulesForCodec.InitialPlayerMaxHp
                        - root.Player.MaxHp - maxHpGain),
                    recovered, _ordering.BossHpRelief,
                    root.Counter(Counter.DeathSaveRelicHpRestored)
                        + root.Counter(Counter.DeathSavePotionHpRestored)) - _relicCreditCeiling;
                int effective = (int)Math.Clamp((long)strategic
                    + PotionHpCost(OptionalPotionPrice(_scorer.MeasurePotions(pricedRoot))),
                    int.MinValue, int.MaxValue);
                if (effective < bestEffective || effective == bestEffective && strategic < bestStrategic)
                    (bestEffective, bestStrategic) = (effective, strategic);
                if (mask == 0)
                    break;
            }
        }
        return new VictoryStopTarget(bestEffective, bestStrategic);
    }

    private bool MeetsVictoryTarget(in Battle state, in ScoreFeatures features, in RouteKey key)
    {
        if (!key.Won || (state.Flags & BattleFlags.HasRisk) != 0
            || features.ProjectedDeathSaveUseCount != 0
            || !_zeroGrowthBudget || _options.HasUnboundedGrowthTargets
            || !_scorer.GrowthTargetsSatisfied(features) || !_scorer.RelicGoalsMet(features)
            || key.OutstandingStolenResource != 0
            || features.PlayerMaxHp < _scorer.RulesForCodec.InitialPlayerMaxHp
            || _options.Potions.ForceAllDisabled && features.ExplicitPotionUseCount != 0)
            return false;
        if (_victoryTarget is { EffectiveHpDeficit: { } target } known)
            return key.EffectiveHpDeficit < target
                || key.EffectiveHpDeficit == target && key.StrategicHpDeficit <= known.StrategicHpDeficit;
        // When the finite ceiling is unknown, both stopping sites use the same full-HP rule.
        return features.ExplicitPotionUseCount == 0 && features.CumulativePlayerHpLost == 0
            && features.PlayerHp >= features.PlayerMaxHp;
    }
}
