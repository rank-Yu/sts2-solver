// A game-version migration rebuilds native roots without decoding the previous Battle layout.
// Seeds and input identities are exported by the old codec; the frozen directory is read-only.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Differential;

internal static class VersionRootCapture
{
    internal sealed record WalkSeed(int RootIndex, int Seed);
    internal sealed record Metadata(string InputJson, string RulesSha256, string[] Choices, WalkSeed[] Walks, int[]? RootSeedSources = null);

    internal static int Run(string data, string metadataDirectory, string output, string root)
    {
        string source = Path.GetFullPath(data);
        string target = Path.GetFullPath(output);
        if (source == target || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("新版根必须写到冻结数据之外的独立目录。");
        if (root is "." or ".." || root.Contains('/') || root.Contains('\\'))
            throw new InvalidDataException("根名不能包含路径分隔符。");
        Metadata metadata = JsonSerializer.Deserialize<Metadata>(
            File.ReadAllText(Path.Combine(metadataDirectory, root + ".json")))
            ?? throw new InvalidDataException("缺少旧格式元数据。");
        string directory = Path.Combine(target, "d1", root);
        if (Directory.Exists(directory))
            throw new InvalidDataException("目标根已经存在，拒绝覆盖。");
        OfflineRequest request = Program.LoadRequest(data, "tests/corpora", root);
        string oldMetaPath = Path.Combine(source, "d1", root, "meta.json");
        if (File.Exists(oldMetaPath))
        {
            using JsonDocument oldMeta = JsonDocument.Parse(File.ReadAllText(oldMetaPath));
            if (oldMeta.RootElement.GetProperty("input").GetString() != metadata.InputJson
                || oldMeta.RootElement.GetProperty("rulesSha256").GetString() != metadata.RulesSha256)
                throw new InvalidDataException("元数据与冻结根的输入或规则标识不符。");
        }
        else if (metadata.RulesSha256.Length != 0)
            throw new InvalidDataException("新用例的旧规则标识必须为空。");

        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        List<GoldenBattleRoot> roots;
        byte[] config;
        if (request.FlashTurnSetupRootForTest)
        {
            SetupRootSet set = SetupRootSet.Build(loop, request, needConfig: true);
            Scorer scorer = new(set.Score ?? throw new InvalidDataException("缺少评分配置。"));
            BoundFeatures bound = set.Bound ?? throw new InvalidDataException("缺少下界配置。");
            roots = [];
            for (int index = 0; index < set.Roots.Count; index++)
                roots.Add(RecaptureRoot.MakeRoot(set.Roots[index], set.Choices[index], scorer, bound));
            config = SearchCodec.EncodeRequest(new SearchCase(set.Rules, [.. set.Roots],
                new BeamOptions(1, 1, 1, TimeSpan.Zero) { Scorer = scorer, Bound = bound }));
        }
        else
        {
            var state = RecaptureRoot.Build(loop, request, root);
            (roots, config) = request.FlashPrefixActions.Length != 0 || request.FlashPrefixPlan.Length != 0
                ? RecaptureRoot.CapturePrefix(state, request)
                : RecaptureRoot.CaptureOrdinary(state, request);
            if (request.FlashPrefixActions.Length != 0 || request.FlashPrefixPlan.Length != 0)
                RecaptureRoot.VerifyNativePrefix(loop, state, request);
        }
        if (metadata.RootSeedSources is null && roots.Count != metadata.Choices.Length)
            throw new InvalidDataException($"新版准备根数量 {metadata.Choices.Length} → {roots.Count}，需要显式确定游走种子的映射。");
        if (metadata.RootSeedSources is { } sources
            && (sources.Length != roots.Count || sources.Any(i => i < 0 || i >= metadata.Choices.Length)))
            throw new InvalidDataException("准备根种子映射不完整或下标越界。");
        SearchCase search = SearchCodec.DecodeRequest(config);
        byte[] rulesBytes = RulesCodec.Encode(search.Rules);
        string hash = Convert.ToHexString(SHA256.HashData(rulesBytes)).ToLowerInvariant();
        List<GoldenWalk> walks = [];
        for (int index = 0; index < roots.Count; index++)
        {
            int seedSource = metadata.RootSeedSources?[index] ?? index;
            WalkSeed[] seeds = metadata.Walks.Where(w => w.RootIndex == seedSource).ToArray();
            if (seeds.Length == 0) throw new InvalidDataException("旧格式元数据没有游走种子。");
            foreach (WalkSeed seed in seeds)
                walks.Add(new GoldenWalk { RootIndex = index, Seed = seed.Seed, States = [] });
        }
        GoldenRootRecord record = new() { InputJson = metadata.InputJson, RulesSha256 = hash,
            Roots = roots, K1 = [], Walks = walks };
        Directory.CreateDirectory(directory);
        string rulesDirectory = Path.Combine(target, "objects", "rules");
        Directory.CreateDirectory(rulesDirectory);
        WriteCompressed(Path.Combine(directory, "golden.br"), GoldenData.Encode(record));
        WriteCompressed(Path.Combine(directory, "config.br"), config);
        string rulesPath = Path.Combine(rulesDirectory, hash + ".br");
        WriteSharedRules(rulesPath, rulesBytes);
        File.WriteAllText(Path.Combine(directory, "meta.json"), JsonSerializer.Serialize(new {
            formatVersion = Codec.FormatVersion, battleSize = BattleCodec.SerializedSize, root,
            input = metadata.InputJson, rulesSha256 = hash,
            counts = new { roots = roots.Count, k1 = 0, walks = walks.Count, walkStates = 0, transitions = 0 }
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"VERSION_ROOT_CAPTURE root={root} oldRoots={metadata.Choices.Length} newRoots={roots.Count} rules={hash}");
        return 0;
    }


    private static void WriteSharedRules(string path, byte[] bytes)
    {
        // Several native capture processes can produce the same rules hash.
        // Publish only a complete compressed object.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteCompressed(temporary, bytes);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path))
            {
                using FileStream file = File.OpenRead(path);
                using BrotliStream br = new(file, CompressionMode.Decompress);
                using MemoryStream decoded = new();
                br.CopyTo(decoded);
                if (!decoded.ToArray().AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("同一规则哈希对应了不同数据。");
            }
        }
        finally { File.Delete(temporary); }
    }

    private static void WriteCompressed(string path, byte[] bytes)
    {
        using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write);
        using BrotliStream br = new(file, CompressionLevel.SmallestSize);
        br.Write(bytes);
    }
}
