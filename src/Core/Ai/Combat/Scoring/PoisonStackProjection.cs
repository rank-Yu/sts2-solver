// Ported from CombatSolver src/Search/PowerCardValuation/Projection/PoisonStackProjection.cs
// at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members
// Sts2Solver reads. DelayedStackDamage is a Sts2Solver addition for state valuation; it composes
// the ported ExtraTriggerDamage once per trigger instead of duplicating the trigger arithmetic.
namespace Sts2Solver.Ai.Combat.Scoring;

internal static class PoisonStackProjection
{
    internal static int ExtraTriggerDamage(
        int poison,
        int extraTriggers,
        int targetHp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(poison);
        ArgumentOutOfRangeException.ThrowIfNegative(extraTriggers);
        ArgumentOutOfRangeException.ThrowIfNegative(targetHp);
        return (int)Math.Min(targetHp, (long)poison * extraTriggers);
    }

    internal static int RecurringApplicationDamage(
        int poisonPerTurn,
        int futureTurns,
        int targetHp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(poisonPerTurn);
        ArgumentOutOfRangeException.ThrowIfNegative(futureTurns);
        ArgumentOutOfRangeException.ThrowIfNegative(targetHp);
        int incrementalPoison = 0;
        long damage = 0;
        for (int turn = 0; turn < futureTurns; turn++)
        {
            incrementalPoison = (int)Math.Min(
                int.MaxValue,
                (long)Math.Max(0, incrementalPoison - 1) + poisonPerTurn);
            damage += incrementalPoison;
        }
        return (int)Math.Min(targetHp, damage);
    }

    /// <summary>
    /// Damage from an existing stack over whole turns: each trigger uses the current stack and
    /// decrements it once. <see cref="ExtraTriggerDamage"/> is the per-trigger primitive; this is
    /// the same sequence the game resolves for <c>PoisonPower.Trigger</c>.
    /// </summary>
    internal static int DelayedStackDamage(
        int poison,
        int triggersPerTurn,
        int turns,
        int targetHp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(poison);
        ArgumentOutOfRangeException.ThrowIfNegative(triggersPerTurn);
        ArgumentOutOfRangeException.ThrowIfNegative(turns);
        ArgumentOutOfRangeException.ThrowIfNegative(targetHp);
        int hits = (int)Math.Min(
            Math.Min((long)poison, (long)triggersPerTurn * turns),
            targetHp);
        int damage = 0;
        int remaining = targetHp;
        for (int hit = 0; hit < hits; hit++)
        {
            int dealt = ExtraTriggerDamage(poison - hit, 1, remaining);
            damage += dealt;
            remaining -= dealt;
            if (remaining == 0)
                break;
        }
        return damage;
    }
}
