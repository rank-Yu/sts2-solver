// Ported from CombatSolver src/UI/SolverMemoryUsageBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The NoGC/Server GC states and the
// cleanup pulse are not ported; the original label, segment bar and eased motion remain. The bar
// reads the controller's 4 Hz sample and the search ledger peak, never the process RSS as a limit.
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace Sts2Solver;

internal sealed partial class SolverMemoryBar : PanelContainer
{
    private const double Gigabyte = 1_000_000_000d;

    private readonly Label _label;
    private readonly PanelContainer _progress;
    private readonly ColorRect _otherSegment;
    private readonly ColorRect _processSegment;
    private readonly ColorRect _remainingSegment;
    private double _shownOtherRatio;
    private double _shownProcessRatio;
    private double _targetOtherRatio;
    private double _targetProcessRatio;
    private bool _hasShown;

    public SolverMemoryBar()
    {
        Name = "MemoryUsage";
        CustomMinimumSize = new Vector2(0f, SolverUiTokens.Size.ButtonHeight);
        MouseFilter = MouseFilterEnum.Pass;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        VBoxContainer content = new() { MouseFilter = MouseFilterEnum.Ignore };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xxs);
        AddChild(content);
        _label = SolverUiTokens.CreateLabel(
            SolverText.Get("当前内存 --"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Regular);
        _label.HorizontalAlignment = HorizontalAlignment.Right;
        _label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        content.AddChild(_label);

        _progress = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0f, 8f),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        _progress.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.ProgressBackground,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            0,
            0));
        HBoxContainer segments = new() { MouseFilter = MouseFilterEnum.Ignore };
        segments.AddThemeConstantOverride("separation", 0);
        _otherSegment = CreateSegment(SolverUiTokens.Palette.TextMuted.Darkened(0.3f));
        _processSegment = CreateSegment(SolverUiTokens.Palette.Accent);
        _remainingSegment = CreateSegment(Colors.Transparent);
        segments.AddChild(_otherSegment);
        segments.AddChild(_processSegment);
        segments.AddChild(_remainingSegment);
        _progress.AddChild(segments);
        content.AddChild(_progress);
    }

    public void Apply(MemoryUiSnapshot memory, SearchUiSnapshot? search)
    {
        long process = Math.Max(0, memory.WorkingSetBytes);
        long managed = Math.Max(0, memory.ManagedHeapBytes);
        long available = Math.Max(0, memory.GcAvailableBytes);
        long other = Math.Max(0, process - managed);
        long total = Math.Max(1, other + managed + available);
        _targetOtherRatio = other / (double)total;
        _targetProcessRatio = managed / (double)total;
        string text = SolverText.Format(
            $"游戏占用 {Gigabytes(process)} GB · 系统可用 {Gigabytes(available)} GB");
        if (search is { SearchMemoryPeakBytes: > 0 })
        {
            text += SolverText.Format(
                $"  ·  搜索峰值 {Gigabytes(search.SearchMemoryPeakBytes)} GB / 上限 {Gigabytes(memory.BudgetBytes)} GB");
        }
        _label.Text = text;
        TooltipText = SolverText.Get("求解器内存与性能监视") + "\n"
            + SolverText.Get("- 游戏占用是进程工作集，包含求解器和其他 Mod；系统可用是运行时可用内存，两者不是用量与上限。") + "\n"
            + SolverText.Get("- 整条表示可用内存；灰色为其他占用估计，彩色为托管堆，空白为剩余可用；数字取自同一轮采样。") + "\n"
            + SolverText.Format($"搜索内存上限：{Gigabytes(memory.BudgetBytes)} GB（{SolverPanelText.MemorySource(memory.BudgetSource)}）。这不是游戏进程的 RSS 上限。");
        if (!_hasShown)
        {
            _hasShown = true;
            _shownOtherRatio = _targetOtherRatio;
            _shownProcessRatio = _targetProcessRatio;
            ApplyShown();
        }
    }

    public void Advance(double delta)
    {
        if (!IsVisibleInTree())
        {
            _hasShown = false;
            return;
        }
        double blend = SolverUiMotion.Blend(delta, SolverUiMotion.ReadoutTimeConstantSeconds);
        _shownOtherRatio = SolverUiMotion.Approach(_shownOtherRatio, _targetOtherRatio, blend, 0.0005d);
        _shownProcessRatio = SolverUiMotion.Approach(_shownProcessRatio, _targetProcessRatio, blend, 0.0005d);
        ApplyShown();
    }

    private void ApplyShown()
    {
        SetSegmentRatio(_otherSegment, _shownOtherRatio);
        SetSegmentRatio(_processSegment, _shownProcessRatio);
        SetSegmentRatio(_remainingSegment, Math.Max(0d, 1d - _shownOtherRatio - _shownProcessRatio));
    }

    private static string Gigabytes(long bytes)
        => (bytes / Gigabyte).ToString("F1", CultureInfo.InvariantCulture);

    private static ColorRect CreateSegment(Color color) => new()
    {
        Color = color,
        MouseFilter = MouseFilterEnum.Ignore,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
    };

    private static void SetSegmentRatio(Control segment, double ratio)
    {
        float stretchRatio = (float)Math.Clamp(ratio, 0d, 1d);
        segment.Visible = stretchRatio > 0f;
        segment.SizeFlagsStretchRatio = stretchRatio;
    }
}
