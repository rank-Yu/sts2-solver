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
        ["import_failed"] = "无法导入当前战斗状态",
        ["import_unsupported"] = "当前战斗含有尚未支持的内容",
        ["import_capacity"] = "当前战斗超出求解器的状态容量",
        ["request_invalid"] = "搜索请求无效",
        ["route_invalid"] = "搜索结果无法回放，已丢弃",
        ["route_unsupported"] = "路线含有尚未支持的效果",
        ["route_capacity"] = "路线回放超出状态容量",
        ["search_failed"] = "搜索没有找到可执行路线",
        ["boundary_deviation"] = "回合开始时实际状态与预测不同，将重新搜索",
        ["action_deviation"] = "动作结果与预测不同，将重新搜索",
        ["deploy_failed"] = "动作执行失败，将重新搜索",
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

    private static readonly IReadOnlyDictionary<string, string> CancelNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["hotkey"] = "已按 F8 停止",
        ["ui"] = "已在面板上停止",
        ["combat_ended"] = "战斗已结束",
        ["tree_exiting"] = "游戏正在退出",
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

    private static readonly IReadOnlyDictionary<string, string> SearchReasonNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["initial"] = "首次搜索",
        ["turn_start"] = "回合开始重新搜索",
    };

    /// <summary>Every source string looked up through the maps above; the logic check requires an English entry for each.</summary>
    internal static IEnumerable<string> MappedSourceStrings
        => ErrorNames.Values.Concat(CancelNames.Values).Concat(StopNames.Values)
            .Concat(SkipNames.Values).Concat(EvaluationNames.Values).Concat(RetentionNames.Values)
            .Concat(PotionLimitNames.Values).Concat(SearchReasonNames.Values).Distinct(StringComparer.Ordinal);

    internal static string Phase(CombatUiPhase phase, string? endReason) => phase switch
    {
        CombatUiPhase.Idle => SolverText.Get("待命"),
        CombatUiPhase.WaitingTurn => SolverText.Get("等待回合"),
        CombatUiPhase.Searching => SolverText.Get("搜索中"),
        CombatUiPhase.Deploying => SolverText.Get("执行中"),
        CombatUiPhase.Paused => SolverText.Get("已暂停"),
        CombatUiPhase.Completed => endReason switch
        {
            "victory" => SolverText.Get("胜利"),
            "defeat" => SolverText.Get("战败"),
            _ => SolverText.Get("已结束"),
        },
        CombatUiPhase.Cancelled => SolverText.Get("已停止"),
        CombatUiPhase.Failed => SolverText.Get("失败"),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    internal static bool IsRunning(CombatUiPhase phase)
        => phase is CombatUiPhase.WaitingTurn or CombatUiPhase.Searching or CombatUiPhase.Deploying;

    internal static string Error(string code)
        => ErrorNames.TryGetValue(code, out string? source)
            ? SolverText.Get(source)
            : SolverText.Format($"错误：{code}");

    internal static string Pause(string? code, int turn)
        => code switch
        {
            "turn_limit" => SolverText.Get("回合数超过上限，已暂停全自动"),
            "capacity_bounded" => SolverText.Format($"第 {turn} 回合搜索超出战斗状态容量，已暂停全自动"),
            "repeated_failures" => SolverText.Format($"第 {turn} 回合连续多次失败，已暂停全自动"),
            null => SolverText.Get("全自动已暂停"),
            _ => SolverText.Format($"全自动已暂停：{code}"),
        };

    internal static string Cancel(string? reason)
        => reason != null && CancelNames.TryGetValue(reason, out string? source)
            ? SolverText.Get(source)
            : SolverText.Get("全自动已停止");

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

    internal static string Tier(SearchPerformanceTier tier) => tier switch
    {
        SearchPerformanceTier.Low => SolverText.Get("低档"),
        SearchPerformanceTier.Medium => SolverText.Get("中档"),
        SearchPerformanceTier.High => SolverText.Get("高档"),
        SearchPerformanceTier.ExtraHigh => SolverText.Get("极高"),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    /// <summary>Real Sts2Solver numbers for one tier, read from the same profile the search uses.</summary>
    internal static string TierDescription(SearchPerformanceTier tier)
    {
        SearchPerformanceProfile profile = SearchPerformanceProfile.For(tier);
        double baselineSeconds = profile.BaselineWidth * (double)profile.MillisecondsPerWidth / 1000d;
        return SolverText.Format(
            $"基准宽度 {profile.BaselineWidth}（每轮 {baselineSeconds:0.##} 秒），窄 {profile.NarrowWidth}、宽 {profile.WideWidth}、进攻 {profile.OffensiveWidth}。每轮时限是宽度 × {profile.MillisecondsPerWidth} 毫秒；一次决策按顺序运行多轮。");
    }

    internal static string MemorySource(SearchMemoryBudgetSource source) => source switch
    {
        SearchMemoryBudgetSource.Environment => SolverText.Get("环境变量"),
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
