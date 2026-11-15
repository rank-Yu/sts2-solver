using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>BigHat</c>: on <c>AfterSideTurnStart</c> while the player's turn number is at most 1, generate
/// its <c>Cards</c> distinct Ethereal character cards into the hand.
/// </summary>
[ContentId("BIG_HAT")]
public sealed class BigHat : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1) return;
        Span<Card> cards = stackalloc Card[Limits.Cards];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterEthereal,
            (int)x.R.RelicVar(Id, 0), distinct: true, RngStream.CombatCardGeneration, cards);
        for (int i = 0; i < count; i++)
        {
            if (!Kernel.AddGeneratedCard(ref x, cards[i], Pile.Hand)) return;
            if (x.NeedsChoice) return;
        }
    }

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterEthereal];
}
