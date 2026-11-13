using System.Buffers.Binary;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

internal static class RelicStorageChecks
{
    internal static void Run()
    {
        Registry.TryRelic("HAPPY_FLOWER", out ushort flower);
        Registry.TryRelic("PHILOSOPHERS_STONE", out ushort stone);
        Registry.TryRelic("PUMPKIN_CANDLE", out ushort candle);
        Registry.TryRelic("PEN_NIB", out ushort nib);
        Relic[] rows = Enumerable.Range(0, 24).Select(i => new Relic
            { Id = flower, Counter = i, Aux = -i }).ToArray();
        Battle battle = default;
        Rules rules = new RulesDraft().Build();
        TestRelics.Set(ref battle, ref rules, rows);
        Require(battle.RelicStateCount == 24, "zero-valued stateful instances also occupy a slot");
        Battle copy = default;
        Battle.CopyPacked(battle, ref copy);
        copy.RelicState(rules, 0).Counter = 100;
        Require(battle.GetRelic(rules, 0).Counter == 0 && copy.GetRelic(rules, 0).Counter == 100,
            "copies share only immutable data");
        RelicRoster roster = rules.Relics;
        rows[0].Id = stone;
        Require(roster.Fixed(0).Id == flower, "input array cannot mutate roster");
        Battle before = battle;
        bool rejected = false;
        try { RelicRoster.Create(Enumerable.Repeat(new Relic { Id = flower }, 25).ToArray()); }
        catch (CapacityException e) { rejected = e.Region == "RelicStates"; }
        Require(rejected && Fingerprint.Equal(before, battle), "25th dynamic instance fails atomically");
        rejected = false;
        try { roster.Import(rows, ref battle); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && Fingerprint.Equal(before, battle), "mismatched immutable roster rejected before mutation");
        RoundTrip(battle, roster);
        TestRelics.Set(ref battle, ref rules, new Relic { Id = stone, Aux = 7 },
            new Relic { Id = candle, Counter = 9 });
        Require(battle.RelicStateCount == 0 && battle.GetRelic(rules, 0).Aux == 7
            && battle.GetRelic(rules, 1).Counter == 9, "fixed counters do not consume mutable slots");
        RoundTrip(battle, rules.Relics);

        rows = Enumerable.Repeat(new Relic { Id = stone, Aux = 2 }, 302).ToArray();
        rows[299] = new Relic { Id = nib, Counter = 9, Aux = 1 };
        rows[300] = new Relic { Id = nib, Counter = 8, Aux = 2 };
        rows[301] = new Relic { Id = nib, Flags = RelicFlags.Melted, Counter = 7 };
        TestRelics.Set(ref battle, ref rules, rows);
        Require(rules.PenNibRelicIndex == 300, "wide Pen Nib cache selects last unmelted instance");
        Require(battle.GetRelic(rules, 299).Counter == 9 && battle.GetRelic(rules, 300).Counter == 8,
            "duplicate instances keep independent counters and original indices");
        RoundTrip(battle, rules.Relics);
        byte[] request = SearchCodec.EncodeRequest(new SearchCase(rules, [battle, battle],
            new BeamOptions(1, 1, 1, TimeSpan.Zero)));
        SearchCase decoded = SearchCodec.DecodeRequest(request);
        Require(decoded.Rules.Relics.Equivalent(rules.Relics) && decoded.Rules.PenNibRelicIndex == 300
            && SearchCodec.EncodeRequest(decoded).AsSpan().SequenceEqual(request), "multi-root context round trip");
        // Immutable constants distinguish roots even when their compact Battle states are identical.
        rows[0].Aux++;
        RelicRoster different = RelicRoster.Create(rows);
        Require(!rules.Relics.Equivalent(different), "fixed constants participate in context identity");

        rows = Enumerable.Repeat(new Relic { Id = candle, Counter = 3 }, Limits.RelicRoster).ToArray();
        TestRelics.Set(ref battle, ref rules, rows);
        Require(rules.Relics.Count == 65535 && battle.RelicStateCount == 0, "full ushort roster");
        RoundTrip(battle, rules.Relics);
        rejected = false;
        try { RelicRoster.Create(new Relic[65536]); }
        catch (CapacityException e) { rejected = e.Region == "RelicRoster"; }
        Require(rejected, "65536 roster entries explicitly rejected");
        Console.WriteLine("RELIC_SHARED24 copy/isolation/duplicates/fixed/wide-index/24+1/65535+1/current-format/multi-root=pass");
    }

    private static void RoundTrip(in Battle battle, RelicRoster roster)
    {
        byte[] bytes = BattleCodec.Encode(battle, roster);
        Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) == Codec.FormatVersion, "wire version");
        Battle decoded = BattleCodec.Decode(bytes, out RelicRoster decodedRoster);
        Require(Fingerprint.Equal(battle, decoded) && roster.Equivalent(decodedRoster)
            && bytes.AsSpan().SequenceEqual(BattleCodec.Encode(decoded, decodedRoster)), "lossless wire round trip");
        byte[] old = (byte[])bytes.Clone();
        foreach (ushort version in new ushort[] { 7, 8 })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(old.AsSpan(4), version);
            bool oldRejected = false;
            try { BattleCodec.Decode(old, out _); }
            catch (InvalidDataException) { oldRejected = true; }
            Require(oldRejected, "obsolete format rejected");
        }
        bool rejected = false;
        try { BattleCodec.Decode(bytes.AsSpan(0, bytes.Length - 1), out _); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "truncated roster rejected");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException("Relic storage: " + message);
    }
}
