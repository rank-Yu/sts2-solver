// Check native single-player content against executable definitions and risk tables.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;

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

        HashSet<string> supportedCards = [.. Enumerable.Range(0, Registry.CardCount)
            .Select(id => Registry.Card((ushort)id).NativeId)];
        HashSet<string> supportedRelics = [.. Enumerable.Range(0, Registry.RelicCount)
            .Select(id => Registry.Relic((ushort)id).NativeId)];
        List<string> unsupported = [.. ModelDb.AllCards
            .Where(card => card is not DeprecatedCard
                && card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly
                && !supportedCards.Contains(card.Id.Entry))
            .Select(card => "card:" + card.Id.Entry)
            .Concat(ModelDb.AllRelics.Where(relic => relic is not DeprecatedRelic
                && !supportedRelics.Contains(relic.Id.Entry)).Select(relic => "relic:" + relic.Id.Entry))
            .Order(StringComparer.Ordinal)];
        foreach (string id in unsupported)
            Console.Error.WriteLine($"unsupported-native {id}");
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
            && registryMissing.Count == 0 && unsupported.Count == 0 ? 0 : 1;
    }
}
