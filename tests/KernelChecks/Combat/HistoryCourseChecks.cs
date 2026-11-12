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
            Check(Fingerprint.Equal(battle.HistoryCourseCard, discarded),
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
            Kernel.AutoPrePlay(ref x);
            Check(!x.NeedsChoice && battle.Enemies[0].Creature.CurrentHp < hp,
                "历史课下一回合仍应自动打出攻击");
            Check(battle.CardCount == 1 && battle.DiscardCount == 1,
                "历史课复制品应移出战斗，原牌保留");
            Check((battle.Flags & BattleFlags.HistoryCourseCardPresent) == 0,
                "复制品不能覆盖新的历史记录");
        }
        Console.WriteLine("HISTORY_COURSE_COST cleanup/permanent/replay/next-turn=pass");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("HistoryCourse: " + message);
    }
}
