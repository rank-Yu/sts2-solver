// Main-thread controller for the combat panel. It ports the interaction state machine of
// CombatSolver src/Runtime/SolverController.cs and SolverControllerSessions.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). Search execution, action
// deployment and reconciliation stay in Sts2Solver's SearchSession, ActionDeployer and
// BattleReconciler; this type only owns the session flags, commands and events.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    private const bool StopFullAutoOnCombatEnd = false;
    private const bool StopFullAutoOnDeathTurn = true;
    private const bool StopFullAutoOnWorseRecalculation = true;

    private static readonly List<CombatPilotSearchEntry> SearchLog = [];
    private static readonly List<CombatPilotDeviationEntry> DeviationLog = [];
    private static readonly List<CombatPilotFailureEntry> FailureLog = [];
    private static readonly List<string> PotionsUsed = [];

    private static CombatState? _state;
    private static Player? _player;
    private static Rules? _rules;
    private static LiveCombatTracker? _tracker;
    private static CombatDisplayNames? _names;
    private static CancellationTokenSource _combatCancellation = new();
    private static CancellationTokenSource? _turnSchedule;
    private static CombatSearchSession? _search;
    private static CombatDeploymentSession? _deployment;
    private static CombatControllerResult? _latestResult;
    private static CombatControllerResult? _deploymentResult;
    private static CombatControllerResult? _stoppedResult;
    private static CombatControllerResult? _continuationSource;
    private static CombatStateStamp? _latestStamp;
    private static CompleteProjectionBaseline? _pendingCompleteBaseline;
    private static SolverMode _mode = SolverMode.AutoSearch;
    private static bool _solverDisabled;
    private static bool _fullAutoEnabled;
    private static bool _automaticSearchPaused;
    private static int? _automaticSearchPausedTurn;
    private static int? _lastSolverDeployedTurn;
    private static string? _pauseCode;
    private static string? _pauseReason;
    private static int? _pausePlannedHpLost;
    private static int? _pauseLiveHpLost;
    private static bool _pausePlayerDead;
    private static string? _errorCode;
    private static string? _errorDetail;
    private static string? _importErrorCode;
    private static string? _importErrorDetail;
    private static string? _rejectCode;
    private static CombatUiPhase _phase = CombatUiPhase.Idle;
    private static bool _combatActive;
    private static bool _trackerSeeded;
    private static int _combatGeneration;
    private static int _nextSearchGeneration;
    private static int _currentActionIndex;
    private static int _completedActions;
    private static int _searches;
    private static int _reusedTurns;
    private static int _deviations;
    private static int _deploymentFailures;
    private static int _consecutiveFailures;
    private static int? _consecutiveFailureTurn;
    private static long _combatStartedAt;
    private static long _searchStartedAt;
    private static int _searchThreads = 1;
    private static SearchPerformanceTier _searchTier = SearchPerformanceTier.Medium;
    private static SearchPerformanceProfile _searchProfile = SearchPerformanceProfile.For(SearchPerformanceTier.Medium);
    private static SearchMemberRunUi[] _memberRuns = [];
    private static SearchUiSnapshot? _finishedSearch;
    private static SearchProgressSnapshot? _lastProgress;
    private static int _lastCandidateVersion;
    private static int _activeMemberRun = -1;
    private static int _activeMemberWidth;
    private static long _activeMemberBudget;
    private static long _activeMemberStartedAt;
    private static long _lastProgressPublishAt;
    private static string? _testRoot;
    private static CombatPilotResult? _lastCompletedForTesting;

#if STS2_SOLVER_TESTING
    internal static Func<SearchPolicy, Player, SearchPolicy>? SearchPolicyOverrideForTest { get; set; }
#endif

    private sealed record CompleteProjectionBaseline(
        int StartTurnNumber,
        int ProjectedBattleHpLost,
        string StateDifference);

    internal static bool HasActiveSearch => _search != null;

    internal static bool IsActiveForTest => _combatActive && _state != null;

    internal static int SearchCountForTest => _searches;

    internal static bool HasActiveDeployment => _deployment != null;

    internal static bool FullAutoEnabled => _fullAutoEnabled;

    internal static bool AutomaticSearchPaused => _automaticSearchPaused;

    internal static bool IsPaused => _phase == CombatUiPhase.Paused;

    internal static bool IsBusy => _search != null || _deployment != null;

    internal static CombatPilotResult CapturePilotResultForTest() => BuildPilotResult();

    internal static SolverMode Mode => _mode;

    internal static CombatPilotResult? LastCompletedResultForTesting
        => Volatile.Read(ref _lastCompletedForTesting);

    internal static bool IsMultiplayerSession
#if STS2_SOLVER_TESTING
        => ForceMultiplayerForTest
            || RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type.IsMultiplayer();
#else
        => RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type.IsMultiplayer();
#endif

#if STS2_SOLVER_TESTING
    internal static bool ForceMultiplayerForTest { get; set; }

    internal static bool HoldAutoSearchForTest { get; set; }

    internal static void ResumeAutoSearchForTest()
    {
        HoldAutoSearchForTest = false;
        if (_combatActive && _state != null)
            OnTurnStarted(_state);
    }
#endif

    private static bool ShouldAutomaticallySearchNextTurn
        => _fullAutoEnabled || _mode is SolverMode.AutoSearch or SolverMode.FullAuto;

    internal static void Initialize()
    {
        SolverSettingsData settings = SolverSettings.Current;
        _mode = settings.Automation.Mode;
        _solverDisabled = settings.Automation.SolverDisabled;
        SearchSession.ConfigureProgress(true, 200);
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_PROGRESS_CONFIG subscribed={SearchSession.ProgressSubscribed} " +
            $"mode={_mode}");
    }

    internal static void SetTestRoot(string root)
    {
        _testRoot = root;
    }

    internal static void BeginCombat(CombatState? state)
    {
        RequireMainThread();
        CancellationTokenSource previousCombat = _combatCancellation;
        CancelTurnSchedule();
        CancelSearch();
        CancelDeployment();
        previousCombat.Cancel();
        previousCombat.Dispose();
        _combatCancellation = new CancellationTokenSource();
        _state = state;
        _player = state == null ? null : LocalContext.GetMe(state);
        _rules = null;
        _tracker = null;
        _names = null;
        _search = null;
        _deployment = null;
        _latestResult = null;
        _deploymentResult = null;
        _stoppedResult = null;
        _continuationSource = null;
        _latestStamp = null;
        _pendingCompleteBaseline = null;
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        _lastSolverDeployedTurn = null;
        _pauseCode = null;
        _pauseReason = null;
        _pausePlannedHpLost = null;
        _pauseLiveHpLost = null;
        _pausePlayerDead = false;
        _errorCode = null;
        _errorDetail = null;
        _importErrorCode = null;
        _importErrorDetail = null;
        _rejectCode = null;
        _finishedSearch = null;
        _lastProgress = null;
        _lastProgressPublishAt = 0;
        _lastCandidateVersion = 0;
        _currentActionIndex = 0;
        _completedActions = 0;
        _searches = 0;
        _reusedTurns = 0;
        _deviations = 0;
        _deploymentFailures = 0;
        _consecutiveFailures = 0;
        _consecutiveFailureTurn = null;
        _memberRuns = [];
        SearchLog.Clear();
        DeviationLog.Clear();
        FailureLog.Clear();
        PotionsUsed.Clear();
        _lastCompletedForTesting = null;
        _combatStartedAt = Environment.TickCount64;
        SolverSettingsData settings = SolverSettings.Current;
        _mode = settings.Automation.Mode;
        _solverDisabled = settings.Automation.SolverDisabled;
        _combatActive = state != null;
        _trackerSeeded = false;
        _combatGeneration++;
        _phase = state == null ? CombatUiPhase.Idle : CombatUiPhase.WaitingTurn;
        if (state == null)
        {
            PublishSnapshot();
            return;
        }
        if (state.Players.Count == 1 && state.RunState is RunState runState && _player != null)
        {
            try
            {
                _rules = RulesCompiler.Compile(state, _player, runState);
                _tracker = new LiveCombatTracker(state);
            }
            catch (UnsupportedImportException error)
            {
                _errorCode = "import_failed";
                _errorDetail = $"reason={error.Reason} {error.Detail}";
                _importErrorCode = _errorCode;
                _importErrorDetail = _errorDetail;
            }
            catch (UnsupportedException error)
            {
                _errorCode = "import_unsupported";
                _errorDetail = error.Message;
                _importErrorCode = _errorCode;
                _importErrorDetail = _errorDetail;
            }
            catch (CapacityException error)
            {
                _errorCode = "import_capacity";
                _errorDetail = $"region={error.Region} {error.Message}";
                _importErrorCode = _errorCode;
                _importErrorDetail = _errorDetail;
            }
            catch (InvalidDataException error)
            {
                _errorCode = "invalid_data";
                _errorDetail = error.Message;
                _importErrorCode = _errorCode;
                _importErrorDetail = _errorDetail;
            }
            catch (InvalidOperationException error)
            {
                _errorCode = "invalid_operation";
                _errorDetail = error.Message;
                _importErrorCode = _errorCode;
                _importErrorDetail = _errorDetail;
            }
        }
        _fullAutoEnabled = CanArmFullAuto();
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_BEGIN combat={state.GetHashCode()} players={state.Players.Count} " +
            $"mode={_mode} disabled={_solverDisabled} full_auto={_fullAutoEnabled} " +
            $"multiplayer={IsMultiplayerSession} error={_errorCode ?? "-"}");
    }

    internal static async Task<bool> CancelAndWaitAsync(NGame host, string reason,
        int timeoutMilliseconds)
    {
        CancelTurnSchedule();
        CancelSearch();
        CancelDeployment();
        _phase = _combatActive ? CombatUiPhase.WaitingTurn : CombatUiPhase.Idle;
        PublishSnapshot();
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (IsBusy || SearchSession.IsRunning)
        {
            if (Environment.TickCount64 >= deadline)
            {
                ModLog.Error($"[Sts2Solver] CONTROLLER_CANCEL_TIMEOUT reason={reason}");
                return false;
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        return true;
    }

    internal static void OnCombatEnded()
    {
        int generation = _combatGeneration;
        if (_deploymentResult is { } active)
        {
            _completedActions = active.Plan.Actions.Length;
            _currentActionIndex = active.Plan.Actions.Length;
        }
        _phase = CombatUiPhase.Completed;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_COMBAT_ENDED in_progress={CombatManager.Instance.IsInProgress} " +
            $"turn={CurrentTurn()} hp={_player?.Creature.CurrentHp.ToString() ?? "-"} " +
            $"enemies={_state?.Enemies.Count.ToString() ?? "-"}");
        CancelTurnSchedule();
        CancelSearch();
        CancelDeployment();
        _combatCancellation.Cancel();
        _combatActive = false;
        _fullAutoEnabled = false;
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        _search = null;
        _deployment = null;
        _ = FinalizeCombatEndAsync(generation);
    }

    private static async Task FinalizeCombatEndAsync(int generation)
    {
        NGame? host = NGame.Instance;
        if (host != null)
        {
            for (int frame = 0; frame < 4; frame++)
                await CombatFrameWaiter.NextFrameAsync(host);
        }
        if (generation != _combatGeneration)
            return;
        CaptureCompletedSummary();
        _phase = CombatUiPhase.Completed;
        PublishSnapshot();
        ClearCombatState();
        _phase = CombatUiPhase.Idle;
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_END searches={_searches} reused={_reusedTurns} " +
            $"deviations={_deviations} failures={_deploymentFailures} " +
            $"status={Volatile.Read(ref _lastCompletedForTesting)?.Status ?? "-"}");
    }

    private static void ClearCombatState()
    {
        _state = null;
        _player = null;
        _rules = null;
        _tracker = null;
        _names = null;
        _search = null;
        _deployment = null;
        _latestResult = null;
        _deploymentResult = null;
        _stoppedResult = null;
        _continuationSource = null;
        _latestStamp = null;
        _pendingCompleteBaseline = null;
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        _lastSolverDeployedTurn = null;
        _fullAutoEnabled = false;
        _pauseCode = null;
        _pauseReason = null;
        _pausePlannedHpLost = null;
        _pauseLiveHpLost = null;
        _pausePlayerDead = false;
        _consecutiveFailures = 0;
        _consecutiveFailureTurn = null;
        _errorCode = null;
        _errorDetail = null;
        _importErrorCode = null;
        _importErrorDetail = null;
        _rejectCode = null;
        _finishedSearch = null;
        _lastProgress = null;
        _lastProgressPublishAt = 0;
        _lastCandidateVersion = 0;
        _currentActionIndex = 0;
        _completedActions = 0;
    }

    internal static void Reset(string reason)
    {
        RequireMainThread();
        _combatGeneration++;
        if (_combatActive)
            CaptureCompletedSummary();
        CancelTurnSchedule();
        CancelSearch();
        CancelDeployment();
        _combatCancellation.Cancel();
        ClearCombatState();
        _combatActive = false;
        _phase = CombatUiPhase.Idle;
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_RESET reason={reason}");
    }

    internal static void OnTurnStarted(CombatState state)
    {
        RequireMainThread();
        if (!_combatActive || !ReferenceEquals(state, _state))
            return;
#if STS2_SOLVER_TESTING
        if (HoldAutoSearchForTest)
            return;
#endif
        if (_solverDisabled || IsMultiplayerSession || state.CurrentSide != CombatSide.Player)
            return;
        Player? player = LocalContext.GetMe(state);
        if (player?.PlayerCombatState is not { } playerState)
            return;
        int turn = playerState.TurnNumber;
        ResetConsecutiveFailures(turn);
        if (_automaticSearchPaused
            && _automaticSearchPausedTurn is { } stoppedTurn
            && stoppedTurn != turn)
        {
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_AUTO_PAUSE_CLEARED stopped_turn={stoppedTurn} turn={turn}");
        }
        if (!ShouldAutomaticallySearchNextTurn)
        {
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return;
        }
        if (_automaticSearchPaused)
        {
            _pauseCode = "stopped";
            _phase = CombatUiPhase.Paused;
            PublishSnapshot();
            return;
        }
        if (_search != null || _deployment != null)
            return;
        CancelTurnSchedule();
        CancellationTokenSource schedule =
            CancellationTokenSource.CreateLinkedTokenSource(_combatCancellation.Token);
        _turnSchedule = schedule;
        _ = RunTurnStartedAsync(state, turn, schedule);
    }

    private static async Task RunTurnStartedAsync(CombatState state, int turn,
        CancellationTokenSource schedule)
    {
        CancellationToken token = schedule.Token;
        try
        {
            NGame? host = NGame.Instance;
            if (host == null)
                return;
            // Native turn-start card selection keeps PlayerTurnPhase at Start while the setup
            // task awaits a player choice. The action queue can already be empty at that point,
            // so there is no frame bound here: wait until Play, or until the schedule is canceled
            // by a turn change, combat end, solver disable or Manual mode.
            int frame = 0;
            while (frame < 2 || !IsPlayable(state, turn))
            {
                token.ThrowIfCancellationRequested();
                if (!_combatActive
                    || _solverDisabled
                    || _mode == SolverMode.Manual
                    || !ReferenceEquals(state, _state)
                    || !CombatManager.Instance.IsInProgress)
                {
                    return;
                }
                await CombatFrameWaiter.NextFrameAsync(host);
                frame++;
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
            await CombatFrameWaiter.SettleAsync(host);
            token.ThrowIfCancellationRequested();
#if STS2_SOLVER_TESTING
            await CombatControllerTestHooks.BeforeAutoSearchAsync(state, turn, token);
#endif
            if (!_combatActive
                || _solverDisabled
                || IsMultiplayerSession
                || !ShouldAutomaticallySearchNextTurn
                || _automaticSearchPaused
                || _search != null
                || _deployment != null
                || !ReferenceEquals(state, _state)
                || !IsPlayable(state, turn))
            {
                return;
            }
            ModLog.Info($"[Sts2Solver] CONTROLLER_AUTO_SEARCH turn={turn} mode={_mode}");
            RequestSearch(CombatSearchReason.AutoTurnStart, deployWhenReady: false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_turnSchedule, schedule))
                _turnSchedule = null;
            schedule.Dispose();
        }
    }

    internal static void Pump()
    {
        if (!_combatActive || _search == null)
            return;
        SearchProgressSnapshot? progress = SearchSession.LatestProgress;
        if (progress == null)
            return;
        bool changed = !ReferenceEquals(progress, _lastProgress);
        _lastProgress = progress;
        if (progress.Route != null && progress.CandidateVersion > _search.RenderedCandidateVersion)
            _search.RenderedCandidateVersion = progress.CandidateVersion;
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.AutoStopOnCandidate
            && _search.RenderedCandidateVersion > 0 && !_search.StopRequested)
        {
            StopSearch();
        }
#endif
        if (progress.CandidateVersion > _lastCandidateVersion)
            _lastCandidateVersion = progress.CandidateVersion;
        _activeMemberRun = progress.MemberIndex;
        _activeMemberWidth = progress.Member.Width;
        _activeMemberBudget = checked((long)progress.Member.Width * _searchProfile.MillisecondsPerWidth);
        int runningExpanded = _memberRuns.Sum(static run => run.ExpandedNodes);
        if (progress.ExpandedNodes > runningExpanded)
            _activeMemberStartedAt = Math.Max(0,
                Environment.TickCount64 - Math.Max(0, progress.ElapsedMilliseconds));
        long now = Environment.TickCount64;
        if (!changed && now - _lastProgressPublishAt < 100)
            return;
        _lastProgressPublishAt = now;
        _phase = CombatUiPhase.Searching;
        PublishSnapshot();
    }

    internal static void OnSettingsChanged()
    {
        if (!_combatActive)
            return;
        SolverSettingsData settings = SolverSettings.Current;
        if (_solverDisabled != settings.Automation.SolverDisabled)
            SetSolverEnabled(!settings.Automation.SolverDisabled, persist: false);
        else if (_mode != settings.Automation.Mode)
            SetMode(settings.Automation.Mode, persist: false);
        else
            PublishSnapshot();
    }

    private static bool IsPlayable(CombatState state, int turn)
    {
        if (!ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
            || !CombatManager.Instance.IsInProgress
            || state.Players.Count != 1
            || state.CurrentSide != CombatSide.Player)
        {
            return false;
        }
        Player? player = LocalContext.GetMe(state);
        return player?.PlayerCombatState is { Phase: PlayerTurnPhase.Play } playerState
            && playerState.TurnNumber == turn;
    }

    private static void ResetConsecutiveFailures(int turn)
    {
        if (_consecutiveFailureTurn == turn)
            return;
        _consecutiveFailureTurn = turn;
        _consecutiveFailures = 0;
    }

    private static void ClearConsecutiveFailures(int turn)
    {
        _consecutiveFailureTurn = turn;
        _consecutiveFailures = 0;
    }

    /// <summary>
    /// Counts one search/deployment failure in the current turn. The third consecutive failure
    /// pauses full auto for this turn and prevents another automatic search.
    /// </summary>
    private static bool RecordConsecutiveFailure(int turn, string reason, string detail)
    {
        ResetConsecutiveFailures(turn);
        _consecutiveFailures++;
        FailureLog.Add(new CombatPilotFailureEntry(turn, reason, detail));
        if (!CombatFailureLimit.Reached(_consecutiveFailures))
        {
            ModLog.Warn(
                $"[Sts2Solver] CONTROLLER_FAILURE_COUNT turn={turn} count={_consecutiveFailures} " +
                $"reason={reason} {detail}");
            return false;
        }
        DisableFullAuto(persistMode: true);
        _continuationSource = null;
        _automaticSearchPaused = true;
        _automaticSearchPausedTurn = turn;
        _pauseCode = "repeated_failure";
        _pauseReason = $"consecutive_failures={_consecutiveFailures}";
        _phase = CombatUiPhase.Paused;
        PublishSnapshot();
        ModLog.Warn(
            $"[Sts2Solver] CONTROLLER_REPEATED_FAILURE turn={turn} count={_consecutiveFailures} " +
            $"last_reason={reason} {detail}");
        return true;
    }

    /// <summary>
    /// Whether full auto may be armed for this combat. This is the same capability gate as
    /// <see cref="BeginCombat"/>: a live single-player combat, solver enabled, and no import error.
    /// Play phase is deliberately not part of it; execution waits for the turn-start event.
    /// </summary>
    private static bool CanArmFullAuto()
    {
        if (_mode != SolverMode.FullAuto
            || !_combatActive
            || _state == null
            || _player == null
            || _rules == null
            || _tracker == null
            || _solverDisabled
            || IsMultiplayerSession
            || _state.Players.Count != 1
            || _importErrorCode != null)
        {
            return false;
        }
        return CombatManager.Instance.IsInProgress
            && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _state);
    }

    private static bool CanSolve(out string rejection)
    {
        if (!_combatActive || _state == null)
        {
            rejection = "not_in_combat";
            return false;
        }
        if (_solverDisabled)
        {
            rejection = "solver_disabled";
            return false;
        }
        if (IsMultiplayerSession)
        {
            rejection = "multiplayer";
            return false;
        }
        if (!ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _state)
            || !CombatManager.Instance.IsInProgress
            || _state.Players.Count != 1
            || _player == null
            || _rules == null
            || _tracker == null)
        {
            rejection = "not_playable";
            return false;
        }
        if (_player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play }
            || _state.CurrentSide != CombatSide.Player)
        {
            rejection = "not_play_phase";
            return false;
        }
        rejection = "";
        return true;
    }

    private static int CurrentTurn()
        => _player?.PlayerCombatState?.TurnNumber ?? 0;

    private static SolverMode NextMode(SolverMode mode) => mode switch
    {
        SolverMode.Manual => SolverMode.AutoSearch,
        SolverMode.AutoSearch => SolverMode.FullAuto,
        _ => SolverMode.Manual,
    };

    internal static SolverMode CycleMode()
    {
        SolverMode next = NextMode(_mode);
        _ = SetMode(next);
        return next;
    }

    private static void DisableFullAuto(bool persistMode)
    {
        if (_fullAutoEnabled)
            ModLog.Info($"[Sts2Solver] CONTROLLER_FULL_AUTO_OFF source=disable persist={persistMode} " +
                $"pause={_pauseCode ?? "-"}");
        _fullAutoEnabled = false;
        if (!persistMode || _mode != SolverMode.FullAuto)
            return;
        _mode = SolverMode.AutoSearch;
        if (!SolverSettings.Update(current => current with
            {
                Automation = current.Automation with { Mode = SolverMode.AutoSearch },
            }))
        {
            ModLog.Error("[Sts2Solver] CONTROLLER_MODE_PERSIST_FAILED mode=AutoSearch");
        }
    }

    private static void CancelSearch()
    {
        CombatSearchSession? search = _search;
        _search = null;
        if (search == null)
        {
            _turnSchedule?.Cancel();
            return;
        }
        if (!search.Cancellation.IsCancellationRequested)
            search.Cancellation.Cancel();
        SearchSession.Cancel("controller");
        search.Cancellation.Dispose();
    }

    private static void CancelDeployment()
    {
        CombatDeploymentSession? deployment = _deployment;
        _deployment = null;
        if (deployment == null)
            return;
        if (!deployment.Cancellation.IsCancellationRequested)
            deployment.Cancellation.Cancel();
        deployment.Cancellation.Dispose();
    }

    private static void CancelTurnSchedule()
    {
        CancellationTokenSource? schedule = _turnSchedule;
        _turnSchedule = null;
        if (schedule == null)
            return;
        if (!schedule.IsCancellationRequested)
            schedule.Cancel();
        schedule.Dispose();
    }

    private static void CaptureCompletedSummary()
    {
        CombatPilotResult summary = BuildPilotResult();
        _lastCompletedForTesting = summary;
    }

    private static string? TerminalStatus()
    {
        if (_pauseCode != null && _combatActive)
            return "paused";
        if (_state == null)
            return null;
        Player? player = LocalContext.GetMe(_state);
        if (player == null)
            return null;
        bool alive = player.Creature.IsAlive;
        bool enemiesAlive = _state.Enemies.Any(static enemy => enemy.IsAlive);
        if (!alive)
            return "defeat";
        return !enemiesAlive ? "victory" : null;
    }

    private static async Task KickAutoSearchIfNeededAsync()
    {
        if (!_combatActive
            || _solverDisabled
            || _search != null
            || _deployment != null
            || !ShouldAutomaticallySearchNextTurn
            || _automaticSearchPaused
            || _state == null
            || _player?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } state)
        {
            return;
        }
        int turn = state.TurnNumber;
        if (!IsPlayable(_state, turn))
            return;
        NGame? host = NGame.Instance;
        if (host != null)
            await CombatFrameWaiter.NextFrameAsync(host);
        if (!IsPlayable(_state!, turn) || _search != null || _deployment != null)
            return;
#if STS2_SOLVER_TESTING
        await CombatControllerTestHooks.BeforeAutoSearchAsync(_state!, turn,
            _combatCancellation.Token);
        if (!IsPlayable(_state!, turn) || _search != null || _deployment != null)
            return;
#endif
        RequestSearch(CombatSearchReason.AutoTurnStart, deployWhenReady: false);
    }

    private static void RequireMainThread()
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("战斗控制状态只能在游戏主线程修改。");
    }
}
