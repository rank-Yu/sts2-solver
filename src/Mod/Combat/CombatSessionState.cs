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
    PlanExhausted,

    /// <summary>A setup search started after the first visible native choice page.</summary>
    TurnSetup,
}

internal sealed class CombatSearchSession
{
    internal CombatSearchSession(
        int generation,
        CombatState state,
        in Battle root,
        CombatStateStamp stamp,
        int startTurn,
        CombatSearchReason reason,
        SearchPolicyConfiguration policyConfiguration,
        bool deployWhenReady,
        CancellationTokenSource cancellation)
    {
        Generation = generation;
        State = state;
        Root = root;
        Stamp = stamp;
        StartTurn = startTurn;
        Reason = reason;
        PolicyConfiguration = policyConfiguration;
        DeployWhenReady = deployWhenReady;
        Cancellation = cancellation;
    }

    internal int Generation { get; }
    internal CombatState State { get; }
    internal Battle Root { get; }
    internal CombatStateStamp Stamp { get; }
    internal int StartTurn { get; }
    internal CombatSearchReason Reason { get; }
    internal SearchPolicyConfiguration PolicyConfiguration { get; }
    internal CancellationTokenSource Cancellation { get; }
    internal bool DeployWhenReady { get; set; }

    /// <summary>Set when the user stopped a search whose rendered candidate must be preserved.</summary>
    internal bool StopRequested { get; set; }

    /// <summary>Set when the user asked to apply the current turn while the search was running.</summary>
    internal bool ApplyCurrentTurn { get; set; }

    /// <summary>Candidate version last rendered by the main thread; stop adopts this exact version.</summary>
    internal int RenderedCandidateVersion { get; set; }

    internal int CandidateVersion { get; set; }

    internal long ReviewedWorldlinesBeforeSearch { get; set; }

    /// <summary>This session searches the Play roots enumerated from the setup Start root.</summary>
    internal bool IsTurnSetup { get; init; }

    /// <summary>The compiled multi-root setup request; null for an ordinary search.</summary>
    internal SearchCase? SetupCase { get; init; }

    /// <summary>The native <c>SetupPlayerTurn</c> entry root, imported before the page opened.</summary>
    internal Battle? SetupStartRoot { get; init; }

    /// <summary>Search inputs captured when this request started; null for a setup search.</summary>
    internal SearchSettingsSnapshot? Settings { get; init; }

    /// <summary>Host-only resume point when this request continues an earlier stop.</summary>
    internal SearchResumeState? Resume { get; init; }

    /// <summary>The plan the stopped request was launched with; reused verbatim when continuing.</summary>
    internal SearchPlan? ResumePlan { get; init; }

    internal bool IsContinuation => Resume != null;
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

    /// <summary>
    /// The next turn's setup page was taken over while this deployment waited for the boundary.
    /// Its EndTurn prediction no longer describes the live turn and must not count as a deviation.
    /// </summary>
    internal bool SupersededByTurnSetup { get; set; }
}

/// <summary>One completed search, replayed and ready for the controller or deployment.</summary>
internal sealed class CombatSearchRun
{
    internal CombatSearchRun(
        in Battle root,
        SearchCase? @case,
        CombatSearchResult outcome,
        BeamResult? beam,
        ReplayedRoute? route,
        CombatRoutePlan? plan,
        SearchProgressSnapshot? finalProgress,
        long startedAtMilliseconds,
        SearchPerformanceTier tier,
        SearchPerformanceProfile profile,
        int searchThreads,
        SearchMemberRunUi[] memberRuns,
        string? errorCode,
        string? errorDetail)
    {
        Root = root;
        Case = @case;
        Outcome = outcome;
        Beam = beam;
        Route = route;
        Plan = plan;
        FinalProgress = finalProgress;
        StartedAtMilliseconds = startedAtMilliseconds;
        Tier = tier;
        Profile = profile;
        SearchThreads = searchThreads;
        MemberRuns = memberRuns;
        ErrorCode = errorCode;
        ErrorDetail = errorDetail;
    }

    internal Battle Root { get; }
    internal SearchCase? Case { get; }
    internal CombatSearchResult Outcome { get; }
    internal BeamResult? Beam { get; }
    internal ReplayedRoute? Route { get; }
    internal CombatRoutePlan? Plan { get; }
    internal SearchProgressSnapshot? FinalProgress { get; }
    internal long StartedAtMilliseconds { get; }
    internal SearchPerformanceTier Tier { get; }
    internal SearchPerformanceProfile Profile { get; }
    internal int SearchThreads { get; }
    internal SearchMemberRunUi[] MemberRuns { get; }
    internal string? ErrorCode { get; }
    internal string? ErrorDetail { get; }
    internal int StartTurn => Plan?.StartTurn ?? Case?.Options.InitialPlayerTurn ?? 0;

    /// <summary>The Start root a setup search was launched from; null for an ordinary search.</summary>
    internal Battle? SetupStartRoot { get; init; }

    /// <summary>Selections that produced <see cref="Root"/> in a setup search.</summary>
    internal PlannedChoice[] TurnSetupChoices { get; init; } = [];

    /// <summary>Exact native Play state the setup plan predicted for its Play root.</summary>
    internal CombatStateStamp? SetupPlayStamp { get; init; }

    /// <summary>The plan this search was launched with; the continuation keeps it host-only.</summary>
    internal SearchPlan? RequestPlan { get; init; }
}

/// <summary>
/// Main-thread resume state kept after a user stop. The Core resume point, the exact encoded
/// request, the stop-time root stamp, the settings snapshot and the plan all stay in host memory;
/// nothing here enters SREQ/SRES or frozen data.
/// </summary>
internal sealed record CombatSearchContinuation(
    SearchResumeState Resume,
    byte[] RequestBytes,
    CombatStateStamp Stamp,
    SearchSettingsSnapshot Settings,
    SearchPlan Plan,
    int StartTurn,
    CombatControllerResult Result);

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
    int BattleHpLostSoFar = 0,
    PlannedChoice[]? TurnSetupChoices = null,
    CombatStateStamp? SetupPlayStamp = null,
    bool IsContinuationSetup = false)
{
    internal bool WasReused => Plan.WasReused;

    internal int StartTurn => Plan.StartTurn;

    /// <summary>The setup selections this result must replay, or an empty array.</summary>
    internal PlannedChoice[] SetupChoices => TurnSetupChoices ?? [];

    internal bool IsTurnSetup => SetupPlayStamp != null || IsContinuationSetup;
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
