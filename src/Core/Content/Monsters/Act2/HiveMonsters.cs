using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Monsters.Act2;

/// <summary>Chomper: "SCREECH_MOVE" adds three Dazed to the discard pile; its Artifact is a starting power.</summary>
[ContentId("CHOMPER")]
public sealed partial class Chomper : MonsterDef
{
    private const int ScreechDazed = 3;


    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("DAZED", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId != "SCREECH_MOVE")
            return;
        for (int index = 0; index < ScreechDazed; index++)
            Kernel.AddGeneratedCard(ref x,
                Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Dazed>()), Pile.Discard, creator: Limits.NoOwner);
    }
}

/// <summary>
/// Entomancer: "PHEROMONE_SPIT_MOVE" adds one PersonalHive stack and one Strength while the hive is
/// below three, otherwise only Strength 2 (MonsterMoveEffects "Entomancer"). Its starting
/// PersonalHivePower is imported with the root.
/// </summary>
[ContentId("ENTOMANCER")]
public sealed partial class Entomancer : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId != "PHEROMONE_SPIT_MOVE")
            return;
        ushort hive = Registry.PowerId<Powers.PersonalHivePower>();
        if (Kernel.GetAmount(x.B, owner, hive) >= 3)
        {
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
            return;
        }
        Kernel.ApplyPower(ref x, owner, hive, 1, owner);
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 1, owner);
    }
}

/// <summary>Exoskeleton: "ENRAGE_MOVE" applies Strength 2 to itself; HardToKill is a starting power.</summary>
[ContentId("EXOSKELETON")]
public sealed partial class Exoskeleton : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId == "ENRAGE_MOVE")
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(), 2, owner);
    }
}

/// <summary>Myte: "TOXIC_MOVE" adds two Toxic to the hand; "SUCK_MOVE" applies <c>SuckStrength</c> Strength.</summary>
[ContentId("MYTE")]
public sealed class Myte : MonsterDef
{
    private const int SuckStrength = 0;

    public override IReadOnlyList<string> StaticIntNames => ["SuckStrength"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("TOXIC", 0)];

    public override int ResolveConditional(ref Exec x, byte owner, int branchNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[branchNode].NativeId != "INIT_MOVE")
            return -1;
        byte slot = x.B.Enemy(owner).Slot;
        if (slot == Kernel.NoSlot)
            throw new UnsupportedException("Myte 的 INIT_MOVE 没有遭遇槽位。");
        return x.R.EncounterSlots[slot] switch
        {
            "first" => rules.NodeIndex("TOXIC_MOVE"),
            "second" => rules.NodeIndex("SUCK_MOVE"),
            _ => throw new UnsupportedException($"Myte 的 INIT_MOVE 没有槽位 {x.R.EncounterSlots[slot]} 的分支。"),
        };
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "TOXIC_MOVE":
                for (int index = 0; index < 2; index++)
                {
                    Kernel.AddGeneratedCard(ref x,
                        Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Toxic>()), Pile.Hand, creator: Limits.NoOwner);
                }
                break;
            case "SUCK_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                    rules.StaticInts[SuckStrength], owner);
                break;
        }
    }
}

/// <summary>
/// Ovicopter: "LAY_EGGS_MOVE" summons up to three ToughEgg into the last free encounter slots as
/// minions (MonsterMoveEffects "Ovicopter"); "TENDERIZER_MOVE" applies Vulnerable 2;
/// "NUTRITIONAL_PASTE_MOVE" applies <c>NutritionalPasteStrengthAmount</c> Strength. The
/// SUMMON_BRANCH_STATE conditional lays eggs only while at most three enemies are alive
/// (BranchMonsterAi.ResolveConditional).
/// </summary>
[ContentId("OVICOPTER")]
public sealed class Ovicopter : MonsterDef
{
    private const int NutritionalPasteStrengthAmount = 0;

    public override IReadOnlyList<string> StaticIntNames => ["NutritionalPasteStrengthAmount"];

    public override IReadOnlyList<string> SpawnedMonsterIds => ["TOUGH_EGG"];

    public override int ResolveConditional(ref Exec x, byte owner, int branchNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[branchNode].NativeId != "SUMMON_BRANCH_STATE")
            return -1;
        int alive = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            if (x.B.Creature(Kernel.EnemyOwner(x.B.Roster[position])).IsAlive)
                alive++;
        }
        return rules.NodeIndex(alive <= 3 ? "LAY_EGGS_MOVE" : "NUTRITIONAL_PASTE_MOVE");
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "LAY_EGGS_MOVE":
                for (int index = 0; index < 3; index++)
                {
                    string? slot = Kernel.LastFreeSlot(x.B, x.R);
                    if (slot == null)
                        break;
                    Kernel.SpawnMonster(ref x, owner,
                        x.R.SpawnRulesId(Registry.MonsterId<ToughEgg>()),
                        Kernel.SlotIndex(x.R, slot), minion: true);
                }
                break;
            case "TENDERIZER_MOVE":
                Kernel.ApplyPowerFromMonster(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.VulnerablePower>(), 2, owner);
                break;
            case "NUTRITIONAL_PASTE_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                    rules.StaticInts[NutritionalPasteStrengthAmount], owner);
                break;
        }
    }
}

/// <summary>
/// ToughEgg: summoned by Ovicopter. Entrance applies HatchPower 2 during the enemy turn and 1
/// otherwise (MonsterSpawnSupport.ApplyNativeEntrancePowers). "HATCH_MOVE" removes every power except
/// Minion, rolls its new HP from the Niche stream in [HatchlingMinHp, HatchlingMaxHp] and sets both
/// current and max HP (MonsterMoveEffects "ToughEgg"). The follow-up NIBBLE_MOVE is a plain attack.
/// </summary>
[ContentId("TOUGH_EGG")]
public sealed partial class ToughEgg : MonsterDef
{
    private const int HatchlingMinHp = 0;
    private const int HatchlingMaxHp = 1;

    public override IReadOnlyList<string> StaticIntNames => ["HatchlingMinHp", "HatchlingMaxHp"];

    // MonsterValueReader/fingerprint only: HATCH_MOVE clears itself; the native IsHatched gate in
    // AfterAddedToRoom is unreachable for a freshly summoned egg (it enters unhatched).
    public override IReadOnlyList<string> InertMonsterIntNames => ["_isHatched", "_hatched"];

    public override void ApplyEntrancePowers(ref Exec x, byte owner)
    {
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.HatchPower>(),
            x.B.Side == Side.Enemy ? 2 : 1, owner);
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "HATCH_MOVE")
            return;
        for (int index = x.B.PowerCount - 1; index >= 0; index--)
        {
            if (x.B.Powers[index].Owner != owner)
                continue;
            if (Registry.Power(x.B.Powers[index].Id).NativeId == "MINION_POWER")
                continue;
            Kernel.SetPowerAmount(ref x.B, owner, x.B.Powers[index].Id, 0);
        }
        int hp = x.B.RngStream(RngStream.Niche).NextInt(
            rules.StaticInts[HatchlingMinHp], rules.StaticInts[HatchlingMaxHp] + 1);
        ref Creature creature = ref x.B.Creature(owner);
        creature.MaxHp = hp;
        creature.CurrentHp = hp;
    }
}

/// <summary>
/// SpinyToad: "PROTRUDING_SPIKES_MOVE" applies Thorns 5; "SPIKE_EXPLOSION_MOVE" applies Thorns -5.
/// The private <c>_isSpiny</c> flag only selects animations.
/// </summary>
[ContentId("SPINY_TOAD")]
public sealed class SpinyToad : MonsterDef
{
    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        switch (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId)
        {
            case "PROTRUDING_SPIKES_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.ThornsPower>(), 5, owner);
                break;
            case "SPIKE_EXPLOSION_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.ThornsPower>(), -5, owner);
                break;
        }
    }
}

/// <summary>
/// TheObscura: "ILLUSION_MOVE" summons a Parafright into the encounter's "illusion" slot
/// (MonsterMoveEffects "TheObscura"); "SAIL_MOVE" applies Strength 3 to every living enemy including
/// itself; "HARDENING_STRIKE_MOVE" gains <c>HardeningStrikeBlock</c> move block. The private
/// <c>_hasSummoned</c> flag only selects animations.
/// </summary>
[ContentId("THE_OBSCURA")]
public sealed class TheObscura : MonsterDef
{
    private const int HardeningStrikeBlock = 0;

    public override IReadOnlyList<string> StaticIntNames => ["HardeningStrikeBlock"];

    // MonsterMoveEffects writes it; only the native animator reads it.
    public override IReadOnlyList<string> InertMonsterIntNames => ["_hasSummoned"];

    public override IReadOnlyList<string> SpawnedMonsterIds => ["PARAFRIGHT"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "ILLUSION_MOVE":
                Kernel.SpawnMonster(ref x, owner,
                    x.R.SpawnRulesId(Registry.MonsterId<Parafright>()),
                    Kernel.SlotIndex(x.R, "illusion"), minion: false);
                break;
            case "SAIL_MOVE":
                for (int position = 0; position < x.B.RosterCount; position++)
                {
                    byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
                    if (x.B.Creature(enemy).IsAlive)
                        Kernel.ApplyPower(ref x, enemy, Registry.PowerId<Powers.StrengthPower>(), 3, owner);
                }
                break;
            case "HARDENING_STRIKE_MOVE":
                Kernel.GainBlock(ref x, owner, rules.StaticInts[HardeningStrikeBlock],
                    ValueProps.Move, fromCard: false);
                break;
        }
    }
}

/// <summary>
/// Parafright: summoned illusion. <c>MonsterSpawnSupport.ApplyNativeEntrancePowers</c> gives it
/// IllusionPower 1 (applier itself) and MinionPower 1 (no applier); its only move SLAM_MOVE is a plain
/// attack. IllusionPower owns the revive, so the kernel appends the synthetic REVIVE_MOVE node.
/// </summary>
[ContentId("PARAFRIGHT")]
public sealed class Parafright : MonsterDef
{
    public override bool IllusionRevive => true;

    public override void ApplyEntrancePowers(ref Exec x, byte owner)
    {
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.IllusionPower>(), 1, owner);
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.MinionPower>(), 1, Limits.NoOwner);
    }
}

/// <summary>
/// Tunneler: "BURROW_MOVE" applies BurrowedPower 1 and <c>BlockGain</c> move block
/// (MonsterMoveEffects "Tunneler"). BurrowedPower owns the retained block, the block-break stun and the
/// forced BITE_MOVE follow-up. The private <c>_isStunned</c> flag only selects animations.
/// </summary>
[ContentId("TUNNELER")]
public sealed class Tunneler : MonsterDef
{
    private const int BlockGain = 0;

    public override IReadOnlyList<string> StaticIntNames => ["BlockGain"];

    // TriggeredPowerSupport/MonsterMoveEffects write it; only the native animator reads it.
    public override IReadOnlyList<string> InertMonsterIntNames => ["_isStunned"];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[moveNode].NativeId != "BURROW_MOVE")
            return;
        Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.BurrowedPower>(), 1, owner);
        Kernel.GainBlock(ref x, owner, rules.StaticInts[BlockGain], ValueProps.Move, fromCard: false);
    }
}

/// <summary>
/// TheInsatiable: "SALIVATE_MOVE" applies <c>SalivateStrength</c> Strength to itself;
/// "LIQUIFY_GROUND_MOVE" applies 4 targeted Sandpit to the player and adds three FranticEscape to a
/// random draw position and three to a random discard position (<c>P/MonsterMoveEffects.cs:938</c>,
/// the existing engine's compensation). Attacks and branch weights come from the compiled state
/// machine; THRASH/LUNGING_BITE have no extra effect.
/// </summary>
[ContentId("THE_INSATIABLE")]
public sealed class TheInsatiable : MonsterDef
{
    private const int LiquifySandpit = 1;
    private const int LiquifyCardsPerPile = 3;

    public override IReadOnlyList<string> StaticIntNames => ["SalivateStrength", "SandpitAmount"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("FRANTIC_ESCAPE", 0)];

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "SALIVATE_MOVE":
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                    rules.StaticInts[0], owner);
                break;
            case "LIQUIFY_GROUND_MOVE":
                Kernel.ApplyPowerTargeted(ref x, owner, Limits.PlayerOwner,
                    Registry.PowerId<Powers.SandpitPower>(), rules.StaticInts[LiquifySandpit], owner);
                for (int index = 0; index < LiquifyCardsPerPile; index++)
                {
                    Kernel.AddGeneratedCard(ref x,
                        Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.FranticEscape>()),
                        Pile.Draw, PilePosition.Random, creator: Limits.NoOwner);
                }
                for (int index = 0; index < LiquifyCardsPerPile; index++)
                {
                    Kernel.AddGeneratedCard(ref x,
                        Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.FranticEscape>()),
                        Pile.Discard, PilePosition.Random, creator: Limits.NoOwner);
                }
                break;
        }
    }
}

/// <summary>
/// KnowledgeDemon: "CURSE_OF_KNOWLEDGE_MOVE" asks the player to choose one of two curse cards and
/// immediately applies its power (<c>P/KnowledgeDemonChoiceSupport.cs</c>); the native
/// <c>_curseOfKnowledgeCounter</c> selects the option set, gates the PONDER branch and is advanced
/// after the choice. "PONDER_MOVE" heals 30 and gains <c>PonderStrength</c>
/// (<c>MonsterMoveEffects.Apply</c>); SLAP and KNOWLEDGE_OVERWHELMING are pure attacks. The branch
/// after PONDER reads the counter (<c>BranchMonsterAi.Advance</c>): below three it returns to CURSE,
/// otherwise SLAP.
/// </summary>
[ContentId("KNOWLEDGE_DEMON")]
public sealed class KnowledgeDemon : MonsterDef
{
    private const int CurseSetCount = 3;
    private const int PonderHeal = 30;
    private const int DisintegrationBase = 6;
    private const int MindRotAmount = 1;
    private const int SlothAmount = 3;
    private const int WasteAwayAmount = 1;

    public override IReadOnlyList<string> StaticIntNames => ["PonderStrength"];

    // _monsterIntStates: the native KnowledgeDemon._curseOfKnowledgeCounter, read by the option set,
    // the PONDER conditional branch and the choice resolution.
    public override IReadOnlyList<string> AiRegisterNames => ["_curseOfKnowledgeCounter"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards =>
        [("DISINTEGRATION", 0), ("MIND_ROT", 0), ("SLOTH", 0), ("WASTE_AWAY", 0)];

    public override int ResolveConditional(ref Exec x, byte owner, int branchNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        if (rules.Nodes[branchNode].NativeId != "CurseOfKnowledgeBranch")
            return -1;
        int counter = x.B.Enemy(owner).Ai[0];
        return rules.NodeIndex(counter < CurseSetCount ? "CURSE_OF_KNOWLEDGE_MOVE" : "SLAP_MOVE");
    }

    public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
    {
        MonsterRules rules = Kernel.MonsterRules(ref x, owner);
        switch (rules.Nodes[moveNode].NativeId)
        {
            case "CURSE_OF_KNOWLEDGE_MOVE":
                ResolveCurseChoice(ref x, owner);
                break;
            case "PONDER_MOVE":
                Kernel.Heal(ref x, owner, PonderHeal);
                if (x.NeedsChoice)
                    return;
                Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.StrengthPower>(),
                    rules.StaticInts[0], owner);
                break;
        }
    }

    private static void ResolveCurseChoice(ref Exec x, byte owner)
    {
        int counter = x.B.Enemy(owner).Ai[0];
        if ((uint)counter >= (uint)CurseSetCount)
            throw new UnsupportedException($"知识恶魔诅咒计数超出范围：{counter}。");

        Span<ushort> optionIds = stackalloc ushort[2];
        optionIds[0] = Registry.CardId<Cards.Status.Disintegration>();
        optionIds[1] = counter switch
        {
            0 => Registry.CardId<Cards.Status.MindRot>(),
            1 => Registry.CardId<Cards.Status.Sloth>(),
            _ => Registry.CardId<Cards.Status.WasteAway>(),
        };
        Span<Card> options = stackalloc Card[2];
        for (int index = 0; index < options.Length; index++)
            options[index] = Kernel.CreateCard(x.R, optionIds[index]);
        if (!Kernel.ChooseOption(ref x, ChoiceEffect.ApplyKnowledgeCurse, options, out int selected))
            return;

        ushort card = optionIds[selected];
        if (card == Registry.CardId<Cards.Status.Disintegration>())
        {
            Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.DisintegrationPower>(), DisintegrationBase + (x.R.Mode == CombatMode.Dread ? 2 : 0) + counter, Limits.PlayerOwner);
        }
        else if (card == Registry.CardId<Cards.Status.MindRot>())
        {
            Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.MindRotPower>(), MindRotAmount, Limits.PlayerOwner);
        }
        else if (card == Registry.CardId<Cards.Status.Sloth>())
        {
            Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.SlothPower>(), SlothAmount, Limits.PlayerOwner);
        }
        else if (card == Registry.CardId<Cards.Status.WasteAway>())
        {
            Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.WasteAwayPower>(), WasteAwayAmount, Limits.PlayerOwner);
        }
        else
        {
            throw new UnsupportedException($"知识恶魔诅咒 {Registry.Card(card).NativeId} 没有模拟效果。");
        }
        x.B.Enemy(owner).Ai[0] = counter + 1;
    }
}
