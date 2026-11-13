using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;

namespace Sts2Solver.Ai.Combat.Scoring;

/// <summary>Which remaining-healing certificate the root uses; mirrors <c>RemainingHealingPotentialCore</c>.</summary>
internal enum HealingCertificate : byte
{
    /// <summary>No certificate: the existing search passes no remaining-healing function.</summary>
    None = 0,
    Component = 1,
    KnownNative = 2,
}

/// <summary>
/// The frozen per-state bound inputs the incumbent pruning reads. One evaluation mirrors the existing
/// search's <c>SimulationSnapshot</c> members plus <c>RemainingHealingPotential</c> and
/// <c>StrategicHpLowerBound</c>; <see cref="BoundFeatures.Diff"/> is the K5 comparison.
/// </summary>
internal readonly record struct BoundValues(
    bool HasRisk,
    int FutureHealPotential,
    int RemainingHealingPotential,
    int FutureHealUsed,
    int CumulativePlayerHpLost,
    int RecoveredPlayerHp,
    int PlayerHp,
    int PlayerMaxHp,
    int DeathSaveHpRestored,
    int ExplicitPotionStrategicCost,
    int StrategicHpLowerBound,
    int SmartPotionRemainingHealingPotential,
    int SmartPotionHpLowerBound);

/// <summary>
/// Root-frozen healing-bound tables for Beam and the K5 probe. The bound compiler classifies every admitted
/// content id with the existing engine's own predicates; evaluation reads only <see cref="Battle"/>
/// and this table. When a bound input cannot be represented exactly the compiler disables the whole bound,
/// so the solver never prunes with an approximation.
/// </summary>
internal sealed class BoundFeatures
{
    internal sealed class Config
    {
        public required bool Enabled { get; init; }
        public required string DisabledReason { get; init; }
        public required int FutureHealPotential { get; init; }
        public required HealingCertificate Certificate { get; init; }
        public required int PostCombatHeal { get; init; }
        public required BossHpRelief BossHpRelief { get; init; }
        public required bool RequireCertifiedHealingProof { get; init; }
        public required bool PruneEqualHp { get; init; }
        public required bool[] ComponentUnsafeCard { get; init; }
        public required bool[] ComponentUnsafePower { get; init; }
        public required bool[] ComponentUnsafeRelic { get; init; }
        public required bool[] ComponentUnsafeMonster { get; init; }
        public required bool[] ComponentUnsafePotion { get; init; }
        public required bool[] SafeEnchantment { get; init; }
        public required bool[] SafeAffliction { get; init; }
        public required bool[] KnownBlockingCard { get; init; }
        public required bool[] KnownBlockingRelic { get; init; }
        public required bool[] RegenPower { get; init; }
        public required bool[] ReattachPower { get; init; }
        public required bool[] SwipePower { get; init; }
        public required bool[] RegenPotion { get; init; }
        public required bool[] DirectHealPotion { get; init; }
        public required bool[] FruitJuicePotion { get; init; }
        public required bool[] FairyPotion { get; init; }
        public required bool[] NativePotion { get; init; }
        public required int[] RegenPotionAmount { get; init; }
        public required decimal[] DirectHealPotionPercent { get; init; }
        public required bool[] ReattachSafeMonster { get; init; }
        public required int RegenPowerId { get; init; }
        public required int ReattachPowerId { get; init; }
    }

    /// <summary>
    /// Pruning-only lower-bound result. <see cref="RemainingUnknown"/> mirrors the old
    /// Component-certificate guard: a certified root whose remaining healing cannot be bounded
    /// skips incumbent pruning entirely.
    /// </summary>
    internal readonly record struct PruningBound(
        bool HasRisk,
        int StrategicHpLowerBound,
        bool RemainingUnknown);

    private readonly Config _config;

    public BoundFeatures(Config config) => _config = config;

    /// <summary>Complete frozen bound configuration, for the deterministic golden request codec.</summary>
    internal Config ConfigForCodec => _config;

    public bool Enabled => _config.Enabled;

    public string DisabledReason => _config.DisabledReason;

    public int FutureHealPotential => _config.FutureHealPotential;

    public BossHpRelief BossHpRelief => _config.BossHpRelief;

    public bool RequireCertifiedHealingProof => _config.RequireCertifiedHealingProof;

    public bool PruneEqualHp => _config.PruneEqualHp;

    /// <summary>
    /// The existing engine's <c>CombatPredictionSimulator.HasRisk</c>. Risk is recorded when a generation
    /// effect leaves the player's choice unresolved (Discovery/Splash) and stays true for the rest of the
    /// combat, so it maps to one monotonic battle flag.
    /// </summary>
    public bool HasRisk(in Battle battle) => (battle.Flags & BattleFlags.HasRisk) != 0;

    public BoundValues Evaluate(in Battle battle, int explicitPotionStrategicCost,
        bool includeSmartPotionLowerBound = false)
    {
        // the existing search's RemainingHealingPotentialCore: an unresolved risk invalidates the
        // certificate and returns int.MaxValue regardless of the configured component.
        int remaining = HasRisk(battle)
            ? int.MaxValue
            : _config.Certificate switch
            {
                HealingCertificate.Component => ComponentRemaining(battle),
                HealingCertificate.KnownNative => KnownNativeRemaining(battle),
                _ => int.MaxValue,
            };
        int futureUsed = _config.RequireCertifiedHealingProof && _config.PruneEqualHp
            ? remaining
            : Math.Min(remaining, _config.FutureHealPotential);
        int cumulativeHpLost = battle.Counter(Counter.CumulativeHpLost);
        int recoveredHp = battle.Counter(Counter.RecoveredHp);
        int deathSaveHpRestored = battle.Counter(Counter.DeathSaveRelicHpRestored)
            + battle.Counter(Counter.DeathSavePotionHpRestored);
        int lowerBound = ActEndingBossPolicy.StrategicHpDeficit(
            cumulativeHpLost,
            maxHpDeficit: 0,
            StrategicHpRecoveryBound.OptimisticRecoveredHp(
                recoveredHp, battle.Player.CurrentHp, battle.Player.MaxHp, futureUsed),
            _config.BossHpRelief,
            deathSaveHpRestored);
        // the existing search's ApplySmartPotionEligibilityBound passes RemainingHealingPotential
        // directly (not the incumbent's min with FutureHealPotential), so the ceiling keeps its
        // own lower-bound input. Non-audit searches never read it and skip the second
        // StrategicHpDeficit call on the hot retention path.
        int smartLowerBound = includeSmartPotionLowerBound
            ? ActEndingBossPolicy.StrategicHpDeficit(
                cumulativeHpLost,
                maxHpDeficit: 0,
                StrategicHpRecoveryBound.OptimisticRecoveredHp(
                    recoveredHp, battle.Player.CurrentHp, battle.Player.MaxHp, remaining),
                _config.BossHpRelief,
                deathSaveHpRestored)
            : lowerBound;
        return new BoundValues(
            HasRisk: HasRisk(battle),
            FutureHealPotential: _config.FutureHealPotential,
            RemainingHealingPotential: remaining,
            FutureHealUsed: futureUsed,
            CumulativePlayerHpLost: cumulativeHpLost,
            RecoveredPlayerHp: recoveredHp,
            PlayerHp: battle.Player.CurrentHp,
            PlayerMaxHp: battle.Player.MaxHp,
            DeathSaveHpRestored: deathSaveHpRestored,
            ExplicitPotionStrategicCost: explicitPotionStrategicCost,
            StrategicHpLowerBound: lowerBound,
            SmartPotionRemainingHealingPotential: remaining,
            SmartPotionHpLowerBound: smartLowerBound);
    }

    /// <summary>
    /// The incumbent-pruning lower bound. It differs from <see cref="Evaluate"/> only where a
    /// one-shot death save or a certified finite healing source is involved: the K5 probe keeps
    /// using <see cref="Evaluate"/> unchanged, while pruning may discard HP restored by a death save
    /// (the incumbent has zero death saves) and may count finite Not Yet/Feed/BookRepairKnife/Fruit
    /// Juice healing as bounded.
    /// </summary>
    internal int PruningStrategicHpLowerBound(in Battle battle, Rules rules)
        => EvaluatePruning(in battle, rules).StrategicHpLowerBound;

    internal PruningBound EvaluatePruning(in Battle battle, Rules rules)
    {
        bool risk = HasRisk(battle);
        PruningHealing healing = risk
            ? new PruningHealing(int.MaxValue, 0)
            : _config.Certificate switch
            {
                HealingCertificate.Component => ComponentPruningHealing(battle, rules),
                HealingCertificate.KnownNative => KnownNativePruningHealing(battle, rules),
                _ => new PruningHealing(int.MaxValue, 0),
            };
        long totalHealing = SaturatingAdd(healing.RecoveredHealing, healing.MaxHpGain);
        int remaining = totalHealing >= int.MaxValue ? int.MaxValue : (int)totalHealing;
        int futureUsed = _config.RequireCertifiedHealingProof && _config.PruneEqualHp
            ? remaining
            : Math.Min(remaining, _config.FutureHealPotential);
        int recoveredHp = battle.Counter(Counter.RecoveredHp);
        int deathSaveHpRestored = battle.Counter(Counter.DeathSaveRelicHpRestored)
            + battle.Counter(Counter.DeathSavePotionHpRestored);
        // A max-HP gain also moves current HP up by the same amount, so it enlarges the headroom
        // that the ordinary optimistic cap applies to. Keeping it in both the gain and the cap is
        // deliberately optimistic and therefore safe for a lower bound.
        long headroom = Math.Max(0, battle.Player.MaxHp - battle.Player.CurrentHp)
            + healing.MaxHpGain;
        int optimisticRecovered = recoveredHp + (int)Math.Min(headroom, futureUsed);
        int lowerBound = ActEndingBossPolicy.StrategicHpDeficit(
            battle.Counter(Counter.CumulativeHpLost),
            maxHpDeficit: 0,
            optimisticRecovered,
            _config.BossHpRelief,
            deathSaveHpRestored);
        return new PruningBound(risk, lowerBound, remaining == int.MaxValue);
    }

    private readonly record struct PruningHealing(long RecoveredHealing, long MaxHpGain);

    private static readonly ushort NotYetCardId = CardId("NOT_YET");
    private static readonly ushort FeedCardId = CardId("FEED");
    private static readonly ushort BookRepairKnifeRelicId = RelicId("BOOK_REPAIR_KNIFE");
    private static readonly ushort DragonFruitRelicId = RelicId("DRAGON_FRUIT");
    private static readonly ushort BookOfFiveRingsRelicId = RelicId("BOOK_OF_FIVE_RINGS");
    private static readonly ushort DarkstonePeriaptRelicId = RelicId("DARKSTONE_PERIAPT");
    private static readonly ushort ChosenCheeseRelicId = RelicId("CHOSEN_CHEESE");
    private static readonly ushort DemonTongueRelicId = RelicId("DEMON_TONGUE");
    private static readonly ushort LizardTailRelicId = RelicId("LIZARD_TAIL");
    private static readonly ushort HandOfGreedCardId = CardId("HAND_OF_GREED");
    private static readonly ushort FairyPotionId = PotionId("FAIRY_IN_A_BOTTLE");
    private static readonly ushort FruitJuicePotionId = PotionId("FRUIT_JUICE");
    private static readonly ushort[] NotYetLoopCards =
    [
        CardId("BURST"), CardId("ECHO_FORM"), CardId("EIDOLON"), CardId("HIDDEN_GEM"),
        CardId("NIGHTMARE"), CardId("SCULPTING_STRIKE"), CardId("TRANSFIGURE"),
    ];
    private static readonly ushort[] NotYetLoopPowers =
    [
        PowerId("BURST_POWER"), PowerId("DUPLICATION_POWER"), PowerId("ECHO_FORM_POWER"),
        PowerId("NIGHTMARE_POWER"),
    ];
    private static readonly ushort[] NotYetLoopRelics =
    [
        RelicId("BURNING_STICKS"), RelicId("THROWING_AXE"),
    ];
    private static readonly ushort[] NotYetLoopPotions = [PotionId("DUPLICATOR")];
    private static readonly ushort[] NotYetLoopEnchantments =
    [
        EnchantmentId("GLAM"), EnchantmentId("SOULS_POWER"), EnchantmentId("SPIRAL"),
    ];

    private static ushort CardId(string nativeId)
        => Registry.TryCard(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static ushort RelicId(string nativeId)
        => Registry.TryRelic(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static ushort PotionId(string nativeId)
        => Registry.TryPotion(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static ushort PowerId(string nativeId)
        => Registry.TryPower(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static ushort EnchantmentId(string nativeId)
        => Registry.TryEnchantment(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static bool Contains(ushort[] ids, ushort id)
        => id != ushort.MaxValue && Array.IndexOf(ids, id) >= 0;

    /// <summary>
    /// Root-only certificate for a shared victory target. It includes regeneration potions without
    /// changing the existing per-state pruning policy. Reusable growth sources remain unbounded.
    /// </summary>
    internal bool TryVictoryHealing(in Battle battle, Rules rules, out long healing, out long maxHpGain)
    {
        healing = maxHpGain = 0;
        if ((!Enabled && DisabledReason != "regen_potion_policy") || HasRisk(battle))
            return false;
        if (_config.Certificate == HealingCertificate.None)
        {
            if (_config.FutureHealPotential == int.MaxValue)
                return false;
            healing = Math.Max(0, _config.FutureHealPotential);
            return true;
        }
        // These existing pruning estimates count enemy/gold payouts only once. They are not a
        // certificate for terminating the entire portfolio when enemies or cards can recur.
        for (int i = 0; i < battle.RelicCount; i++)
            if (battle.Relics[i].Id == BookRepairKnifeRelicId || battle.Relics[i].Id == DragonFruitRelicId)
                return false;
        bool hasFiniteHealCard = false;
        for (int i = 0; i < battle.CardCount; i++)
        {
            ref readonly Card card = ref battle.Cards[i];
            if (card.Id != NotYetCardId && card.Id != FeedCardId)
                continue;
            hasFiniteHealCard = true;
            if (Piles.PileOf(battle, i, out _) is not (Pile.Hand or Pile.Draw or Pile.Discard))
                continue;
            if ((card.Keywords & Keywords.Exhaust) == 0
                || card.EnchantmentId != 0
                    && Contains(NotYetLoopEnchantments, (ushort)(card.EnchantmentId - 1)))
                return false;
        }
        if (hasFiniteHealCard && HasNotYetLoopRisk(battle))
            return false;
        PruningHealing value = _config.Certificate == HealingCertificate.Component
            ? ComponentPruningHealing(battle, rules, victoryTarget: true)
            : KnownNativePruningHealing(battle, rules);
        healing = value.RecoveredHealing;
        maxHpGain = value.MaxHpGain;
        if (maxHpGain > 0)
            for (int slot = 0; slot < battle.PotionSlotCount; slot++)
                if (battle.Potions[slot] is > 0 and var stored
                    && _config.DirectHealPotion[stored - 1])
                    healing += (long)Math.Ceiling(maxHpGain
                        * _config.DirectHealPotionPercent[stored - 1] / 100m);
        return healing < int.MaxValue && maxHpGain < int.MaxValue;
    }

    private PruningHealing ComponentPruningHealing(in Battle battle, Rules rules, bool victoryTarget = false)
    {
        if (HasInCombatPotionGeneration(battle))
            return new PruningHealing(int.MaxValue, 0);
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (_config.ComponentUnsafeMonster[battle.Enemies[index].MonsterId])
                return new PruningHealing(int.MaxValue, 0);
        }
        long directHeal = 0;
        long maxHpGain = 0;
        bool hasNotYet = HasCard(in battle, NotYetCardId);
        bool notYetLoopRisk = hasNotYet && HasNotYetLoopRisk(battle);
        int cardCount = battle.CardCount;
        for (int index = 0; index < cardCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            if (card.EnchantmentId != 0 && !_config.SafeEnchantment[card.EnchantmentId - 1])
                return new PruningHealing(int.MaxValue, 0);
            if (card.AfflictionId != 0 && !_config.SafeAffliction[card.AfflictionId - 1])
                return new PruningHealing(int.MaxValue, 0);
            if (!_config.ComponentUnsafeCard[card.Id])
                continue;
            if (card.Id == NotYetCardId)
            {
                if (notYetLoopRisk)
                    return new PruningHealing(int.MaxValue, 0);
                if (Piles.PileOf(battle, index, out _) is Pile.Hand or Pile.Draw or Pile.Discard)
                    directHeal += CardHealing(rules, in card, 0);
                continue;
            }
            if (card.Id == FeedCardId)
            {
                if (Piles.PileOf(battle, index, out _) is Pile.Hand or Pile.Draw or Pile.Discard)
                    maxHpGain += CardHealing(rules, in card, 1);
                continue;
            }
            return new PruningHealing(int.MaxValue, 0);
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if ((relic.Flags & RelicFlags.Melted) != 0 || !_config.ComponentUnsafeRelic[relic.Id])
                continue;
            if (relic.Id == BookRepairKnifeRelicId)
            {
                directHeal += SaturatingMultiply(EnemyCount(battle), RelicHealing(rules, relic.Id, 0));
                continue;
            }
            if (relic.Id == DragonFruitRelicId)
            {
                if (!TryBoundDragonFruitGain(in battle, rules, relic.Id, out long fruitGain))
                    return new PruningHealing(int.MaxValue, 0);
                maxHpGain = SaturatingAdd(maxHpGain, fruitGain);
                continue;
            }
            if (relic.Id is var id
                && (id == BookOfFiveRingsRelicId || id == DarkstonePeriaptRelicId
                    || id == ChosenCheeseRelicId || id == DemonTongueRelicId
                    || id == LizardTailRelicId))
            {
                continue;
            }
            return new PruningHealing(int.MaxValue, 0);
        }
        long regen = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (victoryTarget && power.Id == _config.RegenPowerId && power.Owner == Limits.PlayerOwner)
            {
                regen += Math.Max(0, power.Amount);
                continue;
            }
            if (_config.ComponentUnsafePower[power.Id])
                return new PruningHealing(int.MaxValue, 0);
            if (power.Id == _config.RegenPowerId && power.Owner == Limits.PlayerOwner)
                regen += Math.Max(0, power.Amount);
            if (power.Id == _config.ReattachPowerId && !ReattachOwnerIsSafe(battle, power.Owner))
                return new PruningHealing(int.MaxValue, 0);
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion == 0)
                continue;
            int id = potion - 1;
            if (id == FairyPotionId)
                continue;
            if (victoryTarget && _config.RegenPotion[id])
            {
                regen += Math.Max(0, _config.RegenPotionAmount[id]);
                continue;
            }
            if (victoryTarget && _config.DirectHealPotion[id])
            {
                directHeal += (long)Math.Ceiling(
                    battle.Player.MaxHp * _config.DirectHealPotionPercent[id] / 100m);
                continue;
            }
            if (!_config.ComponentUnsafePotion[id] && !_config.RegenPotion[id])
                continue;
            if (id == FruitJuicePotionId)
            {
                maxHpGain += PotionHealing(rules, (ushort)id, 0);
                continue;
            }
            return new PruningHealing(int.MaxValue, 0);
        }
        return new PruningHealing(
            SaturatingAdd(directHeal,
                StrategicHpRecoveryBound.RegenerationHealingUpperBound(regen, _config.PostCombatHeal)),
            maxHpGain);
    }

    private PruningHealing KnownNativePruningHealing(in Battle battle, Rules rules)
    {
        if (HasInCombatPotionGeneration(battle))
            return new PruningHealing(int.MaxValue, 0);
        long directHeal = 0;
        long maxHpGain = 0;
        bool hasNotYet = HasCard(in battle, NotYetCardId);
        bool notYetLoopRisk = hasNotYet && HasNotYetLoopRisk(battle);
        int cardCount = battle.CardCount;
        for (int index = 0; index < cardCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            if (!_config.KnownBlockingCard[card.Id])
                continue;
            if (card.Id == NotYetCardId)
            {
                if (notYetLoopRisk)
                    return new PruningHealing(int.MaxValue, 0);
                if (Piles.PileOf(battle, index, out _) is Pile.Hand or Pile.Draw or Pile.Discard)
                    directHeal += CardHealing(rules, in card, 0);
                continue;
            }
            if (card.Id == FeedCardId)
            {
                if (Piles.PileOf(battle, index, out _) is Pile.Hand or Pile.Draw or Pile.Discard)
                    maxHpGain += CardHealing(rules, in card, 1);
                continue;
            }
            return new PruningHealing(int.MaxValue, 0);
        }
        long regen = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (_config.SwipePower[power.Id])
                return new PruningHealing(int.MaxValue, 0);
            if (_config.RegenPower[power.Id] && power.Owner == Limits.PlayerOwner)
                regen += Math.Max(0, power.Amount);
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if ((relic.Flags & RelicFlags.Melted) != 0 || !_config.KnownBlockingRelic[relic.Id])
                continue;
            if (relic.Id == BookRepairKnifeRelicId)
            {
                directHeal += SaturatingMultiply(EnemyCount(battle), RelicHealing(rules, relic.Id, 0));
                continue;
            }
            if (relic.Id == DragonFruitRelicId)
            {
                if (!TryBoundDragonFruitGain(in battle, rules, relic.Id, out long fruitGain))
                    return new PruningHealing(int.MaxValue, 0);
                maxHpGain = SaturatingAdd(maxHpGain, fruitGain);
                continue;
            }
            if (relic.Id is var id
                && (id == BookOfFiveRingsRelicId || id == DarkstonePeriaptRelicId
                    || id == ChosenCheeseRelicId || id == DemonTongueRelicId
                    || id == LizardTailRelicId))
            {
                continue;
            }
            return new PruningHealing(int.MaxValue, 0);
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion == 0)
                continue;
            int id = potion - 1;
            if (!_config.NativePotion[id])
                return new PruningHealing(int.MaxValue, 0);
            if (id == FairyPotionId)
                continue;
            if (_config.RegenPotion[id])
                regen += Math.Max(0, _config.RegenPotionAmount[id]);
            else if (_config.DirectHealPotion[id])
                directHeal += (long)Math.Ceiling(
                    battle.Player.MaxHp * _config.DirectHealPotionPercent[id] / 100m);
            else if (id == FruitJuicePotionId)
                maxHpGain += PotionHealing(rules, (ushort)id, 0);
            else if (_config.FruitJuicePotion[id])
                return new PruningHealing(int.MaxValue, 0);
        }
        return new PruningHealing(
            SaturatingAdd(directHeal,
                StrategicHpRecoveryBound.RegenerationHealingUpperBound(regen, _config.PostCombatHeal)),
            maxHpGain);
    }

    private static bool HasCard(in Battle battle, ushort id)
    {
        if (id == ushort.MaxValue)
            return false;
        for (int index = 0; index < battle.CardCount; index++)
        {
            if (battle.Cards[index].Id == id)
                return true;
        }
        return false;
    }

    /// <summary>
    /// DragonFruit's in-combat MaxHp gain, bounded by the approved Feed-style approximation: every
    /// live HandOfGreed copy pays at most once. A different content definition that also gains gold
    /// in combat, or a non-card source such as LuckyFysh returning a stolen card, cannot be bounded
    /// this way and keeps the whole pruning bound unknown.
    /// </summary>
    private static bool TryBoundDragonFruitGain(in Battle battle, Rules rules, ushort relicId,
        out long maxHpGain)
    {
        maxHpGain = 0;
        // Non-card sources first: a held LuckyFysh can gain gold when SwipePower returns a stolen
        // card, which is not expressible as one payout per live HandOfGreed copy.
        for (int index = 0; index < battle.PowerCount; index++)
        {
            if (battle.Powers[index].Amount > 0
                && Registry.Power(battle.Powers[index].Id).GainsGoldInCombat)
            {
                return false;
            }
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if ((relic.Flags & RelicFlags.Melted) == 0
                && Registry.Relic(relic.Id).GainsGoldInCombat)
            {
                return false;
            }
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion != 0 && Registry.Potion((ushort)(potion - 1)).GainsGoldInCombat)
                return false;
        }
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (Registry.Monster(battle.Enemies[index].MonsterId).GainsGoldInCombat)
                return false;
        }
        for (int index = 0; index < battle.OrbCount; index++)
        {
            if (Registry.Orb(battle.Orbs[index].Id).GainsGoldInCombat)
                return false;
        }
        int cardCount = battle.CardCount;
        long perCard = 0;
        bool perCardLoaded = false;
        for (int index = 0; index < cardCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            if (!Registry.Card(card.Id).GainsGoldInCombat)
                continue;
            if (card.Id != HandOfGreedCardId)
                return false;
            if (Piles.PileOf(battle, index, out _) is Pile.Hand or Pile.Draw or Pile.Discard)
            {
                if (!perCardLoaded)
                {
                    perCard = RelicHealing(rules, relicId, 0);
                    perCardLoaded = true;
                }
                maxHpGain = SaturatingAdd(maxHpGain, perCard);
            }
        }
        for (int index = 0; index < cardCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            if (card.EnchantmentId != 0
                && Registry.Enchantment((ushort)(card.EnchantmentId - 1)).GainsGoldInCombat)
            {
                return false;
            }
            if (card.AfflictionId != 0
                && Registry.Affliction((ushort)(card.AfflictionId - 1)).GainsGoldInCombat)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Any effect that can replay, copy, un-exhaust, or auto-play an exhausted care with Not Yet.
    /// Unknown combinations stay unknown; Feed deliberately ignores these paths because its max-HP
    /// payout is only a finite approximation requested for this bound.
    /// </summary>
    private static bool HasNotYetLoopRisk(in Battle battle)
    {
        for (int index = 0; index < battle.CardCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            if (Contains(NotYetLoopCards, card.Id))
                return true;
            if (card.Id == NotYetCardId && card.EnchantmentId != 0
                && Contains(NotYetLoopEnchantments, (ushort)(card.EnchantmentId - 1)))
            {
                return true;
            }
        }
        for (int index = 0; index < battle.PowerCount; index++)
        {
            if (Contains(NotYetLoopPowers, battle.Powers[index].Id))
                return true;
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            if (Contains(NotYetLoopRelics, battle.Relics[index].Id))
                return true;
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion != 0 && Contains(NotYetLoopPotions, (ushort)(potion - 1)))
                return true;
        }
        // A card-generation source that is not itself classified above can produce one of those
        // effects later. Random pools and fixed generated ids are both checked; an incomplete
        // generator audit therefore stays unknown rather than silently assuming one play.
        for (int index = 0; index < battle.CardCount; index++)
        {
            if (ContentCanProduceReplayEffect(Registry.Card(battle.Cards[index].Id)))
                return true;
        }
        for (int index = 0; index < battle.PowerCount; index++)
        {
            if (ContentCanProduceReplayEffect(Registry.Power(battle.Powers[index].Id)))
                return true;
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            if (ContentCanProduceReplayEffect(Registry.Relic(battle.Relics[index].Id)))
                return true;
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion != 0 && ContentCanProduceReplayEffect(
                    Registry.Potion((ushort)(potion - 1))))
            {
                return true;
            }
        }
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (ContentCanProduceReplayEffect(Registry.Monster(battle.Enemies[index].MonsterId)))
                return true;
        }
        return false;
    }

    private static bool ContentCanProduceReplayEffect(ContentDef def)
    {
        if (def.GeneratedCardPools.Count > 0)
            return true;
        foreach ((string cardId, _) in def.GeneratedCards)
        {
            if (Registry.TryCard(cardId, out ushort id)
                && (id == NotYetCardId || Contains(NotYetLoopCards, id)))
            {
                return true;
            }
        }
        foreach (string powerId in def.AppliedPowers)
        {
            if (Registry.TryPower(powerId, out ushort id) && Contains(NotYetLoopPowers, id))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A in-combat random-potion source can materialize another Fruit Juice (or another direct heal
    /// potion), so the fruit-juice bound stays unknown whenever one is present. Entropic Brew is also
    /// covered because its own definition declares out-of-combat potion generation.
    /// </summary>
    private static bool HasInCombatPotionGeneration(in Battle battle)
    {
        for (int index = 0; index < battle.CardCount; index++)
        {
            ContentDef def = Registry.Card(battle.Cards[index].Id);
            if (def.GeneratesRandomPotion || def.GeneratesOutOfCombatPotion)
                return true;
        }
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ContentDef def = Registry.Power(battle.Powers[index].Id);
            if (def.GeneratesRandomPotion || def.GeneratesOutOfCombatPotion)
                return true;
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ContentDef def = Registry.Relic(battle.Relics[index].Id);
            if (def.GeneratesRandomPotion || def.GeneratesOutOfCombatPotion)
                return true;
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion == 0)
                continue;
            ContentDef def = Registry.Potion((ushort)(potion - 1));
            if (def.GeneratesRandomPotion || def.GeneratesOutOfCombatPotion)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every currently known enemy slot, including a corpse that can still revive. Counting all slots
    /// is an upper bound on the future Book Repair Knife payouts; each slot is still counted once.
    /// </summary>
    private static long EnemyCount(in Battle battle) => battle.EnemyCount;

    private static long CardHealing(Rules rules, in Card card, int variable)
    {
        decimal[] vars = rules.Card(card.Id, card.Upgrade).Vars;
        return (uint)variable < (uint)vars.Length
            ? Math.Max(0, (long)Math.Ceiling(vars[variable]))
            : 0;
    }

    private static long RelicHealing(Rules rules, ushort relicId, int variable)
        => Math.Max(0, (long)Math.Ceiling(rules.RelicVar(relicId, variable)));

    private static long PotionHealing(Rules rules, ushort potionId, int variable)
    {
        decimal[] vars = rules.Potion(potionId).Vars;
        return (uint)variable < (uint)vars.Length
            ? Math.Max(0, (long)Math.Ceiling(vars[variable]))
            : 0;
    }

    private static long SaturatingAdd(long left, long right)
        => left >= int.MaxValue - right ? int.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right)
        => left <= 0 || right <= 0
            ? 0
            : left >= int.MaxValue / right ? int.MaxValue : left * right;

    private int ComponentRemaining(in Battle battle)
    {
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (_config.ComponentUnsafeMonster[battle.Enemies[index].MonsterId])
                return int.MaxValue;
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            if (_config.ComponentUnsafeRelic[battle.Relics[index].Id])
                return int.MaxValue;
        }
        int cardCount = battle.CardCount;
        for (int index = 0; index < cardCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            if (_config.ComponentUnsafeCard[card.Id])
                return int.MaxValue;
            if (card.EnchantmentId != 0 && !_config.SafeEnchantment[card.EnchantmentId - 1])
                return int.MaxValue;
            if (card.AfflictionId != 0 && !_config.SafeAffliction[card.AfflictionId - 1])
                return int.MaxValue;
        }
        // Pending returning cards and player allies are not representable states (the importer rejects
        // Osty; the kernel does not implement returning cards), so both are empty here.
        long regen = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (_config.ComponentUnsafePower[power.Id])
                return int.MaxValue;
            if (power.Id == _config.RegenPowerId && power.Owner == Limits.PlayerOwner)
                regen += Math.Max(0, power.Amount);
            if (power.Id == _config.ReattachPowerId && !ReattachOwnerIsSafe(battle, power.Owner))
                return int.MaxValue;
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion == 0)
                continue;
            if (_config.ComponentUnsafePotion[potion - 1] || _config.RegenPotion[potion - 1])
                return int.MaxValue;
        }
        return StrategicHpRecoveryBound.RegenerationHealingUpperBound(regen, _config.PostCombatHeal);
    }

    private bool ReattachOwnerIsSafe(in Battle battle, byte owner)
    {
        if (owner < Limits.FirstEnemyOwner)
            return false;
        int enemy = owner - Limits.FirstEnemyOwner;
        return (uint)enemy < (uint)battle.EnemyCount
            && _config.ReattachSafeMonster[battle.Enemies[enemy].MonsterId];
    }

    private int KnownNativeRemaining(in Battle battle)
    {
        int cardCount = battle.CardCount;
        for (int index = 0; index < cardCount; index++)
        {
            if (_config.KnownBlockingCard[battle.Cards[index].Id])
                return int.MaxValue;
        }
        // Pending returning cards are not representable; a SwipePower's stolen NotYet/Feed is unknown.
        long regen = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (_config.SwipePower[power.Id])
                return int.MaxValue;
            if (_config.RegenPower[power.Id] && power.Owner == Limits.PlayerOwner)
                regen += Math.Max(0, power.Amount);
        }
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if ((relic.Flags & RelicFlags.Melted) == 0 && _config.KnownBlockingRelic[relic.Id])
                return int.MaxValue;
        }
        long directHeal = 0;
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort potion = battle.Potions[slot];
            if (potion == 0)
                continue;
            int id = potion - 1;
            if (!_config.NativePotion[id] || _config.FairyPotion[id])
                return int.MaxValue;
            if (_config.RegenPotion[id])
                regen += Math.Max(0, _config.RegenPotionAmount[id]);
            else if (_config.DirectHealPotion[id])
                directHeal += (long)Math.Ceiling(
                    battle.Player.MaxHp * _config.DirectHealPotionPercent[id] / 100m);
            else if (_config.FruitJuicePotion[id])
                return int.MaxValue;
        }
        return (int)Math.Min(int.MaxValue,
            directHeal + StrategicHpRecoveryBound.RegenerationHealingUpperBound(regen, _config.PostCombatHeal));
    }

    /// <summary>K5: the members that differ, as <c>name expected=… actual=…</c>.</summary>
    public static List<string> Diff(in BoundValues expected, in BoundValues actual)
    {
        List<string> lines = [];
        void Check<T>(string name, T left, T right)
        {
            if (!EqualityComparer<T>.Default.Equals(left, right))
                lines.Add($"{name} expected={left} actual={right}");
        }
        Check(nameof(BoundValues.HasRisk), expected.HasRisk, actual.HasRisk);
        Check(nameof(BoundValues.FutureHealPotential), expected.FutureHealPotential, actual.FutureHealPotential);
        Check(nameof(BoundValues.RemainingHealingPotential), expected.RemainingHealingPotential, actual.RemainingHealingPotential);
        Check(nameof(BoundValues.FutureHealUsed), expected.FutureHealUsed, actual.FutureHealUsed);
        Check(nameof(BoundValues.CumulativePlayerHpLost), expected.CumulativePlayerHpLost, actual.CumulativePlayerHpLost);
        Check(nameof(BoundValues.RecoveredPlayerHp), expected.RecoveredPlayerHp, actual.RecoveredPlayerHp);
        Check(nameof(BoundValues.PlayerHp), expected.PlayerHp, actual.PlayerHp);
        Check(nameof(BoundValues.PlayerMaxHp), expected.PlayerMaxHp, actual.PlayerMaxHp);
        Check(nameof(BoundValues.DeathSaveHpRestored), expected.DeathSaveHpRestored, actual.DeathSaveHpRestored);
        Check(nameof(BoundValues.ExplicitPotionStrategicCost),
            expected.ExplicitPotionStrategicCost, actual.ExplicitPotionStrategicCost);
        Check(nameof(BoundValues.StrategicHpLowerBound), expected.StrategicHpLowerBound, actual.StrategicHpLowerBound);
        Check(nameof(BoundValues.SmartPotionRemainingHealingPotential),
            expected.SmartPotionRemainingHealingPotential, actual.SmartPotionRemainingHealingPotential);
        Check(nameof(BoundValues.SmartPotionHpLowerBound),
            expected.SmartPotionHpLowerBound, actual.SmartPotionHpLowerBound);
        return lines;
    }
}
