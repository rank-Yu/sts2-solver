namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Monotonic decision clock used to report elapsed time and schedule member deadlines. The default
/// reader is <see cref="Environment.TickCount64"/>; a caller-supplied reader is also invoked from
/// expansion worker threads through <c>DeadlineExpired</c>, so it must be thread-safe.
/// </summary>
internal sealed class SearchClock
{
    private readonly Func<long> _clock;
    private readonly long _started;

    internal SearchClock(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _started = _clock();
    }

    internal long Now => _clock();
    internal long Elapsed => Math.Max(0, Now - _started);

    /// <summary>
    /// Search share of one member's configured slot budget. Keeps the historical replay reserve
    /// (100 ms to 2 s, 10% of the configured budget) so the production deadline is unchanged.
    /// </summary>
    internal static long MemberSearchMilliseconds(long configured)
    {
        long share = Math.Max(1, configured);
        return Math.Max(1, share - Math.Min(2_000, Math.Max(100, share / 10)));
    }
}
