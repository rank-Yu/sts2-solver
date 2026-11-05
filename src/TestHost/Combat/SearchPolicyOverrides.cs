// Shared request-policy overrides. The differential host's config compare and the Mod's live SREQ capture
// both apply exactly the same fields so a frozen fixture reproduces its request.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Solver.TestHost;

internal static class SearchPolicyOverrides
{
    internal static SearchPolicy ApplyOverrides(SearchPolicy policy, OfflineRequest request,
        Player player)
    {
        if (request.GrowthBudgets is { } budgets)
            policy = policy with { GrowthBudgets = budgets.ToPolicy() };
        if (request.IgnoreLongTermRewards is { } ignore)
            policy = policy with { IgnoreLongTermRewards = ignore };
        if (request.ActTransitionBossHpStrategy is { Length: > 0 } actStrategy)
            policy = policy with { ActTransitionBossHpStrategy = ParseBossStrategy(actStrategy) };
        if (request.FinalBossHpStrategy is { Length: > 0 } finalStrategy)
            policy = policy with { FinalBossHpStrategy = ParseBossStrategy(finalStrategy) };
        if (request.TheftPolicyForTest is { Length: > 0 } theft)
            policy = policy with { TheftPolicy = ParseTheftPolicy(theft) };
        if (request.PotionDefaultModeForTest is { Length: > 0 } potionMode)
            policy = policy with { PotionDefaultMode = ParsePotionMode(potionMode) };
        if (request.PotionDirectivesForTest.Length != 0)
        {
            policy = policy with
            {
                PotionDirectives = SearchPolicyCapture.ReconcilePotionDirectives(
                    ParsePotionDirectives(request.PotionDirectivesForTest), CurrentPotionSlots(player)),
            };
        }
        if (request.Act3BossStrategyForTest is { } act3)
            policy = policy with { Act3BossStrategy = act3 };
        if (request.RelicCounterRules.Length != 0)
            policy = policy with { RelicTargets = ParseRelicTargets(request.RelicCounterRules, player) };
        return policy;
    }

    private static SearchBossHpStrategy ParseBossStrategy(string text)
        => Enum.TryParse(text, ignoreCase: true, out SearchBossHpStrategy value)
            ? value
            : throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"未知 Boss HP 策略：{text}");

    private static SearchTheftPolicy ParseTheftPolicy(string text)
        => Enum.TryParse(text, ignoreCase: true, out SearchTheftPolicy value)
            ? value
            : throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"未知偷窃策略：{text}");

    private static SearchPotionMode ParsePotionMode(string text)
        => Enum.TryParse(text, ignoreCase: true, out SearchPotionMode value)
            ? value
            : throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"未知药水策略：{text}");

    private static List<(int Slot, string PotionId)> CurrentPotionSlots(Player player)
    {
        List<(int Slot, string PotionId)> slots = [];
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (player.GetPotionAtSlotIndex(slot) is { } potion)
                slots.Add((slot, potion.Id.Entry));
        }
        return slots;
    }

    private static SearchPotionDirective[] ParsePotionDirectives(
        IReadOnlyList<OfflinePotionDirective> directives)
    {
        SearchPotionDirective[] parsed = new SearchPotionDirective[directives.Count];
        for (int index = 0; index < directives.Count; index++)
        {
            OfflinePotionDirective directive = directives[index];
            if (directive.Slot < 0 || string.IsNullOrEmpty(directive.PotionId))
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"非法药水策略：slot={directive.Slot} potion={directive.PotionId}");
            }
            parsed[index] = new SearchPotionDirective(directive.Slot, directive.PotionId,
                ParsePotionMode(directive.Mode));
        }
        return parsed;
    }

    private static IReadOnlyList<SearchRelicCounterTarget> ParseRelicTargets(
        IReadOnlyList<OfflineRelicCounterRule> rules, Player player)
    {
        List<SearchRelicCounterTarget> targets = [];
        HashSet<SearchRelicCounter> seen = [];
        foreach (OfflineRelicCounterRule rule in rules)
        {
            if (!Enum.TryParse(rule.Id, ignoreCase: true, out SearchRelicCounter id)
                || !Enum.IsDefined(id))
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"未知遗物计数目标：{rule.Id}");
            }
            if (!rule.Enabled || !OwnsRelic(player, id))
                continue;
            int period = RelicPeriod(id);
            if (!seen.Add(id) || rule.Minimum < 0 || rule.Maximum < rule.Minimum
                || rule.Maximum >= period || rule.HpAllowance is < 0 or > 1000
                || rule.Priority is < 1 or > 3)
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"遗物计数规则非法：{rule.Id}");
            }
            if (rule.Minimum > 0 || rule.Maximum < period - 1)
            {
                targets.Add(new SearchRelicCounterTarget(id, rule.Minimum, rule.Maximum,
                    rule.HpAllowance, period, rule.Priority));
            }
        }
        targets.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return targets;
    }

    private static bool OwnsRelic(Player player, SearchRelicCounter id)
    {
        foreach (var relic in player.Relics)
        {
            if (relic.IsMelted)
                continue;
            if (IdentifyRelic(relic) == id)
                return true;
        }
        return false;
    }

    private static SearchRelicCounter? IdentifyRelic(MegaCrit.Sts2.Core.Models.RelicModel relic)
        => relic switch
        {
            MegaCrit.Sts2.Core.Models.Relics.HappyFlower => SearchRelicCounter.HappyFlower,
            MegaCrit.Sts2.Core.Models.Relics.FakeHappyFlower => SearchRelicCounter.FakeHappyFlower,
            MegaCrit.Sts2.Core.Models.Relics.Pendulum => SearchRelicCounter.Pendulum,
            MegaCrit.Sts2.Core.Models.Relics.PollinousCore => SearchRelicCounter.PollinousCore,
            MegaCrit.Sts2.Core.Models.Relics.PenNib => SearchRelicCounter.PenNib,
            MegaCrit.Sts2.Core.Models.Relics.Nunchaku => SearchRelicCounter.Nunchaku,
            MegaCrit.Sts2.Core.Models.Relics.TuningFork => SearchRelicCounter.TuningFork,
            MegaCrit.Sts2.Core.Models.Relics.JossPaper => SearchRelicCounter.JossPaper,
            MegaCrit.Sts2.Core.Models.Relics.IronClub => SearchRelicCounter.IronClub,
            MegaCrit.Sts2.Core.Models.Relics.GalacticDust => SearchRelicCounter.GalacticDust,
            MegaCrit.Sts2.Core.Models.Relics.MeatOnTheBone => SearchRelicCounter.MeatOnTheBone,
            _ => null,
        };

    private static int RelicPeriod(SearchRelicCounter id) => id switch
    {
        SearchRelicCounter.HappyFlower => 3,
        SearchRelicCounter.FakeHappyFlower => 5,
        SearchRelicCounter.Pendulum => 3,
        SearchRelicCounter.PollinousCore => 4,
        SearchRelicCounter.PenNib => 10,
        SearchRelicCounter.Nunchaku => 10,
        SearchRelicCounter.TuningFork => 10,
        SearchRelicCounter.JossPaper => 5,
        SearchRelicCounter.IronClub => 4,
        SearchRelicCounter.GalacticDust => 10,
        SearchRelicCounter.MeatOnTheBone => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}
