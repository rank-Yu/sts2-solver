using System.Runtime.CompilerServices;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>
    /// One routing-context entry. The original per-node fields are constant along a route segment and
    /// only change when a choice is answered (and once more when the choice's signature stops being
    /// current), so nodes reference a shared entry instead of storing six fields each.
    /// </summary>
    private readonly record struct RoutingMeta(
        int SignatureId,
        int Turn,
        int ChoiceDepth,
        int ParentRank,
        double ParentScore,
        int ParentEnergy);

    private sealed class RoutingMetaStore
    {
        private readonly ChunkedList<RoutingMeta> _items;

        internal RoutingMetaStore(SearchStorage storage)
            => _items = new ChunkedList<RoutingMeta>(storage);

        internal int Count => _items.Count;

        internal RoutingMeta this[int index] => _items[index];

        internal void Clear()
        {
            _items.Clear();
            // Index 0 is the root context: no signature, turn 0, depth 0, maximum parent rank.
            _items.Add(new RoutingMeta(0, 0, 0, int.MaxValue, 0d, 0));
        }

        internal int Add(in RoutingMeta meta)
        {
            _items.Add(meta);
            return _items.Count - 1;
        }

        internal void Move(int from, int to) => _items.Move(from, to);

        internal void Set(int index, in RoutingMeta meta) => _items[index] = meta;

        internal void Truncate(int count) => _items.Truncate(count);
    }

    /// <summary>
    /// Structure-of-arrays route-node store. Each field has its own chunked array, so a growing node
    /// chain never holds an old and a new copy of a multi-megabyte record array at the same time.
    /// The routing context is interned in <see cref="RoutingMetaStore"/>.
    /// </summary>
    private sealed class RouteNodeStore
    {
        private const int ChunkShift = 12;
        private const int ChunkSize = 1 << ChunkShift;

        private readonly ChunkedList<int> _parent;
        private readonly ChunkedList<int> _depth;
        private readonly ChunkedList<PlayerAction> _action;
        private readonly ChunkedList<ulong> _rootActionKey;
        private readonly ChunkedList<ulong> _lineage;
        private readonly ChunkedList<ushort> _potionId;
        private readonly ChunkedList<ushort> _firstCardId;
        private readonly ChunkedList<int> _firstCardTarget;
        private readonly ChunkedList<int> _commitment;
        private readonly ChunkedList<ushort> _actionCardId;
        private readonly ChunkedList<int> _targetCombatId;
        private readonly ChunkedList<int> _routingMetaId;
        private readonly ChunkedList<int> _parentTurn;
        private readonly ChunkedList<int> _potionLineageKeyId;
        private readonly ChunkedList<TurnProgressState> _progress;
        private readonly ChunkedList<EnemyProgressHighs> _progressEnemyHighs;
        private readonly ChunkedList<int> _crossTurnId;
        private readonly ChunkedList<CrossTurnProgress> _crossTurn;
        private readonly ChunkedList<int>? _brightestFlameSpent;
        private readonly RoutingMetaStore _metas;
        private readonly SearchStorage _storage;

        internal RouteNodeStore(SearchStorage storage, RoutingMetaStore metas,
            bool trackBrightestFlameSpent)
        {
            _parent = new ChunkedList<int>(storage);
            _depth = new ChunkedList<int>(storage);
            _action = new ChunkedList<PlayerAction>(storage);
            _rootActionKey = new ChunkedList<ulong>(storage);
            _lineage = new ChunkedList<ulong>(storage);
            _potionId = new ChunkedList<ushort>(storage);
            _firstCardId = new ChunkedList<ushort>(storage);
            _firstCardTarget = new ChunkedList<int>(storage);
            _commitment = new ChunkedList<int>(storage);
            _actionCardId = new ChunkedList<ushort>(storage);
            _targetCombatId = new ChunkedList<int>(storage);
            _routingMetaId = new ChunkedList<int>(storage);
            _parentTurn = new ChunkedList<int>(storage);
            _potionLineageKeyId = new ChunkedList<int>(storage);
            _progress = new ChunkedList<TurnProgressState>(storage);
            _progressEnemyHighs = new ChunkedList<EnemyProgressHighs>(storage);
            _crossTurnId = new ChunkedList<int>(storage);
            _crossTurn = new ChunkedList<CrossTurnProgress>(storage);
            PeakProgressEnemyHighsCount = 0;
            _storage = storage;
            _brightestFlameSpent = trackBrightestFlameSpent
                ? new ChunkedList<int>(storage) : null;
            _metas = metas;
        }

        internal int Count => _parent.Count;

        internal int PeakCount { get; private set; }

        /// <summary>True only when the request carries a BrightestFlame cap; the default path is free.</summary>
        internal bool TracksBrightestFlameSpent => _brightestFlameSpent is not null;

        internal int BrightestFlameSpentAt(int index) => _brightestFlameSpent![index];

        /// <summary>High-water mark of sparse per-enemy progress details in this solve.</summary>
        internal int PeakProgressEnemyHighsCount { get; private set; }

        /// <summary>Logical chunk bytes retained by the sparse progress detail table at its peak.</summary>
        internal long ProgressEnemyHighsStorageBytes
        {
            get
            {
                long chunks = ((long)PeakProgressEnemyHighsCount + ChunkSize - 1) >> ChunkShift;
                return chunks * ChunkSize * Unsafe.SizeOf<EnemyProgressHighs>();
            }
        }

        /// <summary>Logical bytes of the node chunks at the high-water node count.</summary>
        internal long StorageBytes
        {
            get
            {
                long chunkBytes = (long)ChunkSize * (
                    Unsafe.SizeOf<int>() + Unsafe.SizeOf<int>() + Unsafe.SizeOf<PlayerAction>()
                    + Unsafe.SizeOf<ulong>() + Unsafe.SizeOf<ulong>() + Unsafe.SizeOf<int>()
                    + Unsafe.SizeOf<ushort>() + Unsafe.SizeOf<ushort>() + Unsafe.SizeOf<int>()
                    + Unsafe.SizeOf<int>() + Unsafe.SizeOf<ushort>() + Unsafe.SizeOf<int>()
                    + Unsafe.SizeOf<int>() + Unsafe.SizeOf<int>() + Unsafe.SizeOf<int>()
                    + Unsafe.SizeOf<TurnProgressState>() + Unsafe.SizeOf<int>()
                    + (_brightestFlameSpent is null ? 0 : Unsafe.SizeOf<int>()));
                long chunks = ((long)PeakCount + ChunkSize - 1) >> ChunkShift;
                return chunks * chunkBytes;
            }
        }

        internal void Clear()
        {
            _parent.Clear();
            _depth.Clear();
            _action.Clear();
            _rootActionKey.Clear();
            _lineage.Clear();
            _potionId.Clear();
            _firstCardId.Clear();
            _firstCardTarget.Clear();
            _commitment.Clear();
            _actionCardId.Clear();
            _targetCombatId.Clear();
            _routingMetaId.Clear();
            _parentTurn.Clear();
            _potionLineageKeyId.Clear();
            _progress.Clear();
            _progressEnemyHighs.Clear();
            _crossTurnId.Clear();
            _crossTurn.Clear();
            PeakProgressEnemyHighsCount = 0;
            _brightestFlameSpent?.Clear();
            PeakCount = 0;
        }

        internal void Add(in RouteNode node, int routingMetaId, int brightestFlameSpent)
        {
            _parent.Add(node.Parent);
            _depth.Add(node.Depth);
            _action.Add(node.Action);
            _rootActionKey.Add(node.RootActionKey);
            _lineage.Add(node.Lineage);
            _potionId.Add(node.PotionId);
            _firstCardId.Add(node.FirstCardId);
            _firstCardTarget.Add(node.FirstCardTarget);
            _commitment.Add(node.Commitment);
            _actionCardId.Add(checked((ushort)node.ActionCardId));
            _targetCombatId.Add(node.TargetCombatId);
            _routingMetaId.Add(routingMetaId);
            _parentTurn.Add(node.ParentTurn);
            _potionLineageKeyId.Add(node.PotionLineageKeyId);
            _progress.Add(node.Progress);
            _crossTurnId.Add(node.CrossTurnId);
            _brightestFlameSpent?.Add(brightestFlameSpent);
            if (Count > PeakCount)
                PeakCount = Count;
        }

        internal RouteNodeView View(int index) => new(this, index);

        internal int ParentAt(int index) => _parent[index];

        internal void SetParent(int index, int value) => _parent[index] = value;

        internal int CommitmentAt(int index) => _commitment[index];

        internal void SetCommitment(int index, int value) => _commitment[index] = value;

        internal int RoutingMetaIdAt(int index) => _routingMetaId[index];

        internal void SetRoutingMetaId(int index, int value) => _routingMetaId[index] = value;

        internal int PotionLineageKeyAt(int index) => _potionLineageKeyId[index];

        internal TurnProgressState ProgressAt(int index) => _progress[index];

        internal int CrossTurnIdAt(int index) => _crossTurnId[index];

        internal void SetCrossTurnId(int index, int id) => _crossTurnId[index] = id;

        /// <summary>Stores a turn-start record and returns its 1-based id; every node has one.</summary>
        internal int AddCrossTurn(in CrossTurnProgress progress)
        {
            _crossTurn.Add(progress);
            return _crossTurn.Count;
        }

        internal CrossTurnProgress CrossTurnAt(int id)
        {
            if (id <= 0 || id > _crossTurn.Count)
                throw new InvalidOperationException($"路线跨回合进展记录越界：{id}/{_crossTurn.Count}。");
            return _crossTurn[id - 1];
        }

        /// <summary>
        /// Keeps only the turn-start records a live node references and rewrites their 1-based ids,
        /// in the same pass order as <see cref="CompactProgressEnemyHighs"/>.
        /// </summary>
        internal void CompactCrossTurn()
        {
            int count = _crossTurn.Count;
            if (count == 0)
                return;
            int[] map = _storage.IntScratch(count + 1);
            Array.Fill(map, -1);
            for (int index = 0; index < Count; index++)
            {
                int id = _crossTurnId[index];
                if (id <= 0 || id > count)
                    throw new InvalidOperationException($"路线跨回合进展记录越界：{id}/{count}。");
                map[id] = -2;
            }
            int live = 1;
            for (int id = 1; id <= count; id++)
            {
                if (map[id] == -2)
                    map[id] = live++;
            }
            if (live == count + 1)
                return;
            for (int id = 1; id <= count; id++)
            {
                int target = map[id];
                if (target > 0 && target != id)
                    _crossTurn.Move(id - 1, target - 1);
            }
            _crossTurn.Truncate(live - 1);
            for (int index = 0; index < Count; index++)
                _crossTurnId[index] = map[_crossTurnId[index]];
        }

        internal int AddProgressEnemyHighs(in EnemyProgressHighs highs)
        {
            _progressEnemyHighs.Add(highs);
            int count = _progressEnemyHighs.Count;
            if (count > PeakProgressEnemyHighsCount)
                PeakProgressEnemyHighsCount = count;
            return count;
        }

        internal EnemyProgressHighs ProgressEnemyHighsAt(int index)
            => index == 0 ? default : _progressEnemyHighs[index - 1];

        /// <summary>
        /// Keeps only per-enemy details referenced by a live route node and rewrites their 1-based
        /// ids after node compaction. Runs on the search thread after node remapping; the shared
        /// integer scratch is free at this point.
        /// </summary>
        internal void CompactProgressEnemyHighs()
        {
            int count = _progressEnemyHighs.Count;
            if (count == 0)
                return;
            int[] map = _storage.IntScratch(count + 1);
            Array.Fill(map, -1);
            map[0] = 0;
            for (int index = 0; index < Count; index++)
            {
                int id = _progress[index].EnemyHighsId;
                if ((uint)id > (uint)count)
                    throw new InvalidOperationException(
                        $"路线 progress 明细越界：{id}/{count}。");
                if (id > 0)
                    map[id] = -2;
            }
            int live = 1;
            for (int id = 1; id <= count; id++)
            {
                if (map[id] == -2)
                    map[id] = live++;
            }
            if (live == count + 1)
                return;
            for (int id = 1; id <= count; id++)
            {
                int target = map[id];
                if (target > 0 && target != id)
                    _progressEnemyHighs.Move(id - 1, target - 1);
            }
            _progressEnemyHighs.Truncate(live - 1);
            for (int index = 0; index < Count; index++)
            {
                TurnProgressState progress = _progress[index];
                if (progress.EnemyHighsId > 0)
                {
                    _progress[index] = progress with
                    {
                        EnemyHighsId = map[progress.EnemyHighsId],
                    };
                }
            }
        }

        /// <summary>
        /// Read-only field view of one route node. The store is structure-of-arrays, so this avoids
        /// rebuilding and copying the whole <see cref="RouteNode"/> for a caller that reads a few fields.
        /// </summary>
        internal readonly ref struct RouteNodeView
        {
            private readonly RouteNodeStore _store;
            private readonly int _index;

            internal RouteNodeView(RouteNodeStore store, int index)
            {
                _store = store;
                _index = index;
            }

            internal int Parent => _store._parent[_index];
            internal int Depth => _store._depth[_index];
            internal PlayerAction Action => _store._action[_index];
            internal ulong RootActionKey => _store._rootActionKey[_index];
            internal ulong Lineage => _store._lineage[_index];
            internal ushort PotionId => _store._potionId[_index];
            internal ushort FirstCardId => _store._firstCardId[_index];
            internal int FirstCardTarget => _store._firstCardTarget[_index];
            internal int Commitment => _store._commitment[_index];
            internal int ActionCardId => _store._actionCardId[_index];
            internal int TargetCombatId => _store._targetCombatId[_index];
            internal int RoutingSignatureId => _store._metas[_store._routingMetaId[_index]].SignatureId;
            internal int RoutingChoiceTurn => _store._metas[_store._routingMetaId[_index]].Turn;
            internal int RoutingChoiceDepth => _store._metas[_store._routingMetaId[_index]].ChoiceDepth;
            internal int RoutingParentRank => _store._metas[_store._routingMetaId[_index]].ParentRank;
            internal double RoutingParentScore => _store._metas[_store._routingMetaId[_index]].ParentScore;
            internal int RoutingParentEnergy => _store._metas[_store._routingMetaId[_index]].ParentEnergy;
            internal int ParentTurn => _store._parentTurn[_index];
            internal int RoutingMetaId => _store._routingMetaId[_index];
            internal int PotionLineageKeyId => _store._potionLineageKeyId[_index];
        }

        internal void Move(int from, int to)
        {
            _parent.Move(from, to);
            _depth.Move(from, to);
            _action.Move(from, to);
            _rootActionKey.Move(from, to);
            _lineage.Move(from, to);
            _potionId.Move(from, to);
            _firstCardId.Move(from, to);
            _firstCardTarget.Move(from, to);
            _commitment.Move(from, to);
            _actionCardId.Move(from, to);
            _targetCombatId.Move(from, to);
            _routingMetaId.Move(from, to);
            _parentTurn.Move(from, to);
            _potionLineageKeyId.Move(from, to);
            _progress.Move(from, to);
            _crossTurnId.Move(from, to);
            _brightestFlameSpent?.Move(from, to);
        }

        internal void Truncate(int count)
        {
            _parent.Truncate(count);
            _depth.Truncate(count);
            _action.Truncate(count);
            _rootActionKey.Truncate(count);
            _lineage.Truncate(count);
            _potionId.Truncate(count);
            _firstCardId.Truncate(count);
            _firstCardTarget.Truncate(count);
            _commitment.Truncate(count);
            _actionCardId.Truncate(count);
            _targetCombatId.Truncate(count);
            _routingMetaId.Truncate(count);
            _parentTurn.Truncate(count);
            _potionLineageKeyId.Truncate(count);
            _progress.Truncate(count);
            _crossTurnId.Truncate(count);
            _brightestFlameSpent?.Truncate(count);
        }
    }

    /// <summary>
    /// Keeps only nodes that are an ancestor of a live search state. Roots are every pool slot, the
    /// current best, the best potion-free/audited candidates and the end-turn prefix candidates. The
    /// rewrite preserves ascending index order, so the node-index tie-breakers compare exactly as
    /// before; parent links are rewritten through the same map.
    /// </summary>
    private void CompactRouteNodes(ref int bestNode,
        Pool? current, Pool? next, Pool? ended, Pool? turnFrontier)
    {
        // The offline trace keys its watched targets by the original node identity. Trace is a
        // diagnostic mode, not a measured search, so keep its identities stable.
        if (_trace != null)
            return;
        List<int> roots = _compactRoots;
        roots.Clear();
        current?.AppendNodes(roots);
        next?.AppendNodes(roots);
        ended?.AppendNodes(roots);
        turnFrontier?.AppendNodes(roots);
        // Multi-root requests must keep every original root node: a descendant whose parent root
        // was compacted away would be rewritten to a new root and lose its true RootIndex. Keeping
        // the few roots also keeps BestRootIndex meaningful after compaction. Single-root searches
        // keep the previous pruning behavior bit for bit.
        if (_rootNodeCount > 1)
        {
            for (int index = 0; index < _rootNodeCount; index++)
                AddRoot(roots, index);
        }
        AddRoot(roots, bestNode);
        AddRoot(roots, _bestFeaturesNode);
        foreach (ExpansionWorkspace workspace in _workspaces)
        {
            AddRoot(roots, workspace.EndTurnPrefixNode);
            AddRoot(roots, workspace.EndTurnPrefixAttemptNode);
        }
        if (_bestPotionFree is { } potionFree)
            AddRoot(roots, potionFree.Node);
        if (_auditBestByPotionCount is { } audited)
        {
            foreach (Candidate candidate in audited.Values)
                AddRoot(roots, candidate.Node);
        }
        int count = _nodes.Count;
        if (count == 0)
            return;
        int[] map = _storage.IntScratch(count);
        Array.Fill(map, -1, 0, count);
        for (int index = 0; index < roots.Count; index++)
        {
            if (_options.UnlimitedNodes && (index & 1023) == 0) SearchCheckpoint();
            int cursor = roots[index];
            if ((uint)cursor >= (uint)count)
                throw new InvalidOperationException($"路线节点根越界：{cursor}/{count}。");
            int traversed = 0;
            while (cursor >= 0 && map[cursor] == -1)
            {
                if (_options.UnlimitedNodes && (traversed++ & 1023) == 0) SearchCheckpoint();
                map[cursor] = -2;
                cursor = _nodes.ParentAt(cursor);
            }
        }

        int live = 0;
        for (int index = 0; index < count; index++)
        {
            if (_options.UnlimitedNodes && (index & 1023) == 0) SearchCheckpoint();
            if (map[index] == -2)
                map[index] = live++;
        }
        if (live == count)
            return;

        // Ascending order means every destination is already-finalized space: the source at that
        // index (when live) was moved earlier because its new index is below the current one.
        for (int index = 0; index < count; index++)
        {
            int target = map[index];
            if (target >= 0 && target != index)
                _nodes.Move(index, target);
        }
        for (int index = 0; index < live; index++)
        {
            int parent = _nodes.ParentAt(index);
            _nodes.SetParent(index, parent < 0 ? -1 : map[parent]);
        }
        _nodes.Truncate(live);

        current?.RemapNodes(map);
        next?.RemapNodes(map);
        ended?.RemapNodes(map);
        turnFrontier?.RemapNodes(map);
        bestNode = RemapNode(map, bestNode);
        _bestFeaturesNode = RemapNode(map, _bestFeaturesNode);
        foreach (ExpansionWorkspace workspace in _workspaces)
        {
            workspace.EndTurnPrefixNode = RemapNode(map, workspace.EndTurnPrefixNode);
            workspace.EndTurnPrefixAttemptNode =
                RemapNode(map, workspace.EndTurnPrefixAttemptNode);
        }
        if (_bestPotionFree is { } bestFree)
            _bestPotionFree = bestFree with { Node = RemapNode(map, bestFree.Node) };
        if (_auditBestByPotionCount is { } bestByCount)
        {
            _auditKeysScratch.Clear();
            foreach (int key in bestByCount.Keys)
                _auditKeysScratch.Add(key);
            for (int index = 0; index < _auditKeysScratch.Count; index++)
            {
                int key = _auditKeysScratch[index];
                Candidate candidate = bestByCount[key];
                bestByCount[key] = candidate with { Node = RemapNode(map, candidate.Node) };
            }
        }
        CompactCommitments(live);
        CompactRoutingMetadata();
        _nodes.CompactProgressEnemyHighs();
        _nodes.CompactCrossTurn();
        if (_options.UnlimitedNodes) SearchCheckpoint();
    }

    private void CompactCommitments(int liveNodes)
    {
        int count = _commitments.Count;
        if (count == 0)
            return;
        int[] map = _storage.IntScratch(count);
        Array.Fill(map, -1, 0, count);
        for (int index = 0; index < liveNodes; index++)
        {
            int commitment = _nodes.CommitmentAt(index);
            if (commitment > 0)
                map[commitment - 1] = -2;
        }
        int live = 0;
        for (int index = 0; index < count; index++)
        {
            if (map[index] == -2)
                map[index] = live++;
        }
        if (live == count)
            return;
        for (int index = 0; index < count; index++)
        {
            int target = map[index];
            if (target >= 0 && target != index)
                _commitments.Move(index, target);
        }
        for (int index = 0; index < liveNodes; index++)
        {
            int commitment = _nodes.CommitmentAt(index);
            if (commitment > 0)
                _nodes.SetCommitment(index, map[commitment - 1] + 1);
        }
        _commitments.Truncate(live);
    }

    private void CompactRoutingMetadata()
    {
        int metaCount = _routingMetas.Count;
        int nodeCount = _nodes.Count;
        int[] map = _storage.IntScratch(metaCount);
        Array.Fill(map, -1, 0, metaCount);
        map[0] = -2;
        for (int index = 0; index < nodeCount; index++)
        {
            int meta = _nodes.RoutingMetaIdAt(index);
            if ((uint)meta >= (uint)metaCount)
                throw new InvalidOperationException($"路线路由上下文越界：{meta}/{metaCount}。");
            map[meta] = -2;
        }
        int liveMeta = 0;
        for (int index = 0; index < metaCount; index++)
        {
            if (map[index] == -2)
                map[index] = liveMeta++;
        }
        if (liveMeta != metaCount)
        {
            for (int index = 0; index < metaCount; index++)
            {
                int target = map[index];
                if (target >= 0 && target != index)
                    _routingMetas.Move(index, target);
            }
            _routingMetas.Truncate(liveMeta);
            for (int index = 0; index < nodeCount; index++)
                _nodes.SetRoutingMetaId(index, map[_nodes.RoutingMetaIdAt(index)]);
        }

        int signatureCount = _routingSignatures.Count;
        if (signatureCount == 0)
            return;
        int[] signatureMap = _storage.IntScratch(signatureCount);
        Array.Fill(signatureMap, -1, 0, signatureCount);
        for (int index = 0; index < liveMeta; index++)
        {
            int signature = _routingMetas[index].SignatureId;
            if (signature > 0)
                signatureMap[signature - 1] = -2;
        }
        int liveSignatures = 0;
        for (int index = 0; index < signatureCount; index++)
        {
            if (signatureMap[index] == -2)
                signatureMap[index] = liveSignatures++;
        }
        if (liveSignatures != signatureCount)
        {
            for (int index = 0; index < signatureCount; index++)
            {
                int target = signatureMap[index];
                if (target >= 0 && target != index)
                    _routingSignatures.Move(index, target);
            }
            _routingSignatures.Truncate(liveSignatures);
            for (int index = 0; index < liveMeta; index++)
            {
                RoutingMeta meta = _routingMetas[index];
                if (meta.SignatureId > 0)
                {
                    _routingMetas.Set(index, meta with
                    {
                        SignatureId = signatureMap[meta.SignatureId - 1] + 1,
                    });
                }
            }
            _routingSignatureIds.Clear();
            for (int index = 0; index < liveSignatures; index++)
                _routingSignatureIds.Add(_routingSignatures[index], index + 1);
        }
    }

    private static void AddRoot(List<int> roots, int node)
    {
        if (node >= 0)
            roots.Add(node);
    }

    /// <summary>
    /// On a budget stop (memory, deadline, soft time or node ceiling) with no routable candidate yet,
    /// returns the best partially expanded state so the caller still gets a route for the current
    /// turn instead of an empty route.
    /// </summary>
    private int BudgetFallbackNode(Pool? current, Pool? ended, Pool? turnFrontier)
    {
        int best = -1;
        double bestScore = double.NegativeInfinity;
        int bestDepth = -1;
        ConsiderPool(current);
        ConsiderPool(ended);
        ConsiderPool(turnFrontier);
        return best;

        void ConsiderPool(Pool? pool)
        {
            if (pool == null)
                return;
            for (int index = 0; index < pool.Count; index++)
            {
                int node = pool.Node(index);
                int depth = _nodes.View(node).Depth;
                if (depth <= 0)
                    continue;
                double score = pool.Score(index);
                if (best < 0 || score > bestScore || score.Equals(bestScore) && depth > bestDepth)
                {
                    best = node;
                    bestScore = score;
                    bestDepth = depth;
                }
            }
        }
    }

    /// <summary>
    /// Exact interning of the sorted explicit-potion-id multiset along a route. A node inherits its
    /// parent's key id; a potion use inserts one id and looks the canonical multiset up by a
    /// commutative hash, confirming equality by comparing the stored ids. Equal multisets therefore
    /// share one id, and different multisets never do.
    /// </summary>
    private sealed class PotionLineageKeyStore
    {
        private readonly List<ushort> _ids = [];
        private readonly List<int> _start = [];
        private readonly List<int> _length = [];
        private readonly List<ulong> _hash = [];
        private readonly Dictionary<ulong, List<int>> _buckets = new();
        private readonly List<ushort> _scratch = [];

        internal void Clear()
        {
            _ids.Clear();
            _start.Clear();
            _length.Clear();
            _hash.Clear();
            _buckets.Clear();
            _scratch.Clear();
            AddKey(_scratch, 0);
        }

        internal int Extend(int parent, ushort potionId)
        {
            _scratch.Clear();
            int start = _start[parent];
            int length = _length[parent];
            for (int index = 0; index < length; index++)
                _scratch.Add(_ids[start + index]);
            int insert = 0;
            while (insert < _scratch.Count && _scratch[insert] <= potionId)
                insert++;
            _scratch.Insert(insert, potionId);
            ulong hash = unchecked(_hash[parent] + ItemHash(potionId));
            if (_buckets.TryGetValue(hash, out List<int>? bucket))
            {
                for (int index = 0; index < bucket.Count; index++)
                {
                    int candidate = bucket[index];
                    if (SequenceEquals(candidate))
                        return candidate;
                }
            }
            return AddKey(_scratch, hash);
        }

        private bool SequenceEquals(int key)
        {
            if (_length[key] != _scratch.Count)
                return false;
            int start = _start[key];
            for (int index = 0; index < _scratch.Count; index++)
            {
                if (_ids[start + index] != _scratch[index])
                    return false;
            }
            return true;
        }

        private int AddKey(List<ushort> ids, ulong hash)
        {
            int key = _start.Count;
            _start.Add(_ids.Count);
            _length.Add(ids.Count);
            _hash.Add(hash);
            for (int index = 0; index < ids.Count; index++)
                _ids.Add(ids[index]);
            if (!_buckets.TryGetValue(hash, out List<int>? bucket))
            {
                bucket = [];
                _buckets.Add(hash, bucket);
            }
            bucket.Add(key);
            return key;
        }

        private static ulong ItemHash(ushort id)
        {
            ulong value = id + 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    /// <summary>
    /// Boundary cleanup trigger. One node costs 130 logical bytes; keeping the trigger near a third of
    /// a small budget leaves room for the pools and the transient mark map. Large/unlimited runs keep
    /// a one-million-node ceiling so dead route history never accumulates without a sweep.
    /// </summary>
    private static int NodeCleanupThreshold(long budgetBytes)
    {
        if (budgetBytes <= 0)
            return 1_000_000;
        long byBudget = budgetBytes / 256;
        return (int)Math.Clamp(byBudget, 65_536, 2_000_000);
    }

    private static int RemapNode(int[] map, int node)
        => node < 0 ? -1 : map[node];
}
