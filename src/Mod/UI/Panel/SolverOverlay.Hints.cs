// Search-limit and significant-damage hints ported from CombatSolver src/UI/SolverOverlay.cs
// (CreatePerformanceHint, SetPerformanceHintVisible) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The novelty portfolio, SpeedX and
// Boss HP hints are not ported.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverOverlay
{
    private const int SignificantBattleHpLossThreshold = 8;
    private Button? _performanceHintButton;

    private Control CreatePerformanceHint()
    {
        _performanceHintButton = SolverUiTokens.CreateButton(
            SolverText.Get("本场战斗出现大战损，若对结果不满意可以前往 设置 > 性能，将性能预设调为高或极高后重试。点击本消息之后不再提示"),
            SolverButtonStyle.Secondary);
        _performanceHintButton.Name = "PerformanceHint";
        _performanceHintButton.Visible = false;
        _performanceHintButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _performanceHintButton.CustomMinimumSize = new Vector2(0, 44);
        _performanceHintButton.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _performanceHintButton.AddThemeStyleboxOverride("normal", CreateNoticeBox());
        _performanceHintButton.AddThemeStyleboxOverride("hover", CreateNoticeBox(hover: true));
        _performanceHintButton.Pressed += DismissPerformanceHint;
        return _performanceHintButton;
    }

    private static StyleBoxFlat CreateNoticeBox(bool hover = false)
        => SolverUiTokens.CreateBox(
            hover ? SolverUiTokens.Palette.SurfaceHover : SolverUiTokens.Palette.SurfaceRaised,
            hover ? SolverUiTokens.Palette.Border : SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs);

    private void DismissPerformanceHint()
    {
        SolverSettings.Update(current => current with
        {
            Ui = current.Ui with { ShowBattleDamagePerformanceHint = false },
        });
        SetPerformanceHintVisible(false);
        ModLog.Info("[Sts2Solver] UI_ACTION action=performance_hint_dismissed");
    }

    private void SetPerformanceHintVisible(bool visible)
    {
        visible &= SolverSettings.Current.Ui.ShowBattleDamagePerformanceHint;
        if (_performanceHintButton == null || _performanceHintButton.Visible == visible)
            return;
        _performanceHintButton.Visible = visible;
        QueueLayout();
    }

    private static bool IsSignificantBattleHpLoss(bool known, int hpLost)
        => known && hpLost >= SignificantBattleHpLossThreshold;
}
