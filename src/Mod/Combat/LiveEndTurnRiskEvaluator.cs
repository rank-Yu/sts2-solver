// Before submitting the planned end turn, replay it from the live imported Battle. This is the
// Sts2Solver counterpart of CombatSolver src/Runtime/LiveEndTurnRiskEvaluator.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors): the live state, not the route's
// predicted state, provides the damage and death risk.
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

internal readonly record struct LiveEndTurnRiskProjection(
    int HpBefore,
    int HpAfter,
    int HpLost,
    bool PlayerDead);

internal static class LiveEndTurnRiskEvaluator
{
    internal static LiveEndTurnRiskProjection Evaluate(in Battle live, ReplayedAction plannedEndTurn,
        Rules rules)
    {
        if (plannedEndTurn.Action.Kind != PlayerActionKind.EndTurn)
            throw new InvalidOperationException("实机结束回合复核收到了非结束回合动作。");
        Battle simulated = live;
        int hpBefore = simulated.Player.CurrentHp;
        int lostBefore = simulated.Counter(Counter.CumulativeHpLost);
        StepResult result = Kernel.Step(ref simulated, plannedEndTurn.Action, rules,
            out _, out _, out _);
        if (result != StepResult.Completed)
            throw new InvalidDataException("实机结束回合复核请求了路线未提供的选牌。");
        int hpLost = simulated.Counter(Counter.CumulativeHpLost) - lostBefore;
        bool dead = simulated.Outcome == Outcome.Defeat || simulated.Player.CurrentHp <= 0;
        return new LiveEndTurnRiskProjection(hpBefore, simulated.Player.CurrentHp, hpLost, dead);
    }
}
