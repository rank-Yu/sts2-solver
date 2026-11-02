using System.Globalization;

namespace Sts2Solver;

internal enum SearchMemoryBudgetSource
{
    Default,
    Settings,
    Environment,
}

internal readonly record struct SearchMemoryBudgetResolution(
    long Bytes,
    SearchMemoryBudgetSource Source,
    long? SettingsBytes,
    string? EnvironmentValue)
{
    internal string SourceName => Source switch
    {
        SearchMemoryBudgetSource.Default => "default",
        SearchMemoryBudgetSource.Settings => "settings",
        SearchMemoryBudgetSource.Environment => "environment",
        _ => throw new ArgumentOutOfRangeException(nameof(Source)),
    };
}

/// <summary>
/// Resolves the Mod's search-memory ceiling. The search core only receives the resulting byte count;
/// it never reads process or GC state, so the same request still produces the same route offline.
/// Priority: <c>STS2_SOLVER_SEARCH_MEMORY_MB</c>, then the settings override, then 20% of the total
/// memory the runtime may use (<see cref="GC.GetGCMemoryInfo"/>), with a 1 GiB floor. The value is a
/// fixed per-machine ceiling, not the momentary free memory.
/// </summary>
internal static class SearchMemoryBudget
{
    /// <summary>Optional override in MiB. Documented in <c>docs/mod.md</c>.</summary>
    internal const string OverrideVariable = "STS2_SOLVER_SEARCH_MEMORY_MB";

    private const long Mebibyte = 1024L * 1024L;
    private const long FloorBytes = 1L << 30;

    private static readonly object Gate = new();
    private static string? _cachedKey;
    private static SearchMemoryBudgetResolution _cached;

    internal static long Bytes => Resolve().Bytes;

    internal static SearchMemoryBudgetResolution Resolve()
    {
        long? settingsBytes = SolverSettings.Current.Search.MemoryBudgetBytes;
        string? environment = Environment.GetEnvironmentVariable(OverrideVariable);
        lock (Gate)
        {
            string key = $"{settingsBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{environment ?? "-"}";
            if (_cachedKey == key)
                return _cached;
            SearchMemoryBudgetResolution resolution = ResolveCore(settingsBytes, environment,
                DefaultBytes(), out bool ignoredEnvironment);
            if (ignoredEnvironment)
            {
                ModLog.Error(
                    $"[Sts2Solver] SEARCH_MEMORY_IGNORED value={environment} " +
                    $"reason=not_positive_integer variable={OverrideVariable}");
            }
            ModLog.Info(
                $"[Sts2Solver] SEARCH_MEMORY source={resolution.SourceName} bytes={resolution.Bytes} " +
                $"mib={Megabytes(resolution.Bytes)} setting_bytes={resolution.SettingsBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
                $"env={resolution.EnvironmentValue ?? "-"}");
            _cachedKey = key;
            _cached = resolution;
            return resolution;
        }
    }

    /// <summary>Pure resolution used by tests; invalid environment values fall through to settings.</summary>
    internal static SearchMemoryBudgetResolution Resolve(long? settingsBytes, string? environmentValue,
        long fallbackBytes)
        => ResolveCore(settingsBytes, environmentValue, fallbackBytes, out _);

    internal static long DefaultBytes() => DefaultBytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>20% of <paramref name="availableBytes"/>, at least 1 GiB.</summary>
    internal static long DefaultBytes(long availableBytes)
    {
        if (availableBytes <= 0)
            return FloorBytes;
        // Integer arithmetic avoids overflowing the 20% product on very large machines.
        return Math.Max(FloorBytes, availableBytes / 100 * 20 + availableBytes % 100 * 20 / 100);
    }

    private static SearchMemoryBudgetResolution ResolveCore(long? settingsBytes, string? environmentValue,
        long fallbackBytes, out bool ignoredEnvironment)
    {
        ignoredEnvironment = false;
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            if (long.TryParse(environmentValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long mebibytes)
                && mebibytes > 0)
            {
                long bytes = mebibytes > long.MaxValue / Mebibyte
                    ? long.MaxValue
                    : mebibytes * Mebibyte;
                return new(bytes, SearchMemoryBudgetSource.Environment, settingsBytes, environmentValue);
            }
            ignoredEnvironment = true;
        }
        if (settingsBytes is > 0)
            return new(settingsBytes.Value, SearchMemoryBudgetSource.Settings, settingsBytes, environmentValue);
        return new(fallbackBytes, SearchMemoryBudgetSource.Default, settingsBytes, environmentValue);
    }

    private static string Megabytes(long bytes)
        => (bytes / (double)Mebibyte).ToString("0.0", CultureInfo.InvariantCulture);
}
