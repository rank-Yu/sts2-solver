using System.Globalization;
using System.Text.Json;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Runs;

namespace Sts2SimExport;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!ExportOptions.TryParse(args, out ExportOptions options, out string error))
        {
            if (error.Length != 0)
                Console.Error.WriteLine(error);
            Console.WriteLine(ExportOptions.Usage);
            return 2;
        }

        ModelDb.Init(ContentRegistry.AllTypes);
        Dictionary<string, CharacterModel> characters = ModelDb.AllCharacters
            .ToDictionary(static character => character.Id.Entry, StringComparer.Ordinal);
        List<CharacterModel> selectedCharacters = new();
        foreach (string id in options.Characters)
        {
            if (!characters.TryGetValue(id, out CharacterModel? character))
            {
                Console.Error.WriteLine($"未知角色：{id}");
                return 2;
            }
            selectedCharacters.Add(character);
        }

        string output = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(output);
        string runsLog = Path.Combine(output, "runs.jsonl");
        HashSet<string> completedRuns = options.Resume && File.Exists(runsLog)
            ? File.ReadLines(runsLog)
                .Select(static line => JsonSerializer.Deserialize<RunLog>(line,
                    OfflineRequestDto.JsonLineOptions)?.Seed)
                .Where(static seed => seed is { Length: > 0 })
                .Select(static seed => seed!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var exporter = new RunExporter(options);
        var summary = new ExportSummary();
        int buckets = selectedCharacters.Count * options.Ascensions.Length;
        for (int runIndex = 0; runIndex < options.Runs; runIndex++)
        {
            if (options.TargetCombats > 0 && summary.ExportedCombats >= options.TargetCombats)
                break;
            int bucket = runIndex % buckets;
            CharacterModel character = selectedCharacters[bucket / options.Ascensions.Length];
            int ascension = options.Ascensions[bucket % options.Ascensions.Length];
            int round = runIndex / buckets;
            string seed = string.Create(CultureInfo.InvariantCulture,
                $"{options.SeedPrefix}-{character.Id.Entry}-A{ascension}-{round:D4}");
            if (!completedRuns.Add(seed))
            {
                Console.WriteLine($"RUN-SKIPPED seed={seed} reason=resume");
                continue;
            }

            summary.Runs++;
            RunDriver.Result? result = await exporter.RunOneAsync(seed, character, ascension, summary);
            if (result == null)
                summary.RunsFailed++;
            else if (result.Won)
                summary.RunsWon++;
            File.AppendAllText(runsLog, JsonSerializer.Serialize(new RunLog
            {
                Seed = seed,
                CharacterId = character.Id.Entry,
                Ascension = ascension,
                Outcome = result?.Outcome.ToString() ?? "failed",
                Won = result?.Won ?? false,
                Floors = result?.FloorsVisited ?? 0,
                FinalHp = result?.FinalPlayerHp ?? 0,
            }, OfflineRequestDto.JsonLineOptions) + Environment.NewLine);
        }

        WriteSummary(output, options, summary);
        Console.WriteLine(
            $"EXPORT-SUMMARY runs={summary.Runs} failed={summary.RunsFailed} won={summary.RunsWon} " +
            $"combats={summary.CombatsSeen} exported={summary.ExportedCombats} " +
            $"forceWin={options.ForceWin} forceWinRandomized={options.RandomizeForceWin} " +
            $"out={output}");
        if (options.ForceWin)
            Console.WriteLine(
                $"FORCE-WIN injected={summary.InjectedCombatOutcomes} " +
                $"missingOpening={summary.InjectionWithoutOpening} " +
                $"nestedRoomSkipped={summary.InjectionNestedRoomSkipped} " +
                $"potionsAvailable={summary.InjectedPotionsAvailable} " +
                $"consumedPotionSlots={summary.InjectedConsumedPotionSlots} " +
                $"finalHpChecked={summary.InjectedFinalHpChecked} " +
                $"finalHpMismatch={summary.InjectedFinalHpMismatch} " +
                $"settlementHpChecked={summary.InjectedSettlementHpChecked} " +
                $"settlementHpMismatch={summary.InjectedSettlementHpMismatch} " +
                $"settlementPotionChecked={summary.InjectedSettlementPotionChecked} " +
                $"settlementPotionMismatch={summary.InjectedSettlementPotionMismatch} " +
                $"nextOpeningHpChecked={summary.NextOpeningHpChecked} " +
                $"nextOpeningHpMismatch={summary.NextOpeningHpMismatch} " +
                $"nextOpeningHpHigher={summary.NextOpeningHpHigher} " +
                $"nextOpeningHpLower={summary.NextOpeningHpLower} " +
                $"nextOpeningMaxHpChanged={summary.NextOpeningMaxHpChanged} " +
                $"nextOpeningHpHigherWithMaxHpChange={summary.NextOpeningHpHigherWithMaxHpChange} " +
                $"nextOpeningHpHigherWithSameMaxHp={summary.NextOpeningHpHigherWithSameMaxHp} " +
                $"nextOpeningPotionChecked={summary.NextOpeningPotionChecked} " +
                $"nextOpeningPotionMismatch={summary.NextOpeningPotionMismatch} " +
                $"nextOpeningUnchecked={summary.NextOpeningUnchecked}");
        foreach ((string reason, int count) in summary.SkippedByReason
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"EXPORT-SKIP {reason}={count}");
        foreach ((string key, int count) in summary.ExportedByRoomType
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"EXPORT-ROOM {key}={count}");
        foreach ((string key, int count) in summary.ExportedByAct
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"EXPORT-ACT {key}={count}");
        foreach ((string key, int count) in summary.ExportedByActName
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"EXPORT-ACTNAME {key}={count}");
        foreach ((string key, int count) in summary.ExportedByCharacter
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"EXPORT-CHARACTER {key}={count}");
        foreach ((string key, int count) in summary.ExportedByAscension
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"EXPORT-ASCENSION {key}={count}");
        return summary.ExportedCombats == 0 ? 1 : 0;
    }

    private static void WriteSummary(string output, ExportOptions options, ExportSummary summary)
    {
        var manifest = new
        {
            schemaVersion = 1,
            tool = "Sts2SimExport",
            outputDirectory = options.OutputDirectory,
            runLimit = options.Runs,
            targetCombats = options.TargetCombats,
            maxFloors = options.MaxFloors,
            seedPrefix = options.SeedPrefix,
            characters = options.Characters,
            ascensions = options.Ascensions,
            runsAttempted = summary.Runs,
            runsFailed = summary.RunsFailed,
            runsWon = summary.RunsWon,
            forceWin = options.ForceWin,
            forceWinRandomized = options.RandomizeForceWin,
            forceWinRandomMinimumHpPercent = options.RandomizeForceWin
                ? RandomizedForceWinCombatOutcomeProvider.MinimumHpPercent
                : (int?)null,
            combatsSeen = summary.CombatsSeen,
            combatsExported = summary.ExportedCombats,
            injectedCombatOutcomes = summary.InjectedCombatOutcomes,
            injectionWithoutOpening = summary.InjectionWithoutOpening,
            injectionNestedRoomSkipped = summary.InjectionNestedRoomSkipped,
            injectedPotionsAvailable = summary.InjectedPotionsAvailable,
            injectedConsumedPotionSlots = summary.InjectedConsumedPotionSlots,
            injectedFinalHpChecked = summary.InjectedFinalHpChecked,
            injectedFinalHpMismatch = summary.InjectedFinalHpMismatch,
            injectedSettlementHpChecked = summary.InjectedSettlementHpChecked,
            injectedSettlementHpMismatch = summary.InjectedSettlementHpMismatch,
            injectedSettlementPotionChecked = summary.InjectedSettlementPotionChecked,
            injectedSettlementPotionMismatch = summary.InjectedSettlementPotionMismatch,
            nextOpeningHpChecked = summary.NextOpeningHpChecked,
            nextOpeningHpMismatch = summary.NextOpeningHpMismatch,
            nextOpeningHpHigher = summary.NextOpeningHpHigher,
            nextOpeningHpLower = summary.NextOpeningHpLower,
            nextOpeningMaxHpChanged = summary.NextOpeningMaxHpChanged,
            nextOpeningHpHigherWithMaxHpChange = summary.NextOpeningHpHigherWithMaxHpChange,
            nextOpeningHpHigherWithSameMaxHp = summary.NextOpeningHpHigherWithSameMaxHp,
            nextOpeningPotionChecked = summary.NextOpeningPotionChecked,
            nextOpeningPotionMismatch = summary.NextOpeningPotionMismatch,
            nextOpeningUnchecked = summary.NextOpeningUnchecked,
            injectedHpPercentBuckets = summary.InjectedHpPercentBuckets,
            injectedConsumedPotionBuckets = summary.InjectedConsumedPotionBuckets,
            injectedPotionCountBuckets = summary.InjectedPotionCountBuckets,
            exportedByCharacter = summary.ExportedByCharacter,
            exportedByAscension = summary.ExportedByAscension,
            exportedByAct = summary.ExportedByAct,
            exportedByActName = summary.ExportedByActName,
            exportedByRoomType = summary.ExportedByRoomType,
            exportedByEncounter = summary.ExportedByEncounter,
            skippedByReason = summary.SkippedByReason,
        };
        File.WriteAllText(Path.Combine(output, "summary.json"),
            JsonSerializer.Serialize(manifest, OfflineRequestDto.JsonOptions) + Environment.NewLine);
    }

    private sealed record RunLog
    {
        public string Seed { get; init; } = string.Empty;
        public string CharacterId { get; init; } = string.Empty;
        public int Ascension { get; init; }
        public string Outcome { get; init; } = string.Empty;
        public bool Won { get; init; }
        public int Floors { get; init; }
        public int FinalHp { get; init; }
    }
}
