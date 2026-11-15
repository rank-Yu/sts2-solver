using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

[ContentId("CRACKED_CORE")]
public sealed partial class CrackedCore : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Lightning"];

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1)
            return;
        int count = (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0);
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("LIGHTNING_ORB"), count);
    }
}

[ContentId("RUNIC_CAPACITOR")]
public sealed class RunicCapacitor : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1)
            return;
        Kernel.AddOrbSlots(ref x.B, (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0));
    }
}

[ContentId("SYMBIOTIC_VIRUS")]
public sealed class SymbioticVirus : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Dark"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1)
            return;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("DARK_ORB"), (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0));
    }
}

[ContentId("GOLD_PLATED_CABLES")]
public sealed class GoldPlatedCables : RelicDef
{
    public override int ModifyOrbPassiveTriggerCounts(in Battle battle, Rules rules, Listener self,
        in Orb orb, int orbIndex, int triggerCount)
        => orbIndex == 0 ? triggerCount + 1 : triggerCount;
}

[ContentId("POWER_CELL")]
public sealed class PowerCell : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1) return;
        Span<int> picks = stackalloc int[x.B.DrawCount];
        int total = 0;
        Span<Card> cards = stackalloc Card[x.B.DrawCount];
        Span<int> positions = stackalloc int[x.B.DrawCount];
        // Both StableShuffle calls execute even when a group is empty.
        for (int group = 0; group < 2; group++)
        {
            int count = 0;
            for (int i = 0; i < x.B.DrawCount; i++)
            {
                Card card = Piles.At(ref x.B, Pile.Draw, i);
                if ((card.Flags & CardFlags.CostsX) != 0
                    || Kernel.EnergyCostWithLocalModifiers(x.B, card) != 0
                    || ((card.Keywords & Keywords.Innate) != 0) != (group == 1)) continue;
                cards[count] = card;
                positions[count++] = i;
            }
            CardSort.SortWithOrigins(cards[..count], positions[..count], x.R, x.B, Pile.Draw);
            RngState rng = x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
            for (int i = count - 1; i > 0; i--)
            {
                int other = rng.NextInt(i + 1);
                (positions[i], positions[other]) = (positions[other], positions[i]);
            }
            positions[..count].CopyTo(picks[total..]);
            total += count;
        }
        int take = Math.Min(total, (int)x.R.RelicVar(Id, 0));
        for (int i = 0; i < take; i++)
        {
            int index = picks[i];
            Kernel.MoveCard(ref x.B, Pile.Draw, index, Pile.Hand, PilePosition.Bottom);
            for (int j = i + 1; j < take; j++)
                if (picks[j] > index) picks[j]--;
        }
    }
}

[ContentId("REPTILE_TRINKET")]
public sealed class ReptileTrinket : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];

    public override void AfterPotionUsed(ref Exec x, Listener self, ushort potionId)
    {
        if (x.B.Player.IsDead)
            return;
        Kernel.ApplyTemporaryStrength(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.ReptileTrinketPower>(), (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0),
            Limits.PlayerOwner, sign: 1);
    }
}
