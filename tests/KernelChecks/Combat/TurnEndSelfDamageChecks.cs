using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>
/// Contracts for the content-declared turn-end self-damage and its threat projection. Every
/// turn-end-in-hand card and every self-damaging power is settled once against the same battle;
/// the projected HP after the enemy turn must equal the real EndTurn result. The ordering cases
/// cover block, per-hit HP-loss reduction (Tungsten Rod) and Buffer consumption.
/// </summary>
internal static class TurnEndSelfDamageChecks
{
    private const int Damage = 7;
    private const int FillerCount = 2;

    internal static void Check()
    {
        Registry.TryMonster("NIBBIT", out ushort nibbit);
        Registry.TryCard("DEFEND_IRONCLAD", out ushort filler);
        Registry.TryRelic("TUNGSTEN_ROD", out ushort tungsten);
        Registry.TryRelic("LIZARD_TAIL", out ushort lizardTail);
        Registry.TryPower("SANDPIT_POWER", out ushort sandpit);
        Registry.TryPower("BUFFER_POWER", out ushort buffer);
        Registry.TryPower("DIE_FOR_YOU_POWER", out ushort dieForYou);
        Registry.TryPower("THE_GAMBIT_POWER", out ushort gambit);
        Registry.TryPotion("FAIRY_IN_A_BOTTLE", out ushort fairy);

        Rules harmless = BuildRules(nibbit, filler, tungsten, lizardTail, attack: 0);
        Rules attacker = BuildRules(nibbit, filler, tungsten, lizardTail, attack: 10);
        ScoreRules score = BuildScore(harmless, sandpit, buffer, dieForYou, gambit, fairy, lizardTail);
        ScoreRules attackerScore = BuildScore(attacker, sandpit, buffer, dieForYou, gambit, fairy, lizardTail);
        Scorer scorer = new(score);
        Scorer attackerScorer = new(attackerScore);

        CheckCardDeclarations(scorer, harmless, nibbit, filler);
        CheckPowerDeclarations(scorer, harmless, nibbit);
        CheckOrdering(attackerScorer, attacker, nibbit, filler, tungsten, buffer);

        Console.WriteLine($"TURN_END_SELF_DAMAGE_CONTRACTS cards={CountTurnEndCards()} "
            + $"powers={CountDeclaredPowers(harmless, nibbit)}");
    }

    private static void CheckCardDeclarations(Scorer scorer, Rules rules, ushort nibbit, ushort filler)
    {
        ushort[] turnEndCards = TurnEndCards();
        foreach (ushort id in turnEndCards)
        {
            CardDef def = Registry.Card(id);
            Card card = new() { Id = id, BaseCost = -1, BaseStarCost = -1 };
            Battle declarationState = BuildBattle(nibbit, hand: [card, Filler(filler), Filler(filler)]);
            bool declares = def.TryGetTurnEndSelfDamage(declarationState, rules, card,
                out TurnEndSelfDamage declaration);
            Battle state = BuildBattle(nibbit, hand: [card, Filler(filler), Filler(filler)]);
            CheckProjectionMatchesSettlement(scorer, rules, state, declares ? declaration : null,
                $"{def.NativeId} 无格挡");

            Battle blocked = BuildBattle(nibbit, block: Damage,
                hand: [card, Filler(filler), Filler(filler)]);
            CheckProjectionMatchesSettlement(scorer, rules, blocked, declares ? declaration : null,
                $"{def.NativeId} 有格挡");
        }
    }

    private static void CheckPowerDeclarations(Scorer scorer, Rules rules, ushort nibbit)
    {
        int declared = 0;
        for (ushort id = 0; id < Registry.PowerCount; id++)
        {
            Battle declarationState = BuildBattle(nibbit);
            declarationState.PowerCount = 1;
            declarationState.Powers[0] = new Power { Id = id, Owner = Limits.PlayerOwner, Amount = Damage };
            if (!Registry.Power(id).TryGetTurnEndSelfDamage(declarationState, rules, 0,
                    out TurnEndSelfDamage declaration))
            {
                continue;
            }
            declared++;
            Battle state = BuildBattle(nibbit);
            state.PowerCount = 1;
            state.Powers[0] = new Power { Id = id, Owner = Limits.PlayerOwner, Amount = Damage };
            CheckProjectionMatchesSettlement(scorer, rules, state, declaration,
                $"{Registry.Power(id).NativeId} 无格挡");

            Battle blocked = BuildBattle(nibbit, block: Damage);
            blocked.PowerCount = 1;
            blocked.Powers[0] = new Power { Id = id, Owner = Limits.PlayerOwner, Amount = Damage };
            CheckProjectionMatchesSettlement(scorer, rules, blocked, declaration,
                $"{Registry.Power(id).NativeId} 有格挡");
        }
        if (declared == 0)
            throw new InvalidDataException("turn-end-self-damage: 没有发现任何已声明的自伤 Power。");
    }

    private static void CheckOrdering(Scorer scorer, Rules rules, ushort nibbit, ushort filler,
        ushort tungsten, ushort buffer)
    {
        Registry.TryCard("BURN", out ushort burn);
        Registry.TryCard("REGRET", out ushort regret);

        // Burn's 7 is blocked first (3 block leaves 4, Tungsten Rod reduces it to 3); the enemy's
        // 10 then spends no block and Tungsten reduces it to 9. Ignoring Burn would project 6.
        Battle burning = BuildBattle(nibbit, block: 3, hand: [new Card { Id = burn }]);
        AddRelic(ref burning, tungsten);
        int projected = ProjectLoss(scorer, in burning);
        int actual = EndTurnHpLoss(rules, ref burning);
        Check(projected == 12 && actual == 12,
            $"BURN 与攻击的格挡顺序不符：projected={projected} actual={actual}");

        // Regret 3 is unblockable: it must not spend the 5 block that the 10 attack consumes.
        Battle regretBattle = BuildBattle(nibbit, block: 5,
            hand: [new Card { Id = regret }, Filler(filler), Filler(filler)]);
        projected = ProjectLoss(scorer, in regretBattle);
        actual = EndTurnHpLoss(rules, ref regretBattle);
        Check(projected == 8 && actual == 8,
            $"REGRET 不应消耗格挡：projected={projected} actual={actual}");

        // Burn consumes the single Buffer before the enemy attack, so only the attack's 10 is lost.
        Battle buffered = BuildBattle(nibbit, hand: [new Card { Id = burn }]);
        buffered.PowerCount = 1;
        buffered.Powers[0] = new Power { Id = buffer, Owner = Limits.PlayerOwner, Amount = 1 };
        projected = ProjectLoss(scorer, in buffered);
        actual = EndTurnHpLoss(rules, ref buffered);
        Check(projected == 10 && actual == 10,
            $"BURN 应先消耗 Buffer：projected={projected} actual={actual}");
        Check(buffered.PowerCount == 0, "真实结束回合应消耗 Buffer");
    }

    private static void CheckProjectionMatchesSettlement(Scorer scorer, Rules rules, Battle state,
        TurnEndSelfDamage? declaration, string label)
    {
        int before = state.Player.CurrentHp;
        Scorer.ThreatProjection projection = scorer.ProjectHpAfterThreat(in state);
        int projectedLoss = before - projection.Hp;
        int actualLoss = EndTurnHpLoss(rules, ref state);
        Check(projectedLoss == actualLoss,
            $"{label}：投影掉血 {projectedLoss} != 实际掉血 {actualLoss}");
        if (declaration is { } value)
        {
            long expectedBlockable = (value.Props & ValueProps.Unblockable) == 0
                ? BlockableAmount(value)
                : 0;
            Check(projection.BlockableDamage == expectedBlockable,
                $"{label}：可格挡伤害 {projection.BlockableDamage} != 声明 {expectedBlockable}");
        }
        else
        {
            Check(projection.BlockableDamage == 0,
                $"{label}：未声明自伤的牌不应产生可格挡伤害");
        }
    }

    private static long BlockableAmount(in TurnEndSelfDamage damage)
    {
        long total = 0;
        for (int hit = 0; hit < damage.HitCount; hit++)
        {
            decimal amount = damage.DecrementPerHit ? damage.Amount - hit : damage.Amount;
            if (amount <= 0m)
                break;
            total += Math.Max(0, (int)Math.Floor(amount));
        }
        return total;
    }

    private static int EndTurnHpLoss(Rules rules, ref Battle battle)
    {
        int before = battle.Player.CurrentHp;
        StepResult result = Kernel.Step(ref battle, PlayerAction.EndTurn(), rules, out ChoiceRequest request);
        if (result == StepResult.NeedsChoice)
            throw new InvalidDataException($"turn-end-self-damage: EndTurn 意外请求选择 {request.Kind}。");
        return before - battle.Player.CurrentHp;
    }

    private static int ProjectLoss(Scorer scorer, in Battle battle)
    {
        Battle probe = battle;
        Scorer.ThreatProjection projection = scorer.ProjectHpAfterThreat(in probe);
        return battle.Player.CurrentHp - projection.Hp;
    }

    private static Battle BuildBattle(ushort nibbit, int block = 0, params Card[] hand)
    {
        Battle battle = new()
        {
            Round = 1,
            PlayerTurn = 1,
            Energy = 3,
            Side = Side.Player,
            Phase = PlayerPhase.Play,
            Flags = BattleFlags.PlayerHooksActive,
            MaxHandSize = 10,
            EnemyCount = 1,
            RosterCount = 1,
            Player = new Creature { CurrentHp = 1000, MaxHp = 1000, Flags = CreatureFlags.Present },
        };
        battle.Player.Block = block;
        battle.Enemies[0] = new Enemy
        {
            Creature = new Creature { CurrentHp = 1000, MaxHp = 1000, Flags = CreatureFlags.Present },
            MonsterId = nibbit,
            CombatId = 1,
            Move = 0,
            ForcedMove = byte.MaxValue,
            Flags = EnemyFlags.InCombat,
        };
        battle.RngStream(RngStream.Shuffle) = RngState.FromSerializable(0, 1, 2, 3, 4);
        battle.RngStream(RngStream.MonsterAi) = RngState.FromSerializable(0, 5, 6, 7, 8);
        foreach (Card card in hand)
            Piles.Append(ref battle, Pile.Hand, card);
        return battle;
    }

    private static void AddRelic(ref Battle battle, ushort relic)
    {
        battle.Relics[battle.RelicCount++] = new Relic { Id = relic };
    }

    private static Card Filler(ushort id) => new() { Id = id, BaseCost = 1, BaseStarCost = -1 };

    private static Rules BuildRules(ushort nibbit, ushort filler, ushort tungsten, ushort lizardTail,
        int attack)
    {
        RulesDraft builder = new()
        {
            BaseMaxEnergy = 3,
            HasEncounter = true,
            EncounterSlots = ["first"],
            RootEnemyCount = 1,
        };
        builder.SetMonster(0, new MonsterRules
        {
            MonsterId = nibbit,
            Nodes =
            [
                new MonsterNode
                {
                    NativeId = "NOOP_MOVE",
                    Kind = MonsterNodeKind.Move,
                    FollowUp = 0,
                    Attacks = attack > 0 ? [new MonsterAttack(attack, 1)] : [],
                },
                new MonsterNode
                {
                    NativeId = "STUNNED",
                    Kind = MonsterNodeKind.Move,
                    FollowUp = 0,
                    MustPerformOnceBeforeTransitioning = true,
                },
            ],
            InitialNode = 0,
            PendingInitialMoveNode = 0,
            InitialMoveLogged = true,
            StunnedNode = 1,
            ReviveNode = -1,
            InitialHpMin = 100,
            InitialHpMax = 100,
            StaticInts = [],
            InitialAiRegisters = [],
        });
        builder.SetRelicVars(tungsten, [1m]);
        builder.SetRelicVars(lizardTail, [50m]);
        builder.SetCard(filler, 0, SimpleStats(1));
        foreach (ushort id in TurnEndCards())
        {
            int vars = Registry.Card(id).VarNames.Count;
            builder.SetCard(id, 0, SimpleStats(vars));
        }
        return builder.Build();
    }

    private static CardStats SimpleStats(int vars) => new()
    {
        Type = CardType.Status,
        Target = TargetType.None,
        Rarity = CardRarity.Common,
        EnergyCost = -1,
        CostsX = false,
        StarCost = -1,
        StarCostsX = false,
        Keywords = Keywords.None,
        MaxUpgradeLevel = 0,
        Vars = vars == 0 ? [] : Enumerable.Repeat((decimal)Damage, vars).ToArray(),
    };

    private static ScoreRules BuildScore(Rules rules, ushort sandpit, ushort buffer, ushort dieForYou,
        ushort gambit, ushort fairy, ushort lizardTail) => new()
    {
        Rules = rules,
        Cards = new CardScore?[Registry.CardCount][],
        Powers = new PowerScore[Registry.PowerCount],
        PotionStrategicCost = [],
        InitialPlayerMaxHp = 1000,
        IsActEndingBoss = false,
        Act3BossStrategy = false,
        IgnoreLongTermRewards = true,
        TheftPolicy = null,
        InitialEnemyCount = 1,
        InitialPersistentBuffValue = 0,
        InitialEnemyStrengthSuppression = 0,
        InitialEnemyWeakTurns = 0,
        InitialRetainedAttackValue = 0,
        RootPotions = [],
        RootPotionSlotCount = 0,
        SandpitPowerId = sandpit,
        BufferPowerId = buffer,
        DieForYouPowerId = dieForYou,
        TheGambitPowerId = gambit,
        FairyInABottlePotionId = fairy,
        LizardTailRelicId = lizardTail,
    };

    private static ushort[] TurnEndCards()
    {
        List<ushort> cards = [];
        for (ushort id = 0; id < Registry.CardCount; id++)
        {
            if (Registry.Card(id).HasTurnEndInHandEffect)
                cards.Add(id);
        }
        return [.. cards];
    }

    private static int CountTurnEndCards() => TurnEndCards().Length;

    private static int CountDeclaredPowers(Rules rules, ushort nibbit)
    {
        int count = 0;
        for (ushort id = 0; id < Registry.PowerCount; id++)
        {
            Battle battle = BuildBattle(nibbit);
            battle.PowerCount = 1;
            battle.Powers[0] = new Power { Id = id, Owner = Limits.PlayerOwner, Amount = Damage };
            if (Registry.Power(id).TryGetTurnEndSelfDamage(battle, rules, 0, out _))
                count++;
        }
        return count;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException("turn-end-self-damage: " + message);
    }
}
