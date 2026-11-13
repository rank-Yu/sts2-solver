// Negative checks for the import rejection paths: a missing private field, an unclaimed captured
// private-state entry, and a model from a third-party assembly must all reject explicitly.
using System.Collections;
using System.Reflection;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Differential;

internal static class ImportNegative
{
    public static int Run(string data, string root, CombatState state)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        Rules rules = ImportCompare.LoadRules(data, root);
        ImportBoundaryChecks.Run(state, player, rules);
        int failures = 0;
        FieldInfo internalDataField = typeof(PowerModel).GetField("_internalData",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PowerModel).FullName, "_internalData");
        FieldInfo powersField = typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature).GetField("_powers",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Creature", "_powers");
        FieldInfo ownerField = typeof(PowerModel).GetField("_owner",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PowerModel).FullName, "_owner");
        IList playerPowers = (IList)powersField.GetValue(player.Creature)!;
        failures += Check("private-read", ImportReason.MissingPrivateField, () =>
        {
            JugglingPower power = state.Creatures.SelectMany(creature => creature.Powers)
                .OfType<JugglingPower>().FirstOrDefault()
                ?? throw new InvalidOperationException("负例根没有 JugglingPower。");
            object? original = internalDataField.GetValue(power);
            try
            {
                internalDataField.SetValue(power, null);
                _ = BattleImporter.Import(state, player, rules);
            }
            finally
            {
                internalDataField.SetValue(power, original);
            }
        });
        failures += Check("unclaimed-private-state", ImportReason.UnclaimedPrivateState, () =>
            PrivateStateLedger.RequireAllClaimedForTest(["SOULBOUND_POWER.root"], []));
        failures += Check("third-party-content", ImportReason.ThirdPartyContent, () =>
        {
            ThirdPartyPower power = new();
            ownerField.SetValue(power, player.Creature);
            playerPowers.Add(power);
            try
            {
                _ = BattleImporter.Import(state, player, rules);
            }
            finally
            {
                playerPowers.Remove(power);
            }
        });
        Console.WriteLine($"negative failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, string expectedReason, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"NEGATIVE case={name} status=accepted expected={expectedReason}");
            return 1;
        }
        catch (UnsupportedImportException error)
        {
            bool ok = string.Equals(error.Reason, expectedReason, StringComparison.Ordinal);
            Console.WriteLine($"NEGATIVE case={name} status=rejected reason={error.Reason} expected={expectedReason} ok={ok}");
            return ok ? 0 : 1;
        }
    }

    /// <summary>A model deliberately defined outside the game assembly.</summary>
    private sealed class ThirdPartyPower : PowerModel
    {
        public override bool ShouldReceiveCombatHooks => true;

        public override MegaCrit.Sts2.Core.Entities.Powers.PowerType Type => MegaCrit.Sts2.Core.Entities.Powers.PowerType.Buff;

        public override MegaCrit.Sts2.Core.Entities.Powers.PowerStackType StackType => MegaCrit.Sts2.Core.Entities.Powers.PowerStackType.Single;
    }
}
