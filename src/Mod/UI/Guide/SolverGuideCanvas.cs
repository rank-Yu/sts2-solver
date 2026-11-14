// Drawing for the in-game guide: highlighted source (hand card, potion, end-turn button), a curved
// arrow to the target creature or the play area, target brackets and highlighted choice cards. All
// geometry is in viewport coordinates taken from the native controls' canvas transforms.
using Godot;

namespace Sts2Solver;

internal enum SolverGuideKind
{
    Attack,
    Skill,
    Power,
    Potion,
    EndTurn,
    Choice,
}

/// <summary>A native control's rectangle in viewport coordinates, rotation and scale included.</summary>
internal readonly record struct SolverGuideRect(Vector2 A, Vector2 B, Vector2 C, Vector2 D)
{
    /// <summary>Top-left, top-right, bottom-right, bottom-left of a visible control with a size.</summary>
    internal static SolverGuideRect? Of(Control? control)
    {
        if (control == null || !GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree())
            return null;
        Vector2 size = control.Size;
        if (size.X <= 1 || size.Y <= 1)
            return null;
        Transform2D transform = control.GetGlobalTransformWithCanvas();
        return new SolverGuideRect(
            transform * Vector2.Zero,
            transform * new Vector2(size.X, 0),
            transform * size,
            transform * new Vector2(0, size.Y));
    }

    internal Vector2 Center => (A + C) / 2;

    internal Vector2 Top => (A + B) / 2;

    internal Vector2 Bottom => (C + D) / 2;

    /// <summary>The same rectangle grown outward by <paramref name="amount"/> pixels on every side.</summary>
    internal SolverGuideRect Grow(float amount)
    {
        Vector2 center = Center;
        return new SolverGuideRect(Out(A), Out(B), Out(C), Out(D));

        Vector2 Out(Vector2 corner)
        {
            Vector2 direction = corner - center;
            float length = direction.Length();
            return length < 0.001f ? corner : corner + direction / length * amount * 1.41f;
        }
    }
}

/// <summary>One frame of guide geometry, rebuilt every frame from the live nodes.</summary>
internal readonly record struct SolverGuideFrame(
    SolverGuideKind Kind,
    SolverGuideRect? Source,
    SolverGuideRect? Target,
    IReadOnlyList<SolverGuideRect> Marks,
    Vector2? DropPoint,
    Vector2 CaptionAnchor,
    string Caption,
    bool CaptionBelow = false);

internal sealed partial class SolverGuideCanvas : Control
{
    private const int CurveSamples = 40;
    private static readonly Color Outline = new(0.03f, 0.04f, 0.05f, 0.85f);

    private readonly Vector2[] _curve = new Vector2[CurveSamples + 1];

    internal SolverGuideFrame? Frame { get; set; }

    public override void _Draw()
    {
        if (Frame is not { } frame)
            return;
        float time = Time.GetTicksMsec() / 1000f;
        float pulse = 0.5f + 0.5f * Mathf.Sin(time * 4.5f);
        Color color = KindColor(frame.Kind);

        if (frame.Source is { } source)
            DrawHighlight(source, color, pulse);
        foreach (SolverGuideRect mark in frame.Marks)
            DrawHighlight(mark, color, pulse);
        if (frame.Target is { } target)
            DrawBrackets(target.Grow(10), color, pulse);

        if (frame.Source is { } from)
        {
            bool fromBelow = frame.Kind == SolverGuideKind.Potion;
            Vector2 start = fromBelow ? from.Bottom : from.Top;
            if (frame.Target is { } to)
                DrawGuideArrow(start, to.Center, color, time, bendDown: fromBelow);
            else if (frame.DropPoint is { } drop)
            {
                DrawGuideArrow(start, drop, color, time, bendDown: false);
                DrawDropRing(drop, color, pulse);
            }
        }
    }

    private static Color KindColor(SolverGuideKind kind) => kind switch
    {
        SolverGuideKind.Attack => SolverUiTokens.Palette.Attack,
        SolverGuideKind.Skill => SolverUiTokens.Palette.Skill,
        SolverGuideKind.Power => SolverUiTokens.Palette.Power,
        SolverGuideKind.Potion => SolverUiTokens.Palette.Potion,
        _ => SolverUiTokens.Palette.Choice,
    };

    private void DrawHighlight(SolverGuideRect rect, Color color, float pulse)
    {
        SolverGuideRect grown = rect.Grow(6);
        Vector2[] loop = [grown.A, grown.B, grown.C, grown.D, grown.A];
        Color glow = color with { A = 0.18f + 0.22f * pulse };
        DrawPolyline(loop, glow, 14f + 6f * pulse, antialiased: true);
        DrawPolyline(loop, Outline, 7f, antialiased: true);
        DrawPolyline(loop, color, 4f, antialiased: true);
    }

    private void DrawBrackets(SolverGuideRect rect, Color color, float pulse)
    {
        Vector2[] corners = [rect.A, rect.B, rect.C, rect.D];
        float scale = 0.22f + 0.04f * pulse;
        for (int index = 0; index < 4; index++)
        {
            Vector2 corner = corners[index];
            Vector2 next = corners[(index + 1) % 4];
            Vector2 previous = corners[(index + 3) % 4];
            Vector2[] bracket =
            [
                corner + (previous - corner) * scale,
                corner,
                corner + (next - corner) * scale,
            ];
            DrawPolyline(bracket, Outline, 10f, antialiased: true);
            DrawPolyline(bracket, color, 6f, antialiased: true);
        }
        Vector2 center = rect.Center;
        float radius = 10f + 4f * pulse;
        DrawCircle(center, radius + 3f, Outline);
        DrawCircle(center, radius, color with { A = 0.85f });
    }

    private void DrawDropRing(Vector2 center, Color color, float pulse)
    {
        float radius = 46f + 8f * pulse;
        DrawArc(center, radius, 0, Mathf.Tau, 48, Outline, 8f, antialiased: true);
        DrawArc(center, radius, 0, Mathf.Tau, 48, color, 4f, antialiased: true);
    }

    /// <summary>A quadratic curve bowed away from the hand, with moving dots and an arrowhead.</summary>
    private void DrawGuideArrow(Vector2 start, Vector2 end, Color color, float time, bool bendDown)
    {
        Vector2 delta = end - start;
        float distance = delta.Length();
        if (distance < 24f)
            return;
        Vector2 middle = (start + end) / 2;
        float bow = Mathf.Clamp(distance * 0.35f, 80f, 260f);
        Vector2 control = middle + new Vector2(0, bendDown ? bow * 0.3f : -bow);
        for (int index = 0; index <= CurveSamples; index++)
        {
            float t = index / (float)CurveSamples;
            float u = 1 - t;
            _curve[index] = u * u * start + 2 * u * t * control + t * t * end;
        }
        // Stop the shaft short of the tip so the head covers it.
        const float headLength = 30f;
        int shaftEnd = CurveSamples;
        while (shaftEnd > 1 && _curve[shaftEnd].DistanceTo(end) < headLength * 0.7f)
            shaftEnd--;
        Vector2[] shaft = _curve[..(shaftEnd + 1)];
        DrawPolyline(shaft, Outline, 13f, antialiased: true);
        DrawPolyline(shaft, color, 7f, antialiased: true);

        for (int dot = 0; dot < 3; dot++)
        {
            float phase = (time * 0.7f + dot / 3f) % 1f;
            int sample = Math.Min(shaftEnd, (int)(phase * shaftEnd));
            DrawCircle(_curve[sample], 4.5f, Colors.White with { A = 0.9f });
        }

        Vector2 direction = (end - _curve[Math.Max(0, shaftEnd - 2)]).Normalized();
        Vector2 normal = new(-direction.Y, direction.X);
        Vector2 baseCenter = end - direction * headLength;
        Vector2[] head =
        [
            end,
            baseCenter + normal * headLength * 0.62f,
            baseCenter - normal * headLength * 0.62f,
        ];
        Vector2[] headOutline = [head[0], head[1], head[2], head[0]];
        DrawPolyline(headOutline, Outline, 6f, antialiased: true);
        DrawColoredPolygon(head, color);
    }
}
