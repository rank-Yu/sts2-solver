using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Deterministic little-endian primitives shared by the Sts2Solver golden codecs. The writer never emits
/// timestamps, unordered dictionary enumeration or runtime-dependent values; the reader rejects
/// truncated input and unknown versions explicitly.
/// </summary>
internal static class Codec
{
    internal const ushort FormatVersion = 5;

    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }
        byte[] bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static string? ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0)
            return null;
        if (length > 1 << 20)
            throw new InvalidDataException($"字符串长度 {length} 超出上限。");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidDataException("字符串数据被截断。");
        return Utf8.GetString(bytes);
    }

    internal static void WriteDecimal(BinaryWriter writer, decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        for (int index = 0; index < bits.Length; index++)
            writer.Write(bits[index]);
    }

    internal static decimal ReadDecimal(BinaryReader reader)
    {
        int a = reader.ReadInt32();
        int b = reader.ReadInt32();
        int c = reader.ReadInt32();
        int flags = reader.ReadInt32();
        return new decimal([a, b, c, flags]);
    }

    internal static void WriteDecimals(BinaryWriter writer, decimal[] values)
    {
        writer.Write(values.Length);
        for (int index = 0; index < values.Length; index++)
            WriteDecimal(writer, values[index]);
    }

    internal static decimal[] ReadDecimals(BinaryReader reader)
    {
        int count = ReadCount(reader, 1 << 16, "decimal 数组");
        decimal[] values = new decimal[count];
        for (int index = 0; index < count; index++)
            values[index] = ReadDecimal(reader);
        return values;
    }

    internal static void WriteIntArray(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        for (int index = 0; index < values.Length; index++)
            writer.Write(values[index]);
    }

    internal static int[] ReadIntArray(BinaryReader reader, int max, string what)
    {
        int count = ReadCount(reader, max, what);
        int[] values = new int[count];
        for (int index = 0; index < count; index++)
            values[index] = reader.ReadInt32();
        return values;
    }

    internal static void WriteUShortArray(BinaryWriter writer, ushort[] values)
    {
        writer.Write(values.Length);
        for (int index = 0; index < values.Length; index++)
            writer.Write(values[index]);
    }

    internal static ushort[] ReadUShortArray(BinaryReader reader, int max, string what)
    {
        int count = ReadCount(reader, max, what);
        ushort[] values = new ushort[count];
        for (int index = 0; index < count; index++)
            values[index] = reader.ReadUInt16();
        return values;
    }

    internal static int ReadCount(BinaryReader reader, int max, string what)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > max)
            throw new InvalidDataException($"{what}元素个数 {count} 不在 0..{max} 内。");
        return count;
    }

    internal static bool ReadBool(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        if (value > 1)
            throw new InvalidDataException($"布尔值 {value} 非法。");
        return value != 0;
    }

    /// <summary>Rejects bytes after the declared payload; callers use it to catch trailing corruption.</summary>
    internal static void ExpectEnd(BinaryReader reader, Stream stream, string what)
    {
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"{what} 尾部有 {stream.Length - stream.Position} 个未消费字节。");
    }

    internal static byte[] ToBytes(Action<BinaryWriter> write)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Utf8, leaveOpen: true))
            write(writer);
        return stream.ToArray();
    }

    internal static T Read<T>(byte[] bytes, string what, Func<BinaryReader, T> read)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream, Utf8, leaveOpen: true);
        T value = read(reader);
        ExpectEnd(reader, stream, what);
        return value;
    }

    internal static byte[] Sha256(ReadOnlySpan<byte> bytes) => SHA256.HashData(bytes);

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(Sha256(bytes)).ToLowerInvariant();

    /// <summary>The truncated SHA-256 of the packed <see cref="Battle"/> prefix (64 bits).</summary>
    internal static ulong BattleHash(in Battle battle)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Fingerprint.Bytes(battle), hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    internal static ulong Sha256U64(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }
}

/// <summary>
/// Packed <see cref="Battle"/> file: version, the packed prefix length and the packed byte image.
/// Only the header and the cards at <c>[0, CardCount)</c> are stored; the struct suffix is undefined.
/// Writers and readers use the current version only.
/// </summary>
public static class BattleCodec
{
    internal const uint Magic = 0x4C544142; // "BATL"

    /// <summary>Encoded header bytes: magic, version, size.</summary>
    public const int HeaderSize = 4 + 2 + 4;

    public static byte[] Encode(in Battle battle)
    {
        int size = Unsafe.SizeOf<Battle>();
        if (size != Battle.ExpectedSize)
            throw new InvalidOperationException(
                $"Battle 大小 {size} 与布局断言 {Battle.ExpectedSize} 不符。");
        ReadOnlySpan<byte> packed = Fingerprint.Bytes(battle);
        byte[] bytes = new byte[HeaderSize + packed.Length];
        Span<byte> header = bytes.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Codec.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], packed.Length);
        packed.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    public static Battle Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
            throw new InvalidDataException($"Battle 数据只有 {bytes.Length} 字节，短于头部 {HeaderSize}。");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != Magic)
            throw new InvalidDataException($"Battle 魔数 {magic:X8} 不符。");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (version != Codec.FormatVersion)
            throw new InvalidDataException($"Battle 格式版本 {version} 不受支持（当前 {Codec.FormatVersion}）。");
        int declared = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        int current = Unsafe.SizeOf<Battle>();
        if (current != Battle.ExpectedSize)
            throw new InvalidDataException(
                $"Battle 大小 {current} 与布局断言 {Battle.ExpectedSize} 不符。");
        if (bytes.Length != HeaderSize + declared)
            throw new InvalidDataException(
                $"Battle 数据长度 {bytes.Length} 与声明 {HeaderSize + declared} 不符。");
        if (bytes.Length <= HeaderSize)
            throw new InvalidDataException("Battle 载荷为空。");
        ReadOnlySpan<byte> payload = bytes[HeaderSize..];
        if (declared < Battle.HeaderSize || declared > current)
            throw new InvalidDataException(
                $"Battle 打包长度 {declared} 不在 {Battle.HeaderSize}..{current} 内。");
        Unsafe.SkipInit(out Battle battle);
        payload.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref battle, 1)));
        if (Battle.PackedSize(in battle) != declared)
            throw new InvalidDataException(
                $"Battle 打包长度 {declared} 与计数推出的 {Battle.PackedSize(in battle)} 不符。");
        Validate(in battle);
        return battle;
    }

    /// <summary>Rejects counts that cannot describe a canonical state; later reads would be out of range.</summary>
    public static void Validate(in Battle battle)
    {
        if (battle.HandCount + battle.DrawCount + battle.DiscardCount + battle.ExhaustCount + battle.PlayCount
            > Limits.Cards)
            throw new InvalidDataException("Battle 卡牌计数超出数组容量。");
        if (battle.EnemyCount > Limits.Enemies || battle.RosterCount > Limits.Enemies)
            throw new InvalidDataException("Battle 敌人计数超出数组容量。");
        if (battle.PowerCount > Limits.Powers || battle.RelicCount > Limits.Relics
            || battle.OrbCount > Limits.Orbs)
            throw new InvalidDataException("Battle Power/遗物/球计数超出数组容量。");
        if (battle.PotionSlotCount > Limits.PotionSlots)
            throw new InvalidDataException("Battle 药水槽计数超出容量。");
        if (battle.DecimalVarCount > Limits.DecimalCardVars)
            throw new InvalidDataException("Battle decimal 变量计数超出容量。");
        if (battle.RosterCount > battle.EnemyCount)
            throw new InvalidDataException("Battle roster 数量大于已知敌人数。");
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (battle.Enemies[index].Rules >= Limits.MonsterRules)
                throw new InvalidDataException($"Battle 敌人 {index} 的规则下标越界。");
        }
        for (int index = 0; index < battle.CardCount; index++)
        {
            if (battle.Cards[index].Id >= Registry.CardCount)
                throw new InvalidDataException($"Battle 卡牌 {index} 的内容下标越界。");
        }
    }
}
