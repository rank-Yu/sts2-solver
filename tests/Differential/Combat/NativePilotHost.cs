// Low-tier CombatPilot host for the in-process boundary comparison. Search uses the same request
// compiler and BeamSolver as the offline whole-combat host; deployment goes through
// NativeActionExecutor so each route action is checked against the original game.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.TestHost;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Differential;

internal sealed class NativePilotHost(
    MainLoopContext loop,
    CombatState combat,
    Player player,
    Rules rules,
    LiveCombatTracker tracker,
    Battle opening,
    SearchPolicy policy,
    SearchPerformanceProfile profile,
    long memoryBudgetBytes,
    Func<string, IDisposable>? measure = null,
    Action<Battle, Battle>? onDifference = null,
    Action<CombatSearchRequest>? onSearchRequest = null) : ICombatPilotHost
{
    private readonly NativeActionExecutor _executor = new(loop, combat, player, rules, tracker);
    private readonly List<string> _potionsUsed = [];
    private Battle _current = opening;

    internal Battle Current => _current;

    internal CombatActionCoverage Coverage { get; } = new();

    public ValueTask<CombatPilotTurn?> WaitForTurnAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_current.Outcome != Outcome.None)
            return ValueTask.FromResult<CombatPilotTurn?>(null);
        return ValueTask.FromResult<CombatPilotTurn?>(new CombatPilotTurn(
            _current.PlayerTurn, _current.Player.CurrentHp, _current.Player.MaxHp));
    }

    public ValueTask<CombatPilotSearch> SearchAsync(int turn, string reason,
        CancellationToken token)
    {
        using IDisposable? timing = measure?.Invoke("combat_search");
        token.ThrowIfCancellationRequested();
        CombatState live = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new OfflineDeploymentException("live_missing", "搜索时找不到原生战斗。");
        Player livePlayer = LocalContext.GetMe(live)
            ?? throw new OfflineDeploymentException("live_player_missing", "搜索时找不到本地玩家。");
        try
        {
            using (measure?.Invoke("import_check"))
            {
            Battle liveRoot = tracker.ImportForComparison(live, livePlayer, rules, _current);
            if (!CombatRouteDecision.BoundaryMatches(_current, liveRoot))
            {
                string[] rootDiff = CombatRouteDecision.BoundaryDiff(_current, liveRoot);
                return ValueTask.FromResult(new CombatPilotSearch(null, "diff", 0, 0, 0, false,
                    string.Join(" | ", rootDiff.Take(8)), "root_deviation",
                    string.Join('\n', rootDiff), 0, 0));
            }
            }
            SearchCase searchCase = SearchRequestCompiler.CompileRequest(
                live, livePlayer, rules, _current, policy, EvaluationRootFacts.Default, profile,
                deterministic: true, memoryBudgetBytes);
            SearchStorage storage = new();
            SearchPlan plan = profile.ToSearchPlan(memoryBudgetBytes, live.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Boss,
            live.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Elite, live.RunState.CurrentActIndex);
            CombatSearchRequest request = new(searchCase, plan);
            onSearchRequest?.Invoke(request);
            ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage shared, CancellationToken ct)
            {
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                BeamResult result = new BeamSolver(member.Rules, member.Options).Solve(member.Battles, shared, ct);
                return ValueTask.FromResult(new SearchMemberMeasurement(result, "ok", null,
                    watch.ElapsedMilliseconds));
            }
            CombatSearchResult outcome = CombatSearchCoordinator.RunAsync(request, storage, RunMember, token)
                .AsTask().GetAwaiter().GetResult();
            BeamResult beam = outcome.Beam ?? throw new InvalidDataException(
                $"search {outcome.FailureStatus}: {outcome.FailureError}");
            ReplayedRoute route = ReplayedRoute.Build(_current, rules, beam.BestRoute,
                beam.BestIsVictory);
            return ValueTask.FromResult(new CombatPilotSearch(route, "ok", outcome.ElapsedMs,
                outcome.TotalExpandedNodes, beam.BestRoute.Length,
                beam.BestIsVictory, null, null, null, 0, 0)
            {
                Members = outcome.Members, MembersRun = outcome.MembersRun,
                MembersSkippedByTime = outcome.MembersSkippedByTime,
                SelectedMember = outcome.SelectedMember, SearchStatus = outcome.Status,
            });
        }
        catch (UnsupportedImportException error)
        {
            string detail = $"reason={error.Reason} {error.Detail}";
            return ValueTask.FromResult(Failed("rejected", detail, "import_failed", detail));
        }
        catch (Sts2Solver.Simulation.Combat.UnsupportedException error)
        {
            return ValueTask.FromResult(Failed("rejected", error.Message, "import_unsupported",
                error.Message));
        }
        catch (CapacityException error)
        {
            string detail = $"region={error.Region} {error.Message}";
            return ValueTask.FromResult(Failed("rejected", detail, "import_capacity", detail));
        }
        catch (InvalidDataException error)
        {
            return ValueTask.FromResult(Failed("rejected", error.Message, "request_invalid",
                error.Message));
        }
    }

    public ValueTask<CombatPilotBoundary> CheckBoundaryAsync(ReplayedAction expected, int turn,
        int index, CancellationToken token)
    {
        using IDisposable? timing = measure?.Invoke("import_check");
        token.ThrowIfCancellationRequested();
        CombatState live = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new OfflineDeploymentException("live_missing", "边界检查时找不到原生战斗。");
        Player livePlayer = LocalContext.GetMe(live)
            ?? throw new OfflineDeploymentException("live_player_missing", "边界检查时找不到本地玩家。");
        Battle liveRoot = tracker.ImportForComparison(live, livePlayer, rules, expected.Before);
        bool same = CombatRouteDecision.BoundaryMatches(expected.Before, liveRoot);
        return ValueTask.FromResult(new CombatPilotBoundary(same,
            CombatPilotAction.Describe(expected),
            same ? [] : CombatRouteDecision.BoundaryDiff(expected.Before, liveRoot)));
    }

    public void BeginDeployment(int turn)
    {
    }

    public ValueTask<CombatPilotDeployment> DeployAsync(ReplayedAction action, int turn,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Coverage.CountAction(action.Action.Kind);
        try
        {
            Battle live;
            using (measure?.Invoke("native_combat"))
                live = _executor.Execute(action);
            using IDisposable? timing = measure?.Invoke("import_check");
            if (action.After.Outcome != Outcome.None)
            {
                string? terminalDiff = TerminalBattleCapture.Compare(action.After);
                if (terminalDiff != null)
                {
                    return ValueTask.FromResult(new CombatPilotDeployment(false, false,
                        "terminal_diff", terminalDiff,
                        CombatPilotAction.Describe(action), terminalDiff.Split('\n')));
                }
            }
            else if (!CombatRouteDecision.ActionMatches(action, live))
            {
                onDifference?.Invoke(action.After, live);
                string[] diff = CombatRouteDecision.BoundaryDiff(action.After, live);
                return ValueTask.FromResult(new CombatPilotDeployment(false, false,
                    "boundary_diff", string.Join('\n', diff),
                    CombatPilotAction.Describe(action), diff));
            }
            _current = action.After;
            return ValueTask.FromResult(new CombatPilotDeployment(true, true, null, null,
                CombatPilotAction.Describe(action), []));
        }
        catch (OfflineDeploymentException error)
        {
            return ValueTask.FromResult(new CombatPilotDeployment(false, false, error.Reason,
                error.Message, CombatPilotAction.Describe(action), []));
        }
    }

    public void OnActionDeployed(ReplayedAction action)
    {
        _current = action.After;
        if (action.Action.Kind == PlayerActionKind.UsePotion)
        {
            ushort stored = action.Before.Potions[action.Action.PotionSlot];
            if (stored != 0)
                _potionsUsed.Add(Registry.Potion((ushort)(stored - 1)).NativeId);
        }
    }

    public void OnRouteAccepted(ReplayedRoute route)
    {
    }

    public CombatPilotFinal Final(bool cancelled)
    {
        string outcome;
        string status;
        if (_current.Outcome == Outcome.Victory)
        {
            outcome = "victory";
            status = "victory";
        }
        else if (_current.Outcome == Outcome.Defeat)
        {
            outcome = "defeat";
            status = "defeat";
        }
        else if (_current.Outcome == Outcome.EventDefeat)
        {
            outcome = _current.Player.IsDead ? "defeat" : "ended";
            status = outcome == "ended" ? "failed" : "defeat";
        }
        else
        {
            outcome = "in_progress";
            status = cancelled ? "cancelled" : "paused";
        }
        return new CombatPilotFinal(outcome, status, _current.PlayerTurn,
            _current.Player.CurrentHp, _current.Player.MaxHp, [.. _potionsUsed]);
    }

    private static CombatPilotSearch Failed(string status, string error, string reason,
        string detail)
        => new(null, status, 0, 0, 0, false, error, reason, detail, 0, 0);

}
