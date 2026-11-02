// Adapted from CombatSolver src/UI/SolverUiTokens.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The palettes are redrawn for Sts2Solver (translucent surfaces over one panel background whose
// alpha follows the opacity setting); the spacing scale, box/label/button factories and the
// procedural textures keep the original structure. The switch texture keeps only the dark colors.
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace Sts2Solver;

internal enum SolverButtonStyle
{
    Secondary,
    Primary,
    Positive,
    Warning,
    Danger,
}

/// <summary>Design tokens and control factories for the combat panel. Main thread only.</summary>
internal static class SolverUiTokens
{
    internal static class Spacing
    {
        public const int Xxs = 2;
        public const int Xs = 4;
        public const int Sm = 8;
        public const int Md = 12;
        public const int Lg = 16;
    }

    internal static class Radius
    {
        public const int Small = 4;
        public const int Medium = 6;
        public const int Pill = 6;
        public const int Large = 8;
    }

    internal static class Type
    {
        public const int Title = 16;
        public const int Metric = 15;
        public const int Body = 14;
        public const int Caption = 13;
        public const int Outline = 0;
    }

    internal static class Size
    {
        public const float PanelMargin = 24f;
        public const float ExpandedMaxWidth = 820f;
        public const float ExpandedMaxHeight = 440f;
        public const float SettingsMaxHeight = 540f;
        public const float ExpandedMinWidth = 560f;
        public const float CollapsedWidth = 520f;
        public const float CollapsedHeight = 120f;
        public const float RouteViewportHeight = 148f;
        public const float RouteViewportHeightWithDetails = 96f;
        public const float RouteRowHeight = 44f;
        public const float ActionPillHeight = 28f;
        public const float TurnColumnWidth = 88f;
        public const float OutcomeColumnWidth = 238f;
        public const float MetricsDamageWidth = 92f;
        public const float MetricsHpWidth = 64f;
        public const float MetricsEnergyWidth = 52f;
        public const float ButtonHeight = 34f;
        public const float ResizeEdgeThickness = 8f;
        public const int ResizeGripSize = 20;
    }

    internal static class Palette
    {
        public static Color Background => Color.FromHtml("101216f5");
        public static Color Surface => Color.FromHtml("191c22fa");
        public static Color SurfaceRaised => Color.FromHtml("23272ffb");
        public static Color SurfaceHover => Color.FromHtml("2b3039ff");
        public static Color Border => Color.FromHtml("3a404aeb");
        public static Color BorderSubtle => Color.FromHtml("2a2f38cc");
        public static Color Accent => Color.FromHtml("5c9fc7ff");
        public static Color AccentHover => Color.FromHtml("73b4d8ff");
        public static Color TextPrimary => Color.FromHtml("eef1f6ff");
        public static Color TextSecondary => Color.FromHtml("b8c0ccff");
        public static Color TextMuted => Color.FromHtml("858f9fff");
        public static Color TextOutline => Color.FromHtml("080a0de6");
        public static Color Warning => Color.FromHtml("d6a34cff");
        public static Color Danger => Color.FromHtml("e26666ff");
        public static Color Success => Color.FromHtml("69b77dff");
        public static Color Positive => Color.FromHtml("34764fff");
        public static Color PositiveHover => Color.FromHtml("428d60ff");

        public static Color Attack => Color.FromHtml("d96363ff");
        public static Color Skill => Color.FromHtml("5b91d1ff");
        public static Color Power => Color.FromHtml("d7a84fff");
        public static Color Negative => Color.FromHtml("9b70c9ff");
        public static Color Potion => Color.FromHtml("55b9a5ff");
        public static Color Choice => Color.FromHtml("ff8533ff");
        public static Color ProgressBackground => Background;
        public static Color ProgressFill => Accent.Darkened(0.12f);
        public static Color CompletedActionModulate => new(0.54f, 0.58f, 0.66f, 0.52f);
        public static Color ActiveActionModulate => new(1f, 0.88f, 0.48f, 1f);
        public static Color CompletedRowModulate => new(1f, 1f, 1f, 0.55f);
        public static Color PanelBackground => Background;
        public static Color PanelBorder => Border;
        public static Color Shadow => new(0f, 0f, 0f, 0.38f);

        public static string TextSecondaryHex => "#b8c0cc";
        public static string TextMutedHex => "#858f9f";
        public static string AccentHex => "#5c9fc7";
        public static string WarningHex => "#d6a34c";
        public static string DangerHex => "#e26666";
        public static string SuccessHex => "#69b77d";
        public static string Hex(Color color) => "#" + color.ToHtml(false);
    }

    public static StyleBoxFlat CreateBox(
        Color background,
        Color border,
        int radius = Radius.Medium,
        int horizontalPadding = Spacing.Sm,
        int verticalPadding = Spacing.Sm,
        int borderWidth = 1,
        bool shadow = false)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomRight = radius,
            CornerRadiusBottomLeft = radius,
            ContentMarginLeft = horizontalPadding,
            ContentMarginTop = verticalPadding,
            ContentMarginRight = horizontalPadding,
            ContentMarginBottom = verticalPadding,
            ShadowColor = shadow ? Palette.Shadow : Colors.Transparent,
            ShadowSize = shadow ? 8 : 0,
            ShadowOffset = shadow ? new Vector2(0, 3) : Vector2.Zero,
            AntiAliasing = true,
        };
    }

    /// <summary>The panel background; its alpha is the persisted opacity, text stays fully opaque.</summary>
    public static StyleBoxFlat CreatePanelBox()
        => CreateBox(Palette.PanelBackground, Palette.PanelBorder, Radius.Large,
            Spacing.Md, Spacing.Md, shadow: true);

    public static PanelContainer CreateSectionPanel(string name, bool raised = false)
    {
        PanelContainer panel = new()
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", CreateBox(
            raised ? Palette.SurfaceRaised : Palette.Surface,
            Palette.BorderSubtle,
            Radius.Medium,
            Spacing.Sm,
            Spacing.Sm));
        return panel;
    }

    public static ColorRect CreateDivider() => new()
    {
        Color = Palette.BorderSubtle,
        CustomMinimumSize = new Vector2(0, 1),
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    public static Label CreateLabel(
        string text,
        int fontSize,
        Color color,
        FontType fontType = FontType.Bold)
    {
        Label label = new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.ApplyLocaleFontSubstitution(fontType, "font");
        return label;
    }

    public static RichTextLabel CreateRichText(int fontSize)
    {
        RichTextLabel label = new()
        {
            BbcodeEnabled = true,
            FitContent = false,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("normal_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_font_size", fontSize);
        label.AddThemeFontSizeOverride("italics_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_italics_font_size", fontSize);
        label.AddThemeFontSizeOverride("mono_font_size", fontSize);
        label.AddThemeColorOverride("default_color", Palette.TextPrimary);
        label.ApplyLocaleFontSubstitution(FontType.Bold, "normal_font");
        label.ApplyLocaleFontSubstitution(FontType.Bold, "bold_font");
        label.ApplyLocaleFontSubstitution(FontType.Italic, "italics_font");
        return label;
    }

    public static Button CreateButton(string text, SolverButtonStyle style)
    {
        Button button = new()
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(0, Size.ButtonHeight),
        };
        button.AddThemeFontSizeOverride("font_size", Type.Body);
        ApplyButtonStyle(button, style);
        button.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        return button;
    }

    public static void ApplyButtonStyle(Button button, SolverButtonStyle style)
    {
        const int radius = Radius.Medium;
        const int hPad = Spacing.Sm;
        const int vPad = Spacing.Xs;
        (Color background, Color border, Color hover, Color pressed) = style switch
        {
            SolverButtonStyle.Primary => (
                Palette.Accent.Darkened(0.12f),
                Palette.Accent.Lightened(0.08f),
                Palette.AccentHover,
                Palette.Accent.Darkened(0.24f)),
            SolverButtonStyle.Positive => (
                Palette.Positive,
                Palette.Success,
                Palette.PositiveHover,
                Palette.Positive.Darkened(0.18f)),
            SolverButtonStyle.Warning => (
                Palette.Warning.Darkened(0.28f),
                Palette.Warning,
                Palette.Warning.Lightened(0.08f),
                Palette.Warning.Darkened(0.38f)),
            SolverButtonStyle.Danger => (
                Palette.Danger.Darkened(0.22f),
                Palette.Danger,
                Palette.Danger.Lightened(0.08f),
                Palette.Danger.Darkened(0.32f)),
            _ => (
                Palette.SurfaceRaised,
                Palette.BorderSubtle,
                Palette.SurfaceHover,
                Palette.Surface),
        };
        button.AddThemeStyleboxOverride("normal", CreateBox(background, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("hover", CreateBox(hover, border.Lightened(0.12f), radius, hPad, vPad));
        button.AddThemeStyleboxOverride("pressed", CreateBox(pressed, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("hover_pressed", CreateBox(pressed, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("disabled", CreateBox(
            Palette.Background, Palette.BorderSubtle, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("focus", CreateBox(hover, Palette.Accent, radius, hPad, vPad));
        button.AddThemeColorOverride("font_color", Palette.TextPrimary);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeColorOverride("font_hover_pressed_color", Colors.White);
        button.AddThemeColorOverride("font_disabled_color", Palette.TextMuted);
    }

    /// <summary>
    /// Fixes the button's width to its widest label so swapping labels never moves the layout. The
    /// button must already be in the tree, where its theme font is resolved.
    /// </summary>
    public static void FitWidestText(Button button, params string[] texts)
    {
        string shown = button.Text;
        float width = button.CustomMinimumSize.X;
        foreach (string text in texts)
        {
            button.Text = text;
            width = Math.Max(width, button.GetMinimumSize().X);
        }
        button.Text = shown;
        button.CustomMinimumSize = new Vector2(width, button.CustomMinimumSize.Y);
    }

    /// <summary>Panel font override count, used by the headless locale check.</summary>
    internal static int FontOverrideCount(Control root)
    {
        int count = 0;
        foreach (Node child in root.GetChildren())
        {
            if (child is Control control)
            {
                if (control.HasThemeFontOverride("font")
                    || control.HasThemeFontOverride("normal_font")
                    || control.HasThemeFontOverride("bold_font")
                    || control.HasThemeFontOverride("italics_font"))
                    count++;
                count += FontOverrideCount(control);
            }
        }
        return count;
    }

    public static Texture2D CreateCircleTexture(Color color, int size = 12)
    {
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float center = (size - 1) / 2f;
        float radius = size / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                float alpha = Math.Clamp(radius - distance, 0f, 1f);
                if (alpha > 0f)
                    image.SetPixel(x, y, new Color(color, color.A * alpha));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    public static Texture2D CreateChevronTexture(Color color, int width = 10, int height = 5)
    {
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        int half = width / 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x >= half - 1 - y && x <= half + y)
                    image.SetPixel(x, height - 1 - y, color);
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D? _switchOn;
    private static Texture2D? _switchOff;

    public static Texture2D SwitchTexture(bool isChecked)
        => isChecked
            ? _switchOn ??= CreateSwitchTexture(true)
            : _switchOff ??= CreateSwitchTexture(false);

    private static Texture2D CreateSwitchTexture(bool isChecked)
    {
        const int width = 38;
        const int height = 20;
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        const float radius = height / 2f;
        const float cx1 = radius - 0.5f;
        const float cx2 = width - radius - 0.5f;
        const float cy = radius - 0.5f;

        Color fill = isChecked ? Color.FromHtml("2d7bd4ff") : Color.FromHtml("222832ff");
        Color border = isChecked ? Color.FromHtml("458de6ff") : Color.FromHtml("3c4656ff");

        float knobX = isChecked ? cx2 : cx1;
        float knobY = cy;
        const float knobRadius = 7.0f;

        for (int y = 0; y < height; y++)
        {
            float dy = y - cy;
            for (int x = 0; x < width; x++)
            {
                float dx = x < cx1 ? x - cx1 : (x > cx2 ? x - cx2 : 0f);
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > radius + 0.5f)
                    continue;

                float edgeAlpha = Math.Clamp(radius + 0.5f - dist, 0f, 1f);
                float borderFactor = Math.Clamp(dist - (radius - 1.2f), 0f, 1f);
                Color track = fill.Lerp(border, borderFactor);
                track.A *= edgeAlpha;

                float kdx = x - knobX;
                float kdy = y - knobY;
                float kdist = MathF.Sqrt(kdx * kdx + kdy * kdy);

                if (kdist <= knobRadius + 0.5f)
                {
                    float knobAlpha = Math.Clamp(knobRadius + 0.5f - kdist, 0f, 1f);
                    Color finalColor = track.Lerp(Colors.White, knobAlpha);
                    finalColor.A = Math.Max(track.A, knobAlpha * edgeAlpha);
                    image.SetPixel(x, y, finalColor);
                }
                else
                {
                    image.SetPixel(x, y, track);
                }
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    public static Texture2D CreateResizeGripTexture(Color color)
    {
        const int size = Size.ResizeGripSize;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        foreach (int length in new[] { 4, 8, 12 })
        {
            for (int index = 0; index < length; index++)
            {
                int x = size - 3 - index;
                int y = size - 3 - (length - 1 - index);
                image.SetPixel(x, y, color);
                if (x > 0)
                    image.SetPixel(x - 1, y, color);
            }
        }
        return ImageTexture.CreateFromImage(image);
    }
}
