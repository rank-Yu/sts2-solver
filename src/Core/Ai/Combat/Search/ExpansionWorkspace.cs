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
        PowerCommitmentState Commitment);

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
        public int EndEvent;
        public int RoutingIndex;
        public int ResolvedRoutingId;
        public int NodeId;
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
        public int SuccessorEnd;
        public bool HasCommitment;
        public PowerCommitmentState Commitment;
        public int RoutingEvent;
        public int ParentRecord;
    }

    /// <summary>
    /// Reusable fan-out buffer for one parent. A solve owns one workspace today; the buffers are
    /// sized for reuse across parents and never allocated per child. Event/child arrays and the
    /// packed-state byte buffer grow by doubling, and the end-turn prefix is a single
    /// <see cref="Battle"/> snapshot.
    /// </summary>
    private sealed class ExpansionWorkspace
    {
        private const int InitialEventCapacity = 1024;
        private const int InitialChildCapacity = 256;
        private const int InitialRoutingCapacity = 64;
        private const int InitialStateBytes = 256 * 1024;

        private readonly BeamSolver _solver;
        private readonly PlayerAction[] _actions = new PlayerAction[Kernel.MaxLegalActions];
        private readonly PlayerAction[] _commitmentActions = new PlayerAction[Kernel.MaxLegalActions];

        private ExpansionEvent[] _events = new ExpansionEvent[InitialEventCapacity];
        private ChildRecord[] _children = new ChildRecord[InitialChildCapacity];
        private RoutingEvent[] _routings = new RoutingEvent[InitialRoutingCapacity];
        private FetchedRecord[] _fetched = new FetchedRecord[16];
        private byte[] _stateBytes = new byte[InitialStateBytes];
        private int _eventCount;
        private int _childCount;
        private int _routingCount;
        private int _fetchedCount;
        private int _stateBytesUsed;

        private Battle _draft;
        private Battle _scratch;
        private Battle _fetchedState;

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

        internal ExpansionWorkspace(BeamSolver solver) => _solver = solver;

        internal SearchEquivalenceDiagnostics EquivalenceDiagnostics() => new(
            PlayDeduped, PlayVerifiedExact, PlayVerifiedApprox, PlayVerifiedApproxEqual,
            PlayVerifiedUnresolved, ChoiceByClassRequests, ChoiceVerifiedExact,
            ChoiceVerifiedApprox, ChoiceVerifiedApproxEqual, ChoiceVerifiedUnresolved);

        /// <summary>Starts one parent: drop the previous parent's records and advance the prefix generation.</summary>
        internal void BeginParent()
        {
            _eventCount = 0;
            _childCount = 0;
            _routingCount = 0;
            _fetchedCount = 0;
            _stateBytesUsed = 0;
            _generation++;
        }

        /// <summary>
        /// Pure calculation half. Reads the parent state, the fixed parent snapshot, rules, scorer and
        /// options only; writes its own buffers and the end-turn prefix workspace.
        /// </summary>
        internal void Expand(in Battle parent, in ParentExpansionContext context,
            CancellationToken cancellationToken)
        {
            int count = Kernel.LegalActions(parent, _solver._rules, _actions);
            scoped ReadOnlySpan<PlayerAction> legal = _actions.AsSpan(0, count);
            for (int actionIndex = 0; actionIndex < count; actionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.BeginAction });
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
                ExpandAction(in parent, -1, context.Node, context.Depth, context.Facts,
                    context.Rank, context.Score, context.HasCommitment, context.Commitment,
                    action, null, default, -1, allowFetchedPowerCommit: true,
                    cancellationToken);
            }
        }

        /// <summary>
        /// One action invocation. Choice recursion re-executes the same action from the same parent
        /// with more selections, exactly like the previous one-child-at-a-time expansion.
        /// </summary>
        private void ExpandAction(in Battle parent, int parentRecord, int parentNodeKey,
            int parentDepth, in RetentionFacts parentFacts, int parentRank, double parentScore,
            bool hasParentCommitment, in PowerCommitmentState parentCommitment,
            PlayerAction action, ChoiceEffect? primaryEffect, scoped ReadOnlySpan<Card> selectedPowers,
            int routingEvent, bool allowFetchedPowerCommit, CancellationToken cancellationToken)
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
                if (action.Kind == PlayerActionKind.EndTurn && CanReuseEndTurnPrefix(parentNodeKey))
                {
                    EndTurnPrefixReusedLeaves++;
                    stepResult = Kernel.StepEndTurnFromPrefix(ref _draft, action, _solver._rules,
                        in _endTurnPrefix, out request, out historyRisk);
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
                            out request, out historyRisk, ref _endTurnPrefix);
                        StoreEndTurnPrefix(parentNodeKey);
                    }
                    else
                    {
                        stepResult = Kernel.Step(ref _draft, action, _solver._rules,
                            out request, out historyRisk);
                    }
                }
                if (stepResult == StepResult.NeedsChoice)
                {
                    ExpandChoice(in parent, parentRecord, parentNodeKey, parentDepth, parentFacts,
                        parentRank, parentScore, hasParentCommitment, parentCommitment, action,
                        primaryEffect, selectedPowers, routingEvent, allowFetchedPowerCommit,
                        in request, cancellationToken);
                    return;
                }

                int depth = parentDepth + 1;
                bool isTerminal = !Kernel.IsInProgress(_draft);
                (double score, double rank, _, int potionCost, ScoreFeatures features) =
                    _solver.Score(in _draft, depth, isTerminal, action.Kind == PlayerActionKind.EndTurn);
                bool nonPure = !IsPureTransition(in parent, in _draft, action, _solver._rules, historyRisk);
                SearchRouteTraits childTraits = ClassifyRouteTraits(parentFacts, features, action,
                    DeclinedExtraTurn(in parent), nonPure);
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
                    int fetchedIndex = AddFetched(new FetchedRecord
                    {
                        Features = fetchedFeatures,
                        PoolFacts = fetchedPoolFacts,
                        ParentFacts = fetchedParentFacts,
                    });
                    int fetchedLength;
                    int fetchedOffset = AppendState(in _fetchedState, out fetchedLength);
                    int childIndex = AddChild(new ChildRecord
                    {
                        Action = action,
                        StateOffset = fetchedOffset,
                        StateLength = fetchedLength,
                        Hash = Fingerprint.Hash(in _fetchedState),
                        Score = score,
                        Rank = rank,
                        PotionCost = potionCost,
                        Features = features,
                        Facts = BuildFacts(features, depth, childTraits, childPocketwatchThreshold,
                            childPocketwatchPlayed, childPocketwatchLastTurn, parentFacts,
                            in _fetchedState),
                        Traits = childTraits,
                        PocketwatchThreshold = childPocketwatchThreshold,
                        PocketwatchPlayed = childPocketwatchPlayed,
                        PocketwatchLastTurn = childPocketwatchLastTurn,
                        Depth = depth,
                        UsedPotion = usedPotion,
                        ActionCardId = action.Kind == PlayerActionKind.PlayCard
                            && action.HandIndex < parent.HandCount ? parent.Cards[action.HandIndex].Id : 0,
                        TargetCombatId = TargetCombatId(in parent, action),
                        ToEnded = toEnded,
                        FetchedCandidate = true,
                        FetchedIndex = fetchedIndex,
                        SuccessorEnd = -1,
                        HasCommitment = hasCommitment,
                        Commitment = commitment,
                        RoutingEvent = routingEvent,
                        ParentRecord = parentRecord,
                    });
                    int childEvent = AddEvent(new ExpansionEvent
                    {
                        Kind = ExpansionEventKind.Child,
                        ChildIndex = childIndex,
                        NodeId = -1,
                    });
                    ExpandFetchedPowerSuccessors(in _fetchedState, childEvent, depth,
                        fetchedParentFacts, parentRank, parentScore, hasCommitment, commitment,
                        selectedPowers, cancellationToken);
                    _children[childIndex].SuccessorEnd = _eventCount - 1;
                    _events[childEvent].EndEvent = _eventCount - 1;
                    return;
                }

                int stateOffset = AppendState(in _draft, out int stateLength);
                AddChild(new ChildRecord
                {
                    Action = action,
                    StateOffset = stateOffset,
                    StateLength = stateLength,
                    Hash = isTerminal ? 0 : Fingerprint.Hash(in _draft),
                    Score = score,
                    Rank = rank,
                    PotionCost = potionCost,
                    Features = features,
                    Facts = isTerminal
                        ? default
                        : BuildFacts(features, depth, childTraits, childPocketwatchThreshold,
                            childPocketwatchPlayed, childPocketwatchLastTurn, parentFacts, in _draft),
                    Traits = childTraits,
                    PocketwatchThreshold = childPocketwatchThreshold,
                    PocketwatchPlayed = childPocketwatchPlayed,
                    PocketwatchLastTurn = childPocketwatchLastTurn,
                    Depth = depth,
                    UsedPotion = usedPotion,
                    ActionCardId = action.Kind == PlayerActionKind.PlayCard
                        && action.HandIndex < parent.HandCount ? parent.Cards[action.HandIndex].Id : 0,
                    TargetCombatId = TargetCombatId(in parent, action),
                    Terminal = isTerminal,
                    ToEnded = toEnded,
                    FetchedIndex = -1,
                    SuccessorEnd = -1,
                    HasCommitment = hasCommitment,
                    Commitment = commitment,
                    RoutingEvent = routingEvent,
                    ParentRecord = parentRecord,
                });
                AddEvent(new ExpansionEvent
                {
                    Kind = ExpansionEventKind.Child,
                    ChildIndex = _childCount - 1,
                    NodeId = -1,
                });
            }
            catch (CapacityException ex)
            {
                AddEvent(new ExpansionEvent
                {
                    Kind = ExpansionEventKind.ExecutionDrop,
                    DropRegion = ex.Region,
                });
            }
        }

        private void ExpandChoice(in Battle parent, int parentRecord, int parentNodeKey,
            int parentDepth, in RetentionFacts parentFacts, int parentRank, double parentScore,
            bool hasParentCommitment, in PowerCommitmentState parentCommitment, PlayerAction action,
            ChoiceEffect? primaryEffect, scoped ReadOnlySpan<Card> selectedPowers, int routingEvent,
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
            int headerEvent = AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.ChoiceHeader });
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
                        AddEvent(new ExpansionEvent
                        {
                            Kind = ExpansionEventKind.BranchDrop,
                            DropRegion = ex.Region,
                        });
                        int end = AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.ChoiceEnd });
                        _events[headerEvent].EndEvent = end;
                        return;
                    }
                }
                int branchRouting = routingEvent;
                if (TryBuildRoutingSignature(in parent, action, in request, optionCards,
                        selections[index], parentDepth + 1, parentFacts.Turn, parentRank, parentScore,
                        parentFacts, out RoutingSignature signature, out int turn, out int choiceDepth,
                        out int choiceParentRank, out double choiceParentScore, out int choiceParentEnergy))
                {
                    branchRouting = AddRoutingEvent(new RoutingEvent
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
                    AddEvent(new ExpansionEvent
                    {
                        Kind = ExpansionEventKind.BranchDrop,
                        DropRegion = ex.Region,
                    });
                    continue;
                }
                ExpandAction(in parent, parentRecord, parentNodeKey, parentDepth, parentFacts,
                    parentRank, parentScore, hasParentCommitment, parentCommitment, withChoice,
                    effect, chosenPowers, branchRouting, allowFetchedPowerCommit, cancellationToken);
            }
            int endEvent = AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.ChoiceEnd });
            _events[headerEvent].EndEvent = endEvent;
        }

        private void ExpandFetchedPowerSuccessors(in Battle state, int fetchedEvent, int depth,
            in RetentionFacts fetchedFacts, int parentRank, double parentScore, bool hasCommitment,
            in PowerCommitmentState commitment, scoped ReadOnlySpan<Card> selectedPowers,
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
                ExpandAction(in state, fetchedEvent, -1, depth, fetchedFacts, parentRank, parentScore,
                    hasCommitment, in commitment, action, null, default, -1,
                    allowFetchedPowerCommit: false, cancellationToken);
                AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.SuccessorActionEnd });
            }
            AddEvent(new ExpansionEvent { Kind = ExpansionEventKind.SuccessorEnd });
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
        /// Commit half. Replays the recorded steps in order on the search thread; every live store,
        /// counter and trace write happens here.
        /// </summary>
        internal void Commit(in Battle parent, in ParentExpansionContext context, Pool next,
            Pool ended, TerminalSink terminal)
        {
            int index = 0;
            while (index < _eventCount)
            {
                if (!CommitOne(ref index, _eventCount, in parent, in context, next, ended, terminal))
                    return;
            }
        }

        private bool CommitOne(ref int index, int limit, in Battle parent,
            in ParentExpansionContext context, Pool next, Pool ended, TerminalSink terminal)
        {
            ExpansionEvent record = _events[index];
            switch (record.Kind)
            {
                case ExpansionEventKind.BeginAction:
                    if (_solver._options.UnlimitedNodes && _solver._solveWatch is { } watch
                        && _solver.OutOfBudget(watch))
                        return false;
                    index++;
                    return true;
                case ExpansionEventKind.ChoiceHeader:
                {
                    try
                    {
                        next.Reserve();
                    }
                    catch (CapacityException ex)
                    {
                        _solver.RecordCapacityDrop(ex);
                        index = record.EndEvent + 1;
                        return true;
                    }
                    _solver._transitions++;
                    index++;
                    return true;
                }
                case ExpansionEventKind.ChoiceEnd:
                    index++;
                    return true;
                case ExpansionEventKind.Routing:
                    ResolveRouting(index);
                    index++;
                    return true;
                case ExpansionEventKind.ExecutionDrop:
                {
                    try
                    {
                        next.Reserve();
                    }
                    catch (CapacityException ex)
                    {
                        _solver.RecordCapacityDrop(ex);
                        index++;
                        return true;
                    }
                    _solver._transitions++;
                    _solver.RecordCapacityDrop(record.DropRegion!);
                    index++;
                    return true;
                }
                case ExpansionEventKind.BranchDrop:
                    _solver.RecordCapacityDrop(record.DropRegion!);
                    index++;
                    return true;
                case ExpansionEventKind.SuccessorActionEnd:
                    if (!_solver.CommitmentBudgetAvailable())
                        index = limit;
                    else
                        index++;
                    return true;
                case ExpansionEventKind.SuccessorEnd:
                    index++;
                    return true;
                case ExpansionEventKind.Child:
                    return CommitChild(ref index, in parent, in context, next, ended, terminal);
                default:
                    throw new InvalidOperationException($"未知的展开记录类型 {record.Kind}。");
            }
        }

        private bool CommitChild(ref int index, in Battle parent, in ParentExpansionContext context,
            Pool next, Pool ended, TerminalSink terminal)
        {
            int eventIndex = index;
            ExpansionEvent childEvent = _events[eventIndex];
            ref readonly ChildRecord child = ref _children[childEvent.ChildIndex];
            int parentNode = child.ParentRecord < 0
                ? context.Node
                : _events[child.ParentRecord].NodeId;
            if (parentNode < 0)
                throw new InvalidOperationException("展开记录的父路线节点尚未提交。");
            ref readonly Battle parentState = ref ParentState(child.ParentRecord, in parent);
            RetentionFacts parentFacts = child.ParentRecord < 0
                ? context.Facts
                : _fetched[_children[_events[child.ParentRecord].ChildIndex].FetchedIndex].ParentFacts;

            if (!next.TryReserve())
            {
                _solver.RecordCapacityDrop("Beam.Pool");
                index = child.FetchedCandidate ? child.SuccessorEnd + 1 : index + 1;
                return true;
            }
            _solver._transitions++;

            int traceStep = _solver._trace?.Match(parentNode, child.Action) ?? 0;
            int node = _solver._nodes.Count;
            ulong lineage = _solver.NextLineage(parentNode, in parentState, child.Action, node,
                out ulong rootActionKey, out ushort firstCardId, out int firstCardTarget);
            int parentMetaId = _solver._nodes.RoutingMetaIdAt(parentNode);
            int routingSignature = 0;
            RoutingMeta routingMeta;
            int routingMetaId;
            if (child.RoutingEvent >= 0)
            {
                ExpansionEvent routingRecord = _events[child.RoutingEvent];
                routingSignature = routingRecord.ResolvedRoutingId;
                RoutingEvent routing = _routings[routingRecord.RoutingIndex];
                routingMeta = new RoutingMeta(routingSignature, routing.Turn, routing.ChoiceDepth,
                    routing.ParentRank, routing.ParentScore, routing.ParentEnergy);
            }
            else
            {
                routingMeta = default;
            }
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
            _solver._nodes.Add(new RouteNode(parentNode, child.Depth, child.Action, rootActionKey,
                lineage, child.UsedPotion, firstCardId, firstCardTarget, commitment, child.ActionCardId,
                child.TargetCombatId, routingMeta.SignatureId, routingMeta.Turn,
                routingMeta.ChoiceDepth, routingMeta.ParentRank, routingMeta.ParentScore,
                routingMeta.ParentEnergy, parentFacts.Turn, routingMetaId, potionLineageKey),
                routingMetaId);
            _events[eventIndex].NodeId = node;
            if (traceStep > 0)
                _solver._trace!.NodeCreated(node, traceStep);

            if (child.Terminal)
            {
                CopyRecordState(in child, ref _scratch);
                terminal(in _scratch, node, in child.Features, child.Score);
                index++;
                return true;
            }

            if (child.FetchedCandidate && _solver.CommitmentBudgetAvailable())
            {
                ref readonly FetchedRecord fetched = ref _fetched[child.FetchedIndex];
                CopyRecordState(in child, ref _fetchedState);
                CopyRecordState(in child, ref next.ReservedState);
                bool added;
                try
                {
                    added = next.CommitReserved(node, child.Score, child.Rank, child.PotionCost,
                        in fetched.Features, child.Depth, lineage, commitment, child.Traits,
                        child.PocketwatchThreshold, child.PocketwatchPlayed, child.PocketwatchLastTurn,
                        parentFacts.EnemyBlock, parentFacts.EnemyHp, parentFacts.FutureResourceValue,
                        parentFacts.StrategicResourcePotential, child.Hash, in fetched.PoolFacts);
                }
                catch (CapacityException ex)
                {
                    _solver.RecordCapacityDrop(ex);
                    index = child.SuccessorEnd + 1;
                    return true;
                }
                if (!added)
                    _solver._duplicates++;
                if (!_solver.CommitmentBudgetAvailable())
                {
                    index = child.SuccessorEnd + 1;
                    return true;
                }
                _solver.CountExpanded();
                int successor = eventIndex + 1;
                while (successor < child.SuccessorEnd)
                {
                    if (!CommitOne(ref successor, child.SuccessorEnd, in parent, in context, next,
                            ended, terminal))
                    {
                        break;
                    }
                }
                index = child.SuccessorEnd + 1;
                return true;
            }

            bool toEnded = child.ToEnded;
            bool inserted;
            try
            {
                if (toEnded)
                {
                    CopyRecordState(in child, ref _scratch);
                    inserted = _solver.AddEndTurnChild(ended, in _scratch, node, child.Score,
                        child.Rank, child.PotionCost, in child.Features, child.Depth, lineage,
                        commitment, child.Traits, child.PocketwatchThreshold, child.PocketwatchPlayed,
                        child.PocketwatchLastTurn, parentFacts.EnemyBlock, parentFacts.EnemyHp,
                        parentFacts.FutureResourceValue, parentFacts.StrategicResourcePotential,
                        child.Hash, in child.Facts);
                }
                else
                {
                    CopyRecordState(in child, ref next.ReservedState);
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
                index++;
                return true;
            }
            if (!inserted)
                _solver._duplicates++;
            index = child.FetchedCandidate ? child.SuccessorEnd + 1 : eventIndex + 1;
            return true;
        }

        private ref readonly Battle ParentState(int parentRecord, in Battle parent)
        {
            if (parentRecord < 0)
                return ref Unsafe.AsRef(in parent);
            return ref _fetchedState;
        }

        private void ResolveRouting(int eventIndex)
        {
            ExpansionEvent record = _events[eventIndex];
            RoutingEvent routing = _routings[record.RoutingIndex];
            if (!_solver._routingSignatureIds.TryGetValue(routing.Signature, out int id))
            {
                _solver._routingSignatures.Add(routing.Signature);
                id = _solver._routingSignatures.Count;
                _solver._routingSignatureIds.Add(routing.Signature, id);
                _solver._storage.Memory.Charge(RoutingSignatureDictionaryBytes, "routing-signature");
            }
            _events[eventIndex].ResolvedRoutingId = id;
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
            EnsureStateBytes(offset + length);
            ref byte source = ref MemoryMarshal.GetReference(
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in state, 1)));
            Unsafe.CopyBlockUnaligned(ref _stateBytes[offset], ref source, (uint)length);
            _stateBytesUsed += length;
            return offset;
        }

        private void CopyRecordState(in ChildRecord record, ref Battle destination)
        {
            ref byte source = ref _stateBytes[record.StateOffset];
            ref byte target = ref Unsafe.As<Battle, byte>(ref destination);
            Unsafe.CopyBlockUnaligned(ref target, ref source, (uint)record.StateLength);
        }

        private void EnsureStateBytes(int required)
        {
            if (required <= _stateBytes.Length)
                return;
            int length = _stateBytes.Length;
            while (length < required)
                length = checked(length * 2);
            Array.Resize(ref _stateBytes, length);
        }

        private int AddEvent(ExpansionEvent value)
        {
            if (_eventCount == _events.Length)
                Array.Resize(ref _events, checked(_events.Length * 2));
            _events[_eventCount] = value;
            return _eventCount++;
        }

        private int AddChild(ChildRecord value)
        {
            if (_childCount == _children.Length)
                Array.Resize(ref _children, checked(_children.Length * 2));
            _children[_childCount] = value;
            return _childCount++;
        }

        private int AddFetched(FetchedRecord value)
        {
            if (_fetchedCount == _fetched.Length)
                Array.Resize(ref _fetched, checked(_fetched.Length * 2));
            _fetched[_fetchedCount] = value;
            return _fetchedCount++;
        }

        private int AddRoutingEvent(RoutingEvent value)
        {
            if (_routingCount == _routings.Length)
                Array.Resize(ref _routings, checked(_routings.Length * 2));
            int routingIndex = _routingCount;
            _routings[_routingCount++] = value;
            return AddEvent(new ExpansionEvent
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
