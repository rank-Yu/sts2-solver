using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Monsters.Act1;

/// <summary>ShrinkerBeetle: "SHRINKER_MOVE" applies ShrinkPower -1 to the player.</summary>
[ContentId("SHRINKER_BEETLE")]
public sealed class ShrinkerBeetle : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "SHRINKER_MOVE")
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.ShrinkPower>(), -1, owner);
    }
}

/// <summary>LeafSlimeS: "GOOP_MOVE" adds one Slimed to the discard pile.</summary>
[ContentId("LEAF_SLIME_S")]
public sealed class LeafSlimeS : MonsterDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SLIMED", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "GOOP_MOVE")
            AddSlimed(ref x, count: 1);
    }

    internal static void AddSlimed(ref Exec x, int count)
    {
        for (int index = 0; index < count; index++)
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Slimed>()), Pile.Discard, creator: Limits.NoOwner);
    }
}

/// <summary>LeafSlimeM: "STICKY_SHOT" adds two Slimed to the discard pile.</summary>
[ContentId("LEAF_SLIME_M")]
public sealed class LeafSlimeM : MonsterDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SLIMED", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "STICKY_SHOT")
            LeafSlimeS.AddSlimed(ref x, count: 2);
    }
}

/// <summary>TwigSlimeS: only attacks.</summary>
[ContentId("TWIG_SLIME_S")]
public sealed class TwigSlimeS : MonsterDef;

/// <summary>TwigSlimeM: "STICKY_SHOT_MOVE" adds one Slimed to the discard pile.</summary>
[ContentId("TWIG_SLIME_M")]
public sealed class TwigSlimeM : MonsterDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SLIMED", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "STICKY_SHOT_MOVE")
            LeafSlimeS.AddSlimed(ref x, count: 1);
    }
}

/// <summary>Flyconid: spore moves apply Vulnerable/Frail 2 to the player.</summary>
[ContentId("FLYCONID")]
public sealed class Flyconid : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "VULNERABLE_SPORES_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.VulnerablePower>(), 2, owner);
                break;
            case "FRAIL_SPORES_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.FrailPower>(), 2, owner);
                break;
        }
    }
}

/// <summary>Mawler: "ROAR_MOVE" applies Vulnerable 3 to the player.</summary>
[ContentId("MAWLER")]
public sealed class Mawler : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "ROAR_MOVE")
            Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.VulnerablePower>(), 3, owner);
    }
}

/// <summary>SnappingJaxfruit: "ENERGY_ORB_MOVE" applies Strength 2 to itself.</summary>
[ContentId("SNAPPING_JAXFRUIT")]
public sealed class SnappingJaxfruit : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "ENERGY_ORB_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
    }
}

/// <summary>
/// AxeRubyRaider: both swings gain <c>SwingBlock</c> move block (static value captured from the
/// native model; MonsterMoveEffects "AxeRubyRaider", "SWING_1"/"SWING_2").
/// </summary>
[ContentId("AXE_RUBY_RAIDER")]
public sealed class AxeRubyRaider : MonsterDef
{
    private const int SwingBlock = 0;

    public override IReadOnlyList<string> StaticIntNames => ["SwingBlock"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "SWING_1":
            case "SWING_2":
                Kernel.GainBlock(ref x, owner, rules.StaticInts[SwingBlock], ValueProps.Move, fromCard: false);
                break;
        }
    }
}

/// <summary>AssassinRubyRaider: only "KILLSHOT_MOVE", no non-attack effect.</summary>
[ContentId("ASSASSIN_RUBY_RAIDER")]
public sealed class AssassinRubyRaider : MonsterDef;

/// <summary>BruteRubyRaider: "ROAR_MOVE" applies Strength 3 to itself.</summary>
[ContentId("BRUTE_RUBY_RAIDER")]
public sealed class BruteRubyRaider : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "ROAR_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 3, owner);
    }
}

/// <summary>
/// CrossbowRubyRaider: "RELOAD_MOVE" gains 3 move block. The amount is a literal in
/// MonsterMoveEffects.Apply ("CrossbowRubyRaider", "RELOAD_MOVE"), not a static model value.
/// </summary>
[ContentId("CROSSBOW_RUBY_RAIDER")]
public sealed class CrossbowRubyRaider : MonsterDef
{
    private const int ReloadBlock = 3;

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "RELOAD_MOVE")
            Kernel.GainBlock(ref x, owner, ReloadBlock, ValueProps.Move, fromCard: false);
    }
}

/// <summary>TrackerRubyRaider: "TRACK_MOVE" applies Frail 2 to the player.</summary>
[ContentId("TRACKER_RUBY_RAIDER")]
public sealed class TrackerRubyRaider : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "TRACK_MOVE")
            Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.FrailPower>(), 2, owner);
    }
}

/// <summary>
/// CubexConstruct: all three of its charging/blasting moves apply Strength 2 to itself. Its starting
/// 13 block and one Artifact stack exist before the root and are imported, not replayed here.
/// </summary>
[ContentId("CUBEX_CONSTRUCT")]
public sealed partial class CubexConstruct : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "CHARGE_UP_MOVE":
            case "REPEATER_BLAST_MOVE":
            case "REPEATER_BLAST_MOVE_2":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
                break;
        }
    }
}

/// <summary>
/// SlitheringStrangler: "CONSTRICT" applies Constrict 3 to the player; "THWACK" gains 5 move block
/// (literal in MonsterMoveEffects.Apply, "SlitheringStrangler", "THWACK").
/// </summary>
[ContentId("SLITHERING_STRANGLER")]
public sealed class SlitheringStrangler : MonsterDef
{
    private const int ThwackBlock = 5;

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "CONSTRICT":
                Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.ConstrictPower>(), 3, owner);
                break;
            case "THWACK":
                Kernel.GainBlock(ref x, owner, ThwackBlock, ValueProps.Move, fromCard: false);
                break;
        }
    }
}

/// <summary>Byrdonis: only "PECK_MOVE" and "SWOOP_MOVE"; its TerritorialPower is a starting power.</summary>
[ContentId("BYRDONIS")]
public sealed partial class Byrdonis : MonsterDef;

/// <summary>Inklet: only attacks; its SlipperyPower starts in the root.</summary>
[ContentId("INKLET")]
public sealed partial class Inklet : MonsterDef
{
}

/// <summary>
/// BygoneEffigy: "WAKE_MOVE" applies Strength 10 to itself. The amount is a literal in
/// <c>MonsterMoveEffects.Apply</c> ("BygoneEffigy", "WAKE_MOVE"); the native model exposes no
/// dynamic or static value for it. Sleep moves have no effect.
/// </summary>
[ContentId("BYGONE_EFFIGY")]
public sealed partial class BygoneEffigy : MonsterDef
{
    private const int WakeStrength = 10;

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "WAKE_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), WakeStrength, owner);
    }
}

/// <summary>
/// CorpseSlug: "GOOP_MOVE" applies Frail <c>GoopFrailAmt</c> to the player
/// (<c>MonsterMoveEffects</c> "CorpseSlug"). WHIP_SLAP and GLOMP are compiled attacks. Its
/// RavenousPower is a starting power; the private <c>_isRavenous</c> flag only selects animations.
/// </summary>
[ContentId("CORPSE_SLUG")]
public sealed partial class CorpseSlug : MonsterDef
{
    private const int GoopFrailAmt = 0;


    public override IReadOnlyList<string> StaticIntNames => ["GoopFrailAmt"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId == "GOOP_MOVE")
            Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.FrailPower>(), rules.StaticInts[GoopFrailAmt], owner);
    }
}

/// <summary>
/// Fogmog: "ILLUSION_MOVE" summons an EyeWithTeeth into the encounter's "illusion" slot
/// (<c>MonsterMoveEffects</c> "Fogmog", "ILLUSION_MOVE", a literal slot name); SWIPE_MOVE and
/// SWIPE_RANDOM_MOVE apply Strength 1 to itself. The native ILLUSION_MOVE runs before its attacks
/// (none) and returns without other effects. Fogmog only knows the summon table; the compiler
/// closes over it and compiles the EyeWithTeeth rule entry.
/// </summary>
[ContentId("FOGMOG")]
public sealed class Fogmog : MonsterDef
{
    public override IReadOnlyList<string> SpawnedMonsterIds => ["EYE_WITH_TEETH"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "ILLUSION_MOVE":
                Kernel.SpawnMonster(ref x, owner,
                    x.R.SpawnRulesId(Registry.MonsterId<EyeWithTeeth>()),
                    Kernel.SlotIndex(x.R, "illusion"), minion: false);
                break;
            case "SWIPE_MOVE":
            case "SWIPE_RANDOM_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 1, owner);
                break;
        }
    }
}

/// <summary>
/// EyeWithTeeth: a summoned illusion. <c>MonsterSpawnSupport.ApplyNativeEntrancePowers</c> applies
/// IllusionPower 1 (applier itself) then MinionPower 1 (no applier); the native AfterAddedToRoom
/// order is the same. "DISTRACT_MOVE" adds three Dazed to the discard pile
/// (<c>MonsterMoveEffects</c> "EyeWithTeeth"). Its IllusionPower owns the revive: the kernel appends
/// the synthetic REVIVE_MOVE node (<see cref="IllusionRevive"/>).
/// </summary>
[ContentId("EYE_WITH_TEETH")]
public sealed class EyeWithTeeth : MonsterDef
{
    private const int DistractDazed = 3;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("DAZED", 0)];

    public override bool IllusionRevive => true;

    public override void ApplyEntrancePowers(ref Exec x, byte owner)
    {
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.IllusionPower>(), 1, owner);
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.MinionPower>(), 1, Limits.NoOwner);
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId != "DISTRACT_MOVE")
            return;
        for (int index = 0; index < DistractDazed; index++)
            Kernel.AddGeneratedCard(ref x,
                Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Dazed>()), Pile.Discard, creator: Limits.NoOwner);
    }
}
