// Ported from CombatSolver src/Search/TheftEncounterStrategy.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
// Encounter reward ordering follows CombatSolver src/Search/TheftEncounterStrategy.cs at ab1574c8 (MIT).
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Policy;

internal enum GremlinGoldReward : byte { NotApplicable, AtRisk, Reduced }

internal static class TheftEncounterStrategy
{
    public static bool LosesEncounterGold(GremlinGoldReward reward, in Battle battle)
    {
        if (reward != GremlinGoldReward.AtRisk)
            return reward == GremlinGoldReward.Reduced;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.Escaped) != 0 && Registry.Monster(enemy.MonsterId).NativeId == "FAT_GREMLIN")
                return true;
        }
        return false;
    }

    public static int CompareRecovery(TheftPolicy? policy,
        bool candidateWon, int candidateOutstanding, bool currentWon, int currentOutstanding)
    {
        int victory = currentWon.CompareTo(candidateWon);
        return victory != 0 ? victory : policy == TheftPolicy.PreserveResources
            ? candidateOutstanding.CompareTo(currentOutstanding) : 0;
    }
}
