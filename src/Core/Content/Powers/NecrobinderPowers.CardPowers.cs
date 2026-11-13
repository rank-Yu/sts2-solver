using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>SentryModePower</c>: <c>BeforeHandDraw</c> creates one <c>SweepingGaze</c> token per
/// stack into the hand (<c>TurnStartPowerSupport</c>'s SentryMode case). The generated card enters
/// combat through the same generated-card path as every other in-combat creation.
/// </summary>
[ContentId("SENTRY_MODE_POWER")]
public sealed class SentryModePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SWEEPING_GAZE", 0)];

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if (!Registry.TryCard("SWEEPING_GAZE", out ushort gaze))
            throw new UnsupportedException("SweepingGaze 没有 Sts2Solver 定义。");
        int amount = x.B.Powers[self.Index].Amount;
        for (int index = 0; index < amount; index++)
        {
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, gaze), Pile.Hand);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>CallOfTheVoidPower</c>: <c>BeforeHandDraw</c> generates <c>Amount</c> distinct cards
/// one at a time from the character pool excluding Basic/Ancient, applies Ethereal to each, then adds
/// them to the hand (<c>TurnStartPowerSupport</c>'s CallOfTheVoid case). The existing engine's
/// <c>PrepareCharacterGenerationCandidates(NonBasicAndAncient)</c> plus
/// <c>FilterForCombatAndPlayerCount</c> equals the frozen <c>CharacterAll</c> pool (that filter
/// already excludes Basic/Ancient/Event), and <c>GetDistinctForCombat(1)</c> per amount is one full
/// <c>TakeRandom</c> shuffle per generated card.
/// </summary>
[ContentId("CALL_OF_THE_VOID_POWER")]
public sealed class CallOfTheVoidPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Span<Card> sampled = stackalloc Card[1];
        for (int index = 0; index < amount; index++)
        {
            int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAll, 1, distinct: true,
                RngStream.CombatCardGeneration, sampled);
            if (count == 0)
                return;
            Card card = sampled[0];
            card.AddKeyword(Keywords.Ethereal);
            Kernel.AddGeneratedCard(ref x, card, Pile.Hand);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>PagestormPower</c>: when a card with the Ethereal keyword is drawn, draw Amount more
/// (<c>AfterCardDrawnMirrors.HandlePagestormPower</c>). <c>CardModel.Keywords</c> includes HexPower's
/// global Ethereal, so the battle-aware <c>Kernel.HasKeyword</c> is the source.
/// </summary>
[ContentId("PAGESTORM_POWER")]
public sealed class PagestormPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if ((uint)drawnPosition >= (uint)x.B.CardCount)
            throw new UnsupportedException("Pagestorm 触发时抽到的牌已不在牌堆。");
        if (!Kernel.HasKeyword(x.B, x.B.Cards[drawnPosition], Keywords.Ethereal))
            return;
        Kernel.Draw(ref x, x.B.Powers[self.Index].Amount, fromHandDraw: false);
    }
}
