using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Quality;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

internal sealed record BeamOptions(
    int BeamWidth,
    int MaxExpandedNodes,
    int MaxTurns,
    TimeSpan SoftTimeBudget)
{
    /// <summary>Production searches share time and memory, without a node ceiling.</summary>
    public bool UnlimitedNodes { get; init; }

    /// <summary>
    /// Apply <see cref="MaxExpandedNodes"/> while keeping <see cref="UnlimitedNodes"/>' production
    /// semantics (zero-damage stop gating, cancellation checkpoints and pool behavior). Production
    /// sets it with <see cref="SearchPlan.NodesPerWidth"/> and keeps the soft-time deadline as the
    /// other bound; the offline reproducible budget sets it and drops the deadline.
    /// </summary>
    public bool NodeBudgetActive { get; init; }

    /// <summary>Request deadline shared by every member; host-only, never encoded in SREQ.</summary>
    public Func<bool>? DeadlineExpired { get; init; }

    /// <summary>Optional test observer called at retention checkpoints.</summary>
    public Action? RetentionCheckpoint { get; init; }

    /// <summary>Unbounded growth goals preclude proving an early stop.</summary>
    public bool HasUnboundedGrowthTargets { get; init; }

    /// <summary>End-turn batch trigger and target, in states; zero preserves legacy behavior.</summary>
    public int EndedBatchTrigger { get; init; }
    public int EndedBatchTarget { get; init; }

    /// <summary>Hard cap on search-owned logical bytes for this solve; must be positive.</summary>
    public long SearchMemoryBudgetBytes { get; init; }

    /// <summary>Byte budget of each growable state pool; must be positive.</summary>
    public long StatePoolBudgetBytes { get; init; }

    /// <summary>Which root potions may be used and what each use costs; disabled unless the caller sets it.</summary>
    public PotionPolicy Potions { get; init; } = PotionPolicy.Disabled;

    /// <summary>
    /// The root-frozen evaluation. <see cref="Sts2Solver.Import.SearchRequestCompiler"/> and the
    /// differential host set it; a caller that supplies it is responsible for the same root.
    /// </summary>
    public Scorer? Scorer { get; init; }

    /// <summary>Root values the final route ordering reads; set together with <see cref="Scorer"/>.</summary>
    public RouteOrdering? Ordering { get; init; }

    /// <summary>
    /// The ported incumbent lower bound; null or disabled means the root never prunes. The request
    /// compiler sets it. The initial incumbent is always null; it is established during the search.
    /// </summary>
    public BoundFeatures? Bound { get; init; }

    /// <summary>The existing primary member's effective potion policy, used to qualify incumbent tightening.</summary>
    public PotionUseMode EffectivePotionPolicy { get; init; } = PotionUseMode.Smart;

    /// <summary>The request's theft policy; <c>PreserveResources</c> adds the existing final ordering's recovery keys.</summary>
    public TheftPolicy? TheftPolicy { get; init; }

    /// <summary>
    /// The audited potion-free baseline the existing final ordering compares an audited potion route
    /// against; null means the evaluated candidates decide it. Only potion-audit requests set it.
    /// </summary>
    public PotionFreePolicyBaseline? PotionFreeBaseline { get; init; }

    /// <summary>
    /// The best selectable victory earlier members of the same request found, as a pruning bound the
    /// member starts with (only used when <see cref="Bound"/> is enabled). Encoded as the SREQ extension
    /// <c>INC1</c>; null for a member that starts without one.
    /// </summary>
    public SearchIncumbent? IncumbentSeed { get; init; }

    /// <summary>
    /// Port the existing final ordering's potion policy filter. Set only for the potion audits;
    /// primary/refinement and clean continuations keep the prior route key.
    /// </summary>
    public bool AuditPotionPolicy { get; init; }

    /// <summary>
    /// The request's Act3 boss strategy. Adds a direct play of a fetched, payable
    /// power before layer selection on every searched turn, retaining the ordinary fetched state.
    /// </summary>
    public bool Act3BossStrategy { get; init; }

    /// <summary>
    /// The request root's combat turn (before a fixed prefix), matching the existing solver's
    /// <c>_startTurnNumber</c> for the power-commitment turn offset.
    /// </summary>
    public int InitialPlayerTurn { get; init; }

    /// <summary>
    /// <c>the existing search's SecondRankBand</c>: the global prune's ordinary seats come from score
    /// ranks W+1..2W instead of the top W. Required representatives, power-commitment seats and plan
    /// seats are unchanged. Only the members that explicitly set the profile switch read it.
    /// </summary>
    public bool SecondRankBand { get; init; }

    /// <summary>
    /// <c>the existing search's AggressivePowerCommitment</c>: raise the power-commitment seat quota to
    /// half the beam and extend a commitment's lifetime from two round transitions to three. The
    /// per-node commitment metadata is route history, not <see cref="Battle"/> state.
    /// </summary>
    public bool AggressivePowerCommitment { get; init; }

    /// <summary>
    /// <c>CombatRootSnapshot.HasUnusedCardReplayAllocator</c>: the existing root-action lineage also
    /// keys the route's first played card when the allocator is unused.
    /// </summary>
    public bool PreserveReplayAllocatorOpening { get; init; }

    /// <summary>
    /// Per-card power-commitment descriptors compiled by the request compiler, plus the request's frozen
    /// <c>TotalFloor</c>. Null when the root holds no registered power card: the existing solver also
    /// skips the whole lane for such roots.
    /// </summary>
    public PowerCommitmentCatalog? PowerCommitments { get; init; }

    /// <summary>
    /// The shared frame-pressure pacer. The Mod's search host creates it from the live frame signal so
    /// the Sts2Solver layer and expansion loops yield during play. Null in direct kernel tests.
    /// </summary>
    public SearchWorkPacer? Pacer { get; init; }

    /// <summary>
    /// Total threads that may expand one action layer at the same time. Host-only, like
    /// <see cref="Pacer"/> and <see cref="DeadlineExpired"/>: it never changes the result, so it is
    /// not encoded in SREQ. One keeps the single-thread path.
    /// </summary>
    public int SearchThreads { get; init; } = 1;

    /// <summary>
    /// Test/offline-only cap on one workspace's growable expansion buffers. Zero uses the
    /// production default. Like <see cref="SearchThreads"/> it is host-only and never enters SREQ.
    /// </summary>
    public int WorkspaceBufferLimitBytes { get; init; }

    /// <summary>
    /// Optional progress subscriber. Null is the default and keeps the search free of progress
    /// work; a channel is host-only and never enters SREQ.
    /// </summary>
    public SearchProgressChannel? Progress { get; init; }

    /// <summary>
    /// Offline route trace for test diagnostics only. Production never sets it; the solver only
    /// reports the points it already evaluates.
    /// </summary>
    public BeamTrace? Trace { get; init; }

    /// <summary>
    /// Combat-cumulative BrightestFlame max HP limit; null is unlimited and keeps the default path
    /// free of the per-node ledger. Encoded only as a non-default SREQ extension.
    /// </summary>
    public int? BrightestFlameMaxHpLossLimit { get; init; }

    /// <summary>Max HP already spent on BrightestFlame before this root; root history, not policy.</summary>
    public int InitialBrightestFlameMaxHpSpent { get; init; }
}

/// <summary>One parent's workspace shape, reported only when a test observer is installed.</summary>
internal readonly record struct ParentWorkspaceMeasurement(
    long PeakUsedBytes,
    long PeakAllocatedBytes,
    int Children,
    int Terminals,
    int Choices,
    int Duplicates,
    bool Direct);

/// <summary>One registered power card's existing commitment descriptor.</summary>
internal readonly record struct PowerCommitmentRule(
    PowerCommitmentFamily Family,
    PowerRoutePriority Priority,
    PowerRouteAdmissionPolicy Admission,
    int Index);

/// <summary>
/// The root-frozen power-commitment catalog. Indexed by the kernel's card id; entries with
/// <see cref="PowerCommitmentFamily.None"/> are not registered as commitments.
/// </summary>
internal sealed record PowerCommitmentCatalog(
    PowerCommitmentRule[] Rules,
    int TotalFloor);

/// <summary>The root values <c>FinalPlanOrdering.Select</c> reads for the route keys Beam ports.</summary>
internal sealed record RouteOrdering(
    int InitialPlayerHp,
    BossHpRelief BossHpRelief,
    PostCombatRelicHealProfile PostCombatRelicHeal);

/// <summary>
/// The existing search's potion constraints at the root (<c>EnumeratePlannedPotionActions</c>): the
/// earliest player turn and the strategy/policy identity gate the existing <c>AllowsPotionUse</c>
/// applies. The gate is by (slot, potion identity), so a potion generated into a slot during the
/// search is offered exactly when the existing search would offer it. <see cref="ForceAllDisabled"/>
/// is the potion-free stage: no explicit use is offered and no route with one is eligible.
/// </summary>
internal sealed record PotionPolicy(
    int EarliestPlayerTurn,
    PotionStrategySnapshot Strategy,
    PotionUseMode EffectivePolicy,
    bool ForceAllDisabled)
{
    public static PotionPolicy Disabled { get; } = new(
        int.MaxValue,
        new PotionStrategySnapshot(PotionUseMode.Disabled, Array.Empty<PotionSlotDirective>()),
        PotionUseMode.Disabled, false);

    /// <summary><c>PotionStrategySnapshot.AllowsExplicitUse</c> for the potion now in the slot.</summary>
    public bool AllowsExplicitUse(int slot, ushort potionId)
    {
        PotionDef definition = Registry.Potion(potionId);
        return definition.Searchable
            && Strategy.AllowsExplicitUse(slot, definition.NativeId, EffectivePolicy, ForceAllDisabled);
    }
}

internal sealed record BeamResult(
    PlayerAction[] BestRoute,
    bool BestIsVictory,
    double BestScore,
    int ExpandedNodes,
    int Transitions,
    int Duplicates,
    int TurnsSearched,
    int PrunedIncumbents,
    TimeSpan Elapsed)
{
    public long ExpandedNodesTotal { get; init; }
    public string StopReason { get; init; } = "unknown";

    /// <summary>
    /// The number of the first deadline poll that found the deadline expired; 0 when it never expired.
    /// Not encoded in SRES.
    /// </summary>
    public long DeadlineExpiredAtPoll { get; init; }

    internal static readonly IReadOnlyDictionary<string, int> NoCapacityDrops =
        new Dictionary<string, int>(0);

    /// <summary>Set when the solve ended by adopting a published candidate.</summary>
    public SearchAdoptionInfo? Adoption { get; init; }

    /// <summary>Set when a stop request had no published candidate; no route is returned.</summary>
    public bool StopWithoutCandidate { get; init; }

    /// <summary>Children dropped because their state exceeded a Sts2Solver capacity (a search boundary).</summary>
    public int CapacityPruned { get; init; }

    /// <summary>
    /// Card-play children refused by the per-turn consecutive-no-progress guard. Diagnostic only;
    /// this boundary is not encoded in SRES.
    /// </summary>
    public int TurnLoopGuarded { get; init; }

    /// <summary>
    /// Turn-boundary children refused by the cross-turn no-progress cut. Diagnostic only; this
    /// boundary is not encoded in SRES.
    /// </summary>
    public int CrossTurnNoProgressPruned { get; init; }

    /// <summary>High-water number of sparse poison/doom progress details in this solve.</summary>
    public int RouteProgressDetailPeakCount { get; init; }

    /// <summary>
    /// Logical chunk bytes retained by the sparse poison/doom progress detail table at its peak.
    /// Diagnostic only; it is not part of SRES.
    /// </summary>
    public long RouteProgressDetailStorageBytes { get; init; }

    /// <summary>
    /// Dropped children per capacity region (e.g. <c>Cards</c>, <c>Powers</c>,
    /// <c>Action.Choices</c>). The state-level regions are child boundaries: one instance is
    /// dropped and the rest of the search continues. The pool region only appears for the
    /// per-action-layer pools; the cumulative end-turn pool is compacted instead.
    /// </summary>
    public IReadOnlyDictionary<string, int> CapacityDroppedChildren { get; init; } = NoCapacityDrops;

    /// <summary>How often the end-turn pool was compacted at its capacity bound.</summary>
    public int EndedCompactions { get; init; }

    /// <summary>End-turn children dropped by those compactions.</summary>
    public int EndedCompressedStates { get; init; }

    /// <summary>Search-owned logical bytes charged to the reusable storage at the end of the solve.</summary>
    public long SearchMemoryUsedBytes { get; init; }

    /// <summary>Highest search-owned logical byte count observed during the solve.</summary>
    public long SearchMemoryPeakBytes { get; init; }

    /// <summary>Configured search-memory limit, or zero when unlimited.</summary>
    public long SearchMemoryLimitBytes { get; init; }

    /// <summary>True when an allocation was refused because it would cross the memory limit.</summary>
    public bool SearchMemoryBudgetExceeded { get; init; }

    /// <summary>Charged bytes at the first refused allocation; zero when not exceeded.</summary>
    public long SearchMemoryExceededAtBytes { get; init; }

    /// <summary>
    /// The solve stopped because its soft-time budget was reached (only possible for a limited
    /// solve). The widening loop uses this to mark the round as truncated by the in-game time
    /// insurance. Not encoded in SRES.
    /// </summary>
    public bool SoftTimeBudgetReached { get; init; }

    /// <summary>Bytes occupied by the route-node chunks (the largest search-owned growing store).</summary>
    public long RouteNodeStorageBytes { get; init; }

    /// <summary>Route nodes still retained after the last boundary cleanup.</summary>
    public int RouteNodeCount { get; init; }

    /// <summary>Highest route-node count observed during the solve, before boundary cleanup.</summary>
    public int RouteNodePeakCount { get; init; }

    /// <summary>
    /// Highest total bytes retained by all expansion workspaces. Workspace memory is outside the
    /// search ledger; this is a diagnostic for sizing the thread/window choice.
    /// </summary>
    public long SearchWorkspacePeakBytes { get; init; }

    /// <summary>Highest total growable (capped) bytes retained by all expansion workspaces.</summary>
    public long SearchWorkspaceGrowablePeakBytes { get; init; }

    /// <summary>Parents whose buffered expansion exceeded the workspace limit and streamed instead.</summary>
    public int WorkspaceOverflowParents { get; init; }

    /// <summary>Total action-layer parents expanded by this solve.</summary>
    public int WorkspaceParents { get; init; }

    /// <summary>Wall time spent in straight-through expansion for over-limit parents.</summary>
    public TimeSpan WorkspaceDirectDuration { get; init; }

    /// Which root of the solve the selected route started from: 0 for an ordinary root, or the
    /// turn-setup root candidate index (the coordinator maps it back to the setup choices).
    /// </summary>
    public int BestRootIndex { get; init; }

    /// <summary>
    /// False when an audited request found no route satisfying the ported potion policy. The dispatch
    /// point reports it in the existing search's <c>PotionPolicyUnsatisfiedException</c> shape;
    /// "Sts2Solver found none" is not the existing search's own verdict.
    /// </summary>
    public bool PotionPolicySatisfied { get; init; } = true;

    /// <summary>Frame-pressure yields performed by the shared <see cref="SearchWorkPacer"/>.</summary>
    public int WorkerYieldCount { get; init; }

    /// <summary>How often the pacer waited for a main-thread frame after real frame pressure.</summary>
    public int FrameRecoveryWaitCount { get; init; }

    /// <summary>Total time spent waiting for frame recovery.</summary>
    public TimeSpan FrameRecoveryWaitDuration { get; init; }

    /// <summary>Largest GC pause observed by the pacer during this solve.</summary>
    public TimeSpan MaxObservedGcPause { get; init; }

    /// <summary>
    /// End-turn actions that reused a prepared player-turn-start prefix. Diagnostics only; not part
    /// of the result encoding or the D2 field comparison.
    /// </summary>
    public int EndTurnPrefixShared { get; init; }

    /// <summary>
    /// End-turn actions whose prefix was not shareable (a choice or ending in the first half, or an
    /// extra turn); those actions ran the ordinary complete step. Diagnostics only.
    /// </summary>
    public int EndTurnPrefixUnshared { get; init; }

    /// <summary>
    /// Selection leaves checked against the complete-step path by the default-off equivalence hook.
    /// Diagnostics only.
    /// </summary>
    public int EndTurnPrefixVerified { get; init; }

    /// <summary>
    /// In-place threat projections whose before/after battle bytes were compared by the default-off
    /// equivalence hook. Diagnostics only.
    /// </summary>
    public int ThreatProjectionVerified { get; init; }

    /// <summary>Selection leaves that continued a prepared prefix (at least one per shared parent).</summary>
    public int EndTurnPrefixReusedLeaves { get; init; }

    /// <summary>Unshared prefixes whose first half requested a choice before the player turn start.</summary>
    public int EndTurnPrefixChoiceUnshared { get; init; }

    /// <summary>Unshared prefixes for an extra turn.</summary>
    public int EndTurnPrefixExtraTurnUnshared { get; init; }

    /// <summary>Unshared prefixes because the parent was already over.</summary>
    public int EndTurnPrefixNotInProgress { get; init; }

    /// <summary>
    /// Counts of the two content-deduplications and, when the verification switch is on, of the
    /// skipped-alternative comparisons. Diagnostics only; not part of the result encoding.
    /// </summary>
    public SearchEquivalenceDiagnostics Equivalence { get; init; } = SearchEquivalenceDiagnostics.Empty;
}

/// <summary>
/// Turn-layered beam search over <see cref="Battle"/>. Within a turn it expands card plays
/// depth by depth; end-turn children seed the next turn layer. Children are deduplicated by full
/// byte equality and the best score is kept. Only <see cref="Battle"/> and
/// <see cref="Rules"/> are touched; route materialization happens in the replay/CombatPilot layer.
/// </summary>
[System.Runtime.CompilerServices.SkipLocalsInit]
internal sealed partial class BeamSolver
{
    /// <summary>
    /// Selections explored per choice request (lexicographic within each size). The turn-setup root
    /// enumerator shares the same per-choice bound for its deduplicated root budget.
    /// </summary>
    internal const int MaxChoiceBranches = 32;

    /// <summary>
    /// Cap on skipped physical choice alternatives checked per request by the verification switch.
    /// The equality statistics are a bounded sample; the search itself is unaffected.
    /// </summary>
    private const int MaxChoiceAlternativesVerifiedPerRequest = 8;

    /// <summary>The existing <c>EarlyTurnCandidatesPerLayer</c>.</summary>
    private const int EarlyTurnCandidatesPerLayer = 24;

    /// <summary>
    /// Seat bound of each growable pool, derived from <see cref="BeamOptions.StatePoolBudgetBytes"/>
    /// and the packed-state planning geometry. Actual bytes are charged by the storage ledger.
    /// </summary>
    /// <summary>Dictionary/bucket allowance charged once per unique routing signature.</summary>
    private const int RoutingSignatureDictionaryBytes = 64;

    /// <summary>Production cap for one workspace's growable expansion buffers.</summary>
    internal const int DefaultWorkspaceBufferLimitBytes = 8 << 20;

    private readonly Rules _rules;
    private readonly BeamOptions _options;
    private readonly Scorer _scorer;
    private readonly bool _zeroGrowthBudget;
    private readonly int _relicCreditCeiling;
    private readonly bool _hasRelicTargets;
    private readonly RouteOrdering _ordering;
    private readonly int _workspaceBufferLimit;
    private RouteNodeStore _nodes = null!;
    private PotionLineageKeyStore _potionLineageKeys = null!;
    private RoutingMetaStore _routingMetas = null!;
    private SearchStorage _storage = null!;
    private int _nodeCleanupThreshold = 1_000_000;
    private readonly List<int> _compactRoots = [];
    private readonly List<int> _auditKeysScratch = [];

    /// <summary>
    /// Routing-choice signatures accumulated on the route. They are node metadata only; no value here
    /// enters <see cref="Battle"/>, the fingerprint or the state key.
    /// </summary>
    private readonly record struct RoutingSignature(
        int Turn,
        int SourceId,
        ChoiceEffect Effect,
        int SourcePile,
        int CardId,
        int Upgrade,
        int CardStateKey,
        int Occurrence,
        int CardCount,
        int ContextId,
        ulong EnemyCombatDistributionFirst,
        ulong EnemyCombatDistributionSecond,
        ulong EnemyControlDistributionFirst,
        ulong EnemyControlDistributionSecond,
        ulong UnorderedPileFirst,
        ulong UnorderedPileSecond);

    private readonly record struct RoutingChoice(
        int SignatureId, int Turn, int ChoiceDepth, int ParentRank, double ParentScore,
        int ParentEnergy);

    private ChunkedList<RoutingSignature> _routingSignatures = null!;
    private readonly Dictionary<RoutingSignature, int> _routingSignatureIds = new();

    /// <summary>
    /// One route node's accumulated metadata. <see cref="SearchRouteTraits"/> and the potion-use
    /// lineage are route history: they never enter <see cref="Battle"/>, the fingerprint or the
    /// state key.
    /// </summary>
    private readonly record struct RouteNode(
        int Parent,
        int Depth,
        PlayerAction Action,
        ulong RootActionKey,
        ulong Lineage,
        ushort PotionId,
        ushort FirstCardId,
        int FirstCardTarget,
        int Commitment,
        int ActionCardId,
        int TargetCombatId,
        int RoutingSignatureId,
        int RoutingChoiceTurn,
        int RoutingChoiceDepth,
        int RoutingParentRank,
        double RoutingParentScore,
        int RoutingParentEnergy,
        int ParentTurn,
        int RoutingMetaId,
        int PotionLineageKeyId,
        TurnProgressState Progress,
        int CrossTurnId);

    /// <summary>
    /// Route-node power-commitment metadata (1-based node field; 0 means no commitment). The existing
    /// search holds <c>PowerCommitment</c> on <c>SearchNode</c>, not in the simulated state, so this list
    /// never enters <see cref="Battle"/>, the fingerprint or the state key.
    /// </summary>
    private ChunkedList<PowerCommitmentState> _commitments = null!;
    private readonly PowerCommitmentCatalog? _powerCommitments;
    private readonly bool _aggressivePowerCommitment;
    private readonly bool _preserveReplayAllocatorOpening;
    private readonly BoundFeatures? _bound;
    private readonly bool _pruneEqualHp;
    private readonly bool _preservePotionCostOpportunities;
    private readonly PotionUseMode _effectivePotionPolicy;
    private readonly TheftPolicy? _theftPolicy;
    private readonly bool _auditPotionPolicy;
    private readonly PotionFreePolicyBaseline? _potionFreeBaseline;
    private readonly ushort _ambergrisPotionId;
    private Dictionary<int, Candidate>? _auditBestByPotionCount;
    private Candidate? _bestPotionFree;
    private bool _anyAuditedVictory;
    private SearchIncumbent? _incumbent;
    // The drunk-potion price is a valid part of the pruning bound only while it can never fall: no
    // potion can be generated into a slot (the slot diff would then read a refilled potion as unused)
    // and every root potion's strategic cost is known (non-negative).
    private readonly bool _potionCostMonotone;
    // Per root potion slot: the stored value (id + 1) of an opening-only potion the root holds there, else 0.
    private readonly ushort[] _openingOnlyRootPotion = new ushort[Limits.PotionSlots];
    // The search's first player turn (the latest one when several roots are searched together).
    private int _openingPotionTurn;
    private int _expanded;
    private long _expandedTotal;
    private bool _deadlineReached;
    private int _transitions;
    private int _duplicates;
    private long _deadlinePolls;
    private long _deadlineExpiredAtPoll;
    private bool _crossTurnCutEnded;
    private bool _memoryBudgetExceeded;
    private long _memoryBudgetExceededAtBytes;
    private int _capacityPruned;
    private int _pruned;
    private int _endedCompactions;
    private int _endedCompressedStates;
    private int _endedCompactionTarget;
    private readonly Dictionary<string, int> _capacityDropped = new(StringComparer.Ordinal);

    private bool _verifySearchEquivalence;
    private bool _dedupePlayActions;
    private bool _dedupeChoicesByClass;
    private bool _choiceFillVariants;
    private ExpansionWorkspace _workspace = null!;

    /// <summary>Only a non-null cap allocates and maintains the per-node spent ledger.</summary>
    internal bool TracksBrightestFlameSpent => _options.BrightestFlameMaxHpLossLimit.HasValue;

    /// <summary>One workspace per in-flight parent slot; a single element on the single-thread path.</summary>
    private ExpansionWorkspace[] _workspaces = null!;
    private ParallelExpansion? _parallelExpansion;
    private bool _parallelSearch;
    private long _workspacePeakBytes;
    private long _workspaceGrowablePeakBytes;
    private int _workspaceOverflowParents;
    private int _workspaceParents;
    private long _workspaceDirectTicks;
    internal static Action<ParentWorkspaceMeasurement>? ParentWorkspaceObserver;
    private ParentExpansionContext[] _layerContexts = [];
    private int _workspacePrefixShared;
    private int _workspacePrefixUnshared;
    private int _workspacePrefixVerified;
    private int _workspacePrefixReusedLeaves;
    private int _workspacePrefixChoiceUnshared;
    private int _workspacePrefixExtraTurnUnshared;
    private int _workspacePrefixNotInProgress;
    private int _workspaceTurnLoopGuarded;
    private int _workspaceCrossTurnNoProgressPruned;
    private SearchEquivalenceDiagnostics _workspaceEquivalence = SearchEquivalenceDiagnostics.Empty;

    /// <summary>Offline route trace (null in production) and the pool phase it reports.</summary>
    private readonly BeamTrace? _trace;
    private string _tracePhase = "next";
    private bool _singleRoot;
    private int _rootNodeCount;
    private ScoreFeatures _bestFeatures;
    private int _bestFeaturesNode = -1;

    /// <summary>The existing search's HP-target stop, so a commitment batch stops like its parent layer.</summary>

    private CancellationToken _cancellationToken;

    private readonly int _initialPlayerTurn;

    private Stopwatch? _solveWatch;

    /// <summary>The physical expanded-parent count of the current solve (readable after a canceled solve).</summary>
    internal int ExpandedNodes => _expanded;

    /// <summary>The physical transition count of the current solve (readable after a canceled solve).</summary>
    internal int TransitionCount => _transitions;

    /// <summary>Deadline polls of the current solve so far.</summary>
    internal long DeadlinePolls => Interlocked.Read(ref _deadlinePolls);

    /// <summary>Packed size of one route's turn-progress metadata; asserted by KernelChecks.</summary>
    internal static int TurnProgressStateSize => Unsafe.SizeOf<TurnProgressState>();

    public BeamSolver(Rules rules, BeamOptions options)
    {
        _rules = rules;
        _options = options;
        if (options.BrightestFlameMaxHpLossLimit is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(options),
                options.BrightestFlameMaxHpLossLimit,
                "至亮之焰最大生命消耗上限必须在 0..1000 或 null。");
        if (options.InitialBrightestFlameMaxHpSpent < 0)
            throw new ArgumentOutOfRangeException(nameof(options),
                options.InitialBrightestFlameMaxHpSpent,
                "根上已消耗的最大生命不能为负。");
        _scorer = options.Scorer
            ?? throw new InvalidOperationException("BeamOptions.Scorer 未设置。");
        _zeroGrowthBudget = _scorer.RulesForCodec.GrowthBudgets.Total == 0;
        _relicCreditCeiling = _scorer.RelicCreditCeiling;
        _hasRelicTargets = _scorer.RelicTargetCount != 0;
        _ordering = options.Ordering
            ?? throw new InvalidOperationException("BeamOptions.Ordering 未设置。");
        _bound = options.Bound is { Enabled: true } ? options.Bound : null;
        _potionCostMonotone = rules.PotionPool.Count == 0 && rules.OutOfCombatPotionPool.Count == 0
            && RootPotionCostsKnown(_scorer.RulesForCodec);
        ScoreRules rootScore = _scorer.RulesForCodec;
        for (int slot = 0; slot < rootScore.RootPotionSlotCount && slot < Limits.PotionSlots; slot++)
        {
            ushort stored = rootScore.RootPotions[slot];
            if (stored != 0 && PotionUsePolicy.OpeningOnlyPotionIds.Contains(Registry.Potion((ushort)(stored - 1)).NativeId))
                _openingOnlyRootPotion[slot] = stored;
        }
        _pruneEqualHp = _bound?.PruneEqualHp ?? false;
        _preservePotionCostOpportunities = _bound?.RequireCertifiedHealingProof ?? false;
        _effectivePotionPolicy = options.EffectivePotionPolicy;
        _theftPolicy = options.TheftPolicy;
        _auditPotionPolicy = options.AuditPotionPolicy;
        _potionFreeBaseline = options.PotionFreeBaseline;
        _ambergrisPotionId = _scorer.RulesForCodec.AmbergrisPotionId;
        _initialPlayerTurn = options.InitialPlayerTurn;
        _powerCommitments = options.PowerCommitments is { Rules.Length: > 0 } catalog ? catalog : null;
        _aggressivePowerCommitment = options.AggressivePowerCommitment;
        _preserveReplayAllocatorOpening = options.PreserveReplayAllocatorOpening;
        _trace = options.Trace;
        _workspaceBufferLimit = options.WorkspaceBufferLimitBytes > 0
            ? options.WorkspaceBufferLimitBytes : DefaultWorkspaceBufferLimitBytes;
    }

    /// <summary>
    /// Solves from one or more starting states. A turn-setup request hands over every enumerable setup
    /// root (the existing search's frontier does the same); the returned <see cref="BeamResult.BestRootIndex"/>
    /// identifies which one the selected route started from.
    /// </summary>
    public BeamResult Solve(IReadOnlyList<Battle> roots, CancellationToken cancellationToken)
        => Solve(roots, new SearchStorage(), cancellationToken);

    /// <summary>
    /// Solves with a caller-owned reusable storage. The storage is cleared first and its retained
    /// chunks are rented again before new blocks are allocated; the ledger keeps counting the retained
    /// blocks, so repeated rounds share one memory budget.
    /// </summary>
    public BeamResult Solve(IReadOnlyList<Battle> roots, SearchStorage storage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (roots.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(roots), "至少需要一个搜索根。");
        if (_scorer.QueenPolicy is { } queen)
            foreach (Battle root in roots) queen.ValidateRoot(root);
        if (_options.SearchThreads < 1)
            throw new ArgumentOutOfRangeException(nameof(_options.SearchThreads),
                "搜索线程数必须至少为 1。");
        if (DeadlineExpired()) return DeadlineBeforeStart(storage);
        _deadlineReached = false;
        _storage = storage;
        _storage.BeginSolve(_options.SearchMemoryBudgetBytes);
        _routingMetas = new RoutingMetaStore(storage);
        _routingMetas.Clear();
        _nodes = new RouteNodeStore(storage, _routingMetas,
            _options.BrightestFlameMaxHpLossLimit.HasValue);
        _potionLineageKeys = new PotionLineageKeyStore();
        _potionLineageKeys.Clear();
        _routingSignatures = new ChunkedList<RoutingSignature>(storage);
        _commitments = new ChunkedList<PowerCommitmentState>(storage);
        _nodeCleanupThreshold = NodeCleanupThreshold(_options.SearchMemoryBudgetBytes);
        _singleRoot = roots.Count == 1;
        _rootNodeCount = roots.Count;
        _verifySearchEquivalence = SearchVerificationHooks.VerifySearchEquivalence;
        Kernel.VerifyStableBoundaryInvariants = _verifySearchEquivalence;
        _dedupePlayActions = SearchVerificationHooks.DedupeExactPlayActions;
        _dedupeChoicesByClass = SearchVerificationHooks.DedupeEquivalentChoicesByClass;
        _choiceFillVariants = SearchVerificationHooks.ChoiceFillPhysicalVariants;
        _scorer.VerifyThreatProjection = _verifySearchEquivalence;
        // Trace and the equivalence probes carry mutable diagnostics (and trace state) that are not
        // shared between threads, so those modes keep the single-thread path.
        int searchThreads = Math.Max(1, _options.SearchThreads);
        if (_trace != null || _verifySearchEquivalence)
            searchThreads = 1;
        _parallelSearch = searchThreads > 1;
        bool directOnly = searchThreads == 1;
        int workspaceCount = searchThreads > 1 ? 2 * searchThreads : 1;
        _workspaces = new ExpansionWorkspace[workspaceCount];
        for (int index = 0; index < _workspaces.Length; index++)
            _workspaces[index] = new ExpansionWorkspace(this, directOnly && index == 0);
        _workspace = _workspaces[0];
        _workspacePrefixShared = 0;
        _workspacePrefixUnshared = 0;
        _workspacePrefixVerified = 0;
        _workspacePrefixReusedLeaves = 0;
        _workspacePrefixChoiceUnshared = 0;
        _workspacePrefixExtraTurnUnshared = 0;
        _workspacePrefixNotInProgress = 0;
        _workspaceTurnLoopGuarded = 0;
        _workspaceCrossTurnNoProgressPruned = 0;
        _workspaceEquivalence = SearchEquivalenceDiagnostics.Empty;
        _workspacePeakBytes = 0;
        _workspaceGrowablePeakBytes = 0;
        _workspaceOverflowParents = 0;
        _workspaceParents = 0;
        _workspaceDirectTicks = 0;
        Stopwatch watch = Stopwatch.StartNew();
        _cancellationToken = cancellationToken;
        _solveWatch = watch;
        BeginProgress(roots);
        _expanded = 0;
        _expandedTotal = 0;
        _transitions = 0;
        _duplicates = 0;
        _deadlinePolls = 0;
        _crossTurnCutEnded = false;
        _deadlineExpiredAtPoll = 0;
        _memoryBudgetExceeded = false;
        _memoryBudgetExceededAtBytes = 0;
        _bestPotionFree = null;
        _incumbent = _options.IncumbentSeed;
        _openingPotionTurn = int.MinValue;
        foreach (Battle root in roots)
            _openingPotionTurn = Math.Max(_openingPotionTurn, root.PlayerTurn);
        _auditBestByPotionCount = null;
        _anyAuditedVictory = false;
        _bestFeatures = default;
        _bestFeaturesNode = -1;
        if (DeadlineExpired()) return DeadlineBeforeStart(storage);
        int width = Math.Max(1, _options.BeamWidth);
        // current and turnFrontier only receive CopyTopTo(width) and the roots. next and ended receive
        // children built in the scratch/free slot; their chunks are allocated on first write and never
        // copied, so a duplicate or terminal child does not grow them. The capacity bound below is an
        // explicit rejection boundary, not a true upper bound for ended: ended accumulates one end-turn
        // child per action layer, and a deep single turn can add more layers than MaxLegalActions.
        int frontierCapacity = Math.Max(width, roots.Count);
        // Every turn-setup root is a first-layer parent, even when the profile's beam is narrower than
        // the setup choice set; the existing search also expands its whole root frontier.
        int budgetStates = SearchMemoryBudget.PoolStateCapacity(_options.StatePoolBudgetBytes,
            SearchMemoryBudget.PlanningBlockBytes, SearchMemoryBudget.PlanningBlockStates);
        int poolLimit = budgetStates;
        if (poolLimit <= width)
            throw new SearchMemoryBudgetException("ended-pool-capacity", _storage.Memory.UsedBytes,
                _storage.Memory.LimitBytes);
        int endedLimit = Math.Min(poolLimit, Math.Max(width + 1, _options.EndedBatchTrigger));
        int initialChildren = Math.Min(poolLimit, Math.Max(64, width * 8));
        // The compaction target is measured in beam widths: it always keeps more than the turn
        // boundary consumes (width) plus the same retention lanes, and never exceeds the new pool
        // limit. Reaching the limit is the only trigger, so a root below it keeps the previous
        // behavior bit for bit.
        _endedCompactionTarget = Math.Clamp(_options.EndedBatchTarget, width, endedLimit - 1);
        _endedCompactions = 0;
        _endedCompressedStates = 0;
        _capacityDropped.Clear();
        bool preserveTheft = _theftPolicy == TheftPolicy.PreserveResources;
        int bandWidth = width;
        bool checkPotionQuota = _effectivePotionPolicy != PotionUseMode.Disabled;
        Pool current = new(frontierCapacity, frontierCapacity, preserveTheft, _scorer,
            _options.SecondRankBand, bandWidth, _aggressivePowerCommitment, _powerCommitments, _commitments,
            _nodes, _routingSignatures, checkPotionQuota,
            _options.PreserveReplayAllocatorOpening, _options.Trace, _storage,
            cancellationToken: _options.UnlimitedNodes ? cancellationToken : default,
            checkpoint: _options.UnlimitedNodes ? SearchCheckpoint : null,
            retentionObserver: _options.RetentionCheckpoint);
        Pool next = new(initialChildren, poolLimit, preserveTheft, _scorer,
            _options.SecondRankBand, bandWidth, _aggressivePowerCommitment, _powerCommitments, _commitments,
            _nodes, _routingSignatures, checkPotionQuota,
            _options.PreserveReplayAllocatorOpening, _options.Trace, _storage,
            cancellationToken: _options.UnlimitedNodes ? cancellationToken : default,
            checkpoint: _options.UnlimitedNodes ? SearchCheckpoint : null,
            retentionObserver: _options.RetentionCheckpoint);
        Pool ended = new(Math.Min(initialChildren, endedLimit), endedLimit, preserveTheft, _scorer,
            _options.SecondRankBand, bandWidth, _aggressivePowerCommitment, _powerCommitments, _commitments,
            _nodes, _routingSignatures, checkPotionQuota,
            _options.PreserveReplayAllocatorOpening, _options.Trace, _storage,
            cancellationToken: _options.UnlimitedNodes ? cancellationToken : default,
            checkpoint: _options.UnlimitedNodes ? SearchCheckpoint : null,
            retentionObserver: _options.RetentionCheckpoint);
        Pool turnFrontier = new(frontierCapacity, frontierCapacity, preserveTheft, _scorer,
            _options.SecondRankBand, bandWidth, _aggressivePowerCommitment, _powerCommitments, _commitments,
            _nodes, _routingSignatures, checkPotionQuota,
            _options.PreserveReplayAllocatorOpening, _options.Trace, _storage,
            cancellationToken: _options.UnlimitedNodes ? cancellationToken : default,
            checkpoint: _options.UnlimitedNodes ? SearchCheckpoint : null,
            retentionObserver: _options.RetentionCheckpoint);

        RouteKey? best = null;
        int bestNode = 0;
        bool bestVictory = false;
        void Consider(in Battle state, int node, in RouteKey key,
            in ScoreFeatures features)
        {
            if (best is { } current && CompareRoutes(key, current) >= 0)
                return;
            best = key;
            bestNode = node;
            bestVictory = state.Outcome == Outcome.Victory;
            _bestFeatures = features;
            _bestFeaturesNode = node;
        }
        // The zero-damage stop ends the search after the current turn once a victory lost no HP,
        // used no explicit potion and left the player at full, undiminished HP (growth targets,
        // which disable it there, are rejected at admission).
        bool zeroDamageVictory = false;
        void ConsiderCandidate(in Battle state, int node, in ScoreFeatures features, in RouteKey key)
        {
            bool admissible;
            if (_auditPotionPolicy)
            {
                admissible = ConsiderAudited(node, features, key);
            }
            else
            {
                Consider(state, node, key, features);
                admissible = features.ExplicitPotionUseCount == 0;
            }
            TightenIncumbent(state, features, key, admissible);
        }
        void ConsiderTerminal(in Battle state, int node, in ScoreFeatures features, double score)
        {
            int actionCount = Depth(node);
            RouteKey key = Key(state, features, actionCount);
            ConsiderCandidate(state, node, features, key);
            if (!features.AllEnemiesDead)
                return;
            int explicitPotionUses = ExplicitPotionUses(node);
            if (explicitPotionUses == 0
                && features.CumulativePlayerHpLost == 0
                && features.PlayerMaxHp >= _scorer.RulesForCodec.InitialPlayerMaxHp
                && features.PlayerHp >= features.PlayerMaxHp
                && _scorer.RelicGoalsMet(features)
                && (!_options.UnlimitedNodes || !_options.HasUnboundedGrowthTargets
                    && _zeroGrowthBudget
                    && _scorer.GrowthTargetsSatisfied(features)
                    && key.OutstandingStolenResource == 0))
            {
                zeroDamageVictory = true;
            }
        }

        _routingSignatureIds.Clear();
        for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeadlineExpired()) return DeadlineBeforeStart(storage);
            Battle root = roots[rootIndex];
            (double rootScore, double rootRank, double rootPureRank, int rootPotionCost,
                ScoreFeatures rootFeatures) =
                Score(root, 0, terminal: false, isEndTurn: false);
            TurnProgressState rootProgress = InitialTurnProgress(in root);
            EnemyProgressHighs rootEnemyHighs = CaptureEnemyProgressHighs(in root);
            int rootEnemyHighsId = EnemyProgressHighsEmpty(in rootEnemyHighs)
                ? 0 : _nodes.AddProgressEnemyHighs(in rootEnemyHighs);
            rootProgress = rootProgress with { EnemyHighsId = rootEnemyHighsId };
            int rootCrossTurnId = _nodes.AddCrossTurn(CaptureCrossTurnProgress(in root, in rootFeatures));
            _nodes.Add(new RouteNode(-1, Math.Max(0, 0), default, 0, 0,
                ushort.MaxValue, ushort.MaxValue, int.MinValue, 0,
                0, int.MinValue, 0, 0, 0, int.MaxValue, 0d, 0, 0, 0, 0, rootProgress,
                rootCrossTurnId), 0,
                _options.InitialBrightestFlameMaxHpSpent);
            (int rootReachable, int rootZeroCost, _) = _scorer.ReachableHandPotential(root);
            PocketwatchState(root, out int rootPocketwatchThreshold, out int rootPocketwatchPlayed,
                out int rootPocketwatchLastTurn);
            RetentionFacts rootFacts = RetentionFacts.From(
                rootFeatures, 0, rootReachable, rootZeroCost,
                SearchRouteTraits.None, rootPocketwatchThreshold, rootPocketwatchPlayed,
                rootPocketwatchLastTurn, _scorer.InitialRetainedAttackValue,
                retention: _scorer.CaptureRetentionValues(root));
            turnFrontier.Add(root, rootIndex, rootScore, rootRank, rootPotionCost, rootFacts,
                0, 0);
        }
        if (DeadlineExpired()) return DeadlineBeforeStart(storage);
        TerminalSink terminal = ConsiderTerminal;
        int turns = 0;
        if (_parallelSearch)
        {
            _parallelExpansion = new ParallelExpansion(this, searchThreads);
            current.Parallel = _parallelExpansion;
            next.Parallel = _parallelExpansion;
            ended.Parallel = _parallelExpansion;
            turnFrontier.Parallel = _parallelExpansion;
            if (SearchVerificationHooks.VerifySelectionBatchFailureRecovery)
            {
                current.BatchFailureContract = RunSelectionBatchFailureContract;
                next.BatchFailureContract = RunSelectionBatchFailureContract;
                ended.BatchFailureContract = RunSelectionBatchFailureContract;
                turnFrontier.BatchFailureContract = RunSelectionBatchFailureContract;
            }
        }
        try
        {
        while (turnFrontier.Count > 0 && turns < _options.MaxTurns && !OutOfBudget(watch))
        {
            SearchCheckpoint();
            ProgressCheckpoint(turnFrontier, force: turns == 0, best is null ? -1 : bestNode,
                best ?? default);
            turns++;
            int crossTurnCutBefore = CrossTurnNoProgressPrunedTotal;
            // The previous turn's end-turn pool is no longer a route consumer; clear it before the
            // boundary mark-sweep so only states that can still win a seat are retained.
            ended.Clear();
            next.Clear();
            CompactRouteNodes(ref bestNode, current, next, ended, turnFrontier);
            current.Clear();
            _tracePhase = "turn-frontier";
            turnFrontier.TracePhase = _tracePhase;
            turnFrontier.CopyTopTo(current, turns == 1 ? frontierCapacity : width);
            int playDepth = 0;
            while (current.Count > 0 && !OutOfBudget(watch))
            {
                SearchCheckpoint();
                PollProgressRequest();
                next.Clear();
                if (_nodes.Count >= _nodeCleanupThreshold)
                {
                    CompactRouteNodes(ref bestNode, current, next, ended, turnFrontier);
                    _nodeCleanupThreshold = Math.Max(_nodeCleanupThreshold, _nodes.Count * 2);
                }
                if (_parallelExpansion is null)
                {
                    for (int index = 0; index < current.Count && !OutOfBudget(watch); index++)
                    {
                        SearchCheckpoint();
                        CountExpanded();
                        ref readonly Battle parent = ref current.State(index);
                        ParentExpansionContext context = CaptureParentContext(current, index);
                        int duplicatesBefore = _duplicates;
                        long directStarted = Stopwatch.GetTimestamp();
                        _workspace.ExpandDirect(in parent, in context, next, ended, terminal,
                            _cancellationToken);
                        _workspaceDirectTicks += Stopwatch.GetTimestamp() - directStarted;
                        ObserveParent(_workspace, duplicatesBefore);
                        AccumulateWorkspaceDiagnostics(_workspace);
                    }
                    ObserveWorkspacePeak();
                }
                else
                {
                    CaptureLayerContexts(current);
                    _parallelExpansion.RunLayer(current, next, ended, terminal,
                        _layerContexts, current.Count);
                    ObserveWorkspacePeak();
                }
                current.Clear();
                _tracePhase = "next";
                next.TracePhase = _tracePhase;
                next.CopyTopTo(current, width);
                PrunePool(current);
                playDepth++;
                ProgressCheckpoint(current, force: false, best is null ? -1 : bestNode,
                    best ?? default);
            }
            if (_options.UnlimitedNodes && OutOfBudget(watch)) break;
            // The cross-turn cut left no next-turn state: this turn's start states are the horizon,
            // as at MaxTurns, and compete with the terminal routes in the final ordering. Clearing
            // them would end a search that cannot make progress without any route.
            if (ended.Count == 0 && CrossTurnNoProgressPrunedTotal > crossTurnCutBefore)
            {
                _crossTurnCutEnded = true;
                break;
            }
            turnFrontier.Clear();
            // BeamRetentionPolicy.RankBest at the end-turn frontier: the turn-boundary hand leader is
            // a required representative of every potion group.
            _tracePhase = "turn-boundary";
            ended.TracePhase = _tracePhase;
            ended.CopyTopTo(turnFrontier, width, turnBoundary: true);
            PrunePool(turnFrontier);
            AssignCrossTurnProgress(turnFrontier);
            ProgressCheckpoint(turnFrontier, force: false, best is null ? -1 : bestNode,
                best ?? default);
            if (zeroDamageVictory)
                break;
        }
        }
        catch (SearchDeadlineException)
        {
            _deadlineReached = true;
        }
        catch (SearchMemoryBudgetException error)
        {
            _memoryBudgetExceeded = true;
            _memoryBudgetExceededAtBytes = error.UsedBytes;
        }
        catch (SearchAdoptionException adoption)
        {
            _adoptedResult = adoption.Result;
        }
        finally
        {
            _parallelExpansion?.Dispose();
            _parallelExpansion = null;
        }
        if (_adoptedResult is { } adopted)
        {
            _trace?.Finish("adopted", turns, _expanded);
            return adopted;
        }
        if (_auditPotionPolicy)
        {
            // The existing final ordering evaluates every surviving boundary state next to the terminal
            // routes; an audited request must also see a potion-use frontier even when a (too-expensive)
            // victory already exists, so the boundary states are always collected here.
            for (int index = 0; index < turnFrontier.Count; index++)
            {
                if (_options.UnlimitedNodes && (index & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DeadlineExpired()) { _deadlineReached = true; break; }
                }
                int node = turnFrontier.Node(index);
                int actionCount = Depth(node);
                ref readonly Battle state = ref turnFrontier.State(index);
                ScoreFeatures features = _scorer.Features(state, actionCount, includeRetentionOnly: false);
                ConsiderCandidate(state, node, features, Key(state, features, actionCount));
            }
            if (SelectAudited(out int auditedNode, out RouteKey auditedKey, out bool auditedVictory))
            {
                best = auditedKey;
                bestNode = auditedNode;
                bestVictory = auditedVictory;
            }
            _trace?.Finish(TraceFinishReason(zeroDamageVictory, watch, turns),
                turns, _expanded);
            BeamResult audited = new(_auditPotionPolicy && best is null ? [] : Route(bestNode),
                bestVictory, best?.Score ?? double.NegativeInfinity,
                _expanded, _transitions, _duplicates, turns, _pruned, watch.Elapsed)
            {
                CapacityPruned = _capacityPruned,
                TurnLoopGuarded = TurnLoopGuardedTotal,
                CrossTurnNoProgressPruned = CrossTurnNoProgressPrunedTotal,
                RouteProgressDetailPeakCount = _nodes.PeakProgressEnemyHighsCount,
                RouteProgressDetailStorageBytes = _nodes.ProgressEnemyHighsStorageBytes,
                EndTurnPrefixShared = EndTurnPrefixSharedTotal,
                EndTurnPrefixUnshared = EndTurnPrefixUnsharedTotal,
                EndTurnPrefixVerified = EndTurnPrefixVerifiedTotal,
                ThreatProjectionVerified = _scorer.ThreatProjectionChecks,
                EndTurnPrefixReusedLeaves = EndTurnPrefixReusedLeavesTotal,
                EndTurnPrefixChoiceUnshared = EndTurnPrefixChoiceUnsharedTotal,
                EndTurnPrefixExtraTurnUnshared = EndTurnPrefixExtraTurnUnsharedTotal,
                EndTurnPrefixNotInProgress = EndTurnPrefixNotInProgressTotal,
                CapacityDroppedChildren = _capacityDropped.Count == 0
                    ? BeamResult.NoCapacityDrops
                    : _capacityDropped,
                EndedCompactions = _endedCompactions,
                EndedCompressedStates = _endedCompressedStates,
                PotionPolicySatisfied = best is not null,
                BestRootIndex = RootIndex(bestNode),
                WorkerYieldCount = _options.Pacer?.YieldCount ?? 0,
                FrameRecoveryWaitCount = _options.Pacer?.FrameRecoveryWaitCount ?? 0,
                FrameRecoveryWaitDuration = _options.Pacer?.FrameRecoveryWaitDuration ?? TimeSpan.Zero,
                MaxObservedGcPause = _options.Pacer?.MaxObservedGcPause ?? TimeSpan.Zero,
                Equivalence = EquivalenceDiagnostics(),
            ExpandedNodesTotal = _expandedTotal,
            StopReason = TraceFinishReason(zeroDamageVictory, watch, turns),
            SearchMemoryUsedBytes = _storage.Memory.UsedBytes,
            SearchMemoryPeakBytes = _storage.Memory.PeakBytes,
            SearchMemoryLimitBytes = _storage.Memory.LimitBytes,
            SearchMemoryBudgetExceeded = _memoryBudgetExceeded
                || _storage.Memory.Exceeded,
            SearchMemoryExceededAtBytes = _memoryBudgetExceeded
                ? _memoryBudgetExceededAtBytes : _storage.Memory.ExceededAtBytes,
            SoftTimeBudgetReached = SoftTimeReached(watch),
            DeadlineExpiredAtPoll = Interlocked.Read(ref _deadlineExpiredAtPoll),
            RouteNodeStorageBytes = _nodes.StorageBytes,
            RouteNodeCount = _nodes.Count,
            RouteNodePeakCount = _nodes.PeakCount,
            SearchWorkspacePeakBytes = _workspacePeakBytes,
            SearchWorkspaceGrowablePeakBytes = _workspaceGrowablePeakBytes,
            WorkspaceOverflowParents = _workspaceOverflowParents,
            WorkspaceParents = _workspaceParents,
            WorkspaceDirectDuration = Stopwatch.GetElapsedTime(0, _workspaceDirectTicks),
            };
            return audited;
        }

        // Without a victory, the surviving turn-start states compete with the terminal routes under the
        // same final ordering (the existing search evaluates its boundary nodes alongside terminals).
        if (!bestVictory)
        {
            for (int index = 0; index < turnFrontier.Count; index++)
            {
                if (_options.UnlimitedNodes && (index & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DeadlineExpired()) { _deadlineReached = true; break; }
                }
                int node = turnFrontier.Node(index);
                int actionCount = Depth(node);
                ref readonly Battle state = ref turnFrontier.State(index);
                ScoreFeatures features =
                    _scorer.Features(state, actionCount, includeRetentionOnly: false);
                Consider(state, node, Key(state, features, actionCount), features);
            }
        }
        // A hard-deadline stop also needs the fallback: the frontier pass above breaks on the
        // expired deadline before it considers any state, so without it a member that reached its
        // deadline returns an empty route although its frontier holds hundreds of states (FS8
        // Necrobinder Queen root: 19 of 20 members came back empty).
        if ((_memoryBudgetExceeded || _deadlineReached
                || _options.UnlimitedNodes && SoftTimeReached(watch)
                || _options.NodeBudgetActive && !NodeBudgetAvailable)
            && Route(bestNode).Length == 0)
        {
            int fallback = BudgetFallbackNode(current, ended, turnFrontier);
            if (fallback >= 0)
                bestNode = fallback;
        }
        _trace?.Finish(TraceFinishReason(zeroDamageVictory, watch, turns),
            turns, _expanded);
        BeamResult result = new(Route(bestNode), bestVictory,
            best?.Score ?? double.NegativeInfinity,
            _expanded, _transitions, _duplicates, turns, _pruned, watch.Elapsed)
        {
            CapacityPruned = _capacityPruned,
            TurnLoopGuarded = TurnLoopGuardedTotal,
            CrossTurnNoProgressPruned = CrossTurnNoProgressPrunedTotal,
            RouteProgressDetailPeakCount = _nodes.PeakProgressEnemyHighsCount,
            RouteProgressDetailStorageBytes = _nodes.ProgressEnemyHighsStorageBytes,
            EndTurnPrefixShared = EndTurnPrefixSharedTotal,
            EndTurnPrefixUnshared = EndTurnPrefixUnsharedTotal,
            EndTurnPrefixVerified = EndTurnPrefixVerifiedTotal,
            ThreatProjectionVerified = _scorer.ThreatProjectionChecks,
            EndTurnPrefixReusedLeaves = EndTurnPrefixReusedLeavesTotal,
            EndTurnPrefixChoiceUnshared = EndTurnPrefixChoiceUnsharedTotal,
            EndTurnPrefixExtraTurnUnshared = EndTurnPrefixExtraTurnUnsharedTotal,
            EndTurnPrefixNotInProgress = EndTurnPrefixNotInProgressTotal,
            CapacityDroppedChildren = _capacityDropped.Count == 0
                ? BeamResult.NoCapacityDrops
                : _capacityDropped,
            EndedCompactions = _endedCompactions,
            EndedCompressedStates = _endedCompressedStates,
            BestRootIndex = RootIndex(bestNode),
            WorkerYieldCount = _options.Pacer?.YieldCount ?? 0,
            FrameRecoveryWaitCount = _options.Pacer?.FrameRecoveryWaitCount ?? 0,
            FrameRecoveryWaitDuration = _options.Pacer?.FrameRecoveryWaitDuration ?? TimeSpan.Zero,
            MaxObservedGcPause = _options.Pacer?.MaxObservedGcPause ?? TimeSpan.Zero,
            Equivalence = EquivalenceDiagnostics(),
            ExpandedNodesTotal = _expandedTotal,
            StopReason = TraceFinishReason(zeroDamageVictory, watch, turns),
            SearchMemoryUsedBytes = _storage.Memory.UsedBytes,
            SearchMemoryPeakBytes = _storage.Memory.PeakBytes,
            SearchMemoryLimitBytes = _storage.Memory.LimitBytes,
            SearchMemoryBudgetExceeded = _memoryBudgetExceeded
                || _storage.Memory.Exceeded,
            SearchMemoryExceededAtBytes = _memoryBudgetExceeded
                ? _memoryBudgetExceededAtBytes : _storage.Memory.ExceededAtBytes,
            SoftTimeBudgetReached = SoftTimeReached(watch),
            DeadlineExpiredAtPoll = Interlocked.Read(ref _deadlineExpiredAtPoll),
            RouteNodeStorageBytes = _nodes.StorageBytes,
            RouteNodeCount = _nodes.Count,
            RouteNodePeakCount = _nodes.PeakCount,
            SearchWorkspacePeakBytes = _workspacePeakBytes,
            SearchWorkspaceGrowablePeakBytes = _workspaceGrowablePeakBytes,
            WorkspaceOverflowParents = _workspaceOverflowParents,
            WorkspaceParents = _workspaceParents,
            WorkspaceDirectDuration = Stopwatch.GetElapsedTime(0, _workspaceDirectTicks),
        };
        return result;
    }

    private string TraceFinishReason(bool zeroDamageVictory, Stopwatch watch, int turns)
    {
        if (zeroDamageVictory)
            return "zero-damage-victory";
        if (OutOfBudget(watch))
            return _options.UnlimitedNodes && !_options.NodeBudgetActive
                ? _memoryBudgetExceeded ? "memory" : "time" : "budget";
        if (turns >= _options.MaxTurns)
            return "max-turns";
        if (_crossTurnCutEnded)
            return "no-progress";
        return "frontier-empty";
    }

    /// <summary>Records an audited candidate; true when the potion policy admits it right now.</summary>
    private bool ConsiderAudited(int node, in ScoreFeatures features, in RouteKey key)
    {
        if (key.Won)
            _anyAuditedVictory = true;
        int optionalPotionCount = Math.Max(0, features.ExplicitPotionUseCount);
        int optionalPotionStrategicCost = key.OptionalPotionPrice;
        PotionUseMode effectivePotionPolicy = _effectivePotionPolicy == PotionUseMode.Disabled
            && optionalPotionCount > 0
                ? PotionUseMode.Smart
                : _effectivePotionPolicy;
        RouteKey auditedKey = key with
        {
            OptionalPotionCount = optionalPotionCount,
        };
        Candidate candidate = new(
            node,
            auditedKey,
            features.ExplicitPotionUseCount,
            features.PotionUseCount,
            features.ExplicitPotionStrategicCost,
            optionalPotionStrategicCost,
            Math.Max(0, AmbergrisUses(node)),
            features.PlayerHp,
            features.ProjectedDeathSaveUseCount,
            effectivePotionPolicy);
        if (candidate.ExplicitPotionUseCount == 0
            && (_bestPotionFree is not { } free || ComparePotionFree(candidate, free) < 0))
        {
            _bestPotionFree = candidate;
        }
        Dictionary<int, Candidate> bestByCount = _auditBestByPotionCount ??= new();
        int bucket = candidate.ExplicitPotionUseCount;
        if (!bestByCount.TryGetValue(bucket, out Candidate current)
            || CompareAudited(candidate, current) < 0)
        {
            bestByCount[bucket] = candidate;
        }
        return IsCurrentAuditedPolicyEligible(candidate);
    }

    /// <summary>
    /// The existing <c>FinalPlanOrdering.Select</c> filter and ordering, applied to the best candidate
    /// per explicit potion count. The request's audited baseline is authoritative when present; otherwise
    /// the best potion-free candidate of this search supplies it, exactly like the existing evaluated set.
    /// </summary>
    private bool SelectAudited(out int node, out RouteKey key, out bool victory)
    {
        Candidate? selected = null;
        if (_auditBestByPotionCount is { } bestByCount)
        {
            foreach (Candidate candidate in bestByCount.Values)
            {
                if (!IsCurrentAuditedPolicyEligible(candidate))
                {
                    continue;
                }
                if (selected is not { } current || CompareAudited(candidate, current) < 0)
                    selected = candidate;
            }
        }
        if (selected is not { } bestCandidate)
        {
            node = 0;
            key = default;
            victory = false;
            return false;
        }
        node = bestCandidate.Node;
        key = bestCandidate.Key;
        victory = bestCandidate.Key.Won;
        return true;
    }

    private bool IsCurrentAuditedPolicyEligible(Candidate candidate)
    {
        bool hasPotionFree = _potionFreeBaseline is not null || _bestPotionFree is not null;
        bool potionFreeWon = _potionFreeBaseline?.Won ?? _bestPotionFree?.Key.Won ?? false;
        int potionFreeDeficit = _potionFreeBaseline?.HpDeficit
            ?? _bestPotionFree?.Key.StrategicHpDeficit
            ?? _ordering.InitialPlayerHp;
        int potionFreePlayerHp = _potionFreeBaseline?.PlayerHp ?? _bestPotionFree?.PlayerHp ?? 0;
        int potionFreeDeathSaves = _potionFreeBaseline?.DeathSaveUseCount
            ?? (_bestPotionFree is { } free ? free.ProjectedDeathSaveUseCount : int.MaxValue);
        int potionFreeOutstanding = _potionFreeBaseline?.OutstandingStolenResource
            ?? _bestPotionFree?.Key.OutstandingStolenResource ?? int.MaxValue;
        bool anyRouteWon = potionFreeWon || _anyAuditedVictory;
        return IsAuditedPolicyEligible(candidate, hasPotionFree, potionFreeWon, potionFreeDeficit,
            potionFreePlayerHp, potionFreeDeathSaves, potionFreeOutstanding, anyRouteWon);
    }

    private bool IsAuditedPolicyEligible(Candidate candidate, bool hasPotionFree, bool potionFreeWon,
        int potionFreeDeficit, int potionFreePlayerHp, int potionFreeDeathSaves, int potionFreeOutstanding,
        bool anyRouteWon)
    {
        if (_options.Potions.ForceAllDisabled && candidate.ExplicitPotionUseCount > 0)
            return false;
        bool protectsDeathSave = hasPotionFree && potionFreeWon && candidate.Key.Won
            && _effectivePotionPolicy != PotionUseMode.Disabled
            && candidate.Key.OptionalPotionCount > 0
            && candidate.ProjectedDeathSaveUseCount < potionFreeDeathSaves;
        // FinalPlanOrdering.Select: under the preserve policy, more potion use is allowed when it
        // recovers outstanding loot; the soft-policy exception requires an actual potion use, while
        // the Ambergris exception applies to the recovered-loot key even with zero potions.
        bool recoversLoot = _theftPolicy == TheftPolicy.PreserveResources
            && candidate.Key.OutstandingStolenResource < potionFreeOutstanding;
        bool recoversLootWithPotion = recoversLoot && candidate.PotionUseCount > 0;
        bool passesSoftPolicy = PotionUsePolicy.IsEligible(
                candidate.EffectivePotionPolicy,
                candidate.Key.OptionalPotionCount,
                PotionUsePolicy.SmartRequiredHpSaved(
                    candidate.OptionalPotionStrategicCost, _ordering.BossHpRelief),
                potionFreeWon,
                potionFreeDeficit,
                anyRouteWon,
                candidate.Key.Won,
                candidate.Key.StrategicHpDeficit)
            || protectsDeathSave
            || recoversLootWithPotion;
        bool passesAmbergris = protectsDeathSave
            || recoversLoot
            || PotionUsePolicy.MeetsAmbergrisRestriction(
                hasPotionFree,
                candidate.AmbergrisUses,
                candidate.OptionalPotionStrategicCost,
                _scorer.RulesForCodec.InitialPlayerMaxHp,
                potionFreePlayerHp,
                candidate.PlayerHp);
        return passesSoftPolicy && passesAmbergris;
    }

    /// <summary>Negative when <paramref name="left"/> wins the audited final ordering.</summary>
    private int CompareAudited(in Candidate left, in Candidate right)
    {
        int comparison = CompareRoutes(left.Key, right.Key);
        return comparison != 0 ? comparison : left.Node.CompareTo(right.Node);
    }

    /// <summary>
    /// <c>the existing search's FinalPlanOrdering.ComparePotionFreePolicyBaselines</c> restricted to the
    /// admitted keys (growth and sold HP are zero/rejected; the theft recovery key is included); the
    /// route index breaks exact ties.
    /// </summary>
    private int ComparePotionFree(in Candidate left, in Candidate right)
    {
        int comparison = right.Key.Won.CompareTo(left.Key.Won);
        if (comparison != 0) return comparison;
        if (!left.Key.Won && !right.Key.Won)
        {
            comparison = RightSurvives(right).CompareTo(RightSurvives(left));
            if (comparison != 0) return comparison;
        }
        comparison = left.Key.DeathSaveUseCount.CompareTo(right.Key.DeathSaveUseCount);
        if (comparison != 0) return comparison;
        if (_theftPolicy == TheftPolicy.PreserveResources)
        {
            comparison = left.Key.OutstandingStolenResource.CompareTo(right.Key.OutstandingStolenResource);
            if (comparison != 0) return comparison;
        }
        comparison = left.Key.StrategicHpDeficit.CompareTo(right.Key.StrategicHpDeficit);
        if (comparison != 0) return comparison;
        comparison = right.Key.StrategyGoalHpCredit.CompareTo(left.Key.StrategyGoalHpCredit);
        if (comparison != 0) return comparison;
        comparison = right.Key.StrategyGoalCount.CompareTo(left.Key.StrategyGoalCount);
        if (comparison != 0) return comparison;
        comparison = left.Key.CombatEndedTurn.CompareTo(right.Key.CombatEndedTurn);
        if (comparison != 0) return comparison;
        comparison = left.Key.HealthResourceCost.CompareTo(right.Key.HealthResourceCost);
        if (comparison != 0) return comparison;
        comparison = right.Key.LongTermResourceValue.CompareTo(left.Key.LongTermResourceValue);
        if (comparison != 0) return comparison;
        comparison = left.Key.AngerCopiesGenerated.CompareTo(right.Key.AngerCopiesGenerated);
        if (comparison != 0) return comparison;
        comparison = left.Key.EnemyHp.CompareTo(right.Key.EnemyHp);
        if (comparison != 0) return comparison;
        comparison = right.Key.Score.CompareTo(left.Key.Score);
        if (comparison != 0) return comparison;
        comparison = left.Key.ActionCount.CompareTo(right.Key.ActionCount);
        return comparison != 0 ? comparison : left.Node.CompareTo(right.Node);
    }

    private static int RightSurvives(in Candidate candidate) => candidate.Key.Survives ? 1 : 0;

    private delegate void TerminalSink(in Battle state, int node, in ScoreFeatures features,
        double score);

    /// <summary>
    /// The keys of the existing final ordering (<c>FinalPlanOrdering.Select</c>) that the admitted requests
    /// reach. Growth and relic counter goal credits are carried. The
    /// sold-HP and boundary tie-breakers are not ported yet. The theft recovery key is carried only
    /// under <c>PreserveResources</c>, matching <c>TheftEncounterStrategy.CompareRecovery</c>.
    /// </summary>
    internal readonly record struct RouteKey(
        bool Won,
        bool Survives,
        int DeathSaveUseCount,
        int OutstandingStolenResource,
        int StrategicHpDeficit,
        int StrategyGoalHpCredit,
        int StrategyGoalCount,
        int CombatEndedTurn,
        int HealthResourceCost,
        int LongTermResourceValue,
        int AngerCopiesGenerated,
        int OptionalPotionCount,
        int BoundaryRank,
        int EnemyHp,
        double Score,
        int ActionCount,
        int PotionHpCost = 0,
        int OptionalPotionPrice = 0)
    {
        /// <summary>
        /// The strategic HP deficit with the route's optional potions priced in HP: the HP the Smart policy
        /// requires them to save for their price (<see cref="PotionUsePolicy.OptionalPotionPrice"/>, then
        /// <see cref="PotionUsePolicy.SmartRequiredHpSaved"/>). The Smart threshold reads the same price,
        /// so ranking by it agrees with the threshold and also prices each further potion.
        /// </summary>
        public int EffectiveHpDeficit => StrategicHpDeficit + PotionHpCost;
    }

    /// <summary>An audited final candidate: the ordering key plus the values its policy filter reads.</summary>
    internal readonly record struct Candidate(
        int Node,
        RouteKey Key,
        int ExplicitPotionUseCount,
        int PotionUseCount,
        int ExplicitPotionStrategicCost,
        int OptionalPotionStrategicCost,
        int AmbergrisUses,
        int PlayerHp,
        int ProjectedDeathSaveUseCount,
        PotionUseMode EffectivePotionPolicy);

    private RouteKey Key(in Battle state, in ScoreFeatures features, int actionCount)
    {
        bool won = InterimResultOrdering.IsCompleteVictory(
            actionCount, features.AllEnemiesDead, features.PlayerDead, features.ProjectedPlayerHp);
        int relicHeal = ActEndingBossPolicy.RankedPostCombatRelicHeal(
            _ordering.PostCombatRelicHeal, won, features.PlayerHp, features.PlayerMaxHp);
        int strategicHpDeficit = ActEndingBossPolicy.StrategicHpDeficit(
            features.CumulativePlayerHpLost,
            Math.Max(0, _scorer.RulesForCodec.InitialPlayerMaxHp - features.PlayerMaxHp),
            features.RecoveredPlayerHp + relicHeal,
            _ordering.BossHpRelief,
            features.DeathSaveHpRestored) - features.StrategicHpCredit;
        int optionalPotionCount = features.ExplicitPotionUseCount;
        int potionPrice = OptionalPotionPrice(_scorer.MeasurePotions(state));
        int potionHpCost = PotionHpCost(potionPrice);
        return new RouteKey(
            won,
            !features.PlayerDead && features.ProjectedPlayerHp > 0,
            features.ProjectedDeathSaveUseCount,
            _theftPolicy == TheftPolicy.PreserveResources ? features.OutstandingStolenResource : 0,
            strategicHpDeficit,
            features.StrategyGoalHpCredit,
            features.StrategyGoalCount,
            won ? state.PlayerTurn : int.MaxValue,
            _ordering.InitialPlayerHp - features.PlayerHp
                + _scorer.RulesForCodec.InitialPlayerMaxHp - features.PlayerMaxHp,
            features.LongTermResourceValue,
            features.AngerCopiesGenerated,
            optionalPotionCount,
            // the existing search's PolicyBoundaryRank: None 0, EventDefeat 4. The final ordering compares
            // it after the resource/anger keys and before enemy HP (FinalPlanOrdering).
            state.Outcome == Outcome.EventDefeat ? 4 : 0,
            features.EnemyHp,
            features.Score,
            actionCount,
            potionHpCost,
            potionPrice);
    }

    private int CompareRoutes(in RouteKey left, in RouteKey right) => CompareRouteKeys(left, right);

    /// <summary>
    /// Negative when <paramref name="left"/> is the better route. Static so the panel ranks the search's
    /// previews by the same rule.
    /// </summary>
    internal static int CompareRouteKeys(in RouteKey left, in RouteKey right)
    {
        int comparison = right.Won.CompareTo(left.Won);
        if (comparison != 0) return comparison;
        comparison = (!left.Won && !left.Survives ? 1 : 0).CompareTo(!right.Won && !right.Survives ? 1 : 0);
        if (comparison != 0) return comparison;
        comparison = left.DeathSaveUseCount.CompareTo(right.DeathSaveUseCount);
        if (comparison != 0) return comparison;
        // TheftEncounterStrategy.CompareRecovery: only the preserve policy ranks the unrecovered loot;
        // Key leaves it zero otherwise.
        comparison = left.OutstandingStolenResource.CompareTo(right.OutstandingStolenResource);
        if (comparison != 0) return comparison;
        // Potions priced in HP first; on a tie the lower raw deficit wins, which is the Smart threshold's
        // ">= required" (a potion route that saves exactly its price is preferred, as before).
        comparison = left.EffectiveHpDeficit.CompareTo(right.EffectiveHpDeficit);
        if (comparison != 0) return comparison;
        comparison = left.StrategicHpDeficit.CompareTo(right.StrategicHpDeficit);
        if (comparison != 0) return comparison;
        comparison = right.StrategyGoalHpCredit.CompareTo(left.StrategyGoalHpCredit);
        if (comparison != 0) return comparison;
        comparison = right.StrategyGoalCount.CompareTo(left.StrategyGoalCount);
        if (comparison != 0) return comparison;
        comparison = left.CombatEndedTurn.CompareTo(right.CombatEndedTurn);
        if (comparison != 0) return comparison;
        comparison = left.HealthResourceCost.CompareTo(right.HealthResourceCost);
        if (comparison != 0) return comparison;
        comparison = right.LongTermResourceValue.CompareTo(left.LongTermResourceValue);
        if (comparison != 0) return comparison;
        comparison = left.AngerCopiesGenerated.CompareTo(right.AngerCopiesGenerated);
        if (comparison != 0) return comparison;
        // FinalPlanOrdering.Select: PolicyBoundaryRank, then OptionalPotionCount, then StrategicSold.
        // StrategicSold is the request's committed sold HP plus the route's future sold HP; Beam
        // keeps no route-level future sold HP, so the key is the same for every route of a request
        // and is left out.
        comparison = left.BoundaryRank.CompareTo(right.BoundaryRank);
        if (comparison != 0) return comparison;
        comparison = left.OptionalPotionCount.CompareTo(right.OptionalPotionCount);
        if (comparison != 0) return comparison;
        comparison = left.EnemyHp.CompareTo(right.EnemyHp);
        if (comparison != 0) return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0 ? comparison : left.ActionCount.CompareTo(right.ActionCount);
    }

    /// <summary>
    /// Compares two solved results with the same final-ordering key the solver used to select its best
    /// route. Both routes are replayed from their recorded root and scored at their final state; a
    /// result whose route cannot be replayed or whose root index is invalid is treated as the worse
    /// one. Negative means <paramref name="left"/> is better, zero equal, positive worse. The report
    /// command calls this after solving with the current code; the frozen result's route is replayed
    /// with the same kernel, so the comparison is against the solver's own key, not a proxy score.
    /// </summary>
    internal int CompareResultQuality(IReadOnlyList<Battle> roots, in BeamResult left, in BeamResult right)
    {
        if (!TryCompareResultQuality(roots, left, right, out int comparison))
            return 0;
        return comparison;
    }

    /// <summary>
    /// As <see cref="CompareResultQuality"/>, but reports whether both routes could be replayed and
    /// scored. The report harness uses the false case as "unknown" instead of treating it as a tie.
    /// </summary>
    internal bool TryCompareResultQuality(IReadOnlyList<Battle> roots, in BeamResult left,
        in BeamResult right, out int comparison)
        => TryCompareResultQuality(roots, left, right, out comparison, out _, out _);

    /// <summary>
    /// As <see cref="TryCompareResultQuality"/>, also reporting the last player turn each route
    /// reaches. The stop path uses the turn of the completed member it substitutes.
    /// </summary>
    internal bool TryCompareResultQuality(IReadOnlyList<Battle> roots, in BeamResult left,
        in BeamResult right, out int comparison, out int leftThroughTurn, out int rightThroughTurn)
    {
        comparison = 0;
        leftThroughTurn = 0;
        rightThroughTurn = 0;
        bool leftOk = TryEvaluateResultQuality(roots, left, out RouteKey leftKey, out leftThroughTurn);
        bool rightOk = TryEvaluateResultQuality(roots, right, out RouteKey rightKey,
            out rightThroughTurn);
        if (!leftOk || !rightOk)
            return false;
        comparison = CompareRoutes(leftKey, rightKey);
        return true;
    }

    private bool TryEvaluateResultQuality(IReadOnlyList<Battle> roots, in BeamResult result,
        out RouteKey key, out int throughTurn)
    {
        key = default;
        throughTurn = 0;
        if ((uint)result.BestRootIndex >= (uint)roots.Count)
            return false;
        Battle root = roots[result.BestRootIndex];
        Unsafe.SkipInit(out Battle state);
        Battle.CopyPacked(in root, ref state);
        int actionCount = 0;
        throughTurn = state.PlayerTurn;
        foreach (PlayerAction action in result.BestRoute)
        {
            try
            {
                StepResult step = Kernel.Step(ref state, action, _rules, out ChoiceRequest request);
                if (step != StepResult.Completed)
                    return false;
            }
            catch (CapacityException)
            {
                return false;
            }
            actionCount++;
            throughTurn = state.PlayerTurn;
        }
        ScoreFeatures features = _scorer.Features(state, actionCount, includeRetentionOnly: false);
        key = Key(state, features, actionCount);
        return true;
    }

    /// <summary>
    /// Polls the request's deadline. Polls are numbered from 1; the number of the first poll that found the
    /// deadline expired becomes <see cref="BeamResult.DeadlineExpiredAtPoll"/>, so a replay of a
    /// single-threaded solve can stop at the same poll.
    /// </summary>
    private bool DeadlineExpired()
    {
        long poll = Interlocked.Increment(ref _deadlinePolls);
        if (_options.DeadlineExpired?.Invoke() != true)
            return false;
        Interlocked.CompareExchange(ref _deadlineExpiredAtPoll, poll, 0);
        return true;
    }

    private BeamResult DeadlineBeforeStart(SearchStorage storage) => new([], false,
        double.NegativeInfinity, 0, 0, 0, 0, 0, TimeSpan.Zero)
    {
        SoftTimeBudgetReached = true, PotionPolicySatisfied = false, StopReason = "time",
        DeadlineExpiredAtPoll = Interlocked.Read(ref _deadlineExpiredAtPoll),
        SearchMemoryUsedBytes = storage.Memory.UsedBytes, SearchMemoryPeakBytes = storage.Memory.PeakBytes,
    };

    private void CountExpanded()
    {
        if (_expanded < int.MaxValue) _expanded++;
        _expandedTotal++;
    }

    /// <summary>Publishes one parent's workspace shape to the optional test observer.</summary>
    private void ObserveParent(ExpansionWorkspace workspace, int duplicatesBefore)
    {
        _workspaceParents++;
        if (ParentWorkspaceObserver is not { } observer)
            return;
        observer(new ParentWorkspaceMeasurement(
            workspace.ParentPeakUsedBytes, workspace.ParentPeakAllocatedBytes,
            workspace.ParentChildren, workspace.ParentTerminals, workspace.ParentChoices,
            _duplicates - duplicatesBefore, workspace.ParentDirect));
    }

    /// <summary>
    /// Snapshots the parent route data fixed before this layer. The expansion half receives it and
    /// never reads the live route stores; the commit half remains on the search thread.
    /// </summary>
    private ParentExpansionContext CaptureParentContext(Pool pool, int index)
    {
        int node = pool.Node(index);
        int commitmentIndex = _nodes.CommitmentAt(node);
        PowerCommitmentState commitment = commitmentIndex > 0
            ? _commitments[commitmentIndex - 1]
            : default;
        TurnProgressState progress = _nodes.ProgressAt(node);
        return new ParentExpansionContext(node, _nodes.View(node).Depth, pool.Facts(index),
            pool.RankPosition(index), pool.Rank(index), commitmentIndex > 0, commitment,
            _nodes.TracksBrightestFlameSpent ? _nodes.BrightestFlameSpentAt(node) : 0,
            progress, _nodes.ProgressEnemyHighsAt(progress.EnemyHighsId),
            _nodes.CrossTurnAt(_nodes.CrossTurnIdAt(node)));
    }

    private bool NodeBudgetAvailable
        => _options.UnlimitedNodes && !_options.NodeBudgetActive
            || _expanded < _options.MaxExpandedNodes;

    private bool OutOfBudget(Stopwatch watch)
        => _memoryBudgetExceeded || !NodeBudgetAvailable || DeadlineExpired()
            || !_options.NodeBudgetActive && watch.Elapsed >= _options.SoftTimeBudget;

    /// <summary>
    /// The loop ended on the soft-time limit rather than the node cap or an early stop. A search
    /// that happened to cross the limit exactly at its natural end is reported as timed out; the
    /// flag is diagnostic and never enters the result codec.
    /// </summary>
    private bool SoftTimeReached(Stopwatch watch)
        => !_options.NodeBudgetActive
            && !_memoryBudgetExceeded && NodeBudgetAvailable
            && (_deadlineReached || DeadlineExpired()
                || _options.SoftTimeBudget != TimeSpan.MaxValue
                    && watch.Elapsed >= _options.SoftTimeBudget);

    private int Depth(int node) => _nodes.View(node).Depth;

    private int ExplicitPotionUses(int node)
    {
        int uses = 0;
        for (int cursor = node; _nodes.View(cursor).Parent >= 0;
             cursor = _nodes.View(cursor).Parent)
        {
            if (_nodes.View(cursor).Action.Kind == PlayerActionKind.UsePotion)
                uses++;
        }
        return uses;
    }

    private int AmbergrisUses(int node)
    {
        if (_ambergrisPotionId == ushort.MaxValue)
            return 0;
        int uses = 0;
        for (int cursor = node; _nodes.View(cursor).Parent >= 0;
             cursor = _nodes.View(cursor).Parent)
        {
            PlayerAction action = _nodes.View(cursor).Action;
            if (action.Kind != PlayerActionKind.UsePotion)
                continue;
            if (_nodes.View(cursor).PotionId == _ambergrisPotionId)
                uses++;
        }
        return uses;
    }

    private PlayerAction[] Route(int node)
    {
        List<PlayerAction> route = [];
        for (int cursor = node; _nodes.View(cursor).Parent >= 0;
             cursor = _nodes.View(cursor).Parent)
        {
            route.Add(_nodes.View(cursor).Action);
        }
        route.Reverse();
        return [.. route];
    }

    /// <summary>The root node an action chain descends from (the first <c>nodes</c> entries are roots).</summary>
    private int RootIndex(int node)
    {
        int cursor = node;
        while (_nodes.View(cursor).Parent >= 0)
            cursor = _nodes.View(cursor).Parent;
        return cursor;
    }

    /// <summary>
    /// The existing TightenSearchIncumbentAtTurnLayer, widened: any victory the final ordering could
    /// select sets the bound, potion routes included, valued by the route key's effective deficit (the
    /// strategic deficit plus the potions priced in HP), which is what every route is ranked by.
    /// </summary>
    private void TightenIncumbent(in Battle state, in ScoreFeatures features, in RouteKey key,
        bool admissible)
    {
        if (_effectivePotionPolicy is not (PotionUseMode.Disabled or PotionUseMode.Smart))
            return;
        if (_bound == null || !key.Won || features.ProjectedDeathSaveUseCount > 0)
            return;
        SearchIncumbent? candidate = _incumbent;
        if (IncumbentPolicy.TryTightenIncumbent(
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: !_bound.HasRisk(state),
                candidatePolicyAdmissible: admissible,
                candidateEffectiveHpDeficit: key.EffectiveHpDeficit,
                candidateCombatEndedTurn: state.PlayerTurn,
                ref candidate,
                effectivePotionPolicy: _effectivePotionPolicy,
                candidateDeathSaveUseCount: features.ProjectedDeathSaveUseCount,
                candidateGoalHpCredit: features.StrategyGoalHpCredit,
                candidateGoalCount: features.StrategyGoalCount,
                candidateGoalsComplete: _scorer.RelicGoalsMet(features)))
        {
            _incumbent = candidate;
        }
    }

    /// <summary>
    /// A lower bound on the state's final effective deficit: the strategic HP bound less every relic
    /// allowance it could still earn, plus the HP price of the optional potions it already drank when
    /// that price can only grow.
    /// </summary>
    private int EffectiveLowerBound(int strategicHpLowerBound, in Battle state)
    {
        long bound = (long)strategicHpLowerBound - _relicCreditCeiling;
        if (_potionCostMonotone)
        {
            bound += PotionHpCost(OptionalPotionPrice(_scorer.MeasurePotions(state)));
        }
        return (int)Math.Clamp(bound, int.MinValue, int.MaxValue);
    }

    /// <summary>The strategic price of the optional potions a state has drunk (see <see cref="PotionUsePolicy.OptionalPotionPrice"/>).</summary>
    private static int OptionalPotionPrice(in PotionUsage usage)
        => PotionUsePolicy.OptionalPotionPrice(usage.ExplicitStrategicCost, usage.DearestExplicitStrategicCost);

    /// <summary>The HP the Smart policy requires a potion price to save: its weight in the route key.</summary>
    private int PotionHpCost(int optionalPotionPrice)
        => optionalPotionPrice > 0
            ? PotionUsePolicy.SmartRequiredHpSaved(optionalPotionPrice, _ordering.BossHpRelief)
            : 0;

    private static bool RootPotionCostsKnown(ScoreRules rules)
    {
        for (int slot = 0; slot < rules.RootPotionSlotCount && slot < Limits.PotionSlots; slot++)
        {
            ushort potion = rules.RootPotions[slot];
            if (potion != 0 && (potion > rules.PotionStrategicCost.Length
                    || rules.PotionStrategicCost[potion - 1] < 0))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The existing <c>ApplyPrimaryIncumbentBound</c> local path. It runs after the ranked retention
    /// (action layer and turn boundary) and compacts the pool; removed seats are deliberately not refilled.
    /// </summary>
    private void PrunePool(Pool pool)
    {
        SearchIncumbent? incumbent = _bound == null ? null : _incumbent;
        if (incumbent is null || _bound is null)
            return;
        SearchIncumbent bounded = incumbent.Value;
        // A state can still earn every relic allowance, so its bound gives that credit back. Until the
        // incumbent meets every relic goal, an equal-HP state may beat it on the goals and is kept.
        bool goalTiesSettled = !_hasRelicTargets || bounded.GoalsComplete;
        int write = 0;
        for (int read = 0; read < pool.Count; read++)
        {
            BoundFeatures.PruningBound values = _bound.EvaluatePruning(
                pool.State(read), _scorer.RulesForCodec.Rules);
            bool certifiedUnknown = _bound.RequireCertifiedHealingProof && _pruneEqualHp
                && values.RemainingUnknown;
            bool incumbentPrune = false;
            if (!certifiedUnknown)
            {
                incumbentPrune = IncumbentPolicy.ShouldPruneByIncumbent(
                    EffectiveLowerBound(values.StrategicHpLowerBound, pool.State(read)),
                    pool.State(read).PlayerTurn,
                    bounded,
                    allowTurnTieBound: goalTiesSettled,
                    pruneEqualHp: goalTiesSettled && _pruneEqualHp && !values.HasRisk);
            }
            if (!incumbentPrune)
            {
                if (write != read)
                    pool.Move(read, write);
                write++;
                continue;
            }
            _pruned++;
            if (_trace != null)
            {
                int node = pool.Node(read);
                if (_trace.IsWatched(node))
                    _trace.NodePruned(node, pool.TracePhase, "incumbent-lower-bound");
            }
        }
        pool.Truncate(write);
    }

    /// <summary>
    /// The node's final score and its intermediate ranking value. The final score is the ported
    /// <see cref="Scorer.Features"/> score; the ranking value is
    /// <see cref="Scorer.BeamRankScore"/> minus the strategic HP cost of the potions this route has
    /// spent. The potion term is Sts2Solver-specific: the existing snapshot score does not include potion cost,
    /// so without it the beam would spend potions for free.
    /// </summary>
    private (double Score, double Rank, double PureRank, int PotionCost, ScoreFeatures Features) Score(
        in Battle battle, int actionCount, bool terminal, bool isEndTurn)
    {
        // ReachableHandValue / ZeroCostPlayableCount feed only the retention comparators, not Score,
        // BeamRankScore or RouteKey. The retention pool computes them when a state is stored, which
        // skips duplicate and terminal children.
        ScoreFeatures features = _scorer.Features(battle, actionCount, includeRetentionOnly: false);
        // The plan representative seats keep the existing MaxBy(BeamRankScore) tie order, which does
        // not include Sts2Solver's potion-cost rank penalty. The two values differ only by that term.
        double pureRank = _scorer.BeamRankScore(battle, features, terminal, isEndTurn);
        // ExplicitPotionStrategicCost includes the fixed prefix and potions generated during the
        // search; the previous root-slot diff saw neither generated uses nor their identity costs.
        // An audited member is filtered and ranked by the final ordering's potion policy, so the
        // Sts2Solver-specific "spending is free" correction would rank a potion route below states
        // that have not spent the potion yet; the final ordering still charges the cost. Non-audit
        // members keep the correction.
        double rank = pureRank - features.ExplicitPotionStrategicCost * ScoreWeights.Hp;
        return (features.Score, rank, pureRank, features.ExplicitPotionStrategicCost, features);
    }

    private static bool CommitmentHasCard(in PowerCommitmentState state, int bit)
    {
        if (bit is < 0 or >= 128)
            return false;
        return bit < 64
            ? (state.CardsLow & (1UL << bit)) != 0
            : (state.CardsHigh & (1UL << (bit - 64))) != 0;
    }

    /// <summary>
    /// The slot holds a searchable potion the request's strategy/policy offers now. The existing
    /// <c>EnumeratePlannedPotionActions</c> reads the potion currently in the slot, so a potion generated
    /// during the search is offered by its own identity rather than by the root potion's policy.
    /// A root potion of an opening-only kind (<see cref="PotionUsePolicy.OpeningOnlyPotionIds"/>) still in
    /// its root slot is offered only on the search's first turn: drinking it later is never better.
    /// </summary>
    private bool PotionAllowed(in Battle battle, int slot)
    {
        PotionPolicy policy = _options.Potions;
        if ((uint)slot >= (uint)battle.PotionSlotCount || battle.PlayerTurn < policy.EarliestPlayerTurn)
            return false;
        ushort stored = battle.Potions[slot];
        if (stored == 0 || !policy.AllowsExplicitUse(slot, (ushort)(stored - 1)))
            return false;
        return battle.PlayerTurn <= _openingPotionTurn || _openingOnlyRootPotion[slot] != stored;
    }

    /// <summary>
    /// A route node's power-commitment metadata: the ported <c>PowerCommitment</c> without its string
    /// card list. Registered cards are indexed by the power-commitment catalog, so the played set is an exact
    /// 128-bit mask (87 registered powers today). This is route history and never enters
    /// <see cref="Battle"/>, the fingerprint or the state key.
    /// </summary>
    /// <summary>
    /// <c>the existing search's AttachPowerCommitment</c> over the fixed parent commitment snapshot.
    /// Returns null when the child carries no commitment; the commit half adds a non-null value to
    /// <see cref="_commitments"/> and records its 1-based index. A child whose parent has no
    /// commitment and that does not play a registered power returns immediately.
    /// </summary>
    private PowerCommitmentState? ComputePowerCommitment(bool hasParentCommitment,
        in PowerCommitmentState parentState, in Battle parent, in Battle child,
        in PlayerAction action, in RetentionFacts parentFacts, in ScoreFeatures childFeatures,
        int depth)
    {
        if (_powerCommitments is not { } catalog)
            return null;
        bool powerPlay = action.Kind == PlayerActionKind.PlayCard
            && action.HandIndex < parent.HandCount;
        PowerCommitmentRule rule = default;
        if (powerPlay)
            powerPlay = TryRegisteredPower(parent, parent.Cards[action.HandIndex], catalog, out rule);
        if (!powerPlay && !hasParentCommitment)
            return null;

        int progress = 0, realized = 0;
        RetentionFacts childFacts = default;
        PowerEvidence.View childView = new(child, childFeatures, _rules, _scorer);
        ushort playedCardId = powerPlay && action.HandIndex < parent.HandCount
            ? parent.Cards[action.HandIndex].Id
            : ushort.MaxValue;
        if (hasParentCommitment)
        {
            (int reachableHandValue, int zeroCostPlayableCount, _) = _scorer.ReachableHandPotential(child);
            childFacts = RetentionFacts.From(childFeatures, depth,
                reachableHandValue, zeroCostPlayableCount);
            PowerEvidence.View parentView = new(parent, default, _rules, _scorer);
            progress = PowerEvidence.SilentProgress(parentState, parentView, childView);
            int specialized = PowerEvidence.SilentRealized(parentState,
                parentFacts.PlayerBlock, parentFacts.ProjectedPlayerHp, parentFacts.ReachableHandValue,
                parentFacts.Turn, childFacts.PlayerBlock, childFacts.ProjectedPlayerHp,
                childFacts.ReachableHandValue, childFacts.Turn, childFacts.AliveEnemyCount,
                parent, child, playedCardId, action.Kind == PlayerActionKind.PlayCard,
                childView, out bool handled);
            realized = handled
                ? specialized
                : GenericPowerCommitmentRealized(parentFacts, childFacts);
        }

        bool attached = false;
        PowerCommitmentState commitment = parentState;
        if (powerPlay)
        {
            int spentEnergy = Math.Max(0, parentFacts.Energy - childFeatures.Energy);
            int turnOffset = Math.Max(0, childFeatures.Turn - _initialPlayerTurn);
            int investment = PowerActivationInvestmentPolicy.EnergyInvestment(
                spentEnergy, catalog.TotalFloor, turnOffset);
            investment = SaturatingCommitmentAdd(investment,
                Math.Max(0, childFeatures.CumulativePlayerHpLost - parentFacts.CumulativePlayerHpLost));
            int setupGain = PowerCommitmentSetupGain(parentFacts, childFeatures);
            int projectedPotential = PowerEvidence.ProjectionPotential(
                playedCardId, childView, parent, parentFacts.ProjectedPlayerHp);
            int triggerFloor = PowerEvidence.TriggerProjectionFloor(playedCardId, childView);
            PowerRouteAdmissionResult admission = PowerRouteAdmission.Evaluate(new(
                CardId: Registry.Card(playedCardId).NativeId,
                IsAutoPlay: false,
                SpentEnergy: spentEnergy,
                RemainingEnergy: childFeatures.Energy,
                HasTriggerEvidence: PowerEvidence.HasTriggerEvidence(playedCardId, parent, childView),
                ImmediateDefenseGain: Math.Max(0,
                    childFeatures.ProjectedPlayerHp - parentFacts.ProjectedPlayerHp),
                SetupGain: setupGain,
                ProjectedPotential: projectedPotential,
                TriggerProjectionFloor: triggerFloor,
                Investment: investment), rule.Admission);
            if (admission.Admitted)
            {
                int cardBit = rule.Index;
                if (!hasParentCommitment)
                {
                    commitment = new(rule.Family, rule.Priority,
                        cardBit < 64 ? 1UL << cardBit : 0UL,
                        cardBit >= 64 ? 1UL << (cardBit - 64) : 0UL,
                        0, childFeatures.Turn, investment, admission.Potential, 0, 0, depth);
                }
                else
                {
                    commitment = AddPowerCommitment(parentState, rule, cardBit,
                        investment, admission.Potential, progress, realized, childFeatures.Turn);
                }
                attached = true;
            }
        }

        if (!attached)
        {
            if (!hasParentCommitment)
                return null;
            PowerCommitmentState? advanced = AdvancePowerCommitment(parentState,
                parent.PlayerTurn, child.PlayerTurn,
                _aggressivePowerCommitment ? 3 : 2, progress, realized);
            if (advanced is not { } next)
                return null;
            commitment = next;
        }
        return commitment;
    }

    private bool TryRegisteredPower(in Battle battle, in Card card,
        PowerCommitmentCatalog catalog, out PowerCommitmentRule rule)
    {
        rule = default;
        return card.Id < catalog.Rules.Length
            && (rule = catalog.Rules[card.Id]).Family != PowerCommitmentFamily.None
            && Kernel.ResolveCardType(battle, _rules, card) == CardType.Power;
    }

    private static PowerCommitmentState AddPowerCommitment(
        in PowerCommitmentState commitment,
        in PowerCommitmentRule rule,
        int cardBit,
        int investment,
        int potential,
        int progressEvidence,
        int realizedEvidence,
        int turn)
    {
        int unrealizedPriorPotential = Math.Max(0,
            commitment.ProvisionalPotential - realizedEvidence);
        ulong cardsLow = commitment.CardsLow;
        ulong cardsHigh = commitment.CardsHigh;
        if (cardBit < 64)
            cardsLow |= 1UL << cardBit;
        else
            cardsHigh |= 1UL << (cardBit - 64);
        return commitment with
        {
            Family = commitment.Family | rule.Family,
            Priority = rule.Priority > commitment.Priority ? rule.Priority : commitment.Priority,
            CardsLow = cardsLow,
            CardsHigh = cardsHigh,
            Investment = SaturatingCommitmentAdd(commitment.Investment, investment),
            ProvisionalPotential = SaturatingCommitmentAdd(unrealizedPriorPotential, potential),
            ProgressEvidence = SaturatingCommitmentAdd(commitment.ProgressEvidence, progressEvidence),
            RealizedEvidence = SaturatingCommitmentAdd(commitment.RealizedEvidence, realizedEvidence),
            LastEvidenceTurn = progressEvidence > 0 || realizedEvidence > 0
                ? turn
                : commitment.LastEvidenceTurn,
        };
    }

    /// <summary><c>PowerCommitmentLifecycle.Advance</c>; null means expired or realized.</summary>
    private static PowerCommitmentState? AdvancePowerCommitment(
        in PowerCommitmentState commitment,
        int parentTurn,
        int childTurn,
        int maximumTransitions,
        int progressEvidence,
        int realizedEvidence)
    {
        int roundTransitions = commitment.RoundTransitions
            + Math.Max(0, childTurn - parentTurn);
        bool hasEvidence = progressEvidence > 0 || realizedEvidence > 0;
        int lastEvidenceTurn = hasEvidence ? childTurn : commitment.LastEvidenceTurn;
        if (roundTransitions > maximumTransitions
            || roundTransitions > 0 && childTurn - lastEvidenceTurn > 1)
        {
            return null;
        }
        int remainingPotential = Math.Max(0,
            commitment.ProvisionalPotential - realizedEvidence);
        if (remainingPotential == 0 && realizedEvidence > 0)
            return null;
        return commitment with
        {
            RoundTransitions = roundTransitions,
            LastEvidenceTurn = lastEvidenceTurn,
            ProgressEvidence = SaturatingCommitmentAdd(commitment.ProgressEvidence, progressEvidence),
            RealizedEvidence = SaturatingCommitmentAdd(commitment.RealizedEvidence, realizedEvidence),
            ProvisionalPotential = remainingPotential,
        };
    }

    /// <summary><c>PowerCommitmentSetupGain</c> over the K4-checked feature deltas.</summary>
    private static int PowerCommitmentSetupGain(in RetentionFacts before, in ScoreFeatures after)
    {
        long gain = Math.Max(0, after.PersistentBuffValue - before.PersistentBuffValue);
        gain += Math.Max(0, after.StrategicRetentionValue - before.StrategicRetentionValue);
        gain += Math.Max(0, after.FutureResourceValue - before.FutureResourceValue);
        gain += Math.Max(0, after.LatentSetupValue - before.LatentSetupValue);
        return (int)Math.Min(int.MaxValue, gain);
    }

    /// <summary><c>PowerCommitmentProgressEvidence</c> generic fallback (no specialized card table).</summary>
    private static int GenericPowerCommitmentProgress(in RetentionFacts before, in RetentionFacts after)
        => (int)Math.Min(int.MaxValue,
            (long)Math.Max(0, after.OffensiveProgressValue - before.OffensiveProgressValue)
            + Math.Max(0, after.ReachableHandValue - before.ReachableHandValue)
            + Math.Max(0, after.ZeroCostPlayableCount - before.ZeroCostPlayableCount) * 4L
            + Math.Max(0, after.ProjectedPlayerHp - before.ProjectedPlayerHp));

    /// <summary><c>GenericPowerCommitmentEvidence</c>, also used as the realized signal.</summary>
    private static int GenericPowerCommitmentRealized(in RetentionFacts before, in RetentionFacts after)
        => GenericPowerCommitmentProgress(before, after);

    private static int SaturatingCommitmentAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);

    /// <summary>
    /// The evaluation values the ported retention comparators read. Every member is already frozen by
    /// <see cref="Scorer.Features"/> and checked field by field against the existing snapshot by K4,
    /// so retention does not re-evaluate a state or introduce a second scoring path.
    /// </summary>
    private readonly record struct RetentionFacts(
        int Depth,
        int PotionCount,
        int ProjectedPlayerHp,
        int PlayerBlock,
        int OstyHp,
        int OstyMaxHp,
        int DefensiveBlockValue,
        int AliveEnemyCount,
        int RawEnemyHp,
        int EnemyBlock,
        int EnemyHp,
        int MaxCurrentEnemyHp,
        int RevivingEnemyCount,
        int RetainedAttackValue,
        int StrategicRetentionValue,
        int SetupValue,
        int SetupComboValue,
        int LiveDeckSize,
        int LiveDeckClutter,
        long AttackDensity,
        int EnemyStrengthSuppression,
        int EnemyWeakTurns,
        int FocusTargetPressure,
        int FocusTargetRemainingHp,
        int FocusTargetCurrentThreat,
        uint? FocusTargetCombatId,
        uint? MostVulnerableTargetCombatId,
        int FocusTargetVulnerableTurns,
        int EnemyVulnerableTurns,
        int SandpitRemaining,
        int ReactiveDamageValue,
        int LongTermResourceValue,
        int Energy,
        int Stars,
        int HandCount,
        int ReachableHandValue,
        int ZeroCostPlayableCount,
        int FutureResourceValue,
        int LatentSetupValue,
        int ReplayPotentialValue,
        int DelayedDamageValue,
        PersistentSetupTraits SetupTraits,
        PersistentSetupTraits StrategySetupTraits,
        int PersistentBuffValue,
        int OffensiveProgressValue,
        int OutstandingStolenResource,
        int Turn,
        int CumulativePlayerHpLost,
        int PotionStrategicCost,
        double Score,
        int PlayerHp,
        int PlayerMaxHp,
        ulong AliveEnemyMask,
        bool AllEnemiesDead,
        bool PlayerDead,
        int PocketwatchCardThreshold,
        int PocketwatchCardsPlayedThisTurn,
        int PocketwatchCardsPlayedLastTurn,
        SearchRouteTraits Traits,
        int StrategicHpCredit,
        int StrategyGoalCount,
        int AngerCopiesGenerated,
        int RetainedAttackGrowth,
        int FutureSoldHp,
        int PlayableAttackCount,
        int StrategicResourcePotential,
        int ParentEnemyBlock,
        int ParentEnemyHp,
        int ParentFutureResourceValue,
        int ParentStrategicResourcePotential,
        int ProjectedShuffleOrderValue,
        ulong ProjectedShuffleOrderKeyFirst,
        ulong ProjectedShuffleOrderKeySecond,
        bool CanTriggerArtOfWarNextTurn,
        ulong EnemyCombatDistributionFirst,
        ulong EnemyCombatDistributionSecond,
        ulong EnemyControlDistributionFirst,
        ulong EnemyControlDistributionSecond,
        ulong UnorderedPileKeyFirst,
        ulong UnorderedPileKeySecond)
    {
        /// <summary>Re-projects the retention-only values of an already stored byte-identical state.</summary>
        public Scorer.RetentionValues RetentionProjection()
            => new(ProjectedShuffleOrderValue, ProjectedShuffleOrderKeyFirst, ProjectedShuffleOrderKeySecond,
                CanTriggerArtOfWarNextTurn, EnemyCombatDistributionFirst, EnemyCombatDistributionSecond,
                EnemyControlDistributionFirst, EnemyControlDistributionSecond, UnorderedPileKeyFirst,
                UnorderedPileKeySecond);

        public static RetentionFacts From(in ScoreFeatures features, int depth,
            int reachableHandValue, int zeroCostPlayableCount, SearchRouteTraits traits = SearchRouteTraits.None,
            int pocketwatchCardThreshold = -1, int pocketwatchCardsPlayedThisTurn = 0,
            int pocketwatchCardsPlayedLastTurn = 0,
            int initialRetainedAttackValue = 0, int parentEnemyBlock = 0, int parentEnemyHp = 0,
            int parentFutureResourceValue = 0, int parentStrategicResourcePotential = 0,
            in Scorer.RetentionValues retention = default) => new(
            depth,
            features.PotionUseCount,
            features.ProjectedPlayerHp,
            features.PlayerBlock,
            features.OstyHp,
            features.OstyMaxHp,
            features.DefensiveBlockValue,
            features.AliveEnemyCount,
            features.RawEnemyHp,
            features.EnemyBlock,
            features.EnemyHp,
            features.MaxCurrentEnemyHp,
            features.RevivingEnemyCount,
            features.RetainedAttackValue,
            features.StrategicRetentionValue,
            SetupLane(features),
            SetupCombo(features, reachableHandValue),
            features.LiveDeckSize,
            features.LiveDeckClutter,
            (long)features.RetainedAttackValue * 1024 / Math.Max(1, features.LiveDeckSize),
            features.EnemyStrengthSuppression,
            features.EnemyWeakTurns,
            features.FocusTargetPressure,
            features.FocusTargetRemainingHp,
            features.FocusTargetCurrentThreat,
            features.FocusTargetCombatId,
            features.MostVulnerableTargetCombatId,
            features.FocusTargetVulnerableTurns,
            features.EnemyVulnerableTurns,
            features.SandpitRemaining,
            features.ReactiveDamageValue,
            features.LongTermResourceValue,
            features.Energy,
            features.Stars,
            features.HandCount,
            reachableHandValue,
            zeroCostPlayableCount,
            features.FutureResourceValue,
            features.LatentSetupValue,
            features.ReplayPotentialValue,
            features.DelayedDamageValue,
            features.PersistentSetupTraits,
            (PersistentSetupTraits)features.StrategicSetupTraits,
            features.PersistentBuffValue,
            features.OffensiveProgressValue,
            features.OutstandingStolenResource,
            features.Turn,
            features.CumulativePlayerHpLost,
            features.PotionStrategicCost,
            features.Score,
            features.PlayerHp,
            features.PlayerMaxHp,
            features.AliveEnemyMask,
            features.AllEnemiesDead,
            features.PlayerDead,
            pocketwatchCardThreshold,
            pocketwatchCardsPlayedThisTurn,
            pocketwatchCardsPlayedLastTurn,
            traits,
            features.StrategicHpCredit,
            features.StrategyGoalCount,
            features.AngerCopiesGenerated,
            Math.Min(ScoreWeights.RetainedAttackGrowthBeamCap,
                Math.Max(0, features.RetainedAttackValue - initialRetainedAttackValue)),
            0,
            features.PlayableAttackCount,
            features.StrategicResourcePotential,
            parentEnemyBlock,
            parentEnemyHp,
            parentFutureResourceValue,
            parentStrategicResourcePotential,
            retention.ProjectedShuffleOrderValue,
            retention.ProjectedShuffleOrderKeyFirst,
            retention.ProjectedShuffleOrderKeySecond,
            retention.CanTriggerArtOfWarNextTurn,
            retention.EnemyCombatDistributionFirst,
            retention.EnemyCombatDistributionSecond,
            retention.EnemyControlDistributionFirst,
            retention.EnemyControlDistributionSecond,
            retention.UnorderedPileKeyFirst,
            retention.UnorderedPileKeySecond);

        /// <summary>
        /// BeamRetentionPolicy.SetupLaneValue: StrategicEffects.RetentionValue * 16
        /// + LatentSetupValue * 8 + ReplayPotentialValue * 16 + FutureResourceValue.
        /// </summary>
        private static int SetupLane(in ScoreFeatures features)
            => features.StrategicRetentionValue * 16
                + features.LatentSetupValue * 8
                + features.ReplayPotentialValue * 16
                + features.FutureResourceValue;

        /// <summary>
        /// BeamRetentionPolicy.FindBestSetup: Scaling + Resource + Control lanes (LaneValue in
        /// BeamRetentionPolicy.Ranking.cs). Every term is the existing formula; the resource lane's
        /// Stars and ReachableHandValue and the control lane's SandpitRemaining are K4-checked.
        /// </summary>
        private static int SetupCombo(in ScoreFeatures features, int reachableHandValue)
            => features.StrategicRetentionValue * 16
                + features.LatentSetupValue * 8
                + features.ReplayPotentialValue * 16
                + features.FutureResourceValue
                + features.DelayedDamageValue
                + features.Energy * 16
                + features.Stars * 8
                + features.HandCount
                + reachableHandValue
                + features.FutureResourceValue
                + features.OstyHp * 16
                + features.OstyMaxHp * 4
                + features.SandpitRemaining * 32
                + features.EnemyStrengthSuppression * 32
                + features.EnemyWeakTurns * 8
                + features.FocusTargetVulnerableTurns * 4
                    * Math.Min(ScoreWeights.VulnerableAttackWindowCap, features.RetainedAttackValue)
                + Math.Max(0, features.EnemyVulnerableTurns - features.FocusTargetVulnerableTurns)
                    * Math.Min(ScoreWeights.VulnerableAttackWindowCap, features.RetainedAttackValue)
                + features.DelayedDamageValue
                - features.LiveDeckClutter * 8;
    }

    /// <summary>
    /// K4 entry: the Sts2Solver route-trait delta for one transition, computed from the same feature
    /// projection the probe compares field by field. The reference side calls the existing
    /// <c>Classify*Traits</c>; <c>pure</c> and the PaelsEye declined flag are inputs the probe derives
    /// from the simulator history/relics for both sides.
    /// </summary>
    internal static SearchRouteTraits ClassifyRouteTraitsForTesting(
        in ScoreFeatures before, in ScoreFeatures after, PlayerAction action,
        bool declinedExtraTurn, bool nonPure)
    {
        RetentionFacts facts = RetentionFacts.From(before, 0,
            before.ReachableHandValue, before.ZeroCostPlayableCount);
        return ClassifyRouteTraits(facts, after, action, declinedExtraTurn, nonPure);
    }

    /// <summary>
    /// K4 entry for the production <c>pure</c> input: the history entries of one play are pure exactly
    /// when the play only started/finished cards and dealt/received damage (the existing
    /// <c>SearchProbeHelpers.IsPureHistoryEntry</c> set). Every other history entry is produced by a
    /// draw, generation, selection, channel, affliction, cost randomisation, draw-pile auto-play or
    /// potion generation, so those transitions are detected from the branch state the Sts2Solver kernel
    /// changed. The probe checks this input against the history-derived reference on every transition.
    /// </summary>
    internal static bool IsPureTransitionForTesting(in Battle before, in Battle after,
        in PlayerAction action, Rules rules, bool historyRisk)
        => IsPureTransition(before, after, action, rules, historyRisk);

    private static bool IsPureTransition(in Battle before, in Battle after, in PlayerAction action,
        Rules rules, bool historyRisk)
    {
        if (action.Kind != PlayerActionKind.PlayCard)
            return false;
        if (historyRisk)
            return false;
        if (action.ChoiceCount > 0)
            return false;
        // A card whose OnPlay mirror dispatch is inferred/unsupported records a prediction risk entry
        // on every play; the existing history-derived pure input is false for it.
        if (action.HandIndex >= before.HandCount)
            return false;
        Card played = before.Cards[action.HandIndex];
        if (rules.Card(played.Id, played.Upgrade).PlaysRecordRisk)
            return false;
        if (after.Counter(Counter.CardsDrawn) != before.Counter(Counter.CardsDrawn))
            return false;
        // Any CardGenerated/CardGenerationResolved entry is non-pure, including a card generated by
        // an enemy (the combat counter only counts the player's). A completed play keeps the total
        // card count unchanged (it moves hand -> play -> discard/exhaust), so a larger total is a
        // generated card.
        if (after.CardCount > before.CardCount)
            return false;
        if (after.Counter(Counter.CardsGeneratedThisCombat)
            != before.Counter(Counter.CardsGeneratedThisCombat))
        {
            return false;
        }
        if (after.Counter(Counter.OrbChannels) != before.Counter(Counter.OrbChannels))
            return false;
        if (after.DrawCount < before.DrawCount)
            return false;

        for (int index = 0; index < after.PotionSlotCount; index++)
        {
            ushort potion = after.Potions[index];
            if (potion != 0 && potion != before.Potions[index])
                return false;
        }

        // Cards move between piles within one play (hand -> play -> discard), so afflictions are
        // compared as a multiset of (id, amount) over the whole card image, not by position.
        (ulong beforeFirst, ulong beforeSecond) = AfflictionMultiset(before);
        (ulong afterFirst, ulong afterSecond) = AfflictionMultiset(after);
        return beforeFirst == afterFirst && beforeSecond == afterSecond;
    }

    private static (ulong First, ulong Second) AfflictionMultiset(in Battle battle)
    {
        ulong first = 0, second = 0;
        for (int index = 0; index < battle.CardCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            ulong value = ((ulong)(ushort)card.AfflictionId << 32)
                | (ushort)card.AfflictionAmount;
            first += StateFingerprintBuilder.MixFirst(value);
            second += StateFingerprintBuilder.MixSecond(value);
        }
        return (first, second);
    }

    /// <summary>
    /// <c>SearchProbeHelpers.ClassifyCardTraits</c>/<c>ClassifyPotionTraits</c>/<c>ClassifyRoundTransitionTraits</c>
    /// over the parent/child feature values. The cumulative route traits are node metadata: they never enter
    /// <see cref="Battle"/>, the fingerprint or the state key.
    /// <para>
    /// The existing <c>pure</c> input is derived from the simulator history entry types
    /// (<c>CardPlayStarted</c>/<c>CardPlayFinished</c>/<c>CreatureAttacked</c>/<c>DamageReceived</c>).
    /// Sts2Solver route metadata has no history entry stream, so a card play is classified as not pure. That
    /// only widens the <see cref="SearchRouteTraits.Control"/> candidate set; the other terms of the same
    /// classification are exact feature comparisons.
    /// </para>
    /// </summary>
    private static SearchRouteTraits ClassifyRouteTraits(in RetentionFacts before,
        in ScoreFeatures after, in PlayerAction action, bool declinedExtraTurn, bool nonPure)
    {
        SearchRouteTraits traits = before.Traits;
        if (action.Kind == PlayerActionKind.PlayCard)
        {
            if (declinedExtraTurn)
                traits |= SearchRouteTraits.DeclinedExtraTurn;
            if (after.PersistentBuffValue > before.PersistentBuffValue
                || after.DelayedDamageValue > before.DelayedDamageValue
                || after.ReplayPotentialValue > before.ReplayPotentialValue
                || after.LongTermResourceValue > before.LongTermResourceValue)
            {
                traits |= SearchRouteTraits.Scaling;
            }
            if (after.LongTermResourceValue > before.LongTermResourceValue)
                traits |= SearchRouteTraits.LongTermResource;
            if (after.PlayerHp < before.PlayerHp
                || after.PlayerMaxHp < before.PlayerMaxHp
                || after.CumulativePlayerHpLost > before.CumulativePlayerHpLost)
            {
                traits |= SearchRouteTraits.HpInvestment;
            }
            if (after.ReactiveDamageValue > before.ReactiveDamageValue)
                traits |= SearchRouteTraits.ReactiveDamage;
            if (after.Energy > before.Energy
                || after.Stars > before.Stars
                || after.HandCount >= before.HandCount
                || after.ReachableHandValue > before.ReachableHandValue
                || after.ZeroCostPlayableCount > before.ZeroCostPlayableCount
                || after.FutureResourceValue > before.FutureResourceValue)
            {
                traits |= SearchRouteTraits.Resource;
            }
            if (nonPure
                || after.SandpitRemaining > before.SandpitRemaining
                || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
                || after.EnemyWeakTurns > before.EnemyWeakTurns
                || after.EnemyVulnerableTurns > before.EnemyVulnerableTurns
                || after.LiveDeckClutter < before.LiveDeckClutter
                || after.DelayedDamageValue > before.DelayedDamageValue)
            {
                traits |= SearchRouteTraits.Control;
            }
            if (OpensRevivalWindow(before, after))
                traits |= SearchRouteTraits.RevivalWindow;
        }
        else if (action.Kind == PlayerActionKind.UsePotion)
        {
            if (after.PersistentBuffValue > before.PersistentBuffValue
                || after.DelayedDamageValue > before.DelayedDamageValue)
            {
                traits |= SearchRouteTraits.Scaling;
            }
            if (after.Energy > before.Energy
                || after.Stars > before.Stars
                || after.HandCount > before.HandCount
                || after.FutureResourceValue > before.FutureResourceValue)
            {
                traits |= SearchRouteTraits.Resource;
            }
            if (after.SandpitRemaining > before.SandpitRemaining
                || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
                || after.EnemyWeakTurns > before.EnemyWeakTurns
                || after.LiveDeckClutter < before.LiveDeckClutter
                || after.DelayedDamageValue > before.DelayedDamageValue
                || after.EnemyHp == before.EnemyHp && after.PlayerBlock == before.PlayerBlock)
            {
                traits |= SearchRouteTraits.Control;
            }
            if (before.Energy == 0
                && after.HandCount == 0
                && before.LiveDeckSize - after.LiveDeckSize >= 6
                && before.PocketwatchCardThreshold >= 0
                && before.PocketwatchCardsPlayedThisTurn == before.PocketwatchCardThreshold)
            {
                traits |= SearchRouteTraits.EndTurnDeckCompression;
            }
            if (OpensRevivalWindow(before, after))
                traits |= SearchRouteTraits.RevivalWindow;
        }
        else if (OpensRevivalWindow(before, after))
        {
            traits |= SearchRouteTraits.RevivalWindow;
        }
        return traits;
    }

    private static bool OpensRevivalWindow(in RetentionFacts before, in ScoreFeatures after)
        => after.RevivingEnemyCount > before.RevivingEnemyCount
            || after.RawEnemyHp < before.RawEnemyHp && after.EnemyHp >= before.EnemyHp
            || (before.AliveEnemyMask & ~after.AliveEnemyMask) != 0
                && (after.AliveEnemyMask & ~before.AliveEnemyMask) != 0;

    /// <summary><c>SimulatedCombatState.TryGetPocketwatchState</c>: the first non-melted relic's threshold
    /// and cards played this turn. A root without the relic reports -1/0, exactly like the snapshot.</summary>
    private void PocketwatchState(in Battle battle, out int threshold,
        out int cardsPlayedThisTurn, out int cardsPlayedLastTurn)
        => PocketwatchState(battle, _rules, out threshold, out cardsPlayedThisTurn, out cardsPlayedLastTurn);

    internal static void PocketwatchState(in Battle battle, Rules rules,
        out int threshold, out int cardsPlayedThisTurn)
        => PocketwatchState(battle, rules, out threshold, out cardsPlayedThisTurn, out _);

    /// <summary>
    /// <c>SimulatedCombatState.TryGetPocketwatchState</c>: the first non-melted relic's threshold,
    /// this turn's plays (Counter) and last turn's plays (Aux; the importer projects the engine's
    /// StatefulRelicState Previous into Aux).
    /// </summary>
    internal static void PocketwatchState(in Battle battle, Rules rules,
        out int threshold, out int cardsPlayedThisTurn, out int cardsPlayedLastTurn)
    {
        threshold = -1;
        cardsPlayedThisTurn = 0;
        cardsPlayedLastTurn = 0;
        ushort id = Registry.PocketwatchRelicId;
        if (id == 0)
            return;
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if (relic.Id != id || (relic.Flags & RelicFlags.Melted) != 0)
                continue;
            threshold = (int)rules.RelicVar(id, 0);
            cardsPlayedThisTurn = relic.Counter;
            cardsPlayedLastTurn = relic.Aux;
            return;
        }
    }

    /// <summary><c>SimulatedCombatState.IsPaelsEyeUnused</c>: the relic exists, is not melted and its
    /// stateful counter is still zero.</summary>
    internal static bool DeclinedExtraTurn(in Battle battle)
    {
        ushort id = Registry.PaelsEyeRelicId;
        if (id == 0)
            return false;
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if (relic.Id == id && (relic.Flags & RelicFlags.Melted) == 0 && relic.Counter == 0)
                return true;
        }
        return false;
    }

    // The leader comparators below are the existing BeamRetentionPolicy.Ranking comparators with the
    // same tie order. The Osty terms are imported from the pet state (IsBetterDefensive and the Resource
    // lane), matching the existing engine's leader comparators.
    private static bool IsBetterDefensive(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
            || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                && (candidate.OstyHp > current.OstyHp
                    || candidate.OstyHp == current.OstyHp
                        && (candidate.OstyMaxHp > current.OstyMaxHp
                            || candidate.OstyMaxHp == current.OstyMaxHp
                                && (candidate.DefensiveBlockValue > current.DefensiveBlockValue
                                    || candidate.DefensiveBlockValue == current.DefensiveBlockValue
                                        && candidate.Score > current.Score)));

    private static bool IsBetterOffensive(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.AliveEnemyCount < current.AliveEnemyCount
            || candidate.AliveEnemyCount == current.AliveEnemyCount
                && (candidate.RawEnemyHp < current.RawEnemyHp
                    || candidate.RawEnemyHp == current.RawEnemyHp
                        && (candidate.EnemyHp < current.EnemyHp
                            || candidate.EnemyHp == current.EnemyHp
                                && (candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
                                    || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                                        && candidate.Score > current.Score)));

    private static bool IsBetterSetup(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.SetupValue > current.SetupValue
            || candidate.SetupValue == current.SetupValue
                && (candidate.RetainedAttackValue > current.RetainedAttackValue
                    || candidate.RetainedAttackValue == current.RetainedAttackValue
                        && (candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
                            || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                                && candidate.Score > current.Score));

    private static bool IsBetterSetupCombo(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.SetupComboValue > current.SetupComboValue
            || candidate.SetupComboValue == current.SetupComboValue
                && (candidate.RetainedAttackValue > current.RetainedAttackValue
                    || candidate.RetainedAttackValue == current.RetainedAttackValue
                        && (candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
                            || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                                && candidate.Score > current.Score));

    private static bool IsBetterDeckCuration(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.AttackDensity > current.AttackDensity
            || candidate.AttackDensity == current.AttackDensity
                && (candidate.LiveDeckClutter < current.LiveDeckClutter
                    || candidate.LiveDeckClutter == current.LiveDeckClutter
                        && IsBetterSetup(candidate, current));

    private static bool IsBetterCompressedDeck(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.LiveDeckSize < current.LiveDeckSize
            || candidate.LiveDeckSize == current.LiveDeckSize
                && (candidate.AttackDensity > current.AttackDensity
                    || candidate.AttackDensity == current.AttackDensity
                        && IsBetterSetup(candidate, current));

    private static bool IsBetterUtilityDefensive(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
            || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                && candidate.Score > current.Score;

    private static bool IsBetterEnemyStrengthControl(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.EnemyStrengthSuppression > current.EnemyStrengthSuppression
            || candidate.EnemyStrengthSuppression == current.EnemyStrengthSuppression
                && (candidate.EnemyWeakTurns > current.EnemyWeakTurns
                    || candidate.EnemyWeakTurns == current.EnemyWeakTurns
                        && IsBetterDefensive(candidate, current));

    private static bool IsBetterEnemyWeakControl(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.EnemyWeakTurns > current.EnemyWeakTurns
            || candidate.EnemyWeakTurns == current.EnemyWeakTurns
                && (candidate.EnemyStrengthSuppression > current.EnemyStrengthSuppression
                    || candidate.EnemyStrengthSuppression == current.EnemyStrengthSuppression
                        && IsBetterDefensive(candidate, current));

    /// <summary>the existing search's BeamRetentionPolicy.IsBetterSearchNode: score, then fewer actions.</summary>
    private static bool IsBetterSearchNode(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.Score > current.Score
            || candidate.Score.Equals(current.Score) && candidate.Depth < current.Depth;

    /// <summary>FindBestTacticalEnabler: free card plays, reachable hand value, hand size.</summary>
    private static bool IsBetterTacticalEnabler(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.ZeroCostPlayableCount > current.ZeroCostPlayableCount
            || candidate.ZeroCostPlayableCount == current.ZeroCostPlayableCount
                && (candidate.ReachableHandValue > current.ReachableHandValue
                    || candidate.ReachableHandValue == current.ReachableHandValue
                        && (candidate.HandCount > current.HandCount
                            || candidate.HandCount == current.HandCount
                                && IsBetterSearchNode(candidate, current)));

    /// <summary>
    /// FindBestTurnBoundaryHand (BeamRetentionPolicy.Ranking.cs): projected HP, then Osty HP, hand
    /// size, reachable hand value and enemy HP at the turn boundary.
    /// </summary>
    private static bool IsBetterTurnBoundaryHand(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
            || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                && (candidate.OstyHp > current.OstyHp
                    || candidate.OstyHp == current.OstyHp
                        && (candidate.HandCount > current.HandCount
                            || candidate.HandCount == current.HandCount
                                && (candidate.ReachableHandValue > current.ReachableHandValue
                                    || candidate.ReachableHandValue == current.ReachableHandValue
                                        && (candidate.EnemyHp < current.EnemyHp
                                            || candidate.EnemyHp == current.EnemyHp
                                                && candidate.Score > current.Score))));

    /// <summary>
    /// FindBestCuratedTurnBoundaryHand (BeamRetentionPolicy.Ranking.cs): projected HP, Osty HP, the
    /// projected shuffle order value, reachable hand value, fewer hand cards, enemy HP, then score.
    /// </summary>
    private static bool IsBetterCuratedTurnBoundaryHand(in RetentionFacts candidate,
        in RetentionFacts current)
    {
        if (candidate.ProjectedPlayerHp != current.ProjectedPlayerHp)
            return candidate.ProjectedPlayerHp > current.ProjectedPlayerHp;
        if (candidate.OstyHp != current.OstyHp)
            return candidate.OstyHp > current.OstyHp;
        if (candidate.ProjectedShuffleOrderValue != current.ProjectedShuffleOrderValue)
            return candidate.ProjectedShuffleOrderValue > current.ProjectedShuffleOrderValue;
        if (candidate.ReachableHandValue != current.ReachableHandValue)
            return candidate.ReachableHandValue > current.ReachableHandValue;
        if (candidate.HandCount != current.HandCount)
            return candidate.HandCount < current.HandCount;
        if (candidate.EnemyHp != current.EnemyHp)
            return candidate.EnemyHp < current.EnemyHp;
        return candidate.Score > current.Score;
    }

    /// <summary>FindBestTargetPressure: focus pressure, remaining HP, threat, then safety.</summary>
    private static bool IsBetterTargetPressure(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.FocusTargetPressure > current.FocusTargetPressure
            || candidate.FocusTargetPressure == current.FocusTargetPressure
                && (candidate.FocusTargetRemainingHp < current.FocusTargetRemainingHp
                    || candidate.FocusTargetRemainingHp == current.FocusTargetRemainingHp
                        && (candidate.FocusTargetCurrentThreat > current.FocusTargetCurrentThreat
                            || candidate.FocusTargetCurrentThreat == current.FocusTargetCurrentThreat
                                && (candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
                                    || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                                        && candidate.Score > current.Score)));

    /// <summary>FindBestTargetSetup: setup lane, then focus pressure, then safety.</summary>
    private static bool IsBetterTargetSetup(in RetentionFacts candidate, in RetentionFacts current)
        => candidate.SetupValue > current.SetupValue
            || candidate.SetupValue == current.SetupValue
                && (candidate.RetainedAttackValue > current.RetainedAttackValue
                    || candidate.RetainedAttackValue == current.RetainedAttackValue
                        && (candidate.FocusTargetPressure > current.FocusTargetPressure
                            || candidate.FocusTargetPressure == current.FocusTargetPressure
                                && (candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
                                    || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                                        && candidate.Score > current.Score)));

    /// <summary>
    /// BeamRetentionPolicy.Ranking.LaneValue. The existing leader is restricted to nodes whose
    /// accumulated route traits contain the lane; Beam does not model route traits, so the
    /// comparator runs over the whole group. A state with the extreme lane value almost always
    /// carries the trait, which is the case the existing retention protects.
    /// </summary>
    private static int LaneValue(in RetentionFacts facts, SearchRouteTraits trait)
        => trait switch
        {
            SearchRouteTraits.Scaling => facts.SetupValue + facts.DelayedDamageValue,
            SearchRouteTraits.Resource => facts.Energy * 16
                + facts.Stars * 8
                + facts.HandCount
                + facts.ReachableHandValue
                + facts.FutureResourceValue
                + facts.OstyHp * 16
                + facts.OstyMaxHp * 4,
            SearchRouteTraits.LongTermResource => facts.LongTermResourceValue,
            SearchRouteTraits.Control => facts.SandpitRemaining * 32
                + facts.EnemyStrengthSuppression * 32
                + facts.EnemyWeakTurns * 8
                + facts.FocusTargetVulnerableTurns * 4
                    * Math.Min(ScoreWeights.VulnerableAttackWindowCap, facts.RetainedAttackValue)
                + Math.Max(0, facts.EnemyVulnerableTurns - facts.FocusTargetVulnerableTurns)
                    * Math.Min(ScoreWeights.VulnerableAttackWindowCap, facts.RetainedAttackValue)
                + facts.DelayedDamageValue
                - facts.LiveDeckClutter * 8,
            SearchRouteTraits.RevivalWindow => facts.RevivingEnemyCount * 1024
                - facts.RawEnemyHp * 4
                - facts.MaxCurrentEnemyHp * 8,
            SearchRouteTraits.DeclinedExtraTurn => 0,
            SearchRouteTraits.ReactiveDamage => facts.ReactiveDamageValue,
            SearchRouteTraits.EndTurnDeckCompression => facts.Energy * 64
                + facts.FutureResourceValue * 16
                + (int)Math.Min(int.MaxValue, facts.AttackDensity)
                + facts.FocusTargetPressure
                - facts.LiveDeckSize * 16,
            SearchRouteTraits.HpInvestment => facts.StrategicRetentionValue * 16
                + facts.FutureResourceValue * 8
                + facts.DelayedDamageValue * 8
                + facts.FocusTargetPressure,
            _ => throw new ArgumentOutOfRangeException(nameof(trait), trait, null),
        };

    private static bool IsBetterLane(in RetentionFacts candidate, in RetentionFacts current,
        SearchRouteTraits trait)
    {
        int candidateValue = LaneValue(candidate, trait);
        int currentValue = LaneValue(current, trait);
        return candidateValue > currentValue
            || candidateValue == currentValue
                && (candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
                    || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                        && (candidate.AliveEnemyCount < current.AliveEnemyCount
                            || candidate.AliveEnemyCount == current.AliveEnemyCount
                                && (candidate.EnemyHp < current.EnemyHp
                                    || candidate.EnemyHp == current.EnemyHp
                                        && candidate.Score > current.Score)));
    }

    private static readonly SearchRouteTraits[] RetainedLanes =
    [
        SearchRouteTraits.Scaling,
        SearchRouteTraits.Resource,
        SearchRouteTraits.Control,
        SearchRouteTraits.RevivalWindow,
        SearchRouteTraits.ReactiveDamage,
        SearchRouteTraits.EndTurnDeckCompression,
        SearchRouteTraits.LongTermResource,
        SearchRouteTraits.HpInvestment,
    ];

    private sealed class LineageLeaders
    {
        public int Leader = -1;
        public int Defensive = -1;
        public int Offensive = -1;
        public int Setup = -1;
        public int SetupCombo = -1;
        public int Curation = -1;
        public int Compressed = -1;
        public int Tactical = -1;
        public int TargetPressure = -1;
    }

    private sealed class PotionLeaders
    {
        public int Leader = -1;
        public int Defensive = -1;
        public int Offensive = -1;
        public int Setup = -1;
        public int SetupCombo = -1;
        public int Curation = -1;
        public int Compressed = -1;
        public int Strength = -1;
        public int Weak = -1;
        public int Tactical = -1;
        public int TurnBoundary = -1;
        public int Resource = -1;
        public readonly int[] Lanes = new int[8];

        public PotionLeaders()
        {
            Array.Fill(Lanes, -1);
        }
    }

    private sealed class TraitLeaders
    {
        public int Defensive = -1;
        public int Setup = -1;
        public int Position = int.MaxValue;
        public double MaxRank = double.NegativeInfinity;
    }

    private sealed class FocusLeaders
    {
        public int Pressure = -1;
        public int Setup = -1;
        public int MaxPressure = int.MinValue;
        public int Position = int.MaxValue;
    }

    /// <summary>
    /// A fixed-size chunked array. Chunks are allocated on first write and never copied, so growing a
    /// pool has no reallocation. Every chunk is made at least 85,000 bytes (except the state chunks,
    /// which are deliberately larger; see <see cref="ComputeShift"/>) so it lands in the LOH instead of
    /// being promoted repeatedly through Gen0/Gen1.
    /// </summary>
    private sealed class ChunkedArray<T>
    {
        private static readonly int Shift = ComputeShift();
        public static int ChunkSize => 1 << Shift;
        public static int ChunkMask => ChunkSize - 1;

        private readonly T[]?[] _chunks;
        private readonly SearchStorage? _storage;

        public ChunkedArray(int maxCapacity, SearchStorage? storage = null)
        {
            _storage = storage;
            int chunks = checked((int)(((long)maxCapacity + ChunkSize - 1) >> Shift));
            _chunks = storage?.RentArray<T[]?>(chunks) ?? new T[]?[chunks];
        }

        public ref T this[int index]
        {
            get
            {
                int chunkIndex = index >> Shift;
                T[] chunk = _chunks[chunkIndex] ??= CreateChunk();
                return ref chunk[index & ChunkMask];
            }
        }

        private T[] CreateChunk()
            => _storage?.RentChunk<T>(ChunkSize) ?? new T[ChunkSize];

        private static int ComputeShift()
        {
            int size = Unsafe.SizeOf<T>();
            int shift = 0;
            while ((1 << shift) * size < 85_000)
                shift++;
            return shift;
        }
    }

}
