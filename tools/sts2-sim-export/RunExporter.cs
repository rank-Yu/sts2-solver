using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Reporting;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Solver.Simulation.Combat;

namespace Sts2SimExport;

internal sealed class ExportSummary
{
    internal Dictionary<string, int> ExportedByCharacter { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> ExportedByAscension { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> ExportedByAct { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> ExportedByActName { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> ExportedByRoomType { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> ExportedByEncounter { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> InjectedHpPercentBuckets { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> InjectedConsumedPotionBuckets { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> InjectedPotionCountBuckets { get; } = new(StringComparer.Ordinal);
    internal int ExportedCombats { get; set; }
    internal int Runs { get; set; }
    internal int RunsFailed { get; set; }
    internal int RunsWon { get; set; }
    internal int CombatsSeen { get; set; }
    internal int InjectedCombatOutcomes { get; set; }
    internal int InjectionWithoutOpening { get; set; }
    internal int InjectionNestedRoomSkipped { get; set; }
    internal int InjectedConsumedPotionSlots { get; set; }
    internal int InjectedPotionsAvailable { get; set; }
    internal int InjectedFinalHpChecked { get; set; }
    internal int InjectedFinalHpMismatch { get; set; }
    internal int InjectedSettlementHpChecked { get; set; }
    internal int InjectedSettlementHpMismatch { get; set; }
    internal int InjectedSettlementPotionChecked { get; set; }
    internal int InjectedSettlementPotionMismatch { get; set; }
    internal int NextOpeningHpChecked { get; set; }
    internal int NextOpeningHpMismatch { get; set; }
    internal int NextOpeningHpHigher { get; set; }
    internal int NextOpeningHpLower { get; set; }
    internal int NextOpeningMaxHpChanged { get; set; }
    internal int NextOpeningHpHigherWithMaxHpChange { get; set; }
    internal int NextOpeningHpHigherWithSameMaxHp { get; set; }
    internal int NextOpeningPotionChecked { get; set; }
    internal int NextOpeningPotionMismatch { get; set; }
    internal int NextOpeningUnchecked { get; set; }

    internal void CountExport(CombatExportContext context)
    {
        ExportedCombats++;
        Add(ExportedByCharacter, context.CharacterId);
        Add(ExportedByAscension, context.Ascension.ToString(CultureInfo.InvariantCulture));
        Add(ExportedByAct, context.ActIndex.ToString(CultureInfo.InvariantCulture));
        Add(ExportedByActName, context.ActName);
        Add(ExportedByRoomType, context.RoomType);
        Add(ExportedByEncounter, context.EncounterId);
    }

    internal void CountSkip(string reason) => Add(SkippedByReason, reason);

    internal void CountSeen() => CombatsSeen++;

    internal void AddOutcomeStats(CombatOutcomeStats stats)
    {
        InjectedCombatOutcomes += stats.Injected;
        InjectionWithoutOpening += stats.MissingOpening;
        InjectionNestedRoomSkipped += stats.NestedRoomSkipped;
        InjectedConsumedPotionSlots += stats.ConsumedPotionSlots;
        InjectedPotionsAvailable += stats.PotionsAvailable;
        InjectedFinalHpChecked += stats.FinalHpChecked;
        InjectedFinalHpMismatch += stats.FinalHpMismatch;
        InjectedSettlementHpChecked += stats.SettlementHpChecked;
        InjectedSettlementHpMismatch += stats.SettlementHpMismatch;
        InjectedSettlementPotionChecked += stats.SettlementPotionChecked;
        InjectedSettlementPotionMismatch += stats.SettlementPotionMismatch;
        NextOpeningHpChecked += stats.NextOpeningHpChecked;
        NextOpeningHpMismatch += stats.NextOpeningHpMismatch;
        NextOpeningHpHigher += stats.NextOpeningHpHigher;
        NextOpeningHpLower += stats.NextOpeningHpLower;
        NextOpeningMaxHpChanged += stats.NextOpeningMaxHpChanged;
        NextOpeningHpHigherWithMaxHpChange += stats.NextOpeningHpHigherWithMaxHpChange;
        NextOpeningHpHigherWithSameMaxHp += stats.NextOpeningHpHigherWithSameMaxHp;
        NextOpeningPotionChecked += stats.NextOpeningPotionChecked;
        NextOpeningPotionMismatch += stats.NextOpeningPotionMismatch;
        NextOpeningUnchecked += stats.NextOpeningUnchecked;
        foreach (int percent in stats.InjectedHpPercents)
            Add(InjectedHpPercentBuckets, PercentBucket(percent));
        foreach (int count in stats.InjectedConsumedCounts)
            Add(InjectedConsumedPotionBuckets, count.ToString(CultureInfo.InvariantCulture));
        foreach (int count in stats.InjectedPotionCounts)
            Add(InjectedPotionCountBuckets, count.ToString(CultureInfo.InvariantCulture));
    }

    internal static void Add(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private static string PercentBucket(int percent)
    {
        if (percent >= 100)
            return "100";
        int low = percent / 10 * 10;
        return string.Create(CultureInfo.InvariantCulture, $"{low}-{low + 9}");
    }
}

internal sealed record CombatExportContext(
    string ScenarioId,
    string CharacterId,
    int Ascension,
    int ActIndex,
    string ActName,
    string RoomType,
    string EncounterId,
    string Seed,
    int Floor);

internal sealed class RunExporter
{
    private readonly ExportOptions _options;
    private readonly string _outputDirectory;
    private readonly string _indexPath;
    private readonly CombatOutcomeCoordinator _outcomes;
    private int _combatOrdinalInRun;
    private string _runSeed = string.Empty;
    private PendingFloor? _pending;

    internal RunExporter(ExportOptions options)
    {
        _options = options;
        _outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(_outputDirectory);
        _indexPath = Path.Combine(_outputDirectory, "combats.jsonl");
        _outcomes = new CombatOutcomeCoordinator(options.RandomizeForceWin
            ? new RandomizedForceWinCombatOutcomeProvider()
            : options.ForceWin ? new ForceWinCombatOutcomeProvider() : null);
    }

    internal string IndexPath => _indexPath;

    internal async Task<RunDriver.Result?> RunOneAsync(string seed, CharacterModel character,
        int ascension, ExportSummary summary)
    {
        _combatOrdinalInRun = 0;
        _runSeed = seed;
        _pending = null;
        RunState runState = new(seed, ActDefinition.GetRandomList(seed), ascension);
        Player player = Player.CreateForNewRun(character, runState);
        runState.AddPlayer(player);
        if (_options.ForceWin)
        {
            // RunDriver only accepts ICombatOutcomeOverride in an imagination clone. Cloning at the
            // run boundary keeps the requested-seed run reproducible while replacing the hidden
            // encounter future with a seed derived from it.
            runState = runState.CloneReseeded(ForceWinImaginationSeed(seed));
        }
        SetRun(runState, ascension);
        var recorder = new ExportRecorder(runState, ascension, this, summary);
        var decisions = new DecisionSource(runState, seed, _outcomes);
        var driver = new RunDriver(runState, decisions, createAncientEventRoom: null,
            recorder: recorder, useAvailablePotions: false);
        RunDriver.Result result;
        try
        {
            result = await driver.RunAsync(_options.MaxFloors);
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine($"RUN-FAILED seed={seed} reason={error.Message}");
            return null;
        }
        catch (NotSupportedException error)
        {
            Console.Error.WriteLine($"RUN-FAILED seed={seed} reason={error.Message}");
            return null;
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine($"RUN-FAILED seed={seed} reason={error.Message}");
            return null;
        }
        catch (KeyNotFoundException error)
        {
            Console.Error.WriteLine($"RUN-FAILED seed={seed} reason={error.Message}");
            return null;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"RUN-FAILED seed={seed} reason={error.Message}");
            return null;
        }
        catch (InvalidCastException error)
        {
            Console.Error.WriteLine($"RUN-FAILED seed={seed} reason={error.Message}");
            return null;
        }
        finally
        {
            summary.AddOutcomeStats(_outcomes.TakeStats());
        }
        Console.WriteLine(
            $"RUN seed={seed} character={character.Id.Entry} ascension={ascension} " +
            $"outcome={result.Outcome} won={result.Won} floors={result.FloorsVisited} " +
            $"hp={result.FinalPlayerHp} combats={_combatOrdinalInRun}");
        return result;
    }

    internal void EnterFloor(MapPoint point, RoomType roomType)
    {
        if (_currentRun is not null)
            _outcomes.ObserveFloorStart(_currentRun);
        _pending = null;
        if (roomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
            return;
        _pending = BuildPending(roomType);
    }

    internal void ObserveCombatSettlement(RunState runState) =>
        _outcomes.ObserveCombatSettlement(runState);

    internal void EndCombat(PlayerSnapshot finalPlayer) => _outcomes.ObserveCombatEnd(finalPlayer.Hp);

    internal void BeginCombat(RunState runState, RoomType encounterType, CombatState combatState,
        ExportSummary summary)
    {
        summary.CountSeen();
        _combatOrdinalInRun++;
        Player? openingPlayer = combatState.Players.Count == 1 ? combatState.Players[0] : null;
        _outcomes.NoteCombatOpening(null, openingPlayer?.Creature.CurrentHp ?? 0, _runSeed,
            runState.TotalFloor, _combatOrdinalInRun);
        PendingFloor? pending = _pending;
        _pending = null;
        if (pending == null)
        {
            summary.CountSkip("no-floor-snapshot");
            return;
        }
        if (pending.SkipReason is { Length: > 0 } pendingReason)
        {
            summary.CountSkip(pendingReason);
            return;
        }
        if (combatState.Players.Count != 1)
        {
            summary.CountSkip("multi-player");
            return;
        }
        if (runState.CurrentRoom is not CombatRoom room || room.Encounter == null)
        {
            summary.CountSkip("encounter-missing");
            return;
        }
        string encounterId = room.Encounter.IdEntry;
        if (!Registry.TryEncounter(encounterId, out _))
        {
            summary.CountSkip("encounter-unsupported:" + encounterId);
            return;
        }
        if (room.RoomType != encounterType)
        {
            summary.CountSkip("room-type-mismatch");
            return;
        }
        if (room.RoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
        {
            summary.CountSkip("non-combat-room");
            return;
        }

        Player player = combatState.Players[0];
        if (player.Creature.CurrentHp <= 0 || player.Creature.MaxHp <= 0)
        {
            summary.CountSkip("player-dead");
            return;
        }
        if (player.Deck.Cards.Count != pending.Deck.Length
            || player.Relics.Count != pending.RelicIds.Length
            || !player.Relics.Select(static relic => relic.Id.Entry)
                .SequenceEqual(pending.RelicIds, StringComparer.Ordinal))
        {
            summary.CountSkip("loadout-changed-on-entry");
            return;
        }

        int actIndex = runState.CurrentActIndex;
        string actName = runState.Act.GetType().Name;
        int floor = runState.TotalFloor;
        string roomType = room.RoomType.ToString();
        string runTag = RunTag(runState.Rng.StringSeed);
        string scenarioId = string.Create(CultureInfo.InvariantCulture,
            $"{_options.SeedPrefix}-{pending.CharacterId}-A{pending.Ascension}-{runTag}-" +
            $"Act{actIndex}-F{floor:D3}-" +
            $"{roomType}-{encounterId}-{_combatOrdinalInRun:D2}");
        string combatSeed = DeriveSeed(runState.Rng.StringSeed, actIndex, floor, encounterId,
            _combatOrdinalInRun);
        var request = new OfflineRequestDto
        {
            ScenarioId = scenarioId,
            CharacterId = pending.CharacterId,
            EncounterId = encounterId,
            Seed = combatSeed,
            Ascension = pending.Ascension,
            ActIndexForTest = actIndex,
            TargetRoomType = roomType,
            TargetMapPointType = roomType,
            InitialPlayerHp = player.Creature.CurrentHp,
            InitialPlayerMaxHp = player.Creature.MaxHp,
            Cards = [],
            RunCards = pending.Deck,
            Relics = pending.Relics,
            Potions = pending.Potions,
            ClearRunDeck = true,
        };
        _outcomes.NoteCombatOpening(scenarioId, player.Creature.CurrentHp, _runSeed,
            floor, _combatOrdinalInRun);
        string path = Path.Combine(_outputDirectory, scenarioId + ".json");
        string json = JsonSerializer.Serialize(request, OfflineRequestDto.JsonOptions);
        File.WriteAllText(path, json + Environment.NewLine);
        var context = new CombatExportContext(scenarioId, pending.CharacterId, pending.Ascension,
            actIndex, actName, roomType, encounterId, combatSeed, floor);
        summary.CountExport(context);
        File.AppendAllText(_indexPath, JsonSerializer.Serialize(new
        {
            scenarioId,
            path = Path.GetRelativePath(_outputDirectory, path),
            characterId = pending.CharacterId,
            ascension = pending.Ascension,
            actIndex,
            actName,
            floor,
            roomType,
            encounterId,
            seed = combatSeed,
            runSeed = runState.Rng.StringSeed,
            deckSize = pending.Deck.Length,
            relicCount = pending.Relics.Length,
            potionCount = pending.Potions.Length,
            initialHp = player.Creature.CurrentHp,
            initialMaxHp = player.Creature.MaxHp,
        }, OfflineRequestDto.JsonLineOptions) + Environment.NewLine);
    }

    private PendingFloor BuildPending(RoomType roomType)
    {
        Player player = _currentRun!.Players[0];
        string characterId = player.Character.Id.Entry;
        if (!RelicStateExport.StartingRelics.TryGetValue(characterId, out string[]? startingRelics))
            return PendingFloor.Skip("character-unsupported:" + characterId);

        string[] relicIds = player.Relics.Select(static relic => relic.Id.Entry).ToArray();
        foreach (string starting in startingRelics)
        {
            int matching = relicIds.Count(id => string.Equals(id, starting, StringComparison.Ordinal));
            if (matching == 0)
                return PendingFloor.Skip("starting-relic-missing:" + starting);
            if (matching > 1)
                return PendingFloor.Skip("starting-relic-duplicate:" + starting);
        }
        if (player.Relics.Any(static relic => relic.StackCount != 1 &&
                RelicStateExport.StartingRelics.Values.Any(list => list.Contains(relic.Id.Entry))))
            return PendingFloor.Skip("starting-relic-stack");

        if (player.Deck.Cards.Count == 0)
            return PendingFloor.Skip("empty-deck");
        if (player.Deck.Cards.Count > Limits.Cards)
            return PendingFloor.Skip("card-capacity:" + player.Deck.Cards.Count);

        List<ExportCard> deck = new(player.Deck.Cards.Count);
        foreach (CardModel card in player.Deck.Cards)
        {
            if (!CardStateExport.TryExport(card, out ExportCard exported, out string reason))
                return PendingFloor.Skip(reason);
            deck.Add(exported);
        }

        List<ExportRelic> relics = new(player.Relics.Count);
        foreach (RelicModel relic in player.Relics)
        {
            if (startingRelics.Contains(relic.Id.Entry, StringComparer.Ordinal))
                continue;
            if (!RelicStateExport.TryExport(relic, out ExportRelic exported, out string reason))
                return PendingFloor.Skip(reason);
            relics.Add(exported);
        }

        List<ExportPotion> potions = new();
        foreach (PotionModel? potion in player.PotionSlots)
        {
            if (potion == null)
                continue;
            if (!Registry.TryPotion(potion.Id.Entry, out _))
                return PendingFloor.Skip("potion-unsupported:" + potion.Id.Entry);
            potions.Add(new ExportPotion { PotionId = potion.Id.Entry });
        }

        return new PendingFloor(
            CharacterId: characterId,
            Ascension: _ascension,
            Deck: [.. deck],
            Relics: [.. relics],
            RelicIds: relicIds,
            Potions: [.. potions],
            SkipReason: null);
    }

    private RunState? _currentRun;
    private int _ascension;

    internal void SetRun(RunState runState, int ascension)
    {
        _currentRun = runState;
        _ascension = ascension;
    }

    private static string RunTag(string runSeed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(runSeed));
        return Convert.ToHexString(hash.AsSpan(0, 4));
    }

    private static ulong ForceWinImaginationSeed(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("fs1b-force-win:" + seed));
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    private static string DeriveSeed(string runSeed, int actIndex, int floor, string encounterId,
        int combatOrdinal)
    {
        string input = string.Create(CultureInfo.InvariantCulture,
            $"{runSeed}|{actIndex}|{floor}|{encounterId}|{combatOrdinal}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private sealed record PendingFloor(
        string CharacterId,
        int Ascension,
        ExportCard[] Deck,
        ExportRelic[] Relics,
        string[] RelicIds,
        ExportPotion[] Potions,
        string? SkipReason)
    {
        internal static PendingFloor Skip(string reason) => new(
            string.Empty, 0, [], [], [], [], reason);
    }
}
