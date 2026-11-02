using System.Globalization;

namespace Sts2Solver;

internal enum SearchThreadCountSource
{
    Default,
    Settings,
    Environment,
}

internal readonly record struct SearchThreadCountResolution(
    int Threads,
    SearchThreadCountSource Source,
    int LogicalProcessors,
    string? EnvironmentValue)
{
    internal string SourceName => Source switch
    {
        SearchThreadCountSource.Default => "default",
        SearchThreadCountSource.Settings => "settings",
        SearchThreadCountSource.Environment => "environment",
        _ => throw new ArgumentOutOfRangeException(nameof(Source)),
    };
}

/// <summary>
/// Resolves how many threads one search member may use. The search core only receives the resulting
/// count; it never reads processor or environment state, so the same request still produces the same
/// route offline. Priority is <c>STS2_SOLVER_SEARCH_THREADS</c>, then the persisted setting, then
/// <c>clamp(ProcessorCount - 2, 1, 8)</c>; both explicit values are clamped to 1..8. The value is
/// resolved once on the game main thread because it is a fixed per-machine setting.
/// </summary>
internal static class SearchThreadCount
{
    /// <summary>Optional positive integer override. Documented in <c>docs/mod.md</c>.</summary>
    internal const string OverrideVariable = "STS2_SOLVER_SEARCH_THREADS";

    internal const int Maximum = 8;

    private static readonly object Gate = new();
    private static SearchThreadCountResolution? _lastLogged;

    internal static int Value => ResolveForSearch().Threads;

    internal static SearchThreadCountResolution Resolve()
    {
        lock (Gate)
            return ResolveForSearchLocked();
    }

    /// <summary>Reads the environment and the current setting; the setting can change between searches.</summary>
    internal static SearchThreadCountResolution ResolveForSearch()
    {
        lock (Gate)
            return ResolveForSearchLocked();
    }

    private static SearchThreadCountResolution ResolveForSearchLocked()
    {
        string? environment = Environment.GetEnvironmentVariable(OverrideVariable);
        int? settings = SolverSettings.Current.Search.Threads;
        SearchThreadCountResolution resolution = ResolveCore(environment, Environment.ProcessorCount,
            settings, out bool ignoredEnvironment, out bool clampedEnvironment);
        if (_lastLogged is { } previous
            && previous.Threads == resolution.Threads
            && previous.Source == resolution.Source
            && previous.LogicalProcessors == resolution.LogicalProcessors)
        {
            return resolution;
        }
        if (ignoredEnvironment)
        {
            ModLog.Error(
                $"[Sts2Solver] SEARCH_THREADS_IGNORED value={environment} " +
                $"reason=not_positive_integer variable={OverrideVariable}");
        }
        else if (clampedEnvironment)
        {
            ModLog.Info(
                $"[Sts2Solver] SEARCH_THREADS_CLAMPED value={environment} " +
                $"clamped={resolution.Threads} range=1..{Maximum} variable={OverrideVariable}");
        }
        ModLog.Info(
            $"[Sts2Solver] SEARCH_THREADS value={resolution.Threads} source={resolution.SourceName} " +
            $"logical={resolution.LogicalProcessors}");
        _lastLogged = resolution;
        return resolution;
    }

    /// <summary>Pure resolution used by tests; invalid environment values fall back to the default.</summary>
    internal static SearchThreadCountResolution Resolve(string? environmentValue, int logicalProcessors)
        => ResolveCore(environmentValue, logicalProcessors, null, out _, out _);

    /// <summary>Pure resolution with the persisted setting between environment and default.</summary>
    internal static SearchThreadCountResolution Resolve(string? environmentValue, int logicalProcessors,
        int? settingsThreads)
        => ResolveCore(environmentValue, logicalProcessors, settingsThreads, out _, out _);

    private static SearchThreadCountResolution ResolveCore(string? environmentValue, int logicalProcessors,
        int? settingsThreads, out bool ignoredEnvironment, out bool clampedEnvironment)
    {
        ignoredEnvironment = false;
        clampedEnvironment = false;
        int logical = Math.Max(1, logicalProcessors);
        int fallback = Math.Clamp(logical - 2, 1, Maximum);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            if (int.TryParse(environmentValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int requested)
                && requested > 0)
            {
                int threads = Math.Clamp(requested, 1, Maximum);
                clampedEnvironment = threads != requested;
                return new(threads, SearchThreadCountSource.Environment, logical, environmentValue);
            }
            ignoredEnvironment = true;
        }
        if (settingsThreads is { } configured)
            return new(Math.Clamp(configured, 1, Maximum), SearchThreadCountSource.Settings,
                logical, environmentValue);
        return new(fallback, SearchThreadCountSource.Default, logical, environmentValue);
    }
}
