using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Reporting;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2SimExport;

/// <summary>
/// Minimal <see cref="IRunRecorder"/> whose only job is the passive combat-start notification.
/// sts2-sim invokes it after <c>Hook.BeforeCombatStart</c>, so the player loadout is the same one
/// the upcoming combat sees; every other recording callback is deliberately a no-op.
/// </summary>
internal sealed class ExportRecorder : IRunRecorder
{
    private static readonly IReadOnlyDictionary<string, CombatLog> NoCombatLogs =
        new Dictionary<string, CombatLog>(StringComparer.Ordinal);

    private readonly RunState _runState;
    private readonly int _ascension;
    private readonly RunExporter _exporter;
    private readonly ExportSummary _summary;

    internal ExportRecorder(RunState runState, int ascension, RunExporter exporter,
        ExportSummary summary)
    {
        _runState = runState;
        _ascension = ascension;
        _exporter = exporter;
        _summary = summary;
    }

    public void BeginRun(RunState runState)
    {
    }

    public void EnterFloor(MapPoint point, RoomType roomType) =>
        _exporter.EnterFloor(point, roomType);

    public void RecordFloorDetail(FloorDetail detail)
    {
    }

    public string BeginCombat(RunState runState, RoomType encounterType, string encounterName,
        CombatState combatState)
    {
        _exporter.BeginCombat(runState, encounterType, combatState, _summary);
        return encounterName;
    }

    public void RecordTurnStart(CombatState combatState)
    {
    }

    public void RecordDraw(CardModel card)
    {
    }

    public void RecordCardPlay(CardPlay cardPlay, PlayerSnapshot before, PlayerSnapshot after,
        IReadOnlyList<EnemySnapshot> enemiesBefore, IReadOnlyList<EnemySnapshot> enemiesAfter)
    {
    }

    public void RecordPotionUse(PotionModel potion, Creature? target, PlayerSnapshot before,
        PlayerSnapshot after, IReadOnlyList<EnemySnapshot> enemiesBefore,
        IReadOnlyList<EnemySnapshot> enemiesAfter)
    {
    }

    public void RecordPotionUse(PotionModel potion, Creature? target, PlayerSnapshot before,
        PlayerSnapshot after, IReadOnlyList<EnemySnapshot> enemiesBefore,
        IReadOnlyList<EnemySnapshot> enemiesAfter, bool consumed,
        IReadOnlyList<string?> potionSlotsBefore, IReadOnlyList<string?> potionSlotsAfter)
    {
    }

    public void RecordEnemyAction(Creature source, string moveId,
        IReadOnlyList<ActionSnapshotSegment> segments)
    {
    }

    public void RecordEnemyAction(Creature source, string moveId,
        IReadOnlyList<ActionSnapshotSegment> segments,
        IReadOnlyList<DamageResult> resolvedDamage)
    {
    }

    public void RecordEndTurn(PlayerSnapshot finalPlayer,
        IReadOnlyList<EnemySnapshot> finalEnemies)
    {
    }

    public void EndCombat(bool victory, PlayerSnapshot finalPlayer,
        IReadOnlyList<EnemySnapshot> finalEnemies, CombatRewards rewards)
    {
    }

    public void ExitFloor()
    {
    }

    public void EndRun(bool won, int floorsVisited, int finalHp, bool reachedBoss = false,
        bool? survived = null, bool truncated = false, int actsCleared = 0)
    {
    }

    public RunManifest BuildManifest() => new(
        SchemaVersion: "1",
        RunId: string.Empty,
        Seed: _runState.Rng.StringSeed,
        Character: _runState.Players[0].Character.Id.Entry,
        Ascension: _ascension,
        StartedAtUtc: DateTimeOffset.MinValue,
        EndedAtUtc: DateTimeOffset.MinValue,
        Result: string.Empty,
        FinalHp: 0,
        MaxHp: 0,
        FloorsVisited: 0,
        Floors: Array.Empty<FloorEntry>());

    public IReadOnlyDictionary<string, CombatLog> CombatLogs => NoCombatLogs;
}
