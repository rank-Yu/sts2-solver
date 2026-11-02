namespace Sts2Solver;

internal enum SearchThreadCountSource
{
    Default,
    Settings,
}

internal readonly record struct SearchThreadCountResolution(
    int Threads,
    SearchThreadCountSource Source,
    int LogicalProcessors);

/// <summary>
/// Resolves how many threads one search member may use. The search core only receives the resulting
/// count; it never reads processor state, so the same request still produces the same route offline.
/// Priority is the persisted setting, then <c>clamp(ProcessorCount - 2, 1, 8)</c>; the setting is
/// clamped to 1..8. The value is resolved on the game main thread because it is a fixed per-machine
/// setting.
/// </summary>
internal static class SearchThreadCount
{
    internal const int Maximum = 8;

    internal static int Value => Resolve().Threads;

    internal static SearchThreadCountResolution Resolve()
        => ResolveCore(Environment.ProcessorCount, SolverSettings.Current.Search.Threads);

    /// <summary>The machine default for <paramref name="logicalProcessors"/>: two fewer, clamped to 1..8.</summary>
    internal static int DefaultFor(int logicalProcessors)
        => Math.Clamp(Math.Max(1, logicalProcessors) - 2, 1, Maximum);

    /// <summary>Pure resolution used by tests; without a persisted setting it follows the default.</summary>
    internal static SearchThreadCountResolution Resolve(int logicalProcessors)
        => ResolveCore(logicalProcessors, null);

    /// <summary>Pure resolution with the persisted setting before the default.</summary>
    internal static SearchThreadCountResolution Resolve(int logicalProcessors, int? settingsThreads)
        => ResolveCore(logicalProcessors, settingsThreads);

    private static SearchThreadCountResolution ResolveCore(int logicalProcessors, int? settingsThreads)
    {
        int logical = Math.Max(1, logicalProcessors);
        if (settingsThreads is { } configured)
            return new(Math.Clamp(configured, 1, Maximum), SearchThreadCountSource.Settings, logical);
        return new(DefaultFor(logical), SearchThreadCountSource.Default, logical);
    }
}
