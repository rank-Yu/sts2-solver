// Ported from CombatSolver src/UI/SolverLoopGroup.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Implements ISolverFlowGroup so SolverRouteActionFlow can wrap the loop; the panel is rebuilt on a
// language change, so the badge text is set once per build.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverLoopGroup : PanelContainer, ISolverFlowGroup
{
    private const float ContentMarginH = 5f;
    private const float ExpandMarginV = 2f;

    private readonly PanelContainer _badge;
    private readonly Label _badgeLabel;

    public HFlowContainer Actions { get; } = new()
    {
        Name = "LoopActions",
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
        MouseFilter = MouseFilterEnum.Ignore,
    };

    public PanelContainer Badge => _badge;

    public Label BadgeLabel => _badgeLabel;

    public SolverLoopGroup(SolverActionRun run)
    {
        Name = "LoopGroup";
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = SolverText.Format(
            $"框内 {run.Period} 个动作按顺序重复 {run.Repetitions} 次，共 {run.Count} 个动作");

        StyleBoxFlat box = SolverUiTokens.CreateBox(
            new Color(0.12f, 0.14f, 0.17f, 0.50f),
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            0,
            0);
        box.ContentMarginLeft = ContentMarginH;
        box.ContentMarginRight = ContentMarginH;
        box.ExpandMarginTop = ExpandMarginV;
        box.ExpandMarginBottom = ExpandMarginV;
        AddThemeStyleboxOverride("panel", box);

        Actions.AddThemeConstantOverride("h_separation", 6);
        Actions.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        AddChild(Actions);

        _badge = new PanelContainer
        {
            Name = "LoopBadge",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _badge.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Accent.Darkened(0.70f),
            SolverUiTokens.Palette.Accent.Darkened(0.35f),
            SolverUiTokens.Radius.Small,
            horizontalPadding: 8,
            verticalPadding: 3,
            borderWidth: 1));
        _badgeLabel = SolverUiTokens.CreateLabel(
            SolverText.Format($"循环 ×{run.Repetitions}"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Accent);
        _badgeLabel.Name = "LoopCount";
        _badgeLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _badge.AddChild(_badgeLabel);

        Actions.AddChild(_badge);
        Actions.ChildEnteredTree += child =>
        {
            if (child != _badge)
                Actions.MoveChild(_badge, -1);
        };
    }

    public void SetCompleted(bool completed)
        => SelfModulate = completed ? SolverUiTokens.Palette.CompletedActionModulate : Colors.White;

    public float NaturalWidth
    {
        get
        {
            float contentMargin = ContentMarginH * 2f;
            int visibleCount = 0;
            float itemsWidth = 0;
            foreach (Control child in Actions.GetChildren().OfType<Control>().Where(static c => c.Visible))
            {
                itemsWidth += child.GetCombinedMinimumSize().X;
                visibleCount++;
            }
            float separation = Mathf.Max(0, visibleCount - 1) * Actions.GetThemeConstant("h_separation");
            return contentMargin + itemsWidth + separation;
        }
    }

    public Vector2 GetWrappedDimensions(float maxAvailableWidth)
    {
        float contentMarginH = ContentMarginH * 2f;
        float maxInnerWidth = float.IsPositiveInfinity(maxAvailableWidth)
            ? float.PositiveInfinity
            : Mathf.Max(0, maxAvailableWidth - contentMarginH);
        float hSep = Actions.GetThemeConstant("h_separation");
        float vSep = Actions.GetThemeConstant("v_separation");
        float currentLineWidth = 0;
        float maxLineWidth = 0;
        int lineCount = 1;
        bool hasChildren = false;

        IEnumerable<Control> cards = Actions.GetChildren().OfType<Control>()
            .Where(child => child != _badge && child.Visible);
        foreach (Control child in cards.Append(_badge))
        {
            hasChildren = true;
            float itemWidth = child.GetCombinedMinimumSize().X;
            if (currentLineWidth > 0 && currentLineWidth + hSep + itemWidth > maxInnerWidth)
            {
                maxLineWidth = Mathf.Max(maxLineWidth, currentLineWidth);
                currentLineWidth = itemWidth;
                lineCount++;
            }
            else
            {
                if (currentLineWidth > 0)
                    currentLineWidth += hSep;
                currentLineWidth += itemWidth;
            }
        }

        if (!hasChildren)
            return new Vector2(contentMarginH, SolverUiTokens.Size.ActionPillHeight);

        maxLineWidth = Mathf.Max(maxLineWidth, currentLineWidth);
        float totalWidth = maxLineWidth + contentMarginH + 1f;
        float totalHeight = lineCount * SolverUiTokens.Size.ActionPillHeight + (lineCount - 1) * vSep;
        return new Vector2(totalWidth, totalHeight);
    }

    public override Vector2 _GetMinimumSize()
    {
        float maxItemWidth = Actions.GetChildren().OfType<Control>().Where(static c => c.Visible)
            .Select(static c => c.GetCombinedMinimumSize().X).DefaultIfEmpty(0).Max();
        return new Vector2(maxItemWidth + ContentMarginH * 2f, SolverUiTokens.Size.ActionPillHeight);
    }
}
