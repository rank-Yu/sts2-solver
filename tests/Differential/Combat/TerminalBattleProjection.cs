// Terminal comparison contract at the original combat-end boundary. It does not replay or
// simulate any post-combat hook: it compares only fields that are still present in the native
// combat state before CombatManager.EndCombatInternal / ProcessPendingLoss cleanup.
using System.Text;
using Sts2Solver.Simulation.Combat;

namespace Differential;

internal static class TerminalBattleProjection
{
    /// <summary>
    /// Compares the predicted terminal battle with the independently captured native battle.
    /// Returns null when the contract matches, or the first diff lines.
    /// </summary>
    internal static string? Compare(in Battle predicted, in Battle native,
        TerminalCapturePath path, Rules rules)
    {
        List<string> diff = [];
        if (predicted.Outcome == Outcome.EventDefeat)
        {
            diff.Add("outcome: event-defeat is not covered by the terminal contract");
            return diff[0];
        }
        Outcome nativeOutcome = path == TerminalCapturePath.Victory
            ? Outcome.Victory : Outcome.Defeat;
        if (predicted.Outcome != nativeOutcome)
            diff.Add($"outcome: predicted={predicted.Outcome} native={nativeOutcome}");
        if (predicted.Player.CurrentHp != native.Player.CurrentHp)
            diff.Add($"player.hp: predicted={predicted.Player.CurrentHp} native={native.Player.CurrentHp}");
        if (predicted.Player.MaxHp != native.Player.MaxHp)
            diff.Add($"player.maxHp: predicted={predicted.Player.MaxHp} native={native.Player.MaxHp}");
        if (predicted.Gold != native.Gold)
            diff.Add($"gold: predicted={predicted.Gold} native={native.Gold}");
        if (predicted.PotionSlotCount != native.PotionSlotCount)
            diff.Add($"potions.count: predicted={predicted.PotionSlotCount} native={native.PotionSlotCount}");
        else
        {
            for (int slot = 0; slot < predicted.PotionSlotCount; slot++)
            {
                if (predicted.Potions[slot] != native.Potions[slot])
                    diff.Add($"potions.{slot}: predicted={predicted.Potions[slot]} native={native.Potions[slot]}");
            }
        }
        for (int index = 0; index < rules.Relics.Count; index++)
        {
            Relic left = predicted.GetRelic(rules, index);
            Relic right = native.GetRelic(rules, index);
            if (left.Flags != right.Flags || left.Counter != right.Counter || left.Aux != right.Aux)
                diff.Add($"relic.{index}: predicted={RelicText(in left)} native={RelicText(in right)}");
        }
        for (int stream = 0; stream < Limits.RngStreams; stream++)
        {
            RngState left = predicted.RngStream((RngStream)stream);
            RngState right = native.RngStream((RngStream)stream);
            if (left.Counter != right.Counter || left.S0 != right.S0 || left.S1 != right.S1
                || left.S2 != right.S2 || left.S3 != right.S3)
            {
                diff.Add($"rng.{stream}: predicted={RngText(in left)} native={RngText(in right)}");
            }
        }
        for (int index = 0; index < diff.Count; index++)
            diff[index] = "  " + diff[index];
        return diff.Count == 0 ? null : string.Join('\n', diff);
    }

    private static string RelicText(in Relic relic)
        => $"{Registry.Relic(relic.Id).NativeId},{relic.Flags},{relic.Counter},{relic.Aux}";

    private static string RngText(in RngState rng)
        => $"counter={rng.Counter},s0={rng.S0:x16},s1={rng.S1:x16},s2={rng.S2:x16},s3={rng.S3:x16}";
}
