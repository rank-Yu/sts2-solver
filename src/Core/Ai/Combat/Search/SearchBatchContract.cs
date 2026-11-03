namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    private bool _batchFailureContractDone;

    /// <summary>
    /// Default-off verification only: proves a worker exception from <see cref="ParallelExpansion.RunBatch"/>
    /// is rethrown on the search thread and that the same workers can immediately run another batch.
    /// The solve then continues, so the next real batch and the following action layers also run on
    /// this same <see cref="ParallelExpansion"/> instance.
    /// </summary>
    private void RunSelectionBatchFailureContract(ParallelExpansion parallel)
    {
        if (_batchFailureContractDone)
            return;
        _batchFailureContractDone = true;
        int callerThread = Environment.CurrentManagedThreadId;
        Exception injected = new InvalidOperationException("selection-batch-failure-contract");
        SearchVerificationHooks.SelectionBatchFailure = injected;
        int workerThrew = 0;
        bool caught = false;
        try
        {
            parallel.RunBatch(64, index =>
            {
                if (Environment.CurrentManagedThreadId == callerThread)
                {
                    // Let a worker claim an item first; this also makes the contract deterministic
                    // when the search thread is faster than the wake-up path.
                    if (!SpinWait.SpinUntil(() => Volatile.Read(ref workerThrew) != 0,
                            TimeSpan.FromSeconds(10)))
                    {
                        throw new InvalidDataException(
                            "selection-batch-failure-contract: no worker claimed the failing batch.");
                    }
                    return;
                }
                if (Interlocked.Exchange(ref workerThrew, 1) == 0)
                    throw injected;
            });
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, injected))
        {
            caught = true;
        }
        if (!caught)
        {
            throw new InvalidDataException(
                "selection-batch-failure-contract: RunBatch did not rethrow the worker exception.");
        }
        SearchVerificationHooks.SelectionBatchFailureObserved = injected;

        int completed = 0;
        parallel.RunBatch(16, _ => Interlocked.Increment(ref completed));
        if (completed != 16)
        {
            throw new InvalidDataException(
                $"selection-batch-failure-contract: recovery batch completed {completed}/16.");
        }
        SearchVerificationHooks.SelectionBatchRecoveryItems = completed;
    }
}
