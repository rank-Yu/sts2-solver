using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Golden;

/// <summary>
/// Re-records selected D1 roots with the current kernel on a copy of the frozen data directory. The
/// root battle, input mapping, rules hash and config sidecar are inputs and stay untouched; K1
/// completions, K2 walks, every derived hash and the meta counts are recomputed. The walk strategy is
/// the M0 host's: the recorded seed list, one <see cref="Random"/> per walk, 60 steps per walk and
/// the same card-favouring draw. A semantic change therefore diverges exactly where the changed
/// kernel first produces a different state.
/// </summary>
internal static class RerecordCommand
{
    private const int WalkStepLimit = 60;
    private const int OneStepChoiceCap = 64;

    internal static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "用法：Golden rerecord <source-dir> <target-dir> --roots <file> --reason <text> "
                + "[--recapture-root <dir>] [--jobs N]");
            return 2;
        }
        string source = Path.GetFullPath(args[0]);
        string target = Path.GetFullPath(args[1]);
        string? rootsPath = null;
        string? recapturePath = null;
        int jobs = Environment.ProcessorCount;
        string? reason = null;
        for (int index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--roots":
                    rootsPath = Next(args, ref index, "--roots");
                    break;
                case "--recapture-root":
                    recapturePath = Path.GetFullPath(Next(args, ref index, "--recapture-root"));
                    break;
                case "--jobs":
                    if (!int.TryParse(Next(args, ref index, "--jobs"), out jobs) || jobs <= 0)
                        throw new InvalidDataException("--jobs 需要正整数。");
                    break;
                case "--reason":
                    reason = Next(args, ref index, "--reason");
                    if (reason.Length == 0)
                        throw new InvalidDataException("--reason 不能为空。");
                    break;
                default:
                    throw new InvalidDataException($"未知参数：{args[index]}");
            }
        }
        if (rootsPath == null)
            throw new InvalidDataException("rerecord 需要 --roots <file>。");
        if (reason == null)
            throw new InvalidDataException("rerecord 需要 --reason <text>，写明这次重录对应的语义修正。");
        string sourcePrefix = source + Path.DirectorySeparatorChar;
        string targetPrefix = target + Path.DirectorySeparatorChar;
        if (string.Equals(source, target, StringComparison.Ordinal)
            || targetPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal)
            || sourcePrefix.StartsWith(targetPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("rerecord 的源目录与目标目录必须是两份互不包含的副本。");
        if (!Directory.Exists(source))
            throw new InvalidDataException($"源数据目录不存在：{source}");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidDataException($"目标目录已存在且非空：{target}");
        string sourceD1 = Path.Combine(source, "d1");
        if (!Directory.Exists(sourceD1))
            throw new InvalidDataException($"源数据目录缺少 d1：{source}");

        string[] roots = ReadRoots(Path.GetFullPath(rootsPath), sourceD1);
        if (recapturePath != null && !Directory.Exists(recapturePath))
            throw new InvalidDataException($"重新捕获载荷目录不存在：{recapturePath}");
        GoldenFiles.CopyDirectory(source, target);

        string targetD1 = Path.Combine(target, "d1");
        RerecordSummary[] summaries = new RerecordSummary[roots.Length];
        Parallel.For(0, roots.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, index =>
        {
            summaries[index] = RerecordRoot(source, targetD1, roots[index], recapturePath);
        });

        foreach (RerecordSummary summary in summaries)
            Console.WriteLine(summary.Line);

        string hash = RerecordHash(target, roots);
        string rootList = string.Join(',', roots.Order(StringComparer.Ordinal));
        ManifestProvenance.D1Rerecord = $"roots={rootList} "
            + (recapturePath == null ? "" : $"recaptureRoots={rootList} ")
            + $"reason={reason} sha256={hash}";
        ManifestBuilder.Write(target);
        int changed = summaries.Count(static summary => summary.Changed);
        Console.WriteLine($"RERECORD_OK source={source} target={target} roots={roots.Length} "
            + $"changed={changed} jobs={jobs} sha256={hash}");
        return 0;
    }

    private static string[] ReadRoots(string path, string sourceD1)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"--roots 文件不存在：{path}");
        List<string> roots = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string name = raw.Trim();
            if (name.Length == 0 || name[0] == '#')
                continue;
            if (name.Contains('/') || name.Contains('\\'))
                throw new InvalidDataException($"根名不能包含路径分隔符：{name}");
            if (!seen.Add(name))
                throw new InvalidDataException($"根名重复：{name}");
            if (!Directory.Exists(Path.Combine(sourceD1, name)))
                throw new InvalidDataException($"源数据目录没有根 {name}。");
            roots.Add(name);
        }
        if (roots.Count == 0)
            throw new InvalidDataException("--roots 文件没有列出任何根。");
        return [.. roots];
    }

    private static RerecordSummary RerecordRoot(string source, string targetD1, string rootName,
        string? recapturePath)
    {
        string sourceDirectory = Path.Combine(source, "d1", rootName);
        string targetDirectory = Path.Combine(targetD1, rootName);
        GoldenRootRecord oldRecord = GoldenData.Decode(
            GoldenFiles.ReadCompressed(Path.Combine(sourceDirectory, "golden.br")));
        List<GoldenBattleRoot> recordedRoots = oldRecord.Roots;
        string configDirectory = sourceDirectory;
        if (recapturePath != null)
        {
            string payloadDirectory = Path.Combine(recapturePath, rootName);
            string payloadRoots = Path.Combine(payloadDirectory, "roots.gold");
            string payloadConfig = Path.Combine(payloadDirectory, "config.br");
            if (!File.Exists(payloadRoots) || !File.Exists(payloadConfig))
                throw new InvalidDataException($"根 {rootName} 缺少重新捕获载荷：{payloadDirectory}");
            GoldenRootRecord recaptured = GoldenData.Decode(File.ReadAllBytes(payloadRoots));
            if (!string.Equals(recaptured.InputJson, oldRecord.InputJson, StringComparison.Ordinal)
                || !string.Equals(recaptured.RulesSha256, oldRecord.RulesSha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"根 {rootName} 的重新捕获载荷与冻结记录的输入/规则哈希不符。");
            if (recaptured.Roots.Count != oldRecord.Roots.Count)
                throw new InvalidDataException(
                    $"根 {rootName} 的重新捕获载荷有 {recaptured.Roots.Count} 个根，"
                    + $"冻结记录有 {oldRecord.Roots.Count} 个。");
            recordedRoots = recaptured.Roots;
            File.Copy(payloadConfig, Path.Combine(targetDirectory, "config.br"), overwrite: true);
            configDirectory = targetDirectory;
        }
        Rules rules = SimulationVerifier.LoadRules(source, oldRecord.RulesSha256);
        SearchCase config = SimulationVerifier.LoadConfig(configDirectory, rootName,
            oldRecord.RulesSha256);
        Scorer scorer = config.Options.Scorer
            ?? throw new InvalidDataException($"根 {rootName} 的特征配置没有 Scorer。");
        BoundFeatures? bound = config.Options.Bound;

        List<GoldenBattleRoot> roots = new(recordedRoots.Count);
        foreach (GoldenBattleRoot root in recordedRoots)
        {
            (_, ulong scoreHash, byte boundStatus, ulong boundHash) =
                SimulationVerifier.ComputeFeatures(root.Battle, 0, scorer, bound);
            roots.Add(new GoldenBattleRoot
            {
                Battle = root.Battle,
                Choices = root.Choices,
                ScoreHash = scoreHash,
                BoundStatus = boundStatus,
                BoundHash = boundHash,
            });
        }

        List<GoldenK1Action> k1 = [];
        Span<PlayerAction> actions = stackalloc PlayerAction[Kernel.MaxLegalActions];
        for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            Battle root = roots[rootIndex].Battle;
            ScoreFeatures rootFeatures = scorer.Features(root, 0);
            int count = Kernel.LegalActions(root, rules, actions);
            for (int actionIndex = 0; actionIndex < count; actionIndex++)
            {
                PlayerAction baseAction = actions[actionIndex];
                List<Completion> completions = [];
                try
                {
                    Complete(rules, root, baseAction, [], completions);
                }
                catch (UnsupportedException)
                {
                    continue;
                }
                catch (CapacityException)
                {
                    continue;
                }
                foreach (Completion completion in completions)
                {
                    if (completion.DeadEnd)
                        continue;
                    (ScoreFeatures after, ulong scoreHash, byte boundStatus, ulong boundHash) =
                        SimulationVerifier.ComputeFeatures(completion.Child, 1, scorer, bound);
                    (bool traitPresent, ulong traitHash) = SimulationVerifier.ComputeTrait(root,
                        rootFeatures, after, completion.Child, completion.Action, rules,
                        completion.HistoryRisk);
                    PlayerAction recorded = completion.Action;
                    int choices = recorded.ChoiceCount;
                    ulong[] selections = new ulong[choices];
                    for (int choice = 0; choice < choices; choice++)
                        selections[choice] = recorded.Choices[choice];
                    recorded.ChoiceCount = 0;
                    k1.Add(new GoldenK1Action
                    {
                        RootIndex = rootIndex,
                        BaseAction = recorded,
                        Selections = selections,
                        Requests = completion.Requests,
                        ChildHash = Codec.BattleHash(completion.Child),
                        Ended = !Kernel.IsInProgress(completion.Child),
                        ScoreHash = scoreHash,
                        BoundStatus = boundStatus,
                        BoundHash = boundHash,
                        TraitPresent = traitPresent,
                        TraitHash = traitHash,
                    });
                }
            }
        }

        List<GoldenWalk> walks = [];
        foreach (GoldenWalk oldWalk in oldRecord.Walks)
        {
            if ((uint)oldWalk.RootIndex >= (uint)roots.Count)
                throw new InvalidDataException(
                    $"根 {rootName} 的游走根下标 {oldWalk.RootIndex} 越界。");
            Battle root = roots[oldWalk.RootIndex].Battle;
            (ScoreFeatures rootFeatures, ulong rootScore, byte rootBoundStatus, ulong rootBound) =
                SimulationVerifier.ComputeFeatures(root, 0, scorer, bound);
            ScoreFeatures walkFeatures = rootFeatures;
            ulong walkScore = rootScore;
            byte walkBoundStatus = rootBoundStatus;
            ulong walkBound = rootBound;
            Random random = new(oldWalk.Seed);
            Battle battle = root;
            List<GoldenState> states = [];
            for (int step = 0; step < WalkStepLimit; step++)
            {
                if (!Kernel.IsInProgress(battle))
                    break;
                int count = Kernel.LegalActions(battle, rules, actions);
                PlayerAction[] legal = new PlayerAction[count];
                byte[] statuses = new byte[count];
                ChoiceRequest[] immediate = new ChoiceRequest[count];
                for (int index = 0; index < count; index++)
                {
                    legal[index] = actions[index];
                    Battle copy = battle;
                    try
                    {
                        StepResult result = Kernel.Step(ref copy, actions[index], rules,
                            out ChoiceRequest request, out _);
                        statuses[index] = result == StepResult.NeedsChoice ? (byte)1 : (byte)0;
                        if (statuses[index] == 1)
                            immediate[index] = request;
                    }
                    catch (UnsupportedException)
                    {
                        statuses[index] = 2;
                    }
                    catch (CapacityException)
                    {
                        statuses[index] = 2;
                    }
                }

                PlayerAction action = count > 1 && random.Next(4) != 0
                    ? actions[random.Next(count - 1)]
                    : actions[random.Next(count)];
                Completion completion;
                try
                {
                    completion = CompleteRandomly(rules, battle, action, random);
                }
                catch (UnsupportedException)
                {
                    states.Add(new GoldenState
                    {
                        StateHash = Codec.BattleHash(battle),
                        LegalActions = legal,
                        ImmediateStatuses = statuses,
                        ImmediateRequests = immediate,
                        ScoreHash = walkScore,
                        BoundStatus = walkBoundStatus,
                        BoundHash = walkBound,
                    });
                    break;
                }
                catch (CapacityException)
                {
                    states.Add(new GoldenState
                    {
                        StateHash = Codec.BattleHash(battle),
                        LegalActions = legal,
                        ImmediateStatuses = statuses,
                        ImmediateRequests = immediate,
                        ScoreHash = walkScore,
                        BoundStatus = walkBoundStatus,
                        BoundHash = walkBound,
                    });
                    break;
                }
                if (completion.DeadEnd)
                {
                    states.Add(new GoldenState
                    {
                        StateHash = Codec.BattleHash(battle),
                        LegalActions = legal,
                        ImmediateStatuses = statuses,
                        ImmediateRequests = immediate,
                        ScoreHash = walkScore,
                        BoundStatus = walkBoundStatus,
                        BoundHash = walkBound,
                    });
                    break;
                }
                (ScoreFeatures after, ulong scoreHash, byte boundStatus, ulong boundHash) =
                    SimulationVerifier.ComputeFeatures(completion.Child, 1, scorer, bound);
                (bool traitPresent, ulong traitHash) = SimulationVerifier.ComputeTrait(battle,
                    walkFeatures, after, completion.Child, completion.Action, rules,
                    completion.HistoryRisk);
                PlayerAction recorded = completion.Action;
                int choices = recorded.ChoiceCount;
                ulong[] selections = new ulong[choices];
                for (int choice = 0; choice < choices; choice++)
                    selections[choice] = recorded.Choices[choice];
                recorded.ChoiceCount = 0;
                states.Add(new GoldenState
                {
                    StateHash = Codec.BattleHash(battle),
                    LegalActions = legal,
                    ImmediateStatuses = statuses,
                    ImmediateRequests = immediate,
                    HasStep = true,
                    StepBase = recorded,
                    StepSelections = selections,
                    StepRequests = completion.Requests,
                    ChildHash = Codec.BattleHash(completion.Child),
                    Ended = !Kernel.IsInProgress(completion.Child),
                    // M0's FinishWalkStep overwrites the state's score/bound with the child values;
                    // a no-step state keeps the values passed to BeginWalkState.
                    ScoreHash = scoreHash,
                    BoundStatus = boundStatus,
                    BoundHash = boundHash,
                    TraitPresent = traitPresent,
                    TraitHash = traitHash,
                });
                walkFeatures = after;
                walkScore = scoreHash;
                walkBoundStatus = boundStatus;
                walkBound = boundHash;
                battle = completion.Child;
                if (!Kernel.IsInProgress(battle))
                {
                    states.Add(new GoldenState
                    {
                        StateHash = Codec.BattleHash(battle),
                        LegalActions = [],
                        ImmediateStatuses = [],
                        ImmediateRequests = [],
                        ScoreHash = walkScore,
                        BoundStatus = walkBoundStatus,
                        BoundHash = walkBound,
                    });
                    break;
                }
            }
            walks.Add(new GoldenWalk
            {
                RootIndex = oldWalk.RootIndex,
                Seed = oldWalk.Seed,
                States = states,
            });
        }

        GoldenRootRecord newRecord = new()
        {
            InputJson = oldRecord.InputJson,
            RulesSha256 = oldRecord.RulesSha256,
            Roots = roots,
            K1 = k1,
            Walks = walks,
        };
        GoldenFiles.WriteCompressedSmallest(Path.Combine(targetDirectory, "golden.br"),
            GoldenData.Encode(newRecord));
        UpdateMeta(Path.Combine(targetDirectory, "meta.json"), newRecord);
        return Summarize(rootName, oldRecord, newRecord, rules, scorer);
    }

    private static void UpdateMeta(string path, GoldenRootRecord record)
    {
        int walkStates = 0, transitions = 0;
        foreach (GoldenWalk walk in record.Walks)
        {
            walkStates += walk.States.Count;
            transitions += walk.States.Count(static state => state.HasStep);
        }
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement metaRoot = meta.RootElement;
        JsonElement counts = metaRoot.GetProperty("counts");
        if (counts.GetProperty("roots").GetInt32() == record.Roots.Count
            && counts.GetProperty("k1").GetInt32() == record.K1.Count
            && counts.GetProperty("walks").GetInt32() == record.Walks.Count
            && counts.GetProperty("walkStates").GetInt32() == walkStates
            && counts.GetProperty("transitions").GetInt32() == transitions)
        {
            return;
        }
        Dictionary<string, object?> updated = new()
        {
            ["formatVersion"] = metaRoot.GetProperty("formatVersion").GetInt32(),
            ["battleSize"] = metaRoot.GetProperty("battleSize").GetInt32(),
            ["root"] = metaRoot.GetProperty("root").GetString(),
            ["input"] = metaRoot.GetProperty("input").GetString(),
            ["rulesSha256"] = metaRoot.GetProperty("rulesSha256").GetString(),
            ["counts"] = new Dictionary<string, int>
            {
                ["roots"] = record.Roots.Count,
                ["k1"] = record.K1.Count,
                ["walks"] = record.Walks.Count,
                ["walkStates"] = walkStates,
                ["transitions"] = transitions,
            },
        };
        File.WriteAllText(path,
            JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

    private static bool Complete(Rules rules, in Battle battle, PlayerAction action,
        List<ChoiceRequest> requests, List<Completion> output)
    {
        Battle child = battle;
        StepResult result = Kernel.Step(ref child, action, rules, out ChoiceRequest request,
            out bool historyRisk);
        if (result == StepResult.Completed)
        {
            output.Add(new Completion(action, [.. requests], child, false, historyRisk));
            return false;
        }
        Span<ulong> selections = stackalloc ulong[OneStepChoiceCap];
        int count = ChoiceEnumerator.Enumerate(request, selections);
        if (count == 0)
        {
            output.Add(new Completion(action, [.. requests], default, true, false));
            return false;
        }
        bool capped = ChoiceEnumerator.Count(request, OneStepChoiceCap + 1) > count;
        requests.Add(request);
        for (int index = 0; index < count && output.Count < OneStepChoiceCap; index++)
            capped |= Complete(rules, battle, action.WithChoice(selections[index]), requests, output);
        requests.RemoveAt(requests.Count - 1);
        return capped || output.Count >= OneStepChoiceCap;
    }

    private static Completion CompleteRandomly(Rules rules, in Battle battle, PlayerAction action,
        Random random)
    {
        List<ChoiceRequest> requests = [];
        Span<ulong> selections = stackalloc ulong[OneStepChoiceCap];
        while (true)
        {
            Battle child = battle;
            if (Kernel.Step(ref child, action, rules, out ChoiceRequest request,
                    out bool historyRisk) == StepResult.Completed)
            {
                return new Completion(action, [.. requests], child, false, historyRisk);
            }
            int count = ChoiceEnumerator.Enumerate(request, selections);
            if (count == 0)
                return new Completion(action, [.. requests], default, true, false);
            requests.Add(request);
            action = action.WithChoice(selections[random.Next(count)]);
        }
    }

    private static RerecordSummary Summarize(string rootName, GoldenRootRecord oldRecord,
        GoldenRootRecord newRecord, Rules rules, Scorer scorer)
    {
        int oldK1 = oldRecord.K1.Count, newK1 = newRecord.K1.Count;
        int changedK1 = 0;
        int firstK1 = -1;
        for (int index = 0; index < Math.Max(oldK1, newK1); index++)
        {
            if (index < oldK1 && index < newK1
                && K1Equals(oldRecord.K1[index], newRecord.K1[index]))
                continue;
            changedK1++;
            if (firstK1 < 0)
                firstK1 = index;
        }

        int oldStates = 0, oldTransitions = 0, newStates = 0, newTransitions = 0;
        foreach (GoldenWalk walk in oldRecord.Walks)
        {
            oldStates += walk.States.Count;
            oldTransitions += walk.States.Count(static state => state.HasStep);
        }
        foreach (GoldenWalk walk in newRecord.Walks)
        {
            newStates += walk.States.Count;
            newTransitions += walk.States.Count(static state => state.HasStep);
        }

        int firstWalk = -1, firstWalkState = -1;
        bool walksEqual = oldRecord.Walks.Count == newRecord.Walks.Count;
        for (int index = 0; index < Math.Min(oldRecord.Walks.Count, newRecord.Walks.Count)
             && firstWalk < 0; index++)
        {
            GoldenWalk oldWalk = oldRecord.Walks[index];
            GoldenWalk newWalk = newRecord.Walks[index];
            if (oldWalk.RootIndex != newWalk.RootIndex || oldWalk.Seed != newWalk.Seed
                || oldWalk.States.Count != newWalk.States.Count)
            {
                firstWalk = index;
                walksEqual = false;
                firstWalkState = 0;
                continue;
            }
            for (int stateIndex = 0; stateIndex < oldWalk.States.Count; stateIndex++)
            {
                if (StateEquals(oldWalk.States[stateIndex], newWalk.States[stateIndex]))
                    continue;
                firstWalk = index;
                firstWalkState = stateIndex;
                walksEqual = false;
                break;
            }
        }
        if (oldRecord.Walks.Count != newRecord.Walks.Count && firstWalk < 0)
        {
            firstWalk = Math.Min(oldRecord.Walks.Count, newRecord.Walks.Count);
            firstWalkState = 0;
            walksEqual = false;
        }
        bool rootsEqual = RootHashesEqual(oldRecord, newRecord);
        bool changed = !rootsEqual || changedK1 != 0 || !walksEqual;

        string first = "none";
        string action = "-";
        if (!rootsEqual)
        {
            first = "root";
        }
        else if (firstK1 >= 0)
        {
            first = $"k1[{firstK1}]";
            GoldenK1Action entry = firstK1 < newK1 ? newRecord.K1[firstK1] : oldRecord.K1[firstK1];
            Battle parent = newRecord.Roots[entry.RootIndex].Battle;
            action = DescribeAction(parent, entry.BaseAction);
        }
        else if (firstWalk >= 0)
        {
            GoldenWalk walk = firstWalk < newRecord.Walks.Count
                ? newRecord.Walks[firstWalk]
                : oldRecord.Walks[firstWalk];
            first = $"walk seed={walk.Seed} state={firstWalkState}";
            action = DescribeWalkAction(walk, newRecord, rules, firstWalkState);
        }
        return new RerecordSummary(
            $"RERECORD {rootName} {(changed ? "changed" : "unchanged")} first={first} action={action} "
            + $"k1Changed={changedK1} k1={oldK1}->{newK1} "
            + $"walks={oldRecord.Walks.Count}->{newRecord.Walks.Count} "
            + $"states={oldStates}->{newStates} transitions={oldTransitions}->{newTransitions}",
            changed);
    }

    private static bool RootHashesEqual(GoldenRootRecord oldRecord, GoldenRootRecord newRecord)
    {
        if (oldRecord.Roots.Count != newRecord.Roots.Count)
            return false;
        for (int index = 0; index < oldRecord.Roots.Count; index++)
        {
            GoldenBattleRoot oldRoot = oldRecord.Roots[index];
            GoldenBattleRoot newRoot = newRecord.Roots[index];
            if (oldRoot.ScoreHash != newRoot.ScoreHash
                || oldRoot.BoundStatus != newRoot.BoundStatus
                || oldRoot.BoundHash != newRoot.BoundHash
                || !string.Equals(oldRoot.Choices, newRoot.Choices, StringComparison.Ordinal)
                || !BattleCodec.Encode(oldRoot.Battle).AsSpan()
                    .SequenceEqual(BattleCodec.Encode(newRoot.Battle)))
                return false;
        }
        return true;
    }

    private static bool K1Equals(GoldenK1Action left, GoldenK1Action right)
        => left.RootIndex == right.RootIndex
            && GoldenFiles.ActionEquals(left.BaseAction, right.BaseAction)
            && left.Selections.AsSpan().SequenceEqual(right.Selections)
            && RequestsEqual(left.Requests, right.Requests)
            && left.ChildHash == right.ChildHash
            && left.Ended == right.Ended
            && left.ScoreHash == right.ScoreHash
            && left.BoundStatus == right.BoundStatus
            && left.BoundHash == right.BoundHash
            && left.TraitPresent == right.TraitPresent
            && left.TraitHash == right.TraitHash;

    private static bool StateEquals(GoldenState left, GoldenState right)
    {
        if (left.StateHash != right.StateHash
            || left.LegalActions.Length != right.LegalActions.Length
            || left.HasStep != right.HasStep)
            return false;
        for (int index = 0; index < left.LegalActions.Length; index++)
        {
            if (!GoldenFiles.ActionEquals(left.LegalActions[index], right.LegalActions[index]))
                return false;
        }
        return left.ImmediateStatuses.AsSpan().SequenceEqual(right.ImmediateStatuses)
            && RequestsEqual(left.ImmediateRequests, right.ImmediateRequests)
            && (!left.HasStep
                || (GoldenFiles.ActionEquals(left.StepBase, right.StepBase)
                    && left.StepSelections.AsSpan().SequenceEqual(right.StepSelections)
                    && RequestsEqual(left.StepRequests, right.StepRequests)
                    && left.ChildHash == right.ChildHash
                    && left.Ended == right.Ended
                    && left.ScoreHash == right.ScoreHash
                    && left.BoundStatus == right.BoundStatus
                    && left.BoundHash == right.BoundHash
                    && left.TraitPresent == right.TraitPresent
                    && left.TraitHash == right.TraitHash));
    }

    private static bool RequestsEqual(ChoiceRequest[] left, ChoiceRequest[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            if (!RequestEquals(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static bool RequestEquals(in ChoiceRequest left, in ChoiceRequest right)
    {
        if (left.Kind != right.Kind || left.Effect != right.Effect
            || left.Timing != right.Timing || left.Source != right.Source
            || left.SourceIsNone != right.SourceIsNone
            || left.OptionsAreGenerated != right.OptionsAreGenerated
            || left.OptionCount != right.OptionCount || left.MinCount != right.MinCount
            || left.MaxCount != right.MaxCount)
            return false;
        for (int index = 0; index < left.OptionCount; index++)
        {
            if (left.Options[index] != right.Options[index])
                return false;
        }
        if (left.OptionsAreGenerated)
        {
            for (int index = 0; index < left.OptionCount; index++)
            {
                if (!Fingerprint.Equal(left.GeneratedOptions[index], right.GeneratedOptions[index]))
                    return false;
            }
        }
        return true;
    }

    private static string DescribeWalkAction(GoldenWalk walk, GoldenRootRecord record, Rules rules,
        int stateIndex)
    {
        if (stateIndex < 0 || stateIndex >= walk.States.Count)
            return "-";
        Battle battle = record.Roots[walk.RootIndex].Battle;
        for (int index = 0; index < stateIndex; index++)
        {
            GoldenState previous = walk.States[index];
            if (!previous.HasStep)
                return "-";
            PlayerAction action = previous.StepBase;
            for (int choice = 0; choice < previous.StepRequests.Length; choice++)
                action = action.WithChoice(previous.StepSelections[choice]);
            Battle child = battle;
            if (Kernel.Step(ref child, action, rules, out _, out _) != StepResult.Completed)
                return "-";
            battle = child;
        }
        GoldenState state = walk.States[stateIndex];
        return state.HasStep ? DescribeAction(battle, state.StepBase) : "-";
    }

    private static string DescribeAction(in Battle battle, in PlayerAction action)
    {
        switch (action.Kind)
        {
            case PlayerActionKind.PlayCard:
                if (action.HandIndex >= battle.HandCount)
                    return $"PLAY_CARD hand={action.HandIndex}";
                ushort cardId = battle.Cards[action.HandIndex].Id;
                return $"PLAY_CARD {Registry.Card(cardId).NativeId} hand={action.HandIndex} "
                    + $"target={action.Target}";
            case PlayerActionKind.UsePotion:
                if (action.PotionSlot >= battle.PotionSlotCount)
                    return $"USE_POTION slot={action.PotionSlot}";
                ushort stored = battle.Potions[action.PotionSlot];
                return stored == 0
                    ? $"USE_POTION slot={action.PotionSlot} empty"
                    : $"USE_POTION {Registry.Potion((ushort)(stored - 1)).NativeId} "
                        + $"slot={action.PotionSlot} target={action.Target}";
            case PlayerActionKind.EndTurn:
                return "END_TURN";
            default:
                return action.Kind.ToString();
        }
    }

    private static string RerecordHash(string target, string[] roots)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string root in roots.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.ASCII.GetBytes(root));
            byte[] payload = GoldenFiles.ReadCompressed(
                Path.Combine(target, "d1", root, "golden.br"));
            hash.AppendData(SHA256.HashData(payload));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (++index >= args.Length)
            throw new InvalidDataException($"{name} 缺少参数值。");
        return args[index];
    }

    private sealed record Completion(PlayerAction Action, ChoiceRequest[] Requests, Battle Child,
        bool DeadEnd, bool HistoryRisk);

    private sealed record RerecordSummary(string Line, bool Changed);
}
