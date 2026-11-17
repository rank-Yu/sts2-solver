using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// <c>AmbergrisPower</c>: while its amount is positive its owner takes another player turn instead of
/// the enemy turn; <c>AfterTakingExtraTurn</c> decrements it
/// (<c>ExtraTurnMirrors</c> registers both callbacks).
/// </summary>
[ContentId("AMBERGRIS_POWER")]
public sealed class AmbergrisPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool ShouldTakeExtraTurn(ref Exec x, Listener self)
        => self.Owner == Limits.PlayerOwner && x.B.Powers[self.Index].Amount > 0;

    public override void AfterTakingExtraTurn(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.ModifyPowerAmount(ref x.B, Limits.PlayerOwner, Id, amount - 1);
    }
}

/// <summary>
/// <c>ConfusedPower</c>: after a card is drawn, a card with a non-negative canonical energy cost gets a
/// random this-combat cost in 0..3 (<c>AfterCardDrawnMirrors.HandleConfusedPower</c> →
/// <c>CardEnergyCost.SetThisCombat(CombatEnergyCosts.NextInt(4))</c>).
/// </summary>
[ContentId("CONFUSED_POWER")]
public sealed class ConfusedPower : PowerDef
{
    private const int CostRange = 4;

    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (self.Owner != Limits.PlayerOwner || (uint)drawnPosition >= (uint)x.B.HandCount)
            return;
        ref Card card = ref x.B.Cards[drawnPosition];
        if (x.R.Card(card.Id, card.Upgrade).EnergyCost < 0)
            return;
        int cost = x.B.RngStream(RngStream.CombatEnergyCosts).NextInt(CostRange);
        Kernel.AddEnergyCostModifier(ref card, cost, CostType.Absolute,
            CostExpiration.EndOfCombat, reduceOnly: false);
    }
}
