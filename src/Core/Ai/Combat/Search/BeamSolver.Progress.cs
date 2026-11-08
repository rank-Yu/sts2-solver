// Progress publication and route adoption. The preview semantics follow CombatSolver
// src/Search/CombatBeamSolver.Phases.cs (RefreshCurrentTurnPreview and PublishRoutePreview) and
// src/Search/CombatSearchCoordinator.cs at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and
// contributors), rewritten over Sts2Solver's packed Battle and route-node store.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>Carries a materialized adoption out of the search loops.</summary>
    private sealed class SearchAdoptionException(BeamResult result) : Exception
    {
        internal BeamResult Result { get; } = result;
    }

    private IReadOnlyList<Battle> _progressRoots = Array.Empty<Battle>();
    private long _lastProgressAt = long.MinValue / 2;
    private int _progressCandidateVersion;
    private BeamResult? _adoptedResult;

    /// <summary>Fixes the roots a published candidate is replayed from; no subscriber means no state.</summary>
    private void BeginProgress(IReadOnlyList<Battle> roots)
    {
        _progressRoots = _options.Progress is null ? Array.Empty<Battle>() : roots;
        _lastProgressAt = long.MinValue / 2;
        _progressCandidateVersion = _options.Progress?.LatestCandidateVersion ?? 0;
        _adoptedResult = null;
    }

    /// <summary>
    /// The search thread's progress point, called between action layers and at turn boundaries.
    /// It first consumes a pending adoption/stop request, then publishes at most once per throttle
    /// interval. Workers never reach this method.
    /// </summary>
    private void ProgressCheckpoint(Pool? retained, bool force)
    {
        if (_options.Progress is null)
            return;
        PollProgressRequest();
        SearchProgressChannel channel = _options.Progress;
        long now = Environment.TickCount64;
        if (!force && now - _lastProgressAt < channel.ThrottleMilliseconds)
            return;
        _lastProgressAt = now;

        SearchRouteCandidate? built = retained is null ? null : BuildRouteCandidate(retained);
        if (built is not null)
            channel.AddCandidate(built);
        SearchRoutePreview? route = built is null ? null : new SearchRoutePreview(
            built.CandidateVersion,
            built.StartTurn,
            built.ThroughTurn,
            built.RootIndex,
            built.Actions,
            built.Turns,
            built.Victory,
            built.OnlyDeathRoutes);
        SearchCurrentTurnPreview? current = built is null ? null : CurrentTurnPreview(built);
        channel.Publish(new SearchProgressSnapshot(
            channel.MemberIndex,
            channel.Member,
            _expandedTotal,
            _solveWatch?.ElapsedMilliseconds ?? 0,
            route,
            current,
            channel.BestComplete,
            built?.CandidateVersion ?? _progressCandidateVersion)
        {
            PlannedBudgetMilliseconds = channel.PlannedBudgetMilliseconds,
            RequestExpandedNodes = channel.CompletedExpandedNodes + _expandedTotal,
            SearchMemoryUsedBytes = _storage.Memory.UsedBytes,
            SearchMemoryPeakBytes = _storage.Memory.PeakBytes,
        });
    }

    /// <summary>
    /// Consumes at most one pending adoption/stop request. Called before each expansion layer so a
    /// request posted while the previous layer committed is honored before more nodes are explored.
    /// </summary>
    private void PollProgressRequest()
    {
        if (_options.Progress is not { } channel)
            return;
        if (channel.TryTakeRequest() is not { } request)
            return;
        if (request.Stop)
        {
            SearchRouteCandidate? latest = channel.LatestCandidate;
            throw new SearchAdoptionException(latest is null
                ? StopWithoutCandidateResult()
                : MaterializeAdoption(latest, SearchAdoptionKind.Stopped));
        }
        SearchRouteCandidate? candidate = channel.FindCandidate(request.CandidateVersion);
        if (candidate is null)
        {
            throw new InvalidDataException(
                $"采用候选版本 {request.CandidateVersion} 已过期"
                + $"（最新版本 {channel.LatestCandidateVersion}）。");
        }
        throw new SearchAdoptionException(
            MaterializeAdoption(candidate, SearchAdoptionKind.Requested));
    }

    /// <summary>
    /// The best final-ordering candidate among the retained nodes, matching
    /// <c>PublishRoutePreview</c>'s RankFinal + FinalOrdering.Select pass. Root seats carry no action
    /// and are not viable, exactly like the existing <c>ActionCount &gt; 0</c> filter.
    /// </summary>
    private SearchRouteCandidate? BuildRouteCandidate(Pool retained)
    {
        int bestSlot = -1;
        int bestNode = -1;
        RouteKey bestKey = default;
        bool onlyDeathRoutes = true;
        for (int slot = 0; slot < retained.Count; slot++)
        {
            int node = retained.Node(slot);
            int depth = _nodes.View(node).Depth;
            if (depth <= 0)
                continue;
            ref readonly Battle state = ref retained.State(slot);
            ScoreFeatures features = ProgressFeatures(in state, depth);
            if (!features.PlayerDead && features.ProjectedPlayerHp > 0)
                onlyDeathRoutes = false;
            RouteKey key = Key(in state, features, depth);
            int comparison = bestSlot < 0 ? -1 : CompareRoutes(key, bestKey);
            if (comparison < 0 || comparison == 0 && node < bestNode)
            {
                bestSlot = slot;
                bestNode = node;
                bestKey = key;
            }
        }
        if (bestSlot < 0)
            return null;

        PlayerAction[] actions = Route(bestNode);
        int rootIndex = RootIndex(bestNode);
        Battle finalState = retained.State(bestSlot);
        ProgressReplay replay = ReplayProgress(actions, rootIndex);
        if (!Fingerprint.Equal(replay.FinalState, finalState))
            throw new InvalidDataException("推演候选从根回放后的状态与保留节点不一致。");
        int version = ++_progressCandidateVersion;
        return new SearchRouteCandidate(version, _initialPlayerTurn, replay.ThroughTurn, rootIndex,
            actions, finalState, replay.Turns, bestKey.Score, bestKey.Won, onlyDeathRoutes);
    }

    /// <summary>
    /// The retained node's feature projection. The verification hook is disabled for this read so
    /// progress publication cannot change <see cref="Scorer.ThreatProjectionChecks"/> or the result.
    /// </summary>
    private ScoreFeatures ProgressFeatures(in Battle state, int actionCount)
    {
        bool verify = _scorer.VerifyThreatProjection;
        _scorer.VerifyThreatProjection = false;
        try
        {
            return _scorer.Features(in state, actionCount, includeRetentionOnly: false);
        }
        finally
        {
            _scorer.VerifyThreatProjection = verify;
        }
    }

    private static SearchCurrentTurnPreview? CurrentTurnPreview(SearchRouteCandidate candidate)
    {
        foreach (SearchProgressTurn turn in candidate.Turns)
        {
            if (turn.Turn != candidate.StartTurn)
                continue;
            return new SearchCurrentTurnPreview(candidate.CandidateVersion, turn.Turn,
                turn.Actions, turn.HpLost, turn.HpRecovered, turn.EnemyHpLost, turn.EnergyLeft,
                turn.CombatEnded);
        }
        return null;
    }

    /// <summary>
    /// Replays a candidate from its request root and records the per-turn values the existing
    /// outcome annotation stores. The replay is also the adoption validity check.
    /// </summary>
    private ProgressReplay ReplayProgress(PlayerAction[] actions, int rootIndex)
    {
        Battle root = _progressRoots[rootIndex];
        Unsafe.SkipInit(out Battle state);
        Battle.CopyPacked(in root, ref state);
        int brightestFlameSpent = _options.InitialBrightestFlameMaxHpSpent;
        List<ProgressTurnBuilder> builders = [];
        ProgressTurnBuilder? current = null;
        for (int index = 0; index < actions.Length; index++)
        {
            PlayerAction action = actions[index];
            Unsafe.SkipInit(out Battle before);
            Battle.CopyPacked(in state, ref before);
            StepResult step = Kernel.Step(ref state, action, _rules, out _,
                out _, out _, out int actionBrightestFlameSpent);
            if (step != StepResult.Completed)
                throw new InvalidDataException("推演候选的路线无法从根回放。");
            if (_options.BrightestFlameMaxHpLossLimit.HasValue)
            {
                brightestFlameSpent = checked(brightestFlameSpent + actionBrightestFlameSpent);
                if (!GrowthCostPolicy.AllowsBrightestFlame(
                        _options.BrightestFlameMaxHpLossLimit,
                        _options.InitialBrightestFlameMaxHpSpent, brightestFlameSpent))
                {
                    throw new InvalidDataException("推演候选的路线超过至亮之焰最大生命消耗上限。");
                }
            }
            int turn = before.PlayerTurn;
            if (current is null || current.Turn != turn)
            {
                current = new ProgressTurnBuilder(turn);
                builders.Add(current);
            }
            current.Actions.Add(ProgressAction(before, state, action));
            current.HpLost += Math.Max(0,
                state.Counter(Counter.CumulativeHpLost) - before.Counter(Counter.CumulativeHpLost));
            current.HpRecovered += Math.Max(0,
                state.Counter(Counter.RecoveredHp) - before.Counter(Counter.RecoveredHp));
            current.EnemyHpLost += Math.Max(0, RawEnemyHp(before) - RawEnemyHp(state));
            current.EnergyLeft = action.Kind == PlayerActionKind.EndTurn
                || state.PlayerTurn > before.PlayerTurn
                    ? before.Energy
                    : state.Energy;
            if (state.Outcome == Outcome.Victory)
                current.CombatEnded = true;
        }
        SearchProgressTurn[] turns = new SearchProgressTurn[builders.Count];
        for (int index = 0; index < builders.Count; index++)
            turns[index] = builders[index].Build();
        int throughTurn = turns.Length == 0 ? _initialPlayerTurn : turns[^1].Turn;
        return new ProgressReplay(state, throughTurn, turns);
    }

    private SearchProgressAction ProgressAction(in Battle before, in Battle after, PlayerAction action)
    {
        string? cardId = null;
        string? cardType = null;
        string? potionId = null;
        int upgrade = 0;
        int replayCount = 0;
        if (action.Kind == PlayerActionKind.PlayCard)
        {
            if (action.HandIndex >= before.HandCount)
            {
                throw new InvalidDataException(
                    $"推演动作的手牌下标 {action.HandIndex} 越界（n={before.HandCount}）。");
            }
            Card card = before.Cards[action.HandIndex];
            cardId = Registry.Card(card.Id).NativeId;
            upgrade = card.Upgrade;
            replayCount = card.ReplayCount;
            cardType = Kernel.ResolveCardType(before, _rules, card) switch
            {
                CardType.Attack => "attack",
                CardType.Skill => "skill",
                CardType.Power => "power",
                CardType.Status => "status",
                CardType.Curse => "curse",
                CardType.Quest => "quest",
                _ => throw new InvalidDataException(
                    $"推演动作的卡牌类型未知：{card.Id}。"),
            };
        }
        else if (action.Kind == PlayerActionKind.UsePotion)
        {
            if (action.PotionSlot >= before.PotionSlotCount)
            {
                throw new InvalidDataException(
                    $"推演动作的药水槽 {action.PotionSlot} 越界（n={before.PotionSlotCount}）。");
            }
            ushort stored = before.Potions[action.PotionSlot];
            if (stored == 0)
                throw new InvalidDataException($"推演动作的药水槽 {action.PotionSlot} 为空。");
            potionId = Registry.Potion((ushort)(stored - 1)).NativeId;
        }
        SearchProgressTargetKind targetKind = SearchProgressTargetKind.None;
        uint? targetCombatId = null;
        string? targetMonsterId = null;
        int targetOrdinal = 0;
        if (action.Target == Limits.PlayerOwner)
        {
            targetKind = SearchProgressTargetKind.Player;
        }
        else if (action.Target == Limits.OstyOwner)
        {
            targetKind = SearchProgressTargetKind.Osty;
        }
        else if (action.Target >= Limits.FirstEnemyOwner)
        {
            int enemyIndex = action.Target - Limits.FirstEnemyOwner;
            if (enemyIndex < before.EnemyCount)
            {
                Enemy enemy = before.Enemies[enemyIndex];
                targetKind = SearchProgressTargetKind.Enemy;
                targetCombatId = enemy.CombatId;
                targetMonsterId = Registry.Monster(enemy.MonsterId).NativeId;
                targetOrdinal = MonsterOrdinal(before, enemyIndex);
            }
        }
        return new SearchProgressAction(
            action.Kind,
            cardId,
            upgrade,
            potionId,
            cardType,
            targetKind,
            targetCombatId,
            targetMonsterId,
            targetOrdinal,
            ProgressKills(before, after),
            before.Player.CurrentHp,
            after.Player.CurrentHp,
            before.Energy,
            after.Energy,
            replayCount);
    }

    private static int MonsterOrdinal(in Battle battle, int enemyIndex)
    {
        ushort monsterId = battle.Enemies[enemyIndex].MonsterId;
        int total = 0;
        int ordinal = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (battle.Enemies[index].MonsterId != monsterId)
                continue;
            total++;
            if (index == enemyIndex)
                ordinal = total;
        }
        return total > 1 ? ordinal : 0;
    }

    private static SearchProgressKill[] ProgressKills(in Battle before, in Battle after)
    {
        List<SearchProgressKill> kills = [];
        int count = Math.Min(before.EnemyCount, after.EnemyCount);
        for (int index = 0; index < count; index++)
        {
            if (!before.Enemies[index].Creature.IsAlive || after.Enemies[index].Creature.IsAlive)
                continue;
            Enemy enemy = before.Enemies[index];
            kills.Add(new SearchProgressKill(
                enemy.CombatId,
                Registry.Monster(enemy.MonsterId).NativeId,
                MonsterOrdinal(before, index)));
        }
        return [.. kills];
    }

    /// <summary>
    /// Replays the candidate one last time and returns a normal result carrying its provenance.
    /// A mismatch is a hard failure; no partial route is handed back.
    /// </summary>
    private BeamResult MaterializeAdoption(SearchRouteCandidate candidate, SearchAdoptionKind kind)
    {
        ProgressReplay replay = ReplayProgress(candidate.Actions, candidate.RootIndex);
        if (!Fingerprint.Equal(replay.FinalState, candidate.FinalState)
            || replay.ThroughTurn != candidate.ThroughTurn)
        {
            throw new InvalidDataException(
                $"采用候选版本 {candidate.CandidateVersion} 回放后的状态与候选快照不一致。");
        }
        BeamResult result = new(candidate.Actions, candidate.Victory, candidate.Score,
            _expanded, _transitions, _duplicates,
            Math.Max(1, candidate.ThroughTurn - _initialPlayerTurn + 1),
            _pruned, _solveWatch?.Elapsed ?? TimeSpan.Zero)
        {
            StopReason = kind == SearchAdoptionKind.Stopped ? "stopped" : "adopted",
            BestRootIndex = candidate.RootIndex,
            PotionPolicySatisfied = true,
            Adoption = new SearchAdoptionInfo(candidate.CandidateVersion, candidate.ThroughTurn,
                kind, replay.FinalState.Outcome)
            {
                OnlyDeathRoutes = candidate.OnlyDeathRoutes,
            },
        };
        return WithSolveDiagnostics(result);
    }

    private BeamResult StopWithoutCandidateResult()
        => WithSolveDiagnostics(new BeamResult([], false, double.NegativeInfinity,
            _expanded, _transitions, _duplicates, 0, _pruned,
            _solveWatch?.Elapsed ?? TimeSpan.Zero)
        {
            StopReason = "stopped",
            StopWithoutCandidate = true,
            PotionPolicySatisfied = false,
        });

    private BeamResult WithSolveDiagnostics(BeamResult result) => result with
    {
        CapacityPruned = _capacityPruned,
        TurnLoopGuarded = TurnLoopGuardedTotal,
        CrossTurnNoProgressPruned = CrossTurnNoProgressPrunedTotal,
        RouteProgressDetailPeakCount = _nodes.PeakProgressEnemyHighsCount,
        RouteProgressDetailStorageBytes = _nodes.ProgressEnemyHighsStorageBytes,
        EndTurnPrefixShared = EndTurnPrefixSharedTotal,
        EndTurnPrefixUnshared = EndTurnPrefixUnsharedTotal,
        EndTurnPrefixVerified = EndTurnPrefixVerifiedTotal,
        ThreatProjectionVerified = _scorer.ThreatProjectionChecks,
        EndTurnPrefixReusedLeaves = EndTurnPrefixReusedLeavesTotal,
        EndTurnPrefixChoiceUnshared = EndTurnPrefixChoiceUnsharedTotal,
        EndTurnPrefixExtraTurnUnshared = EndTurnPrefixExtraTurnUnsharedTotal,
        EndTurnPrefixNotInProgress = EndTurnPrefixNotInProgressTotal,
        CapacityDroppedChildren = _capacityDropped.Count == 0
            ? BeamResult.NoCapacityDrops
            : _capacityDropped,
        EndedCompactions = _endedCompactions,
        EndedCompressedStates = _endedCompressedStates,
        WorkerYieldCount = _options.Pacer?.YieldCount ?? 0,
        FrameRecoveryWaitCount = _options.Pacer?.FrameRecoveryWaitCount ?? 0,
        FrameRecoveryWaitDuration = _options.Pacer?.FrameRecoveryWaitDuration ?? TimeSpan.Zero,
        MaxObservedGcPause = _options.Pacer?.MaxObservedGcPause ?? TimeSpan.Zero,
        Equivalence = EquivalenceDiagnostics(),
        ExpandedNodesTotal = _expandedTotal,
        SearchMemoryUsedBytes = _storage.Memory.UsedBytes,
        SearchMemoryPeakBytes = _storage.Memory.PeakBytes,
        SearchMemoryLimitBytes = _storage.Memory.LimitBytes,
        SearchMemoryBudgetExceeded = _memoryBudgetExceeded || _storage.Memory.Exceeded,
        SearchMemoryExceededAtBytes = _memoryBudgetExceeded
            ? _memoryBudgetExceededAtBytes : _storage.Memory.ExceededAtBytes,
        SoftTimeBudgetReached = SoftTimeReached(_solveWatch!),
        RouteNodeStorageBytes = _nodes.StorageBytes,
        RouteNodeCount = _nodes.Count,
        RouteNodePeakCount = _nodes.PeakCount,
        SearchWorkspacePeakBytes = _workspacePeakBytes,
        SearchWorkspaceGrowablePeakBytes = _workspaceGrowablePeakBytes,
        WorkspaceOverflowParents = _workspaceOverflowParents,
        WorkspaceParents = _workspaceParents,
        WorkspaceDirectDuration = Stopwatch.GetElapsedTime(0, _workspaceDirectTicks),
    };

    private static int RawEnemyHp(in Battle battle)
    {
        int hp = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
            hp += Math.Max(0, battle.Enemies[index].Creature.CurrentHp);
        return hp;
    }

    private readonly record struct ProgressReplay(
        Battle FinalState,
        int ThroughTurn,
        SearchProgressTurn[] Turns);

    private sealed class ProgressTurnBuilder(int turn)
    {
        internal int Turn { get; } = turn;
        internal List<SearchProgressAction> Actions { get; } = [];
        internal int HpLost;
        internal int HpRecovered;
        internal int EnemyHpLost;
        internal int EnergyLeft;
        internal bool CombatEnded;

        internal SearchProgressTurn Build() => new(Turn, [.. Actions], HpLost, HpRecovered,
            EnemyHpLost, EnergyLeft, CombatEnded);
    }
}
