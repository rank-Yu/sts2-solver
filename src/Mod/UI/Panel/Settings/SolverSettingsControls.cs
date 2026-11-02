// Adapted from CombatSolver src/UI/SolverSettingsPanel.Controls.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Dark palette only. Group descriptions are not shown; the slider and the dropdowns keep small
// minimum widths and inputs keep their height so the two columns fit the 560-wide panel. The
// strategy sidebar's HP input is a plain text box with an HP suffix instead of the original SpinBox.
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace Sts2Solver;

/// <summary>
/// Control factories for the settings page and the strategy sidebar; validation and saving live in
/// the page.
/// </summary>
internal static class SolverSettingsControls
{
    private const float RowLabelWidth = 170f;
    private const float InputWidth = 126f;
    private const float InputHeight = 32f;
    private const float SliderWidth = 110f;
    private const float HpInputWidth = 80f;

    public static VBoxContainer CreatePageContent(string name)
    {
        VBoxContainer content = new()
        {
            Name = name,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Lg);
        return content;
    }

    public static ScrollContainer CreatePageScroll(Control content)
    {
        ScrollContainer scroll = new()
        {
            Name = "PageScroll",
            CustomMinimumSize = new Vector2(0, 120),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        scroll.AddChild(content);
        return scroll;
    }

    public static GridContainer CreateSettingsGrid()
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

    public static void AddSettingsSection(VBoxContainer page, string title, Control content)
    {
        VBoxContainer section = CreatePageContent("SettingsSection");
        section.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        section.AddChild(CreateSectionHeading(title));
        section.AddChild(content);
        page.AddChild(section);
    }

    public static OptionButton CreateOptionInput()
    {
        OptionButton input = new()
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(InputWidth, InputHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        input.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Background,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Border,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("pressed", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeIconOverride(
            "arrow",
            SolverUiTokens.CreateChevronTexture(SolverUiTokens.Palette.TextSecondary));
        input.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        return input;
    }

    public static LineEdit CreateInput(string placeholder)
    {
        LineEdit input = new()
        {
            PlaceholderText = placeholder,
            ClearButtonEnabled = true,
            SelectAllOnFocus = true,
            CustomMinimumSize = new Vector2(InputWidth, InputHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        input.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeColorOverride("font_placeholder_color", SolverUiTokens.Palette.TextMuted);
        input.AddThemeColorOverride("font_uneditable_color", SolverUiTokens.Palette.TextMuted);
        input.AddThemeColorOverride("caret_color", SolverUiTokens.Palette.Accent);
        input.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Background,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("read_only", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Background,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Border,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("focus", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        input.TextChanged += _ => input.AddThemeColorOverride(
            "font_color",
            SolverUiTokens.Palette.TextPrimary);
        return input;
    }

    /// <summary>A number box with a muted HP suffix; clicking anywhere in the box edits the number.</summary>
    public static (PanelContainer Box, LineEdit Edit) CreateHpInput()
    {
        StyleBoxFlat normal = CreateInputBox(SolverUiTokens.Palette.Background, SolverUiTokens.Palette.BorderSubtle);
        StyleBoxFlat focus = CreateInputBox(SolverUiTokens.Palette.SurfaceRaised, SolverUiTokens.Palette.Accent);
        PanelContainer box = new()
        {
            CustomMinimumSize = new Vector2(HpInputWidth, InputHeight),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Ibeam,
        };
        box.AddThemeStyleboxOverride("panel", normal);
        HBoxContainer row = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        box.AddChild(row);
        LineEdit edit = new()
        {
            ExpandToTextLength = true,
            SelectAllOnFocus = true,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        edit.AddThemeConstantOverride("minimum_character_width", 0);
        StyleBoxEmpty empty = new();
        edit.AddThemeStyleboxOverride("normal", empty);
        edit.AddThemeStyleboxOverride("focus", empty);
        edit.AddThemeStyleboxOverride("read_only", empty);
        edit.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        edit.AddThemeColorOverride("font_uneditable_color", SolverUiTokens.Palette.TextMuted);
        edit.AddThemeColorOverride("caret_color", SolverUiTokens.Palette.Accent);
        edit.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        row.AddChild(edit);
        row.AddChild(SolverUiTokens.CreateLabel("HP", SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextMuted, FontType.Regular));
        box.GuiInput += inputEvent =>
        {
            if (edit.Editable
                && inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                edit.GrabFocus();
            }
        };
        edit.FocusEntered += () => box.AddThemeStyleboxOverride("panel", focus);
        edit.FocusExited += () => box.AddThemeStyleboxOverride("panel", normal);
        return (box, edit);
    }

    /// <summary>The on/off switch of the original settings and growth panels.</summary>
    public static CheckButton CreateToggle()
    {
        CheckButton toggle = new()
        {
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(38, 20),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        Texture2D onIcon = SolverUiTokens.SwitchTexture(true);
        Texture2D offIcon = SolverUiTokens.SwitchTexture(false);
        toggle.AddThemeIconOverride("checked", onIcon);
        toggle.AddThemeIconOverride("unchecked", offIcon);
        toggle.AddThemeIconOverride("checked_disabled", onIcon);
        toggle.AddThemeIconOverride("unchecked_disabled", offIcon);
        StyleBoxEmpty empty = new();
        toggle.AddThemeStyleboxOverride("normal", empty);
        toggle.AddThemeStyleboxOverride("hover", empty);
        toggle.AddThemeStyleboxOverride("pressed", empty);
        toggle.AddThemeStyleboxOverride("hover_pressed", empty);
        toggle.AddThemeStyleboxOverride("focus", empty);
        toggle.AddThemeStyleboxOverride("disabled", empty);
        return toggle;
    }

    private static StyleBoxFlat CreateInputBox(Color background, Color border)
        => SolverUiTokens.CreateBox(background, border, SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm, SolverUiTokens.Spacing.Xs);

    public static HSlider CreateSlider(double minimum, double maximum, double step)
    {
        HSlider slider = new()
        {
            MinValue = minimum,
            MaxValue = maximum,
            Step = step,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(SliderWidth, 24),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Color track = SolverUiTokens.Palette.Border;
        slider.AddThemeStyleboxOverride("slider", SolverUiTokens.CreateBox(
            track,
            track,
            SolverUiTokens.Radius.Pill,
            horizontalPadding: 0,
            verticalPadding: 3,
            borderWidth: 0));
        slider.AddThemeStyleboxOverride("grabber_area", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Pill,
            horizontalPadding: 0,
            verticalPadding: 3,
            borderWidth: 0));
        slider.AddThemeStyleboxOverride("grabber_area_highlight", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.AccentHover,
            SolverUiTokens.Palette.AccentHover,
            SolverUiTokens.Radius.Pill,
            horizontalPadding: 0,
            verticalPadding: 3,
            borderWidth: 0));
        slider.AddThemeConstantOverride("center_grabber", 1);
        slider.AddThemeIconOverride("grabber", SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.Accent, 16));
        slider.AddThemeIconOverride(
            "grabber_highlight",
            SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.AccentHover, 16));
        slider.AddThemeIconOverride(
            "grabber_disabled",
            SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.TextMuted, 16));
        return slider;
    }

    public static void AddBasicRow(GridContainer grid, string label, Control input, string? tooltip = null)
    {
        Label rowLabel = SolverUiTokens.CreateLabel(
            label,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextSecondary);
        rowLabel.CustomMinimumSize = new Vector2(RowLabelWidth, InputHeight);
        rowLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rowLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (!string.IsNullOrEmpty(tooltip))
        {
            ApplyTooltip(rowLabel, tooltip);
            ApplyTooltip(input, tooltip);
        }
        grid.AddChild(rowLabel);
        grid.AddChild(input);
    }

    private static void ApplyTooltip(Control control, string tooltip)
    {
        control.TooltipText = tooltip;
        if (control is Label)
            control.MouseFilter = Control.MouseFilterEnum.Pass;
    }

    public static HBoxContainer CreateSectionHeading(string text)
    {
        HBoxContainer row = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        row.AddChild(SolverUiTokens.CreateLabel(
            text,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary));
        ColorRect divider = new()
        {
            Color = SolverUiTokens.Palette.BorderSubtle,
            CustomMinimumSize = new Vector2(0, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(divider);
        return row;
    }
}
