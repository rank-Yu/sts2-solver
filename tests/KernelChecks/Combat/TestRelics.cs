using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.KernelChecks;

internal static class TestRelics
{
    internal static void Set(ref Battle battle, ref Rules rules, params Relic[] rows)
    {
        RelicRoster roster = RelicRoster.Create(rows);
        roster.Import(rows, ref battle);
        rules = rules.WithRelics(roster);
    }
    internal static void Append(ref Battle battle, ref Rules rules, Relic row)
    {
        Relic[] rows = new Relic[rules.Relics.Count + 1];
        for (int i = 0; i < rows.Length - 1; i++) rows[i] = battle.GetRelic(rules, i);
        rows[^1] = row;
        Set(ref battle, ref rules, rows);
    }
}
