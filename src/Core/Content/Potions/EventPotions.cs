using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Potions;

[ContentId("FOUL_POTION")]
public sealed class FoulPotion : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // FoulPotion in combat: Damage every non-pet living creature (the player and the hittable
    // enemies) for Damage with Damage.Props, dealt by the owner.
    public override void OnUse(ref Exec x)
    {
        Span<byte> targets = stackalloc byte[Limits.Enemies + 1];
        int count = 0;
        targets[count++] = Limits.PlayerOwner;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (Kernel.IsHittable(x.B, owner))
                targets[count++] = owner;
        }
        Kernel.Damage(ref x, targets[..count], Kernel.PotionVar(x, this, 0),
            Kernel.PotionVarProps(x, this, 0), Limits.PlayerOwner, hasCardSource: false, default);
    }
}

/// <summary>
/// <c>Ambergris</c>: heal <c>MaxHp * HealPercent / 100</c>, then apply 1 <c>AmbergrisPower</c>
/// (<c>PotionOnUseSupport.Use</c> case Ambergris; the power grants the extra turn).
/// </summary>
[ContentId("AMBERGRIS")]
public sealed class AmbergrisPotion : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["HealPercent"];

    public override void OnUse(ref Exec x)
    {
        decimal percent = Kernel.PotionVar(x, this, 0);
        Kernel.Heal(ref x, Limits.PlayerOwner, x.B.Creature(Limits.PlayerOwner).MaxHp * percent / 100m);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.AmbergrisPower>(), 1, Limits.PlayerOwner);
    }
}

[ContentId("GLOWWATER_POTION")]
public sealed class GlowwaterPotion : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // GlowwaterPotion: ExhaustHand(player), then Draw(player, Cards).
    public override void OnUse(ref Exec x)
    {
        int count = x.B.HandCount;
        for (int index = 0; index < count && x.B.HandCount > 0; index++)
        {
            Kernel.Exhaust(ref x, Pile.Hand, 0);
            if (x.NeedsChoice)
                return;
        }
        Kernel.Draw(ref x, (int)Kernel.PotionVar(x, this, 0), fromHandDraw: false);
    }
}
