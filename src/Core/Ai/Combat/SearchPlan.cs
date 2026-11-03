using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal enum SearchEvaluation { Standard, Base, Offensive, Setup, ContinuousSetup }
internal enum SearchRetention { Standard, Power, SecondBand, QueenKillTiming }
internal enum SearchPotionLimit { None = 0, One = 1, Allowed = 2 }

/// <summary>Each member describes the four independent search dimensions.</summary>
internal sealed record SearchMember(int Width, SearchEvaluation Evaluation,
    SearchRetention Retention, SearchPotionLimit Potions);

internal sealed record SearchPlan(int BaselineWidth, int NarrowWidth, int WideWidth,
    int OffensiveWidth, long MemoryBytes)
{
    /// <summary>Time per beam slot; every member gets an independent deadline.</summary>
    public int MillisecondsPerWidth { get; init; }

    /// <summary>Whole-request deadline when the host sets one; overrides the member sum in the UI budget.</summary>
    public long? TotalBudgetMilliseconds { get; init; }

    /// <summary>Encounter-specific default portfolio.</summary>
    public bool QueenStrategies { get; init; }

    /// <summary>Generic setup members cover the queen opening's setup behavior.</summary>
    public bool SetupStrategies { get; init; }

    /// <summary>Wide continuous-gain supplement in the Allowed stage only.</summary>
    public bool ContinuousSetupStrategies { get; init; }

    internal static bool IsLongEncounter(bool isBoss, bool isElite, int actIndex)
        => isBoss || isElite && actIndex == 2;

    public int EndedKeepWidths { get; init; } = 8;
    public int EndedTriggerWidths { get; init; } = 16;
}

/// <summary>A frozen root and policy with the complete decision budget.</summary>
internal sealed class CombatSearchRequest
{
    private readonly SearchCase _case;

    internal CombatSearchRequest(SearchCase searchCase, SearchPlan plan)
    {
        _case = searchCase;
        Plan = plan;
    }

    internal SearchCase Case => _case;
    internal SearchPlan Plan { get; }

    /// <summary>Optional progress publisher for this request; null keeps the search progress-free.</summary>
    internal SearchProgressChannel? Progress { get; init; }

    /// <summary>
    /// Host-only resume point from an earlier stop of the same request; null starts at member zero.
    /// It is never encoded in SREQ/SRES.
    /// </summary>
    internal SearchResumeState? Resume { get; init; }
}

/// <summary>
/// <see cref="CapacityBounded"/>: a member emptied its frontier after dropping children at a
/// <see cref="Battle"/> capacity and no member found a victory, so the best non-victory line may
/// only have survived because better lines were cut. The request returns no route.
/// </summary>
internal enum CombatSearchStatus { Victory, Defeat, Incomplete, Cancelled, Invalid, CapacityBounded }

internal sealed record SearchMemberResult(SearchMember Member, bool Ran, string? SkippedReason,
    long ElapsedMilliseconds, long ExpandedNodes, string StopReason, bool Victory,
    int? HpLost, int? PotionUses, bool Adopted, long MemoryPeakBytes, int EndedCompactions)
{
    public int ActualWidth => Ran ? Member.Width : 0;
    public long MemoryUsedBytes { get; init; }
    public bool MemoryBudgetExceeded { get; init; }
    public long MemoryExceededAtBytes { get; init; }
    public int PoolCapacityDropped { get; init; }
    public long RouteNodeStorageBytes { get; init; }
    public int RouteNodePeakCount { get; init; }
    public long ManagedHeapPeakBytes { get; init; }
    public long WorkingSetPeakBytes { get; init; }
    public int WorkspaceOverflowParents { get; init; }
    public long SearchWorkspacePeakBytes { get; init; }
    public long SearchWorkspaceGrowablePeakBytes { get; init; }
}

internal sealed record CombatSearchResult(CombatSearchStatus Status, BeamResult? Beam,
    int SelectedMember, long ElapsedMilliseconds, long ExpandedNodes,
    SearchMemberResult[] Members, string? Error)
{
    internal long ElapsedMs => ElapsedMilliseconds;
    internal long TotalExpandedNodes => ExpandedNodes;
    internal string FailureStatus => Status.ToString().ToLowerInvariant();
    internal string? FailureError => Error;
    internal int MembersRun => Members.Count(m => m.Ran);
    internal int MembersSkippedByTime => Members.Count(m => m.SkippedReason == "time");
    internal bool SoftTimeRisk => Members.Any(m => m.SkippedReason == "time" || m.StopReason is "time" or "deadline");
    internal bool MemoryBudgetExceeded => Members.Any(m => m.MemoryBudgetExceeded);
    internal long MemoryExceededAtBytes => Members.Select(m => m.MemoryExceededAtBytes).FirstOrDefault(v => v != 0);
    internal int PoolCapacityDropped => (int)Math.Min(int.MaxValue, Members.Sum(m => (long)m.PoolCapacityDropped));
    internal long SearchMemoryUsedBytes => Members.Select(m => m.MemoryUsedBytes).DefaultIfEmpty().Max();
    internal long SearchMemoryPeakBytes => Members.Select(m => m.MemoryPeakBytes).DefaultIfEmpty().Max();
    internal long RouteNodeStorageBytes => Members.Select(m => m.RouteNodeStorageBytes).DefaultIfEmpty().Max();
    internal int RouteNodePeakCount => Members.Select(m => m.RouteNodePeakCount).DefaultIfEmpty().Max();
    internal long ManagedHeapPeakBytes => Members.Select(m => m.ManagedHeapPeakBytes).DefaultIfEmpty().Max();
    internal long WorkingSetPeakBytes => Members.Select(m => m.WorkingSetPeakBytes).DefaultIfEmpty().Max();
    internal int WorkspaceOverflowParents => Members.Sum(m => m.WorkspaceOverflowParents);
    internal long SearchWorkspacePeakBytes => Members.Select(m => m.SearchWorkspacePeakBytes).DefaultIfEmpty().Max();
    internal long SearchWorkspaceGrowablePeakBytes
        => Members.Select(m => m.SearchWorkspaceGrowablePeakBytes).DefaultIfEmpty().Max();

    /// <summary>Threads each member was allowed to use; host-only and not encoded in SREQ.</summary>
    public int SearchThreads { get; init; } = 1;

    /// <summary>How the request ended; <see cref="SearchCompletionKind.Completed"/> for an ordinary solve.</summary>
    internal SearchCompletionKind Completion { get; init; } = SearchCompletionKind.Completed;

    /// <summary>Adoption provenance when <see cref="Completion"/> is an adoption.</summary>
    internal SearchAdoptionInfo? Adoption { get; init; }

    /// <summary>True when the adopted route contains only death lines; host-only.</summary>
    internal bool OnlyDeathRoutes { get; init; }

    /// <summary>Sum of the coordinator's planned member time limits; host-only.</summary>
    internal long PlannedBudgetMilliseconds { get; init; }

    /// <summary>
    /// True when the request forces potions and no route used them all; the request then failed
    /// with <see cref="CombatSearchStatus.Invalid"/>. Host-only.
    /// </summary>
    internal bool ForcedPotionsUnsatisfied { get; init; }

    /// <summary>
    /// Resume point when the request stopped with a route and members remain; null otherwise.
    /// Host-only and never encoded in SREQ/SRES.
    /// </summary>
    internal SearchResumeState? Resume { get; init; }

    /// <summary>
    /// How a resumed request merged its stop-time route with the members it ran afterwards; null for
    /// a request that did not resume.
    /// </summary>
    internal SearchResumeMerge? ResumeMerge { get; init; }
}
