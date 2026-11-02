// Main-thread session records for the panel controller. They hold only frozen search/deployment
// data and a native CombatState reference; background workers never see them.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;

namespace Sts2Solver;

internal enum CombatSearchReason
{
    Manual,
    AutoTurnStart,
    Deploy,
    FullAuto,
    DeploymentDrift,
    PlanExhausted,
}

internal sealed class CombatSearchSession(
    int generation,
    CombatState state,
    Battle root,
    CombatStateStamp stamp,
    int startTurn,
    CombatSearchReason reason,
    bool deployWhenReady,
    CancellationTokenSource cancellation)
{
    internal int Generation { get; } = generation;
    internal CombatState State { get; } = state;
    internal Battle Root { get; } = root;
    internal CombatStateStamp Stamp { get; } = stamp;
    internal int StartTurn { get; } = startTurn;
    internal CombatSearchReason Reason { get; } = reason;
    internal CancellationTokenSource Cancellation { get; } = cancellation;
    internal bool DeployWhenReady { get; set; } = deployWhenReady;

    /// <summary>Set when the user stopped a search whose rendered candidate must be preserved.</summary>
    internal bool StopRequested { get; set; }

    /// <summary>Set when the user asked to apply the current turn while the search was running.</summary>
    internal bool ApplyCurrentTurn { get; set; }

    /// <summary>Candidate version last rendered by the main thread; stop adopts this exact version.</summary>
    internal int RenderedCandidateVersion { get; set; }

    internal int CandidateVersion { get; set; }

    internal long ReviewedWorldlinesBeforeSearch { get; set; }
}

internal sealed class CombatDeploymentSession(
    int turn,
    CombatState state,
    CancellationTokenSource cancellation)
{
    internal int Turn { get; } = turn;
    internal CombatState State { get; } = state;
    internal CancellationTokenSource Cancellation { get; } = cancellation;
    internal Task Operation { get; set; } = Task.CompletedTask;

    /// <summary>Soft stop: no new action is started, the current one settles before the turn returns.</summary>
    internal bool StopRequested { get; set; }
}

/// <summary>One completed search, replayed and ready for the controller or deployment.</summary>
internal sealed record CombatSearchRun(
    Battle Root,
    SearchCase? Case,
    CombatSearchResult Outcome,
    BeamResult? Beam,
    ReplayedRoute? Route,
    CombatRoutePlan? Plan,
    SearchProgressSnapshot? FinalProgress,
    long StartedAtMilliseconds,
    SearchPerformanceTier Tier,
    SearchPerformanceProfile Profile,
    int SearchThreads,
    SearchMemberRunUi[] MemberRuns,
    string? ErrorCode,
    string? ErrorDetail)
{
    internal int StartTurn => Plan?.StartTurn ?? Case?.Options.InitialPlayerTurn ?? 0;
}

/// <summary>The controller's owned result, with the projection baseline applied.</summary>
internal sealed record CombatControllerResult(
    CombatRoutePlan Plan,
    CombatRouteOutcome Outcome,
    CombatStateStamp Stamp,
    int CandidateVersion,
    SearchAdoptionInfo? Adoption,
    int PreviousProjectedBattleHpLost,
    int ProjectedBattleHpLossIncrease,
    bool RecalculatedAfterCompleteProjection,
    SearchPerformanceTier Tier,
    long SearchElapsedMilliseconds,
    long SearchExpandedNodes,
    CombatSearchResult? SearchOutcome,
    SearchMemberRunUi[] MemberRuns,
    string Reason,
    int BattleHpLostSoFar = 0)
{
    internal bool WasReused => Plan.WasReused;

    internal int StartTurn => Plan.StartTurn;
}

internal sealed record CombatPause(
    CombatPauseCode Code,
    string Detail,
    int? PlannedHpLost = null,
    int? LiveHpLost = null,
    bool PlayerDead = false)
{
    internal string CodeName => CombatFullAutoPausePolicy.Code(Code);
}
