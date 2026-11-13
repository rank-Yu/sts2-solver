using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal sealed record ManifestSummary(
    int DataFiles,
    long TotalBytes,
    int D1Files,
    int D1Roots,
    int D1K1,
    int D1Walks,
    int D1WalkStates,
    int D1Transitions,
    int SearchRequestEntrys,
    int D2Expected);

internal static class ManifestVerifier
{
    internal static ManifestSummary Verify(string dataDirectory)
    {
        string manifestPath = Path.Combine(dataDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"缺少 manifest.json：{manifestPath}");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != Codec.FormatVersion)
            throw new InvalidDataException("manifest.json 的格式版本不符。");
        if (root.GetProperty("battleSize").GetInt32() != BattleCodec.SerializedSize)
            throw new InvalidDataException("manifest.json 的 Battle 大小不符。");
        if (!string.Equals(root.GetProperty("compression").GetString(), "brotli", StringComparison.Ordinal))
            throw new InvalidDataException("manifest.json 的压缩格式不是 brotli。");
        if (root.TryGetProperty("sourceCommit", out _))
            throw new InvalidDataException("manifest.json 仍含旧的 sourceCommit 字段。");
        JsonElement provenance = root.GetProperty("provenance");
        if (provenance.GetProperty("d1").GetString() != ManifestProvenance.D1
            || provenance.GetProperty("d2").GetString() != ManifestProvenance.D2
            || provenance.GetProperty("d1Config").GetString() != ManifestProvenance.D1Config
            || provenance.GetProperty("formatMigration").GetString() != ManifestProvenance.FormatMigration)
            throw new InvalidDataException("manifest.json 的 provenance 不符。");

        string normalizedData = Path.GetFullPath(dataDirectory) + Path.DirectorySeparatorChar;
        long declaredBytes = 0;
        int manifestFiles = 0;
        string? previousPath = null;
        HashSet<string> listedPaths = new(StringComparer.Ordinal);
        foreach (JsonElement entry in root.GetProperty("files").EnumerateArray())
        {
            string relative = entry.GetProperty("path").GetString()
                ?? throw new InvalidDataException("manifest.json 文件条目缺少 path。");
            if (previousPath != null && string.CompareOrdinal(previousPath, relative) >= 0)
                throw new InvalidDataException($"manifest.json 文件顺序不唯一或不递增：{relative}");
            previousPath = relative;
            if (!listedPaths.Add(relative))
                throw new InvalidDataException($"manifest.json 文件重复：{relative}");
            string path = Path.GetFullPath(Path.Combine(dataDirectory, relative));
            if (!path.StartsWith(normalizedData, StringComparison.Ordinal))
                throw new InvalidDataException($"manifest.json 文件路径越界：{relative}");
            if (!File.Exists(path))
                throw new InvalidDataException($"manifest.json 列出的文件不存在：{relative}");
            byte[] data = File.ReadAllBytes(path);
            long size = entry.GetProperty("bytes").GetInt64();
            string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (data.Length != size || hash != entry.GetProperty("sha256").GetString())
                throw new InvalidDataException($"manifest.json 文件大小或 SHA-256 不符：{relative}");
            declaredBytes += size;
            manifestFiles++;
        }
        if (declaredBytes != root.GetProperty("totalBytes").GetInt64())
            throw new InvalidDataException("manifest.json 的 totalBytes 与文件合计不符。");

        int d1Files = 0, d1Roots = 0, d1K1 = 0, d1Walks = 0, d1States = 0, d1Transitions = 0;
        string d1Directory = Path.Combine(dataDirectory, "d1");
        if (!Directory.Exists(d1Directory))
            throw new InvalidDataException($"模拟回归目录不存在：{d1Directory}");
        foreach (string directory in Directory.EnumerateDirectories(d1Directory))
        {
            string metaPath = Path.Combine(directory, "meta.json");
            if (!File.Exists(metaPath))
                throw new InvalidDataException($"D1 根 {Path.GetFileName(directory)} 缺少 meta.json。");
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
            JsonElement metaRoot = meta.RootElement;
            if (metaRoot.GetProperty("formatVersion").GetInt32() != Codec.FormatVersion
                || metaRoot.GetProperty("battleSize").GetInt32() != BattleCodec.SerializedSize)
                throw new InvalidDataException($"D1 根 {Path.GetFileName(directory)} 的格式版本或战斗大小不符。");
            JsonElement counts = metaRoot.GetProperty("counts");
            d1Files++;
            d1Roots += counts.GetProperty("roots").GetInt32();
            d1K1 += counts.GetProperty("k1").GetInt32();
            d1Walks += counts.GetProperty("walks").GetInt32();
            d1States += counts.GetProperty("walkStates").GetInt32();
            d1Transitions += counts.GetProperty("transitions").GetInt32();
            string prefix = $"d1/{Path.GetFileName(directory)}/";
            foreach (string required in new[] { "config.br", "golden.br", "meta.json" })
            {
                if (!listedPaths.Contains(prefix + required))
                    throw new InvalidDataException($"manifest.json 缺少 {prefix + required}。");
            }
        }
        int configEntries = listedPaths.Count(static path =>
            path.StartsWith("d1/", StringComparison.Ordinal)
            && path.EndsWith("/config.br", StringComparison.Ordinal));
        if (configEntries != d1Files)
            throw new InvalidDataException("manifest.json 的 D1 config.br 条目数与根数不符。");
        JsonElement d1Counts = root.GetProperty("counts").GetProperty("d1");
        if (d1Counts.GetProperty("files").GetInt32() != d1Files
            || d1Counts.GetProperty("roots").GetInt32() != d1Roots
            || d1Counts.GetProperty("k1").GetInt32() != d1K1
            || d1Counts.GetProperty("walks").GetInt32() != d1Walks
            || d1Counts.GetProperty("walkStates").GetInt32() != d1States
            || d1Counts.GetProperty("transitions").GetInt32() != d1Transitions)
            throw new InvalidDataException("manifest.json 的 D1 计数与 meta 合计不符。");

        string d2Directory = Path.Combine(dataDirectory, "d2");
        string requestDirectory = Path.Combine(d2Directory, "requests");
        string expectedDirectory = Path.Combine(d2Directory, "expected");
        if (!Directory.Exists(requestDirectory) || !Directory.Exists(expectedDirectory))
            throw new InvalidDataException("D2 requests/expected 目录不完整。");
        int d2Requests = Directory.EnumerateFiles(requestDirectory, "*.request.br").Count();
        int d2Expected = Directory.EnumerateFiles(expectedDirectory, "*.result.br").Count();
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
        if (unique != d2Requests || d2Requests != d2Expected)
            throw new InvalidDataException("D2 请求/期望文件数与 report 的 unique 计数不符。");
        HashSet<string> requestEntries = listedPaths
            .Where(static path => path.StartsWith("d2/requests/", StringComparison.Ordinal)
                && path.EndsWith(".request.br", StringComparison.Ordinal))
            .Select(static path => path[(path.LastIndexOf('/') + 1)..^".request.br".Length])
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> expectedEntries = listedPaths
            .Where(static path => path.StartsWith("d2/expected/", StringComparison.Ordinal)
                && path.EndsWith(".result.br", StringComparison.Ordinal))
            .Select(static path => path[(path.LastIndexOf('/') + 1)..^".result.br".Length])
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> requestFiles = Directory.EnumerateFiles(requestDirectory, "*.request.br")
            .Select(static path => Path.GetFileName(path)[..^".request.br".Length])
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> expectedFiles = Directory.EnumerateFiles(expectedDirectory, "*.result.br")
            .Select(static path => Path.GetFileName(path)[..^".result.br".Length])
            .ToHashSet(StringComparer.Ordinal);
        if (!requestEntries.SetEquals(requestFiles) || !expectedEntries.SetEquals(expectedFiles))
            throw new InvalidDataException("manifest.json 的 D2 请求/期望条目与目录不符。");
        JsonElement d2Counts = root.GetProperty("counts").GetProperty("d2");
        if (d2Counts.GetProperty("capturedCalls").GetInt32() != captured
            || d2Counts.GetProperty("uniqueRequests").GetInt32() != unique
            || d2Counts.GetProperty("expectedResults").GetInt32() != d2Expected
            || d2Counts.GetProperty("comparable").GetInt32() != comparable
            || d2Counts.GetProperty("equal").GetInt32() != equal
            || d2Counts.GetProperty("mismatch").GetInt32() != mismatch
            || d2Counts.GetProperty("timeAffected").GetInt32() != timeAffected)
            throw new InvalidDataException("manifest.json 的 D2 计数与 replay-report 合计不符。");

        return new ManifestSummary(manifestFiles, declaredBytes, d1Files, d1Roots, d1K1,
            d1Walks, d1States, d1Transitions, d2Requests, d2Expected);
    }
}
