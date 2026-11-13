using System.Runtime.CompilerServices;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Raised when a search-owned allocation would cross <see cref="BeamOptions.SearchMemoryBudgetBytes"/>.
/// The solver catches it at the search loop and returns the best result found so far, exactly like the
/// node limit. It deliberately does not derive from <see cref="CapacityException"/> so a single-branch
/// capacity handler cannot turn a budget stop into a dropped child.
/// </summary>
internal sealed class SearchMemoryBudgetException : Exception
{
    internal SearchMemoryBudgetException(string region, long usedBytes, long limitBytes)
        : base($"搜索内存预算不足：region={region} used={usedBytes} limit={limitBytes}。")
    {
        Region = region;
        UsedBytes = usedBytes;
        LimitBytes = limitBytes;
    }

    internal string Region { get; }

    internal long UsedBytes { get; }

    internal long LimitBytes { get; }
}

/// <summary>
/// Logical search-memory ledger. It counts allocated search-owned blocks, never GC or process
/// memory, so the offline host and the game see the same decisions. Every solve has a positive limit.
/// </summary>
internal sealed class SearchMemoryLedger
{
    /// <summary>Bytes currently retained by this storage.</summary>
    internal long UsedBytes { get; private set; }

    /// <summary>Highest usage observed during the current solve.</summary>
    internal long PeakBytes { get; private set; }

    /// <summary>Limit of the current solve; always positive.</summary>
    internal long LimitBytes { get; private set; }

    /// <summary>True once the current solve requested a block that would cross the limit.</summary>
    internal bool Exceeded { get; private set; }

    /// <summary>Usage at the first rejected allocation of the current solve.</summary>
    internal long ExceededAtBytes { get; private set; }

    /// <summary>Currently retained bytes by accounting region (diagnostics only).</summary>
    internal IReadOnlyDictionary<string, long> AllocatedByRegion => _byRegion;

    private readonly Dictionary<string, long> _byRegion = new(StringComparer.Ordinal);

    /// <summary>Release unused, reusable blocks before rejecting an allocation.</summary>
    internal Action<long>? ReclaimRetainedBytes { get; set; }

    internal void BeginSolve(long limitBytes)
    {
        if (limitBytes <= 0)
            throw new InvalidDataException("搜索内存预算必须为正。");
        // Per-solve structures are no longer reachable after the previous solve.
        foreach (string region in new[] { "pool-aux", "candidate-routes", "pool-labels", "routing-signature" })
        {
            UsedBytes -= _byRegion.GetValueOrDefault(region);
            _byRegion.Remove(region);
        }
        LimitBytes = limitBytes;
        if (UsedBytes > LimitBytes)
            ReclaimRetainedBytes?.Invoke(UsedBytes - LimitBytes);
        PeakBytes = UsedBytes;
        Exceeded = false;
        ExceededAtBytes = 0;
        if (UsedBytes > LimitBytes)
        {
            Exceeded = true;
            ExceededAtBytes = UsedBytes;
        }
    }

    /// <summary>
    /// Charges one logical block. The block size is the allocated array/chunk length, not the live
    /// payload. A rejected block is not counted, so <see cref="UsedBytes"/> never exceeds the limit.
    /// </summary>
    internal void Charge(long bytes, string region)
    {
        if (bytes <= 0)
            return;
        if (Exceeded)
            throw new SearchMemoryBudgetException(region, ExceededAtBytes, LimitBytes);
        if (UsedBytes + bytes > LimitBytes)
            ReclaimRetainedBytes?.Invoke(UsedBytes + bytes - LimitBytes);
        if (UsedBytes + bytes > LimitBytes)
        {
            Exceeded = true;
            ExceededAtBytes = UsedBytes;
            throw new SearchMemoryBudgetException(region, UsedBytes, LimitBytes);
        }
        UsedBytes += bytes;
        _byRegion[region] = _byRegion.GetValueOrDefault(region) + bytes;
        if (UsedBytes > PeakBytes)
            PeakBytes = UsedBytes;
    }

    internal void ReleaseBlock(long bytes, string region)
    {
        if (bytes <= 0)
            return;
        UsedBytes = Math.Max(0, UsedBytes - bytes);
        _byRegion[region] = Math.Max(0, _byRegion.GetValueOrDefault(region) - bytes);
    }

    /// <summary>Releases a retained block that was replaced; used only for the growable int scratch.</summary>
    internal void Release(long bytes)
    {
        if (bytes <= 0)
            return;
        UsedBytes = Math.Max(0, UsedBytes - bytes);
        long scratch = _byRegion.GetValueOrDefault("scratch") - bytes;
        _byRegion["scratch"] = Math.Max(0, scratch);
    }
}


/// <summary>Capacity planning for one packed state arena; all byte arithmetic is 64-bit.</summary>
internal static class SearchMemoryBudget
{
    // A power-of-two deduplication table needs two slots per state and uses signed int indexes.
    internal const int MaximumPoolStates = 1 << 29;

    /// <summary>
    /// Pre-packed seat geometry: the old 64-state block of 12,696-byte states. The seat bound only
    /// sizes the side arrays and the per-action pool boundary; actual bytes are charged by the
    /// packed arena ledger. Keeping the block geometry keeps every ≤96-card search decision
    /// (including Beam.Pool drops) identical.
    /// </summary>
    internal const int PlanningStateBytes = 12_696;
    internal const int PlanningBlockStates = 64;
    internal const int PlanningBlockBytes = PlanningStateBytes * PlanningBlockStates;

    internal static int PoolStateCapacity(long budgetBytes, int blockBytes, int statesPerBlock)
    {
        if (budgetBytes <= 0 || blockBytes <= 0 || statesPerBlock <= 0
            || (statesPerBlock & (statesPerBlock - 1)) != 0)
            throw new InvalidDataException("状态池预算或区块无效。");
        long blocks = budgetBytes / blockBytes;
        long states = Math.Max(statesPerBlock, blocks * statesPerBlock);
        if (states > MaximumPoolStates)
            throw new InvalidDataException("状态池预算超过可表示容量。");
        return checked((int)states);
    }
}
