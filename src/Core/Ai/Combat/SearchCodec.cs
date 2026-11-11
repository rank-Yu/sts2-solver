using System.Text;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Quality;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// One complete Sts2Solver search request: the frozen rules, every root battle and the exact
/// <see cref="BeamOptions"/> the request compiler constructed.
/// </summary>
internal sealed record SearchCase(Rules Rules, Battle[] Battles, BeamOptions Options)
{
    /// <summary>
    /// Host-only turn-setup plans, aligned with <see cref="Battles"/> by root index. The plans carry
    /// the native choice values the setup made; they are not part of SREQ and
    /// <see cref="SearchCodec"/> never writes them. Null for ordinary single-root requests.
    /// </summary>
    internal IReadOnlyList<PlannedChoice[]>? TurnSetupPlans { get; init; }
}

/// <summary>
/// Deterministic encoding of one <see cref="BeamSolver"/> request and result. The request
/// carries the complete rule and score/bound tables Sts2Solver reads, so a decoded request can be solved
/// without the existing engine. Host-only callbacks (<see cref="BeamOptions.Pacer"/> and
/// <see cref="BeamOptions.Trace"/>) are recorded as presence bits and not reconstructed: they
/// do not participate in route selection, but the report lists them explicitly.
/// </summary>
internal static class SearchCodec
{
    internal const uint RequestMagic = 0x51455253; // "SREQ"
    internal const uint ResultMagic = 0x53455253; // "SRES"
    internal const uint BrightestFlameExtensionMagic = 0x314D4C46; // "FLM1"
    internal const uint IncumbentSeedExtensionMagic = 0x31434E49; // "INC1"

    /// <summary>
    /// Search request/result layout versions. They are independent of <see cref="Codec.FormatVersion"/>:
    /// removing deleted option slots rewrites SREQ/SRES without changing BATL/RULE/GOLD.
    /// </summary>
    internal const ushort RequestFormatVersion = 10;
    internal const ushort ResultFormatVersion = 6;

    internal static byte[] EncodeRequest(SearchCase search)
        => Codec.ToBytes(writer =>
        {
            writer.Write(RequestMagic);
            writer.Write(RequestFormatVersion);
            byte[] rules = RulesCodec.Encode(search.Rules);
            writer.Write(rules.Length);
            writer.Write(rules);
            writer.Write(search.Battles.Length);
            foreach (Battle battle in search.Battles)
            {
                byte[] bytes = BattleCodec.Encode(battle);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            EncodeOptions(writer, search.Options, search.Rules);
        });

    internal static SearchCase DecodeRequest(byte[] bytes)
        => Codec.Read(bytes, "Sts2Solver 搜索请求", reader =>
        {
            if (reader.ReadUInt32() != RequestMagic)
                throw new InvalidDataException("Sts2Solver 搜索请求魔数不符。");
            ushort version = reader.ReadUInt16();
            if (version != RequestFormatVersion)
                throw new InvalidDataException($"Sts2Solver 搜索请求版本 {version} 不受支持。");
            int rulesLength = Codec.ReadCount(reader, 1 << 28, "规则字节");
            byte[] rulesBytes = reader.ReadBytes(rulesLength);
            if (rulesBytes.Length != rulesLength)
                throw new InvalidDataException("Sts2Solver 搜索请求的规则数据被截断。");
            Rules rules = RulesCodec.Decode(rulesBytes);
            int battleCount = Codec.ReadCount(reader, 4096, "搜索根");
            Battle[] battles = new Battle[battleCount];
            for (int index = 0; index < battleCount; index++)
            {
                int length = Codec.ReadCount(reader,
                    BattleCodec.HeaderSize + Battle.ExpectedSize, "搜索根字节");
                byte[] battleBytes = reader.ReadBytes(length);
                if (battleBytes.Length != length)
                    throw new InvalidDataException("Sts2Solver 搜索根数据被截断。");
                battles[index] = BattleCodec.Decode(battleBytes);
            }
            BeamOptions options = DecodeOptions(reader, rules);
            return new SearchCase(rules, battles, options);
        });

    private static void EncodeOptions(BinaryWriter writer, BeamOptions options, Rules rules)
    {
        writer.Write(options.BeamWidth);
        writer.Write(options.MaxExpandedNodes);
        writer.Write(options.MaxTurns);
        writer.Write(options.SoftTimeBudget.Ticks);
        EncodePotionPolicy(writer, options.Potions);
        writer.Write(options.Scorer != null);
        if (options.Scorer is { } scorer)
        {
            writer.Write(scorer.BaseOnly);
            writer.Write(scorer.Offensive);
            EncodeScoreRules(writer, scorer.RulesForCodec);
        }
        writer.Write(options.Ordering != null);
        if (options.Ordering is { } ordering)
            EncodeOrdering(writer, ordering);
        writer.Write(options.Bound != null);
        if (options.Bound is { } bound)
            EncodeBoundConfig(writer, bound.ConfigForCodec);
        writer.Write((int)options.EffectivePotionPolicy);
        writer.Write(options.TheftPolicy != null);
        if (options.TheftPolicy is { } theft)
            writer.Write((int)theft);
        writer.Write(options.PotionFreeBaseline != null);
        if (options.PotionFreeBaseline is { } baseline)
            EncodePotionFreeBaseline(writer, baseline);
        writer.Write(options.AuditPotionPolicy);
        writer.Write(options.Act3BossStrategy);
        writer.Write(options.InitialPlayerTurn);
        writer.Write(options.SecondRankBand);
        writer.Write(options.AggressivePowerCommitment);
        writer.Write(options.PreserveReplayAllocatorOpening);
        writer.Write(options.PowerCommitments != null);
        if (options.PowerCommitments is { } commitments)
            EncodePowerCommitments(writer, commitments);
        if (options.BrightestFlameMaxHpLossLimit is { } flameLimit)
        {
            if (flameLimit is < 0 or > 1000)
                throw new InvalidDataException("至亮之焰最大生命消耗上限必须在 0..1000。");
            if (options.InitialBrightestFlameMaxHpSpent < 0)
                throw new InvalidDataException("根上已消耗的最大生命不能为负。");
            writer.Write(BrightestFlameExtensionMagic);
            writer.Write(flameLimit);
            writer.Write(options.InitialBrightestFlameMaxHpSpent);
        }
        if (options.Scorer?.QueenPolicy is { } queen)
        {
            if (!queen.PreserveKillTiming)
                throw new InvalidDataException("QSP1 女王能力补搜已删除。");
            writer.Write(0x32505351u); // QSP2 retention.
            queen.Encode(writer);
        }
        if (options.Scorer?.SetupPreference == true)
        {
            if (options.Scorer.QueenPolicy is not null)
                throw new InvalidDataException("Setup and queen scoring cannot be combined.");
            writer.Write(options.Scorer.SetupPolicy!.Continuous ? 0x32505553u : 0x31505553u); // SUP2 continuous / SUP1 activation.
            options.Scorer.SetupPolicy!.Encode(writer);
        }
        if (options.IncumbentSeed is { } seed)
        {
            writer.Write(IncumbentSeedExtensionMagic);
            writer.Write(seed.EffectiveHpDeficit);
            writer.Write(seed.CombatEndedTurn);
            writer.Write(seed.GoalHpCredit);
            writer.Write(seed.GoalCount);
            writer.Write(seed.GoalsComplete);
        }
    }

    private static BeamOptions DecodeOptions(BinaryReader reader, Rules rules)
    {
        int beamWidth = reader.ReadInt32();
        int maxExpanded = reader.ReadInt32();
        int maxTurns = reader.ReadInt32();
        TimeSpan budget = TimeSpan.FromTicks(reader.ReadInt64());
        PotionPolicy potions = DecodePotionPolicy(reader);
        bool hasScorer = Codec.ReadBool(reader);
        Scorer? scorer = null;
        bool baseOnly = false;
        bool offensive = false;
        if (hasScorer)
        {
            baseOnly = Codec.ReadBool(reader);
            offensive = Codec.ReadBool(reader);
            scorer = new Scorer(DecodeScoreRules(reader, rules),
                baseOnly: baseOnly, offensive: offensive);
        }
        bool hasOrdering = Codec.ReadBool(reader);
        RouteOrdering? ordering = hasOrdering ? DecodeOrdering(reader) : null;
        bool hasBound = Codec.ReadBool(reader);
        BoundFeatures? bound = hasBound ? new BoundFeatures(DecodeBoundConfig(reader)) : null;
        PotionUseMode effective = ReadEnumInt<PotionUseMode>(reader, "有效药水政策");
        TheftPolicy? theft = Codec.ReadBool(reader)
            ? ReadEnumInt<TheftPolicy>(reader, "窃取政策") : null;
        bool hasBaseline = Codec.ReadBool(reader);
        PotionFreePolicyBaseline? baseline = hasBaseline ? DecodePotionFreeBaseline(reader) : null;
        bool audit = Codec.ReadBool(reader);
        bool act3 = Codec.ReadBool(reader);
        int initialTurn = reader.ReadInt32();
        bool secondRank = Codec.ReadBool(reader);
        bool aggressive = Codec.ReadBool(reader);
        bool preserveOpening = Codec.ReadBool(reader);
        bool hasCommitments = Codec.ReadBool(reader);
        PowerCommitmentCatalog? commitments = hasCommitments ? DecodePowerCommitments(reader) : null;
        int? flameLimit = null;
        int flameSpent = 0;
        bool sawFlame = false;
        bool sawSetup = false;
        bool sawQueen = false;
        SearchIncumbent? seed = null;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            uint extension = reader.ReadUInt32();
            if (extension == IncumbentSeedExtensionMagic)
            {
                if (seed is not null)
                    throw new InvalidDataException("初始现任最优扩展重复。");
                seed = new SearchIncumbent(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadBoolean());
            }
            else if (extension == BrightestFlameExtensionMagic)
            {
                if (sawFlame)
                    throw new InvalidDataException("至亮之焰扩展重复。");
                int limit = reader.ReadInt32();
                if (limit is < 0 or > 1000)
                    throw new InvalidDataException($"至亮之焰最大生命消耗上限 {limit} 非法。");
                int spent = reader.ReadInt32();
                if (spent < 0)
                    throw new InvalidDataException($"根上已消耗的最大生命 {spent} 非法。");
                flameLimit = limit;
                flameSpent = spent;
                sawFlame = true;
            }
            else if (extension is 0x31505553u or 0x32505553u)
            {
                if (scorer is null)
                    throw new InvalidDataException("Search extension requires a scorer.");
                if (sawSetup || sawQueen)
                    throw new InvalidDataException("设置偏好与女王能力扩展不能重复或组合。");
                SetupSearchPolicy setup = SetupSearchPolicy.Decode(reader,
                    continuous: extension == 0x32505553u);
                scorer = new Scorer(scorer.RulesForCodec, baseOnly, offensive, setup: setup);
                sawSetup = true;
            }
            else if (extension == 0x32505351u)
            {
                if (scorer is null)
                    throw new InvalidDataException("Search extension requires a scorer.");
                if (sawSetup || sawQueen)
                    throw new InvalidDataException("设置偏好与女王能力扩展不能重复或组合。");
                QueenSearchPolicy queen = QueenSearchPolicy.Decode(reader) with { PreserveKillTiming = true };
                scorer = new Scorer(scorer.RulesForCodec, baseOnly, offensive, queen: queen);
                sawQueen = true;
            }
            else throw new InvalidDataException("Unknown search request extension.");
        }
        return new BeamOptions(beamWidth, maxExpanded, maxTurns, budget)
        {
            Potions = potions,
            Scorer = scorer,
            Ordering = ordering,
            Bound = bound,
            EffectivePotionPolicy = effective,
            TheftPolicy = theft,
            PotionFreeBaseline = baseline,
            AuditPotionPolicy = audit,
            Act3BossStrategy = act3,
            InitialPlayerTurn = initialTurn,
            SecondRankBand = secondRank,
            AggressivePowerCommitment = aggressive,
            PreserveReplayAllocatorOpening = preserveOpening,
            PowerCommitments = commitments,
            BrightestFlameMaxHpLossLimit = flameLimit,
            InitialBrightestFlameMaxHpSpent = flameSpent,
            IncumbentSeed = seed,
        };
    }

    private static void EncodePotionPolicy(BinaryWriter writer, PotionPolicy policy)
    {
        writer.Write(policy.EarliestPlayerTurn);
        EncodePotionStrategy(writer, policy.Strategy);
        writer.Write((int)policy.EffectivePolicy);
        writer.Write(policy.ForceAllDisabled);
    }

    private static PotionPolicy DecodePotionPolicy(BinaryReader reader)
    {
        int earliest = reader.ReadInt32();
        PotionStrategySnapshot strategy = DecodePotionStrategy(reader);
        PotionUseMode effective = ReadEnumInt<PotionUseMode>(reader, "药水政策");
        bool forceDisabled = Codec.ReadBool(reader);
        return new PotionPolicy(earliest, strategy, effective, forceDisabled);
    }

    private static void EncodePotionStrategy(BinaryWriter writer, PotionStrategySnapshot strategy)
    {
        writer.Write((int)strategy.DefaultPolicy);
        writer.Write(strategy.Directives.Count);
        foreach (PotionSlotDirective directive in strategy.Directives)
        {
            writer.Write(directive.Slot);
            Codec.WriteString(writer, directive.PotionId);
            writer.Write((int)directive.Directive);
        }
    }

    private static PotionStrategySnapshot DecodePotionStrategy(BinaryReader reader)
    {
        PotionUseMode defaultPolicy = ReadEnumInt<PotionUseMode>(reader, "药水默认政策");
        int count = Codec.ReadCount(reader, Limits.PotionSlots, "药水指令");
        PotionSlotDirective[] directives = new PotionSlotDirective[count];
        for (int index = 0; index < count; index++)
        {
            int slot = reader.ReadInt32();
            string potionId = Codec.ReadString(reader)
                ?? throw new InvalidDataException("药水指令 id 缺失。");
            directives[index] = new PotionSlotDirective(slot, potionId,
                ReadEnumInt<PotionDirective>(reader, "药水指令"));
        }
        return new PotionStrategySnapshot(defaultPolicy, directives);
    }

    private static void EncodeOrdering(BinaryWriter writer, RouteOrdering ordering)
    {
        writer.Write(ordering.InitialPlayerHp);
        writer.Write((int)ordering.BossHpRelief);
        EncodePostCombatHeal(writer, ordering.PostCombatRelicHeal);
    }

    private static RouteOrdering DecodeOrdering(BinaryReader reader)
        => new(reader.ReadInt32(),
            ReadEnumInt<BossHpRelief>(reader, "Boss HP 减免"),
            DecodePostCombatHeal(reader));

    private static void EncodePotionFreeBaseline(BinaryWriter writer, PotionFreePolicyBaseline baseline)
    {
        writer.Write(baseline.Won);
        writer.Write(baseline.HpDeficit);
        writer.Write(baseline.PlayerHp);
        writer.Write(baseline.CombatEndedTurn != null);
        writer.Write(baseline.CombatEndedTurn ?? 0);
        writer.Write(baseline.DeathSaveUseCount);
    }

    private static PotionFreePolicyBaseline DecodePotionFreeBaseline(BinaryReader reader)
    {
        bool won = Codec.ReadBool(reader);
        int deficit = reader.ReadInt32();
        int hp = reader.ReadInt32();
        bool hasTurn = Codec.ReadBool(reader);
        int turnValue = reader.ReadInt32();
        int? turn = hasTurn ? turnValue : null;
        int deathSaves = reader.ReadInt32();
        return new PotionFreePolicyBaseline(won, deficit, hp, turn) { DeathSaveUseCount = deathSaves };
    }

    private static void EncodePowerCommitments(BinaryWriter writer, PowerCommitmentCatalog catalog)
    {
        writer.Write(catalog.Rules.Length);
        foreach (PowerCommitmentRule rule in catalog.Rules)
        {
            writer.Write((int)rule.Family);
            writer.Write((int)rule.Priority);
            EncodeAdmission(writer, rule.Admission);
            writer.Write(rule.Index);
        }
        writer.Write(catalog.TotalFloor);
    }

    private static PowerCommitmentCatalog DecodePowerCommitments(BinaryReader reader)
    {
        int count = Codec.ReadCount(reader, 1 << 16, "能力承诺规则");
        PowerCommitmentRule[] rules = new PowerCommitmentRule[count];
        for (int index = 0; index < count; index++)
        {
            PowerCommitmentFamily family = ReadFlagsInt<PowerCommitmentFamily>(reader, "能力承诺族");
            PowerRoutePriority priority = ReadEnumInt<PowerRoutePriority>(reader, "能力路线优先级");
            PowerRouteAdmissionPolicy admission = DecodeAdmission(reader);
            rules[index] = new PowerCommitmentRule(family, priority, admission, reader.ReadInt32());
        }
        return new PowerCommitmentCatalog(rules, reader.ReadInt32());
    }

    private static void EncodeAdmission(BinaryWriter writer, PowerRouteAdmissionPolicy policy)
    {
        writer.Write((int)policy.Priority);
        writer.Write(policy.MinimumProjection);
        writer.Write(policy.AllowTriggerBackedProjectionFloor);
        writer.Write(policy.PreferFreeActivation);
        writer.Write(policy.RequireFreeOrSpareActivation);
        writer.Write(policy.RequireImmediateDefenseGain);
        writer.Write(policy.PreferDedicatedSearch);
        writer.Write(policy.RequirePositiveProjection);
        writer.Write(policy.NoInCombatCommitment);
    }

    private static PowerRouteAdmissionPolicy DecodeAdmission(BinaryReader reader)
        => new(ReadEnumInt<PowerRoutePriority>(reader, "能力路线准入优先级"),
            reader.ReadInt32(), Codec.ReadBool(reader), Codec.ReadBool(reader),
            Codec.ReadBool(reader), Codec.ReadBool(reader), Codec.ReadBool(reader),
            Codec.ReadBool(reader), Codec.ReadBool(reader));

    private static void EncodeScoreRules(BinaryWriter writer, ScoreRules rules)
    {
        writer.Write(rules.Cards.Length);
        for (int id = 0; id < rules.Cards.Length; id++)
        {
            CardScore?[]? levels = rules.Cards[id];
            writer.Write(levels != null);
            if (levels == null)
                continue;
            writer.Write(levels.Length);
            for (int upgrade = 0; upgrade < levels.Length; upgrade++)
            {
                writer.Write(levels[upgrade] != null);
                if (levels[upgrade] is { } score)
                    EncodeCardScore(writer, score);
            }
        }
        writer.Write(rules.Powers.Length);
        for (int id = 0; id < rules.Powers.Length; id++)
        {
            writer.Write(rules.Powers[id] != null);
            if (rules.Powers[id] is { } score)
                EncodePowerScore(writer, score);
        }
        Codec.WriteIntArray(writer, rules.PotionStrategicCost);
        EncodeGrowthValues(writer, rules.GrowthBudgets);
        EncodeGrowthValues(writer, rules.RequiredGrowthRewards);
        EncodeRelicTargets(writer, rules.RelicCounterTargets);
        EncodePostCombatHeal(writer, rules.PostCombatRelicHeal);
        writer.Write((int)rules.BossHpRelief);
        writer.Write(rules.InitialPlayerMaxHp);
        writer.Write(rules.IsActEndingBoss);
        writer.Write(rules.Act3BossStrategy);
        writer.Write(rules.IgnoreGrowthRewards);
        writer.Write(rules.TheftPolicy != null);
        writer.Write((int)(rules.TheftPolicy ?? TheftPolicy.LetEscape));
        writer.Write(rules.InitialEnemyCount);
        writer.Write(rules.InitialPersistentBuffValue);
        writer.Write(rules.InitialEnemyStrengthSuppression);
        writer.Write(rules.InitialEnemyWeakTurns);
        writer.Write(rules.InitialRetainedAttackValue);
        Codec.WriteUShortArray(writer, rules.RootPotions);
        WriteId(writer, rules.StrengthPowerId);
        WriteId(writer, rules.WeakPowerId);
        WriteId(writer, rules.VulnerablePowerId);
        WriteId(writer, rules.PoisonPowerId);
        WriteId(writer, rules.DemisePowerId);
        WriteId(writer, rules.DoomPowerId);
        WriteId(writer, rules.SandpitPowerId);
        WriteId(writer, rules.SummonNextTurnPowerId);
        WriteId(writer, rules.DieForYouPowerId);
        WriteId(writer, rules.SleightOfFleshPowerId);
        WriteId(writer, rules.RetainHandPowerId);
        WriteId(writer, rules.EnergyNextTurnPowerId);
        WriteId(writer, rules.DrawCardsNextTurnPowerId);
        WriteId(writer, rules.StarNextTurnPowerId);
        WriteId(writer, rules.FreeAttackPowerId);
        WriteId(writer, rules.VoidFormPowerId);
        WriteId(writer, rules.FreeSkillPowerId);
        WriteId(writer, rules.FreePowerPowerId);
        WriteId(writer, rules.IllusionPowerId);
        WriteId(writer, rules.ReattachPowerId);
        WriteId(writer, rules.AdaptablePowerId);
        WriteId(writer, rules.BodySlamCardId);
        WriteId(writer, rules.EradicateCardId);
        WriteId(writer, rules.FiddleRelicId);
        WriteId(writer, rules.DropletOfPrecognitionPotionId);
        WriteId(writer, rules.TheHuntCardId);
        WriteId(writer, rules.TheHuntPowerId);
        WriteId(writer, rules.BufferPowerId);
        WriteId(writer, rules.NoDrawPowerId);
        WriteId(writer, rules.TheGambitPowerId);
        WriteId(writer, rules.FairyInABottlePotionId);
        WriteId(writer, rules.AmbergrisPotionId);
        WriteId(writer, rules.LizardTailRelicId);
        writer.Write(rules.RootPotionSlotCount);
    }

    private static ScoreRules DecodeScoreRules(BinaryReader reader, Rules rules)
    {
        int cardCount = Codec.ReadCount(reader, 1 << 16, "评分卡牌表");
        CardScore?[][] cards = new CardScore?[cardCount][];
        for (int id = 0; id < cardCount; id++)
        {
            if (!Codec.ReadBool(reader))
                continue;
            int levels = Codec.ReadCount(reader, 64, "评分卡牌升级表");
            cards[id] = new CardScore?[levels];
            for (int upgrade = 0; upgrade < levels; upgrade++)
            {
                if (Codec.ReadBool(reader))
                    cards[id][upgrade] = DecodeCardScore(reader);
            }
        }
        int powerCount = Codec.ReadCount(reader, 1 << 16, "评分 Power 表");
        PowerScore[] powers = new PowerScore[powerCount];
        for (int id = 0; id < powerCount; id++)
        {
            if (Codec.ReadBool(reader))
                powers[id] = DecodePowerScore(reader);
        }
        int[] potionCosts = Codec.ReadIntArray(reader, 1 << 16, "药水战略代价");
        GrowthValues growth = DecodeGrowthValues(reader);
        GrowthValues required = DecodeGrowthValues(reader);
        RelicCounterTarget[] relicTargets = DecodeRelicTargets(reader);
        PostCombatRelicHealProfile heal = DecodePostCombatHeal(reader);
        BossHpRelief relief = ReadEnumInt<BossHpRelief>(reader, "Boss HP 减免");
        int initialMaxHp = reader.ReadInt32();
        bool actEnding = Codec.ReadBool(reader);
        bool act3 = Codec.ReadBool(reader);
        bool ignoreGrowth = Codec.ReadBool(reader);
        bool hasTheft = Codec.ReadBool(reader);
        int theftValue = reader.ReadInt32();
        TheftPolicy? theft = hasTheft
            ? ReadEnumInt<TheftPolicy>(theftValue, "窃取政策") : null;
        int enemyCount = reader.ReadInt32();
        int persistent = reader.ReadInt32();
        int suppression = reader.ReadInt32();
        int weak = reader.ReadInt32();
        int retained = reader.ReadInt32();
        ushort[] rootPotions = Codec.ReadUShortArray(reader, Limits.PotionSlots, "评分根药水");
        return new ScoreRules
        {
            Rules = rules,
            Cards = cards,
            Powers = powers,
            PotionStrategicCost = potionCosts,
            GrowthBudgets = growth,
            RequiredGrowthRewards = required,
            RelicCounterTargets = relicTargets,
            PostCombatRelicHeal = heal,
            BossHpRelief = relief,
            InitialPlayerMaxHp = initialMaxHp,
            IsActEndingBoss = actEnding,
            Act3BossStrategy = act3,
            IgnoreGrowthRewards = ignoreGrowth,
            TheftPolicy = theft,
            InitialEnemyCount = enemyCount,
            InitialPersistentBuffValue = persistent,
            InitialEnemyStrengthSuppression = suppression,
            InitialEnemyWeakTurns = weak,
            InitialRetainedAttackValue = retained,
            RootPotions = rootPotions,
            StrengthPowerId = ReadId(reader),
            WeakPowerId = ReadId(reader),
            VulnerablePowerId = ReadId(reader),
            PoisonPowerId = ReadId(reader),
            DemisePowerId = ReadId(reader),
            DoomPowerId = ReadId(reader),
            SandpitPowerId = ReadId(reader),
            SummonNextTurnPowerId = ReadId(reader),
            DieForYouPowerId = ReadId(reader),
            SleightOfFleshPowerId = ReadId(reader),
            RetainHandPowerId = ReadId(reader),
            EnergyNextTurnPowerId = ReadId(reader),
            DrawCardsNextTurnPowerId = ReadId(reader),
            StarNextTurnPowerId = ReadId(reader),
            FreeAttackPowerId = ReadId(reader),
            VoidFormPowerId = ReadId(reader),
            FreeSkillPowerId = ReadId(reader),
            FreePowerPowerId = ReadId(reader),
            IllusionPowerId = ReadId(reader),
            ReattachPowerId = ReadId(reader),
            AdaptablePowerId = ReadId(reader),
            BodySlamCardId = ReadId(reader),
            EradicateCardId = ReadId(reader),
            FiddleRelicId = ReadId(reader),
            DropletOfPrecognitionPotionId = ReadId(reader),
            TheHuntCardId = ReadId(reader),
            TheHuntPowerId = ReadId(reader),
            BufferPowerId = ReadId(reader),
            NoDrawPowerId = ReadId(reader),
            TheGambitPowerId = ReadId(reader),
            FairyInABottlePotionId = ReadId(reader),
            AmbergrisPotionId = ReadId(reader),
            LizardTailRelicId = ReadId(reader),
            RootPotionSlotCount = reader.ReadInt32(),
        };
    }

    private static void EncodeCardScore(BinaryWriter writer, CardScore score)
    {
        writer.Write(score.BaseDamage);
        writer.Write(score.BaseBlock);
        writer.Write(score.BaseCards);
        writer.Write(score.IsPower);
        writer.Write(score.PrimaryMoveToHandChoice);
        writer.Write(score.DamageVar);
        writer.Write(score.BlockVar);
        writer.Write(score.CardsVar);
        writer.Write(score.RepeatVar);
        writer.Write(score.ShivCardsVar);
        writer.Write(score.ShivShivsVar);
        writer.Write(score.CalculationBaseVar);
        writer.Write(score.CalculationExtraVar);
        writer.Write(score.DoomThresholdVar);
        writer.Write((byte)score.AttackHits);
        writer.Write((byte)score.ShivSupply);
        writer.Write(score.HasBlockDynamicVar);
        writer.Write(score.HasDebuffDynamicVar);
        writer.Write(score.HasTurnEndInHandEffect);
        writer.Write(score.HasOstyAttackTag);
        writer.Write(score.HasShivTag);
        writer.Write(score.LatentValue);
        writer.Write((int)score.LatentTrait);
        writer.Write(score.DynamicCardType);
        writer.Write((byte)score.Calculated);
    }

    private static CardScore DecodeCardScore(BinaryReader reader)
        => new()
        {
            BaseDamage = reader.ReadDouble(),
            BaseBlock = reader.ReadDouble(),
            BaseCards = reader.ReadDouble(),
            IsPower = Codec.ReadBool(reader),
            PrimaryMoveToHandChoice = Codec.ReadBool(reader),
            DamageVar = reader.ReadSByte(),
            BlockVar = reader.ReadSByte(),
            CardsVar = reader.ReadSByte(),
            RepeatVar = reader.ReadSByte(),
            ShivCardsVar = reader.ReadSByte(),
            ShivShivsVar = reader.ReadSByte(),
            CalculationBaseVar = reader.ReadSByte(),
            CalculationExtraVar = reader.ReadSByte(),
            DoomThresholdVar = reader.ReadSByte(),
            AttackHits = (AttackHitKind)ReadByteEnum(reader, "攻击段数"),
            ShivSupply = (ShivSupplyKind)ReadByteEnum(reader, "飞刀来源"),
            HasBlockDynamicVar = Codec.ReadBool(reader),
            HasDebuffDynamicVar = Codec.ReadBool(reader),
            HasTurnEndInHandEffect = Codec.ReadBool(reader),
            HasOstyAttackTag = Codec.ReadBool(reader),
            HasShivTag = Codec.ReadBool(reader),
            LatentValue = reader.ReadInt32(),
            LatentTrait = (SetupTraits)reader.ReadInt32(),
            DynamicCardType = Codec.ReadBool(reader),
            Calculated = (CalculatedKind)ReadByteEnum(reader, "计算伤害类型"),
        };

    private static void EncodePowerScore(BinaryWriter writer, PowerScore score)
    {
        writer.Write((int)score.Kind);
        writer.Write((int)score.Requirements);
        writer.Write((int)score.Trait);
        writer.Write(score.IsTemporary);
        writer.Write((byte)score.Type);
        writer.Write(score.DanseMacabreEnergyThreshold);
    }

    private static PowerScore DecodePowerScore(BinaryReader reader)
        => new()
        {
            Kind = (StrategicKind)ReadInt(reader),
            Requirements = (StrategicRequirements)ReadInt(reader),
            Trait = (SetupTraits)ReadInt(reader),
            IsTemporary = Codec.ReadBool(reader),
            Type = (PowerType)ReadByteEnum(reader, "Power 类型"),
            DanseMacabreEnergyThreshold = reader.ReadInt32(),
        };

    private static void EncodeBoundConfig(BinaryWriter writer, BoundFeatures.Config config)
    {
        writer.Write(config.Enabled);
        Codec.WriteString(writer, config.DisabledReason);
        writer.Write(config.FutureHealPotential);
        writer.Write((byte)config.Certificate);
        writer.Write(config.PostCombatHeal);
        writer.Write((int)config.BossHpRelief);
        writer.Write(config.RequireCertifiedHealingProof);
        writer.Write(config.PruneEqualHp);
        writer.Write(config.ForceAllPotionsDisabled);
        writer.Write(config.MaximumPotionUses != null);
        writer.Write(config.MaximumPotionUses ?? 0);
        WriteBools(writer, config.ComponentUnsafeCard);
        WriteBools(writer, config.ComponentUnsafePower);
        WriteBools(writer, config.ComponentUnsafeRelic);
        WriteBools(writer, config.ComponentUnsafeMonster);
        WriteBools(writer, config.ComponentUnsafePotion);
        WriteBools(writer, config.SafeEnchantment);
        WriteBools(writer, config.SafeAffliction);
        WriteBools(writer, config.KnownBlockingCard);
        WriteBools(writer, config.KnownBlockingRelic);
        WriteBools(writer, config.RegenPower);
        WriteBools(writer, config.ReattachPower);
        WriteBools(writer, config.SwipePower);
        WriteBools(writer, config.RegenPotion);
        WriteBools(writer, config.DirectHealPotion);
        WriteBools(writer, config.FruitJuicePotion);
        WriteBools(writer, config.FairyPotion);
        WriteBools(writer, config.NativePotion);
        Codec.WriteIntArray(writer, config.RegenPotionAmount);
        WriteDecimals(writer, config.DirectHealPotionPercent);
        WriteBools(writer, config.ReattachSafeMonster);
        writer.Write(config.RegenPowerId);
        writer.Write(config.ReattachPowerId);
        Codec.WriteUShortArray(writer, config.RootPotions);
        writer.Write(config.RootPotionSlotCount);
    }

    private static BoundFeatures.Config DecodeBoundConfig(BinaryReader reader)
    {
        bool enabled = Codec.ReadBool(reader);
        string reason = Codec.ReadString(reader)
            ?? throw new InvalidDataException("下界失效原因缺失。");
        int future = reader.ReadInt32();
        HealingCertificate certificate = (HealingCertificate)
            ReadByteEnum(reader, "治疗证书");
        int postCombat = reader.ReadInt32();
        BossHpRelief relief = ReadEnumInt<BossHpRelief>(reader, "Boss HP 减免");
        bool certified = Codec.ReadBool(reader);
        bool pruneEqual = Codec.ReadBool(reader);
        bool forceDisabled = Codec.ReadBool(reader);
        bool hasMaximum = Codec.ReadBool(reader);
        int maximumValue = reader.ReadInt32();
        int? maximum = hasMaximum ? maximumValue : null;
        return new BoundFeatures.Config
        {
            Enabled = enabled,
            DisabledReason = reason,
            FutureHealPotential = future,
            Certificate = certificate,
            PostCombatHeal = postCombat,
            BossHpRelief = relief,
            RequireCertifiedHealingProof = certified,
            PruneEqualHp = pruneEqual,
            ForceAllPotionsDisabled = forceDisabled,
            MaximumPotionUses = maximum,
            ComponentUnsafeCard = ReadBools(reader, 1 << 16, "下界卡牌安全表"),
            ComponentUnsafePower = ReadBools(reader, 1 << 16, "下界 Power 安全表"),
            ComponentUnsafeRelic = ReadBools(reader, 1 << 16, "下界遗物安全表"),
            ComponentUnsafeMonster = ReadBools(reader, 1 << 16, "下界怪物安全表"),
            ComponentUnsafePotion = ReadBools(reader, 1 << 16, "下界药水安全表"),
            SafeEnchantment = ReadBools(reader, 1 << 10, "下界附魔安全表"),
            SafeAffliction = ReadBools(reader, 1 << 10, "下界苦难安全表"),
            KnownBlockingCard = ReadBools(reader, 1 << 16, "已知阻断卡牌表"),
            KnownBlockingRelic = ReadBools(reader, 1 << 16, "已知阻断遗物表"),
            RegenPower = ReadBools(reader, 1 << 16, "再生 Power 表"),
            ReattachPower = ReadBools(reader, 1 << 16, "重挂 Power 表"),
            SwipePower = ReadBools(reader, 1 << 16, "窃取 Power 表"),
            RegenPotion = ReadBools(reader, 1 << 16, "再生药水表"),
            DirectHealPotion = ReadBools(reader, 1 << 16, "直接治疗药水表"),
            FruitJuicePotion = ReadBools(reader, 1 << 16, "果汁药水表"),
            FairyPotion = ReadBools(reader, 1 << 16, "精灵药水表"),
            NativePotion = ReadBools(reader, 1 << 16, "原版药水表"),
            RegenPotionAmount = Codec.ReadIntArray(reader, 1 << 16, "再生药水数值"),
            DirectHealPotionPercent = ReadDecimals(reader),
            ReattachSafeMonster = ReadBools(reader, 1 << 16, "重挂安全怪物表"),
            RegenPowerId = reader.ReadInt32(),
            ReattachPowerId = reader.ReadInt32(),
            RootPotions = Codec.ReadUShortArray(reader, Limits.PotionSlots, "下界根药水"),
            RootPotionSlotCount = reader.ReadInt32(),
        };
    }

    private static void EncodeGrowthValues(BinaryWriter writer, GrowthValues values)
    {
        if (!values.Extras.IsEmpty)
            throw new NotSupportedException("第三方成长来源未移植，无法导出 GrowthValues。");
        writer.Write(values.HandOfGreed);
        writer.Write(values.TheHunt);
        writer.Write(values.Feed);
        writer.Write(values.Royalties);
        writer.Write(values.Alchemize);
        writer.Write(values.GeneticAlgorithm);
        writer.Write(values.TheScythe);
        writer.Write(values.Goopy);
        writer.Write(values.ForbiddenGrimoire);
        writer.Write(values.MadScience);
    }

    private static GrowthValues DecodeGrowthValues(BinaryReader reader)
        => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), reader.ReadInt32());

    private static void EncodeRelicTargets(BinaryWriter writer, RelicCounterTarget[] targets)
    {
        writer.Write(targets.Length);
        foreach (RelicCounterTarget target in targets)
        {
            writer.Write(target.Slot);
            writer.Write(target.Period);
            writer.Write(target.Goal);
            writer.Write(target.HpAllowance);
        }
    }

    private static RelicCounterTarget[] DecodeRelicTargets(BinaryReader reader)
    {
        int count = Codec.ReadCount(reader, Limits.Relics, "遗物计数目标");
        RelicCounterTarget[] targets = new RelicCounterTarget[count];
        for (int index = 0; index < count; index++)
        {
            RelicCounterTarget target = new(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(),
                reader.ReadInt32());
            if (target.Slot >= Limits.Relics || target.Period == 0 || target.Goal >= target.Period
                || target.HpAllowance is < 0 or > 1000)
                throw new InvalidDataException($"遗物计数目标无效：{target}");
            targets[index] = target;
        }
        return targets;
    }

    private static void EncodePostCombatHeal(BinaryWriter writer, PostCombatRelicHealProfile heal)
    {
        writer.Write(heal.UnconditionalHeal);
        writer.Write(heal.WoundedHeal);
        writer.Write(heal.WoundedHpPercent);
    }

    private static PostCombatRelicHealProfile DecodePostCombatHeal(BinaryReader reader)
        => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

    private static void WriteId(BinaryWriter writer, ushort id) => writer.Write(id);

    private static ushort ReadId(BinaryReader reader) => reader.ReadUInt16();

    private static void WriteBools(BinaryWriter writer, bool[] values)
    {
        writer.Write(values.Length);
        for (int index = 0; index < values.Length; index++)
            writer.Write(values[index]);
    }

    private static bool[] ReadBools(BinaryReader reader, int max, string what)
    {
        int count = Codec.ReadCount(reader, max, what);
        bool[] values = new bool[count];
        for (int index = 0; index < count; index++)
            values[index] = Codec.ReadBool(reader);
        return values;
    }

    private static void WriteDecimals(BinaryWriter writer, decimal[] values)
    {
        writer.Write(values.Length);
        for (int index = 0; index < values.Length; index++)
            Codec.WriteDecimal(writer, values[index]);
    }

    private static decimal[] ReadDecimals(BinaryReader reader)
    {
        int count = Codec.ReadCount(reader, 1 << 16, "decimal 数组");
        decimal[] values = new decimal[count];
        for (int index = 0; index < count; index++)
            values[index] = Codec.ReadDecimal(reader);
        return values;
    }

    private static int ReadInt(BinaryReader reader) => reader.ReadInt32();

    private static T ReadEnumInt<T>(BinaryReader reader, string what) where T : struct, Enum
        => ReadEnumInt<T>(reader.ReadInt32(), what);

    private static T ReadFlagsInt<T>(BinaryReader reader, string what) where T : struct, Enum
    {
        int value = reader.ReadInt32();
        int all = 0;
        foreach (T item in Enum.GetValues<T>())
            all |= Convert.ToInt32(item);
        if ((value & ~all) != 0)
            throw new InvalidDataException($"{what}位 {value:X} 含未定义标志。");
        return (T)Enum.ToObject(typeof(T), value);
    }

    private static T ReadEnumInt<T>(int value, string what) where T : struct, Enum
    {
        if (!Enum.IsDefined(typeof(T), value))
            throw new InvalidDataException($"{what}值 {value} 未定义。");
        return (T)Enum.ToObject(typeof(T), value);
    }

    private static byte ReadByteEnum(BinaryReader reader, string what) => reader.ReadByte();

    // ---------------------------------------------------------------------------------------------
    // Result codec: every deterministic field, no wall-clock, GC or frame-pressure measurements.

    internal static byte[] EncodeResult(BeamResult result)
        => Codec.ToBytes(writer =>
        {
            writer.Write(ResultMagic);
            writer.Write(ResultFormatVersion);
            WriteRoute(writer, result.BestRoute);
            writer.Write(result.BestIsVictory);
            writer.Write(BitConverter.DoubleToInt64Bits(result.BestScore));
            writer.Write(result.ExpandedNodes);
            writer.Write(result.Transitions);
            writer.Write(result.Duplicates);
            writer.Write(result.TurnsSearched);
            writer.Write(result.PrunedIncumbents);
            writer.Write(result.CapacityPruned);
            writer.Write(result.CapacityDroppedChildren.Count);
            foreach (KeyValuePair<string, int> entry in result.CapacityDroppedChildren
                         .OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                Codec.WriteString(writer, entry.Key);
                writer.Write(entry.Value);
            }
            writer.Write(result.EndedCompactions);
            writer.Write(result.EndedCompressedStates);
            writer.Write(result.BestRootIndex);
            writer.Write(result.PotionPolicySatisfied);
        });

    internal static BeamResult DecodeResult(byte[] bytes, int expectedBestRootIndex = -1)
        => Codec.Read(bytes, "Sts2Solver 搜索结果", reader =>
        {
            if (reader.ReadUInt32() != ResultMagic)
                throw new InvalidDataException("Sts2Solver 搜索结果魔数不符。");
            ushort version = reader.ReadUInt16();
            if (version != ResultFormatVersion)
                throw new InvalidDataException($"Sts2Solver 搜索结果版本 {version} 不受支持。");
            PlayerAction[] best = ReadRoute(reader);
            bool victory = Codec.ReadBool(reader);
            double score = BitConverter.Int64BitsToDouble(reader.ReadInt64());
            int expanded = reader.ReadInt32();
            int transitions = reader.ReadInt32();
            int duplicates = reader.ReadInt32();
            int turns = reader.ReadInt32();
            int pruned = reader.ReadInt32();
            int capacityPruned = reader.ReadInt32();
            int capacityCount = Codec.ReadCount(reader, 1 << 10, "容量丢弃表");
            Dictionary<string, int> capacity = new(StringComparer.Ordinal);
            for (int index = 0; index < capacityCount; index++)
            {
                string key = Codec.ReadString(reader)
                    ?? throw new InvalidDataException("容量丢弃键缺失。");
                if (!capacity.TryAdd(key, reader.ReadInt32()))
                    throw new InvalidDataException($"容量丢弃键 {key} 重复。");
            }
            int endedCompactions = reader.ReadInt32();
            int endedCompressed = reader.ReadInt32();
            int bestRoot = reader.ReadInt32();
            bool potionSatisfied = Codec.ReadBool(reader);
            if (expectedBestRootIndex >= 0 && bestRoot != expectedBestRootIndex)
                throw new InvalidDataException(
                    $"Sts2Solver 搜索结果根下标 {bestRoot} 与请求的 {expectedBestRootIndex} 不同。");
            return new BeamResult(best, victory, score, expanded, transitions,
                duplicates, turns, pruned, TimeSpan.Zero)
            {
                CapacityPruned = capacityPruned,
                CapacityDroppedChildren = capacity,
                EndedCompactions = endedCompactions,
                EndedCompressedStates = endedCompressed,
                BestRootIndex = bestRoot,
                PotionPolicySatisfied = potionSatisfied,
            };
        });

    private static void WriteRoute(BinaryWriter writer, PlayerAction[] route)
    {
        writer.Write(route.Length);
        foreach (PlayerAction action in route)
            GoldenData.WriteAction(writer, action);
    }

    private static PlayerAction[] ReadRoute(BinaryReader reader)
    {
        int count = Codec.ReadCount(reader, 1 << 20, "路线动作");
        PlayerAction[] route = new PlayerAction[count];
        for (int index = 0; index < count; index++)
            route[index] = GoldenData.ReadAction(reader);
        return route;
    }
}
