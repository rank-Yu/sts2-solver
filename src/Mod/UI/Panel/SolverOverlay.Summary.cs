// Summary section ported from CombatSolver src/UI/SolverOverlay.cs (CreateSummarySection, SetStatus,
// ShowProgress) at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The readout is driven by CombatUiSnapshot instead of SolverProgress.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverOverlay
{
    private PanelContainer? _summaryPanel;
    private PanelContainer? _summaryStatusBadge;
    private Label? _summaryStateLabel;
    private Label? _summaryContextLabel;
    private RichTextLabel? _summaryText;
    private Label? _progressText;
    private Label? _reviewText;
    private PanelContainer? _searchLimitHint;
    private Label? _searchLimitHintLabel;

    private Control CreateSummarySection()
    {
        _summaryPanel = SolverUiTokens.CreateSectionPanel("SummaryPanel");
        _summaryPanel.MouseFilter = Control.MouseFilterEnum.Pass;
        _summaryPanel.CustomMinimumSize = Vector2.Zero;
        VBoxContainer layout = new() { MouseFilter = Control.MouseFilterEnum.Pass };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        HBoxContainer statusRow = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        statusRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        HFlowContainer statisticsRow = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        statisticsRow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        statisticsRow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        _summaryStatusBadge = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 24),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        _summaryStateLabel = SolverUiTokens.CreateLabel(
            SolverText.Get("等待战斗状态"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextMuted);
        _summaryStatusBadge.AddChild(_summaryStateLabel);
        statusRow.AddChild(_summaryStatusBadge);
        _summaryContextLabel = SolverUiTokens.CreateLabel(
            string.Empty,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextSecondary);
        _summaryContextLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _summaryContextLabel.CustomMinimumSize = new Vector2(0, 22);
        _summaryContextLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        _summaryContextLabel.Visible = false;
        statisticsRow.AddChild(_summaryContextLabel);
        _summaryText = SolverUiTokens.CreateRichText(SolverUiTokens.Type.Metric);
        _summaryText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _summaryText.FitContent = true;
        _summaryText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _summaryText.CustomMinimumSize = new Vector2(0, 22);
        _summaryText.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _progressText = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary);
        _progressText.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _progressText.CustomMinimumSize = new Vector2(0, 22);
        _progressText.AutowrapMode = TextServer.AutowrapMode.Off;
        _progressText.Visible = false;
        statisticsRow.AddChild(_progressText);
        _reviewText = SolverUiTokens.CreateLabel(
            string.Empty,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextSecondary);
        _reviewText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _reviewText.CustomMinimumSize = new Vector2(0, 22);
        _reviewText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _reviewText.Visible = false;
        statusRow.AddChild(_summaryText);
        layout.AddChild(statusRow);
        statisticsRow.AddChild(_reviewText);
        layout.AddChild(statisticsRow);
        _summaryPanel.AddChild(layout);
        return _summaryPanel;
    }


    private Control CreateSearchLimitHint()
    {
        _searchLimitHint = new PanelContainer
        {
            Name = "SearchLimitHint",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _searchLimitHint.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _searchLimitHintLabel = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.Warning);
        _searchLimitHintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _searchLimitHintLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _searchLimitHint.AddChild(_searchLimitHintLabel);
        return _searchLimitHint;
    }

    private void ApplySummary(CombatUiSnapshot snapshot, long now)
    {
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        SetSearchLimitHint(LimitWarning(snapshot.Search));
        // Show or hide the elapsed-time readout for the new snapshot at once, not at the next tick.
        TickSummary(now);
    }

    private static string? LimitWarning(SearchUiSnapshot? search) => search?.BoundaryCode switch
    {
        "time" => SolverText.Get(
            "搜索尚未彻底穷尽，已达到当前设置的【时间上限】；现展示目前找到的最佳路线。若想探索更优世界线，可在 设置 > 性能 中提高上限后重新搜索。"),
        "node" => SolverText.Get(
            "搜索尚未彻底穷尽，已达到当前设置的【节点上限】；现展示目前找到的最佳路线。若想探索更优世界线，可在 设置 > 性能 中提高上限后重新搜索。"),
        _ => null,
    };

    private void TickSummary(long now)
    {
        if (_lastSnapshot is { Phase: CombatUiPhase.Searching, Search: { } search })
        {
            double elapsed = Math.Max(0, now - search.StartedAtMilliseconds) / 1000d;
            if (_progressText != null)
            {
                _progressText.Visible = true;
                _progressText.Text = SolverText.Format($"已用 {elapsed:F1} s");
            }
            return;
        }
        if (_progressText != null)
            _progressText.Visible = false;
    }

    private void SetStatus(string text, Color color, string context = "")
    {
        if (_summaryStateLabel == null)
            return;
        _summaryStateLabel.Text = text;
        _summaryStateLabel.AddThemeColorOverride("font_color", color);
        if (_summaryStatusBadge != null)
        {
            _summaryStatusBadge.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
                SolverUiTokens.Palette.SurfaceRaised,
                SolverUiTokens.Palette.BorderSubtle,
                SolverUiTokens.Radius.Small,
                horizontalPadding: SolverUiTokens.Spacing.Sm,
                verticalPadding: 2));
        }
        if (_summaryContextLabel != null)
        {
            _summaryContextLabel.Text = context;
            _summaryContextLabel.Visible = !string.IsNullOrEmpty(context);
        }
    }

    private void SetReviewText(string? text)
    {
        if (_reviewText == null)
            return;
        _reviewText.Visible = !string.IsNullOrEmpty(text);
        _reviewText.Text = text ?? string.Empty;
    }

    private void SetSearchLimitHint(string? text)
    {
        if (_searchLimitHint == null || _searchLimitHintLabel == null)
            return;
        bool visible = !string.IsNullOrEmpty(text);
        string value = text ?? string.Empty;
        if (_searchLimitHint.Visible == visible && _searchLimitHintLabel.Text == value)
            return;
        _searchLimitHint.Visible = visible;
        _searchLimitHintLabel.Text = value;
        QueueLayout();
    }
}
