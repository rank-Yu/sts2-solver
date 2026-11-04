using Sts2Sim.Core.Models.Cards;
using Sts2Solver.Simulation.Combat;
using SimCard = Sts2Sim.Core.Models.CardModel;

namespace Sts2SimExport;

internal static class CardStateExport
{
    internal static bool TryExport(SimCard card, out ExportCard exported, out string reason)
    {
        exported = new ExportCard { CardId = card.Id.Entry };
        reason = string.Empty;
        string id = card.Id.Entry;
        if (!Registry.TryCard(id, out _))
        {
            reason = "card-unsupported:" + id;
            return false;
        }

        int upgrades = card.CurrentUpgradeLevel;
        if (upgrades < 0)
        {
            reason = "card-upgrade-negative:" + id;
            return false;
        }

        string? enchantmentId = null;
        int enchantmentAmount = 1;
        if (card.Enchantments.Count > 1)
        {
            reason = "card-multi-enchantment:" + id;
            return false;
        }
        if (card.Enchantments.Count == 1)
        {
            var enchantment = card.Enchantments[0];
            enchantmentId = enchantment.Id.Entry;
            if (!Registry.TryEnchantment(enchantmentId, out _))
            {
                reason = "card-enchantment-unsupported:" + enchantmentId;
                return false;
            }
            if (enchantment.Status != Sts2Sim.Core.Models.EnchantmentStatus.Normal)
            {
                reason = "card-enchantment-status:" + enchantmentId;
                return false;
            }
            decimal magnitude = enchantment.Magnitude;
            if (magnitude != decimal.Truncate(magnitude))
            {
                reason = "card-enchantment-amount:" + enchantmentId;
                return false;
            }
            enchantmentAmount = checked((int)magnitude);
            if (enchantmentAmount <= 0)
            {
                reason = "card-enchantment-amount:" + enchantmentId;
                return false;
            }
        }

        string? afflictionId = null;
        int afflictionAmount = 1;
        if (card.Affliction is { } affliction)
        {
            afflictionId = affliction.Id.Entry;
            if (!Registry.TryAffliction(afflictionId, out _))
            {
                reason = "card-affliction-unsupported:" + afflictionId;
                return false;
            }
            afflictionAmount = affliction.Amount;
            if (afflictionAmount <= 0)
            {
                reason = "card-affliction-amount:" + afflictionId;
                return false;
            }
        }

        Dictionary<string, int>? dynamicVars = null;
        Dictionary<string, string>? enumMembers = null;
        switch (card)
        {
            case GeneticAlgorithm algorithm when algorithm.IncreasedBlock != 0:
                reason = "card-state-unrepresentable:GENETIC_ALGORITHM-increased-block";
                return false;
            case TheScythe scythe when scythe.IncreasedDamage != 0:
                reason = "card-state-unrepresentable:THE_SCYTHE-increased-damage";
                return false;
            case MadScience science:
                enumMembers = new(StringComparer.Ordinal)
                {
                    ["TinkerTimeType"] = science.Type.ToString(),
                    ["TinkerTimeRider"] = science.Rider.ToString(),
                };
                break;
        }

        exported = new ExportCard
        {
            CardId = id,
            Count = 1,
            UpgradeLevels = upgrades,
            EnchantmentId = enchantmentId,
            EnchantmentAmount = enchantmentAmount,
            AfflictionId = afflictionId,
            AfflictionAmount = afflictionAmount,
            DynamicVars = dynamicVars,
            EnumMembers = enumMembers,
        };
        return true;
    }
}
