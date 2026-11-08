using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;

namespace Sts2Solver.Ai.Combat.Scoring;

/// <summary>
/// The evaluation values Beam ranks by, computed from a <see cref="Battle"/>
/// by <see cref="Scorer"/>. Every member has the name and meaning of the
/// <see cref="SimulationSnapshot"/> member it mirrors (<c>the existing search's Snapshot</c>), and the K4
/// check (docs/archive/admission-and-verification.md §3) requires each one to equal the snapshot
/// of the same state. <see cref="Score"/> is compared as an exact double.
/// </summary>
internal readonly record struct ScoreFeatures
{
    // Snapshot score and its terms.
    public double Score { get; init; }
    public bool PlayerDead { get; init; }
    public bool AllEnemiesDead { get; init; }
    public int PlayerHp { get; init; }
    public int PlayerMaxHp { get; init; }
    public int PlayerBlock { get; init; }
    public int OstyHp { get; init; }
    public int OstyMaxHp { get; init; }
    public int CumulativePlayerHpLost { get; init; }
    public int RecoveredPlayerHp { get; init; }
    public int DeathSaveHpRestored { get; init; }
    public int DeathSaveUseCount { get; init; }
    public int ProjectedDeathSaveUseCount { get; init; }
    public int ProjectedPlayerHp { get; init; }
    public int DefensiveBlockValue { get; init; }
    public int LongTermResourceValue { get; init; }
    public int GrowthHpCredit { get; init; }

    /// <summary><c>Snapshot.GrowthRewards</c>: the per-source realized growth rewards.</summary>
    public GrowthValues GrowthRewards { get; init; }

    /// <summary><c>Snapshot.GrowthRewards.Total</c>.</summary>
    public int GrowthRewardCount { get; init; }

    /// <summary>HP credit of the relic counter goals a won state meets.</summary>
    public int RelicGoalHpCredit { get; init; }

    /// <summary>Relic counter goals a won state meets.</summary>
    public int RelicGoalCount { get; init; }

    /// <summary><c>Snapshot.StrategicHpCredit</c>: the realized growth and relic goal HP credit.</summary>
    public int StrategicHpCredit { get; init; }

    /// <summary>The credit route ranking breaks HP ties with: growth rewards plus relic goals.</summary>
    public int StrategyGoalHpCredit => GrowthHpCredit + RelicGoalHpCredit;

    /// <summary>The goal count route ranking breaks credit ties with.</summary>
    public int StrategyGoalCount => GrowthRewardCount + RelicGoalCount;
    public int AngerCopiesGenerated { get; init; }
    public int EnemyHp { get; init; }
    public int EnemyBlock { get; init; }
    public int RawEnemyHp { get; init; }
    public int MaxCurrentEnemyHp { get; init; }
    public int AliveEnemyCount { get; init; }
    public ulong AliveEnemyMask { get; init; }
    public int RevivingEnemyCount { get; init; }
    public int LiveDeckClutter { get; init; }
    public int LiveDeckSize { get; init; }
    public int OutstandingStolenResource { get; init; }

    /// <summary>
    /// <c>SimulatedCombatState.MinimumOutstandingStolenResource</c>: the part of the outstanding loot
    /// that is already irrecoverable. The existing engine reads it only for the primary incumbent's
    /// loot bucket; K4 compares it on every matched state.
    /// </summary>
    public int MinimumOutstandingStolenResource { get; init; }
    public int RetainedAttackValue { get; init; }
    public uint? FocusTargetCombatId { get; init; }
    public int FocusTargetPressure { get; init; }
    public int FocusTargetRemainingHp { get; init; }
    public int FocusTargetCurrentThreat { get; init; }
    public int FocusTargetVulnerableTurns { get; init; }
    public uint? MostVulnerableTargetCombatId { get; init; }
    public int EnemyVulnerableTurns { get; init; }

    // BeamRankScore terms.
    public int Energy { get; init; }
    public int Stars { get; init; }
    public int HandCount { get; init; }

    /// <summary><c>SearchNode.Turn</c> (the snapshot's combat turn, equal to the Sts2Solver player turn).</summary>
    public int Turn { get; init; }
    public int ReachableHandValue { get; init; }
    public int ZeroCostPlayableCount { get; init; }

    /// <summary><c>Snapshot.PlayableAttackCount</c>: playable attack cards in the current hand.</summary>
    public int PlayableAttackCount { get; init; }
    public int PersistentBuffValue { get; init; }
    public int StrategicRetentionValue { get; init; }

    public double SetupPotentialHp { get; init; }

    /// <summary><c>Snapshot.StrategicEffects.DamagePotential</c>.</summary>
    public int StrategicDamagePotential { get; init; }

    /// <summary><c>Snapshot.StrategicEffects.PreventionPotential</c>.</summary>
    public int StrategicPreventionPotential { get; init; }

    /// <summary><c>Snapshot.StrategicEffects.ResourcePotential</c>.</summary>
    public int StrategicResourcePotential { get; init; }

    /// <summary><c>Snapshot.StrategicEffects.CardAccessPotential</c>.</summary>
    public int StrategicCardAccessPotential { get; init; }

    /// <summary><c>Snapshot.StrategicEffects.ScalingPotential</c>.</summary>
    public int StrategicScalingPotential { get; init; }

    /// <summary><c>Snapshot.HasRisk</c> (<c>CombatPredictionSimulator.HasRisk</c>).</summary>
    public bool HasRisk { get; init; }

    /// <summary>
    /// <c>StrategicEffectContext.Act3BossInteractions</c>: the frozen three-act boss policy reached a
    /// contributing strategic power. Diagnostic only; its effect is already in the score terms.
    /// </summary>
    public bool Act3BossInteractions { get; init; }

    /// <summary><c>StrategicEffectContext.FirstAttackDamage</c>, read by Lethality in the boss branch.</summary>
    public int FirstAttackDamage { get; init; }

    /// <summary><c>StrategicEffectContext.EtherealDrawTriggers</c> (Pagestorm).</summary>
    public int EtherealDrawTriggers { get; init; }

    /// <summary><c>StrategicEffectContext.PagestormBonusDrawCapacity</c>.</summary>
    public int PagestormBonusDrawCapacity { get; init; }

    /// <summary><c>StrategicEffectContext.HighEnergyPlays</c> (Danse Macabre).</summary>
    public int HighEnergyPlays { get; init; }

    /// <summary><c>StrategicEffectContext.DemesneEnergyGain</c>.</summary>
    public int DemesneEnergyGain { get; init; }

    /// <summary><c>StrategicEffectContext.DemesneDrawGain</c>.</summary>
    public int DemesneDrawGain { get; init; }
    public PersistentSetupTraits PersistentSetupTraits { get; init; }
    public PersistentSetupTraits StrategicSetupTraits { get; init; }
    public int LatentSetupValue { get; init; }
    public int FutureResourceValue { get; init; }
    public int ReplayPotentialValue { get; init; }
    public int DelayedDamageValue { get; init; }
    public int ReactiveDamageValue { get; init; }
    public int OffensiveProgressValue { get; init; }
    public int SandpitRemaining { get; init; }
    public int EnemyStrengthSuppression { get; init; }
    public int EnemyWeakTurns { get; init; }

    // Potion accounting (final ordering and Pareto dimensions).
    public int PotionUseCount { get; init; }
    public int PotionStrategicCost { get; init; }
    public int AutomaticPotionUseCount { get; init; }
    public int ExplicitPotionStrategicCost { get; init; }
    public int ExplicitPotionUseCount { get; init; }

    /// <summary>
    /// <c>Snapshot.ProjectedShuffleOrderValue</c>: the value of the live pile after the stable shuffle
    /// projection (base card value weighted by the projected position). Retention-only; K4 compares it
    /// on every projected state.
    /// </summary>
    public int ProjectedShuffleOrderValue { get; init; }

    /// <summary>
    /// Sts2Solver-side projected-shuffle key. The existing key hashes the existing engine's per-card state
    /// fingerprint; Sts2Solver hashes the complete <see cref="Card"/> byte image instead. It partitions
    /// the exact projected card sequence the same way (equal sequence of equal card states), which is
    /// what the ordered-pile cohorts consume; the numeric hash is not comparable across engines.
    /// </summary>
    public ulong ProjectedShuffleOrderKeyFirst { get; init; }
    public ulong ProjectedShuffleOrderKeySecond { get; init; }

    /// <summary><c>SimulatedCombatState.CanTriggerArtOfWarNextTurn</c>: an unmelted Art of War relic
    /// whose current-turn attack flag is still zero.</summary>
    public bool CanTriggerArtOfWarNextTurn { get; init; }

    /// <summary>
    /// <c>Snapshot.EnemyCombatDistributionKey</c>: per known enemy (CombatId, current HP, block,
    /// effective HP, containment) in <see cref="Battle.Enemies"/> order. K4 compares both halves.
    /// </summary>
    public ulong EnemyCombatDistributionFirst { get; init; }
    public ulong EnemyCombatDistributionSecond { get; init; }

    /// <summary>
    /// <c>Snapshot.EnemyControlDistributionKey</c>: per living, contained enemy (CombatId, strength
    /// suppression, weak turns, vulnerable turns). K4 compares both halves.
    /// </summary>
    public ulong EnemyControlDistributionFirst { get; init; }
    public ulong EnemyControlDistributionSecond { get; init; }

    /// <summary>
    /// Sts2Solver-side unordered pile key: the order-invariant multiset hash over hand, draw, discard and
    /// exhaust. The existing key sums the existing per-card state fingerprints; the numeric value is
    /// therefore not comparable, but the equivalence class (same multiset of equal card states) is.
    /// </summary>
    public ulong UnorderedPileKeyFirst { get; init; }
    public ulong UnorderedPileKeySecond { get; init; }

    /// <summary>One line per member that differs, as <c>name expected=… actual=…</c>.</summary>
    public static List<string> Diff(in ScoreFeatures expected, in ScoreFeatures actual)
    {
        List<string> lines = [];
        void Check<T>(string name, T left, T right)
        {
            if (!EqualityComparer<T>.Default.Equals(left, right))
                lines.Add($"{name} expected={left} actual={right}");
        }
        Check(nameof(Score), BitConverter.DoubleToInt64Bits(expected.Score), BitConverter.DoubleToInt64Bits(actual.Score));
        if (lines.Count > 0)
            lines[0] = $"{nameof(Score)} expected={expected.Score:R} actual={actual.Score:R} delta={actual.Score - expected.Score:R}";
        Check(nameof(PlayerDead), expected.PlayerDead, actual.PlayerDead);
        Check(nameof(AllEnemiesDead), expected.AllEnemiesDead, actual.AllEnemiesDead);
        Check(nameof(PlayerHp), expected.PlayerHp, actual.PlayerHp);
        Check(nameof(PlayerMaxHp), expected.PlayerMaxHp, actual.PlayerMaxHp);
        Check(nameof(PlayerBlock), expected.PlayerBlock, actual.PlayerBlock);
        Check(nameof(OstyHp), expected.OstyHp, actual.OstyHp);
        Check(nameof(OstyMaxHp), expected.OstyMaxHp, actual.OstyMaxHp);
        Check(nameof(CumulativePlayerHpLost), expected.CumulativePlayerHpLost, actual.CumulativePlayerHpLost);
        Check(nameof(RecoveredPlayerHp), expected.RecoveredPlayerHp, actual.RecoveredPlayerHp);
        Check(nameof(DeathSaveHpRestored), expected.DeathSaveHpRestored, actual.DeathSaveHpRestored);
        Check(nameof(DeathSaveUseCount), expected.DeathSaveUseCount, actual.DeathSaveUseCount);
        Check(nameof(ProjectedDeathSaveUseCount), expected.ProjectedDeathSaveUseCount, actual.ProjectedDeathSaveUseCount);
        Check(nameof(ProjectedPlayerHp), expected.ProjectedPlayerHp, actual.ProjectedPlayerHp);
        Check(nameof(DefensiveBlockValue), expected.DefensiveBlockValue, actual.DefensiveBlockValue);
        Check(nameof(LongTermResourceValue), expected.LongTermResourceValue, actual.LongTermResourceValue);
        Check(nameof(GrowthHpCredit), expected.GrowthHpCredit, actual.GrowthHpCredit);
        Check(nameof(GrowthRewardCount), expected.GrowthRewardCount, actual.GrowthRewardCount);
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            Check($"{nameof(GrowthRewards)}.{source}",
                expected.GrowthRewards.Get(source), actual.GrowthRewards.Get(source));
        }
        Check(nameof(StrategicHpCredit), expected.StrategicHpCredit, actual.StrategicHpCredit);
        Check(nameof(AngerCopiesGenerated), expected.AngerCopiesGenerated, actual.AngerCopiesGenerated);
        Check(nameof(EnemyHp), expected.EnemyHp, actual.EnemyHp);
        Check(nameof(EnemyBlock), expected.EnemyBlock, actual.EnemyBlock);
        Check(nameof(RawEnemyHp), expected.RawEnemyHp, actual.RawEnemyHp);
        Check(nameof(MaxCurrentEnemyHp), expected.MaxCurrentEnemyHp, actual.MaxCurrentEnemyHp);
        Check(nameof(AliveEnemyCount), expected.AliveEnemyCount, actual.AliveEnemyCount);
        Check(nameof(AliveEnemyMask), expected.AliveEnemyMask, actual.AliveEnemyMask);
        Check(nameof(RevivingEnemyCount), expected.RevivingEnemyCount, actual.RevivingEnemyCount);
        Check(nameof(LiveDeckClutter), expected.LiveDeckClutter, actual.LiveDeckClutter);
        Check(nameof(LiveDeckSize), expected.LiveDeckSize, actual.LiveDeckSize);
        Check(nameof(OutstandingStolenResource), expected.OutstandingStolenResource, actual.OutstandingStolenResource);
        Check(nameof(MinimumOutstandingStolenResource), expected.MinimumOutstandingStolenResource,
            actual.MinimumOutstandingStolenResource);
        Check(nameof(RetainedAttackValue), expected.RetainedAttackValue, actual.RetainedAttackValue);
        Check(nameof(FocusTargetCombatId), expected.FocusTargetCombatId, actual.FocusTargetCombatId);
        Check(nameof(FocusTargetPressure), expected.FocusTargetPressure, actual.FocusTargetPressure);
        Check(nameof(FocusTargetRemainingHp), expected.FocusTargetRemainingHp, actual.FocusTargetRemainingHp);
        Check(nameof(FocusTargetCurrentThreat), expected.FocusTargetCurrentThreat, actual.FocusTargetCurrentThreat);
        Check(nameof(FocusTargetVulnerableTurns), expected.FocusTargetVulnerableTurns, actual.FocusTargetVulnerableTurns);
        Check(nameof(MostVulnerableTargetCombatId), expected.MostVulnerableTargetCombatId, actual.MostVulnerableTargetCombatId);
        Check(nameof(EnemyVulnerableTurns), expected.EnemyVulnerableTurns, actual.EnemyVulnerableTurns);
        Check(nameof(Energy), expected.Energy, actual.Energy);
        Check(nameof(Stars), expected.Stars, actual.Stars);
        Check(nameof(HandCount), expected.HandCount, actual.HandCount);
        Check(nameof(Turn), expected.Turn, actual.Turn);
        Check(nameof(ReachableHandValue), expected.ReachableHandValue, actual.ReachableHandValue);
        Check(nameof(ZeroCostPlayableCount), expected.ZeroCostPlayableCount, actual.ZeroCostPlayableCount);
        Check(nameof(PlayableAttackCount), expected.PlayableAttackCount, actual.PlayableAttackCount);
        Check(nameof(PersistentBuffValue), expected.PersistentBuffValue, actual.PersistentBuffValue);
        Check(nameof(SetupPotentialHp), expected.SetupPotentialHp, actual.SetupPotentialHp);
        Check(nameof(StrategicRetentionValue), expected.StrategicRetentionValue, actual.StrategicRetentionValue);
        Check(nameof(StrategicDamagePotential), expected.StrategicDamagePotential, actual.StrategicDamagePotential);
        Check(nameof(StrategicPreventionPotential), expected.StrategicPreventionPotential, actual.StrategicPreventionPotential);
        Check(nameof(StrategicResourcePotential), expected.StrategicResourcePotential, actual.StrategicResourcePotential);
        Check(nameof(StrategicCardAccessPotential), expected.StrategicCardAccessPotential, actual.StrategicCardAccessPotential);
        Check(nameof(StrategicScalingPotential), expected.StrategicScalingPotential, actual.StrategicScalingPotential);
        Check(nameof(HasRisk), expected.HasRisk, actual.HasRisk);
        Check(nameof(Act3BossInteractions), expected.Act3BossInteractions, actual.Act3BossInteractions);
        Check(nameof(FirstAttackDamage), expected.FirstAttackDamage, actual.FirstAttackDamage);
        Check(nameof(EtherealDrawTriggers), expected.EtherealDrawTriggers, actual.EtherealDrawTriggers);
        Check(nameof(PagestormBonusDrawCapacity), expected.PagestormBonusDrawCapacity,
            actual.PagestormBonusDrawCapacity);
        Check(nameof(HighEnergyPlays), expected.HighEnergyPlays, actual.HighEnergyPlays);
        Check(nameof(DemesneEnergyGain), expected.DemesneEnergyGain, actual.DemesneEnergyGain);
        Check(nameof(DemesneDrawGain), expected.DemesneDrawGain, actual.DemesneDrawGain);
        Check(nameof(PersistentSetupTraits), expected.PersistentSetupTraits, actual.PersistentSetupTraits);
        Check(nameof(StrategicSetupTraits), expected.StrategicSetupTraits, actual.StrategicSetupTraits);
        Check(nameof(LatentSetupValue), expected.LatentSetupValue, actual.LatentSetupValue);
        Check(nameof(FutureResourceValue), expected.FutureResourceValue, actual.FutureResourceValue);
        Check(nameof(ReplayPotentialValue), expected.ReplayPotentialValue, actual.ReplayPotentialValue);
        Check(nameof(DelayedDamageValue), expected.DelayedDamageValue, actual.DelayedDamageValue);
        Check(nameof(ReactiveDamageValue), expected.ReactiveDamageValue, actual.ReactiveDamageValue);
        Check(nameof(OffensiveProgressValue), expected.OffensiveProgressValue, actual.OffensiveProgressValue);
        Check(nameof(SandpitRemaining), expected.SandpitRemaining, actual.SandpitRemaining);
        Check(nameof(EnemyStrengthSuppression), expected.EnemyStrengthSuppression, actual.EnemyStrengthSuppression);
        Check(nameof(EnemyWeakTurns), expected.EnemyWeakTurns, actual.EnemyWeakTurns);
        Check(nameof(PotionUseCount), expected.PotionUseCount, actual.PotionUseCount);
        Check(nameof(PotionStrategicCost), expected.PotionStrategicCost, actual.PotionStrategicCost);
        Check(nameof(AutomaticPotionUseCount), expected.AutomaticPotionUseCount, actual.AutomaticPotionUseCount);
        Check(nameof(ExplicitPotionStrategicCost), expected.ExplicitPotionStrategicCost, actual.ExplicitPotionStrategicCost);
        Check(nameof(ExplicitPotionUseCount), expected.ExplicitPotionUseCount, actual.ExplicitPotionUseCount);
        Check(nameof(ProjectedShuffleOrderValue), expected.ProjectedShuffleOrderValue,
            actual.ProjectedShuffleOrderValue);
        Check(nameof(CanTriggerArtOfWarNextTurn), expected.CanTriggerArtOfWarNextTurn,
            actual.CanTriggerArtOfWarNextTurn);
        Check(nameof(EnemyCombatDistributionFirst), expected.EnemyCombatDistributionFirst,
            actual.EnemyCombatDistributionFirst);
        Check(nameof(EnemyCombatDistributionSecond), expected.EnemyCombatDistributionSecond,
            actual.EnemyCombatDistributionSecond);
        Check(nameof(EnemyControlDistributionFirst), expected.EnemyControlDistributionFirst,
            actual.EnemyControlDistributionFirst);
        Check(nameof(EnemyControlDistributionSecond), expected.EnemyControlDistributionSecond,
            actual.EnemyControlDistributionSecond);
        return lines;
    }
}
