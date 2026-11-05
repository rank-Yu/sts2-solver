// Ported from CombatSolver src/Search/PowerCardValuation/Projection/RetainedHandTransition.cs
// at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members
// Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Scoring;

internal readonly record struct RetainedHandCardFact(int Value, int EnergyCost);

internal readonly record struct RetainedHandTransitionResult(
    int RetainedValue,
    int BlockedDrawValue,
    int PayableRetainedValue)
{
    public int NetValue => (int)Math.Clamp(
        (long)PayableRetainedValue - BlockedDrawValue,
        int.MinValue,
        int.MaxValue);
}

internal static class RetainedHandTransition
{
    internal static RetainedHandTransitionResult Evaluate(
        int nextTurnEnergy,
        int handLimit,
        int normalDrawCount,
        ReadOnlySpan<RetainedHandCardFact> retainedCards,
        ReadOnlySpan<int> nextDrawValues)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextTurnEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(handLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(normalDrawCount);
        int retainedCount = Math.Min(handLimit, retainedCards.Length);
        int retainedValue = 0;
        int payableValue = 0;
        int spent = 0;
        foreach (RetainedHandCardFact card in retainedCards[..retainedCount]
                     .ToArray()
                     .OrderByDescending(card => card.Value))
        {
            retainedValue = SaturatingAdd(retainedValue, card.Value);
            if (spent + card.EnergyCost > nextTurnEnergy)
                continue;
            spent += card.EnergyCost;
            payableValue = SaturatingAdd(payableValue, card.Value);
        }

        int retainedDrawCapacity = Math.Max(0, handLimit - retainedCount);
        int blockedDraws = Math.Max(0, normalDrawCount - retainedDrawCapacity);
        long blockedDrawValue = 0;
        int counted = Math.Min(blockedDraws, nextDrawValues.Length);
        for (int index = 0; index < counted; index++)
            blockedDrawValue += Math.Max(0, nextDrawValues[index]);
        return new(retainedValue, (int)Math.Min(int.MaxValue, blockedDrawValue), payableValue);
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
