using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Golden;

internal sealed record SimulationSummary(
    int Files,
    int Roots,
    int K1,
    int Walks,
    int WalkStates,
    int Transitions,
    long FeatureStates,
    long FeatureTraits,
    long RulesObjects,
    int SetupPauseChecks);

internal sealed record SimulationFileResult(
    int Roots,
    int K1,
    int Walks,
    int WalkStates,
    int Transitions,
    long FeatureStates,
    long FeatureTraits,
    int SetupPauseChecks);

internal static class SimulationVerifier
{
    private static readonly ConcurrentDictionary<string, Rules> RulesCache = new(StringComparer.Ordinal);

    internal static SimulationSummary Run(string dataDirectory, int jobs, int shard, int shards)
    {
        string d1Directory = Path.Combine(dataDirectory, "d1");
        if (!Directory.Exists(d1Directory))
            throw new InvalidDataException($"模拟回归目录不存在：{d1Directory}");
        string[] selected = Directory.EnumerateDirectories(d1Directory)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Where((_, index) => index % shards == shard)
            .ToArray();
        if (selected.Length == 0)
            throw new InvalidDataException($"--shard {shard}/{shards} 没有选到 模拟回归文件。");

        ConcurrentDictionary<string, SimulationFileResult> results = new(StringComparer.Ordinal);
        Parallel.ForEach(selected, new ParallelOptions { MaxDegreeOfParallelism = jobs }, directory =>
        {
            string name = Path.GetFileName(directory);
            results[name] = VerifyFile(dataDirectory, directory);
        });

        int roots = 0, k1 = 0, walks = 0, states = 0, transitions = 0;
        long featureStates = 0, featureTraits = 0;
        int setupPauseChecks = 0;
        foreach (string directory in selected)
        {
            string name = Path.GetFileName(directory);
            SimulationFileResult result = results[name];
            roots += result.Roots;
            k1 += result.K1;
            walks += result.Walks;
            states += result.WalkStates;
            transitions += result.Transitions;
            featureStates += result.FeatureStates;
            featureTraits += result.FeatureTraits;
            setupPauseChecks += result.SetupPauseChecks;
            Console.WriteLine($"ROOT {name} roots={result.Roots} k1={result.K1} walks={result.Walks} "
                + $"states={result.WalkStates} transitions={result.Transitions} "
                + $"features={result.FeatureStates} traits={result.FeatureTraits} OK");
        }
        return new SimulationSummary(selected.Length, roots, k1, walks, states, transitions,
            featureStates, featureTraits, RulesCache.Count, setupPauseChecks);
    }

    private static SimulationFileResult VerifyFile(string dataDirectory, string directory)
    {
        string rootName = Path.GetFileName(directory);
        string goldenPath = Path.Combine(directory, "golden.br");
        string metaPath = Path.Combine(directory, "meta.json");
        if (!File.Exists(goldenPath) || !File.Exists(metaPath))
            throw new InvalidDataException($"根 {rootName} 缺少 golden.br 或 meta.json。");

        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        JsonElement metaRoot = meta.RootElement;
        if (metaRoot.GetProperty("formatVersion").GetInt32() != Codec.FormatVersion)
            throw new InvalidDataException($"根 {rootName} 的格式版本不符。");
        if (metaRoot.GetProperty("battleSize").GetInt32() != BattleCodec.SerializedSize)
            throw new InvalidDataException($"根 {rootName} 的 Battle 大小不符。");

        GoldenRootRecord record = GoldenData.Decode(GoldenFiles.ReadCompressed(goldenPath));
        Rules rules = LoadRules(dataDirectory, record.RulesSha256).WithRelics(record.Roots[0].Relics);
        if (record.Roots.Any(root => !rules.Relics.Equivalent(root.Relics)))
            throw new InvalidDataException("D1 roots have different relic rosters.");
        SearchCase config = LoadConfig(directory, rootName, record.RulesSha256);
        Scorer scorer = config.Options.Scorer
            ?? throw new InvalidDataException($"根 {rootName} 的特征配置没有 Scorer。");
        BoundFeatures? bound = config.Options.Bound;

        if (record.Roots.Count == 0)
            throw new InvalidDataException($"根 {rootName} 没有 Sts2Solver 根。");
        JsonElement counts = metaRoot.GetProperty("counts");
        if (counts.GetProperty("roots").GetInt32() != record.Roots.Count
            || counts.GetProperty("k1").GetInt32() != record.K1.Count
            || counts.GetProperty("walks").GetInt32() != record.Walks.Count)
            throw new InvalidDataException($"根 {rootName} 的 meta 计数与 golden 文件不符。");

        long featureStates = 0, featureTraits = 0;
        int walkStates = 0, transitions = 0;
        int setupPauseChecks = 0;

        for (int index = 0; index < record.Roots.Count; index++)
        {
            GoldenBattleRoot root = record.Roots[index];
            CheckFeatures(rootName, $"root[{index}]", root.Battle, 0,
                scorer, bound, root.ScoreHash, root.BoundStatus, root.BoundHash);
            featureStates++;
            CheckRootEndTurnSetupPause(rootName, $"root[{index}]", root.Battle, rules,
                ref setupPauseChecks);
        }

        foreach (GoldenK1Action entry in record.K1)
        {
            if ((uint)entry.RootIndex >= (uint)record.Roots.Count)
                throw new InvalidDataException($"根 {rootName} 的 K1 根下标 {entry.RootIndex} 越界。");
            VerifyK1(rootName, record.Roots[entry.RootIndex].Battle, entry, rules,
                scorer, bound, ref featureStates, ref featureTraits,
                ref setupPauseChecks);
        }

        foreach (GoldenWalk walk in record.Walks)
        {
            if ((uint)walk.RootIndex >= (uint)record.Roots.Count)
                throw new InvalidDataException($"根 {rootName} 的游走根下标 {walk.RootIndex} 越界。");
            VerifyWalk(rootName, walk, record.Roots[walk.RootIndex].Battle, rules,
                scorer, bound, ref featureStates,
                ref featureTraits, ref walkStates, ref transitions,
                ref setupPauseChecks);
        }

        if (counts.GetProperty("walkStates").GetInt32() != walkStates
            || counts.GetProperty("transitions").GetInt32() != transitions)
            throw new InvalidDataException($"根 {rootName} 的游走计数与 meta 不符。");

        return new SimulationFileResult(record.Roots.Count, record.K1.Count, record.Walks.Count,
            walkStates, transitions, featureStates, featureTraits, setupPauseChecks);
    }

    private static void VerifyK1(string rootName, in Battle root, GoldenK1Action entry,
        Rules rules, Scorer scorer, BoundFeatures? bound,
        ref long featureStates, ref long featureTraits, ref int setupPauseChecks)
    {
        PlayerAction action = entry.BaseAction;
        int firstSetup = -1;
        for (int index = 0; index < entry.Requests.Length; index++)
        {
            Battle partial = root;
            StepResult result = Kernel.Step(ref partial, action, rules,
                out ChoiceRequest request, out _);
            if (result != StepResult.NeedsChoice)
                throw Mismatch(rootName, $"K1 root={entry.RootIndex} choice[{index}] expected needs-choice but got {result}");
            CompareRequest(rootName, $"K1 root={entry.RootIndex} choice[{index}]", entry.Requests[index], request);
            if (request.Timing == ChoiceTiming.PlayerTurnStart && firstSetup < 0)
                firstSetup = index;
            if (index >= entry.Selections.Length)
                throw Mismatch(rootName, $"K1 root={entry.RootIndex} missing selection {index}");
            action = action.WithChoice(entry.Selections[index]);
        }
        if (entry.Requests.Length != entry.Selections.Length)
            throw Mismatch(rootName, $"K1 root={entry.RootIndex} request/selection counts differ");

        Battle child = root;
        StepResult final = Kernel.Step(ref child, action, rules, out _, out bool historyRisk);
        if (final != StepResult.Completed)
            throw Mismatch(rootName, $"K1 root={entry.RootIndex} final step returned {final}");
        ulong expectedChild = Codec.BattleHash(child);
        if (entry.ChildHash != expectedChild)
            throw Mismatch(rootName, $"K1 root={entry.RootIndex} child hash expected={entry.ChildHash:X16} actual={expectedChild:X16}");
        CheckEndTurnSetupPause(rootName, $"K1 root={entry.RootIndex}", root, action, firstSetup,
            child, rules, ref setupPauseChecks);
        if (entry.Ended != !Kernel.IsInProgress(child))
            throw Mismatch(rootName, $"K1 root={entry.RootIndex} ended expected={entry.Ended} actual={!Kernel.IsInProgress(child)}");

        ScoreFeatures after = CheckFeatures(rootName, $"K1 root={entry.RootIndex}", child, 1,
            scorer, bound, entry.ScoreHash, entry.BoundStatus, entry.BoundHash);
        featureStates++;
        ScoreFeatures before = scorer.Features(root, 0);
        CheckTrait(rootName, $"K1 root={entry.RootIndex}", root, before, after, child, action,
            rules, historyRisk, entry.TraitPresent, entry.TraitHash);
        if (entry.TraitPresent)
            featureTraits++;
    }

    private static void VerifyWalk(string rootName, GoldenWalk walk, in Battle root,
        Rules rules, Scorer scorer, BoundFeatures? bound,
        ref long featureStates, ref long featureTraits, ref int walkStates, ref int transitions,
        ref int setupPauseChecks)
    {
        Battle battle = root;
        Span<PlayerAction> actions = stackalloc PlayerAction[Kernel.MaxLegalActions];
        for (int stateIndex = 0; stateIndex < walk.States.Count; stateIndex++)
        {
            GoldenState state = walk.States[stateIndex];
            ulong hash = Codec.BattleHash(battle);
            if (state.StateHash != hash)
                throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} hash expected={state.StateHash:X16} actual={hash:X16}");
            int count = Kernel.LegalActions(battle, rules, actions);
            if (count != state.LegalActions.Length)
                throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} legal count expected={state.LegalActions.Length} actual={count}");
            for (int index = 0; index < count; index++)
            {
                PlayerAction actual = actions[index];
                PlayerAction expected = state.LegalActions[index];
                if (!GoldenFiles.ActionEquals(actual, expected))
                    throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} legal[{index}] mismatch");
                if (index >= state.ImmediateStatuses.Length)
                    throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} missing status {index}");
                Battle copy = battle;
                byte status;
                ChoiceRequest request = default;
                try
                {
                    StepResult result = Kernel.Step(ref copy, actual, rules, out request, out _);
                    status = result == StepResult.NeedsChoice ? (byte)1 : (byte)0;
                }
                catch (UnsupportedException)
                {
                    status = 2;
                }
                catch (CapacityException)
                {
                    status = 2;
                }
                if (state.ImmediateStatuses[index] != status)
                    throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} legal[{index}] status expected={state.ImmediateStatuses[index]} actual={status}");
                if (status == 1)
                    CompareRequest(rootName, $"walk seed={walk.Seed} state={stateIndex} legal[{index}]",
                        state.ImmediateRequests[index], request);
            }
            walkStates++;
            if (!state.HasStep)
            {
                if (stateIndex != walk.States.Count - 1)
                    throw Mismatch(rootName, $"walk seed={walk.Seed} has states after a no-step state");
                break;
            }
            transitions++;

            PlayerAction action = state.StepBase;
            int firstSetup = -1;
            for (int index = 0; index < state.StepRequests.Length; index++)
            {
                Battle partial = battle;
                StepResult result = Kernel.Step(ref partial, action, rules,
                    out ChoiceRequest request, out _);
                if (result != StepResult.NeedsChoice)
                    throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} step choice[{index}] expected needs-choice but got {result}");
                CompareRequest(rootName, $"walk seed={walk.Seed} state={stateIndex} step choice[{index}]",
                    state.StepRequests[index], request);
                if (request.Timing == ChoiceTiming.PlayerTurnStart && firstSetup < 0)
                    firstSetup = index;
                if (index >= state.StepSelections.Length)
                    throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} missing step selection {index}");
                action = action.WithChoice(state.StepSelections[index]);
            }
            if (state.StepRequests.Length != state.StepSelections.Length)
                throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} step request/selection counts differ");

            Battle child = battle;
            StepResult final = Kernel.Step(ref child, action, rules, out _, out bool historyRisk);
            if (final != StepResult.Completed)
                throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} step returned {final}");
            ulong childHash = Codec.BattleHash(child);
            if (state.ChildHash != childHash)
                throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} child hash expected={state.ChildHash:X16} actual={childHash:X16}");
            CheckEndTurnSetupPause(rootName, $"walk seed={walk.Seed} state={stateIndex}", battle,
                action, firstSetup, child, rules, ref setupPauseChecks);
            if (state.Ended != !Kernel.IsInProgress(child))
                throw Mismatch(rootName, $"walk seed={walk.Seed} state={stateIndex} ended expected={state.Ended} actual={!Kernel.IsInProgress(child)}");

            ScoreFeatures after = CheckFeatures(rootName, $"walk seed={walk.Seed} state={stateIndex}",
                child, 1, scorer, bound, state.ScoreHash, state.BoundStatus, state.BoundHash);
            featureStates++;
            ScoreFeatures before = scorer.Features(battle, 0);
            CheckTrait(rootName, $"walk seed={walk.Seed} state={stateIndex}", battle, before, after, child,
                action, rules, historyRisk, state.TraitPresent, state.TraitHash);
            if (state.TraitPresent)
                featureTraits++;
            battle = child;
        }
        if (walk.States.Count > 0 && walk.States[^1].HasStep && walk.States[^1].Ended)
            throw Mismatch(rootName, $"walk seed={walk.Seed} ended without the recorded terminal state");
    }

    private static ScoreFeatures CheckFeatures(string rootName, string where, in Battle battle,
        int actionCount, Scorer scorer, BoundFeatures? bound, ulong scoreHash,
        byte boundStatus, ulong boundHash)
    {
        (ScoreFeatures features, ulong actualScore, byte actualStatus, ulong actualBound) =
            ComputeFeatures(battle, actionCount, scorer, bound);
        if (actualScore != scoreHash)
            throw Mismatch(rootName, $"{where} scoreHash expected={scoreHash:X16} actual={actualScore:X16}");
        if (actualStatus != boundStatus)
            throw Mismatch(rootName, $"{where} boundStatus expected={boundStatus} actual={actualStatus}");
        if (actualBound != boundHash)
            throw Mismatch(rootName, $"{where} boundHash expected={boundHash:X16} actual={actualBound:X16}");
        return features;
    }

    /// <summary>The K4 state hash inputs shared by the verifier and the D1 re-recorder.</summary>
    internal static (ScoreFeatures Features, ulong ScoreHash, byte BoundStatus, ulong BoundHash)
        ComputeFeatures(in Battle battle, int actionCount, Scorer scorer, BoundFeatures? bound)
    {
        ScoreFeatures features = scorer.Features(battle, actionCount);
        byte boundStatus;
        ulong boundHash = 0;
        if (bound is null)
        {
            boundStatus = 0;
        }
        else if (!bound.Enabled)
        {
            boundStatus = 2;
        }
        else
        {
            boundStatus = 1;
            BoundValues values = bound.Evaluate(battle, scorer.RulesForCodec.Rules, features.ExplicitPotionStrategicCost,
                includeSmartPotionLowerBound: true);
            boundHash = GoldenHash.Bound(values);
        }
        return (features, GoldenHash.Score(features), boundStatus, boundHash);
    }

    private static void CheckTrait(string rootName, string where, in Battle beforeBattle,
        in ScoreFeatures before, in ScoreFeatures after, in Battle child,
        in PlayerAction action, Rules rules, bool historyRisk, bool present, ulong expected)
    {
        if (!present)
        {
            if (expected != 0)
                throw Mismatch(rootName, $"{where} trait hash present=false expected={expected:X16}");
            return;
        }
        (bool actualPresent, ulong actual) = ComputeTrait(beforeBattle, before, after, child, action,
            rules, historyRisk);
        if (!actualPresent || actual != expected)
            throw Mismatch(rootName, $"{where} traitHash expected={expected:X16} actual={actual:X16}");
    }

    /// <summary>
    /// K4 route-trait hash for one completed transition. The frozen data records a trait for every
    /// K1 completion and every K2 step, including the killing step; only a walk state without a step
    /// (terminal or boundary) has no trait.
    /// </summary>
    internal static (bool Present, ulong Hash) ComputeTrait(in Battle beforeBattle,
        in ScoreFeatures before, in ScoreFeatures after, in Battle child, in PlayerAction action,
        Rules rules, bool historyRisk)
    {
        bool pure = BeamSolver.IsPureTransitionForTesting(beforeBattle, child, action, rules, historyRisk);
        bool declined = BeamSolver.DeclinedExtraTurn(beforeBattle, rules);
        SearchRouteTraits traits = BeamSolver.ClassifyRouteTraitsForTesting(before, after, action, declined, !pure);
        BeamSolver.PocketwatchState(child, rules, out int threshold, out int played);
        return (true, GoldenHash.Trait(traits, pure, declined, threshold, played));
    }

    internal static Rules LoadRules(string dataDirectory, string hash)
        => RulesCache.GetOrAdd(hash, key =>
        {
            string path = Path.Combine(dataDirectory, "objects", "rules", key + ".br");
            if (!File.Exists(path))
                throw new InvalidDataException($"缺少规则对象 {key}。");
            byte[] bytes = GoldenFiles.ReadCompressed(path);
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (actual != key)
                throw new InvalidDataException($"规则对象 {key} 的 SHA-256 是 {actual}。");
            return RulesCodec.Decode(bytes);
        });

    internal static SearchCase LoadConfig(string directory, string rootName, string rulesHash)
    {
        string path = Path.Combine(directory, "config.br");
        if (!File.Exists(path))
            throw new InvalidDataException($"缺少 模拟回归特征配置：{path}");
        SearchCase config = SearchCodec.DecodeRequest(GoldenFiles.ReadCompressed(path));
        string actual = Convert.ToHexString(SHA256.HashData(RulesCodec.Encode(config.Rules))).ToLowerInvariant();
        if (actual != rulesHash)
            throw new InvalidDataException($"根 {rootName} 的特征配置规则哈希是 {actual}，期望 {rulesHash}。");
        return config;
    }

    private static void CompareRequest(string rootName, string where,
        in ChoiceRequest expected, in ChoiceRequest actual)
    {
        if (expected.Kind != actual.Kind || expected.Effect != actual.Effect
            || expected.Timing != actual.Timing || expected.Source != actual.Source
            || expected.SourceIsNone != actual.SourceIsNone
            || expected.OptionsAreGenerated != actual.OptionsAreGenerated
            || expected.OptionCount != actual.OptionCount || expected.MinCount != actual.MinCount
            || expected.MaxCount != actual.MaxCount)
            throw Mismatch(rootName, $"{where} request header expected="
                + $"{expected.Kind}/{expected.Effect}/{expected.Timing}/{expected.Source}/"
                + $"{expected.OptionCount}/{expected.MinCount}/{expected.MaxCount} actual="
                + $"{actual.Kind}/{actual.Effect}/{actual.Timing}/{actual.Source}/"
                + $"{actual.OptionCount}/{actual.MinCount}/{actual.MaxCount}");
        for (int index = 0; index < expected.OptionCount; index++)
        {
            if (expected.Options[index] != actual.Options[index])
                throw Mismatch(rootName, $"{where} option[{index}] expected={expected.Options[index]} actual={actual.Options[index]}");
        }
        if (expected.OptionsAreGenerated)
        {
            for (int index = 0; index < expected.OptionCount; index++)
            {
                if (!Fingerprint.Equal(expected.GeneratedOptions[index], actual.GeneratedOptions[index]))
                    throw Mismatch(rootName, $"{where} generated option[{index}] mismatch");
            }
        }
    }


    /// <summary>
    /// Long-term contract for the end-turn setup path: every EndTurn transition whose next turn asks
    /// a PlayerTurnStart choice must produce exactly the Play root that
    /// <see cref="Kernel.PreparePlayerPlayPhase"/> produces from the same Start root and the same
    /// setup selections when the native selector answers the choice (no setup pause). Extra turns use
    /// the same contract; the replay receives the bootstrapped <c>ShouldTakeExtraTurn</c> fact from
    /// <see cref="Kernel.TryAdvanceToPlayerSetupStart"/>. The visible setup-page pause is a separate
    /// path exercised by the turn-setup roots.
    /// </summary>
    private static void CheckEndTurnSetupPause(string rootName, string where, in Battle before,
        in PlayerAction action, int firstSetup, in Battle child, Rules rules, ref int checks)
    {
        if (action.Kind != PlayerActionKind.EndTurn || firstSetup < 0)
            return;
        if (!Kernel.TryAdvanceToPlayerSetupStart(in before, rules, action, out Battle start,
                out bool takingExtraTurn))
            throw Mismatch(rootName, $"{where} setup-pause contract cannot advance to the setup entry");
        int choiceCount = action.ChoiceCount - firstSetup;
        if (choiceCount < 0 || choiceCount > Limits.MaxChoices)
            throw Mismatch(rootName, $"{where} setup-pause contract choice range {choiceCount}");
        Span<ulong> setupChoices = stackalloc ulong[choiceCount];
        for (int index = 0; index < choiceCount; index++)
            setupChoices[index] = action.Choices[firstSetup + index];
        Battle prepared = start;
        StepResult result = Kernel.PreparePlayerPlayPhase(ref prepared, rules, setupChoices, out _,
            takingExtraTurn, setupChoicePauses: false);
        checks++;
        if (result != StepResult.Completed)
            throw Mismatch(rootName, $"{where} setup-pause contract replay returned {result}");
        if (!Fingerprint.Equal(in child, in prepared))
        {
            string diff = string.Join(" | ",
                BattleDump.Diff(in child, in prepared, "endturn", "prepare").Take(6));
            throw Mismatch(rootName, $"{where} setup-pause contract root mismatch: {diff}");
        }
    }

    /// <summary>
    /// Runs one deterministic EndTurn from a root (first enumerated selection at each request) to
    /// cover roots whose recorded K1/walks happen not to execute EndTurn.
    /// </summary>
    private static void CheckRootEndTurnSetupPause(string rootName, string where, in Battle root,
        Rules rules, ref int checks)
    {
        if (!Kernel.IsInProgress(root) || root.Phase != PlayerPhase.Play || root.Side != Side.Player)
            return;
        Battle state = root;
        PlayerAction action = PlayerAction.EndTurn();
        int firstSetup = -1;
        Battle child = default;
        Span<ulong> buffer = stackalloc ulong[BeamSolver.MaxChoiceBranches];
        for (int depth = 0; ; depth++)
        {
            if (depth > Limits.MaxChoices)
                throw Mismatch(rootName, $"{where} setup-pause root scan exceeded the choice limit");
            Battle partial = state;
            StepResult result = Kernel.Step(ref partial, action, rules,
                out ChoiceRequest request, out _);
            if (result == StepResult.Completed)
            {
                child = partial;
                break;
            }
            if (request.Timing == ChoiceTiming.PlayerTurnStart && firstSetup < 0)
                firstSetup = action.ChoiceCount;
            if (action.ChoiceCount >= Limits.MaxChoices)
                throw Mismatch(rootName, $"{where} setup-pause root scan exceeded the choice limit");
            int written = ChoiceEnumerator.Enumerate(request, buffer);
            if (written == 0)
                throw Mismatch(rootName, $"{where} setup-pause root scan found no selections");
            action = action.WithChoice(buffer[0]);
        }
        CheckEndTurnSetupPause(rootName, where, root, action, firstSetup, child, rules,
            ref checks);
    }

    private static InvalidDataException Mismatch(string rootName, string detail)
        => new($"模拟回归 {rootName}：{detail}");

}
