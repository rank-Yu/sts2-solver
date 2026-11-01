// Adapted from CombatSolver src/UI/SolverLoopGroup.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Implements ISolverFlowGroup so the ported SolverRouteActionFlow can wrap it; the badge text is
// set once per build because the whole panel is rebuilt on a language change.
using Godot;

namespace Sts2Solver;

internal sealed partial class SolverLoopGroup : PanelContainer, ISolverFlowGroup
{
    private const float ContentMarginH = 5f;
    private const float ExpandMarginV = 2f;

    private readonly PanelContainer _badge;

    public HFlowContainer Actions { get; } = new()
    {
        Name = "LoopActions",
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
        MouseFilter = MouseFilterEnum.Ignore,
    };

    public PanelContainer Badge => _badge;

    public SolverLoopGroup(SolverActionRun run)
    {
        Name = "LoopGroup";
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = SolverText.Format(
            $"框内 {run.Period} 个动作按顺序重复 {run.Repetitions} 次，共 {run.Count} 个动作");

        StyleBoxFlat box = SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
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
            SolverUiTokens.Palette.AccentSoft,
            new Color(SolverUiTokens.Palette.Accent, 0.45f),
            SolverUiTokens.Radius.Small,
            horizontalPadding: 8,
            verticalPadding: 2));
        Label badgeLabel = SolverUiTokens.CreateLabel(
            SolverText.Format($"循环 ×{run.Repetitions}"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Accent);
        badgeLabel.Name = "LoopCount";
        _badge.AddChild(badgeLabel);

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
            int visibleCount = 0;
            float itemsWidth = 0;
            foreach (Control child in Actions.GetChildren().OfType<Control>().Where(static c => c.Visible))
            {
                itemsWidth += child.GetCombinedMinimumSize().X;
                visibleCount++;
            }
            float separation = Mathf.Max(0, visibleCount - 1) * Actions.GetThemeConstant("h_separation");
            return ContentMarginH * 2f + itemsWidth + separation;
        }
    }

    public Vector2 GetWrappedDimensions(float availableWidth)
    {
        float margin = ContentMarginH * 2f;
        float maxInnerWidth = float.IsPositiveInfinity(availableWidth)
            ? float.PositiveInfinity
            : Mathf.Max(0, availableWidth - margin);
        float hSep = Actions.GetThemeConstant("h_separation");
        float vSep = Actions.GetThemeConstant("v_separation");
        float currentLineWidth = 0;
        float maxLineWidth = 0;
        int lineCount = 1;
        bool hasChildren = false;
        IEnumerable<Control> items = Actions.GetChildren().OfType<Control>()
            .Where(child => child != _badge && child.Visible);
        foreach (Control child in items.Append(_badge))
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
            return new Vector2(margin, SolverUiTokens.Size.ActionPillHeight);
        maxLineWidth = Mathf.Max(maxLineWidth, currentLineWidth);
        return new Vector2(
            maxLineWidth + margin + 1f,
            lineCount * SolverUiTokens.Size.ActionPillHeight + (lineCount - 1) * vSep);
    }

    public override Vector2 _GetMinimumSize()
    {
        float maxItemWidth = Actions.GetChildren().OfType<Control>().Where(static c => c.Visible)
            .Select(static c => c.GetCombinedMinimumSize().X).DefaultIfEmpty(0).Max();
        return new Vector2(maxItemWidth + ContentMarginH * 2f, SolverUiTokens.Size.ActionPillHeight);
    }
}
