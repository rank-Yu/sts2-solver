using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>A conservative policy check made once at a default potion-stage boundary.</summary>
internal static class PotionStageGate
{
    internal static bool HasRunCombination(IReadOnlyList<SearchMemberResult> completed, SearchMember member)
    {
        foreach (SearchMemberResult result in completed)
            if (result.Ran && result.Member.Width == member.Width
                && result.Member.Evaluation == member.Evaluation && result.Member.Retention == member.Retention)
                return true;
        return false;
    }

    internal static bool CanSkip(IReadOnlyList<Battle> roots, Rules rules, BeamOptions options,
        BeamSolver.Candidate? selected, BeamSolver.Candidate? freeBaseline)
    {
        if (selected is not { } best || !best.Key.Won || !best.Key.Survives
            || best.ExplicitPotionUseCount != 0 || best.ProjectedDeathSaveUseCount != 0
            || freeBaseline is not { } free || !free.Key.Won || !free.Key.Survives
            || free.ExplicitPotionUseCount != 0 || free.ProjectedDeathSaveUseCount != 0
            || options.InitialExplicitPotionUses != 0 || options.InitialAutomaticPotionUses != 0
            || options.MinimumPotionUses != 0
            || options.EffectivePotionPolicy != PotionUseMode.Smart
            || options.Potions.EffectivePolicy != PotionUseMode.Smart
            || options.HasUnboundedGrowthTargets
            || options.Ordering is not { } ordering
            || options.Scorer is not { } scorer || roots.Count == 0)
            return false;
        ScoreRules scores = scorer.RulesForCodec;
        if (scores.GrowthBudgets.IsEnabled || scores.RequiredGrowthRewards.IsEnabled
            || scores.RelicCounterRules.Length != 0
            || options.TheftPolicy == TheftPolicy.PreserveResources
                && (best.Key.OutstandingStolenResource != 0 || free.Key.OutstandingStolenResource != 0))
            return false;

        // A max-HP gain expands the healing headroom. The current engine supports Feed, including
        // generated copies, so an available rule is enough to disable this proof conservatively.
        if (!Registry.TryCard("FEED", out ushort feed)
            || feed < rules.CardTable.Length && rules.CardTable[feed].Length != 0)
            return false;

        int minimum = int.MaxValue;
        int missingHp = Math.Max(0, ordering.InitialPlayerMaxHp - ordering.InitialPlayerHp);
        foreach (Battle root in roots)
        {
            // Mid-combat roots can already carry healing and damage ledgers whose lower bound is
            // not the missing HP at this boundary. Defer rather than discard that history.
            if (root.Counter(Counter.EntropicFreePotionMask) != 0
                || root.Counter(Counter.CumulativeHpLost) != 0
                || root.Counter(Counter.RecoveredHp) != 0
                || root.Counter(Counter.DeathSaveUseCount) != 0
                || root.Counter(Counter.DeathSavePotionHpRestored) != 0
                || root.Counter(Counter.DeathSaveRelicHpRestored) != 0)
                return false;
            missingHp = Math.Max(missingHp, Math.Max(0, root.Player.MaxHp - root.Player.CurrentHp));
            for (int slot = 0; slot < root.PotionSlotCount; slot++)
            {
                ushort stored = root.Potions[slot];
                if (stored != 0 && !TryConsider((ushort)(stored - 1), slot)) return false;
                foreach (ushort potion in rules.PotionPool)
                    if (!TryConsider(potion, slot)) return false;
                foreach (ushort potion in rules.OutOfCombatPotionPool)
                    if (!TryConsider(potion, slot)) return false;
            }
        }
        if (minimum == int.MaxValue || minimum <= 0) return false;
        int discounted = PotionUsePolicy.ApplyReplacementCredit(minimum, 1, 0);
        if (discounted <= 0 || ordering.BossHpRelief == BossHpRelief.ActClearHeal && discounted > int.MaxValue / 5)
            return false;
        int required = PotionUsePolicy.SmartRequiredHpSaved(discounted, ordering.BossHpRelief);
        int recoverable = ActEndingBossPolicy.PersistentValueOfRecoveredHp(missingHp, ordering.BossHpRelief);
        // Smart eligibility is anchored to the None-stage baseline, not a better zero-potion
        // result discovered later by a potion-enabled member.
        long mostPossibleSaved = (long)free.Key.StrategicHpDeficit + recoverable;
        return mostPossibleSaved >= 0 && mostPossibleSaved < required;

        bool TryConsider(ushort potion, int slot)
        {
            if (!options.Potions.AllowsExplicitUse(slot, potion)) return true;
            string name = Registry.Potion(potion).NativeId;
            // Fruit Juice changes the lower HP bound; Entropic Brew can create zero-cost potions.
            if (name is "FRUIT_JUICE" or "ENTROPIC_BREW") return false;
            if (potion >= scores.PotionStrategicCost.Length || potion >= rules.PotionTable.Length
                || rules.PotionTable[potion] is not { } stats) return false;
            int rootCost = scores.PotionStrategicCost[potion];
            int generatedCost = stats.StrategicCost;
            if (rootCost < 0 || generatedCost < 0) return false;
            minimum = Math.Min(minimum, Math.Min(rootCost, generatedCost));
            return true;
        }
    }
}
