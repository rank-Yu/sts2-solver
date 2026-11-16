// Footer and command bindings, first ported from CombatSolver src/UI/SolverOverlay.cs (CreateFooter,
// RefreshControls and the button handlers) at 5c773caa (MIT License, Copyright (c) 2026 Torch1230
// and contributors). Search and execution each occupy a fixed row with their own switch.
// Which keys show follows how far the current turn's search
// and execution have gone, never whether the search found a route.
using Godot;

namespace Sts2Solver;

/// <summary>One footer key as the stage rules want it.</summary>
internal readonly record struct SolverFooterKey(bool Visible, string Text, SolverButtonStyle Style,
    bool Disabled, string? Tooltip);

/// <summary>The whole footer for one snapshot; <see cref="SolverOverlay.FooterFor"/> builds it.</summary>
internal readonly record struct SolverFooterState(SolverFooterKey Search, SolverFooterKey Restart,
    SolverFooterKey ExecuteTurn, SolverFooterKey ExecuteCombat, SolverFooterKey Stop);

internal sealed partial class SolverOverlay
{
    private SolverActionBar? _actionBar;
    private SolverMemoryBar? _memoryBar;
    private CheckButton? _autoSearchSwitch;
    private CheckButton? _autoExecuteSwitch;
    private Control? _autoExecuteGroup;
    private Button? _searchButton;
    private Button? _restartButton;
    private Button? _executeTurnButton;
    private Button? _executeCombatButton;
    private Button? _stopButton;
    private readonly Dictionary<Button, (string? Text, SolverButtonStyle Style, string? Tooltip)> _rendered = [];
    private bool _syncingAutoSearchSwitch;
    private bool _syncingAutoExecuteSwitch;

    private Control CreateFooter()
    {
        HBoxContainer autoSearch = new()
        {
            Name = "AutoSearch",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = SolverText.Get("ui.automatically_search_for_a_card_play_route_when_combat_starts"),
        };
        autoSearch.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        autoSearch.AddChild(SolverUiTokens.CreateLabel(SolverText.Get("action.auto_search"), SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary));
        _autoSearchSwitch = SolverSettingsControls.CreateToggle();
        _autoSearchSwitch.Name = "AutoSearchSwitch";
        _autoSearchSwitch.TooltipText = autoSearch.TooltipText;
        _autoSearchSwitch.ButtonPressed = SolverSettings.Current.Automation.AutoSearch;
        _autoSearchSwitch.Toggled += OnAutoSearchToggled;
        autoSearch.AddChild(_autoSearchSwitch);

        HBoxContainer autoExecute = new()
        {
            Name = "AutoExecute",
            TooltipText = SolverText.Get("ui.automatically_play_the_route_when_the_search_finishes"),
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        autoExecute.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        autoExecute.AddChild(SolverUiTokens.CreateLabel(SolverText.Get("action.auto_play"), SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary));
        _autoExecuteSwitch = SolverSettingsControls.CreateToggle();
        _autoExecuteSwitch.Name = "AutoExecuteSwitch";
        _autoExecuteSwitch.TooltipText = autoExecute.TooltipText;
        _autoExecuteSwitch.ButtonPressed = SolverSettings.Current.Automation.AutoExecute;
        _autoExecuteSwitch.Toggled += OnAutoExecuteToggled;
        autoExecute.AddChild(_autoExecuteSwitch);
        _autoExecuteGroup = autoExecute;

        _searchButton = CreateFooterButton("SearchButton", "action.search", 112);
        _searchButton.Pressed += OnSearchPressed;
        _restartButton = CreateFooterButton("RestartSearchButton", "action.restart_search", 112);
        _restartButton.Pressed += OnRestartPressed;
        _executeTurnButton = CreateFooterButton("ExecuteTurnButton", "action.play_turn", 112);
        _executeTurnButton.Pressed += OnExecuteTurnPressed;
        _executeCombatButton = CreateFooterButton("ExecuteCombatButton", "action.play_combat", 132);
        _executeCombatButton.Pressed += OnExecuteCombatPressed;
        _stopButton = CreateFooterButton("StopExecutionButton", "action.stop_play", 112);
        _stopButton.Pressed += OnStopPressed;

        _memoryBar = new SolverMemoryBar { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _actionBar = new SolverActionBar(autoSearch, _searchButton, _restartButton, autoExecute, _executeTurnButton,
            _executeCombatButton, _stopButton, _memoryBar);
        return _actionBar;
    }

    private static Button CreateFooterButton(string name, string text, float minimumWidth)
    {
        Button button = SolverUiTokens.CreateButton(SolverText.Get(text), SolverButtonStyle.Secondary);
        button.Name = name;
        button.CustomMinimumSize = new Vector2(minimumWidth, SolverUiTokens.Size.ButtonHeight);
        return button;
    }

    private void OnAutoSearchToggled(bool enabled)
    {
        if (_syncingAutoSearchSwitch)
            return;
        bool accepted = CombatUiCommands.Instance.SetAutoSearch(enabled);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=auto_search enabled={enabled} accepted={accepted}");
    }

    private void OnAutoExecuteToggled(bool enabled)
    {
        if (_syncingAutoExecuteSwitch)
            return;
        bool accepted = CombatUiCommands.Instance.SetAutoExecute(enabled);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=auto_execute enabled={enabled} accepted={accepted}");
    }

    /// <summary>The left search key: 开始搜索, 停止搜索 or 继续搜索 depending on the stage.</summary>
    private void OnSearchPressed()
    {
        CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
        (string action, bool accepted) = snapshot.IsSearching
            ? ("stop_search", CombatUiCommands.Instance.StopSearch())
            : snapshot.SearchStage == TurnSearchStage.NotStarted
                ? ("start_search", CombatUiCommands.Instance.StartSearch())
                : ("continue_search", CombatUiCommands.Instance.ContinueSearch());
        ModLog.Info($"[Sts2Solver] UI_ACTION action={action} accepted={accepted}");
    }

    private void OnRestartPressed()
    {
        bool accepted = CombatUiCommands.Instance.StartSearch();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=restart_search accepted={accepted}");
    }

    private void OnExecuteTurnPressed()
    {
        bool accepted = CombatUiCommands.Instance.ExecuteCurrentTurn();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=execute_turn accepted={accepted}");
    }

    private void OnExecuteCombatPressed()
    {
        bool accepted = CombatUiCommands.Instance.ExecuteCombat();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=execute_combat accepted={accepted}");
    }

    private void OnStopPressed()
    {
        bool accepted = CombatUiCommands.Instance.StopExecution();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=stop_execution accepted={accepted}");
    }

    private void RefreshControls(CombatUiSnapshot snapshot)
    {
        if (_searchButton == null || _restartButton == null || _executeTurnButton == null
            || _executeCombatButton == null || _stopButton == null || _actionBar == null
            || _autoSearchSwitch == null || _autoExecuteSwitch == null || _autoExecuteGroup == null)
        {
            return;
        }
        if (_autoSearchSwitch.ButtonPressed != snapshot.AutoSearchEnabled)
        {
            _syncingAutoSearchSwitch = true;
            _autoSearchSwitch.ButtonPressed = snapshot.AutoSearchEnabled;
            _syncingAutoSearchSwitch = false;
        }
        if (_autoExecuteSwitch.ButtonPressed != snapshot.AutoExecuteEnabled)
        {
            _syncingAutoExecuteSwitch = true;
            _autoExecuteSwitch.ButtonPressed = snapshot.AutoExecuteEnabled;
            _syncingAutoExecuteSwitch = false;
        }
        _autoExecuteSwitch.Disabled = !snapshot.AutoSearchEnabled;
        _autoExecuteGroup.Modulate = new Color(1f, 1f, 1f, snapshot.AutoSearchEnabled ? 1f : 0.4f);
        SolverFooterState footer = FooterFor(snapshot);
        Apply(_searchButton, footer.Search);
        Apply(_restartButton, footer.Restart);
        Apply(_executeTurnButton, footer.ExecuteTurn);
        Apply(_executeCombatButton, footer.ExecuteCombat);
        Apply(_stopButton, footer.Stop);
        _actionBar.Refresh(new SolverActionBarState(_collapsed, footer.Search.Visible,
            footer.Restart.Visible, footer.ExecuteTurn.Visible, footer.ExecuteCombat.Visible,
            footer.Stop.Visible));
        _memoryBar?.Apply(snapshot.Memory, snapshot.Search, snapshot.LastSearchMemoryPeakBytes);
    }

    /// <summary>
    /// The footer rules. Executing (a turn, 执行本场战斗, a setup-page drive, or a replan that deploys
    /// when ready) shows only 停止执行 and greys the search keys. A plain search shows 停止搜索 and no
    /// execute key. Otherwise the search keys follow the stage (开始搜索 before the turn's first search,
    /// 继续搜索 + 重新搜索 after it) and both execute keys stay in place, greyed when nothing can run.
    /// </summary>
    internal static SolverFooterState FooterFor(CombatUiSnapshot snapshot)
    {
        bool executing = snapshot.IsDeploying || snapshot.TurnSetupDriving || snapshot.CombatRunActive
            || snapshot.IsSearching && snapshot.DeployWhenReady;
        bool notStarted = snapshot.SearchStage == TurnSearchStage.NotStarted;
        SolverFooterKey hidden = new(false, "", SolverButtonStyle.Secondary, true, null);
        SolverFooterKey search;
        SolverFooterKey restart;
        if (executing)
        {
            string blocked = SolverText.Get("ui.stop_automatic_play_first");
            search = new(true, SolverText.Get(notStarted ? "action.search" : "action.continue_search"),
                SolverButtonStyle.Secondary, true, blocked);
            restart = notStarted ? hidden
                : new(true, SolverText.Get("action.restart_search"), SolverButtonStyle.Secondary, true, blocked);
        }
        else if (snapshot.IsSearching)
        {
            bool stopping = snapshot.Search?.Phase == SearchUiPhase.Stopping;
            search = new(true, SolverText.Get("action.stop_search"), SolverButtonStyle.Danger, stopping,
                stopping ? SolverText.Get("ui.stopping_the_search") : SolverText.Get("ui.stop_searching_and_keep_the_route_found_so_far"));
            restart = hidden;
        }
        else
        {
            string? block = snapshot.CanStartSearch ? null : SolverPanelText.SearchBlock(snapshot.SearchBlockReason);
            if (notStarted)
            {
                search = new(true, SolverText.Get("action.search"), SolverButtonStyle.Secondary,
                    !snapshot.CanStartSearch, block ?? SolverText.Get("ui.search_for_a_card_play_route_from_the_current_situation"));
                restart = hidden;
            }
            else
            {
                string? continueBlock = snapshot.CanContinueSearch ? null
                    : block ?? (snapshot.ContinuationAvailable
                        ? SolverText.Get("ui.the_situation_or_settings_have_changed_start_a_new_search")
                        : SolverText.Get("ui.the_search_is_complete_there_is_nothing_left_to_continue"));
                search = new(true, SolverText.Get("action.continue_search"), SolverButtonStyle.Secondary,
                    !snapshot.CanContinueSearch, continueBlock ?? SolverText.Get("ui.resume_the_search_from_where_it_stopped"));
                restart = new(true, SolverText.Get("action.restart_search"), SolverButtonStyle.Secondary,
                    !snapshot.CanStartSearch, block ?? SolverText.Get("ui.start_a_new_search_from_the_current_situation"));
            }
        }

        SolverFooterKey executeTurn;
        SolverFooterKey executeCombat;
        SolverFooterKey stop;
        if (executing)
        {
            executeTurn = hidden;
            executeCombat = hidden;
            stop = new(true, SolverText.Get("action.stop_play"), SolverButtonStyle.Danger,
                snapshot.IsStoppingDeployment,
                snapshot.IsStoppingDeployment ? SolverText.Get("ui.stops_after_the_current_action")
                    : SolverText.Get("ui.stop_automatic_play_after_the_current_action_finishes"));
        }
        else if (snapshot.IsSearching)
        {
            executeTurn = hidden;
            executeCombat = hidden;
            stop = hidden;
        }
        else
        {
            bool ready = snapshot.CurrentTurnRouteReady;
            string? blocked = ready ? null
                : notStarted ? SolverText.Get("ui.search_for_a_card_play_route_first")
                : snapshot.CanStartSearch || snapshot.SearchBlockReason == null
                    ? SolverText.Get("ui.no_route_is_ready_to_play_start_a_new_search")
                    : SolverPanelText.SearchBlock(snapshot.SearchBlockReason);
            executeTurn = new(true, SolverText.Get("action.play_turn"), SolverButtonStyle.Secondary, !ready,
                blocked ?? SolverText.Get("ui.play_this_turn_using_the_route"));
            executeCombat = new(true, SolverText.Get("action.play_combat"), SolverButtonStyle.Secondary, !ready,
                blocked ?? (snapshot.CombatRunPaused && snapshot.PauseCode != null
                    ? SolverText.Get("ui.continue_playing_this_route_automatically")
                    : SolverText.Get("ui.play_the_route_automatically_until_combat_ends")));
            stop = hidden;
        }
        return new SolverFooterState(search, restart, executeTurn, executeCombat, stop);
    }

    private void Apply(Button button, SolverFooterKey key)
    {
        _rendered.TryGetValue(button, out (string? Text, SolverButtonStyle Style, string? Tooltip) shown);
        bool first = !_rendered.ContainsKey(button);
        if (first || shown.Text != key.Text)
            button.Text = key.Text;
        if (first || shown.Style != key.Style)
            SolverUiTokens.ApplyButtonStyle(button, key.Style);
        if (first || shown.Tooltip != key.Tooltip)
            button.TooltipText = key.Tooltip ?? "";
        _rendered[button] = (key.Text, key.Style, key.Tooltip);
        button.Disabled = key.Disabled;
    }
}
