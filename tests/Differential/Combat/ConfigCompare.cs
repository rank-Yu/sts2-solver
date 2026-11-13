// M2a-4 evaluation-config comparison: compile ScoreRules/Bound from the native root, assemble the
// same SREQ request the M0 harness wrote, and compare it with d1/<root>/config.br byte for byte.
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Golden;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Ai.Combat.Policy;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal enum ConfigStatus
{
    Same,
    Different,
    Rejected,
}

internal sealed record ConfigResult(ConfigStatus Status, string Reason, string FirstField,
    string Detail, bool SubParts)
{
    public string Line => $"CONFIG{(SubParts ? " scope=subparts" : "")} " + Status switch
    {
        ConfigStatus.Same => "status=same",
        ConfigStatus.Different => $"status=diff field={FirstField}",
        _ => $"status=reject reason={Reason}",
    };
}

internal static class ConfigCompare
{

    /// <summary>
    /// Compiles the rules/config for a fixed-prefix root: rules come from the replayed Battle, while
    /// the root-frozen score/bound facts come from the pre-prefix native request root and the replay
    /// counters, matching CombatSolver's FlashDifferentialProbe.
    /// </summary>

    /// <summary>Assembles the full SREQ request from all enumerated turn-setup roots and compares it.</summary>
    public static ConfigResult RunSetup(string data, string root, SetupRootSet set)
    {
        if (set.Score is not { } score || set.Bound is not { } boundConfig)
            return new(ConfigStatus.Rejected, ImportReason.UnsupportedPolicy, "",
                "回合准备根的评分/下界配置没有编译。", false);
        byte[] expectedBytes;
        SearchCase expected;
        GoldenRootRecord golden;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            expectedBytes = ReadBrotli(Path.Combine(directory, "config.br"));
            expected = SearchCodec.DecodeRequest(expectedBytes);
            golden = GoldenData.Decode(ReadBrotli(Path.Combine(directory, "golden.br")));
        }
        catch (InvalidDataException error)
        {
            return new(ConfigStatus.Rejected, "invalid-golden", "", error.Message, false);
        }
        if (set.Roots.Count < golden.Roots.Count)
        {
            return new(ConfigStatus.Different, "", "roots",
                $"setup roots compiled={set.Roots.Count} golden={golden.Roots.Count}", false);
        }
        List<Battle> ordered = new(golden.Roots.Count);
        bool[] used = new bool[set.Roots.Count];
        for (int index = 0; index < golden.Roots.Count; index++)
        {
            int match = -1;
            for (int candidate = 0; candidate < set.Roots.Count; candidate++)
            {
                if (used[candidate] || !ImportCompare.SameBytes(golden.Roots[index].Battle, set.Roots[candidate]))
                    continue;
                match = candidate;
                break;
            }
            if (match < 0)
                return new(ConfigStatus.Different, "", "roots",
                    $"golden root[{index}] 没有匹配的 setup 根", false);
            used[match] = true;
            ordered.Add(set.Roots[match]);
        }

        Scorer scorer = new(score);
        BoundFeatures bound = boundConfig;
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Scorer = scorer,
            Bound = bound,
        };
        SearchCase actual = new(set.Rules, [.. ordered], options);
        byte[] actualBytes = SearchCodec.EncodeRequest(actual);
        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            string? section = DescribeRequestDifference(expected, actual, out string detail);
            return new(ConfigStatus.Different, "", section ?? "<bytes>", detail, false);
        }
        for (int index = 0; index < golden.Roots.Count; index++)
        {
            ConfigResult hashes = CheckHashes(golden.Roots[index], ordered[index], scorer, bound, false);
            if (hashes.Status != ConfigStatus.Same)
                return hashes;
        }
        return new(ConfigStatus.Same, "", "", "", false);
    }

    public static ConfigResult RunPrefix(string data, string root, CombatState state,
        OfflineRequest request)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        if (state.RunState is not RunState runState)
            return new(ConfigStatus.Rejected, ImportReason.RulesMismatch, "", "原生战斗的 RunState 不是具体 RunState", false);

        PrefixAction[] actions;
        PrefixReplayResult replay;
        Battle pre;
        Rules preRules;
        Rules rules;
        try
        {
            preRules = RulesCompiler.Compile(state, player, runState);
            pre = BattleImporter.Import(state, player, preRules, ImportOptions.Default);
            actions = PrefixPlan.Parse(request);
            replay = PrefixReplay.Replay(pre, preRules, actions);
            rules = RulesCompiler.CompileAfterPrefix(state, player, runState, replay.Battle);
        }
        catch (UnsupportedImportException error)
        {
            return new(ConfigStatus.Rejected, error.Reason, "", error.Detail, false);
        }
        catch (UnsupportedException error)
        {
            return new(ConfigStatus.Rejected, "core-unsupported", "", error.Message, false);
        }
        catch (CapacityException error)
        {
            return new(ConfigStatus.Rejected, ImportReason.Capacity, error.Region, error.Message, false);
        }
        catch (InvalidOperationException error)
        {
            return new(ConfigStatus.Rejected, ImportReason.PrefixPlan, "", error.Message, false);
        }

        byte[] expectedBytes;
        SearchCase expected;
        GoldenRootRecord golden;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            expectedBytes = ReadBrotli(Path.Combine(directory, "config.br"));
            expected = SearchCodec.DecodeRequest(expectedBytes);
            golden = GoldenData.Decode(ReadBrotli(Path.Combine(directory, "golden.br")));
            if (golden.Roots.Count != 1)
                throw new InvalidDataException($"{root} 的前缀快照有 {golden.Roots.Count} 个根，预期 1 个。");
        }
        catch (InvalidDataException error)
        {
            return new(ConfigStatus.Rejected, "invalid-golden", "", error.Message, false);
        }

        SearchPolicy policy;
        Scorer scorer;
        BoundFeatures bound;
        EvaluationRootFacts facts = EvaluationRootFacts.Default;
        try
        {
            policy = SearchPolicyOverrides.ApplyOverrides(SearchPolicyCapture.Capture(state, player), request, player);
            scorer = EvaluationConfigCompiler.CompileScorer(state, player, rules, replay.Battle,
                policy, facts, rankBaselineRoot: pre, rankBaselineRules: preRules);
            bound = EvaluationConfigCompiler.CompileBound(state, player, replay.Battle, policy, facts);
        }
        catch (UnsupportedImportException error)
        {
            return new(ConfigStatus.Rejected, error.Reason, "", error.Detail, false);
        }
        catch (UnsupportedException error)
        {
            return new(ConfigStatus.Rejected, "core-unsupported", "", error.Message, false);
        }
        catch (CapacityException error)
        {
            return new(ConfigStatus.Rejected, ImportReason.Capacity, error.Region, error.Message, false);
        }

        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Scorer = scorer,
            Bound = bound,
        };
        SearchCase actual = new(rules, [replay.Battle], options);
        byte[] actualBytes = SearchCodec.EncodeRequest(actual);
        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            string? section = DescribeRequestDifference(expected, actual, out string detail);
            return new(ConfigStatus.Different, "", section ?? "<bytes>", detail, false);
        }
        return CheckHashes(golden.Roots[0], replay.Battle, scorer, bound, false);
    }

    public static ConfigResult Run(string data, string root, CombatState state,
        IReadOnlyList<string> setupSelections, bool preInjectionSetupRoot,
        OfflineRequest request)
    {
        if (request.FlashPrefixActions.Length != 0 || request.FlashPrefixPlan.Length != 0)
        {
            return new(ConfigStatus.Rejected, ImportReason.PrefixPlan, "",
                "前缀根必须走 ConfigCompare.RunPrefix", false);
        }
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        if (state.RunState is not RunState runState)
            return new(ConfigStatus.Rejected, ImportReason.RulesMismatch, "", "原生战斗的 RunState 不是具体 RunState", preInjectionSetupRoot);

        Rules rules;
        try
        {
            rules = RulesCompiler.Compile(state, player, runState);
        }
        catch (UnsupportedImportException error)
        {
            return new(ConfigStatus.Rejected, error.Reason, "", error.Detail, preInjectionSetupRoot);
        }
        catch (UnsupportedException error)
        {
            return new(ConfigStatus.Rejected, "core-unsupported", "", error.Message, preInjectionSetupRoot);
        }
        catch (CapacityException error)
        {
            return new(ConfigStatus.Rejected, ImportReason.Capacity, error.Region, error.Message, preInjectionSetupRoot);
        }

        Battle battle;
        try
        {
            battle = BattleImporter.Import(state, player, rules,
                preInjectionSetupRoot ? ImportOptions.Default with { TurnSetupRoot = true } : ImportOptions.Default);
        }
        catch (UnsupportedImportException error)
        {
            return new(ConfigStatus.Rejected, error.Reason, "", error.Detail, preInjectionSetupRoot);
        }
        catch (UnsupportedException error)
        {
            return new(ConfigStatus.Rejected, "core-unsupported", "", error.Message, preInjectionSetupRoot);
        }
        catch (CapacityException error)
        {
            return new(ConfigStatus.Rejected, ImportReason.Capacity, error.Region, error.Message, preInjectionSetupRoot);
        }

        byte[] expectedBytes;
        SearchCase expected;
        GoldenRootRecord golden;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            expectedBytes = ReadBrotli(Path.Combine(directory, "config.br"));
            expected = SearchCodec.DecodeRequest(expectedBytes);
            golden = GoldenData.Decode(ReadBrotli(Path.Combine(directory, "golden.br")));
        }
        catch (InvalidDataException error)
        {
            return new(ConfigStatus.Rejected, "invalid-golden", "", error.Message, preInjectionSetupRoot);
        }

        SearchPolicy policy;
        try
        {
            policy = SearchPolicyOverrides.ApplyOverrides(SearchPolicyCapture.Capture(state, player), request, player);
        }
        catch (UnsupportedImportException error)
        {
            return new(ConfigStatus.Rejected, error.Reason, "", error.Detail, preInjectionSetupRoot);
        }
        EvaluationRootFacts facts = EvaluationRootFacts.Default with
        {
            TurnSetupRoot = preInjectionSetupRoot,
        };

        Scorer scorer;
        BoundFeatures bound;
        try
        {
            scorer = EvaluationConfigCompiler.CompileScorer(state, player, rules, battle, policy, facts);
            bound = EvaluationConfigCompiler.CompileBound(state, player, battle, policy, facts);
        }
        catch (UnsupportedImportException error)
        {
            return new(ConfigStatus.Rejected, error.Reason, "", error.Detail, preInjectionSetupRoot);
        }
        catch (UnsupportedException error)
        {
            return new(ConfigStatus.Rejected, "core-unsupported", "", error.Message, preInjectionSetupRoot);
        }
        catch (CapacityException error)
        {
            return new(ConfigStatus.Rejected, ImportReason.Capacity, error.Region, error.Message, preInjectionSetupRoot);
        }

        if (preInjectionSetupRoot)
        {
            string? section = CompareSubParts(expected, scorer, bound, out string detail);
            if (section != null)
                return new(ConfigStatus.Different, "", section, detail, true);
            (Battle _, int selectedIndex) = ImportCompare.SelectExpectedRoot(golden, setupSelections, true);
            return CheckHashes(golden.Roots[selectedIndex], battle, scorer, bound, true);
        }

        SearchCase actual = EvaluationConfigCompiler.CompileRequest(
            state, player, rules, battle, policy, facts);
        byte[] actualBytes = SearchCodec.EncodeRequest(actual);
        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            string? section = DescribeRequestDifference(expected, actual, out string detail);
            return new(ConfigStatus.Different, "", section ?? "<bytes>", detail, false);
        }

        return CheckHashes(golden.Roots[0], battle, scorer, bound, false);
    }



    private static ConfigResult CheckHashes(GoldenBattleRoot golden,
        Battle battle, Scorer scorer, BoundFeatures bound, bool subParts)
    {
        ScoreFeatures features = scorer.Features(battle, 0);
        ulong expectedScore = golden.ScoreHash;
        ulong actualScore = GoldenHash.Score(features);
        if (expectedScore != actualScore)
        {
            return new(ConfigStatus.Different, "", "scoreHash",
                $"scoreHash golden={expectedScore:X16} compiled={actualScore:X16}", subParts);
        }
        byte expectedStatus = golden.BoundStatus;
        ulong expectedBound = golden.BoundHash;
        byte actualStatus;
        ulong actualBound = 0;
        if (!bound.Enabled)
        {
            actualStatus = 2;
        }
        else
        {
            actualStatus = 1;
            BoundValues values = bound.Evaluate(battle, scorer.RulesForCodec.Rules,
                features.ExplicitPotionStrategicCost, includeSmartPotionLowerBound: true);
            actualBound = GoldenHash.Bound(values);
        }
        if (expectedStatus != actualStatus || expectedBound != actualBound)
        {
            return new(ConfigStatus.Different, "", "boundHash",
                $"boundStatus golden={expectedStatus} compiled={actualStatus} "
                + $"boundHash golden={expectedBound:X16} compiled={actualBound:X16}", subParts);
        }
        return new(ConfigStatus.Same, "", "", "", subParts);
    }

    private static string? CompareSubParts(SearchCase expected, Scorer scorer,
        BoundFeatures bound, out string detail)
    {
        byte[] expectedScore = EncodeScoreProbe(expected);
        byte[] actualScore = EncodeScoreProbe(expected.Rules, scorer);
        if (!expectedScore.AsSpan().SequenceEqual(actualScore))
        {
            detail = ScoreRulesDiff(expected.Options.Scorer!.RulesForCodec,
                scorer.RulesForCodec);
            return "score";
        }
        byte[] expectedBound = EncodeBoundProbe(expected);
        byte[] actualBound = EncodeBoundProbe(expected.Rules, bound);
        if (!expectedBound.AsSpan().SequenceEqual(actualBound))
        {
            detail = BoundConfigDiff(expected.Options.Bound!.ConfigForCodec,
                bound.ConfigForCodec);
            return "bound";
        }
        detail = "";
        return null;
    }

    private static byte[] EncodeScoreProbe(SearchCase expected)
        => EncodeScoreProbe(expected.Rules, expected.Options.Scorer!);

    private static byte[] EncodeScoreProbe(Rules rules, Scorer scorer)
    {
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Scorer = scorer,
        };
        return SearchCodec.EncodeRequest(new SearchCase(rules, [], options));
    }

    private static byte[] EncodeBoundProbe(SearchCase expected)
        => EncodeBoundProbe(expected.Rules, expected.Options.Bound!);

    private static byte[] EncodeBoundProbe(Rules rules, BoundFeatures bound)
    {
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Bound = bound,
        };
        return SearchCodec.EncodeRequest(new SearchCase(rules, [], options));
    }

    internal static string? DescribeRequestDifference(SearchCase expected, SearchCase actual,
        out string detail)
    {
        if (!RulesCodec.Encode(expected.Rules).AsSpan().SequenceEqual(RulesCodec.Encode(actual.Rules)))
        {
            IReadOnlyList<string> differences = RulesDiff.Compare(expected.Rules, actual.Rules);
            detail = string.Join('\n', differences.Count == 0 ? ["rules bytes"] : differences.Take(16));
            return differences.Count == 0 ? "rules" : differences[0].Split(' ')[0];
        }
        if (expected.Battles.Length != actual.Battles.Length)
        {
            detail = $"battle count expected={expected.Battles.Length} actual={actual.Battles.Length}";
            return "battleCount";
        }
        for (int index = 0; index < expected.Battles.Length; index++)
        {
            if (!Fingerprint.Equal(in expected.Battles[index], in actual.Battles[index]))
            {
                List<string> differences = BattleDump.Diff(expected.Battles[index],
                    actual.Battles[index], "golden", "compiled");
                detail = string.Join('\n', differences.Take(16));
                return differences.Count == 0 ? "battle" : FieldOf(differences[0]);
            }
        }
        SearchCase expectedScore = expected with
        {
            Battles = [],
            Options = new BeamOptions(1, 1, 1, TimeSpan.Zero)
            {
                Scorer = expected.Options.Scorer,
            },
        };
        SearchCase actualScore = expectedScore with { Options = expectedScore.Options with { Scorer = actual.Options.Scorer } };
        if (!SearchCodec.EncodeRequest(expectedScore).AsSpan()
            .SequenceEqual(SearchCodec.EncodeRequest(actualScore)))
        {
            detail = ScoreRulesDiff(expected.Options.Scorer!.RulesForCodec,
                actual.Options.Scorer!.RulesForCodec);
            return "score";
        }
        SearchCase expectedBound = expected with
        {
            Battles = [],
            Options = new BeamOptions(1, 1, 1, TimeSpan.Zero)
            {
                Bound = expected.Options.Bound,
            },
        };
        SearchCase actualBound = expectedBound with { Options = expectedBound.Options with { Bound = actual.Options.Bound } };
        if (!SearchCodec.EncodeRequest(expectedBound).AsSpan()
            .SequenceEqual(SearchCodec.EncodeRequest(actualBound)))
        {
            detail = BoundConfigDiff(expected.Options.Bound!.ConfigForCodec,
                actual.Options.Bound!.ConfigForCodec);
            return "bound";
        }
        detail = OptionDiff(expected.Options, actual.Options);
        return "options";
    }

    private static string OptionDiff(BeamOptions expected, BeamOptions actual)
    {
        if (expected.BeamWidth != actual.BeamWidth)
            return $"beamWidth {expected.BeamWidth}->{actual.BeamWidth}";
        if (expected.MaxExpandedNodes != actual.MaxExpandedNodes)
            return $"maxExpandedNodes {expected.MaxExpandedNodes}->{actual.MaxExpandedNodes}";
        if (expected.MaxTurns != actual.MaxTurns)
            return $"maxTurns {expected.MaxTurns}->{actual.MaxTurns}";
        if (expected.SoftTimeBudget != actual.SoftTimeBudget)
            return $"softTimeBudget {expected.SoftTimeBudget}->{actual.SoftTimeBudget}";
        if (expected.Act3BossStrategy != actual.Act3BossStrategy)
            return $"act3BossStrategy {expected.Act3BossStrategy}->{actual.Act3BossStrategy}";
        if (expected.TheftPolicy != actual.TheftPolicy)
            return $"theftPolicy {expected.TheftPolicy}->{actual.TheftPolicy}";
        return "options bytes differ";
    }

    internal static string ScoreRulesDiff(ScoreRules expected, ScoreRules actual)
    {
        if (expected.Cards.Length != actual.Cards.Length)
            return $"cards.length {expected.Cards.Length}->{actual.Cards.Length}";
        for (int id = 0; id < expected.Cards.Length; id++)
        {
            CardScore?[]? left = expected.Cards[id];
            CardScore?[]? right = actual.Cards[id];
            if (left == null != (right == null))
                return $"cards[{id}] {Presence(left)}->{Presence(right)}";
            if (left == null)
                continue;
            if (left.Length != right!.Length)
                return $"cards[{id}].length {left.Length}->{right.Length}";
            for (int upgrade = 0; upgrade < left.Length; upgrade++)
            {
                CardScore? l = left[upgrade];
                CardScore? r = right[upgrade];
                if (l == null != (r == null))
                    return $"cards[{id}+{upgrade}] {Presence(l)}->{Presence(r)}";
                if (l != null && !CardScoreEquals(l, r!))
                    return $"cards[{id}+{upgrade}] {ValueDiff(l, r!)}";
            }
        }
        if (expected.Powers.Length != actual.Powers.Length)
            return $"powers.length {expected.Powers.Length}->{actual.Powers.Length}";
        for (int id = 0; id < expected.Powers.Length; id++)
        {
            PowerScore? l = expected.Powers[id];
            PowerScore? r = actual.Powers[id];
            if (l == null != (r == null))
                return $"powers[{id}] {Presence(l)}->{Presence(r)}";
            if (l != null && !PowerScoreEquals(l, r!))
                return $"powers[{id}] {ValueDiff(l, r!)}";
        }
        if (!expected.PotionStrategicCost.AsSpan().SequenceEqual(actual.PotionStrategicCost))
            return ArrayDiff("potionStrategicCost", expected.PotionStrategicCost, actual.PotionStrategicCost);
        if (!expected.GrowthBudgets.Equals(actual.GrowthBudgets))
            return $"growthBudgets {Format(expected.GrowthBudgets)}->{Format(actual.GrowthBudgets)}";
        if (!expected.RequiredGrowthRewards.Equals(actual.RequiredGrowthRewards))
            return $"requiredGrowthRewards {Format(expected.RequiredGrowthRewards)}->{Format(actual.RequiredGrowthRewards)}";
        if (!expected.PostCombatRelicHeal.Equals(actual.PostCombatRelicHeal))
            return $"postCombatRelicHeal {Format(expected.PostCombatRelicHeal)}->" +
                $"{Format(actual.PostCombatRelicHeal)}";
        if (expected.BossHpRelief != actual.BossHpRelief)
            return $"bossHpRelief {expected.BossHpRelief}->{actual.BossHpRelief}";
        if (expected.InitialPlayerMaxHp != actual.InitialPlayerMaxHp)
            return $"initialPlayerMaxHp {expected.InitialPlayerMaxHp}->{actual.InitialPlayerMaxHp}";
        if (expected.IsActEndingBoss != actual.IsActEndingBoss)
            return $"isActEndingBoss {expected.IsActEndingBoss}->{actual.IsActEndingBoss}";
        if (expected.Act3BossStrategy != actual.Act3BossStrategy)
            return $"act3BossStrategy {expected.Act3BossStrategy}->{actual.Act3BossStrategy}";
        if (expected.IgnoreGrowthRewards != actual.IgnoreGrowthRewards)
            return $"ignoreGrowthRewards {expected.IgnoreGrowthRewards}->{actual.IgnoreGrowthRewards}";
        if (expected.TheftPolicy != actual.TheftPolicy)
            return $"theftPolicy {expected.TheftPolicy}->{actual.TheftPolicy}";
        if (expected.InitialEnemyCount != actual.InitialEnemyCount)
            return $"initialEnemyCount {expected.InitialEnemyCount}->{actual.InitialEnemyCount}";
        if (!expected.InitialPersistentBuffValue.Equals(actual.InitialPersistentBuffValue))
            return $"initialPersistentBuffValue {Format(expected.InitialPersistentBuffValue)}->" +
                $"{Format(actual.InitialPersistentBuffValue)}";
        if (!expected.InitialEnemyStrengthSuppression.Equals(actual.InitialEnemyStrengthSuppression))
            return $"initialEnemyStrengthSuppression {Format(expected.InitialEnemyStrengthSuppression)}->" +
                $"{Format(actual.InitialEnemyStrengthSuppression)}";
        if (!expected.InitialEnemyWeakTurns.Equals(actual.InitialEnemyWeakTurns))
            return $"initialEnemyWeakTurns {Format(expected.InitialEnemyWeakTurns)}->" +
                $"{Format(actual.InitialEnemyWeakTurns)}";
        if (!expected.InitialRetainedAttackValue.Equals(actual.InitialRetainedAttackValue))
            return $"initialRetainedAttackValue {Format(expected.InitialRetainedAttackValue)}->" +
                $"{Format(actual.InitialRetainedAttackValue)}";
        if (!expected.RootPotions.AsSpan().SequenceEqual(actual.RootPotions))
            return ArrayDiff("rootPotions", expected.RootPotions, actual.RootPotions);
        if (expected.RootPotionSlotCount != actual.RootPotionSlotCount)
            return $"rootPotionSlotCount {expected.RootPotionSlotCount}->{actual.RootPotionSlotCount}";
        return RemainingScoreRulesDiff(expected, actual);
    }

    private static string RemainingScoreRulesDiff(ScoreRules expected, ScoreRules actual)
    {
        foreach (PropertyInfo property in typeof(ScoreRules).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name is nameof(ScoreRules.Rules) or nameof(ScoreRules.Cards)
                or nameof(ScoreRules.Powers) or nameof(ScoreRules.PotionStrategicCost)
                or nameof(ScoreRules.RootPotions))
            {
                continue;
            }
            object? left = property.GetValue(expected);
            object? right = property.GetValue(actual);
            if (ScalarEquals(left, right))
                continue;
            return $"{property.Name} {Format(left)}->{Format(right)}";
        }
        return "scoreRules";
    }

    private static string ValueDiff<T>(T expected, T actual)
    {
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? left = property.GetValue(expected);
            object? right = property.GetValue(actual);
            if (ScalarEquals(left, right))
                continue;
            return $"{property.Name} {Format(left)}->{Format(right)}";
        }
        return "values differ";
    }

    private static bool ScalarEquals(object? left, object? right)
        => left is double leftDouble && right is double rightDouble
            ? BitConverter.DoubleToInt64Bits(leftDouble) == BitConverter.DoubleToInt64Bits(rightDouble)
            : Equals(left, right);

    private static string Presence(object? value) => value == null ? "missing" : "present";

    private static string ArrayDiff<T>(string name, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        int common = Math.Min(expected.Count, actual.Count);
        for (int index = 0; index < common; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[index], actual[index]))
                return $"{name}[{index}] {Format(expected[index])}->{Format(actual[index])}";
        }
        return $"{name}.length {expected.Count}->{actual.Count}";
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static bool CardScoreEquals(CardScore left, CardScore right)
        => BitConverter.DoubleToInt64Bits(left.BaseDamage) == BitConverter.DoubleToInt64Bits(right.BaseDamage)
            && BitConverter.DoubleToInt64Bits(left.BaseBlock) == BitConverter.DoubleToInt64Bits(right.BaseBlock)
            && BitConverter.DoubleToInt64Bits(left.BaseCards) == BitConverter.DoubleToInt64Bits(right.BaseCards)
            && left.IsPower == right.IsPower
            && left.PrimaryMoveToHandChoice == right.PrimaryMoveToHandChoice
            && left.DamageVar == right.DamageVar && left.BlockVar == right.BlockVar
            && left.CardsVar == right.CardsVar && left.RepeatVar == right.RepeatVar
            && left.ShivCardsVar == right.ShivCardsVar && left.ShivShivsVar == right.ShivShivsVar
            && left.CalculationBaseVar == right.CalculationBaseVar
            && left.CalculationExtraVar == right.CalculationExtraVar
            && left.DoomThresholdVar == right.DoomThresholdVar
            && left.AttackHits == right.AttackHits && left.ShivSupply == right.ShivSupply
            && left.HasBlockDynamicVar == right.HasBlockDynamicVar
            && left.HasDebuffDynamicVar == right.HasDebuffDynamicVar
            && left.HasTurnEndInHandEffect == right.HasTurnEndInHandEffect
            && left.HasOstyAttackTag == right.HasOstyAttackTag
            && left.HasShivTag == right.HasShivTag && left.LatentValue == right.LatentValue
            && left.LatentTrait == right.LatentTrait && left.DynamicCardType == right.DynamicCardType
            && left.Calculated == right.Calculated;

    private static bool PowerScoreEquals(PowerScore left, PowerScore right)
        => left.Kind == right.Kind && left.Requirements == right.Requirements
            && left.Trait == right.Trait && left.IsTemporary == right.IsTemporary
            && left.Type == right.Type
            && left.DanseMacabreEnergyThreshold == right.DanseMacabreEnergyThreshold;

    private static string BoundConfigDiff(BoundFeatures.Config expected, BoundFeatures.Config actual)
    {
        if (expected.Enabled != actual.Enabled)
            return "enabled";
        if (expected.DisabledReason != actual.DisabledReason)
            return "disabledReason";
        if (expected.FutureHealPotential != actual.FutureHealPotential)
            return "futureHealPotential";
        if (expected.Certificate != actual.Certificate)
            return $"certificate golden={expected.Certificate} compiled={actual.Certificate}";
        if (expected.PostCombatHeal != actual.PostCombatHeal)
            return "postCombatHeal";
        if (expected.BossHpRelief != actual.BossHpRelief)
            return "bossHpRelief";
        if (expected.RequireCertifiedHealingProof != actual.RequireCertifiedHealingProof)
            return "requireCertifiedHealingProof";
        if (expected.PruneEqualHp != actual.PruneEqualHp)
            return "pruneEqualHp";
        if (!expected.ComponentUnsafeCard.AsSpan().SequenceEqual(actual.ComponentUnsafeCard))
            return "componentUnsafeCard";
        if (!expected.ComponentUnsafePower.AsSpan().SequenceEqual(actual.ComponentUnsafePower))
            return "componentUnsafePower";
        if (!expected.ComponentUnsafeRelic.AsSpan().SequenceEqual(actual.ComponentUnsafeRelic))
            return "componentUnsafeRelic";
        if (!expected.ComponentUnsafeMonster.AsSpan().SequenceEqual(actual.ComponentUnsafeMonster))
            return "componentUnsafeMonster";
        if (!expected.ComponentUnsafePotion.AsSpan().SequenceEqual(actual.ComponentUnsafePotion))
            return "componentUnsafePotion";
        if (!expected.SafeEnchantment.AsSpan().SequenceEqual(actual.SafeEnchantment))
            return "safeEnchantment";
        if (!expected.SafeAffliction.AsSpan().SequenceEqual(actual.SafeAffliction))
            return "safeAffliction";
        if (!expected.KnownBlockingCard.AsSpan().SequenceEqual(actual.KnownBlockingCard))
            return "knownBlockingCard";
        if (!expected.KnownBlockingRelic.AsSpan().SequenceEqual(actual.KnownBlockingRelic))
            return "knownBlockingRelic";
        if (!expected.RegenPower.AsSpan().SequenceEqual(actual.RegenPower))
            return "regenPower";
        if (!expected.ReattachPower.AsSpan().SequenceEqual(actual.ReattachPower))
            return "reattachPower";
        if (!expected.SwipePower.AsSpan().SequenceEqual(actual.SwipePower))
            return "swipePower";
        if (!expected.RegenPotion.AsSpan().SequenceEqual(actual.RegenPotion))
            return "regenPotion";
        if (!expected.DirectHealPotion.AsSpan().SequenceEqual(actual.DirectHealPotion))
            return "directHealPotion";
        if (!expected.FruitJuicePotion.AsSpan().SequenceEqual(actual.FruitJuicePotion))
            return "fruitJuicePotion";
        if (!expected.FairyPotion.AsSpan().SequenceEqual(actual.FairyPotion))
            return "fairyPotion";
        if (!expected.NativePotion.AsSpan().SequenceEqual(actual.NativePotion))
            return "nativePotion";
        if (!expected.RegenPotionAmount.AsSpan().SequenceEqual(actual.RegenPotionAmount))
            return "regenPotionAmount";
        if (!expected.DirectHealPotionPercent.AsSpan().SequenceEqual(actual.DirectHealPotionPercent))
            return "directHealPotionPercent";
        if (!expected.ReattachSafeMonster.AsSpan().SequenceEqual(actual.ReattachSafeMonster))
            return "reattachSafeMonster";
        if (expected.RegenPowerId != actual.RegenPowerId)
            return "regenPowerId";
        if (expected.ReattachPowerId != actual.ReattachPowerId)
            return "reattachPowerId";
        return "boundConfig";
    }

    private static string FieldOf(string line)
    {
        int space = line.IndexOf(' ');
        if (space < 0)
            return line;
        string rest = line[(space + 1)..];
        int colon = rest.IndexOf(':');
        return colon < 0 ? rest : rest[..colon];
    }

    internal static byte[] ReadBrotli(string path)
    {
        using FileStream file = File.OpenRead(path);
        using BrotliStream brotli = new(file, CompressionMode.Decompress);
        using MemoryStream buffer = new();
        brotli.CopyTo(buffer);
        return buffer.ToArray();
    }
}
