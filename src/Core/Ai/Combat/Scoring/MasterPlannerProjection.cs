// Ported from CombatSolver src/Search/PowerCardValuation/Projection/MasterPlannerProjection.cs
// at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members
// Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Scoring;

internal readonly record struct MasterPlannerSkillFact(
    int Value,
    int EnergyCost,
    int TurnsUntilSeed,
    int TurnsUntilPayoff);

internal static class MasterPlannerProjection
{
    internal static int Evaluate(
        int currentEnergy,
        int futureEnergyPerTurn,
        int remainingTurns,
        int discardWindows,
        ReadOnlySpan<MasterPlannerSkillFact> skills)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(futureEnergyPerTurn);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingTurns);
        ArgumentOutOfRangeException.ThrowIfNegative(discardWindows);
        if (remainingTurns <= 1 || discardWindows == 0 || skills.IsEmpty)
            return 0;

        int maximumSeeds = Math.Min(discardWindows, skills.Length);
        int futureEnergyBudget = futureEnergyPerTurn
            * Math.Min(2, Math.Max(0, remainingTurns - 1));
        int[,,] best = new int[
            maximumSeeds + 1,
            currentEnergy + 1,
            futureEnergyBudget + 1];
        for (int count = 0; count <= maximumSeeds; count++)
            for (int energy = 0; energy <= currentEnergy; energy++)
                for (int futureEnergy = 0; futureEnergy <= futureEnergyBudget; futureEnergy++)
                    best[count, energy, futureEnergy] = -1;
        best[0, 0, 0] = 0;

        foreach (MasterPlannerSkillFact skill in skills)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(skill.Value);
            ArgumentOutOfRangeException.ThrowIfNegative(skill.EnergyCost);
            ArgumentOutOfRangeException.ThrowIfNegative(skill.TurnsUntilSeed);
            ArgumentOutOfRangeException.ThrowIfNegative(skill.TurnsUntilPayoff);
            if (skill.Value == 0
                || skill.TurnsUntilSeed > 2
                || skill.TurnsUntilPayoff >= remainingTurns)
            {
                continue;
            }
            bool currentTurnSeed = skill.TurnsUntilSeed == 0;
            int currentCost = currentTurnSeed ? skill.EnergyCost : 0;
            int futureCost = currentTurnSeed ? 0 : skill.EnergyCost;
            if (currentCost > currentEnergy || futureCost > futureEnergyBudget)
                continue;
            int discountedValue = Math.Max(
                1,
                skill.Value / Math.Max(1, skill.TurnsUntilPayoff));
            for (int count = maximumSeeds; count >= 1; count--)
            {
                for (int energy = currentEnergy; energy >= currentCost; energy--)
                {
                    for (int futureEnergy = futureEnergyBudget;
                         futureEnergy >= futureCost;
                         futureEnergy--)
                    {
                        int previous = best[
                            count - 1,
                            energy - currentCost,
                            futureEnergy - futureCost];
                        if (previous < 0)
                            continue;
                        best[count, energy, futureEnergy] = Math.Max(
                            best[count, energy, futureEnergy],
                            SaturatingAdd(previous, discountedValue));
                    }
                }
            }
        }

        int bestValue = 0;
        for (int count = 1; count <= maximumSeeds; count++)
        {
            for (int energy = 0; energy <= currentEnergy; energy++)
            {
                for (int futureEnergy = 0; futureEnergy <= futureEnergyBudget; futureEnergy++)
                {
                    if (best[count, energy, futureEnergy] > bestValue)
                        bestValue = best[count, energy, futureEnergy];
                }
            }
        }
        return bestValue;
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
