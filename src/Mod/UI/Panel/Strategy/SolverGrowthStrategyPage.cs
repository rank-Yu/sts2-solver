// Adapted from CombatSolver src/UI/SolverGrowthStrategyPanel.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The row order, the ten built-in sources with their card portraits and names (Goopy as the
// enchantment plus Defend, Mad Science with the Improvement rider), the two switches, the flame
// limit's default of 2 and the greyed budgets under "ignore" are the original ones. Budgets are
// text boxes with an HP suffix that accept integers 0–1000 instead of SpinBoxes, the tooltips are
// shortened, third-party sources are not listed, and every change goes through ICombatUiCommands
// and only saves for the next search.
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>The strategy sidebar's growth page. Main thread only.</summary>
internal sealed partial class SolverGrowthStrategyPage : VBoxContainer, ISolverSidebarPage
{
    private const int MinimumHp = 0;
    private const int MaximumHp = 1000;
    private const int DefaultFlameLimit = 2;
    private const float PortraitSize = 32f;
    private const float RowHeight = 32f;
    private const float GreyedPortraitAlpha = 0.45f;

    private readonly CheckButton _ignore;
    private readonly CheckButton _limitFlame;
    private readonly HpRow _flame;
    private readonly HpRow[] _budgets;
    private readonly Label _status;

    public SolverGrowthStrategyPage()
    {
        Name = "GrowthStrategyPage";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);

        VBoxContainer content = SolverSettingsControls.CreatePageContent("GrowthContent");
        VBoxContainer top = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        top.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _ignore = AddSwitchRow(top, SolverText.Get("不考虑局外收益"), "IgnoreLongTermRewards");
        CardModel flame = ModelDb.Card<BrightestFlame>();
        _limitFlame = AddSwitchRow(top, SolverText.Format($"限制{flame.Title}的最大生命消耗"),
            "LimitBrightestFlame");
        string flameTip = SolverText.Get("0 表示求解器不再使用；已手动消耗的额度也计入。");
        _flame = AddHpRow(top, flame.Portrait, SolverText.Get("每场最多消耗"),
            "BrightestFlameMaxHpLossLimit", flameTip, labelTooltip: true,
            value => CombatUiCommands.Instance.SetBrightestFlameLimit(value));
        content.AddChild(top);

        VBoxContainer section = new()
        {
            Name = "GrowthBudgets",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        section.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        string budgetTip = SolverText.Get("0 仍优先获取同等战损下的收益；多次成功触发逐次累计。");
        HBoxContainer heading = SolverSettingsControls.CreateSectionHeading(
            SolverText.Get("每次收益允许的额外战损"));
        heading.TooltipText = budgetTip;
        heading.MouseFilter = MouseFilterEnum.Pass;
        section.AddChild(heading);
        GrowthSource[] sources = Enum.GetValues<GrowthSource>();
        _budgets = new HpRow[sources.Length];
        foreach (GrowthSource source in sources)
        {
            CardModel card = SourceCard(source);
            string title = source == GrowthSource.Goopy
                ? ModelDb.Enchantment<Goopy>().Title.GetFormattedText()
                    + (SolverText.IsEnglish ? " " : "") + card.Title
                : card.Title;
            _budgets[(int)source] = AddHpRow(section, card.Portrait, title, source.ToString(), budgetTip,
                labelTooltip: false, value => CombatUiCommands.Instance.SetGrowthBudget(source, value));
        }
        content.AddChild(section);
        AddChild(SolverSettingsControls.CreatePageScroll(content));

        _status = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Success);
        _status.Name = "GrowthStatus";
        _status.CustomMinimumSize = new Vector2(0, 20);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_status);

        _ignore.Toggled += OnIgnoreToggled;
        _limitFlame.Toggled += OnFlameLimitToggled;
    }

    public SolverStrategyPageKind Kind => SolverStrategyPageKind.Growth;

    public string Title => SolverText.Get("成长策略");

    public Control Root => this;

    public void Opened()
    {
        _status.Text = string.Empty;
        Apply(CombatUiStore.Snapshot);
    }

    /// <summary>Shows the saved strategy; a box being edited keeps its text.</summary>
    public void Apply(CombatUiSnapshot snapshot)
    {
        StrategyUiSnapshot strategy = snapshot.Strategy;
        _ignore.SetPressedNoSignal(strategy.IgnoreLongTermRewards);
        _limitFlame.SetPressedNoSignal(strategy.BrightestFlameMaxHpLossLimit.HasValue);
        ApplyRow(_flame, strategy.BrightestFlameMaxHpLossLimit ?? DefaultFlameLimit,
            strategy.BrightestFlameMaxHpLossLimit.HasValue);
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            ApplyRow(_budgets[(int)source], Budget(strategy.GrowthBudgets, source),
                !strategy.IgnoreLongTermRewards);
        }
    }

    public bool CommitPending()
    {
        if (!Commit(_flame))
            return false;
        foreach (HpRow row in _budgets)
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

    private static CardModel SourceCard(GrowthSource source) => source switch
    {
        GrowthSource.HandOfGreed => ModelDb.Card<HandOfGreed>(),
        GrowthSource.TheHunt => ModelDb.Card<TheHunt>(),
        GrowthSource.Feed => ModelDb.Card<Feed>(),
        GrowthSource.Royalties => ModelDb.Card<Royalties>(),
        GrowthSource.Alchemize => ModelDb.Card<Alchemize>(),
        GrowthSource.GeneticAlgorithm => ModelDb.Card<GeneticAlgorithm>(),
        GrowthSource.TheScythe => ModelDb.Card<TheScythe>(),
        GrowthSource.Goopy => ModelDb.Card<DefendIronclad>(),
        GrowthSource.ForbiddenGrimoire => ModelDb.Card<ForbiddenGrimoire>(),
        GrowthSource.MadScience => ImprovementMadScience(),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static MadScience ImprovementMadScience()
    {
        MadScience card = (MadScience)ModelDb.Card<MadScience>().ToMutable();
        card.TinkerTimeType = CardType.Power;
        card.TinkerTimeRider = TinkerTime.RiderEffect.Improvement;
        return card;
    }

    private static int Budget(SearchGrowthBudget budgets, GrowthSource source) => source switch
    {
        GrowthSource.HandOfGreed => budgets.HandOfGreed,
        GrowthSource.TheHunt => budgets.TheHunt,
        GrowthSource.Feed => budgets.Feed,
        GrowthSource.Royalties => budgets.Royalties,
        GrowthSource.Alchemize => budgets.Alchemize,
        GrowthSource.GeneticAlgorithm => budgets.GeneticAlgorithm,
        GrowthSource.TheScythe => budgets.TheScythe,
        GrowthSource.Goopy => budgets.Goopy,
        GrowthSource.ForbiddenGrimoire => budgets.ForbiddenGrimoire,
        GrowthSource.MadScience => budgets.MadScience,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static CheckButton AddSwitchRow(VBoxContainer parent, string text, string name)
    {
        HBoxContainer row = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, RowHeight),
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        Label label = SolverUiTokens.CreateLabel(text, SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextSecondary);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(label);
        CheckButton toggle = SolverSettingsControls.CreateToggle();
        toggle.Name = name;
        row.AddChild(toggle);
        parent.AddChild(row);
        return toggle;
    }

    private HpRow AddHpRow(VBoxContainer parent, Texture2D portrait, string title, string name,
        string tooltip, bool labelTooltip, Func<int, bool> save)
    {
        HBoxContainer row = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, RowHeight),
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        TextureRect picture = new()
        {
            Texture = portrait,
            CustomMinimumSize = new Vector2(PortraitSize, PortraitSize),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddChild(picture);
        Label label = SolverUiTokens.CreateLabel(title, SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        if (labelTooltip)
        {
            label.TooltipText = tooltip;
            label.MouseFilter = MouseFilterEnum.Pass;
        }
        row.AddChild(label);
        (PanelContainer box, LineEdit edit) = SolverSettingsControls.CreateHpInput();
        box.Name = name;
        box.TooltipText = tooltip;
        edit.TooltipText = tooltip;
        row.AddChild(box);
        parent.AddChild(row);
        HpRow hpRow = new(name, picture, label, edit, save);
        edit.TextSubmitted += _ => Commit(hpRow);
        edit.FocusExited += () => Commit(hpRow);
        edit.TextChanged += _ => ColorNumber(hpRow);
        return hpRow;
    }

    private static void ApplyRow(HpRow row, int value, bool enabled)
    {
        if (!enabled && row.Edit.HasFocus())
            row.Edit.ReleaseFocus();
        row.Edit.Editable = enabled;
        row.Edit.FocusMode = enabled ? FocusModeEnum.All : FocusModeEnum.None;
        row.Label.AddThemeColorOverride("font_color",
            enabled ? SolverUiTokens.Palette.TextPrimary : SolverUiTokens.Palette.TextMuted);
        row.Portrait.Modulate = new Color(1f, 1f, 1f, enabled ? 1f : GreyedPortraitAlpha);
        if (!row.Edit.HasFocus())
            Show(row, value);
    }

    private bool Commit(HpRow row)
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
        bool accepted = row.Save(value);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=growth_value row={row.Name} value={value} accepted={accepted}");
        ShowSaved(accepted);
        if (!accepted)
            Apply(CombatUiStore.Snapshot);
        return accepted;
    }

    private void OnIgnoreToggled(bool ignore)
    {
        bool accepted = CombatUiCommands.Instance.SetIgnoreLongTermRewards(ignore);
        ModLog.Info($"[Sts2Solver] UI_ACTION action=ignore_long_term_rewards value={ignore} accepted={accepted}");
        ShowSaved(accepted);
        Apply(CombatUiStore.Snapshot);
    }

    private void OnFlameLimitToggled(bool limited)
    {
        int? limit = limited ? DefaultFlameLimit : null;
        bool accepted = CombatUiCommands.Instance.SetBrightestFlameLimit(limit);
        ModLog.Info(
            $"[Sts2Solver] UI_ACTION action=brightest_flame_limit value={limit?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
            $"accepted={accepted}");
        ShowSaved(accepted);
        Apply(CombatUiStore.Snapshot);
    }

    private void ShowSaved(bool accepted)
        => SetStatus(accepted
                ? SolverText.Get("已保存，下次搜索生效")
                : SolverText.Get("保存失败，详情已写入游戏日志"),
            accepted ? SolverUiTokens.Palette.Success : SolverUiTokens.Palette.Danger);

    private void SetStatus(string text, Color color)
    {
        _status.AddThemeColorOverride("font_color", color);
        _status.Text = text;
    }

    /// <summary>Displays a saved value; a box still showing it has nothing to commit.</summary>
    private static void Show(HpRow row, int value)
    {
        row.Shown = value.ToString(CultureInfo.InvariantCulture);
        if (row.Edit.Text != row.Shown)
            row.Edit.Text = row.Shown;
        ColorNumber(row);
    }

    /// <summary>Zero is shown muted like the mockup; other numbers use the primary text color.</summary>
    private static void ColorNumber(HpRow row)
        => row.Edit.AddThemeColorOverride("font_color", row.Edit.Text.Trim() == "0"
            ? SolverUiTokens.Palette.TextMuted
            : SolverUiTokens.Palette.TextPrimary);

    private sealed class HpRow(string name, TextureRect portrait, Label label, LineEdit edit,
        Func<int, bool> save)
    {
        public string Name { get; } = name;

        public TextureRect Portrait { get; } = portrait;

        public Label Label { get; } = label;

        public LineEdit Edit { get; } = edit;

        public Func<int, bool> Save { get; } = save;

        public string Shown { get; set; } = string.Empty;
    }

#if STS2_SOLVER_TESTING
    internal CheckButton IgnoreToggleForTest => _ignore;

    internal CheckButton FlameLimitToggleForTest => _limitFlame;

    internal LineEdit FlameLimitInputForTest => _flame.Edit;

    internal LineEdit BudgetInputForTest(GrowthSource source) => _budgets[(int)source].Edit;

    internal string StatusTextForTest => _status.Text;
#endif
}
