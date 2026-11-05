using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class PotionStageContracts
{
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("potion-stage: " + message); }

    internal static void Run(SearchCase fixture, SearchPlan plan)
    {
        Assert(Registry.TryPotion("FIRE_POTION", out ushort fire), "fire potion");
        Assert(Registry.TryPotion("STRENGTH_POTION", out ushort cheap), "cheap potion fixture");
        Assert(Registry.TryPotion("FRUIT_JUICE", out ushort fruit), "fruit juice");
        Assert(Registry.TryCard("FEED", out ushort feed), "Feed");
        ScoreRules scores = fixture.Options.Scorer!.RulesForCodec;
        Assert(!scores.GrowthBudgets.IsEnabled && !scores.RequiredGrowthRewards.IsEnabled,
            "neutral score fixture");
        int savedFire = scores.PotionStrategicCost[fire], savedCheap = scores.PotionStrategicCost[cheap];
        try
        {
            scores.PotionStrategicCost[fire] = 9;
            scores.PotionStrategicCost[cheap] = 1;
            RulesDraft draft = new();
            draft.SetPotion(fire, new PotionStats { Target = TargetType.AnyEnemy, Rarity = PotionRarity.Common,
                StrategicCost = 9, Vars = [], VarProps = [] });
            draft.SetPotion(cheap, new PotionStats { Target = TargetType.Self, Rarity = PotionRarity.Common,
                StrategicCost = 1, Vars = [], VarProps = [] });
            Rules rules = draft.Build();
            Battle root = default;
            root.Player.CurrentHp = 75; root.Player.MaxHp = 75;
            root.PotionSlotCount = 1; root.Potions[0] = (ushort)(fire + 1);
            BeamOptions options = fixture.Options with
            {
                HasUnboundedGrowthTargets = false,
                TheftPolicy = null, EffectivePotionPolicy = PotionUseMode.Smart,
                Ordering = new(75, BossHpRelief.None, PostCombatRelicHealProfile.None),
                Potions = new(int.MinValue, null, new(PotionUseMode.Smart, []), PotionUseMode.Smart, false),
            };
            BeamSolver.Candidate selected = default(BeamSolver.Candidate) with
            {
                Key = default(BeamSolver.RouteKey) with { Won = true, Survives = true, StrategicHpDeficit = 8 },
            };
            bool Skip(Battle b, Rules r, BeamOptions o, BeamSolver.Candidate? c)
                => PotionStageGate.CanSkip([b], r, o, c, c);
            Assert(Skip(root, rules, options, selected), "eight loss cannot justify cost nine");
            BeamSolver.Candidate oldFree = selected with { Key = selected.Key with { StrategicHpDeficit = 28 } };
            Assert(!PotionStageGate.CanSkip([root], rules, options, selected, oldFree),
                "a later eight-loss zero-potion result must not replace the 28-loss Smart baseline");
            Assert(!Skip(root, rules, options, selected with { Key = selected.Key with { StrategicHpDeficit = 9 } }),
                "equal benefit can still improve tie breaks");
            Battle wounded = root; wounded.Player.CurrentHp = 70;
            Assert(!Skip(wounded, rules, options, selected), "pre-existing wounds can be healed");
            Battle history = root; history.SetCounter(Counter.RecoveredHp, 20);
            Assert(!Skip(history, rules, options, selected), "already recovered HP keeps search open");
            history = root; history.SetCounter(Counter.CumulativeHpLost, 1);
            Assert(!Skip(history, rules, options, selected), "existing damage ledger keeps search open");
            history = root; history.SetCounter(Counter.DeathSaveUseCount, 1);
            Assert(!Skip(history, rules, options, selected), "existing death-save use keeps search open");
            Assert(!Skip(root, rules, options, selected with { ExplicitPotionUseCount = 1 }),
                "after a one-potion victory search the unrestricted stage again");
            Assert(!Skip(root, rules, options, selected with { ProjectedDeathSaveUseCount = 1 }), "death save bypasses gate");
            RulesDraft lowPool = new();
            lowPool.SetPotion(fire, new PotionStats { Target = TargetType.AnyEnemy, Rarity = PotionRarity.Common,
                StrategicCost = 9, Vars = [], VarProps = [] });
            lowPool.SetPotion(cheap, new PotionStats { Target = TargetType.Self, Rarity = PotionRarity.Common,
                StrategicCost = 1, Vars = [], VarProps = [] });
            lowPool.SetPotionPool([cheap]);
            Assert(!Skip(root, lowPool.Build(), options, selected), "generated low-cost potion is included");
            PotionStrategySnapshot protectedCheap = new(PotionUseMode.Smart,
                [new(0, "STRENGTH_POTION", PotionDirective.Disabled)]);
            Assert(Skip(root, lowPool.Build(), options with { Potions = options.Potions with { Strategy = protectedCheap } }, selected),
                "disabled generated bottle does not reduce the allowed minimum");
            RulesDraft zeroPool = new();
            zeroPool.SetPotion(fire, new PotionStats { Target = TargetType.AnyEnemy, Rarity = PotionRarity.Common,
                StrategicCost = 0, Vars = [], VarProps = [] });
            Assert(!Skip(root, zeroPool.Build(), options, selected), "zero-cost potion keeps search open");
            Assert(!Skip(root, rules, options with { TheftPolicy = TheftPolicy.PreserveResources },
                selected with { Key = selected.Key with { OutstandingStolenResource = 1 } }), "stolen resources bypass gate");
            RulesDraft fruitPool = new();
            fruitPool.SetPotion(fire, new PotionStats { Target = TargetType.AnyEnemy, Rarity = PotionRarity.Common,
                StrategicCost = 9, Vars = [], VarProps = [] });
            fruitPool.SetPotionPool([fruit]);
            Assert(!Skip(root, fruitPool.Build(), options, selected), "possible max HP potion bypasses gate");
            RulesDraft feedRules = new();
            feedRules.SetCard(feed, 0, new CardStats { Type = CardType.Attack, Target = TargetType.AnyEnemy,
                Rarity = CardRarity.Rare, EnergyCost = 1, CostsX = false, StarCost = -1,
                StarCostsX = false, Keywords = Keywords.None, MaxUpgradeLevel = 1, Vars = [] });
            Assert(!Skip(root, feedRules.Build(), options, selected), "possible max HP card bypasses gate");
            Assert(Skip(root, rules, options with { Ordering = new(75, BossHpRelief.ActClearHeal,
                PostCombatRelicHealProfile.None) }, selected), "boss HP policy is applied");
        }
        finally
        {
            scores.PotionStrategicCost[fire] = savedFire;
            scores.PotionStrategicCost[cheap] = savedCheap;
        }

        SearchMember[] powered = PortfolioBuilder.Build(plan, power: true);
        SearchMember[] ordinary = PortfolioBuilder.Build(plan, power: false);
        Assert(powered.Length == 13 && ordinary.Length == 10,
            "compact portfolio sizes");
        Assert(powered.Take(6).Select(m => m.Potions).SequenceEqual(new[]
            {
                SearchPotionLimit.None, SearchPotionLimit.None, SearchPotionLimit.None,
                SearchPotionLimit.None, SearchPotionLimit.None, SearchPotionLimit.None,
            })
            && powered.Skip(6).Take(2).All(m => m.Potions == SearchPotionLimit.One)
            && powered.Skip(8).All(m => m.Potions == SearchPotionLimit.Allowed),
            "compact portfolio keeps the None/One/Allowed stage order");
        Assert(powered[1].Width == plan.NarrowWidth && powered[2].Width == plan.WideWidth
            && powered[8].Width == plan.BaselineWidth && powered[9].Width == plan.BaselineWidth
            && powered[10].Width == plan.WideWidth
            && powered.Skip(8).All(m => m.Width != plan.NarrowWidth),
            "compact portfolio widens the ordinary Allowed stage");
        Console.WriteLine("POTION_STAGE costs/history/targets/portfolios=pass");
    }
}
