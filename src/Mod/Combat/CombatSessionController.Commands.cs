// Command surface of the controller: mode, search, stop, execute and strategy changes. All commands
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
    internal static bool SetMode(SolverMode mode, bool persist = true)
    {
        RequireMainThread();
        if (!Enum.IsDefined(mode))
            return false;
        if (mode == SolverMode.FullAuto && IsMultiplayerSession)
            return false;
        if (persist && SolverSettings.Current.Automation.Mode != mode
            && !SolverSettings.Update(current => current with
            {
                Automation = current.Automation with { Mode = mode },
            }))
        {
            return false;
        }
        bool changed = _mode != mode;
        _mode = mode;
        ClearFailureState();
        if (mode != SolverMode.FullAuto && changed && PlayerTurnSetupRuntime.IsDriving)
            PlayerTurnSetupRuntime.StopDriving();
        if (mode != SolverMode.FullAuto)
        {
            if (_fullAutoEnabled)
                ModLog.Info($"[Sts2Solver] CONTROLLER_FULL_AUTO_OFF source=mode mode={mode}");
            _fullAutoEnabled = false;
            if (_fullAutoPausedThisCombat)
            {
                _fullAutoPausedThisCombat = false;
                _pauseCode = null;
                _pauseReason = null;
            }
            if (mode == SolverMode.Manual)
                CancelTurnSchedule();
            if (_search == null && !HasBlockingDeployment && _pauseCode == null)
                _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            if (mode == SolverMode.AutoSearch && changed
                && _search == null && !HasBlockingDeployment
                && !_automaticSearchPaused)
            {
                TrySearchOnModeSwitch(CombatSearchReason.AutoTurnStart);
            }
            return true;
        }

        bool resumePaused = _fullAutoPausedThisCombat;
        _fullAutoPausedThisCombat = false;
        _pauseCode = null;
        _pauseReason = null;
        _rejectCode = null;
        if (resumePaused)
        {
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
        }
        _fullAutoEnabled = CanArmFullAuto();
        if (!_fullAutoEnabled)
        {
            ModLog.Info("[Sts2Solver] CONTROLLER_FULL_AUTO_NOT_ARMED reason=combat_not_eligible");
            _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            return true;
        }
        if (PlayerTurnSetupRuntime.HasActivePage)
        {
            if (!_automaticSearchPaused)
                PlayerTurnSetupRuntime.OnFullAutoResumed(skipPause: resumePaused);
            PublishSnapshot();
            return true;
        }
        if (_turnSetupResult != null)
        {
            if (TryActivateTurnSetup(CurrentTurn()))
            {
                PublishSnapshot();
                return true;
            }
            // The pending TurnStarted / normal search owns the turn; do not start deployment from
            // an unvalidated setup result.
            PublishSnapshot();
            return true;
        }
        if (_search != null || HasBlockingDeployment)
        {
            PublishSnapshot();
            return true;
        }
        if (!_automaticSearchPaused
            && _latestResult is { } result && ResultStampMatches(result))
        {
            StartFullAutoDeployment(result);
        }
        else if (!_automaticSearchPaused)
        {
            TrySearchOnModeSwitch(CombatSearchReason.FullAuto);
        }
        if (_search == null && !HasBlockingDeployment && _phase != CombatUiPhase.Searching
            && _phase != CombatUiPhase.Deploying && _pauseCode == null)
        {
            _phase = CombatUiPhase.WaitingTurn;
        }
        PublishSnapshot();
        return true;
    }

    internal static bool StartOrRecalculateSearch()
    {
        RequireMainThread();
        if (IsMultiplayerSession || _search is { IsTurnSetup: true } || HasBlockingDeployment
            || SearchSession.IsRunning)
            return false;
        if (PlayerTurnSetupRuntime.HasActivePage)
            return PlayerTurnSetupRuntime.TryRecalculate();
        if (_turnSetupResult != null)
            DiscardTurnSetupPlan("manual_search");
        if (_search != null)
            return false;
        if (!CanSolve(out string rejection))
        {
            _rejectCode = rejection;
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_REJECT reason={rejection}");
            return false;
        }
        SearchResumeState? resume = null;
        SearchPlan? resumePlan = null;
        if (_searchContinuation is { } continuation
            && TryContinueSearch(continuation, out SearchResumeState state, out SearchPlan plan))
        {
            resume = state;
            resumePlan = plan;
        }
        RequestSearch(CombatSearchReason.Manual, deployWhenReady: false, resume, resumePlan);
        return true;
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

    internal static bool StopDeployment()
    {
        RequireMainThread();
        if (IsMultiplayerSession)
            return false;
        if (PlayerTurnSetupRuntime.StopDriving())
        {
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_TURN_SETUP_DRIVE turn={CurrentTurn()}");
            return true;
        }
        if (_deployment is not { } deployment || deployment.StopRequested)
            return false;
        deployment.StopRequested = true;
        _automaticSearchPaused = true;
        _automaticSearchPausedTurn = CurrentTurn();
        _pauseCode = "stopped";
        _pauseReason = "stopped_by_user";
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_DEPLOYMENT turn={deployment.Turn}");
        return true;
    }

    internal static bool ExecuteCurrentTurn()
    {
        RequireMainThread();
        if (IsMultiplayerSession || HasBlockingDeployment)
            return false;
        if (PlayerTurnSetupRuntime.HasActivePage)
        {
            if (_search != null)
                return false;
            if (!PlayerTurnSetupRuntime.TryStartDriving(deployAfterSetup: true))
                return false;
            ResumeFullAutoForExecute();
            _automaticSearchPaused = false;
            _automaticSearchPausedTurn = null;
            _pauseCode = null;
            _pauseReason = null;
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_EXECUTE_TURN_SETUP turn={CurrentTurn()}");
            return true;
        }
        // A running search offers no execute: the player stops it first (the stop keeps the best
        // route) and then executes that route.
        if (_search != null)
            return false;
        if (_turnSetupResult != null)
        {
            if (!TryActivateTurnSetup(CurrentTurn()))
                return false;
            // Activation clears the pause on success and sets a new one when the live state moved.
            if (_mode == SolverMode.FullAuto && _pauseCode == null && !HasBlockingDeployment
                && _latestResult is { } activated && ResultStampMatches(activated))
            {
                ResumeFullAutoForExecute();
                StartDeployment(activated);
            }
            return true;
        }
        if (_latestResult is { } result && ResultStampMatches(result))
        {
            ResumeFullAutoForExecute();
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
    /// In full-auto mode the execute key always starts full auto; there is no play-this-turn-only
    /// key there (user decision 2026-11-07). A pause of this combat (projected death, deviation,
    /// failure) or a stop by the player is lifted and full auto is armed again. After a pause the
    /// press confirms the current turn: it deploys without the before-deployment and live end-turn
    /// pause checks (<see cref="_playerConfirmedTurn"/>); later turns are checked as usual.
    /// </summary>
    private static void ResumeFullAutoForExecute()
    {
        if (_mode != SolverMode.FullAuto)
            return;
        bool wasPaused = _fullAutoPausedThisCombat;
        string? pause = _pauseCode;
        _fullAutoPausedThisCombat = false;
        _pauseCode = null;
        _pauseReason = null;
        _rejectCode = null;
        _automaticSearchPaused = false;
        _automaticSearchPausedTurn = null;
        if (wasPaused)
            _playerConfirmedTurn = CurrentTurn();
        bool wasEnabled = _fullAutoEnabled;
        _fullAutoEnabled = CanArmFullAuto();
        ModLog.Info(
            $"[Sts2Solver] CONTROLLER_FULL_AUTO_EXECUTE armed={_fullAutoEnabled} was_enabled={wasEnabled} " +
            $"was_paused={wasPaused} pause={pause ?? "-"} confirmed_turn={_playerConfirmedTurn?.ToString() ?? "-"}");
    }

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

    /// <summary>Starts the mode-switch search without replacing a route that is still current.</summary>
    private static void TrySearchOnModeSwitch(CombatSearchReason reason)
    {
        if (PlayerTurnSetupRuntime.HasActivePage)
            return;
        if (_latestResult is { } result && ResultStampMatches(result))
            return;
        if (!CanSolve(out _))
            return;
        RequestSearch(reason, deployWhenReady: false);
    }
}
