// Command surface of the controller: auto-search switch, search, stop, execute and strategy changes. All commands
// run on the game main thread and return false when the button would be disabled.
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
    /// <summary>
    /// The auto-search switch (persisted). It only decides whether a player turn searches by itself
    /// when it starts; it never starts or stops an execution and never replaces a route. Turning it
    /// on in a turn that has not searched yet starts that turn's search; turning it off leaves a
    /// running search alone (the player stops it with 停止搜索).
    /// </summary>
    internal static bool SetAutoSearch(bool enabled, bool persist = true)
    {
        RequireMainThread();
        bool previous = _autoSearch;
        // Set first: the settings store calls back into OnSettingsChanged, which must see the new
        // value and only publish instead of switching a second time.
        _autoSearch = enabled;
        if (persist && SolverSettings.Current.Automation.AutoSearch != enabled
            && !SolverSettings.Update(current => current with
            {
                Automation = current.Automation with { AutoSearch = enabled },
            }))
        {
            _autoSearch = previous;
            return false;
        }
        if (previous != enabled)
        {
            ModLog.Info($"[Sts2Solver] CONTROLLER_AUTO_SEARCH_SWITCH enabled={enabled} turn={CurrentTurn()}");
            if (enabled)
                TrySearchOnAutoSearchSwitch();
        }
        PublishSnapshot();
        return true;
    }

    /// <summary>
    /// 开始搜索 / 重新搜索: a fresh search of the current turn. A stopped search's continuation is
    /// discarded. On an open turn-setup page it recalculates the page plan.
    /// </summary>
    internal static bool StartSearch()
    {
        RequireMainThread();
        if (!CanIssueSearch())
            return false;
        if (PlayerTurnSetupRuntime.HasActivePage)
            return PlayerTurnSetupRuntime.TryRecalculate();
        if (_turnSetupResult != null)
            DiscardTurnSetupPlan("manual_search");
        if (_search != null || !CanSolveForCommand())
            return false;
        InvalidateSearchContinuation("restart");
        RequestSearch(CombatSearchReason.Manual, deployWhenReady: false);
        return true;
    }

    /// <summary>
    /// 继续搜索: resumes a stopped search from its next member when the recompiled request is
    /// byte-identical to the stop; a different request searches from scratch. Without a stopped
    /// search there is nothing to continue and the press is refused (the panel greys the key out).
    /// </summary>
    internal static bool ContinueSearch()
    {
        RequireMainThread();
        if (!CanIssueSearch() || PlayerTurnSetupRuntime.HasActivePage || _search != null
            || !CanSolveForCommand())
        {
            return false;
        }
        if (_searchContinuation is not { } continuation)
        {
            ModLog.Info("[Sts2Solver] CONTROLLER_CONTINUE_REJECT reason=no_continuation");
            return false;
        }
        // The key is offered when the state stamp and the settings match the stop; the exact
        // request comparison happens here. A difference falls back to a fresh search (logged by
        // TryContinueSearch) rather than ignoring the press.
        if (TryContinueSearch(continuation, out SearchResumeState state, out SearchPlan plan))
            RequestSearch(CombatSearchReason.Manual, deployWhenReady: false, state, plan);
        else
            RequestSearch(CombatSearchReason.Manual, deployWhenReady: false);
        return true;
    }

    private static bool CanIssueSearch()
        => !IsMultiplayerSession && !_runCombat && _search is not { IsTurnSetup: true }
            && !HasBlockingDeployment && !SearchSession.IsRunning;

    private static bool CanSolveForCommand()
    {
        if (CanSolve(out string rejection))
            return true;
        _rejectCode = rejection;
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_REJECT reason={rejection}");
        return false;
    }

    internal static bool StopSearch()
    {
        RequireMainThread();
        if (IsMultiplayerSession || _search is not { } search || search.StopRequested)
            return false;
        _automaticSearchPaused = true;
        _automaticSearchPausedTurn = CurrentTurn();
        _pauseCode = "stopped";
        _pauseReason = "stopped_by_user";
        _phase = CombatUiPhase.Searching;
        search.StopRequested = true;
        search.DeployWhenReady = false;
        if (!SearchSession.RequestStop())
        {
            search.Cancellation.Cancel();
            SearchSession.Cancel("user");
        }
        PublishSnapshot();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_STOP_REQUEST turn={search.StartTurn} " +
            $"setup={search.IsTurnSetup}");
        return true;
    }

    /// <summary>
    /// 停止执行: ends 执行本场战斗, soft-stops the running turn (the current action settles), stops a
    /// setup-page drive and stops a search that belongs to the execution (the run's next-turn search
    /// or a replan that would deploy when ready). Afterwards the player is back in control.
    /// </summary>
    internal static bool StopExecution()
    {
        RequireMainThread();
        if (IsMultiplayerSession)
            return false;
        bool stopped = false;
        if (_runCombat)
        {
            StopRun("player");
            stopped = true;
        }
        if (PlayerTurnSetupRuntime.StopDriving())
        {
            ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_TURN_SETUP_DRIVE turn={CurrentTurn()}");
            stopped = true;
        }
        else if (_deployment is { StopRequested: false } deployment)
        {
            deployment.StopRequested = true;
            _automaticSearchPaused = true;
            _automaticSearchPausedTurn = CurrentTurn();
            _pauseCode = "stopped";
            _pauseReason = "stopped_by_user";
            ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_DEPLOYMENT turn={deployment.Turn}");
            stopped = true;
        }
        if (_search is { StopRequested: false } search && (stopped || search.DeployWhenReady))
            return StopSearch() || stopped;
        if (stopped)
            PublishSnapshot();
        return stopped;
    }

    /// <summary>执行本回合: executes the current turn's route only; the next turn waits for the player.</summary>
    internal static bool ExecuteCurrentTurn() => Execute(runCombat: false);

    /// <summary>
    /// 执行本场战斗: executes the current turn's route and keeps searching (or reusing) and executing
    /// every following turn until the combat ends, a pause stops it or the player stops it.
    /// </summary>
    internal static bool ExecuteCombat() => Execute(runCombat: true);

    private static bool Execute(bool runCombat)
    {
        RequireMainThread();
        if (IsMultiplayerSession || HasBlockingDeployment || _runCombat)
            return false;
        if (runCombat && !CanRunCombat())
        {
            ModLog.Info("[Sts2Solver] CONTROLLER_EXECUTE_REJECT reason=combat_not_eligible");
            return false;
        }
        // A running search offers no execute: the player stops it first (the stop keeps the best
        // route) and then executes that route.
        if (_search != null)
            return false;
        if (PlayerTurnSetupRuntime.HasActivePage)
        {
            if (!PlayerTurnSetupRuntime.TryStartDriving(deployAfterSetup: true))
                return false;
            BeginExecution(runCombat);
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_EXECUTE_TURN_SETUP turn={CurrentTurn()} run={runCombat}");
            return true;
        }
        if (_turnSetupResult != null)
        {
            if (!TryActivateTurnSetup(CurrentTurn()))
                return false;
            // Activation clears the pause on success and sets a new one when the live state moved;
            // an activation that already deploys (the page asked for it) needs nothing more.
            if (_pauseCode == null && !HasBlockingDeployment
                && _latestResult is { } activated && ResultStampMatches(activated))
            {
                BeginExecution(runCombat);
                StartDeployment(activated);
            }
            return true;
        }
        if (_latestResult is { } result && ResultStampMatches(result))
        {
            BeginExecution(runCombat);
            StartDeployment(result);
            return true;
        }
        if (!CanSolve(out string rejection))
        {
            ModLog.Info($"[Sts2Solver] CONTROLLER_EXECUTE_REJECT reason={rejection}");
            return false;
        }
        ModLog.Info("[Sts2Solver] CONTROLLER_EXECUTE_REJECT reason=no_current_turn_route");
        return false;
    }

    /// <summary>
    /// The press is the player's decision for this turn: a pause (projected death, deviation,
    /// failure) or a stop is lifted. 执行本回合 leaves the run off, so the turn deploys without the
    /// run's pause checks. 执行本场战斗 starts the run; after one of the run's own pauses the press
    /// confirms this turn, which skips the before-deployment and live end-turn checks
    /// (<see cref="_playerConfirmedTurn"/>); later turns are checked as usual.
    /// </summary>
    private static void BeginExecution(bool runCombat)
    {
        bool wasPaused = _runPaused;
        string? pause = _pauseCode;
        _runPaused = false;
        _pauseCode = null;
        _pauseReason = null;
        _rejectCode = null;
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        if (runCombat)
        {
            if (wasPaused)
                _playerConfirmedTurn = CurrentTurn();
            _runCombat = true;
        }
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_EXECUTE turn={CurrentTurn()} run={runCombat} " +
            $"was_paused={wasPaused} pause={pause ?? "-"} " +
            $"confirmed_turn={_playerConfirmedTurn?.ToString() ?? "-"}");
    }

#if STS2_SOLVER_TESTING
    /// <summary>
    /// Test seam: arms 执行本场战斗 the way a run that is already going reaches the current turn, so
    /// the run's own paths (reuse or search, then the pause checks before deploying, the setup-page
    /// drive) are exercised without a player press. A pause of the run is lifted as a press would.
    /// The player-facing entry is <see cref="ExecuteCombat"/>.
    /// </summary>
    internal static bool ArmRunForTest()
    {
        RequireMainThread();
        if (IsMultiplayerSession || !CanRunCombat())
            return false;
        bool resumePaused = _runPaused;
        _runPaused = false;
        _pauseCode = null;
        _pauseReason = null;
        _rejectCode = null;
        if (resumePaused)
        {
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
        }
        _runCombat = true;
        ModLog.Info($"[Sts2Solver] CONTROLLER_RUN_ARMED_FOR_TEST turn={CurrentTurn()} resumed={resumePaused}");
        if (PlayerTurnSetupRuntime.HasActivePage)
        {
            if (!_automaticSearchPaused)
                PlayerTurnSetupRuntime.OnRunArmedForTest(skipPause: resumePaused);
            PublishSnapshot();
            return true;
        }
        if (_turnSetupResult != null)
        {
            TryActivateTurnSetup(CurrentTurn());
            PublishSnapshot();
            return true;
        }
        if (_search != null || HasBlockingDeployment)
        {
            PublishSnapshot();
            return true;
        }
        if (!_automaticSearchPaused && _latestResult is { } result && ResultStampMatches(result))
            StartFullAutoDeployment(result);
        else if (!_automaticSearchPaused && !PlayerTurnSetupRuntime.HasActivePage && CanSolve(out _))
            RequestSearch(CombatSearchReason.AutoSearchSwitch, deployWhenReady: false);
        if (_search == null && !HasBlockingDeployment && _phase != CombatUiPhase.Searching
            && _phase != CombatUiPhase.Deploying && _pauseCode == null)
        {
            _phase = CombatUiPhase.WaitingTurn;
        }
        PublishSnapshot();
        return true;
    }

    /// <summary>
    /// Test seam: ends the run the way leaving the old full-auto mode did (a setup-page drive stops,
    /// a pause of the run is cleared; a running deployment is not stopped), then sets the switch.
    /// </summary>
    internal static bool LeaveRunForTest(bool autoSearch)
    {
        RequireMainThread();
        if (_runCombat && PlayerTurnSetupRuntime.IsDriving)
            PlayerTurnSetupRuntime.StopDriving();
        StopRun("test");
        if (_runPaused)
        {
            _runPaused = false;
            _pauseCode = null;
            _pauseReason = null;
        }
        if (!autoSearch)
            CancelTurnSchedule();
        if (_search == null && !HasBlockingDeployment && _pauseCode == null)
            _phase = CombatUiPhase.WaitingTurn;
        return SetAutoSearch(autoSearch);
    }
#endif

    internal static bool SetPotionDirective(int slot, string potionId, SolverPotionMode mode)
    {
        RequireMainThread();
        if (!Enum.IsDefined(mode) || _state == null || _player == null
            || slot < 0 || slot >= _player.PotionSlots.Count || string.IsNullOrEmpty(potionId))
        {
            return false;
        }
        if (_player.GetPotionAtSlotIndex(slot) is not { } potion
            || !string.Equals(potion.Id.Entry, potionId, StringComparison.Ordinal))
        {
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_POTION_DIRECTIVE_REJECT reason=slot_identity slot={slot} " +
                $"potion={potionId}");
            return false;
        }
        PotionDirective current = SolverSettings.Current.Search.Potions
            .ResolveDirective(slot, potionId);
        PotionDirective next = mode switch
        {
            SolverPotionMode.Disabled => PotionDirective.Disabled,
            SolverPotionMode.Force => PotionDirective.Force,
            _ => PotionDirective.Smart,
        };
        if (current == next)
            return true;
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with
                {
                    Potions = settings.Search.Potions.ApplyDirective(slot, potionId, mode),
                },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"potion slot={slot} potion={potionId} mode={mode}");
        return true;
    }

    internal static bool SetAllPotionDirectives(SolverPotionMode mode)
    {
        RequireMainThread();
        if (!Enum.IsDefined(mode))
            return false;
        if (!TryCollectSearchablePotions(out List<(int Slot, string PotionId)> searchable))
            return false;
        SolverSettingsData current = SolverSettings.Current;
        SolverPotionSettings potions = current.Search.Potions;
        foreach ((int slot, string potionId) in searchable)
            potions = potions.ApplyDirective(slot, potionId, mode);
        SolverPotionPreset preset = mode switch
        {
            SolverPotionMode.Disabled => SolverPotionPreset.ProtectAll,
            SolverPotionMode.Force => SolverPotionPreset.ForceAll,
            _ => SolverPotionPreset.Smart,
        };
        potions = potions with { Preset = preset };
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with { Potions = potions },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"potions all={mode} count={searchable.Count}");
        return true;
    }

    /// <summary>
    /// <c>PotionStrategyPreset.OnlyForced</c>: keep the potions already forced, protect the rest.
    /// </summary>
    internal static bool SetOnlyForcedPotionDirectives()
    {
        RequireMainThread();
        if (!TryCollectSearchablePotions(out List<(int Slot, string PotionId)> searchable))
            return false;
        SolverSettingsData current = SolverSettings.Current;
        SolverPotionSettings potions = current.Search.Potions;
        foreach ((int slot, string potionId) in searchable)
        {
            SolverPotionMode mode = potions.ResolveDirective(slot, potionId)
                == PotionDirective.Force ? SolverPotionMode.Force : SolverPotionMode.Disabled;
            potions = potions.ApplyDirective(slot, potionId, mode);
        }
        potions = potions with { Preset = SolverPotionPreset.OnlyForced };
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with { Potions = potions },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"potions only_forced count={searchable.Count}");
        return true;
    }

    private static bool TryCollectSearchablePotions(
        out List<(int Slot, string PotionId)> searchable)
    {
        searchable = [];
        if (_state == null || _player == null)
            return false;
        for (int slot = 0; slot < _player.PotionSlots.Count; slot++)
        {
            if (_player.GetPotionAtSlotIndex(slot) is not { } potion)
                continue;
            if (!Registry.TryPotion(potion.Id.Entry, out ushort id))
                return false;
            if (!Registry.Potion(id).Searchable)
                continue;
            searchable.Add((slot, potion.Id.Entry));
        }
        return true;
    }

    internal static bool SetGrowthBudget(GrowthSource source, int value)
    {
        RequireMainThread();
        if (!Enum.IsDefined(source) || value is < 0 or > 1000)
            return false;
        SolverSettingsData current = SolverSettings.Current;
        if (current.Search.GrowthBudgets.Get(source) == value)
            return true;
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with
                {
                    GrowthBudgets = settings.Search.GrowthBudgets.With(source, value),
                },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"growth source={source} value={value}");
        return true;
    }

    internal static bool SetIgnoreLongTermRewards(bool ignore)
    {
        RequireMainThread();
        if (SolverSettings.Current.Search.IgnoreLongTermRewards == ignore)
            return true;
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with { IgnoreLongTermRewards = ignore },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"ignore_long_term_rewards={ignore}");
        return true;
    }

    internal static bool SetBrightestFlameLimit(int? limit)
    {
        RequireMainThread();
        if (limit is < 0 or > 1000)
            return false;
        if (SolverSettings.Current.Search.BrightestFlameMaxHpLossLimit == limit)
            return true;
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with { BrightestFlameMaxHpLossLimit = limit },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"brightest_flame_limit={limit?.ToString() ?? "none"}");
        return true;
    }

    internal static bool SetActTransitionBossHpStrategy(SearchBossHpStrategy strategy)
    {
        RequireMainThread();
        if (!Enum.IsDefined(strategy))
            return false;
        if (SolverSettings.Current.Search.ActTransitionBossHpStrategy == strategy)
            return true;
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with { ActTransitionBossHpStrategy = strategy },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"act_transition_boss_hp={strategy}");
        return true;
    }

    internal static bool SetFinalBossHpStrategy(SearchBossHpStrategy strategy)
    {
        RequireMainThread();
        if (!Enum.IsDefined(strategy))
            return false;
        if (SolverSettings.Current.Search.FinalBossHpStrategy == strategy)
            return true;
        if (!SolverSettings.Update(settings => settings with
            {
                Search = settings.Search with { FinalBossHpStrategy = strategy },
            }))
        {
            return false;
        }
        StrategySettingsChanged($"final_boss_hp={strategy}");
        return true;
    }

    internal static bool SetTheftPolicy(SearchTheftPolicy policy)
    {
        RequireMainThread();
        if (!Enum.IsDefined(policy) || _state == null
            || !SearchPolicyCapture.IsTheftApplicable(_state))
        {
            return false;
        }
        if (_theftPolicy == policy)
            return true;
        _theftPolicy = policy;
        _continuationSource = null;
        _pendingCompleteBaseline = null;
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_THEFT_POLICY policy={policy}");
        return true;
    }

    private static void StrategySettingsChanged(string detail)
    {
        _continuationSource = null;
        _pendingCompleteBaseline = null;
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_STRATEGY_CHANGED {detail}");
    }

    /// <summary>
    /// Turning auto search on starts the turn's search only when this turn has not searched or
    /// reused a route yet, nothing runs and the turn can be solved. A current route is never replaced.
    /// </summary>
    private static void TrySearchOnAutoSearchSwitch()
    {
        if (!_combatActive || _runCombat || _search != null || HasBlockingDeployment
            || PlayerTurnSetupRuntime.HasActivePage || _automaticSearchPaused
            || _searchedTurn == CurrentTurn())
        {
            return;
        }
        if (_latestResult is { } result && ResultStampMatches(result))
            return;
        if (!CanSolve(out _))
            return;
        RequestSearch(CombatSearchReason.AutoSearchSwitch, deployWhenReady: false);
    }
}
