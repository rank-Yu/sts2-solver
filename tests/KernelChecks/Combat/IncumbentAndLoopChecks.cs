using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Quality;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>
/// Small closed-world contracts for the pruning certificate and the per-turn no-progress boundary.
/// The cards here are test-only content: a self-drawing block loop, a self-drawing poison loop, a
/// self-drawing Strength loop and a plain one-damage attack.
/// </summary>
internal static class IncumbentAndLoopChecks
{
    private static void Assert(bool value, string message)
    {
        if (!value)
            throw new InvalidDataException("incumbent-and-loop-checks: " + message);
    }

    internal static void Check(Rules fixtureRules)
    {
        Assert(BeamSolver.TurnProgressStateSize <= 16,
            $"route turn-progress metadata must stay within 16 bytes, actual={BeamSolver.TurnProgressStateSize}");
        CheckPruningBounds(fixtureRules);
        CheckLoopGuard();
        Console.WriteLine("INCUMBENT_AND_LOOP contracts=pass");
    }

    private static void CheckPruningBounds(Rules fixtureRules)
    {
        Assert(Registry.TryCard("NOT_YET", out ushort notYet), "missing NOT_YET");
        Assert(Registry.TryCard("FEED", out ushort feed), "missing FEED");
        Assert(Registry.TryPotion("FAIRY_IN_A_BOTTLE", out ushort fairy), "missing fairy");
        Assert(Registry.TryPotion("FRUIT_JUICE", out ushort fruit), "missing fruit juice");
        Assert(Registry.TryRelic("LIZARD_TAIL", out ushort lizard), "missing lizard tail");
        Assert(Registry.TryRelic("DRAGON_FRUIT", out ushort dragonFruit), "missing dragon fruit");
        Assert(Registry.TryRelic("LUCKY_FYSH", out ushort luckyFysh), "missing lucky fysh");
        Assert(Registry.TryCard("HAND_OF_GREED", out ushort handOfGreed), "missing Hand of Greed");
        Assert(Registry.TryCard("BURST", out ushort burst), "missing BURST");
        Assert(Registry.TryRelic("BURNING_STICKS", out ushort burningSticks),
            "missing burning sticks");

        Rules rules = BuildBoundRules(fixtureRules, notYet, feed, fruit, dragonFruit);
        BoundFeatures known = new(KnownNativeConfig(notYet, feed, lizard, dragonFruit, fairy, fruit));
        BoundFeatures component = new(ComponentConfig(notYet, feed, lizard, dragonFruit, fairy, fruit));

        foreach (BoundFeatures bound in new[] { known, component })
        {
            Battle fairyBattle = PruningBattle(fairyPotion: fairy);
            BoundValues fairyK5 = bound.Evaluate(in fairyBattle, 0);
            Assert(fairyK5.RemainingHealingPotential == int.MaxValue,
                "fairy K5 remaining healing must stay unknown");
            Assert(fairyK5.StrategicHpLowerBound == -20,
                "fairy K5 lower bound changed");
            Assert(bound.PruningStrategicHpLowerBound(in fairyBattle, rules) == 20,
                "fairy must not block the pruning lower bound");

            Battle lizardBattle = PruningBattle(lizardRelic: lizard);
            BoundValues lizardK5 = bound.Evaluate(in lizardBattle, 0);
            Assert(lizardK5.RemainingHealingPotential == int.MaxValue,
                "lizard tail K5 remaining healing must stay unknown");
            Assert(lizardK5.StrategicHpLowerBound == -20,
                "lizard tail K5 lower bound changed");
            Assert(bound.PruningStrategicHpLowerBound(in lizardBattle, rules) == 20,
                "lizard tail must not block the pruning lower bound");

            Battle exhausted = PruningBattle(cards: [(notYet, 0, Pile.Exhaust)]);
            Assert(bound.PruningStrategicHpLowerBound(in exhausted, rules) == 20,
                "exhausted Not Yet must no longer block or count");

            Battle live = PruningBattle(cards: [(notYet, 0, Pile.Hand)]);
            Assert(bound.PruningStrategicHpLowerBound(in live, rules) == 10,
                "live Not Yet must count its heal");

            Battle upgraded = PruningBattle(cards: [(notYet, 1, Pile.Draw)]);
            Assert(bound.PruningStrategicHpLowerBound(in upgraded, rules) == 7,
                "upgraded Not Yet must count its upgraded heal");

            Battle feedBattle = PruningBattle(cards: [(feed, 0, Pile.Discard)]);
            Assert(bound.PruningStrategicHpLowerBound(in feedBattle, rules) == 17,
                "live Feed must count one max-HP payout");

            Battle replay = PruningBattle(cards: [(notYet, 0, Pile.Hand), (burst, 0, Pile.Hand)]);
            Assert(bound.PruningStrategicHpLowerBound(in replay, rules) == -20,
                "a replay source must keep Not Yet unknown");

            Battle retrieved = PruningBattle(cards: [(notYet, 0, Pile.Exhaust)]);
            retrieved.RelicCount = 1;
            retrieved.Relics[0] = new Relic { Id = burningSticks };
            Assert(bound.PruningStrategicHpLowerBound(in retrieved, rules) == -20,
                "a source that can return an exhausted Not Yet must keep it unknown");

            Battle fruitBattle = PruningBattle(potions: [fruit]);
            Assert(bound.PruningStrategicHpLowerBound(in fruitBattle, rules) == 15,
                "Fruit Juice must count one max-HP gain");

            Battle dragonFruitBattle = PruningBattle(lizardRelic: dragonFruit);
            Assert(bound.PruningStrategicHpLowerBound(in dragonFruitBattle, rules) == 20,
                "Dragon Fruit without an in-combat gold source must not block");
            Battle goldBattle = PruningBattle(lizardRelic: dragonFruit,
                cards: [(handOfGreed, 0, Pile.Hand)]);
            Assert(bound.PruningStrategicHpLowerBound(in goldBattle, rules) == 19,
                "each live Hand of Greed pays one Dragon Fruit max-HP gain");
            Battle goldExhausted = PruningBattle(lizardRelic: dragonFruit,
                cards: [(handOfGreed, 0, Pile.Exhaust)]);
            Assert(bound.PruningStrategicHpLowerBound(in goldExhausted, rules) == 20,
                "an exhausted Hand of Greed pays no Dragon Fruit max-HP gain");
            Battle twoGold = PruningBattle(lizardRelic: dragonFruit,
                cards: [(handOfGreed, 0, Pile.Hand), (handOfGreed, 0, Pile.Discard)]);
            Assert(bound.PruningStrategicHpLowerBound(in twoGold, rules) == 18,
                "every live Hand of Greed copy pays once");
            Battle luckyBattle = PruningBattle(relics: [dragonFruit, luckyFysh],
                cards: [(handOfGreed, 0, Pile.Hand)]);
            Assert(bound.PruningStrategicHpLowerBound(in luckyBattle, rules) == -20,
                "LuckyFysh's in-combat gold gain cannot be bounded by Hand of Greed copies");
        }
    }

    private static Rules BuildBoundRules(Rules fixtureRules, ushort notYet, ushort feed, ushort fruit,
        ushort dragonFruit)
    {
        _ = fixtureRules;
        RulesDraft builder = new() { BaseMaxEnergy = 3, RootEnemyCount = 1 };
        builder.SetCard(notYet, 0, CardStats(CardType.Skill, TargetType.Self, 2, [10m]));
        builder.SetCard(notYet, 1, CardStats(CardType.Skill, TargetType.Self, 2, [13m]));
        builder.SetCard(feed, 0, CardStats(CardType.Attack, TargetType.AnyEnemy, 1, [10m, 3m]));
        builder.SetPotion(fruit, new PotionStats
        {
            Target = TargetType.Self,
            Rarity = PotionRarity.Rare,
            StrategicCost = 0,
            Vars = [5m],
            VarProps = [ValueProps.None],
        });
        builder.SetRelicVars(dragonFruit, [1m]);
        return builder.Build();
    }

    private static BoundFeatures.Config KnownNativeConfig(ushort notYet, ushort feed,
        ushort lizard, ushort dragonFruit, ushort fairy, ushort fruit)
        => BaseConfig(HealingCertificate.KnownNative,
            knownCards: [notYet, feed],
            knownRelics: [lizard, dragonFruit],
            fairyPotion: fairy,
            fruitPotion: fruit);

    private static BoundFeatures.Config ComponentConfig(ushort notYet, ushort feed,
        ushort lizard, ushort dragonFruit, ushort fairy, ushort fruit)
        => BaseConfig(HealingCertificate.Component,
            knownCards: [notYet, feed],
            knownRelics: [lizard, dragonFruit],
            fairyPotion: fairy,
            fruitPotion: fruit);

    private static BoundFeatures.Config BaseConfig(HealingCertificate certificate,
        IReadOnlyList<ushort> knownCards, IReadOnlyList<ushort> knownRelics,
        ushort fairyPotion, ushort fruitPotion)
    {
        bool[] knownBlockingCard = new bool[Registry.CardCount];
        foreach (ushort id in knownCards)
            knownBlockingCard[id] = true;
        bool[] knownBlockingRelic = new bool[Registry.RelicCount];
        foreach (ushort id in knownRelics)
            knownBlockingRelic[id] = true;
        bool[] fairy = new bool[Registry.PotionCount];
        fairy[fairyPotion] = true;
        bool[] fruit = new bool[Registry.PotionCount];
        fruit[fruitPotion] = true;
        bool[] native = new bool[Registry.PotionCount];
        Array.Fill(native, true);
        bool[] allTrueCards = new bool[Registry.CardCount];
        Array.Fill(allTrueCards, true);
        bool[] allTrueRelics = new bool[Registry.RelicCount];
        Array.Fill(allTrueRelics, true);
        bool[] safeEnchantment = new bool[256];
        Array.Fill(safeEnchantment, true);
        bool[] safeAffliction = new bool[256];
        Array.Fill(safeAffliction, true);
        ushort regenPower = Registry.TryPower("REGEN_POWER", out ushort regen)
            ? regen : ushort.MaxValue;
        ushort reattachPower = Registry.TryPower("REATTACH_POWER", out ushort reattach)
            ? reattach : ushort.MaxValue;
        return new BoundFeatures.Config
        {
            Enabled = true,
            DisabledReason = "",
            FutureHealPotential = int.MaxValue,
            Certificate = certificate,
            PostCombatHeal = 0,
            BossHpRelief = BossHpRelief.None,
            RequireCertifiedHealingProof = certificate == HealingCertificate.Component,
            PruneEqualHp = true,
            ForceAllPotionsDisabled = false,
            MaximumPotionUses = null,
            ComponentUnsafeCard = certificate == HealingCertificate.Component
                ? [.. knownBlockingCard]
                : new bool[Registry.CardCount],
            ComponentUnsafePower = new bool[Registry.PowerCount],
            ComponentUnsafeRelic = certificate == HealingCertificate.Component
                ? [.. knownBlockingRelic]
                : new bool[Registry.RelicCount],
            ComponentUnsafeMonster = new bool[Registry.MonsterCount],
            ComponentUnsafePotion = certificate == HealingCertificate.Component
                ? [.. fairy.Select((value, id) => value || id == fruitPotion)]
                : new bool[Registry.PotionCount],
            SafeEnchantment = safeEnchantment,
            SafeAffliction = safeAffliction,
            KnownBlockingCard = knownBlockingCard,
            KnownBlockingRelic = knownBlockingRelic,
            RegenPower = new bool[Registry.PowerCount],
            ReattachPower = new bool[Registry.PowerCount],
            SwipePower = new bool[Registry.PowerCount],
            RegenPotion = new bool[Registry.PotionCount],
            DirectHealPotion = new bool[Registry.PotionCount],
            FruitJuicePotion = fruit,
            FairyPotion = fairy,
            NativePotion = native,
            RegenPotionAmount = new int[Registry.PotionCount],
            DirectHealPotionPercent = new decimal[Registry.PotionCount],
            ReattachSafeMonster = new bool[Registry.MonsterCount],
            RegenPowerId = regenPower,
            ReattachPowerId = reattachPower,
            RootPotions = new ushort[Limits.PotionSlots],
            RootPotionSlotCount = 0,
        };
    }

    private static Battle PruningBattle(
        IReadOnlyList<(ushort Id, int Upgrade, Pile Pile)>? cards = null,
        IReadOnlyList<ushort>? potions = null,
        ushort? fairyPotion = null,
        ushort? lizardRelic = null,
        IReadOnlyList<ushort>? relics = null)
    {
        Battle battle = NewBattle(50, 50, 100, energy: 3);
        battle.SetCounter(Counter.CumulativeHpLost, 20);
        battle.Player.CurrentHp = 10;
        if (potions is { Count: > 0 })
        {
            battle.PotionSlotCount = (byte)potions.Count;
            for (int index = 0; index < potions.Count; index++)
                battle.Potions[index] = (ushort)(potions[index] + 1);
        }
        else if (fairyPotion is { } fairy)
        {
            battle.PotionSlotCount = 1;
            battle.Potions[0] = (ushort)(fairy + 1);
        }
        if (relics is { Count: > 0 })
        {
            battle.RelicCount = checked((byte)relics.Count);
            for (int index = 0; index < relics.Count; index++)
                battle.Relics[index] = new Relic { Id = relics[index] };
        }
        else if (lizardRelic is { } lizard)
        {
            battle.RelicCount = 1;
            battle.Relics[0] = new Relic { Id = lizard };
        }
        if (cards is not null)
        {
            foreach ((ushort id, int upgrade, Pile pile) in cards)
                Piles.Append(ref battle, pile, new Card { Id = id, Upgrade = (byte)upgrade, BaseCost = 0 });
        }
        return battle;
    }

    private static void CheckLoopGuard()
    {
        ushort top = Registry.TryRelic("UNCEASING_TOP", out ushort topId) ? topId : ushort.MaxValue;
        Assert(top != ushort.MaxValue, "missing UNCEASING_TOP");
        ushort block = CardId("IP1_TEST_BLOCK_LOOP");
        ushort strike = CardId("IP1_TEST_STRIKE");
        ushort poison = CardId("IP1_TEST_POISON_LOOP");
        ushort strength = CardId("IP1_TEST_STRENGTH_LOOP");

        Rules blockRules = BuildSearchRules(block, strike, poison, strength, attack: 12);
        (Rules attackRules, _) = (BuildSearchRules(block, strike, poison, strength, attack: 0), 0);

        // Block reaches the 12-damage threat line after three plays, then the next 256 actions are
        // no-progress and the following play child is refused. End-turn remains available.
        Battle loopRoot = SearchRoot(blockRules, block, inHand: true, inDiscard: true,
            enemyHp: 100, withTop: false);
        BeamResult loop = Solve(loopRoot, blockRules, maxNodes: 1000, threads: 1);
        Assert(loop.TurnLoopGuarded > 0, "block loop should hit the no-progress guard");
        Assert(loop.ExpandedNodes < 1000, "block loop should finish before the node budget");
        Assert(loop.StopReason != "budget", "block loop must not exhaust the node budget");

        Battle loopRoot4 = SearchRoot(blockRules, block, inHand: true, inDiscard: true,
            enemyHp: 100, withTop: false);
        BeamResult loop4 = Solve(loopRoot4, blockRules, maxNodes: 1000, threads: 4);
        Assert(loop.TurnLoopGuarded == loop4.TurnLoopGuarded
            && loop.ExpandedNodes == loop4.ExpandedNodes
            && SameRoute(loop.BestRoute, loop4.BestRoute),
            "turn-loop guard result must not depend on expansion threads");

        // One 0-cost attack plus Unceasing Top makes every hit a new enemy HP+block low, so a
        // 300-HP enemy dies after more than 256 plays without the guard firing.
        Battle attackRoot = SearchRoot(attackRules, strike, inHand: true, inDiscard: false,
            enemyHp: 300, withTop: true);
        BeamResult attack = Solve(attackRoot, attackRules, maxNodes: 4000, threads: 1);
        Assert(attack.BestIsVictory, "the attack loop should win");
        Assert(attack.BestRoute.Length > 256, "every attack hit must remain progress past 256 actions");
        Assert(attack.TurnLoopGuarded == 0, "progressing attacks must not be loop-guarded");

        // Poison and Strength each grow to a new high below their conservative line, so 300 plays
        // are not guarded. The search is allowed to stop at its node budget without a guard count.
        Rules scalingRules = BuildSearchRules(block, strike, poison, strength, attack: 0);
        Battle poisonRoot = SearchRoot(scalingRules, poison, inHand: true, inDiscard: true,
            enemyHp: 1000, withTop: false);
        BeamResult poisonResult = Solve(poisonRoot, scalingRules, maxNodes: 500, threads: 1);
        Assert(poisonResult.TurnLoopGuarded == 0 && poisonResult.ExpandedNodes >= 300,
            "poison growth below its line must not be loop-guarded");
        Battle poisonRoot4 = SearchRoot(scalingRules, poison, inHand: true, inDiscard: true,
            enemyHp: 1000, withTop: false);
        BeamResult poisonResult4 = Solve(poisonRoot4, scalingRules, maxNodes: 500, threads: 4);
        Assert(poisonResult.TurnLoopGuarded == poisonResult4.TurnLoopGuarded
            && poisonResult.ExpandedNodes == poisonResult4.ExpandedNodes
            && SameRoute(poisonResult.BestRoute, poisonResult4.BestRoute),
            "per-enemy progress metadata must not depend on expansion threads");

        Battle strengthRoot = SearchRoot(scalingRules, strength, inHand: true, inDiscard: true,
            enemyHp: 1000, withTop: false);
        BeamResult strengthResult = Solve(strengthRoot, scalingRules, maxNodes: 500, threads: 1);
        Assert(strengthResult.TurnLoopGuarded == 0 && strengthResult.ExpandedNodes >= 300,
            "Strength growth below its line must not be loop-guarded");
    }


    private static bool SameRoute(IReadOnlyList<PlayerAction> left,
        IReadOnlyList<PlayerAction> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            PlayerAction a = left[index];
            PlayerAction b = right[index];
            if (a.Kind != b.Kind || a.HandIndex != b.HandIndex || a.Target != b.Target
                || a.PotionSlot != b.PotionSlot || a.ChoiceCount != b.ChoiceCount)
            {
                return false;
            }
            for (int choice = 0; choice < a.ChoiceCount; choice++)
            {
                if (a.Choices[choice] != b.Choices[choice])
                    return false;
            }
        }
        return true;
    }

    private static ushort CardId(string nativeId)
    {
        Assert(Registry.TryCard(nativeId, out ushort id), $"missing test card {nativeId}");
        return id;
    }

    private static Rules BuildSearchRules(ushort block, ushort strike, ushort poison,
        ushort strength, int attack)
    {
        RulesDraft builder = new()
        {
            BaseMaxEnergy = 3,
            RootEnemyCount = 1,
            HasEncounter = true,
            EncounterSlots = ["first", "second", "third"],
        };
        Registry.TryMonster("NIBBIT", out ushort monster);
        builder.SetMonster(0, new MonsterRules
        {
            MonsterId = monster,
            Nodes =
            [
                new MonsterNode
                {
                    NativeId = "MOVE", Kind = MonsterNodeKind.Move, FollowUp = 2,
                    Attacks = [new MonsterAttack(attack, 1)],
                },
                new MonsterNode
                {
                    NativeId = "STUNNED", Kind = MonsterNodeKind.Move,
                    MustPerformOnceBeforeTransitioning = true,
                },
                new MonsterNode
                {
                    NativeId = "MOVE_ALT", Kind = MonsterNodeKind.Move, FollowUp = 0,
                    Attacks = [new MonsterAttack(attack, 1)],
                },
            ],
            InitialNode = 0,
            StunnedNode = 1,
            InitialHpMin = 100,
            InitialHpMax = 100,
            StaticInts = [],
        });
        builder.SetCard(block, 0, CardStats(CardType.Skill, TargetType.Self, 0, [4m]));
        builder.SetCard(strike, 0, CardStats(CardType.Attack, TargetType.AnyEnemy, 0, [1m]));
        builder.SetCard(poison, 0, CardStats(CardType.Skill, TargetType.Self, 0, []));
        builder.SetCard(strength, 0, CardStats(CardType.Skill, TargetType.Self, 0, []));
        return builder.Build();
    }

    private static Battle SearchRoot(Rules rules, ushort card, bool inHand, bool inDiscard,
        int enemyHp, bool withTop)
    {
        _ = rules;
        Battle battle = NewBattle(50, 50, enemyHp, energy: 3);
        battle.PlayerTurn = 1;
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.Flags |= BattleFlags.PlayerHooksActive;
        battle.MaxHandSize = 10;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0].Rules = 0;
        battle.Enemies[0].Move = 0;
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        Card value = new() { Id = card, BaseCost = 0, BaseStarCost = -1 };
        if (inHand)
            Piles.Append(ref battle, Pile.Hand, value);
        if (inDiscard)
            Piles.Append(ref battle, Pile.Discard, value);
        if (withTop)
        {
            Registry.TryRelic("UNCEASING_TOP", out ushort top);
            battle.RelicCount = 1;
            battle.Relics[0] = new Relic { Id = top };
        }
        return battle;
    }

    private static BeamResult Solve(Battle root, Rules rules, int maxNodes, int threads)
    {
        ScoreRules score = BuildScoreRules(rules);
        BeamOptions options = new(BeamWidth: 1, MaxExpandedNodes: maxNodes, MaxTurns: 1,
            SoftTimeBudget: TimeSpan.MaxValue)
        {
            Scorer = new Scorer(score),
            Ordering = new RouteOrdering(50, BossHpRelief.None, PostCombatRelicHealProfile.None),
            Potions = PotionPolicy.Disabled,
            EffectivePotionPolicy = PotionUseMode.Disabled,
            SearchMemoryBudgetBytes = 512L << 20,
            StatePoolBudgetBytes = 256L << 20,
            SearchThreads = threads,
        };
        return new BeamSolver(rules, options).Solve([root], CancellationToken.None);
    }

    private static ScoreRules BuildScoreRules(Rules rules)
    {
        CardScore?[][] cards = new CardScore?[Registry.CardCount][];
        for (int id = 0; id < cards.Length; id++)
            cards[id] = [new CardScore(), new CardScore()];
        CardScore? strikeScore = new() { BaseDamage = 1d };
        CardScore? blockScore = new() { BaseBlock = 4d };
        Registry.TryCard("IP1_TEST_STRIKE", out ushort strike);
        Registry.TryCard("IP1_TEST_BLOCK_LOOP", out ushort block);
        cards[strike] = [strikeScore, strikeScore];
        cards[block] = [blockScore, blockScore];
        PowerScore[] powers = new PowerScore[Registry.PowerCount];
        Array.Fill(powers, new PowerScore());
        return new ScoreRules
        {
            Rules = rules,
            Cards = cards,
            Powers = powers,
            PotionStrategicCost = new int[Registry.PotionCount],
            InitialPlayerMaxHp = 50,
            IsActEndingBoss = false,
            Act3BossStrategy = false,
            IgnoreLongTermRewards = false,
            TheftPolicy = null,
            InitialEnemyCount = 1,
            InitialPersistentBuffValue = 0,
            InitialEnemyStrengthSuppression = 0,
            InitialEnemyWeakTurns = 0,
            InitialRetainedAttackValue = 0,
            RootPotions = new ushort[Limits.PotionSlots],
            RootPotionSlotCount = 0,
            StrengthPowerId = Registry.TryPower("STRENGTH_POWER", out ushort strength)
                ? strength : ushort.MaxValue,
            PoisonPowerId = Registry.TryPower("POISON_POWER", out ushort poison)
                ? poison : ushort.MaxValue,
            DoomPowerId = Registry.TryPower("DOOM_POWER", out ushort doom)
                ? doom : ushort.MaxValue,
            BodySlamCardId = Registry.TryCard("BODY_SLAM", out ushort bodySlam)
                ? bodySlam : ushort.MaxValue,
        };
    }

    private static CardStats CardStats(CardType type, TargetType target, int cost, decimal[] vars)
        => new()
        {
            Type = type,
            Target = target,
            Rarity = CardRarity.Basic,
            EnergyCost = cost,
            CostsX = false,
            StarCost = -1,
            StarCostsX = false,
            Keywords = Keywords.None,
            MaxUpgradeLevel = type == CardType.Skill && vars.Length == 1 ? 1 : 0,
            Vars = vars,
        };

    private static Battle NewBattle(int playerHp, int playerMaxHp, int enemyHp, int energy)
    {
        Battle battle = default;
        battle.Player = new Creature
        {
            CurrentHp = playerHp,
            MaxHp = playerMaxHp,
            Flags = CreatureFlags.Present,
        };
        battle.Energy = energy;
        battle.MaxHandSize = 10;
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0].Creature = new Creature
        {
            CurrentHp = enemyHp,
            MaxHp = enemyHp,
            Flags = CreatureFlags.Present,
        };
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        return battle;
    }
}

[ContentId("IP1_TEST_BLOCK_LOOP")]
public sealed class Ip1BlockLoopCard : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        Kernel.GainBlock(ref x, Limits.PlayerOwner, 4m, ValueProps.Unpowered, fromCard: true);
        Kernel.Draw(ref x, 1, fromHandDraw: false);
    }
}

[ContentId("IP1_TEST_STRIKE")]
public sealed class Ip1StrikeCard : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        // Keep the fixture a real Attack card without the kernel's 255-powered-hit cap; the test
        // only needs a monotone enemy-HP decrease, not Strength/Vulnerable arithmetic.
        Span<byte> targets = stackalloc byte[1];
        targets[0] = x.Target;
        Kernel.Damage(ref x, targets, 1, ValueProps.Move | ValueProps.Unpowered,
            Limits.PlayerOwner, hasCardSource: true, x.Card);
    }
}

[ContentId("IP1_TEST_POISON_LOOP")]
public sealed class Ip1PoisonLoopCard : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        Kernel.ApplyPower(ref x, Limits.FirstEnemyOwner,
            Registry.PowerId<Content.Powers.PoisonPower>(), 1, Limits.PlayerOwner);
        Kernel.Draw(ref x, 1, fromHandDraw: false);
    }
}

[ContentId("IP1_TEST_STRENGTH_LOOP")]
public sealed class Ip1StrengthLoopCard : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Content.Powers.StrengthPower>(), 1, Limits.PlayerOwner);
        Kernel.Draw(ref x, 1, fromHandDraw: false);
    }
}
