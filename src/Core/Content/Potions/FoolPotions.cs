// 对照游戏 0.113.0 (16ac0fe3) 的 PrecisionPotion、BottledSong、CrystallineEuphoria。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Cards.Fool;
namespace Sts2Solver.Content.Potions;

[ContentId("PRECISION_POTION")]
public sealed class PrecisionPotion : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["AimPower"];
    public override IReadOnlyList<string> AppliedPowers => ["AIM_POWER"];
    public override void OnUse(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.AimPower>(),
            (int)Kernel.PotionVar(x, this, 0), Limits.PlayerOwner);
}

[ContentId("CRYSTALLINE_EUPHORIA")]
public sealed class CrystallineEuphoria : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("JOY", 0), ("SADNESS", 0)];
    public override void OnUse(ref Exec x)
    {
        int count = (int)Kernel.PotionVar(x, this, 0);
        for (int i = 0; i < count && !x.NeedsChoice; i++)
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<Joy>()), Pile.Hand);
        if (x.NeedsChoice) return;
        count = x.B.MaxHandSize - x.B.HandCount;
        for (int i = 0; i < count && !x.NeedsChoice; i++)
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<Sadness>()), Pile.Hand);
    }
}

[ContentId("BOTTLED_SONG")]
public sealed class BottledSong : PotionDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.Wah, CardPools.Cho, CardPools.Ki];
    public override void OnUse(ref Exec x)
    {
        Span<Card> cards = stackalloc Card[3];
        int count = 0;
        for (int i = 0; i < 3; i++)
        {
            string pool = i == 0 ? CardPools.Wah : i == 1 ? CardPools.Cho : CardPools.Ki;
            int added = Kernel.SampleCardPool(ref x.B, x.R, pool, 1, true,
                RngStream.CombatCardGeneration, cards[count..]);
            if (added > 0) Kernel.MakeFreeThisTurn(x.R, ref cards[count++]);
        }
        for (int i = 0; i < count && !x.NeedsChoice; i++)
            Kernel.AddGeneratedCard(ref x, cards[i], Pile.Hand);
    }
}
