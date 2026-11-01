// Adapted from CombatSolver src/UI/SolverSettingsPanel.Controls.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Control factories only; validation and saving live in SolverSettingsPanel.
using Godot;

namespace Sts2Solver;

internal static class SolverSettingsControls
{
    public static VBoxContainer CreateSection(string title, string? description)
    {
        VBoxContainer section = new()
        {
            Name = "SettingsSection",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        section.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        HBoxContainer heading = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        heading.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        heading.AddChild(SolverUiTokens.CreateLabel(title, SolverUiTokens.Type.Body, SolverUiTokens.Palette.Accent));
        ColorRect divider = SolverUiTokens.CreateDivider();
        divider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        divider.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        heading.AddChild(divider);
        section.AddChild(heading);
        if (!string.IsNullOrWhiteSpace(description))
            section.AddChild(CreateHint(description));
        return section;
    }

    public static Label CreateHint(string text)
    {
        Label hint = SolverUiTokens.CreateLabel(text, SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextMuted, SolverFontWeight.Normal);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return hint;
    }

    public static GridContainer CreateGrid()
    {
        GridContainer grid = new()
        {
            Columns = 2,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        grid.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Lg);
        grid.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        return grid;
    }

    public static void AddRow(GridContainer grid, string label, Control input, string? tooltip = null)
    {
        Label rowLabel = SolverUiTokens.CreateLabel(label, SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextSecondary);
        rowLabel.CustomMinimumSize = new Vector2(150, 32);
        rowLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rowLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (!string.IsNullOrEmpty(tooltip))
        {
            rowLabel.TooltipText = tooltip;
            rowLabel.MouseFilter = Control.MouseFilterEnum.Pass;
            input.TooltipText = tooltip;
        }
        grid.AddChild(rowLabel);
        grid.AddChild(input);
    }

    public static OptionButton CreateOption()
    {
        OptionButton input = new()
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(200, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        input.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeColorOverride("font_hover_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeColorOverride("font_pressed_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeColorOverride("font_focus_color", SolverUiTokens.Palette.TextPrimary);
        StyleInput(input, "normal", "hover", "pressed", "focus");
        input.AddThemeIconOverride("arrow", SolverUiTokens.CreateChevronTexture(SolverUiTokens.Palette.TextSecondary));
        SolverUiTokens.ApplyFont(input, SolverFontWeight.Strong, "font");
        StylePopup(input.GetPopup());
        return input;
    }

    public static LineEdit CreateLineEdit(string placeholder)
    {
        LineEdit input = new()
        {
            PlaceholderText = placeholder,
            ClearButtonEnabled = true,
            SelectAllOnFocus = true,
            CustomMinimumSize = new Vector2(200, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        input.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeColorOverride("font_uneditable_color", SolverUiTokens.Palette.TextMuted);
        input.AddThemeColorOverride("font_placeholder_color", SolverUiTokens.Palette.TextMuted);
        input.AddThemeColorOverride("caret_color", SolverUiTokens.Palette.Accent);
        input.AddThemeColorOverride("selection_color", SolverUiTokens.Palette.AccentSoft);
        StyleInput(input, "normal", "focus", "read_only");
        input.AddThemeStyleboxOverride("focus", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised, SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Small, SolverUiTokens.Spacing.Sm, SolverUiTokens.Spacing.Xs));
        SolverUiTokens.ApplyFont(input, SolverFontWeight.Input, "font");
        return input;
    }

    public static CheckButton CreateToggle()
    {
        CheckButton toggle = new()
        {
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(38, 24),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
        };
        Texture2D on = SolverUiTokens.CreateSwitchTexture(true);
        Texture2D off = SolverUiTokens.CreateSwitchTexture(false);
        toggle.AddThemeIconOverride("checked", on);
        toggle.AddThemeIconOverride("unchecked", off);
        toggle.AddThemeIconOverride("checked_disabled", on);
        toggle.AddThemeIconOverride("unchecked_disabled", off);
        StyleBoxEmpty empty = new();
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
            toggle.AddThemeStyleboxOverride(state, empty);
        return toggle;
    }

    public static HSlider CreateSlider(double minimum, double maximum, double step)
    {
        HSlider slider = new()
        {
            MinValue = minimum,
            MaxValue = maximum,
            Step = step,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(180, 24),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        Color track = SolverUiTokens.Palette.Border;
        slider.AddThemeStyleboxOverride("slider", SolverUiTokens.CreateBox(
            track, track, 3, horizontalPadding: 0, verticalPadding: 2, borderWidth: 0));
        slider.AddThemeStyleboxOverride("grabber_area", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Accent, SolverUiTokens.Palette.Accent, 3, 0, 2, borderWidth: 0));
        slider.AddThemeStyleboxOverride("grabber_area_highlight", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.AccentHover, SolverUiTokens.Palette.AccentHover, 3, 0, 2, borderWidth: 0));
        slider.AddThemeConstantOverride("center_grabber", 1);
        slider.AddThemeIconOverride("grabber", SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.Accent, 16));
        slider.AddThemeIconOverride("grabber_highlight",
            SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.AccentHover, 16));
        slider.AddThemeIconOverride("grabber_disabled",
            SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.TextMuted, 16));
        return slider;
    }

    private static void StyleInput(Control input, params string[] states)
    {
        foreach (string state in states)
        {
            bool hover = state is "hover" or "pressed";
            input.AddThemeStyleboxOverride(state, SolverUiTokens.CreateBox(
                hover ? SolverUiTokens.Palette.SurfaceHover : SolverUiTokens.Palette.SurfaceRaised,
                hover ? SolverUiTokens.Palette.Border : SolverUiTokens.Palette.BorderSubtle,
                SolverUiTokens.Radius.Small,
                SolverUiTokens.Spacing.Sm,
                SolverUiTokens.Spacing.Xs));
        }
    }

    private static void StylePopup(PopupMenu popup)
    {
        popup.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLight ? Color.FromHtml("fbf7efff") : Color.FromHtml("1a212cff"),
            SolverUiTokens.Palette.Border,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Xs,
            SolverUiTokens.Spacing.Xs));
        popup.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.AccentSoft, Colors.Transparent, SolverUiTokens.Radius.Small, 0, 0, borderWidth: 0));
        popup.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        popup.AddThemeColorOverride("font_hover_color", SolverUiTokens.Palette.TextPrimary);
        popup.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        popup.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        if (SolverUiTokens.ResolveFont(SolverFontWeight.Strong) is { } font)
            popup.AddThemeFontOverride("font", font);
    }
}
