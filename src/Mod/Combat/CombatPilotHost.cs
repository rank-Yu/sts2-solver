// Mod-side implementation of the shared whole-combat host. The decision loop lives in
// Core/Autoplay/CombatPilot.cs; this file keeps only native I/O: waiting for the play phase,
// importing roots, running the in-game search worker, deploying actions and projecting the live
// boundary back to Battle.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Autoplay;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
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
    SearchPerformanceTier? tierOverride,
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
    private ReplayedRoute? _route;
    private CombatDisplayNames? _names;
    private int _completedActions;
    private int _currentActionIndex;
    private bool _routeReused;
    private int _searches;
    private int _reusedTurns;
    private int _deviations;
    private int _deploymentFailures;
    private List<SearchMemberRunUi> _memberRuns = [];
    private long _searchStartedAt;
    private int _searchThreads = 1;

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
                PublishWaiting();
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
            if (_route != null)
                _currentActionIndex = Math.Min(_completedActions, _route.Count);
            PublishWaiting();
            return new CombatPilotTurn(turn, _lastHp, _lastMaxHp);
        }
    }

    public async ValueTask<CombatPilotSearch> SearchAsync(int turn, string reason,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SearchPerformanceTier tier = AutoCombatSession.ResolveSearchTier(tierOverride);
        SearchPerformanceProfile profile = SearchPerformanceProfile.For(tier);
        _searchThreads = SearchThreadCount.Value;
        _searches++;
        _route = null;
        _completedActions = 0;
        _currentActionIndex = 0;
        _routeReused = false;
        _memberRuns = [];
        _searchStartedAt = Environment.TickCount64;
        PublishSearchStarted(turn, reason, tier, profile);
        Battle searchRoot;
        SearchCase searchCase;
        CombatPilotSearch Rejected(string kind, string detail)
        {
            ModLog.Error($"[Sts2Solver] AUTO_IMPORT_FAILED turn={turn} reason={reason} {detail}");
            _deploymentFailures++;
            PublishSearchFailed(kind, turn, reason, tier, profile);
            return new CombatPilotSearch(null, "rejected", 0, 0, 0, false, detail, kind, detail, 0, 0);
        }
        CombatPilotSearch RouteRejected(CombatSearchResult result, BeamResult resultBeam,
            string kind, string detail)
        {
            ModLog.Error($"[Sts2Solver] AUTO_ROUTE_INVALID turn={turn} reason={reason} {detail}");
            _deploymentFailures++;
            PublishSearchFinished(result, kind, turn, reason, tier, profile);
            return InvalidRoute(result, resultBeam, "invalid", kind, detail);
        }
        try
        {
            tracker.Refresh(_combat);
            searchRoot = tracker.ImportRoot(_combat, _player, rules);
            searchCase = SearchSession.CompileCase(
                _combat, _player, rules, searchRoot, configurePolicy, deterministic: false, tier);
            searchCase = searchCase with
            {
                Options = searchCase.Options with { SearchThreads = _searchThreads },
            };
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
        SearchPlan plan = profile.ToSearchPlan(searchCase.Options.SearchMemoryBudgetBytes,
            _combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Boss,
            _combat.Encounter?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Elite, _combat.RunState.CurrentActIndex);
        SearchStorage storage = new();
        CombatSearchResult outcome;
        try
        {
            outcome = await CombatSearchCoordinator.RunAsync(
                new(searchCase, plan), storage, RunMember, token);
        }
        catch (OperationCanceledException)
        {
            PublishSearchCancelled(turn, reason, tier, profile);
            throw;
        }
        if (outcome.Beam is not { } beam)
        {
            string status = outcome.FailureStatus.Length > 0 ? outcome.FailureStatus : "failed";
            string error = outcome.FailureError ?? "搜索没有返回路线";
            string detail = $"search {status}: {error}";
            ModLog.Error(
                $"[Sts2Solver] AUTO_SEARCH_FAILED turn={turn} reason={reason} " +
                $"status={status} error={error} members={outcome.Members.Length}");
            _deploymentFailures++;
            PublishSearchFinished(outcome, "search_failed", turn, reason, tier, profile);
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
        _names = CombatDisplayNames.Capture(_combat);
        PublishSearchFinished(outcome, null, turn, reason, tier, profile);
        ModLog.Info(
            $"[Sts2Solver] AUTO_SEARCH turn={turn} reason={reason} ms={outcome.ElapsedMs} " +
            $"expanded={outcome.TotalExpandedNodes} route={beam.BestRoute.Length} " +
            $"victory={beam.BestIsVictory} score={beam.BestScore} members={outcome.Members.Length} " +
            $"selected_member={outcome.SelectedMember} " +
            $"threads={_searchThreads} overflow_parents={outcome.WorkspaceOverflowParents} " +
            $"workspace_growable_peak_mb={outcome.SearchWorkspaceGrowablePeakBytes / (1024d * 1024d):0.0} " +
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
            PublishMemberStarted(member.Options.BeamWidth,
                checked((long)member.Options.BeamWidth * profile.MillisecondsPerWidth));
            SearchRunResult result = await SearchSession.RunAsync(
                host, _root, member, deterministic: false, tier, resultDirectory: null,
                cancelAfterMilliseconds: null, shared, ct);
            _memberRuns.Add(new SearchMemberRunUi
            {
                Index = _memberRuns.Count,
                Status = result.Status,
                ErrorKind = result.ErrorKind.Length == 0 ? null : result.ErrorKind,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
                ExpandedNodes = result.ExpandedNodes,
                Transitions = result.Transitions,
                ManagedHeapPeakBytes = result.ManagedHeapPeakBytes,
                WorkingSetPeakBytes = result.WorkingSetPeakBytes,
                AllocatedBytes = result.AllocatedBytes,
                PacerYields = result.PacerYields,
                PacerFrameRecoveryWaits = result.PacerFrameRecoveryWaits,
                PacerFrameRecoveryWaitMilliseconds = result.PacerFrameRecoveryWaitMilliseconds,
            });
            PublishMemberFinished();
            ModLog.Info($"[Sts2Solver] AUTO_SEARCH_MEMBER turn={turn} reason={reason} " +
                $"width={member.Options.BeamWidth} status={result.Status} " +
                $"ms={result.ElapsedMilliseconds} expanded={result.ExpandedNodes} " +
                $"yields={result.PacerYields} frame_waits={result.PacerFrameRecoveryWaits}");
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
        _currentActionIndex = index;
        _routeReused = true;
        if (BattleReconciler.Same(expected.Before, live))
        {
            _reusedTurns++;
            ModLog.Info($"[Sts2Solver] TURN_REUSE turn={turn} index={index}");
            PublishRoute(CombatUiPhase.WaitingTurn);
            return ValueTask.FromResult(new CombatPilotBoundary(true, CombatPilotAction.Describe(expected), []));
        }
        string[] diff = BattleReconciler.Diff(expected.Before, live);
        ModLog.Error(
            $"[Sts2Solver] TURN_DEVIATION turn={turn} index={index} " +
            $"action={CombatPilotAction.Describe(expected)} diff={string.Join(" | ", diff)}");
        _deviations++;
        _route = null;
        PublishError("boundary_deviation", string.Join(" | ", diff), CombatUiPhase.WaitingTurn);
        return ValueTask.FromResult(new CombatPilotBoundary(false, CombatPilotAction.Describe(expected), diff));
    }

    public void BeginDeployment(int turn)
    {
        _lastTurn = turn;
        _currentActionIndex = Math.Min(_completedActions, _route?.Count ?? _completedActions);
        _deployer = new ActionDeployer(host, _combat, _player, rules, tracker);
        PublishRoute(CombatUiPhase.Deploying);
    }

    public async ValueTask<CombatPilotDeployment> DeployAsync(ReplayedAction action, int turn,
        CancellationToken token)
    {
        ActionDeployer deployer = _deployer
            ?? throw new InvalidOperationException("行动部署前没有建立部署器。");
        _currentActionIndex = action.Index;
        PublishRoute(CombatUiPhase.Deploying);
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
            _deploymentFailures++;
            _route = null;
            PublishError("deploy_failed", error.Reason, CombatUiPhase.WaitingTurn);
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
            _deviations++;
            _completedActions = action.Index + 1;
            _route = null;
            PublishError("action_deviation", string.Join(" | ", diff), CombatUiPhase.WaitingTurn);
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
        _completedActions = action.Index + 1;
        _currentActionIndex = action.Index;
        _lastTurn = action.After.PlayerTurn;
        _lastHp = action.After.Player.CurrentHp;
        _lastMaxHp = action.After.Player.MaxHp;
        bool turnEnded = action.Action.Kind == PlayerActionKind.EndTurn
            || action.After.PlayerTurn > action.Before.PlayerTurn;
        if (turnEnded && _route != null)
            _currentActionIndex = Math.Min(_completedActions, _route.Count);
        PublishRoute(turnEnded ? CombatUiPhase.WaitingTurn : CombatUiPhase.Deploying);
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
        _route = route;
        _completedActions = 0;
        _currentActionIndex = 0;
        _routeReused = false;
        _lastTurn = route[0].Before.PlayerTurn;
        _lastHp = route[0].Before.Player.CurrentHp;
        _lastMaxHp = route[0].Before.Player.MaxHp;
        _names ??= CombatDisplayNames.Capture(_combat);
        PublishRoute(CombatUiPhase.WaitingTurn);
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
        CombatUiPhase phase = status switch
        {
            "victory" or "defeat" => CombatUiPhase.Completed,
            "cancelled" => CombatUiPhase.Cancelled,
            "paused" => CombatUiPhase.Paused,
            _ => CombatUiPhase.Failed,
        };
        if (CombatUiStore.Initialized)
        {
            CombatUiStore.Update(snapshot => snapshot with
            {
                Phase = phase,
                Turn = _lastTurn,
                PlayerHp = _lastHp,
                PlayerMaxHp = _lastMaxHp,
                EndReason = status,
                Searches = _searches,
                ReusedTurns = _reusedTurns,
                Deviations = _deviations,
                DeploymentFailures = _deploymentFailures,
                Route = BuildRoute(),
            });
        }
        return new CombatPilotFinal(outcome, status, _lastTurn, _lastHp, _lastMaxHp,
            [.. _potionsUsed]);
    }

    /// <summary>Live session counters; the terminal publish replaces them with the pilot's own totals.</summary>
    private CombatUiSnapshot WithCounters(CombatUiSnapshot snapshot) => snapshot with
    {
        Searches = _searches,
        ReusedTurns = _reusedTurns,
        Deviations = _deviations,
        DeploymentFailures = _deploymentFailures,
    };

    private void PublishMemberStarted(int width, long budgetMilliseconds)
    {
        if (!CombatUiStore.Initialized)
            return;
        int run = _memberRuns.Count;
        long now = Environment.TickCount64;
        CombatUiStore.Update(snapshot => snapshot.Search is { Phase: SearchUiPhase.Running } search
            ? snapshot with
            {
                Search = search with
                {
                    ActiveMemberRun = run,
                    ActiveMemberWidth = width,
                    ActiveMemberBudgetMilliseconds = budgetMilliseconds,
                    ActiveMemberStartedAtMilliseconds = now,
                },
            }
            : snapshot);
    }

    private void PublishMemberFinished()
    {
        if (!CombatUiStore.Initialized)
            return;
        SearchMemberRunUi[] runs = [.. _memberRuns];
        long now = Environment.TickCount64;
        CombatUiStore.Update(snapshot => snapshot.Search is { Phase: SearchUiPhase.Running } search
            ? snapshot with
            {
                Search = search with
                {
                    ActiveMemberRun = -1,
                    MemberRuns = runs,
                    ElapsedMilliseconds = Math.Max(0, now - search.StartedAtMilliseconds),
                    ExpandedNodes = runs.Sum(static item => (long)item.ExpandedNodes),
                    Transitions = runs.Sum(static item => (long)item.Transitions),
                },
            }
            : snapshot);
    }

    private void PublishWaiting()
    {
        if (!CombatUiStore.Initialized)
            return;
        RouteUiSnapshot? route = BuildRoute();
        CombatUiStore.Update(snapshot => WithCounters(snapshot) with
        {
            Phase = CombatUiPhase.WaitingTurn,
            Turn = _lastTurn,
            PlayerHp = _lastHp,
            PlayerMaxHp = _lastMaxHp,
            Route = route,
            ErrorCode = null,
            ErrorDetail = null,
        });
    }

    private void PublishSearchStarted(int turn, string reason, SearchPerformanceTier tier,
        SearchPerformanceProfile profile)
    {
        if (!CombatUiStore.Initialized)
            return;
        SearchUiSnapshot search = CombatUiProjection.SearchStarted(turn, reason, tier, profile,
            _searchThreads, _searchStartedAt);
        CombatUiStore.Update(snapshot => WithCounters(snapshot) with
        {
            Phase = CombatUiPhase.Searching,
            Turn = _lastTurn,
            PlayerHp = _lastHp,
            PlayerMaxHp = _lastMaxHp,
            Search = search,
            Route = null,
            ErrorCode = null,
            ErrorDetail = null,
        });
    }

    private void PublishSearchFailed(string code, int turn, string reason, SearchPerformanceTier tier,
        SearchPerformanceProfile profile)
    {
        if (!CombatUiStore.Initialized)
            return;
        SearchUiSnapshot search = CombatUiProjection.SearchFailed(code, turn, reason, tier, profile,
            _searchThreads, _searchStartedAt);
        CombatUiStore.Update(snapshot => WithCounters(snapshot) with
        {
            Phase = CombatUiPhase.WaitingTurn,
            Search = search,
            ErrorCode = code,
            ErrorDetail = null,
        });
    }

    private void PublishSearchCancelled(int turn, string reason, SearchPerformanceTier tier,
        SearchPerformanceProfile profile)
    {
        if (!CombatUiStore.Initialized)
            return;
        SearchUiSnapshot search = CombatUiProjection.SearchCancelled(turn, reason, tier, profile,
            _searchThreads, _searchStartedAt, [.. _memberRuns]);
        CombatUiStore.Update(snapshot => WithCounters(snapshot) with
        {
            Phase = CombatUiPhase.WaitingTurn,
            Search = search,
            ErrorCode = "cancelled",
            ErrorDetail = null,
        });
    }

    private void PublishSearchFinished(CombatSearchResult outcome, string? errorCode, int turn,
        string reason, SearchPerformanceTier tier, SearchPerformanceProfile profile)
    {
        if (!CombatUiStore.Initialized)
            return;
        SearchUiSnapshot search = CombatUiProjection.SearchFinished(outcome, [.. _memberRuns],
            turn, reason, tier, profile, errorCode, _searchStartedAt);
        CombatUiStore.Update(snapshot => WithCounters(snapshot) with
        {
            Phase = CombatUiPhase.WaitingTurn,
            Search = search,
            ErrorCode = errorCode,
            ErrorDetail = null,
        });
    }

    private void PublishRoute(CombatUiPhase phase)
    {
        if (!CombatUiStore.Initialized)
            return;
        CombatUiStore.Update(snapshot => WithCounters(snapshot) with
        {
            Phase = phase,
            Turn = _lastTurn,
            PlayerHp = _lastHp,
            PlayerMaxHp = _lastMaxHp,
            Route = BuildRoute(),
            ErrorCode = null,
            ErrorDetail = null,
        });
    }

    private void PublishError(string code, string? detail, CombatUiPhase phase)
    {
        if (!CombatUiStore.Initialized)
            return;
        CombatUiStore.Update(snapshot => WithCounters(snapshot) with
        {
            Phase = phase,
            Turn = _lastTurn,
            PlayerHp = _lastHp,
            PlayerMaxHp = _lastMaxHp,
            Route = BuildRoute(),
            ErrorCode = code,
            ErrorDetail = detail,
        });
    }

    private RouteUiSnapshot? BuildRoute()
    {
        if (_route == null || _names == null)
            return null;
        return CombatUiProjection.Route(_route, _names, _lastTurn, _currentActionIndex,
            _completedActions, _routeReused);
    }
}
