using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Monsters.Act3;

/// <summary>
/// ScrollOfBiting: MORE_TEETH applies Strength 2 to itself; its starting PaperCutsPower 2 is a root
/// power (<c>MonsterMoveEffects</c> "ScrollOfBiting", native <c>AfterAddedToRoom</c>). The initial
/// move differs per instance (<c>StarterMoveIdx</c>) and is already the compiled initial node.
/// </summary>
[ContentId("SCROLL_OF_BITING")]
public sealed partial class ScrollOfBiting : MonsterDef
{

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "MORE_TEETH")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
    }
}

/// <summary>
/// SlimedBerserker: VOMIT_ICHOR_MOVE adds ten Slimed to the discard pile, LEECHING_HUG_MOVE applies
/// Weak 3 to the player with a null applier and Strength 3 to itself (<c>MonsterMoveEffects</c>
/// "SlimedBerserker"; <c>SlimedBerserker.cs:77</c>).
/// </summary>
[ContentId("SLIMED_BERSERKER")]
public sealed class SlimedBerserker : MonsterDef
{
    private const int VomitSlime = 10;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SLIMED", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "VOMIT_ICHOR_MOVE":
                for (int index = 0; index < VomitSlime; index++)
                {
                    Kernel.AddGeneratedCard(ref x,
                        Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Slimed>()), Pile.Discard, creator: Limits.NoOwner);
                }
                break;
            case "LEECHING_HUG_MOVE":
                // SlimedBerserker passes a null applier natively; NoOwner keeps the native
                // applier-given modifiers and ApplierDied paths off this Weak row.
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.WeakPower>(), 3, Limits.NoOwner);
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 3, owner);
                break;
        }
    }
}

/// <summary>
/// SoulNexus: DRAIN_LIFE_MOVE applies Vulnerable 2 and Weak 2 to the player
/// (<c>MonsterMoveEffects</c> "SoulNexus"); the other moves are plain attacks.
/// </summary>
[ContentId("SOUL_NEXUS")]
public sealed class SoulNexus : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId != "DRAIN_LIFE_MOVE")
            return;
        Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.VulnerablePower>(), 2, owner);
        Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.WeakPower>(), 2, owner);
    }
}

/// <summary>
/// TheLost: DEBILITATING_SMOG takes <c>DebilitatingSmogStrengthStealAmount</c> Strength from the
/// player and gives it to itself; EYE_LASERS is a plain attack. Its starting PossessStrengthPower 1
/// is a root power (<c>MonsterMoveEffects</c> "TheLost").
/// </summary>
[ContentId("THE_LOST")]
public sealed partial class TheLost : MonsterDef
{
    private const int StrengthStealStat = 0;

    public override IReadOnlyList<string> StaticIntNames => ["DebilitatingSmogStrengthStealAmount"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "DEBILITATING_SMOG")
            return;
        int stolen = rules.StaticInts[StrengthStealStat];
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.StrengthPower>(),
            -stolen, owner);
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), stolen, owner);
    }
}

/// <summary>
/// TheForgotten: MIASMA takes <c>DebilitatingSmogDexStealAmount</c> Dexterity from the player, gains
/// 8 Block and gives the Dexterity to itself; DREAD's damage is its base plus the owner's current
/// Dexterity minus its root value (<c>MonsterMoveEffects</c>, <c>AdjustMonsterMoveDamage</c>). Its
/// starting PossessSpeedPower 1 is a root power.
/// </summary>
[ContentId("THE_FORGOTTEN")]
public sealed partial class TheForgotten : MonsterDef
{
    private const int DexterityStealStat = 0;

    public override IReadOnlyList<string> StaticIntNames => ["DebilitatingSmogDexStealAmount"];

    public override IReadOnlyList<string> RootPowerAmountNames => ["DEXTERITY_POWER"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "MIASMA")
            return;
        int stolen = rules.StaticInts[DexterityStealStat];
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.DexterityPower>(),
            -stolen, owner);
        Kernel.GainBlock(ref x, owner, 8, ValueProps.Move, fromCard: false);
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.DexterityPower>(), stolen, owner);
    }

    public override int ModifyAttackDamage(in Battle battle, Rules rules, byte owner, int moveNode,
        int attackIndex, int damage)
    {
        ref Battle mutable = ref System.Runtime.CompilerServices.Unsafe.AsRef(in battle);
        MonsterRules machine = rules.MonsterRule(mutable.Enemy(owner).Rules);
        if (machine.Nodes[moveNode].NativeId != "DREAD")
            return damage;
        int current = Kernel.GetAmount(battle, owner, Registry.PowerId<Powers.DexterityPower>());
        return damage + current - machine.RootPowerAmounts[0];
    }
}
