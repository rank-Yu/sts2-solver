// Shared whole-combat decision loop. The Mod and the offline host both run this state machine; the
// host supplies the live import, search execution, native deployment and boundary projection. Keeping
// the search cadence, route ownership, failure policy and turn bookkeeping here is what makes the
// offline result the same as the in-game full combat.
using System.Diagnostics;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Autoplay;

/// <summary>One search in the whole-combat log.</summary>
internal sealed record CombatPilotSearchEntry(
    int Turn,
    string Reason,
    string Status,
    long ElapsedMs,
    long ExpandedNodes,
    int RouteActions,
    bool Victory,
    string? Error,
    long ManagedHeapPeakBytes = 0,
    long WorkingSetPeakBytes = 0,
    bool SoftTimeRisk = false,
    bool MemoryBudgetExceeded = false,
    long MemoryExceededAtBytes = 0,
    int PoolCapacityDropped = 0,
    long SearchMemoryUsedBytes = 0,
    long SearchMemoryPeakBytes = 0,
    long RouteNodeStorageBytes = 0,
    int RouteNodePeakCount = 0)
{
    public int SelectedMember { get; init; } = -1;
    public CombatSearchStatus SearchStatus { get; init; }
    public SearchMemberResult[] Members { get; init; } = [];
    public int MembersRun { get; init; }
    public int MembersSkippedByTime { get; init; }
}

/// <summary>One predicted/live boundary deviation.</summary>
internal sealed record CombatPilotDeviationEntry(
    int Turn,
    int ActionIndex,
    string Action,
    string[] Fields);

/// <summary>One search/deployment failure that did not produce a route.</summary>
internal sealed record CombatPilotFailureEntry(
    int Turn,
    string Reason,
    string Detail);

/// <summary>The whole-combat result. Field names match the Mod's <c>full-combat.json</c>.</summary>
internal sealed record CombatPilotResult(
    string Root,
    string Status,
    string Outcome,
    int Turns,
    int PlayerHp,
    int PlayerMaxHp,
    string[] PotionsUsed,
    int Searches,
    int ReusedTurns,
    int Deviations,
    int DeploymentFailures,
    long ElapsedMs,
    string? PauseReason,
    CombatPilotSearchEntry[] SearchLog,
    CombatPilotDeviationEntry[] DeviationLog,
    CombatPilotFailureEntry[] FailureLog);

/// <summary>A stable play-phase turn returned by <see cref="ICombatPilotHost.WaitForTurnAsync"/>.</summary>
internal readonly record struct CombatPilotTurn(int Turn, int PlayerHp, int PlayerMaxHp);

/// <summary>The result of one host search, including the failure detail and memory peaks.</summary>
internal sealed record CombatPilotSearch(
    ReplayedRoute? Route,
    string Status,
    long ElapsedMs,
    long ExpandedNodes,
    int RouteActions,
    bool Victory,
    string? Error,
    string? FailureReason,
    string? FailureDetail,
    long ManagedHeapPeakBytes,
    long WorkingSetPeakBytes,
    bool SoftTimeRisk = false,
    bool MemoryBudgetExceeded = false,
    long MemoryExceededAtBytes = 0,
    int PoolCapacityDropped = 0,
    long SearchMemoryUsedBytes = 0,
    long SearchMemoryPeakBytes = 0,
    long RouteNodeStorageBytes = 0,
    int RouteNodePeakCount = 0)
{
    public int SelectedMember { get; init; } = -1;
    public CombatSearchStatus SearchStatus { get; init; }
    public SearchMemberResult[] Members { get; init; } = [];
    public int MembersRun { get; init; }
    public int MembersSkippedByTime { get; init; }
}

/// <summary>A boundary projection comparison at a reused turn start.</summary>
internal sealed record CombatPilotBoundary(
    bool Same,
    string Action,
    string[] Diff);

/// <summary>The outcome of deploying one route action.</summary>
internal sealed record CombatPilotDeployment(
    bool Success,
    bool SameAfter,
    string? FailureReason,
    string? FailureDetail,
    string Action,
    string[] Diff);

/// <summary>The terminal snapshot the host reads after the loop.</summary>
internal sealed record CombatPilotFinal(
    string Outcome,
    string Status,
    int Turns,
    int PlayerHp,
    int PlayerMaxHp,
    string[] PotionsUsed);

/// <summary>
/// Host-specific work for one whole combat. The driver owns every decision that determines the
/// result (when to search, which route to keep, when a route is exhausted, failure counts); the host
/// owns native I/O and validation.
/// </summary>
internal interface ICombatPilotHost
{
    /// <summary>
    /// Waits for the next stable play-phase turn and returns it, or null when combat is over.
    /// </summary>
    ValueTask<CombatPilotTurn?> WaitForTurnAsync(CancellationToken token);

    /// <summary>Imports the current root, compiles the request, solves it and replays the route.</summary>
    ValueTask<CombatPilotSearch> SearchAsync(int turn, string reason, CancellationToken token);

    /// <summary>Compares the expected route boundary with the live state (Mod only).</summary>
    ValueTask<CombatPilotBoundary> CheckBoundaryAsync(ReplayedAction expected, int turn, int index,
        CancellationToken token);

    /// <summary>Called once before the actions of a turn are deployed.</summary>
    void BeginDeployment(int turn);

    /// <summary>Executes one route action natively (Mod) or advances the predicted state (offline).</summary>
    ValueTask<CombatPilotDeployment> DeployAsync(ReplayedAction action, int turn, CancellationToken token);

    /// <summary>Called after an action that matched its prediction.</summary>
    void OnActionDeployed(ReplayedAction action);

    /// <summary>Called when a new route starts; the Mod observes the first predicted boundary here.</summary>
    void OnRouteAccepted(ReplayedRoute route);

    /// <summary>Reads the terminal outcome after the loop.</summary>
    CombatPilotFinal Final(bool cancelled);
}

/// <summary>
/// The single whole-combat loop. Behaviour is frozen against the M2b-2 Mod loop: search at a new or
/// exhausted route, reuse the remaining route while its boundaries match, abandon the route on any
/// failure/deviation, stop after three consecutive failures in one turn, and stop at 200 turns.
/// </summary>
internal static class CombatPilot
{
    internal const int MaximumTurns = 200;

    public static async Task<CombatPilotResult> RunAsync(ICombatPilotHost host, string root,
        CancellationToken token)
    {
        Stopwatch total = Stopwatch.StartNew();
        List<CombatPilotSearchEntry> searchLog = [];
        List<CombatPilotDeviationEntry> deviationLog = [];
        List<CombatPilotFailureEntry> failureLog = [];
        int searches = 0;
        int reused = 0;
        int deviations = 0;
        int deploymentFailures = 0;
        int consecutiveFailures = 0;
        int failureTurn = -1;
        string? pauseReason = null;
        bool cancelled = false;
        ReplayedRoute? route = null;
        int index = 0;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                CombatPilotTurn? maybeTurn = await host.WaitForTurnAsync(token);
                if (maybeTurn is not { } turn)
                    break;
                if (turn.Turn > MaximumTurns)
                {
                    pauseReason = $"回合数超过上限 {MaximumTurns}";
                    deploymentFailures++;
                    failureLog.Add(new CombatPilotFailureEntry(turn.Turn, "turn_limit", pauseReason));
                    break;
                }
                if (failureTurn != turn.Turn)
                {
                    failureTurn = turn.Turn;
                    consecutiveFailures = 0;
                }

                if (route == null || CombatRouteDecision.NeedsSearch(route.Actions, index, turn.Turn))
                {
                    string reason = route == null ? "initial" : "turn_start";
                    CombatPilotSearch search = await host.SearchAsync(turn.Turn, reason, token);
                    searches++;
                    searchLog.Add(new CombatPilotSearchEntry(turn.Turn, reason, search.Status,
                        search.ElapsedMs, search.ExpandedNodes, search.RouteActions, search.Victory,
                        search.Error, search.ManagedHeapPeakBytes, search.WorkingSetPeakBytes,
                        search.SoftTimeRisk, search.MemoryBudgetExceeded,
                        search.MemoryExceededAtBytes, search.PoolCapacityDropped,
                        search.SearchMemoryUsedBytes, search.SearchMemoryPeakBytes,
                        search.RouteNodeStorageBytes, search.RouteNodePeakCount)
                    {
                        SelectedMember = search.SelectedMember,
                        SearchStatus = search.SearchStatus,
                        Members = search.Members,
                        MembersRun = search.MembersRun,
                        MembersSkippedByTime = search.MembersSkippedByTime,
                    });
                    if (search.Route == null)
                    {
                        deploymentFailures++;
                        consecutiveFailures++;
                        if (search.FailureReason is { } failureReason)
                        {
                            failureLog.Add(new CombatPilotFailureEntry(turn.Turn, failureReason,
                                search.FailureDetail ?? ""));
                        }
                        // CapacityBounded only reaches this no-route branch. A capacity-limited
                        // Defeat/Incomplete route is a normal route and falls through to execution.
                        if (search.SearchStatus == CombatSearchStatus.CapacityBounded)
                        {
                            pauseReason = $"第 {turn.Turn} 回合搜索超出战斗状态容量";
                            break;
                        }
                        if (CombatFailureLimit.Reached(consecutiveFailures))
                        {
                            pauseReason = $"第 {turn.Turn} 回合连续 {consecutiveFailures} 次搜索/部署失败";
                            break;
                        }
                        continue;
                    }
                    route = search.Route;
                    index = 0;
                    if (route.Count == 0 || route[0].Before.PlayerTurn != turn.Turn)
                    {
                        deploymentFailures++;
                        consecutiveFailures++;
                        failureLog.Add(new CombatPilotFailureEntry(turn.Turn, "route_turn",
                            $"路线首动作回合 {(route.Count == 0 ? 0 : route[0].Before.PlayerTurn)} 与当前 {turn.Turn} 不符。"));
                        route = null;
                        if (CombatFailureLimit.Reached(consecutiveFailures))
                        {
                            pauseReason = $"第 {turn.Turn} 回合连续 {consecutiveFailures} 次搜索/部署失败";
                            break;
                        }
                        continue;
                    }
                    host.OnRouteAccepted(route);
                }
                else if (index > 0)
                {
                    ReplayedAction expected = route[index];
                    CombatPilotBoundary boundary =
                        await host.CheckBoundaryAsync(expected, turn.Turn, index, token);
                    if (!boundary.Same)
                    {
                        deviations++;
                        consecutiveFailures++;
                        deviationLog.Add(new CombatPilotDeviationEntry(turn.Turn, index,
                            boundary.Action, boundary.Diff));
                        route = null;
                        if (CombatFailureLimit.Reached(consecutiveFailures))
                        {
                            pauseReason = $"第 {turn.Turn} 回合连续 {consecutiveFailures} 次预测偏差/部署失败";
                            break;
                        }
                        continue;
                    }
                    reused++;
                }

                bool turnEnded = false;
                bool finished = false;
                host.BeginDeployment(turn.Turn);
                while (index < route.Count && CombatRouteDecision.IsActionInTurn(route[index], turn.Turn))
                {
                    token.ThrowIfCancellationRequested();
                    ReplayedAction action = route[index];
                    CombatPilotDeployment deployment =
                        await host.DeployAsync(action, turn.Turn, token);
                    if (!deployment.Success)
                    {
                        deploymentFailures++;
                        consecutiveFailures++;
                        failureLog.Add(new CombatPilotFailureEntry(turn.Turn,
                            deployment.FailureReason ?? "deploy_failed",
                            deployment.FailureDetail ?? ""));
                        route = null;
                        break;
                    }
                    bool terminal = action.After.Outcome != Outcome.None;
                    if (!terminal && !deployment.SameAfter)
                    {
                        deviations++;
                        consecutiveFailures++;
                        deviationLog.Add(new CombatPilotDeviationEntry(turn.Turn, index,
                            deployment.Action, deployment.Diff));
                        route = null;
                        break;
                    }
                    host.OnActionDeployed(action);
                    if (action.After.PlayerTurn > turn.Turn)
                        turnEnded = true;
                    index++;
                    if (terminal)
                    {
                        finished = true;
                        break;
                    }
                    if (CombatRouteDecision.NeedsSearch(route.Actions, index, turn.Turn))
                        break;
                }
                if (finished)
                    break;
                if (route == null)
                {
                    if (CombatFailureLimit.Reached(consecutiveFailures))
                    {
                        pauseReason = $"第 {turn.Turn} 回合连续 {consecutiveFailures} 次预测偏差/部署失败";
                        break;
                    }
                    continue;
                }
                if (turnEnded)
                    continue;
                deploymentFailures++;
                consecutiveFailures++;
                failureLog.Add(new CombatPilotFailureEntry(turn.Turn, "turn_plan_exhausted",
                    $"第 {turn.Turn} 回合路线用完但没有结束回合动作。"));
                route = null;
                if (CombatFailureLimit.Reached(consecutiveFailures))
                {
                    pauseReason = $"第 {turn.Turn} 回合连续 {consecutiveFailures} 次预测偏差/部署失败";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        total.Stop();
        CombatPilotFinal final = host.Final(cancelled || token.IsCancellationRequested);
        return new CombatPilotResult(root, final.Status, final.Outcome, final.Turns, final.PlayerHp,
            final.PlayerMaxHp, final.PotionsUsed, searches, reused, deviations, deploymentFailures,
            total.ElapsedMilliseconds, pauseReason, [.. searchLog], [.. deviationLog],
            [.. failureLog]);
    }
}

/// <summary>Stable action description shared by the Mod and offline hosts.</summary>
internal static class CombatPilotAction
{
    internal static string Describe(ReplayedAction action)
    {
        string text = action.Action.ToString();
        if (action.Action.Kind == PlayerActionKind.PlayCard
            && action.Action.HandIndex < action.Before.HandCount)
        {
            Card card = action.Before.Cards[action.Action.HandIndex];
            text += $"[{Registry.Card(card.Id).NativeId}+{card.Upgrade}]";
        }
        else if (action.Action.Kind == PlayerActionKind.UsePotion
            && action.Action.PotionSlot < action.Before.PotionSlotCount)
        {
            ushort stored = action.Before.Potions[action.Action.PotionSlot];
            if (stored != 0)
                text += $"[{Registry.Potion((ushort)(stored - 1)).NativeId}]";
        }
        return text;
    }
}
