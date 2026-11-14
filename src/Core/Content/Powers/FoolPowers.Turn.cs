// 对照游戏 0.113.0 (16ac0fe3) 的同名 PowerModel。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Cards.Fool;
namespace Sts2Solver.Content.Powers;

[ContentId("ENCORE_POWER")]
public sealed class EncorePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Counter;
    // Started plays include completed plays and all nested plays still on the stack.
    public override bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card)
        => self.Owner != Limits.PlayerOwner || battle.Powers[self.Index].Aux0 < battle.Powers[self.Index].Amount;
    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner == Limits.PlayerOwner) x.B.Powers[self.Index].Aux0++;
    }
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
}

[ContentId("ECSTASY_POWER")]
public sealed class EcstasyPower : FoolBuff
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("JOY", 0)];
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner) return;
        Span<Card> cards = stackalloc Card[x.B.HandCount];
        Span<int> origins = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int i = 0; i < x.B.HandCount; i++)
            if (x.B.Cards[i].Id == Registry.CardId<Sadness>())
            { cards[count] = x.B.Cards[i]; origins[count++] = i; }
        if (count == 0) return;
        CardSort.SortWithOrigins(cards[..count], origins[..count], x.R, x.B, Pile.Hand);
        var rng = x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        for (int i = count - 1; i > 0; i--)
        {
            int pick = rng.NextInt(i + 1);
            (origins[i], origins[pick]) = (origins[pick], origins[i]);
        }
        int take = Math.Min(count, x.B.Powers[self.Index].Amount);
        Kernel.TransformCards(ref x, Pile.Hand, origins[..take], Kernel.CreateCard(x.R, Registry.CardId<Joy>()), 0);
    }
}

[ContentId("SIMPLETON_POWER")]
public sealed class SimpletonPower : FoolBuff
{
    public override void AfterAutoPostPlayPhaseEntered(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner) return;
        Span<int> options = stackalloc int[Limits.MaxHandSize];
        // Native SimpletonPower (0.113.0) re-reads Amount after each auto-play. Playing
        // another Simpleton adds iterations; other hooks may also move this power's row.
        for (int i = 0; !x.NeedsChoice && i < Kernel.GetAmount(x.B, self.Owner, Id); i++)
        {
            int choices = 0;
            decimal cheapest = decimal.MaxValue;
            for (int c = 0; c < x.B.HandCount; c++)
            {
                Card card = x.B.Cards[c];
                if (card.HasKeyword(Keywords.Unplayable)) continue;
                decimal cost = (card.Flags & CardFlags.CostsX) != 0 ? 999999999m
                    : Kernel.EnergyCostWithModifiers(x.B, x.R, card, Pile.Hand);
                if (cost < cheapest) { cheapest = cost; choices = 0; }
                if (cost == cheapest) options[choices++] = c;
            }
            if (choices == 0) continue;
            int selected = x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, choices);
            Kernel.AutoPlay(ref x, Pile.Hand, options[selected]);
        }
    }
}

[ContentId("QUIETUS_POWER")]
public sealed class QuietusPower : FoolBuff
{
    public override void AfterArrowShot(ref Exec x, Listener self, int arrow)
    {
        if (self.Owner == Limits.PlayerOwner && arrow >= 0) x.B.Fool.Arrows[arrow].DestroyAfterImpaled = true;
    }
    public override void BeforeAttack(ref Exec x, Listener self, byte attacker, ValueProps props)
    {
        if (attacker != self.Owner || !props.IsPoweredAttack() || (props & ValueProps.FromArrow) == 0
            || x.B.Powers[self.Index].Aux0 != 0) return;
        ushort identity = Kernel.EnsureFoolCardIdentity(ref x);
        x.B.Powers[self.Index].Aux0 = identity;
        x.B.Powers[self.Index].Aux1 = x.B.Fool.ActiveAttack;
    }
    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        ref readonly Power power = ref x.B.Powers[self.Index];
        return damage.Dealer == self.Owner && damage.Props.IsPoweredAttack()
            && (damage.Props & ValueProps.FromArrow) != 0
            && (power.Aux0 == 0 || !damage.HasCardSource || damage.CardSource.FoolIdentity == power.Aux0)
                ? power.Amount : 1m;
    }
    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        int attack = x.B.Powers[self.Index].Aux1;
        if (attack != 0 && attack == x.B.Fool.ActiveAttack) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
}
