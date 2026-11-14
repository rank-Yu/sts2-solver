using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;

namespace Sts2Solver.Ai.Combat.Scoring;

/// <summary>
/// Ports the existing search's state evaluation to <see cref="Battle"/>. Every computation follows
/// <c>the existing search's Snapshot</c> (the existing search's StateEvaluation.cs) and
/// <c>BeamRetentionPolicy.BeamRankScore</c> (the existing search's BeamRetentionPolicy.Ranking.cs) exactly:
/// the same terms, the same order of operations, the same checked/rounding/clamping.
/// </summary>
/// <remarks>
/// The per-content values (card values, latent setup values and traits, power strategic effects) come from
/// <see cref="ScoreRules"/>, which the request compiler builds from the reference engine's
/// models at the root. This type only reads the battle, the rule table and that compile output.
/// It allocates nothing on the hot path.
/// </remarks>
[System.Runtime.CompilerServices.SkipLocalsInit]
internal sealed class Scorer
{
    private readonly ScoreRules _score;

    private readonly bool _baseOnly;
    private readonly bool _offensive;
    private readonly RelicCounterTarget[] _relicTargets;

    public Scorer(ScoreRules score, bool baseOnly = false, bool offensive = false, QueenSearchPolicy? queen = null, SetupSearchPolicy? setup = null)
    {
        _score = score;
        _baseOnly = baseOnly;
        _offensive = offensive;
        QueenPolicy = queen;
        SetupPolicy = setup;
        _relicTargets = score.RelicCounterTargets;
        int ceiling = 0;
        foreach (RelicCounterTarget target in _relicTargets)
            ceiling = checked(ceiling + target.HpAllowance);
        RelicCreditCeiling = ceiling;
    }

    /// <summary>Number of relic counter targets in the request.</summary>
    internal int RelicTargetCount => _relicTargets.Length;

    /// <summary>The most relic goal credit any route can earn: every allowance once.</summary>
    internal int RelicCreditCeiling { get; }

    /// <summary>Frozen opt-in policy applied only to the supplemental member.</summary>
    internal QueenSearchPolicy? QueenPolicy { get; }
    internal SetupSearchPolicy? SetupPolicy { get; }
    internal bool SetupPreference => SetupPolicy is not null;

    /// <summary>The base-score member ranks by the base score alone.</summary>
    internal bool BaseOnly => _baseOnly;

    /// <summary>The offensive member subtracts enemy HP from the intermediate rank.</summary>
    internal bool Offensive => _offensive;

    /// <summary>
    /// Default-off test hook, set by <see cref="BeamSolver.Solve"/> from
    /// <see cref="SearchVerificationHooks.VerifySearchEquivalence"/>. Every projection then compares
    /// the battle before and after the in-place mutation.
    /// </summary>
    internal bool VerifyThreatProjection { get; set; }

    /// <summary>Number of in-place threat projections checked by the default-off test hook.</summary>
    internal int ThreatProjectionChecks { get; private set; }

    /// <summary>Complete frozen score table, for the deterministic golden request codec.</summary>
    internal ScoreRules RulesForCodec => _score;

    private Rules Rules => _score.Rules;

    /// <summary>The root-frozen initial retained-attack value.</summary>
    public int InitialRetainedAttackValue => _score.InitialRetainedAttackValue;

    /// <summary>The root-frozen kernel rule table, used by the retention stand-pat replay.</summary>
    internal Rules RulesForRetention => _score.Rules;

    /// <summary>
    /// The played card's primary <c>CardChoiceSupport</c> effect is <c>MoveToHand</c>. Root-frozen
    /// with the rest of the score table; used by the fetched-power commitment on every searched turn.
    /// </summary>
    public bool IsPrimaryMoveToHandCard(ushort id, int upgrade)
        => _score.Card(id, upgrade).PrimaryMoveToHandChoice;

    /// <summary>The registered Droplet of Precognition id, or <see cref="ushort.MaxValue"/>.</summary>
    public ushort DropletOfPrecognitionPotionId => _score.DropletOfPrecognitionPotionId;

    // ---------------------------------------------------------------------------------------------
    // Card values (CardChoiceSupport.CardValue / DynamicVarBaseValue, plus BranchCardValue extras).

    /// <summary>Current value of variable <paramref name="index"/>: canonical value plus the instance delta.</summary>
    private static decimal Var(in Battle battle, in Card card, CardStats stats, int index)
    {
        if (index < 0 || index >= stats.Vars.Length)
            return 0m;
        return CardEffects.Var(in battle, in card, stats, index);
    }

    /// <summary>
    /// <c>CardChoiceSupport.CardValue</c> for one stored card, used by the stand-pat probe's
    /// rule-based automatic turn-start answer (the same ordering <c>BuildAutomaticPolicyChoice</c>
    /// uses: ascending hand values, descending pile/generated values).
    /// </summary>
    internal double AutomaticChoiceValue(in Battle battle, in Card card)
        => CardValue(battle, card, Rules.Card(card.Id, card.Upgrade), _score.Card(card.Id, card.Upgrade));

    /// <summary>
    /// Port of <c>CardChoiceSupport.CardValue</c> with the instance's variable deltas applied. The
    /// existing engine converts each completed decimal <c>BaseValue</c> to double once; adding a
    /// double delta to a double base would round differently for decimals such as 0.75.
    /// </summary>
    private double CardValue(in Battle battle, in Card card, CardStats stats, CardScore score)
        => VarDouble(battle, card, stats, score.DamageVar, score.BaseDamage)
            + VarDouble(battle, card, stats, score.BlockVar, score.BaseBlock) * 0.8d
            + VarDouble(battle, card, stats, score.CardsVar, score.BaseCards) * 3d
            + (score.DynamicCardType
                ? (ResolveCardType(battle, card, stats) == CardType.Power ? 8d : 0d)
                : (score.IsPower ? 8d : 0d));

    /// <summary>
    /// Native live <c>CardModel.Type</c>: the rule table's canonical value unless the definition reads
    /// per-card state (MadScience's saved TinkerTimeType). Roots without such cards take the cheap branch.
    /// </summary>
    private CardType ResolveCardType(in Battle battle, in Card card, CardStats stats)
        => stats.HasDynamicType ? Registry.Card(card.Id).ResolveCardType(battle, Rules, card) : stats.Type;

    private static double VarDouble(in Battle battle, in Card card, CardStats stats, int index,
        double fallback)
    {
        if (index < 0)
            return fallback;
        decimal delta = card.Private > 0
            ? DecimalVars.Delta(in battle, in card, index)
            : index switch { 0 => card.Var0, 1 => card.Var1, _ => 0m };
        return (double)(stats.Vars[index] + delta);
    }

    /// <summary>Port of <c>the existing search's BranchCardValue</c> (calculated damage where registered).</summary>
    private double BranchCardValue(in Battle battle, in Card card, CardStats stats, CardScore score)
    {
        double value = CardValue(battle, card, stats, score);
        if (ResolveCardType(battle, card, stats) != CardType.Attack || score.Calculated == CalculatedKind.None)
            return value;
        decimal baseVar = Var(battle, card, stats, score.CalculationBaseVar);
        decimal extraVar = Var(battle, card, stats, score.CalculationExtraVar);
        decimal damage = 0m;
        // Only these calculated kinds read the target. Every other multiplier is a state-wide
        // value: compute it once and apply the same candidate while any hittable enemy exists.
        // The original loop's Math.Max over identical candidates is exactly that candidate clamped
        // at zero (its initial damage value).
        bool targetDependent = score.Calculated is CalculatedKind.TargetVulnerable
            or CalculatedKind.TargetDebuffPowerCount or CalculatedKind.DoomOverThreshold
            or CalculatedKind.TargetDoom;
        if (targetDependent)
        {
            decimal doomThreshold = score.Calculated == CalculatedKind.DoomOverThreshold
                ? Var(battle, card, stats, score.DoomThresholdVar)
                : 0m;
            for (int position = 0; position < battle.RosterCount; position++)
            {
                byte owner = Kernel.EnemyOwner(battle.Roster[position]);
                if (!Kernel.IsHittable(battle, owner))
                    continue;
                decimal multiplier = score.Calculated switch
                {
                    CalculatedKind.TargetVulnerable => GetAmount(battle, _score.VulnerablePowerId, owner),
                    CalculatedKind.TargetDebuffPowerCount => CountTargetDebuffPowers(battle, owner),
                    CalculatedKind.DoomOverThreshold => Math.Floor(
                        GetAmount(battle, _score.DoomPowerId, owner) / doomThreshold),
                    _ => GetAmount(battle, _score.DoomPowerId, owner),
                };
                damage = Math.Max(damage, baseVar + extraVar * multiplier);
            }
        }
        else
        {
            decimal multiplier = score.Calculated switch
            {
                CalculatedKind.PlayerBlock => battle.Player.Block,
                CalculatedKind.StrikeCardCount => CountCardsWithTag(battle, CardTag.Strike),
                CalculatedKind.ExhaustPileCount => battle.ExhaustCount,
                CalculatedKind.PlayerStrength => Math.Max(0, GetAmount(battle, _score.StrengthPowerId, Limits.PlayerOwner)),
                CalculatedKind.DrawPileCount => battle.DrawCount,
                CalculatedKind.FinishedCardPlays => battle.Counter(Counter.FinishedCardPlays),
                CalculatedKind.CardsDiscardedThisTurn => battle.Counter(Counter.CardsDiscardedThisTurn),
                CalculatedKind.NegativeHandCount => -battle.HandCount,
                CalculatedKind.CardsDrawn => battle.Counter(Counter.CardsDrawn),
                CalculatedKind.StarCardCount => CountStarCostCards(battle),
                CalculatedKind.OstyCurrentHp => (battle.Flags & BattleFlags.OstyPresent) != 0
                    && battle.Osty.IsAlive ? battle.Osty.CurrentHp : 0,
                CalculatedKind.OstyMaxHp => (battle.Flags & BattleFlags.OstyPresent) != 0
                    && battle.Osty.IsAlive ? battle.Osty.MaxHp : 0,
                CalculatedKind.OstyAttacksPlusOne => 1 + battle.Counter(Counter.OstyAttacksThisTurn),
                CalculatedKind.OstyAttackCardsMinusSelf => CountCardsWithTag(battle, CardTag.OstyAttack)
                    - ((stats.Tags & CardTag.OstyAttack) != 0 ? 1 : 0),
                CalculatedKind.ExhaustSoulCount => CountExhaustSoulCards(battle),
                CalculatedKind.EtherealPlays => battle.Counter(Counter.EtherealPlays),
                CalculatedKind.NonHandDraws => battle.Counter(Counter.NonHandDrawsThisTurn),
                CalculatedKind.GeneratedCardCount => battle.Counter(Counter.CardsGeneratedThisCombat),
                _ => 0,
            };
            decimal candidate = baseVar + extraVar * multiplier;
            for (int position = 0; position < battle.RosterCount; position++)
            {
                byte owner = Kernel.EnemyOwner(battle.Roster[position]);
                if (!Kernel.IsHittable(battle, owner))
                    continue;
                damage = Math.Max(0m, candidate);
                break;
            }
        }
        return value + (double)damage - score.BaseDamage;
    }

    /// <summary>
    /// Port of <c>the existing search's CalculateReachableHandPotential</c>'s value/count half: each card
    /// playable from hand contributes its modified energy and star cost plus the same ceiling of
    /// <see cref="BranchCardValue"/> (including the enchanted replay multiplier), and the exact
    /// <see cref="ReachableHandValue"/> knapsack is fed the player's current energy and stars.
    /// </summary>
    internal (int Value, int ZeroCostPlayableCount, int PlayableAttackCount) ReachableHandPotential(in Battle battle)
    {
        Span<(int Energy, int Stars, int Value)> playable = battle.HandCount <= 64
            ? stackalloc (int, int, int)[battle.HandCount]
            : new (int, int, int)[battle.HandCount];
        int count = 0;
        int zeroCostPlayableCount = 0;
        int playableAttackCount = 0;
        for (int index = 0; index < battle.HandCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            // CanPlayCard computes the same effective hand costs; reuse them instead of running the
            // energy/star cost hook dispatch a second time for the same card and pile.
            if (!Kernel.CanPlayCard(battle, Rules, index, out int energyCost, out int starCost))
                continue;
            energyCost = Math.Max(0, energyCost);
            starCost = Math.Max(0, starCost);
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            CardScore cardScore = _score.Card(card.Id, card.Upgrade);
            double cardValue = BranchCardValue(battle, card, stats, cardScore)
                * (1 + Math.Max(0, Kernel.EnchantedReplayCount(card)));
            playable[count++] = (energyCost, starCost,
                Math.Max(1, (int)Math.Ceiling(cardValue)));
            if (energyCost == 0 && starCost == 0 && !stats.CostsX && !stats.StarCostsX)
                zeroCostPlayableCount++;
            if (ResolveCardType(battle, card, stats) == CardType.Attack)
                playableAttackCount++;
        }
        return (ReachableHandValue.Calculate(playable[..count], battle.Energy, battle.Stars),
            zeroCostPlayableCount, playableAttackCount);
    }

    /// <summary><c>CalculatedVarSpecRegistry.CrescentSpear</c>: the combat piles' star-cost cards.</summary>
    private int CountStarCostCards(in Battle battle)
    {
        int count = 0;
        for (int position = 0; position < battle.CardCount; position++)
        {
            ref readonly Card card = ref battle.Cards[position];
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            if (stats.StarCost >= 0 || stats.StarCostsX)
                count++;
        }
        return count;
    }

    private static readonly Pile[] LivePiles = [Pile.Discard, Pile.Draw, Pile.Hand];

    /// <summary><c>liveCards.Count(card => card.Preview is Soul)</c> over Discard, Draw and Hand.</summary>
    private int CountLiveSoulCards(in Battle battle)
    {
        int count = 0;
        foreach (Pile pile in LivePiles)
        {
            int start = Piles.Start(battle, pile);
            int size = Piles.Count(battle, pile);
            for (int offset = 0; offset < size; offset++)
            {
                ref readonly Card card = ref battle.Cards[start + offset];
                if (Rules.Card(card.Id, card.Upgrade).IsSoul)
                    count++;
            }
        }
        return count;
    }

    /// <summary><c>exhaustPile.Cards.Count(candidate =&gt; candidate.Preview is Soul)</c> (SoulStorm).</summary>
    private int CountExhaustSoulCards(in Battle battle)
    {
        int start = Piles.Start(battle, Pile.Exhaust);
        int count = Piles.Count(battle, Pile.Exhaust);
        int souls = 0;
        for (int offset = 0; offset < count; offset++)
        {
            ref readonly Card card = ref battle.Cards[start + offset];
            if (Rules.Card(card.Id, card.Upgrade).IsSoul)
                souls++;
        }
        return souls;
    }

    /// <summary><c>liveCards.Count(card =&gt; card.Preview.Tags.Contains(CardTag.OstyAttack))</c>.</summary>
    private int CountLiveOstyAttackCards(in Battle battle)
    {
        int count = 0;
        foreach (Pile pile in LivePiles)
        {
            int start = Piles.Start(battle, pile);
            int size = Piles.Count(battle, pile);
            for (int offset = 0; offset < size; offset++)
            {
                ref readonly Card card = ref battle.Cards[start + offset];
                if ((Rules.Card(card.Id, card.Upgrade).Tags & CardTag.OstyAttack) != 0)
                    count++;
            }
        }
        return count;
    }

    private int CountCardsWithTag(in Battle battle, CardTag tag)
    {
        int count = 0;
        for (int position = 0; position < battle.CardCount; position++)
        {
            ref readonly Card card = ref battle.Cards[position];
            // The rule-table tag set is canonical; instance keywords/tags never change.
            if ((Rules.Card(card.Id, card.Upgrade).Tags & tag) != 0)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Port of <c>CalculatedVarSpecRegistry.TryMultiplier</c>'s Rend case: the target's powers whose
    /// type for their current amount is Debuff, excluding temporary powers.
    /// </summary>
    private static int CountTargetDebuffPowers(in Battle battle, byte owner)
    {
        int count = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != owner)
                continue;
            PowerDef def = Registry.Power(power.Id);
            if (def.TypeForAmount(power.Amount) == PowerType.Debuff && !def.IsTemporaryPower)
                count++;
        }
        return count;
    }

    // ---------------------------------------------------------------------------------------------
    // Power amounts.

    private static int GetAmount(in Battle battle, ushort powerId, byte owner)
    {
        if (powerId == ushort.MaxValue)
            return 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Id == powerId && power.Owner == owner)
                return power.Amount;
        }
        return 0;
    }

    /// <summary>
    /// Number of poison hits per trigger for an enemy target: one plus the player's and Osty's
    /// Accelerant amounts, matching <c>Kernel.TriggerPoison</c>'s enemy-target acceleration.
    /// </summary>
    private static int PoisonTriggersPerTurn(in Battle battle)
    {
        long triggers = 1L
            + GetAmount(battle, PowerEvidenceIds.Accelerant, Limits.PlayerOwner);
        if ((battle.Flags & BattleFlags.OstyPresent) != 0 && battle.Osty.IsAlive)
            triggers += GetAmount(battle, PowerEvidenceIds.Accelerant, Limits.OstyOwner);
        return (int)Math.Clamp(triggers, 0L, int.MaxValue);
    }

    private static bool HasPower(in Battle battle, ushort powerId, byte owner)
        => powerId != ushort.MaxValue && GetAmount(battle, powerId, owner) > 0;

    // ---------------------------------------------------------------------------------------------
    // Growth rewards (the existing search's Snapshot).

    /// <summary>
    /// <c>SimulatedCombatState.GrowthRewards</c> from the packed counters. The kernel packs two
    /// 16-bit source counts per counter; see <see cref="GrowthRewards"/>.
    /// </summary>
    private static GrowthValues CaptureGrowthRewards(in Battle battle) => new(
        GrowthRewards.Get(battle, GrowthRewardSource.HandOfGreed),
        GrowthRewards.Get(battle, GrowthRewardSource.TheHunt),
        GrowthRewards.Get(battle, GrowthRewardSource.Feed),
        GrowthRewards.Get(battle, GrowthRewardSource.Royalties),
        GrowthRewards.Get(battle, GrowthRewardSource.Alchemize),
        GrowthRewards.Get(battle, GrowthRewardSource.GeneticAlgorithm),
        GrowthRewards.Get(battle, GrowthRewardSource.TheScythe),
        GrowthRewards.Get(battle, GrowthRewardSource.Goopy),
        GrowthRewards.Get(battle, GrowthRewardSource.ForbiddenGrimoire),
        GrowthRewards.Get(battle, GrowthRewardSource.MadScience));

    // ---------------------------------------------------------------------------------------------
    // Snapshot.

    /// <param name="includeRetentionOnly">
    /// When false, the two values read only by the retention comparators
    /// (<see cref="ScoreFeatures.ReachableHandValue"/> and
    /// <see cref="ScoreFeatures.ZeroCostPlayableCount"/>) are left at zero. The solver computes
    /// them when a state actually enters a retention pool; the K4 differential calls the default
    /// (true) and keeps checking every field.
    /// </param>
    /// <summary>
    /// Potion uses read from the state: root slots whose potion is gone (Fairy in a Bottle is the only
    /// automatic one) plus the generated-potion counters. Shared by the features and the pruning bound.
    /// </summary>
    internal PotionUsage MeasurePotions(in Battle battle)
    {
        int potionUseCount = 0;
        int potionStrategicCost = 0;
        int explicitPotionStrategicCost = 0;
        int dearestExplicitCost = 0;
        for (int slot = 0; slot < _score.RootPotionSlotCount && slot < Limits.PotionSlots; slot++)
        {
            ushort rootPotion = _score.RootPotions[slot];
            if (rootPotion == 0)
                continue;
            if (slot >= battle.PotionSlotCount || battle.Potions[slot] != rootPotion)
            {
                int cost = _score.PotionStrategicCost[rootPotion - 1];
                potionUseCount = checked(potionUseCount + 1);
                potionStrategicCost = checked(potionStrategicCost + cost);
                // Automatic potion uses are death saves (Fairy in a Bottle), which the search never
                // offers explicitly and which empty their slot when they fire.
                if (rootPotion != _score.FairyInABottlePotionId + 1)
                {
                    explicitPotionStrategicCost = checked(explicitPotionStrategicCost + cost);
                    dearestExplicitCost = Math.Max(dearestExplicitCost, cost);
                }
            }
        }
        int automaticPotionUseCount = battle.Counter(Counter.AutomaticPotionUseCount);
        // Generated potions never occupied a root slot, so the slot diff cannot see their use. The
        // packed cost distinguishes explicit from automatic uses (Fairy in a Bottle); the free
        // EntropicBrew uses carry zero.
        potionUseCount = checked(potionUseCount + battle.Counter(Counter.GeneratedPotionUseCount));
        int generatedCosts = battle.Counter(Counter.GeneratedPotionStrategicCost);
        int generatedExplicitCost = generatedCosts & 0xFFFF;
        int generatedAutomaticCost = (int)((uint)generatedCosts >> 16);
        potionStrategicCost = checked(potionStrategicCost + generatedExplicitCost + generatedAutomaticCost);
        explicitPotionStrategicCost = checked(explicitPotionStrategicCost + generatedExplicitCost);
        // The generated uses are one packed sum, so they count as a single potion here: the dearest
        // cost can only be overstated, never understated, and it still never falls along a route.
        dearestExplicitCost = Math.Max(dearestExplicitCost, generatedExplicitCost);
        return new PotionUsage(potionUseCount, potionStrategicCost, explicitPotionStrategicCost,
            automaticPotionUseCount, dearestExplicitCost);
    }

    public ScoreFeatures Features(in Battle battle, int actionCount, bool includeRetentionOnly = true)
        => Evaluate(battle, actionCount, includeRetentionOnly);

    /// <summary>
    /// Every bounded growth goal has been realized.
    /// The request already disabled the HP-target stop for unbounded targets.
    /// </summary>
    public bool GrowthTargetsSatisfied(in ScoreFeatures features)
        => features.GrowthRewards.Satisfies(_score.RequiredGrowthRewards);

    /// <summary>Every relic counter target ends on its goal; true when the request has none.</summary>
    public bool RelicGoalsMet(in ScoreFeatures features)
        => features.RelicGoalCount == _relicTargets.Length;

    private ScoreFeatures Evaluate(in Battle battle, int actionCount, bool includeRetentionOnly)
    {
        int enemyHp = 0, enemyBlock = 0, rawEnemyHp = 0, maxCurrentEnemyHp = 0;
        int revivingEnemyCount = 0, aliveEnemyCount = 0;
        ulong aliveEnemyMask = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            int currentHp = enemy.Creature.CurrentHp;
            int effectiveHp = EffectiveEnemyHp(battle, index);
            bool inCombat = (enemy.Flags & EnemyFlags.InCombat) != 0;
            enemyHp += effectiveHp;
            if (effectiveHp > 0 && inCombat)
                enemyBlock += Math.Max(0, enemy.Creature.Block);
            rawEnemyHp += Math.Max(0, currentHp);
            maxCurrentEnemyHp = Math.Max(maxCurrentEnemyHp, Math.Max(0, currentHp));
            if (currentHp <= 0 && effectiveHp > 0)
                revivingEnemyCount++;
            if (effectiveHp > 0 && inCombat)
            {
                aliveEnemyCount++;
                aliveEnemyMask |= 1UL << index;
            }
        }
        bool dead = battle.Player.IsDead || battle.Outcome == Outcome.Defeat;
        bool won = !dead && battle.Outcome == Outcome.Victory;
        // The existing snapshot takes ThreatProjection(player.CurrentHp, 0, 0) whenever `won` is false
        // but the boundary is not None (the existing search's StateEvaluation.cs). EventDefeat is such a
        // boundary: it must not project the enemies' next intents or death saves.
        bool eventDefeat = battle.Outcome == Outcome.EventDefeat;
        bool risk = (battle.Flags & BattleFlags.HasRisk) != 0;
        int projectedHp;
        int projectedDeathSaveHpRestored = 0;
        int projectedDeathSaveUseCount = 0;
        int blockConsumed = 0;
        if (won || eventDefeat)
        {
            projectedHp = battle.Player.CurrentHp;
        }
        else
        {
            ThreatProjection threat = ProjectHpAfterThreat(battle);
            projectedHp = threat.Hp;
            projectedDeathSaveHpRestored = threat.DeathSaveHpRestored;
            projectedDeathSaveUseCount = threat.DeathSaveUseCount;
            blockConsumed = threat.BlockConsumed;
        }
        int cumulativePlayerHpLost = battle.Counter(Counter.CumulativeHpLost);
        int recoveredPlayerHp = battle.Counter(Counter.RecoveredHp);
        int deathSaveHpRestored = battle.Counter(Counter.DeathSaveRelicHpRestored)
            + battle.Counter(Counter.DeathSavePotionHpRestored);
        int deathSaveUseCount = battle.Counter(Counter.DeathSaveUseCount);
        double hpWeight = ScoreWeights.Hp;
        double score = dead || projectedHp <= 0
            ? ScoreWeights.DeathPenalty
            : projectedHp * hpWeight;
        score += (battle.Player.MaxHp - _score.InitialPlayerMaxHp) * hpWeight;
        score -= cumulativePlayerHpLost * hpWeight;
        score -= ActEndingBossPolicy.DeathSaveBeamCost(
            deathSaveHpRestored + projectedDeathSaveHpRestored) * hpWeight;

        int realizedLongTermResourceValue = _score.IgnoreGrowthRewards
            ? 0
            : battle.Counter(Counter.LongTermResourceValue);
        int exhaustedTheHunts = 0;
        if (_score.TheHuntCardId != ushort.MaxValue)
        {
            int start = Piles.Start(battle, Pile.Exhaust);
            int count = Piles.Count(battle, Pile.Exhaust);
            for (int index = 0; index < count; index++)
            {
                if (battle.Cards[start + index].Id == _score.TheHuntCardId)
                    exhaustedTheHunts++;
            }
        }
        int rewardedTheHunts = _score.TheHuntPowerId == ushort.MaxValue
            ? 0
            : Math.Max(0, GetAmount(battle, _score.TheHuntPowerId, Limits.PlayerOwner));
        int missedTheHuntRewards = Math.Max(0, exhaustedTheHunts - rewardedTheHunts);
        int longTermResourceValue = _score.IgnoreGrowthRewards
            ? 0
            : realizedLongTermResourceValue - missedTheHuntRewards * TheHuntLongTermResourceValue;
        GrowthValues growthRewards = _score.IgnoreGrowthRewards
            ? default
            : CaptureGrowthRewards(battle);
        int growthHpCredit = _score.GrowthBudgets.Credit(growthRewards);
        score += Math.Min(
            realizedLongTermResourceValue * ScoreWeights.LongTermResourceBeamValue,
            ScoreWeights.LongTermResourceBeamCap);
        score += (double)growthHpCredit * hpWeight;
        RelicCounterGoals relicGoals = default;
        if (_relicTargets.Length != 0)
        {
            relicGoals = RelicCounterGoals.Evaluate(battle, _relicTargets, won, Rules);
            score += (double)relicGoals.HpCredit * hpWeight
                + relicGoals.Count * ScoreWeights.RelicGoalBeamValue;
        }

        int angerCopiesGenerated = battle.Counter(Counter.AngerCopiesGenerated);
        score += angerCopiesGenerated * ScoreWeights.AngerCopyBeamPenalty;
        if (won)
            score += ScoreWeights.VictoryBonus;
        score += enemyHp * ScoreWeights.EnemyHp;
        int liveDeckClutter = 0;
        int liveCardCount = battle.HandCount + battle.DrawCount + battle.DiscardCount;
        int retainedAttackValue = 0;
        bool hasBlockDamagePayoff = false;
        Span<SetupTraits> latentTraits = stackalloc SetupTraits[liveCardCount];
        Span<int> latentValues = stackalloc int[liveCardCount];
        int latentCount = 0;
        SetupTraits latentSetupTraits = SetupTraits.None;
        // One pass over the live piles feeds the clutter count, the retained attack value, the
        // Body Slam payoff flag and the latent setup values. The accumulators see the same pile and
        // card order as the separate loops they replace, so every sum and count is unchanged.
        for (int pile = 0; pile < 3; pile++)
        {
            Pile segment = pile switch
            {
                0 => Pile.Discard,
                1 => Pile.Draw,
                _ => Pile.Hand,
            };
            int start = Piles.Start(battle, segment);
            int count = Piles.Count(battle, segment);
            for (int offset = 0; offset < count; offset++)
            {
                ref readonly Card card = ref battle.Cards[start + offset];
                CardStats stats = Rules.Card(card.Id, card.Upgrade);
                CardScore cardScore = _score.Card(card.Id, card.Upgrade);
                CardType cardType = ResolveCardType(battle, card, stats);
                if ((cardType is CardType.Status or CardType.Curse)
                    && !LeavesHandAtTurnEnd(battle, segment, card, cardScore))
                {
                    liveDeckClutter = checked(liveDeckClutter + 1);
                }
                if (cardType == CardType.Attack)
                {
                    retainedAttackValue += Math.Max(
                        1,
                        (int)Math.Round(BranchCardValue(battle, card, stats, cardScore)));
                }
                if (card.Id == _score.BodySlamCardId)
                    hasBlockDamagePayoff = true;
                SetupTraits trait = cardScore.LatentTrait;
                int latentValue = cardScore.LatentValue;
                int rider = cardScore.DynamicCardType ? Registry.Card(card.Id).PrivateRider(card) : -1;
                if (cardScore.DynamicCardType && rider >= 0 && cardType == CardType.Power)
                {
                    // the existing search's StateEvaluation.LatentCardSetupValue's MadScience cases.
                    trait = rider == CardPrivateRiders.MadScienceCurious
                        ? SetupTraits.Curious
                        : SetupTraits.RecurringScaling;
                    latentValue = rider == CardPrivateRiders.MadScienceCurious
                        ? 10
                        : 5;
                }
                latentTraits[latentCount] = trait;
                latentValues[latentCount] = latentValue;
                latentCount++;
                latentSetupTraits |= trait;
            }
        }
        score += liveDeckClutter * ScoreWeights.LiveDeckClutterPenalty;

        // SimulatedCombatState's outstanding-stolen-resource counters. Only theft roots carry them; the
        // root flag keeps every other root off these per-node scans.
        int outstandingStolenResource = 0;
        int minimumOutstandingStolenResource = 0;
        if (Rules.HasTheftContent)
        {
            outstandingStolenResource = Kernel.OutstandingStolenResource(battle);
            minimumOutstandingStolenResource = Kernel.MinimumOutstandingStolenResource(battle);
        }
        if (_score.TheftPolicy == TheftPolicy.PreserveResources)
            score += outstandingStolenResource * ScoreWeights.OutstandingStolenResourcePenalty;
        ThreatFocus focus = BuildThreatFocus(battle);

        // Strategic effects over the powers that contribute (player-side buffs, positive amount, not
        // temporary), in the existing engine's listener order.
        StrategicEffectVector strategicEffects = default;
        double setupPotentialHp = 0;
        int offensivePersistentBuffValue = 0;
        SetupTraits persistentSetupTraits = SetupTraits.None;
        StrategicRequirements requirements = StrategicRequirements.None;
        bool needsExhaustDrawTiming = false;
        bool skillsExhaust = false;
        bool needsRecurringEnergy = false;
        // the existing search's StateEvaluation: the third-act branch reads Lethality's native first attack,
        // and the interaction potential is captured only when one of its three powers contributes.
        bool needsFirstAttackDamage = false;
        bool hasPagestorm = false;
        int danseMacabreEnergyThreshold = 0;
        int demesneAmount = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != Limits.PlayerOwner)
                continue;
            PowerScore rules = _score.Power(power.Id);
            if (!Contributes(power, rules))
                continue;
            requirements |= rules.Requirements;
            needsExhaustDrawTiming |= rules.Kind == StrategicKind.DarkEmbrace;
            skillsExhaust |= rules.Kind == StrategicKind.Corruption;
            needsRecurringEnergy |= rules.Kind is StrategicKind.RecurringEnergy or StrategicKind.OrbitEnergy;
            needsFirstAttackDamage |= rules.Kind == StrategicKind.Lethality;
            if (_score.Act3BossStrategy)
            {
                hasPagestorm |= rules.Kind == StrategicKind.Pagestorm;
                if (rules.Kind == StrategicKind.DanseMacabre)
                {
                    danseMacabreEnergyThreshold = Math.Max(
                        danseMacabreEnergyThreshold, rules.DanseMacabreEnergyThreshold);
                }
                if (rules.Kind == StrategicKind.Demesne)
                    demesneAmount += Math.Max(0, power.Amount);
            }
        }
        StrategicEffectContext? strategicContext = null;
        int refundSpend = 0, refundCapacity = 0, refundDraws = 0;
        bool refundCaptured = false;
        // The resource probes copy the battle only when a matching listener exists. One score reads
        // the same battle throughout, so the first probe's result serves every branch below.
        int modifiedMaxEnergy = -1;
        int modifiedHandDraw = -1;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != Limits.PlayerOwner)
                continue;
            PowerScore rules = _score.Power(power.Id);
            if (!Contributes(power, rules))
                continue;
            if (strategicContext is null)
            {
                StrategicEffectContext context = BuildStrategicContext(
                    battle, enemyHp, focus.TotalThreat, focus.IncomingHitCount, requirements, skillsExhaust);
                if (_score.Act3BossStrategy)
                {
                    context = context with
                    {
                        Act3BossInteractions = true,
                        FirstAttackDamage = needsFirstAttackDamage
                            ? CaptureFirstAttackDamage(battle, ref modifiedMaxEnergy) : 0,
                    };
                    if (hasPagestorm || danseMacabreEnergyThreshold > 0 || demesneAmount > 0)
                    {
                        var interactions = CaptureAct3BossInteractionPotential(
                            battle,
                            ref modifiedMaxEnergy,
                            ref modifiedHandDraw,
                            context.RemainingTurns,
                            context.ReachableCards,
                            hasPagestorm,
                            skillsExhaust,
                            demesneAmount,
                            danseMacabreEnergyThreshold);
                        context = context with
                        {
                            EtherealDrawTriggers = interactions.EtherealDrawTriggers,
                            PagestormBonusDrawCapacity = interactions.PagestormBonusDrawCapacity,
                            HighEnergyPlays = interactions.HighEnergyPlays,
                            DemesneEnergyGain = interactions.DemesneEnergyGain,
                            DemesneDrawGain = interactions.DemesneDrawGain,
                        };
                    }
                }
                strategicContext = needsExhaustDrawTiming
                    ? WithExhaustDrawTiming(context, battle, skillsExhaust) : context;
            }
            StrategicEffectContext effectContext = strategicContext.Value;
            if (needsRecurringEnergy && !refundCaptured)
            {
                (refundSpend, refundCapacity) = CaptureEnergyRefundWindow(
                    battle, effectContext, skillsExhaust, ref modifiedMaxEnergy);
                refundDraws = Math.Max(0, effectContext.ReachableCards - battle.HandCount);
                refundCaptured = true;
            }
            if (rules.Kind == StrategicKind.RecurringEnergy)
            {
                int cardsLeft = power.Aux0;
                int triggers = refundDraws < cardsLeft ? 0 : 1 + (refundDraws - cardsLeft) / 10;
                int gain = (int)Math.Min(refundCapacity, (long)triggers * power.Amount);
                refundCapacity -= gain;
                effectContext = effectContext with { RecurringEnergyGain = gain };
            }
            else if (rules.Kind == StrategicKind.OrbitEnergy)
            {
                // the existing search's StateEvaluation's Orbit branch: spend + remainder over 4, times the
                // energy granted per trigger. The shared refund capacity decreases per instance.
                int gain = (int)Math.Min(refundCapacity,
                    ((long)refundSpend + power.Aux0) / 4 * power.Amount);
                refundCapacity -= gain;
                effectContext = effectContext with { RecurringEnergyGain = gain };
            }
            StrategicEffectVector effect = EvaluateStrategic(rules.Kind, power.Amount, effectContext);
            strategicEffects += effect;
            if (SetupPolicy is { } setup)
                setupPotentialHp += setup.PreferenceHp(power, effect.RetentionValue);
            offensivePersistentBuffValue += effect.DamagePotential + effect.ScalingPotential;
            persistentSetupTraits |= rules.Trait;
        }
        if (SetupPolicy is {} setupPolicy && !dead && battle.Outcome == Outcome.None)
            score += Math.Min(48d, setupPotentialHp) * ScoreWeights.Hp;
        int persistentBuffValue = strategicEffects.RetentionValue;
        if (battle.OrbCount > 0)
        {
            // the existing search's StateEvaluation.OrbRetentionValue: the player's orb queue is an
            // OrbEngine setup trait, and each orb's (Focus-modified) passive adds to the persistent
            // buff value; Glass counts once per living enemy.
            persistentSetupTraits |= SetupTraits.OrbEngine;
            persistentBuffValue = checked(persistentBuffValue + OrbRetentionValue(battle, aliveEnemyCount));
        }

        int latentSetupValue = 0;
        for (int index = 0; index < latentCount; index++)
        {
            SetupTraits trait = latentTraits[index];
            if (trait == SetupTraits.None || !persistentSetupTraits.HasFlag(trait))
                latentSetupValue += latentValues[index];
        }
        int replayPotentialValue = ReplayPotentialValue(battle);
        // the existing search's StateEvaluation.CalculateReachableHandPotential reads the same playable
        // hand costs/values and feeds them to ReachableHandValue.Calculate.
        int reachableHandValue = 0;
        int zeroCostPlayableCount = 0;
        int playableAttackCount = 0;
        if (includeRetentionOnly)
        {
            (reachableHandValue, zeroCostPlayableCount, playableAttackCount) =
                ReachableHandPotential(battle);
        }
        int strategicRetentionValue = strategicEffects.RetentionValue;

        int retainedHandValue = 0;
        for (int offset = 0; offset < battle.HandCount; offset++)
        {
            ref readonly Card card = ref battle.Cards[offset];
            if ((card.Flags & CardFlags.RetainThisTurn) == 0
                && (card.Keywords & Keywords.Retain) == 0)
                continue;
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            CardScore cardScore = _score.Card(card.Id, card.Upgrade);
            retainedHandValue = checked(retainedHandValue + Math.Max(
                1,
                (int)Math.Ceiling(CardValue(battle, card, stats, cardScore) * 2d)));
        }
        // Free-card opportunity (FreeAttackPower), Void Form and summon next turn. Void Form has no
        // admitted Sts2Solver content.
        int summonNextTurn = GetAmount(battle, _score.SummonNextTurnPowerId, Limits.PlayerOwner);
        int summonNextTurnValue = summonNextTurn == 0
            ? 0
            : summonNextTurn * (4 + Math.Min(12, CountLiveOstyAttackCards(battle) * 2));
        int futureResourceValue = GetAmount(battle, _score.EnergyNextTurnPowerId, Limits.PlayerOwner) * 16
            + GetAmount(battle, _score.DrawCardsNextTurnPowerId, Limits.PlayerOwner) * 8
            + GetAmount(battle, _score.StarNextTurnPowerId, Limits.PlayerOwner) * 8
            + GetAmount(battle, _score.RetainHandPowerId, Limits.PlayerOwner) * 4
            + summonNextTurnValue
            + retainedHandValue
            + FreeCardOpportunityValue(battle, CardType.Attack, _score.FreeAttackPowerId)
            + FreeCardOpportunityValue(battle, CardType.Skill, _score.FreeSkillPowerId)
            + FreeCardOpportunityValue(battle, CardType.Power, _score.FreePowerPowerId)
            + VoidFormOpportunityValue(battle);

        // A poison tick applies the current stacks and decrements once per hit. A lethal turn
        // resolves only the single tick before the player dies. Otherwise an enemy's poison ticks
        // while that enemy lives: enemies are assumed to die in ascending effective-HP order under
        // the shared damage model (next turn's damage plus linear growth), so enemy e lives the
        // turns needed to deal the HP of the enemies killed before it plus its own, at most the
        // shared remaining-turn estimate R (the last enemy) and counted as at least two ticks.
        int poisonTriggers = 0;
        bool poisonPrepared = false;
        bool poisonLethal = false;
        Span<int> enemyLife = stackalloc int[battle.EnemyCount];
        int delayedDamageValue = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.InCombat) == 0 || !enemy.Creature.IsAlive)
                continue;
            byte owner = Kernel.EnemyOwner((byte)index);
            int poison = Math.Max(0, GetAmount(battle, _score.PoisonPowerId, owner));
            int demise = Math.Max(0, GetAmount(battle, _score.DemisePowerId, owner));
            int doom = Math.Max(0, GetAmount(battle, _score.DoomPowerId, owner));
            int currentHp = Math.Max(0, enemy.Creature.CurrentHp);
            int poisonValue = 0;
            if (poison > 0)
            {
                if (!poisonPrepared)
                {
                    poisonTriggers = PoisonTriggersPerTurn(battle);
                    poisonLethal = projectedHp <= 0;
                    if (!poisonLethal)
                    {
                        ScoreFeatures turnFeatures = new()
                        {
                            RetainedAttackValue = retainedAttackValue,
                            LiveDeckSize = liveCardCount,
                            EnemyHp = enemyHp,
                            AliveEnemyCount = aliveEnemyCount,
                        };
                        PowerEvidence.View turnView = new(
                            battle, turnFeatures, Rules, this);
                        turnView.FillEnemyLifeTurns(enemyLife);
                    }
                    poisonPrepared = true;
                }
                int poisonTurns = poisonLethal
                    ? 1
                    : Math.Max(2, enemyLife[index]);
                poisonValue = PoisonStackProjection.DelayedStackDamage(
                    poison, poisonTriggers, poisonTurns, currentHp);
            }
            int cappedDoom = Math.Min(currentHp, doom);
            int doomProgress = currentHp <= 0
                ? 0
                : (int)Math.Min(currentHp, (long)cappedDoom * cappedDoom / currentHp);
            delayedDamageValue = checked(
                delayedDamageValue + poisonValue + demise + doomProgress);
        }
        persistentBuffValue += CountLiveSoulCards(battle);
        int reactiveDamageValue = Math.Max(0, GetAmount(battle, _score.SleightOfFleshPowerId, Limits.PlayerOwner));

        int offensiveProgressValue = offensivePersistentBuffValue
            + delayedDamageValue
            + reactiveDamageValue
            + (hasBlockDamagePayoff ? Math.Max(0, battle.Player.Block) : 0);

        int enemyStrengthSuppression = 0;
        int enemyWeakTurns = 0;
        int vulnerable = 0;
        int focusTargetVulnerableTurns = 0;
        uint? mostVulnerableTargetCombatId = null;
        int mostVulnerableTurns = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.InCombat) == 0 || !enemy.Creature.IsAlive)
                continue;
            byte owner = Kernel.EnemyOwner((byte)index);
            int strengthSuppression = -GetAmount(battle, _score.StrengthPowerId, owner);
            int weakTurns = Math.Max(0, GetAmount(battle, _score.WeakPowerId, owner));
            int vulnerableTurns = Math.Max(0, GetAmount(battle, _score.VulnerablePowerId, owner));
            enemyStrengthSuppression += strengthSuppression;
            enemyWeakTurns += weakTurns;
            vulnerable += vulnerableTurns;
            if (focus.CombatId is { } focusId && enemy.CombatId == focusId)
                focusTargetVulnerableTurns = vulnerableTurns;
            if (vulnerableTurns > mostVulnerableTurns)
            {
                mostVulnerableTurns = vulnerableTurns;
                mostVulnerableTargetCombatId = enemy.CombatId;
            }
        }


        // the existing search's StateEvaluation: positive Sandpit stacks targeting this player whose owner is
        // alive, summed with checked overflow. The existing engine visits EffectivePowers (live roster
        // powers); dead owners have their powers removed by ApplyEnemyDeathPowers.
        int sandpitRemaining = 0;
        if (_score.SandpitPowerId != ushort.MaxValue)
        {
            ref Battle mutable = ref Unsafe.AsRef(in battle);
            for (int index = 0; index < battle.PowerCount; index++)
            {
                ref readonly Power power = ref battle.Powers[index];
                if (power.Id != _score.SandpitPowerId
                    || power.Target != Limits.PlayerOwner
                    || !mutable.Creature(power.Owner).IsAlive)
                {
                    continue;
                }
                sandpitRemaining = checked(sandpitRemaining + Math.Max(0, power.Amount));
            }
        }

        int vulnerableAttackWindow = Math.Min(
            ScoreWeights.VulnerableAttackWindowCap,
            retainedAttackValue);
        score += (long)focusTargetVulnerableTurns
            * vulnerableAttackWindow
            * ScoreWeights.VulnerableAttackMultiplierBeamValue;
        score += (long)Math.Max(0, vulnerable - focusTargetVulnerableTurns)
            * vulnerableAttackWindow
            * ScoreWeights.OffTargetVulnerableAttackMultiplierBeamValue;
        score += actionCount * ScoreWeights.ActionPenalty;
        if (risk)
            score += ScoreWeights.RiskPenalty;

        (int potionUseCount, int potionStrategicCost, int explicitPotionStrategicCost,
            int automaticPotionUseCount, _) = MeasurePotions(battle);
        int defensiveBlockValue = MeasureDefensiveBlockReserve(battle, blockConsumed);

        // Retention-only projections (the existing search's StateEvaluation): the projected shuffle
        // order, the unordered pile multiset and the Art of War next-turn window. None of these enter
        // Score, BeamRankScore or the route key, so a plain scored state skips them.
        RetentionValues retention = includeRetentionOnly ? CaptureRetentionValues(battle) : default;

        return new ScoreFeatures
        {
            Score = score,
            PlayerDead = dead,
            AllEnemiesDead = won,
            PlayerHp = battle.Player.CurrentHp,
            PlayerMaxHp = battle.Player.MaxHp,
            PlayerBlock = battle.Player.Block,
            OstyHp = (battle.Flags & BattleFlags.OstyPresent) != 0 ? battle.Osty.CurrentHp : 0,
            OstyMaxHp = (battle.Flags & BattleFlags.OstyPresent) != 0 ? battle.Osty.MaxHp : 0,
            CumulativePlayerHpLost = cumulativePlayerHpLost,
            RecoveredPlayerHp = recoveredPlayerHp,
            DeathSaveHpRestored = deathSaveHpRestored,
            DeathSaveUseCount = deathSaveUseCount,
            ProjectedDeathSaveUseCount = deathSaveUseCount + projectedDeathSaveUseCount,
            ProjectedPlayerHp = projectedHp,
            DefensiveBlockValue = defensiveBlockValue,
            LongTermResourceValue = longTermResourceValue,
            GrowthHpCredit = growthHpCredit,
            GrowthRewards = growthRewards,
            GrowthRewardCount = growthRewards.Total,
            RelicGoalHpCredit = relicGoals.HpCredit,
            RelicGoalCount = relicGoals.Count,
            StrategicHpCredit = growthHpCredit + relicGoals.HpCredit,
            AngerCopiesGenerated = angerCopiesGenerated,
            EnemyHp = enemyHp,
            EnemyBlock = enemyBlock,
            RawEnemyHp = rawEnemyHp,
            MaxCurrentEnemyHp = maxCurrentEnemyHp,
            AliveEnemyCount = aliveEnemyCount,
            AliveEnemyMask = aliveEnemyMask,
            RevivingEnemyCount = revivingEnemyCount,
            LiveDeckClutter = liveDeckClutter,
            LiveDeckSize = liveCardCount,
            OutstandingStolenResource = outstandingStolenResource,
            MinimumOutstandingStolenResource = minimumOutstandingStolenResource,
            RetainedAttackValue = retainedAttackValue,
            FocusTargetCombatId = focus.CombatId,
            FocusTargetPressure = focus.Pressure,
            FocusTargetRemainingHp = focus.RemainingHp,
            FocusTargetCurrentThreat = focus.CurrentThreat,
            FocusTargetVulnerableTurns = focusTargetVulnerableTurns,
            MostVulnerableTargetCombatId = mostVulnerableTargetCombatId,
            EnemyVulnerableTurns = vulnerable,
            Energy = battle.Energy,
            Stars = battle.Stars,
            HandCount = battle.HandCount,
            Turn = battle.PlayerTurn,
            ReachableHandValue = reachableHandValue,
            ZeroCostPlayableCount = zeroCostPlayableCount,
            PlayableAttackCount = playableAttackCount,
            PersistentBuffValue = persistentBuffValue,
            StrategicRetentionValue = strategicRetentionValue,
            SetupPotentialHp = setupPotentialHp,
            StrategicDamagePotential = strategicEffects.DamagePotential,
            StrategicPreventionPotential = strategicEffects.PreventionPotential,
            StrategicResourcePotential = strategicEffects.ResourcePotential,
            StrategicCardAccessPotential = strategicEffects.CardAccessPotential,
            StrategicScalingPotential = strategicEffects.ScalingPotential,
            HasRisk = risk,
            Act3BossInteractions = strategicContext?.Act3BossInteractions ?? false,
            FirstAttackDamage = strategicContext?.FirstAttackDamage ?? 0,
            EtherealDrawTriggers = strategicContext?.EtherealDrawTriggers ?? 0,
            PagestormBonusDrawCapacity = strategicContext?.PagestormBonusDrawCapacity ?? 0,
            HighEnergyPlays = strategicContext?.HighEnergyPlays ?? 0,
            DemesneEnergyGain = strategicContext?.DemesneEnergyGain ?? 0,
            DemesneDrawGain = strategicContext?.DemesneDrawGain ?? 0,
            PersistentSetupTraits = (PersistentSetupTraits)persistentSetupTraits,
            StrategicSetupTraits = (PersistentSetupTraits)(persistentSetupTraits | latentSetupTraits),
            LatentSetupValue = latentSetupValue,
            FutureResourceValue = futureResourceValue,
            ReplayPotentialValue = replayPotentialValue,
            DelayedDamageValue = delayedDamageValue,
            ReactiveDamageValue = reactiveDamageValue,
            OffensiveProgressValue = offensiveProgressValue,
            SandpitRemaining = sandpitRemaining,
            EnemyStrengthSuppression = enemyStrengthSuppression,
            EnemyWeakTurns = enemyWeakTurns,
            PotionUseCount = potionUseCount,
            PotionStrategicCost = potionStrategicCost,
            AutomaticPotionUseCount = automaticPotionUseCount,
            ExplicitPotionStrategicCost = explicitPotionStrategicCost,
            ExplicitPotionUseCount = checked(potionUseCount - automaticPotionUseCount),
            ProjectedShuffleOrderValue = retention.ProjectedShuffleOrderValue,
            ProjectedShuffleOrderKeyFirst = retention.ProjectedShuffleOrderKeyFirst,
            ProjectedShuffleOrderKeySecond = retention.ProjectedShuffleOrderKeySecond,
            CanTriggerArtOfWarNextTurn = retention.CanTriggerArtOfWarNextTurn,
            EnemyCombatDistributionFirst = retention.EnemyCombatDistributionFirst,
            EnemyCombatDistributionSecond = retention.EnemyCombatDistributionSecond,
            EnemyControlDistributionFirst = retention.EnemyControlDistributionFirst,
            EnemyControlDistributionSecond = retention.EnemyControlDistributionSecond,
            UnorderedPileKeyFirst = retention.UnorderedPileKeyFirst,
            UnorderedPileKeySecond = retention.UnorderedPileKeySecond,
        };
    }

    /// <summary>
    /// Retention-only values shared by K4 and the retention facts. Computed once per stored state (and
    /// once per K4-checked state), never on the plain score path.
    /// </summary>
    internal readonly record struct RetentionValues(
        int ProjectedShuffleOrderValue,
        ulong ProjectedShuffleOrderKeyFirst,
        ulong ProjectedShuffleOrderKeySecond,
        bool CanTriggerArtOfWarNextTurn,
        ulong EnemyCombatDistributionFirst,
        ulong EnemyCombatDistributionSecond,
        ulong EnemyControlDistributionFirst,
        ulong EnemyControlDistributionSecond,
        ulong UnorderedPileKeyFirst,
        ulong UnorderedPileKeySecond);

    internal RetentionValues CaptureRetentionValues(in Battle battle)
    {
        (int value, ulong projectedFirst, ulong projectedSecond,
            ulong unorderedFirst, ulong unorderedSecond) = CaptureShuffleAndUnorderedKeys(battle);
        (ulong combatFirst, ulong combatSecond) = CaptureEnemyCombatDistribution(battle);
        (ulong controlFirst, ulong controlSecond) = CaptureEnemyControlDistribution(battle);
        bool artOfWar = false;
        ushort artOfWarId = Registry.ArtOfWarRelicId;
        if (artOfWarId != 0)
        {
            for (int index = 0; index < Rules.Relics.Count; index++)
            {
                Relic relic = battle.GetRelic(Rules, index);
                if (relic.Id == artOfWarId && (relic.Flags & RelicFlags.Melted) == 0
                    && relic.Counter == 0)
                {
                    artOfWar = true;
                    break;
                }
            }
        }
        return new RetentionValues(value, projectedFirst, projectedSecond, artOfWar,
            combatFirst, combatSecond, controlFirst, controlSecond, unorderedFirst, unorderedSecond);
    }

    /// <summary>
    /// <c>SearchEvaluator</c>'s enemy combat distribution: per known enemy, in list order,
    /// (CombatId, current HP, block, effective HP, containment).
    /// </summary>
    private (ulong First, ulong Second) CaptureEnemyCombatDistribution(in Battle battle)
    {
        StateFingerprintBuilder key = new();
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            key.Add(enemy.CombatId);
            key.Add(enemy.Creature.CurrentHp);
            key.Add(enemy.Creature.Block);
            key.Add(EffectiveEnemyHp(battle, index));
            key.Add((enemy.Flags & EnemyFlags.InCombat) != 0);
        }
        StateFingerprint finish = key.Finish();
        return (finish.First, finish.Second);
    }

    /// <summary>
    /// <c>SearchEvaluator</c>'s enemy control distribution: per living, contained enemy,
    /// (CombatId, strength suppression, weak turns, vulnerable turns).
    /// </summary>
    private (ulong First, ulong Second) CaptureEnemyControlDistribution(in Battle battle)
    {
        StateFingerprintBuilder key = new();
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.InCombat) == 0 || !enemy.Creature.IsAlive)
                continue;
            byte owner = Kernel.EnemyOwner((byte)index);
            key.Add(enemy.CombatId);
            key.Add(-GetAmount(battle, _score.StrengthPowerId, owner));
            key.Add(Math.Max(0, GetAmount(battle, _score.WeakPowerId, owner)));
            key.Add(Math.Max(0, GetAmount(battle, _score.VulnerablePowerId, owner)));
        }
        StateFingerprint finish = key.Finish();
        return (finish.First, finish.Second);
    }

    /// <summary>
    /// <c>SearchEvaluator.BuildProjectedShuffleOrder</c>: discard + draw + hand in that order, the
    /// native stable-shuffle projection (unstable sort by card id/upgrade, then Fisher-Yates from the
    /// end on a copy of the shuffle RNG), then the value sum and a card-state key. The existing key
    /// hashes the existing engine's per-card fingerprint; Sts2Solver hashes the card byte image instead.
    /// </summary>
    private (int Value, ulong ProjectedFirst, ulong ProjectedSecond,
        ulong UnorderedFirst, ulong UnorderedSecond) CaptureShuffleAndUnorderedKeys(in Battle battle)
    {
        int total = battle.DiscardCount + battle.DrawCount + battle.HandCount;
        Span<Card> cards = stackalloc Card[total];
        Span<ulong> hashes = stackalloc ulong[total];
        Span<int> origins = stackalloc int[total];
        Span<ulong> shuffledHashes = stackalloc ulong[total];
        int discardCount = battle.DiscardCount;
        int drawCount = battle.DrawCount;
        int handCount = battle.HandCount;
        int position = 0;
        CopyPileAndHash(battle, Pile.Discard, cards, hashes, ref position,
            out ulong discardFirst, out ulong discardSecond);
        CopyPileAndHash(battle, Pile.Draw, cards, hashes, ref position,
            out ulong drawFirst, out ulong drawSecond);
        CopyPileAndHash(battle, Pile.Hand, cards, hashes, ref position,
            out ulong handFirst, out ulong handSecond);
        // The unordered key also covers the exhaust pile, which the projected shuffle does not use.
        ulong exhaustFirst = 0, exhaustSecond = 0;
        ReadOnlySpan<Card> exhaust = Piles.ReadCards(battle, Pile.Exhaust);
        for (int index = 0; index < exhaust.Length; index++)
        {
            ulong hash = Fingerprint.HashCard(exhaust[index]);
            exhaustFirst += StateFingerprintBuilder.MixFirst(hash);
            exhaustSecond += StateFingerprintBuilder.MixSecond(hash);
        }
        StateFingerprintBuilder unordered = new();
        AppendUnorderedPile(ref unordered, 'H', handCount, handFirst, handSecond);
        AppendUnorderedPile(ref unordered, 'D', drawCount, drawFirst, drawSecond);
        AppendUnorderedPile(ref unordered, 'C', discardCount, discardFirst, discardSecond);
        AppendUnorderedPile(ref unordered, 'X', exhaust.Length, exhaustFirst, exhaustSecond);
        StateFingerprint unorderedFinish = unordered.Finish();

        for (int index = 0; index < total; index++)
            origins[index] = index;
        CardSort.SortWithOrigins(cards, origins, Rules, in battle, Pile.Draw);
        for (int index = 0; index < total; index++)
            shuffledHashes[index] = hashes[origins[index]];
        ref Battle mutable = ref Unsafe.AsRef(in battle);
        RngState parentShuffle = mutable.RngStream(RngStream.Shuffle);
        int shuffleCounter = parentShuffle.Counter;
        RngState shuffle = parentShuffle.CreateShuffleRng();
        for (int n = total; n > 1;)
        {
            n--;
            int k = shuffle.NextInt(n + 1);
            (cards[k], cards[n]) = (cards[n], cards[k]);
            (shuffledHashes[k], shuffledHashes[n]) = (shuffledHashes[n], shuffledHashes[k]);
        }
        StateFingerprintBuilder key = new();
        key.Add(shuffleCounter);
        key.Add(total);
        int value = 0;
        for (int index = 0; index < total; index++)
        {
            Card card = cards[index];
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            CardScore cardScore = _score.Card(card.Id, card.Upgrade);
            // CardChoiceSupport.CardValue reads the live DynamicVars BaseValue, which some cards
            // (SovereignBlade's forged damage) grow per instance; the instance deltas are the same
            // values the score table's CardValue applies.
            double cardValue = CardValue(battle, card, stats, cardScore);
            value += (int)Math.Round(cardValue * (total - index));
            ulong hash = shuffledHashes[index];
            key.Add(StateFingerprintBuilder.MixFirst(hash));
            key.Add(StateFingerprintBuilder.MixSecond(hash));
        }
        StateFingerprint finish = key.Finish();
        return (value, finish.First, finish.Second, unorderedFinish.First, unorderedFinish.Second);
    }

    /// <summary>
    /// Copies one live pile into the shuffle buffer and records each card's fingerprint once. The same
    /// hash feeds the projected shuffle key (after the sort/Fisher-Yates reorders it with the card) and
    /// the order-invariant unordered-pile sums, so no card is hashed twice.
    /// </summary>
    private static void CopyPileAndHash(in Battle battle, Pile pile, Span<Card> cards,
        Span<ulong> hashes, ref int position, out ulong first, out ulong second)
    {
        ReadOnlySpan<Card> source = Piles.ReadCards(battle, pile);
        source.CopyTo(cards[position..]);
        first = 0;
        second = 0;
        for (int index = 0; index < source.Length; index++)
        {
            ulong hash = Fingerprint.HashCard(source[index]);
            hashes[position + index] = hash;
            first += StateFingerprintBuilder.MixFirst(hash);
            second += StateFingerprintBuilder.MixSecond(hash);
        }
        position += source.Length;
    }

    private static void AppendUnorderedPile(ref StateFingerprintBuilder unordered, char marker,
        int count, ulong first, ulong second)
    {
        unordered.Add(marker);
        unordered.Add(count);
        unordered.Add(first);
        unordered.Add(second);
    }

    private static readonly ushort StockPowerId =
        Registry.TryPower("STOCK_POWER", out ushort stock) ? stock : ushort.MaxValue;
    private static readonly ushort AxebotMonsterId =
        Registry.TryMonster("AXEBOT", out ushort axebot) ? axebot : ushort.MaxValue;

    /// <summary>
    /// HP of an Axebot's pending replacements (native <c>StockPower.AfterDeath</c>): each one spawns
    /// in the same slot with one less stock, at the compiled replacement rule's HP range shifted by
    /// RespawnMaxHpBonus for every further missing stock (the range shift the kernel's Axebot
    /// AfterDeath applies). The combat cannot end before they are dealt with too. The range maximum
    /// is used: the replacement's HP is rolled only at spawn, and a lower estimate would make killing
    /// an Axebot raise the remaining HP whenever the roll lands above it, teaching the search to
    /// leave it alive.
    /// </summary>
    private int StockReplacementHp(in Battle battle, int index)
    {
        if (AxebotMonsterId == ushort.MaxValue || battle.Enemies[index].MonsterId != AxebotMonsterId)
            return 0;
        int stock = Math.Max(0, GetAmount(battle, StockPowerId, Kernel.EnemyOwner((byte)index)));
        if (stock == 0)
            return 0;
        int rulesId = Rules.SpawnRulesId(AxebotMonsterId);
        int maximum = Rules.MonsterRule(checked((byte)rulesId)).InitialHpMax;
        int total = 0;
        for (int replacement = 1; replacement <= stock; replacement++)
        {
            int newStock = stock - replacement;
            total = checked(total + Math.Max(1, maximum + (1 - newStock) * AxebotRespawnHpBonus));
        }
        return total;
    }

    /// <summary>Native Axebot <c>RespawnMaxHpBonus</c> per respawn.</summary>
    private const int AxebotRespawnHpBonus = 10;

    /// <summary><see cref="EffectiveEnemyHp"/> for the remaining-turn kill order.</summary>
    internal int EffectiveEnemyHpForEvidence(in Battle battle, int index)
        => EffectiveEnemyHp(battle, index);

    private int EffectiveEnemyHp(in Battle battle, int index)
    {
        ref readonly Enemy enemy = ref battle.Enemies[index];
        // SimulatedCombatState.EffectiveEnemyHp: an armed Steam Eruption phase counts as zero HP.
        if (enemy.SteamPhase != SteamPhase.None)
            return 0;
        // SimulatedCombatState.EffectiveEnemyHp's TestSubject branch: while Adaptable is alive, the
        // remaining forms' HP is part of the effective HP even at full current HP.
        if (_score.AdaptablePowerId != ushort.MaxValue
            && HasPower(battle, _score.AdaptablePowerId, Kernel.EnemyOwner((byte)index))
            && enemy.DeathPhase != DeathPhase.PermanentlyDead
            && Registry.Monster(enemy.MonsterId).HasAdaptableFormHp)
        {
            MonsterRules rules = Rules.MonsterRule(enemy.Rules);
            int remaining = Math.Max(0, enemy.Creature.CurrentHp);
            if (enemy.Ai[0] < 1)
                remaining += enemy.Ai[2];
            if (enemy.Ai[0] < 2)
                remaining += enemy.Ai[3];
            return remaining;
        }
        if (enemy.Creature.CurrentHp > 0)
            return checked(enemy.Creature.CurrentHp + StockReplacementHp(battle, index));
        if (enemy.DeathPhase != DeathPhase.Reviving)
            return 0;
        byte owner = Kernel.EnemyOwner((byte)index);
        // SimulatedCombatState.RevivingEnemyHp: a Decimillipede segment restores its ReattachPower
        // amount only while another reattaching segment has not completed death (alive or reviving).
        if (HasPower(battle, _score.ReattachPowerId, owner))
        {
            for (int position = 0; position < battle.RosterCount; position++)
            {
                byte other = Kernel.EnemyOwner(battle.Roster[position]);
                if (other == owner || !HasPower(battle, _score.ReattachPowerId, other))
                    continue;
                if (battle.Enemies[battle.Roster[position]].DeathPhase != DeathPhase.PermanentlyDead)
                    return Math.Max(0, GetAmount(battle, _score.ReattachPowerId, owner));
            }
            return 0;
        }
        // Illusion-powered revives restore the captured max HP; the power amount is irrelevant.
        return HasPower(battle, _score.IllusionPowerId, owner) ? enemy.Creature.MaxHp : 0;
    }

    /// <summary>
    /// HP that still has to be removed before the combat can end: every primary enemy's
    /// <see cref="EffectiveEnemyHp"/> (including Test Subject's later forms, Decimillipede's
    /// reattach heal and an Axebot's stocked replacements). Secondary enemies (MinionPower, including the reviving
    /// EyeWithTeeth and Parafright) are excluded: the native <c>CombatManager.IsCombatEnding</c>
    /// ends the combat when no primary enemy is alive, and <c>CreatureCmd.Kill</c> kills all
    /// remaining all-secondary teammates when the last primary dies.
    /// </summary>
    internal int RemainingPrimaryEnemyHp(in Battle battle)
    {
        long total = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.InCombat) == 0)
                continue;
            byte owner = Kernel.EnemyOwner((byte)index);
            if (!Kernel.IsPrimaryEnemy(battle, owner))
                continue;
            total += Math.Max(0, EffectiveEnemyHp(battle, index));
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private bool LeavesHandAtTurnEnd(in Battle battle, Pile pile, in Card card, CardScore score)
    {
        if (pile != Pile.Hand)
            return false;
        if (score.HasTurnEndInHandEffect)
            return false;
        // The existing engine reads `card.HasKeyword(State, Ethereal)`, i.e. through Hook.ModifyKeywords:
        // HexPower grants Ethereal to every Hexed card, so a Hexed status/curse in hand leaves at the
        // turn end even without a local Ethereal keyword. The local keyword alone under-counts the
        // exception and makes the card look like persistent clutter.
        // Hook.ShouldEtherealTrigger has no supported listeners, so it is always true here.
        return Kernel.HasKeyword(battle, card, Keywords.Ethereal);
    }

    private int ReplayPotentialValue(in Battle battle)
    {
        int total = 0;
        for (int pile = 0; pile < 3; pile++)
        {
            Pile segment = pile switch
            {
                0 => Pile.Discard,
                1 => Pile.Draw,
                _ => Pile.Hand,
            };
            int start = Piles.Start(battle, segment);
            int count = Piles.Count(battle, segment);
            for (int offset = 0; offset < count; offset++)
            {
                ref readonly Card card = ref battle.Cards[start + offset];
                CardStats stats = Rules.Card(card.Id, card.Upgrade);
                int replayCount = Math.Max(0, Kernel.EnchantedReplayCount(card));
                if (replayCount == 0
                    || ResolveCardType(battle, card, stats) is not (CardType.Attack or CardType.Skill or CardType.Power))
                    continue;
                CardScore cardScore = _score.Card(card.Id, card.Upgrade);
                double perPlayValue = Math.Max(4d, BranchCardValue(battle, card, stats, cardScore));
                total += (int)Math.Ceiling(perPlayValue * replayCount);
                if (total >= ScoreWeights.ReplayPotentialBeamCap)
                    return ScoreWeights.ReplayPotentialBeamCap;
            }
        }
        return total;
    }

    // ---------------------------------------------------------------------------------------------
    // Projected threat.

    /// <summary>
    /// Result of the threat projection: remaining HP, the one-shot death saves it would consume, the
    /// block spent on incoming hits, and the total blockable damage in the projected sequence
    /// (independent of how much block the state currently has).
    /// </summary>
    internal readonly record struct ThreatProjection(
        int Hp, int DeathSaveHpRestored, int DeathSaveUseCount, int BlockConsumed = 0,
        long BlockableDamage = 0);

    internal ThreatProjection ProjectHpAfterThreat(in Battle battle)
    {
        if (!VerifyThreatProjection)
            return ProjectHpAfterThreatBody(in battle);
        Unsafe.SkipInit(out Battle before);
        Battle.CopyPacked(in battle, ref before);
        ThreatProjection projection = ProjectHpAfterThreatBody(in battle);
        CheckThreatProjection(in before, in Unsafe.AsRef(in battle));
        return projection;
    }

    private ThreatProjection ProjectHpAfterThreatBody(in Battle battle)
    {
        // the existing search's ProjectHpAfterThreat walks the run-listener table, which still contains the
        // player's relics and cards after the player dies; Sts2Solver's PlayerHooksActive flag only gates the
        // combat dispatch. The projection must include those modifiers (Tungsten Rod's per-hit reduction
        // is the visible case) even on a dead snapshot.
        //
        // The only write performed by the projected listeners is BufferPower's
        // AfterModifyingHpLostAfterOsty, which sets that power's amount (and may remove the row). Every
        // other ModifyDamage / ModifyHpLost / ModifyUnblockedDamageTarget implementation is a pure
        // query. Run the projection in place and restore the flags and, only when a Buffer power is
        // present, the power array and count. This avoids a 12 KiB Battle copy per scored state.
        ref Battle probe = ref Unsafe.AsRef(in battle);
        BattleFlags savedFlags = probe.Flags;
        bool mayConsumeBuffer = _score.BufferPowerId != ushort.MaxValue;
        if (mayConsumeBuffer)
        {
            mayConsumeBuffer = false;
            for (int index = 0; index < probe.PowerCount; index++)
            {
                if (probe.Powers[index].Id == _score.BufferPowerId)
                {
                    mayConsumeBuffer = true;
                    break;
                }
            }
        }
        int savedPowerCount = 0;
        PowerArray savedPowers;
        Unsafe.SkipInit(out savedPowers);
        if (mayConsumeBuffer)
        {
            savedPowerCount = probe.PowerCount;
            savedPowers = probe.Powers;
        }
        probe.Flags = savedFlags | BattleFlags.PlayerHooksActive;
        try
        {
            return ProjectHpAfterThreatCore(ref probe);
        }
        finally
        {
            probe.Flags = savedFlags;
            if (mayConsumeBuffer)
            {
                probe.PowerCount = (byte)savedPowerCount;
                probe.Powers = savedPowers;
            }
        }
    }

    private void CheckThreatProjection(in Battle before, in Battle after)
    {
        ThreatProjectionChecks++;
        if (BattleBytesEqual(before, after))
            return;
        List<string> differences = BattleDump.Diff(before, after, "before", "after");
        throw new InvalidOperationException(
            "威胁投影原地自检失败：" + string.Join(" | ", differences));
    }

    private static bool BattleBytesEqual(in Battle left, in Battle right)
        => Fingerprint.Equal(in left, in right);

    private ThreatProjection ProjectHpAfterThreatCore(ref Battle probe)
    {
        Exec x = new(ref probe, Rules, default);
        int hp = probe.Player.CurrentHp;
        int block = probe.Player.Block;
        int originalBlock = block;
        long blockableDamage = 0;
        // the existing search's ProjectHpAfterThreat's ostyHp: the pet absorbs redirected powered attacks
        // until it dies; the remaining loss spills over to the player.
        int ostyHp = (probe.Flags & BattleFlags.OstyPresent) != 0 ? probe.Osty.CurrentHp : 0;
        ProjectedDeathPrevention prevention = new(probe, _score, Rules);
        // the existing search's ProjectHpAfterThreat's gambitActive latch: the first unblocked damage from a
        // powered attack kills the owner through TheGambitPower, then death prevention may revive it.
        bool gambitActive = _score.TheGambitPowerId != ushort.MaxValue
            && GetAmount(probe, _score.TheGambitPowerId, Limits.PlayerOwner) > 0;

        // One pass over the power table serves the Sandpit boundary and collects every player-owned
        // power that declares a turn-end self-damage. The table is the existing per-state scan the
        // Sandpit check already performed.
        Span<ushort> selfDamagePowers = stackalloc ushort[probe.PowerCount];
        int selfDamagePowerCount = 0;
        bool sandpitKills = false;
        for (int index = 0; index < probe.PowerCount; index++)
        {
            ref readonly Power power = ref probe.Powers[index];
            if (_score.SandpitPowerId != ushort.MaxValue
                && power.Id == _score.SandpitPowerId
                && power.Amount == 1
                && power.Target == Limits.PlayerOwner
                && probe.Creature(power.Owner).IsAlive)
            {
                sandpitKills = true;
            }
            if (power.Owner == Limits.PlayerOwner
                && Registry.Power(power.Id).TryGetTurnEndSelfDamage(probe, Rules, index, out _))
            {
                selfDamagePowers[selfDamagePowerCount++] = (ushort)index;
            }
        }

        // 1. Turn-end cards resolve from the hand in hand order. The root-level rule flag skips the
        // hand scan when this root's rule table has no turn-end-in-hand card at all.
        if (Rules.HasTurnEndInHandCards)
        {
            int handCount = probe.HandCount;
            for (int position = 0; position < handCount; position++)
            {
                ref readonly Card card = ref probe.Cards[position];
                CardDef def = Registry.Card(card.Id);
                if (!def.HasTurnEndInHandEffect
                    || !def.TryGetTurnEndSelfDamage(probe, Rules, card, out TurnEndSelfDamage damage))
                {
                    continue;
                }
                ProjectSelfDamage(ref x, in damage, in card, ref ostyHp, ref block, ref hp,
                    ref blockableDamage, ref gambitActive, ref prevention);
            }
        }

        // 2. Player-side-turn-end powers (Constrict), the TriggerRegular listener pass (Demise), then
        // AfterSideTurnEndLate (Disintegration).
        ProjectPowerSelfDamagePhase(ref x, selfDamagePowers[..selfDamagePowerCount],
            TurnEndSelfDamageTiming.PlayerSideTurnEnd, ref ostyHp, ref block, ref hp,
            ref blockableDamage, ref gambitActive, ref prevention);
        ProjectPowerSelfDamagePhase(ref x, selfDamagePowers[..selfDamagePowerCount],
            TurnEndSelfDamageTiming.EndTurnRegular, ref ostyHp, ref block, ref hp,
            ref blockableDamage, ref gambitActive, ref prevention);
        ProjectPowerSelfDamagePhase(ref x, selfDamagePowers[..selfDamagePowerCount],
            TurnEndSelfDamageTiming.AfterSideTurnEndLate, ref ostyHp, ref block, ref hp,
            ref blockableDamage, ref gambitActive, ref prevention);

        // 3. The Sandpit countdown happens at the enemy side turn start, after the player turn end. Its
        // force kill ignores block and one-shot death saves; the death saves consumed above stay on the
        // projection.
        if (sandpitKills)
            return BuildThreatProjection(0, in prevention, originalBlock, block, blockableDamage);

        // 4. Enemy attacks in native roster order. Like the previous projection, keep walking after
        // the player reaches zero HP so the projected block accounting remains the old one; each hit
        // still runs death prevention independently.
        for (int position = 0; position < probe.RosterCount; position++)
        {
            ref readonly Enemy enemy = ref probe.Enemies[probe.Roster[position]];
            byte owner = Kernel.EnemyOwner(probe.Roster[position]);
            if (!enemy.Creature.IsAlive)
                continue;
            if ((enemy.Flags & EnemyFlags.SkipNextMove) != 0)
                continue;
            // SimulatedCombatState.TryGetForcedMoveId: about-to-blow has no damage intent; explode
            // projects its recorded damage once, without the normal move's attacks.
            if (enemy.SteamPhase == SteamPhase.AboutToBlow)
                continue;
            if (enemy.SteamPhase == SteamPhase.Explode)
            {
                ProjectThreatHit(ref x, owner, enemy.Ai[Kernel.SteamEruptionDamageRegister],
                    ref ostyHp, ref block, ref hp, ref blockableDamage, ref gambitActive, ref prevention);
                continue;
            }
            MonsterNode move = Rules.MonsterRule(enemy.Rules).Nodes[enemy.Move];
            for (int attackIndex = 0; attackIndex < move.Attacks.Length; attackIndex++)
            {
                MonsterAttack attack = move.Attacks[attackIndex];
                MonsterDef def = Registry.Monster(enemy.MonsterId);
                int baseDamage = def.ModifyAttackDamage(probe, Rules, owner, enemy.Move, attackIndex, attack.BaseDamage);
                int repeats = Math.Max(
                    def.ModifyAttackRepeats(probe, Rules, owner, enemy.Move, attackIndex, attack.Repeats), 1);
                for (int hit = 0; hit < repeats; hit++)
                {
                    ProjectThreatHit(ref x, owner, baseDamage, ref ostyHp, ref block, ref hp,
                        ref blockableDamage, ref gambitActive, ref prevention);
                }
            }
        }

        // 5. The player's poison ticks at the end of the enemy turn, before the next player turn starts.
        if (hp > 0)
        {
            ProjectPowerSelfDamagePhase(ref x, selfDamagePowers[..selfDamagePowerCount],
                TurnEndSelfDamageTiming.EnemySideTurnEnd, ref ostyHp, ref block, ref hp,
                ref blockableDamage, ref gambitActive, ref prevention);
        }
        return BuildThreatProjection(hp, in prevention, originalBlock, block, blockableDamage);
    }

    private static ThreatProjection BuildThreatProjection(int hp, in ProjectedDeathPrevention prevention,
        int originalBlock, int block, long blockableDamage)
        => new(hp, prevention.DeathSaveHpRestored, prevention.UseCount,
            Math.Max(0, originalBlock - block), blockableDamage);

    /// <summary>Applies every declared power effect in one native phase, in power-table order.</summary>
    private void ProjectPowerSelfDamagePhase(ref Exec x, scoped ReadOnlySpan<ushort> powerIndices,
        TurnEndSelfDamageTiming timing, ref int ostyHp, ref int block, ref int hp,
        ref long blockableDamage, ref bool gambitActive, ref ProjectedDeathPrevention prevention)
    {
        Card noCard = default;
        for (int entry = 0; entry < powerIndices.Length; entry++)
        {
            int index = powerIndices[entry];
            if (hp <= 0 || (uint)index >= (uint)x.B.PowerCount)
                continue;
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Amount <= 0 || !x.B.Creature(power.Owner).IsAlive)
                continue;
            if (!Registry.Power(power.Id).TryGetTurnEndSelfDamage(x.B, Rules, index,
                    out TurnEndSelfDamage damage)
                || damage.Timing != timing)
            {
                continue;
            }
            ProjectSelfDamage(ref x, in damage, in noCard, ref ostyHp, ref block, ref hp,
                ref blockableDamage, ref gambitActive, ref prevention);
            if (hp <= 0)
                return;
        }
    }

    /// <summary>Applies one declared effect's hits through the same damage and HP-loss chain as attacks.</summary>
    private void ProjectSelfDamage(ref Exec x, in TurnEndSelfDamage damage, in Card cardSource,
        ref int ostyHp, ref int block, ref int hp, ref long blockableDamage, ref bool gambitActive,
        ref ProjectedDeathPrevention prevention)
    {
        if (hp <= 0)
            return;
        for (int hit = 0; hit < damage.HitCount; hit++)
        {
            decimal amount = damage.DecrementPerHit ? damage.Amount - hit : damage.Amount;
            if (amount <= 0m)
                return;
            ProjectDamageHit(ref x, damage.Props, damage.Dealer, amount, damage.HasCardSource,
                in cardSource, ref ostyHp, ref block, ref hp, ref blockableDamage, ref gambitActive,
                ref prevention);
            if (hp <= 0)
                return;
        }
    }

    /// <summary>A monster attack hit: powered move damage with no card source.</summary>
    private void ProjectThreatHit(ref Exec x, byte attacker, int baseDamage,
        ref int ostyHp, ref int block, ref int hp, ref long blockableDamage, ref bool gambitActive,
        ref ProjectedDeathPrevention prevention)
    {
        Card noCard = default;
        ProjectDamageHit(ref x, ValueProps.Move, attacker, baseDamage, hasCardSource: false,
            in noCard, ref ostyHp, ref block, ref hp, ref blockableDamage, ref gambitActive,
            ref prevention);
    }

    /// <summary>
    /// One incoming damage call through the native pipeline: ModifyDamage, block, the before/after-Osty
    /// HP-loss modifiers, the unblocked-damage redirect, Buffer consumption, TheGambit and death saves.
    /// </summary>
    private void ProjectDamageHit(ref Exec x, ValueProps props, byte dealer, decimal baseDamage,
        bool hasCardSource, in Card cardSource, ref int ostyHp, ref int block, ref int hp,
        ref long blockableDamage, ref bool gambitActive, ref ProjectedDeathPrevention prevention)
    {
        decimal modified = HookDispatch.ModifyDamage(
            ref x, Limits.PlayerOwner, dealer, baseDamage, props, hasCardSource, in cardSource);
        int adjustedHit = Math.Max(0, (int)Math.Floor(modified));
        bool blockable = (props & ValueProps.Unblockable) == 0;
        if (blockable)
            blockableDamage = checked(blockableDamage + adjustedHit);
        int blocked = blockable ? Math.Min(block, adjustedHit) : 0;
        block -= blocked;

        HpLossContext loss = new(
            Limits.PlayerOwner, props, dealer, hasCardSource, in cardSource);
        Span<Listener> changed = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R) * 2];
        decimal hpLoss = ProjectedModifyHpLost(
            ref x, loss, adjustedHit - blocked, afterOsty: false, changed, out _);
        // the existing search's ProjectThreatHit: Hook.ModifyUnblockedDamageTarget, then the engine's explicit
        // DieForYou fallback and dead-Osty guard, then the after-Osty modifiers on the chosen receiver.
        byte target = HookDispatch.ModifyUnblockedDamageTarget(
            ref x, Limits.PlayerOwner, hpLoss, props, dealer);
        if (target == Limits.PlayerOwner
            && props.IsPoweredAttack()
            && (x.B.Flags & BattleFlags.OstyPresent) != 0
            && ostyHp > 0
            && GetAmount(x.B, _score.DieForYouPowerId, Limits.OstyOwner) > 0)
        {
            target = Limits.OstyOwner;
        }
        if (target == Limits.OstyOwner && ostyHp <= 0)
            target = Limits.PlayerOwner;
        HpLossContext redirected = new(
            target, props, dealer, hasCardSource, in cardSource);
        hpLoss = ProjectedModifyHpLost(ref x, redirected, hpLoss, afterOsty: true, changed, out int changedCount);
        // The projected Buffer consumption is local to this forecast: notify the changed listeners so the
        // probe's Buffer amount drops, exactly as ProjectedHpLossModifiers.Consume does in the engine.
        HookDispatch.AfterModifyingHpLostAfterOsty(ref x, changed[..changedCount]);
        int damage = Math.Max(0, (int)Math.Floor(hpLoss));
        if (target == Limits.OstyOwner)
        {
            int absorbed = Math.Min(ostyHp, damage);
            ostyHp -= absorbed;
            // Native redirected damage applies the original receiver's AfterOsty modifiers to the
            // overkill as a separate loss; the engine also filters Buffer there.
            Span<Listener> overflowChanges = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R) * 2];
            HpLossContext overflow = new(
                Limits.PlayerOwner, props, dealer, hasCardSource, in cardSource);
            decimal overflowLoss = ProjectedModifyHpLost(
                ref x, overflow, damage - absorbed, afterOsty: true, overflowChanges, out int overflowCount);
            HookDispatch.AfterModifyingHpLostAfterOsty(ref x, overflowChanges[..overflowCount]);
            int playerLoss = Math.Max(0, (int)Math.Floor(overflowLoss));
            hp -= playerLoss;
            if (playerLoss > 0 && gambitActive && props.IsPoweredAttack())
            {
                gambitActive = false;
                hp = 0;
            }
            prevention.TryRevive(ref hp);
            return;
        }
        hp -= damage;
        if (damage > 0 && gambitActive && props.IsPoweredAttack())
        {
            gambitActive = false;
            hp = 0;
        }
        prevention.TryRevive(ref hp);
    }

    /// <summary>
    /// <see cref="HookDispatch.ModifyHpLost"/> with the engine's projected Buffer filter: a Buffer whose
    /// locally projected remaining count is exhausted no longer zeroes later hits.
    /// </summary>
    private decimal ProjectedModifyHpLost(ref Exec x, in HpLossContext loss, decimal amount,
        bool afterOsty, scoped Span<Listener> changed, out int changedCount)
    {
        changedCount = 0;
        HookMask normalMask = afterOsty ? HookMask.ModifyHpLostAfterOsty : HookMask.ModifyHpLostBeforeOsty;
        HookMask lateMask = afterOsty ? HookMask.ModifyHpLostAfterOstyLate : HookMask.ModifyHpLostBeforeOstyLate;
        if ((Registry.AnyHooks & (normalMask | lateMask)) == 0)
            return amount;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R)];
        for (int pass = 0; pass < 2; pass++)
        {
            int count = HookDispatch.CollectListeners(x.B, x.R, pass == 0 ? normalMask : lateMask, listeners);
            for (int index = 0; index < count; index++)
            {
                Listener listener = listeners[index];
                if (listener.Kind == ListenerKind.Power
                    && x.B.Powers[listener.Index].Id == _score.BufferPowerId
                    && x.B.Powers[listener.Index].Amount <= 0)
                {
                    continue;
                }
                ListenerDef def = HookDispatch.Definition(x.B, x.R, listener);
                decimal previous = amount;
                amount = (afterOsty, pass) switch
                {
                    (false, 0) => def.ModifyHpLostBeforeOsty(ref x, listener, loss, amount),
                    (false, _) => def.ModifyHpLostBeforeOstyLate(ref x, listener, loss, amount),
                    (true, 0) => def.ModifyHpLostAfterOsty(ref x, listener, loss, amount),
                    _ => def.ModifyHpLostAfterOstyLate(ref x, listener, loss, amount),
                };
                if (decimal.Truncate(previous) != decimal.Truncate(amount))
                    changed[changedCount++] = listener;
            }
        }
        return amount;
    }

    /// <summary>Port of <c>the existing search's BuildProjectedDeathPrevention</c>.</summary>
    private struct ProjectedDeathPrevention
    {
        private int _fairyCount;
        private readonly int _fairyHeal;
        private bool _lizardTailAvailable;
        private readonly int _lizardTailHeal;

        public ProjectedDeathPrevention(in Battle battle, ScoreRules score, Rules rules)
        {
            _fairyCount = 0;
            if (score.FairyInABottlePotionId != ushort.MaxValue)
            {
                for (int slot = 0; slot < battle.PotionSlotCount; slot++)
                {
                    if (battle.Potions[slot] == score.FairyInABottlePotionId + 1)
                        _fairyCount++;
                }
            }
            _fairyHeal = (int)Math.Max(1m, battle.Player.MaxHp * 0.3m);
            _lizardTailAvailable = false;
            _lizardTailHeal = 0;
            if (score.LizardTailRelicId != ushort.MaxValue)
            {
                for (int index = 0; index < rules.Relics.Count; index++)
                {
                    Relic relic = battle.GetRelic(rules, index);
                    if (relic.Id != score.LizardTailRelicId || relic.Counter != 0
                        || (relic.Flags & RelicFlags.Melted) != 0)
                    {
                        continue;
                    }
                    _lizardTailAvailable = true;
                    _lizardTailHeal = (int)Math.Max(1m,
                        battle.Player.MaxHp * (rules.RelicVar(relic.Id, 0) / 100m));
                    break;
                }
            }
            DeathSaveHpRestored = 0;
            UseCount = 0;
        }

        public int DeathSaveHpRestored { get; private set; }

        public int UseCount { get; private set; }

        public void TryRevive(ref int hp)
        {
            if (hp > 0)
                return;
            if (_fairyCount > 0)
            {
                _fairyCount--;
                hp = _fairyHeal;
                DeathSaveHpRestored += _fairyHeal;
                UseCount++;
                return;
            }
            if (!_lizardTailAvailable)
                return;
            _lizardTailAvailable = false;
            DeathSaveHpRestored += _lizardTailHeal;
            UseCount++;
            hp = _lizardTailHeal;
        }
    }

    private int MeasureDefensiveBlockReserve(in Battle battle, int blockConsumed)
    {
        int consumed = Math.Clamp(blockConsumed, 0, Math.Max(0, battle.Player.Block));
        int remaining = Math.Max(0, battle.Player.Block - consumed);
        if (remaining == 0)
            return consumed;
        // ShouldClearBlock and BlockAfterPreventingClear are pure queries in every registered definition
        // (Calipers only reads its relic var; Barricade/Blur/Hive read owner equality; no implementation
        // writes x.B), and the Exec constructor only stores fields. The query therefore runs on the
        // read-only state without a 12 KiB copy; this is the only place a Exec wraps a battle that is
        // still reachable through an `in` parameter, and it performs no write.
        ref Battle state = ref Unsafe.AsRef(in battle);
        Exec x = new(ref state, Rules, default);
        if (ShouldClearBlock(ref x, Limits.PlayerOwner, out Listener preventer))
            return consumed;
        int retained = HookDispatch.Definition(state, Rules, preventer)
            .BlockAfterPreventingClear(ref x, preventer, Limits.PlayerOwner, remaining);
        return consumed + Math.Min(Math.Max(0, retained), Math.Max(0, battle.Player.MaxHp));
    }

    private static bool ShouldClearBlock(ref Exec x, byte owner, out Listener preventer)
    {
        preventer = default;
        if ((Registry.AnyHooks & HookMask.ShouldClearBlock) == 0)
            return true;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R)];
        int count = HookDispatch.CollectListeners(x.B, x.R, HookMask.ShouldClearBlock, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!HookDispatch.Definition(x.B, x.R, listeners[index]).ShouldClearBlock(ref x, listeners[index], owner))
            {
                preventer = listeners[index];
                return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // CorePowerSupport.TheHuntLongTermResourceValue (30): an exhausted TheHunt that did not kill its
    // target loses the presumed card-reward resource.
    private const int TheHuntLongTermResourceValue = 30;

    // the existing search's StateEvaluation.OrbRetentionValue. The existing engine applies
    // Hook.ModifyOrbValue (Focus) through OrbMirrors.GetPassiveValue; Kernel.OrbPassiveValue is
    // the same pure query over the Sts2Solver listener list.
    private int OrbRetentionValue(in Battle battle, int aliveEnemyCount)
    {
        decimal value = 0m;
        for (int index = 0; index < battle.OrbCount; index++)
        {
            decimal passive = Math.Max(0m, Kernel.OrbPassiveValue(battle, Rules, index));
            value += passive * Registry.Orb(battle.Orbs[index].Id).RetentionMultiplier(battle, aliveEnemyCount);
        }
        return checked((int)Math.Ceiling(value));
    }

    // Threat focus (BuildThreatFocus).

    private readonly record struct ThreatFocus(
        uint? CombatId, int Pressure, int RemainingHp, int CurrentThreat, int TotalThreat, int IncomingHitCount);

    private ThreatFocus BuildThreatFocus(in Battle battle)
    {
        uint? bestCombatId = null;
        int bestPressure = 0;
        int bestRemainingHp = int.MaxValue;
        int bestThreat = 0;
        int totalThreat = 0;
        int incomingHitCount = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.InCombat) == 0 || !enemy.Creature.IsAlive)
                continue;
            byte owner = Kernel.EnemyOwner((byte)index);
            int remainingHp = Math.Max(0, enemy.Creature.CurrentHp);
            int maxHp = Math.Max(1, enemy.Creature.MaxHp);
            int currentThreat = 0;
            if ((enemy.Flags & EnemyFlags.SkipNextMove) == 0)
            {
                MonsterNode move = Rules.MonsterRule(enemy.Rules).Nodes[enemy.Move];
                for (int attackIndex = 0; attackIndex < move.Attacks.Length; attackIndex++)
                {
                    MonsterAttack attack = move.Attacks[attackIndex];
                    MonsterDef def = Registry.Monster(enemy.MonsterId);
                    int damage = def.ModifyAttackDamage(battle, Rules, owner, enemy.Move, attackIndex, attack.BaseDamage);
                    int repeats = Math.Max(
                        def.ModifyAttackRepeats(battle, Rules, owner, enemy.Move, attackIndex, attack.Repeats), 1);
                    for (int hit = 0; hit < repeats; hit++)
                    {
                        incomingHitCount++;
                        currentThreat += Math.Max(0, damage);
                    }
                }
            }
            totalThreat += currentThreat;
            int progress = Math.Max(0, maxHp - remainingHp);
            if (progress == 0)
                continue;
            int pressure = (int)Math.Min(
                int.MaxValue,
                (long)progress * (16 + Math.Min(64, currentThreat)) * 1024 / maxHp);
            if (pressure < bestPressure
                || pressure == bestPressure && currentThreat < bestThreat
                || pressure == bestPressure && currentThreat == bestThreat && remainingHp >= bestRemainingHp)
            {
                continue;
            }
            bestCombatId = enemy.CombatId;
            bestPressure = pressure;
            bestRemainingHp = remainingHp;
            bestThreat = currentThreat;
        }
        return new ThreatFocus(bestCombatId, bestPressure, bestRemainingHp, bestThreat, totalThreat, incomingHitCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Strategic effects (StrategicEffectModel).

    private readonly record struct StrategicEffectVector(
        int DamagePotential, int PreventionPotential, int ResourcePotential, int CardAccessPotential, int ScalingPotential)
    {
        public int RetentionValue => (int)Math.Clamp(
            (long)DamagePotential + PreventionPotential + ResourcePotential + CardAccessPotential + ScalingPotential,
            0L,
            int.MaxValue);

        public static StrategicEffectVector operator +(
            StrategicEffectVector left, StrategicEffectVector right)
            => new(
                SaturatingAdd(left.DamagePotential, right.DamagePotential),
                SaturatingAdd(left.PreventionPotential, right.PreventionPotential),
                SaturatingAdd(left.ResourcePotential, right.ResourcePotential),
                SaturatingAdd(left.CardAccessPotential, right.CardAccessPotential),
                SaturatingAdd(left.ScalingPotential, right.ScalingPotential));

        private static int SaturatingAdd(int left, int right)
            => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
    }

    private readonly record struct StrategicEffectContext(
        int EnemyHp, int IncomingDamage, int IncomingHitCount, int RemainingTurns, int UsefulCardPlays,
        int AttackPlays, int SkillPlays, int BlockSkillPlays, int PowerPlays, int ExhaustPlays, int ShivPlays,
        int DebuffApplications, int SkillEnergySpend, int PowerEnergySpend, int AverageCardValue,
        int BestCardValue, int AverageAttackValue, int StatusDrawTriggers)
    {
        public int? AttackHits { get; init; }
        public int? ExhaustDrawPlays { get; init; }
        public int ReachableCards { get; init; }

        /// <summary>The request's Act3 boss strategy, frozen in the score table.</summary>
        public bool Act3BossInteractions { get; init; }

        /// <summary><c>CaptureFirstAttackDamage</c>: the best payable attack's total first-hit damage.</summary>
        public int FirstAttackDamage { get; init; }

        /// <summary><c>CaptureAct3BossInteractionPotential</c> values (zero when not computed).</summary>
        public int EtherealDrawTriggers { get; init; }
        public int PagestormBonusDrawCapacity { get; init; }
        public int HighEnergyPlays { get; init; }
        public int DemesneEnergyGain { get; init; }
        public int DemesneDrawGain { get; init; }

        /// <summary>Automation/Orbit recurring energy from the refund window (StrategicEffectModel).</summary>
        public int RecurringEnergyGain { get; init; }
    }

    private static bool Contributes(in Power power, PowerScore rules)
        => power.Amount > 0
            && !rules.IsTemporary
            && rules.Type == PowerType.Buff;

    private StrategicEffectContext BuildStrategicContext(
        in Battle battle, int enemyHp, int incomingDamage, int incomingHitCount,
        StrategicRequirements requirements, bool skillsExhaust)
    {
        if (requirements == StrategicRequirements.None)
        {
            return new StrategicEffectContext(
                Math.Max(0, enemyHp), Math.Max(0, incomingDamage), Math.Max(0, incomingHitCount),
                1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0);
        }

        const StrategicRequirements reachablePlayRequirements =
            StrategicRequirements.UsefulCardPlays
            | StrategicRequirements.AttackPlays
            | StrategicRequirements.SkillPlays
            | StrategicRequirements.BlockSkillPlays
            | StrategicRequirements.PowerPlays
            | StrategicRequirements.ExhaustPlays
            | StrategicRequirements.ShivPlays
            | StrategicRequirements.DebuffApplications
            | StrategicRequirements.StatusDrawTriggers
            | StrategicRequirements.AttackHits;
        bool needsRemainingTurns = (requirements & StrategicRequirements.RemainingTurns) != 0
            || (requirements & reachablePlayRequirements) != 0;
        bool needsAllCardValues = (requirements & StrategicRequirements.AverageCardValue) != 0
            || (requirements & StrategicRequirements.BestCardValue) != 0;
        bool needsAttackValues = needsRemainingTurns
            || (requirements & StrategicRequirements.AverageAttackValue) != 0;
        bool needsAttackCount = (requirements & StrategicRequirements.AttackPlays) != 0
            || (requirements & StrategicRequirements.AverageAttackValue) != 0;
        bool needsSkillCount = (requirements & StrategicRequirements.SkillPlays) != 0;
        bool needsBlockSkillCount = (requirements & StrategicRequirements.BlockSkillPlays) != 0;
        bool needsPowerCount = (requirements & StrategicRequirements.PowerPlays) != 0;
        bool needsExhaustCount = (requirements & StrategicRequirements.ExhaustPlays) != 0;
        bool needsShivCount = (requirements & StrategicRequirements.ShivPlays) != 0
            || (requirements & StrategicRequirements.AttackHits) != 0;
        bool needsDebuffCount = (requirements & StrategicRequirements.DebuffApplications) != 0;
        bool needsStatusCount = (requirements & StrategicRequirements.StatusDrawTriggers) != 0;
        bool needsSkillEnergy = (requirements & StrategicRequirements.SkillEnergySpend) != 0;
        bool needsPowerEnergy = (requirements & StrategicRequirements.PowerEnergySpend) != 0;

        int attackCount = 0, attackHitCount = 0, skillCount = 0, blockSkillCount = 0;
        int powerCount = 0, exhaustCount = 0, shivCount = 0, reusableShivCount = 0;
        int generatedShivCount = 0, shivGeneratorCount = 0;
        int singleUseGeneratedShivs = 0, singleUseShivGenerators = 0;
        int debuffCount = 0, statusCount = 0, skillEnergy = 0, powerEnergy = 0;
        int totalCardValue = 0, bestCardValue = 0, totalAttackValue = 0;
        int liveCount = battle.DiscardCount + battle.DrawCount + battle.HandCount;
        for (int pile = 0; pile < 3; pile++)
        {
            Pile segment = pile switch
            {
                0 => Pile.Discard,
                1 => Pile.Draw,
                _ => Pile.Hand,
            };
            int start = Piles.Start(battle, segment);
            int count = Piles.Count(battle, segment);
            for (int offset = 0; offset < count; offset++)
            {
                ref readonly Card card = ref battle.Cards[start + offset];
                CardStats stats = Rules.Card(card.Id, card.Upgrade);
                CardScore cardScore = _score.Card(card.Id, card.Upgrade);
                CardType cardType = ResolveCardType(battle, card, stats);
                int cardValue = 0;
                if (needsAllCardValues || (needsAttackValues && cardType == CardType.Attack))
                {
                    cardValue = Math.Max(1, (int)Math.Ceiling(CardValue(battle, card, stats, cardScore)));
                    if (needsAllCardValues)
                    {
                        totalCardValue += cardValue;
                        bestCardValue = Math.Max(bestCardValue, cardValue);
                    }
                    if (needsAttackValues && cardType == CardType.Attack)
                        totalAttackValue += cardValue;
                }
                switch (cardType)
                {
                    case CardType.Attack:
                        if (needsAttackCount)
                            attackCount++;
                        if ((requirements & StrategicRequirements.AttackHits) != 0
                            && !cardScore.HasOstyAttackTag && !cardScore.HasShivTag)
                        {
                            attackHitCount += AttackHits(battle, cardScore, card, stats);
                        }
                        break;
                    case CardType.Skill:
                        if (needsSkillCount)
                            skillCount++;
                        if (needsSkillEnergy)
                            skillEnergy += EnergyCost(battle, card, stats, segment);
                        if (needsBlockSkillCount && cardScore.HasBlockDynamicVar)
                            blockSkillCount++;
                        break;
                    case CardType.Power:
                        if (needsPowerCount)
                            powerCount++;
                        if (needsPowerEnergy)
                            powerEnergy += EnergyCost(battle, card, stats, segment);
                        break;
                    case CardType.Status:
                        if (needsStatusCount)
                            statusCount++;
                        break;
                }
                bool hasExhaustKeyword = (card.Keywords & Keywords.Exhaust) != 0;
                bool exhaustsOnPlay = hasExhaustKeyword
                    || skillsExhaust && cardType == CardType.Skill;
                if (needsExhaustCount && exhaustsOnPlay)
                    exhaustCount++;
                if (needsShivCount)
                {
                    if (cardScore.HasShivTag)
                    {
                        shivCount++;
                        if (!hasExhaustKeyword)
                            reusableShivCount++;
                    }
                    int generated = ImmediateShivSupply(battle, cardScore, card, stats);
                    generatedShivCount += generated;
                    if (generated > 0)
                        shivGeneratorCount++;
                    if (generated > 0 && (cardType == CardType.Power
                        || skillsExhaust && cardType == CardType.Skill
                        || hasExhaustKeyword))
                    {
                        singleUseGeneratedShivs += generated;
                        singleUseShivGenerators++;
                    }
                }
                if (needsDebuffCount && cardScore.HasDebuffDynamicVar)
                    debuffCount++;
            }
        }

        int actualDeckSize = liveCount;
        int deckSize = Math.Max(1, actualDeckSize);
        int remainingTurns = 1;
        int reachableCards = 0;
        if (needsRemainingTurns)
        {
            int cardsPerTurn = Math.Min(10, Math.Max(1, Math.Min(5, deckSize)));
            int damagePerCycle = Math.Max(1, totalAttackValue);
            int estimatedCycles = (int)Math.Ceiling((double)Math.Max(1, enemyHp) / damagePerCycle);
            remainingTurns = Math.Clamp(
                estimatedCycles * Math.Max(1, (int)Math.Ceiling(deckSize / 5d)),
                1,
                ScoreWeights.SetupValueHorizonTurns);
            reachableCards = actualDeckSize == 0
                ? 0
                : Math.Min(deckSize * 2, remainingTurns * cardsPerTurn);
        }
        int attackPlays = (requirements & StrategicRequirements.AttackPlays) != 0
            ? ReachablePlays(attackCount, deckSize, reachableCards) : 0;
        int skillPlays = (requirements & StrategicRequirements.SkillPlays) != 0
            ? ReachablePlays(skillCount, deckSize, reachableCards) : 0;
        int blockSkillPlays = (requirements & StrategicRequirements.BlockSkillPlays) != 0
            ? ReachablePlays(blockSkillCount, deckSize, reachableCards) : 0;
        int powerPlays = (requirements & StrategicRequirements.PowerPlays) != 0
            ? Math.Min(powerCount, reachableCards) : 0;
        int exhaustPlays = (requirements & StrategicRequirements.ExhaustPlays) != 0
            ? Math.Min(exhaustCount, reachableCards) : 0;
        int shivPlays = (requirements & StrategicRequirements.ShivPlays) != 0
            ? EstimatedShivPlays(shivCount, generatedShivCount, shivGeneratorCount, deckSize, reachableCards,
                reusableShivCount, singleUseGeneratedShivs, singleUseShivGenerators)
            : 0;
        int debuffApplications = (requirements & StrategicRequirements.DebuffApplications) != 0
            ? ReachablePlays(debuffCount, deckSize, reachableCards) : 0;
        return new StrategicEffectContext(
            Math.Max(0, enemyHp),
            Math.Max(0, incomingDamage),
            Math.Max(0, incomingHitCount),
            remainingTurns,
            reachableCards,
            attackPlays,
            skillPlays,
            blockSkillPlays,
            powerPlays,
            exhaustPlays,
            shivPlays,
            debuffApplications,
            skillEnergy,
            powerEnergy,
            (requirements & StrategicRequirements.AverageCardValue) != 0
                ? Math.Max(1, totalCardValue / deckSize) : 1,
            (requirements & StrategicRequirements.BestCardValue) != 0
                ? Math.Max(1, bestCardValue) : 1,
            (requirements & StrategicRequirements.AverageAttackValue) != 0 && attackCount > 0
                ? Math.Max(1, totalAttackValue / attackCount) : 0,
            (requirements & StrategicRequirements.StatusDrawTriggers) != 0
                ? Math.Min(remainingTurns, ReachablePlays(statusCount, deckSize, reachableCards)) : 0)
        {
            AttackHits = (requirements & StrategicRequirements.AttackHits) != 0
                ? EstimatedAttackHits(attackHitCount, shivCount, generatedShivCount,
                    shivGeneratorCount, deckSize, reachableCards, reusableShivCount,
                    singleUseGeneratedShivs, singleUseShivGenerators)
                : null,
            ReachableCards = reachableCards,
        };
    }

    /// <summary>
    /// Port of <c>the existing search's FreeCardOpportunityValue</c> for FreeAttackPower: for each playable
    /// Attack in hand without X cost, the local cost minus the hook-modified cost times 16 plus a card
    /// value bonus; the best <c>freeUses</c> values are summed.
    /// </summary>
    private int FreeCardOpportunityValue(in Battle battle, CardType cardType, ushort powerId)
    {
        int freeUses = GetAmount(battle, powerId, Limits.PlayerOwner);
        if (freeUses <= 0)
            return 0;
        // Add-to-hand effects can put the stable hand above Limits.MaxHandSize; the hand itself is
        // the exact bound.
        Span<int> values = stackalloc int[battle.HandCount];
        int count = 0;
        for (int index = 0; index < battle.HandCount; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            if (ResolveCardType(battle, card, stats) != cardType
                || (card.Flags & CardFlags.CostsX) != 0
                || !Kernel.CanPlayCard(battle, Rules, index))
            {
                continue;
            }
            int normalCost = Math.Max(0, Kernel.EnergyCostWithLocalModifiers(battle, card));
            int currentCost = Math.Max(0, Kernel.EnergyCostWithModifiers(battle, Rules, card, Pile.Hand));
            int savedEnergy = Math.Max(0, normalCost - currentCost);
            double cardValue = CardValue(battle, card, stats, _score.Card(card.Id, card.Upgrade));
            values[count++] = savedEnergy * 16
                + Math.Min(16, Math.Max(1, (int)Math.Ceiling(cardValue)));
        }
        for (int index = 1; index < count; index++)
        {
            int value = values[index];
            int position = index;
            while (position > 0 && values[position - 1] < value)
            {
                values[position] = values[position - 1];
                position--;
            }
            values[position] = value;
        }
        int total = 0;
        for (int index = 0; index < Math.Min(freeUses, count); index++)
            total += values[index];
        return total;
    }

    /// <summary>
    /// Port of <c>the existing search's VoidFormOpportunityValue</c>: while VoidFormPower has free plays
    /// left, every playable hand card without X cost saves its local cost minus the hook-modified cost
    /// times 16 plus its star saving times 8 plus a card-value bonus; the best <c>freeUses</c> values
    /// are summed. <see cref="Power.Aux0"/> is the power's CardsPlayedThisTurn counter.
    /// </summary>
    private int VoidFormOpportunityValue(in Battle battle)
    {
        if (_score.VoidFormPowerId == ushort.MaxValue)
            return 0;
        int powerIndex = Kernel.FindPower(battle, Limits.PlayerOwner, _score.VoidFormPowerId);
        if (powerIndex < 0)
            return 0;
        int freeUses = battle.Powers[powerIndex].Amount - battle.Powers[powerIndex].Aux0;
        if (freeUses <= 0)
            return 0;
        // Add-to-hand effects can put the stable hand above Limits.MaxHandSize; the hand itself is
        // the exact bound.
        Span<int> values = stackalloc int[battle.HandCount];
        int count = 0;
        for (int hand = 0; hand < battle.HandCount; hand++)
        {
            ref readonly Card card = ref battle.Cards[hand];
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            if (stats.CostsX || stats.StarCostsX || !Kernel.CanPlayCard(battle, Rules, hand))
                continue;
            int normalEnergy = Math.Max(0, Kernel.EnergyCostWithLocalModifiers(battle, card));
            int currentEnergy = Math.Max(0, Kernel.EnergyCostWithModifiers(battle, Rules, card, Pile.Hand));
            int normalStars = Math.Max(0, stats.StarCost);
            int currentStars = Math.Max(0, Kernel.StarCostWithModifiers(battle, Rules, card, Pile.Hand));
            double cardValue = CardValue(battle, card, stats, _score.Card(card.Id, card.Upgrade));
            values[count++] = Math.Max(0, normalEnergy - currentEnergy) * 16
                + Math.Max(0, normalStars - currentStars) * 8
                + Math.Min(16, Math.Max(1, (int)Math.Ceiling(cardValue)));
        }
        for (int index = 1; index < count; index++)
        {
            int value = values[index];
            int position = index;
            while (position > 0 && values[position - 1] < value)
            {
                values[position] = values[position - 1];
                position--;
            }
            values[position] = value;
        }
        int total = 0;
        for (int index = 0; index < Math.Min(freeUses, count); index++)
            total += values[index];
        return total;
    }

    /// <summary>
    /// Port of <c>the existing search's CaptureEnergyRefundWindow</c> for Automation's recurring energy:
    /// the energy the remaining live cards can consume this cycle, and the capacity left after the
    /// current energy plus future turn resets. <c>liveCards</c> is every combat card in pile order.
    /// </summary>
    private (int Spend, int Capacity) CaptureEnergyRefundWindow(in Battle battle,
        StrategicEffectContext context, bool skillsExhaust, ref int cachedMaxEnergy)
    {
        // the existing search's CaptureEnergyRefundWindow's liveCards are Discard, Draw and Hand only;
        // the played and exhausted piles do not participate in the projected cycle.
        int liveCount = battle.HandCount + battle.DrawCount + battle.DiscardCount;
        if (liveCount == 0 || context.ReachableCards == 0)
            return (0, 0);
        // PersistentPowerSupport.GetModifiedMaxEnergy: WasteAway and other ModifyMaxEnergy listeners
        // reduce the future-turn energy supply. The evaluation never mutates the battle being scored.
        int maxEnergy = ModifiedMaxEnergy(battle, ref cachedMaxEnergy);
        int handCount = battle.HandCount;
        int futureTurns = Math.Min(Math.Max(0, context.RemainingTurns - 1),
            (Math.Max(0, context.ReachableCards - handCount) + 5 - 1) / 5);
        long energySupply = Math.Max(0, battle.Energy) + (long)maxEnergy * futureTurns;
        double visits = (double)context.ReachableCards / liveCount;
        double energyDemand = 0;
        for (int pile = 0; pile < 3; pile++)
        {
            Pile segment = pile switch
            {
                0 => Pile.Discard,
                1 => Pile.Draw,
                _ => Pile.Hand,
            };
            int start = Piles.Start(battle, segment);
            int count = Piles.Count(battle, segment);
            for (int offset = 0; offset < count; offset++)
            {
                ref readonly Card card = ref battle.Cards[start + offset];
                CardStats stats = Rules.Card(card.Id, card.Upgrade);
                if (card.HasKeyword(Keywords.Unplayable) || skillsExhaust && ResolveCardType(battle, card, stats) == CardType.Skill)
                    continue;
                int cost = (card.Flags & CardFlags.CostsX) != 0
                    ? maxEnergy
                    : Math.Max(0, Kernel.EnergyCostWithLocalModifiers(battle, card));
                bool singleUse = ResolveCardType(battle, card, stats) == CardType.Power || card.HasKeyword(Keywords.Exhaust);
                energyDemand += cost * (singleUse ? Math.Min(1, visits) : visits);
            }
        }
        long demand = (long)Math.Ceiling(energyDemand);
        return ((int)Math.Min(int.MaxValue, Math.Min(energySupply, demand)),
            (int)Math.Min(int.MaxValue, Math.Max(0, demand - energySupply)));
    }

    /// <summary>
    /// <c>PersistentPowerSupport.GetModifiedMaxEnergy</c>. A battle with no matching listener returns
    /// the same base value without a copy; <c>WasteAway</c> and other listeners lower the supply.
    /// </summary>
    private int ModifiedMaxEnergy(in Battle battle)
    {
        int cached = -1;
        return ModifiedMaxEnergy(battle, ref cached);
    }

    /// <summary>The first call in one score copies the battle and every later call reuses the result.</summary>
    private int ModifiedMaxEnergy(in Battle battle, ref int cached)
    {
        if (cached >= 0)
            return cached;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(battle, Rules)];
        if (HookDispatch.CollectListeners(battle, Rules, HookMask.ModifyMaxEnergy, listeners) == 0)
            return cached = Math.Max(0, (int)(decimal)Rules.BaseMaxEnergy);
        Unsafe.SkipInit(out Battle probe);
        Battle.CopyPacked(in battle, ref probe);
        Exec probeExec = new(ref probe, Rules, default);
        return cached = Math.Max(0, Kernel.ModifiedMaxEnergy(ref probeExec));
    }

    /// <summary>
    /// <c>PersistentPowerSupport.GetModifiedHandDraw</c> for the commitment evidence port. The base
    /// draw is the game's <c>CombatManager.baseHandDrawCount</c> (5), the same literal the existing
    /// capture uses.
    /// </summary>
    internal int ModifiedHandDrawForEvidence(in Battle battle) => ModifiedHandDraw(battle);

    /// <summary><c>PersistentPowerSupport.GetModifiedMaxEnergy</c> for the commitment evidence port.</summary>
    internal int ModifiedMaxEnergyForEvidence(in Battle battle) => ModifiedMaxEnergy(battle);

    /// <summary><c>CardChoiceSupport.CardValue</c> of a live card, with its current variables.</summary>
    internal double CardValueForEvidence(in Battle battle, in Card card)
    {
        CardStats stats = Rules.Card(card.Id, card.Upgrade);
        CardScore score = _score.Cards[card.Id]?[card.Upgrade]
            ?? throw new InvalidOperationException($"评分表缺少卡牌 {card.Id}+{card.Upgrade}。");
        return CardValue(battle, card, stats, score);
    }

    /// <summary>
    /// <see cref="BranchCardValue"/> for the commitment evidence port: attack values include the
    /// registered calculated-damage formulas, same as the retained-attack aggregate.
    /// </summary>
    internal double BranchCardValueForEvidence(in Battle battle, in Card card)
    {
        CardStats stats = Rules.Card(card.Id, card.Upgrade);
        CardScore score = _score.Cards[card.Id]?[card.Upgrade]
            ?? throw new InvalidOperationException($"评分表缺少卡牌 {card.Id}+{card.Upgrade}。");
        return BranchCardValue(battle, card, stats, score);
    }

    /// <summary>A named dynamic variable's current value for the commitment evidence port.</summary>
    internal int VarForEvidence(in Battle battle, in Card card, int index)
    {
        CardStats stats = Rules.Card(card.Id, card.Upgrade);
        CardScore score = CardScoreForEvidence(card.Id, card.Upgrade);
        return (int)Var(battle, card, stats, index);
    }

    /// <summary><c>CardMechanismFacts.AttackHits</c> for the commitment evidence port.</summary>
    internal int AttackHitsForEvidence(in Battle battle, in Card card)
    {
        CardStats stats = Rules.Card(card.Id, card.Upgrade);
        CardScore score = CardScoreForEvidence(card.Id, card.Upgrade);
        return AttackHits(battle, score, card, stats);
    }

    /// <summary><c>CardMechanismFacts.ImmediateShivSupply</c> for the commitment evidence port.</summary>
    internal int ImmediateShivSupplyForEvidence(in Battle battle, in Card card)
    {
        CardStats stats = Rules.Card(card.Id, card.Upgrade);
        CardScore score = CardScoreForEvidence(card.Id, card.Upgrade);
        return ImmediateShivSupply(battle, score, card, stats);
    }

    /// <summary>The compiled per-card score definition, for variable indexes and block/debuff flags.</summary>
    internal CardScore CardScoreForEvidence(ushort id, int upgrade)
        => _score.Cards[id]?[upgrade]
            ?? throw new InvalidOperationException($"评分表缺少卡牌 {id}+{upgrade}。");

    private int ModifiedHandDraw(in Battle battle)
    {
        int cached = -1;
        return ModifiedHandDraw(battle, ref cached);
    }

    /// <summary>The first call in one score copies the battle and every later call reuses the result.</summary>
    private int ModifiedHandDraw(in Battle battle, ref int cached)
    {
        if (cached >= 0)
            return cached;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(battle, Rules)];
        int count = HookDispatch.CollectListeners(battle, Rules, HookMask.ModifyHandDraw, listeners);
        if (count == 0)
            count = HookDispatch.CollectListeners(battle, Rules, HookMask.ModifyHandDrawLate, listeners);
        if (count == 0)
            return cached = Math.Max(0, (int)(decimal)5);
        Unsafe.SkipInit(out Battle probe);
        Battle.CopyPacked(in battle, ref probe);
        Exec probeExec = new(ref probe, Rules, default);
        return cached = Math.Max(0, Kernel.ModifiedHandDraw(ref probeExec, 5));
    }

    /// <summary>
    /// Port of <c>the existing search's CaptureFirstAttackDamage</c>: the best total damage of a payable
    /// Attack in the live piles, counting every <c>Eradicate</c> hit the available energy pays for.
    /// The energy ceiling is the current energy or the modified maximum, whichever is larger.
    /// </summary>
    private int CaptureFirstAttackDamage(in Battle battle, ref int cachedMaxEnergy)
    {
        int energy = Math.Max(battle.Energy, ModifiedMaxEnergy(battle, ref cachedMaxEnergy));
        int best = 0;
        for (int pile = 0; pile < 3; pile++)
        {
            Pile segment = pile switch
            {
                0 => Pile.Discard,
                1 => Pile.Draw,
                _ => Pile.Hand,
            };
            int start = Piles.Start(battle, segment);
            int count = Piles.Count(battle, segment);
            for (int offset = 0; offset < count; offset++)
            {
                ref readonly Card card = ref battle.Cards[start + offset];
                CardStats stats = Rules.Card(card.Id, card.Upgrade);
                if (ResolveCardType(battle, card, stats) != CardType.Attack || card.HasKeyword(Keywords.Unplayable))
                    continue;
                CardScore score = _score.Card(card.Id, card.Upgrade);
                if (score.HasOstyAttackTag)
                    continue;
                if (Kernel.EnergyCostWithModifiers(battle, Rules, card, segment) > energy)
                    continue;
                int hits = _score.EradicateCardId != ushort.MaxValue && card.Id == _score.EradicateCardId
                    ? Math.Max(0, energy)
                    : AttackHits(battle, score, card, stats);
                int damage = (int)Math.Floor(Var(battle, card, stats, score.DamageVar));
                best = Math.Max(best, damage * hits);
            }
        }
        return best;
    }

    /// <summary>
    /// Port of <c>the existing search's CaptureAct3BossInteractionPotential</c>: Pagestorm's future ethereal
    /// draw triggers and unused bonus-draw capacity, Danse Macabre's affordable high-energy plays, and
    /// Demesne's future energy and draw gains. Values stay zero when the matching power did not
    /// contribute or the projected cycle cannot realize the interaction.
    /// </summary>
    private (int EtherealDrawTriggers, int PagestormBonusDrawCapacity, int HighEnergyPlays,
        int DemesneEnergyGain, int DemesneDrawGain) CaptureAct3BossInteractionPotential(
            in Battle battle,
            ref int cachedMaxEnergy,
            ref int cachedHandDraw,
            int remainingTurns,
            int reachableCards,
            bool hasPagestorm,
            bool skillsExhaust,
            int demesneAmount,
            int danseMacabreEnergyThreshold)
    {
        int boundedTurns = Math.Max(1, remainingTurns);
        int boundedReachableCards = Math.Max(0, reachableCards);
        int liveCount = battle.HandCount + battle.DrawCount + battle.DiscardCount;
        int etherealDrawTriggers = 0;
        int pagestormBonusDrawCapacity = 0;
        if (hasPagestorm)
        {
            int futurePileCards = battle.DrawCount + battle.DiscardCount;
            int futureEtherealCards = 0;
            for (int pile = 0; pile < 2; pile++)
            {
                Pile segment = pile == 0 ? Pile.Draw : Pile.Discard;
                int start = Piles.Start(battle, segment);
                int count = Piles.Count(battle, segment);
                for (int offset = 0; offset < count; offset++)
                {
                    // the existing search's StateEvaluation uses `HasKeyword(State, Ethereal)` here too,
                    // so a Hexed card in the draw/discard pile is part of Pagestorm's projection.
                    if (Kernel.HasKeyword(battle, battle.Cards[start + offset], Keywords.Ethereal))
                        futureEtherealCards++;
                }
            }
            int drawPerTurn = ModifiedHandDraw(battle, ref cachedHandDraw);
            if (futurePileCards > 0 && futureEtherealCards > 0 && drawPerTurn > 0)
            {
                int reachableFutureDraws = (int)Math.Min(
                    (long)futurePileCards * 2,
                    boundedReachableCards);
                etherealDrawTriggers = (int)Math.Min(
                    (long)futureEtherealCards * 2,
                    ((long)futureEtherealCards * reachableFutureDraws
                        + futurePileCards - 1) / futurePileCards);
                if (!HasUnmeltedFiddle(battle))
                {
                    int maxHandSize = battle.MaxHandSize;
                    int retainedCards = 0;
                    for (int offset = 0; offset < battle.HandCount; offset++)
                    {
                        if (ShouldRetainThisTurn(battle.Cards[offset]))
                            retainedCards++;
                    }
                    int firstTurnSpace = Math.Max(0, maxHandSize - retainedCards);
                    int firstTurnBonusSpace = Math.Max(
                        0,
                        firstTurnSpace - Math.Min(firstTurnSpace, drawPerTurn));
                    int laterTurnBonusSpace = Math.Max(
                        0,
                        maxHandSize - Math.Min(maxHandSize, drawPerTurn));
                    pagestormBonusDrawCapacity = (int)Math.Min(
                        (long)futurePileCards * 2,
                        (long)firstTurnBonusSpace
                            + (long)Math.Max(0, boundedTurns - 1) * laterTurnBonusSpace);
                }
            }
        }

        int highEnergyPlays = 0;
        if (danseMacabreEnergyThreshold > 0 && liveCount > 0)
        {
            Span<int> highEnergyCosts = stackalloc int[liveCount * 2];
            int costCount = 0;
            int highEnergyCards = 0;
            for (int pile = 0; pile < 3; pile++)
            {
                Pile segment = pile switch
                {
                    0 => Pile.Discard,
                    1 => Pile.Draw,
                    _ => Pile.Hand,
                };
                int start = Piles.Start(battle, segment);
                int count = Piles.Count(battle, segment);
                for (int offset = 0; offset < count; offset++)
                {
                    ref readonly Card card = ref battle.Cards[start + offset];
                    CardStats stats = Rules.Card(card.Id, card.Upgrade);
                    if (card.HasKeyword(Keywords.Unplayable))
                        continue;
                    int cost = Kernel.EnergyCostWithModifiers(battle, Rules, card, segment);
                    if (cost < danseMacabreEnergyThreshold)
                        continue;
                    highEnergyCards++;
                    highEnergyCosts[costCount++] = cost;
                    bool singleUse = ResolveCardType(battle, card, stats) == CardType.Power
                        || card.HasKeyword(Keywords.Exhaust)
                        || skillsExhaust && ResolveCardType(battle, card, stats) == CardType.Skill;
                    if (!singleUse)
                        highEnergyCosts[costCount++] = cost;
                }
            }
            int reachableHighEnergyCards = (int)Math.Min(
                costCount,
                ((long)highEnergyCards * boundedReachableCards
                    + liveCount - 1) / liveCount);
            int futureMaxEnergy = ModifiedMaxEnergy(battle, ref cachedMaxEnergy);
            Span<int> turnEnergy = stackalloc int[boundedTurns];
            turnEnergy[0] = Math.Max(0, battle.Energy);
            turnEnergy[1..].Fill(Math.Max(0, futureMaxEnergy));
            highEnergyCosts[..costCount].Sort();
            for (int index = 0;
                 index < reachableHighEnergyCards;
                 index++)
            {
                for (int turn = 0; turn < turnEnergy.Length; turn++)
                {
                    if (turnEnergy[turn] < highEnergyCosts[index]) continue;
                    turnEnergy[turn] -= highEnergyCosts[index];
                    highEnergyPlays++;
                    break;
                }
            }
        }

        int demesneEnergyGain = 0, demesneDrawGain = 0;
        int futureTurns = Math.Max(0, boundedTurns - 1);
        if (demesneAmount > 0 && futureTurns > 0 && liveCount > 0)
        {
            long deckEnergyDemand = 0;
            for (int pile = 0; pile < 3; pile++)
            {
                Pile segment = pile switch
                {
                    0 => Pile.Discard,
                    1 => Pile.Draw,
                    _ => Pile.Hand,
                };
                int start = Piles.Start(battle, segment);
                int count = Piles.Count(battle, segment);
                for (int offset = 0; offset < count; offset++)
                {
                    ref readonly Card card = ref battle.Cards[start + offset];
                    if (card.HasKeyword(Keywords.Unplayable))
                        continue;
                    deckEnergyDemand += Math.Max(0,
                        Kernel.EnergyCostWithModifiers(battle, Rules, card, segment));
                }
            }
            long reachableEnergyDemand = (deckEnergyDemand * boundedReachableCards
                + liveCount - 1) / liveCount;
            int baseEnergy = Math.Max(0, ModifiedMaxEnergy(battle, ref cachedMaxEnergy) - demesneAmount);
            long baseEnergySupply = Math.Max(0, battle.Energy) + (long)baseEnergy * futureTurns;
            demesneEnergyGain = (int)Math.Clamp(reachableEnergyDemand - baseEnergySupply,
                0L, (long)demesneAmount * futureTurns);
            int baseDraw = Math.Max(0, ModifiedHandDraw(battle, ref cachedHandDraw) - demesneAmount);
            int retained = 0;
            for (int offset = 0; offset < battle.HandCount; offset++)
            {
                if (ShouldRetainThisTurn(battle.Cards[offset]))
                    retained++;
            }
            int firstSpace = Math.Max(0, battle.MaxHandSize - retained - baseDraw);
            int laterSpace = Math.Max(0, battle.MaxHandSize - baseDraw);
            demesneDrawGain = (int)Math.Min((long)liveCount * 2,
                Math.Min(demesneAmount, firstSpace)
                + (long)Math.Max(0, futureTurns - 1) * Math.Min(demesneAmount, laterSpace));
        }
        return (etherealDrawTriggers, pagestormBonusDrawCapacity, highEnergyPlays,
            demesneEnergyGain, demesneDrawGain);
    }

    private static bool ShouldRetainThisTurn(in Card card)
        => (card.Flags & CardFlags.RetainThisTurn) != 0
            || (card.Keywords & Keywords.Retain) != 0;

    /// <summary><c>combat.RelicsOf(player).Any(relic =&gt; relic is Fiddle &amp;&amp; !relic.IsMelted)</c>.</summary>
    private bool HasUnmeltedFiddle(in Battle battle)
    {
        if (_score.FiddleRelicId == ushort.MaxValue)
            return false;
        for (int index = 0; index < Rules.Relics.Count; index++)
        {
            Relic relic = battle.GetRelic(Rules, index);
            if (relic.Id == _score.FiddleRelicId
                && (relic.Flags & RelicFlags.Melted) == 0)
            {
                return true;
            }
        }
        return false;
    }

    private int EnergyCost(in Battle battle, in Card card, CardStats stats, Pile pile)
    {
        if ((card.Flags & CardFlags.CostsX) != 0)
            return 0;
        // StrategicEffectModel.EnergyCost reads CostModifiers.All (local only), not the combat hooks.
        return Math.Max(0, Kernel.EnergyCostWithLocalModifiers(battle, card));
    }

    private static int ReachablePlays(int matchingCards, int deckSize, int reachableCards)
        => matchingCards == 0
            ? 0
            : Math.Max(1, (int)Math.Ceiling((double)matchingCards * reachableCards / deckSize));

    private static int AttackHits(in Battle battle, CardScore score, in Card card,
        CardStats stats)
        => score.AttackHits switch
        {
            AttackHitKind.Two => 2,
            AttackHitKind.RepeatVar => Math.Max(0, (int)Var(battle, card, stats, score.RepeatVar)),
            _ => 1,
        };

    private static int ImmediateShivSupply(in Battle battle, CardScore score, in Card card,
        CardStats stats)
        => score.ShivSupply switch
        {
            ShivSupplyKind.CardsVar => Math.Max(0, (int)Var(battle, card, stats, score.ShivCardsVar)),
            ShivSupplyKind.ShivsVar => Math.Max(0, (int)Var(battle, card, stats, score.ShivShivsVar)),
            _ => 0,
        };

    private StrategicEffectContext WithExhaustDrawTiming(
        StrategicEffectContext context, in Battle battle, bool skillsExhaust)
    {
        int noDrawIndex = -1, embraceIndex = -1;
        int visit = 0;
        bool corruption = false;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != Limits.PlayerOwner)
                continue;
            PowerScore rules = _score.Power(power.Id);
            if (power.Id == _score.NoDrawPowerId)
                noDrawIndex = visit;
            if (rules.Kind == StrategicKind.DarkEmbrace)
                embraceIndex = visit;
            if (rules.Kind == StrategicKind.Corruption)
                corruption = true;
            visit++;
        }
        if (embraceIndex < 0)
            return context;
        int ethereal = 0, exhaustingEthereal = 0;
        for (int offset = 0; offset < battle.HandCount; offset++)
        {
            ref readonly Card card = ref battle.Cards[offset];
            if ((card.Keywords & Keywords.Ethereal) == 0)
                continue;
            ethereal++;
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            if ((card.Keywords & Keywords.Exhaust) != 0
                || (corruption || skillsExhaust) && ResolveCardType(battle, card, stats) == CardType.Skill)
            {
                exhaustingEthereal++;
            }
        }
        return context with
        {
            ExhaustDrawPlays = ExhaustDrawPotential(
                Math.Max(0, context.ExhaustPlays - exhaustingEthereal),
                context.RemainingTurns,
                noDrawIndex >= 0,
                ethereal,
                noDrawIndex < 0 || noDrawIndex < embraceIndex),
        };
    }

    private static int ExhaustDrawPotential(int ordinaryExhausts, int remainingTurns, bool noDrawNow,
        int etherealInHand, bool drawsAtTurnEnd)
    {
        int ordinary = noDrawNow ? (int)Math.Ceiling((double)ordinaryExhausts
            * Math.Max(0, remainingTurns - 1) / Math.Max(1, remainingTurns)) : ordinaryExhausts;
        return ordinary + (drawsAtTurnEnd ? etherealInHand : 0);
    }

    private static int EstimatedShivPlays(int existing, int generated, int generators, int deckSize, int actions,
        int reusableExisting, int singleUseGenerated, int singleUseGenerators)
    {
        if (actions <= 0 || deckSize <= 0) return 0;
        int cycleSize = deckSize + generated;
        int setupActions = ShivSetupActions(generators, cycleSize, actions, singleUseGenerators);
        int reusable = Math.Clamp(reusableExisting, 0, existing);
        int oneShot = existing - reusable;
        int existingPlays = Math.Min(oneShot, (int)Math.Ceiling((double)oneShot * actions / cycleSize))
            + (int)Math.Ceiling((double)reusable * actions / cycleSize);
        int oneShotGenerated = Math.Clamp(singleUseGenerated, 0, generated);
        int generatedPlays = Math.Min(oneShotGenerated, (int)Math.Ceiling((double)oneShotGenerated * actions / cycleSize))
            + (int)Math.Ceiling((double)(generated - oneShotGenerated) * actions / cycleSize);
        return Math.Min(actions - setupActions, existingPlays + generatedPlays);
    }

    private static int EstimatedAttackHits(int ordinaryHits, int existingShivs, int generatedShivs,
        int generators, int deckSize, int actions, int reusableShivs,
        int singleUseGenerated, int singleUseGenerators)
    {
        if (actions <= 0 || deckSize <= 0) return 0;
        int shivPlays = EstimatedShivPlays(existingShivs, generatedShivs, generators, deckSize, actions,
            reusableShivs, singleUseGenerated, singleUseGenerators);
        int setup = ShivSetupActions(generators, deckSize + generatedShivs, actions, singleUseGenerators);
        int remainingActions = Math.Max(0, actions - shivPlays - setup);
        int ordinaryDeckSize = Math.Max(1, deckSize - existingShivs - generators);
        return shivPlays + (int)Math.Ceiling((double)ordinaryHits * remainingActions / ordinaryDeckSize);
    }

    private static int ShivSetupActions(int generators, int cycleSize, int actions, int singleUseGenerators)
    {
        int oneShot = Math.Clamp(singleUseGenerators, 0, generators);
        return Math.Min(actions, Math.Min(oneShot, (int)Math.Ceiling((double)oneShot * actions / cycleSize))
            + (int)Math.Ceiling((double)(generators - oneShot) * actions / cycleSize));
    }

    private StrategicEffectVector EvaluateStrategic(
        StrategicKind kind, int amountRaw, in StrategicEffectContext context)
    {
        int amount = Math.Max(1, amountRaw);
        int enemyHp = context.EnemyHp;
        int energyUnit = Math.Max(3, context.AverageCardValue);
        int cardAccessUnit = context.AverageCardValue;
        return kind switch
        {
            StrategicKind.Afterimage => Prevention(amount * context.UsefulCardPlays, context),
            StrategicKind.Buffer => Prevention(BufferPrevention(amount, context), context),
            StrategicKind.FeelNoPain => Prevention(amount * context.ExhaustPlays, context),
            StrategicKind.DarkEmbrace => CardAccess(
                amount * (context.ExhaustDrawPlays ?? context.ExhaustPlays) * cardAccessUnit),
            StrategicKind.Thorns => Damage(amount * context.IncomingHitCount, enemyHp),
            StrategicKind.EchoForm when context.UsefulCardPlays > 0 => Damage(
                context.BestCardValue
                    * Math.Min(amount, Math.Max(1, context.UsefulCardPlays))
                    * context.RemainingTurns,
                enemyHp),
            StrategicKind.EchoForm => default,
            StrategicKind.Envenom => Damage(amount * context.AttackPlays, enemyHp),
            StrategicKind.Accuracy => Damage(amount * context.ShivPlays, enemyHp),
            StrategicKind.SleightOfFlesh => Damage(amount * context.DebuffApplications, enemyHp),
            StrategicKind.Strength => Damage(
                amount * (context.AttackHits ?? context.AttackPlays), enemyHp),
            StrategicKind.Lethality when context.Act3BossInteractions && context.AttackPlays > 0
                => Damage(
                    context.FirstAttackDamage * Math.Min(context.AttackPlays, context.RemainingTurns)
                        * amount / 100,
                    enemyHp),
            StrategicKind.Lethality when context.AttackPlays > 0 => Damage(
                context.AverageAttackValue
                    * Math.Min(context.AttackPlays, context.RemainingTurns)
                    * amount / 100,
                enemyHp),
            StrategicKind.ReaperForm when context.AttackPlays > 0 => Damage(
                context.AverageAttackValue * context.AttackPlays * amount, enemyHp),
            StrategicKind.Lethality or StrategicKind.ReaperForm => default,
            StrategicKind.Dexterity => Prevention(amount * context.BlockSkillPlays, context),
            StrategicKind.DemonForm when context.AttackPlays > 0 => Damage(
                amount * Math.Max(1, context.AttackPlays / Math.Max(1, context.RemainingTurns))
                    * context.RemainingTurns * (context.RemainingTurns + 1) / 2,
                enemyHp),
            StrategicKind.DemonForm => default,
            StrategicKind.Curious => Resource(
                Math.Min(context.PowerEnergySpend, amount * context.PowerPlays) * energyUnit),
            StrategicKind.Corruption => Resource(context.SkillEnergySpend * energyUnit),
            StrategicKind.RecurringEnergy or StrategicKind.OrbitEnergy
                => Resource(context.RecurringEnergyGain * energyUnit),
            StrategicKind.CreativeAi => CardAccess(
                amount * context.RemainingTurns * cardAccessUnit),
            StrategicKind.Iteration => CardAccess(
                amount * context.StatusDrawTriggers * cardAccessUnit),
            StrategicKind.MasterPlanner => CardAccess(context.SkillPlays * cardAccessUnit),
            StrategicKind.Pagestorm when context.Act3BossInteractions => CardAccess(
                Math.Min(
                    context.PagestormBonusDrawCapacity,
                    amount * context.EtherealDrawTriggers) * cardAccessUnit),
            StrategicKind.DanseMacabre when context.Act3BossInteractions => Prevention(
                amount * context.HighEnergyPlays,
                context),
            StrategicKind.Demesne when context.Act3BossInteractions =>
                Resource(context.DemesneEnergyGain * energyUnit)
                + CardAccess(context.DemesneDrawGain * cardAccessUnit),
            StrategicKind.PrepTime when context.Act3BossInteractions => Damage(
                amount * Math.Min(context.RemainingTurns, context.AttackPlays), enemyHp),
            StrategicKind.Focus => Scaling(amount * context.RemainingTurns * 2),
            StrategicKind.Furnace => Scaling(amount * context.RemainingTurns * 2),
            StrategicKind.Thunder => Damage(amount * context.RemainingTurns, enemyHp),
            StrategicKind.LightningRod => Scaling(amount * context.RemainingTurns),
            _ => Scaling(amount),
        };
    }

    private static StrategicEffectVector Damage(int value, int enemyHp)
        => new(Math.Min(Math.Max(0, enemyHp), Math.Max(0, value)), 0, 0, 0, 0);

    private static StrategicEffectVector Prevention(int value, in StrategicEffectContext context)
    {
        int cap = context.IncomingDamage == 0
            ? value
            : context.IncomingDamage * Math.Min(2, context.RemainingTurns);
        return new(0, Math.Min(Math.Max(0, cap), Math.Max(0, value)), 0, 0, 0);
    }

    private static int BufferPrevention(int amount, in StrategicEffectContext context)
    {
        if (context.IncomingDamage == 0)
            return amount * 8;
        int averageHit = Math.Max(
            1,
            context.IncomingDamage / Math.Max(1, context.IncomingHitCount));
        return Math.Min(context.IncomingDamage, amount * averageHit);
    }

    private static StrategicEffectVector Resource(int value) => new(0, 0, Math.Max(0, value), 0, 0);

    private static StrategicEffectVector CardAccess(int value) => new(0, 0, 0, Math.Max(0, value), 0);

    private static StrategicEffectVector Scaling(int value) => new(0, 0, 0, 0, Math.Max(0, value));

    // ---------------------------------------------------------------------------------------------
    // Intermediate beam ranking (BeamRetentionPolicy.BeamRankScore).

    public double BeamRankScore(in Battle battle, in ScoreFeatures features,
        bool terminal, bool isEndTurn)
    {
        // Setup is an alternative ranking only. Actual terminal scores and route quality stay intact.
        if (SetupPreference && terminal)
            return features.Score;
        // the existing search's BeamRankScore: the base-score member ranks by the base score alone (the
        // development strategy that could adjust it is rejected at admission).
        if (_baseOnly)
            return features.Score;
        int persistentBuffCap = _score.IsActEndingBoss
            ? ScoreWeights.PersistentBuffDeltaBeamCap
            : ScoreWeights.StandardPersistentBuffDeltaBeamCap;
        double persistentBuffValue = _score.IsActEndingBoss
            ? ScoreWeights.PersistentBuffDeltaBeamValue
            : ScoreWeights.StandardPersistentBuffDeltaBeamValue;
        bool useLatentSetup = _score.IsActEndingBoss || _score.InitialEnemyCount > 1;
        int strengthSuppressionHorizon = _score.IsActEndingBoss
            ? ScoreWeights.BossEnemyStrengthSuppressionHorizon
            : ScoreWeights.StandardEnemyStrengthSuppressionHorizon;
        int weakExpectedHpSaved = _score.IsActEndingBoss
            ? ScoreWeights.BossEnemyWeakExpectedHpSaved
            : ScoreWeights.StandardEnemyWeakExpectedHpSaved;
        double score = features.Score
            + Math.Min(ScoreWeights.CurrentEnergyBeamCap, features.Energy)
                * ScoreWeights.CurrentEnergyBeamValue
            + Math.Min(
                    persistentBuffCap,
                    Math.Max(0, features.PersistentBuffValue - _score.InitialPersistentBuffValue))
                * persistentBuffValue
            + (useLatentSetup
                ? Math.Min(ScoreWeights.LatentSetupBeamCap, features.LatentSetupValue)
                    * ScoreWeights.LatentSetupBeamValue
                : 0d)
            + (_score.IsActEndingBoss
                ? features.FutureResourceValue * ScoreWeights.FutureResourceBeamValue
                : 0d)
            + Math.Min(
                    ScoreWeights.ReplayPotentialBeamCap,
                    features.ReplayPotentialValue)
                * ScoreWeights.ReplayPotentialBeamValue
            + RetainedAttackGrowth(features) * ScoreWeights.RetainedAttackGrowthBeamValue
            + features.DelayedDamageValue * ScoreWeights.DelayedDamageBeamValue
            + features.SandpitRemaining * ScoreWeights.SandpitTurnBeamValue
            + Math.Min(
                    ScoreWeights.EnemyStrengthSuppressionBeamCap,
                    Math.Max(0, features.EnemyStrengthSuppression - _score.InitialEnemyStrengthSuppression))
                * strengthSuppressionHorizon
                * ScoreWeights.Hp
            + Math.Min(
                    ScoreWeights.EnemyWeakTurnsBeamCap,
                    Math.Max(0, features.EnemyWeakTurns - _score.InitialEnemyWeakTurns))
                * weakExpectedHpSaved
                * ScoreWeights.Hp;
        if (_offensive && !terminal)
            score -= features.EnemyHp * 5_000d;
        return score;
    }

    private int RetainedAttackGrowth(in ScoreFeatures features)
        => Math.Min(
            ScoreWeights.RetainedAttackGrowthBeamCap,
            Math.Max(0, features.RetainedAttackValue - _score.InitialRetainedAttackValue));
}

/// <summary>The potion uses <see cref="Scorer.MeasurePotions"/> reads from a state.</summary>
internal readonly record struct PotionUsage(int UseCount, int StrategicCost, int ExplicitStrategicCost,
    int AutomaticUseCount, int DearestExplicitStrategicCost);
