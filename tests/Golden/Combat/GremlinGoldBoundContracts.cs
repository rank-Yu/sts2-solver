using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class GremlinGoldBoundContracts
{
    internal static void Run()
    {
        SearchCase encounter = GoldenFixtures.Search("tw-gremlin-merc");
        BeamOptions options = SearchBudgetPolicy.ApplyRegression(encounter.Options) with
        {
            BeamWidth = 100, TheftPolicy = TheftPolicy.PreserveResources, SearchThreads = 1,
        };
        BeamSolver solver = new(encounter.Rules, options);
        BeamResult win = solver.Solve(encounter.Battles, CancellationToken.None);
        solver.AssessResult(encounter.Battles, win, CancellationToken.None, () => false,
            out _, out _, out _, out var earned);
        if (earned is not { } bound || win.PrunedIncumbents == 0)
            throw new InvalidDataException("保全金币的胜利应当产生可共享的战损上限，并用于剪枝。");

        ReplayedRoute route = ReplayedRoute.Build(encounter.Battles[win.BestRootIndex],
            encounter.Rules, win.BestRoute, true);
        Battle terminal = route.Actions[^1].After;
        terminal.AddCounter(Counter.OutstandingStolenGold, 20);
        solver.AssessResult([terminal], win with { BestRootIndex = 0, BestRoute = [] },
            CancellationToken.None, () => false, out _, out _, out _, out var lostGold);
        if (lostGold.HasValue)
            throw new InvalidDataException("未追回金币的胜利不能限制后续成员的战损。");

        Battle finalTurn = route.Actions.First(a => a.Before.PlayerTurn == bound.CombatEndedTurn).Before;
        BeamSolver next = new(encounter.Rules, options with
        {
            IncumbentSeed = bound with { CombatEndedTurn = bound.CombatEndedTurn + 1 },
        });
        var faster = next.Solve([finalTurn], CancellationToken.None);
        var candidate = next.AssessResult([finalTurn], faster, CancellationToken.None, () => false,
            out _, out _, out _, out var improved);
        if (improved is not { } finish || finish.EffectiveHpDeficit != bound.EffectiveHpDeficit
            || finish.CombatEndedTurn != bound.CombatEndedTurn || candidate.Key.TheftLossRank != 0)
            throw new InvalidDataException("成员应能在相同战损下提前一回合保全金币获胜。");
    }
}
