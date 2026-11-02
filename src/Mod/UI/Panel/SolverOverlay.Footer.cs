// Footer and command bindings ported from CombatSolver src/UI/SolverOverlay.cs (CreateFooter,
// RefreshControls and the button handlers) at 5c773caa (MIT License, Copyright (c) 2026 Torch1230
// and contributors). The frozen route, the independent full-auto button, the auto-start switch and
// the memory-release button are omitted; the three-position mode lives in the header.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverOverlay
{
    private SolverActionBar? _actionBar;
    private SolverMemoryBar? _memoryBar;
    private Button? _recalculateButton;
    private Button? _stopSearchButton;
    private Button? _adoptRouteButton;
    private Button? _executeButton;
    private SolverMode? _renderedMode;

    private Control CreateFooter()
    {
        _recalculateButton = CreateButton(SolverText.Get("重新搜索"));
        _recalculateButton.Name = "RecalculateButton";
        _recalculateButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _recalculateButton.Pressed += OnRecalculatePressed;

        _stopSearchButton = CreateButton(SolverText.Get("停止搜索"));
        _stopSearchButton.Name = "StopSearchButton";
        SolverUiTokens.ApplyButtonStyle(_stopSearchButton, SolverButtonStyle.Danger);
        _stopSearchButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _stopSearchButton.Pressed += OnStopSearchPressed;

        _adoptRouteButton = CreateButton(SolverText.Get("采用当前路线"));
        _adoptRouteButton.Name = "AdoptRouteButton";
        _adoptRouteButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _adoptRouteButton.Pressed += OnAdoptRoutePressed;
        _adoptRouteButton.TooltipText = SolverText.Get(
            "结束搜索并采用屏幕当前路线；已开启的全自动或排队执行仍按原设置继续。");

        _executeButton = CreateButton(SolverText.Get("执行本回合"));
        _executeButton.Name = "ExecuteButton";
        _executeButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _executeButton.Pressed += OnExecutePressed;

        _memoryBar = new SolverMemoryBar { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _actionBar = new SolverActionBar(_executeButton, _recalculateButton, _stopSearchButton,
            _adoptRouteButton, _memoryBar);
        return _actionBar;
    }

    private static Button CreateButton(string text)
        => SolverUiTokens.CreateButton(text, SolverButtonStyle.Secondary);

    private void OnRecalculatePressed()
    {
        bool accepted = CombatUiCommands.Instance.StartOrRecalculateSearch();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=recalculate accepted={accepted}");
    }

    private void OnStopSearchPressed()
    {
        bool accepted = CombatUiCommands.Instance.StopSearch();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=stop_search accepted={accepted}");
    }

    private void OnAdoptRoutePressed()
    {
        bool accepted = CombatUiCommands.Instance.AdoptCurrentRoute();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=adopt accepted={accepted}");
    }

    private void OnExecutePressed()
    {
        bool accepted = CombatUiCommands.Instance.ExecuteOrApplyCurrentTurn();
        ModLog.Info($"[Sts2Solver] UI_ACTION action=deploy accepted={accepted}");
    }

    private void RefreshControls(CombatUiSnapshot snapshot)
    {
        if (_recalculateButton == null || _stopSearchButton == null || _adoptRouteButton == null
            || _executeButton == null || _actionBar == null)
        {
            return;
        }
        bool solverDisabled = snapshot.SolverDisabled;
        if (_solverEnabledButton != null)
        {
            _solverEnabledButton.Text = SolverText.Get(solverDisabled ? "求解器：关" : "求解器：开");
            _solverEnabledButton.AddThemeColorOverride("font_color",
                solverDisabled ? SolverUiTokens.Palette.TextMuted : SolverUiTokens.Palette.TextPrimary);
        }
        if (_modeButton != null && _renderedMode != snapshot.Mode)
        {
            _renderedMode = snapshot.Mode;
            _modeButton.Text = SolverPanelText.Mode(snapshot.Mode);
            SolverUiTokens.ApplyButtonStyle(_modeButton,
                snapshot.Mode == SolverMode.FullAuto ? SolverButtonStyle.Positive
                    : SolverButtonStyle.Secondary);
        }
        bool searching = snapshot.IsSearching;
        bool adopting = snapshot.IsAdoptingRoute;
        _recalculateButton.Text = !snapshot.AutomaticCalculationEnabled && snapshot.Searches == 0
            ? SolverText.Get("开始搜索")
            : SolverText.Get("重新搜索");
        _recalculateButton.Disabled = solverDisabled || searching || snapshot.IsDeploying || adopting;
        _stopSearchButton.Disabled = solverDisabled || !searching
            || snapshot.Search?.Phase == SearchUiPhase.Stopping;
        _adoptRouteButton.Disabled = solverDisabled || !snapshot.CanAdoptCurrentRoute || adopting;
        _adoptRouteButton.Text = adopting
            ? SolverText.Get("正在采用…")
            : SolverText.Get("采用当前路线");
        _executeButton.Disabled = solverDisabled
            || snapshot.IsDeploying
            || snapshot.IsApplyingCurrentTurn
            || adopting
            || searching && !snapshot.CanApplyCurrentTurn
            || !searching && !snapshot.CanExecuteCurrentTurn;
        _executeButton.Text = snapshot.IsDeploying
            ? SolverText.Get("执行中…")
            : snapshot.IsApplyingCurrentTurn
                ? SolverText.Get("正在应用…")
                : searching
                    ? SolverText.Get("应用当前回合")
                    : snapshot.DeployWhenReady
                        ? SolverText.Get("已排队执行")
                        : _presentation == SolverOverlayPresentation.ExecutedHistory
                            && _waitingForNextTurnPlan
                            ? SolverText.Get("等待下一回合")
                            : SolverText.Get("执行本回合");
        _executeButton.TooltipText = SolverText.Get(solverDisabled
            ? "求解器已关闭，请从标题栏开启。"
            : adopting
                ? "正在采用路线，请等待完成。"
                : snapshot.IsDeploying
                    ? "正在执行当前回合。"
                    : _executeButton.Disabled
                        ? "当前没有可执行的回合，请等待搜索或完成当前选牌。"
                        : "执行当前回合的动作。");
        _actionBar.Refresh(new SolverActionBarState(_collapsed, searching,
            snapshot.CanAdoptCurrentRoute || adopting));
        _memoryBar?.Apply(snapshot.Memory, snapshot.Search);
    }
}
