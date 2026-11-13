// 对照游戏 0.113.0 (16ac0fe3) MythicFormPower.BeforeHandDraw。
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Content.Powers;

[ContentId("MYTHIC_FORM_POWER")]
public sealed class MythicFormPower : FoolBuff
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.Tarot];
    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner) return;
        int count = x.B.Powers[self.Index].Amount;
        int upgraded = x.B.Powers[self.Index].Aux0;
        Span<Card> selected = stackalloc Card[1];
        for (int i = 0; i < count && !x.NeedsChoice; i++)
        {
            int chosen = Kernel.SampleCardPool(ref x.B, x.R, CardPools.Tarot, 1, false,
                RngStream.CombatCardSelection, selected);
            if (chosen == 0) continue;
            if (i < upgraded) Kernel.UpgradeCard(x.R, ref selected[0]);
            Kernel.AddGeneratedCard(ref x, selected[0], Pile.Hand);
        }
    }
}
