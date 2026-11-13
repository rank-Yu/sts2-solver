// 对照游戏 0.113.0 (16ac0fe3) 的领域能力；开场施加由原生游戏完成。
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Content.Powers;

[ContentId("AEONGLASS_DOMINION_POWER")]
public sealed class AeonglassDominionPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("WITHER", 0)];
    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner == Limits.PlayerOwner)
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Wither>()),
                Pile.Draw, PilePosition.Top, Limits.NoOwner);
    }
}

[ContentId("INSATIABLE_DOMINION_POWER")]
public sealed class InsatiableDominionPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void AfterSideTurnEndLate(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates) return;
        Span<byte> targets = stackalloc byte[1] { self.Owner };
        Kernel.Damage(ref x, targets, x.R.PowerVar(Id, 0) + x.B.PlayerTurn,
            ValueProps.Unpowered, Limits.NoOwner, false, default);
    }
}

[ContentId("LAGAVULIN_MATRIARCH_DOMINION_POWER")]
public sealed class LagavulinMatriarchDominionPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override IReadOnlyList<string> AppliedPowers => ["STRENGTH_POWER", "DEXTERITY_POWER"];
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side != Side.Player || !participates) return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.ApplyPowerWithoutCardSource(ref x, self.Owner, Registry.PowerId<StrengthPower>(), -amount, Limits.NoOwner);
        if (x.NeedsChoice) return;
        Kernel.ApplyPowerWithoutCardSource(ref x, self.Owner, Registry.PowerId<DexterityPower>(), -amount, Limits.NoOwner);
        if (!x.NeedsChoice) Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

[ContentId("AMNESIA_POWER")]
public sealed class AmnesiaPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy"];
    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal count)
        => self.Owner == Limits.PlayerOwner ? Math.Max(0m, count - x.R.PowerVar(Id, 0)) : count;
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (self.Owner == Limits.PlayerOwner) Kernel.LoseEnergy(ref x, x.R.PowerVar(Id, 1));
    }
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side != Side.Enemy) Kernel.RemovePowerAt(ref x.B, self.Index);
    }
}

[ContentId("YOU_ARE_MINE_POWER")]
public sealed class YouAreMinePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override IReadOnlyList<string> AppliedPowers => ["WEAK_POWER", "VULNERABLE_POWER", "FRAIL_POWER"];
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner || x.B.PlayerTurn < 2) return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.ApplyPowerWithoutCardSource(ref x, self.Owner, Registry.PowerId<WeakPower>(), amount, Limits.NoOwner);
        if (x.NeedsChoice) return;
        Kernel.ApplyPowerWithoutCardSource(ref x, self.Owner, Registry.PowerId<VulnerablePower>(), amount, Limits.NoOwner);
        if (x.NeedsChoice) return;
        Kernel.ApplyPowerWithoutCardSource(ref x, self.Owner, Registry.PowerId<FrailPower>(), amount, Limits.NoOwner);
        if (!x.NeedsChoice) Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

[ContentId("EXPLOSIVE_POWER")]
public sealed class ExplosivePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (creature != self.Owner || wasRemovalPrevented || x.B.Player.IsDead) return;
        Span<byte> targets = stackalloc byte[1] { Limits.PlayerOwner };
        Kernel.Damage(ref x, targets, x.B.Powers[self.Index].Amount, ValueProps.Unpowered,
            Limits.NoOwner, false, default);
    }
}
