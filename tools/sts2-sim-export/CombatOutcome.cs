using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2SimExport;

/// <summary>
/// Everything a combat-result provider needs to decide what to inject. <see cref="ScenarioId"/> is
/// null when the opening could not be exported; providers may still return an outcome so the run
/// keeps advancing. <see cref="PlayerHpAtCombatStart"/> is the value already written to the
/// <c>OfflineRequest</c> (or the HP observed when no opening record could be built).
/// </summary>
internal sealed record CombatOutcomeContext(
    RunState Run,
    CombatRoom Room,
    string? ScenarioId,
    string EncounterId,
    int ActIndex,
    int PlayerHpAtCombatStart);

/// <summary>
/// Replaceable source of injected combat results. FS3 can implement this against the solver's real
/// native results; <see cref="ForceWinCombatOutcomeProvider"/> is the current force-win provider.
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

internal readonly record struct CombatOutcomeStats(
    int Injected,
    int MissingOpening,
    int NestedRoomSkipped,
    int ConsumedPotionSlots,
    int FinalHpChecked,
    int FinalHpMismatch);

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
    private int _finalHpChecked;
    private int _finalHpMismatch;
    private int? _lastExpectedHp;

    internal CombatOutcomeCoordinator(ICombatOutcomeProvider? provider)
    {
        _provider = provider;
    }

    internal void NoteCombatOpening(string? scenarioId, int playerHp) =>
        _opening = new Opening(scenarioId, playerHp);

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
        int playerHp = opening is { PlayerHp: > 0 }
            ? opening.PlayerHp
            : run.Players[0].Creature.CurrentHp;
        string encounterId = room.Encounter?.IdEntry ?? string.Empty;
        var context = new CombatOutcomeContext(
            run, room, opening?.ScenarioId, encounterId, run.CurrentActIndex, playerHp);
        InjectedCombatOutcome? outcome = _provider.Resolve(context);
        if (outcome is not null)
        {
            _injected++;
            _consumedPotionSlots += outcome.ConsumedPotionSlots.Count;
            _lastExpectedHp = outcome.Died ? 0 : outcome.RemainingHp;
        }
        return outcome;
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
        var stats = new CombatOutcomeStats(
            _injected, _missingOpening, _nestedRoomSkipped, _consumedPotionSlots,
            _finalHpChecked, _finalHpMismatch);
        _injected = 0;
        _missingOpening = 0;
        _nestedRoomSkipped = 0;
        _consumedPotionSlots = 0;
        _finalHpChecked = 0;
        _finalHpMismatch = 0;
        return stats;
    }

    private sealed record Opening(string? ScenarioId, int PlayerHp);
}
