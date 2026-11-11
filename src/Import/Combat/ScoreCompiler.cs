// Ported from CombatSolver src/Flash/Bridge/FlashScoreBridge.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). The existing engine's simulated snapshot
// is replaced by the imported Battle plus direct reads of the native models. The four rank
// baselines are computed by the Core scorer on the imported root, as FlashRankBaseline did.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Ai.Combat.Policy;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

using NativeCardType = MegaCrit.Sts2.Core.Entities.Cards.CardType;
using NativeCardTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;

namespace Sts2Solver.Import;

/// <summary>Root-local facts the score/bound bridges froze besides the policy.</summary>
public sealed record EvaluationRootFacts
{
    /// <summary>The root is a turn-setup root: the four rank baselines are forced to zero.</summary>
    public bool TurnSetupRoot { get; init; }

    public static EvaluationRootFacts Default { get; } = new();
}

/// <summary>
/// Compiles the per-content score table and the root-frozen policy settings from the native models,
/// the compiled rules and the imported root.
/// </summary>
internal static class ScoreCompiler
{
    private static readonly HashSet<Type> CalculatedSupportedTypes =
    [
        typeof(PreciseCut), typeof(Stack), typeof(Squeeze), typeof(Mirage), typeof(Rattle),
        typeof(MindBlast), typeof(GangUp), typeof(Mimic), typeof(KnifeTrap), typeof(Unleash),
        typeof(Radiate), typeof(PerfectedStrike), typeof(SovereignBlade), typeof(Supermassive),
        typeof(Sacrifice), typeof(TimesUp), typeof(MementoMori), typeof(SoulStorm), typeof(Voltaic),
        typeof(TearAsunder), typeof(ExpectAFight), typeof(HelixDrill), typeof(PullFromBelow),
        typeof(Normality), typeof(Synchronize), typeof(Protector), typeof(NoEscape), typeof(Flechettes),
        typeof(Rend), typeof(GoldAxe), typeof(FlakCannon), typeof(LunarBlast), typeof(Murder),
        typeof(CompileDriver), typeof(Finisher), typeof(Bully), typeof(BeatIntoShape),
        typeof(DeathMarch), typeof(DemonicShield), typeof(Barrage), typeof(CrescentSpear),
        typeof(BodySlam), typeof(AshenStrike),
    ];

    public static ScoreRules Compile(
        CombatState state,
        Player player,
        Rules rules,
        Battle root,
        SearchPolicy policy,
        EvaluationRootFacts facts,
        Battle? rankBaselineRoot = null,
        Rules? rankBaselineRules = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePolicy(policy);

        CardScore?[][] cards = CompileCards(player);
        PowerScore[] powers = CompilePowers(policy.Act3BossStrategy);
        int[] potionCosts = CompilePotionCosts(player);
        ValidateAdmittedCards(root, cards);
        PostCombatRelicHealProfile postCombatHeal = CompilePostCombatRelicHeal(player);
        BossHpRelief bossHpRelief = ResolveBossHpRelief(state, policy);
        ushort[] rootPotions = RootPotions(root);
        RelicCounterTarget[] relicTargets = CompileRelicTargets(state, player, root, policy);

        ScoreRules Build(int persistent, int suppression, int weak, int retained,
            Rules? ruleTable = null) => new()
        {
            Rules = ruleTable ?? rules,
            Cards = cards,
            Powers = powers,
            PotionStrategicCost = potionCosts,
            GrowthBudgets = policy.EffectiveGrowthBudgets.ToCore(),
            RequiredGrowthRewards = policy.EffectiveHasGrowthTargets
                ? policy.RequiredGrowthRewards.ToCore()
                : default,
            RelicCounterTargets = relicTargets,
            PostCombatRelicHeal = postCombatHeal,
            BossHpRelief = bossHpRelief,
            // The reference bridge freezes the request root's native max HP / enemy count. A fixed
            // prefix can add enemies (Fabricator's bots), so these must come from the pre-prefix
            // native state rather than the replayed Battle.
            InitialPlayerMaxHp = player.Creature.MaxHp,
            IsActEndingBoss = bossHpRelief != BossHpRelief.None,
            Act3BossStrategy = policy.Act3BossStrategy,
            IgnoreGrowthRewards = policy.IgnoreGrowthRewards,
            TheftPolicy = policy.TheftPolicy is { } theft ? (TheftPolicy)(int)theft : null,
            InitialEnemyCount = state.Enemies.Count,
            InitialPersistentBuffValue = persistent,
            InitialEnemyStrengthSuppression = suppression,
            InitialEnemyWeakTurns = weak,
            InitialRetainedAttackValue = retained,
            RootPotions = rootPotions,
            RootPotionSlotCount = root.PotionSlotCount,
            StrengthPowerId = PowerId<StrengthPower>(),
            WeakPowerId = PowerId<WeakPower>(),
            VulnerablePowerId = PowerId<VulnerablePower>(),
            PoisonPowerId = PowerId<PoisonPower>(),
            DemisePowerId = PowerId<DemisePower>(),
            DoomPowerId = PowerId<DoomPower>(),
            SandpitPowerId = PowerId<SandpitPower>(),
            SummonNextTurnPowerId = PowerId<SummonNextTurnPower>(),
            DieForYouPowerId = PowerId<DieForYouPower>(),
            SleightOfFleshPowerId = PowerId<SleightOfFleshPower>(),
            RetainHandPowerId = PowerId<RetainHandPower>(),
            EnergyNextTurnPowerId = PowerId<EnergyNextTurnPower>(),
            DrawCardsNextTurnPowerId = PowerId<DrawCardsNextTurnPower>(),
            StarNextTurnPowerId = PowerId<StarNextTurnPower>(),
            FreeAttackPowerId = PowerId<FreeAttackPower>(),
            VoidFormPowerId = PowerId<VoidFormPower>(),
            FreeSkillPowerId = PowerId<FreeSkillPower>(),
            FreePowerPowerId = PowerId<FreePowerPower>(),
            IllusionPowerId = PowerId<IllusionPower>(),
            ReattachPowerId = PowerId<ReattachPower>(),
            AdaptablePowerId = PowerId<AdaptablePower>(),
            BodySlamCardId = CardId<BodySlam>(),
            EradicateCardId = CardId<Eradicate>(),
            FiddleRelicId = RelicId<Fiddle>(),
            DropletOfPrecognitionPotionId = PotionId<DropletOfPrecognition>(),
            TheHuntCardId = CardId<TheHunt>(),
            TheHuntPowerId = PowerId<TheHuntPower>(),
            BufferPowerId = PowerId<MegaCrit.Sts2.Core.Models.Powers.BufferPower>(),
            NoDrawPowerId = PowerId<NoDrawPower>(),
            TheGambitPowerId = PowerId<TheGambitPower>(),
            FairyInABottlePotionId = PotionId<FairyInABottle>(),
            AmbergrisPotionId = PotionId<Ambergris>(),
            LizardTailRelicId = RelicId<LizardTail>(),
        };

        if (facts.TurnSetupRoot)
            return Build(0, 0, 0, 0);
        // The reference bridge freezes the four rank baselines on the request root (the state
        // before a fixed prefix), not on the replayed search root. That root can contain a card the
        // prefix removed, so the baseline scorer uses the request root's rule table too.
        Battle baseline = rankBaselineRoot ?? root;
        ScoreRules provisional = Build(0, 0, 0, 0, rankBaselineRules ?? rules);
        ScoreFeatures features = new Scorer(provisional).Features(baseline, 0);
        return Build(features.PersistentBuffValue, features.EnemyStrengthSuppression,
            features.EnemyWeakTurns, features.RetainedAttackValue);
    }

    private static void ValidatePolicy(SearchPolicy policy)
    {
        if (policy.HasDevelopmentStrategy)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                "DevelopmentStrategy：Prioritize/Retain 依赖 ActionCandidate 与 RankBest 必保通道");
        }
        if (!policy.IgnoreGrowthRewards && policy.HasThirdPartyGrowthSources)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                "第三方成长来源的额度未移植");
        }
        if (policy.EffectiveHasGrowthTargets && policy.HasThirdPartyGrowthTargets)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                "第三方成长来源的目标未移植");
        }
        if (policy.HasStrategicEffectMirrors)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                "第三方战略效果登记表非空");
        }
        if (policy.HasCardRemovalValueMirrors)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                "卡牌移除估值登记表非空");
        }
    }

    private static CardScore?[][] CompileCards(Player player)
    {
        CardScore?[][] result = new CardScore?[Registry.CardCount][];
        for (ushort id = 0; id < Registry.CardCount; id++)
        {
            CardDef def = Registry.Card(id);
            if (!RulesCompiler.CanonicalCardModels.TryGetValue(def.NativeId, out CardModel? canonical))
                continue;
            int maxUpgrade = canonical.MaxUpgradeLevel;
            CardScore?[] levels = new CardScore?[maxUpgrade + 1];
            for (int upgrade = 0; upgrade <= maxUpgrade; upgrade++)
            {
                CardModel card = RulesCompiler.CreateUpgradedCard(canonical, upgrade, player);
                levels[upgrade] = CompileCard(def, card);
            }
            result[id] = levels;
        }
        return result;
    }

    private static CardScore CompileCard(CardDef def, CardModel card)
    {
        bool hasBlockVar = false;
        bool hasDebuffVar = false;
        foreach (KeyValuePair<string, DynamicVar> entry in card.DynamicVars)
        {
            hasBlockVar |= IsBlockDynamicVar(entry.Key);
            hasDebuffVar |= IsDebuffDynamicVar(entry.Key);
        }

        IReadOnlyList<string> names = def.VarNames;
        return new CardScore
        {
            BaseDamage = DynamicVarBaseValue(card.DynamicVars, "Damage"),
            BaseBlock = DynamicVarBaseValue(card.DynamicVars, "Block"),
            BaseCards = DynamicVarBaseValue(card.DynamicVars, "Cards"),
            IsPower = card.Type == NativeCardType.Power,
            DynamicCardType = def.HasDynamicCardType,
            PrimaryMoveToHandChoice = card is Hologram or Graveblast or SecretWeapon or SecretTechnique
                or Wish or Dredge or NeowsFury or SeekerStrike,
            DamageVar = VarIndex(names, "Damage"),
            BlockVar = VarIndex(names, "Block"),
            CardsVar = VarIndex(names, "Cards"),
            RepeatVar = VarIndex(names, "Repeat"),
            ShivCardsVar = VarIndex(names, "Cards"),
            ShivShivsVar = VarIndex(names, "Shivs"),
            CalculationBaseVar = VarIndex(names, "CalculationBase"),
            CalculationExtraVar = names.Contains("CalculationExtra")
                ? VarIndex(names, "CalculationExtra")
                : VarIndex(names, "ExtraDamage"),
            DoomThresholdVar = VarIndex(names, "DoomThreshold"),
            AttackHits = card switch
            {
                TwinStrike or RipAndTear => AttackHitKind.Two,
                SwordBoomerang or SovereignBlade => AttackHitKind.RepeatVar,
                _ => AttackHitKind.One,
            },
            ShivSupply = card switch
            {
                BladeDance or CloakAndDagger => ShivSupplyKind.CardsVar,
                FanOfKnives => ShivSupplyKind.ShivsVar,
                _ => ShivSupplyKind.None,
            },
            HasBlockDynamicVar = hasBlockVar,
            HasDebuffDynamicVar = hasDebuffVar,
            HasTurnEndInHandEffect = card.HasTurnEndInHandEffect,
            HasOstyAttackTag = card.Tags.Contains(NativeCardTag.OstyAttack),
            HasShivTag = card.Tags.Contains(NativeCardTag.Shiv),
            LatentValue = LatentValue(card),
            LatentTrait = LatentTrait(card),
            Calculated = CalculatedKindOf(card),
        };
    }

    private static sbyte VarIndex(IReadOnlyList<string> names, string name)
    {
        for (int index = 0; index < names.Count; index++)
        {
            if (names[index] == name)
                return checked((sbyte)index);
        }
        return -1;
    }

    private static double DynamicVarBaseValue(DynamicVarSet vars, string key)
        => vars.TryGetValue(key, out DynamicVar? variable) ? (double)variable.BaseValue : 0d;

    private static bool IsDebuffDynamicVar(string key)
        => key.Contains("Weak", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Poison", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Doom", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Debuff", StringComparison.OrdinalIgnoreCase)
            || key.Contains("StrengthLoss", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockDynamicVar(string key)
        => key.Contains("Block", StringComparison.OrdinalIgnoreCase);

    private static CalculatedKind CalculatedKindOf(CardModel card)
    {
        if (!card.DynamicVars.ContainsKey("CalculatedDamage")
            || !CalculatedSupportedTypes.Contains(card.GetType()))
        {
            return CalculatedKind.None;
        }
        return card switch
        {
            BodySlam or DemonicShield => CalculatedKind.PlayerBlock,
            PerfectedStrike => CalculatedKind.StrikeCardCount,
            AshenStrike => CalculatedKind.ExhaustPileCount,
            ExpectAFight => CalculatedKind.PlayerStrength,
            Bully => CalculatedKind.TargetVulnerable,
            MindBlast => CalculatedKind.DrawPileCount,
            Rend => CalculatedKind.TargetDebuffPowerCount,
            GoldAxe => CalculatedKind.FinishedCardPlays,
            MementoMori => CalculatedKind.CardsDiscardedThisTurn,
            PreciseCut => CalculatedKind.NegativeHandCount,
            Murder => CalculatedKind.CardsDrawn,
            CrescentSpear => CalculatedKind.StarCardCount,
            Unleash => CalculatedKind.OstyCurrentHp,
            Rattle => CalculatedKind.OstyAttacksPlusOne,
            Squeeze => CalculatedKind.OstyAttackCardsMinusSelf,
            Protector => CalculatedKind.OstyMaxHp,
            SoulStorm => CalculatedKind.ExhaustSoulCount,
            PullFromBelow => CalculatedKind.EtherealPlays,
            NoEscape => CalculatedKind.DoomOverThreshold,
            TimesUp => CalculatedKind.TargetDoom,
            DeathMarch => CalculatedKind.NonHandDraws,
            Supermassive => CalculatedKind.GeneratedCardCount,
            _ => CalculatedKind.Unsupported,
        };
    }

    private static int LatentValue(CardModel card) => card switch
    {
        EchoForm => 12,
        MegaCrit.Sts2.Core.Models.Cards.Buffer => 8,
        Defragment or Hotfix => 6,
        LightningRod => 4,
        Thunder => 3,
        DemonForm or CreativeAi => 5,
        MasterPlanner => 4,
        _ when card.Type == NativeCardType.Power => 2,
        _ => 0,
    };

    private static SetupTraits LatentTrait(CardModel card) => card switch
    {
        EchoForm => SetupTraits.EchoForm,
        MegaCrit.Sts2.Core.Models.Cards.Buffer => SetupTraits.Buffer,
        Defragment or Hotfix => SetupTraits.Focus,
        LightningRod => SetupTraits.OrbEngine,
        Thunder => SetupTraits.Thunder,
        DemonForm or CreativeAi => SetupTraits.RecurringScaling,
        _ => SetupTraits.None,
    };

    private static PowerScore[] CompilePowers(bool act3BossStrategy)
    {
        PowerScore[] result = new PowerScore[Registry.PowerCount];
        for (ushort id = 0; id < Registry.PowerCount; id++)
        {
            PowerDef def = Registry.Power(id);
            PowerModel model = RulesCompiler.CanonicalPowerModels.TryGetValue(def.NativeId,
                out PowerModel? canonical)
                ? canonical
                : throw new UnsupportedImportException(ImportReason.UnsupportedPower,
                    $"原版没有 Power {def.NativeId}");
            result[id] = new PowerScore
            {
                Kind = StrategicKindOf(model, act3BossStrategy),
                Requirements = (StrategicRequirements)(int)Requirements(model, act3BossStrategy),
                Trait = PersistentPowerTrait(model),
                IsTemporary = model is ITemporaryPower,
                Type = (PowerType)(byte)model.Type,
                DanseMacabreEnergyThreshold = model is DanseMacabrePower danseMacabre
                    ? danseMacabre.DynamicVars.Energy.IntValue
                    : 0,
            };
        }
        return result;
    }

    private static StrategicKind StrategicKindOf(PowerModel power, bool act3BossStrategy) => power switch
    {
        AfterimagePower => StrategicKind.Afterimage,
        MegaCrit.Sts2.Core.Models.Powers.BufferPower => StrategicKind.Buffer,
        FeelNoPainPower => StrategicKind.FeelNoPain,
        DarkEmbracePower => StrategicKind.DarkEmbrace,
        ThornsPower => StrategicKind.Thorns,
        EchoFormPower => StrategicKind.EchoForm,
        EnvenomPower => StrategicKind.Envenom,
        AccuracyPower => StrategicKind.Accuracy,
        SleightOfFleshPower => StrategicKind.SleightOfFlesh,
        StrengthPower => StrategicKind.Strength,
        LethalityPower => StrategicKind.Lethality,
        ReaperFormPower => StrategicKind.ReaperForm,
        DexterityPower => StrategicKind.Dexterity,
        DemonFormPower => StrategicKind.DemonForm,
        CuriousPower => StrategicKind.Curious,
        CorruptionPower => StrategicKind.Corruption,
        CreativeAiPower => StrategicKind.CreativeAi,
        IterationPower => StrategicKind.Iteration,
        MasterPlannerPower => StrategicKind.MasterPlanner,
        PagestormPower when act3BossStrategy => StrategicKind.Pagestorm,
        DanseMacabrePower when act3BossStrategy => StrategicKind.DanseMacabre,
        DemesnePower when act3BossStrategy => StrategicKind.Demesne,
        PrepTimePower when act3BossStrategy => StrategicKind.PrepTime,
        FocusPower => StrategicKind.Focus,
        FurnacePower => StrategicKind.Furnace,
        ThunderPower => StrategicKind.Thunder,
        LightningRodPower => StrategicKind.LightningRod,
        AutomationPower => StrategicKind.RecurringEnergy,
        OrbitPower => StrategicKind.OrbitEnergy,
        _ => StrategicKind.Scaling,
    };

    private static StrategicRequirements Requirements(PowerModel power, bool act3BossInteractions)
        => power switch
        {
            AfterimagePower => StrategicRequirements.UsefulCardPlays,
            MegaCrit.Sts2.Core.Models.Powers.BufferPower => StrategicRequirements.RemainingTurns,
            FeelNoPainPower => StrategicRequirements.ExhaustPlays,
            DarkEmbracePower => StrategicRequirements.ExhaustPlays
                | StrategicRequirements.AverageCardValue,
            EchoFormPower => StrategicRequirements.UsefulCardPlays
                | StrategicRequirements.BestCardValue,
            EnvenomPower => StrategicRequirements.AttackPlays,
            StrengthPower => StrategicRequirements.AttackHits,
            AccuracyPower => StrategicRequirements.ShivPlays,
            SleightOfFleshPower => StrategicRequirements.DebuffApplications,
            LethalityPower or ReaperFormPower => StrategicRequirements.AttackPlays
                | StrategicRequirements.AverageAttackValue,
            DexterityPower => StrategicRequirements.BlockSkillPlays,
            DemonFormPower => StrategicRequirements.AttackPlays
                | StrategicRequirements.RemainingTurns,
            CuriousPower => StrategicRequirements.PowerEnergySpend
                | StrategicRequirements.PowerPlays
                | StrategicRequirements.AverageCardValue,
            CorruptionPower => StrategicRequirements.SkillEnergySpend
                | StrategicRequirements.AverageCardValue,
            OrbitPower or AutomationPower => StrategicRequirements.RemainingTurns
                | StrategicRequirements.AverageCardValue,
            CreativeAiPower => StrategicRequirements.RemainingTurns
                | StrategicRequirements.AverageCardValue,
            IterationPower => StrategicRequirements.StatusDrawTriggers
                | StrategicRequirements.AverageCardValue,
            MasterPlannerPower => StrategicRequirements.SkillPlays
                | StrategicRequirements.AverageCardValue,
            PagestormPower when act3BossInteractions => StrategicRequirements.RemainingTurns
                | StrategicRequirements.AverageCardValue,
            DanseMacabrePower when act3BossInteractions =>
                StrategicRequirements.RemainingTurns,
            DemesnePower when act3BossInteractions => StrategicRequirements.RemainingTurns
                | StrategicRequirements.AverageCardValue,
            PrepTimePower when act3BossInteractions => StrategicRequirements.RemainingTurns
                | StrategicRequirements.AttackPlays,
            FocusPower or FurnacePower or ThunderPower or LightningRodPower
                => StrategicRequirements.RemainingTurns,
            _ => StrategicRequirements.None,
        };

    private static SetupTraits PersistentPowerTrait(PowerModel power) => power switch
    {
        CuriousPower => SetupTraits.Curious,
        EchoFormPower => SetupTraits.EchoForm,
        MegaCrit.Sts2.Core.Models.Powers.BufferPower => SetupTraits.Buffer,
        FocusPower => SetupTraits.Focus,
        ThunderPower => SetupTraits.Thunder,
        LightningRodPower => SetupTraits.OrbEngine,
        DemonFormPower or CreativeAiPower => SetupTraits.RecurringScaling,
        _ => SetupTraits.None,
    };

    private static int[] CompilePotionCosts(Player player)
    {
        bool renewablePotionShapedRock = player.Relics
            .OfType<PetrifiedToad>().Any(static relic => !relic.IsMelted);
        int[] costs = new int[Registry.PotionCount];
        for (ushort id = 0; id < Registry.PotionCount; id++)
        {
            PotionDef def = Registry.Potion(id);
            PotionModel potion = RulesCompiler.CanonicalPotionModels.TryGetValue(def.NativeId,
                out PotionModel? canonical)
                ? canonical
                : throw new UnsupportedImportException(ImportReason.UnsupportedPotion,
                    $"原版没有药水 {def.NativeId}");
            costs[id] = StrategicCost(potion, renewablePotionShapedRock);
        }
        return costs;
    }

    /// <summary><c>PotionValuationRegistry.Default.StrategicHpCost</c> at a62f1850.</summary>
    private static int StrategicCost(PotionModel potion, bool renewablePotionShapedRock)
    {
        if (potion.Rarity == MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Token
            || renewablePotionShapedRock && potion is PotionShapedRock)
        {
            return 0;
        }
        return potion.Id.Entry switch
        {
            "GLOWWATER_POTION" or "SWIFT_POTION" or "GAMBLERS_BREW" or "DUPLICATOR"
                or "OROBIC_ACID" or "POT_OF_GHOULS" => 18,
            "DISTILLED_CHAOS" or "CLARITY" or "RADIANT_TINCTURE" or "CURE_ALL"
                or "LIQUID_MEMORIES" or "BOTTLED_POTENTIAL" or "TOUCH_OF_INSANITY" => 14,
            _ => 9,
        };
    }

    private static void ValidateAdmittedCards(Battle root, CardScore?[][] cards)
    {
        for (int position = 0; position < root.CardCount; position++)
        {
            ref readonly Card card = ref root.Cards[position];
            CardScore score = Score(cards, card.Id, card.Upgrade);
            if (score.Calculated == CalculatedKind.Unsupported)
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedCard,
                    $"{Registry.Card(card.Id).NativeId} 的计算伤害乘数未移植");
            }
        }
    }

    private static CardScore Score(CardScore?[][] cards, ushort id, int upgrade)
        => id < cards.Length
            && cards[id] is { } levels
            && (uint)upgrade < (uint)levels.Length
            && levels[upgrade] is { } score
                ? score
                : throw new UnsupportedImportException(ImportReason.UnsupportedCard,
                    $"评分表缺少卡牌 {id}+{upgrade}");

    internal static PostCombatRelicHealProfile CompilePostCombatRelicHeal(Player player)
    {
        int unconditionalHeal = 0;
        int woundedHeal = 0;
        int woundedHpPercent = 0;
        foreach (RelicModel relic in player.Relics)
        {
            if (relic.IsMelted)
                continue;
            switch (relic)
            {
                case BurningBlood or BlackBlood:
                    unconditionalHeal += relic.DynamicVars.TryGetValue("Heal",
                        out DynamicVar? heal) ? heal.IntValue : 0;
                    break;
                case MeatOnTheBone:
                    woundedHeal += relic.DynamicVars.TryGetValue("Heal",
                        out DynamicVar? wounded) ? wounded.IntValue : 0;
                    woundedHpPercent = Math.Max(woundedHpPercent,
                        relic.DynamicVars.TryGetValue("HpThreshold", out DynamicVar? threshold)
                            ? threshold.IntValue : 0);
                    break;
            }
        }
        return new PostCombatRelicHealProfile(unconditionalHeal, woundedHeal, woundedHpPercent);
    }

    /// <summary>
    /// One target per owned, unmelted relic of <see cref="RelicCounterCatalog"/> that the policy lists:
    /// end the fight with its counter one short of triggering. None when the fight ends the run, because
    /// no combat follows to use the counter. The growth switch (<c>IgnoreGrowthRewards</c>) does not
    /// apply: each relic has its own switch.
    /// </summary>
    internal static RelicCounterTarget[] CompileRelicTargets(CombatState state, Player player, Battle root,
        SearchPolicy policy)
    {
        if (ResolveNativeHpRelief(state) == BossHpRelief.RunEnding)
            return [];
        List<RelicCounterTarget> targets = [];
        IReadOnlyList<RelicModel> relics = player.Relics;
        for (int index = 0; index < relics.Count; index++)
        {
            RelicModel relic = relics[index];
            if (relic.IsMelted || !RelicCounterCatalog.TryRead(relic, out _, out int period)
                || !policy.RelicCounterAllowances.TryGetValue(relic.Id.Entry, out int allowance))
            {
                continue;
            }
            if (index >= root.RelicCount
                || Registry.Relic(root.Relics[index].Id).NativeId != relic.Id.Entry)
            {
                throw new UnsupportedImportException(ImportReason.Unrepresentable,
                    $"遗物 {relic.Id.Entry} 不在战斗状态的第 {index} 格");
            }
            targets.Add(new RelicCounterTarget(checked((byte)index), checked((byte)period),
                checked((byte)RelicCounterCatalog.TargetCount(period)), allowance));
        }
        return [.. targets];
    }

    internal static BossHpRelief ResolveBossHpRelief(CombatState state, SearchPolicy policy)
    {
        BossHpRelief encounter = ResolveNativeHpRelief(state);
        return encounter switch
        {
            BossHpRelief.ActClearHeal
                when policy.ActTransitionBossHpStrategy == SearchBossHpStrategy.MinimizeHpLoss => BossHpRelief.None,
            BossHpRelief.RunEnding
                when policy.FinalBossHpStrategy == SearchBossHpStrategy.MinimizeHpLoss => BossHpRelief.None,
            _ => encounter,
        };
    }

    /// <summary><c>ActEndingBossPolicy.ResolveHpRelief</c> over the native run state.</summary>
    internal static BossHpRelief ResolveNativeHpRelief(CombatState state)
    {
        if (state.Encounter?.RoomType != MegaCrit.Sts2.Core.Rooms.RoomType.Boss)
            return BossHpRelief.None;
        RunState runState = state.RunState as RunState
            ?? throw new UnsupportedImportException(ImportReason.Unrepresentable,
                "Boss 战没有可识别的 RunState");
        if (runState.CurrentActIndex < runState.Acts.Count - 1)
            return BossHpRelief.ActClearHeal;
        return runState.Act.SecondBossEncounter is not { } second
            || second.Id == state.Encounter.Id
                ? BossHpRelief.RunEnding
                : BossHpRelief.None;
    }

    private static ushort[] RootPotions(Battle root)
    {
        ushort[] potions = new ushort[Limits.PotionSlots];
        for (int slot = 0; slot < Limits.PotionSlots && slot < root.PotionSlotCount; slot++)
            potions[slot] = root.Potions[slot];
        return potions;
    }

    private static ushort PowerId<TPower>() where TPower : PowerModel
        => Registry.TryPower(ModelDb.Power<TPower>().Id.Entry, out ushort id)
            ? id
            : ushort.MaxValue;

    private static ushort CardId<TCard>() where TCard : CardModel
        => Registry.TryCard(ModelDb.Card<TCard>().Id.Entry, out ushort id)
            ? id
            : ushort.MaxValue;

    private static ushort PotionId<TPotion>() where TPotion : PotionModel
        => Registry.TryPotion(ModelDb.Potion<TPotion>().Id.Entry, out ushort id)
            ? id
            : ushort.MaxValue;

    private static ushort RelicId<TRelic>() where TRelic : RelicModel
        => Registry.TryRelic(ModelDb.Relic<TRelic>().Id.Entry, out ushort id)
            ? id
            : ushort.MaxValue;
}
