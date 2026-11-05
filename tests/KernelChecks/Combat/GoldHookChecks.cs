using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>
/// Contracts for the unified in-combat gold entry (<c>Kernel.GainGold</c>): modifier order,
/// changed-only <c>AfterModifyingGoldGained</c>, run-listener <c>AfterGoldGained</c>, DragonFruit's
/// max-HP gain and the two supported gold-modifier relics.
/// </summary>
internal static class GoldHookChecks
{
    private static void Assert(bool value, string message)
    {
        if (!value)
            throw new InvalidDataException("gold-hook-checks: " + message);
    }

    internal static void Check()
    {
        Assert(Registry.TryRelic("BOWLER_HAT", out ushort bowlerHat), "missing BowlerHat");
        Assert(Registry.TryRelic("ECTOPLASM", out ushort ectoplasm), "missing Ectoplasm");
        Assert(Registry.TryRelic("DRAGON_FRUIT", out ushort dragonFruit), "missing DragonFruit");
        Assert(Registry.TryRelic("LUCKY_FYSH", out ushort luckyFysh), "missing LuckyFysh");
        Assert(Registry.TryCard("HAND_OF_GREED", out ushort handOfGreed), "missing HandOfGreed");
        Assert(Registry.Card(handOfGreed).GainsGoldInCombat,
            "HandOfGreed must be classified as an in-combat gold source");
        Assert(Registry.Relic(luckyFysh).GainsGoldInCombat,
            "LuckyFysh must be classified as an in-combat gold source");
        Assert(!Registry.Relic(bowlerHat).GainsGoldInCombat
            && !Registry.Relic(ectoplasm).GainsGoldInCombat
            && !Registry.Relic(dragonFruit).GainsGoldInCombat,
            "gold modifiers and DragonFruit only react to a gain, they do not gain gold");
        Assert((Registry.AnyHooksExt & (HookMaskExt.ModifyGoldGained
                | HookMaskExt.AfterModifyingGoldGained | HookMaskExt.AfterGoldGained)) != 0,
            "registry does not expose the gold hooks");

        Rules rules = GoldRules(bowlerHat, ectoplasm, dragonFruit, handOfGreed);
        CheckHandOfGreedPlay(rules, handOfGreed, bowlerHat, ectoplasm, dragonFruit);

        Battle plain = BattleWith(relics: []);
        Exec plainExec = new(ref plain, rules, default);
        Assert(Kernel.GainGold(ref plainExec, 20m) == 20 && plain.Gold == 20,
            "plain gain must add the truncated amount");

        Battle dragon = BattleWith(relics: [dragonFruit]);
        Exec dragonExec = new(ref dragon, rules, default);
        Assert(Kernel.GainGold(ref dragonExec, 20m) == 20 && dragon.Gold == 20,
            "DragonFruit must not change the gold amount");
        Assert(dragon.Player.MaxHp == 51 && dragon.Player.CurrentHp == 51,
            "DragonFruit must gain one max HP and heal the same amount");

        Battle fractional = BattleWith(relics: [dragonFruit]);
        Exec fractionalExec = new(ref fractional, rules, default);
        Assert(Kernel.GainGold(ref fractionalExec, 0.25m) == 0 && fractional.Gold == 0,
            "a positive fractional amount adds zero gold");
        Assert(fractional.Player.MaxHp == 51 && fractional.Player.CurrentHp == 51,
            "AfterGoldGained must still run for a positive fractional amount");

        Battle zero = BattleWith(relics: [dragonFruit]);
        Exec zeroExec = new(ref zero, rules, default);
        Assert(Kernel.GainGold(ref zeroExec, 0m) == 0 && zero.Player.MaxHp == 50,
            "zero gain must not dispatch AfterGoldGained");
        Battle negative = BattleWith(relics: [dragonFruit]);
        Exec negativeExec = new(ref negative, rules, default);
        Assert(Kernel.GainGold(ref negativeExec, -5m) == 0 && negative.Player.MaxHp == 50,
            "negative gain must not dispatch AfterGoldGained");

        Battle bowler = BattleWith(relics: [dragonFruit, bowlerHat]);
        Exec bowlerExec = new(ref bowler, rules, default);
        Assert(Kernel.GainGold(ref bowlerExec, 20m) == 25 && bowler.Gold == 25,
            "BowlerHat must multiply the amount by GoldIncrease");
        Assert(bowler.Player.MaxHp == 51 && bowler.Player.CurrentHp == 51,
            "DragonFruit must use the amount gained after BowlerHat");

        Battle ectoplasmBattle = BattleWith(relics: [dragonFruit, ectoplasm]);
        Exec ectoplasmExec = new(ref ectoplasmBattle, rules, default);
        Assert(Kernel.GainGold(ref ectoplasmExec, 20m) == 0 && ectoplasmBattle.Gold == 0,
            "Ectoplasm must zero the gain");
        Assert(ectoplasmBattle.Player.MaxHp == 50 && ectoplasmBattle.Player.CurrentHp == 50,
            "zero post-modifier gain must not dispatch DragonFruit");

        Battle bowlerThenEctoplasm = BattleWith(relics: [dragonFruit, bowlerHat, ectoplasm]);
        Exec bowlerThenEctoplasmExec = new(ref bowlerThenEctoplasm, rules, default);
        Assert(Kernel.GainGold(ref bowlerThenEctoplasmExec, 20m) == 0
            && bowlerThenEctoplasm.Gold == 0 && bowlerThenEctoplasm.Player.MaxHp == 50,
            "BowlerHat then Ectoplasm must settle to zero");

        Battle ectoplasmThenBowler = BattleWith(relics: [dragonFruit, ectoplasm, bowlerHat]);
        Exec ectoplasmThenBowlerExec = new(ref ectoplasmThenBowler, rules, default);
        Assert(Kernel.GainGold(ref ectoplasmThenBowlerExec, 20m) == 0
            && ectoplasmThenBowler.Gold == 0 && ectoplasmThenBowler.Player.MaxHp == 50,
            "Ectoplasm then BowlerHat must settle to zero");

        Battle melted = BattleWith(relics: [dragonFruit]);
        melted.Relics[0].Flags |= RelicFlags.Melted;
        Exec meltedExec = new(ref melted, rules, default);
        Assert(Kernel.GainGold(ref meltedExec, 20m) == 20 && melted.Player.MaxHp == 50,
            "a melted DragonFruit must not run its after-gain hook");

        Battle inactive = BattleWith(relics: [dragonFruit, bowlerHat], hooksActive: false);
        Exec inactiveExec = new(ref inactive, rules, default);
        Assert(Kernel.GainGold(ref inactiveExec, 20m) == 20 && inactive.Gold == 20
            && inactive.Player.MaxHp == 50,
            "inactive player hooks must skip gold modifiers and DragonFruit");

        CheckListenerOrder();
        Console.WriteLine("GOLD_HOOK contracts=pass");
    }

    private static void CheckHandOfGreedPlay(Rules rules, ushort handOfGreed, ushort bowlerHat,
        ushort ectoplasm, ushort dragonFruit)
    {
        Battle plain = GoldPlayBattle(handOfGreed, rules, relics: []);
        Kernel.Step(ref plain, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);
        Assert(plain.Gold == 27, $"plain HandOfGreed gold should be 7 + 20, actual {plain.Gold}");
        Assert(plain.Player.MaxHp == 50, "HandOfGreed without DragonFruit must not gain max HP");
        Assert(plain.Counter(Counter.LongTermResourceValue) == 20,
            "plain HandOfGreed long-term resource should be the 20 actual gold");
        Assert(GrowthRewards.Get(plain, GrowthRewardSource.HandOfGreed) == 1,
            "HandOfGreed must record one growth reward");

        Battle boosted = GoldPlayBattle(handOfGreed, rules, relics: [dragonFruit, bowlerHat]);
        Kernel.Step(ref boosted, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);
        Assert(boosted.Gold == 32, $"BowlerHat HandOfGreed gold should be 7 + 25, actual {boosted.Gold}");
        Assert(boosted.Player.MaxHp == 51 && boosted.Player.CurrentHp == 51,
            "DragonFruit must gain and heal one max HP after HandOfGreed");
        Assert(boosted.Counter(Counter.LongTermResourceValue) == 25,
            "long-term resource must use the post-modifier gold amount");
        Assert(GrowthRewards.Get(boosted, GrowthRewardSource.HandOfGreed) == 1,
            "the growth reward must be recorded after the gold hooks");

        Battle blocked = GoldPlayBattle(handOfGreed, rules, relics: [dragonFruit, ectoplasm]);
        Kernel.Step(ref blocked, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);
        Assert(blocked.Gold == 7, $"Ectoplasm HandOfGreed gold should stay 7, actual {blocked.Gold}");
        Assert(blocked.Player.MaxHp == 50 && blocked.Player.CurrentHp == 50,
            "Ectoplasm zeroes the gain before DragonFruit's after-gain hook");
        Assert(blocked.Counter(Counter.LongTermResourceValue) == 0,
            "zero actual gold must not add long-term resource");
        Assert(GrowthRewards.Get(blocked, GrowthRewardSource.HandOfGreed) == 1,
            "the growth reward is recorded even when gold is zero");
    }

    private static Battle GoldPlayBattle(ushort handOfGreed, Rules rules, IReadOnlyList<ushort> relics)
    {
        Battle battle = BattleWith(relics);
        battle.Gold = 7;
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0].Creature = new Creature
        {
            CurrentHp = 1,
            MaxHp = 1,
            Flags = CreatureFlags.Present,
        };
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, handOfGreed));
        return battle;
    }

    private static void CheckListenerOrder()
    {
        Assert(Registry.TryRelic("DF1_TEST_GOLD_DOUBLER", out ushort doubler), "missing test doubler");
        Assert(Registry.TryRelic("DF1_TEST_GOLD_OBSERVER", out ushort observer), "missing test observer");
        Assert(Registry.TryPower("DF1_TEST_GOLD_POWER", out ushort power), "missing test power");
        Battle battle = BattleWith(relics: [doubler, observer]);
        battle.PowerCount = 1;
        battle.Powers[0] = new Power { Id = power, Owner = Limits.PlayerOwner, Amount = 1 };
        Rules rules = new RulesDraft { BaseMaxEnergy = 3, RootEnemyCount = 1 }.Build();
        Exec exec = new(ref battle, rules, default);
        Assert(Kernel.GainGold(ref exec, 10m) == 20 && battle.Gold == 20,
            "test doubler must modify the amount before the balance");
        Assert(battle.Counter(Counter.CardsDrawn) == 1,
            "AfterModifyingGoldGained must run only for the changed listener");
        Assert(battle.Counter(Counter.FinishedCardPlays) == 12,
            "AfterGoldGained must run in listener order after the balance changed");
        Assert(battle.Counter(Counter.EnergySpentThisTurn) == 13,
            $"run listeners must run before combat power listeners, and powers skip AfterGoldGained; actual={battle.Counter(Counter.EnergySpentThisTurn)}");
    }

    private static Battle BattleWith(IReadOnlyList<ushort> relics, bool hooksActive = true)
    {
        Battle battle = default;
        battle.Round = 1;
        battle.MaxHandSize = 10;
        battle.Energy = 3;
        battle.Player = new Creature
        {
            CurrentHp = 50,
            MaxHp = 50,
            Flags = CreatureFlags.Present,
        };
        if (hooksActive)
            battle.Flags |= BattleFlags.PlayerHooksActive;
        battle.RelicCount = checked((byte)relics.Count);
        for (int index = 0; index < relics.Count; index++)
            battle.Relics[index] = new Relic { Id = relics[index] };
        return battle;
    }

    private static Rules GoldRules(ushort bowlerHat, ushort ectoplasm, ushort dragonFruit,
        ushort handOfGreed)
    {
        RulesDraft builder = new() { BaseMaxEnergy = 3, RootEnemyCount = 1 };
        builder.SetRelicVars(bowlerHat, [1.25m]);
        builder.SetRelicVars(ectoplasm, [1m]);
        builder.SetRelicVars(dragonFruit, [1m]);
        builder.SetCard(handOfGreed, 0, new CardStats
        {
            Type = CardType.Attack,
            Target = TargetType.AnyEnemy,
            Rarity = CardRarity.Rare,
            EnergyCost = 2,
            CostsX = false,
            StarCost = -1,
            StarCostsX = false,
            Keywords = Keywords.None,
            MaxUpgradeLevel = 1,
            Vars = [20m, 20m],
        });
        return builder.Build();
    }

    private static void Append(ref Battle battle, Counter counter, int digit)
        => battle.SetCounter(counter, checked(battle.Counter(counter) * 10 + digit));

    [ContentId("DF1_TEST_GOLD_DOUBLER")]
    public sealed class GoldDoubler : RelicDef
    {
        public override decimal ModifyGoldGained(ref Exec x, Listener self, decimal amount)
        {
            Append(ref x.B, Counter.EnergySpentThisTurn, 1);
            return amount * 2m;
        }

        public override void AfterModifyingGoldGained(ref Exec x, Listener self, decimal amount)
            => Append(ref x.B, Counter.CardsDrawn, 1);

        public override void AfterGoldGained(ref Exec x, Listener self)
            => Append(ref x.B, Counter.FinishedCardPlays, 1);
    }

    [ContentId("DF1_TEST_GOLD_OBSERVER")]
    public sealed class GoldObserver : RelicDef
    {
        public override decimal ModifyGoldGained(ref Exec x, Listener self, decimal amount)
            => amount;

        public override void AfterModifyingGoldGained(ref Exec x, Listener self, decimal amount)
            => Append(ref x.B, Counter.CardsDrawn, 2);

        public override void AfterGoldGained(ref Exec x, Listener self)
            => Append(ref x.B, Counter.FinishedCardPlays, 2);
    }

    /// <summary>
    /// A player power that answers all three gold hooks. Native ModifyGoldGained includes combat
    /// listeners after the run listeners, while native AfterGoldGained does not include powers.
    /// </summary>
    [ContentId("DF1_TEST_GOLD_POWER")]
    public sealed class GoldPower : PowerDef
    {
        public override PowerType Type => PowerType.Buff;

        public override PowerStackType StackType => PowerStackType.Counter;

        public override decimal ModifyGoldGained(ref Exec x, Listener self, decimal amount)
        {
            Append(ref x.B, Counter.EnergySpentThisTurn, 3);
            return amount;
        }

        public override void AfterModifyingGoldGained(ref Exec x, Listener self, decimal amount)
            => Append(ref x.B, Counter.EnergySpentThisTurn, 4);

        public override void AfterGoldGained(ref Exec x, Listener self)
            => Append(ref x.B, Counter.EnergySpentThisTurn, 9);
    }
}
