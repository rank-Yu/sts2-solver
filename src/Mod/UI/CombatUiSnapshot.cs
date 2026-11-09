// Snapshot shape adapted from CombatSolver src/UI/SolverOverlaySnapshot.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The Sts2Solver records carry only frozen route/search/session data and no engine types.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Import;

namespace Sts2Solver;

internal enum CombatUiPhase
{
    Idle,
    WaitingTurn,
    Searching,
    Deploying,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

internal enum SearchUiPhase
{
    Idle,
    Running,
    Stopping,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>Immutable main-thread projection of the current combat session. No live game object or search node is retained.</summary>
/// <summary>The current turn's search stage shown by the footer (<see cref="CombatUiSnapshot.SearchStage"/>).</summary>
internal enum TurnSearchStage
{
    NotStarted,
    Running,
    Ended,
}

internal sealed record CombatUiSnapshot
{
    internal const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Monotonic publish order, useful to drop stale reads.</summary>
    public long Sequence { get; init; }

    public CombatUiPhase Phase { get; init; } = CombatUiPhase.Idle;

    public string Root { get; init; } = "";

    /// <summary>The auto-search switch (persisted setting).</summary>
    public bool AutoSearchEnabled { get; init; } = true;

    /// <summary>执行本场战斗 is running in this combat.</summary>
    public bool CombatRunActive { get; init; }

    /// <summary>
    /// The run stopped itself with a pause (projected death, deviation, failure); the next
    /// 执行本场战斗 press confirms the current turn.
    /// </summary>
    public bool CombatRunPaused { get; init; }

    /// <summary>Auto search or 执行本场战斗 searches the coming turns by itself.</summary>
    public bool AutomaticCalculationEnabled { get; init; }

    /// <summary>
    /// How far the current turn's search has gone: not started, running, or stopped/finished (a
    /// reused or activated route counts as finished). The footer keys follow this, not the result.
    /// </summary>
    public TurnSearchStage SearchStage { get; init; }

    public bool AutomaticSearchPaused { get; init; }

    public int? AutomaticSearchPausedTurn { get; init; }

    /// <summary>A stop or paused search applies to the current turn only.</summary>
    public bool AutomationStoppedThisTurn { get; init; }

    public bool IsSearching { get; init; }

    public bool IsDeploying { get; init; }

    /// <summary>A visible native turn-setup choice page is open.</summary>
    public bool TurnSetupPageOpen { get; init; }

    /// <summary>A setup search returned a plan and the page is still waiting for the player or the driver.</summary>
    public bool TurnSetupPlanReady { get; init; }

    /// <summary>The ready plan was reused from the previous turn's EndTurn action instead of searched.</summary>
    public bool TurnSetupContinuation { get; init; }

    /// <summary>The solver is driving the visible setup page; the blocker is active.</summary>
    public bool TurnSetupDriving { get; init; }

    /// <summary>The setup search reached the first visible page and started a worker.</summary>
    public bool TurnSetupSearchStarted { get; init; }

    /// <summary>Number of setup selections in the current plan.</summary>
    public int TurnSetupChoiceCount { get; init; }

    /// <summary>The running search will deploy its result when it finishes (drift continuation).</summary>
    public bool DeployWhenReady { get; init; }

    /// <summary>A soft stop was requested; the current action still has to settle.</summary>
    public bool IsStoppingDeployment { get; init; }

    public bool CanStartSearch { get; init; }

    public bool CanStopSearch { get; init; }

    /// <summary>Stable <see cref="SearchBlockReason"/> when search is not possible.</summary>
    public string? SearchBlockReason { get; init; }

    /// <summary>The retained route matches the live state for the current turn.</summary>
    public bool CurrentTurnRouteReady { get; init; }

    /// <summary>
    /// A stopped search kept a host-only resume point, the live root still matches it and every
    /// search input is unchanged, so the search key continues instead of restarting.
    /// </summary>
    public bool CanContinueSearch { get; init; }

    /// <summary>
    /// A host-only resume point from this turn's stop exists, but the root or the inputs changed;
    /// the search key restarts instead of continuing.
    /// </summary>
    public bool ContinuationAvailable { get; init; }

    /// <summary>The published search candidate contains only death lines.</summary>
    public bool SearchCandidateAllDeath { get; init; }

    public int? LastSolverDeployedTurn { get; init; }

    public CombatResultUiSnapshot? Result { get; init; }

    /// <summary>
    /// The result whose plan <see cref="Route"/> shows: <see cref="Result"/> when there is one,
    /// otherwise the result being (or last) executed, which deployment takes out of
    /// <see cref="Result"/>. The panel reads the projected outcome from it.
    /// </summary>
    public CombatResultUiSnapshot? RouteResult { get; init; }

    public int Turn { get; init; }

    public int PlayerHp { get; init; }

    public int PlayerMaxHp { get; init; }

    /// <summary>Stable terminal code such as <c>victory</c>, <c>defeat</c>, <c>paused</c> or <c>cancelled</c>.</summary>
    public string? EndReason { get; init; }

    /// <summary>Human-readable detail from the pilot; tests must assert <see cref="EndReason"/> instead.</summary>
    public string? PauseReason { get; init; }

    /// <summary>Structured reason behind <see cref="PauseCode"/>; the panel localizes it.</summary>
    public CombatFailureReason? PauseDetailReason { get; init; }

    /// <summary>
    /// Stable code behind a pause or stop: <c>death_turn</c>, <c>live_end_turn_risk</c>,
    /// <c>repeated_failure</c>, <c>choice_mismatch</c>, <c>action_deviation</c>,
    /// <c>boundary_deviation</c>, <c>deployment_deviation</c>, <c>deploy_failed</c> or
    /// <c>stopped</c>; null otherwise.
    /// Rejected commands use <see cref="RejectCode"/>. The panel localizes this instead of
    /// <see cref="PauseReason"/>.
    /// </summary>
    public string? PauseCode { get; init; }

    public int? PausePlannedHpLost { get; init; }

    public int? PauseLiveHpLost { get; init; }

    public bool PausePlayerDead { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>Raw diagnostic detail; never shown verbatim by the panel.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>Structured reason behind <see cref="ErrorCode"/>; the panel localizes it.</summary>
    public CombatFailureReason? ErrorReason { get; init; }

    /// <summary>Stable reason a command/search request was rejected without pausing.</summary>
    public string? RejectCode { get; init; }

    public SearchUiSnapshot? Search { get; init; }

    public RouteUiSnapshot? Route { get; init; }

    public MemoryUiSnapshot Memory { get; init; } = new();

    public SettingsUiSnapshot Settings { get; init; } = new();

    public StrategyUiSnapshot Strategy { get; init; } = new();

    public int Searches { get; init; }

    public int ReusedTurns { get; init; }

    public int Deviations { get; init; }

    public int DeploymentFailures { get; init; }

    /// <summary>Worldlines reviewed by completed searches in this combat.</summary>
    public long ReviewedWorldlines { get; init; }

    /// <summary>The latest search-ledger peak this combat; the memory bar shows it between searches.</summary>
    public long LastSearchMemoryPeakBytes { get; init; }
}

/// <summary>Resolved search settings the settings page shows next to the persisted values.</summary>
internal sealed record SettingsUiSnapshot
{
    /// <summary>Effective thread count of one search member (setting or default).</summary>
    public int SearchThreads { get; init; } = 1;

    /// <summary>The machine default: logical processors minus two, clamped to 1..8.</summary>
    public int SearchThreadsDefault { get; init; } = 1;

    /// <summary>Effective search memory ceiling.</summary>
    public long MemoryBudgetBytes { get; init; }

    /// <summary>The machine default: 20% of the memory available to the runtime, at least 1 GiB.</summary>
    public long MemoryBudgetDefaultBytes { get; init; }
}

/// <summary>
/// Frozen strategy values shown by the strategy sidebar, the settings page's Boss group and the theft
/// bar. Only stable codes, ids and numbers; display names and icons are resolved from the game model
/// by the panel.
/// </summary>
internal sealed record StrategyUiSnapshot
{
    public PotionSlotUi[] Potions { get; init; } = [];

    public SearchGrowthBudget GrowthBudgets { get; init; }

    public bool IgnoreLongTermRewards { get; init; }

    /// <summary>The player's unmelted counter relics, in relic order, with their live counters.</summary>
    public RelicCounterUi[] RelicCounters { get; init; } = [];

    /// <summary>Saved extra HP allowances by native relic id; a missing id allows 0.</summary>
    public IReadOnlyDictionary<string, int> RelicCounterAllowances { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Combat-cumulative BrightestFlame max HP limit; null is unlimited.</summary>
    public int? BrightestFlameMaxHpLossLimit { get; init; }

    /// <summary>Max HP already spent on BrightestFlame in the current combat.</summary>
    public int BrightestFlameMaxHpSpent { get; init; }

    public SearchBossHpStrategy ActTransitionBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    public SearchBossHpStrategy FinalBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    /// <summary>Stable native relief code: <c>none</c>, <c>act_clear_heal</c> or <c>run_ending</c>.</summary>
    public string BossHpRelief { get; init; } = "none";

    /// <summary>True for a boss whose HP carries into an act transition or ends the run.</summary>
    public bool IsActEndingBoss { get; init; }

    public bool TheftApplicable { get; init; }

    /// <summary>Empty when the encounter has no theft policy; otherwise <c>preserve_resources</c> or <c>let_escape</c>.</summary>
    public string? TheftPolicy { get; init; }
}

/// <summary>One owned relic of <c>RelicCounterCatalog</c>: its relic slot, id and live counter.</summary>
internal sealed record RelicCounterUi
{
    public int Slot { get; init; }

    public string RelicId { get; init; } = "";

    /// <summary>The counter reduced to its cycle.</summary>
    public int Value { get; init; }

    public int Period { get; init; }
}

internal sealed record PotionSlotUi
{
    public int Slot { get; init; }

    public string PotionId { get; init; } = "";

    /// <summary>Stable code <c>smart</c>, <c>disabled</c> or <c>force</c>.</summary>
    public string Mode { get; init; } = "smart";

    /// <summary>False for potions the search never offers explicitly (for example Fairy in a Bottle).</summary>
    public bool CanAssignPolicy { get; init; }
}

/// <summary>The projected values of the most recent complete search result.</summary>
internal sealed record CombatResultUiSnapshot
{
    public int StartTurn { get; init; }

    public int? DeathTurn { get; init; }

    public int? CombatEndedTurn { get; init; }

    public int ProjectedBattleHpLost { get; init; }

    public int BattleHpLostSoFar { get; init; }

    public long SearchExpandedNodes { get; init; }

    public long SearchElapsedMilliseconds { get; init; }

    public int ProjectedBattlePotionCount { get; init; }

    /// <summary>HP the post-combat relics restore after the route's victory.</summary>
    public int PostCombatRelicHeal { get; init; }

    public int PreviousProjectedBattleHpLost { get; init; }

    public int ProjectedBattleHpLossIncrease { get; init; }

    public bool WasReused { get; init; }

    public bool Victory { get; init; }

    public bool OnlyDeathRoutes { get; init; }

    /// <summary>Stable outcome code: <c>victory</c>, <c>defeat</c>, <c>incomplete</c> or <c>event_defeat</c>.</summary>
    public string Outcome { get; init; } = "";

    public SearchAdoptionKind? AdoptionKind { get; init; }

    public int CandidateVersion { get; init; }

    /// <summary>Capacity regions that dropped candidate branches while still returning this route.</summary>
    public IReadOnlyDictionary<string, int>? CapacityDroppedRegions { get; init; }

    /// <summary>The relic counter targets at the route's end (native relic id, value, goal, met).</summary>
    public StrategyRelicUi[] StrategyRelics { get; init; } = [];

    /// <summary>The built-in growth rewards realized by the route's end (source name, count).</summary>
    public StrategyGrowthUi[] StrategyGrowth { get; init; } = [];
}

/// <summary>One relic counter target at the end of the result route.</summary>
internal sealed record StrategyRelicUi(string RelicId, int Value, int Goal, bool Met);

/// <summary>One built-in growth source realized by the end of the result route.</summary>
internal sealed record StrategyGrowthUi(string Source, int Count);

internal sealed record MemoryUiSnapshot
{
    public long BudgetBytes { get; init; }

    public SearchMemoryBudgetSource BudgetSource { get; init; }

    public long ManagedHeapBytes { get; init; }

    public long WorkingSetBytes { get; init; }

    public long GcAvailableBytes { get; init; }

    public long SampledAtMilliseconds { get; init; }
}

/// <summary>One search, from request compilation to the coordinator's final member table.</summary>
internal sealed record SearchUiSnapshot
{
    public SearchUiPhase Phase { get; init; } = SearchUiPhase.Running;

    public SearchPerformanceTier Tier { get; init; } = SearchPerformanceTier.Medium;

    public int Turn { get; init; }

    public string Reason { get; init; } = "";

    /// <summary>Actual threads used by this search, including the search thread itself.</summary>
    public int SearchThreads { get; init; } = 1;

    /// <summary><see cref="Environment.TickCount64"/> at search start; the panel derives the live elapsed time.</summary>
    public long StartedAtMilliseconds { get; init; }

    /// <summary>Run-order index of the member searching right now; -1 between members and after the search.</summary>
    public int ActiveMemberRun { get; init; } = -1;

    public int ActiveMemberWidth { get; init; }

    /// <summary>The active member's time share (width × milliseconds per width).</summary>
    public long ActiveMemberBudgetMilliseconds { get; init; }

    public long ActiveMemberStartedAtMilliseconds { get; init; }

    public long BudgetMilliseconds { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public long ExpandedNodes { get; init; }

    public long Transitions { get; init; }

    public int RouteActions { get; init; }

    /// <summary>Null while the search is running.</summary>
    public bool? Victory { get; init; }

    /// <summary>Host-visible result such as <c>ok</c>, <c>failed</c>, <c>cancelled</c> or <c>running</c>.</summary>
    public string Status { get; init; } = "running";

    /// <summary>Coordinator status such as <c>victory</c>, <c>incomplete</c> or <c>capacitybounded</c>.</summary>
    public string? SearchStatus { get; init; }

    public string? StopReason { get; init; }

    /// <summary>Stable limit code: <c>time</c>, <c>node</c>, or null when no limit was hit.</summary>
    public string? BoundaryCode { get; init; }

    public long ReviewedWorldlinesBeforeSearch { get; init; }

    /// <summary>Battle HP already lost at the search root.</summary>
    public int BattleHpLostBeforeSearch { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>Latest published candidate version, or zero before the first snapshot.</summary>
    public int CandidateVersion { get; init; }

    /// <summary>Candidate version the panel last rendered; the stop command adopts this version.</summary>
    public int RenderedCandidateVersion { get; init; }

    /// <summary>True when the panel has a rendered candidate that can be preserved on stop.</summary>
    public bool StopCandidateAvailable { get; init; }

    public SearchRoutePreviewUi? RoutePreview { get; init; }

    public SearchCurrentTurnPreviewUi? CurrentTurnPreview { get; init; }

    public int SelectedMember { get; init; } = -1;

    public int MembersRun { get; init; }

    public int MembersSkippedByTime { get; init; }

    public bool SoftTimeRisk { get; init; }

    public bool MemoryBudgetExceeded { get; init; }

    public long SearchMemoryUsedBytes { get; init; }

    public long SearchMemoryPeakBytes { get; init; }

    /// <summary>Parents that exceeded one workspace's growable-buffer cap and expanded straight through.</summary>
    public int WorkspaceOverflowParents { get; init; }

    /// <summary>Highest growable workspace bytes retained by one member; outside the search ledger.</summary>
    public long SearchWorkspaceGrowablePeakBytes { get; init; }

    /// <summary>Highest total workspace bytes retained by one member; outside the search ledger.</summary>
    public long SearchWorkspacePeakBytes { get; init; }

    public long ManagedHeapPeakBytes { get; init; }

    public long WorkingSetPeakBytes { get; init; }

    public SearchMemberUi[] Members { get; init; } = [];

    public SearchMemberRunUi[] MemberRuns { get; init; } = [];
}

internal sealed record SearchMemberUi
{
    public int Index { get; init; }

    public int Width { get; init; }

    public string Evaluation { get; init; } = "";

    public string Retention { get; init; } = "";

    public string Potions { get; init; } = "";

    public bool Ran { get; init; }

    public string? SkippedReason { get; init; }

    public string StopReason { get; init; } = "";

    public long ElapsedMilliseconds { get; init; }

    public long ExpandedNodes { get; init; }

    public bool Victory { get; init; }

    public bool Adopted { get; init; }

    public long MemoryPeakBytes { get; init; }

    public long ManagedHeapPeakBytes { get; init; }

    public long WorkingSetPeakBytes { get; init; }

    public int? HpLost { get; init; }

    public int? PotionUses { get; init; }
}

/// <summary>One <c>SearchRunResult</c>, recorded in coordinator order for member-level diagnostics.</summary>
internal sealed record SearchMemberRunUi
{
    public int Index { get; init; }

    public string Status { get; init; } = "";

    public string? ErrorKind { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public int ExpandedNodes { get; init; }

    public int Transitions { get; init; }

    public long ManagedHeapPeakBytes { get; init; }

    public long WorkingSetPeakBytes { get; init; }

    public long AllocatedBytes { get; init; }

    public int PacerYields { get; init; }

    /// <summary>Frame-recovery waits, including those drained from expansion workers.</summary>
    public int PacerFrameRecoveryWaits { get; init; }

    public long PacerFrameRecoveryWaitMilliseconds { get; init; }

    /// <summary>The member's deadline poll that stopped it; 0 when the deadline never expired.</summary>
    public long DeadlineExpiredAtPoll { get; init; }
}

/// <summary>A route frozen into per-turn groups; indexes are global over the whole route.</summary>
internal sealed record RouteUiSnapshot
{
    public int StartTurn { get; init; }

    public int CurrentTurn { get; init; }

    /// <summary>Global index of the action being deployed or the next action when waiting.</summary>
    public int CurrentActionIndex { get; init; }

    public int CompletedActions { get; init; }

    /// <summary>True only while an executable action is actually being deployed.</summary>
    public bool ActionRunning { get; init; }

    public int ActionCount { get; init; }

    public bool Victory { get; init; }

    public bool Reused { get; init; }

    /// <summary>Reserved for the per-turn command set; always false until then.</summary>
    public bool Frozen { get; init; }

    public RouteTurnUi[] Turns { get; init; } = [];
}

internal sealed record RouteTurnUi
{
    public int Turn { get; init; }

    public int StartActionIndex { get; init; }

    public int HpBefore { get; init; }

    public int HpAfter { get; init; }

    /// <summary>HP lost during the turn, summed from the route's cumulative loss counter.</summary>
    public int HpLoss { get; init; }

    /// <summary>HP recovered during the turn, summed from the route's cumulative recovery counter.</summary>
    public int HpRecovered { get; init; }

    public int EnemyHpLost { get; init; }

    public bool CombatEnded { get; init; }

    /// <summary>Energy left when the end-turn action starts; null when the turn has no end-turn action.</summary>
    public int? EnergyLeft { get; init; }

    /// <summary>Turn-start card choices; empty until turn-setup roots are captured.</summary>
    public TurnStartChoiceUi[] TurnStartChoices { get; init; } = [];

    public RouteActionUi[] Actions { get; init; } = [];
}

/// <summary>One turn-start selection, with stable source/effect codes and card identities.</summary>
internal sealed record TurnStartChoiceUi
{
    /// <summary>Stable source kind: <c>relic</c>, <c>power</c>, <c>card</c>, <c>potion</c> or empty.</summary>
    public string SourceKind { get; init; } = "";

    /// <summary>Native id of the source; empty when the choice has no displayable source.</summary>
    public string SourceId { get; init; } = "";

    /// <summary>Upgrade level of a card source, otherwise zero.</summary>
    public int SourceUpgrade { get; init; }

    public string Effect { get; init; } = "";

    public ChoiceCardUi[] Cards { get; init; } = [];
}

/// <summary>A card identity resolved to a localized name on the UI thread.</summary>
internal sealed record ChoiceCardUi
{
    public string CardId { get; init; } = "";

    public int Upgrade { get; init; }
}

/// <summary>An enemy killed by one action, frozen by stable identity.</summary>
internal sealed record KillUi
{
    public uint CombatId { get; init; }

    public string MonsterId { get; init; } = "";

    public int Ordinal { get; init; }
}

/// <summary>One speculative route action published by the search thread.</summary>
internal sealed record SearchActionPreviewUi
{
    public string Kind { get; init; } = "";

    public string? CardId { get; init; }

    public int Upgrade { get; init; }

    public string? PotionId { get; init; }

    public string? CardType { get; init; }

    /// <summary>Stable target kind: <c>player</c>, <c>osty</c>, <c>enemy</c> or empty.</summary>
    public string TargetKind { get; init; } = "";

    public uint? TargetCombatId { get; init; }

    public string? TargetMonsterId { get; init; }

    public int TargetOrdinal { get; init; }

    public KillUi[] Kills { get; init; } = [];

    public int HpBefore { get; init; }

    public int HpAfter { get; init; }

    public int EnergyBefore { get; init; }

    public int EnergyAfter { get; init; }

    public int ReplayCount { get; init; }
}

internal sealed record RouteActionUi
{
    public int Index { get; init; }

    /// <summary>Stable action kind: <c>play_card</c>, <c>use_potion</c> or <c>end_turn</c>.</summary>
    public string Kind { get; init; } = "";

    public string? CardId { get; init; }

    public int Upgrade { get; init; }

    /// <summary>Resolved type of the played card: <c>attack</c>, <c>skill</c>, <c>power</c>, <c>status</c>, <c>curse</c> or <c>quest</c>.</summary>
    public string? CardType { get; init; }

    public string? PotionId { get; init; }

    public uint? TargetCombatId { get; init; }

    /// <summary>Stable target kind: <c>player</c>, <c>osty</c>, <c>enemy</c> or empty.</summary>
    public string TargetKind { get; init; } = "";

    public string? TargetMonsterId { get; init; }

    public int TargetOrdinal { get; init; }

    /// <summary>One entry per carried choice; each entry lists the selected card identities.</summary>
    public ChoiceCardUi[][] Choices { get; init; } = [];

    public KillUi[] Kills { get; init; } = [];

    public int HpBefore { get; init; }

    public int HpAfter { get; init; }

    public int EnergyBefore { get; init; }

    public int EnergyAfter { get; init; }

    /// <summary>Card replay count before the action; zero for potions and end turn.</summary>
    public int ReplayCount { get; init; }

    public bool EndsTurn { get; init; }
}

/// <summary>An immutable speculative route published by the search thread and read on the main thread.</summary>
internal sealed record SearchRoutePreviewUi
{
    public int CandidateVersion { get; init; }

    public int StartTurn { get; init; }

    public int ThroughTurn { get; init; }

    public int ActionCount { get; init; }

    public bool Victory { get; init; }

    public bool OnlyDeathRoutes { get; init; }

    public int ProjectedBattleHpLost { get; init; }

    public int ProjectedBattlePotionCount { get; init; }

    public SearchTurnPreviewUi[] Turns { get; init; } = [];
}

internal sealed record SearchTurnPreviewUi
{
    public int Turn { get; init; }

    public int ActionCount { get; init; }

    public int HpLost { get; init; }

    public int HpRecovered { get; init; }

    public int EnemyHpLost { get; init; }

    public int EnergyLeft { get; init; }

    public bool CombatEnded { get; init; }

    public SearchActionPreviewUi[] Actions { get; init; } = [];
}

internal sealed record SearchCurrentTurnPreviewUi
{
    public int CandidateVersion { get; init; }

    public int Turn { get; init; }

    public int ActionCount { get; init; }

    public int HpLost { get; init; }

    public int HpRecovered { get; init; }

    public int EnemyHpLost { get; init; }

    public int EnergyLeft { get; init; }

    public bool CombatEnded { get; init; }

    public SearchActionPreviewUi[] Actions { get; init; } = [];
}
