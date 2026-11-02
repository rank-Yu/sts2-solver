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
            if (_search == null && _deployment == null && _pauseCode == null)
                _phase = CombatUiPhase.WaitingTurn;
            PublishSnapshot();
            if (mode == SolverMode.AutoSearch && changed
                && _search == null && _deployment == null
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
        if (_search != null || _deployment != null)
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
        if (_search == null && _deployment == null && _phase != CombatUiPhase.Searching
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
        if (IsMultiplayerSession || _search != null || _deployment != null
            || SearchSession.IsRunning)
            return false;
        if (!CanSolve(out string rejection))
        {
            _rejectCode = rejection;
            PublishSnapshot();
            ModLog.Info($"[Sts2Solver] CONTROLLER_SEARCH_REJECT reason={rejection}");
            return false;
        }
        RequestSearch(CombatSearchReason.Manual, deployWhenReady: false);
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
        search.ApplyCurrentTurn = false;
        search.DeployWhenReady = false;
        if (!SearchSession.RequestStop())
        {
            search.Cancellation.Cancel();
            SearchSession.Cancel("user");
        }
        PublishSnapshot();
        ModLog.Info($"[Sts2Solver] CONTROLLER_STOP_REQUEST turn={search.StartTurn}");
        return true;
    }

    internal static bool StopDeployment()
    {
        RequireMainThread();
        if (IsMultiplayerSession || _deployment is not { } deployment || deployment.StopRequested)
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
        if (IsMultiplayerSession || _deployment != null)
            return false;
        if (_search is { } search)
        {
            if (search.StopRequested || search.ApplyCurrentTurn
                || search.RenderedCandidateVersion <= 0
                || _lastProgress?.CurrentTurn == null
                || _lastProgress.Route?.OnlyDeathRoutes == true)
            {
                return false;
            }
            _automaticSearchPaused = true;
            _automaticSearchPausedTurn = CurrentTurn();
            search.ApplyCurrentTurn = true;
            search.StopRequested = true;
            search.DeployWhenReady = true;
            _pauseCode = "stopped";
            _pauseReason = "execute_current_turn";
            _phase = CombatUiPhase.Searching;
            if (!SearchSession.RequestStop())
            {
                search.ApplyCurrentTurn = false;
                search.StopRequested = false;
                search.DeployWhenReady = false;
                _automaticSearchPaused = false;
                _automaticSearchPausedTurn = null;
                _pauseCode = null;
                _pauseReason = null;
                return false;
            }
            PublishSnapshot();
            ModLog.Info(
                $"[Sts2Solver] CONTROLLER_EXECUTE_CURRENT_TURN turn={search.StartTurn} " +
                $"candidate={search.RenderedCandidateVersion}");
            return true;
        }
        if (_latestResult is { } result && ResultStampMatches(result))
        {
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
        if (_latestResult is { } result && ResultStampMatches(result))
            return;
        if (!CanSolve(out _))
            return;
        RequestSearch(reason, deployWhenReady: false);
    }
}
