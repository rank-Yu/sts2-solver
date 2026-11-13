using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Monsters.Act3;

/// <summary>
/// DevotedSculptor: "FORBIDDEN_INCANTATION_MOVE" applies RitualPower with the private
/// <c>_ritualGain</c> field (9), and SAVAGE_MOVE is a plain attack
/// (<c>MonsterMoveEffects</c> "DevotedSculptor").
/// </summary>
[ContentId("DEVOTED_SCULPTOR")]
public sealed class DevotedSculptor : MonsterDef
{
    private const int RitualGain = 0;

    public override IReadOnlyList<string> StaticIntNames => ["_ritualGain"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "FORBIDDEN_INCANTATION_MOVE")
            return;
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.RitualPower>(),
            rules.StaticInts[RitualGain], Limits.NoOwner);
    }
}

/// <summary>
/// FlailKnight: WAR_CHANT applies Strength 3 to itself; FLAIL_MOVE and RAM_MOVE are plain attacks
/// (<c>MonsterMoveEffects</c> "FlailKnight").
/// </summary>
[ContentId("FLAIL_KNIGHT")]
public sealed class FlailKnight : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "WAR_CHANT")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 3, owner);
    }
}

/// <summary>
/// SpectralKnight: "HEX" applies HexPower 2 to the player; SOUL_SLASH and SOUL_FLAME are plain attacks
/// (<c>MonsterMoveEffects</c> "SpectralKnight").
/// </summary>
[ContentId("SPECTRAL_KNIGHT")]
public sealed class SpectralKnight : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId != "HEX")
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.HexPower>(), 2, owner);
    }
}

/// <summary>
/// GlobeHead: SHOCKING_SLAP applies Frail 2 to the player, GALVANIC_BURST applies Strength 2 to
/// itself; THUNDER_STRIKE is a plain attack (<c>MonsterMoveEffects</c> "GlobeHead"). Its starting
/// GalvanicPower 6 (8 on DeadlyEnemies) comes from the root powers, and Galvanized is normalized by
/// the kernel.
/// </summary>
[ContentId("GLOBE_HEAD")]
public sealed partial class GlobeHead : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "SHOCKING_SLAP":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.FrailPower>(), 2, owner);
                break;
            case "GALVANIC_BURST":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
                break;
        }
    }
}

/// <summary>
/// FrogKnight: FOR_THE_QUEEN applies Strength 5, TONGUE_LASH applies Frail 2, BEETLE_CHARGE marks
/// <c>_hasBeetleCharged</c>; HALF_HEALTH picks TONGUE_LASH while charged or at half HP or more and
/// BEETLE_CHARGE otherwise (<c>MonsterMoveEffects</c> and <c>BranchMonsterAi.ResolveConditional</c>).
/// The starting PlatingPower 15 (19 on ToughEnemies) is a root power.
/// </summary>
[ContentId("FROG_KNIGHT")]
public sealed partial class FrogKnight : MonsterDef
{
    private const int HasBeetleCharged = 0;

    public override IReadOnlyList<string> AiRegisterNames => ["_hasBeetleCharged"];

    public override int ResolveConditional(ref Exec x, byte owner, int branchNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[branchNode].NativeId != "HALF_HEALTH")
            return -1;
        ref readonly Creature creature = ref x.B.Creature(owner);
        bool charged = x.B.Enemy(owner).Ai[HasBeetleCharged] != 0;
        return charged || creature.CurrentHp >= creature.MaxHp / 2
            ? rules.NodeIndex("TONGUE_LASH")
            : rules.NodeIndex("BEETLE_CHARGE");
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "FOR_THE_QUEEN":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 5, owner);
                break;
            case "TONGUE_LASH":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.FrailPower>(), 2, owner);
                break;
            case "BEETLE_CHARGE":
                // MonsterMoveEffects.Apply sets the private flag before the switch, after the attack.
                x.B.Enemy(owner).Ai[HasBeetleCharged] = 1;
                break;
        }
    }
}

/// <summary>
/// MechaKnight: FLAMETHROWER_MOVE adds four Burn to the player's hand, WINDUP_MOVE gains 15 Block and
/// Strength 5 (<c>MonsterMoveEffects</c> "MechaKnight"). The starting Artifact 3 is a root power.
/// </summary>
[ContentId("MECHA_KNIGHT")]
public sealed partial class MechaKnight : MonsterDef
{
    private const int FlamethrowerBurn = 4;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("BURN", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "FLAMETHROWER_MOVE":
                for (int index = 0; index < FlamethrowerBurn; index++)
                {
                    Kernel.AddGeneratedCard(ref x,
                        Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Burn>()), Pile.Hand, creator: Limits.NoOwner);
                }
                break;
            case "WINDUP_MOVE":
                Kernel.GainBlock(ref x, owner, 15, ValueProps.Move, fromCard: false);
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 5, owner);
                break;
        }
    }
}

/// <summary>
/// OwlMagistrate: JUDICIAL_FLIGHT applies SoarPower 1, VERDICT applies Vulnerable 4 and removes Soar
/// (<c>MonsterMoveEffects</c> "OwlMagistrate"). Soar's damage reduction has no existing-engine
/// mirror, so <see cref="Powers.SoarPower"/> stays inert.
/// </summary>
[ContentId("OWL_MAGISTRATE")]
public sealed class OwlMagistrate : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "JUDICIAL_FLIGHT":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.SoarPower>(), 1, owner);
                break;
            case "VERDICT":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.VulnerablePower>(), 4, owner);
                Kernel.SetPowerAmount(ref x.B, owner, Registry.PowerId<Powers.SoarPower>(), 0);
                break;
        }
    }
}

/// <summary>
/// Queen: PUPPET_STRINGS applies ChainsOfBindingPower 3, YOU_ARE_MINE applies Frail/Weak/Vulnerable
/// 99, BURN_BRIGHT_FOR_ME applies Strength 1 to every other enemy then gains 20 Block, ENRAGE applies
/// Strength 2 (<c>MonsterMoveEffects</c> "Queen"). YOURE_MINE_NOW_BRANCH and
/// BURN_BRIGHT_FOR_ME_BRANCH pick OFF_WITH_YOUR_HEAD once the Torch Head Amalgam died and
/// BURN_BRIGHT_FOR_ME otherwise. The amalgam's death sets <c>_hasAmalgamDied</c> and, while the
/// Queen's current move is BURN_BRIGHT_FOR_ME, forces ENRAGE_MOVE
/// (<c>AfterDeathMirrors.HandleQueen</c>, <c>BranchMonsterAi.ResolveConditional</c>).
/// </summary>
[ContentId("QUEEN")]
public sealed class Queen : MonsterDef
{
    private const int HasAmalgamDied = 0;

    public override IReadOnlyList<string> AiRegisterNames => ["_hasAmalgamDied"];

    public override int ResolveConditional(ref Exec x, byte owner, int branchNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        string native = rules.Nodes[branchNode].NativeId;
        if (native is not ("YOURE_MINE_NOW_BRANCH" or "BURN_BRIGHT_FOR_ME_BRANCH"))
            return -1;
        return x.B.Enemy(owner).Ai[HasAmalgamDied] != 0
            ? rules.NodeIndex("OFF_WITH_YOUR_HEAD_MOVE")
            : rules.NodeIndex("BURN_BRIGHT_FOR_ME_MOVE");
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "PUPPET_STRINGS_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.ChainsOfBindingPower>(), 3, owner);
                break;
            case "DREAD_INITIAL_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.ChainsOfBindingPower>(), 3, owner);
                goto case "YOU_ARE_MINE_MOVE";
            case "YOU_ARE_MINE_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.FrailPower>(), 99, owner);
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.WeakPower>(), 99, owner);
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.VulnerablePower>(), 99, owner);
                break;
            case "BURN_BRIGHT_FOR_ME_MOVE":
                for (int position = 0; position < x.B.RosterCount; position++)
                {
                    byte other = Kernel.EnemyOwner(x.B.Roster[position]);
                    if (other != owner)
                        Kernel.ApplyPower(ref x, other, Registry.PowerId<Powers.StrengthPower>(),
                            x.R.Mode == CombatMode.Dread ? 2 : 1, owner);
                }
                Kernel.GainBlock(ref x, owner, 20, ValueProps.Move, fromCard: false);
                break;
            case "ENRAGE_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
                break;
        }
    }

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (creature < Limits.FirstEnemyOwner
            || creature == self.Owner
            || x.B.Enemy(creature).MonsterId != Registry.MonsterId<TorchHeadAmalgam>()
            || !x.B.Creature(self.Owner).IsAlive)
        {
            return;
        }
        x.B.Enemy(self.Owner).Ai[HasAmalgamDied] = 1;
        MonsterRules rules = Kernel.MonsterRules(ref x, self.Owner);
        if (rules.Nodes[x.B.Enemy(self.Owner).Move].NativeId == "BURN_BRIGHT_FOR_ME_MOVE")
        {
            Kernel.ForceMonsterMove(ref x, self.Owner, rules.NodeIndex("ENRAGE_MOVE"));
        }
    }
}

/// <summary>
/// MagiKnight: DAMPEN_MOVE runs <c>SimulatedCombatState.ApplyDampen</c> — one DampenPower per caster
/// (tracked as a bitmask; the power is applied only for the first caster) downgrades every upgraded
/// combat card once. POWER_SHIELD_MOVE and PREP_MOVE gain <c>PowerShieldBlock</c>; the rest are plain
/// attacks (<c>MonsterMoveEffects</c> "MagiKnight"). When a caster dies, its bit is cleared and the
/// last death removes DampenPower and restores every downgraded card
/// (<c>RemoveDampenCaster</c>, <c>AfterDeathMirrors.HandleDampen</c>).
/// </summary>
[ContentId("MAGI_KNIGHT")]
public sealed class MagiKnight : MonsterDef
{
    private const int PowerShieldBlockStat = 0;
    private const int LastKnownUpgradeLevel = 2;

    public override IReadOnlyList<string> StaticIntNames => ["PowerShieldBlock"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "POWER_SHIELD_MOVE":
            case "PREP_MOVE":
                Kernel.GainBlock(ref x, owner, rules.StaticInts[PowerShieldBlockStat],
                    ValueProps.Move, fromCard: false);
                break;
            case "DAMPEN_MOVE":
                ApplyDampen(ref x, owner);
                break;
        }
    }

    private static void ApplyDampen(ref Exec x, byte owner)
    {
        int bit = 1 << (owner - Limits.FirstEnemyOwner);
        int mask = x.B.Counter(Counter.DampenCasterMask);
        if ((mask & bit) != 0)
            return;
        x.B.SetCounter(Counter.DampenCasterMask, mask | bit);
        if (mask != 0)
            return;
        if (Registry.TryPower("DAMPEN_POWER", out ushort dampen))
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, dampen, 1, owner);
        for (int position = 0; position < x.B.CardCount; position++)
        {
            if (x.B.Cards[position].Upgrade != 0)
                Kernel.DowngradeCard(ref x, position);
        }
    }

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (creature != self.Owner || wasRemovalPrevented)
            return;
        int bit = 1 << (self.Owner - Limits.FirstEnemyOwner);
        int mask = x.B.Counter(Counter.DampenCasterMask);
        if ((mask & bit) == 0)
            return;
        mask &= ~bit;
        x.B.SetCounter(Counter.DampenCasterMask, mask);
        if (mask != 0)
            return;
        if (Registry.TryPower("DAMPEN_POWER", out ushort dampen))
            Kernel.SetPowerAmount(ref x.B, Limits.PlayerOwner, dampen, 0);
        Kernel.RestoreDampenedCards(ref x);
    }
}
