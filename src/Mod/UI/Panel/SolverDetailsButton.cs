// Ported from CombatSolver src/UI/SolverHudButton.cs (class SolverDetailsButton) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
using Godot;

namespace Sts2Solver;

/// <summary>Flat "details" toggle with the original text chevron.</summary>
internal sealed partial class SolverDetailsButton : Button
{
    private readonly Label _arrowLabel;

    public SolverDetailsButton()
    {
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = new Vector2(90, 26);
        ToggleMode = true;
        SolverUiTokens.ApplyButtonStyle(this, SolverButtonStyle.Secondary);
        Flat = true;
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            AddThemeStyleboxOverride(state, new StyleBoxEmpty());

        HBoxContainer layout = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        layout.AddChild(SolverUiTokens.CreateLabel(
            SolverText.Get("状态详情"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary));
        _arrowLabel = SolverUiTokens.CreateLabel(
            "▾",
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary);
        layout.AddChild(_arrowLabel);
        AddChild(layout);
    }

    public void SetExpanded(bool expanded)
    {
        SetPressedNoSignal(expanded);
        _arrowLabel.Text = expanded ? "▴" : "▾";
    }
}
