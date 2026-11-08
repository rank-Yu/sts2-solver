// Parallel calculation threads with ordered, single-threaded commit.
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>
    /// Total threads assigned to one solve. The search thread also claims parents while it waits for
    /// the next commit, so <c>threads</c> includes it. The workers live for the whole solve and block
    /// between layers; the caller disposes the object after the search loop.
    /// </summary>
    private sealed class ParallelExpansion : IDisposable
    {
        private const int SpinsBeforeBlocking = 8;
        /// <summary>Below this many items the wake-up cost exceeds one batch's parallel gain.</summary>
        private const int MinimumBatchItems = 8;

        private interface IWorkerJob
        {
            void RunWorker(SearchWorkPacer? pacer);
        }

        private readonly BeamSolver _solver;
        private readonly ExpansionWorkspace[] _workspaces;
        private readonly int _window;
        private readonly Thread[] _threads;
        private readonly SearchWorkPacer?[] _threadPacers;
        private readonly object _gate = new();
        private readonly BatchJob _batchJob;
        private readonly ManualResetEventSlim _batchCompleted = new(false);
        private IWorkerJob? _job;
        private int _generation;
        private int _workersDone;
        private int _batchWorkersDone;
        private volatile bool _shutdown;
        /// <summary>One while the ordered layer job is active; read-only batches are refused then.</summary>
        private int _layerActive;

        internal ParallelExpansion(BeamSolver solver, int threads)
        {
            _solver = solver;
            _workspaces = solver._workspaces;
            _window = solver._workspaces.Length;
            _batchJob = new BatchJob();
            _threads = new Thread[threads - 1];
            _threadPacers = new SearchWorkPacer?[_threads.Length];
            SearchWorkPacer? pacer = solver._options.Pacer;
            for (int index = 0; index < _threads.Length; index++)
            {
                int id = index;
                if (pacer != null)
                    _threadPacers[id] = new SearchWorkPacer(pacer.FramePressureSignal);
                Thread thread = new(() => WorkerMain(id))
                {
                    IsBackground = true,
                    Name = $"sts2-search-worker-{id + 1}",
                };
                if (OperatingSystem.IsWindows())
                    thread.Priority = ThreadPriority.BelowNormal;
                _threads[index] = thread;
            }
            int started = 0;
            try
            {
                for (; started < _threads.Length; started++)
                    _threads[started].Start();
            }
            catch
            {
                lock (_gate)
                {
                    _shutdown = true;
                    Monitor.PulseAll(_gate);
                }
                for (int index = 0; index < started; index++)
                    _threads[index].Join();
                throw;
            }
        }

        /// <summary>
        /// Runs one action layer. The search thread commits parents in their original order and also
        /// claims work while the next slot is not ready. On return no worker is inspecting the layer.
        /// </summary>
        internal void RunLayer(Pool current, Pool next, Pool ended, TerminalSink terminal,
            ParentExpansionContext[] contexts, int count)
        {
            int window = Math.Min(_window, count);
            if (window <= 0)
                return;
            Volatile.Write(ref _layerActive, 1);
            try
            {
                LayerJob job = new(this, current, next, ended, terminal, contexts, window, count);
                lock (_gate)
                {
                    _job = job;
                    _generation++;
                    _workersDone = 0;
                    Monitor.PulseAll(_gate);
                }
                try
                {
                    job.RunSearch(_solver._options.Pacer);
                }
                finally
                {
                    job.Stop();
                    lock (_gate)
                    {
                        while (_workersDone < _threads.Length)
                            Monitor.Wait(_gate);
                        _job = null;
                    }
                }
                job.RethrowFailure();
            }
            finally
            {
                Volatile.Write(ref _layerActive, 0);
            }
        }

        /// <summary>
        /// True when the workers are between layers and can take a read-only index batch. The ordered
        /// layer commits call pool selection too, and must keep using the serial path because their
        /// own workers are still expanding parents.
        /// </summary>
        internal bool CanRunBatch => !_shutdown && Volatile.Read(ref _layerActive) == 0;

        /// <summary>
        /// Runs independent indexes 0..<paramref name="count"/> on the search thread and the existing
        /// search workers. Each index writes its own result slot; the caller reduces them in index
        /// order, so the outcome does not depend on which thread ran an item.
        /// </summary>
        internal void RunBatch(int count, Action<int> work)
        {
            if (count <= 0)
                return;
            if (count < MinimumBatchItems || _threads.Length == 0 || !CanRunBatch)
            {
                for (int index = 0; index < count; index++)
                    work(index);
                return;
            }
            _batchJob.Prepare(work, count);
            _batchWorkersDone = 0;
            lock (_gate)
            {
                _job = _batchJob;
                _generation++;
                Monitor.PulseAll(_gate);
            }
            try
            {
                _batchJob.RunSearch();
            }
            finally
            {
                // Workers report through a one-shot event instead of contending on _gate, which is
                // the dominant wake cost for the small selection batches.
                while (Volatile.Read(ref _batchWorkersDone) < _threads.Length)
                    _batchCompleted.Wait();
                _batchCompleted.Reset();
                lock (_gate)
                    _job = null;
            }
            _batchJob.RethrowFailure();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _shutdown = true;
                Monitor.PulseAll(_gate);
            }
            foreach (Thread thread in _threads)
                thread.Join();
            _batchCompleted.Dispose();
            if (_solver._options.Pacer is not { } pacer)
                return;
            foreach (SearchWorkPacer? worker in _threadPacers)
            {
                if (worker != null)
                    pacer.DrainFrom(worker);
            }
        }

        private void WorkerMain(int id)
        {
            int seen = 0;
            while (true)
            {
                IWorkerJob job;
                lock (_gate)
                {
                    while (!_shutdown && _generation == seen)
                        Monitor.Wait(_gate);
                    if (_shutdown)
                        return;
                    seen = _generation;
                    job = _job!;
                }
                try
                {
                    job.RunWorker(_threadPacers[id]);
                }
                finally
                {
                    if (job is BatchJob)
                    {
                        if (Interlocked.Increment(ref _batchWorkersDone) == _threads.Length)
                            _batchCompleted.Set();
                    }
                    else
                    {
                        lock (_gate)
                        {
                            _workersDone++;
                            Monitor.PulseAll(_gate);
                        }
                    }
                }
            }
        }

        /// <summary>A reusable unsynchronized index batch; only one batch is active at a time.</summary>
        private sealed class BatchJob : IWorkerJob
        {
            private Action<int>? _work;
            private int _count;
            private int _next;
            private ExceptionDispatchInfo? _failure;
            private readonly object _gate = new();

            internal void Prepare(Action<int> work, int count)
            {
                _work = work;
                _count = count;
                _next = 0;
                _failure = null;
            }

            public void RunWorker(SearchWorkPacer? pacer)
            {
                try
                {
                    Action<int> work = _work!;
                    while (true)
                    {
                        int index = Interlocked.Increment(ref _next) - 1;
                        if (index >= _count)
                            return;
                        work(index);
                    }
                }
                // Any worker failure is handed back to the search thread and rethrown there by
                // RunBatch after every worker has stopped touching the batch. This is ownership
                // transfer, not suppression: the caller observes the original exception object, the
                // same contract LayerJob has.
                catch (Exception error)
                {
                    lock (_gate)
                        _failure ??= ExceptionDispatchInfo.Capture(error);
                }
            }

            internal void RunSearch() => RunWorker(null);

            internal void RethrowFailure()
            {
                ExceptionDispatchInfo? failure;
                lock (_gate)
                    failure = _failure;
                failure?.Throw();
            }
        }

        /// <summary>One action layer's parent queue, result slots and ordered commit cursor.</summary>
        private sealed class LayerJob : IWorkerJob
        {
            private const int SlotFree = 0;
            private const int SlotClaimed = 1;
            private const int SlotReady = 2;

            private sealed class Slot
            {
                public int Index = -1;
                public int State = SlotFree;
                public bool Overflow;
            }

            private readonly BeamSolver _solver;
            private readonly Pool _current;
            private readonly Pool _next;
            private readonly Pool _ended;
            private readonly TerminalSink _terminal;
            private readonly ParentExpansionContext[] _contexts;
            private readonly ExpansionWorkspace[] _workspaces;
            private readonly int _window;
            private readonly int _count;
            private readonly object _gate = new();
            private readonly object _commitGate = new();
            private readonly object _windowGate = new();
            private readonly Slot[] _slots;
            private int _nextClaim;
            private int _commitCursor;
            private int _publishVersion;
            private int _windowVersion;
            private volatile bool _stop;
            private ExceptionDispatchInfo? _failure;

            internal LayerJob(ParallelExpansion owner, Pool current, Pool next, Pool ended,
                TerminalSink terminal, ParentExpansionContext[] contexts, int window, int count)
            {
                _solver = owner._solver;
                _current = current;
                _next = next;
                _ended = ended;
                _terminal = terminal;
                _contexts = contexts;
                _workspaces = owner._workspaces;
                _window = window;
                _count = count;
                _slots = new Slot[window];
                for (int index = 0; index < _slots.Length; index++)
                    _slots[index] = new Slot();
            }

            internal void RunSearch(SearchWorkPacer? pacer)
            {
                for (int index = 0; index < _count; index++)
                {
                    if (_solver.OutOfBudget(_solver._solveWatch!))
                        return;
                    _solver.SearchCheckpoint();
                    _solver.CountExpanded();
                    while (true)
                    {
                        if (WaitForCommit(index, out int claim))
                            break;
                        if (claim < 0)
                            return;
                        Expand(claim, pacer);
                    }
                    Commit(index);
                }
            }

            public void RunWorker(SearchWorkPacer? pacer)
            {
                try
                {
                    while (true)
                    {
                        int index = ClaimWorker();
                        if (index < 0)
                            return;
                        Expand(index, pacer);
                    }
                }
                catch (Exception error)
                {
                    Fail(error);
                }
            }

            internal void Stop()
            {
                lock (_gate)
                    _stop = true;
                SignalCommit();
                SignalWindow(wakeAll: true);
            }

            internal void RethrowFailure()
            {
                ExceptionDispatchInfo? failure;
                lock (_gate)
                    failure = _failure;
                failure?.Throw();
            }

            private bool CanClaim() => _nextClaim < _count && _nextClaim < _commitCursor + _window;

            private bool ReadyFast(int index)
            {
                Slot slot = _slots[index % _window];
                return slot.Index == index && Volatile.Read(ref slot.State) == SlotReady;
            }

            private int ClaimWorker()
            {
                SpinWait spin = default;
                for (int attempt = 0; attempt < SpinsBeforeBlocking; attempt++)
                {
                    if (_stop)
                        return -1;
                    if (Volatile.Read(ref _nextClaim) >= _count)
                        return -1;
                    if (Volatile.Read(ref _nextClaim) < Volatile.Read(ref _commitCursor) + _window)
                        break;
                    spin.SpinOnce();
                }
                while (true)
                {
                    int version;
                    lock (_windowGate)
                        version = Volatile.Read(ref _windowVersion);
                    lock (_gate)
                    {
                        if (_failure != null || _stop || _nextClaim >= _count)
                            return -1;
                        if (CanClaim())
                            return ClaimLocked();
                    }
                    lock (_windowGate)
                    {
                        if (Volatile.Read(ref _windowVersion) == version && !_stop
                            && Volatile.Read(ref _nextClaim) < _count)
                        {
                            Monitor.Wait(_windowGate);
                        }
                    }
                }
            }

            /// <summary>
            /// Waits until the commit slot is ready. While waiting, the search thread claims the next
            /// unstarted parent itself; <paramref name="claim"/> returns -1 when the layer stopped.
            /// </summary>
            private bool WaitForCommit(int index, out int claim)
            {
                claim = -1;
                SpinWait spin = default;
                for (int attempt = 0; attempt < SpinsBeforeBlocking; attempt++)
                {
                    if (_stop || ReadyFast(index))
                        break;
                    spin.SpinOnce();
                }
                while (true)
                {
                    lock (_gate)
                    {
                        if (_failure != null)
                            _failure.Throw();
                        if (_stop)
                            return false;
                        if (ReadyFast(index))
                            return true;
                        if (CanClaim())
                        {
                            claim = ClaimLocked();
                            return false;
                        }
                    }
                    lock (_commitGate)
                    {
                        int version = Volatile.Read(ref _publishVersion);
                        if (ReadyFast(index))
                            return true;
                        if (_failure != null)
                            _failure.Throw();
                        if (_stop)
                            return false;
                        if (Volatile.Read(ref _publishVersion) == version)
                            Monitor.Wait(_commitGate);
                    }
                }
            }

            private int ClaimLocked()
            {
                int index = _nextClaim++;
                Slot slot = _slots[index % _window];
                slot.Index = index;
                Volatile.Write(ref slot.Overflow, false);
                Volatile.Write(ref slot.State, SlotClaimed);
                return index;
            }

            private void Expand(int index, SearchWorkPacer? pacer)
            {
                try
                {
                    _solver.WorkerCheckpoint(pacer);
                    ExpansionWorkspace workspace = _workspaces[index % _window];
                    ref readonly Battle parent = ref _current.State(index);
                    workspace.ResetDiagnostics();
                    bool buffered = workspace.SupportsBufferedExpansion
                        && workspace.Expand(in parent, in _contexts[index], _solver._cancellationToken);
                    lock (_gate)
                    {
                        Slot slot = _slots[index % _window];
                        Volatile.Write(ref slot.Overflow, !buffered);
                        Volatile.Write(ref slot.State, SlotReady);
                    }
                    lock (_commitGate)
                    {
                        _publishVersion++;
                        Monitor.Pulse(_commitGate);
                    }
                }
                catch (Exception error)
                {
                    Fail(error);
                }
            }

            private void Commit(int index)
            {
                ExpansionWorkspace workspace = _workspaces[index % _window];
                ref readonly Battle parent = ref _current.State(index);
                bool overflow;
                lock (_gate)
                    overflow = Volatile.Read(ref _slots[index % _window].Overflow);
                int duplicatesBefore = _solver._duplicates;
                if (overflow)
                {
                    _solver._workspaceOverflowParents++;
                    workspace.ResetDiagnostics();
                    long directStarted = Stopwatch.GetTimestamp();
                    workspace.ExpandDirect(in parent, in _contexts[index], _next, _ended,
                        _terminal, _solver._cancellationToken);
                    _solver._workspaceDirectTicks += Stopwatch.GetTimestamp() - directStarted;
                }
                else
                {
                    workspace.Commit(in parent, in _contexts[index], _next, _ended, _terminal);
                }
                _solver.ObserveParent(workspace, duplicatesBefore);
                _solver.AccumulateWorkspaceDiagnostics(workspace);
                lock (_gate)
                {
                    Slot slot = _slots[index % _window];
                    Volatile.Write(ref slot.State, SlotFree);
                    slot.Index = -1;
                    _commitCursor = index + 1;
                }
                SignalWindow(wakeAll: false);
            }

            private void Fail(Exception error)
            {
                lock (_gate)
                {
                    _failure ??= ExceptionDispatchInfo.Capture(error);
                    _stop = true;
                }
                SignalCommit();
                SignalWindow(wakeAll: true);
            }

            private void SignalCommit()
            {
                lock (_commitGate)
                {
                    _publishVersion++;
                    Monitor.PulseAll(_commitGate);
                }
            }

            private void SignalWindow(bool wakeAll)
            {
                lock (_windowGate)
                {
                    _windowVersion++;
                    if (wakeAll)
                        Monitor.PulseAll(_windowGate);
                    else
                        Monitor.Pulse(_windowGate);
                }
            }
        }
    }

    /// <summary>
    /// Worker-side checkpoint: the same cancellation, deadline and pacer points the single-thread
    /// expansion loop runs around each parent, using the calling worker's pacer.
    /// </summary>
    private void WorkerCheckpoint(SearchWorkPacer? pacer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (DeadlineExpired())
            throw new SearchDeadlineException();
        pacer?.YieldIfNeeded();
        _cancellationToken.ThrowIfCancellationRequested();
        if (DeadlineExpired())
            throw new SearchDeadlineException();
    }

    /// <summary>Records the largest total retained workspace size after a layer.</summary>
    private void ObserveWorkspacePeak()
    {
        long total = 0;
        long growable = 0;
        foreach (ExpansionWorkspace workspace in _workspaces)
        {
            total += workspace.RetainedBytes;
            growable += workspace.GrowableBytes;
        }
        if (total > _workspacePeakBytes)
            _workspacePeakBytes = total;
        if (growable > _workspaceGrowablePeakBytes)
            _workspaceGrowablePeakBytes = growable;
    }

    /// <summary>Freezes every parent of this layer before any worker can start expanding one.</summary>
    private void CaptureLayerContexts(Pool current)
    {
        if (_layerContexts.Length < current.Count)
            _layerContexts = new ParentExpansionContext[current.Count];
        for (int index = 0; index < current.Count; index++)
            _layerContexts[index] = CaptureParentContext(current, index);
    }

    /// <summary>
    /// Moves one committed workspace's end-turn diagnostics into the solve totals. Only called on
    /// the commit thread, after that parent is committed and before its slot is reused.
    /// </summary>
    private void AccumulateWorkspaceDiagnostics(ExpansionWorkspace workspace)
    {
        _workspaceEquivalence = _workspaceEquivalence.Add(workspace.EquivalenceDiagnostics());
        _workspacePrefixShared += workspace.EndTurnPrefixShared;
        _workspacePrefixUnshared += workspace.EndTurnPrefixUnshared;
        _workspacePrefixVerified += workspace.EndTurnPrefixVerified;
        _workspacePrefixReusedLeaves += workspace.EndTurnPrefixReusedLeaves;
        _workspacePrefixChoiceUnshared += workspace.EndTurnPrefixChoiceUnshared;
        _workspacePrefixExtraTurnUnshared += workspace.EndTurnPrefixExtraTurnUnshared;
        _workspacePrefixNotInProgress += workspace.EndTurnPrefixNotInProgress;
        _workspaceTurnLoopGuarded += workspace.TurnLoopGuarded;
        _workspaceCrossTurnNoProgressPruned += workspace.CrossTurnNoProgressPruned;
        workspace.ResetDiagnostics();
    }

    private int EndTurnPrefixSharedTotal => _workspacePrefixShared;
    private int EndTurnPrefixUnsharedTotal => _workspacePrefixUnshared;
    private int EndTurnPrefixVerifiedTotal => _workspacePrefixVerified;
    private int EndTurnPrefixReusedLeavesTotal => _workspacePrefixReusedLeaves;
    private int EndTurnPrefixChoiceUnsharedTotal => _workspacePrefixChoiceUnshared;
    private int EndTurnPrefixExtraTurnUnsharedTotal => _workspacePrefixExtraTurnUnshared;
    private int EndTurnPrefixNotInProgressTotal => _workspacePrefixNotInProgress;
    private int TurnLoopGuardedTotal => _workspaceTurnLoopGuarded;
    private int CrossTurnNoProgressPrunedTotal => _workspaceCrossTurnNoProgressPruned;
}
