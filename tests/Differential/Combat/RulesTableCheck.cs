// Contract check: every supported registry id has a risk value, and every table entry
// exists in the current native models. Unregistered native content may remain unmapped.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Models;

namespace Differential;

internal static class RulesTableCheck
{
    public static int Run()
    {
        HashSet<string> nativeCards = [.. ModelDb.All.OfType<CardModel>()
            .Select(card => card.Id.Entry)];
        HashSet<string> nativePowers = [.. ModelDb.All.OfType<PowerModel>()
            .Select(power => power.Id.Entry)];
        HashSet<string> tableCards = [.. MirrorRiskTables.CardIds];
        HashSet<string> tablePowers = [.. MirrorRiskTables.PowerIds];

        List<string> missingCards = [.. nativeCards.Except(tableCards).Order(StringComparer.Ordinal)];
        List<string> extraCards = [.. tableCards.Except(nativeCards).Order(StringComparer.Ordinal)];
        List<string> missingPowers = [.. nativePowers.Except(tablePowers).Order(StringComparer.Ordinal)];
        List<string> extraPowers = [.. tablePowers.Except(nativePowers).Order(StringComparer.Ordinal)];
        List<string> registryMissing = [];
        for (ushort id = 0; id < Registry.CardCount; id++)
        {
            if (!tableCards.Contains(Registry.Card(id).NativeId))
                registryMissing.Add("card:" + Registry.Card(id).NativeId);
        }
        for (ushort id = 0; id < Registry.PowerCount; id++)
        {
            if (!tablePowers.Contains(Registry.Power(id).NativeId))
                registryMissing.Add("power:" + Registry.Power(id).NativeId);
        }

        Console.WriteLine(
            $"RULES-TABLES cards={tableCards.Count} powers={tablePowers.Count} "
            + $"native_cards={nativeCards.Count} native_powers={nativePowers.Count} "
            + $"unmapped_native_cards={missingCards.Count} extra_cards={extraCards.Count} "
            + $"unmapped_native_powers={missingPowers.Count} extra_powers={extraPowers.Count} "
            + $"registry_missing={registryMissing.Count}");
        foreach (string id in missingCards.Take(8))
            Console.Error.WriteLine($"unmapped-native-card {id}");
        foreach (string id in extraCards.Take(8))
            Console.Error.WriteLine($"extra-card {id}");
        foreach (string id in missingPowers.Take(8))
            Console.Error.WriteLine($"unmapped-native-power {id}");
        foreach (string id in extraPowers.Take(8))
            Console.Error.WriteLine($"extra-power {id}");
        foreach (string id in registryMissing.Take(8))
            Console.Error.WriteLine($"registry-missing {id}");
        return extraCards.Count == 0 && extraPowers.Count == 0
            && registryMissing.Count == 0 ? 0 : 1;
    }
}
