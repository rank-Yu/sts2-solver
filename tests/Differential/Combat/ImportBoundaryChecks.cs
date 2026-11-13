// Import contracts exercised against native history and mutable native relic data.
using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using DamageResult = MegaCrit.Sts2.Core.Entities.Creatures.DamageResult;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Differential;

internal static class ImportBoundaryChecks
{
    internal static void Run(CombatState state, Player player, Rules rules)
    {
        CheckPoweredHits(state, player, rules);
        CheckRelicHeal(player);
    }

    private static void CheckPoweredHits(CombatState state, Player player, Rules rules)
    {
        CombatHistory history = CombatManager.Instance.History;
        List<CombatHistoryEntry> entries = (List<CombatHistoryEntry>)typeof(CombatHistory)
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;
        CombatHistoryEntry[] original = entries.ToArray();
        var enemy = state.Enemies[0];
        try
        {
            entries.RemoveAll(entry => entry is DamageReceivedEntry);
            int count = 0;
            foreach (int target in new[] { 0, 255, 256, 65535 })
            {
                while (count < target)
                {
                    history.DamageReceived(state, enemy, player.Creature,
                        new DamageResult(enemy, ValueProp.Move), null);
                    count++;
                }
                Battle battle = BattleImporter.Import(state, player, rules);
                if (battle.Enemies[0].PoweredAttackHitsThisTurn != target)
                    throw new InvalidDataException($"Powered-hit import: expected {target}, got {battle.Enemies[0].PoweredAttackHitsThisTurn}.");
            }
            history.DamageReceived(state, enemy, player.Creature,
                new DamageResult(enemy, ValueProp.Move), null);
            ExpectRejected("powered-hits-65536", ImportReason.Unrepresentable,
                () => BattleImporter.Import(state, player, rules));
        }
        finally
        {
            entries.Clear();
            entries.AddRange(original);
        }
        Console.WriteLine("IMPORT_POWERED_HITS 0/255/256/65535 accepted; 65536 rejected");
    }

    private static void CheckRelicHeal(Player player)
    {
        IList list = (IList)typeof(Player).GetField("_relics",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
        RelicModel[] original = player.Relics.ToArray();
        RelicModel[] relics =
        [
            ModelDb.Relic<BurningBlood>().ToMutable(),
            ModelDb.Relic<BlackBlood>().ToMutable(),
            ModelDb.Relic<MeatOnTheBone>().ToMutable(),
        ];
        try
        {
            list.Clear();
            foreach (RelicModel relic in relics) list.Add(relic);
            var profile = ScoreCompiler.CompilePostCombatRelicHeal(player);
            if (profile.UnconditionalHeal != relics[0].DynamicVars["Heal"].IntValue
                    + relics[1].DynamicVars["Heal"].IntValue
                || profile.WoundedHeal != relics[2].DynamicVars["Heal"].IntValue
                || profile.WoundedHpPercent != relics[2].DynamicVars["HpThreshold"].IntValue)
                throw new InvalidDataException("Native relic healing profile differs from its required values.");

            foreach ((RelicModel relic, string key) in new[]
                { (relics[0], "Heal"), (relics[1], "Heal"),
                  (relics[2], "Heal"), (relics[2], "HpThreshold") })
            {
                var vars = (Dictionary<string, DynamicVar>)typeof(DynamicVarSet)
                    .GetField("_vars", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(relic.DynamicVars)!;
                DynamicVar originalVar = vars[key];
                vars.Remove(key);
                try
                {
                    ExpectRejected(relic.Id.Entry + "." + key, ImportReason.UnsupportedRelic,
                        () => ScoreCompiler.CompilePostCombatRelicHeal(player));
                }
                finally
                {
                    vars.Add(key, originalVar);
                }
            }
        }
        finally
        {
            list.Clear();
            foreach (RelicModel relic in original) list.Add(relic);
        }
        Console.WriteLine("IMPORT_RELIC_HEAL native values/missing required fields=pass");
    }

    private static void ExpectRejected(string name, string reason, Action action)
    {
        try { action(); }
        catch (UnsupportedImportException error) when (error.Reason == reason)
        {
            Console.WriteLine($"IMPORT_REJECT {name} reason={reason}");
            return;
        }
        throw new InvalidDataException($"Expected {name} to reject with {reason}.");
    }
}
