// Ported from CombatSolver src/UI/SolverActionPill.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The action text is already resolved from stable ids by SolverOverlaySnapshot on the UI thread;
// the pill keeps the original type marker, title, target, choice, relic, kill and replay layout.
using Godot;

namespace Sts2Solver;

internal static class SolverActionPill
{
    public static Control Create(SolverOverlayActionSnapshot action)
    {
        bool killed = action.Kills.Count > 0;
        Color marker = killed ? SolverUiTokens.Palette.Success : ActionColor(action.VisualKind);

        PanelContainer pill = new()
        {
            Name = "ActionPill",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = action.Tooltip,
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            killed ? new Color(SolverUiTokens.Palette.Success, 0.55f) : SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        HBoxContainer content = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        content.AddChild(new ColorRect
        {
            Color = marker,
            CustomMinimumSize = new Vector2(3, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        content.AddChild(SolverUiTokens.CreateLabel(
            action.Title,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary));
        if (action.ReplayCount > 0)
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                SolverText.Format($"重放×{action.ReplayCount}"),
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Warning));
        }
        if (!string.IsNullOrEmpty(action.TargetName))
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                $"➔  {action.TargetName}",
                SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.TextPrimary));
        }
        if (action.ChoiceText != null)
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                action.ChoiceText,
                SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.Accent));
        }
        if (action.RelicLabels.Count > 0)
        {
            const int maxVisibleRelics = 2;
            for (int index = 0; index < Math.Min(action.RelicLabels.Count, maxVisibleRelics); index++)
            {
                content.AddChild(SolverUiTokens.CreateLabel(
                    action.RelicLabels[index],
                    SolverUiTokens.Type.Caption,
                    SolverUiTokens.Palette.Warning));
            }
            if (action.RelicLabels.Count > maxVisibleRelics)
            {
                content.AddChild(SolverUiTokens.CreateLabel(
                    $"+{action.RelicLabels.Count - maxVisibleRelics}",
                    SolverUiTokens.Type.Caption,
                    SolverUiTokens.Palette.Warning));
            }
        }
        if (killed)
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                SolverText.Format($"击杀：{string.Join(SolverText.Get("、"), action.Kills)}"),
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Success));
        }
        pill.AddChild(content);
        return pill;
    }

    public static Control CreateChoice(string text)
    {
        PanelContainer pill = new()
        {
            Name = "ChoicePill",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = text,
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        HBoxContainer content = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        content.AddChild(new ColorRect
        {
            Color = SolverUiTokens.Palette.Choice,
            CustomMinimumSize = new Vector2(3, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        content.AddChild(SolverUiTokens.CreateLabel(
            text,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary));
        pill.AddChild(content);
        return pill;
    }

    public static Control CreateStatus(string text, Color color)
    {
        PanelContainer pill = new()
        {
            Name = "StatusPill",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        pill.AddChild(SolverUiTokens.CreateLabel(text, SolverUiTokens.Type.Body, color));
        return pill;
    }

    public static SolverLoopGroup CreateCycle(SolverActionRun run, out HFlowContainer actions)
    {
        SolverLoopGroup group = new(run);
        actions = group.Actions;
        return group;
    }

    private static Color ActionColor(SolverOverlayActionVisualKind kind)
        => kind switch
        {
            SolverOverlayActionVisualKind.Attack => SolverUiTokens.Palette.Attack,
            SolverOverlayActionVisualKind.Skill => SolverUiTokens.Palette.Skill,
            SolverOverlayActionVisualKind.Power => SolverUiTokens.Palette.Power,
            SolverOverlayActionVisualKind.Negative => SolverUiTokens.Palette.Negative,
            SolverOverlayActionVisualKind.Potion => SolverUiTokens.Palette.Potion,
            _ => SolverUiTokens.Palette.Border,
        };
}
