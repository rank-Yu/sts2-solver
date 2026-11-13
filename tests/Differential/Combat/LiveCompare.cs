// --live-compare: compare a directory written by the in-game Mod test host with the frozen
// snapshot and with the offline host's own native-state dump for the same root.
using System.IO.Compression;
using System.Text.Json;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal sealed record LiveStatus
{
    public int SchemaVersion { get; init; } = 1;
    public string Root { get; init; } = string.Empty;
    public string Status { get; init; } = "failed";
    public string? Error { get; init; }
    public string[] SetupSelections { get; init; } = [];
    public bool TurnSetupRoot { get; init; }
}

internal static class LiveCompare
{
    public static int Run(string data, string corpora, string resultDirectory)
    {
        if (!Directory.Exists(resultDirectory))
        {
            Console.Error.WriteLine($"找不到实机结果目录：{resultDirectory}");
            return 2;
        }
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();

        string[] directories = Directory.EnumerateDirectories(resultDirectory)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        int same = 0, different = 0, rejected = 0, native = 0;
        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            LiveStatus? status;
            try
            {
                string statusPath = Path.Combine(directory, "status.json");
                if (!File.Exists(statusPath))
                {
                    Console.Error.WriteLine($"root={name} status=reject reason=missing-status");
                    rejected++;
                    continue;
                }
                status = JsonSerializer.Deserialize<LiveStatus>(
                    File.ReadAllText(statusPath), OfflineJson.Options);
            }
            catch (JsonException error)
            {
                Console.Error.WriteLine($"root={name} status=reject reason=invalid-status detail={error.Message}");
                rejected++;
                continue;
            }
            if (status == null || !string.Equals(status.Root, name, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"root={name} status=reject reason=status-root-mismatch");
                rejected++;
                continue;
            }
            if (!string.Equals(status.Status, "ok", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"root={name} status=reject reason=capture-failed detail={status.Error}");
                rejected++;
                continue;
            }

            GoldenBattleRoot expectedRoot;
            Battle expectedBattle;
            try
            {
                string rootDirectory = Path.Combine(data, "d1", name);
                GoldenRootRecord golden = GoldenData.Decode(
                    ReadBrotli(Path.Combine(rootDirectory, "golden.br")));
                (expectedBattle, int rootIndex) = ImportCompare.SelectExpectedRoot(
                    golden, status.SetupSelections, status.TurnSetupRoot);
                expectedRoot = golden.Roots[rootIndex];
            }
            catch (InvalidDataException error)
            {
                Console.Error.WriteLine($"root={name} status=reject reason=invalid-golden detail={error.Message}");
                rejected++;
                continue;
            }
            catch (KeyNotFoundException error)
            {
                Console.Error.WriteLine($"root={name} status=reject reason=invalid-golden detail={error.Message}");
                rejected++;
                continue;
            }

            bool ok = true;
            string? first = null;
            string? detail = null;

            byte[] battleBytes;
            try
            {
                battleBytes = File.ReadAllBytes(Path.Combine(directory, "BATL"));
            }
            catch (FileNotFoundException)
            {
                Console.Error.WriteLine($"root={name} status=reject reason=missing-batl");
                rejected++;
                continue;
            }
            Battle actualBattle;
            RelicRoster actualRelics;
            try
            {
                actualBattle = BattleCodec.Decode(battleBytes, out actualRelics);
            }
            catch (InvalidDataException error)
            {
                Console.Error.WriteLine($"root={name} status=reject reason=invalid-batl detail={error.Message}");
                rejected++;
                continue;
            }
            if (!expectedRoot.Relics.Equivalent(actualRelics) || !ImportCompare.SameBytes(expectedBattle, actualBattle))
            {
                List<string> diff = BattleDump.Diff(expectedBattle, actualBattle, "golden", "live");
                ok = false;
                first = diff.Count == 0 ? "<bytes>" : FieldOf(diff[0]);
                detail = string.Join('\n', diff.Take(24));
            }

            byte[] actualRules;
            try
            {
                actualRules = File.ReadAllBytes(Path.Combine(directory, "RULE"));
            }
            catch (FileNotFoundException)
            {
                Console.Error.WriteLine($"root={name} status=reject reason=missing-rule");
                rejected++;
                continue;
            }
            byte[] expectedRules = ImportCompare.LoadRulesBytes(data, name);
            if (!actualRules.AsSpan().SequenceEqual(expectedRules))
            {
                if (ok)
                {
                    string ruleDetail;
                    try
                    {
                        Rules goldenRules = RulesCodec.Decode(expectedRules);
                        Rules liveRules = RulesCodec.Decode(actualRules);
                        IReadOnlyList<string> differences = RulesDiff.Compare(goldenRules, liveRules);
                        first = differences.Count == 0 ? "<bytes>" : differences[0].Split(' ')[0];
                        ruleDetail = string.Join('\n', differences.Take(16));
                    }
                    catch (InvalidDataException error)
                    {
                        first = "<bytes>";
                        ruleDetail = error.Message;
                    }
                    detail = ruleDetail;
                }
                ok = false;
            }

            byte[] actualRequest;
            try
            {
                string captureRequest = Path.Combine(directory, "SREQ.capture");
                actualRequest = File.ReadAllBytes(captureRequest);
            }
            catch (FileNotFoundException)
            {
                Console.Error.WriteLine($"root={name} status=reject reason=missing-sreq-capture");
                rejected++;
                continue;
            }
            byte[] expectedRequest = ReadBrotli(Path.Combine(data, "d1", name, "config.br"));
            bool requestSame = actualRequest.AsSpan().SequenceEqual(expectedRequest);
            string requestScope = "full";
            if (!requestSame && status.TurnSetupRoot)
            {
                requestScope = "subparts";
                (requestSame, string subDetail) = CompareSetupSubParts(actualRequest, expectedRequest);
                if (!requestSame && ok)
                {
                    first = "sreq";
                    detail = subDetail;
                }
            }
            else if (!requestSame && ok)
            {
                (string? section, string requestDetail) = DescribeRequestDifference(expectedRequest, actualRequest);
                first = section ?? "<bytes>";
                detail = requestDetail;
            }
            ok &= requestSame;

            bool nativeSame = false;
            string? nativeDetail = null;
            try
            {
                OfflineRequest request = Program.LoadRequest(data, corpora, name);
                if (status.TurnSetupRoot)
                    TurnSetupRootCapture.BeginBuild();
                Task<CombatState> build = ScenarioBuilder.BuildAsync(
                    new OfflineCombatWaiter(loop), request,
                    skipInitialInjection: status.TurnSetupRoot,
                    turnSetupSelector: status.TurnSetupRoot ? new TurnSetupCardSelector(0) : null);
                loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), $"实机对照建局 {name}");
                CombatState combat = build.GetAwaiter().GetResult();
                string expectedNative = NativeStateDump.Capture(combat);
                string actualNative = File.ReadAllText(Path.Combine(directory, "native.txt"));
                nativeSame = string.Equals(expectedNative, actualNative, StringComparison.Ordinal);
                if (!nativeSame)
                {
                    nativeDetail = FirstDifferentLine(expectedNative, actualNative);
                    if (ok)
                    {
                        first = "native";
                        detail = nativeDetail;
                    }
                }
                RunManager.Instance.CleanUp(true);
            }
            catch (UnsupportedImportException error)
            {
                nativeDetail = $"unsupported reason={error.Reason} {error.Detail}";
                if (ok)
                {
                    first = "native";
                    detail = nativeDetail;
                }
            }
            catch (UnsupportedException error)
            {
                nativeDetail = error.Message;
                if (ok)
                {
                    first = "native";
                    detail = nativeDetail;
                }
            }
            catch (InvalidOperationException error)
            {
                nativeDetail = error.Message;
                if (ok)
                {
                    first = "native";
                    detail = nativeDetail;
                }
            }
            catch (TimeoutException error)
            {
                nativeDetail = error.Message;
                if (ok)
                {
                    first = "native";
                    detail = nativeDetail;
                }
            }

            if (nativeSame)
                native++;
            ok &= nativeSame;
            if (ok)
            {
                same++;
                Console.WriteLine(
                    $"root={name} status=same sreq={requestScope} native=same");
            }
            else
            {
                different++;
                Console.WriteLine(
                    $"root={name} status=diff first={first} sreq={requestScope} native={(nativeSame ? "same" : "diff")}");
                if (detail != null)
                    Console.WriteLine(detail);
            }
        }
        Console.WriteLine(
            $"live roots={directories.Length} same={same} diff={different} reject={rejected} native={native}");
        return different == 0 && rejected == 0 ? 0 : 1;
    }

    private static (bool Same, string Detail) CompareSetupSubParts(byte[] actual, byte[] expected)
    {
        try
        {
            SearchCase actualCase = SearchCodec.DecodeRequest(actual);
            SearchCase expectedCase = SearchCodec.DecodeRequest(expected);
            if (actualCase.Options.Scorer == null || actualCase.Options.Bound == null
                || expectedCase.Options.Scorer == null || expectedCase.Options.Bound == null)
                return (false, "setup subparts missing scorer/bound");
            byte[] actualScore = EncodeScoreProbe(actualCase.Rules, actualCase.Options.Scorer);
            byte[] expectedScore = EncodeScoreProbe(expectedCase.Rules, expectedCase.Options.Scorer);
            if (!actualScore.AsSpan().SequenceEqual(expectedScore))
                return (false, "setup score subpart differs");
            byte[] actualBound = EncodeBoundProbe(actualCase.Rules, actualCase.Options.Bound);
            byte[] expectedBound = EncodeBoundProbe(expectedCase.Rules, expectedCase.Options.Bound);
            if (!actualBound.AsSpan().SequenceEqual(expectedBound))
                return (false, "setup bound subpart differs");
            return (true, "");
        }
        catch (InvalidDataException error)
        {
            return (false, error.Message);
        }
    }

    private static byte[] EncodeScoreProbe(Rules rules, Scorer scorer)
    {
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Scorer = scorer,
        };
        return SearchCodec.EncodeRequest(new SearchCase(rules, [], options));
    }

    private static byte[] EncodeBoundProbe(Rules rules, BoundFeatures bound)
    {
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Bound = bound,
        };
        return SearchCodec.EncodeRequest(new SearchCase(rules, [], options));
    }

    private static (string? Section, string Detail) DescribeRequestDifference(
        byte[] expectedBytes, byte[] actualBytes)
    {
        try
        {
            SearchCase expected = SearchCodec.DecodeRequest(expectedBytes);
            SearchCase actual = SearchCodec.DecodeRequest(actualBytes);
            if (expected.Battles.Length != actual.Battles.Length)
                return ("battleCount", $"expected={expected.Battles.Length} actual={actual.Battles.Length}");
            for (int index = 0; index < expected.Battles.Length; index++)
            {
                if (!ImportCompare.SameBytes(expected.Battles[index], actual.Battles[index]))
                {
                    List<string> diff = BattleDump.Diff(expected.Battles[index],
                        actual.Battles[index], "golden", "live");
                    return (diff.Count == 0 ? "battle" : FieldOf(diff[0]),
                        string.Join('\n', diff.Take(16)));
                }
            }
            if (!RulesCodec.Encode(expected.Rules).AsSpan()
                .SequenceEqual(RulesCodec.Encode(actual.Rules)))
            {
                IReadOnlyList<string> differences = RulesDiff.Compare(expected.Rules, actual.Rules);
                return (differences.Count == 0 ? "rules" : differences[0].Split(' ')[0],
                    string.Join('\n', differences.Take(16)));
            }
            BeamOptions expectedOptions = expected.Options;
            BeamOptions actualOptions = actual.Options;
            if (expectedOptions.Scorer?.RulesForCodec.InitialPlayerMaxHp
                != actualOptions.Scorer?.RulesForCodec.InitialPlayerMaxHp)
                return ("initialPlayerMaxHp",
                    $"{expectedOptions.Scorer?.RulesForCodec.InitialPlayerMaxHp}"
                    + $"->{actualOptions.Scorer?.RulesForCodec.InitialPlayerMaxHp}");
            if (expectedOptions.Scorer != null && actualOptions.Scorer != null)
            {
                byte[] expectedScore = EncodeScoreProbe(expected.Rules, expectedOptions.Scorer);
                byte[] actualScore = EncodeScoreProbe(actual.Rules, actualOptions.Scorer);
                if (!expectedScore.AsSpan().SequenceEqual(actualScore))
                    return ("score", "score subpart differs: " + ConfigCompare.ScoreRulesDiff(
                        expectedOptions.Scorer.RulesForCodec, actualOptions.Scorer.RulesForCodec));
            }
            if (expectedOptions.Bound != null && actualOptions.Bound != null)
            {
                byte[] expectedBound = EncodeBoundProbe(expected.Rules, expectedOptions.Bound);
                byte[] actualBound = EncodeBoundProbe(actual.Rules, actualOptions.Bound);
                if (!expectedBound.AsSpan().SequenceEqual(actualBound))
                    return ("bound", "bound subpart differs");
            }
            return ("<bytes>", "request bytes differ");
        }
        catch (InvalidDataException error)
        {
            return ("<bytes>", error.Message);
        }
    }

    private static string FirstDifferentLine(string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        int shared = Math.Min(expectedLines.Length, actualLines.Length);
        for (int index = 0; index < shared; index++)
        {
            if (!string.Equals(expectedLines[index], actualLines[index], StringComparison.Ordinal))
                return $"line={index + 1} expected=[{expectedLines[index]}] actual=[{actualLines[index]}]";
        }
        return expectedLines.Length == actualLines.Length
            ? ""
            : $"line={shared + 1} expected=[{(shared < expectedLines.Length ? expectedLines[shared] : "<eof>")}] "
                + $"actual=[{(shared < actualLines.Length ? actualLines[shared] : "<eof>")}]";
    }

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
