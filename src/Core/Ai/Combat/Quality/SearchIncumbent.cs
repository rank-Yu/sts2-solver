// Ported from CombatSolver src/Search/CombatBeamSolver.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Quality;

/// <summary>
/// The best potion-free victory found so far. <see cref="StrategicHpDeficit"/> is net of the goal
/// credit it earned; <see cref="GoalsComplete"/> is false while some relic counter target is unmet, and
/// then an equal-HP state may still beat it on the goals.
/// </summary>
internal readonly record struct SearchIncumbent(
    int StrategicHpDeficit,
    int CombatEndedTurn,
    int GoalHpCredit = 0,
    int GoalCount = 0,
    bool GoalsComplete = true);
