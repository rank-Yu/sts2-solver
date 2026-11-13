using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Recomputes every D2 expected result with the current solver on a copy of the data directory and
/// rewrites the manifest. The command never touches the source directory; the coordinator decides
/// whether a refrozen copy replaces tests/Golden/data after user approval.
/// </summary>
internal static class RefreezeCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：Golden refreeze <source-dir> <target-dir> [--jobs N] [--reason 文本] [--add-requests <dir>] [--add-config-roots <file>]");
            return 2;
        }
        string source = Path.GetFullPath(args[0]);
        string target = Path.GetFullPath(args[1]);
        int jobs = Environment.ProcessorCount;
        string? reason = null;
        List<string> addRequestDirectories = [];
        List<string> configRootFiles = [];
        for (int index = 2; index < args.Length; index++)
        {
            if (args[index] == "--jobs")
            {
                if (++index >= args.Length || !int.TryParse(args[index], out jobs) || jobs <= 0)
                    throw new InvalidDataException("--jobs 需要正整数。");
            }
            else if (args[index] == "--reason")
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new InvalidDataException("--reason 需要非空文本。");
                reason = args[index];
            }
            else if (args[index] == "--add-config-roots")
            {
                if (++index >= args.Length || !File.Exists(args[index]))
                    throw new InvalidDataException("--add-config-roots 需要根名列表文件。");
                configRootFiles.Add(args[index]);
            }
            else if (args[index] == "--add-requests")
            {
                if (++index >= args.Length || args[index].Length == 0)
                    throw new InvalidDataException("--add-requests 需要目录。");
                addRequestDirectories.Add(Path.GetFullPath(args[index]));
            }
            else
            {
                throw new InvalidDataException($"未知参数：{args[index]}");
            }
        }
        string sourcePrefix = source + Path.DirectorySeparatorChar;
        string targetPrefix = target + Path.DirectorySeparatorChar;
        if (string.Equals(source, target, StringComparison.Ordinal)
            || targetPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal)
            || sourcePrefix.StartsWith(targetPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("refreeze 的源目录与目标目录必须是两份互不包含的副本。");
        if (!Directory.Exists(source))
            throw new InvalidDataException($"源数据目录不存在：{source}");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidDataException($"目标目录已存在且非空：{target}");

        GoldenFiles.CopyDirectory(source, target);
        int captured = ReadCapturedCalls(source);
        if (addRequestDirectories.Count > 0)
            captured += MergeCaptures(target, addRequestDirectories);
        if (configRootFiles.Count > 0)
            captured += MergeConfigRoots(target, configRootFiles);
        string requestDirectory = Path.Combine(target, "d2", "requests");
        string expectedDirectory = Path.Combine(target, "d2", "expected");
        string[] requests = Directory.EnumerateFiles(requestDirectory, "*.request.br")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (requests.Length == 0)
            throw new InvalidDataException("D2 requests 为空。");

        int completed = 0;
        Parallel.ForEach(requests, new ParallelOptions { MaxDegreeOfParallelism = jobs }, path =>
        {
            string hash = Path.GetFileName(path)[..^".request.br".Length];
            SearchCase search = SearchCodec.DecodeRequest(GoldenFiles.ReadCompressed(path));
            BeamOptions options = SearchBudgetPolicy.ApplyRegression(search.Options with
            {
                SoftTimeBudget = TimeSpan.MaxValue,
                Pacer = null,
                Trace = null,
            });
            BeamResult result = new BeamSolver(search.Rules, options)
                .Solve(search.Battles, CancellationToken.None);
            byte[] encoded = SearchCodec.EncodeResult(result);
            // Keep the stored bytes when the decoded result is unchanged, so a refreeze only
            // rewrites the results that actually changed.
            string expected = Path.Combine(expectedDirectory, hash + ".result.br");
            if (!File.Exists(expected) || !GoldenFiles.ReadCompressed(expected).AsSpan().SequenceEqual(encoded))
                GoldenFiles.WriteCompressed(expected, encoded);
            int done = Interlocked.Increment(ref completed);
            if (done % 200 == 0)
                Console.WriteLine($"REFREEZE progress={done}/{requests.Length}");
        });

        foreach (string report in Directory.EnumerateFiles(Path.Combine(target, "d2"),
                     "replay-report-*.json"))
            File.Delete(report);
        WriteReport(Path.Combine(target, "d2", "replay-report-1-of-1.json"), captured, requests.Length);
        string content = ResultSetHash(requests, expectedDirectory);
        ManifestProvenance.Refreeze = reason is null
            ? $"M3b current-code results sha256={content}"
            : $"{reason} sha256={content}";
        ManifestBuilder.Write(target);
        Console.WriteLine($"REFREEZE_OK source={source} target={target} requests={requests.Length} "
            + $"captured={captured} jobs={jobs} refreeze={ManifestProvenance.Refreeze}");
        return 0;
    }

    // Prefix and turn-setup roots also receive a deterministic search regression. Their native
    // evaluation sidecars lack production coordinator options, so record an explicit fixed-budget
    // request and report this source separately from production member captures.
    private static int MergeConfigRoots(string target, IEnumerable<string> files)
    {
        string[] roots = files.SelectMany(File.ReadAllLines).Select(static s => s.Trim())
            .Where(static s => s.Length > 0 && !s.StartsWith('#'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Dictionary<string, string> rules = new(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            if (Path.GetFileName(root) != root || root is "." or "..")
                throw new InvalidDataException($"非法根名：{root}");
            SearchCase search = GoldenFixtures.SearchConfig(Path.Combine(target, "d1", root, "config.br"));
            byte[] bytes = SearchCodec.EncodeRequest(search);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            GoldenFiles.WriteCompressedSmallest(Path.Combine(target, "d2", "requests", hash + ".request.br"), bytes);
            rules[hash] = Convert.ToHexString(SHA256.HashData(RulesCodec.Encode(search.Rules))).ToLowerInvariant();
            Console.WriteLine($"CONFIG_SEARCH root={root} battles={search.Battles.Length} request={hash}");
        }
        MergeRulesIndex(target, rules);
        return roots.Length;
    }

    /// <summary>
    /// Adds requests written by <c>Differential --capture-d2</c>. The capture directory must contain
    /// one <c>capture.json</c> per root (with the number of coordinator member calls) and the
    /// Brotli-compressed SREQ files beside it. Requests are re-encoded canonically and deduplicated
    /// by the SHA-256 of the SREQ bytes; the rules index gains one entry per new request.
    /// </summary>
    private static int MergeCaptures(string target, IReadOnlyList<string> directories)
    {
        int calls = 0;
        int rootCaptures = 0;
        Dictionary<string, string> rulesByRequest = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
                throw new InvalidDataException($"--add-requests 目录不存在：{directory}");
            string[] reports = Directory.EnumerateFiles(directory, "capture.json",
                SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
            if (reports.Length == 0)
                throw new InvalidDataException($"--add-requests 目录没有 capture.json：{directory}");
            foreach (string report in reports)
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
                JsonElement root = document.RootElement;
                int reportCalls = root.GetProperty("calls").GetInt32();
                JsonElement members = root.GetProperty("members");
                if (reportCalls < 0 || members.GetArrayLength() != reportCalls)
                    throw new InvalidDataException($"capture.json 的 calls 与 members 不符：{report}");
                int reportUnique = members.EnumerateArray()
                    .Select(static member => member.GetProperty("requestSha256").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                if (reportUnique != root.GetProperty("uniqueRequests").GetInt32())
                    throw new InvalidDataException($"capture.json 的 uniqueRequests 不符：{report}");
                calls += reportCalls;
                rootCaptures++;
            }
            foreach (string path in Directory.EnumerateFiles(directory, "*.request.br",
                         SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.Ordinal))
            {
                SearchCase request = SearchCodec.DecodeRequest(
                    GoldenFiles.ReadCompressed(path));
                byte[] bytes = SearchCodec.EncodeRequest(request);
                string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!seen.Add(hash))
                    continue;
                string requestPath = Path.Combine(target, "d2", "requests", hash + ".request.br");
                if (!File.Exists(requestPath))
                    GoldenFiles.WriteCompressedSmallest(requestPath, bytes);
                string rulesHash = Convert.ToHexString(
                    SHA256.HashData(RulesCodec.Encode(request.Rules))).ToLowerInvariant();
                if (rulesByRequest.TryGetValue(hash, out string? existing)
                    && !string.Equals(existing, rulesHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"新增请求 {hash} 的规则哈希不一致。");
                rulesByRequest[hash] = rulesHash;
            }
        }
        if (rootCaptures == 0)
            throw new InvalidDataException("--add-requests 没有可合并的 capture.json。");
        MergeRulesIndex(target, rulesByRequest);
        return calls;
    }

    private static void MergeRulesIndex(string target, Dictionary<string, string> additions)
    {
        string d2 = Path.Combine(target, "d2");
        string path = Directory.EnumerateFiles(d2, "rules-index-*.json")
            .OrderBy(static item => item, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? Path.Combine(d2, "rules-index-0-of-1.json");
        SortedDictionary<string, string> index = new(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                index[property.Name] = property.Value.GetString()
                    ?? throw new InvalidDataException($"{Path.GetFileName(path)} 的规则引用不是字符串。");
        }
        foreach ((string requestHash, string rulesHash) in additions)
        {
            if (index.TryGetValue(requestHash, out string? existing)
                && !string.Equals(existing, rulesHash, StringComparison.Ordinal))
                throw new InvalidDataException($"请求 {requestHash} 的规则索引不一致。");
            index[requestHash] = rulesHash;
        }
        if (!string.Equals(path, Path.Combine(d2, "rules-index-0-of-1.json"),
                StringComparison.Ordinal))
            path = Path.Combine(d2, "rules-index-0-of-1.json");
        File.WriteAllText(path, JsonSerializer.Serialize(index,
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static int ReadCapturedCalls(string source)
    {
        string manifestPath = Path.Combine(source, "manifest.json");
        if (!File.Exists(manifestPath))
            return 0;
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return manifest.RootElement.GetProperty("counts").GetProperty("d2")
            .GetProperty("capturedCalls").GetInt32();
    }

    private static void WriteReport(string path, int captured, int unique)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", Codec.FormatVersion);
            writer.WriteNumber("requestsCaptured", captured);
            writer.WriteNumber("requestsUnique", unique);
            writer.WriteNumber("comparable", unique);
            writer.WriteNumber("equal", unique);
            writer.WriteNumber("mismatch", 0);
            writer.WriteNumber("timeAffected", 0);
            writer.WriteEndObject();
        }
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static string ResultSetHash(string[] requests, string expectedDirectory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string request in requests)
        {
            string requestHash = Path.GetFileName(request)[..^".request.br".Length];
            hash.AppendData(Encoding.ASCII.GetBytes(requestHash));
            byte[] expected = File.ReadAllBytes(
                Path.Combine(expectedDirectory, requestHash + ".result.br"));
            hash.AppendData(SHA256.HashData(expected));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

}
