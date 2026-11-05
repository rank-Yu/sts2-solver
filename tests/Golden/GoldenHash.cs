using System.Security.Cryptography;
using System.Text;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Ai.Combat.Policy;

namespace Sts2Solver.Golden;

/// <summary>
/// The M0 host's D1 feature hash, ported field by field. The field order below is the wire order;
/// it never comes from reflection.
/// </summary>
internal static class GoldenHash
{
    public static ulong Score(in ScoreFeatures value)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(BitConverter.DoubleToInt64Bits(value.Score));
            writer.Write(value.PlayerDead);
            writer.Write(value.AllEnemiesDead);
            writer.Write(value.PlayerHp);
            writer.Write(value.PlayerMaxHp);
            writer.Write(value.PlayerBlock);
            writer.Write(value.OstyHp);
            writer.Write(value.OstyMaxHp);
            writer.Write(value.CumulativePlayerHpLost);
            writer.Write(value.RecoveredPlayerHp);
            writer.Write(value.DeathSaveHpRestored);
            writer.Write(value.DeathSaveUseCount);
            writer.Write(value.ProjectedDeathSaveUseCount);
            writer.Write(value.ProjectedPlayerHp);
            writer.Write(value.DefensiveBlockValue);
            writer.Write(value.LongTermResourceValue);
            writer.Write(value.GrowthHpCredit);
            WriteGrowth(writer, value.GrowthRewards);
            writer.Write(value.GrowthRewardCount);
            writer.Write(value.StrategicHpCredit);
            writer.Write(value.AngerCopiesGenerated);
            writer.Write(value.EnemyHp);
            writer.Write(value.EnemyBlock);
            writer.Write(value.RawEnemyHp);
            writer.Write(value.MaxCurrentEnemyHp);
            writer.Write(value.AliveEnemyCount);
            writer.Write(value.AliveEnemyMask);
            writer.Write(value.RevivingEnemyCount);
            writer.Write(value.LiveDeckClutter);
            writer.Write(value.LiveDeckSize);
            writer.Write(value.OutstandingStolenResource);
            writer.Write(value.MinimumOutstandingStolenResource);
            writer.Write(value.RetainedAttackValue);
            WriteNullable(writer, value.FocusTargetCombatId);
            writer.Write(value.FocusTargetPressure);
            writer.Write(value.FocusTargetRemainingHp);
            writer.Write(value.FocusTargetCurrentThreat);
            writer.Write(value.FocusTargetVulnerableTurns);
            WriteNullable(writer, value.MostVulnerableTargetCombatId);
            writer.Write(value.EnemyVulnerableTurns);
            writer.Write(value.Energy);
            writer.Write(value.Stars);
            writer.Write(value.HandCount);
            writer.Write(value.Turn);
            writer.Write(value.ReachableHandValue);
            writer.Write(value.ZeroCostPlayableCount);
            writer.Write(value.PlayableAttackCount);
            writer.Write(value.PersistentBuffValue);
            writer.Write(value.StrategicRetentionValue);
            writer.Write(value.StrategicDamagePotential);
            writer.Write(value.StrategicPreventionPotential);
            writer.Write(value.StrategicResourcePotential);
            writer.Write(value.StrategicCardAccessPotential);
            writer.Write(value.StrategicScalingPotential);
            writer.Write(value.HasRisk);
            writer.Write(value.Act3BossInteractions);
            writer.Write(value.FirstAttackDamage);
            writer.Write(value.EtherealDrawTriggers);
            writer.Write(value.PagestormBonusDrawCapacity);
            writer.Write(value.HighEnergyPlays);
            writer.Write(value.DemesneEnergyGain);
            writer.Write(value.DemesneDrawGain);
            writer.Write((int)value.PersistentSetupTraits);
            writer.Write((int)value.StrategicSetupTraits);
            writer.Write(value.LatentSetupValue);
            writer.Write(value.FutureResourceValue);
            writer.Write(value.ReplayPotentialValue);
            writer.Write(value.DelayedDamageValue);
            writer.Write(value.ReactiveDamageValue);
            writer.Write(value.OffensiveProgressValue);
            writer.Write(value.SandpitRemaining);
            writer.Write(value.EnemyStrengthSuppression);
            writer.Write(value.EnemyWeakTurns);
            writer.Write(value.PotionUseCount);
            writer.Write(value.PotionStrategicCost);
            writer.Write(value.AutomaticPotionUseCount);
            writer.Write(value.ExplicitPotionStrategicCost);
            writer.Write(value.ExplicitPotionUseCount);
            writer.Write(value.ProjectedShuffleOrderValue);
            writer.Write(value.ProjectedShuffleOrderKeyFirst);
            writer.Write(value.ProjectedShuffleOrderKeySecond);
            writer.Write(value.CanTriggerArtOfWarNextTurn);
            writer.Write(value.EnemyCombatDistributionFirst);
            writer.Write(value.EnemyCombatDistributionSecond);
            writer.Write(value.EnemyControlDistributionFirst);
            writer.Write(value.EnemyControlDistributionSecond);
            writer.Write(value.UnorderedPileKeyFirst);
            writer.Write(value.UnorderedPileKeySecond);
        }
        return Hash(stream.ToArray());
    }

    public static ulong Bound(in BoundValues value)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(value.HasRisk);
            writer.Write(value.FutureHealPotential);
            writer.Write(value.RemainingHealingPotential);
            writer.Write(value.FutureHealUsed);
            writer.Write(value.CumulativePlayerHpLost);
            writer.Write(value.RecoveredPlayerHp);
            writer.Write(value.PlayerHp);
            writer.Write(value.PlayerMaxHp);
            writer.Write(value.DeathSaveHpRestored);
            writer.Write(value.ExplicitPotionStrategicCost);
            writer.Write(value.StrategicHpLowerBound);
            writer.Write(value.SmartPotionRemainingHealingPotential);
            writer.Write(value.SmartPotionHpLowerBound);
        }
        return Hash(stream.ToArray());
    }

    public static ulong Trait(SearchRouteTraits traits, bool pure, bool declined, int threshold, int played)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write((int)traits);
            writer.Write(pure);
            writer.Write(declined);
            writer.Write(threshold);
            writer.Write(played);
        }
        return Hash(stream.ToArray());
    }

    private static void WriteGrowth(BinaryWriter writer, GrowthValues value)
    {
        writer.Write(value.HandOfGreed);
        writer.Write(value.TheHunt);
        writer.Write(value.Feed);
        writer.Write(value.Royalties);
        writer.Write(value.Alchemize);
        writer.Write(value.GeneticAlgorithm);
        writer.Write(value.TheScythe);
        writer.Write(value.Goopy);
        writer.Write(value.ForbiddenGrimoire);
        writer.Write(value.MadScience);
    }

    private static void WriteNullable(BinaryWriter writer, uint? value)
    {
        writer.Write(value != null);
        writer.Write(value ?? 0);
    }

    private static ulong Hash(byte[] bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return BitConverter.ToUInt64(hash);
    }
}
