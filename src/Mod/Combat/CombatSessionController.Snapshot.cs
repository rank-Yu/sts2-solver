// Snapshot publication. The panel only ever reads these immutable records; every value is built
// on the game main thread from controller state, a frozen search progress snapshot or a replayed
// route.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    private static void PublishSnapshot()
    {
        if (!NGame.IsMainThread() || !CombatUiStore.Initialized)
            return;
        CombatUiSnapshot current = CombatUiStore.Snapshot;
        SearchMemoryBudgetResolution budget = SearchMemoryBudget.Resolve();
        int turn = CurrentTurn();
        int hp = _player?.Creature.CurrentHp ?? current.PlayerHp;
        int maxHp = _player?.Creature.MaxHp ?? current.PlayerMaxHp;
        bool turnSetupPage = PlayerTurnSetupRuntime.HasActivePage;
        bool turnSetupPlanAvailable = _turnSetupResult != null;
        bool turnSetupPlan = turnSetupPlanAvailable && turnSetupPage;
        bool turnSetupDriving = PlayerTurnSetupRuntime.IsDriving;
        bool latestStateMatches = _search == null && _latestResult is { } latest
            && ResultStampMatches(latest);
        bool canExecute = (!HasBlockingDeployment && latestStateMatches)
            || (turnSetupPlan && !turnSetupDriving && _search == null
                && !HasBlockingDeployment);
        bool canStart = false;
        string? searchBlock = null;
        if (_search == null)
        {
            if (turnSetupPage)
            {
                if (turnSetupPlan && !turnSetupDriving && !HasBlockingDeployment
                    && !SearchSession.IsRunning)
                {
                    canStart = true;
                }
                else
                {
                    searchBlock = "turn_setup";
                }
            }
            else if (!HasBlockingDeployment && !SearchSession.IsRunning && CanSolve(out _))
            {
                canStart = true;
            }
            else
            {
                searchBlock = CanSolve(out string reason) ? "searching" : reason;
            }
        }
        bool canContinue = canStart && latestStateMatches && _searchContinuation is { } continuation
            && ContinuationInputsMatch(continuation);
        bool continuationAvailable = _search == null && !HasBlockingDeployment
            && _searchContinuation != null;
        bool continuationSearch = _search is { IsContinuation: true };
        CombatControllerResult? routeSource = _search == null
            ? _latestResult ?? _deploymentResult
            : continuationSearch ? _latestResult : null;
        CombatControllerResult? shown = _search == null || continuationSearch
            ? _latestResult : null;
        CombatResultUiSnapshot? shownUi = shown == null ? null : ResultUi(shown);
        CombatFailureReason? errorReason =
            CombatFailureReasonClassifier.Error(_errorCode, _errorDetail, _finishedSearch);
        CombatFailureReason? pauseDetailReason =
            CombatFailureReasonClassifier.Pause(_pauseCode, _pauseReason, errorReason);
        CombatUiSnapshot next = new()
        {
            Phase = _phase,
            Root = current.Root.Length == 0 ? _testRoot ?? "" : current.Root,
            AutoSearchEnabled = _autoSearch,
            CombatRunActive = _runCombat,
            CombatRunPaused = _runPaused,
            AutomaticCalculationEnabled = _autoSearch || _runCombat,
            SearchStage = _search != null ? TurnSearchStage.Running
                : _searchedTurn == turn ? TurnSearchStage.Ended
                : TurnSearchStage.NotStarted,
            AutomaticSearchPaused = _automaticSearchPaused,
            AutomaticSearchPausedTurn = _automaticSearchPausedTurn,
            AutomationStoppedThisTurn = _automaticSearchPaused
                && _automaticSearchPausedTurn == turn,
            IsSearching = _search != null,
            IsDeploying = HasBlockingDeployment,
            TurnSetupPageOpen = turnSetupPage,
            TurnSetupPlanReady = turnSetupPlan,
            TurnSetupContinuation = PlayerTurnSetupRuntime.IsContinuationPlanReady,
            TurnSetupDriving = turnSetupDriving,
            TurnSetupSearchStarted = PlayerTurnSetupRuntime.SearchStarted,
            TurnSetupChoiceCount = PlayerTurnSetupRuntime.ChoiceCount,
            DeployWhenReady = _search?.DeployWhenReady ?? false,
            IsStoppingDeployment = HasBlockingDeployment && _deployment!.StopRequested,
            CanStartSearch = canStart,
            CanStopSearch = _search != null,
            SearchBlockReason = searchBlock,
            CurrentTurnRouteReady = canExecute,
            CanContinueSearch = canContinue,
            ContinuationAvailable = continuationAvailable,
            SearchCandidateAllDeath = _lastProgress?.Route?.OnlyDeathRoutes ?? false,
            LastSolverDeployedTurn = _lastSolverDeployedTurn,
            Result = shownUi,
            RouteResult = routeSource == null ? null
                : ReferenceEquals(routeSource, shown) ? shownUi : ResultUi(routeSource),
            Turn = turn,
            PlayerHp = hp,
            PlayerMaxHp = maxHp,
            EndReason = EndReason(),
            PauseReason = _pauseReason,
            PauseDetailReason = pauseDetailReason,
            PauseCode = _pauseCode ?? (_automaticSearchPaused ? "stopped" : null),
            ErrorCode = _errorCode,
            ErrorDetail = _errorDetail,
            ErrorReason = errorReason,
            RejectCode = _rejectCode,
            Search = BuildSearchUi(),
            Route = BuildRouteUi(routeSource),
            Memory = current.Memory,
            Settings = CombatUiProjection.Settings(budget),
            Strategy = BuildStrategyUi(),
            Searches = _searches,
            ReusedTurns = _reusedTurns,
            Deviations = _deviations,
            DeploymentFailures = _deploymentFailures,
            ReviewedWorldlines = ReviewedWorldlinesSoFar()
                + (_search != null ? _lastProgress?.RequestExpandedNodes ?? 0 : 0),
            LastSearchMemoryPeakBytes = _lastSearchMemoryPeakBytes,
        };
        CombatUiStore.Publish(next);
    }

    private static StrategyUiSnapshot BuildStrategyUi()
    {
        SolverSearchSettings search = SolverSettings.Current.Search;
        List<PotionSlotUi> potions = [];
        if (_player != null)
        {
            for (int slot = 0; slot < _player.PotionSlots.Count; slot++)
            {
                if (_player.GetPotionAtSlotIndex(slot) is not { } potion)
                    continue;
                PotionDirective directive = search.Potions
                    .ResolveDirective(slot, potion.Id.Entry);
                bool canAssign = Registry.TryPotion(potion.Id.Entry, out ushort id)
                    && Registry.Potion(id).Searchable;
                potions.Add(new PotionSlotUi
                {
                    Slot = slot,
                    PotionId = potion.Id.Entry,
                    Mode = directive.ToString().ToLowerInvariant(),
                    CanAssignPolicy = canAssign,
                });
            }
        }
        List<RelicCounterUi> relics = [];
        if (_player != null)
        {
            for (int index = 0; index < _player.Relics.Count; index++)
            {
                var relic = _player.Relics[index];
                if (relic.IsMelted || !RelicCounterCatalog.TryRead(relic, out int value, out int period))
                    continue;
                relics.Add(new RelicCounterUi
                {
                    Slot = index,
                    RelicId = relic.Id.Entry,
                    Value = value,
                    Period = period,
                });
            }
        }
        BossHpRelief relief = _state == null
            ? BossHpRelief.None
            : ScoreCompiler.ResolveNativeHpRelief(_state);
        return new StrategyUiSnapshot
        {
            Potions = [.. potions],
            GrowthBudgets = search.GrowthBudgets.ToPolicy(),
            IgnoreLongTermRewards = search.IgnoreLongTermRewards,
            RelicCounters = [.. relics],
            RelicCounterAllowances = new Dictionary<string, int>(search.RelicCounterAllowances,
                StringComparer.Ordinal),
            BrightestFlameMaxHpLossLimit = search.BrightestFlameMaxHpLossLimit,
            BrightestFlameMaxHpSpent = _state == null ? 0 : NativeHistory.BrightestFlameMaxHpSpent(),
            ActTransitionBossHpStrategy = search.ActTransitionBossHpStrategy,
            FinalBossHpStrategy = search.FinalBossHpStrategy,
            BossHpRelief = relief switch
            {
                BossHpRelief.ActClearHeal => "act_clear_heal",
                BossHpRelief.RunEnding => "run_ending",
                _ => "none",
            },
            IsActEndingBoss = relief != BossHpRelief.None,
            TheftApplicable = _state != null && SearchPolicyCapture.IsTheftApplicable(_state),
            TheftPolicy = _theftPolicy switch
            {
                SearchTheftPolicy.PreserveResources => "preserve_resources",
                SearchTheftPolicy.LetEscape => "let_escape",
                _ => null,
            },
        };
    }

    private static string? EndReason() => _phase switch
    {
        CombatUiPhase.Completed => TerminalStatus() ?? "ended",
        CombatUiPhase.Paused => "paused",
        CombatUiPhase.Cancelled => "cancelled",
        CombatUiPhase.Failed => "failed",
        _ => null,
    };

    private static CombatResultUiSnapshot ResultUi(CombatControllerResult result) => new()
    {
        StartTurn = result.StartTurn,
        DeathTurn = result.Outcome.DeathTurn,
        CombatEndedTurn = result.Outcome.CombatEndedTurn,
        ProjectedBattleHpLost = result.Outcome.ProjectedBattleHpLost,
        BattleHpLostSoFar = result.BattleHpLostSoFar,
        SearchExpandedNodes = result.SearchExpandedNodes,
        SearchElapsedMilliseconds = result.SearchElapsedMilliseconds,
        ProjectedBattlePotionCount = result.Outcome.ProjectedBattlePotionCount,
        PostCombatRelicHeal = result.Outcome.PostCombatRelicHeal,
        PreviousProjectedBattleHpLost = result.PreviousProjectedBattleHpLost,
        ProjectedBattleHpLossIncrease = result.ProjectedBattleHpLossIncrease,
        WasReused = result.WasReused,
        Victory = result.Outcome.Victory,
        OnlyDeathRoutes = result.Outcome.OnlyDeathRoutes,
        Outcome = OutcomeName(result.Outcome),
        AdoptionKind = result.Adoption?.Kind,
        CandidateVersion = result.CandidateVersion,
        CapacityDroppedRegions = result.CapacityDroppedRegions,
        StrategyRelics = [.. result.Outcome.RelicCounters.Select(static relic => new StrategyRelicUi(
            Registry.Relic(relic.RelicId).NativeId, relic.Value, relic.Goal, relic.Met))],
        StrategyGrowth = [.. result.Outcome.GrowthGained.Select(static gain => new StrategyGrowthUi(
            gain.Source.ToString(), gain.Count))],
        PotionsUsedSoFar = result.PotionsUsedSoFar ?? [],
        PlannedPotionIds = PlannedPotionIds(result.Plan),
        UnrecoveredCards = result.Outcome.UnrecoveredCards,
        UnrecoveredGold = result.Outcome.UnrecoveredGold,
    };

    /// <summary>The potions the plan's actions drink, read from the slot each use empties.</summary>
    private static string[] PlannedPotionIds(CombatRoutePlan plan)
    {
        List<string> ids = [];
        foreach (ReplayedAction action in plan.Actions)
        {
            PlayerAction kind = action.Action;
            if (kind.Kind != PlayerActionKind.UsePotion || kind.PotionSlot >= action.Before.PotionSlotCount)
                continue;
            ushort stored = action.Before.Potions[kind.PotionSlot];
            if (stored != 0)
                ids.Add(Registry.Potion((ushort)(stored - 1)).NativeId);
        }
        return [.. ids];
    }

    private static string OutcomeName(CombatRouteOutcome outcome)
        => outcome.Victory ? "victory"
            : outcome.DeathTurn.HasValue ? "defeat"
            : "incomplete";

    private static RouteUiSnapshot? BuildRouteUi(CombatControllerResult? result)
    {
        if (result == null || _names == null)
            return null;
        return CombatUiProjection.Route(result.Plan, _names, CurrentTurn(),
            _currentActionIndex, _completedActions, result.WasReused, _actionRunning);
    }

    private static SearchUiSnapshot? BuildSearchUi()
    {
        if (_search == null)
            return _finishedSearch;
        SearchProgressSnapshot? progress = _lastProgress;
        SearchUiPhase phase = _search.StopRequested ? SearchUiPhase.Stopping
            : SearchUiPhase.Running;
        return new SearchUiSnapshot
        {
            Phase = phase,
            Tier = _searchTier,
            Turn = _search.StartTurn,
            Reason = ReasonName(_search.Reason),
            SearchThreads = _searchThreads,
            ReviewedWorldlinesBeforeSearch = _search.ReviewedWorldlinesBeforeSearch,
            StartedAtMilliseconds = _searchStartedAt,
            ActiveMemberRun = _activeMemberRun,
            ActiveMemberWidth = _activeMemberWidth,
            ActiveMemberStartedAtMilliseconds = _activeMemberStartedAt,
            ElapsedMilliseconds = progress?.ElapsedMilliseconds
                ?? Math.Max(0, Environment.TickCount64 - _searchStartedAt),
            ExpandedNodes = progress?.RequestExpandedNodes ?? 0,
            SearchMemoryUsedBytes = progress?.SearchMemoryUsedBytes ?? 0,
            SearchMemoryPeakBytes = progress?.SearchMemoryPeakBytes ?? 0,
            BattleHpLostBeforeSearch = _search.BattleHpLostBeforeSearch,
            Transitions = _memberRuns.Sum(static run => (long)run.Transitions),
            CandidateVersion = progress?.CandidateVersion ?? 0,
            RenderedCandidateVersion = _search.RenderedCandidateVersion,
            StopCandidateAvailable = _search.RenderedCandidateVersion > 0,
            RoutePreview = _displayRoute == null ? null : RoutePreview(_displayRoute),
            CurrentTurnPreview = _displayCurrentTurn == null
                ? null
                : CurrentTurnPreview(_displayCurrentTurn),
            Status = "running",
            MemberRuns = _memberRuns,
        };
    }

    private static SearchRoutePreviewUi RoutePreview(SearchRoutePreview preview) => new()
    {
        CandidateVersion = preview.CandidateVersion,
        StartTurn = preview.StartTurn,
        ThroughTurn = preview.ThroughTurn,
        ActionCount = preview.Actions.Length,
        Victory = preview.Victory,
        OnlyDeathRoutes = preview.OnlyDeathRoutes,
        ProjectedBattleHpLost = preview.Turns.Sum(static turn => turn.HpLost),
        Turns = preview.Turns.Select(static turn => new SearchTurnPreviewUi
        {
            Turn = turn.Turn,
            ActionCount = turn.Actions.Length,
            HpLost = turn.HpLost,
            HpRecovered = turn.HpRecovered,
            EnemyHpLost = turn.EnemyHpLost,
            EnergyLeft = turn.EnergyLeft,
            CombatEnded = turn.CombatEnded,
            Actions = turn.Actions.Select(PreviewAction).ToArray(),
        }).ToArray(),
    };

    private static SearchActionPreviewUi PreviewAction(SearchProgressAction action) => new()
    {
        Kind = action.Kind switch
        {
            PlayerActionKind.PlayCard => "play_card",
            PlayerActionKind.UsePotion => "use_potion",
            PlayerActionKind.EndTurn => "end_turn",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        },
        CardId = action.CardId,
        Upgrade = action.Upgrade,
        PotionId = action.PotionId,
        CardType = action.CardType,
        TargetKind = action.TargetKind switch
        {
            SearchProgressTargetKind.Player => "player",
            SearchProgressTargetKind.Osty => "osty",
            SearchProgressTargetKind.Enemy => "enemy",
            _ => "",
        },
        TargetCombatId = action.TargetCombatId,
        TargetMonsterId = action.TargetMonsterId,
        TargetOrdinal = action.TargetOrdinal,
        Kills = action.Kills.Select(static kill => new KillUi
        {
            CombatId = kill.CombatId,
            MonsterId = kill.MonsterId,
            Ordinal = kill.Ordinal,
        }).ToArray(),
        HpBefore = action.HpBefore,
        HpAfter = action.HpAfter,
        EnergyBefore = action.EnergyBefore,
        EnergyAfter = action.EnergyAfter,
        ReplayCount = action.ReplayCount,
    };

    private static SearchCurrentTurnPreviewUi CurrentTurnPreview(SearchCurrentTurnPreview preview)
        => new()
        {
            CandidateVersion = preview.CandidateVersion,
            Turn = preview.Turn,
            ActionCount = preview.Actions.Length,
            HpLost = preview.HpLost,
            HpRecovered = preview.HpRecovered,
            EnemyHpLost = preview.EnemyHpLost,
            EnergyLeft = preview.EnergyLeft,
            CombatEnded = preview.CombatEnded,
            Actions = preview.Actions.Select(PreviewAction).ToArray(),
        };

    private static SearchUiSnapshot BuildFinishedSearch(CombatSearchSession session,
        CombatSearchRun? run, string status, string? error)
    {
        SearchPerformanceProfile profile = run?.Profile ?? _searchProfile;
        SearchPerformanceTier tier = run?.Tier ?? _searchTier;
        SearchUiSnapshot baseSnapshot;
        if (run != null)
        {
            baseSnapshot = CombatUiProjection.SearchFinished(run.Outcome, run.MemberRuns,
                session.StartTurn, ReasonName(session.Reason), tier, profile, error,
                _searchStartedAt);
        }
        else
        {
            baseSnapshot = new SearchUiSnapshot
            {
                Phase = SearchUiPhase.Cancelled,
                Tier = tier,
                Turn = session.StartTurn,
                Reason = ReasonName(session.Reason),
                StartedAtMilliseconds = _searchStartedAt,
                SearchThreads = _searchThreads,
                ElapsedMilliseconds = Math.Max(0, Environment.TickCount64 - _searchStartedAt),
                Status = status,
                SearchStatus = status,
                ErrorCode = error,
                Victory = false,
            };
        }
        SearchProgressSnapshot? final = run?.FinalProgress ?? _lastProgress;
        return baseSnapshot with
        {
            Phase = status switch
            {
                "cancelled" => SearchUiPhase.Cancelled,
                "failed" or "stale" => SearchUiPhase.Failed,
                _ => SearchUiPhase.Completed,
            },
            Status = status,
            ErrorCode = error,
            CandidateVersion = final?.CandidateVersion ?? 0,
            RenderedCandidateVersion = session.RenderedCandidateVersion,
            StopCandidateAvailable = session.RenderedCandidateVersion > 0,
            ReviewedWorldlinesBeforeSearch = session.ReviewedWorldlinesBeforeSearch,
            RoutePreview = final?.Route == null ? null : RoutePreview(final.Route),
            CurrentTurnPreview = final?.CurrentTurn == null
                ? null
                : CurrentTurnPreview(final.CurrentTurn),
            MemberRuns = run?.MemberRuns ?? [],
        };
    }

    private static CombatPilotResult BuildPilotResult()
    {
        string status = TerminalStatus() ?? (_pauseCode != null ? "paused" : "in_progress");
        string outcome = status switch
        {
            "victory" => "victory",
            "defeat" => "defeat",
            "paused" => "in_progress",
            _ => "in_progress",
        };
        int turn = CurrentTurn();
        int hp = _player?.Creature.CurrentHp ?? 0;
        int maxHp = _player?.Creature.MaxHp ?? 0;
        return new CombatPilotResult(
            _testRoot ?? "manual", status, outcome, turn, hp, maxHp, [.. PotionsUsed],
            _searches, _reusedTurns, _deviations, _deploymentFailures,
            Math.Max(0, Environment.TickCount64 - _combatStartedAt), _pauseReason,
            [.. SearchLog], [.. DeviationLog], [.. FailureLog]);
    }
}
