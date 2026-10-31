using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Monsters.Act1;

/// <summary>CalcifiedCultist: "INCANTATION_MOVE" applies Ritual <c>IncantationAmount</c> to itself.</summary>
[ContentId("CALCIFIED_CULTIST")]
public sealed class CalcifiedCultist : MonsterDef
{
    private const int IncantationAmount = 0;

    public override IReadOnlyList<string> StaticIntNames => ["IncantationAmount"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId == "INCANTATION_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.RitualPower>(),
                rules.StaticInts[IncantationAmount], owner);
    }
}

/// <summary>DampCultist: "INCANTATION_MOVE" applies Ritual <c>IncantationAmount</c> to itself.</summary>
[ContentId("DAMP_CULTIST")]
public sealed class DampCultist : MonsterDef
{
    private const int IncantationAmount = 0;

    public override IReadOnlyList<string> StaticIntNames => ["IncantationAmount"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId == "INCANTATION_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.RitualPower>(),
                rules.StaticInts[IncantationAmount], owner);
    }
}

/// <summary>
/// HauntedShip: "HAUNT_MOVE" applies Weak 3 to the player (<c>Debuff</c>) and adds five Dazed to the
/// discard pile (MonsterMoveEffects "HauntedShip", "HAUNT_MOVE", literals in the existing engine).
/// Its other two moves are pure attacks.
/// </summary>
[ContentId("HAUNTED_SHIP")]
public sealed class HauntedShip : MonsterDef
{
    private const int HauntWeak = 3;
    private const int HauntDazed = 5;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("DAZED", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId != "HAUNT_MOVE")
            return;
        Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.WeakPower>(), HauntWeak, owner);
        for (int index = 0; index < HauntDazed; index++)
            Kernel.AddGeneratedCard(ref x,
                Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Dazed>()), Pile.Discard, creator: Limits.NoOwner);
    }
}

/// <summary>
/// PunchConstruct: "READY_MOVE" gains 10 move block and "FAST_PUNCH_MOVE" applies Frail 1 to the
/// player. The values are literals in MonsterMoveEffects.Apply ("PunchConstruct", "READY_MOVE" /
/// "FAST_PUNCH_MOVE"), not static model values. Its starting Artifact is imported, not replayed here.
/// </summary>
[ContentId("PUNCH_CONSTRUCT")]
public sealed partial class PunchConstruct : MonsterDef
{
    private const int ReadyBlock = 10;

    private const int FastPunchFrail = 1;

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "READY_MOVE":
                Kernel.GainBlock(ref x, owner, ReadyBlock, ValueProps.Move, fromCard: false);
                break;
            case "FAST_PUNCH_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.FrailPower>(), FastPunchFrail, owner);
                break;
        }
    }
}

/// <summary>
/// Seapunk: "BUBBLE_BURP_MOVE" gains <c>BubbleBlock</c> move block and applies <c>BubbleStr</c>
/// Strength to itself (both are static model values captured for MonsterMoveEffects).
/// </summary>
[ContentId("SEAPUNK")]
public sealed class Seapunk : MonsterDef
{
    private const int BubbleBlock = 0;
    private const int BubbleStr = 1;

    public override IReadOnlyList<string> StaticIntNames => ["BubbleBlock", "BubbleStr"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "BUBBLE_BURP_MOVE")
            return;
        Kernel.GainBlock(ref x, owner, rules.StaticInts[BubbleBlock], ValueProps.Move, fromCard: false);
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
            rules.StaticInts[BubbleStr], owner);
    }
}

/// <summary>
/// SewerClam: "PRESSURIZE_MOVE" applies Strength 4 to itself (literal in MonsterMoveEffects.Apply,
/// "SewerClam", "PRESSURIZE_MOVE"). Its starting Plating is imported, not replayed here.
/// </summary>
[ContentId("SEWER_CLAM")]
public sealed partial class SewerClam : MonsterDef
{
    private const int PressurizeStrength = 4;

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "PRESSURIZE_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                PressurizeStrength, owner);
    }
}

/// <summary>SludgeSpinner: "OIL_SPRAY_MOVE" applies Weak 1 to the player; "RAGE_MOVE" applies Strength 3 to itself.</summary>
[ContentId("SLUDGE_SPINNER")]
public sealed class SludgeSpinner : MonsterDef
{
    private const int OilSprayWeak = 1;
    private const int RageStrength = 3;

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "OIL_SPRAY_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.WeakPower>(), OilSprayWeak, owner);
                break;
            case "RAGE_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                    RageStrength, owner);
                break;
        }
    }
}

/// <summary>
/// Toadpole: "SPIKEN_MOVE" applies Thorns 2 to itself and "SPIKE_SPIT_MOVE" removes 2 Thorns
/// (MonsterMoveEffects "Toadpole", literals <c>SpikenAmount = 2</c> in the decompiled model).
/// </summary>
[ContentId("TOADPOLE")]
public sealed partial class Toadpole : MonsterDef
{
    private const int SpikenAmount = 2;

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "SPIKEN_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.ThornsPower>(),
                    SpikenAmount, owner);
                break;
            case "SPIKE_SPIT_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.ThornsPower>(),
                    -SpikenAmount, owner);
                break;
        }
    }
}

/// <summary>
/// TwoTailedRat: the only first-act summoner whose private counters drive branch selection.
/// SCRATCH_MOVE, DISEASE_BITE_MOVE and SCREECH_MOVE decrement <c>_turnsUntilSummonable</c>;
/// CALL_FOR_BACKUP_MOVE spawns another rat in <c>LastFreeSlot</c> and raises every roster rat's
/// <c>_callForBackupCount</c> to (max + 1) (<c>MonsterMoveEffects</c> "TwoTailedRat"). The branch
/// weights are the literals in <c>BranchMonsterAi.GetBranchWeight</c> (0.75 for CALL_FOR_BACKUP,
/// 1/12 for the other three while the rat can summon, 1 otherwise) and its
/// <c>CanTwoTailedRatSummon</c> checks the turn counter, the 3-use cap, the next free slot and the
/// other rats' current intent. Registers: 0 = _turnsUntilSummonable, 1 = _callForBackupCount.
/// </summary>
[ContentId("TWO_TAILED_RAT")]
public sealed class TwoTailedRat : MonsterDef
{
    private const int TurnsUntilSummonable = 0;

    private const int CallForBackupCount = 1;
    private const float CallForBackupWeight = 0.75f;
    private const float OtherBranchWeightWhenSummonable = 1f / 12f;
    private const int CallForBackupLimit = 3;

    public override IReadOnlyList<string> SpawnedMonsterIds => ["TWO_TAILED_RAT"];

    public override IReadOnlyList<string> AiRegisterNames => ["_turnsUntilSummonable", "_callForBackupCount"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        string id = rules.Nodes[moveNode].NativeId;
        if (id is "SCRATCH_MOVE" or "DISEASE_BITE_MOVE" or "SCREECH_MOVE")
        {
            ref Enemy enemy = ref x.B.Enemy(owner);
            enemy.Ai[TurnsUntilSummonable] = enemy.Ai[TurnsUntilSummonable] - 1;
        }
        switch (id)
        {
            case "SCREECH_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.FrailPower>(), 1, owner);
                break;
            case "CALL_FOR_BACKUP_MOVE":
            {
                string? slot = Kernel.LastFreeSlot(x.B, x.R);
                if (!string.IsNullOrEmpty(slot))
                {
                    Kernel.SpawnMonster(ref x, owner,
                        x.R.SpawnRulesId(Registry.MonsterId<TwoTailedRat>()),
                        Kernel.SlotIndex(x.R, slot), minion: false);
                }
                ushort ratId = Registry.MonsterId<TwoTailedRat>();
                int next = int.MinValue;
                for (int position = 0; position < x.B.RosterCount; position++)
                {
                    byte rat = Kernel.EnemyOwner(x.B.Roster[position]);
                    if (x.R.MonsterRule(x.B.Enemy(rat).Rules).MonsterId == ratId)
                        next = Math.Max(next, x.B.Enemy(rat).Ai[CallForBackupCount] + 1);
                }
                if (next == int.MinValue)
                    throw new UnsupportedException("召唤备用时场上没有双尾鼠。");
                for (int position = 0; position < x.B.RosterCount; position++)
                {
                    byte rat = Kernel.EnemyOwner(x.B.Roster[position]);
                    if (x.R.MonsterRule(x.B.Enemy(rat).Rules).MonsterId == ratId)
                        x.B.Enemy(rat).Ai[CallForBackupCount] = next;
                }
                break;
            }
        }
    }

    public override float BranchWeight(ref Exec x, byte owner, int branchNode, int option, float baseWeight)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[branchNode].NativeId != "RAND")
            return baseWeight;
        bool canSummon = CanSummon(ref x, owner);
        string optionId = rules.Nodes[rules.Nodes[branchNode].Options[option].Target].NativeId;
        return optionId switch
        {
            "CALL_FOR_BACKUP_MOVE" => canSummon ? CallForBackupWeight : 0f,
            "SCREECH_MOVE" or "SCRATCH_MOVE" or "DISEASE_BITE_MOVE"
                => canSummon ? OtherBranchWeightWhenSummonable : 1f,
            _ => baseWeight,
        };
    }

    /// <summary>Mirrors <c>BranchMonsterAi.CanTwoTailedRatSummon</c>.</summary>
    private static bool CanSummon(ref Exec x, byte owner)
    {
        ref Enemy enemy = ref x.B.Enemy(owner);
        if (enemy.Ai[TurnsUntilSummonable] > 0 || enemy.Ai[CallForBackupCount] >= CallForBackupLimit)
            return false;
        if (string.IsNullOrEmpty(Kernel.NextFreeSlot(x.B, x.R)))
            return false;
        ushort ratId = Registry.MonsterId<TwoTailedRat>();
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte other = Kernel.EnemyOwner(x.B.Roster[position]);
            if (other == owner)
                continue;
            ref Enemy candidate = ref x.B.Enemy(other);
            if (x.R.MonsterRule(candidate.Rules).MonsterId == ratId
                && x.R.MonsterRule(candidate.Rules).Nodes[candidate.Move].NativeId == "CALL_FOR_BACKUP_MOVE")
                return false;
        }
        return true;
    }
}

/// <summary>
/// Native <c>GremlinMerc</c> (GREMLIN_MERC_NORMAL): room-entry SurprisePower 1 and ThieveryPower 20,
/// then the GIMME / DOUBLE_SMASH / HEHE cycle. Each move records the stolen gold after its attack;
/// DOUBLE_SMASH also applies Weak 2 and HEHE Strength 2.
/// </summary>
[ContentId("GREMLIN_MERC")]
public sealed partial class GremlinMerc : MonsterDef
{
    public override IReadOnlyList<string> SpawnedMonsterIds => ["FAT_GREMLIN", "SNEAKY_GREMLIN"];

    public override void ApplyEntrancePowers(ref Exec x, byte owner)
    {
        // GremlinMerc.AfterAddedToRoom: SurprisePower 1, then one ThieveryPower 20 per player with
        // the player creature as Target. Single-player has exactly one player.
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.SurprisePower>(), 1, owner);
        Kernel.ApplyPowerTargeted(ref x, owner, Limits.PlayerOwner,
            Registry.PowerId<Powers.ThieveryPower>(), 20, owner);
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "GIMME_MOVE":
                Kernel.RecordThievery(ref x, owner);
                break;
            case "DOUBLE_SMASH_MOVE":
                Kernel.RecordThievery(ref x, owner);
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.WeakPower>(), 2, owner);
                break;
            case "HEHE_MOVE":
                Kernel.RecordThievery(ref x, owner);
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
                break;
        }
    }
}

/// <summary>
/// Native <c>FatGremlin</c> (created by SurprisePower with slot "fat", or the flee-phase monster):
/// SPAWNED_MOVE does nothing in prediction, FLEE_MOVE escapes through
/// <c>SimulatedCombatState.CreatureEscaped</c>. Its HeistPower (transferred stolen gold) is removed by
/// the escape without being recovered.
/// </summary>
[ContentId("FAT_GREMLIN")]
public sealed class FatGremlin : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "FLEE_MOVE")
            Kernel.EscapeEnemy(ref x, owner);
    }
}

/// <summary>
/// Native <c>SneakyGremlin</c> (created by SurprisePower with slot "sneaky"): SPAWNED_MOVE does
/// nothing in prediction and TACKLE_MOVE is a plain compiled attack.
/// </summary>
[ContentId("SNEAKY_GREMLIN")]
public sealed class SneakyGremlin : MonsterDef;
