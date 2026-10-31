using Sts2Solver.Simulation.Combat;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sts2Solver.Ai.Combat;

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
            Console.Error.WriteLine("用法：Golden refreeze <source-dir> <target-dir> [--jobs N]");
            return 2;
        }
        string source = Path.GetFullPath(args[0]);
        string target = Path.GetFullPath(args[1]);
        int jobs = Environment.ProcessorCount;
        for (int index = 2; index < args.Length; index++)
        {
            if (args[index] == "--jobs")
            {
                if (++index >= args.Length || !int.TryParse(args[index], out jobs) || jobs <= 0)
                    throw new InvalidDataException("--jobs 需要正整数。");
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
            BeamOptions options = search.Options with
            {
                SoftTimeBudget = TimeSpan.MaxValue,
                Pacer = null,
                Trace = null,
            };
            BeamResult result = new BeamSolver(search.Rules, options)
                .Solve(search.Battles, CancellationToken.None);
            byte[] encoded = SearchCodec.EncodeResult(result);
            GoldenFiles.WriteCompressed(Path.Combine(expectedDirectory, hash + ".result.br"), encoded);
            int done = Interlocked.Increment(ref completed);
            if (done % 200 == 0)
                Console.WriteLine($"REFREEZE progress={done}/{requests.Length}");
        });

        foreach (string report in Directory.EnumerateFiles(Path.Combine(target, "d2"),
                     "replay-report-*.json"))
            File.Delete(report);
        WriteReport(Path.Combine(target, "d2", "replay-report-1-of-1.json"), captured, requests.Length);
        string content = ResultSetHash(requests, expectedDirectory);
        ManifestProvenance.Refreeze = $"M3b current-code results sha256={content}";
        ManifestBuilder.Write(target);
        Console.WriteLine($"REFREEZE_OK source={source} target={target} requests={requests.Length} "
            + $"captured={captured} jobs={jobs} refreeze={ManifestProvenance.Refreeze}");
        return 0;
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
