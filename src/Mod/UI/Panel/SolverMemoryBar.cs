// Ported from CombatSolver src/UI/SolverMemoryUsageBar.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The NoGC/Server GC states and the
// cleanup pulse are not ported; the label, bar and eased motion remain. The whole bar is the
// search memory budget; the filled part is the search ledger in use while a search runs and the
// latest search peak of this combat between searches. The game process working set is shown as
// text only, never as a limit.
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace Sts2Solver;

internal sealed partial class SolverMemoryBar : PanelContainer
{
    private const double Gigabyte = 1_000_000_000d;

    private readonly Label _label;
    private readonly PanelContainer _progress;
    private readonly ColorRect _searchSegment;
    private readonly ColorRect _remainingSegment;
    private double _shownSearchRatio;
    private double _targetSearchRatio;
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
            SolverText.Get("ui.search_memory"),
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
        _searchSegment = CreateSegment(SolverUiTokens.Palette.Accent);
        _remainingSegment = CreateSegment(Colors.Transparent);
        segments.AddChild(_searchSegment);
        segments.AddChild(_remainingSegment);
        _progress.AddChild(segments);
        content.AddChild(_progress);
    }

    public void Apply(MemoryUiSnapshot memory, SearchUiSnapshot? search, long lastSearchPeakBytes)
    {
        long process = Math.Max(0, memory.WorkingSetBytes);
        long budget = Math.Max(1, memory.BudgetBytes);
        bool running = search is { Status: "running" };
        long used = running ? Math.Max(0, search!.SearchMemoryUsedBytes) : 0;
        long peak = running ? 0 : Math.Max(0, lastSearchPeakBytes);
        _targetSearchRatio = Math.Clamp((running ? used : peak) / (double)budget, 0d, 1d);
        _searchSegment.Color = search is { MemoryBudgetExceeded: true }
            ? SolverUiTokens.Palette.Danger
            : SolverUiTokens.Palette.Accent;
        _label.Text = running
            ? SolverText.Format("ui.search_memory_in_use_limit_gb_game_gb", Gigabytes(used), Gigabytes(memory.BudgetBytes), Gigabytes(process))
            : peak > 0
                ? SolverText.Format("ui.search_memory_peak_limit_gb_game_gb", Gigabytes(peak), Gigabytes(memory.BudgetBytes), Gigabytes(process))
                : SolverText.Format("ui.search_memory_limit_gb_game_gb", Gigabytes(memory.BudgetBytes), Gigabytes(process));
        TooltipText = SolverText.Get("ui.solver_memory_monitor") + "\n"
            + SolverText.Get("ui.the_whole_bar_is_the_search_memory_limit_the_colored") + "\n"
            + SolverText.Get("ui.game_is_the_memory_the_game_process_actually_uses_including") + "\n"
            + SolverText.Format("ui.search_memory_limit_gb_change_it_in_settings_performance", Gigabytes(memory.BudgetBytes), SolverPanelText.MemorySource(memory.BudgetSource));
        if (!_hasShown)
        {
            _hasShown = true;
            _shownSearchRatio = _targetSearchRatio;
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
        _shownSearchRatio = SolverUiMotion.Approach(_shownSearchRatio, _targetSearchRatio, blend, 0.0005d);
        ApplyShown();
    }

    private void ApplyShown()
    {
        SetSegmentRatio(_searchSegment, _shownSearchRatio);
        SetSegmentRatio(_remainingSegment, Math.Max(0d, 1d - _shownSearchRatio));
    }

    private static string Gigabytes(long bytes)
        => (bytes / Gigabyte).ToString("F1", SolverText.Culture);

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
