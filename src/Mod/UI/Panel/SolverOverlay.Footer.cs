// Footer and command bindings ported from CombatSolver src/UI/SolverOverlay.cs (CreateFooter,
// RefreshControls and the button handlers) at 5c773caa (MIT License, Copyright (c) 2026 Torch1230
// and contributors). The footer has two slots: search on the left and execute on the right. The
// current mode is the segmented control in the header.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverOverlay
{
    private SolverActionBar? _actionBar;
    private SolverMemoryBar? _memoryBar;
    private Button? _searchButton;
    private Button? _executeButton;
    private SolverMode? _renderedMode;
    private bool _renderedFullAutoPaused;
    private SolverButtonStyle _renderedSearchStyle = SolverButtonStyle.Secondary;
    private SolverButtonStyle _renderedExecuteStyle = SolverButtonStyle.Secondary;
    private string? _renderedSearchText;
    private string? _renderedExecuteText;
    private string? _renderedSearchTooltip;
    private string? _renderedExecuteTooltip;

    private Control CreateFooter()
    {
        _searchButton = CreateButton(SolverText.Get("开始搜索"));
        _searchButton.Name = "SearchButton";
        _searchButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _searchButton.Pressed += OnSearchPressed;

        _executeButton = CreateButton(SolverText.Get("执行本回合"));
        _executeButton.Name = "ExecuteButton";
        _executeButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _executeButton.Pressed += OnExecutePressed;

        _memoryBar = new SolverMemoryBar { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _actionBar = new SolverActionBar(_searchButton, _executeButton, _memoryBar);
        return _actionBar;
    }

    private static Button CreateButton(string text)
        => SolverUiTokens.CreateButton(text, SolverButtonStyle.Secondary);

    private void OnSearchPressed()
    {
        CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
        bool accepted = snapshot.IsSearching
            ? CombatUiCommands.Instance.StopSearch()
            : CombatUiCommands.Instance.StartOrRecalculateSearch();
        ModLog.Info(
            $"[Sts2Solver] UI_ACTION action={(snapshot.IsSearching ? "stop_search" : "search")} " +
            $"accepted={accepted}");
    }

    private void OnExecutePressed()
    {
        CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
        bool accepted = snapshot.IsDeploying
            ? CombatUiCommands.Instance.StopDeployment()
            : snapshot.IsSearching && snapshot.DeployWhenReady
                ? CombatUiCommands.Instance.StopSearch()
                : CombatUiCommands.Instance.ExecuteCurrentTurn();
        string action = snapshot.IsDeploying ? "stop_deployment"
            : snapshot.IsSearching && snapshot.DeployWhenReady ? "stop_queued_execution"
            : "execute";
        ModLog.Info($"[Sts2Solver] UI_ACTION action={action} accepted={accepted}");
    }

    private void RefreshControls(CombatUiSnapshot snapshot)
    {
        if (_searchButton == null || _executeButton == null || _actionBar == null)
            return;
        RefreshModeButtons(snapshot);
        bool searching = snapshot.IsSearching;
        bool deploying = snapshot.IsDeploying;
        bool routeReady = snapshot.CurrentTurnRouteReady;
        bool searchingStopping = snapshot.Search?.Phase == SearchUiPhase.Stopping;

        string searchText;
        SolverButtonStyle searchStyle = SolverButtonStyle.Secondary;
        bool searchDisabled;
        string? searchTooltip = null;
        if (searching)
        {
            searchText = SolverText.Get("停止搜索");
            searchStyle = SolverButtonStyle.Danger;
            searchDisabled = searchingStopping;
            if (searchDisabled)
                searchTooltip = SolverText.Get("正在停止搜索…");
        }
        else
        {
            searchText = SolverText.Get(
                deploying || routeReady || snapshot.Phase == CombatUiPhase.Failed
                    ? "重新搜索"
                    : "开始搜索");
            searchDisabled = !snapshot.CanStartSearch;
            if (searchDisabled)
                searchTooltip = SolverPanelText.SearchBlock(snapshot.SearchBlockReason);
        }
        SetButton(_searchButton, searchText, searchStyle, searchDisabled, searchTooltip,
            ref _renderedSearchText, ref _renderedSearchStyle, ref _renderedSearchTooltip);

        bool showExecute;
        string executeText;
        SolverButtonStyle executeStyle = SolverButtonStyle.Secondary;
        bool executeDisabled = false;
        string? executeTooltip = null;
        if (deploying)
        {
            showExecute = true;
            executeText = SolverText.Get("停止执行");
            executeStyle = SolverButtonStyle.Danger;
            executeDisabled = snapshot.IsStoppingDeployment;
            executeTooltip = snapshot.IsStoppingDeployment
                ? SolverText.Get("当前动作完成后停止。")
                : SolverText.Get("停止执行当前回合；已发出的动作会结算完。");
        }
        else if (searching && snapshot.DeployWhenReady)
        {
            showExecute = true;
            executeText = SolverText.Get("停止执行");
            executeStyle = SolverButtonStyle.Danger;
            executeDisabled = searchingStopping;
            executeTooltip = SolverText.Get("停止这次搜索；本回合不再自动执行。");
        }
        else if (searching)
        {
            showExecute = !searchingStopping
                && snapshot.Search?.StopCandidateAvailable == true
                && snapshot.Search?.CurrentTurnPreview != null
                && !snapshot.SearchCandidateAllDeath;
            executeText = SolverText.Get("执行本回合");
            executeTooltip = SolverText.Get("停止搜索，按当前最佳候选执行本回合。");
        }
        else if (routeReady)
        {
            showExecute = true;
            executeText = SolverText.Get("执行本回合");
            executeTooltip = snapshot.FullAutoPausedThisCombat && snapshot.PauseCode != null
                ? SolverText.Get("玩家确认后强制执行这条路线。")
                : SolverText.Get("执行当前回合的动作。");
        }
        else
        {
            showExecute = false;
            executeText = SolverText.Get("执行本回合");
        }
        SetButton(_executeButton, executeText, executeStyle, executeDisabled, executeTooltip,
            ref _renderedExecuteText, ref _renderedExecuteStyle, ref _renderedExecuteTooltip);
        _actionBar.Refresh(new SolverActionBarState(_collapsed, showExecute));
        _memoryBar?.Apply(snapshot.Memory, snapshot.Search);
    }

    private void RefreshModeButtons(CombatUiSnapshot snapshot)
    {
        if (_manualModeButton == null || _autoSearchModeButton == null || _fullAutoModeButton == null)
            return;
        if (_renderedMode == snapshot.Mode
            && _renderedFullAutoPaused == snapshot.FullAutoPausedThisCombat)
        {
            return;
        }
        _renderedMode = snapshot.Mode;
        _renderedFullAutoPaused = snapshot.FullAutoPausedThisCombat;
        ApplyModeButton(_manualModeButton, SolverMode.Manual, snapshot);
        ApplyModeButton(_autoSearchModeButton, SolverMode.AutoSearch, snapshot);
        ApplyModeButton(_fullAutoModeButton, SolverMode.FullAuto, snapshot);
        _fullAutoModeButton.TooltipText = SolverText.Get(snapshot.FullAutoPausedThisCombat
            ? "本场全自动已暂停，点击恢复"
            : "手动不自动搜索；自动搜索每回合搜索但不出牌；全自动搜索后自动执行。");
    }

    private static void ApplyModeButton(Button button, SolverMode mode, CombatUiSnapshot snapshot)
    {
        SolverButtonStyle style = snapshot.Mode != mode
            ? SolverButtonStyle.Secondary
            : mode == SolverMode.FullAuto
                ? snapshot.FullAutoPausedThisCombat
                    ? SolverButtonStyle.Warning
                    : SolverButtonStyle.Positive
                : SolverButtonStyle.Primary;
        SolverUiTokens.ApplyButtonStyle(button, style);
    }

    private static void SetButton(Button button, string text, SolverButtonStyle style,
        bool disabled, string? tooltip, ref string? renderedText,
        ref SolverButtonStyle renderedStyle, ref string? renderedTooltip)
    {
        if (renderedText != text)
        {
            renderedText = text;
            button.Text = text;
        }
        if (renderedStyle != style)
        {
            renderedStyle = style;
            SolverUiTokens.ApplyButtonStyle(button, style);
        }
        if (renderedTooltip != tooltip)
        {
            renderedTooltip = tooltip;
            button.TooltipText = tooltip ?? "";
        }
        button.Disabled = disabled;
    }
}
