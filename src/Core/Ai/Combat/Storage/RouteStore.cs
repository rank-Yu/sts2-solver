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
        private readonly ChunkedList<int> _payoffCount;
        private readonly ChunkedList<ushort> _potionId;
        private readonly ChunkedList<ushort> _firstCardId;
        private readonly ChunkedList<int> _firstCardTarget;
        private readonly ChunkedList<int> _commitment;
        private readonly ChunkedList<ushort> _actionCardId;
        private readonly ChunkedList<int> _targetCombatId;
        private readonly ChunkedList<int> _routingMetaId;
        private readonly ChunkedList<int> _parentTurn;
        private readonly RoutingMetaStore _metas;

        internal RouteNodeStore(SearchStorage storage, RoutingMetaStore metas)
        {
            _parent = new ChunkedList<int>(storage);
            _depth = new ChunkedList<int>(storage);
            _action = new ChunkedList<PlayerAction>(storage);
            _rootActionKey = new ChunkedList<ulong>(storage);
            _lineage = new ChunkedList<ulong>(storage);
            _payoffCount = new ChunkedList<int>(storage);
            _potionId = new ChunkedList<ushort>(storage);
            _firstCardId = new ChunkedList<ushort>(storage);
            _firstCardTarget = new ChunkedList<int>(storage);
            _commitment = new ChunkedList<int>(storage);
            _actionCardId = new ChunkedList<ushort>(storage);
            _targetCombatId = new ChunkedList<int>(storage);
            _routingMetaId = new ChunkedList<int>(storage);
            _parentTurn = new ChunkedList<int>(storage);
            _metas = metas;
        }

        internal int Count => _parent.Count;

        internal int PeakCount { get; private set; }

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
                    + Unsafe.SizeOf<int>() + Unsafe.SizeOf<int>());
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
            _payoffCount.Clear();
            _potionId.Clear();
            _firstCardId.Clear();
            _firstCardTarget.Clear();
            _commitment.Clear();
            _actionCardId.Clear();
            _targetCombatId.Clear();
            _routingMetaId.Clear();
            _parentTurn.Clear();
            PeakCount = 0;
        }

        internal void Add(in RouteNode node, int routingMetaId)
        {
            _parent.Add(node.Parent);
            _depth.Add(node.Depth);
            _action.Add(node.Action);
            _rootActionKey.Add(node.RootActionKey);
            _lineage.Add(node.Lineage);
            _payoffCount.Add(node.PayoffCount);
            _potionId.Add(node.PotionId);
            _firstCardId.Add(node.FirstCardId);
            _firstCardTarget.Add(node.FirstCardTarget);
            _commitment.Add(node.Commitment);
            _actionCardId.Add(checked((ushort)node.ActionCardId));
            _targetCombatId.Add(node.TargetCombatId);
            _routingMetaId.Add(routingMetaId);
            _parentTurn.Add(node.ParentTurn);
            if (Count > PeakCount)
                PeakCount = Count;
        }

        internal RouteNode this[int index]
        {
            get
            {
                RoutingMeta meta = _metas[_routingMetaId[index]];
                return new RouteNode(
                    _parent[index], _depth[index], _action[index], _rootActionKey[index],
                    _lineage[index], _payoffCount[index], _potionId[index], _firstCardId[index],
                    _firstCardTarget[index], _commitment[index], _actionCardId[index],
                    _targetCombatId[index], meta.SignatureId, meta.Turn, meta.ChoiceDepth,
                    meta.ParentRank, meta.ParentScore, meta.ParentEnergy, _parentTurn[index],
                    _routingMetaId[index]);
            }
        }

        internal int ParentAt(int index) => _parent[index];

        internal void SetParent(int index, int value) => _parent[index] = value;

        internal int CommitmentAt(int index) => _commitment[index];

        internal void SetCommitment(int index, int value) => _commitment[index] = value;

        internal int RoutingMetaIdAt(int index) => _routingMetaId[index];

        internal void SetRoutingMetaId(int index, int value) => _routingMetaId[index] = value;

        internal void Move(int from, int to)
        {
            _parent.Move(from, to);
            _depth.Move(from, to);
            _action.Move(from, to);
            _rootActionKey.Move(from, to);
            _lineage.Move(from, to);
            _payoffCount.Move(from, to);
            _potionId.Move(from, to);
            _firstCardId.Move(from, to);
            _firstCardTarget.Move(from, to);
            _commitment.Move(from, to);
            _actionCardId.Move(from, to);
            _targetCombatId.Move(from, to);
            _routingMetaId.Move(from, to);
            _parentTurn.Move(from, to);
        }

        internal void Truncate(int count)
        {
            _parent.Truncate(count);
            _depth.Truncate(count);
            _action.Truncate(count);
            _rootActionKey.Truncate(count);
            _lineage.Truncate(count);
            _payoffCount.Truncate(count);
            _potionId.Truncate(count);
            _firstCardId.Truncate(count);
            _firstCardTarget.Truncate(count);
            _commitment.Truncate(count);
            _actionCardId.Truncate(count);
            _targetCombatId.Truncate(count);
            _routingMetaId.Truncate(count);
            _parentTurn.Truncate(count);
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
        AddRoot(roots, bestNode);
        AddRoot(roots, _bestFeaturesNode);
        AddRoot(roots, _endTurnPrefixNode);
        AddRoot(roots, _endTurnPrefixAttemptNode);
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
        _endTurnPrefixNode = RemapNode(map, _endTurnPrefixNode);
        _endTurnPrefixAttemptNode = RemapNode(map, _endTurnPrefixAttemptNode);
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
    /// On a memory-budget stop with no routable candidate yet, returns the best partially expanded
    /// state so the caller still gets a route for the current turn instead of an empty route.
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
                int depth = _nodes[node].Depth - _options.InitialActionCount;
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
    /// Boundary cleanup trigger. One node costs 126 logical bytes; keeping the trigger near a third of
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
