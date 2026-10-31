// Mod-side implementation of the shared whole-combat host. The decision loop lives in
// Core/Autoplay/CombatPilot.cs; this file keeps only native I/O: waiting for the play phase,
// importing roots, running the in-game search worker, deploying actions and projecting the live
// boundary back to Battle.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Autoplay;
using Sts2Solver.Ai.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver;

/// <summary>Native host for <see cref="CombatPilot"/>.</summary>
internal sealed class CombatPilotHost(
    NGame host,
    Rules rules,
    LiveCombatTracker tracker,
    Func<SearchPolicy, Player, SearchPolicy>? configurePolicy,
    SearchPerformanceTier tier,
    string root,
    CombatState combat,
    Player player) : ICombatPilotHost
{
    private readonly string _root = root;
    private readonly List<string> _potionsUsed = [];
    private CombatState _combat = combat;
    private Player _player = player;
    private ActionDeployer? _deployer;
    private int _settledTurn = -1;
    private int _lastTurn = player.PlayerCombatState?.TurnNumber ?? 0;
    private int _lastHp = player.Creature.CurrentHp;
    private int _lastMaxHp = player.Creature.MaxHp;
    private readonly int _combatStartHp = player.Creature.CurrentHp;
    private readonly int _combatStartMaxHp = player.Creature.MaxHp;

    public async ValueTask<CombatPilotTurn?> WaitForTurnAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CombatState? liveState = CombatManager.Instance.DebugOnlyGetState();
            if (liveState == null || !CombatManager.Instance.IsInProgress)
                return null;
            Player? livePlayer = LocalContext.GetMe(liveState);
            if (livePlayer?.PlayerCombatState is not { } playerState)
            {
                await CombatFrameWaiter.NextFrameAsync(host);
                continue;
            }
            if (liveState.CurrentSide != CombatSide.Player
                || playerState.Phase != PlayerTurnPhase.Play
                || !RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted)
            {
                await CombatFrameWaiter.NextFrameAsync(host);
                continue;
            }
            int turn = playerState.TurnNumber;
            // The old loop checked the turn cap before spending the new-turn settle wait.
            if (turn > CombatPilot.MaximumTurns)
            {
                _lastTurn = turn;
                _lastHp = livePlayer.Creature.CurrentHp;
                _lastMaxHp = livePlayer.Creature.MaxHp;
                return new CombatPilotTurn(turn, _lastHp, _lastMaxHp);
            }
            if (_settledTurn != turn)
            {
                await CombatFrameWaiter.SettleAsync(host);
                _settledTurn = turn;
            }
            liveState = CombatManager.Instance.DebugOnlyGetState();
            if (liveState == null || !CombatManager.Instance.IsInProgress)
                return null;
            livePlayer = LocalContext.GetMe(liveState);
            if (livePlayer?.PlayerCombatState is not { } settledPlayerState)
                continue;
            _combat = liveState;
            _player = livePlayer;
            turn = settledPlayerState.TurnNumber;
            _lastTurn = turn;
            _lastHp = livePlayer.Creature.CurrentHp;
            _lastMaxHp = livePlayer.Creature.MaxHp;
            return new CombatPilotTurn(turn, _lastHp, _lastMaxHp);
        }
    }

    public async ValueTask<CombatPilotSearch> SearchAsync(int turn, string reason,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Battle searchRoot;
        SearchCase searchCase;
        CombatPilotSearch Rejected(string kind, string detail)
        {
            ModLog.Error($"[Sts2Solver] AUTO_IMPORT_FAILED turn={turn} reason={reason} {detail}");
            return new CombatPilotSearch(null, "rejected", 0, 0, 0, false, detail, kind, detail, 0, 0);
        }
        CombatPilotSearch RouteRejected(CombatSearchResult result, BeamResult resultBeam,
            string kind, string detail)
        {
            ModLog.Error($"[Sts2Solver] AUTO_ROUTE_INVALID turn={turn} reason={reason} {detail}");
            return InvalidRoute(result, resultBeam, "invalid", kind, detail);
        }
        try
        {
            tracker.Refresh(_combat);
            searchRoot = tracker.ImportRoot(_combat, _player, rules);
            searchCase = SearchSession.CompileCase(
                _combat, _player, rules, searchRoot, configurePolicy, deterministic: false, tier);
        }
        catch (UnsupportedImportException error)
        { return Rejected("import_failed", $"reason={error.Reason} {error.Detail}"); }
        catch (UnsupportedException error)
        { return Rejected("import_unsupported", error.Message); }
        catch (CapacityException error)
        { return Rejected("import_capacity", $"region={error.Region} {error.Message}"); }
        catch (InvalidDataException error)
        { return Rejected("request_invalid", error.Message); }
        catch (InvalidOperationException error)
        { return Rejected("request_invalid", error.Message); }
        SearchPerformanceProfile profile = SearchPerformanceProfile.For(tier);
        SearchPlan plan = profile.ToSearchPlan(searchCase.Options.SearchMemoryBudgetBytes,
            _combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Boss,
            _combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Elite, _combat.RunState.CurrentActIndex);
        SearchStorage storage = new();
        CombatSearchResult outcome = await CombatSearchCoordinator.RunAsync(
            new(searchCase, plan), storage, RunMember, token);
        if (outcome.Beam is not { } beam)
        {
            string status = outcome.FailureStatus.Length > 0 ? outcome.FailureStatus : "failed";
            string error = outcome.FailureError ?? "搜索没有返回路线";
            string detail = $"search {status}: {error}";
            ModLog.Error(
                $"[Sts2Solver] AUTO_SEARCH_FAILED turn={turn} reason={reason} " +
                $"status={status} error={error} members={outcome.Members.Length}");
            return new CombatPilotSearch(null, status, outcome.ElapsedMs,
                outcome.TotalExpandedNodes, 0, false, error, "search_failed", detail,
                outcome.ManagedHeapPeakBytes, outcome.WorkingSetPeakBytes, outcome.SoftTimeRisk,
                outcome.MemoryBudgetExceeded, outcome.MemoryExceededAtBytes,
                outcome.PoolCapacityDropped, outcome.SearchMemoryUsedBytes,
                outcome.SearchMemoryPeakBytes, outcome.RouteNodeStorageBytes,
                outcome.RouteNodePeakCount)
            {
                SelectedMember = outcome.SelectedMember,
                SearchStatus = outcome.Status,
                Members = outcome.Members,
                MembersRun = outcome.MembersRun,
                MembersSkippedByTime = outcome.MembersSkippedByTime,
            };
        }
        ReplayedRoute route;
        try
        {
            route = ReplayedRoute.Build(searchRoot, rules, beam.BestRoute, beam.BestIsVictory);
        }
        catch (InvalidDataException error)
        { return RouteRejected(outcome, beam, "route_invalid", error.Message); }
        catch (InvalidOperationException error)
        { return RouteRejected(outcome, beam, "route_invalid", error.Message); }
        catch (UnsupportedException error)
        { return RouteRejected(outcome, beam, "route_unsupported", error.Message); }
        catch (CapacityException error)
        { return RouteRejected(outcome, beam, "route_capacity", $"region={error.Region} {error.Message}"); }
        tracker.Observe(searchRoot);
        ModLog.Info(
            $"[Sts2Solver] AUTO_SEARCH turn={turn} reason={reason} ms={outcome.ElapsedMs} " +
            $"expanded={outcome.TotalExpandedNodes} route={beam.BestRoute.Length} " +
            $"victory={beam.BestIsVictory} score={beam.BestScore} members={outcome.Members.Length} " +
            $"selected_member={outcome.SelectedMember} " +
            $"soft_time_risk={outcome.SoftTimeRisk} skipped={outcome.MembersSkippedByTime}");
        return new CombatPilotSearch(route, "ok", outcome.ElapsedMs, outcome.TotalExpandedNodes,
            beam.BestRoute.Length, beam.BestIsVictory, null, null, null,
            outcome.ManagedHeapPeakBytes, outcome.WorkingSetPeakBytes, outcome.SoftTimeRisk,
            outcome.MemoryBudgetExceeded, outcome.MemoryExceededAtBytes,
            outcome.PoolCapacityDropped, outcome.SearchMemoryUsedBytes,
            outcome.SearchMemoryPeakBytes, outcome.RouteNodeStorageBytes,
            outcome.RouteNodePeakCount)
        {
            SelectedMember = outcome.SelectedMember,
            SearchStatus = outcome.Status,
            Members = outcome.Members,
            MembersRun = outcome.MembersRun,
            MembersSkippedByTime = outcome.MembersSkippedByTime,
        };

        async ValueTask<SearchMemberMeasurement> RunMember(SearchCase member, SearchStorage shared, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SearchRunResult result = await SearchSession.RunAsync(
                host, _root, member, deterministic: false, tier, resultDirectory: null,
                cancelAfterMilliseconds: null, shared, ct);
            ModLog.Info($"[Sts2Solver] AUTO_SEARCH_MEMBER turn={turn} reason={reason} " +
                $"width={member.Options.BeamWidth} status={result.Status} " +
                $"ms={result.ElapsedMilliseconds} expanded={result.ExpandedNodes}");
            return new(result.Beam, result.Status, result.Error, result.ElapsedMilliseconds,
                result.ManagedHeapPeakBytes, result.WorkingSetPeakBytes);
        }
    }

    private static CombatPilotSearch InvalidRoute(CombatSearchResult outcome, BeamResult beam,
        string status, string reason, string detail)
        => new(null, status, outcome.ElapsedMs, outcome.TotalExpandedNodes,
            beam.BestRoute.Length, beam.BestIsVictory, detail, reason, detail,
            outcome.ManagedHeapPeakBytes, outcome.WorkingSetPeakBytes, outcome.SoftTimeRisk,
            outcome.MemoryBudgetExceeded, outcome.MemoryExceededAtBytes,
            outcome.PoolCapacityDropped, outcome.SearchMemoryUsedBytes,
            outcome.SearchMemoryPeakBytes, outcome.RouteNodeStorageBytes,
            outcome.RouteNodePeakCount)
        {
            SelectedMember = outcome.SelectedMember,
            SearchStatus = outcome.Status,
            Members = outcome.Members,
            MembersRun = outcome.MembersRun,
            MembersSkippedByTime = outcome.MembersSkippedByTime,
        };

    public ValueTask<CombatPilotBoundary> CheckBoundaryAsync(ReplayedAction expected, int turn,
        int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Battle live = tracker.ImportForComparison(_combat, _player, rules, expected.Before);
        if (BattleReconciler.Same(expected.Before, live))
        {
            ModLog.Info($"[Sts2Solver] TURN_REUSE turn={turn} index={index}");
            return ValueTask.FromResult(new CombatPilotBoundary(true, CombatPilotAction.Describe(expected), []));
        }
        string[] diff = BattleReconciler.Diff(expected.Before, live);
        ModLog.Error(
            $"[Sts2Solver] TURN_DEVIATION turn={turn} index={index} " +
            $"action={CombatPilotAction.Describe(expected)} diff={string.Join(" | ", diff)}");
        return ValueTask.FromResult(new CombatPilotBoundary(false, CombatPilotAction.Describe(expected), diff));
    }

    public void BeginDeployment(int turn)
    {
        _deployer = new ActionDeployer(host, _combat, _player, rules, tracker);
    }

    public async ValueTask<CombatPilotDeployment> DeployAsync(ReplayedAction action, int turn,
        CancellationToken token)
    {
        ActionDeployer deployer = _deployer
            ?? throw new InvalidOperationException("行动部署前没有建立部署器。");
        Battle actual;
        try
        {
            actual = await deployer.ExecuteAsync(action, token);
        }
        catch (DeploymentFailureException error)
        {
            ModLog.Error(
                $"[Sts2Solver] DEPLOY_FAILURE turn={turn} index={action.Index} " +
                $"action={CombatPilotAction.Describe(action)} reason={error.Reason} detail={error.Message}");
            return new CombatPilotDeployment(false, false, error.Reason, error.Message,
                CombatPilotAction.Describe(action), []);
        }
        bool terminal = action.After.Outcome != Outcome.None;
        if (!terminal && !BattleReconciler.Same(action.After, actual))
        {
            string[] diff = BattleReconciler.Diff(action.After, actual);
            ModLog.Error(
                $"[Sts2Solver] ACTION_DEVIATION turn={turn} index={action.Index} " +
                $"action={CombatPilotAction.Describe(action)} diff={string.Join(" | ", diff)}");
            return new CombatPilotDeployment(true, false, null, null, CombatPilotAction.Describe(action), diff);
        }
        return new CombatPilotDeployment(true, true, null, null, CombatPilotAction.Describe(action), []);
    }

    public void OnActionDeployed(ReplayedAction action)
    {
        tracker.Observe(action.After);
        if (action.Action.Kind == PlayerActionKind.UsePotion)
        {
            ushort stored = action.Before.Potions[action.Action.PotionSlot];
            if (stored != 0)
                _potionsUsed.Add(Registry.Potion((ushort)(stored - 1)).NativeId);
        }
        ModLog.Info(
            $"[Sts2Solver] DEPLOY_OK turn={action.Before.PlayerTurn} index={action.Index} " +
            $"action={CombatPilotAction.Describe(action)} outcome={action.After.Outcome} " +
            $"next_turn={action.After.PlayerTurn}");
        if (action.After.Outcome != Outcome.None)
        {
            ModLog.Info(
                $"[Sts2Solver] DEPLOY_TERMINAL_CONFIRMED turn={action.Before.PlayerTurn} " +
                $"index={action.Index} action={CombatPilotAction.Describe(action)} outcome={action.After.Outcome}");
        }
    }

    public void OnRouteAccepted(ReplayedRoute route)
    {
        tracker.Observe(route[0].Before);
    }

    public CombatPilotFinal Final(bool cancelled)
    {
        bool inProgress = CombatManager.Instance.IsInProgress;
        CombatState? final = CombatManager.Instance.DebugOnlyGetState();
        Player? finalPlayer = final == null ? null : LocalContext.GetMe(final);
        if (finalPlayer != null)
        {
            _lastHp = finalPlayer.Creature.CurrentHp;
            _lastMaxHp = finalPlayer.Creature.MaxHp;
            _lastTurn = finalPlayer.PlayerCombatState?.TurnNumber ?? _lastTurn;
        }
        string outcome;
        string status;
        if (!inProgress)
        {
            bool playerAlive = finalPlayer?.Creature.IsAlive ?? false;
            bool enemiesAlive = final?.Enemies.Any(static enemy => enemy.IsAlive) ?? false;
            outcome = playerAlive && !enemiesAlive ? "victory" : playerAlive ? "ended" : "defeat";
            status = outcome == "ended" ? "failed" : outcome;
        }
        else if (cancelled)
        {
            outcome = "in_progress";
            status = "cancelled";
        }
        else
        {
            outcome = "in_progress";
            status = "paused";
        }
        return new CombatPilotFinal(outcome, status, _lastTurn, _lastHp, _lastMaxHp,
            [.. _potionsUsed]);
    }


}
