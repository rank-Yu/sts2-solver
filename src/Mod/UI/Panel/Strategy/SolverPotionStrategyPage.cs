// Adapted from CombatSolver src/UI/SolverPotionStrategyPanel.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The four presets, the three-state mode button with its cycle order, labels and colors, the empty
// state and the ineligible tooltip are the original ones. Each potion is one row (icon, "#slot name",
// fixed-width mode button) instead of a two-line card; rows come from StrategyUiSnapshot, the presets
// are disabled when no potion takes a policy, and every change goes through ICombatUiCommands and
// only saves for the next search.
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Solver;

/// <summary>The strategy sidebar's potion page. Main thread only.</summary>
internal sealed partial class SolverPotionStrategyPage : VBoxContainer, ISolverSidebarPage
{
    private const float IconSize = 32f;
    private const float RowHeight = 34f;

    private readonly Button _allSmart;
    private readonly Button _allProtect;
    private readonly Button _allForce;
    private readonly Button _onlyForced;
    private readonly VBoxContainer _rows;
    private readonly Label _status;
    private readonly List<PotionRow> _potionRows = [];
    private string? _rowsKey;

    public SolverPotionStrategyPage()
    {
        Name = "PotionStrategyPage";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);

        GridContainer presets = new()
        {
            Name = "PotionPresets",
            Columns = 2,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        presets.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Sm);
        presets.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        _allSmart = CreatePreset(presets, SolverText.Get("全部智能"), "AllSmart",
            () => CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Smart));
        _allProtect = CreatePreset(presets, SolverText.Get("全部保护"), "AllProtected",
            () => CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Disabled));
        _allForce = CreatePreset(presets, SolverText.Get("全部强制"), "AllForced",
            () => CombatUiCommands.Instance.SetAllPotionDirectives(SolverPotionMode.Force));
        _onlyForced = CreatePreset(presets, SolverText.Get("仅用已强制"), "OnlyForced",
            () => CombatUiCommands.Instance.SetOnlyForcedPotionDirectives());
        AddChild(presets);

        _rows = new VBoxContainer
        {
            Name = "PotionRows",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _rows.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        AddChild(SolverSettingsControls.CreatePageScroll(_rows));

        _status = SolverUiTokens.CreateLabel(string.Empty, SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Success);
        _status.Name = "PotionStatus";
        _status.CustomMinimumSize = new Vector2(0, 20);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_status);
    }

    public SolverStrategyPageKind Kind => SolverStrategyPageKind.Potions;

    public string Title => SolverText.Get("药水策略");

    public Control Root => this;

    public void Opened()
    {
        _status.Text = string.Empty;
        Apply(CombatUiStore.Snapshot);
    }

    public void Apply(CombatUiSnapshot snapshot)
    {
        PotionSlotUi[] potions = snapshot.Strategy.Potions;
        string key = string.Join('|', potions.Select(static potion =>
            $"{potion.Slot}:{potion.PotionId}:{potion.CanAssignPolicy}"));
        if (key != _rowsKey)
        {
            Rebuild(potions);
            _rowsKey = key;
        }
        bool anyAssignable = false;
        bool anyForced = false;
        for (int index = 0; index < potions.Length; index++)
        {
            SolverPotionMode mode = ParseMode(potions[index].Mode);
            PotionRow row = _potionRows[index];
            row.Mode = mode;
            ApplyModeButton(row.Button, mode, row.CanAssign);
            anyAssignable |= row.CanAssign;
            anyForced |= row.CanAssign && mode == SolverPotionMode.Force;
        }
        _allSmart.Disabled = !anyAssignable;
        _allProtect.Disabled = !anyAssignable;
        _allForce.Disabled = !anyAssignable;
        _onlyForced.Disabled = !anyForced;
    }

    public bool CommitPending() => true;

    private Button CreatePreset(GridContainer grid, string text, string name, Func<bool> command)
    {
        Button button = SolverUiTokens.CreateButton(text, SolverButtonStyle.Secondary);
        button.Name = name;
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.Pressed += () =>
        {
            bool accepted = command();
            ModLog.Info($"[Sts2Solver] UI_ACTION action=potion_preset preset={name} accepted={accepted}");
            ShowSaved(accepted);
        };
        grid.AddChild(button);
        return button;
    }

    private void Rebuild(PotionSlotUi[] potions)
    {
        foreach (Node child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }
        _potionRows.Clear();
        if (potions.Length == 0)
        {
            Label empty = SolverUiTokens.CreateLabel(SolverText.Get("当前没有药水"),
                SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextMuted);
            empty.Name = "NoPotions";
            empty.CustomMinimumSize = new Vector2(0, 32);
            _rows.AddChild(empty);
            return;
        }
        foreach (PotionSlotUi potion in potions)
            _potionRows.Add(CreateRow(potion));
    }

    private PotionRow CreateRow(PotionSlotUi potion)
    {
        HBoxContainer row = new()
        {
            Name = $"PotionRow{potion.Slot}",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, RowHeight),
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        row.AddChild(new TextureRect
        {
            Name = "PotionIcon",
            Texture = PotionImage(potion.PotionId),
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        });
        Label title = SolverUiTokens.CreateLabel(
            $"#{potion.Slot + 1} {CombatDisplayNames.PotionTitle(potion.PotionId)}",
            SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        title.Name = "PotionTitle";
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(title);
        Button button = SolverUiTokens.CreateButton(string.Empty, SolverButtonStyle.Secondary);
        button.Name = "PotionModeButton";
        button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(button);
        _rows.AddChild(row);
        SolverUiTokens.FitWidestText(button,
            ModeText(SolverPotionMode.Smart), ModeText(SolverPotionMode.Disabled),
            ModeText(SolverPotionMode.Force));
        PotionRow potionRow = new(potion.Slot, potion.PotionId, potion.CanAssignPolicy, button);
        if (potion.CanAssignPolicy)
            button.Pressed += () => CycleMode(potionRow);
        return potionRow;
    }

    private void CycleMode(PotionRow row)
    {
        SolverPotionMode next = row.Mode switch
        {
            SolverPotionMode.Smart => SolverPotionMode.Disabled,
            SolverPotionMode.Disabled => SolverPotionMode.Force,
            _ => SolverPotionMode.Smart,
        };
        bool accepted = CombatUiCommands.Instance.SetPotionDirective(row.Slot, row.PotionId, next);
        ModLog.Info(
            $"[Sts2Solver] UI_ACTION action=potion_mode slot={row.Slot} potion={row.PotionId} " +
            $"mode={next} accepted={accepted}");
        ShowSaved(accepted);
    }

    private void ShowSaved(bool accepted)
    {
        _status.AddThemeColorOverride("font_color",
            accepted ? SolverUiTokens.Palette.Success : SolverUiTokens.Palette.Danger);
        _status.Text = accepted
            ? SolverText.Get("已保存，下次搜索生效")
            : SolverText.Get("保存失败，详情已写入游戏日志");
    }

    private static SolverPotionMode ParseMode(string code) => code switch
    {
        "smart" => SolverPotionMode.Smart,
        "disabled" => SolverPotionMode.Disabled,
        "force" => SolverPotionMode.Force,
        _ => throw new InvalidDataException($"未知的药水策略码：{code}"),
    };

    private static string ModeText(SolverPotionMode mode) => mode switch
    {
        SolverPotionMode.Disabled => "✕  " + SolverText.Get("禁用 / 保护"),
        SolverPotionMode.Force => "✓  " + SolverText.Get("强制使用"),
        _ => "—  " + SolverText.Get("智能使用"),
    };

    private static void ApplyModeButton(Button button, SolverPotionMode mode, bool canAssign)
    {
        Color tone = mode switch
        {
            SolverPotionMode.Disabled => SolverUiTokens.Palette.Danger,
            SolverPotionMode.Force => SolverUiTokens.Palette.Success,
            _ => SolverUiTokens.Palette.TextSecondary,
        };
        button.Text = ModeText(mode);
        button.Disabled = !canAssign;
        button.TooltipText = !canAssign
            ? SolverText.Get("该药水不可指定使用策略")
            : mode == SolverPotionMode.Smart
                ? SolverText.Get("只在能省下足够的血、或不用就赢不了时使用。")
                : string.Empty;
        button.AddThemeColorOverride("font_color", tone);
        button.AddThemeColorOverride("font_hover_color", tone.Lightened(0.12f));
        button.AddThemeColorOverride("font_pressed_color", tone.Darkened(0.12f));
        button.AddThemeColorOverride("font_hover_pressed_color", tone.Darkened(0.12f));
        button.AddThemeColorOverride("font_disabled_color", SolverUiTokens.Palette.TextMuted);
        Color background = SolverUiTokens.Palette.SurfaceRaised;
        Color border = SolverUiTokens.Palette.BorderSubtle;
        button.AddThemeStyleboxOverride("normal", ModeBox(background, border));
        button.AddThemeStyleboxOverride("hover", ModeBox(SolverUiTokens.Palette.SurfaceHover,
            SolverUiTokens.Palette.Border));
        button.AddThemeStyleboxOverride("pressed", ModeBox(background.Darkened(0.12f), border));
        button.AddThemeStyleboxOverride("hover_pressed", ModeBox(background.Darkened(0.12f), border));
        button.AddThemeStyleboxOverride("disabled", ModeBox(SolverUiTokens.Palette.Background, border));
    }

    private static StyleBoxFlat ModeBox(Color background, Color border)
        => SolverUiTokens.CreateBox(background, border, SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm, SolverUiTokens.Spacing.Xs);

    private static Texture2D PotionImage(string nativeId)
        => ModelDb.All.OfType<PotionModel>().FirstOrDefault(potion => potion.Id.Entry == nativeId)?.Image
            ?? throw new InvalidDataException($"找不到药水 {nativeId} 的图标。");

    private sealed class PotionRow(int slot, string potionId, bool canAssign, Button button)
    {
        public int Slot { get; } = slot;

        public string PotionId { get; } = potionId;

        public bool CanAssign { get; } = canAssign;

        public Button Button { get; } = button;

        public SolverPotionMode Mode { get; set; }
    }

#if STS2_SOLVER_TESTING
    internal Button AllSmartButtonForTest => _allSmart;

    internal Button AllProtectButtonForTest => _allProtect;

    internal Button AllForceButtonForTest => _allForce;

    internal Button OnlyForcedButtonForTest => _onlyForced;

    internal string StatusTextForTest => _status.Text;

    internal IReadOnlyList<(int Slot, string PotionId, bool CanAssign, Button Button)> RowsForTest
        => [.. _potionRows.Select(static row => (row.Slot, row.PotionId, row.CanAssign, row.Button))];
#endif
}
