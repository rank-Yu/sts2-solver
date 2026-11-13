using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sts2Solver.Simulation.Combat;

/// <summary>Combat RNG streams, in the order of the existing engine's prediction RNG set.</summary>
public enum RngStream : byte
{
    Shuffle,
    CombatCardGeneration,
    CombatPotionGeneration,
    CombatCardSelection,
    CombatEnergyCosts,
    CombatTargets,
    CombatOrbGeneration,
    MonsterAi,
    Niche,
    NonCombatGeneration,
}

/// <summary>
/// Bit-exact copy of the native <c>Rng</c> wrapper over <c>MegaRandom</c> (xoshiro256**).
/// Every public draw increments <see cref="Counter"/> exactly once, as the native wrapper does.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RngState
{
    private const double DoubleIncrement = 1.1102230246251565E-16;

    public ulong S0;
    public ulong S1;
    public ulong S2;
    public ulong S3;
    public int Counter;

    // Explicit padding keeps the struct at 40 bytes with a defined value (always zero).
    private int _padding;

    public static RngState FromSerializable(int counter, ulong s0, ulong s1, ulong s2, ulong s3)
        => new() { Counter = counter, S0 = s0, S1 = s1, S2 = s2, S3 = s3 };

    /// <summary>0.113 ListExtensions.UnstableShuffle consumes one seed from its parent stream.</summary>
    public RngState CreateShuffleRng()
    {
        Counter++;
        return FromSeed(NextULongInner());
    }

    public static RngState FromSeed(ulong seed)
        => new() { S0 = SplitMix(ref seed), S1 = SplitMix(ref seed),
            S2 = SplitMix(ref seed), S3 = SplitMix(ref seed) };

    // SplitMix64 seed expansion (public-domain algorithm), matching native MegaRandom.
    private static ulong SplitMix(ref ulong seed)
    {
        unchecked
        {
            ulong value = seed += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    /// <summary>Native <c>Rng.NextInt(int maxExclusive)</c>.</summary>
    public int NextInt(int maxExclusive)
    {
        Counter++;
        if (maxExclusive < 1)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxValue must be > 0");
        return (int)(NextDoubleInner() * maxExclusive);
    }

    /// <summary>Native <c>Rng.NextInt(int minInclusive, int maxExclusive)</c>.</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException(nameof(minInclusive), "Minimum must be lower than maximum.");
        Counter++;
        long range = (long)maxExclusive - minInclusive;
        if (range <= int.MaxValue)
            return (int)(NextDoubleInner() * (int)range) + minInclusive;
        return (int)((long)(NextDoubleInner() * range) + minInclusive);
    }

    /// <summary>Native <c>Rng.NextBool()</c>: a two-way <c>Next(2) == 0</c>.</summary>
    public bool NextBool()
    {
        Counter++;
        return (int)(NextDoubleInner() * 2) == 0;
    }

    /// <summary>Native <c>Rng.NextDouble()</c>.</summary>
    public double NextDouble()
    {
        Counter++;
        return NextDoubleInner();
    }

    /// <summary>Native <c>Rng.NextFloat(float max)</c>.</summary>
    public float NextFloat(float max) => NextFloat(0f, max);

    /// <summary>Native <c>Rng.NextFloat(float min, float max)</c>.</summary>
    public float NextFloat(float min, float max)
    {
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min), "Minimum must not be higher than maximum.");
        Counter++;
        return (float)(NextDoubleInner() * (double)(max - min) + (double)min);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double NextDoubleInner() => (NextULongInner() >> 11) * DoubleIncrement;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextULongInner()
    {
        ulong s0 = S0;
        ulong s1 = S1;
        ulong s2 = S2;
        ulong s3 = S3;
        ulong result = BitOperations.RotateLeft(s1 * 5, 7) * 9;
        ulong t = s1 << 17;
        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = BitOperations.RotateLeft(s3, 45);
        S0 = s0;
        S1 = s1;
        S2 = s2;
        S3 = s3;
        return result;
    }
}

[InlineArray(Limits.RngStreams)]
public struct RngStreams
{
    private RngState _first;
}
