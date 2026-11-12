using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

internal static class HistoryCourseChecks
{
    internal static void Run()
    {
        Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
        Registry.TryRelic("HISTORY_COURSE", out ushort relic);
        RulesDraft draft = new()
        {
            BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"],
            RootEnemyCount = 1, HasHistoryCourse = true,
        };
        draft.SetCard(strike, 0, new CardStats
        {
            Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Basic,
            EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false,
            Keywords = Keywords.None, MaxUpgradeLevel = 1, Vars = [6m],
        });
        Rules rules = draft.Build();
        foreach (bool permanent in new[] { false, true })
        foreach (byte replays in new byte[] { 0, 1 })
        {
            Battle battle = default;
            battle.Player = new Creature { CurrentHp = 50, MaxHp = 50, Flags = CreatureFlags.Present };
            battle.MaxHandSize = 10;
            battle.Energy = 3;
            battle.PlayerTurn = 1;
            battle.Side = Side.Player;
            battle.Phase = PlayerPhase.Play;
            battle.Flags = BattleFlags.PlayerHooksActive;
            battle.EnemyCount = battle.RosterCount = 1;
            battle.Roster[0] = 0;
            battle.Enemies[0].Creature = new Creature
            { CurrentHp = 100, MaxHp = 100, Flags = CreatureFlags.Present };
            battle.Enemies[0].Flags = EnemyFlags.InCombat;
            battle.RelicCount = 1;
            battle.Relics[0] = new Relic { Id = relic };
            Card attack = Kernel.CreateCard(rules, strike);
            attack.ReplayCount = replays;
            if (permanent)
                Kernel.MakeFreeThisCombat(rules, ref attack);
            Kernel.MakeFreeThisTurn(rules, ref attack);
            Piles.Append(ref battle, Pile.Hand, attack);
            Check(Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.FirstEnemyOwner), rules, out _)
                == StepResult.Completed, "零费攻击应完成");
            Check(battle.Energy == 3, "临时零费应在出牌时生效");
            Check(battle.Enemies[0].Creature.CurrentHp == 100 - 6 * (replays + 1),
                "重放次数和伤害应保持不变");
            Check(battle.DiscardCount == 1, "原牌应进入弃牌堆");
            Card discarded = Piles.At(ref battle, Pile.Discard, 0);
            Check(Fingerprint.Equal(battle.HistoryCourseCard, Kernel.HistoryCourseValue(discarded)),
                "历史课应记录出牌后清理过费用的原牌");
            Check(discarded.EnergyModifierCount == (permanent ? 1 : 0)
                && discarded.HasTemporaryStarCost == permanent,
                "只清理打出即到期的能量和星星费用，保留整场零费");
            if (permanent)
                Check(discarded.EnergyModifiers[0].Expiration == CostExpiration.EndOfCombat
                    && !discarded.TemporaryStarCostClearsWhenPlayed,
                    "整场费用修正应保留原有期限");

            battle.PlayerTurn = 2;
            battle.Phase = PlayerPhase.AutoPrePlay;
            int hp = battle.Enemies[0].Creature.CurrentHp;
            Exec x = new(ref battle, rules, PlayerAction.EndTurn());
            Kernel.BeginHistoryCourseTurn(ref x);
            Kernel.AutoPrePlay(ref x);
            Check(!x.NeedsChoice && battle.Enemies[0].Creature.CurrentHp < hp,
                "历史课下一回合仍应自动打出攻击");
            Check(battle.CardCount == 1 && battle.DiscardCount == 1,
                "历史课复制品应移出战斗，原牌保留");
            Check((battle.Flags & BattleFlags.HistoryCourseCardPresent) == 0,
                "复制品不能覆盖新的历史记录");
        }
        CheckSourceIdentity();
        CheckDupeClone();
        Console.WriteLine("HISTORY_COURSE_COST cleanup/permanent/replay/next-turn/source-identity=pass");
    }

    private static void CheckSourceIdentity()
    {
        Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
        Registry.TryCard("DEFEND_IRONCLAD", out ushort defend);
        Registry.TryRelic("HISTORY_COURSE", out ushort relic);
        RulesDraft draft = new()
        {
            BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"],
            RootEnemyCount = 1, HasHistoryCourse = true,
        };
        draft.SetCard(strike, 0, new CardStats
        {
            Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Basic,
            EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false,
            Keywords = Keywords.None, MaxUpgradeLevel = 1, Vars = [6m],
        });
        draft.SetCard(defend, 0, new CardStats
        {
            Type = CardType.Skill, Target = TargetType.Self, Rarity = CardRarity.Basic,
            EnergyCost = 0, CostsX = false, StarCost = -1, StarCostsX = false,
            Keywords = Keywords.None, MaxUpgradeLevel = 1, Vars = [5m],
        });
        Rules rules = draft.Build();
        Battle battle = default;
        battle.Player = new Creature { CurrentHp = 50, MaxHp = 50, Flags = CreatureFlags.Present };
        battle.MaxHandSize = 10;
        battle.Energy = 3;
        battle.PlayerTurn = 1;
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.Flags = BattleFlags.PlayerHooksActive;
        battle.EnemyCount = battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0].Creature = new Creature
        { CurrentHp = 300, MaxHp = 300, Flags = CreatureFlags.Present };
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        battle.RelicCount = 1;
        battle.Relics[0] = new Relic { Id = relic };
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, strike));
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.FirstEnemyOwner), rules, out _);
        Card sameAttack = Kernel.HistoryCourseValue(Piles.At(ref battle, Pile.Discard, 0));
        Piles.Append(ref battle, Pile.Discard, sameAttack);
        Piles.At(ref battle, Pile.Discard, 1).Var0 = 40;
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, defend));
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
        Check(battle.HistoryCourseCard.Var0 == 0, "修改另一张同名同值牌不能影响历史课");
        Piles.At(ref battle, Pile.Discard, 0).Var0 = 3;
        Kernel.AddEnergyCostModifier(ref Piles.At(ref battle, Pile.Discard, 0), -1,
            CostType.Relative, CostExpiration.EndOfCombat, false);
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, defend));
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
        Check(battle.HistoryCourseCard.Var0 == 3 && battle.HistoryCourseCard.EnergyModifierCount == 1,
            "后续动作改变原牌的伤害或费用时，历史课记录必须同步");
        Check(Kernel.CloneCard(Piles.At(ref battle, Pile.Discard, 0)).HistoryCourseRole == 0,
            "克隆牌不能继承原牌的历史身份");

        battle.PlayerTurn = 2;
        battle.Phase = PlayerPhase.AutoPrePlay;
        Exec x = new(ref battle, rules, PlayerAction.EndTurn());
        Kernel.BeginHistoryCourseTurn(ref x);
        Card openingAttack = Kernel.CreateCard(rules, strike);
        openingAttack.Var0 = 5;
        Piles.Append(ref battle, Pile.Hand, openingAttack);
        Kernel.AutoPlay(ref x, Pile.Hand, 0, Limits.FirstEnemyOwner);
        int hp = battle.Enemies[0].Creature.CurrentHp;
        Kernel.AutoPrePlay(ref x);
        Check(battle.Enemies[0].Creature.CurrentHp == hp - 9,
            "回合准备先自动打出另一张攻击后，历史课仍应复制上回合原牌的最新值");
        Check(battle.HistoryCourseCard.Var0 == 5
            && (battle.Flags & BattleFlags.HistoryCourseCardPresent) != 0,
            "历史课不能清除本回合准备期间已经打出的攻击记录");
        int sources = 0;
        for (int i = 0; i < battle.CardCount; i++)
        {
            Check((battle.Cards[i].HistoryCourseRole & 2) == 0, "上一回合的临时身份应清除");
            if (battle.Cards[i].HistoryCourseRole == 1) sources++;
        }
        Check(sources == 1, "自动预打出后只有本回合最后攻击带历史身份");
        Card unplayable = Kernel.CreateCard(rules, strike);
        unplayable.Keywords |= Keywords.Unplayable;
        Check(!Kernel.AutoPlay(ref x, unplayable, historyCourseDupe: true)
            && !x.CurrentPlayIsHistoryCourseDupe,
            "复制品无法打出时，也应恢复外层出牌身份");
    }

    private static void CheckDupeClone()
    {
        Registry.TryCard("ADAPTIVE_STRIKE", out ushort adaptive);
        RulesDraft draft = new() { BaseMaxEnergy = 3, HasHistoryCourse = true };
        draft.SetCard(adaptive, 0, new CardStats
        {
            Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Rare,
            EnergyCost = 2, CostsX = false, StarCost = -1, StarCostsX = false,
            Keywords = Keywords.None, MaxUpgradeLevel = 1, Vars = [18m],
        });
        Rules rules = draft.Build();
        Battle battle = default;
        battle.Player = new Creature { CurrentHp = 50, MaxHp = 50, Flags = CreatureFlags.Present };
        battle.MaxHandSize = 10;
        battle.Energy = 3;
        battle.PlayerTurn = 2;
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.Flags = BattleFlags.PlayerHooksActive;
        battle.EnemyCount = battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0].Creature = new Creature
        { CurrentHp = 100, MaxHp = 100, Flags = CreatureFlags.Present };
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        Card dupe = Kernel.CreateCard(rules, adaptive);
        dupe.IsClone = dupe.IsDupe = true;
        Piles.Append(ref battle, Pile.Hand, dupe);
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.FirstEnemyOwner), rules, out _);
        Check(battle.CardCount == 1 && battle.DiscardCount == 1,
            "手动打出的历史课复制品应移出战斗，只留下适应打击生成的牌");
        Card generated = Piles.At(ref battle, Pile.Discard, 0);
        Check(generated.IsClone && generated.IsDupe && generated.EnergyModifierCount == 1,
            "适应打击从复制品生成的克隆应保留 IsDupe 和整场零费");
        Check((battle.Flags & BattleFlags.HistoryCourseCardPresent) == 0,
            "打出复制品不能成为历史课的下一次来源");
        Kernel.MoveCard(ref battle, Pile.Discard, 0, Pile.Hand);
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.FirstEnemyOwner), rules, out _);
        Check(battle.CardCount == 1 && battle.Energy == 1,
            "再次打出零费复制品仍应移出战斗，且不消耗能量");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("HistoryCourse: " + message);
    }
}
