// Route decisions shared by the Mod controller and the offline whole-combat loop. The turn
// trimming, continuation point and deviation comparison are one implementation so the in-game
// controller and tests/Differential cannot drift. The shapes follow CombatSolver
// src/Runtime/SolverController.cs and src/Search/CombatPlan.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors), rewritten over Sts2Solver's
// ReplayedRoute.
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Autoplay;

/// <summary>One search result replayed from its root, trimmed or not, with its decision data.</summary>
internal sealed class CombatRoutePlan
{
    private CombatRoutePlan(Battle root, ReplayedAction[] actions, Rules rules, bool victory,
        bool wasReused, CombatRouteOutcome outcome, PlannedChoice[] rootSetupChoices)
    {
        Root = root;
        Actions = actions;
        Rules = rules;
        Victory = victory;
        WasReused = wasReused;
        Outcome = outcome;
        RootSetupChoices = rootSetupChoices;
    }

    internal Battle Root { get; }

    internal ReplayedAction[] Actions { get; }

    internal Rules Rules { get; }

    internal bool Victory { get; }

    internal bool WasReused { get; private init; }

    internal CombatRouteOutcome Outcome { get; }

    /// <summary>
    /// Turn-start selections that produced <see cref="Root"/> during <c>SetupPlayerTurn</c>.
    /// Empty for an ordinary Play root. The UI shows them as the root turn's start-choice pills.
    /// </summary>
    internal PlannedChoice[] RootSetupChoices { get; }

    internal int StartTurn => Actions.Length == 0 ? 0 : Actions[0].Before.PlayerTurn;

    internal static CombatRoutePlan FromReplay(ReplayedRoute route, bool onlyDeathRoutes = false,
        PlannedChoice[]? rootSetupChoices = null)
    {
        CombatRouteOutcome outcome = CombatRouteOutcome.From(route.Actions, route.Root, route.Victory,
            onlyDeathRoutes);
        return new CombatRoutePlan(route.Root, route.Actions, route.Rules, route.Victory, false,
            outcome, rootSetupChoices ?? []);
    }

    /// <summary>
    /// Cuts the route at <paramref name="turn"/>: the returned plan starts at the first action of
    /// that turn and keeps the original per-action boundaries. The projected totals stay those of
    /// the complete replayed route. Returns null when the route has no action in the turn.
    /// </summary>
    internal CombatRoutePlan? TrimToTurn(int turn)
    {
        int index = CombatRouteDecision.FindTurnIndex(Actions, turn);
        if (index < 0)
            return null;
        ReplayedAction[] sliced = new ReplayedAction[Actions.Length - index];
        for (int offset = 0; offset < sliced.Length; offset++)
        {
            ReplayedAction source = Actions[index + offset];
            sliced[offset] = new ReplayedAction
            {
                Index = offset,
                Action = source.Action,
                Before = source.Before,
                After = source.After,
                Choices = source.Choices,
                EnemySideTurnEndReached = source.EnemySideTurnEndReached,
            };
        }
        return new CombatRoutePlan(Root, sliced, Rules, Victory, true, Outcome, RootSetupChoices);
    }

    /// <summary>
    /// Keeps exactly one action, reindexed from zero. The live end-turn pause uses this to retain
    /// only the unplayed end-turn action as the current-turn route.
    /// </summary>
    internal CombatRoutePlan? TrimToAction(int index)
    {
        if (index < 0 || index >= Actions.Length)
            return null;
        ReplayedAction source = Actions[index];
        ReplayedAction sliced = new()
        {
            Index = 0,
            Action = source.Action,
            Before = source.Before,
            After = source.After,
            Choices = source.Choices,
            EnemySideTurnEndReached = source.EnemySideTurnEndReached,
        };
        return new CombatRoutePlan(Root, [sliced], Rules, Victory, WasReused, Outcome, RootSetupChoices);
    }

    /// <summary>Returns the same plan with a different root-turn setup choice list.</summary>
    internal CombatRoutePlan WithRootSetupChoices(PlannedChoice[] rootSetupChoices)
        => new(Root, Actions, Rules, Victory, WasReused, Outcome, rootSetupChoices);

    internal ReplayedAction? EndTurnAction(int turn)
    {
        foreach (ReplayedAction action in Actions)
        {
            if (action.Before.PlayerTurn == turn && action.Action.Kind == PlayerActionKind.EndTurn)
                return action;
        }
        return null;
    }
}

/// <summary>The values the panel and the full-auto pause policy read from a replayed route.</summary>
internal sealed record CombatRouteOutcome(
    int? DeathTurn,
    int? CombatEndedTurn,
    int ProjectedBattleHpLost,
    int ProjectedBattlePotionCount,
    bool Victory,
    bool OnlyDeathRoutes,
    IReadOnlyDictionary<int, int> HpLostByTurn)
{
    internal static CombatRouteOutcome From(IReadOnlyList<ReplayedAction> actions, in Battle root,
        bool victory, bool onlyDeathRoutes)
    {
        Dictionary<int, int> hpLostByTurn = [];
        int lastTurn = root.PlayerTurn;
        Outcome terminal = root.Outcome;
        if (actions.Count > 0)
        {
            ReplayedAction last = actions[^1];
            lastTurn = last.After.PlayerTurn;
            terminal = last.After.Outcome;
        }
        foreach (ReplayedAction action in actions)
        {
            int turn = action.Before.PlayerTurn;
            int lost = action.After.Counter(Counter.CumulativeHpLost)
                - action.Before.Counter(Counter.CumulativeHpLost);
            hpLostByTurn[turn] = hpLostByTurn.GetValueOrDefault(turn) + lost;
        }
        int projectedHpLost = actions.Count > 0
            ? actions[^1].After.Counter(Counter.CumulativeHpLost)
            : root.Counter(Counter.CumulativeHpLost);
        int potionUses = root.Counter(Counter.AutomaticPotionUseCount)
            + root.Counter(Counter.GeneratedPotionUseCount);
        foreach (ReplayedAction action in actions)
        {
            if (action.Action.Kind == PlayerActionKind.UsePotion)
                potionUses++;
        }
        int? combatEndedTurn = null;
        int? deathTurn = null;
        if (victory && actions.Count > 0)
            combatEndedTurn = TerminalTurn(actions[^1]);
        if (terminal == Outcome.Defeat && actions.Count > 0)
            deathTurn = lastTurn;
        return new CombatRouteOutcome(deathTurn, combatEndedTurn, projectedHpLost, potionUses,
            victory, onlyDeathRoutes, hpLostByTurn);
    }

    /// <summary>
    /// The turn a terminal route ended in. An end-turn action is replayed into the enemy side; a
    /// kill there belongs to the turn that ended, not to the next player turn.
    /// </summary>
    private static int TerminalTurn(ReplayedAction action)
        => action.Action.Kind == PlayerActionKind.EndTurn || action.EnemySideTurnEndReached
            ? action.Before.PlayerTurn
            : action.After.PlayerTurn;
}

/// <summary>Turn trimming, continuation point and boundary comparison for a replayed route.</summary>
internal static class CombatRouteDecision
{
    internal static bool NeedsSearch(IReadOnlyList<ReplayedAction> actions, int index, int turn)
        => index < 0 || index >= actions.Count || actions[index].Before.PlayerTurn != turn;

    internal static bool IsActionInTurn(ReplayedAction action, int turn)
        => action.Before.PlayerTurn == turn;

    internal static int FindTurnIndex(IReadOnlyList<ReplayedAction> actions, int turn)
    {
        for (int index = 0; index < actions.Count; index++)
        {
            if (actions[index].Before.PlayerTurn == turn)
                return index;
        }
        return -1;
    }

    internal static ReplayedAction[] CurrentTurnActions(IReadOnlyList<ReplayedAction> actions, int turn)
    {
        int start = FindTurnIndex(actions, turn);
        if (start < 0)
            return [];
        int end = start;
        while (end < actions.Count && actions[end].Before.PlayerTurn == turn)
            end++;
        ReplayedAction[] sliced = new ReplayedAction[end - start];
        for (int index = start; index < end; index++)
            sliced[index - start] = actions[index];
        return sliced;
    }

    /// <summary>Finds the first action of <paramref name="turn"/> whose boundary equals the live state.</summary>
    internal static int FindContinuationIndex(IReadOnlyList<ReplayedAction> actions, int turn, in Battle live)
    {
        int index = FindTurnIndex(actions, turn);
        return index >= 0 && BoundaryMatches(actions[index].Before, live) ? index : -1;
    }

    internal static bool ActionMatches(ReplayedAction action, in Battle live)
        => BattleReconciler.Same(action.After, live);

    internal static bool BoundaryMatches(in Battle expected, in Battle live)
        => BattleReconciler.Same(expected, live);

    internal static string[] BoundaryDiff(in Battle expected, in Battle live)
        => BattleReconciler.Diff(expected, live);
}

/// <summary>
/// The normalized live state captured when a search starts. Comparing the stamp at completion
/// rejects a result whose root changed while the worker ran; the comparison is exact over the same
/// normalized Battle surface as reconciliation.
/// </summary>
internal sealed record CombatStateStamp(Battle Normalized)
{
    internal static CombatStateStamp Capture(in Battle battle)
        => new(BattleReconciler.Normalize(battle));

    internal bool Matches(in Battle live)
        => Fingerprint.Equal(Normalized, BattleReconciler.Normalize(live));
}

/// <summary>
/// The shared consecutive-failure bound. The offline whole-combat loop and the in-game controller
/// both stop after this many search/deployment failures in one unchanged turn.
/// </summary>
internal static class CombatFailureLimit
{
    internal const int MaximumConsecutiveFailures = 3;

    internal static bool Reached(int consecutiveFailures)
        => consecutiveFailures >= MaximumConsecutiveFailures;
}

internal enum CombatPauseCode
{
    DeathTurn,
    LiveEndTurnRisk,
    NoSurvival,
}

/// <summary>
/// The full-auto pause conditions. Before a turn runs, a projected death on that turn always stops
/// the solver; a route that ends in death on a later turn (no surviving route found) stops it once
/// per combat, so the player can raise the search tier or take over before irreversible actions,
/// and resuming continues without that pause. A route that ends without an outcome (search horizon
/// or budget) never pauses. The live end-turn recheck always pauses when the live simulation dies
/// or loses more HP than the route predicted.
/// </summary>
internal static class CombatFullAutoPausePolicy
{
    /// <param name="startTurn">The turn about to be deployed.</param>
    /// <param name="outcome">The replayed route's outcome.</param>
    /// <param name="noSurvivalPause">
    /// False once this combat already paused for <see cref="CombatPauseCode.NoSurvival"/>.
    /// </param>
    internal static CombatPauseCode? BeforeDeployment(int startTurn, in CombatRouteOutcome outcome,
        bool noSurvivalPause)
        => outcome.DeathTurn == startTurn ? CombatPauseCode.DeathTurn
            : outcome.DeathTurn is not null && noSurvivalPause ? CombatPauseCode.NoSurvival
            : null;

    internal static CombatPauseCode? LiveEndTurn(int plannedHpLost, int liveHpLost, bool playerDead)
        => playerDead || liveHpLost > plannedHpLost ? CombatPauseCode.LiveEndTurnRisk : null;

    internal static string Code(CombatPauseCode code) => code switch
    {
        CombatPauseCode.DeathTurn => "death_turn",
        CombatPauseCode.LiveEndTurnRisk => "live_end_turn_risk",
        CombatPauseCode.NoSurvival => "no_survival",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
