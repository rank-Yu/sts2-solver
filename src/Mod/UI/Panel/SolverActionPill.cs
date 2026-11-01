// Adapted from CombatSolver src/UI/SolverActionPill.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The pill reads Sts2Solver's frozen RouteActionUi (native ids, frozen target/choice/kill names)
// instead of the old overlay snapshot; relic labels and replay counts have no source here and are
// not shown. The layout (type marker, title, target, choice, kills, tooltip) is kept.
using Godot;

namespace Sts2Solver;

internal static class SolverActionPill
{
    internal const string ActionPillName = "ActionPill";

    public static Control Create(RouteActionUi action)
    {
        bool killed = action.Kills.Length > 0;
        Color marker = killed ? SolverUiTokens.Palette.Success : MarkerColor(action);

        PanelContainer pill = new()
        {
            Name = ActionPillName,
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = Tooltip(action),
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            killed ? new Color(SolverUiTokens.Palette.Success, 0.55f) : SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xxs));

        HBoxContainer content = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs + 1);
        content.AddChild(new ColorRect
        {
            Color = marker,
            CustomMinimumSize = new Vector2(3, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        content.AddChild(SolverUiTokens.CreateLabel(
            Title(action),
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary));
        if (!string.IsNullOrEmpty(action.TargetName))
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                "→ " + action.TargetName,
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.TextSecondary,
                SolverFontWeight.Normal));
        }
        if (ChoiceText(action) is { } choice)
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                choice,
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Accent));
        }
        if (killed)
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                SolverText.Format($"击杀 {string.Join(SolverText.Get("、"), action.Kills)}"),
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Success));
        }
        pill.AddChild(content);
        return pill;
    }

    public static Control CreateStatus(string text, Color color, string? tooltip = null)
    {
        PanelContainer pill = new()
        {
            Name = "StatusPill",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = tooltip == null ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass,
            TooltipText = tooltip ?? "",
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xxs));
        pill.AddChild(SolverUiTokens.CreateLabel(text, SolverUiTokens.Type.Caption, color));
        return pill;
    }

    /// <summary>Equality used by loop folding; the global index is deliberately not compared.</summary>
    public static bool HasSamePresentation(RouteActionUi left, RouteActionUi right)
        => left.Kind == right.Kind
            && left.CardId == right.CardId
            && left.Upgrade == right.Upgrade
            && left.PotionId == right.PotionId
            && left.CardType == right.CardType
            && left.TargetCombatId == right.TargetCombatId
            && left.TargetName == right.TargetName
            && left.EndsTurn == right.EndsTurn
            && left.Kills.SequenceEqual(right.Kills)
            && left.Choices.Length == right.Choices.Length
            && left.Choices.Zip(right.Choices).All(static pair => pair.First.SequenceEqual(pair.Second));

    /// <summary>Same displayed content, including the per-action HP numbers shown in the tooltip.</summary>
    public static bool HasSameContent(RouteActionUi left, RouteActionUi right)
        => HasSamePresentation(left, right)
            && left.Index == right.Index
            && left.HpBefore == right.HpBefore
            && left.HpAfter == right.HpAfter
            && left.EnergyBefore == right.EnergyBefore
            && left.EnergyAfter == right.EnergyAfter;

    public static string Title(RouteActionUi action) => action.Kind switch
    {
        "play_card" => action.CardId is { } card
            ? CombatDisplayNames.CardTitle(card, action.Upgrade)
            : SolverText.Get("未知卡牌"),
        "use_potion" => action.PotionId is { } potion
            ? CombatDisplayNames.PotionTitle(potion)
            : SolverText.Get("未知药水"),
        "end_turn" => SolverText.Get("结束回合"),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "未知路线动作类型。"),
    };

    private static string? ChoiceText(RouteActionUi action)
    {
        if (action.Choices.Length == 0)
            return null;
        string separator = SolverText.Get("、");
        string[] layers = action.Choices
            .Select(layer => layer.Length == 0 ? SolverText.Get("不选") : string.Join(separator, layer))
            .ToArray();
        return SolverText.Format($"选 {string.Join(" / ", layers)}");
    }

    private static string Tooltip(RouteActionUi action)
    {
        List<string> lines = [Title(action)];
        if (action.Kind == "use_potion")
            lines[0] += SolverText.Get("（药水）");
        if (!string.IsNullOrEmpty(action.TargetName))
            lines.Add(SolverText.Format($"目标：{action.TargetName}"));
        if (ChoiceText(action) is { } choice)
            lines.Add(choice);
        if (action.Kills.Length > 0)
            lines.Add(SolverText.Format($"击杀：{string.Join(SolverText.Get("、"), action.Kills)}"));
        lines.Add(SolverText.Format($"生命 {action.HpBefore} → {action.HpAfter}"));
        if (!action.EndsTurn)
            lines.Add(SolverText.Format($"能量 {action.EnergyBefore} → {action.EnergyAfter}"));
        return string.Join("\n", lines);
    }

    private static Color MarkerColor(RouteActionUi action)
    {
        if (action.Kind == "use_potion")
            return SolverUiTokens.Palette.Potion;
        if (action.Kind == "end_turn")
            return SolverUiTokens.Palette.TextMuted;
        return action.CardType switch
        {
            "attack" => SolverUiTokens.Palette.Attack,
            "skill" => SolverUiTokens.Palette.Skill,
            "power" => SolverUiTokens.Palette.Power,
            "status" or "curse" => SolverUiTokens.Palette.Negative,
            _ => SolverUiTokens.Palette.Border,
        };
    }
}
