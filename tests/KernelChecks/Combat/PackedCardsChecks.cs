using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>Packed card-storage contracts: prefix identity, arena reservation/compaction and &gt;96 cards.</summary>
internal static class PackedCardsChecks
{
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("packed-cards: " + message); }

    internal static void Check(Rules rules)
    {
        Battle state = default;
        state.Round = 7;
        state.HandCount = 3;
        state.Cards[0] = new Card { Id = 0, Private = 1 };
        state.Cards[1] = new Card { Id = 0, Private = 2 };
        state.Cards[2] = new Card { Id = 0, Private = 3 };
        state.Flags = BattleFlags.HistoryCourseCardPresent;
        state.HistoryCourseCard = new Card { Id = 0, Private = 99 };

        // A packed copy carries the defined prefix; the suffix is undefined and ignored.
        Battle copy = default;
        Battle.CopyPacked(in state, ref copy);
        int packed = Battle.PackedSize(in state);
        Span<byte> suffix = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref copy, 1))[packed..];
        suffix.Fill(0xA5);
        Assert(Fingerprint.Hash(in copy) == Fingerprint.Hash(in state), "hash ignores suffix");
        Assert(Fingerprint.Equal(in copy, in state), "equality ignores suffix");
        Assert(BattleCodec.Encode(copy).AsSpan().SequenceEqual(BattleCodec.Encode(state)),
            "encoding ignores suffix");
        Assert(Battle.PackedSize(in copy) == packed, "packed size follows CardCount");

        Battle decoded = BattleCodec.Decode(BattleCodec.Encode(state));
        Assert(Fingerprint.Equal(in decoded, in state), "codec round trip");
        Assert(Battle.PackedSize(in decoded) == packed, "codec round trip size");

        // Arena: full 200-card records cross the 512 KiB block boundary, compact in place, and
        // reclaim the released blocks.
        SearchStorage storage = new();
        storage.BeginSolve(0);
        PackedStateArena arena = new(64, storage);
        Battle[] originals = new Battle[40];
        for (int index = 0; index < originals.Length; index++)
        {
            Battle child = default;
            child.Round = index + 1;
            for (int card = 0; card < 200; card++)
                Piles.Append(ref child, Pile.Draw, new Card { Id = 0, Private = checked((short)card) });
            originals[index] = child;
            arena.Reserve();
            Battle.CopyPacked(in child, ref arena.ReservedState);
            arena.CommitReserved();
        }
        Assert(arena.Count == originals.Length, "arena count");
        Assert(arena.UsedBytes > PackedStateArena.PayloadBytes, "records cross a block boundary");
        for (int index = 0; index < originals.Length; index++)
            Assert(Fingerprint.Equal(in arena[index], in originals[index]), $"arena state {index}");

        long beforeCompact = arena.UsedBytes;
        int write = 0;
        for (int read = 0; read < arena.Count; read += 2)
            arena.Move(read, write++);
        arena.Truncate(write);
        Assert(arena.UsedBytes <= beforeCompact, "compaction reclaims bytes");
        for (int index = 0; index < write; index++)
            Assert(Fingerprint.Equal(in arena[index], in originals[index * 2]),
                $"compacted state {index}");
        Assert(Fingerprint.Hash(in arena[0]) == Fingerprint.Hash(in originals[0]),
            "compaction preserves the packed hash");

        arena.Clear();
        Assert(arena.Count == 0 && arena.UsedBytes == 0, "clear keeps reusable blocks");

        CheckCompactionInvariant();
        CheckUnorderedCompaction();

        // A 200-card state is a normal Battle: it can be stepped and inserted (deduplicated by the
        // pool's hash/equality contract).
        Battle large = default;
        large.Player = new Creature { CurrentHp = 50, MaxHp = 50, Flags = CreatureFlags.Present };
        large.MaxHandSize = 10;
        large.Energy = 3;
        for (int card = 0; card < 200; card++)
            Piles.Append(ref large, Pile.Draw, new Card { Id = 0, Private = checked((short)card) });
        Assert(large.CardCount == 200, "200-card state");
        StepResult result = Kernel.Step(ref large, PlayerAction.EndTurn(), rules,
            out ChoiceRequest request, out bool historyRisk);
        Assert(result is StepResult.Completed or StepResult.NeedsChoice,
            $"200-card step result {result}");
        _ = request;
        _ = historyRisk;
        Assert(large.CardCount <= Limits.Cards, "step stays within capacity");

        arena.Reserve();
        Battle.CopyPacked(in large, ref arena.ReservedState);
        arena.CommitReserved();
        arena.Reserve();
        Battle.CopyPacked(in large, ref arena.ReservedState);
        ulong hash = Fingerprint.Hash(in arena[arena.Count - 1]);
        Assert(hash == Fingerprint.Hash(in large), "dedupe hash for a 200-card state");
        Assert(Fingerprint.Equal(in arena[arena.Count - 1], in large), "dedupe equality for a 200-card state");
        Console.WriteLine($"PACKED_CARDS states=200 arenaBytes={PackedStateArena.BlockBytes} pass");
    }

    private static void CheckCompactionInvariant()
    {
        SearchStorage storage = new();
        storage.BeginSolve(0);
        PackedStateArena arena = new(64, storage);
        int[] counts = [255, 240, 220, 200, 180, 160, 140, 120, 100, 80, 60, 40, 20, 5, 255, 200, 150, 100, 50, 10];
        Battle[] originals = new Battle[counts.Length];
        for (int index = 0; index < counts.Length; index++)
        {
            Battle state = BuildWithCards(counts[index], index + 1);
            originals[index] = state;
            arena.Reserve();
            Battle.CopyPacked(in state, ref arena.ReservedState);
            arena.CommitReserved();
        }
        long before = arena.UsedBytes;
        arena.Compact();
        Assert(arena.Count == counts.Length, "compaction keeps count");
        Assert(arena.UsedBytes <= before, "compaction does not grow the tail");
        for (int index = 0; index < counts.Length; index++)
        {
            long offset = arena.OffsetOf(index);
            int inBlock = PackedStateArena.InBlockOffset(offset);
            Assert(inBlock + Battle.ExpectedSize <= PackedStateArena.BlockBytes,
                $"compacted record {index} leaves a full Battle tail");
            Assert(inBlock <= PackedStateArena.PayloadBytes,
                $"compacted record {index} starts in the payload");
            Battle compacted = arena[index];
            Assert(Fingerprint.Bytes(in compacted).SequenceEqual(Fingerprint.Bytes(in originals[index])),
                $"compacted record {index} bytes unchanged");
        }
    }

    private static void CheckUnorderedCompaction()
    {
        SearchStorage storage = new();
        storage.BeginSolve(0);
        PackedStateArena arena = new(8, storage);
        foreach (int cards in new[] { 5, 20, 200 })
        {
            Battle state = BuildWithCards(cards, cards);
            arena.Reserve();
            Battle.CopyPacked(in state, ref arena.ReservedState);
            arena.CommitReserved();
        }
        arena.Move(2, 0);
        bool threw = false;
        try
        {
            arena.Compact();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Assert(threw, "unordered compaction is rejected");
    }

    private static Battle BuildWithCards(int count, int round)
    {
        Battle state = default;
        state.Round = round;
        for (int card = 0; card < count; card++)
            Piles.Append(ref state, Pile.Draw, new Card { Id = 0, Private = checked((short)card) });
        return state;
    }
}
