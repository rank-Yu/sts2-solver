using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

/// <summary>How the player configured the thief encounter; <see cref="Auto"/> keeps the root capture.</summary>
internal enum SolverTheftMode
{
    Auto,
    PreserveResources,
    LetEscape,
}

internal enum SolverPotionPreset
{
    Smart,
    ProtectAll,
    ForceAll,
    OnlyForced,
}

internal enum SolverUiTheme
{
    Dark,
    Light,
}

/// <summary>The persisted three-position automation mode.</summary>
internal enum SolverMode
{
    Manual,
    AutoSearch,
    FullAuto,
}

/// <summary>How deployment overrides the game's battle speed; <see cref="FollowGame"/> keeps it.</summary>
internal enum SolverDeploymentSpeed
{
    FollowGame,
    Normal,
    Fast,
}

/// <summary>
/// The complete persisted player configuration. Values are immutable; the store replaces one whole
/// record at a time. Search settings are stored here but only the tier and memory override are wired
/// into the request yet.
/// </summary>
internal sealed record SolverSettingsData
{
    internal const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public SolverSearchSettings Search { get; init; } = new();

    public SolverAutomationSettings Automation { get; init; } = new();

    public SolverUiSettings Ui { get; init; } = new();

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"不支持的设置版本：{SchemaVersion}");
        if (Search is null)
            throw new InvalidDataException("设置缺少 search 分区。");
        if (Automation is null)
            throw new InvalidDataException("设置缺少 automation 分区。");
        if (Ui is null)
            throw new InvalidDataException("设置缺少 ui 分区。");
        Search.Validate();
        ValidateAutomation();
        Ui.Validate();
    }

    private void ValidateAutomation()
    {
        if (!Enum.IsDefined(Automation.Mode))
            throw new InvalidDataException($"未知自动化模式：{Automation.Mode}");
        if (!Enum.IsDefined(Automation.DeploymentSpeed))
            throw new InvalidDataException($"未知自动出牌速度：{Automation.DeploymentSpeed}");
        if (!double.IsFinite(Automation.InterActionDelaySeconds)
            || Automation.InterActionDelaySeconds is < 0d or > 3d)
            throw new InvalidDataException(
                $"牌间额外停顿必须在 0..3 秒：{Automation.InterActionDelaySeconds}");
    }
}

/// <summary>Search-side configuration, including strategy values that are not sent to the solver yet.</summary>
internal sealed record SolverSearchSettings
{
    public SearchPerformanceTier Tier { get; init; } = SearchPerformanceTier.Medium;

    /// <summary>Explicit search ceiling; null means environment variable then the 20% default.</summary>
    public long? MemoryBudgetBytes { get; init; }

    /// <summary>Explicit thread count for one member; null means the logical-core default.</summary>
    public int? Threads { get; init; }

    public SolverPotionSettings Potions { get; init; } = new();

    public SearchGrowthBudget GrowthBudgets { get; init; }

    public bool IgnoreLongTermRewards { get; init; }

    public SearchRelicCounterTarget[] RelicTargets { get; init; } = [];

    public SearchBossHpStrategy ActTransitionBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    public SearchBossHpStrategy FinalBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    public SolverTheftMode Theft { get; init; } = SolverTheftMode.Auto;

    internal void Validate()
    {
        if (!Enum.IsDefined(Tier))
            throw new InvalidDataException($"未知搜索档位：{Tier}");
        if (Potions is null)
            throw new InvalidDataException("设置缺少 search.potions 分区。");
        if (RelicTargets is null)
            throw new InvalidDataException("设置缺少 search.relicTargets。");
        if (MemoryBudgetBytes is <= 0)
            throw new InvalidDataException($"搜索内存上限必须为正：{MemoryBudgetBytes}");
        if (Threads is < 1 or > 8)
            throw new InvalidDataException($"搜索线程数必须在 1..8：{Threads}");
        Potions.Validate();
        GrowthBudgets.ToCore().ValidateBudgets();
        if (!Enum.IsDefined(ActTransitionBossHpStrategy))
            throw new InvalidDataException($"未知幕末 Boss 血量策略：{ActTransitionBossHpStrategy}");
        if (!Enum.IsDefined(FinalBossHpStrategy))
            throw new InvalidDataException($"未知最终 Boss 血量策略：{FinalBossHpStrategy}");
        if (!Enum.IsDefined(Theft))
            throw new InvalidDataException($"未知盗贼策略：{Theft}");
        HashSet<SearchRelicCounter> ids = [];
        foreach (SearchRelicCounterTarget target in RelicTargets)
        {
            if (!Enum.IsDefined(target.Id))
                throw new InvalidDataException($"未知遗物计数目标：{target.Id}");
            if (!ids.Add(target.Id))
                throw new InvalidDataException($"重复的遗物计数目标：{target.Id}");
            if (target.Minimum is < 0 or > 15 || target.Maximum is < 0 or > 15
                || target.Minimum > target.Maximum)
                throw new InvalidDataException(
                    $"遗物计数目标 {target.Id} 的范围必须在 0..15 且最小值不大于最大值：{target.Minimum}..{target.Maximum}");
            if (target.Period is < 1 or > 15)
                throw new InvalidDataException($"遗物计数目标 {target.Id} 的周期必须在 1..15：{target.Period}");
            if (target.HpAllowance < 0)
                throw new InvalidDataException($"遗物计数目标 {target.Id} 的允许战损不能为负。");
            if (target.Priority is < 1 or > 8)
                throw new InvalidDataException($"遗物计数目标 {target.Id} 的优先级必须在 1..8：{target.Priority}");
        }
    }

    internal SearchTheftPolicy? TheftPolicyOrAuto()
        => Theft switch
        {
            SolverTheftMode.Auto => null,
            SolverTheftMode.PreserveResources => SearchTheftPolicy.PreserveResources,
            SolverTheftMode.LetEscape => SearchTheftPolicy.LetEscape,
            _ => throw new ArgumentOutOfRangeException(nameof(Theft)),
        };
}

internal sealed record SolverPotionSettings
{
    public PotionUseMode DefaultMode { get; init; } = PotionUseMode.Smart;

    public SolverPotionPreset Preset { get; init; } = SolverPotionPreset.Smart;

    public PotionSlotDirective[] Directives { get; init; } = [];

    internal void Validate()
    {
        if (!Enum.IsDefined(DefaultMode))
            throw new InvalidDataException($"未知药水默认策略：{DefaultMode}");
        if (Directives is null)
            throw new InvalidDataException("设置缺少 search.potions.directives。");
        if (!Enum.IsDefined(Preset))
            throw new InvalidDataException($"未知药水预设：{Preset}");
        HashSet<(int Slot, string PotionId)> seen = [];
        foreach (PotionSlotDirective directive in Directives)
        {
            if (directive.Slot is < 0 or >= Limits.PotionSlots)
                throw new InvalidDataException(
                    $"药水槽位必须在 0..{Limits.PotionSlots - 1}：{directive.Slot}");
            if (string.IsNullOrEmpty(directive.PotionId))
                throw new InvalidDataException($"药水槽 {directive.Slot} 的策略缺少药水 id。");
            if (!Enum.IsDefined(directive.Directive))
                throw new InvalidDataException($"未知药水槽策略：{directive.Directive}");
            if (!seen.Add((directive.Slot, directive.PotionId)))
                throw new InvalidDataException(
                    $"重复的药水槽策略：slot={directive.Slot} potion={directive.PotionId}");
        }
    }
}

internal sealed record SolverAutomationSettings
{
    /// <summary>Manual, automatic search or full automatic; persisted across combats and restarts.</summary>
    public SolverMode Mode { get; init; } = SolverMode.AutoSearch;

    /// <summary>Battle speed override while the solver plays cards; follow the game by default.</summary>
    public SolverDeploymentSpeed DeploymentSpeed { get; init; } = SolverDeploymentSpeed.FollowGame;

    /// <summary>Extra pause between two played cards, 0..3 seconds.</summary>
    public double InterActionDelaySeconds { get; init; }
}

/// <summary>Panel geometry and appearance for the overlay; the panel itself is built separately.</summary>
internal sealed record SolverUiSettings
{
    public float? PanelX { get; init; }

    public float? PanelY { get; init; }

    public float? PanelWidth { get; init; }

    public float? PanelHeight { get; init; }

    public bool Collapsed { get; init; }

    public float Opacity { get; init; } = 0.65f;

    public SolverUiTheme Theme { get; init; } = SolverUiTheme.Dark;

    /// <summary>Show the significant-battle-damage hint until the player dismisses it.</summary>
    public bool ShowBattleDamagePerformanceHint { get; init; } = true;

    internal void Validate()
    {
        if ((PanelX is null) != (PanelY is null))
            throw new InvalidDataException("面板位置必须同时给出 x 和 y。");
        if ((PanelWidth is null) != (PanelHeight is null))
            throw new InvalidDataException("面板尺寸必须同时给出宽和高。");
        if (PanelX is { } x && (!float.IsFinite(x) || PanelY is not { } y || !float.IsFinite(y)))
            throw new InvalidDataException("面板位置必须是有限数。");
        if (PanelWidth is { } width)
        {
            if (!float.IsFinite(width) || width <= 0
                || PanelHeight is not { } height || !float.IsFinite(height) || height <= 0)
                throw new InvalidDataException("面板尺寸必须是有限正数。");
        }
        if (!float.IsFinite(Opacity) || Opacity is < 0.25f or > 1f)
            throw new InvalidDataException($"透明度必须在 0.25..1：{Opacity}");
        if (!Enum.IsDefined(Theme))
            throw new InvalidDataException($"未知界面主题：{Theme}");
    }
}
