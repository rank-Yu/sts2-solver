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
    internal const ushort FormatVersion = 9;

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
        => Sha256U64(Fingerprint.Bytes(battle));

    internal static void WriteCard(BinaryWriter writer, in Card card)
        => writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in card, 1)));

    internal static Card ReadCard(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<Card>()];
        reader.BaseStream.ReadExactly(bytes);
        Card card = MemoryMarshal.Read<Card>(bytes);
        BattleCodec.ValidateCard(card);
        return card;
    }

    internal static ulong Sha256U64(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }
}

/// <summary>Current Battle byte prefix followed by its immutable relic roster.</summary>
public static class BattleCodec
{
    internal const uint Magic = 0x4C544142; // BATL
    public const int HeaderSize = 4 + 2 + 4;
    public const int SerializedSize = Battle.ExpectedSize;
    public const int MaxSerializedSize = 4 + SerializedSize + 2 + Limits.RelicRoster * 12;

    public static byte[] Encode(in Battle battle, RelicRoster relics)
    {
        int packedSize = Battle.PackedSize(battle);
        byte[] bytes = new byte[HeaderSize + 4 + packedSize + 2 + relics.Count * 12];
        Span<byte> target = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(target, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(target[4..], Codec.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(target[6..], bytes.Length - HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(target[HeaderSize..], packedSize);
        Fingerprint.Bytes(battle).CopyTo(target[(HeaderSize + 4)..]);
        Span<byte> roster = target[(HeaderSize + 4 + packedSize)..];
        BinaryPrimitives.WriteUInt16LittleEndian(roster, checked((ushort)relics.Count));
        for (int i = 0; i < relics.Count; i++)
        {
            Relic row = relics.Fixed(i);
            MemoryMarshal.Write(roster.Slice(2 + i * 12, 12), in row);
        }
        return bytes;
    }

    public static Battle Decode(ReadOnlySpan<byte> bytes, out RelicRoster relics)
    {
        if (bytes.Length < HeaderSize + 4 + Battle.HeaderSize + 2
            || bytes.Length > HeaderSize + MaxSerializedSize)
            throw new InvalidDataException("Invalid Battle file length.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            throw new InvalidDataException("Invalid Battle magic.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Codec.FormatVersion)
            throw new InvalidDataException("Unsupported Battle format version.");
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]) != bytes.Length - HeaderSize)
            throw new InvalidDataException("Battle payload length mismatch.");
        int packedSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[HeaderSize..]);
        if (packedSize < Battle.HeaderSize || packedSize > SerializedSize
            || bytes.Length < HeaderSize + 4 + packedSize + 2)
            throw new InvalidDataException("Invalid packed Battle length.");
        Battle battle = default;
        bytes.Slice(HeaderSize + 4, packedSize)
            .CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref battle, 1)));
        Validate(battle);
        if (packedSize != Battle.PackedSize(battle))
            throw new InvalidDataException("Battle pile counts do not match packed length.");
        ReadOnlySpan<byte> tail = bytes[(HeaderSize + 4 + packedSize)..];
        int count = BinaryPrimitives.ReadUInt16LittleEndian(tail);
        if (tail.Length != 2 + count * 12)
            throw new InvalidDataException("Relic roster length mismatch.");
        ReadOnlySpan<Relic> rows = MemoryMarshal.Cast<byte, Relic>(tail[2..]);
        relics = RelicRoster.Create(rows);
        if (battle.RelicStateCount != relics.StateCount)
            throw new InvalidDataException("Battle relic states do not match the roster.");
        for (int i = 0; i < count; i++)
        {
            Relic fixedRow = relics.Fixed(i);
            if (fixedRow.Counter != rows[i].Counter || fixedRow.Aux != rows[i].Aux
                || fixedRow.Flags != rows[i].Flags)
                throw new InvalidDataException("Fixed relic roster contains mutable state.");
        }
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
        if (battle.PowerCount > Limits.Powers || battle.RelicStateCount > Limits.RelicStates
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
            ValidateCard(battle.Cards[index]);
        for (int index = 0; index < Limits.DetachedCards; index++)
            ValidateCard(battle.DetachedCards[index]);
        ValidateCard(battle.HistoryCourseCard);
    }
    internal static void ValidateCard(in Card card)
    {
        if (card.Id >= Registry.CardCount)
            throw new InvalidDataException("Unknown card id.");
        if (card.EnergyModifierCount > Limits.CardEnergyCostModifiers)
            throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
    }
}
