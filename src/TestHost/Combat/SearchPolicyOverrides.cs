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
}
