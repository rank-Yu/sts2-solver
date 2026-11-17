using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Monsters.Act1;

/// <summary>
/// Ceremonial Beast (CEREMONIAL_BEAST_BOSS): STAMP applies PlowPower (<c>PlowAmount</c>), PLOW and
/// CRUSH add Strength after their attack, and BEAST_CRY afflicts the player with RingingPower.
/// STUN_MOVE is the native must-perform-once stun state; the Plow removal forces the separate
/// synthetic STUNNED follow-up to BEAST_CRY_MOVE (see <see cref="PlowPower"/>).
/// </summary>
[ContentId("CEREMONIAL_BEAST")]
public sealed class CeremonialBeast : MonsterDef
{
    private const int PlowStrength = 0;
    private const int CrushStrength = 1;
    private const int PlowAmount = 2;

    public override IReadOnlyList<string> StaticIntNames => ["PlowStrength", "CrushStrength", "PlowAmount"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "STAMP_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<PlowPower>(),
                    rules.StaticInts[PlowAmount], owner);
                break;
            case "PLOW_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<StrengthPower>(),
                    rules.StaticInts[PlowStrength], owner);
                break;
            case "CRUSH_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<StrengthPower>(),
                    rules.StaticInts[CrushStrength], owner);
                break;
            case "BEAST_CRY_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<RingingPower>(), 1, owner);
                break;
        }
    }
}

/// <summary>
/// Vine Shambler (VINE_SHAMBLER_NORMAL): GRASPING_VINES_MOVE applies TangledPower 1 to the player
/// after its attack; SWIPE_MOVE and CHOMP_MOVE are attacks only.
/// </summary>
[ContentId("VINE_SHAMBLER")]
public sealed class VineShambler : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId == "GRASPING_VINES_MOVE")
        {
            Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                Registry.PowerId<TangledPower>(), 1, owner);
        }
    }
}

/// <summary>
/// Infested Prism (INFESTED_PRISMS_ELITE): PULSATE applies Vulnerable to the player and
/// VitalSparkPower to itself. Its entrance VitalSpark is already in the root;
/// JAB_MOVE and WHIRLWIND_MOVE are attacks only.
/// </summary>
[ContentId("INFESTED_PRISM")]
public sealed partial class InfestedPrism : MonsterDef
{
    private const int RadiateBlock = 0;
    private const int PulsateBlock = 1;
    private const int VitalSparkAmount = 2;

    public override IReadOnlyList<string> StaticIntNames => ["RadiateBlock", "PulsateBlock", "VitalSparkAmount"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "RADIATE_MOVE":
                Kernel.GainBlock(ref x, owner, rules.StaticInts[RadiateBlock], ValueProps.Move, fromCard: false);
                break;
            case "PULSATE_MOVE":
                Kernel.GainBlock(ref x, owner, rules.StaticInts[PulsateBlock], ValueProps.Move, fromCard: false);
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<VitalSparkPower>(),
                    rules.StaticInts[VitalSparkAmount], owner);
                break;
        }
    }
}

/// <summary>
/// Living Fog (LIVING_FOG_NORMAL): BLOAT summons <c>BloatAmount</c> Gas Bombs into the next free
/// encounter slots before its attack (<c>MonsterMoveEffects.ApplyBeforeAttack</c>); ADVANCED_GAS_MOVE
/// applies SmoggyPower 1 to the player after its attack.
/// </summary>
[ContentId("LIVING_FOG")]
public sealed class LivingFog : MonsterDef
{
    private const int BloatAmount = 0;

    public override IReadOnlyList<string> StaticIntNames => ["BloatAmount"];

    public override IReadOnlyList<string> SpawnedMonsterIds => ["GAS_BOMB"];

    public override void BeforeMove(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "BLOAT_MOVE")
            return;
        int count = rules.StaticInts[BloatAmount];
        if (!x.R.TrySpawnRulesId(Registry.MonsterId<GasBomb>(), out int spawnRules))
            throw new UnsupportedException("Living Fog 的生成闭包缺少 Gas Bomb。");
        for (int index = 0; index < count; index++)
        {
            string? slot = Kernel.NextFreeSlot(x.B, x.R);
            if (string.IsNullOrEmpty(slot))
                break;
            Kernel.SpawnMonster(ref x, owner, spawnRules, Kernel.SlotIndex(x.R, slot), minion: false);
            if (x.NeedsChoice)
                return;
        }
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId == "ADVANCED_GAS_MOVE")
        {
            Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                Registry.PowerId<SmoggyPower>(), 1, owner);
        }
    }
}

/// <summary>
/// Gas Bomb (summoned by Living Fog): gains MinionPower 1 on entrance and EXPLODE_MOVE attacks then
/// kills itself. Mirrors MonsterSpawnSupport.ApplyNativeEntrancePowers and the GasBomb EXPLODE case.
/// </summary>
[ContentId("GAS_BOMB")]
public sealed class GasBomb : MonsterDef
{
    public override void ApplyEntrancePowers(ref Exec x, byte owner)
        => Kernel.ApplyPower(ref x, owner, Registry.PowerId<MinionPower>(), 1, owner);

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "EXPLODE_MOVE")
            return;
        Span<byte> self = stackalloc byte[1];
        self[0] = owner;
        Kernel.Kill(ref x, self, force: true);
    }
}
