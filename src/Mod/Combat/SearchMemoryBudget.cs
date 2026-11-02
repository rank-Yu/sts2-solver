using System.Globalization;

namespace Sts2Solver;

internal enum SearchMemoryBudgetSource
{
    Default,
    Settings,
}

internal readonly record struct SearchMemoryBudgetResolution(
    long Bytes,
    SearchMemoryBudgetSource Source,
    long? SettingsBytes)
{
    internal string SourceName => Source switch
    {
        SearchMemoryBudgetSource.Default => "default",
        SearchMemoryBudgetSource.Settings => "settings",
        _ => throw new ArgumentOutOfRangeException(nameof(Source)),
    };
}

/// <summary>
/// Resolves the Mod's search-memory ceiling. The search core only receives the resulting byte count;
/// it never reads process or GC state, so the same request still produces the same route offline.
/// Priority: the settings override, then 20% of the total memory the runtime may use
/// (<see cref="GC.GetGCMemoryInfo"/>), with a 1 GiB floor. The value is a fixed per-machine ceiling,
/// not the momentary free memory.
/// </summary>
internal static class SearchMemoryBudget
{
    private const long Mebibyte = 1024L * 1024L;
    private const long FloorBytes = 1L << 30;

    private static readonly object Gate = new();
    private static string? _cachedKey;
    private static SearchMemoryBudgetResolution _cached;

    internal static long Bytes => Resolve().Bytes;

    internal static SearchMemoryBudgetResolution Resolve()
    {
        long? settingsBytes = SolverSettings.Current.Search.MemoryBudgetBytes;
        lock (Gate)
        {
            string key = settingsBytes?.ToString(CultureInfo.InvariantCulture) ?? "-";
            if (_cachedKey == key)
                return _cached;
            SearchMemoryBudgetResolution resolution = ResolveCore(settingsBytes, DefaultBytes());
            ModLog.Info(
                $"[Sts2Solver] SEARCH_MEMORY source={resolution.SourceName} bytes={resolution.Bytes} " +
                $"mib={Megabytes(resolution.Bytes)} setting_bytes={resolution.SettingsBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            _cachedKey = key;
            _cached = resolution;
            return resolution;
        }
    }

    /// <summary>Pure resolution used by tests; a non-positive setting falls through to the default.</summary>
    internal static SearchMemoryBudgetResolution Resolve(long? settingsBytes, long fallbackBytes)
        => ResolveCore(settingsBytes, fallbackBytes);

    internal static long DefaultBytes() => DefaultBytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>20% of <paramref name="availableBytes"/>, at least 1 GiB.</summary>
    internal static long DefaultBytes(long availableBytes)
    {
        if (availableBytes <= 0)
            return FloorBytes;
        // Integer arithmetic avoids overflowing the 20% product on very large machines.
        return Math.Max(FloorBytes, availableBytes / 100 * 20 + availableBytes % 100 * 20 / 100);
    }

    private static SearchMemoryBudgetResolution ResolveCore(long? settingsBytes, long fallbackBytes)
    {
        if (settingsBytes is > 0)
            return new(settingsBytes.Value, SearchMemoryBudgetSource.Settings, settingsBytes);
        return new(fallbackBytes, SearchMemoryBudgetSource.Default, settingsBytes);
    }

    private static string Megabytes(long bytes)
        => (bytes / (double)Mebibyte).ToString("0.0", CultureInfo.InvariantCulture);
}
