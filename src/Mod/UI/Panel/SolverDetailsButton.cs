// Adapted from CombatSolver src/UI/SolverHudButton.cs (class SolverDetailsButton) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The arrow is drawn as a texture instead of a glyph so it does not depend on the font's coverage.
using Godot;

namespace Sts2Solver;

/// <summary>Flat "details" toggle with an up/down chevron.</summary>
internal sealed partial class SolverDetailsButton : Button
{
    private readonly TextureRect _arrow;
    private readonly Texture2D _down;
    private readonly Texture2D _up;

    public SolverDetailsButton()
    {
        Name = "DetailsButton";
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = new Vector2(96, 26);
        ToggleMode = true;
        Flat = true;
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            AddThemeStyleboxOverride(state, new StyleBoxEmpty());

        HBoxContainer layout = new()
        {
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs + 2);
        layout.AddChild(SolverUiTokens.CreateLabel(
            SolverText.Get("状态详情"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary));
        Image source = SolverUiTokens.CreateChevronTexture(SolverUiTokens.Palette.TextSecondary).GetImage();
        _down = ImageTexture.CreateFromImage(source);
        Image flipped = (Image)source.Duplicate();
        flipped.FlipY();
        _up = ImageTexture.CreateFromImage(flipped);
        _arrow = new TextureRect
        {
            Texture = _down,
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            CustomMinimumSize = new Vector2(10, 10),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layout.AddChild(_arrow);
        AddChild(layout);
    }

    public void SetExpanded(bool expanded)
    {
        SetPressedNoSignal(expanded);
        _arrow.Texture = expanded ? _up : _down;
    }
}
