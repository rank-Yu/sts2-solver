using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Policy;

/// <summary>
/// One owned relic whose counter carries into the next combat. <see cref="Slot"/> indexes
/// <see cref="Battle.Relics"/>; the counter is read modulo <see cref="Period"/>, and
/// <see cref="Goal"/> is the value one short of the relic triggering, so the relic fires at the first
/// opportunity of the next combat. A won route that ends with the counter on the goal earns
/// <see cref="HpAllowance"/> once; an allowance of zero still prefers the goal among routes of equal HP.
/// </summary>
internal readonly record struct RelicCounterTarget(byte Slot, byte Period, byte Goal, int HpAllowance);

/// <summary>
/// What a state's relic counters earn: the credit and number of the goals a won state meets. Both are zero
/// on every other state, because the counters only carry over from a won fight.
/// </summary>
internal readonly record struct RelicCounterGoals(int HpCredit, int Count)
{
    internal static RelicCounterGoals Evaluate(in Battle battle, RelicCounterTarget[] targets, bool won)
    {
        if (!won)
            return default;
        int credit = 0;
        int count = 0;
        foreach (RelicCounterTarget target in targets)
        {
            int value = battle.Relics[target.Slot].Counter % target.Period;
            if (value < 0)
                value += target.Period;
            if (value == target.Goal)
            {
                credit = checked(credit + target.HpAllowance);
                count++;
            }
        }
        return new RelicCounterGoals(credit, count);
    }
}
