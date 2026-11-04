// Mid-combat live import. The native game removes dead enemies and has no source for a handful of
// old-search ledgers; this tracker keeps the stable known-enemy order and carries those ledgers so
// the reconciliation projection can still be compared byte for byte. Reference semantics follow
// CombatSolver src/Runtime/ContinuationStamp.cs at a62f1850 (MIT, Copyright (c) 2026 Torch1230 and
// contributors), but the field set is Sts2SolverBattle-only and is documented in docs/import.md 第 3.1 节.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Solver.Import;

internal sealed class LiveCombatTracker
{
    /// <summary>
    /// Ledgers the native game does not expose and the old simulator accumulated during an action.
    /// They are carried from the last predicted boundary; all other counters are imported from
    /// native history or current state and compared strictly.
    /// </summary>
    private static readonly Counter[] CarriedCounters =
    [
        Counter.RecoveredHp,
        Counter.LongTermResourceValue,
        Counter.AngerCopiesGenerated,
        Counter.DeathSaveRelicHpRestored,
        Counter.DeathSavePotionHpRestored,
        Counter.DeathSaveUseCount,
        Counter.AutomaticPotionUseCount,
        Counter.GeneratedPotionUseCount,
        Counter.GeneratedPotionStrategicCost,
        Counter.EntropicFreePotionMask,
        Counter.VitalSparkNormalizedAmount,
        Counter.GrowthRewardsHandOfGreedTheHunt,
        Counter.GrowthRewardsFeedRoyalties,
        Counter.GrowthRewardsAlchemizeGeneticAlgorithm,
        Counter.GrowthRewardsTheScytheGoopy,
        Counter.GrowthRewardsForbiddenGrimoireMadScience,
    ];

    private readonly List<NativeCreature> _known;
    private readonly List<NativeCreature> _rootEnemies;
    private readonly List<NativeCreature> _pendingAdds = [];
    private readonly Dictionary<NativeCreature, (int Gold, int Cards)> _loot =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<NativeCreature> _escapedSeen =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CardModel> _rootCards;
    private readonly int _rootEnemyCount;
    private int _escapedGold;
    private int _escapedCards;
    private bool _hasRisk;
    private bool _hasPredicted;
    private Battle _predicted;

    internal LiveCombatTracker(CombatState state)
    {
        _known = [.. state.Enemies];
        _rootEnemies = [.. state.Enemies];
        state.CreaturesChanged += OnCreaturesChanged;
        Player? player = state.Players.Count == 1 ? state.Players[0] : null;
        _rootCards = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
        if (player?.PlayerCombatState is { } combatState)
        {
            foreach (CardModel card in combatState.AllCards)
                _rootCards.Add(card);
        }
        _rootEnemyCount = _known.Count;
    }

    internal int RootEnemyCount => _rootEnemyCount;

    /// <summary>Whether an <see cref="Observe"/>d route boundary is available for carrying.</summary>
    internal bool HasPredictedBoundary => _hasPredicted;

    /// <summary>The last observed search risk flag, taken from the verified boundary.</summary>
    internal bool HasRisk => _hasRisk;

    /// <summary>Stops observing the combat; the tracker is replaced when the test host reinjects state.</summary>
    internal void Detach(CombatState state) => state.CreaturesChanged -= OnCreaturesChanged;

    internal IReadOnlyList<NativeCreature> KnownEnemies => _known;

    /// <summary>Refreshes the known order with summons that appeared after the combat root.</summary>
    internal void Refresh(CombatState state)
    {
        // CombatState.Enemies is sorted by encounter slot, not the order creatures were added.
        // The predicted table appends at AddCreature time, so record additions from the native
        // CreaturesChanged event (one event per AddCreature) and append in that order. Creation
        // order is not a substitute: SurprisePower creates the fat gremlin first but adds the
        // sneaky gremlin first.
        // A summon can be added and die before the next import (Fabricator's bot, Living Fog's Gas
        // Bomb, Phrog's Wrigglers). The native list only reports aliveness now, but the comparison
        // still needs the historical row so EnemyCount and the added card's status match.
        foreach (NativeCreature creature in _pendingAdds)
        {
            if (!Contains(creature))
                _known.Add(creature);
        }
        _pendingAdds.Clear();
        // CreatureCmd.Escape removes the escaped creature's powers before removing it from
        // CombatState.Enemies, so its stolen resources are no longer observable. Carry the loot
        // observed while it was present: the kernel leaves it outstanding and irrecoverable.
        foreach (NativeCreature escaped in state.EscapedCreatures)
        {
            if (!_escapedSeen.Add(escaped))
                continue;
            _known.Remove(escaped);
            _known.Add(escaped);
            if (_loot.Remove(escaped, out (int Gold, int Cards) carried))
            {
                _escapedGold += carried.Gold;
                _escapedCards += carried.Cards;
            }
        }
        foreach (NativeCreature creature in _known)
        {
            if (creature.IsAlive)
                _loot[creature] = LootOf(creature);
            else
                _loot.Remove(creature);
        }
        // Fallback for an addition that happened before the event subscription or was not reported:
        // append in the native list's current order, never by CombatId.
        foreach (NativeCreature creature in state.Enemies)
        {
            if (creature.IsAlive && !Contains(creature))
                _known.Add(creature);
        }
    }

    /// <summary>
    /// Rules row for each known enemy: the original root index for a root enemy, -1 for a summon.
    /// The native known list is compacted after an escape, so the current index is no longer the
    /// rules index.
    /// </summary>
    private int[] KnownRules()
    {
        int[] rules = new int[_known.Count];
        for (int index = 0; index < _known.Count; index++)
            rules[index] = _rootEnemies.IndexOf(_known[index]);
        return rules;
    }

    private static (int Gold, int Cards) LootOf(NativeCreature creature)
    {
        int gold = 0, cards = 0;
        foreach (PowerModel power in creature.Powers)
        {
            switch (power)
            {
                case SwipePower { StolenCard: not null }:
                    cards++;
                    break;
                case ThieveryPower thievery:
                    gold += Math.Max(0, thievery.DynamicVars.Gold.IntValue);
                    break;
                case HeistPower heist:
                    gold += Math.Max(0, heist.Amount);
                    break;
            }
        }
        return (gold, cards);
    }

    private static bool ContainsCreature(IReadOnlyList<NativeCreature> creatures, NativeCreature creature)
    {
        for (int index = 0; index < creatures.Count; index++)
        {
            if (ReferenceEquals(creatures[index], creature))
                return true;
        }
        return false;
    }

    private void OnCreaturesChanged(ICombatState state)
    {
        foreach (NativeCreature creature in state.Enemies)
        {
            if (Contains(creature) || _pendingAdds.Contains(creature))
                continue;
            _pendingAdds.Add(creature);
        }
    }

    /// <summary>Imports a live search root, carrying the last observed non-native ledgers.</summary>
    internal Battle ImportRoot(CombatState state, Player player, Rules rules)
    {
        Refresh(state);
        ImportOptions options = new()
        {
            KnownEnemies = _known,
            KnownEnemyRules = KnownRules(),
            AllowDeadEnemies = true,
            AllowEscapedEnemies = true,
            RootEnemyCount = _rootEnemyCount,
            HasRisk = _hasRisk,
            RootCards = _rootCards,
            Reconciliation = true,
        };
        IReadOnlySet<int>? reviving = null;
        if (_hasPredicted)
            reviving = RevivingEnemies(in _predicted);
        reviving = MergeReviving(reviving, RootRevivingEnemies(state));
        if (reviving != null)
            options = options with { RevivingEnemies = reviving };
        Battle live = BattleImporter.Import(state, player, rules, options);
        AddEscapedLoot(ref live);
        if (_hasPredicted)
            CarryNonNative(ref live, _predicted);
        return live;
    }

    /// <summary>
    /// Imports the native <c>SetupPlayerTurn</c> entry state with the production reconciliation
    /// semantics. The root is not in the play phase yet, and it is a turn-setup root: the non-native
    /// ledgers and reviving rows come from the last verified prediction boundary, while the setup
    /// root's own facts (Vital Spark normalized to zero, no action-local shuffle events) are kept.
    /// This is the production counterpart of the frozen setup-root import.
    /// </summary>
    internal Battle ImportSetupRoot(CombatState state, Player player, Rules rules)
    {
        Refresh(state);
        ImportOptions options = new()
        {
            RequirePlayPhase = false,
            TurnSetupRoot = true,
            KnownEnemies = _known,
            KnownEnemyRules = KnownRules(),
            AllowDeadEnemies = true,
            AllowEscapedEnemies = true,
            RootEnemyCount = _rootEnemyCount,
            HasRisk = _hasRisk,
            RootCards = _rootCards,
            Reconciliation = true,
        };
        IReadOnlySet<int>? reviving = null;
        if (_hasPredicted)
            reviving = RevivingEnemies(in _predicted);
        reviving = MergeReviving(reviving, RootRevivingEnemies(state));
        if (reviving != null)
            options = options with { RevivingEnemies = reviving };
        Battle live = BattleImporter.Import(state, player, rules, options);
        AddEscapedLoot(ref live);
        if (_hasPredicted)
            CarryNonNativeForSetup(ref live, _predicted);
        live.SetCounter(Counter.VitalSparkNormalizedAmount, 0);
        live.ShuffleEventCount = 0;
        live.Flags = (live.Flags & ~BattleFlags.HasRisk)
            | (_hasRisk ? BattleFlags.HasRisk : 0);
        return live;
    }

    /// <summary>Imports a live boundary for comparison with the predicted route state.</summary>
    internal Battle ImportForComparison(CombatState state, Player player, Rules rules,
        in Battle expected, bool requirePlayPhase = true, bool allowDeadPlayer = false)
    {
        Refresh(state);
        ImportOptions options = new()
        {
            RequirePlayPhase = requirePlayPhase,
            AllowDeadPlayer = allowDeadPlayer,
            KnownEnemies = _known,
            KnownEnemyRules = KnownRules(),
            AllowDeadEnemies = true,
            AllowEscapedEnemies = true,
            RootEnemyCount = _rootEnemyCount,
            HasRisk = (expected.Flags & BattleFlags.HasRisk) != 0,
            RootCards = _rootCards,
            RevivingEnemies = MergeReviving(RevivingEnemies(in expected),
                RootRevivingEnemies(state)),
            Reconciliation = true,
        };
        Battle live = BattleImporter.Import(state, player, rules, options);
        AddEscapedLoot(ref live);
        CarryNonNative(ref live, expected);
        return live;
    }

    private void AddEscapedLoot(ref Battle live)
    {
        if (_escapedGold != 0)
            live.SetCounter(Counter.OutstandingStolenGold,
                live.Counter(Counter.OutstandingStolenGold) + _escapedGold);
        if (_escapedCards != 0)
            live.SetCounter(Counter.OutstandingStolenCards,
                live.Counter(Counter.OutstandingStolenCards) + _escapedCards);
    }

    /// <summary>Records the last predicted state; it supplies the carried ledgers of later roots.</summary>
    internal void Observe(in Battle predicted)
    {
        _predicted = predicted;
        _hasPredicted = true;
        _hasRisk = (predicted.Flags & BattleFlags.HasRisk) != 0;
    }

    private static void CarryNonNative(ref Battle live, in Battle predicted)
    {
        foreach (Counter counter in CarriedCounters)
            live.SetCounter(counter, predicted.Counter(counter));
        // ShuffleEventCount is an action-local kernel counter: the importer cannot recover it from
        // the native game, so the predicted value is carried. Card piles and all nine RNG streams
        // remain strictly compared.
        live.ShuffleEventCount = predicted.ShuffleEventCount;
        live.Flags = (live.Flags & ~BattleFlags.HasRisk) | (predicted.Flags & BattleFlags.HasRisk);
    }

    /// <summary>
    /// Carries the non-native ledgers into a turn-setup root. Vital Spark is a setup-root fact
    /// (normalized to zero at the native setup entry), so it is deliberately not copied; the caller
    /// zeroes the action-local shuffle counter and takes HasRisk from the tracker.
    /// </summary>
    private static void CarryNonNativeForSetup(ref Battle live, in Battle predicted)
    {
        foreach (Counter counter in CarriedCounters)
        {
            if (counter == Counter.VitalSparkNormalizedAmount)
                continue;
            live.SetCounter(counter, predicted.Counter(counter));
        }
        live.Flags = (live.Flags & ~BattleFlags.HasRisk) | (predicted.Flags & BattleFlags.HasRisk);
    }

    private static IReadOnlySet<int> RevivingEnemies(in Battle expected)
    {
        HashSet<int> reviving = [];
        for (int index = 0; index < expected.EnemyCount; index++)
        {
            if (expected.Enemies[index].DeathPhase == DeathPhase.Reviving)
                reviving.Add(index);
        }
        return reviving;
    }

    /// <summary>
    /// Dead rows that are still in the native roster because a removal-preventing power keeps them
    /// pending a revive (Test Subject's Adaptable, Illusion). The native game removes a creature with
    /// no such power, so a dead row that is still listed is reviving even before any predicted boundary.
    /// </summary>
    private IReadOnlySet<int>? RootRevivingEnemies(CombatState state)
    {
        HashSet<int>? reviving = null;
        for (int index = 0; index < _known.Count; index++)
        {
            NativeCreature creature = _known[index];
            if (creature.IsAlive || !ContainsCreature(state.Enemies, creature)
                || !HasRemovalPreventingPower(creature))
                continue;
            (reviving ??= []).Add(index);
        }
        return reviving;
    }

    private static bool HasRemovalPreventingPower(NativeCreature creature)
    {
        foreach (PowerModel power in creature.Powers)
        {
            if (power.Amount > 0 && Registry.TryPower(power.Id.Entry, out ushort id)
                && Registry.Power(id).PreventsRemovalAfterDeath)
                return true;
        }
        return false;
    }

    private static IReadOnlySet<int>? MergeReviving(IReadOnlySet<int>? left,
        IReadOnlySet<int>? right)
    {
        if (left == null)
            return right;
        if (right == null)
            return left;
        HashSet<int> merged = [.. left];
        merged.UnionWith(right);
        return merged;
    }

    private bool Contains(NativeCreature creature)
    {
        for (int index = 0; index < _known.Count; index++)
        {
            if (ReferenceEquals(_known[index], creature))
                return true;
        }
        return false;
    }
}
