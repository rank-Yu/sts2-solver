using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    private sealed partial class ParallelExpansion
    {
        private sealed partial class LayerJob : IChoiceScheduler
        {
            private enum ChoiceState { Free, Reserved, Pending, Running, Chunk, Done }
            private sealed class ChoiceSlot(ExpansionWorkspace workspace)
            {
                internal readonly ExpansionWorkspace Workspace = workspace;
                internal ExpansionWorkspace Owner = null!;
                internal ChoiceState State;
                internal bool Inline, Discard;
            }
            private readonly record struct WorkClaim(int Index, bool Choice = false);
            private int _pendingChoices, _activeChoices;
            private ChoiceSlot[] _choiceSlots = null!;
            private Func<int, bool, bool> _publishChoice = null!;

            private void InitializeChoices()
            {
                int offset = _workspaces.Length * 2 / 3;
                _choiceSlots = new ChoiceSlot[_workspaces.Length - offset];
                for (int index = 0; index < _choiceSlots.Length; index++)
                {
                    _workspaces[offset + index].BranchSlot = index;
                    _choiceSlots[index] = new ChoiceSlot(_workspaces[offset + index]);
                }
                _publishChoice = PublishChoice;
            }

            public ExpansionWorkspace? Rent(ExpansionWorkspace owner)
            {
                lock (_gate)
                {
                    if (_stop || !owner.SupportsBufferedExpansion)
                        return null;
                    foreach (ChoiceSlot slot in _choiceSlots)
                    {
                        if (slot.State != ChoiceState.Free) continue;
                        slot.State = ChoiceState.Reserved;
                        _activeChoices++;
                        slot.Owner = owner;
                        slot.Discard = false;
                        return slot.Workspace;
                    }
                    return null;
                }
            }

            public void Enqueue(ExpansionWorkspace helper)
            {
                lock (_gate)
                {
                    _choiceSlots[helper.BranchSlot].State = ChoiceState.Pending;
                    _pendingChoices++;
                    Monitor.PulseAll(_gate);
                }
                SignalCommit();
                SignalWindow(wakeAll: true);
            }

            // Only steal work before the suspended producer. Helping later work could create a
            // cycle: the later owner needs this producer's next chunk before it can drain its helper.
            private int ClaimChoiceBefore(int root, UInt128 order)
            {
                if (_pendingChoices == 0) return -1;
                int best = -1;
                for (int index = 0; index < _choiceSlots.Length; index++)
                {
                    ChoiceSlot slot = _choiceSlots[index];
                    ExpansionWorkspace w = slot.Workspace;
                    if (slot.State != ChoiceState.Pending
                        || w.RootIndex > root || (w.RootIndex == root && w.Order >= order)) continue;
                    if (best < 0 || w.RootIndex < _choiceSlots[best].Workspace.RootIndex
                        || (w.RootIndex == _choiceSlots[best].Workspace.RootIndex
                            && w.Order < _choiceSlots[best].Workspace.Order)) best = index;
                }
                if (best >= 0)
                {
                    _pendingChoices--;
                    _choiceSlots[best].State = ChoiceState.Running;
                    _choiceSlots[best].Inline = false;
                }
                return best;
            }

            private void RunChoice(int index, SearchWorkPacer? pacer, bool searchThread = false)
            {
                ChoiceSlot slot = _choiceSlots[index];
                _solver.WorkerCheckpoint(pacer);
                slot.Workspace.Pacer = pacer;
                slot.Workspace.ExpandChoiceTask(searchThread, slot.Inline, _publishChoice,
                    _solver._cancellationToken);
                lock (_gate)
                {
                    if (_stop) return;
                    slot.State = ChoiceState.Done;
                    Monitor.PulseAll(_gate);
                }
                SignalCommit();
            }

            public bool Join(ExpansionWorkspace owner, ExpansionWorkspace helper, bool discard)
            {
                ChoiceSlot slot = _choiceSlots[helper.BranchSlot];
                while (true)
                {
                    bool run = false, take = false, done = false;
                    int earlier = -1;
                    lock (_gate)
                    {
                        if (_stop) throw new ExpansionWorkspace.ChunkStoppedException();
                        slot.Discard = discard;
                        if (slot.State == ChoiceState.Pending)
                        {
                            _pendingChoices--;
                            slot.State = ChoiceState.Running;
                            slot.Inline = true;
                            run = true;
                        }
                        else if (slot.State is ChoiceState.Chunk or ChoiceState.Done)
                        {
                            take = true;
                            done = slot.State == ChoiceState.Done;
                        }
                        else if (!owner.OnSearchThread)
                        {
                            earlier = ClaimChoiceBefore(owner.RootIndex, owner.Order);
                            if (earlier < 0) Monitor.Wait(_gate);
                        }
                    }
                    if (run) RunChoice(helper.BranchSlot, owner.Pacer, owner.OnSearchThread);
                    else if (take)
                    {
                        owner.AcceptChoiceChunk(helper, discard);
                        bool completed = helper.ChoiceCompleted;
                        if (done) owner.AddChoiceDiagnostics(helper);
                        lock (_gate)
                        {
                            slot.State = done ? ChoiceState.Free : ChoiceState.Running;
                            if (done) _activeChoices--;
                            Monitor.PulseAll(_gate);
                        }
                        SignalCommit();
                        if (done) return completed;
                    }
                    else if (earlier >= 0) RunChoice(earlier, owner.Pacer);
                    else if (owner.OnSearchThread) PumpForChoice(slot, waitForAcknowledgement: false);
                }
            }

            private bool PublishChoice(int index, bool searchThread)
            {
                ChoiceSlot slot = _choiceSlots[index];
                if (slot.Inline)
                {
                    slot.Owner.AcceptChoiceChunk(slot.Workspace, slot.Discard);
                    return !_stop;
                }
                lock (_gate)
                {
                    if (_stop) return false;
                    slot.State = ChoiceState.Chunk;
                    Monitor.PulseAll(_gate);
                }
                SignalCommit();
                while (true)
                {
                    int earlier = -1;
                    lock (_gate)
                    {
                        if (_stop) return false;
                        if (slot.State != ChoiceState.Chunk) return true;
                        if (!searchThread)
                        {
                            earlier = ClaimChoiceBefore(slot.Workspace.RootIndex, slot.Workspace.Order);
                            if (earlier < 0) Monitor.Wait(_gate);
                        }
                    }
                    if (earlier >= 0) RunChoice(earlier, slot.Workspace.Pacer);
                    else if (searchThread) PumpForChoice(slot, waitForAcknowledgement: true);
                }
            }

            // The search thread may itself be a helper while the owning producer waits for its
            // chunks. Keep the ordered consumer running until that owner acknowledges the chunk.
            private void PumpForChoice(ChoiceSlot slot, bool waitForAcknowledgement)
            {
                int index;
                lock (_gate)
                {
                    if (_stop) return;
                    if (waitForAcknowledgement ? slot.State != ChoiceState.Chunk
                        : slot.State is ChoiceState.Chunk or ChoiceState.Done or ChoiceState.Pending) return;
                    index = _commitCursor;
                    if (!ReadyFast(index))
                    {
                        Monitor.Wait(_gate);
                        return;
                    }
                }
                if (BeginCommitParent(index)) Commit(index);
            }
        }
    }
}
