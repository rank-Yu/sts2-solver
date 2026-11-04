using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Solver.Import;

/// <summary>
/// Root-local values the old search bridge carried on its snapshot rather than in the native
/// combat state. Defaults describe a normal play-phase root captured by the offline host.
/// </summary>
public sealed record ImportOptions
{
    /// <summary><c>SimulationSnapshot.HasRisk</c>; false unless the caller carries a recorded risk.</summary>
    public bool HasRisk { get; init; }

    /// <summary>
    /// Potion uses already spent by a fixed prefix (<c>OpeningProbe</c>). M2a roots have none; kept
    /// so the generated-potion counters have the same meaning as the old bridge.
    /// </summary>
    public int SkippedPotionUses { get; init; }

    /// <summary>The root was captured at a search boundary where the battleworn dummy timed out.</summary>
    public bool EventDefeatBoundary { get; init; }

    /// <summary>Require the player play phase; true for every search root.</summary>
    public bool RequirePlayPhase { get; init; } = true;

    /// <summary>
    /// Allow importing a terminal combat where the player is already dead. Only the differential
    /// host's terminal observation uses this; search roots keep the default.
    /// </summary>
    public bool AllowDeadPlayer { get; init; }

    /// <summary>
    /// The root belongs to a turn-setup fixture (the ordinary Play root or an enumerated setup
    /// branch), captured before the test host's initial injection. The old bridge's setup capture
    /// normalized Vital Spark at least once.
    /// </summary>
    public bool TurnSetupRoot { get; init; }

    /// <summary>
    /// The exact card objects present in the player's combat piles at the turn-setup Start snapshot.
    /// <see cref="BattleImporter"/> marks a card created during setup with
    /// <see cref="Sts2Solver.Simulation.Combat.Keywords.CreatedAfterRoot"/> when it is not in this set. Null keeps
    /// the previous approximation for non-setup roots.
    /// </summary>
    public IReadOnlySet<CardModel>? RootCards { get; init; }

    /// <summary>
    /// The stable known-enemy order for a mid-combat import. Root capture leaves this null and uses
    /// <c>CombatState.Enemies</c> (all alive). Deployment reconciliation passes the root order plus
    /// summons, so an enemy removed from the native list after death keeps its Sts2Solver row.
    /// </summary>
    public IReadOnlyList<Creature>? KnownEnemies { get; init; }

    /// <summary>
    /// Allows a known enemy that is no longer in <c>CombatState.Enemies</c> or no longer alive. Such
    /// an enemy is imported as a permanent-death tombstone; its native move state machine has already
    /// been reset and is not read.
    /// </summary>
    public bool AllowDeadEnemies { get; init; }

    /// <summary>
    /// Root enemy count for a mid-combat import. Zero means <see cref="KnownEnemies"/> must equal the
    /// rule root count, preserving the root-capture behavior.
    /// </summary>
    public int RootEnemyCount { get; init; }

    /// <summary>
    /// Rules row of each <see cref="KnownEnemies"/> entry, or -1 for a summon. A mid-combat import
    /// needs this after an escape: the native known list is compacted, so a row's rules id no longer
    /// equals its current index.
    /// </summary>
    public IReadOnlyList<int>? KnownEnemyRules { get; init; }

    /// <summary>
    /// Allows escaped creatures to be absent from <c>CombatState.Enemies</c>. The caller must have
    /// already applied the kernel's escape representation: shift later owner indexes down and
    /// append the escaped creature as an <c>Escaped | PermanentlyDead</c> row at the end of
    /// <see cref="KnownEnemies"/>.
    /// </summary>
    public bool AllowEscapedEnemies { get; init; }

    /// <summary>
    /// Stable enemy indexes whose predicted death phase is <c>Reviving</c>. They are dead in the
    /// native state but must stay in the roster and keep their move/AI row; a plain dead enemy is a
    /// permanent-death tombstone.
    /// </summary>
    public IReadOnlySet<int>? RevivingEnemies { get; init; }

    /// <summary>
    /// Mid-combat reconciliation: reconstruct the per-turn/cumulative counters the running kernel
    /// accumulates from native history. Root capture keeps the frozen-snapshot behavior.
    /// </summary>
    public bool Reconciliation { get; init; }

    public static ImportOptions Default { get; } = new();
}
