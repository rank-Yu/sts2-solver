using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Monsters.Act1;

/// <summary>
/// Vantom: "DISMEMBER_MOVE" adds three Wounds to the discard pile; "PREPARE_MOVE" applies Strength 2
/// to itself (MonsterMoveEffects "Vantom", literals). Its SlipperyPower 8/9 is a starting power
/// imported from the root, not replayed here.
/// </summary>
[ContentId("VANTOM")]
public sealed partial class Vantom : MonsterDef
{
    private const int DismemberWounds = 3;
    private const int PrepareStrength = 2;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("WOUND", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "DISMEMBER_MOVE":
                for (int index = 0; index < DismemberWounds; index++)
                    Kernel.AddGeneratedCard(ref x,
                        Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Wound>()), Pile.Discard, creator: Limits.NoOwner);
                break;
            case "PREPARE_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                    PrepareStrength, owner);
                break;
        }
    }
}

/// <summary>
/// KinPriest: "ORB_OF_FRAILTY_MOVE" applies Frail 1 to the player, "ORB_OF_WEAKNESS_MOVE" Weak 1
/// (both <c>ApplyFromMonster</c>), and "RITUAL_MOVE" applies Strength <c>RitualStrength</c> to itself
/// (<c>MonsterMoveEffects</c> "KinPriest"; the existing engine applies Strength, not RitualPower).
/// </summary>
[ContentId("KIN_PRIEST")]
public sealed class KinPriest : MonsterDef
{
    private const int RitualStrength = 0;

    public override IReadOnlyList<string> StaticIntNames => ["RitualStrength"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "ORB_OF_FRAILTY_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.FrailPower>(), 1, owner);
                break;
            case "ORB_OF_WEAKNESS_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.WeakPower>(), 1, owner);
                break;
            case "RITUAL_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                    rules.StaticInts[RitualStrength], owner);
                break;
        }
    }
}

/// <summary>
/// KinFollower: "POWER_DANCE_MOVE" applies Strength <c>DanceStrength</c> to itself
/// (<c>MonsterMoveEffects</c> "KinFollower"). Its MinionPower is a starting power imported from the
/// root. The encounter's first follower starts on the dance move; that initial state is imported.
/// </summary>
[ContentId("KIN_FOLLOWER")]
public sealed partial class KinFollower : MonsterDef
{
    private const int DanceStrength = 0;


    public override IReadOnlyList<string> StaticIntNames => ["DanceStrength"];

    // Native KinFollower.AfterAddedToRoom applies MinionPower 1 with itself as applier. Root
    // followers already carry it in their imported power table; followers summoned during combat
    // need the entrance application to stay secondary enemies.
    public override void ApplyEntrancePowers(ref Exec x, byte owner)
        => Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.MinionPower>(), 1, owner);

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId == "POWER_DANCE_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                rules.StaticInts[DanceStrength], owner);
    }
}

/// <summary>
/// PhantasmalGardener: "ENLARGE_MOVE" applies Strength <c>EnlargeStr</c> to itself
/// (<c>MonsterMoveEffects</c> "PhantasmalGardener"). Its SkittishPower is a starting power imported
/// from the root.
///
/// The native state machine's initial state INIT_MOVE is a conditional branch keyed by the
/// encounter slot ("first" → FLAIL_MOVE, "second" → BITE_MOVE, "third" → LASH_MOVE,
/// "fourth" → ENLARGE_MOVE) that lives outside MonsterMoveStateMachine.States, so the rule compiler
/// stores no root selection for it. At the root each gardener's current move is already resolved and
/// imported; the branch is only walked for a fresh initial roll, and the four-gardener encounter
/// spawns them in slot order, so the enemy index recovers the native slot.
/// </summary>
[ContentId("PHANTASMAL_GARDENER")]
public sealed partial class PhantasmalGardener : MonsterDef
{
    private const int EnlargeStr = 0;

    public override IReadOnlyList<string> StaticIntNames => ["EnlargeStr"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId == "ENLARGE_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                rules.StaticInts[EnlargeStr], owner);
    }

    public override int ResolveConditional(ref Exec x, byte owner, int branchNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[branchNode].NativeId != "INIT_MOVE")
            return -1;
        string move = (owner - Limits.FirstEnemyOwner) switch
        {
            0 => "FLAIL_MOVE",
            1 => "BITE_MOVE",
            2 => "LASH_MOVE",
            3 => "ENLARGE_MOVE",
            int index => throw new UnsupportedException($"花园幽灵鳗没有第 {index} 个槽位的初始行动。"),
        };
        return rules.NodeIndex(move);
    }
}

/// <summary>
/// LagavulinMatriarch: "SLASH2_MOVE" gains <c>Slash2Block</c> Unpowered... move block; "SOUL_SIPHON_MOVE"
/// applies Strength -2 and Dexterity -2 to the player and Strength +2 to itself (all through
/// <c>combat.Apply</c>, not <c>ApplyFromMonster</c>); "STUNNED" clears Plating
/// (<c>MonsterMoveEffects.Apply</c> "LagavulinMatriarch"). The private <c>_isAwake</c> flag is
/// animation-only in the existing engine: no prediction branch reads it.
///
/// The native SLEEP_MOVE follows a ConditionalBranchState SLEEP_BRANCH; the existing engine resolves
/// it dynamically from the AsleepPower amount (<c>BranchMonsterAi.ResolveConditional</c>) rather than
/// the root-frozen selection, so the definition overrides ResolveConditional.
/// </summary>
[ContentId("LAGAVULIN_MATRIARCH")]
public sealed partial class LagavulinMatriarch : MonsterDef
{
    private const int Slash2Block = 0;

    public override IReadOnlyList<string> StaticIntNames => ["Slash2Block"];

    // The existing engine's fingerprint/continuation text reads _isAwake, but no resolution path
    // does (move selection reads the AsleepPower amount, and STUNNED only clears Plating).
    public override IReadOnlyList<string> InertMonsterIntNames => ["_isAwake"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "SLASH2_MOVE":
                Kernel.GainBlock(ref x, owner, rules.StaticInts[Slash2Block], ValueProps.Move, fromCard: false);
                break;
            case "SOUL_SIPHON_MOVE":
                Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.StrengthPower>(), -2, owner);
                Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.DexterityPower>(), -2, owner);
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
                break;
            case "STUNNED":
                Kernel.SetPowerAmount(ref x.B, owner, Registry.PowerId<Powers.PlatingPower>(), 0);
                break;
        }
    }

    public override int ResolveConditional(ref Exec x, byte owner, int branchNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[branchNode].NativeId != "SLEEP_BRANCH")
            return -1;
        return Kernel.GetAmount(x.B, owner, Registry.PowerId<Powers.AsleepPower>()) > 0
            ? rules.NodeIndex("SLEEP_MOVE")
            : rules.NodeIndex("SLASH_MOVE");
    }
}
