// Adapted from CombatSolver src/UI/SolverSettingsPanel.cs, SolverSettingsPanel.General.cs and
// SolverSettingsPanel.Performance.cs at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and
// contributors). The page replaces the panel body like the original, without tabs: five groups
// (出牌, 性能, Boss 策略, 显示, 反馈) with only the settings Sts2Solver wires. The thread count and
// memory limit are free inputs prefilled with the
// effective value; an input left as shown writes nothing,
// and a value equal to the machine default is stored as null so it keeps following the machine. The
// memory limit has 0.1 GB precision. The two Boss HP policies go through ICombatUiCommands.
using System.Globalization;
using Godot;
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>
/// The settings page shown in place of the panel body. Saves through
/// <see cref="SolverSettings.Update"/> and the Boss HP commands.
/// </summary>
internal sealed partial class SolverSettingsPage : PanelContainer
{
    internal const int MinimumThreads = 1;
    internal const double MinimumMemoryGigabytes = 0.25d;
    private const double Gigabyte = 1_000_000_000d;
    private const double MinimumDelaySeconds = 0d;
    private const double MaximumDelaySeconds = 3d;
    private const ulong OpacitySaveDelayMilliseconds = 400;

    private readonly Button _exportLog;
    private Task<DiagnosticLogExport>? _export;
    private readonly Button _reset;
    private readonly OptionButton _speed;
    private readonly LineEdit _delay;
    private readonly OptionButton _tier;
    private readonly LineEdit _threads;
    private readonly LineEdit _memory;
    private readonly OptionButton _actBoss;
    private readonly OptionButton _finalBoss;
    private readonly HSlider _opacity;
    private readonly Label _opacityValue;
    private readonly VBoxContainer _content;
    private readonly ScrollContainer _scroll;
    private readonly Label _status;
    private SettingsUiSnapshot _settings = new();
    private long _availableMemoryBytes;
    private string _delayShown = string.Empty;
    private string _threadsShown = string.Empty;
    private string _memoryShown = string.Empty;
    private bool _loading;
    private float? _pendingOpacity;
    private ulong _opacitySaveAt;

    /// <summary>Opacity changes are previewed on every slider step and saved after a short pause.</summary>
    public event Action<float>? OpacityPreview;

    /// <summary>Defaults were restored, including the panel geometry.</summary>
    public event Action? DefaultsRestored;

    public SolverSettingsPage()
    {
        Name = "SettingsPage";
        Visible = false;
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Md));
        VBoxContainer layout = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        AddChild(layout);

        HBoxContainer heading = new()
        {
            Name = "SettingsHeading",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        heading.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        Label title = SolverUiTokens.CreateLabel(SolverText.Get("求解器设置"), SolverUiTokens.Type.Title,
            SolverUiTokens.Palette.TextPrimary);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(title);
        _reset = SolverUiTokens.CreateButton(SolverText.Get("恢复默认"), SolverButtonStyle.Secondary);
        _reset.Name = "ResetDefaults";
        _reset.CustomMinimumSize = new Vector2(96, SolverUiTokens.Size.ButtonHeight);
        _reset.Pressed += ResetDefaults;
        heading.AddChild(_reset);
        layout.AddChild(heading);

        VBoxContainer content = SolverSettingsControls.CreatePageContent("SettingsContent");
        _content = content;

        GridContainer play = SolverSettingsControls.CreateSettingsGrid();
        _speed = SolverSettingsControls.CreateOptionInput();
        _speed.Name = "DeploymentSpeedOption";
        _speed.AddItem(SolverText.Get("跟随游戏（默认）"), (int)SolverDeploymentSpeed.FollowGame);
        _speed.AddItem(SolverText.Get("正常"), (int)SolverDeploymentSpeed.Normal);
        _speed.AddItem(SolverText.Get("快速"), (int)SolverDeploymentSpeed.Fast);
        _speed.ItemSelected += OnSpeedSelected;
        SolverSettingsControls.AddBasicRow(play, SolverText.Get("自动出牌速度"), _speed);
        _delay = SolverSettingsControls.CreateInput("0");
        _delay.Name = "InterActionDelayInput";
        _delay.TextSubmitted += _ => CommitDelay();
        _delay.FocusExited += () => CommitDelay();
        SolverSettingsControls.AddBasicRow(play, SolverText.Get("牌间额外停顿（秒）"), _delay);
        SolverSettingsControls.AddSettingsSection(content, SolverText.Get("出牌"), play);

        GridContainer performance = SolverSettingsControls.CreateSettingsGrid();
        _tier = SolverSettingsControls.CreateOptionInput();
        _tier.Name = "TierOption";
        foreach (SearchPerformanceTier tier in Enum.GetValues<SearchPerformanceTier>())
        {
            _tier.AddItem(tier == SearchPerformanceTier.Medium
                ? SolverText.Get("中档（默认）")
                : SolverPanelText.Tier(tier), (int)tier);
        }
        _tier.ItemSelected += OnTierSelected;
        SolverSettingsControls.AddBasicRow(performance, SolverText.Get("性能预设"), _tier,
            SolverText.Get("越高越可能找到更好的路线，但搜索更久。"));
        _threads = SolverSettingsControls.CreateInput(string.Empty);
        _threads.Name = "ThreadsInput";
        _threads.TextSubmitted += _ => CommitThreads();
        _threads.FocusExited += () => CommitThreads();
        SolverSettingsControls.AddBasicRow(performance, SolverText.Get("搜索并行度"), _threads,
            SolverText.Get("越大搜索越快，但更占 CPU，可能让游戏卡顿。"));
        _memory = SolverSettingsControls.CreateInput(string.Empty);
        _memory.Name = "MemoryInput";
        _memory.TextSubmitted += _ => CommitMemory();
        _memory.FocusExited += () => CommitMemory();
        SolverSettingsControls.AddBasicRow(performance, SolverText.Get("搜索内存上限（GB）"), _memory,
            SolverText.Get("只限制搜索用的内存。设太小可能搜不完。"));
        SolverSettingsControls.AddSettingsSection(content, SolverText.Get("性能"), performance);

        GridContainer boss = SolverSettingsControls.CreateSettingsGrid();
        _actBoss = CreateBossInput("ActBossHpOption");
        _actBoss.ItemSelected += OnActBossSelected;
        SolverSettingsControls.AddBasicRow(boss, SolverText.Get("第一、二幕血量取舍"), _actBoss,
            SolverText.Get("通关优先会按战后回复 80% 折算血量价值并尽量保留药水；最低战损会按普通战斗完整比较掉血。"));
        _finalBoss = CreateBossInput("FinalBossHpOption");
        _finalBoss.ItemSelected += OnFinalBossSelected;
        SolverSettingsControls.AddBasicRow(boss, SolverText.Get("最终 Boss 血量取舍"), _finalBoss,
            SolverText.Get("通关优先只要求路线存活并优先保留资源；最低战损会继续比较剩余血量。"));
        SolverSettingsControls.AddSettingsSection(content, SolverText.Get("Boss 策略"), boss);

        GridContainer display = SolverSettingsControls.CreateSettingsGrid();
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
        _opacityValue = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary);
        _opacityValue.HorizontalAlignment = HorizontalAlignment.Right;
        _opacityValue.CustomMinimumSize = new Vector2(48, 24);
        opacityRow.AddChild(_opacityValue);
        SolverSettingsControls.AddBasicRow(display, SolverText.Get("覆盖层透明度"), opacityRow);
        SolverSettingsControls.AddSettingsSection(content, SolverText.Get("显示"), display);

        HBoxContainer feedback = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _exportLog = SolverUiTokens.CreateButton(SolverText.Get("导出Mod日志"), SolverButtonStyle.Secondary);
        _exportLog.Name = "ExportLogs";
        _exportLog.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _exportLog.TooltipText = SolverText.Get("遇到问题时可导出发给作者");
        _exportLog.Pressed += ExportLogs;
        feedback.AddChild(_exportLog);
        SolverSettingsControls.AddSettingsSection(content, SolverText.Get("反馈"), feedback);

        _scroll = SolverSettingsControls.CreatePageScroll(content);
        layout.AddChild(_scroll);
        _status = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Success);
        _status.Name = "SettingsStatus";
        _status.CustomMinimumSize = new Vector2(0, 20);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        layout.AddChild(_status);
        SetProcess(false);
    }

    /// <summary>
    /// Height the groups need beyond the scroll area's minimum; the panel adds it to its request so
    /// the page shows whole when there is room.
    /// </summary>
    internal float HiddenContentHeight
        => Math.Max(0f, _content.GetCombinedMinimumSize().Y - _scroll.GetCombinedMinimumSize().Y);

    /// <summary>Reloads the page from the current settings; called each time the page opens.</summary>
    public void Opened() => Reload();

    public void Apply(CombatUiSnapshot snapshot)
    {
        ApplyEffectiveValues(snapshot, force: false);
        ApplyBossStrategies(snapshot.Strategy);
    }

    /// <summary>Saves inputs still being edited; false keeps the page open on an invalid value.</summary>
    public bool CommitPending()
    {
        FlushOpacity();
        return CommitDelay() && CommitThreads() && CommitMemory();
    }

    /// <summary>
    /// Parses the thread-count input. Empty text and the machine default both mean "follow the
    /// default" and return null; false when the text is not an integer in 1..8.
    /// </summary>
    internal static bool TryParseThreads(string text, int defaultThreads, out int? threads)
    {
        threads = null;
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return true;
        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value < MinimumThreads || value > SearchThreadCount.Maximum)
        {
            return false;
        }
        if (value != defaultThreads)
            threads = value;
        return true;
    }

    /// <summary>
    /// Parses the memory input in GB (10^9 bytes), rounded to 0.1 GB. Empty text and a value equal to
    /// the displayed default both return null; false when the text is not a number in 0.25 GB..maximum.
    /// </summary>
    internal static bool TryParseMemoryGigabytes(string text, long defaultBytes, long maximumBytes,
        out long? bytes)
    {
        bytes = null;
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return true;
        if (!double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out double gigabytes)
            || gigabytes < MinimumMemoryGigabytes || gigabytes * Gigabyte > maximumBytes)
        {
            return false;
        }
        long value = (long)(Math.Round(gigabytes * 10d, MidpointRounding.AwayFromZero) * (Gigabyte / 10d));
        if (FormatGigabytes(value) != FormatGigabytes(defaultBytes))
            bytes = value;
        return true;
    }

    internal static string FormatGigabytes(long bytes)
        => (bytes / Gigabyte).ToString("0.0", CultureInfo.InvariantCulture);

    public override void _Process(double delta)
    {
        if (_export is { IsCompleted: true } export)
        {
            _export = null;
            ShowExport(export.Result);
        }
        if (_pendingOpacity != null && Time.GetTicksMsec() >= _opacitySaveAt)
            FlushOpacity();
        if (_pendingOpacity == null && _export == null)
            SetProcess(false);
    }

    private void ExportLogs()
    {
        if (_export != null)
            return;
        _exportLog.Disabled = true;
        _exportLog.Text = SolverText.Get("正在打包…");
        SetStatus(SolverText.Get("正在打包…"), SolverUiTokens.Palette.TextSecondary);
        _export = CombatUiCommands.Instance.ExportLogsAsync();
        SetProcess(true);
    }

    private void ShowExport(DiagnosticLogExport result)
    {
        _exportLog.Disabled = false;
        _exportLog.Text = SolverText.Get("导出Mod日志");
        if (result.ZipPath is not { } zip)
        {
            ModLog.Error($"[Sts2Solver] LOG_EXPORT_FAILED reason={result.Error}");
            SetStatus(SolverText.Format($"导出失败：{result.Error}"), SolverUiTokens.Palette.Danger);
            return;
        }
        ModLog.Info($"[Sts2Solver] LOG_EXPORTED path={zip}");
        SetStatus(SolverText.Format($"已导出 {Path.GetFileName(zip)}"), SolverUiTokens.Palette.Success);
        if (!ModEntry.TestMode && OS.ShellShowInFileManager(zip) != Error.Ok)
            SetStatus(SolverText.Format($"已导出，请打开：{zip}"), SolverUiTokens.Palette.TextSecondary);
    }

    public override void _Input(InputEvent inputEvent)
    {
        // Clicking outside the focused text box ends editing, so the game regains its keys.
        if (IsVisibleInTree()
            && inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click
            && GetViewport().GuiGetFocusOwner() is LineEdit focused
            && IsAncestorOf(focused)
            && !focused.GetGlobalRect().HasPoint(click.Position))
        {
            focused.ReleaseFocus();
        }
    }

    private void Reload()
    {
        _loading = true;
        SolverSettingsData data = SolverSettings.Current;
        _speed.Select(_speed.GetItemIndex((int)data.Automation.DeploymentSpeed));
        double delay = data.Automation.InterActionDelaySeconds;
        Show(_delay, ref _delayShown,
            delay > 0d ? delay.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty);
        _tier.Select(_tier.GetItemIndex((int)data.Search.Tier));
        _opacity.SetValueNoSignal(data.Ui.Opacity);
        _opacityValue.Text = Percent(data.Ui.Opacity);
        _pendingOpacity = null;
        foreach (LineEdit input in new[] { _delay, _threads, _memory })
            MarkValid(input);
        _status.Text = _export != null ? SolverText.Get("正在打包…") : string.Empty;
        _loading = false;
        ApplyEffectiveValues(CombatUiStore.Snapshot, force: true);
        ApplyBossStrategies(CombatUiStore.Snapshot.Strategy);
    }

    private static OptionButton CreateBossInput(string name)
    {
        OptionButton input = SolverSettingsControls.CreateOptionInput();
        input.Name = name;
        input.AddItem(SolverText.Get("通关优先（默认）"), (int)SearchBossHpStrategy.ProgressionFirst);
        input.AddItem(SolverText.Get("最低战损"), (int)SearchBossHpStrategy.MinimizeHpLoss);
        return input;
    }

    private void ApplyBossStrategies(StrategyUiSnapshot strategy)
    {
        SelectBoss(_actBoss, strategy.ActTransitionBossHpStrategy);
        SelectBoss(_finalBoss, strategy.FinalBossHpStrategy);
    }

    private static void SelectBoss(OptionButton input, SearchBossHpStrategy strategy)
    {
        int index = input.GetItemIndex((int)strategy);
        if (input.Selected != index)
            input.Select(index);
    }

    private void OnActBossSelected(long index)
    {
        SearchBossHpStrategy strategy = (SearchBossHpStrategy)_actBoss.GetItemId((int)index);
        bool accepted = CombatUiCommands.Instance.SetActTransitionBossHpStrategy(strategy);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=act_boss_hp strategy={strategy} accepted={accepted}");
        ShowCommandResult(accepted);
    }

    private void OnFinalBossSelected(long index)
    {
        SearchBossHpStrategy strategy = (SearchBossHpStrategy)_finalBoss.GetItemId((int)index);
        bool accepted = CombatUiCommands.Instance.SetFinalBossHpStrategy(strategy);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=final_boss_hp strategy={strategy} accepted={accepted}");
        ShowCommandResult(accepted);
    }

    private void ShowCommandResult(bool accepted)
    {
        if (accepted)
        {
            SetStatus(SolverText.Get("已保存，下次搜索生效"), SolverUiTokens.Palette.Success);
            return;
        }
        SetStatus(SolverText.Get("保存失败，详情已写入游戏日志"), SolverUiTokens.Palette.Danger);
        ApplyBossStrategies(CombatUiStore.Snapshot.Strategy);
    }

    /// <summary>Shows the effective thread count and memory limit; inputs being edited are left alone.</summary>
    private void ApplyEffectiveValues(CombatUiSnapshot snapshot, bool force)
    {
        _settings = snapshot.Settings;
        _availableMemoryBytes = snapshot.Memory.GcAvailableBytes;
        if (force || !_threads.HasFocus())
            Show(_threads, ref _threadsShown, _settings.SearchThreads.ToString(CultureInfo.InvariantCulture));
        if (force || !_memory.HasFocus())
            Show(_memory, ref _memoryShown, FormatGigabytes(_settings.MemoryBudgetBytes));
    }

    private void OnSpeedSelected(long index)
    {
        if (_loading)
            return;
        SolverDeploymentSpeed speed = (SolverDeploymentSpeed)_speed.GetItemId((int)index);
        Save(current => current with { Automation = current.Automation with { DeploymentSpeed = speed } },
            SolverText.Get("已保存，下次执行生效"));
    }

    private void OnTierSelected(long index)
    {
        if (_loading)
            return;
        SearchPerformanceTier tier = (SearchPerformanceTier)_tier.GetItemId((int)index);
        Save(current => current with { Search = current.Search with { Tier = tier } },
            SolverText.Get("已保存，下次搜索生效"));
    }

    private bool CommitDelay()
    {
        if (_loading || _delay.Text == _delayShown)
            return true;
        string text = _delay.Text.Trim();
        double seconds = 0d;
        if (text.Length > 0
            && (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)
                || seconds < MinimumDelaySeconds || seconds > MaximumDelaySeconds))
        {
            ShowInvalid(_delay,
                SolverText.Format($"请输入 {MinimumDelaySeconds:0.###}–{MaximumDelaySeconds:0.###} 的数字"));
            return false;
        }
        MarkValid(_delay);
        _delayShown = _delay.Text;
        if (SolverSettings.Current.Automation.InterActionDelaySeconds.Equals(seconds))
            return true;
        return Save(current => current with
            {
                Automation = current.Automation with { InterActionDelaySeconds = seconds },
            }, SolverText.Get("已保存，下次执行生效"));
    }

    private bool CommitThreads()
    {
        if (_loading || _threads.Text == _threadsShown)
            return true;
        if (!TryParseThreads(_threads.Text, _settings.SearchThreadsDefault, out int? threads))
        {
            ShowInvalid(_threads,
                SolverText.Format($"请输入 {MinimumThreads}–{SearchThreadCount.Maximum} 的整数"));
            return false;
        }
        MarkValid(_threads);
        Show(_threads, ref _threadsShown,
            (threads ?? _settings.SearchThreadsDefault).ToString(CultureInfo.InvariantCulture));
        if (SolverSettings.Current.Search.Threads == threads)
            return true;
        return Save(current => current with { Search = current.Search with { Threads = threads } },
            SolverText.Get("已保存，下次搜索生效"));
    }

    private bool CommitMemory()
    {
        if (_loading || _memory.Text == _memoryShown)
            return true;
        long maximumBytes = Math.Max(_availableMemoryBytes, _settings.MemoryBudgetDefaultBytes);
        if (!TryParseMemoryGigabytes(_memory.Text, _settings.MemoryBudgetDefaultBytes, maximumBytes,
                out long? bytes))
        {
            double maximum = Math.Floor(maximumBytes / Gigabyte * 10d) / 10d;
            ShowInvalid(_memory,
                SolverText.Format($"请输入 {MinimumMemoryGigabytes:0.###}–{maximum:0.###} 的数字"));
            return false;
        }
        MarkValid(_memory);
        Show(_memory, ref _memoryShown, FormatGigabytes(bytes ?? _settings.MemoryBudgetDefaultBytes));
        if (SolverSettings.Current.Search.MemoryBudgetBytes == bytes)
            return true;
        return Save(current => current with { Search = current.Search with { MemoryBudgetBytes = bytes } },
            SolverText.Get("已保存，下次搜索生效"));
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
            SolverText.Get("已保存并立即生效"));
    }

    /// <summary>
    /// Resets this page's settings (the Boss HP policies through their commands), the panel geometry
    /// and the big-damage hint; the mode stays.
    /// </summary>
    private void ResetDefaults()
    {
        _pendingOpacity = null;
        SolverSearchSettings search = new();
        SolverAutomationSettings automation = new();
        SolverUiSettings ui = new();
        if (!SolverSettings.Update(current => current with
            {
                Search = current.Search with
                {
                    Tier = search.Tier,
                    Threads = search.Threads,
                    MemoryBudgetBytes = search.MemoryBudgetBytes,
                },
                Automation = current.Automation with
                {
                    DeploymentSpeed = automation.DeploymentSpeed,
                    InterActionDelaySeconds = automation.InterActionDelaySeconds,
                },
                Ui = current.Ui with
                {
                    PanelX = ui.PanelX,
                    PanelY = ui.PanelY,
                    PanelWidth = ui.PanelWidth,
                    PanelHeight = ui.PanelHeight,
                    Opacity = ui.Opacity,
                    ShowBattleDamagePerformanceHint = ui.ShowBattleDamagePerformanceHint,
                },
            }))
        {
            SetStatus(SolverText.Get("保存失败，详情已写入游戏日志"), SolverUiTokens.Palette.Danger);
            return;
        }
        bool actReset = CombatUiCommands.Instance.SetActTransitionBossHpStrategy(
            search.ActTransitionBossHpStrategy);
        bool finalReset = CombatUiCommands.Instance.SetFinalBossHpStrategy(search.FinalBossHpStrategy);
        Reload();
        if (actReset && finalReset)
            SetStatus(SolverText.Get("已恢复默认设置"), SolverUiTokens.Palette.Success);
        else
            SetStatus(SolverText.Get("保存失败，详情已写入游戏日志"), SolverUiTokens.Palette.Danger);
        DefaultsRestored?.Invoke();
        OpacityPreview?.Invoke(SolverSettings.Current.Ui.Opacity);
    }

    private bool Save(Func<SolverSettingsData, SolverSettingsData> change, string message)
    {
        if (!SolverSettings.Update(change))
        {
            SetStatus(SolverText.Get("保存失败，详情已写入游戏日志"), SolverUiTokens.Palette.Danger);
            Reload();
            return false;
        }
        SetStatus(message, SolverUiTokens.Palette.Success);
        return true;
    }

    private void ShowInvalid(LineEdit input, string message)
    {
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Danger);
        SetStatus(message, SolverUiTokens.Palette.Danger);
        input.GrabFocus();
    }

    private static void MarkValid(LineEdit input)
        => input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);

    /// <summary>Displays a saved value; an input still showing it has nothing to commit.</summary>
    private static void Show(LineEdit input, ref string shown, string text)
    {
        shown = text;
        if (input.Text != text)
            input.Text = text;
    }

    private void SetStatus(string text, Color color)
    {
        _status.AddThemeColorOverride("font_color", color);
        _status.Text = text;
    }

    private static string Percent(float value)
        => Math.Round(value * 100d).ToString(CultureInfo.InvariantCulture) + "%";

#if STS2_SOLVER_TESTING
    internal Button ExportButtonForTest => _exportLog;

    internal bool ExportPendingForTest => _export != null;

    internal Button ResetButtonForTest => _reset;

    internal OptionButton ActBossOptionForTest => _actBoss;

    internal OptionButton FinalBossOptionForTest => _finalBoss;

    internal string StatusTextForTest => _status.Text;
#endif
}
