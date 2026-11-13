using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

internal static class CardCostModifierChecks
{
    internal static void Run()
    {
        CheckWire();
        Card repeated = default;
        for (int i = 0; i < 1000; i++)
            Kernel.AddEnergyCostModifier(ref repeated, -2, CostType.Relative, CostExpiration.EndOfCombat, false);
        Require(repeated.EnergyModifierCount == 1 && repeated.EnergyModifiers[0].Amount == -2000, "long relative run");
        Kernel.AddEnergyCostModifier(ref repeated, 2000, CostType.Relative, CostExpiration.EndOfCombat, false);
        Require(repeated.EnergyModifierCount == 1 && repeated.EnergyModifiers[0].Amount == 0, "zero retains presence");

        Card large = default;
        CardCostModifiers.Append(ref large, CostModifier.Create(30000, CostType.Relative, CostExpiration.EndOfCombat, false));
        CardCostModifiers.Append(ref large, CostModifier.Create(30000, CostType.Relative, CostExpiration.EndOfCombat, false));
        Require(large.EnergyModifierCount == 2 && large.EnergyModifiers[0].Amount == short.MaxValue
            && large.EnergyModifiers[1].Amount == 60000-short.MaxValue, "wide sum splits without truncation");

        Card full = default;
        for (int i = 0; i < Limits.CardEnergyCostModifiers; i++)
            CardCostModifiers.Append(ref full, CostModifier.Create(1, CostType.Absolute, CostExpiration.EndOfCombat, false));
        Card before = full;
        bool capacity = false;
        try { CardCostModifiers.Append(ref full, CostModifier.Create(1, CostType.Absolute, CostExpiration.EndOfCombat, false)); }
        catch (CapacityException) { capacity = true; }
        Require(capacity && Fingerprint.Equal(before, full), "capacity failure is atomic");

        Random random = new(113016);
        for (int trial = 0; trial < 10000; trial++)
        {
            Card raw = default;
            int count = random.Next(1, Limits.CardEnergyCostModifiers + 1);
            raw.EnergyModifierCount = count;
            for (int i = 0; i < count; i++)
            {
                // Include long repeated runs and barriers, both short extremes and mixed signs.
                raw.EnergyModifiers[i] = i > 0 && random.Next(2) == 0
                    ? raw.EnergyModifiers[i-1]
                    : CostModifier.Create(random.Next(4) == 0 ? random.Next(short.MinValue, short.MaxValue+1) : random.Next(-5,6),
                        random.Next(4) == 0 ? CostType.Absolute : CostType.Relative,
                        (CostExpiration)(random.Next(4)*2), random.Next(4)==0);
            }
            Card compact = raw;
            CardCostModifiers.Compact(ref compact);
            Card again = compact;
            CardCostModifiers.Compact(ref again);
            Require(Fingerprint.Equal(compact, again), "idempotence");
            foreach (int expiryMask in new[] {0, 2, 4, 6})
            {
                Card originalAfter = Filter(raw, expiryMask);
                Card compactAfter = Filter(compact, expiryMask);
                foreach (int baseCost in new[] {0,1,3,127,32767})
                    Require(Evaluate(originalAfter, baseCost) == Evaluate(compactAfter, baseCost), "cost/expiry equivalence");
                Require((originalAfter.EnergyModifierCount != 0) == (compactAfter.EnergyModifierCount != 0), "modifier presence");
                CardCostModifiers.Compact(ref originalAfter);
                CardCostModifiers.Compact(ref compactAfter);
                Require(Fingerprint.Equal(originalAfter, compactAfter), "expiration regrouping canonical");
                for (int i=compactAfter.EnergyModifierCount;i<Limits.CardEnergyCostModifiers;i++)
                    Require(compactAfter.EnergyModifiers[i].Amount==0 && compactAfter.EnergyModifiers[i].Packed==0,"unused zero");
            }
            Card turn = compact;
            Kernel.EndOfTurnCleanup(ref turn);
            Card expected = Filter(raw, 2);
            CardCostModifiers.Compact(ref expected);
            Require(Fingerprint.Equal(turn,expected),"turn cleanup integration");
        }
        Console.WriteLine("COST_MODIFIER_MERGE cases=10000 expiry/base-cost/presence/barriers/wide-sum/capacity/canonical=pass");
    }

    private static void CheckWire()
    {
        Random random = new(120082);
        for (int trial = 0; trial < 512; trial++)
        {
            Card card = default;
            Span<byte> raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref card, 1));
            random.NextBytes(raw);
            card.Id = 0;
            card.EnergyModifiers = default;
            card.EnergyModifierCount = trial % (Limits.CardEnergyCostModifiers + 1);
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            Codec.WriteCard(writer, card);
            Require(stream.Length == 64, "current card encoding has no expansion");
            stream.Position = 0;
            using BinaryReader reader = new(stream);
            Card decoded = Codec.ReadCard(reader);
            Require(Fingerprint.Equal(card, decoded) && Fingerprint.HashCard(card) == Fingerprint.HashCard(decoded),
                "current card value and hash round trip");
        }
        Card merged = default;
        CardCostModifiers.Append(ref merged, CostModifier.Create(-2, CostType.Relative, CostExpiration.EndOfCombat, false));
        Battle state = default;
        state.HandCount = 1;
        state.Cards[0] = merged;
        state.HistoryCourseCard = merged;
        state.Flags |= BattleFlags.HistoryCourseCardPresent;
        for (int i = 0; i < Limits.DetachedCards; i++)
        {
            state.DetachedCards[i] = merged;
            state.DetachedCards[i].Flags |= CardFlags.PowerAfflictionChecked;
        }
        state.DecimalVarCount = 1;
        state.DecimalVars[0] = 3.125m;
        byte[] encoded = BattleCodec.Encode(state, RelicRoster.Empty);
        Require(encoded.Length == BattleCodec.HeaderSize + 4 + Battle.HeaderSize + 64 + 2,
            "current battle size");
        Battle copy = BattleCodec.Decode(encoded, out _);
        Require(Fingerprint.Equal(state, copy), "battle header/detached/history/card wire mapping");
        Require(encoded.AsSpan().SequenceEqual(BattleCodec.Encode(copy, RelicRoster.Empty)), "stable wire bytes");
        Console.WriteLine("CAPACITY10 current-wire/hash/header=pass");
    }

    private static Card Filter(Card card,int mask)
    {
        int kept=0, count=card.EnergyModifierCount;
        for(int i=0;i<count;i++)
            if(((byte)card.EnergyModifiers[i].Expiration & mask)==0)
                card.EnergyModifiers[kept++]=card.EnergyModifiers[i];
        for(int i=kept;i<Limits.CardEnergyCostModifiers;i++) card.EnergyModifiers[i]=default;
        card.EnergyModifierCount=kept;
        return card;
    }

    private static int Evaluate(in Card card,int cost)
    {
        for(int i=0;i<card.EnergyModifierCount;i++)
        {
            var m=card.EnergyModifiers[i];
            int changed=m.Type==CostType.Relative ? cost+m.Amount : m.Amount;
            cost=m.IsReduceOnly ? Math.Min(cost,changed) : changed;
        }
        return Math.Max(0,cost);
    }

    private static void Require(bool ok,string reason)
    {
        if(!ok) throw new InvalidDataException("Cost modifier merge: "+reason);
    }
}
