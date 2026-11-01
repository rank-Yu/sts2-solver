// Adapted from CombatSolver src/UI/SolverUiTokens.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The palettes are redrawn for Sts2Solver (translucent surfaces over one panel background whose
// alpha follows the opacity setting); the spacing scale, box/label/button factories and the
// procedural textures keep the original structure.
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace Sts2Solver;

internal enum SolverButtonStyle
{
    Secondary,
    Primary,
    Positive,
    Danger,
    Ghost,
}

/// <summary>Text weight. CJK languages always use the game's bold serif substitute, as the original UI did.</summary>
internal enum SolverFontWeight
{
    Strong,
    Normal,
    Input,
}

/// <summary>Design tokens and control factories for the combat panel. Main thread only.</summary>
internal static class SolverUiTokens
{
    private const string LatinBoldFontPath = "res://themes/kreon_bold_shared.tres";
    private const string LatinRegularFontPath = "res://themes/kreon_regular_shared.tres";

    private static SolverUiTheme _theme = SolverUiTheme.Dark;
    private static readonly Dictionary<string, Font?> LatinFonts = new(StringComparer.Ordinal);

    internal static SolverUiTheme ActiveTheme => _theme;

    internal static bool IsLight => _theme == SolverUiTheme.Light;

    internal static void Configure(SolverUiTheme theme)
    {
        if (!Enum.IsDefined(theme))
            throw new ArgumentOutOfRangeException(nameof(theme), theme, "未知界面主题。");
        _theme = theme;
    }

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
        public const int Large = 10;
    }

    internal static class Type
    {
        public const int Title = 17;
        public const int Metric = 15;
        public const int Body = 14;
        public const int Caption = 13;
    }

    internal static class Size
    {
        public const float PanelMargin = 24f;
        public const float DefaultWidthShare = 0.46f;
        public const float DefaultMinWidth = 560f;
        public const float DefaultMaxWidth = 800f;
        public const float MinimumWidth = 440f;
        public const float MinimumHeight = 240f;
        public const float CollapsedWidth = 460f;
        public const float AutoMaxHeight = 560f;
        public const float HeaderHeight = 34f;
        public const float RouteRowHeight = 40f;
        public const float ActionPillHeight = 28f;
        public const float TurnColumnWidth = 84f;
        public const float MetricsDamageWidth = 86f;
        public const float MetricsHpWidth = 92f;
        public const float MetricsEnergyWidth = 70f;
        public const float ButtonHeight = 32f;
        public const float RouteViewportMinHeight = 96f;
        public const float RouteViewportAutoMaxHeight = 300f;
        public const float DetailsViewportMaxHeight = 220f;
        public const float ResizeEdgeThickness = 8f;
        public const int ResizeGripSize = 18;
    }

    internal static class Palette
    {
        public static Color PanelBackground => Pick("121822ff", "f6f0e4ff");
        public static Color PanelBorder => Pick("5a4a30d9", "c9b48aff");
        public static Color Surface => Pick("ffffff0a", "0000000a");
        public static Color SurfaceRaised => Pick("ffffff13", "00000012");
        public static Color SurfaceHover => Pick("ffffff21", "0000001e");
        public static Color Border => Pick("ffffff29", "00000026");
        public static Color BorderSubtle => Pick("ffffff15", "00000014");
        public static Color Accent => Pick("e6be6aff", "9a6a14ff");
        public static Color AccentHover => Pick("f2d08aff", "7f560cff");
        public static Color AccentSoft => Pick("e6be6a2e", "9a6a1424");
        public static Color Info => Pick("79aee0ff", "1f6fb5ff");
        public static Color InfoSoft => Pick("79aee02a", "1f6fb51f");
        public static Color TextPrimary => Pick("f3ecdcff", "2a2318ff");
        public static Color TextSecondary => Pick("c9c0adff", "584e3eff");
        public static Color TextMuted => Pick("8f8878ff", "887d69ff");
        public static Color Warning => Pick("e6a24aff", "a5620aff");
        public static Color Danger => Pick("e5675eff", "b83a2fff");
        public static Color Success => Pick("84c784ff", "2e7d32ff");
        public static Color Positive => Pick("3f7a4cff", "2e7d32ff");
        public static Color PositiveHover => Pick("4b8d59ff", "256b29ff");

        public static Color Attack => Pick("d8644fff", "c0392bff");
        public static Color Skill => Pick("5d9ad6ff", "1f6fb5ff");
        public static Color Power => Pick("d9ae52ff", "a87a12ff");
        public static Color Negative => Pick("a07ad0ff", "6b4fa0ff");
        public static Color Potion => Pick("4fbfa8ff", "00796bff");
        public static Color Choice => Pick("f08a3cff", "c4570fff");

        public static Color ProgressBackground => Pick("00000059", "0000001a");
        public static Color Shadow => Pick("00000073", "00000024");

        public static Color CompletedActionModulate => IsLight
            ? new Color(0.72f, 0.72f, 0.72f, 0.55f)
            : new Color(0.58f, 0.6f, 0.64f, 0.5f);

        public static Color ActiveActionModulate => IsLight
            ? new Color(1f, 0.86f, 0.62f, 1f)
            : new Color(1f, 0.9f, 0.55f, 1f);

        public static Color CompletedRowModulate => IsLight
            ? new Color(1f, 1f, 1f, 0.6f)
            : new Color(1f, 1f, 1f, 0.55f);

        public static string Hex(Color color) => "#" + color.ToHtml(false);

        private static Color Pick(string dark, string light) => Color.FromHtml(IsLight ? light : dark);
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
            ShadowSize = shadow ? 10 : 0,
            ShadowOffset = shadow ? new Vector2(0, 3) : Vector2.Zero,
            AntiAliasing = true,
        };
    }

    /// <summary>The panel background; its alpha is the persisted opacity, text stays fully opaque.</summary>
    public static StyleBoxFlat CreatePanelBox(float opacity)
    {
        StyleBoxFlat box = CreateBox(Palette.PanelBackground, Palette.PanelBorder, Radius.Large,
            Spacing.Md, Spacing.Md, shadow: true);
        ApplyPanelOpacity(box, opacity);
        return box;
    }

    public static void ApplyPanelOpacity(StyleBoxFlat box, float opacity)
    {
        float alpha = Math.Clamp(opacity, 0.25f, 1f);
        Color background = Palette.PanelBackground;
        box.BgColor = new Color(background, alpha);
        Color border = Palette.PanelBorder;
        box.BorderColor = new Color(border, border.A * (0.5f + 0.5f * alpha));
        Color shadow = Palette.Shadow;
        box.ShadowColor = new Color(shadow, shadow.A * alpha);
    }

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
        SolverFontWeight weight = SolverFontWeight.Strong)
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
        ApplyFont(label, weight, "font");
        return label;
    }

    public static RichTextLabel CreateRichText(int fontSize)
    {
        RichTextLabel label = new()
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        foreach (string slot in new[]
                 {
                     "normal_font_size", "bold_font_size", "italics_font_size",
                     "bold_italics_font_size", "mono_font_size",
                 })
        {
            label.AddThemeFontSizeOverride(slot, fontSize);
        }
        label.AddThemeColorOverride("default_color", Palette.TextSecondary);
        label.AddThemeConstantOverride("table_h_separation", Spacing.Md);
        label.AddThemeConstantOverride("table_v_separation", Spacing.Xxs);
        ApplyFont(label, SolverFontWeight.Normal, "normal_font");
        ApplyFont(label, SolverFontWeight.Strong, "bold_font");
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
        ApplyFont(button, SolverFontWeight.Strong, "font");
        return button;
    }

    public static void ApplyButtonStyle(Button button, SolverButtonStyle style)
    {
        const int radius = Radius.Medium;
        const int hPad = Spacing.Md;
        const int vPad = Spacing.Xs;
        bool light = IsLight;
        (Color background, Color border, Color hover, Color pressed, Color font, Color hoverFont) = style switch
        {
            SolverButtonStyle.Primary => (
                Palette.AccentSoft,
                new Color(Palette.Accent, 0.7f),
                new Color(Palette.Accent, light ? 0.22f : 0.3f),
                new Color(Palette.Accent, light ? 0.3f : 0.4f),
                Palette.Accent,
                light ? Palette.AccentHover : Palette.AccentHover),
            SolverButtonStyle.Positive => (
                Palette.Positive,
                Palette.Success,
                Palette.PositiveHover,
                Palette.Positive.Darkened(0.18f),
                Colors.White,
                Colors.White),
            SolverButtonStyle.Danger => (
                light ? Palette.Danger : Palette.Danger.Darkened(0.35f),
                Palette.Danger,
                light ? Palette.Danger.Darkened(0.12f) : Palette.Danger.Darkened(0.2f),
                Palette.Danger.Darkened(0.45f),
                Colors.White,
                Colors.White),
            SolverButtonStyle.Ghost => (
                Colors.Transparent,
                Colors.Transparent,
                Palette.SurfaceHover,
                Palette.SurfaceRaised,
                Palette.TextSecondary,
                Palette.TextPrimary),
            _ => (
                Palette.SurfaceRaised,
                Palette.Border,
                Palette.SurfaceHover,
                Palette.Surface,
                Palette.TextPrimary,
                Palette.TextPrimary),
        };
        button.AddThemeStyleboxOverride("normal", CreateBox(background, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("hover", CreateBox(hover, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("pressed", CreateBox(pressed, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("hover_pressed", CreateBox(pressed, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("disabled", CreateBox(
            Palette.Surface, Palette.BorderSubtle, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.AddThemeColorOverride("font_color", font);
        button.AddThemeColorOverride("font_hover_color", hoverFont);
        button.AddThemeColorOverride("font_pressed_color", hoverFont);
        button.AddThemeColorOverride("font_hover_pressed_color", hoverFont);
        button.AddThemeColorOverride("font_focus_color", font);
        button.AddThemeColorOverride("font_disabled_color", Palette.TextMuted);
    }

    /// <summary>
    /// CJK, Russian, Polish, Thai and other substituted languages use the game's own substitute
    /// fonts; Latin languages use the game's Kreon UI font explicitly so a theme switch or a
    /// language change never leaves a stale substitute behind.
    /// </summary>
    public static void ApplyFont(Control control, SolverFontWeight weight, StringName slot)
    {
        if (ResolveFont(weight) is { } font)
            control.AddThemeFontOverride(slot, font);
    }

    public static Font? ResolveFont(SolverFontWeight weight)
    {
        string language = LocManager.Instance?.Language ?? "";
        if (FontManager.NeedsFontSubstitution(language))
        {
            FontType type = weight == SolverFontWeight.Input ? FontType.Regular : FontType.Bold;
            return FontManager.GetSubstituteFont(language, type);
        }
        return LatinFont(weight == SolverFontWeight.Strong ? LatinBoldFontPath : LatinRegularFontPath);
    }

    /// <summary>Latin font lookups so far: (resolved, missing). Used by the headless locale check.</summary>
    internal static (int Resolved, int Missing) LatinFontsForTesting
        => (LatinFonts.Values.Count(static font => font != null), LatinFonts.Values.Count(static font => font == null));

    private static Font? LatinFont(string path)
    {
        if (LatinFonts.TryGetValue(path, out Font? cached))
            return cached;
        Font? font = ResourceLoader.Exists(path)
            ? ResourceLoader.Load<Font>(path, null, ResourceLoader.CacheMode.Reuse)
            : null;
        // A missing game font only changes the face; the default theme font still renders the text.
        if (font == null)
            ModLog.Warn($"[Sts2Solver] UI_FONT_MISSING path={path}");
        LatinFonts[path] = font;
        return font;
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

    public static Texture2D CreateSwitchTexture(bool isChecked)
    {
        const int width = 38;
        const int height = 20;
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        const float radius = height / 2f;
        const float cx1 = radius - 0.5f;
        const float cx2 = width - radius - 0.5f;
        const float cy = radius - 0.5f;
        Color fill = isChecked
            ? Palette.Accent
            : IsLight ? Color.FromHtml("e2d9c6ff") : Color.FromHtml("2a313cff");
        Color border = isChecked
            ? Palette.Accent.Darkened(0.15f)
            : IsLight ? Color.FromHtml("c3b597ff") : Color.FromHtml("434d5cff");
        Color knob = isChecked && !IsLight ? Color.FromHtml("1b1f27ff") : Colors.White;
        float knobX = isChecked ? cx2 : cx1;
        const float knobRadius = 7f;
        for (int y = 0; y < height; y++)
        {
            float dy = y - cy;
            for (int x = 0; x < width; x++)
            {
                float dx = x < cx1 ? x - cx1 : x > cx2 ? x - cx2 : 0f;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance > radius + 0.5f)
                    continue;
                float edgeAlpha = Math.Clamp(radius + 0.5f - distance, 0f, 1f);
                float borderFactor = Math.Clamp(distance - (radius - 1.2f), 0f, 1f);
                Color track = fill.Lerp(border, borderFactor);
                track.A *= edgeAlpha;
                float kdx = x - knobX;
                float kdy = y - cy;
                float knobDistance = MathF.Sqrt(kdx * kdx + kdy * kdy);
                if (knobDistance <= knobRadius + 0.5f)
                {
                    float knobAlpha = Math.Clamp(knobRadius + 0.5f - knobDistance, 0f, 1f);
                    Color shown = track.Lerp(knob, knobAlpha);
                    shown.A = Math.Max(track.A, knobAlpha * edgeAlpha);
                    image.SetPixel(x, y, shown);
                }
                else
                {
                    image.SetPixel(x, y, track);
                }
            }
        }
        return ImageTexture.CreateFromImage(image);
    }
}
