namespace Sts2Solver.Ai.Combat;

internal sealed class SearchDeadlineException : Exception;

internal sealed partial class BeamSolver
{
    /// <summary>
    /// The cancellation/frame-pressure checkpoint the existing search performs around each parent and
    /// replay step. <see cref="SearchWorkPacer.YieldIfNeeded"/> internally rate-limits its work slice;
    /// the cancellation check runs on every call so a cancel is observed inside one action loop.
    /// </summary>
    private void SearchCheckpoint()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (DeadlineExpired())
        {
            _deadlineReached = true;
            throw new SearchDeadlineException();
        }
        _options.Pacer?.YieldIfNeeded();
        _cancellationToken.ThrowIfCancellationRequested();
        if (DeadlineExpired())
        {
            _deadlineReached = true;
            throw new SearchDeadlineException();
        }
    }
}
