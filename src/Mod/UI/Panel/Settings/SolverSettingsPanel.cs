// Adapted from CombatSolver src/UI/SolverSettingsPanel.cs, SolverSettingsPanel.General.cs and
// SolverSettingsPanel.Performance.cs at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and
// contributors). Only the settings Sts2Solver actually wires are shown (auto start, search tier,
// memory override, appearance); tier text comes from Sts2Solver's own SearchPerformanceProfile.
using System.Globalization;
using Godot;
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>Settings page shown in place of the route view. Saves through <see cref="SolverSettings.Update"/>.</summary>
internal sealed partial class SolverSettingsPanel : PanelContainer
{
    private const long MebiByte = 1024L * 1024L;
    private const long MinimumMemoryMiB = 256;
    private const ulong OpacitySaveDelayMilliseconds = 400;

    private readonly Label _status;
    private readonly CheckButton _autoStart;
    private readonly OptionButton _tier;
    private readonly Label _tierDescription;
    private readonly LineEdit _memory;
    private readonly Label _memoryEffective;
    private readonly Label _memoryNote;
    private readonly OptionButton _theme;
    private readonly HSlider _opacity;
    private readonly Label _opacityValue;
    private bool _loading;
    private float? _pendingOpacity;
    private ulong _opacitySaveAt;

    /// <summary>Opacity changes are previewed on every slider step and saved after a short pause.</summary>
    public event Action<float>? OpacityPreview;

    /// <summary>The theme was saved; the shell rebuilds the panel with the new palette.</summary>
    public event Action? ThemeSaved;

    /// <summary>Defaults were restored, including the panel geometry.</summary>
    public event Action? DefaultsRestored;

    public SolverSettingsPanel()
    {
        Name = "SettingsPanel";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Md));

        VBoxContainer root = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        AddChild(root);

        HBoxContainer heading = new() { MouseFilter = MouseFilterEnum.Pass };
        heading.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        Label title = SolverUiTokens.CreateLabel(SolverText.Get("求解器设置"), SolverUiTokens.Type.Title,
            SolverUiTokens.Palette.TextPrimary);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(title);
        Button reset = SolverUiTokens.CreateButton(SolverText.Get("恢复默认"), SolverButtonStyle.Secondary);
        reset.Name = "ResetDefaults";
        reset.TooltipText = SolverText.Get("恢复本页的全部设置，并把面板放回默认位置和大小。");
        reset.Pressed += ResetDefaults;
        heading.AddChild(reset);
        root.AddChild(heading);

        VBoxContainer content = new()
        {
            Name = "SettingsContent",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Lg);
        ScrollContainer scroll = new()
        {
            CustomMinimumSize = new Vector2(0, 160),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = MouseFilterEnum.Pass,
        };
        scroll.AddChild(content);
        root.AddChild(scroll);

        // Automation.
        VBoxContainer automation = SolverSettingsControls.CreateSection(SolverText.Get("自动执行"),
            SolverText.Get("进入可操作的单人战斗时自动开始全自动，效果与按 F8 相同。本场手动停止后不会再次自动开始。"));
        GridContainer automationGrid = SolverSettingsControls.CreateGrid();
        _autoStart = SolverSettingsControls.CreateToggle();
        _autoStart.Name = "AutoStartToggle";
        _autoStart.Toggled += OnAutoStartToggled;
        SolverSettingsControls.AddRow(automationGrid, SolverText.Get("每场自动开启全自动"), _autoStart);
        automation.AddChild(automationGrid);
        content.AddChild(automation);

        // Search.
        VBoxContainer search = SolverSettingsControls.CreateSection(SolverText.Get("搜索"),
            SolverText.Get("档位决定每轮搜索的宽度和时限，下一次搜索开始时生效，正在进行的搜索不变。"));
        GridContainer searchGrid = SolverSettingsControls.CreateGrid();
        _tier = SolverSettingsControls.CreateOption();
        _tier.Name = "TierOption";
        foreach (SearchPerformanceTier tier in Enum.GetValues<SearchPerformanceTier>())
        {
            string name = SolverPanelText.Tier(tier);
            if (tier == SearchPerformanceTier.Medium)
                name = SolverText.Format($"{name}（默认）");
            _tier.AddItem(name, (int)tier);
        }
        _tier.ItemSelected += OnTierSelected;
        SolverSettingsControls.AddRow(searchGrid, SolverText.Get("搜索档位"), _tier);
        search.AddChild(searchGrid);
        _tierDescription = SolverSettingsControls.CreateHint("");
        search.AddChild(_tierDescription);

        GridContainer memoryGrid = SolverSettingsControls.CreateGrid();
        _memory = SolverSettingsControls.CreateLineEdit(SolverText.Get("留空使用默认值"));
        _memory.Name = "MemoryInput";
        _memory.TextSubmitted += _ => CommitMemory();
        _memory.FocusExited += () => CommitMemory();
        _memory.TextChanged += _ => _memory.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        SolverSettingsControls.AddRow(memoryGrid, SolverText.Get("搜索内存上限（MiB）"), _memory,
            SolverText.Get("只约束求解器自己的搜索存储，不是游戏进程的内存上限。"));
        search.AddChild(memoryGrid);
        _memoryEffective = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption, SolverUiTokens.Palette.TextSecondary);
        _memoryEffective.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        search.AddChild(_memoryEffective);
        _memoryNote = SolverSettingsControls.CreateHint("");
        search.AddChild(_memoryNote);
        content.AddChild(search);

        // Appearance.
        VBoxContainer appearance = SolverSettingsControls.CreateSection(SolverText.Get("外观"), null);
        GridContainer appearanceGrid = SolverSettingsControls.CreateGrid();
        _theme = SolverSettingsControls.CreateOption();
        _theme.Name = "ThemeOption";
        _theme.AddItem(SolverText.Get("深色（默认）"), (int)SolverUiTheme.Dark);
        _theme.AddItem(SolverText.Get("浅色"), (int)SolverUiTheme.Light);
        _theme.ItemSelected += OnThemeSelected;
        SolverSettingsControls.AddRow(appearanceGrid, SolverText.Get("主题"), _theme);
        HBoxContainer opacityRow = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        opacityRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _opacity = SolverSettingsControls.CreateSlider(0.25, 1, 0.05);
        _opacity.Name = "OpacitySlider";
        _opacity.ValueChanged += OnOpacityChanged;
        _opacity.DragEnded += _ => FlushOpacity();
        opacityRow.AddChild(_opacity);
        _opacityValue = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        _opacityValue.HorizontalAlignment = HorizontalAlignment.Right;
        _opacityValue.CustomMinimumSize = new Vector2(48, 24);
        opacityRow.AddChild(_opacityValue);
        SolverSettingsControls.AddRow(appearanceGrid, SolverText.Get("背景不透明度"), opacityRow,
            SolverText.Get("调整面板背景的不透明度，范围 25%–100%；文字始终保持清晰。"));
        appearance.AddChild(appearanceGrid);
        content.AddChild(appearance);

        _status = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Caption, SolverUiTokens.Palette.Success);
        _status.CustomMinimumSize = new Vector2(0, 18);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(_status);
        SetProcess(false);
    }

    /// <summary>Loads every input from the current settings and the latest snapshot's memory resolution.</summary>
    public void Reload()
    {
        _loading = true;
        SolverSettingsData data = SolverSettings.Current;
        _autoStart.SetPressedNoSignal(data.Automation.AutoEnableFullAuto);
        _tier.Select(_tier.GetItemIndex((int)data.Search.Tier));
        _tierDescription.Text = SolverPanelText.TierDescription(data.Search.Tier);
        _memory.Text = data.Search.MemoryBudgetBytes is { } bytes
            ? (bytes / MebiByte).ToString(CultureInfo.InvariantCulture)
            : "";
        _theme.Select(_theme.GetItemIndex((int)data.Ui.Theme));
        _opacity.SetValueNoSignal(data.Ui.Opacity);
        _opacityValue.Text = Percent(data.Ui.Opacity);
        _pendingOpacity = null;
        _status.Text = "";
        _loading = false;
        ApplySnapshot(CombatUiStore.Snapshot);
    }

    /// <summary>Refreshes the effective memory line; called with each snapshot while the page is open.</summary>
    public void ApplySnapshot(CombatUiSnapshot snapshot)
    {
        SettingsUiSnapshot settings = snapshot.Settings;
        string effective = SolverText.Format(
            $"当前生效：{SolverPanelText.Bytes(settings.MemoryBudgetBytes)}（来源：{SolverPanelText.MemorySource(settings.MemoryBudgetSource)}）");
        if (_memoryEffective.Text != effective)
            _memoryEffective.Text = effective;
        bool environment = settings.MemoryBudgetSource == SearchMemoryBudgetSource.Environment;
        _memory.Editable = !environment;
        _memory.ClearButtonEnabled = !environment;
        string note = environment
            ? SolverText.Format($"已由环境变量 {SearchMemoryBudget.OverrideVariable} 指定，这里只显示；要在面板里修改，请先移除该环境变量。")
            : SolverText.Get("留空时使用默认值：运行时可用内存的 15%，至少 1 GiB。下一次搜索开始时生效。");
        if (_memoryNote.Text != note)
            _memoryNote.Text = note;
        if (environment && !_memory.HasFocus())
        {
            string shown = (settings.MemoryBudgetBytes / MebiByte).ToString(CultureInfo.InvariantCulture);
            if (_memory.Text != shown)
                _memory.Text = shown;
        }
    }

    /// <summary>Saves inputs that are still being edited; false when an input is invalid.</summary>
    public bool CommitPending()
    {
        FlushOpacity();
        return CommitMemory();
    }

    public override void _Process(double delta)
    {
        if (_pendingOpacity == null)
        {
            SetProcess(false);
            return;
        }
        if (Time.GetTicksMsec() >= _opacitySaveAt)
            FlushOpacity();
    }

    public override void _Input(InputEvent inputEvent)
    {
        // Clicking anywhere outside the focused text box ends editing, so the game regains its keys.
        if (IsVisibleInTree()
            && inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click
            && _memory.HasFocus()
            && !_memory.GetGlobalRect().HasPoint(click.Position))
        {
            _memory.ReleaseFocus();
        }
    }

#if STS2_SOLVER_TESTING
    internal OptionButton ThemeOptionForTest => _theme;
#endif

    private void OnAutoStartToggled(bool enabled)
    {
        if (_loading)
            return;
        Save(current => current with
        {
            Automation = current.Automation with { AutoEnableFullAuto = enabled },
        }, enabled
            ? SolverText.Get("已开启，下一场战斗自动开始全自动")
            : SolverText.Get("已关闭每场自动开启"));
    }

    private void OnTierSelected(long index)
    {
        SearchPerformanceTier tier = (SearchPerformanceTier)_tier.GetItemId((int)index);
        _tierDescription.Text = SolverPanelText.TierDescription(tier);
        if (_loading)
            return;
        Save(current => current with { Search = current.Search with { Tier = tier } },
            SolverText.Get("搜索档位已保存，下一次搜索生效"));
    }

    private bool CommitMemory()
    {
        if (_loading || !_memory.Editable)
            return true;
        string text = _memory.Text.Trim();
        long? current = SolverSettings.Current.Search.MemoryBudgetBytes;
        if (text.Length == 0)
        {
            if (current == null)
                return true;
            return Save(data => data with { Search = data.Search with { MemoryBudgetBytes = null } },
                SolverText.Get("已恢复默认内存上限，下一次搜索生效"));
        }
        long maximum = MaximumMemoryMiB();
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long mebibytes)
            || mebibytes < MinimumMemoryMiB || mebibytes > maximum)
        {
            _memory.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Danger);
            SetStatus(SolverText.Format($"请输入 {MinimumMemoryMiB}–{maximum} 之间的整数（MiB）"), SolverUiTokens.Palette.Danger);
            return false;
        }
        long bytes = mebibytes * MebiByte;
        if (current == bytes)
            return true;
        return Save(data => data with { Search = data.Search with { MemoryBudgetBytes = bytes } },
            SolverText.Get("搜索内存上限已保存，下一次搜索生效"));
    }

    private static long MaximumMemoryMiB()
    {
        long available = CombatUiStore.Snapshot.Memory.GcAvailableBytes / MebiByte;
        return Math.Max(MinimumMemoryMiB, available > 0 ? available : 1L << 20);
    }

    private void OnThemeSelected(long index)
    {
        if (_loading)
            return;
        SolverUiTheme theme = (SolverUiTheme)_theme.GetItemId((int)index);
        if (theme == SolverSettings.Current.Ui.Theme)
            return;
        if (Save(current => current with { Ui = current.Ui with { Theme = theme } },
                SolverText.Get("主题已保存")))
        {
            ThemeSaved?.Invoke();
        }
    }

    private void OnOpacityChanged(double value)
    {
        float opacity = (float)value;
        _opacityValue.Text = Percent(opacity);
        if (_loading)
            return;
        OpacityPreview?.Invoke(opacity);
        _pendingOpacity = opacity;
        _opacitySaveAt = Time.GetTicksMsec() + OpacitySaveDelayMilliseconds;
        SetProcess(true);
    }

    private void FlushOpacity()
    {
        if (_pendingOpacity is not { } opacity)
            return;
        _pendingOpacity = null;
        if (Math.Abs(SolverSettings.Current.Ui.Opacity - opacity) < 0.0001f)
            return;
        Save(current => current with { Ui = current.Ui with { Opacity = opacity } },
            SolverText.Get("不透明度已保存"));
    }

    private void ResetDefaults()
    {
        SolverUiTheme previousTheme = SolverSettings.Current.Ui.Theme;
        _pendingOpacity = null;
        SolverSearchSettings defaults = new();
        if (!Save(current => current with
            {
                Search = current.Search with
                {
                    Tier = defaults.Tier,
                    MemoryBudgetBytes = defaults.MemoryBudgetBytes,
                },
                Automation = new SolverAutomationSettings(),
                Ui = new SolverUiSettings(),
            }, SolverText.Get("已恢复默认设置")))
        {
            return;
        }
        Reload();
        SetStatus(SolverText.Get("已恢复默认设置"), SolverUiTokens.Palette.Success);
        DefaultsRestored?.Invoke();
        OpacityPreview?.Invoke(SolverSettings.Current.Ui.Opacity);
        if (previousTheme != SolverSettings.Current.Ui.Theme)
            ThemeSaved?.Invoke();
    }

    private bool Save(Func<SolverSettingsData, SolverSettingsData> change, string message)
    {
        if (!SolverSettings.Update(change))
        {
            SetStatus(SolverText.Get("保存失败，详情已写入游戏日志"), SolverUiTokens.Palette.Danger);
            Reload();
            return false;
        }
        _memory.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        SetStatus(message, SolverUiTokens.Palette.Success);
        return true;
    }

    private void SetStatus(string text, Color color)
    {
        _status.AddThemeColorOverride("font_color", color);
        _status.Text = text;
    }

    private static string Percent(float value)
        => Math.Round(value * 100d).ToString(CultureInfo.InvariantCulture) + "%";
}
