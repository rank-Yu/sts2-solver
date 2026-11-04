using System.Buffers.Binary;
using System.Text;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// K0 contract for the on-disk format tags. Every codec magic must decode, in little-endian byte
/// order, to exactly the ASCII label below; the version is shared by all codecs.
/// </summary>
internal static class FormatContract
{
    internal static string Run()
    {
        (string Label, uint Magic)[] tags =
        [
            ("BATL", BattleCodec.Magic),
            ("RULE", RulesCodec.Magic),
            ("GOLD", GoldenData.Magic),
            ("SREQ", SearchCodec.RequestMagic),
            ("SRES", SearchCodec.ResultMagic),
        ];
        Span<byte> bytes = stackalloc byte[4];
        string[] labels = new string[tags.Length];
        for (int index = 0; index < tags.Length; index++)
        {
            (string expected, uint magic) = tags[index];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
            string actual = Encoding.ASCII.GetString(bytes);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"格式魔数 {magic:X8} 按小端字节是 {actual}，不是 {expected}。");
            labels[index] = actual;
        }
        if (Codec.FormatVersion != 5)
            throw new InvalidOperationException($"数据格式版本应为 5，实际为 {Codec.FormatVersion}。");
        return string.Join(',', labels);
    }
}
