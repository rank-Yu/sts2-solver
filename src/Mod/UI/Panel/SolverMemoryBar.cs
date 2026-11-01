// Adapted from CombatSolver src/UI/SolverMemoryUsageBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Sts2Solver has no NoGC region, Server GC or reclaim service, so those states are gone; the bar
// reads the 4 Hz MemoryUiSnapshot and keeps the original segment layout and eased motion. The
// search budget is a separate ceiling for the solver's own storage and is never drawn as RSS.
using Godot;

namespace Sts2Solver;

/// <summary>Process memory (working set, managed heap) against runtime memory, plus the search ceiling.</summary>
internal sealed partial class SolverMemoryBar : PanelContainer
{
    private readonly Label _processLabel;
    private readonly Label _searchLabel;
    private readonly ColorRect _managedSegment;
    private readonly ColorRect _nativeSegment;
    private readonly ColorRect _remainingSegment;
    private MemoryUiSnapshot? _lastMemory;
    private SearchUiSnapshot? _lastSearch;
    private double _targetManaged;
    private double _targetNative;
    private double _shownManaged;
    private double _shownNative;
    private bool _hasShown;

    public SolverMemoryBar()
    {
        Name = "MemoryUsage";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        VBoxContainer content = new() { MouseFilter = MouseFilterEnum.Ignore };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xxs + 1);
        AddChild(content);

        HBoxContainer labels = new() { MouseFilter = MouseFilterEnum.Ignore };
        labels.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        _processLabel = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary, SolverFontWeight.Normal);
        _processLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _processLabel.ClipText = true;
        labels.AddChild(_processLabel);
        _searchLabel = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary, SolverFontWeight.Normal);
        _searchLabel.HorizontalAlignment = HorizontalAlignment.Right;
        labels.AddChild(_searchLabel);
        content.AddChild(labels);

        PanelContainer track = new()
        {
            CustomMinimumSize = new Vector2(0, 6),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        track.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.ProgressBackground, Colors.Transparent, 3, 0, 0, borderWidth: 0));
        HBoxContainer segments = new() { MouseFilter = MouseFilterEnum.Ignore };
        segments.AddThemeConstantOverride("separation", 0);
        _managedSegment = CreateSegment(SolverUiTokens.Palette.Accent);
        _nativeSegment = CreateSegment(new Color(SolverUiTokens.Palette.TextMuted, 0.55f));
        _remainingSegment = CreateSegment(Colors.Transparent);
        segments.AddChild(_managedSegment);
        segments.AddChild(_nativeSegment);
        segments.AddChild(_remainingSegment);
        track.AddChild(segments);
        content.AddChild(track);
    }

    public void Apply(MemoryUiSnapshot memory, SearchUiSnapshot? search)
    {
        if (ReferenceEquals(memory, _lastMemory) && ReferenceEquals(search, _lastSearch))
            return;
        _lastMemory = memory;
        _lastSearch = search;
        string workingSet = SolverPanelText.Bytes(memory.WorkingSetBytes);
        string managed = SolverPanelText.Bytes(memory.ManagedHeapBytes);
        SetText(_processLabel, SolverText.Format($"游戏工作集 {workingSet} · 托管堆 {managed}"));
        string budget = SolverPanelText.Bytes(memory.BudgetBytes);
        string source = SolverPanelText.MemorySource(memory.BudgetSource);
        string searchText = search is { Phase: SearchUiPhase.Completed, SearchMemoryPeakBytes: > 0 }
            ? SolverText.Format($"搜索峰值 {SolverPanelText.Bytes(search.SearchMemoryPeakBytes)} / 上限 {budget}（{source}）")
            : SolverText.Format($"搜索上限 {budget}（{source}）");
        SetText(_searchLabel, searchText);
        TooltipText = Tooltip(memory);

        double total = memory.GcAvailableBytes;
        if (total > 0)
        {
            double workingSetRatio = Math.Clamp(memory.WorkingSetBytes / total, 0d, 1d);
            _targetManaged = Math.Clamp(memory.ManagedHeapBytes / total, 0d, workingSetRatio);
            _targetNative = Math.Max(0d, workingSetRatio - _targetManaged);
        }
        else
        {
            _targetManaged = 0d;
            _targetNative = 0d;
        }
        if (!_hasShown)
        {
            _hasShown = true;
            _shownManaged = _targetManaged;
            _shownNative = _targetNative;
            ApplyShown();
        }
    }

    public void Advance(double delta)
    {
        if (!_hasShown || !IsVisibleInTree())
            return;
        double blend = SolverUiMotion.Blend(delta, SolverUiMotion.ReadoutTimeConstantSeconds);
        double managed = SolverUiMotion.Approach(_shownManaged, _targetManaged, blend, 0.0005d);
        double native = SolverUiMotion.Approach(_shownNative, _targetNative, blend, 0.0005d);
        if (managed == _shownManaged && native == _shownNative)
            return;
        _shownManaged = managed;
        _shownNative = native;
        ApplyShown();
    }

    private void ApplyShown()
    {
        SetSegmentRatio(_managedSegment, _shownManaged);
        SetSegmentRatio(_nativeSegment, _shownNative);
        SetSegmentRatio(_remainingSegment, Math.Max(0d, 1d - _shownManaged - _shownNative));
    }

    private static string Tooltip(MemoryUiSnapshot memory)
    {
        string total = memory.GcAvailableBytes > 0 ? SolverPanelText.Bytes(memory.GcAvailableBytes) : SolverText.Get("未知");
        return SolverText.Get("内存")
            + "\n" + SolverText.Get("- 游戏工作集：游戏进程占用的物理内存，包含游戏本体和所有 Mod。")
            + "\n" + SolverText.Get("- 托管堆：.NET 托管对象当前占用，是工作集的一部分（彩色段）。")
            + "\n" + SolverText.Format($"- 整条代表运行时可用内存 {total}；灰色段是工作集里托管堆以外的部分。")
            + "\n" + SolverText.Get("- 搜索上限只约束求解器自己的搜索存储，不是游戏进程的内存上限。")
            + "\n" + SolverText.Format($"- 搜索上限来源：{SolverPanelText.MemorySource(memory.BudgetSource)}（环境变量 > 设置 > 默认 15%，至少 1 GiB）。");
    }

    private static ColorRect CreateSegment(Color color) => new()
    {
        Color = color,
        MouseFilter = MouseFilterEnum.Ignore,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
    };

    private static void SetSegmentRatio(Control segment, double ratio)
    {
        float stretch = (float)Math.Clamp(ratio, 0d, 1d);
        segment.Visible = stretch > 0f;
        segment.SizeFlagsStretchRatio = stretch;
    }

    private static void SetText(Label label, string text)
    {
        if (label.Text != text)
            label.Text = text;
    }
}
