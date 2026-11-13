// 对照游戏 0.113.0 (16ac0fe3) 的同名 PowerModel。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Cards.Fool;
namespace Sts2Solver.Content.Powers;

[ContentId("CONDUCTORS_BATON_POWER")]
public sealed class ConductorsBatonPower : FoolBuff
{
    public override PowerStackType StackType => PowerStackType.Single;
    public override IReadOnlyList<string> VarNames => ["DamageDecrease"];
    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
        => damage.Target == self.Owner && damage.Props.IsPoweredAttack() ? x.R.PowerVar(Id, 0) : 1m;
    public override void GlobalAfterSideTurnStart(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
}

[ContentId("YAH_POWER")]
public sealed class YahPower : FoolBuff
{
    public override void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
    {
        if (participates) x.B.Powers[self.Index].Aux0 = 0;
    }
    public override void AfterArrowDislodged(ref Exec x, Listener self, byte target, int arrow,
        bool hasCardSource, in Card cardSource)
    {
        if (self.Owner != Limits.PlayerOwner || x.B.Powers[self.Index].Aux0 != 0) return;
        x.B.Powers[self.Index].Aux0 = 1;
        Kernel.GainEnergy(ref x, x.B.Powers[self.Index].Amount);
    }
}

[ContentId("EXPLOSIVE_ARROWS_POWER")]
public sealed class ExplosiveArrowsPower : FoolBuff
{
    public override void AfterArrowImpaled(ref Exec x, Listener self, byte target, int arrow)
    {
        if (self.Owner == Limits.PlayerOwner)
            CardEffects.DamageAllEnemies(ref x, x.B.Powers[self.Index].Amount, ValueProps.Unpowered, self.Owner);
    }
}

[ContentId("KI_VERSE_POWER")]
public sealed class KiVersePower : FoolBuff
{
    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target, int playCount)
        => self.Owner == Limits.PlayerOwner
            && (x.R.Card(card.Id, card.Upgrade).Tags & (CardTag.Wah | CardTag.Cho | CardTag.Ki)) != 0
                ? playCount + 1 : playCount;
    public override void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
        => Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, x.B.Powers[self.Index].Amount - 1);
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
}

[ContentId("ENNUI_POWER")]
public sealed class EnnuiPower : FoolBuff
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SADNESS", 0)];
    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + x.B.Powers[self.Index].Amount : amount;
    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner) return;
        int count = x.B.Powers[self.Index].Aux0;
        for (int i = 0; i < count && !x.NeedsChoice; i++)
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<Sadness>()), Pile.Hand);
    }
}

[ContentId("LULLABY_POWER")]
public sealed class LullabyPower : FoolBuff
{
    public override void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
    {
        if (participates) x.B.Powers[self.Index].Aux0 = 0;
    }
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner || x.B.Powers[self.Index].Aux0 != 0
            || Kernel.MelodiesThisTurn(x.B) < 3 || !Kernel.IsInProgress(x.B)) return;
        x.B.Powers[self.Index].Aux0 = 1;
        Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount, ValueProps.Unpowered, false);
    }
}
