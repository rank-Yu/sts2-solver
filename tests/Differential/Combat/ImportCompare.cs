// Import-compare mode: builds the native root, imports it with Sts2Solver.Import, and compares the
// decoded expected GOLD root byte for byte (the first root, or the matching ordinary setup root for
// the three turn-setup fixtures).
using System.IO.Compression;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Differential;

internal enum ImportStatus
{
    Same,
    Different,
    Rejected,
}

internal sealed record ImportResult(ImportStatus Status, string Reason, string FirstField, string Detail)
{
    public string Line => Status switch
    {
        ImportStatus.Same => "IMPORT status=same",
        ImportStatus.Different => $"IMPORT status=diff field={FirstField}",
        _ => $"IMPORT status=reject reason={Reason}",
    };
}

internal static class ImportCompare
{
    public static ImportResult Run(string data, string root, CombatState state,
        IReadOnlyList<string> setupSelections, bool preInjectionSetupRoot, bool compiledRules = false)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        Battle expected;
        RelicRoster expectedRelics;
        Rules? rules = null;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            using System.Text.Json.JsonDocument meta = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "meta.json")));
            string sha = meta.RootElement.GetProperty("rulesSha256").GetString()
                ?? throw new InvalidDataException($"{root} 缺少 rulesSha256。");
            GoldenRootRecord golden = GoldenData.Decode(ReadBrotli(Path.Combine(directory, "golden.br")));
            (Battle expectedRoot, int rootIndex) = SelectExpectedRoot(golden, setupSelections, preInjectionSetupRoot);
            expected = expectedRoot;
            expectedRelics = golden.Roots[rootIndex].Relics;
            if (!compiledRules)
                rules = RulesCodec.Decode(ReadBrotli(Path.Combine(data, "objects", "rules", sha + ".br")))
                    .WithRelics(golden.Roots[rootIndex].Relics);
            Console.WriteLine($"IMPORT-ROOT index={rootIndex} of={golden.Roots.Count}");
        }
        catch (InvalidDataException error)
        {
            return new(ImportStatus.Rejected, "invalid-golden", "", error.Message);
        }

        if (compiledRules)
        {
            try
            {
                if (state.RunState is not MegaCrit.Sts2.Core.Runs.RunState runState)
                {
                    return new(ImportStatus.Rejected, ImportReason.RulesMismatch, "",
                        "原生战斗的 RunState 不是具体 RunState");
                }
                rules = RulesCompiler.Compile(state, player, runState);
            }
            catch (UnsupportedImportException error)
            {
                return new(ImportStatus.Rejected, error.Reason, "", error.Detail);
            }
            catch (UnsupportedException error)
            {
                return new(ImportStatus.Rejected, "core-unsupported", "", error.Message);
            }
            catch (CapacityException error)
            {
                return new(ImportStatus.Rejected, ImportReason.Capacity, error.Region, error.Message);
            }
        }

        Battle actual;
        try
        {
            actual = BattleImporter.Import(state, player, rules!,
                preInjectionSetupRoot ? ImportOptions.Default with { TurnSetupRoot = true } : ImportOptions.Default);
        }
        catch (UnsupportedImportException error)
        {
            return new(ImportStatus.Rejected, error.Reason, "", error.Detail);
        }
        catch (UnsupportedException error)
        {
            return new(ImportStatus.Rejected, "core-unsupported", "", error.Message);
        }
        catch (CapacityException error)
        {
            return new(ImportStatus.Rejected, ImportReason.Capacity, error.Region, error.Message);
        }

        if (!expectedRelics.Equivalent(rules!.Relics))
            return new(ImportStatus.Different, "", "relics", "共享遗物清单与冻结根不同");
        if (SameBytes(expected, actual))
            return new(ImportStatus.Same, "", "", "");
        List<string> diff = BattleDump.Diff(expected, actual, "golden", "import");
        string first = diff.Count == 0 ? "<bytes>" : FieldOf(diff[0]);
        return new(ImportStatus.Different, "", first, string.Join('\n', diff.Take(24)));
    }


    /// <summary>
    /// Imports the pre-prefix native root, replays the frozen prefix on the kernel, and compares the
    /// replayed command projection with the fixture's single golden root.
    /// </summary>
    public static ImportResult RunPrefix(string data, string root, CombatState state,
        OfflineRequest request)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        Battle expected;
        RelicRoster expectedRelics;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            GoldenRootRecord golden = GoldenData.Decode(ReadBrotli(Path.Combine(directory, "golden.br")));
            if (golden.Roots.Count != 1)
                throw new InvalidDataException($"{root} 的前缀快照有 {golden.Roots.Count} 个根，预期 1 个。");
            expected = golden.Roots[0].Battle;
            expectedRelics = golden.Roots[0].Relics;
        }
        catch (InvalidDataException error)
        {
            return new(ImportStatus.Rejected, "invalid-golden", "", error.Message);
        }

        try
        {
            if (state.RunState is not MegaCrit.Sts2.Core.Runs.RunState runState)
                return new(ImportStatus.Rejected, ImportReason.RulesMismatch, "",
                    "原生战斗的 RunState 不是具体 RunState");
            Rules rules = RulesCompiler.Compile(state, player, runState);
            Battle pre = BattleImporter.Import(state, player, rules, ImportOptions.Default);
            PrefixAction[] actions = PrefixPlan.Parse(request);
            PrefixReplayResult replay = PrefixReplay.Replay(pre, rules, actions);
            if (!expectedRelics.Equivalent(rules.Relics))
                return new(ImportStatus.Different, "", "relics", "共享遗物清单与冻结根不同");
            if (SameBytes(expected, replay.Battle))
                return new(ImportStatus.Same, "", "", "");
            List<string> diff = BattleDump.Diff(expected, replay.Battle, "golden", "replay");
            string first = diff.Count == 0 ? "<bytes>" : FieldOf(diff[0]);
            return new(ImportStatus.Different, "", first, string.Join('\n', diff.Take(24)));
        }
        catch (UnsupportedImportException error)
        {
            return new(ImportStatus.Rejected, error.Reason, "", error.Detail);
        }
        catch (UnsupportedException error)
        {
            return new(ImportStatus.Rejected, "core-unsupported", "", error.Message);
        }
        catch (CapacityException error)
        {
            return new(ImportStatus.Rejected, ImportReason.Capacity, error.Region, error.Message);
        }
        catch (InvalidOperationException error)
        {
            return new(ImportStatus.Rejected, ImportReason.PrefixPlan, "", error.Message);
        }
    }


    /// <summary>Compares every enumerated turn-setup root with the fixture's golden root set.</summary>
    public static ImportResult RunSetup(string data, string root, SetupRootSet set)
    {
        GoldenRootRecord golden;
        try
        {
            string directory = Path.Combine(data, "d1", root);
            golden = GoldenData.Decode(ReadBrotli(Path.Combine(directory, "golden.br")));
        }
        catch (InvalidDataException error)
        {
            return new(ImportStatus.Rejected, "invalid-golden", "", error.Message);
        }
        if (set.Roots.Count < golden.Roots.Count)
        {
            return new(ImportStatus.Different, "", "roots",
                $"setup roots compiled={set.Roots.Count} golden={golden.Roots.Count} " +
                $"options={string.Join(',', set.OptionIds)}");
        }
        bool[] used = new bool[set.Roots.Count];
        for (int index = 0; index < golden.Roots.Count; index++)
        {
            int match = -1;
            for (int candidate = 0; candidate < set.Roots.Count; candidate++)
            {
                if (used[candidate] || !golden.Roots[index].Relics.Equivalent(set.Rules.Relics)
                    || !SameBytes(golden.Roots[index].Battle, set.Roots[candidate]))
                    continue;
                match = candidate;
                break;
            }
            if (match < 0)
            {
                List<string> diff = BattleDump.Diff(golden.Roots[index].Battle, set.Roots[index],
                    "golden", "setup");
                string first = diff.Count == 0 ? "<bytes>" : FieldOf(diff[0]);
                return new(ImportStatus.Different, "", first,
                    $"golden root[{index}] 没有匹配的 setup 根；" + string.Join('\n', diff.Take(24)));
            }
            used[match] = true;
        }
        return new(ImportStatus.Same, "", "", "");
    }

    public static Rules LoadRules(string data, string root)
        => RulesCodec.Decode(LoadRulesBytes(data, root)).WithRelics(GoldenData.Decode(
            ReadBrotli(Path.Combine(data, "d1", root, "golden.br"))).Roots[0].Relics);

    public static byte[] LoadRulesBytes(string data, string root)
    {
        string directory = Path.Combine(data, "d1", root);
        using System.Text.Json.JsonDocument meta = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "meta.json")));
        string sha = meta.RootElement.GetProperty("rulesSha256").GetString()
            ?? throw new InvalidDataException($"{root} 缺少 rulesSha256。");
        return ReadBrotli(Path.Combine(data, "objects", "rules", sha + ".br"));
    }

    internal static (Battle Root, int Index) SelectExpectedRoot(GoldenRootRecord golden,
        IReadOnlyList<string> setupSelections, bool preInjectionSetupRoot)
    {
        if (!preInjectionSetupRoot || setupSelections.Count == 0)
            return (golden.Roots[0].Battle, 0);
        // A turn-setup fixture's ordinary root is the one reached by the native first-option setup
        // choice. Match the recorded selection to the golden choice breadcrumb instead of assuming
        // index 0; comparison modes now enumerate the remaining roots in SetupRootSet.
        for (int index = 0; index < golden.Roots.Count; index++)
        {
            string choices = golden.Roots[index].Choices;
            bool all = true;
            foreach (string id in setupSelections)
            {
                if (!choices.Contains("/" + id + "+", StringComparison.Ordinal))
                {
                    all = false;
                    break;
                }
            }
            if (all)
                return (golden.Roots[index].Battle, index);
        }
        return (golden.Roots[0].Battle, 0);
    }

    public static bool SameBytes(in Battle left, in Battle right)
        => Fingerprint.Equal(in left, in right);

    private static string FieldOf(string diffLine)
    {
        int space = diffLine.IndexOf(' ');
        if (space < 0)
            return diffLine;
        string rest = diffLine[(space + 1)..];
        int colon = rest.IndexOf(':');
        return colon < 0 ? rest : rest[..colon];
    }

    private static byte[] ReadBrotli(string path)
    {
        using FileStream file = File.OpenRead(path);
        using BrotliStream brotli = new(file, CompressionMode.Decompress);
        using MemoryStream buffer = new();
        brotli.CopyTo(buffer);
        return buffer.ToArray();
    }
}
