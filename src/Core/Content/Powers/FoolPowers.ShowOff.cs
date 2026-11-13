// 对照游戏 0.113.0 (16ac0fe3) ShowOffPower。失去牌堆引用的原卡仍可给牌组版本增加永久成长。
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Content.Powers;

[ContentId("SHOW_OFF_POWER")]
public sealed class ShowOffPower : FoolBuff
{
    public override bool IsInstanced => true;
    public override bool IsIdentityContent => true;
    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (self.Owner != Limits.PlayerOwner || !Kernel.IsEnemyOwner(creature)
            || !Kernel.TargetDeathTriggersFatal(x.B, creature)) return;
        Power power = x.B.Powers[self.Index];
        if (power.Aux0 > 0)
            for (int i = 0; i < x.B.CardCount; i++)
            {
                ref Card card = ref x.B.Cards[i];
                if (card.FoolIdentity != power.Aux0) continue;
                if (power.Amount > short.MaxValue - card.Var0 || power.Amount > short.MaxValue - card.Private)
                    throw new CapacityException("ShowOffGrowth", short.MaxValue);
                card.Var0 += (short)power.Amount;
                card.Private += (short)power.Amount;
                break;
            }
        if (power.Aux1 != 0) x.B.AddCounter(Counter.LongTermResourceValue, power.Amount);
        Kernel.RemovePowerAt(ref x.B, self.Index);
    }
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
}
