// Per-parent expansion split into a pure calculation half and a shared-state commit half.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>
    /// The route information a parent node had fixed before this layer started. The expansion half
    /// reads only this snapshot; the commit half is the only place that reads the live route stores.
    /// </summary>
    private readonly record struct ParentExpansionContext(
        int Node,
        int Depth,
        RetentionFacts Facts,
        int Rank,
        double Score,
        bool HasCommitment,
        PowerCommitmentState Commitment,
        int BrightestFlameSpent,
        TurnProgressState Progress,
        EnemyProgressHighs EnemyHighs,
        CrossTurnProgress CrossTurn);

    private enum ExpansionEventKind : byte
    {
        BeginAction,
        ChoiceHeader,
        ChoiceEnd,
        Routing,
        Child,
        ExecutionDrop,
        BranchDrop,
        SuccessorActionEnd,
        SuccessorEnd,
    }

    /// <summary>
    /// One ordered step of a parent expansion. Child records point at the large
    /// <see cref="ChildRecord"/> array; routing/drop events stay small so the hot buffer does not
    /// carry a score snapshot for control steps.
    /// </summary>
    private struct ExpansionEvent
    {
        public ExpansionEventKind Kind;
        public int ChildIndex;
        public int RoutingIndex;
        public string? DropRegion;
    }

    private struct FetchedRecord
    {
        public ScoreFeatures Features;
        public RetentionFacts PoolFacts;
        public RetentionFacts ParentFacts;
    }

    private struct RoutingEvent
    {
        public RoutingSignature Signature;
        public int Turn;
        public int ChoiceDepth;
        public int ParentRank;
        public double ParentScore;
        public int ParentEnergy;
    }

    /// <summary>
    /// One child's routing context. Buffered records carry the value across chunk boundaries;
    /// straight-through expansion resolves it before the child is emitted.
    /// </summary>
    private readonly record struct RoutingRef(RoutingEvent Value, int ResolvedId, RoutingMeta Meta,
        bool HasRouting);

    private struct ChildRecord
    {
        public PlayerAction Action;
        public int StateOffset;
        public int StateLength;
        public ulong Hash;
        public double Score;
        public double Rank;
        public int PotionCost;
        public ScoreFeatures Features;
        public RetentionFacts Facts;
        public SearchRouteTraits Traits;
        public int PocketwatchThreshold;
        public int PocketwatchPlayed;
        public int PocketwatchLastTurn;
        public int Depth;
        public ushort UsedPotion;
        public int ActionCardId;
        public int TargetCombatId;
        public bool Terminal;
        public bool ToEnded;
        public bool FetchedCandidate;
        public int FetchedIndex;
        public bool HasCommitment;
        public PowerCommitmentState Commitment;
        public RoutingRef Routing;
        public bool FetchedParent;
        public TurnProgressState Progress;
        public EnemyProgressHighs ProgressEnemyHighs;
        public bool ProgressEnemyHighsChanged;
    }

    /// <summary>
    /// Reusable, bounded chunk buffer. Arrays grow by doubling within the workspace limit;
    /// a full chunk is committed before its arrays are reused for the next part of the parent.
    /// Choice recursion remains on the producer's stack while publishing a chunk.
    /// </summary>
    private sealed class ExpansionWorkspace
    {
        private const int InitialEventCapacity = 1024;
        private const int InitialChildCapacity = 256;
        private const int InitialRoutingCapacity = 64;
        private const int InitialStateBytes = 256 * 1024;
        private static readonly FetchedRecord EmptyFetched = default;
        private static readonly Battle EmptyBattle = default;

        private readonly BeamSolver _solver;
        private readonly PlayerAction[] _actions = new PlayerAction[Kernel.MaxLegalActions];
        private readonly PlayerAction[] _commitmentActions = new PlayerAction[Kernel.MaxLegalActions];

        private ExpansionEvent[] _events = [];
        private ChildRecord[] _children = [];
        private int[] _brightestFlameSpent = [];
        private RoutingEvent[] _routings = [];
        private FetchedRecord[] _fetched = [];
        private byte[] _stateBytes = [];
        private int _eventCount;
        private int _childCount;
        private int _routingCount;
        private int _fetchedCount;
        private int _stateBytesUsed;
        private int _growableBytes;
        private readonly int _workspaceBufferLimit;
        private readonly bool _directOnly;
        private bool _direct;
        private bool _parentDirect;
        private ChildRecord _directChild;
        private Pool _directNext = null!;
        private Pool _directEnded = null!;
        private TerminalSink _directTerminal = null!;
        private long _parentPeakUsedBytes;
        private long _parentPeakAllocatedBytes;
        private int _parentChildren;
        private int _parentTerminals;
        private int _parentChoices;

        private Battle _draft;
        private Battle _scratch;
        private Battle _fetchedState;
        private Battle _committedFetchedState;
        private RetentionFacts _committedFetchedFacts;
        private int _committedFetchedNode;
        private int _skipChoiceDepth;
        private bool _skipSuccessors;
        private Func<int, bool, bool>? _publishChunk;
        private int _parentIndex;
        private bool _searchThread;
        internal int PublishedChunks { get; private set; }

        private sealed class ChunkStoppedException : Exception;

        private int MinimumChunkBytes => Battle.ExpectedSize + Unsafe.SizeOf<ChildRecord>()
            + Unsafe.SizeOf<ExpansionEvent>() + Unsafe.SizeOf<RoutingEvent>()
            + Unsafe.SizeOf<FetchedRecord>() + (_solver.TracksBrightestFlameSpent ? sizeof(int) : 0);

        private EndTurnPrefix _endTurnPrefix;
        private int _generation;
        private int _endTurnPrefixAttemptGeneration = -1;
        private int _endTurnPrefixAttemptNode = -1;
        private int _endTurnPrefixGeneration = -1;
        private int _endTurnPrefixNode = -1;
        private bool _endTurnPrefixValid;

        internal long PlayDeduped;
        internal long PlayVerifiedExact;
        internal long PlayVerifiedApprox;
        internal long PlayVerifiedApproxEqual;
        internal long PlayVerifiedUnresolved;
        internal long ChoiceByClassRequests;
        internal long ChoiceVerifiedExact;
        internal long ChoiceVerifiedApprox;
        internal long ChoiceVerifiedApproxEqual;
        internal long ChoiceVerifiedUnresolved;
        internal int EndTurnPrefixShared;
        internal int EndTurnPrefixUnshared;
        internal int EndTurnPrefixVerified;
        internal int EndTurnPrefixReusedLeaves;
        internal int EndTurnPrefixChoiceUnshared;
        internal int EndTurnPrefixExtraTurnUnshared;
        internal int EndTurnPrefixNotInProgress;
        internal int TurnLoopGuarded;
        internal int CrossTurnNoProgressPruned;

        /// <summary>The current parent's turn-start record; constant for the whole parent.</summary>
        private CrossTurnProgress _parentCrossTurn;

        internal ExpansionWorkspace(BeamSolver solver, bool directOnly = false)
        {
            _solver = solver;
            _workspaceBufferLimit = solver._workspaceBufferLimit;
            _directOnly = directOnly;
            if (directOnly || !SupportsBufferedExpansion)
                return;
            // Keep room for one complete record in every array even under a small budget.
            ReserveInitial(ref _events, 1);
            ReserveInitial(ref _children, 1);
            if (solver.TracksBrightestFlameSpent)
                ReserveInitial(ref _brightestFlameSpent, 1);
            ReserveInitial(ref _routings, 1);
            ReserveInitial(ref _fetched, 1);
            ReserveInitial(ref _stateBytes, Battle.ExpectedSize);
            ReserveInitial(ref _events, InitialEventCapacity);
            ReserveInitial(ref _children, InitialChildCapacity);
            if (solver.TracksBrightestFlameSpent)
                ReserveInitial(ref _brightestFlameSpent, InitialChildCapacity);
            ReserveInitial(ref _routings, InitialRoutingCapacity);
            ReserveInitial(ref _fetched, 16);
            ReserveInitial(ref _stateBytes, InitialStateBytes);
        }

        internal static long FixedBytes => 2L * Kernel.MaxLegalActions * Unsafe.SizeOf<PlayerAction>()
            + 4L * Unsafe.SizeOf<Battle>() + Unsafe.SizeOf<EndTurnPrefix>() + 4096;

        internal long GrowableBytes => _growableBytes;
        internal long ParentPeakUsedBytes => _parentPeakUsedBytes;
        internal long ParentPeakAllocatedBytes => _parentPeakAllocatedBytes;
        internal int ParentChildren => _parentChildren;
        internal int ParentTerminals => _parentTerminals;
        internal int ParentChoices => _parentChoices;
        internal bool ParentDirect => _parentDirect;

        private void ReserveInitial<T>(ref T[] array, int target)
        {
            int elementBytes = Unsafe.SizeOf<T>();
            long other = _growableBytes - (long)array.Length * elementBytes;
            long maxLength = Math.Max(0, (_workspaceBufferLimit - other) / elementBytes);
            int capacity = (int)Math.Min(target, maxLength);
            if (capacity <= array.Length)
                return;
            _growableBytes += (capacity - array.Length) * elementBytes;
            array = new T[capacity];
        }

        private bool TryGrowArray<T>(ref T[] array, int requiredLength)
        {
            if (requiredLength <= array.Length)
                return true;
            int elementBytes = Unsafe.SizeOf<T>();
            long other = _growableBytes - (long)array.Length * elementBytes;
            long maxLength = Math.Max(0, (_workspaceBufferLimit - other) / elementBytes);
            if (maxLength < requiredLength)
                return false;
            long target = Math.Max(requiredLength,
                Math.Min((long)array.Length * 2, maxLength));
            int capacity = (int)target;
            _growableBytes += (capacity - array.Length) * elementBytes;
            Array.Resize(ref array, capacity);
            NoteGrowableUse();
            return true;
        }

        private bool TryGrowBytes(ref byte[] array, int requiredLength)
        {
            if (requiredLength <= array.Length)
                return true;
            long other = _growableBytes - array.Length;
            long maxLength = Math.Max(0, (long)_workspaceBufferLimit - other);
            if (maxLength < requiredLength)
                return false;
            long target = Math.Max(requiredLength, Math.Min((long)array.Length * 2, maxLength));
            int capacity = (int)target;
            _growableBytes += capacity - array.Length;
            Array.Resize(ref array, capacity);
            NoteGrowableUse();
            return true;
        }

        private void NoteGrowableUse()
        {
            if (ParentWorkspaceObserver is null)
                return;
            if (_growableBytes > _parentPeakAllocatedBytes)
                _parentPeakAllocatedBytes = _growableBytes;
            long used = _stateBytesUsed
                + (long)_childCount * Unsafe.SizeOf<ChildRecord>()
                + (_solver.TracksBrightestFlameSpent ? (long)_childCount * Unsafe.SizeOf<int>() : 0)
                + (long)_eventCount * Unsafe.SizeOf<ExpansionEvent>()
                + (long)_routingCount * Unsafe.SizeOf<RoutingEvent>()
                + (long)_fetchedCount * Unsafe.SizeOf<FetchedRecord>();
            if (used > _parentPeakUsedBytes)
                _parentPeakUsedBytes = used;
        }


        /// <summary>
        /// Bytes retained by this workspace's fixed buffers and grown arrays. Diagnostic only; the
        /// workspace is covered by the up-front reservation in the search-memory ledger.
        /// </summary>
        internal long RetainedBytes
        {
            get
            {
                long bytes = (long)_actions.Length * Unsafe.SizeOf<PlayerAction>()
                    + (long)_commitmentActions.Length * Unsafe.SizeOf<PlayerAction>()
                    + (long)_events.Length * Unsafe.SizeOf<ExpansionEvent>()
                    + (long)_children.Length * Unsafe.SizeOf<ChildRecord>()
                    + (long)_brightestFlameSpent.Length * Unsafe.SizeOf<int>()
                    + (long)_routings.Length * Unsafe.SizeOf<RoutingEvent>()
                    + (long)_fetched.Length * Unsafe.SizeOf<FetchedRecord>()
                    + _stateBytes.Length
                    + 4L * Unsafe.SizeOf<Battle>()
                    + Unsafe.SizeOf<EndTurnPrefix>();
                return bytes;
            }
        }

        internal SearchEquivalenceDiagnostics EquivalenceDiagnostics() => new(
            PlayDeduped, PlayVerifiedExact, PlayVerifiedApprox, PlayVerifiedApproxEqual,
            PlayVerifiedUnresolved, ChoiceByClassRequests, ChoiceVerifiedExact,
            ChoiceVerifiedApprox, ChoiceVerifiedApproxEqual, ChoiceVerifiedUnresolved);

        /// <summary>Clears one parent's task diagnostics after they are committed or discarded.</summary>
        internal void ResetDiagnostics()
        {
            PlayDeduped = 0;
            PlayVerifiedExact = 0;
            PlayVerifiedApprox = 0;
            PlayVerifiedApproxEqual = 0;
            PlayVerifiedUnresolved = 0;
            ChoiceByClassRequests = 0;
            ChoiceVerifiedExact = 0;
            ChoiceVerifiedApprox = 0;
            ChoiceVerifiedApproxEqual = 0;
            ChoiceVerifiedUnresolved = 0;
            EndTurnPrefixShared = 0;
            EndTurnPrefixUnshared = 0;
            EndTurnPrefixVerified = 0;
            EndTurnPrefixReusedLeaves = 0;
            EndTurnPrefixChoiceUnshared = 0;
            EndTurnPrefixExtraTurnUnshared = 0;
            EndTurnPrefixNotInProgress = 0;
            TurnLoopGuarded = 0;
            CrossTurnNoProgressPruned = 0;
        }

        /// <summary>Starts one parent: drop the previous parent's records and advance the prefix generation.</summary>
        internal void BeginParent()
        {
            _eventCount = 0;
            _childCount = 0;
            _routingCount = 0;
            _fetchedCount = 0;
            _stateBytesUsed = 0;
            _skipChoiceDepth = 0;
            _skipSuccessors = false;
            PublishedChunks = 0;
            _parentPeakUsedBytes = 0;
            _parentPeakAllocatedBytes = _growableBytes;
            _parentChildren = 0;
            _parentTerminals = 0;
            _parentChoices = 0;
            _direct = false;
            _parentDirect = false;
            _generation++;
        }

        private bool EmitBeginAction()
        {
            if (!_direct)
            {
                AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.BeginAction });
                return true;
            }
            return !(_solver._options.UnlimitedNodes
                && _solver._solveWatch is { } watch && _solver.OutOfBudget(watch));
        }

        private bool EmitChoiceHeader()
        {
            _parentChoices++;
            if (!_direct)
            {
                AddEvent(new ExpansionEvent
                {
                    Kind = ExpansionEventKind.ChoiceHeader,
                });
                return true;
            }
            try
            {
                _directNext.Reserve();
            }
            catch (CapacityException ex)
            {
                _solver.RecordCapacityDrop(ex);
                return false;
            }
            _solver._transitions++;
            return true;
        }

        private void EmitChoiceEnd()
        {
            if (!_direct)
                AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.ChoiceEnd });
        }

        private RoutingRef EmitRouting(in RoutingEvent value)
        {
            if (!_direct)
            {
                AddRoutingEvent(value);
                return new RoutingRef(value, 0, default, true);
            }
            return ResolveRouting(in value);
        }

        private void EmitExecutionDrop(string region)
        {
            if (!_direct)
            {
                AddEvent(new ExpansionEvent
                {
                    Kind = ExpansionEventKind.ExecutionDrop,
                    DropRegion = region,
                });
                return;
            }
            try
            {
                _directNext.Reserve();
            }
            catch (CapacityException ex)
            {
                _solver.RecordCapacityDrop(ex);
                return;
            }
            _solver._transitions++;
            _solver.RecordCapacityDrop(region);
        }

        private void EmitBranchDrop(string region)
        {
            if (!_direct)
            {
                AddEvent(new ExpansionEvent
                {
                    Kind = ExpansionEventKind.BranchDrop,
                    DropRegion = region,
                });
                return;
            }
            _solver.RecordCapacityDrop(region);
        }

        private bool EmitSuccessorActionEnd()
        {
            if (!_direct)
            {
                AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.SuccessorActionEnd });
                return true;
            }
            return _solver.CommitmentBudgetAvailable();
        }

        private void EmitSuccessorEnd()
        {
            if (!_direct)
                AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.SuccessorEnd });
        }

        private ref ChildRecord PrepareChild(out int childIndex)
        {
            if (_direct)
            {
                childIndex = 0;
                _parentChildren++;
                return ref _directChild;
            }
            childIndex = _childCount;
            ref ChildRecord child = ref AddChild();
            _parentChildren++;
            return ref child;
        }

        private ChildEmitResult EmitChild(in ChildRecord child, int childIndex,
            in Battle directState, in RoutingRef routing, int parentNode, in Battle parentState,
            in RetentionFacts parentFacts, in FetchedRecord fetched, int brightestFlameSpentDelta)
        {
            if (child.Terminal)
                _parentTerminals++;
            if (!_direct)
            {
                if (_solver.TracksBrightestFlameSpent)
                    _brightestFlameSpent[childIndex] = brightestFlameSpentDelta;
                AddEvent(new ExpansionEvent
                {
                    Kind = ExpansionEventKind.Child,
                    ChildIndex = childIndex,
                });
                return new ChildEmitResult(-1, false, child.Progress);
            }
            ChildCommitResult result = CommitChildCore(in child, in directState, in routing,
                parentNode, in parentState, parentFacts, in fetched, brightestFlameSpentDelta,
                _directNext, _directEnded, _directTerminal);
            return new ChildEmitResult(result.NodeId, result.ExpandSuccessors, result.Progress);
        }

        private readonly record struct ChildEmitResult(int NodeId,
            bool ExpandSuccessors, TurnProgressState Progress);

        /// <summary>Whether the limit holds one complete record, including a maximum-size state.</summary>
        internal bool SupportsBufferedExpansion
            => _workspaceBufferLimit >= MinimumChunkBytes;

        /// <summary>
        /// Produces ordered chunks. Publishing pauses this producer until the search thread has
        /// consumed the chunk; the recursive choice cursor stays on this worker's bounded stack.
        /// </summary>
        internal void Expand(in Battle parent, in ParentExpansionContext context,
            int parentIndex, bool searchThread, Func<int, bool, bool> publishChunk,
            CancellationToken cancellationToken)
        {
            BeginParent();
            _publishChunk = publishChunk;
            _parentIndex = parentIndex;
            _searchThread = searchThread;
            try
            {
                ExpandBody(in parent, in context, cancellationToken);
            }
            catch (ChunkStoppedException)
            {
                // The ordered consumer stopped at its normal search budget boundary.
            }
            finally
            {
                _publishChunk = null;
            }
        }

        private void FlushChunk()
        {
            PublishedChunks++;
            if (!_publishChunk!(_parentIndex, _searchThread))
                throw new ChunkStoppedException();
            _eventCount = 0;
            _childCount = 0;
            _routingCount = 0;
            _fetchedCount = 0;
            _stateBytesUsed = 0;
        }

        // Reserve an entire output record before writing any of it. Flushing halfway through
        // AppendState/PrepareChild would leave a child referring to a previous chunk's bytes.
        private void ReserveRecord(int stateBytes = 0, int children = 0, int fetched = 0,
            int routings = 0)
        {
            if (!TryGrowRecord(stateBytes, children, fetched, routings))
                FlushChunk();
            // Every empty chunk already owns space for one maximum-size atomic record.
        }

        private bool TryGrowRecord(int stateBytes, int children, int fetched, int routings)
            => TryGrowArray(ref _events, _eventCount + 1)
                && TryGrowArray(ref _children, _childCount + children)
                && (!_solver.TracksBrightestFlameSpent
                    || TryGrowArray(ref _brightestFlameSpent, _childCount + children))
                && TryGrowArray(ref _fetched, _fetchedCount + fetched)
                && TryGrowArray(ref _routings, _routingCount + routings)
                && TryGrowBytes(ref _stateBytes, _stateBytesUsed + stateBytes);

        /// <summary>
        /// Straight-through half. The same expansion code emits each step to the shared commit logic
        /// immediately; only fixed scratch buffers are used.
        /// </summary>
        internal void ExpandDirect(in Battle parent, in ParentExpansionContext context,
            Pool next, Pool ended, TerminalSink terminal, CancellationToken cancellationToken)
        {
            BeginParent();
            _direct = true;
            _parentDirect = true;
            _directNext = next;
            _directEnded = ended;
            _directTerminal = terminal;
            try
            {
                ExpandBody(in parent, in context, cancellationToken);
            }
            finally
            {
                _direct = false;
                _directNext = null!;
                _directEnded = null!;
                _directTerminal = null!;
            }
        }

        private void ExpandBody(in Battle parent, in ParentExpansionContext context,
            CancellationToken cancellationToken)
        {
            _parentCrossTurn = context.CrossTurn;
            int count = Kernel.LegalActions(parent, _solver._rules, _actions);
            scoped ReadOnlySpan<PlayerAction> legal = _actions.AsSpan(0, count);
            for (int actionIndex = 0; actionIndex < count; actionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!EmitBeginAction())
                    return;
                ref readonly PlayerAction action = ref legal[actionIndex];
                if (action.Kind == PlayerActionKind.UsePotion
                    && !_solver.PotionAllowed(parent, action.PotionSlot))
                {
                    continue;
                }
                if (_solver._dedupePlayActions
                    && TryFindEarlierEquivalentPlay(in parent, legal, actionIndex,
                        out int representative, out bool exact)
                    && exact)
                {
                    PlayDeduped++;
                    if (_solver._verifySearchEquivalence)
                    {
                        _solver.VerifySkippedPlay(in parent, legal[representative], action, exact, this);
                    }
                    continue;
                }
                ExpandAction(in parent, false, context.Node, context.Depth, context.Facts,
                    context.Rank, context.Score, context.HasCommitment, context.Commitment,
                    context.BrightestFlameSpent, context.Progress, context.EnemyHighs,
                    action, null, default, default,
                    allowFetchedPowerCommit: true, cancellationToken);
            }
        }

        /// <summary>
        /// One action invocation. Choice recursion re-executes the same action from the same parent
        /// with more selections, exactly like the previous one-child-at-a-time expansion.
        /// </summary>
        private void ExpandAction(in Battle parent, bool fetchedParent, int parentNodeKey,
            int parentDepth, in RetentionFacts parentFacts, int parentRank, double parentScore,
            bool hasParentCommitment, in PowerCommitmentState parentCommitment,
            int parentBrightestFlameSpent, in TurnProgressState parentProgress,
            in EnemyProgressHighs parentEnemyHighs,
            PlayerAction action, ChoiceEffect? primaryEffect, scoped ReadOnlySpan<Card> selectedPowers,
            in RoutingRef routing, bool allowFetchedPowerCommit, CancellationToken cancellationToken)
        {
            ushort usedPotion = ushort.MaxValue;
            if (action.Kind == PlayerActionKind.UsePotion
                && (uint)action.PotionSlot < (uint)parent.PotionSlotCount
                && parent.Potions[action.PotionSlot] != 0)
            {
                usedPotion = (ushort)(parent.Potions[action.PotionSlot] - 1);
            }
            try
            {
                StepResult stepResult;
                ChoiceRequest request;
                bool historyRisk;
                int actionBrightestFlameSpent;
                if (action.Kind == PlayerActionKind.EndTurn && CanReuseEndTurnPrefix(parentNodeKey))
                {
                    EndTurnPrefixReusedLeaves++;
                    stepResult = Kernel.StepEndTurnFromPrefix(ref _draft, action, _solver._rules,
                        in _endTurnPrefix, out request, out historyRisk,
                        out actionBrightestFlameSpent);
                    if (_solver._verifySearchEquivalence)
                    {
                        _solver.VerifyPreparedEndTurn(in parent, action, ref _draft, stepResult,
                            in request, historyRisk, this);
                    }
                }
                else
                {
                    Battle.CopyPacked(in parent, ref _draft);
                    if (action.Kind == PlayerActionKind.EndTurn && IsFirstEndTurnLeaf(parentNodeKey))
                    {
                        stepResult = Kernel.StepEndTurnSharingPrefix(ref _draft, action, _solver._rules,
                            out request, out historyRisk, ref _endTurnPrefix,
                            out actionBrightestFlameSpent);
                        StoreEndTurnPrefix(parentNodeKey);
                    }
                    else
                    {
                        stepResult = Kernel.Step(ref _draft, action, _solver._rules,
                            out request, out historyRisk, out _, out actionBrightestFlameSpent);
                    }
                }
                if (stepResult == StepResult.NeedsChoice)
                {
                    ExpandChoice(in parent, fetchedParent, parentNodeKey, parentDepth, parentFacts,
                        parentRank, parentScore, hasParentCommitment, parentCommitment,
                        parentBrightestFlameSpent, parentProgress, parentEnemyHighs, action,
                        primaryEffect, selectedPowers, in routing, allowFetchedPowerCommit,
                        in request, cancellationToken);
                    return;
                }
                int spentAfterAction = 0;
                if (_solver.TracksBrightestFlameSpent)
                {
                    spentAfterAction = checked(parentBrightestFlameSpent + actionBrightestFlameSpent);
                    if (!GrowthCostPolicy.AllowsBrightestFlame(
                            _solver._options.BrightestFlameMaxHpLossLimit,
                            _solver._options.InitialBrightestFlameMaxHpSpent, spentAfterAction))
                    {
                        return;
                    }
                }

                int depth = parentDepth + 1;
                bool isTerminal = !Kernel.IsInProgress(_draft);
                TurnProgressEvaluation progress = _solver.EvaluateTurnProgress(
                    in parent, in parentProgress, in parentEnemyHighs, in _draft, action);
                if (!isTerminal && action.Kind == PlayerActionKind.PlayCard
                    && !progress.HasProgress
                    && parentProgress.ConsecutiveNoProgress >= MaxConsecutiveNoProgressActions)
                {
                    TurnLoopGuarded++;
                    _solver._trace?.ChildBlocked(parentNodeKey, in action,
                        "turn-loop-no-progress");
                    return;
                }
                (double score, double rank, _, int potionCost, ScoreFeatures features) =
                    _solver.Score(in _draft, depth, isTerminal, action.Kind == PlayerActionKind.EndTurn);
                if (!isTerminal
                    && (action.Kind == PlayerActionKind.EndTurn || _draft.PlayerTurn != parent.PlayerTurn)
                    && _solver.CrossTurnNoProgressExceeded(in _parentCrossTurn, in _draft, in features))
                {
                    CrossTurnNoProgressPruned++;
                    _solver._trace?.ChildBlocked(parentNodeKey, in action, "cross-turn-no-progress");
                    return;
                }
                bool nonPure = !IsPureTransition(in parent, in _draft, action, _solver._rules, historyRisk);
                SearchRouteTraits childTraits = ClassifyRouteTraits(parentFacts, features, action,
                    DeclinedExtraTurn(in parent, _solver._rules), nonPure);
                _solver.PocketwatchState(in _draft, out int childPocketwatchThreshold,
                    out int childPocketwatchPlayed, out int childPocketwatchLastTurn);

                bool hasCommitment = false;
                PowerCommitmentState commitment = default;
                if (!isTerminal)
                {
                    PowerCommitmentState? computed = _solver.ComputePowerCommitment(
                        hasParentCommitment, in parentCommitment, in parent, in _draft, action,
                        parentFacts, features, depth);
                    if (computed is { } value)
                    {
                        hasCommitment = true;
                        commitment = value;
                    }
                }

                bool fetchedCandidate = !isTerminal && allowFetchedPowerCommit
                    && _solver._options.Act3BossStrategy
                    && _draft.PlayerTurn == parent.PlayerTurn
                    && primaryEffect == ChoiceEffect.MoveToHand
                    && _solver.HasPlayableFetchedPower(in _draft, selectedPowers);
                bool toEnded = action.Kind == PlayerActionKind.EndTurn
                    || _draft.PlayerTurn != parent.PlayerTurn;

                if (fetchedCandidate)
                {
                    Battle.CopyPacked(in _draft, ref _fetchedState);
                    ScoreFeatures fetchedFeatures = _solver._scorer.Features(in _fetchedState, depth,
                        includeRetentionOnly: true);
                    (int fetchedReachable, int fetchedZeroCost, _) =
                        _solver._scorer.ReachableHandPotential(in _fetchedState);
                    RetentionFacts fetchedParentFacts = RetentionFacts.From(fetchedFeatures, depth,
                        fetchedReachable, fetchedZeroCost, childTraits, childPocketwatchThreshold,
                        childPocketwatchPlayed, childPocketwatchLastTurn,
                        _solver._scorer.InitialRetainedAttackValue, parentFacts.EnemyBlock,
                        parentFacts.EnemyHp, parentFacts.FutureResourceValue,
                        parentFacts.StrategicResourcePotential);
                    RetentionFacts fetchedPoolFacts = RetentionFacts.From(fetchedFeatures, depth,
                        fetchedReachable, fetchedZeroCost, childTraits, childPocketwatchThreshold,
                        childPocketwatchPlayed, childPocketwatchLastTurn,
                        _solver._scorer.InitialRetainedAttackValue, parentFacts.EnemyBlock,
                        parentFacts.EnemyHp, parentFacts.FutureResourceValue,
                        parentFacts.StrategicResourcePotential,
                        _solver._scorer.CaptureRetentionValues(in _fetchedState));
                    FetchedRecord fetched = new()
                    {
                        Features = fetchedFeatures,
                        PoolFacts = fetchedPoolFacts,
                        ParentFacts = fetchedParentFacts,
                    };
                    int fetchedIndex = -1;
                    int fetchedOffset = 0;
                    int fetchedLength = 0;
                    if (!_direct)
                    {
                        ReserveRecord(Battle.PackedSize(in _fetchedState), children: 1, fetched: 1);
                        fetchedIndex = AddFetched(fetched);
                        fetchedOffset = AppendState(in _fetchedState, out fetchedLength);
                    }
                    ref ChildRecord fetchedChild = ref PrepareChild(out int fetchedChildIndex);
                    fetchedChild.Action = action;
                    fetchedChild.StateOffset = fetchedOffset;
                    fetchedChild.StateLength = fetchedLength;
                    fetchedChild.Hash = Fingerprint.Hash(in _fetchedState);
                    fetchedChild.Score = score;
                    fetchedChild.Rank = rank;
                    fetchedChild.PotionCost = potionCost;
                    fetchedChild.Features = features;
                    fetchedChild.Facts = BuildFacts(features, depth, childTraits,
                        childPocketwatchThreshold, childPocketwatchPlayed,
                        childPocketwatchLastTurn, parentFacts, in _fetchedState);
                    fetchedChild.Traits = childTraits;
                    fetchedChild.PocketwatchThreshold = childPocketwatchThreshold;
                    fetchedChild.PocketwatchPlayed = childPocketwatchPlayed;
                    fetchedChild.PocketwatchLastTurn = childPocketwatchLastTurn;
                    fetchedChild.Depth = depth;
                    fetchedChild.UsedPotion = usedPotion;
                    fetchedChild.ActionCardId = action.Kind == PlayerActionKind.PlayCard
                        && action.HandIndex < parent.HandCount
                            ? parent.Cards[action.HandIndex].Id : 0;
                    fetchedChild.TargetCombatId = TargetCombatId(in parent, action);
                    fetchedChild.Terminal = false;
                    fetchedChild.ToEnded = toEnded;
                    fetchedChild.FetchedCandidate = true;
                    fetchedChild.FetchedIndex = fetchedIndex;
                    fetchedChild.HasCommitment = hasCommitment;
                    fetchedChild.Commitment = commitment;
                    fetchedChild.Routing = routing;
                    fetchedChild.FetchedParent = fetchedParent;
                    fetchedChild.Progress = progress.State;
                    fetchedChild.ProgressEnemyHighs = progress.EnemyHighs;
                    fetchedChild.ProgressEnemyHighsChanged = progress.EnemyHighsChanged;
                    ChildEmitResult emitted = EmitChild(in fetchedChild, fetchedChildIndex,
                        in _fetchedState, in routing, parentNodeKey, in parent, parentFacts,
                        in fetched, actionBrightestFlameSpent);
                    if (_direct)
                    {
                        if (emitted.ExpandSuccessors)
                        {
                            TurnProgressState emittedProgress = emitted.Progress;
                            EnemyProgressHighs emittedHighs = progress.EnemyHighs;
                            ExpandFetchedPowerSuccessors(in _fetchedState, emitted.NodeId,
                                depth, fetchedParentFacts, parentRank, parentScore,
                                hasCommitment, commitment, in emittedProgress,
                                in emittedHighs, selectedPowers, spentAfterAction,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        TurnProgressState bufferedProgress = progress.State;
                        EnemyProgressHighs bufferedHighs = progress.EnemyHighs;
                        ExpandFetchedPowerSuccessors(in _fetchedState, -1,
                            depth, fetchedParentFacts, parentRank, parentScore, hasCommitment,
                            commitment, in bufferedProgress, in bufferedHighs, selectedPowers,
                            spentAfterAction, cancellationToken);
                    }
                    return;
                }

                int stateOffset = 0;
                int stateLength = 0;
                if (!_direct)
                {
                    ReserveRecord(Battle.PackedSize(in _draft), children: 1);
                    stateOffset = AppendState(in _draft, out stateLength);
                }
                ref ChildRecord child = ref PrepareChild(out int childIndex);
                child.Action = action;
                child.StateOffset = stateOffset;
                child.StateLength = stateLength;
                child.Hash = isTerminal ? 0 : Fingerprint.Hash(in _draft);
                child.Score = score;
                child.Rank = rank;
                child.PotionCost = potionCost;
                child.Features = features;
                child.Facts = isTerminal
                    ? default
                    : BuildFacts(features, depth, childTraits, childPocketwatchThreshold,
                        childPocketwatchPlayed, childPocketwatchLastTurn, parentFacts, in _draft);
                child.Traits = childTraits;
                child.PocketwatchThreshold = childPocketwatchThreshold;
                child.PocketwatchPlayed = childPocketwatchPlayed;
                child.PocketwatchLastTurn = childPocketwatchLastTurn;
                child.Depth = depth;
                child.UsedPotion = usedPotion;
                child.ActionCardId = action.Kind == PlayerActionKind.PlayCard
                    && action.HandIndex < parent.HandCount ? parent.Cards[action.HandIndex].Id : 0;
                child.TargetCombatId = TargetCombatId(in parent, action);
                child.Terminal = isTerminal;
                child.ToEnded = toEnded;
                child.FetchedCandidate = false;
                child.FetchedIndex = -1;
                child.HasCommitment = hasCommitment;
                child.Commitment = commitment;
                child.Routing = routing;
                child.FetchedParent = fetchedParent;
                child.Progress = progress.State;
                child.ProgressEnemyHighs = progress.EnemyHighs;
                child.ProgressEnemyHighsChanged = progress.EnemyHighsChanged;
                EmitChild(in child, childIndex, in _draft, in routing, parentNodeKey, in parent,
                    parentFacts, in EmptyFetched, actionBrightestFlameSpent);
            }
            catch (CapacityException ex)
            {
                EmitExecutionDrop(ex.Region);
            }
        }

        private void ExpandChoice(in Battle parent, bool fetchedParent, int parentNodeKey,
            int parentDepth, in RetentionFacts parentFacts, int parentRank, double parentScore,
            bool hasParentCommitment, in PowerCommitmentState parentCommitment,
            int parentBrightestFlameSpent, in TurnProgressState parentProgress,
            in EnemyProgressHighs parentEnemyHighs, PlayerAction action,
            ChoiceEffect? primaryEffect, scoped ReadOnlySpan<Card> selectedPowers, in RoutingRef routing,
            bool allowFetchedPowerCommit, in ChoiceRequest request, CancellationToken cancellationToken)
        {
            Span<Card> optionCards = stackalloc Card[request.OptionCount];
            for (int option = 0; option < request.OptionCount; option++)
            {
                if (request.OptionsAreGenerated)
                {
                    optionCards[option] = request.GeneratedOptions[option];
                    continue;
                }
                int position = request.Options[option];
                if ((uint)position >= (uint)_draft.CardCount)
                {
                    throw new InvalidOperationException(
                        $"选择选项 {option} 的卡牌位置 {position} 超出当前牌数 {_draft.CardCount}。");
                }
                optionCards[option] = _draft.Cards[position];
            }
            Span<ulong> selections = stackalloc ulong[MaxChoiceBranches];
            int count;
            if (!_solver._dedupeChoicesByClass)
            {
                count = ChoiceEnumerator.Enumerate(in request, selections);
            }
            else if (RemovalChoicePriority.Applies(in request)
                && ChoiceEnumerator.Count(in request, RemovalChoicePriority.Branches + 1)
                    > RemovalChoicePriority.Branches)
            {
                Span<int> priority = stackalloc int[request.OptionCount];
                for (int option = 0; option < request.OptionCount; option++)
                    priority[option] = RemovalChoicePriority.Of(in optionCards[option], _solver._rules);
                count = ChoiceEnumerator.EnumerateRemoval(in request, optionCards, priority,
                    selections[..RemovalChoicePriority.Branches]);
            }
            else if (_solver._choiceFillVariants)
            {
                bool truncated = ChoiceEnumerator.Count(in request, MaxChoiceBranches + 1)
                    > MaxChoiceBranches;
                count = truncated
                    ? ChoiceEnumerator.EnumerateByClassFilled(in request, optionCards, selections)
                    : ChoiceEnumerator.Enumerate(in request, selections);
            }
            else
            {
                count = ChoiceEnumerator.EnumerateByClass(in request, optionCards, selections);
            }
            if (_solver._dedupeChoicesByClass)
            {
                ChoiceByClassRequests++;
                if (_solver._verifySearchEquivalence)
                {
                    _solver.VerifyChoiceAlternatives(in parent, in _draft, action, in request,
                        optionCards, selections[..count], this);
                }
            }
            bool captureFetchedPowers = allowFetchedPowerCommit && primaryEffect is null
                && request.Timing == ChoiceTiming.Action
                && _solver.IsPrimaryMoveToHandAction(in parent, action)
                && request.Effect == ChoiceEffect.MoveToHand
                && !request.OptionsAreGenerated;
            Span<bool> optionIsPower = stackalloc bool[captureFetchedPowers ? request.OptionCount : 0];
            for (int option = 0; option < optionIsPower.Length; option++)
            {
                optionIsPower[option] =
                    Kernel.ResolveCardType(in _draft, _solver._rules, optionCards[option]) == CardType.Power;
            }
            if (!EmitChoiceHeader())
            {
                EmitChoiceEnd();
                return;
            }
            Span<Card> capturedPowers = stackalloc Card[Limits.MaxChoiceOptions];
            for (int index = 0; index < count; index++)
            {
                ChoiceEffect? effect = primaryEffect;
                scoped ReadOnlySpan<Card> chosenPowers = selectedPowers;
                if (captureFetchedPowers)
                {
                    try
                    {
                        effect = request.Effect;
                        int captured = 0;
                        ulong bits = selections[index];
                        for (int option = 0; option < request.OptionCount; option++)
                        {
                            if ((bits & (1UL << option)) == 0 || !optionIsPower[option])
                                continue;
                            if (captured >= capturedPowers.Length)
                                throw new CapacityException("FetchedPowers", capturedPowers.Length);
                            capturedPowers[captured++] = optionCards[option];
                        }
                        chosenPowers = capturedPowers[..captured];
                    }
                    catch (CapacityException ex)
                    {
                        // The capture sits before the per-selection guard in the old flow, so it
                        // aborts the remaining selections without another reserve or transition.
                        EmitBranchDrop(ex.Region);
                        EmitChoiceEnd();
                        return;
                    }
                }
                RoutingRef branchRouting = routing;
                if (TryBuildRoutingSignature(in parent, action, in request, optionCards,
                        selections[index], parentDepth + 1, parentFacts.Turn, parentRank, parentScore,
                        parentFacts, out RoutingSignature signature, out int turn, out int choiceDepth,
                        out int choiceParentRank, out double choiceParentScore, out int choiceParentEnergy))
                {
                    branchRouting = EmitRouting(new RoutingEvent
                    {
                        Signature = signature,
                        Turn = turn,
                        ChoiceDepth = choiceDepth,
                        ParentRank = choiceParentRank,
                        ParentScore = choiceParentScore,
                        ParentEnergy = choiceParentEnergy,
                    });
                }
                PlayerAction withChoice;
                try
                {
                    withChoice = action.WithChoice(selections[index]);
                }
                catch (CapacityException ex)
                {
                    EmitBranchDrop(ex.Region);
                    continue;
                }
                ExpandAction(in parent, fetchedParent, parentNodeKey, parentDepth, parentFacts,
                    parentRank, parentScore, hasParentCommitment, parentCommitment,
                    parentBrightestFlameSpent, in parentProgress, in parentEnemyHighs, withChoice,
                    effect, chosenPowers, in branchRouting, allowFetchedPowerCommit,
                    cancellationToken);
            }
            EmitChoiceEnd();
        }

        private void ExpandFetchedPowerSuccessors(in Battle state, int fetchedNode,
            int depth, in RetentionFacts fetchedFacts, int parentRank,
            double parentScore, bool hasCommitment, in PowerCommitmentState commitment,
            in TurnProgressState fetchedProgress, in EnemyProgressHighs fetchedEnemyHighs,
            scoped ReadOnlySpan<Card> selectedPowers, int fetchedBrightestFlameSpent,
            CancellationToken cancellationToken)
        {
            _generation++;
            int count = Kernel.LegalActions(in state, _solver._rules, _commitmentActions);
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlayerAction action = _commitmentActions[index];
                if (action.Kind != PlayerActionKind.PlayCard
                    || !_solver.IsSelectedFetchedPower(in state, action.HandIndex, selectedPowers))
                {
                    continue;
                }
                ExpandAction(in state, true, fetchedNode, depth, fetchedFacts,
                    parentRank, parentScore, hasCommitment, in commitment,
                    fetchedBrightestFlameSpent, in fetchedProgress, in fetchedEnemyHighs,
                    action, null, default, default, allowFetchedPowerCommit: false,
                    cancellationToken);
                if (!EmitSuccessorActionEnd())
                    return;
            }
            EmitSuccessorEnd();
        }

        private RetentionFacts BuildFacts(in ScoreFeatures features, int depth,
            SearchRouteTraits traits, int pocketwatchThreshold, int pocketwatchPlayed,
            int pocketwatchLastTurn, in RetentionFacts parentFacts, in Battle state)
        {
            (int reachableHandValue, int zeroCostPlayableCount, _) =
                _solver._scorer.ReachableHandPotential(in state);
            return RetentionFacts.From(features, depth, reachableHandValue, zeroCostPlayableCount,
                traits, pocketwatchThreshold, pocketwatchPlayed, pocketwatchLastTurn,
                _solver._scorer.InitialRetainedAttackValue, parentFacts.EnemyBlock, parentFacts.EnemyHp,
                parentFacts.FutureResourceValue, parentFacts.StrategicResourcePotential,
                _solver._scorer.CaptureRetentionValues(in state));
        }

        /// <summary>
        /// Consumes one chunk in order. Choice rejection and fetched-power continuation state
        /// survive chunk boundaries; neither requires a future event offset in the same array.
        /// </summary>
        internal bool Commit(in Battle parent, in ParentExpansionContext context, Pool next,
            Pool ended, TerminalSink terminal)
        {
            for (int index = 0; index < _eventCount; index++)
            {
                ExpansionEvent record = _events[index];
                if (_skipChoiceDepth > 0)
                {
                    if (record.Kind == ExpansionEventKind.ChoiceHeader) _skipChoiceDepth++;
                    if (record.Kind == ExpansionEventKind.ChoiceEnd) _skipChoiceDepth--;
                    continue;
                }
                if (_skipSuccessors)
                {
                    if (record.Kind == ExpansionEventKind.SuccessorEnd) _skipSuccessors = false;
                    continue;
                }
                switch (record.Kind)
                {
                    case ExpansionEventKind.BeginAction:
                        if (_solver._options.UnlimitedNodes && _solver._solveWatch is { } watch
                            && _solver.OutOfBudget(watch))
                            return false;
                        break;
                    case ExpansionEventKind.ChoiceHeader:
                        try { next.Reserve(); }
                        catch (CapacityException ex)
                        {
                            _solver.RecordCapacityDrop(ex);
                            _skipChoiceDepth = 1;
                            break;
                        }
                        _solver._transitions++;
                        break;
                    case ExpansionEventKind.Routing:
                        ResolveRouting(in _routings[record.RoutingIndex]);
                        break;
                    case ExpansionEventKind.ExecutionDrop:
                        try { next.Reserve(); }
                        catch (CapacityException ex)
                        {
                            _solver.RecordCapacityDrop(ex);
                            break;
                        }
                        _solver._transitions++;
                        _solver.RecordCapacityDrop(record.DropRegion!);
                        break;
                    case ExpansionEventKind.BranchDrop:
                        _solver.RecordCapacityDrop(record.DropRegion!);
                        break;
                    case ExpansionEventKind.SuccessorActionEnd:
                        _skipSuccessors = !_solver.CommitmentBudgetAvailable();
                        break;
                    case ExpansionEventKind.Child:
                        CommitChild(record.ChildIndex, in parent, in context, next, ended, terminal);
                        break;
                    case ExpansionEventKind.ChoiceEnd:
                    case ExpansionEventKind.SuccessorEnd:
                        break;
                    default:
                        throw new InvalidOperationException($"未知的展开记录类型 {record.Kind}。");
                }
            }
            return true;
        }

        private void CommitChild(int childIndex, in Battle parent,
            in ParentExpansionContext context, Pool next, Pool ended, TerminalSink terminal)
        {
            ref readonly ChildRecord child = ref _children[childIndex];
            int parentNode = child.FetchedParent ? _committedFetchedNode : context.Node;
            ref readonly Battle parentState = ref child.FetchedParent
                ? ref _committedFetchedState : ref parent;
            RetentionFacts parentFacts = child.FetchedParent ? _committedFetchedFacts : context.Facts;
            RoutingRef routing = child.Routing.HasRouting ? ResolveRouting(child.Routing.Value) : default;
            int spent = _solver.TracksBrightestFlameSpent ? _brightestFlameSpent[childIndex] : 0;
            ref readonly FetchedRecord fetched = ref child.FetchedCandidate
                ? ref _fetched[child.FetchedIndex] : ref EmptyFetched;
            ChildCommitResult result = CommitChildCore(in child, in EmptyBattle, in routing,
                parentNode, in parentState, parentFacts, in fetched, spent, next, ended, terminal);
            if (child.FetchedCandidate)
            {
                _skipSuccessors = !result.ExpandSuccessors;
                if (result.ExpandSuccessors)
                {
                    _committedFetchedNode = result.NodeId;
                    _committedFetchedFacts = fetched.ParentFacts;
                }
            }
        }

        /// <summary>
        /// Creates and inserts one child. Both buffered commit and straight-through expansion call
        /// this method, so route nodes, routing metadata, pool insertion and capacity handling have
        /// exactly one implementation.
        /// </summary>
        private ChildCommitResult CommitChildCore(in ChildRecord child, in Battle directState,
            in RoutingRef routing, int parentNode, in Battle parentState, in RetentionFacts parentFacts,
            in FetchedRecord fetched, int brightestFlameSpentDelta, Pool next, Pool ended,
            TerminalSink terminal)
        {
            if (!next.TryReserve())
            {
                _solver.RecordCapacityDrop("Beam.Pool");
                return new ChildCommitResult(-1, false, default);
            }
            _solver._transitions++;

            TurnProgressState parentProgress = _solver._nodes.ProgressAt(parentNode);
            int progressEnemyHighsId = parentProgress.EnemyHighsId;
            if (child.ProgressEnemyHighsChanged)
            {
                progressEnemyHighsId = EnemyProgressHighsEmpty(in child.ProgressEnemyHighs)
                    ? 0
                    : _solver._nodes.AddProgressEnemyHighs(in child.ProgressEnemyHighs);
            }
            TurnProgressState progress =
                child.Progress with { EnemyHighsId = progressEnemyHighsId };
            int traceStep = _solver._trace?.Match(parentNode, child.Action) ?? 0;
            int node = _solver._nodes.Count;
            ulong lineage = _solver.NextLineage(parentNode, in parentState, child.Action, node,
                out ulong rootActionKey, out ushort firstCardId, out int firstCardTarget);
            int parentMetaId = _solver._nodes.RoutingMetaIdAt(parentNode);
            int routingSignature = routing.HasRouting ? routing.ResolvedId : 0;
            RoutingMeta routingMeta = routing.HasRouting ? routing.Meta : default;
            int routingMetaId;
            if (routingSignature != 0)
            {
                routingMetaId = _solver._routingMetas.Add(routingMeta);
            }
            else
            {
                RoutingMeta parentMeta = _solver._routingMetas[parentMetaId];
                if (parentMeta.SignatureId == 0)
                {
                    routingMeta = parentMeta;
                    routingMetaId = parentMetaId;
                }
                else
                {
                    routingMeta = parentMeta with { SignatureId = 0 };
                    routingMetaId = _solver._routingMetas.Add(routingMeta);
                }
            }
            int potionLineageKey = child.Action.Kind == PlayerActionKind.UsePotion
                ? _solver._potionLineageKeys.Extend(_solver._nodes.PotionLineageKeyAt(parentNode),
                    child.UsedPotion)
                : _solver._nodes.PotionLineageKeyAt(parentNode);
            int commitment = 0;
            if (child.HasCommitment)
            {
                _solver._commitments.Add(child.Commitment);
                commitment = _solver._commitments.Count;
            }
            int brightestFlameSpent = 0;
            if (_solver.TracksBrightestFlameSpent)
            {
                brightestFlameSpent = checked(
                    _solver._nodes.BrightestFlameSpentAt(parentNode) + brightestFlameSpentDelta);
            }
            _solver._nodes.Add(new RouteNode(parentNode, child.Depth, child.Action, rootActionKey,
                lineage, child.UsedPotion, firstCardId, firstCardTarget, commitment,
                child.ActionCardId, child.TargetCombatId, routingMeta.SignatureId, routingMeta.Turn,
                routingMeta.ChoiceDepth, routingMeta.ParentRank, routingMeta.ParentScore,
                routingMeta.ParentEnergy, parentFacts.Turn, routingMetaId, potionLineageKey,
                progress, _solver._nodes.CrossTurnIdAt(parentNode)),
                routingMetaId, brightestFlameSpent);
            if (traceStep > 0)
                _solver._trace!.NodeCreated(node, traceStep);

            if (child.Terminal)
            {
                CopyChildState(in child, in directState, ref _scratch);
                terminal(in _scratch, node, in child.Features, child.Score);
                return new ChildCommitResult(node, false, progress);
            }

            if (child.FetchedCandidate && _solver.CommitmentBudgetAvailable())
            {
                if (!_direct)
                    CopyRecordState(in child, ref _committedFetchedState);
                CopyChildState(in child, in directState, ref next.ReservedState);
                bool added;
                try
                {
                    added = next.CommitReserved(node, child.Score, child.Rank, child.PotionCost,
                        in fetched.Features, child.Depth, lineage, commitment, child.Traits,
                        child.PocketwatchThreshold, child.PocketwatchPlayed, child.PocketwatchLastTurn,
                        parentFacts.EnemyBlock, parentFacts.EnemyHp,
                        parentFacts.FutureResourceValue, parentFacts.StrategicResourcePotential,
                        child.Hash, in fetched.PoolFacts);
                }
                catch (CapacityException ex)
                {
                    _solver.RecordCapacityDrop(ex);
                    return new ChildCommitResult(node, false, progress);
                }
                if (!added)
                    _solver._duplicates++;
                if (!_solver.CommitmentBudgetAvailable())
                    return new ChildCommitResult(node, false, progress);
                _solver.CountExpanded();
                return new ChildCommitResult(node, true, progress);
            }

            bool toEnded = child.ToEnded;
            bool inserted;
            try
            {
                if (toEnded)
                {
                    if (!ended.IsFull)
                    {
                        // Copy the record straight into the pool's reserved tail. Once the pool is
                        // full the compaction path still needs the scratch state, because the
                        // duplicate probe must run before the pool can make room.
                        ended.Reserve();
                        CopyChildState(in child, in directState, ref ended.ReservedState);
                        inserted = ended.CommitReserved(node, child.Score, child.Rank,
                            child.PotionCost, in child.Features, child.Depth, lineage, commitment,
                            child.Traits, child.PocketwatchThreshold, child.PocketwatchPlayed,
                            child.PocketwatchLastTurn, parentFacts.EnemyBlock, parentFacts.EnemyHp,
                            parentFacts.FutureResourceValue, parentFacts.StrategicResourcePotential,
                            child.Hash, in child.Facts);
                    }
                    else
                    {
                        CopyChildState(in child, in directState, ref _scratch);
                        inserted = _solver.AddEndTurnChild(ended, in _scratch, node, child.Score,
                            child.Rank, child.PotionCost, in child.Features, child.Depth, lineage,
                            commitment, child.Traits, child.PocketwatchThreshold,
                            child.PocketwatchPlayed, child.PocketwatchLastTurn, parentFacts.EnemyBlock,
                            parentFacts.EnemyHp, parentFacts.FutureResourceValue,
                            parentFacts.StrategicResourcePotential, child.Hash, in child.Facts);
                    }
                }
                else
                {
                    CopyChildState(in child, in directState, ref next.ReservedState);
                    inserted = next.CommitReserved(node, child.Score, child.Rank, child.PotionCost,
                        in child.Features, child.Depth, lineage, commitment, child.Traits,
                        child.PocketwatchThreshold, child.PocketwatchPlayed, child.PocketwatchLastTurn,
                        parentFacts.EnemyBlock, parentFacts.EnemyHp, parentFacts.FutureResourceValue,
                        parentFacts.StrategicResourcePotential, child.Hash, in child.Facts);
                }
            }
            catch (CapacityException ex)
            {
                _solver.RecordCapacityDrop(ex);
                return new ChildCommitResult(node, false, progress);
            }
            if (!inserted)
                _solver._duplicates++;
            return new ChildCommitResult(node, false, progress);
        }

        private void CopyChildState(in ChildRecord child, in Battle directState, ref Battle destination)
        {
            if (_direct)
                Battle.CopyPacked(in directState, ref destination);
            else
                CopyRecordState(in child, ref destination);
        }

        private readonly record struct ChildCommitResult(int NodeId, bool ExpandSuccessors,
            TurnProgressState Progress);

        private RoutingRef ResolveRouting(in RoutingEvent routing)
        {
            if (!_solver._routingSignatureIds.TryGetValue(routing.Signature, out int id))
            {
                _solver._routingSignatures.Add(routing.Signature);
                id = _solver._routingSignatures.Count;
                _solver._routingSignatureIds.Add(routing.Signature, id);
                _solver._storage.Memory.Charge(RoutingSignatureDictionaryBytes, "routing-signature");
            }
            return new RoutingRef(routing, id, new RoutingMeta(id, routing.Turn, routing.ChoiceDepth,
                routing.ParentRank, routing.ParentScore, routing.ParentEnergy), true);
        }

        private bool IsFirstEndTurnLeaf(int parentNodeKey)
        {
            if (_endTurnPrefixAttemptGeneration == _generation
                && _endTurnPrefixAttemptNode == parentNodeKey)
                return false;
            _endTurnPrefixAttemptGeneration = _generation;
            _endTurnPrefixAttemptNode = parentNodeKey;
            return true;
        }

        private bool CanReuseEndTurnPrefix(int parentNodeKey)
            => _endTurnPrefixValid
                && _endTurnPrefixGeneration == _generation
                && _endTurnPrefixNode == parentNodeKey;

        private void StoreEndTurnPrefix(int parentNodeKey)
        {
            if (_endTurnPrefix.IsPrepared)
            {
                _endTurnPrefixValid = true;
                _endTurnPrefixGeneration = _generation;
                _endTurnPrefixNode = parentNodeKey;
                EndTurnPrefixShared++;
                return;
            }
            _endTurnPrefixValid = false;
            EndTurnPrefixUnshared++;
            switch (_endTurnPrefix.Status)
            {
                case EndTurnPrefixStatus.ChoiceBeforePlayerStart:
                    EndTurnPrefixChoiceUnshared++;
                    break;
                case EndTurnPrefixStatus.ExtraTurn:
                    EndTurnPrefixExtraTurnUnshared++;
                    break;
                case EndTurnPrefixStatus.NotInProgress:
                    EndTurnPrefixNotInProgress++;
                    break;
            }
        }

        private int AppendState(in Battle state, out int length)
        {
            length = Battle.PackedSize(in state);
            int offset = _stateBytesUsed;
            ref byte source = ref MemoryMarshal.GetReference(
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in state, 1)));
            Unsafe.CopyBlockUnaligned(ref _stateBytes[offset], ref source, (uint)length);
            _stateBytesUsed += length;
            NoteGrowableUse();
            return offset;
        }

        private void CopyRecordState(in ChildRecord record, ref Battle destination)
        {
            ref byte source = ref _stateBytes[record.StateOffset];
            ref byte target = ref Unsafe.As<Battle, byte>(ref destination);
            Unsafe.CopyBlockUnaligned(ref target, ref source, (uint)record.StateLength);
        }

        private void AddEvent(ExpansionEvent value)
        {
            if (!TryGrowArray(ref _events, _eventCount + 1))
                FlushChunk();
            _events[_eventCount] = value;
            _eventCount++;
            NoteGrowableUse();
        }

        /// <summary>Returns the next child slot so the caller writes the record in place.</summary>
        private ref ChildRecord AddChild()
        {
            ref ChildRecord child = ref _children[_childCount++];
            NoteGrowableUse();
            return ref child;
        }

        private int AddFetched(FetchedRecord value)
        {
            _fetched[_fetchedCount] = value;
            int index = _fetchedCount++;
            NoteGrowableUse();
            return index;
        }

        private void AddRoutingEvent(RoutingEvent value)
        {
            ReserveRecord(routings: 1);
            int routingIndex = _routingCount;
            _routings[_routingCount++] = value;
            AddEvent(new ExpansionEvent
            {
                Kind = ExpansionEventKind.Routing,
                RoutingIndex = routingIndex,
            });
        }

        internal int EndTurnPrefixNode
        {
            get => _endTurnPrefixNode;
            set => _endTurnPrefixNode = value;
        }

        internal int EndTurnPrefixAttemptNode
        {
            get => _endTurnPrefixAttemptNode;
            set => _endTurnPrefixAttemptNode = value;
        }

    }
}
