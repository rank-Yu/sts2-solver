using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2SimExport;

/// <summary>
/// Everything a combat-result provider needs to decide what to inject. <see cref="ScenarioId"/> is
/// null when the opening could not be exported; providers may still return an outcome so the run
/// keeps advancing. <see cref="PlayerHpAtCombatStart"/> is the value already written to the
/// <c>OfflineRequest</c> (or the HP observed when no opening record could be built).
/// <see cref="RunSeed"/>, <see cref="Floor"/> and <see cref="CombatOrdinal"/> let providers derive
/// stable per-combat choices without touching run RNG state.
/// </summary>
internal sealed record CombatOutcomeContext(
    RunState Run,
    CombatRoom Room,
    string? ScenarioId,
    string EncounterId,
    int ActIndex,
    int PlayerHpAtCombatStart,
    string RunSeed,
    int Floor,
    int CombatOrdinal);

/// <summary>
/// Replaceable source of injected combat results. FS3 can implement this against the solver's real
/// native results; <see cref="ForceWinCombatOutcomeProvider"/> and
/// <see cref="RandomizedForceWinCombatOutcomeProvider"/> are the current force-win providers.
/// Returning null leaves the combat to the normal combat policy.
/// </summary>
internal interface ICombatOutcomeProvider
{
    InjectedCombatOutcome? Resolve(CombatOutcomeContext context);
}

/// <summary>Skip every exported combat with a victory at the HP it entered with, consuming nothing.</summary>
internal sealed class ForceWinCombatOutcomeProvider : ICombatOutcomeProvider
{
    public InjectedCombatOutcome? Resolve(CombatOutcomeContext context) =>
        new(Died: false, RemainingHp: context.PlayerHpAtCombatStart);
}

/// <summary>
/// Force-win provider that makes the following openings varied: remaining HP is drawn uniformly
/// from [20%, 100%] of max HP (never above the HP the combat started with), and a uniformly drawn
/// number of the potions held at combat start is consumed (a uniformly drawn subset for that
/// count). Both draws use a seed derived only from the run seed, floor and combat ordinal, so the
/// same export arguments reproduce the same outcomes.
/// </summary>
internal sealed class RandomizedForceWinCombatOutcomeProvider : ICombatOutcomeProvider
{
    internal const int MinimumHpPercent = 20;

    public InjectedCombatOutcome? Resolve(CombatOutcomeContext context)
    {
        Player player = context.Run.Players[0];
        var rng = new DeterministicCombatRng(context.RunSeed, context.Floor,
            context.CombatOrdinal);

        int maxHp = Math.Max(1, player.Creature.MaxHp);
        int upper = Math.Clamp(context.PlayerHpAtCombatStart, 1, maxHp);
        int minimum = Math.Max(1, (maxHp * MinimumHpPercent + 99) / 100);
        int lower = Math.Min(minimum, upper);
        int remainingHp = lower + (int)(rng.Next() % (ulong)(upper - lower + 1));

        List<int> occupiedSlots = [];
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
            if (player.PotionSlots[slot] is not null)
                occupiedSlots.Add(slot);
        int consumedCount = (int)(rng.Next() % (ulong)(occupiedSlots.Count + 1));
        for (int index = 0; index < consumedCount; index++)
        {
            int swap = index + (int)(rng.Next() % (ulong)(occupiedSlots.Count - index));
            (occupiedSlots[index], occupiedSlots[swap]) = (occupiedSlots[swap], occupiedSlots[index]);
        }
        int[] consumedSlots = [.. occupiedSlots.Take(consumedCount).Order()];

        return new InjectedCombatOutcome(Died: false, RemainingHp: remainingHp)
        {
            ConsumedPotionSlots = consumedSlots,
        };
    }

    private struct DeterministicCombatRng
    {
        private ulong _state;

        internal DeterministicCombatRng(string runSeed, int floor, int combatOrdinal)
        {
            string key = string.Create(CultureInfo.InvariantCulture,
                $"fs4-force-win-random:v1|{runSeed}|{floor}|{combatOrdinal}");
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            _state = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        }

        internal ulong Next()
        {
            // SplitMix64: fixed, runtime-independent stream for reproducible exports.
            _state += 0x9E3779B97F4A7C15UL;
            ulong value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}

internal readonly record struct CombatOutcomeStats(
    int Injected,
    int MissingOpening,
    int NestedRoomSkipped,
    int ConsumedPotionSlots,
    int PotionsAvailable,
    int FinalHpChecked,
    int FinalHpMismatch,
    int SettlementHpChecked,
    int SettlementHpMismatch,
    int SettlementPotionChecked,
    int SettlementPotionMismatch,
    int NextOpeningHpChecked,
    int NextOpeningHpMismatch,
    int NextOpeningHpHigher,
    int NextOpeningHpLower,
    int NextOpeningMaxHpChanged,
    int NextOpeningHpHigherWithMaxHpChange,
    int NextOpeningHpHigherWithSameMaxHp,
    int NextOpeningPotionChecked,
    int NextOpeningPotionMismatch,
    int NextOpeningUnchecked,
    int[] InjectedHpPercents,
    int[] InjectedConsumedCounts,
    int[] InjectedPotionCounts);

/// <summary>
/// Carries the opening snapshot from the exporter to the decision source and forwards it to the
/// configured <see cref="ICombatOutcomeProvider"/>. The snapshot is taken in BeginCombat, before
/// combat actions, so the injected remaining HP is exactly the exported initial HP.
/// </summary>
internal sealed class CombatOutcomeCoordinator
{
    private readonly ICombatOutcomeProvider? _provider;
    private Opening? _opening;
    private int _injected;
    private int _missingOpening;
    private int _nestedRoomSkipped;
    private int _consumedPotionSlots;
    private int _potionsAvailable;
    private int _finalHpChecked;
    private int _finalHpMismatch;
    private int _settlementHpChecked;
    private int _settlementHpMismatch;
    private int _settlementPotionChecked;
    private int _settlementPotionMismatch;
    private int _nextOpeningHpChecked;
    private int _nextOpeningHpMismatch;
    private int _nextOpeningHpHigher;
    private int _nextOpeningHpLower;
    private int _nextOpeningMaxHpChanged;
    private int _nextOpeningHpHigherWithMaxHpChange;
    private int _nextOpeningHpHigherWithSameMaxHp;
    private int _nextOpeningPotionChecked;
    private int _nextOpeningPotionMismatch;
    private int _nextOpeningUnchecked;
    private int? _lastExpectedHp;
    private PendingSettlement? _pendingSettlement;
    private NextOpening? _nextOpening;
    private readonly List<int> _injectedHpPercents = [];
    private readonly List<int> _injectedConsumedCounts = [];
    private readonly List<int> _injectedPotionCounts = [];

    internal CombatOutcomeCoordinator(ICombatOutcomeProvider? provider)
    {
        _provider = provider;
    }

    internal void NoteCombatOpening(string? scenarioId, int playerHp, string runSeed, int floor,
        int combatOrdinal) =>
        _opening = new Opening(scenarioId, playerHp, runSeed, floor, combatOrdinal);

    internal InjectedCombatOutcome? TryInject(RunState run, CombatRoom room)
    {
        if (_provider is null)
            return null;

        // RunDriver refuses injected outcomes for event forced combats. Leave those to the normal
        // policy instead of aborting the run; the exporter records that they were not injected.
        if (run.CurrentRoomCount > 1)
        {
            _nestedRoomSkipped++;
            return null;
        }

        Opening? opening = _opening;
        if (opening == null)
            _missingOpening++;
        Player player = run.Players[0];
        int playerHp = opening is { PlayerHp: > 0 }
            ? opening.PlayerHp
            : player.Creature.CurrentHp;
        string encounterId = room.Encounter?.IdEntry ?? string.Empty;
        var context = new CombatOutcomeContext(
            run, room, opening?.ScenarioId, encounterId, run.CurrentActIndex, playerHp,
            opening?.RunSeed ?? run.Rng.StringSeed, opening?.Floor ?? run.TotalFloor,
            opening?.CombatOrdinal ?? 0);
        InjectedCombatOutcome? outcome = _provider.Resolve(context);
        if (outcome is null)
            return null;

        _injected++;
        _consumedPotionSlots += outcome.ConsumedPotionSlots.Count;
        int expectedHp = outcome.Died ? 0 : outcome.RemainingHp;
        _lastExpectedHp = expectedHp;
        int potionsAtStart = player.PotionSlots.Count(static potion => potion is not null);
        _potionsAvailable += potionsAtStart;
        _injectedHpPercents.Add(player.Creature.MaxHp <= 0
            ? 0
            : expectedHp * 100 / player.Creature.MaxHp);
        _injectedConsumedCounts.Add(outcome.ConsumedPotionSlots.Count);
        _injectedPotionCounts.Add(potionsAtStart);
        PotionModel?[] potionRefs = [.. player.PotionSlots];
        _pendingSettlement = new PendingSettlement(
            expectedHp,
            [.. player.PotionSlots.Select(static potion => potion?.Id.Entry)],
            [.. outcome.ConsumedPotionSlots],
            potionRefs);
        return outcome;
    }

    /// <summary>
    /// Called after the injected result and rewards' combat settlement have been applied but before
    /// reward selection. Verifies that the injected HP and potion removals are visible in the run
    /// state, then remembers the state for the next room's opening check.
    /// </summary>
    internal void ObserveCombatSettlement(RunState run)
    {
        PendingSettlement? pending = _pendingSettlement;
        if (pending is null)
            return;
        _pendingSettlement = null;

        Player player = run.Players[0];
        _settlementHpChecked++;
        if (player.Creature.CurrentHp != pending.ExpectedHp)
            _settlementHpMismatch++;

        string?[] expectedIds = pending.PotionIdsBefore;
        foreach (int slot in pending.ConsumedSlots)
            expectedIds[slot] = null;
        string?[] actualIds = [.. player.PotionSlots.Select(static potion => potion?.Id.Entry)];
        _settlementPotionChecked++;
        if (!actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal))
            _settlementPotionMismatch++;

        if (pending.ExpectedHp > 0)
        {
            PotionModel[] expectedPotions = [.. player.PotionSlots.OfType<PotionModel>()];
            PotionModel[] consumedPotions = [.. pending.ConsumedSlots
                .Select(slot => pending.PotionRefsBefore[slot])
                .OfType<PotionModel>()];
            _nextOpening = new NextOpening(pending.ExpectedHp, player.Creature.MaxHp,
                expectedPotions, consumedPotions);
        }
    }

    /// <summary>
    /// Called at the next room's <c>EnterFloor</c>, after the previous combat's rewards but before
    /// the new room has any effect. HP must still be the injected value; the potions that survived
    /// injection must still be present and the consumed ones must be gone. New potions from combat
    /// rewards are allowed.
    /// </summary>
    internal void ObserveFloorStart(RunState run)
    {
        NextOpening? pending = _nextOpening;
        if (pending is null)
            return;
        _nextOpening = null;

        Player player = run.Players[0];
        bool maxHpChanged = player.Creature.MaxHp != pending.ExpectedMaxHp;
        if (maxHpChanged)
            _nextOpeningMaxHpChanged++;
        _nextOpeningHpChecked++;
        if (player.Creature.CurrentHp != pending.ExpectedHp)
        {
            _nextOpeningHpMismatch++;
            if (player.Creature.CurrentHp > pending.ExpectedHp)
            {
                _nextOpeningHpHigher++;
                if (maxHpChanged)
                    _nextOpeningHpHigherWithMaxHpChange++;
                else
                    _nextOpeningHpHigherWithSameMaxHp++;
            }
            else
            {
                _nextOpeningHpLower++;
            }
        }

        IReadOnlyList<PotionModel?> actual = player.PotionSlots;
        bool containsAllExpected = pending.ExpectedPotions.All(
            expected => actual.Any(actualPotion => ReferenceEquals(actualPotion, expected)));
        bool containsConsumed = pending.ConsumedPotions.Any(
            consumed => actual.Any(actualPotion => ReferenceEquals(actualPotion, consumed)));
        _nextOpeningPotionChecked++;
        if (!containsAllExpected || containsConsumed)
            _nextOpeningPotionMismatch++;
    }

    /// <summary>Checks that RunDriver applied the injected remaining HP before EndCombat.</summary>
    internal void ObserveCombatEnd(int finalHp)
    {
        if (_lastExpectedHp is not { } expected)
            return;
        _lastExpectedHp = null;
        _finalHpChecked++;
        if (finalHp != expected)
            _finalHpMismatch++;
    }

    internal CombatOutcomeStats TakeStats()
    {
        if (_pendingSettlement is not null)
        {
            _pendingSettlement = null;
            _settlementHpChecked++;
            _settlementHpMismatch++;
            _settlementPotionChecked++;
            _settlementPotionMismatch++;
        }
        if (_nextOpening is not null)
        {
            _nextOpening = null;
            _nextOpeningUnchecked++;
        }
        var stats = new CombatOutcomeStats(
            _injected, _missingOpening, _nestedRoomSkipped, _consumedPotionSlots,
            _potionsAvailable, _finalHpChecked, _finalHpMismatch,
            _settlementHpChecked, _settlementHpMismatch,
            _settlementPotionChecked, _settlementPotionMismatch,
            _nextOpeningHpChecked, _nextOpeningHpMismatch,
            _nextOpeningHpHigher, _nextOpeningHpLower,
            _nextOpeningMaxHpChanged, _nextOpeningHpHigherWithMaxHpChange,
            _nextOpeningHpHigherWithSameMaxHp,
            _nextOpeningPotionChecked, _nextOpeningPotionMismatch, _nextOpeningUnchecked,
            [.. _injectedHpPercents], [.. _injectedConsumedCounts], [.. _injectedPotionCounts]);
        _injected = 0;
        _missingOpening = 0;
        _nestedRoomSkipped = 0;
        _consumedPotionSlots = 0;
        _potionsAvailable = 0;
        _finalHpChecked = 0;
        _finalHpMismatch = 0;
        _settlementHpChecked = 0;
        _settlementHpMismatch = 0;
        _settlementPotionChecked = 0;
        _settlementPotionMismatch = 0;
        _nextOpeningHpChecked = 0;
        _nextOpeningHpMismatch = 0;
        _nextOpeningHpHigher = 0;
        _nextOpeningHpLower = 0;
        _nextOpeningMaxHpChanged = 0;
        _nextOpeningHpHigherWithMaxHpChange = 0;
        _nextOpeningHpHigherWithSameMaxHp = 0;
        _nextOpeningPotionChecked = 0;
        _nextOpeningPotionMismatch = 0;
        _nextOpeningUnchecked = 0;
        _injectedHpPercents.Clear();
        _injectedConsumedCounts.Clear();
        _injectedPotionCounts.Clear();
        _lastExpectedHp = null;
        _opening = null;
        return stats;
    }

    private sealed record Opening(string? ScenarioId, int PlayerHp, string RunSeed, int Floor,
        int CombatOrdinal);

    private sealed record PendingSettlement(
        int ExpectedHp,
        string?[] PotionIdsBefore,
        int[] ConsumedSlots,
        PotionModel?[] PotionRefsBefore);

    private sealed record NextOpening(
        int ExpectedHp,
        int ExpectedMaxHp,
        PotionModel[] ExpectedPotions,
        PotionModel[] ConsumedPotions);
}
