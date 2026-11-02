using System.Reflection;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>
/// K0 contract for the EndTurn prefix split. Every instance field of <see cref="Exec"/> must be in
/// exactly one of the four categories below; the snapshot category must be exactly the field set of
/// <see cref="ExecContinuation"/>. A field added to <see cref="Exec"/> without updating this table
/// fails the contract, so the prefix cannot silently lose it.
/// </summary>
internal static class ExecCoverage
{
    private static readonly string[] Snapshot =
    [
        nameof(Exec.HistoryRisk),
        nameof(Exec.AttackUnblockedHits),
        nameof(Exec.CardDamageTotal),
        nameof(Exec.CardKilledTarget),
        nameof(Exec.LastDamageKills),
        nameof(Exec.LastDamageTotal),
        nameof(Exec.LastDamageOverkill),
        nameof(Exec.Card),
        nameof(Exec.Stats),
        nameof(Exec.Target),
        nameof(Exec.XValue),
        nameof(Exec.StarValue),
        nameof(Exec.PlayIndex),
        nameof(Exec.PlayPosition),
        nameof(Exec.DrawnCardPosition),
        nameof(Exec.IntrinsicDrawHandled),
        nameof(Exec.GeneratedOptions),
        nameof(Exec.GeneratedOptionCount),
        nameof(Exec.PendingPowerChanges),
        nameof(Exec.PendingPowerChangeCount),
        nameof(Exec.CardPlayPairs),
        nameof(Exec.CardPlayPairCount),
        nameof(Exec.MonologuePending),
        nameof(Exec.MonologuePendingCount),
        nameof(Exec.CurrentMonologueBase),
        nameof(Exec.MonologuesResolved),
        nameof(Exec.CardPowerSourceActive),
        nameof(Exec.CardPowerSourceSerial),
        nameof(Exec.CardPowerSourceSerialCounter),
        nameof(Exec.LampTriggerSerial),
        nameof(Exec.LampInternalTypeCount),
        nameof(Exec.LampInternalTypes),
        nameof(Exec.VambraceTriggerActive),
        nameof(Exec.VambraceTriggerPosition),
        nameof(Exec.VambraceTriggerSerial),
        nameof(Exec.LastCardPowerSourceSerial),
        nameof(Exec.PlayIdentity),
        nameof(Exec.LastPlayIdentity),
        nameof(Exec.PlayIdentityCounter),
        nameof(Exec.MusicBoxPlayIdentity),
        nameof(Exec.CurrentPlayIsAutoPlay),
        nameof(Exec.CurrentPlayIsHistoryCourseDupe),
        nameof(Exec.CurrentPlayEnergyValue),
        nameof(Exec.BrightestFlameMaxHpSpent),
    ];

    private static readonly string[] AssertedEmpty =
    [
        nameof(Exec.NeedsChoice),
        nameof(Exec.Request),
        "_choicesTaken",
        nameof(Exec.EffectDepth),
        nameof(Exec.TransientMarkBase),
        nameof(Exec.TakingExtraTurn),
        nameof(Exec.SetupPause),
        nameof(Exec.SetupPreamble),
    ];

    /// <summary>
    /// Step-only phase marker read by route replay. It is neither part of the battle state nor of
    /// the end-turn continuation: a split step is never used for the native final-turn projection.
    /// </summary>
    private static readonly string[] StepTransient =
    [
        nameof(Exec.EnemySideTurnEndReached),
    ];

    private static readonly string[] ResetAtPlayerStart =
    [
        nameof(Exec.PendingBlockBreakOwners),
        nameof(Exec.ChoiceTiming),
    ];

    private static readonly string[] Special =
    [
        nameof(Exec.B),
        nameof(Exec.R),
        "_choices",
        "_choiceCount",
    ];

    internal static string? Validate()
    {
        HashSet<string> execFields = typeof(Exec)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> continuationFields = typeof(ExecContinuation)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        (string Category, string[] Fields)[] categories =
        [
            ("ExecContinuation", Snapshot),
            ("捕获断言为空", AssertedEmpty),
            ("AdvanceRoundPlayerStart 入口重置", ResetAtPlayerStart),
            ("步骤阶段标记", StepTransient),
            ("单独处理", Special),
        ];

        Dictionary<string, List<string>> owners = new(StringComparer.Ordinal);
        foreach ((string category, string[] fields) in categories)
        {
            foreach (string field in fields)
            {
                if (!owners.TryGetValue(field, out List<string>? list))
                    owners.Add(field, list = []);
                list.Add(category);
            }
        }

        List<string> errors = [];
        foreach (string field in execFields.OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!owners.TryGetValue(field, out List<string>? list))
            {
                errors.Add($"`{field}` 未分类");
            }
            else if (list.Count > 1)
            {
                errors.Add($"`{field}` 同时归入多类：{string.Join(" / ", list)}");
            }
        }
        foreach ((string field, List<string> list) in owners)
        {
            if (!execFields.Contains(field))
                errors.Add($"分类表包含不存在的 Exec 字段 `{field}`");
        }
        foreach (string field in Snapshot)
        {
            if (!continuationFields.Contains(field))
                errors.Add($"快照分类 `{field}` 在 ExecContinuation 中不存在");
        }
        foreach (string field in continuationFields)
        {
            if (!Snapshot.Contains(field, StringComparer.Ordinal))
                errors.Add($"ExecContinuation 字段 `{field}` 不在快照分类中");
        }
        return errors.Count == 0 ? null : string.Join("；", errors);
    }
}
