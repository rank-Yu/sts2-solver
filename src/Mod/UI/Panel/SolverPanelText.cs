using System.Globalization;
using Sts2Solver.Import;

namespace Sts2Solver;

/// <summary>
/// Maps the snapshot's stable codes (phase, end reason, error, pause, stop and member labels) to
/// localized player text. Unknown codes are shown verbatim inside a localized template, so a new
/// code is visible instead of being mistaken for a known one.
/// </summary>
internal static class SolverPanelText
{
    private static readonly IReadOnlyDictionary<string, string> ErrorNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["import_failed"] = "搜索初始化失败",
        ["import_unsupported"] = "搜索初始化失败",
        ["import_capacity"] = "搜索初始化失败",
        ["request_invalid"] = "搜索请求无效",
        ["route_invalid"] = "搜索结果无法回放，已丢弃",
        ["route_unsupported"] = "路线含有尚未支持的效果",
        ["route_capacity"] = "路线回放超出状态容量",
        ["search_failed"] = "搜索失败",
        ["forced_potion_unusable"] = "找不到用掉所有“强制使用”药水的路线，请在药水策略里调整后重新搜索",
        ["turn_setup_failed"] = "回合准备选牌失败",
        ["boundary_deviation"] = "回合开始时实际状态与预测不同，已停止",
        ["action_deviation"] = "动作结果与预测不同，已停止",
        ["deploy_failed"] = "自动执行中止",
        ["cancelled"] = "搜索已取消",
        ["pilot_failed"] = "全自动异常结束",
        ["unsupported_import"] = "当前战斗含有尚未支持的内容",
        ["unsupported"] = "遇到尚未支持的游戏语义",
        ["capacity"] = "超出求解器的状态容量",
        ["not_supported"] = "遇到尚未支持的游戏语义",
        ["invalid_data"] = "战斗数据校验失败",
        ["invalid_operation"] = "当前状态不允许继续全自动",
        ["timeout"] = "等待游戏响应超时",
    };

    private static readonly IReadOnlyDictionary<string, string> StopNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["time"] = "用完时间",
        ["memory"] = "达到内存上限",
        ["frontier-empty"] = "搜索树已穷尽",
        ["deadline"] = "复核超时",
        ["invalid"] = "结果无效",
        ["cancelled"] = "已取消",
        ["skipped"] = "跳过",
        ["none"] = "没有结果",
        ["zero-damage-victory"] = "零失血胜利",
        ["budget"] = "节点预算用完",
        ["max-turns"] = "达到回合上限",
        ["ok"] = "完成",
        ["failed"] = "失败",
        ["unsupported"] = "不支持",
        ["capacity"] = "容量不足",
        ["unknown"] = "未知",
    };

    private static readonly IReadOnlyDictionary<string, string> SkipNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["no-allowed-potions"] = "没有可用药水",
        ["limit-already-used"] = "用药次数已满",
        ["duplicate-limit"] = "与已运行的轮次重复",
        ["stage-economy"] = "用药不划算",
        ["invalid"] = "前面的轮次出错",
        ["cancelled"] = "已取消",
        ["target-reached"] = "目标已达成",
        ["memory"] = "内存已满",
    };

    private static readonly IReadOnlyDictionary<string, string> EvaluationNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Standard"] = "标准",
        ["Base"] = "基础分",
        ["Offensive"] = "进攻",
        ["Setup"] = "能力展开",
        ["ContinuousSetup"] = "连续增益",
    };

    private static readonly IReadOnlyDictionary<string, string> RetentionNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Standard"] = "标准",
        ["SecondBand"] = "第二排名段",
        ["Power"] = "能力优先",
        ["QueenKillTiming"] = "女王击杀时机",
    };

    private static readonly IReadOnlyDictionary<string, string> PotionLimitNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["None"] = "无药",
        ["One"] = "最多一瓶",
        ["Allowed"] = "放开",
    };

    private static readonly IReadOnlyDictionary<string, string> PauseNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["action_deviation"] = "动作结果与预测不同，已停止",
        ["boundary_deviation"] = "回合开始时实际状态与预测不同，已停止",
        ["deployment_deviation"] = "实际局面与路线不符，已停止",
        ["deploy_failed"] = "自动执行中止",
    };

    /// <summary>Stable import rejection reasons (see ImportReason).</summary>
    private static readonly IReadOnlyDictionary<string, string> ImportReasonNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["multi-player"] = "当前是多人游戏，求解器只支持单人战斗",
        ["root-phase"] = "当前不是玩家出牌阶段",
        ["pending-choice"] = "还有未完成的选牌",
        ["unsupported-closure"] = "战斗中有尚未支持的卡牌效果或能力组合",
        ["missing-mirror-risk"] = "无法确定镜像复制会复制哪张牌",
        ["unrepresentable-state"] = "当前战斗状态无法表示",
        ["capacity"] = "战斗内容超出求解器容量",
        ["third-party-content"] = "战斗中含有第三方 Mod 内容",
        ["missing-private-field"] = "无法读取游戏的内部战斗状态",
        ["unclaimed-private-state"] = "战斗状态含有求解器未接管的私有数据",
        ["rules-mismatch"] = "游戏规则与求解器预期不一致",
        ["known-enemy-history"] = "怪物的行动历史无法表示",
        ["prefix-plan"] = "开局前缀路线无法表示",
        ["turn-setup-root"] = "回合开始选择无法组成搜索请求",
        ["run-deck-hook"] = "牌组上的局外效果无法表示",
        ["test-content"] = "战斗中含有测试内容",
        ["deprecated-content"] = "战斗中含有已废弃内容",
        ["unsupported-policy"] = "搜索策略无法表示",
        ["unsupported-healing-certificate"] = "治疗上限无法表示",
    };

    /// <summary>Fallback category sentences for unsupported content without a resolvable native id.</summary>
    private static readonly IReadOnlyDictionary<string, string> ContentFallbackNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unsupported-card"] = "战斗中有尚未支持的卡牌",
            ["unsupported-power"] = "战斗中有尚未支持的能力",
            ["unsupported-relic"] = "战斗中有尚未支持的遗物",
            ["unsupported-potion"] = "战斗中有尚未支持的药水",
            ["unsupported-orb"] = "战斗中有尚未支持的充能球",
            ["unsupported-monster"] = "战斗中有尚未支持的怪物",
            ["unsupported-enchantment"] = "战斗中有尚未支持的附魔",
            ["unsupported-affliction"] = "战斗中有尚未支持的灾厄",
        };

    private static readonly IReadOnlyDictionary<string, string> ContentNameFormats =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unsupported-card"] = "战斗中有尚未支持的卡牌：{0}",
            ["unsupported-power"] = "战斗中有尚未支持的能力：{0}",
            ["unsupported-relic"] = "战斗中有尚未支持的遗物：{0}",
            ["unsupported-potion"] = "战斗中有尚未支持的药水：{0}",
            ["unsupported-orb"] = "战斗中有尚未支持的充能球：{0}",
            ["unsupported-monster"] = "战斗中有尚未支持的怪物：{0}",
            ["unsupported-enchantment"] = "战斗中有尚未支持的附魔：{0}",
            ["unsupported-affliction"] = "战斗中有尚未支持的灾厄：{0}",
        };

    /// <summary>Deployment failure reason codes from ActionDeployer / CombatTurnDeployer.</summary>
    private static readonly IReadOnlyDictionary<string, string> DeploymentReasonNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action_failed"] = "原生动作执行出错",
            ["action_canceled"] = "原生动作被游戏取消",
            ["action_not_enqueued"] = "动作没有进入游戏动作队列",
            ["boundary_timeout"] = "等待游戏结算超时",
            ["live_missing"] = "找不到战斗状态",
            ["live_player_missing"] = "找不到战斗状态",
            ["combat_changed"] = "战斗状态已改变或已结束",
            ["combat_over"] = "战斗状态已改变或已结束",
            ["combat_ended"] = "战斗状态已改变或已结束",
            ["import_failed"] = "动作完成后无法读取战斗状态",
            ["import_unsupported"] = "动作完成后无法读取战斗状态",
            ["import_capacity"] = "动作完成后无法读取战斗状态",
            ["import_invalid_operation"] = "动作完成后无法读取战斗状态",
            ["not_main_thread"] = "部署不在游戏主线程",
            ["action_kind"] = "路线中有未知的动作类型",
        };

    /// <summary>Deployment drift reason codes; the same names appear in the stopped summary.</summary>
    private static readonly IReadOnlyDictionary<string, string> DeviationReasonNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["turn_drift"] = "回合和路线对不上",
            ["energy_drift"] = "能量和路线对不上",
            ["stars_drift"] = "星能和路线对不上",
            ["hand_drift"] = "手牌和路线对不上",
            ["potion_slot_drift"] = "药水槽和路线对不上",
            ["card_missing"] = "路线里的牌不在手牌中",
            ["card_mismatch"] = "手牌和路线对不上",
            ["card_unplayable"] = "路线里的牌现在不能打出",
            ["potion_missing"] = "路线里的药水不在药水槽中",
            ["potion_mismatch"] = "药水和路线对不上",
            ["potion_target"] = "药水目标和路线对不上",
            ["target_missing"] = "路线里的目标不存在",
        };

    /// <summary>Battle capacity regions translated for the reason line.</summary>
    private static readonly IReadOnlyDictionary<string, string> CapacityRegionNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cards"] = "牌组",
            ["Enemies"] = "敌人",
            ["Powers"] = "能力",
            ["Relics"] = "遗物",
            ["Orbs"] = "充能球",
            ["PotionSlots"] = "药水槽",
            ["MonsterRules"] = "怪物规则",
            ["Choice.Options"] = "选择项",
            ["Choice.GeneratedOptions"] = "生成的选择项",
            ["GeneratedCards"] = "生成的牌",
            ["DrawnCards"] = "抽牌",
            ["AutoPlayCards"] = "自动打出的牌",
            ["AttackWaves"] = "攻击波次",
            ["Card.EnergyModifiers"] = "卡牌能量修正",
            ["Card.ReplayCount"] = "卡牌重放次数",
            ["Card.DecimalVars"] = "卡牌小数变量",
            ["Card.TemporaryStarCosts"] = "卡牌临时星能费用",
            ["Beam.Pool"] = "搜索候选池",
            ["TransientMarks"] = "临时标记",
            ["DetachedCards"] = "分离的牌",
            ["EncounterSpawns"] = "遭遇生成",
            ["PowerCommitment.Cards"] = "能力承诺的牌",
        };

    private static readonly IReadOnlyDictionary<string, string> SearchReasonNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["initial"] = "首次搜索",
        ["turn_start"] = "回合开始重新搜索",
        ["turn_setup"] = "回合开始选择",
    };

    private static readonly IReadOnlyDictionary<string, string> ChoiceEffectNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Discard"] = "弃",
        ["DiscardAndDraw"] = "弃",
        ["Exhaust"] = "耗尽",
        ["Transform"] = "变换",
        ["MoveToHand"] = "选择",
        ["MoveToHandFreeThisTurn"] = "选择",
        ["GenerateToHand"] = "选择",
        ["Upgrade"] = "升级",
        ["Duplicate"] = "复制",
        ["SetFreeThisCombat"] = "选择",
        ["AutoPlayRepeated"] = "选择",
        ["ApplySly"] = "选择",
        ["ApplyRetain"] = "选择",
        ["ApplyEthereal"] = "选择",
        ["Nightmare"] = "选择",
        ["Modify"] = "选择",
        ["ApplyKnowledgeCurse"] = "选择",
    };

    /// <summary>Every source string looked up through the maps above; the logic check requires an English entry for each.</summary>
    internal static IEnumerable<string> MappedSourceStrings
        => ErrorNames.Values.Concat(StopNames.Values).Concat(PauseNames.Values)
            .Concat(SkipNames.Values).Concat(EvaluationNames.Values).Concat(RetentionNames.Values)
            .Concat(PotionLimitNames.Values).Concat(SearchReasonNames.Values)
            .Concat(ChoiceEffectNames.Values)
            .Concat(ImportReasonNames.Values).Concat(ContentFallbackNames.Values)
            .Concat(ContentNameFormats.Values).Concat(DeploymentReasonNames.Values)
            .Concat(DeviationReasonNames.Values).Concat(CapacityRegionNames.Values)
            .Distinct(StringComparer.Ordinal);

    internal static string Error(string code)
        => ErrorNames.TryGetValue(code, out string? source)
            ? SolverText.Get(source)
            : SolverText.Format($"错误：{code}");

    /// <summary>
    /// Warning under a route summary when at least one member dropped children at a Battle
    /// capacity. The route remains valid; only the explored set was incomplete.
    /// </summary>
    internal static string CapacityDropHint(IReadOnlyDictionary<string, int>? regions)
    {
        if (regions is not { Count: > 0 })
            return string.Empty;
        List<string> names = [];
        foreach (string region in regions.Keys)
        {
            string name = region == "Cards"
                ? SolverText.Get("牌数")
                : CapacityRegionNames.TryGetValue(region, out string? translated)
                    ? SolverText.Get(translated)
                    : SolverText.Get("战斗状态");
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        string joined = string.Join(SolverText.Get("、"), names);
        return SolverText.Format($"部分打法因{joined}超出求解器容量被跳过，可能还有更好的路线");
    }

    /// <summary>
    /// One concrete line below a failed search or stopped turn. Structured details are mapped to
    /// player language; anything without a mapping points at the full log entry instead.
    /// </summary>
    internal static string ErrorReason(CombatFailureReason? reason)
    {
        if (reason is null)
            return SolverText.Get("详细原因已写入日志");
        return reason.Kind switch
        {
            CombatReasonKind.Import => ImportReasonText(reason),
            CombatReasonKind.ImportCapacity => CapacityText(reason),
            CombatReasonKind.CapacityRuntime => CapacityText(reason),
            CombatReasonKind.RouteCapacity => CapacityText(reason),
            CombatReasonKind.ImportUnsupported => SolverText.Get("战斗中有求解器尚未支持的内容"),
            CombatReasonKind.InvalidData => SolverText.Get("游戏返回的战斗数据无法校验"),
            CombatReasonKind.InvalidOperation => SolverText.Get("当前游戏状态不允许继续"),
            CombatReasonKind.RequestInvalid => SolverText.Get("搜索请求无效"),
            CombatReasonKind.RouteInvalid => SolverText.Get("搜索得到的路线无法回放"),
            CombatReasonKind.RouteUnsupported => SolverText.Get("搜索路线含有尚未支持的效果"),
            CombatReasonKind.SearchNoRoute => SolverText.Get("没有找到任何可执行路线"),
            CombatReasonKind.SearchMemoryLimit => SolverText.Get("达到内存上限，没有找到可执行路线"),
            CombatReasonKind.SearchCapacityBounded => SolverText.Get("生成的牌太多，超出求解器容量，没有找到能赢的路线"),
            CombatReasonKind.SearchFailed => SolverText.Get("搜索过程出错，没有返回可执行路线"),
            CombatReasonKind.ForcedPotion => SolverText.Get("没有找到能用掉全部强制使用药水的路线"),
            CombatReasonKind.TurnSetupFailed => SolverText.Get("没有找到可执行的回合开始选牌方案"),
            CombatReasonKind.UnsupportedRuntime => SolverText.Get("搜索遇到尚未支持的游戏语义"),
            CombatReasonKind.Timeout => SolverText.Get("等待游戏响应超时"),
            CombatReasonKind.Deployment => DeploymentReasonText(reason),
            CombatReasonKind.Deviation => DeviationReasonText(reason),
            CombatReasonKind.ChoiceMismatch => SolverText.Get("游戏当前的选牌请求和路线里的选择对不上"),
            CombatReasonKind.RepeatedFailure => reason.Inner is { } inner
                ? ErrorReason(inner)
                : SolverText.Get("连续多次搜索失败"),
            CombatReasonKind.DeathTurn => SolverText.Get("路线预计本回合会死亡"),
            CombatReasonKind.LiveEndTurnRisk => SolverText.Get("结束回合前的实机复核发现死亡或更大战损"),
            _ => SolverText.Get("详细原因已写入日志"),
        };
    }

    private static string ImportReasonText(CombatFailureReason reason)
    {
        if (reason.Code is { } code
            && ContentFallbackNames.TryGetValue(code, out string? fallback)
            && ContentNameFormats.TryGetValue(code, out string? named))
        {
            return CombatDisplayNames.TryContentTitle(code, reason.Detail, out string title)
                ? string.Format(CultureInfo.CurrentCulture, SolverText.Get(named), title)
                : SolverText.Get(fallback);
        }
        return reason.Code is { } known && ImportReasonNames.TryGetValue(known, out string? source)
            ? SolverText.Get(source)
            : SolverText.Get("详细原因已写入日志");
    }

    private static string CapacityText(CombatFailureReason reason)
    {
        if (reason.Region == "Cards")
        {
            if (reason.Actual is { } actual && reason.Capacity is { } capacity)
                return SolverText.Format($"牌组共 {actual} 张，超过求解器上限 {capacity} 张");
            if (reason.Capacity is { } onlyCapacity)
                return SolverText.Format($"牌组超过求解器上限 {onlyCapacity} 张");
            if (reason.Actual is { } onlyActual)
                return SolverText.Format($"牌组共 {onlyActual} 张，超过求解器容量");
            return SolverText.Get("牌组超出求解器容量");
        }
        string region = reason.Region is { } name
            && CapacityRegionNames.TryGetValue(name, out string? translated)
            ? SolverText.Get(translated)
            : SolverText.Get("战斗状态");
        return reason.Capacity is { } limit
            ? SolverText.Format($"{region}超出求解器上限 {limit}")
            : SolverText.Get("战斗状态超出求解器容量");
    }

    private static string DeploymentReasonText(CombatFailureReason reason)
        => reason.Code is { } code && DeploymentReasonNames.TryGetValue(code, out string? source)
            ? SolverText.Get(source)
            : SolverText.Get("详细原因已写入日志");

    private static string DeviationReasonText(CombatFailureReason reason)
    {
        if (reason.Code is { } code && DeviationReasonNames.TryGetValue(code, out string? source))
            return SolverText.Get(source);
        bool hand = false, energy = false, stars = false, potions = false, turn = false;
        bool enemies = false, powers = false, relics = false, pile = false, life = false;
        foreach (string key in reason.Diff)
        {
            if (key is "HandCount" || key.StartsWith("Hand[", StringComparison.Ordinal))
                hand = true;
            else if (key == "Energy")
                energy = true;
            else if (key == "Stars")
                stars = true;
            else if (key is "PotionSlotCount" || key.StartsWith("Potion[", StringComparison.Ordinal))
                potions = true;
            else if (key is "PlayerTurn" or "Round" or "Side" or "Phase")
                turn = true;
            else if (key is "EnemyCount" or "RosterCount"
                     || key.StartsWith("Enemy[", StringComparison.Ordinal)
                     || key.StartsWith("Roster[", StringComparison.Ordinal))
                enemies = true;
            else if (key is "PowerCount" || key.StartsWith("Power[", StringComparison.Ordinal))
                powers = true;
            else if (key is "RelicCount" || key.StartsWith("Relic[", StringComparison.Ordinal))
                relics = true;
            else if (key.StartsWith("Player.", StringComparison.Ordinal)
                     || key.StartsWith("Osty.", StringComparison.Ordinal))
                life = true;
            else if (key is "DrawCount" or "DiscardCount" or "ExhaustCount" or "PlayCount"
                     || key.StartsWith("Draw[", StringComparison.Ordinal)
                     || key.StartsWith("Discard[", StringComparison.Ordinal)
                     || key.StartsWith("Exhaust[", StringComparison.Ordinal)
                     || key.StartsWith("Play[", StringComparison.Ordinal))
                pile = true;
        }
        List<string> parts = [];
        if (hand) parts.Add(SolverText.Get("手牌和路线对不上"));
        if (energy) parts.Add(SolverText.Get("能量和路线对不上"));
        if (stars) parts.Add(SolverText.Get("星能和路线对不上"));
        if (potions) parts.Add(SolverText.Get("药水和路线对不上"));
        if (turn) parts.Add(SolverText.Get("回合和路线对不上"));
        if (enemies) parts.Add(SolverText.Get("敌人和路线对不上"));
        if (powers) parts.Add(SolverText.Get("能力和路线对不上"));
        if (relics) parts.Add(SolverText.Get("遗物和路线对不上"));
        if (life) parts.Add(SolverText.Get("生命和路线对不上"));
        if (pile) parts.Add(SolverText.Get("牌堆和路线对不上"));
        return parts.Count > 0
            ? string.Join(SolverText.Get("、"), parts)
            : SolverText.Get("实际局面和路线对不上");
    }

    internal static string Pause(string? code, int turn)
        => code is not null && PauseNames.TryGetValue(code, out string? pauseSource)
            ? SolverText.Get(pauseSource)
            : code switch
            {
                "turn_limit" => SolverText.Get("回合数超过上限，已暂停全自动"),
                "capacity_bounded" => SolverText.Format($"第 {turn} 回合搜索超出战斗状态容量，已暂停全自动"),
                "repeated_failure" => SolverText.Format($"第 {turn} 回合连续多次失败，已暂停全自动"),
                "choice_mismatch" => SolverText.Get("选牌计划与实际请求不一致，已暂停全自动"),
                "death_turn" => SolverText.Format($"第 {turn} 回合是预计死亡回合，已暂停全自动"),
                "live_end_turn_risk" => SolverText.Get("结束回合的实机复核发现风险，已暂停全自动"),
                "stopped" => SolverText.Get("搜索已停止"),
                null => SolverText.Get("全自动已暂停"),
                _ => SolverText.Format($"全自动已暂停：{code}"),
            };

    internal static string StopReason(string code)
        => StopNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    internal static string StopReasons(string? joined)
        => string.IsNullOrEmpty(joined)
            ? SolverText.Get("无")
            : string.Join(SolverText.Get("、"), joined.Split(',').Select(StopReason));

    internal static string SkipReason(string code)
        => SkipNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    internal static string Evaluation(string code)
        => EvaluationNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    internal static string Retention(string code)
        => RetentionNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    internal static string PotionLimit(string code)
        => PotionLimitNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    internal static string SearchReason(string code)
        => SearchReasonNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    internal static string ChoiceEffect(string code)
        => ChoiceEffectNames.TryGetValue(code, out string? source) ? SolverText.Get(source) : code;

    /// <summary>Why the search slot is disabled; codes come from the controller's CanSolve.</summary>
    internal static string SearchBlock(string? code) => code switch
    {
        "enemy_turn" => SolverText.Get("敌方回合，轮到你时再搜索。"),
        "actions_running" => SolverText.Get("动作结算中（含选牌界面），结算完再搜索。"),
        "turn_start" => SolverText.Get("回合开始结算中，完成开局选择后再搜索。"),
        "turn_setup" => SolverText.Get("等待回合开始选择"),
        "executing" => SolverText.Get("正在执行本回合；要重新搜索请先停止执行。"),
        "searching" => SolverText.Get("搜索仍在收尾，请稍候。"),
        "multiplayer" => SolverText.Get("多人游戏中不能搜索。"),
        "not_playable" => SolverText.Get("当前战斗状态不能搜索。"),
        "not_in_combat" => SolverText.Get("当前不在战斗中。"),
        _ => SolverText.Get("当前状态不能搜索。"),
    };

    internal static string Tier(SearchPerformanceTier tier) => tier switch
    {
        SearchPerformanceTier.Low => SolverText.Get("低档"),
        SearchPerformanceTier.Medium => SolverText.Get("中档"),
        SearchPerformanceTier.High => SolverText.Get("高档"),
        SearchPerformanceTier.ExtraHigh => SolverText.Get("极高"),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    internal static string MemorySource(SearchMemoryBudgetSource source) => source switch
    {
        SearchMemoryBudgetSource.Settings => SolverText.Get("设置"),
        SearchMemoryBudgetSource.Default => SolverText.Get("默认"),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    internal static string Bytes(long bytes)
    {
        const double mebibyte = 1024d * 1024d;
        double mib = bytes / mebibyte;
        return mib < 1024d
            ? mib.ToString("0", CultureInfo.CurrentCulture) + " MiB"
            : (mib / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " GiB";
    }

    internal static string Seconds(long milliseconds)
        => SolverText.Format($"{milliseconds / 1000d:0.0} 秒");

    internal static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
