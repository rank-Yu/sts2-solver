using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class ManifestProvenance
{
    internal const string D1 = "75217006903195bc94654680423bb9ca580ec48a";
    internal const string D2 = "75217006903195bc94654680423bb9ca580ec48a";
    internal const string D1Config = "66a43838d89e7a03b05c0f04d5e4941ff555baec";
    internal const string FormatMigration =
        "请求 v7→v8、结果仍为 v6：SREQ 删除 ScoreRules 的遗物计数规则、"
        + "BeamOptions.InitialActionCount 和 ScoreRules 的三个初始药水计数；"
        + "D1 config.br 与 D2 request.br 机械重编码，D1 的 13 个前缀根按计数为 0 重录 scoreHash，"
        + "其中 qf-misery-roster 的初始显式用药还影响其根/K1/K2 的 score 与 bound 哈希，"
        + "结构数据与其它 12 个根只更新根 scoreHash。SRES 不编码这些字段，D2 期望结果逐字节不变。"
        + "D1 评分哈希同时改为只写当前 ScoreFeatures 字段，删除遗物计数占位槽和重复的策略占位；"
        + "所有 D1 根的根/K1/游走状态 score 哈希按新字段集合重算，字段值、bound 哈希和结构不变。";
    /// <summary>
    /// Set only by the refreeze command for the manifest it writes: a content hash of the newly
    /// generated D2 expected results. The original capture hashes stay in <see cref="D1"/> and
    /// <see cref="D2"/>; this field records the new result provenance without changing the strict
    /// verifier's expected constants for the frozen data directory.
    /// </summary>
    internal static string? Refreeze { get; set; }

    /// <summary>
    /// Set only by the rerecord command for the manifest it writes: the re-recorded root names, the
    /// reason and a content hash of the new <c>golden.br</c> bytes. Like <see cref="Refreeze"/> it is
    /// carried through ordinary manifest rewrites instead of being dropped.
    /// </summary>
    internal static string? D1Rerecord { get; set; }
}

internal static class ManifestBuilder
{
    private const long TargetBytes = 52_428_800;
    private static readonly Regex AllowedFile = new(
        @"^(?:objects/rules/[0-9a-f]{64}\.br"
        + @"|d1/[^/]+/(?:config\.br|golden\.br|meta\.json)"
        + @"|d2/requests/[0-9a-f]{64}\.request\.br"
        + @"|d2/expected/[0-9a-f]{64}\.result\.br"
        + @"|d2/replay-report-[0-9]+-of-[0-9]+\.json"
        + @"|d2/rules-index-[0-9]+-of-[0-9]+\.json)$",
        RegexOptions.CultureInvariant);

    internal static void Write(string dataDirectory)
    {
        string rootDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(rootDirectory))
            throw new InvalidDataException($"数据目录不存在：{dataDirectory}");

        List<FileEntry> entries = [];
        foreach (string path in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = RelativePath(rootDirectory, path);
            if (relative == "manifest.json")
                continue;
            if (!AllowedFile.IsMatch(relative))
                throw new InvalidDataException($"manifest 不认识的文件：{relative}");
            byte[] bytes = File.ReadAllBytes(path);
            entries.Add(new FileEntry(relative, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        entries.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        string d1Directory = Path.Combine(rootDirectory, "d1");
        string objectsDirectory = Path.Combine(rootDirectory, "objects", "rules");
        string d2Directory = Path.Combine(rootDirectory, "d2");
        string requestDirectory = Path.Combine(d2Directory, "requests");
        string expectedDirectory = Path.Combine(d2Directory, "expected");
        if (!Directory.Exists(d1Directory) || !Directory.Exists(objectsDirectory)
            || !Directory.Exists(requestDirectory) || !Directory.Exists(expectedDirectory))
            throw new InvalidDataException("manifest 布局缺少 d1/objects/d2 必需的目录。");

        HashSet<string> referencedRules = new(StringComparer.Ordinal);
        int d1Roots = 0, d1K1 = 0, d1Walks = 0, d1WalkStates = 0, d1Transitions = 0;
        string[] d1Directories = Directory.EnumerateDirectories(d1Directory)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (string directory in d1Directories)
        {
            string rootName = Path.GetFileName(directory);
            if (Directory.EnumerateDirectories(directory).Any())
                throw new InvalidDataException($"D1 根 {rootName} 含有未知子目录。");
            HashSet<string> names = Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.Ordinal)!;
            if (!names.SetEquals(new[] { "config.br", "golden.br", "meta.json" }))
                throw new InvalidDataException($"D1 根 {rootName} 必须恰好有 config.br、golden.br、meta.json。");

            string metaPath = Path.Combine(directory, "meta.json");
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
            JsonElement metaRoot = meta.RootElement;
            if (metaRoot.GetProperty("formatVersion").GetInt32() != Codec.FormatVersion
                || metaRoot.GetProperty("battleSize").GetInt32() != Unsafe.SizeOf<Battle>())
                throw new InvalidDataException($"D1 根 {rootName} 的格式版本或战斗大小不符。");
            string rulesHash = metaRoot.GetProperty("rulesSha256").GetString()
                ?? throw new InvalidDataException($"D1 根 {rootName} 缺少 rulesSha256。");
            if (!IsHash(rulesHash))
                throw new InvalidDataException($"D1 根 {rootName} 的 rulesSha256 非法。");
            referencedRules.Add(rulesHash);
            JsonElement counts = metaRoot.GetProperty("counts");
            d1Roots += NonNegative(counts, "roots", rootName);
            d1K1 += NonNegative(counts, "k1", rootName);
            d1Walks += NonNegative(counts, "walks", rootName);
            d1WalkStates += NonNegative(counts, "walkStates", rootName);
            d1Transitions += NonNegative(counts, "transitions", rootName);
        }

        HashSet<string> requestHashes = HashesIn(requestDirectory, "*.request.br", ".request.br");
        HashSet<string> expectedHashes = HashesIn(expectedDirectory, "*.result.br", ".result.br");
        if (!requestHashes.SetEquals(expectedHashes))
            throw new InvalidDataException("D2 requests 与 expected 文件不一一对应。");

        int captured = -1, unique = -1, comparable = 0, equal = 0, mismatch = 0, timeAffected = 0;
        int reportCount = 0;
        foreach (string reportPath in Directory.EnumerateFiles(d2Directory, "replay-report-*.json")
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            JsonElement reportRoot = report.RootElement;
            if (reportRoot.GetProperty("formatVersion").GetInt32() != Codec.FormatVersion)
                throw new InvalidDataException($"{Path.GetFileName(reportPath)} 的格式版本不符。");
            int reportCaptured = reportRoot.GetProperty("requestsCaptured").GetInt32();
            int reportUnique = reportRoot.GetProperty("requestsUnique").GetInt32();
            if (captured < 0)
            {
                captured = reportCaptured;
                unique = reportUnique;
            }
            else if (captured != reportCaptured || unique != reportUnique)
            {
                throw new InvalidDataException("D2 replay-report 的 captured/unique 计数不一致。");
            }
            comparable += reportRoot.GetProperty("comparable").GetInt32();
            equal += reportRoot.GetProperty("equal").GetInt32();
            mismatch += reportRoot.GetProperty("mismatch").GetInt32();
            timeAffected += reportRoot.GetProperty("timeAffected").GetInt32();
            reportCount++;
        }
        if (reportCount == 0)
            throw new InvalidDataException("缺少 D2 replay-report-*.json。");
        if (unique != requestHashes.Count)
            throw new InvalidDataException("D2 replay-report 的 unique 计数与请求文件数不符。");

        Dictionary<string, string> rulesIndex = new(StringComparer.Ordinal);
        int indexCount = 0;
        foreach (string indexPath in Directory.EnumerateFiles(d2Directory, "rules-index-*.json")
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            using JsonDocument index = JsonDocument.Parse(File.ReadAllText(indexPath));
            foreach (JsonProperty property in index.RootElement.EnumerateObject())
            {
                string requestHash = property.Name;
                string rulesHash = property.Value.GetString()
                    ?? throw new InvalidDataException($"{Path.GetFileName(indexPath)} 的规则引用不是字符串。");
                if (!IsHash(requestHash) || !IsHash(rulesHash))
                    throw new InvalidDataException($"{Path.GetFileName(indexPath)} 含有非法哈希。");
                if (rulesIndex.TryGetValue(requestHash, out string? existing)
                    && !string.Equals(existing, rulesHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"D2 请求 {requestHash} 的规则引用不一致。");
                rulesIndex[requestHash] = rulesHash;
            }
            indexCount++;
        }
        if (requestHashes.Count > 0 && indexCount == 0)
            throw new InvalidDataException("缺少 D2 rules-index-*.json。");
        if (!rulesIndex.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(requestHashes))
            throw new InvalidDataException("D2 规则索引没有覆盖全部请求或含未知请求。");

        HashSet<string> ruleObjects = entries
            .Where(static entry => entry.Path.StartsWith("objects/rules/", StringComparison.Ordinal))
            .Select(static entry => entry.Path["objects/rules/".Length..^".br".Length])
            .ToHashSet(StringComparer.Ordinal);
        // SREQ embeds its complete RULE bytes: D2 rules-index values do not reference
        // objects/rules entries. Only D1 meta.rulesSha256 does.
        string[] orphanRules = ruleObjects.Where(hash => !referencedRules.Contains(hash))
            .Order(StringComparer.Ordinal).ToArray();
        if (orphanRules.Length > 0)
            throw new InvalidDataException($"manifest 不认识的孤儿规则对象：{string.Join(",", orphanRules)}");
        string[] missingRules = referencedRules.Where(hash => !ruleObjects.Contains(hash))
            .Order(StringComparer.Ordinal).ToArray();
        if (missingRules.Length > 0)
            throw new InvalidDataException($"manifest 缺少 D1 引用的规则对象：{string.Join(",", missingRules)}");

        long totalBytes = entries.Sum(static entry => entry.Bytes);
        (string? existingRefreeze, string? existingRerecord) = ReadProvenance(
            Path.Combine(rootDirectory, "manifest.json"));
        byte[] manifestBytes = WriteJson(d1Directories.Length, d1Roots, d1K1, d1Walks, d1WalkStates,
            d1Transitions, captured, unique, expectedHashes.Count, comparable, equal, mismatch,
            timeAffected, totalBytes, entries, existingRefreeze, existingRerecord);
        string manifestPath = Path.Combine(rootDirectory, "manifest.json");
        string temporary = manifestPath + ".tmp";
        File.WriteAllBytes(temporary, manifestBytes);
        File.Move(temporary, manifestPath, overwrite: true);
        Console.WriteLine($"MANIFEST_WRITTEN files={entries.Count} totalBytes={totalBytes} "
            + $"d1Files={d1Directories.Length} d1Roots={d1Roots} d1K1={d1K1} d1Walks={d1Walks} "
            + $"d1WalkStates={d1WalkStates} d1Transitions={d1Transitions} d1Configs={d1Directories.Length} "
            + $"d2Requests={requestHashes.Count} d2Expected={expectedHashes.Count}");
    }

    private static (string? Refreeze, string? D1Rerecord) ReadProvenance(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return (null, null);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!manifest.RootElement.TryGetProperty("provenance", out JsonElement provenance))
            return (null, null);
        return (
            provenance.TryGetProperty("refreeze", out JsonElement refreeze)
                ? refreeze.GetString()
                : null,
            provenance.TryGetProperty("d1Rerecord", out JsonElement rerecord)
                ? rerecord.GetString()
                : null);
    }

    private static byte[] WriteJson(int d1Files, int d1Roots, int d1K1, int d1Walks,
        int d1WalkStates, int d1Transitions, int captured, int unique, int expected, int comparable,
        int equal, int mismatch, int timeAffected, long totalBytes, List<FileEntry> entries,
        string? existingRefreeze, string? existingRerecord)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", Codec.FormatVersion);
            writer.WriteNumber("battleSize", Unsafe.SizeOf<Battle>());
            writer.WriteStartObject("provenance");
            writer.WriteString("d1", ManifestProvenance.D1);
            writer.WriteString("d2", ManifestProvenance.D2);
            writer.WriteString("d1Config", ManifestProvenance.D1Config);
            writer.WriteString("formatMigration", ManifestProvenance.FormatMigration);
            string? refreeze = ManifestProvenance.Refreeze ?? existingRefreeze;
            if (refreeze != null)
                writer.WriteString("refreeze", refreeze);
            string? rerecord = ManifestProvenance.D1Rerecord ?? existingRerecord;
            if (rerecord != null)
                writer.WriteString("d1Rerecord", rerecord);
            writer.WriteEndObject();
            writer.WriteString("compression", "brotli");
            writer.WriteStartObject("counts");
            writer.WriteStartObject("d1");
            writer.WriteNumber("roots", d1Roots);
            writer.WriteNumber("k1", d1K1);
            writer.WriteNumber("walks", d1Walks);
            writer.WriteNumber("walkStates", d1WalkStates);
            writer.WriteNumber("transitions", d1Transitions);
            writer.WriteNumber("files", d1Files);
            writer.WriteEndObject();
            writer.WriteStartObject("d2");
            writer.WriteNumber("capturedCalls", captured);
            writer.WriteNumber("uniqueRequests", unique);
            writer.WriteNumber("expectedResults", expected);
            writer.WriteNumber("comparable", comparable);
            writer.WriteNumber("equal", equal);
            writer.WriteNumber("mismatch", mismatch);
            writer.WriteNumber("timeAffected", timeAffected);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteNumber("totalBytes", totalBytes);
            writer.WriteNumber("targetBytes", TargetBytes);
            writer.WriteStartArray("files");
            foreach (FileEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path);
                writer.WriteNumber("bytes", entry.Bytes);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        byte[] json = stream.ToArray();
        byte[] withNewline = new byte[json.Length + 1];
        json.CopyTo(withNewline, 0);
        withNewline[^1] = (byte)'\n';
        return withNewline;
    }

    private static HashSet<string> HashesIn(string directory, string pattern, string suffix)
    {
        HashSet<string> hashes = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, pattern))
        {
            string name = Path.GetFileName(path);
            string hash = name[..^suffix.Length];
            if (!IsHash(hash))
                throw new InvalidDataException($"D2 文件名不是 SHA-256：{name}");
            if (!hashes.Add(hash))
                throw new InvalidDataException($"D2 哈希重复：{hash}");
        }
        return hashes;
    }

    private static int NonNegative(JsonElement counts, string name, string rootName)
    {
        int value = counts.GetProperty(name).GetInt32();
        if (value < 0)
            throw new InvalidDataException($"D1 根 {rootName} 的 {name} 为负数。");
        return value;
    }

    private static bool IsHash(string text)
        => text.Length == 64 && text.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static string RelativePath(string rootDirectory, string path)
        => Path.GetRelativePath(rootDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    private sealed record FileEntry(string Path, long Bytes, string Sha256);
}

internal static class ManifestCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("用法：Golden manifest <data-dir>");
            return 2;
        }
        ManifestBuilder.Write(args[0]);
        return 0;
    }
}
