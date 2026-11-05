using Sts2Solver.Content.Monsters.Act1;
using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>
/// Contracts for the global <c>CombatManager.IsEnding</c> gate on power applications. Native
/// <c>PowerCmd.Apply</c> and <c>PowerCmd.ModifyAmount</c> return before touching a power while the
/// combat is ending: either the player has died (<c>PendingLoss</c>) or no living primary enemy is
/// left and no <c>ShouldStopCombatFromEnding</c> listener keeps the combat open.
/// </summary>
internal static class PowerEndingChecks
{
    private static void Assert(bool value, string message)
    {
        if (!value)
            throw new InvalidDataException("power-ending-checks: " + message);
    }

    internal static void Check()
    {
        CheckLethalMonsterSpawnHasNoMinionPower();
        CheckPlayerTurnThornsDeathBlocksLaterPower();
        CheckLastPrimaryEnemyDeathBlocksLaterPower();
        CheckModifyAmountGateAndRemoveExemption();
    }

    /// <summary>
    /// A Fabricator-style move that kills the player and then spawns a minion: the spawn still happens,
    /// but the MinionPower application (and any entrance powers) is refused while IsEnding is true.
    /// </summary>
    private static void CheckLethalMonsterSpawnHasNoMinionPower()
    {
        Assert(Registry.TryMonster("FSE_TEST_LETHAL_SPAWNER", out ushort spawner),
            "missing test spawner");
        Assert(Registry.TryMonster("NIBBIT", out ushort nibbit), "missing Nibbit");
        Assert(Registry.TryPower("MINION_POWER", out ushort minion), "missing MinionPower");

        Rules rules = SpawnerRules(spawner, nibbit);
        Battle battle = BattleWith(hp: 1);
        battle.Side = Side.Enemy;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.NextCombatId = 7;
        battle.Enemies[0] = new Enemy
        {
            Creature = new Creature { CurrentHp = 100, MaxHp = 100, Flags = CreatureFlags.Present },
            MonsterId = spawner,
            Rules = 0,
            Slot = Kernel.NoSlot,
            CombatId = 1,
            Move = 0,
            ForcedMove = byte.MaxValue,
            Flags = EnemyFlags.InCombat,
        };

        Exec exec = new(ref battle, rules, default);
        bool playerDied = Kernel.ExecuteMonsterMove(ref exec, Kernel.EnemyOwner(0));

        Assert(playerDied, "the lethal attack must report the player's death");
        Assert(battle.EnemyCount == 2 && battle.Enemies[1].Creature.IsAlive,
            "the post-lethal spawn must still add the minion");
        Assert(Kernel.GetAmount(battle, Kernel.EnemyOwner(1), minion) == 0,
            "MinionPower must not be applied while the combat is ending");
    }

    /// <summary>
    /// The player attacks a Thorns enemy, dies to the reflected damage, and the rest of the same card
    /// tries to add/stack Strength. Native PowerCmd.Apply/ModifyAmount are already gated by PendingLoss.
    /// </summary>
    private static void CheckPlayerTurnThornsDeathBlocksLaterPower()
    {
        Assert(Registry.TryPower("THORNS_POWER", out ushort thorns), "missing ThornsPower");
        Assert(Registry.TryPower("STRENGTH_POWER", out ushort strength), "missing StrengthPower");
        Assert(Registry.TryCard("FSE_TEST_ATTACK_THEN_STRENGTH", out ushort card),
            "missing test card");

        Rules rules = CardRules(card);
        Battle battle = BattleWith(hp: 3);
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0] = EnemyWith(hp: 50, Registry.MonsterId<Nibbit>());
        battle.PowerCount = 2;
        battle.Powers[0] = new Power { Id = strength, Owner = Kernel.EnemyOwner(0), Amount = 2 };
        battle.Powers[1] = new Power { Id = thorns, Owner = Kernel.EnemyOwner(0), Amount = 5 };
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, card));

        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);

        Assert(battle.Player.IsDead, "Thorns must kill the player during the card attack");
        Assert(battle.Outcome == Outcome.Defeat, "the card action must finish as a defeat");
        Assert(battle.Counter(Counter.EnergySpentThisTurn) == 1,
            "the card must continue after the lethal reflected damage");
        Assert(Kernel.GetAmount(battle, Kernel.EnemyOwner(0), strength) == 2,
            "a later Strength application must not add or stack while PendingLoss is set");
    }

    /// <summary>
    /// The player's card kills the last living primary enemy and then tries to add/stack Strength to
    /// the player. No primary enemy is alive, so IsEnding is true even though the player survives.
    /// </summary>
    private static void CheckLastPrimaryEnemyDeathBlocksLaterPower()
    {
        Assert(Registry.TryPower("STRENGTH_POWER", out ushort strength), "missing StrengthPower");
        Assert(Registry.TryCard("FSE_TEST_ATTACK_THEN_STRENGTH", out ushort card),
            "missing test card");

        Rules rules = CardRules(card);
        Battle battle = BattleWith(hp: 50);
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0] = EnemyWith(hp: 1, Registry.MonsterId<Nibbit>());
        battle.PowerCount = 1;
        battle.Powers[0] = new Power { Id = strength, Owner = Limits.PlayerOwner, Amount = 2 };
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, card));

        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);

        Assert(!battle.Player.IsDead, "the player must survive the last-enemy kill");
        Assert(!battle.Creature(Kernel.EnemyOwner(0)).IsAlive, "the only primary enemy must die");
        Assert(battle.Outcome == Outcome.Victory, "the card action must finish as a victory");
        Assert(battle.Counter(Counter.EnergySpentThisTurn) == 1,
            "the card must continue after the last enemy dies");
        Assert(Kernel.GetAmount(battle, Limits.PlayerOwner, strength) == 2,
            "a later Strength application must not add or stack after the last primary enemy dies");
    }

    /// <summary>
    /// The direct <c>PowerCmd.ModifyAmount</c> mirror shares the same ending gate, while the direct
    /// Remove-like <see cref="Kernel.SetPowerAmount"/> path must remain available for death cleanup.
    /// </summary>
    private static void CheckModifyAmountGateAndRemoveExemption()
    {
        Assert(Registry.TryPower("STRENGTH_POWER", out ushort strength), "missing StrengthPower");
        Battle battle = BattleWith(hp: 50);
        battle.PowerCount = 1;
        battle.Powers[0] = new Power { Id = strength, Owner = Limits.PlayerOwner, Amount = 2 };
        Assert(Kernel.IsEnding(battle), "the empty enemy roster must make IsEnding true");
        Assert(!Kernel.ModifyPowerAmount(ref battle, Limits.PlayerOwner, strength, 3),
            "ModifyPowerAmount must refuse while IsEnding is true");
        Assert(Kernel.GetAmount(battle, Limits.PlayerOwner, strength) == 2,
            "ModifyPowerAmount must not change the amount while IsEnding is true");
        Assert(!Kernel.ModifyPowerAmountAt(ref battle, 0, 5),
            "ModifyPowerAmountAt must refuse while IsEnding is true");
        Assert(Kernel.GetAmount(battle, Limits.PlayerOwner, strength) == 2,
            "ModifyPowerAmountAt must not change the amount while IsEnding is true");
        Kernel.SetPowerAmount(ref battle, Limits.PlayerOwner, strength, 0);
        Assert(Kernel.GetAmount(battle, Limits.PlayerOwner, strength) == 0,
            "Remove-like SetPowerAmount must not be gated");
    }

    private static Rules SpawnerRules(ushort spawner, ushort nibbit)
    {
        RulesDraft builder = new()
        {
            RootEnemyCount = 1,
            HasEncounter = true,
            EncounterSlots = ["first", "second"],
        };
        builder.SetMonster(0, new MonsterRules
        {
            MonsterId = spawner,
            Nodes =
            [
                new MonsterNode
                {
                    NativeId = "LETHAL_SPAWN_MOVE",
                    Kind = MonsterNodeKind.Move,
                    FollowUp = 0,
                    Attacks = [new MonsterAttack(999, 1)],
                },
                new MonsterNode
                {
                    NativeId = "STUNNED",
                    Kind = MonsterNodeKind.Move,
                    MustPerformOnceBeforeTransitioning = true,
                },
            ],
            InitialNode = 0,
            PendingInitialMoveNode = 0,
            StunnedNode = 1,
            ReviveNode = -1,
            InitialHpMin = 1,
            InitialHpMax = 1,
            StaticInts = [],
        });
        builder.AddSpawnableMonster(NibbitRules(nibbit));
        return builder.Build();
    }

    private static Rules CardRules(ushort card)
    {
        RulesDraft builder = new()
        {
            RootEnemyCount = 1,
            HasEncounter = true,
            EncounterSlots = ["first"],
            BaseMaxEnergy = 3,
        };
        builder.SetMonster(0, NibbitRules(Registry.MonsterId<Nibbit>()));
        builder.SetCard(card, 0, new CardStats
        {
            Type = CardType.Attack,
            Target = TargetType.AnyEnemy,
            Rarity = CardRarity.Common,
            EnergyCost = 0,
            CostsX = false,
            StarCost = -1,
            StarCostsX = false,
            Keywords = Keywords.None,
            MaxUpgradeLevel = 0,
            Vars = [],
        });
        return builder.Build();
    }

    private static MonsterRules NibbitRules(ushort nibbit) => new()
    {
        MonsterId = nibbit,
        Nodes =
        [
            new MonsterNode
            {
                NativeId = "BUTT_MOVE",
                Kind = MonsterNodeKind.Move,
                FollowUp = 0,
                Attacks = [new MonsterAttack(12, 1)],
            },
            new MonsterNode
            {
                NativeId = "STUNNED",
                Kind = MonsterNodeKind.Move,
                MustPerformOnceBeforeTransitioning = true,
            },
            new MonsterNode
            {
                NativeId = "REVIVE_MOVE",
                Kind = MonsterNodeKind.Move,
                MustPerformOnceBeforeTransitioning = true,
            },
        ],
        InitialNode = 0,
        PendingInitialMoveNode = 0,
        StunnedNode = 1,
        ReviveNode = 2,
        InitialHpMin = 17,
        InitialHpMax = 21,
        StaticInts = [],
        InitialAiRegisters = [2, 0],
    };

    private static Battle BattleWith(int hp)
    {
        Battle battle = default;
        battle.Round = 1;
        battle.PlayerTurn = 1;
        battle.Energy = 3;
        battle.MaxHandSize = 10;
        battle.Flags = BattleFlags.PlayerHooksActive;
        battle.Player = new Creature
        {
            CurrentHp = hp,
            MaxHp = 50,
            Flags = CreatureFlags.Present,
        };
        battle.RngStream(RngStream.Niche) = RngState.FromSerializable(0, 1, 2, 3, 4);
        return battle;
    }

    private static Enemy EnemyWith(int hp, ushort monsterId) => new()
    {
        Creature = new Creature { CurrentHp = hp, MaxHp = hp, Flags = CreatureFlags.Present },
        MonsterId = monsterId,
        Rules = 0,
        Slot = Kernel.NoSlot,
        CombatId = 1,
        Move = 0,
        ForcedMove = byte.MaxValue,
        Flags = EnemyFlags.InCombat,
    };

    [ContentId("FSE_TEST_LETHAL_SPAWNER")]
    public sealed class LethalSpawner : MonsterDef
    {
        public override IReadOnlyList<string> SpawnedMonsterIds => ["NIBBIT"];

        public override void ApplyMoveEffects(ref Exec x, byte owner, int moveNode)
        {
            if (Kernel.MonsterRules(ref x, owner).Nodes[moveNode].NativeId != "LETHAL_SPAWN_MOVE")
                return;
            if (!Registry.TryMonster("NIBBIT", out ushort nibbit)
                || !x.R.TrySpawnRulesId(nibbit, out int rulesId))
            {
                throw new UnsupportedException("测试用致命召唤师缺少 Nibbit 的生成规则。");
            }
            Kernel.SpawnMonster(ref x, owner, rulesId, Kernel.NoSlot, minion: true);
        }
    }

    [ContentId("FSE_TEST_ATTACK_THEN_STRENGTH")]
    public sealed class AttackThenStrength : CardDef
    {
        public override void OnPlay(ref Exec x)
        {
            Kernel.CardAttack(ref x, 5m, 1, AttackTargeting.Single, x.Target);
            // Marker proves that the card continued after its attack even when the attack killed the
            // player or the last enemy.
            x.B.SetCounter(Counter.EnergySpentThisTurn, 1);
            if (!x.NeedsChoice)
            {
                Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<StrengthPower>(), 1,
                    Limits.PlayerOwner);
                Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StrengthPower>(), 1,
                    Limits.PlayerOwner);
            }
        }
    }
}
