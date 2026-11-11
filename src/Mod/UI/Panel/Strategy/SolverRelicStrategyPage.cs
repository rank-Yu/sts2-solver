// The relic list follows CombatSolver src/UI/SolverRelicStrategyPanel.cs at 5c773caa (MIT License,
// Copyright (c) 2026 Torch1230 and contributors): the owned counter relics with their native icons.
// Each row has its own switch, off by default, and one allowance laid out like this Mod's growth page;
// the target is fixed at one count short of triggering and shown in the row, so the original's range,
// priority and unowned list are not ported. There is no page-wide switch: with every row off the
// search ignores relic counters.
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Solver;

/// <summary>The strategy sidebar's relic page. Main thread only.</summary>
internal sealed partial class SolverRelicStrategyPage : VBoxContainer, ISolverSidebarPage
{
    private const int MinimumHp = 0;
    private const int MaximumHp = 1000;
    private const float IconSize = 32f;
    private const float RowHeight = 32f;
    private const float GreyedIconAlpha = 0.45f;

    private readonly HBoxContainer _heading;
    private readonly VBoxContainer _rows;
    private readonly Label _status;
    private readonly List<RelicRow> _relicRows = [];
    private string? _builtRelics;

    public SolverRelicStrategyPage()
    {
        Name = "RelicStrategyPage";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);

        VBoxContainer content = SolverSettingsControls.CreatePageContent("RelicContent");
        _heading = SolverSettingsControls.CreateSectionHeading(
            SolverText.Get("卡遗物计数允许的额外战损"));
        _heading.Name = "RelicHeading";
        content.AddChild(_heading);
        _rows = new VBoxContainer
        {
            Name = "RelicRows",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _rows.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        content.AddChild(_rows);
        AddChild(SolverSettingsControls.CreatePageScroll(content));

        _status = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Success);
        _status.Name = "RelicStatus";
        _status.CustomMinimumSize = new Vector2(0, 20);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_status);
    }

    public SolverStrategyPageKind Kind => SolverStrategyPageKind.Relics;

    public string Title => SolverText.Get("遗物策略");

    public Control Root => this;

    public void Opened()
    {
        _status.Text = string.Empty;
        Apply(CombatUiStore.Snapshot);
    }

    /// <summary>
    /// Shows the owned counter relics with their targets, switches and saved allowances; a box being
    /// edited keeps its text. A relic that is switched off is greyed.
    /// </summary>
    public void Apply(CombatUiSnapshot snapshot)
    {
        StrategyUiSnapshot strategy = snapshot.Strategy;
        string relics = string.Join(',', strategy.RelicCounters.Select(
            static counter => $"{counter.Slot}:{counter.RelicId}"));
        if (relics != _builtRelics)
            Rebuild(strategy.RelicCounters, relics);
        foreach (RelicRow row in _relicRows)
        {
            RelicCounterUi counter = Array.Find(strategy.RelicCounters, item => item.Slot == row.Slot)!;
            row.Count.Text = SolverText.Format($"目标 {counter.Target}/{counter.Period}");
            bool on = Array.IndexOf(strategy.RelicCounterRelics, row.RelicId) >= 0;
            row.Toggle.SetPressedNoSignal(on);
            ApplyRow(row, strategy.RelicCounterAllowances.TryGetValue(row.RelicId, out int hp) ? hp : 0, on);
        }
    }

    public bool CommitPending()
    {
        foreach (RelicRow row in _relicRows)
        {
            if (!Commit(row))
                return false;
        }
        return true;
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

    private void Rebuild(RelicCounterUi[] counters, string relics)
    {
        foreach (Node child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }
        _relicRows.Clear();
        _builtRelics = relics;
        _heading.Visible = counters.Length != 0;
        if (counters.Length == 0)
        {
            Label empty = SolverUiTokens.CreateLabel(SolverText.Get("当前没有计数遗物"),
                SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextMuted);
            empty.Name = "NoRelics";
            empty.CustomMinimumSize = new Vector2(0, RowHeight);
            _rows.AddChild(empty);
            return;
        }
        foreach (RelicCounterUi counter in counters)
            _relicRows.Add(CreateRow(counter));
    }

    private RelicRow CreateRow(RelicCounterUi counter)
    {
        HBoxContainer row = new()
        {
            Name = $"RelicRow{counter.Slot}",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, RowHeight),
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        TextureRect icon = new()
        {
            Name = "RelicIcon",
            Texture = RelicIcon(counter.RelicId),
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddChild(icon);
        Label title = SolverUiTokens.CreateLabel(CombatDisplayNames.RelicTitle(counter.RelicId),
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        title.Name = "RelicTitle";
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(title);
        Label count = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary);
        count.Name = "RelicCount";
        count.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(count);
        CheckButton toggle = SolverSettingsControls.CreateToggle();
        toggle.Name = "RelicEnabled";
        toggle.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(toggle);
        (PanelContainer box, LineEdit edit) = SolverSettingsControls.CreateHpInput();
        box.Name = counter.RelicId;
        row.AddChild(box);
        _rows.AddChild(row);
        RelicRow relicRow = new(counter.Slot, counter.RelicId, icon, title, count, toggle, edit);
        toggle.Toggled += pressed => SetEnabled(relicRow, pressed);
        edit.TextSubmitted += _ => Commit(relicRow);
        edit.FocusExited += () => Commit(relicRow);
        edit.TextChanged += _ => ColorNumber(relicRow);
        return relicRow;
    }

    private static Texture2D RelicIcon(string nativeId)
        => ModelDb.All.OfType<RelicModel>().FirstOrDefault(relic => relic.Id.Entry == nativeId)?.Icon
            ?? throw new InvalidDataException($"找不到遗物 {nativeId} 的图标。");

    private static void ApplyRow(RelicRow row, int value, bool enabled)
    {
        if (!enabled && row.Edit.HasFocus())
            row.Edit.ReleaseFocus();
        row.Edit.Editable = enabled;
        row.Edit.FocusMode = enabled ? FocusModeEnum.All : FocusModeEnum.None;
        row.Title.AddThemeColorOverride("font_color",
            enabled ? SolverUiTokens.Palette.TextPrimary : SolverUiTokens.Palette.TextMuted);
        row.Icon.Modulate = new Color(1f, 1f, 1f, enabled ? 1f : GreyedIconAlpha);
        if (!row.Edit.HasFocus())
            Show(row, value);
    }

    private void SetEnabled(RelicRow row, bool enabled)
    {
        bool accepted = CombatUiCommands.Instance.SetRelicCounterEnabled(row.RelicId, enabled);
        ModLog.Info(
            $"[Sts2Solver] UI_ACTION action=relic_enabled relic={row.RelicId} enabled={enabled} accepted={accepted}");
        SetStatus(accepted
                ? SolverText.Get("已保存，下次搜索生效")
                : SolverText.Get("保存失败，详情已写入游戏日志"),
            accepted ? SolverUiTokens.Palette.Success : SolverUiTokens.Palette.Danger);
        Apply(CombatUiStore.Snapshot);
    }

    private bool Commit(RelicRow row)
    {
        if (row.Edit.Text == row.Shown)
            return true;
        if (!int.TryParse(row.Edit.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                out int value) || value > MaximumHp)
        {
            row.Edit.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Danger);
            SetStatus(SolverText.Format($"请输入 {MinimumHp}–{MaximumHp} 的整数"),
                SolverUiTokens.Palette.Danger);
            row.Edit.GrabFocus();
            return false;
        }
        Show(row, value);
        bool accepted = CombatUiCommands.Instance.SetRelicCounterAllowance(row.RelicId, value);
        ModLog.Info(
            $"[Sts2Solver] UI_ACTION action=relic_allowance relic={row.RelicId} value={value} accepted={accepted}");
        SetStatus(accepted
                ? SolverText.Get("已保存，下次搜索生效")
                : SolverText.Get("保存失败，详情已写入游戏日志"),
            accepted ? SolverUiTokens.Palette.Success : SolverUiTokens.Palette.Danger);
        if (!accepted)
            Apply(CombatUiStore.Snapshot);
        return accepted;
    }

    private void SetStatus(string text, Color color)
    {
        _status.AddThemeColorOverride("font_color", color);
        _status.Text = text;
    }

    /// <summary>Displays a saved value; a box still showing it has nothing to commit.</summary>
    private static void Show(RelicRow row, int value)
    {
        row.Shown = value.ToString(CultureInfo.InvariantCulture);
        if (row.Edit.Text != row.Shown)
            row.Edit.Text = row.Shown;
        ColorNumber(row);
    }

    /// <summary>Zero is shown muted like the growth page; other numbers use the primary text color.</summary>
    private static void ColorNumber(RelicRow row)
        => row.Edit.AddThemeColorOverride("font_color", row.Edit.Text.Trim() == "0"
            ? SolverUiTokens.Palette.TextMuted
            : SolverUiTokens.Palette.TextPrimary);

    private sealed class RelicRow(int slot, string relicId, TextureRect icon, Label title, Label count,
        CheckButton toggle, LineEdit edit)
    {
        public int Slot { get; } = slot;

        public string RelicId { get; } = relicId;

        public TextureRect Icon { get; } = icon;

        public Label Title { get; } = title;

        public Label Count { get; } = count;

        public CheckButton Toggle { get; } = toggle;

        public LineEdit Edit { get; } = edit;

        public string Shown { get; set; } = string.Empty;
    }

#if STS2_SOLVER_TESTING
    internal int RowCountForTest => _relicRows.Count;

    internal bool EmptyShownForTest => _rows.GetNodeOrNull("NoRelics") != null;

    internal LineEdit AllowanceInputForTest(int slot) => _relicRows.Single(row => row.Slot == slot).Edit;

    internal CheckButton EnabledToggleForTest(int slot) => _relicRows.Single(row => row.Slot == slot).Toggle;

    internal string CountTextForTest(int slot) => _relicRows.Single(row => row.Slot == slot).Count.Text;

    internal string TitleTextForTest(int slot) => _relicRows.Single(row => row.Slot == slot).Title.Text;

    internal string StatusTextForTest => _status.Text;
#endif
}
