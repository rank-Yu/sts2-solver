using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Byte-image identity of Sts2Solver values. The packed prefix covers the header and only the cards
/// at <c>[0, CardCount)</c>; the bytes after the last live card are undefined and never inspected.
/// Battle and card hashes follow their current runtime layouts.
/// </summary>
public static class Fingerprint
{
    private const ulong Prime1 = 0x9E3779B185EBCA87UL;
    private const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong Prime3 = 0x165667B19E3779F9UL;

    private static readonly int EnemiesOffset = Offset(static (ref Battle b) => ref Unsafe.As<Enemy, byte>(ref b.Enemies[0]));
    private static readonly int PowersOffset = Offset(static (ref Battle b) => ref Unsafe.As<Power, byte>(ref b.Powers[0]));
    private static readonly int RelicsOffset = Offset(static (ref Battle b) => ref Unsafe.As<RelicState, byte>(ref b.RelicStates[0]));
    private static readonly int OrbsOffset = Offset(static (ref Battle b) => ref Unsafe.As<Orb, byte>(ref b.Orbs[0]));
    private static readonly int CardsOffset = Offset(static (ref Battle b) => ref Unsafe.As<Card, byte>(ref b.Cards[0]));
    private static readonly int HistoryCourseOffset =
        Offset(static (ref Battle b) => ref Unsafe.As<Card, byte>(ref b.HistoryCourseCard));
    private static readonly int DecimalVarsOffset = Offset(static (ref Battle b) => ref Unsafe.As<decimal, byte>(ref b.DecimalVars[0]));
    private static readonly int DecimalVarsEndOffset = DecimalVarsOffset + Limits.DecimalCardVars * Unsafe.SizeOf<decimal>();

    private delegate ref byte FieldSelector(ref Battle battle);

    /// <summary>The defined prefix: the header followed by the cards in use.</summary>
    public static ReadOnlySpan<byte> Bytes(in Battle battle)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in battle, 1))[..Battle.PackedSize(in battle)];

    public static ulong Hash(in Battle battle)
    {
        ReadOnlySpan<byte> all = Bytes(battle);
        ulong hash = Prime3;
        // Roots without decimal card content keep the table zero. Hash the header in two pieces so
        // those roots do not pay for 4 * sizeof(decimal) bytes on every state hash; equality still
        // compares the complete packed prefix.
        hash = Mix(hash, all[..DecimalVarsOffset]);
        int decimalBytes = battle.DecimalVarCount * Unsafe.SizeOf<decimal>();
        if (decimalBytes > 0)
            hash = Mix(hash, all.Slice(DecimalVarsOffset, decimalBytes));
        hash = Mix(hash, all[DecimalVarsEndOffset..EnemiesOffset]);
        hash = Mix(hash, all.Slice(EnemiesOffset, battle.EnemyCount * Unsafe.SizeOf<Enemy>()));
        hash = Mix(hash, all.Slice(PowersOffset, battle.PowerCount * Unsafe.SizeOf<Power>()));
        hash = Mix(hash, all.Slice(RelicsOffset, battle.RelicStateCount * Unsafe.SizeOf<RelicState>()));
        hash = Mix(hash, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in battle.RelicStateFlags, 1))[..battle.RelicStateCount]);
        hash = Mix(hash, all.Slice(OrbsOffset, battle.OrbCount * Unsafe.SizeOf<Orb>()));
        hash = Mix(hash, all.Slice(CardsOffset, battle.CardCount * Unsafe.SizeOf<Card>()));
        // HistoryCourseCard now lives at the end of the header, before the cards. It was hashed
        // after the card segment before the layout change, so mix it last (and skip it in the header
        // gap above) to retain the segment order.
        if ((battle.Flags & BattleFlags.HistoryCourseCardPresent) != 0)
            hash = Mix(hash, all.Slice(HistoryCourseOffset, Unsafe.SizeOf<Card>()));
        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        return hash;
    }

    public static bool Equal(in Battle left, in Battle right) => Bytes(left).SequenceEqual(Bytes(right));

    public static bool Equal(in Card left, in Card right)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in left, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in right, 1)));

    /// <summary>
    /// Stable card-state hash used by retention-only projection keys (the projected shuffle order and
    /// the unordered pile multiset). It hashes the complete card byte image, so byte-identical cards
    /// hash equally without reading model references.
    /// </summary>
    public static ulong HashCard(in Card card)
        => Mix(Prime3, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in card, 1)));

    /// <summary>Four independent lanes over 32-byte blocks, then single words, then the byte tail.</summary>
    private static ulong Mix(ulong seed, ReadOnlySpan<byte> data)
    {
        ulong v0 = seed + Prime1 + Prime2, v1 = seed + Prime2, v2 = seed, v3 = seed - Prime1;
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(data);
        int index = 0;
        for (; index + 4 <= words.Length; index += 4)
        {
            v0 = Round(v0, words[index]);
            v1 = Round(v1, words[index + 1]);
            v2 = Round(v2, words[index + 2]);
            v3 = Round(v3, words[index + 3]);
        }
        ulong hash = BitOperations.RotateLeft(v0, 1) + BitOperations.RotateLeft(v1, 7)
            + BitOperations.RotateLeft(v2, 12) + BitOperations.RotateLeft(v3, 18);
        for (; index < words.Length; index++)
            hash = BitOperations.RotateLeft(hash ^ Round(0, words[index]), 27) * Prime1 + Prime3;
        int tail = data.Length & 7;
        if (tail != 0)
        {
            ulong last = 0;
            data[^tail..].CopyTo(MemoryMarshal.AsBytes(new Span<ulong>(ref last)));
            hash = BitOperations.RotateLeft(hash ^ Round(0, last), 27) * Prime1 + Prime3;
        }
        return hash ^ (ulong)data.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong accumulator, ulong word)
        => BitOperations.RotateLeft(accumulator + word * Prime2, 31) * Prime1;

    private static int Offset(FieldSelector selector)
    {
        Battle probe = default;
        return (int)Unsafe.ByteOffset(ref Unsafe.As<Battle, byte>(ref probe), ref selector(ref probe));
    }
}
