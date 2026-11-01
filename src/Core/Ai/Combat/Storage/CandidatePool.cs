// Candidate storage, deduplication and retention; extracted from BeamSolver.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>
    /// State pool with packed-prefix deduplication keeping the best score. States live in byte
    /// blocks rented through <see cref="SearchStorage"/> and are addressed by per-seat offsets; the
    /// parallel metadata arrays remain fixed-size chunks. Exceeding <c>maxCapacity</c> is a
    /// capacity rejection, while actual bytes are bounded by the search-memory ledger.
    /// </summary>
    private sealed partial class Pool
    {
        private readonly int _maxCapacity;
        private readonly CancellationToken _cancellationToken;
        private readonly Action? _checkpoint;
        private readonly Action? _retentionObserver;
        private int _auxSeats;
        public readonly record struct PoolLabel(
            int PotionCount, int PotionStrategicCost, int FutureSoldHp,
            int CumulativePlayerHpLost, int ActionCount, double Score);

        private static bool Dominates(in PoolLabel left, in PoolLabel right)
            => left.PotionCount <= right.PotionCount
                && left.PotionStrategicCost <= right.PotionStrategicCost
                && left.FutureSoldHp <= right.FutureSoldHp
                && left.CumulativePlayerHpLost <= right.CumulativePlayerHpLost
                && left.ActionCount <= right.ActionCount
                && left.Score >= right.Score;

        private readonly PackedStateArena _states;
        private readonly ChunkedArray<List<PoolLabel>?> _labels;
        private readonly ChunkedArray<int> _nodes;
        private readonly ChunkedArray<ulong> _lineages;
        private readonly ChunkedArray<double> _scores;
        private readonly ChunkedArray<double> _ranks;
        private readonly ChunkedArray<ulong> _hashes;
        private readonly ChunkedArray<RetentionFacts> _facts;
        private readonly ChunkedArray<int> _potionCosts;
        private readonly ChunkedArray<int> _commitment;
        private int[] _table;
        private int[] _order;
        private int[] _rankOrder;
        private int[] _rankPosition;
        private bool[] _taken;
        private bool[] _required;
        private readonly List<int> _selected = new();
        private readonly List<int> _requiredScratch = new();
        private readonly Dictionary<ulong, LineageLeaders> _lineageLeaders = new();
        private readonly List<ulong> _lineageOrder = new();
        private readonly Dictionary<int, PotionLeaders> _potionLeaders = new();
        private readonly List<int> _potionCountOrder = new();
        private readonly Dictionary<(int Potion, PersistentSetupTraits Traits), TraitLeaders> _traitLeaders = new();
        private readonly List<(int Potion, PersistentSetupTraits Traits)> _traitOrder = new();
        private readonly Dictionary<(int Potion, uint? Target), FocusLeaders> _focusLeaders = new();
        private readonly List<(int Potion, uint? Target)> _focusOrder = new();
        private readonly Scorer _scorer;
        private readonly bool _preserveTheft;
        private readonly bool _secondRankBand;
        private readonly int _secondRankBandWidth;
        private readonly bool _aggressivePowerCommitment;
        private readonly bool _trackPowerCommitments;
        private readonly ChunkedList<PowerCommitmentState> _commitmentStates;
        private readonly RouteNodeStore _routeNodes;
        private readonly ChunkedList<RoutingSignature> _routingSignatures;
        private readonly SearchStorage _storage;
        private readonly bool _checkPotionQuota;
        private readonly int _initialActionCount;
        private readonly bool _preserveReplayAllocatorOpening;
        private readonly int _policyWidth;
        private readonly BeamTrace? _trace;

        /// <summary>The solver phase label used by the offline trace (only read when <see cref="_trace"/> is set).</summary>
        public string TracePhase { get; set; } = "next";
        private readonly HashSet<int> _potionReservations = new();
        private readonly Dictionary<int, PotionLineageLeaders> _potionLineageGroups = new();
        private readonly List<int> _potionLineageOrder = new();
        private const int RoutingChoiceLimit = 96;
        private readonly record struct RoutingFamilyKey(int Turn, int SourceId, int Effect, int Pile);
        private readonly record struct RoutingOptionKey(int CardId, int Upgrade, int StateKey, int CardCount);
        private sealed class RoutingGroup
        {
            public readonly List<int> Nodes = new();
            public int BestScore = -1;
            public int BestOffense = -1;
            public int BestDefense = -1;
            public int BestSetup = -1;
            public int BestPileOrder = -1;
            public int MinParentRank = int.MaxValue;
            public double MaxParentScore = double.NegativeInfinity;
            public double MaxBeamRank = double.NegativeInfinity;
        }
        private sealed class RoutingBucket
        {
            public readonly List<int> Groups = new();
            public int MinParentRank = int.MaxValue;
            public double MaxParentScore = double.NegativeInfinity;
            public double MaxBeamRank = double.NegativeInfinity;
        }
        private readonly Dictionary<int, RoutingGroup> _routingGroups = new();
        private readonly List<int> _routingGroupOrder = new();
        private readonly Dictionary<RoutingFamilyKey, RoutingBucket> _routingFamilies = new();
        private readonly List<RoutingFamilyKey> _routingFamilyOrder = new();
        private readonly Dictionary<RoutingOptionKey, RoutingBucket> _routingOptions = new();
        private readonly List<RoutingOptionKey> _routingOptionOrder = new();
        private readonly List<int> _routingChoices = new();
        private readonly record struct ParetoCohortKey(
            ulong EnemyCombatFirst, ulong EnemyCombatSecond,
            ulong EnemyControlFirst, ulong EnemyControlSecond,
            ulong UnorderedPileFirst, ulong UnorderedPileSecond);
        private readonly Dictionary<ParetoCohortKey, List<int>> _paretoCohorts = new();
        private readonly List<int> _pareto = new(3);
        private readonly List<int> _standPatGroup = new();
        private readonly Dictionary<int, List<int>> _standPatGroups = new();
        private readonly record struct StandPatEvaluation(
            bool AllEnemiesDead, int DelayedDamage, int ProjectedPlayerHp, int ResourceValue,
            bool Available);
        private readonly Dictionary<CommitmentGroupKey, CommitmentGroupLeader> _commitmentGroups = new();
        private readonly List<CommitmentGroupKey> _commitmentGroupOrder = new();
        private readonly List<int> _powerRepresentatives = new();
        private bool[] _representative;
        private bool _hasCommitments;

        public Pool(int initialCapacity, int maxCapacity, bool preserveTheft, Scorer scorer,
            bool secondRankBand, int secondRankBandWidth,
            bool aggressivePowerCommitment, PowerCommitmentCatalog? powerCommitments,
            ChunkedList<PowerCommitmentState> commitmentStates, RouteNodeStore routeNodes,
            ChunkedList<RoutingSignature> routingSignatures, bool checkPotionQuota, int initialActionCount,
            bool preserveReplayAllocatorOpening, BeamTrace? trace, SearchStorage storage,
            CancellationToken cancellationToken = default,
            Action? checkpoint = null, Action? retentionObserver = null)
        {
            _maxCapacity = maxCapacity;
            _cancellationToken = cancellationToken;
            _checkpoint = checkpoint;
            _retentionObserver = retentionObserver;
            _preserveTheft = preserveTheft;
            _scorer = scorer;
            _secondRankBand = secondRankBand;
            _secondRankBandWidth = secondRankBandWidth;
            _aggressivePowerCommitment = aggressivePowerCommitment;
            _trackPowerCommitments = powerCommitments is { Rules.Length: > 0 };
            _commitmentStates = commitmentStates;
            _routeNodes = routeNodes;
            _routingSignatures = routingSignatures;
            _storage = storage;
            _checkPotionQuota = checkPotionQuota;
            _initialActionCount = initialActionCount;
            _preserveReplayAllocatorOpening = preserveReplayAllocatorOpening;
            _trace = trace;
            _policyWidth = secondRankBandWidth > 0 ? secondRankBandWidth : maxCapacity;
            _states = new PackedStateArena(maxCapacity, storage);
            _labels = new ChunkedArray<List<PoolLabel>?>(maxCapacity, storage);
            _nodes = new ChunkedArray<int>(maxCapacity, storage);
            _lineages = new ChunkedArray<ulong>(maxCapacity, storage);
            _scores = new ChunkedArray<double>(maxCapacity, storage);
            _ranks = new ChunkedArray<double>(maxCapacity, storage);
            _hashes = new ChunkedArray<ulong>(maxCapacity, storage);
            _facts = new ChunkedArray<RetentionFacts>(maxCapacity, storage);
            _potionCosts = new ChunkedArray<int>(maxCapacity, storage);
            _commitment = new ChunkedArray<int>(maxCapacity, storage);
            // Sorting needs contiguous arrays. Start at the expected seat count and grow them when
            // the pool is actually selected.
            int scratchCapacity = Math.Clamp(initialCapacity, 1, maxCapacity);
            _order = storage.RentArray<int>(scratchCapacity);
            _rankOrder = storage.RentArray<int>(scratchCapacity);
            _rankPosition = storage.RentArray<int>(scratchCapacity);
            _taken = storage.RentArray<bool>(scratchCapacity);
            _required = storage.RentArray<bool>(scratchCapacity);
            _representative = storage.RentArray<bool>(scratchCapacity);
            _table = storage.RentArray<int>(TableSize(Math.Clamp(initialCapacity, 1, maxCapacity)));
            // Selection dictionaries, representative lists and the per-state multi-objective label
            // lists grow with the seat count. They are bounded by maxCapacity entries; charge a
            // conservative fixed allowance so the hard budget cannot be crossed by unmeasured growth.
            _auxSeats = scratchCapacity;
            storage.Memory.Charge((long)_auxSeats * PoolAuxiliaryBytesPerSeat, "pool-aux");
        }

        private void EnsureScratch(int count)
        {
            if (count <= _order.Length)
                return;
            int capacity = Math.Min(_maxCapacity, Math.Max(count, checked(_order.Length * 2)));
            _storage.Memory.Charge((long)(capacity - _auxSeats) * PoolAuxiliaryBytesPerSeat, "pool-aux");
            _auxSeats = capacity;
            // Allocate first: a rejected allocation leaves the existing pool usable for fallback.
            int[] order = _storage.RentArray<int>(capacity);
            int[] rankOrder = _storage.RentArray<int>(capacity);
            int[] rankPosition = _storage.RentArray<int>(capacity);
            bool[] taken = _storage.RentArray<bool>(capacity);
            bool[] required = _storage.RentArray<bool>(capacity);
            bool[] representative = _storage.RentArray<bool>(capacity);
            _storage.DiscardArray(_order);
            _storage.DiscardArray(_rankOrder);
            _storage.DiscardArray(_rankPosition);
            _storage.DiscardArray(_taken);
            _storage.DiscardArray(_required);
            _storage.DiscardArray(_representative);
            _order = order;
            _rankOrder = rankOrder;
            _rankPosition = rankPosition;
            _taken = taken;
            _required = required;
            _representative = representative;
        }

        /// <summary>Upper bound per seat for the pool's small selection structures.</summary>
        private const int PoolAuxiliaryBytesPerSeat = 128;

        /// <summary>Charged per stored multi-objective label; sizeof(PoolLabel) is 28 B.</summary>
        private const int PoolLabelBytes = 64;

        /// <summary>Charged when the first label of a state creates its small list.</summary>
        private const int PoolLabelListBytes = 256;

        private static int TableSize(int capacity)
        {
            int size = 1;
            while (size < capacity * 2)
                size <<= 1;
            return size;
        }

        private int FreeSlot(ulong hash, int mask)
        {
            int slot = (int)(hash & (ulong)mask);
            while (_table[slot] != 0)
                slot = (slot + 1) & mask;
            return slot;
        }

        private void GrowTable()
        {
            int[] table = _storage.RentArray<int>(_table.Length * 2);
            int mask = table.Length - 1;
            for (int index = 0; index < Count; index++)
            {
                ulong hash = _hashes[index];
                int slot = (int)(hash & (ulong)mask);
                while (table[slot] != 0)
                    slot = (slot + 1) & mask;
                table[slot] = index + 1;
            }
            _table = table;
        }

        public int Count { get; private set; }
        public int PeakCount { get; private set; }

        public ref readonly Battle State(int index) => ref _states[index];

        public int Node(int index) => _nodes[index];

        /// <summary>Appends this pool's live route-node indexes to a mark-sweep root list.</summary>
        public void AppendNodes(List<int> roots)
        {
            for (int index = 0; index < Count; index++)
                roots.Add(_nodes[index]);
        }

        /// <summary>Rewrites this pool's route-node indexes through a boundary cleanup map.</summary>
        public void RemapNodes(int[] map)
        {
            for (int index = 0; index < Count; index++)
            {
                int node = _nodes[index];
                if ((uint)node >= (uint)map.Length || map[node] < 0)
                    throw new InvalidOperationException($"局面池路线节点未被清理标记：{node}。");
                _nodes[index] = map[node];
            }
        }

        public double Score(int index) => _scores[index];

        public double Rank(int index) => _ranks[index];

        public int RankPosition(int index) => _rankPosition[index];

        public int PotionCost(int index) => _potionCosts[index];

        public RetentionFacts Facts(int index) => _facts[index];

        /// <summary>Moves one retained entry's offset/metadata to a lower slot; used by incumbent compaction.</summary>
        public void Move(int from, int to)
        {
            _states.Move(from, to);
            _labels[to] = _labels[from];
            _nodes[to] = _nodes[from];
            _lineages[to] = _lineages[from];
            _scores[to] = _scores[from];
            _ranks[to] = _ranks[from];
            _hashes[to] = _hashes[from];
            _facts[to] = _facts[from];
            _potionCosts[to] = _potionCosts[from];
            _commitment[to] = _commitment[from];
        }

        /// <summary>Drops the tail after an incumbent bound; removed seats are not refilled.</summary>
        public void Truncate(int count)
        {
            for (int index = count; index < Count; index++) _labels[index] = null;
            Count = count;
            _states.Truncate(count);
            RebuildTable();
        }

        private void RebuildTable()
        {
            Array.Clear(_table);
            int mask = _table.Length - 1;
            for (int index = 0; index < Count; index++)
                _table[FreeSlot(_hashes[index], mask)] = index + 1;
        }

        public void Clear()
        {
            for (int index = 0; index < Count; index++)
                _labels[index] = null;
            Count = 0;
            _states.Clear();
            _hasCommitments = false;
            Array.Clear(_table);
        }

        /// <summary>The pool has no free seat; the next unique state needs a compaction or a drop.</summary>
        public bool IsFull => Count >= _maxCapacity;

        /// <summary>
        /// Reserves the arena tail for building one child in place before <see cref="CommitReserved"/>.
        /// The block tail always keeps a complete <see cref="Battle"/> of room, so the child has the
        /// full workspace capacity. A duplicate or a terminal child never advances the tail. Any
        /// other call on this pool invalidates the returned reference.
        /// </summary>
        public ref Battle Reserve()
        {
            // The per-action-layer pool reaches this bound only through a choice/commitment
            // expansion that multiplies one parent action into many children. Each unique child that
            // cannot get a seat is dropped and counted as a capacity boundary; the cumulative
            // end-turn pool is compacted in AddEndTurnChild instead.
            if (Count >= _maxCapacity)
                throw new CapacityException("Beam.Pool", _maxCapacity);
            _states.Reserve();
            return ref _states.ReservedState;
        }

        /// <summary>Adds the state built in the reserved slot; returns false when an identical state was already present.</summary>
        public bool CommitReserved(int node, double score, double rank, int potionCost,
            in ScoreFeatures features, int depth, ulong lineage, int commitment,
            SearchRouteTraits traits, int pocketwatchThreshold, int pocketwatchPlayed,
            int pocketwatchLastTurn, int parentEnemyBlock, int parentEnemyHp, int parentFutureResourceValue,
            int parentStrategicResourcePotential)
        {
            return Insert(ref _states.ReservedState, inPlace: true,
                node, score, rank, potionCost, features, default, factsReady: false, depth,
                lineage, commitment, traits, pocketwatchThreshold, pocketwatchPlayed,
                pocketwatchLastTurn, parentEnemyBlock, parentEnemyHp, parentFutureResourceValue,
                parentStrategicResourcePotential);
        }

        /// <summary>Adds a scored state and computes its retention-only values when it is stored.</summary>
        public bool AddScored(in Battle state, int node, double score, double rank,
            int potionCost, in ScoreFeatures features, int depth, ulong lineage,
            int commitment, SearchRouteTraits traits, int pocketwatchThreshold, int pocketwatchPlayed,
            int pocketwatchLastTurn, int parentEnemyBlock, int parentEnemyHp, int parentFutureResourceValue,
            int parentStrategicResourcePotential)
            => Insert(ref Unsafe.AsRef(in state), inPlace: false,
                node, score, rank, potionCost, features, default, factsReady: false, depth,
                lineage, commitment, traits, pocketwatchThreshold, pocketwatchPlayed,
                pocketwatchLastTurn, parentEnemyBlock, parentEnemyHp, parentFutureResourceValue,
                parentStrategicResourcePotential);

        /// <summary>Adds a state whose retention facts are already computed (pool-to-pool copies).</summary>
        public bool Add(in Battle state, int node, double score, double rank,
            int potionCost, in RetentionFacts facts, ulong lineage, int commitment,
            List<PoolLabel>? labels = null)
        {
            bool added = Insert(ref Unsafe.AsRef(in state), inPlace: false,
                node, score, rank, potionCost, default, facts, factsReady: true, facts.Depth,
                lineage, commitment, facts.Traits, facts.PocketwatchCardThreshold,
                facts.PocketwatchCardsPlayedThisTurn, facts.PocketwatchCardsPlayedLastTurn, 0, 0, 0, 0);
            if (added && labels is { Count: > 0 })
            {
                _labels[Count - 1] = new List<PoolLabel>(labels);
                _storage.Memory.Charge(labels.Count * (long)PoolLabelBytes, "pool-labels");
            }
            return added;
        }

        /// <summary>
        /// Appends an entry copied from an already-deduplicated pool. The source hash travels with
        /// the record; the destination is empty, so no hash or equality probe is needed.
        /// </summary>
        private void AddCopiedFrom(Pool source, int index)
        {
            if (Count >= _maxCapacity)
                throw new CapacityException("Beam.Pool", _maxCapacity);
            if ((Count + 1) * 2 > _table.Length)
                GrowTable();
            ulong hash = source._hashes[index];
            _nodes[Count] = source._nodes[index];
            _lineages[Count] = source._lineages[index];
            _scores[Count] = source._scores[index];
            _ranks[Count] = source._ranks[index];
            _hashes[Count] = hash;
            _facts[Count] = source._facts[index];
            _potionCosts[Count] = source._potionCosts[index];
            _commitment[Count] = source._commitment[index];
            _hasCommitments |= source._commitment[index] > 0;
            _states.Add(source._states[index]);
            _table[FreeSlot(hash, _table.Length - 1)] = Count + 1;
            Count++;
            PeakCount = Math.Max(PeakCount, Count);
            if (source._labels[index] is { Count: > 0 } labels)
            {
                _labels[Count - 1] = new List<PoolLabel>(labels);
                _storage.Memory.Charge(labels.Count * (long)PoolLabelBytes, "pool-labels");
            }
        }

        private TraceRouteInfo TraceInfo(int node, int poolSlot, in PoolLabel label)
        {
            RouteNodeStore.RouteNodeView route = _routeNodes.View(node);
            RetentionFacts facts = _facts[poolSlot];
            return new TraceRouteInfo(
                route.RootActionKey, route.Lineage, route.Commitment, _potionCosts[poolSlot],
                facts.PotionCount, route.PotionId, route.RoutingSignatureId,
                label.PotionCount, label.PotionStrategicCost, label.FutureSoldHp,
                label.CumulativePlayerHpLost, label.ActionCount, label.Score,
                facts.SetupValue, facts.SetupComboValue,
                facts.EnemyHp, facts.ProjectedPlayerHp, facts.Turn, facts.Depth);
        }

        private TraceRouteInfo TargetTraceInfo(int node, int potionCost, in ScoreFeatures features,
            int depth, double score, in PoolLabel label, int setupValue, int setupCombo)
        {
            RouteNodeStore.RouteNodeView route = _routeNodes.View(node);
            return new TraceRouteInfo(
                route.RootActionKey, route.Lineage, route.Commitment, potionCost,
                features.PotionUseCount, route.PotionId, route.RoutingSignatureId,
                label.PotionCount, label.PotionStrategicCost, label.FutureSoldHp,
                label.CumulativePlayerHpLost, label.ActionCount, label.Score,
                setupValue, setupCombo,
                features.EnemyHp, features.ProjectedPlayerHp, features.Turn, depth);
        }

        private TraceSetupLeader SetupLeaderInfo(int slot)
            => new(_nodes[slot], _facts[slot].SetupValue, _facts[slot].SetupComboValue,
                _facts[slot].RetainedAttackValue, _facts[slot].ProjectedPlayerHp, _scores[slot],
                _rankPosition[slot], _required[slot]);

        private void TraceSetupLeader(string lane, int leaderSlot)
        {
            if (_trace == null || leaderSlot < 0)
                return;
            for (int slot = 0; slot < Count; slot++)
            {
                if (!_trace.IsWatched(_nodes[slot]))
                    continue;
                _trace.SetupLeader(lane, SetupLeaderInfo(slot), SetupLeaderInfo(leaderSlot),
                    IsBetterSetupCombo(_facts[slot], _facts[leaderSlot]),
                    "SetupComboValue>RetainedAttackValue>ProjectedHp>Score");
                return;
            }
        }

        private bool Insert(ref Battle state, bool inPlace, int node, double score, double rank,
            int potionCost, in ScoreFeatures features, in RetentionFacts knownFacts,
            bool factsReady, int depth, ulong lineage, int commitment,
            SearchRouteTraits traits, int pocketwatchThreshold, int pocketwatchPlayed,
            int pocketwatchLastTurn, int parentEnemyBlock, int parentEnemyHp, int parentFutureResourceValue,
            int parentStrategicResourcePotential)
        {
            ulong hash = Fingerprint.Hash(state);
            int mask = _table.Length - 1;
            int slot = (int)(hash & (ulong)mask);
            while (_table[slot] != 0)
            {
                int existing = _table[slot] - 1;
                if (_hashes[existing] == hash && Fingerprint.Equal(_states[existing], state))
                {
                    int existingNode = _nodes[existing];
                    PoolLabel nextLabel = new(features.PotionUseCount, features.PotionStrategicCost,
                        _facts[existing].FutureSoldHp, features.CumulativePlayerHpLost, depth, score);
                    List<PoolLabel>? labelList = _labels[existing];
                    if (labelList == null)
                    {
                        labelList =
                        [
                            new PoolLabel(_facts[existing].PotionCount, _facts[existing].PotionStrategicCost,
                                _facts[existing].FutureSoldHp, _facts[existing].CumulativePlayerHpLost,
                                _facts[existing].Depth, _scores[existing]),
                        ];
                        _labels[existing] = labelList;
                        _storage.Memory.Charge(PoolLabelListBytes, "pool-labels");
                    }
                    List<PoolLabel> labels = labelList;
                    bool dominated = false;
                    for (int labelIndex = 0; labelIndex < labels.Count; labelIndex++)
                    {
                        if (Dominates(labels[labelIndex], nextLabel))
                        {
                            dominated = true;
                            break;
                        }
                    }
                    if (dominated)
                    {
                        if (_trace != null && !factsReady)
                        {
                            TraceRouteInfo targetInfo = TargetTraceInfo(node, potionCost, features,
                                depth, score, nextLabel, _facts[existing].SetupValue, _facts[existing].SetupComboValue);
                            TraceRouteInfo existingInfo = TraceInfo(existingNode, existing, labels[0]);
                            _trace.NodeInserted(node, added: false, existingNode, incomingWon: false,
                                rank, _ranks[existing], score, _scores[existing], _facts[existing].PotionCount,
                                _facts[existing].Turn, _facts[existing].Depth,
                                _facts[existing].EnemyHp, _facts[existing].ProjectedPlayerHp,
                                targetInfo, existingInfo);
                        }
                        return false;
                    }
                    labels.RemoveAll(current => Dominates(nextLabel, current));
                    labels.Add(nextLabel);
                    _storage.Memory.Charge(PoolLabelBytes, "pool-labels");
                    bool incomingWon = score > _scores[existing]
                        || score.Equals(_scores[existing]) && depth < _facts[existing].Depth;
                    if (incomingWon)
                    {
                        _scores[existing] = score;
                        _ranks[existing] = rank;
                        _nodes[existing] = node;
                        _potionCosts[existing] = potionCost;
                        _facts[existing] = factsReady
                            ? knownFacts
                            // The byte-identical state has the same retention-only hand values; only
                            // the depth-dependent fields move with the better-ranked representative.
                            : RetentionFacts.From(features, depth,
                                _facts[existing].ReachableHandValue, _facts[existing].ZeroCostPlayableCount,
                                traits, pocketwatchThreshold, pocketwatchPlayed, pocketwatchLastTurn,
                                _scorer.InitialRetainedAttackValue, parentEnemyBlock, parentEnemyHp,
                                parentFutureResourceValue, parentStrategicResourcePotential,
                                _facts[existing].RetentionProjection());
                        _lineages[existing] = lineage;
                        _commitment[existing] = commitment;
                        _hasCommitments |= commitment > 0;
                    }
                    if (_trace != null && !factsReady)
                    {
                        TraceRouteInfo targetInfo = TargetTraceInfo(node, potionCost, features,
                            depth, score, nextLabel, _facts[existing].SetupValue, _facts[existing].SetupComboValue);
                        TraceRouteInfo existingInfo = TraceInfo(existingNode, existing, labels[0]);
                        _trace.NodeInserted(node, added: false, existingNode, incomingWon,
                            rank, _ranks[existing], score, _scores[existing], _facts[existing].PotionCount,
                            _facts[existing].Turn, _facts[existing].Depth,
                            _facts[existing].EnemyHp, _facts[existing].ProjectedPlayerHp,
                            targetInfo, existingInfo);
                    }
                    _states.DiscardReserved();
                    return false;
                }
                slot = (slot + 1) & mask;
            }
            // The capacity bound is checked only after the state is known to be unique: a duplicate or a
            // terminal child built in the scratch slot never needs a seat.
            if (Count >= _maxCapacity)
                throw new CapacityException("Beam.Pool", _maxCapacity);
            RetentionFacts facts = knownFacts;
            if (!factsReady)
            {
                (int reachableHandValue, int zeroCostPlayableCount, _) =
                    _scorer.ReachableHandPotential(state);
                facts = RetentionFacts.From(
                    features, depth, reachableHandValue, zeroCostPlayableCount,
                    traits, pocketwatchThreshold, pocketwatchPlayed, pocketwatchLastTurn,
                    _scorer.InitialRetainedAttackValue, parentEnemyBlock, parentEnemyHp,
                    parentFutureResourceValue, parentStrategicResourcePotential,
                    _scorer.CaptureRetentionValues(state));
            }
            if ((Count + 1) * 2 > _table.Length)
                GrowTable();
            _nodes[Count] = node;
            _lineages[Count] = lineage;
            _scores[Count] = score;
            _ranks[Count] = rank;
            _hashes[Count] = hash;
            _facts[Count] = facts;
            _potionCosts[Count] = potionCost;
            _commitment[Count] = commitment;
            _hasCommitments |= commitment > 0;
            if (inPlace)
                _states.CommitReserved();
            else
                _states.Add(in state);
            _table[FreeSlot(hash, _table.Length - 1)] = Count + 1;
            Count++;
            PeakCount = Math.Max(PeakCount, Count);
            if (_trace != null && !factsReady)
            {
                TraceRouteInfo targetInfo = TargetTraceInfo(node, potionCost, features,
                    depth, score, new PoolLabel(features.PotionUseCount, features.PotionStrategicCost,
                        facts.FutureSoldHp, features.CumulativePlayerHpLost, depth, score),
                    facts.SetupValue, facts.SetupComboValue);
                _trace.NodeInserted(node, added: true, -1, false, rank, 0, score, 0, 0, 0, 0, 0, 0,
                    targetInfo, default);
            }
            return true;
        }

    }
}
