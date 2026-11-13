// 对照游戏 0.113.0 (16ac0fe3) 的 BlossomShot、RainShot、Florescence 和 TheShot。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Powers;
namespace Sts2Solver.Content.Cards.Fool;

[ContentId("BLOSSOM_SHOT")]
public sealed class BlossomShot : SingleArrowCard
{
    public override IReadOnlyList<string> AppliedPowers => ["IMPALED_POWER", "STRETCH_POWER"];
    public override void OnPlay(ref Exec x)
    {
        byte target = x.Target;
        if (x.B.Creature(target).IsDead) return;
        int arrow = Kernel.TryShootArrow(ref x);
        if (arrow < 0 || x.NeedsChoice || x.B.Creature(target).IsDead) return;
        int outer = x.B.Fool.ActiveAttack;
        x.B.Fool.ActiveAttack = ++x.B.Fool.AttackSerial;
        try
        {
            Kernel.CardAttack(ref x, FoolEffects.Var(ref x, 0), 1, AttackTargeting.Single, target,
                ValueProps.Move | ValueProps.FromArrow, endAttack: false);
            if (x.NeedsChoice) return;
            int amount = x.LastDamageTotal + x.LastDamageOverkill;
            Kernel.EndAttackContext(ref x, Limits.PlayerOwner);
            if (!x.NeedsChoice) FoolEffects.Apply<StretchPower>(ref x, amount);
        }
        finally { x.B.Fool.ActiveAttack = outer; }
        if (!x.NeedsChoice) Kernel.ImpaleArrow(ref x, target, arrow);
    }
}

[ContentId("RAIN_SHOT")]
public sealed class RainShot : SingleArrowCard
{
    public override void OnPlay(ref Exec x)
    {
        byte target = x.Target;
        if (x.B.Creature(target).IsDead) return;
        int arrow = Kernel.TryShootArrow(ref x);
        if (arrow < 0 || x.NeedsChoice) return;
        // RainShot uses AttackContext directly: no BeforeAttack callback or attacked-history entry.
        Kernel.BeginAttack(ref x, ValueProps.Move | ValueProps.FromArrow, true);
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        targets[0] = target;
        Kernel.Damage(ref x, targets[..1], FoolEffects.Var(ref x, 0),
            ValueProps.Move | ValueProps.FromArrow, Limits.PlayerOwner, true, x.Card);
        if (x.NeedsChoice) return;
        int amount = x.LastDamageTotal + x.LastDamageOverkill;
        Kernel.SetPowerAmount(ref x.B, Limits.PlayerOwner, Registry.PowerId<AimPower>(), 0);
        Kernel.SetPowerAmount(ref x.B, Limits.PlayerOwner, Registry.PowerId<QuietusPower>(), 0);
        int count = 0;
        for (int i = 0; i < x.B.RosterCount; i++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[i]);
            if (owner != target && Kernel.IsHittable(x.B, owner)) targets[count++] = owner;
        }
        if (count > 0) Kernel.Damage(ref x, targets[..count], amount, ValueProps.Move,
            Limits.PlayerOwner, true, x.Card, capOnly: true);
        if (x.NeedsChoice) return;
        Kernel.ImpaleArrow(ref x, target, arrow);
        if (!x.NeedsChoice) Kernel.EndAttackContext(ref x, Limits.PlayerOwner);
    }
}

[ContentId("THE_SHOT")]
public sealed class TheShot : SingleArrowCard
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target,
        bool selfInHand, out decimal damage)
    {
        damage = CardEffects.Var(x.B, x.R, self, 0) + CardEffects.Var(x.B, x.R, self, 1) * self.Private;
        return true;
    }
    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, false, out decimal damage);
        Kernel.ShootArrow(ref x, damage);
    }
}

[ContentId("FLORESCENCE")]
public sealed class Florescence : CardDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("THE_SHOT", 0), ("THE_SHOT", 1)];
    public override void OnPlay(ref Exec x)
    {
        int count = 0;
        for (int i = 0; i < x.B.CardCount; i++)
            if (Piles.PileOf(x.B, i, out _) != Pile.Exhaust
                && (x.R.Card(x.B.Cards[i].Id, x.B.Cards[i].Upgrade).Tags & CardTag.Shoot) != 0) count++;
        Span<byte> marks = stackalloc byte[count];
        Span<byte> outer = stackalloc byte[count];
        int saved = Kernel.ReserveTransientMarks(ref x, count, out int first);
        int next = 0;
        for (int i = 0; i < x.B.CardCount; i++)
        {
            if (Piles.PileOf(x.B, i, out _) == Pile.Exhaust
                || (x.R.Card(x.B.Cards[i].Id, x.B.Cards[i].Upgrade).Tags & CardTag.Shoot) == 0) continue;
            outer[next] = x.B.Cards[i].TransientMark;
            marks[next] = checked((byte)(first + next));
            x.B.Cards[i].TransientMark = marks[next++];
        }
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (Kernel.TryFindUniqueTransientMark(x.B, marks[i], out Pile pile, out int index))
                    Kernel.Exhaust(ref x, pile, index);
                if (x.NeedsChoice) return;
            }
        }
        finally
        {
            Kernel.RestoreMarkedCards(ref x.B, marks, outer);
            Kernel.ReleaseTransientMarks(ref x, saved, first, count);
        }
        Card shot = Kernel.CreateCard(x.R, Registry.CardId<TheShot>(), x.Card.Upgrade);
        shot.Private = checked((short)count);
        Kernel.AddGeneratedCard(ref x, shot, Pile.Hand);
    }
}
