// Search-limit and significant-damage hints ported from CombatSolver src/UI/SolverOverlay.cs
// (CreatePerformanceHint, SetPerformanceHintVisible) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The novelty portfolio, SpeedX and
// Boss HP hints are not ported. The original theft buttons in the footer (保牌/保钱, 放走) become a
// notice bar under the header with one button that switches this combat's theft policy. The
// significant-damage hint and the theft bar are hidden while the settings page is shown.
using Godot;
using Sts2Solver.Import;

namespace Sts2Solver;

internal sealed partial class SolverOverlay
{
    private const int SignificantBattleHpLossThreshold = 8;
    private Button? _performanceHintButton;
    private bool _performanceHintWanted;
    private PanelContainer? _theftNotice;
    private Label? _theftNoticeLabel;
    private Button? _theftNoticeButton;
    private bool _theftApplicable;

    private Control CreateTheftNotice()
    {
        _theftApplicable = false;
        _theftNotice = new PanelContainer
        {
            Name = "TheftNotice",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _theftNotice.AddThemeStyleboxOverride("panel", CreateNoticeBox());
        HBoxContainer row = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        _theftNoticeLabel = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary);
        _theftNoticeLabel.Name = "TheftNoticeText";
        _theftNoticeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _theftNoticeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(_theftNoticeLabel);
        _theftNoticeButton = SolverUiTokens.CreateButton(string.Empty, SolverButtonStyle.Secondary);
        _theftNoticeButton.Name = "TheftNoticeButton";
        _theftNoticeButton.CustomMinimumSize = new Vector2(80, SolverUiTokens.Size.ButtonHeight);
        _theftNoticeButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _theftNoticeButton.Pressed += ToggleTheftPolicy;
        row.AddChild(_theftNoticeButton);
        _theftNotice.AddChild(row);
        return _theftNotice;
    }

    /// <summary>Shows the theft bar in fights with a thief; the backend resets the policy every combat.</summary>
    private void ApplyTheftNotice(StrategyUiSnapshot strategy)
    {
        _theftApplicable = strategy.TheftApplicable;
        if (_theftApplicable && _theftNoticeLabel != null && _theftNoticeButton != null)
        {
            (string text, string button) = strategy.TheftPolicy switch
            {
                "preserve_resources" => (SolverText.Get("本场有盗贼：保住被偷的牌和金币"),
                    SolverText.Get("改为放走")),
                "let_escape" => (SolverText.Get("本场有盗贼：放走，不追回被偷的牌和金币"),
                    SolverText.Get("改为保住")),
                _ => throw new InvalidDataException($"未知的盗贼策略码：{strategy.TheftPolicy ?? "null"}"),
            };
            _theftNoticeLabel.Text = text;
            _theftNoticeButton.Text = button;
        }
        ApplyNoticeVisibility();
    }

    private void ToggleTheftPolicy()
    {
        SearchTheftPolicy next = _lastSnapshot?.Strategy.TheftPolicy == "let_escape"
            ? SearchTheftPolicy.PreserveResources
            : SearchTheftPolicy.LetEscape;
        bool accepted = CombatUiCommands.Instance.SetTheftPolicy(next);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=theft_policy policy={next} accepted={accepted}");
    }

    /// <summary>The settings page takes the panel body; notices about the route wait until it closes.</summary>
    private void ApplyNoticeVisibility()
    {
        bool settingsShown = _settingsVisible && !_collapsed;
        bool changed = false;
        bool theft = _theftApplicable && !settingsShown;
        if (_theftNotice != null && _theftNotice.Visible != theft)
        {
            _theftNotice.Visible = theft;
            changed = true;
        }
        bool hint = _performanceHintWanted && SolverSettings.Current.Ui.ShowBattleDamagePerformanceHint
            && !settingsShown;
        if (_performanceHintButton != null && _performanceHintButton.Visible != hint)
        {
            _performanceHintButton.Visible = hint;
            changed = true;
        }
        if (changed)
            QueueLayout();
    }

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
        _performanceHintWanted = visible;
        ApplyNoticeVisibility();
    }

    private static bool IsSignificantBattleHpLoss(bool known, int hpLost)
        => known && hpLost >= SignificantBattleHpLossThreshold;
}
