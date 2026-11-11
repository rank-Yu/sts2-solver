// Ported from CombatSolver src/Search/CombatBeamSolver.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Quality;

/// <summary>
/// The best selectable victory found so far, potion routes included. <see cref="EffectiveHpDeficit"/> is
/// the route key's effective deficit: the strategic HP deficit net of the goal credit it earned, plus its
/// optional potions priced in HP. <see cref="GoalsComplete"/> is false while some relic counter target is
/// unmet, and then an equal-value state may still beat it on the goals.
/// </summary>
internal readonly record struct SearchIncumbent(
    int EffectiveHpDeficit,
    int CombatEndedTurn,
    int GoalHpCredit = 0,
    int GoalCount = 0,
    bool GoalsComplete = true);
