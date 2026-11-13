namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Canonical storage for adjacent, unrestricted relative cost modifiers with identical expiration.
/// Amounts add before the final cost clamp; absolute and reduce-only entries remain order barriers.
/// Zero totals retain one entry, preserving the native HasLocalModifiers distinction.
/// </summary>
public static class CardCostModifiers
{
    public static void Append(ref Card card, CostModifier modifier)
    {
        int count = card.EnergyModifierCount;
        if ((uint)count > Limits.CardEnergyCostModifiers)
            throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
        int first = count;
        int sum = modifier.Amount;
        if (modifier.Type == CostType.Relative && !modifier.IsReduceOnly)
        {
            while (first > 0 && card.EnergyModifiers[first - 1].Packed == modifier.Packed)
                sum += card.EnergyModifiers[--first].Amount;
        }
        if (first == count)
        {
            if (count == Limits.CardEnergyCostModifiers)
                throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
            card.EnergyModifiers[count] = modifier;
            card.EnergyModifierCount = count + 1;
            return;
        }
        // Canonical short-sized chunks make the representation independent of prior grouping,
        // including when expiration removes a barrier between two previously merged runs.
        int chunk = sum < 0 ? short.MinValue : short.MaxValue;
        int needed = Math.Max(1, (Math.Abs(sum) + Math.Abs(chunk) - 1) / Math.Abs(chunk));
        int end = first + needed;
        if (end > Limits.CardEnergyCostModifiers)
            throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
        for (int index = first; index < end; index++)
        {
            int amount = index + 1 == end ? sum : chunk;
            card.EnergyModifiers[index] = new CostModifier
            {
                Amount = checked((short)amount), Packed = modifier.Packed,
            };
            sum -= amount;
        }
        for (int index = end; index < count; index++)
            card.EnergyModifiers[index] = default;
        card.EnergyModifierCount = end;
    }

    /// <summary>Used after expiration and at cold import/decode boundaries; no per-node scan.</summary>
    public static void Compact(ref Card card)
    {
        int count = card.EnergyModifierCount;
        if ((uint)count > Limits.CardEnergyCostModifiers)
            throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
        if (count < 2)
            return;
        card.EnergyModifierCount = 0;
        for (int index = 0; index < count; index++)
        {
            CostModifier modifier = card.EnergyModifiers[index];
            Append(ref card, modifier);
        }
        for (int index = card.EnergyModifierCount; index < count; index++)
            card.EnergyModifiers[index] = default;
    }

    /// <summary>Canonicalize an old byte-image root when loading it, including detached snapshots.</summary>
    public static void Compact(ref Battle battle)
    {
        for (int index = 0; index < battle.CardCount; index++)
            Compact(ref battle.Cards[index]);
        for (int index = 0; index < Limits.DetachedCards; index++)
            if (battle.DetachedCards[index].IsDetachedCardPresent)
                Compact(ref battle.DetachedCards[index]);
        if ((battle.Flags & BattleFlags.HistoryCourseCardPresent) != 0)
            Compact(ref battle.HistoryCourseCard);
    }
}
