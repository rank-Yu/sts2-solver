using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Host-only resume point of a coordinator request that stopped with a route. It carries the
/// per-pass state the member loop had reached, so the next request can continue after the stopped
/// member without rerunning it. The state never enters SREQ/SRES or frozen data: the Mod keeps it in
/// memory and drops it when the turn, the root or the request changes.
/// </summary>
internal sealed class SearchResumeState(
    int pass,
    int nextMemberIndex,
    BeamResult stoppedResult,
    int stoppedIndex,
    SearchAdoptionInfo adoption)
{
    /// <summary>Zero-based pass whose member loop was interrupted.</summary>
    internal int Pass { get; } = pass;

    /// <summary>Index of the next member to run; the stopped member itself is not rerun.</summary>
    internal int NextMemberIndex { get; } = nextMemberIndex;

    /// <summary>Member logs of this pass up to and including the stopped member.</summary>
    internal SearchMemberResult[] Members { get; init; } = [];

    internal BeamSolver.Candidate? Free { get; init; }
    internal BeamSolver.Candidate? Selected { get; init; }
    internal BeamResult? Best { get; init; }
    internal int BestIndex { get; init; } = -1;
    internal Outcome BestOutcome { get; init; }
    internal SearchInterimResult? BestComplete { get; init; }
    internal SortedDictionary<string, int>? CapacityDrops { get; init; }
    internal long ExpandedNodes { get; init; }

    /// <summary>
    /// The result adopted when the user stopped: the stopped member's candidate or the completed
    /// member that ranked better than it. A later request compares its own winner against this one.
    /// </summary>
    internal BeamResult StoppedResult { get; } = stoppedResult;

    /// <summary>Member-log index the stop-time result belongs to.</summary>
    internal int StoppedIndex { get; } = stoppedIndex;

    internal SearchAdoptionInfo Adoption { get; } = adoption;

    /// <summary>True when the stop-time route contains only death lines.</summary>
    internal bool OnlyDeathRoutes { get; init; }
}

/// <summary>
/// How a resumed request merged the route adopted at the earlier stop with the members it ran
/// afterwards. Host-only diagnostics; the Mod logs the reason when the routes could not be compared.
/// </summary>
internal readonly record struct SearchResumeMerge(bool Compared, bool KeptStopped, string Reason);
