using System.Runtime.CompilerServices;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    private interface IChoiceScheduler
    {
        ExpansionWorkspace? Rent(ExpansionWorkspace owner);
        void Enqueue(ExpansionWorkspace helper);
        bool Join(ExpansionWorkspace owner, ExpansionWorkspace helper, bool discard);
    }

    private sealed partial class ExpansionWorkspace
    {
        [InlineArray(Limits.MaxChoiceOptions)]
        private struct ChoiceCards { private Card _first; }
        [InlineArray(Limits.MaxChoiceOptions)]
        private struct ChoiceFlags { private bool _first; }
        [InlineArray(MaxChoiceBranches)]
        private struct ChoiceMasks { private ulong _first; }

        private struct ChoiceData
        {
            internal ParentExpansionContext Context;
            internal bool FetchedParent, AllowFetchedPowerCommit, CaptureFetchedPowers;
            internal PlayerAction Action;
            internal ChoiceEffect? Effect;
            internal RoutingRef Routing;
            internal ChoiceRequest Request;
            internal ChoiceCards Options, Powers;
            internal ChoiceFlags IsPower;
            internal ChoiceMasks Selections;
            internal int PowerCount, SelectionCount, Start, End, EstimatedChildren;
        }
        private sealed class ChoiceInput { internal ChoiceData Data; }
        private ChoiceInput? _choiceInput;
        private IChoiceScheduler? _choices;
        internal static long ChoiceInputBytes => Unsafe.SizeOf<ChoiceData>() + 32;
        internal SearchWorkPacer? Pacer;
        internal int RootIndex, BranchSlot, OrderDepth;
        // A task splits at most two actions (the original and one fetched-power successor).
        // Eight choices per action, five binary splits per 32-way choice: at most 80 bits.
        internal UInt128 Order;
        internal int ChoiceTasks, ChoiceWorkerTasks;
        internal bool ChoiceCompleted;
        internal bool OnSearchThread => _searchThread;
        internal int BufferedEvents => _eventCount;

        private void PrepareChoice(ExpansionWorkspace owner, in Battle parent, bool fetchedParent,
            int node, int depth, in RetentionFacts facts, int rank, double score, bool hasCommitment,
            in PowerCommitmentState commitment, int spent, in TurnProgressState progress,
            in EnemyProgressHighs highs, PlayerAction action, ChoiceEffect? effect,
            ReadOnlySpan<Card> powers, in RoutingRef routing, bool allowFetched,
            in ChoiceRequest request, ReadOnlySpan<Card> options, ReadOnlySpan<ulong> selections,
            bool capturePowers, ReadOnlySpan<bool> isPower, int start, int end, int estimatedChildren)
        {
            // A helper never commits: its existing commit scratch is the immutable task parent.
            Battle.CopyPacked(in parent, ref _scratch);
            ref ChoiceData data = ref _choiceInput!.Data;
            data.Context = new ParentExpansionContext(node, depth, facts, rank, score, hasCommitment,
                commitment, spent, progress, highs, owner._parentCrossTurn);
            data.FetchedParent = fetchedParent;
            data.Action = action;
            data.Effect = effect;
            data.Routing = routing;
            data.AllowFetchedPowerCommit = allowFetched;
            data.Request = request;
            options.CopyTo(data.Options);
            powers.CopyTo(data.Powers);
            selections.CopyTo(data.Selections);
            isPower.CopyTo(data.IsPower);
            data.PowerCount = powers.Length;
            data.SelectionCount = selections.Length;
            data.CaptureFetchedPowers = capturePowers;
            data.Start = start;
            data.End = end;
            data.EstimatedChildren = estimatedChildren;
            RootIndex = owner.RootIndex;
            Order = owner.Order | ((UInt128)1 << (127 - owner.OrderDepth));
            OrderDepth = owner.OrderDepth + 1;
            _choices = owner._choices;
        }

        internal void ExpandChoiceTask(bool searchThread, bool inline,
            Func<int, bool, bool> publish, CancellationToken token)
        {
            ResetDiagnostics();
            BeginParent();
            if (!inline) ChoiceWorkerTasks++;
            _parentIndex = BranchSlot;
            _searchThread = searchThread;
            _publishChunk = publish;
            ref ChoiceData d = ref _choiceInput!.Data;
            ref readonly ParentExpansionContext c = ref d.Context;
            _parentCrossTurn = c.CrossTurn;
            ChoiceCompleted = false;
            try
            {
                ChoiceCompleted = ExpandSelections(in _scratch, d.FetchedParent, c.Node, c.Depth,
                    c.Facts, c.Rank, c.Score, c.HasCommitment, c.Commitment, c.BrightestFlameSpent,
                    c.Progress, c.EnemyHighs, d.Action, d.Effect, d.Powers[..d.PowerCount], d.Routing,
                    d.AllowFetchedPowerCommit, d.Request, d.Options[..d.Request.OptionCount],
                    d.Selections[..d.SelectionCount], d.CaptureFetchedPowers,
                    d.IsPower[..(d.CaptureFetchedPowers ? d.Request.OptionCount : 0)],
                    d.Start, d.End, d.EstimatedChildren, token);
            }
            catch (ChunkStoppedException) { }
            finally { _publishChunk = null; }
        }

        internal void AcceptChoiceChunk(ExpansionWorkspace helper, bool discard)
        {
            if (discard || helper._eventCount == 0)
                return;
            if (TryAppendChoiceChunk(helper))
                return;
            if (_eventCount != 0)
                FlushChunk();
            // Transfer ownership of whole arrays: no second copy of the packed child states.
            (_events, helper._events) = (helper._events, _events);
            (_children, helper._children) = (helper._children, _children);
            (_brightestFlameSpent, helper._brightestFlameSpent) = (helper._brightestFlameSpent, _brightestFlameSpent);
            (_routings, helper._routings) = (helper._routings, _routings);
            (_fetched, helper._fetched) = (helper._fetched, _fetched);
            (_stateBytes, helper._stateBytes) = (helper._stateBytes, _stateBytes);
            (_growableBytes, helper._growableBytes) = (helper._growableBytes, _growableBytes);
            _eventCount = helper._eventCount;
            _childCount = helper._childCount;
            _routingCount = helper._routingCount;
            _fetchedCount = helper._fetchedCount;
            _stateBytesUsed = helper._stateBytesUsed;
            helper._eventCount = helper._childCount = helper._routingCount = helper._fetchedCount = helper._stateBytesUsed = 0;
            NoteGrowableUse();
            FlushChunk();
        }

        private bool TryAppendChoiceChunk(ExpansionWorkspace helper)
        {
            if (helper._eventCount > _events.Length - _eventCount
                || helper._childCount > _children.Length - _childCount
                || (_solver.TracksBrightestFlameSpent
                    && helper._childCount > _brightestFlameSpent.Length - _childCount)
                || helper._routingCount > _routings.Length - _routingCount
                || helper._fetchedCount > _fetched.Length - _fetchedCount
                || helper._stateBytesUsed > _stateBytes.Length - _stateBytesUsed)
                return false;
            helper._stateBytes.AsSpan(0, helper._stateBytesUsed).CopyTo(_stateBytes.AsSpan(_stateBytesUsed));
            helper._children.AsSpan(0, helper._childCount).CopyTo(_children.AsSpan(_childCount));
            if (_solver.TracksBrightestFlameSpent)
                helper._brightestFlameSpent.AsSpan(0, helper._childCount).CopyTo(_brightestFlameSpent.AsSpan(_childCount));
            for (int i = 0; i < helper._childCount; i++)
            {
                ref ChildRecord child = ref _children[_childCount + i];
                child.StateOffset += _stateBytesUsed;
                if (child.FetchedCandidate) child.FetchedIndex += _fetchedCount;
            }
            helper._routings.AsSpan(0, helper._routingCount).CopyTo(_routings.AsSpan(_routingCount));
            helper._fetched.AsSpan(0, helper._fetchedCount).CopyTo(_fetched.AsSpan(_fetchedCount));
            helper._events.AsSpan(0, helper._eventCount).CopyTo(_events.AsSpan(_eventCount));
            for (int i = 0; i < helper._eventCount; i++)
            {
                ref ExpansionEvent e = ref _events[_eventCount + i];
                if (e.Kind == ExpansionEventKind.Child) e.ChildIndex += _childCount;
                if (e.Kind == ExpansionEventKind.Routing) e.RoutingIndex += _routingCount;
            }
            _eventCount += helper._eventCount;
            _childCount += helper._childCount;
            _routingCount += helper._routingCount;
            _fetchedCount += helper._fetchedCount;
            _stateBytesUsed += helper._stateBytesUsed;
            NoteGrowableUse();
            return true;
        }

        internal void AddChoiceDiagnostics(ExpansionWorkspace other)
        {
            _parentChildren += other._parentChildren;
            _parentChoices += other._parentChoices;
            _parentTerminals += other._parentTerminals;
            _parentPeakUsedBytes = Math.Max(_parentPeakUsedBytes, other._parentPeakUsedBytes);
            _parentPeakAllocatedBytes = Math.Max(_parentPeakAllocatedBytes, other._parentPeakAllocatedBytes);
            ChoiceTasks += other.ChoiceTasks;
            ChoiceWorkerTasks += other.ChoiceWorkerTasks;
            PlayDeduped += other.PlayDeduped;
            PlayVerifiedExact += other.PlayVerifiedExact;
            PlayVerifiedApprox += other.PlayVerifiedApprox;
            PlayVerifiedApproxEqual += other.PlayVerifiedApproxEqual;
            PlayVerifiedUnresolved += other.PlayVerifiedUnresolved;
            ChoiceByClassRequests += other.ChoiceByClassRequests;
            ChoiceVerifiedExact += other.ChoiceVerifiedExact;
            ChoiceVerifiedApprox += other.ChoiceVerifiedApprox;
            ChoiceVerifiedApproxEqual += other.ChoiceVerifiedApproxEqual;
            ChoiceVerifiedUnresolved += other.ChoiceVerifiedUnresolved;
            EndTurnPrefixShared += other.EndTurnPrefixShared;
            EndTurnPrefixUnshared += other.EndTurnPrefixUnshared;
            EndTurnPrefixVerified += other.EndTurnPrefixVerified;
            EndTurnPrefixReusedLeaves += other.EndTurnPrefixReusedLeaves;
            EndTurnPrefixChoiceUnshared += other.EndTurnPrefixChoiceUnshared;
            EndTurnPrefixExtraTurnUnshared += other.EndTurnPrefixExtraTurnUnshared;
            EndTurnPrefixNotInProgress += other.EndTurnPrefixNotInProgress;
            TurnLoopGuarded += other.TurnLoopGuarded;
            CrossTurnNoProgressPruned += other.CrossTurnNoProgressPruned;
        }
    }
}
