using System.Runtime.CompilerServices;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    // Cross-turn no-progress cut, ported from the original CombatSolver at 5c773caa
    // (CombatBeamSolver.ShouldPruneCrossTurnNoProgress and CombatPlan.cs CombatProgressState;
    // MIT License, Copyright (c) 2026 Torch1230 and contributors).
    //
    // A route that crosses this many consecutive turn boundaries without improving any progress
    // measure is not continued into the next turn. The limit is the larger of
    // ScoreWeights.SetupValueHorizonTurns (16) and two deck cycles (live deck size over the modified
    // hand draw). The measures and their directions are the original's: enemy HP, enemy HP+block,
    // each enemy's HP+block, living enemies, the offensive/buff/retention/resource/delayed-damage/
    // replay/retained-attack values, max HP, long-term resources, HP and projected-HP recovery above
    // the route's low, enemy Strength suppression, Weak and Vulnerable, Osty HP and max HP, deck
    // clutter and size, outstanding stolen resources, Sandpit and processed enemy deaths.
    //
    // The original's extensions (cross-turn probes, cycle-exit observations, plan commitments) belong
    // to orchestrations this search does not have, so they are not ported. Like the per-turn guard
    // this is an explicit search boundary, not a kernel rule: the record lives on route nodes only and
    // never enters Battle, the fingerprint or the state key.
    //
    // Two measures get a conservative line, the way the per-turn guard caps its own measures. The
    // original sums each enemy's Weak, Vulnerable and Strength suppression without a cap, and counts
    // any new deck-size low. A Silent root (Knights elite) showed what that does: with one enemy left
    // at 1 HP and the deck exhausted down to two or three cards, lines that re-applied Weak or cycled
    // the last cards reset the count every few turns and kept member frontiers alive for 600 turns.
    // Here each enemy's debuff counts only up to CrossTurnDebuffLine, and a deck-size low counts only
    // while the deck is still larger than a base hand (a deck that small is drawn whole every turn).
    // Even capped, a debuff high that creeps up by one every dozen turns kept those lines going, so
    // a second count tracks kill progress alone (enemy HP, HP+block, each enemy's HP+block, living
    // enemies, deaths, delayed damage). Buffs, debuffs and resources can extend a line, but a route
    // also stops after twice the ordinary limit without kill progress.

    /// <summary>Per-enemy Weak/Vulnerable turns and Strength suppression counted by the cut.</summary>
    private const int CrossTurnDebuffLine = ScoreWeights.SetupValueHorizonTurns;

    /// <summary>The game's base hand draw (<c>CombatManager.baseHandDrawCount</c>).</summary>
    private const int CrossTurnDeckLine = 5;

    private static readonly ushort CrossTurnWeakPowerId = PowerIdByName("WEAK_POWER");
    private static readonly ushort CrossTurnVulnerablePowerId = PowerIdByName("VULNERABLE_POWER");
    private static readonly ushort CrossTurnStrengthPowerId = PowerIdByName("STRENGTH_POWER");

    /// <summary>One enemy's lowest HP+block seen on the route; <c>CombatId</c> guards slot reuse.</summary>
    private readonly record struct CrossTurnEnemyFloor(uint CombatId, int Durability);

    [InlineArray(Limits.Enemies)]
    private struct CrossTurnEnemyFloors
    {
        private CrossTurnEnemyFloor _first;
    }

    /// <summary>
    /// A route's progress highs and lows at the start of a player turn. Only turn-frontier nodes own
    /// a record; every other node references the record of the turn it belongs to.
    /// </summary>
    private struct CrossTurnProgress
    {
        public int EnemyHp;
        public long EnemyDurability;
        public CrossTurnEnemyFloors EnemyFloors;
        public int EnemyFloorCount;
        public int AliveEnemyCount;
        public int OffensiveProgressValue;
        public int PersistentBuffValue;
        public int StrategicRetentionValue;
        public int FutureResourceValue;
        public int DelayedDamageValue;
        public int ReplayPotentialValue;
        public int RetainedAttackValue;
        public int PlayerMaxHp;
        public int LongTermResourceValue;
        public int LowestPlayerHp;
        public int BestPlayerHpRecovery;
        public int LowestProjectedPlayerHp;
        public int BestProjectedPlayerHpRecovery;
        public int EnemyStrengthSuppression;
        public int EnemyWeakTurns;
        public int EnemyVulnerableTurns;
        public int OstyHp;
        public int OstyMaxHp;
        public int LiveDeckClutter;
        public int LiveDeckSize;
        public int OutstandingStolenResource;
        public int SandpitRemaining;
        public int ProcessedEnemyDeaths;
        public int TurnsWithoutProgress;
        public int TurnsWithoutKillProgress;
    }

    /// <summary>
    /// The scorer values the record reads. The expansion check takes them from the child's
    /// <see cref="ScoreFeatures"/>; the turn frontier takes the same values from its pool's
    /// <see cref="RetentionFacts"/>, which copies them from the features of the same state.
    /// </summary>
    private readonly record struct CrossTurnInputs(
        int EnemyHp, int EnemyBlock, int AliveEnemyCount, int OffensiveProgressValue,
        int PersistentBuffValue, int StrategicRetentionValue, int FutureResourceValue,
        int DelayedDamageValue, int ReplayPotentialValue, int RetainedAttackValue, int PlayerMaxHp,
        int LongTermResourceValue, int PlayerHp, int ProjectedPlayerHp, int OstyHp, int OstyMaxHp,
        int LiveDeckClutter, int LiveDeckSize, int OutstandingStolenResource, int SandpitRemaining)
    {
        internal static CrossTurnInputs Of(in ScoreFeatures f) => new(
            f.EnemyHp, f.EnemyBlock, f.AliveEnemyCount, f.OffensiveProgressValue, f.PersistentBuffValue,
            f.StrategicRetentionValue, f.FutureResourceValue, f.DelayedDamageValue,
            f.ReplayPotentialValue, f.RetainedAttackValue, f.PlayerMaxHp, f.LongTermResourceValue,
            f.PlayerHp, f.ProjectedPlayerHp, f.OstyHp, f.OstyMaxHp, f.LiveDeckClutter, f.LiveDeckSize,
            f.OutstandingStolenResource, f.SandpitRemaining);

        internal static CrossTurnInputs Of(in RetentionFacts f) => new(
            f.EnemyHp, f.EnemyBlock, f.AliveEnemyCount, f.OffensiveProgressValue, f.PersistentBuffValue,
            f.StrategicRetentionValue, f.FutureResourceValue, f.DelayedDamageValue,
            f.ReplayPotentialValue, f.RetainedAttackValue, f.PlayerMaxHp, f.LongTermResourceValue,
            f.PlayerHp, f.ProjectedPlayerHp, f.OstyHp, f.OstyMaxHp, f.LiveDeckClutter, f.LiveDeckSize,
            f.OutstandingStolenResource, f.SandpitRemaining);
    }

    private static CrossTurnProgress CaptureCrossTurnProgress(in Battle battle, in ScoreFeatures features)
        => CaptureCrossTurnProgress(in battle, CrossTurnInputs.Of(in features));

    /// <summary><c>CombatProgressState.Capture</c>: the baseline of a root.</summary>
    private static CrossTurnProgress CaptureCrossTurnProgress(in Battle battle, in CrossTurnInputs features)
    {
        (int weak, int vulnerable, int suppression) = CappedEnemyDebuffs(in battle);
        CrossTurnProgress progress = new()
        {
            EnemyHp = features.EnemyHp,
            EnemyDurability = (long)features.EnemyHp + features.EnemyBlock,
            EnemyFloorCount = battle.EnemyCount,
            AliveEnemyCount = features.AliveEnemyCount,
            OffensiveProgressValue = features.OffensiveProgressValue,
            PersistentBuffValue = features.PersistentBuffValue,
            StrategicRetentionValue = features.StrategicRetentionValue,
            FutureResourceValue = features.FutureResourceValue,
            DelayedDamageValue = features.DelayedDamageValue,
            ReplayPotentialValue = features.ReplayPotentialValue,
            RetainedAttackValue = features.RetainedAttackValue,
            PlayerMaxHp = features.PlayerMaxHp,
            LongTermResourceValue = features.LongTermResourceValue,
            LowestPlayerHp = features.PlayerHp,
            BestPlayerHpRecovery = 0,
            LowestProjectedPlayerHp = features.ProjectedPlayerHp,
            BestProjectedPlayerHpRecovery = 0,
            EnemyStrengthSuppression = suppression,
            EnemyWeakTurns = weak,
            EnemyVulnerableTurns = vulnerable,
            OstyHp = features.OstyHp,
            OstyMaxHp = features.OstyMaxHp,
            LiveDeckClutter = features.LiveDeckClutter,
            LiveDeckSize = features.LiveDeckSize,
            OutstandingStolenResource = features.OutstandingStolenResource,
            SandpitRemaining = features.SandpitRemaining,
            ProcessedEnemyDeaths = ProcessedEnemyDeaths(in battle),
            TurnsWithoutProgress = 0,
            TurnsWithoutKillProgress = 0,
        };
        for (int index = 0; index < battle.EnemyCount; index++)
            progress.EnemyFloors[index] = new CrossTurnEnemyFloor(
                battle.Enemies[index].CombatId, EnemyDurability(in battle, index));
        return progress;
    }

    /// <summary>
    /// <c>CombatProgressState.Advance</c> at a turn boundary: any measure past its best resets the
    /// count, otherwise the count grows by one. Lows and highs keep the best value seen on the route.
    /// </summary>
    private static CrossTurnProgress AdvanceCrossTurnProgress(in CrossTurnProgress prior,
        in Battle battle, in CrossTurnInputs features)
    {
        long enemyDurability = (long)features.EnemyHp + features.EnemyBlock;
        int lowestPlayerHp = Math.Min(prior.LowestPlayerHp, features.PlayerHp);
        int playerHpRecovery = features.PlayerHp - lowestPlayerHp;
        int lowestProjectedPlayerHp = Math.Min(prior.LowestProjectedPlayerHp,
            features.ProjectedPlayerHp);
        int projectedPlayerHpRecovery = features.ProjectedPlayerHp - lowestProjectedPlayerHp;
        int processedEnemyDeaths = ProcessedEnemyDeaths(in battle);
        (int weak, int vulnerable, int suppression) = CappedEnemyDebuffs(in battle);

        // EnemyDurabilityProgress.MergeMinimum: only an enemy already on the floor can improve it;
        // an enemy that appeared since (a summon) joins the floor without counting as progress.
        // Enemies are never removed from Battle.Enemies, so a slot keeps its enemy unless the
        // CombatId changed.
        CrossTurnEnemyFloors floors = prior.EnemyFloors;
        bool perEnemyImproved = false;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            uint combatId = battle.Enemies[index].CombatId;
            int durability = EnemyDurability(in battle, index);
            if (index < prior.EnemyFloorCount && floors[index].CombatId == combatId)
            {
                if (durability < floors[index].Durability)
                {
                    perEnemyImproved = true;
                    floors[index] = new CrossTurnEnemyFloor(combatId, durability);
                }
            }
            else
            {
                floors[index] = new CrossTurnEnemyFloor(combatId, durability);
            }
        }

        bool killProgressed = features.EnemyHp < prior.EnemyHp
            || enemyDurability < prior.EnemyDurability
            || perEnemyImproved
            || features.AliveEnemyCount < prior.AliveEnemyCount
            || processedEnemyDeaths > prior.ProcessedEnemyDeaths
            || features.DelayedDamageValue > prior.DelayedDamageValue;
        bool progressed = killProgressed
            || features.OffensiveProgressValue > prior.OffensiveProgressValue
            || features.PersistentBuffValue > prior.PersistentBuffValue
            || features.StrategicRetentionValue > prior.StrategicRetentionValue
            || features.FutureResourceValue > prior.FutureResourceValue
            || features.ReplayPotentialValue > prior.ReplayPotentialValue
            || features.RetainedAttackValue > prior.RetainedAttackValue
            || features.PlayerMaxHp > prior.PlayerMaxHp
            || features.LongTermResourceValue > prior.LongTermResourceValue
            || playerHpRecovery > prior.BestPlayerHpRecovery
            || projectedPlayerHpRecovery > prior.BestProjectedPlayerHpRecovery
            || suppression > prior.EnemyStrengthSuppression
            || weak > prior.EnemyWeakTurns
            || vulnerable > prior.EnemyVulnerableTurns
            || features.OstyHp > prior.OstyHp
            || features.OstyMaxHp > prior.OstyMaxHp
            || features.LiveDeckClutter < prior.LiveDeckClutter
            || features.LiveDeckSize < prior.LiveDeckSize && prior.LiveDeckSize > CrossTurnDeckLine
            || features.OutstandingStolenResource < prior.OutstandingStolenResource
            || features.SandpitRemaining < prior.SandpitRemaining;

        return new CrossTurnProgress
        {
            EnemyHp = Math.Min(prior.EnemyHp, features.EnemyHp),
            EnemyDurability = Math.Min(prior.EnemyDurability, enemyDurability),
            EnemyFloors = floors,
            EnemyFloorCount = Math.Max(prior.EnemyFloorCount, battle.EnemyCount),
            AliveEnemyCount = Math.Min(prior.AliveEnemyCount, features.AliveEnemyCount),
            OffensiveProgressValue = Math.Max(prior.OffensiveProgressValue,
                features.OffensiveProgressValue),
            PersistentBuffValue = Math.Max(prior.PersistentBuffValue, features.PersistentBuffValue),
            StrategicRetentionValue = Math.Max(prior.StrategicRetentionValue,
                features.StrategicRetentionValue),
            FutureResourceValue = Math.Max(prior.FutureResourceValue, features.FutureResourceValue),
            DelayedDamageValue = Math.Max(prior.DelayedDamageValue, features.DelayedDamageValue),
            ReplayPotentialValue = Math.Max(prior.ReplayPotentialValue, features.ReplayPotentialValue),
            RetainedAttackValue = Math.Max(prior.RetainedAttackValue, features.RetainedAttackValue),
            PlayerMaxHp = Math.Max(prior.PlayerMaxHp, features.PlayerMaxHp),
            LongTermResourceValue = Math.Max(prior.LongTermResourceValue,
                features.LongTermResourceValue),
            LowestPlayerHp = lowestPlayerHp,
            BestPlayerHpRecovery = Math.Max(prior.BestPlayerHpRecovery, playerHpRecovery),
            LowestProjectedPlayerHp = lowestProjectedPlayerHp,
            BestProjectedPlayerHpRecovery = Math.Max(prior.BestProjectedPlayerHpRecovery,
                projectedPlayerHpRecovery),
            EnemyStrengthSuppression = Math.Max(prior.EnemyStrengthSuppression, suppression),
            EnemyWeakTurns = Math.Max(prior.EnemyWeakTurns, weak),
            EnemyVulnerableTurns = Math.Max(prior.EnemyVulnerableTurns, vulnerable),
            OstyHp = Math.Max(prior.OstyHp, features.OstyHp),
            OstyMaxHp = Math.Max(prior.OstyMaxHp, features.OstyMaxHp),
            LiveDeckClutter = Math.Min(prior.LiveDeckClutter, features.LiveDeckClutter),
            LiveDeckSize = Math.Min(prior.LiveDeckSize, features.LiveDeckSize),
            OutstandingStolenResource = Math.Min(prior.OutstandingStolenResource,
                features.OutstandingStolenResource),
            SandpitRemaining = Math.Min(prior.SandpitRemaining, features.SandpitRemaining),
            ProcessedEnemyDeaths = Math.Max(prior.ProcessedEnemyDeaths, processedEnemyDeaths),
            TurnsWithoutProgress = progressed ? 0 : checked(prior.TurnsWithoutProgress + 1),
            TurnsWithoutKillProgress = killProgressed
                ? 0 : checked(prior.TurnsWithoutKillProgress + 1),
        };
    }

    /// <summary>
    /// Whether this turn-boundary child is past a no-progress limit: the ordinary limit for any
    /// progress, twice that for kill progress. The deck-cycle part of the limit copies the battle to
    /// evaluate the hand draw, so it is only computed once a count has reached the fixed floor.
    /// </summary>
    private bool CrossTurnNoProgressExceeded(in CrossTurnProgress turnStart, in Battle child,
        in ScoreFeatures features)
    {
        // A boundary adds at most one to each count, so a turn start below both floors by more than
        // one cannot produce a child past a limit: skip the advance on the common path.
        int floor = ScoreWeights.SetupValueHorizonTurns;
        if (turnStart.TurnsWithoutProgress + 1 < floor
            && turnStart.TurnsWithoutKillProgress + 1 < 2 * floor)
        {
            return false;
        }
        CrossTurnProgress next = AdvanceCrossTurnProgress(in turnStart, in child,
            CrossTurnInputs.Of(in features));
        if (next.TurnsWithoutProgress < floor && next.TurnsWithoutKillProgress < 2 * floor)
            return false;
        int drawPerTurn = Math.Max(1, _scorer.ModifiedHandDrawForEvidence(in child));
        int deckCycleTurns = Math.Max(1, (features.LiveDeckSize + drawPerTurn - 1) / drawPerTurn);
        int limit = Math.Max(floor, checked(deckCycleTurns * 2));
        return next.TurnsWithoutProgress >= limit
            || next.TurnsWithoutKillProgress >= checked(2 * limit);
    }

    /// <summary>
    /// Gives every state that starts the next turn its own record. These states entered the end-turn
    /// pool still referencing the record of the turn they ended; redoing the advance from the pool's
    /// facts gives the value the expansion check saw, without scoring the state again and without
    /// storing a record for every end-turn child. Search-equivalence verification also scores the
    /// state and requires the same inputs.
    /// </summary>
    private void AssignCrossTurnProgress(Pool turnFrontier)
    {
        for (int index = 0; index < turnFrontier.Count; index++)
        {
            int node = turnFrontier.Node(index);
            ref readonly Battle state = ref turnFrontier.State(index);
            CrossTurnInputs inputs = CrossTurnInputs.Of(turnFrontier.Facts(index));
            if (_verifySearchEquivalence)
            {
                ScoreFeatures features = _scorer.Features(state, Depth(node), includeRetentionOnly: false);
                if (CrossTurnInputs.Of(in features) != inputs)
                    throw new InvalidOperationException(
                        $"跨回合进展输入与回合前沿池记录不一致：node={node} pool={inputs} "
                        + $"features={CrossTurnInputs.Of(in features)}。");
            }
            CrossTurnProgress prior = _nodes.CrossTurnAt(_nodes.CrossTurnIdAt(node));
            CrossTurnProgress next = AdvanceCrossTurnProgress(in prior, in state, in inputs);
            _nodes.SetCrossTurnId(node, _nodes.AddCrossTurn(in next));
        }
    }

    private static int EnemyDurability(in Battle battle, int index)
    {
        ref readonly Enemy enemy = ref battle.Enemies[index];
        if ((enemy.Flags & EnemyFlags.InCombat) == 0)
            return 0;
        long value = (long)Math.Max(0, enemy.Creature.CurrentHp) + Math.Max(0, enemy.Creature.Block);
        return (int)Math.Min(int.MaxValue, value);
    }

    /// <summary>
    /// The living enemies' Weak and Vulnerable turns and Strength suppression (negative Strength),
    /// each enemy's value capped at <see cref="CrossTurnDebuffLine"/>. Same enemy filter as the
    /// scorer's sums.
    /// </summary>
    private static (int Weak, int Vulnerable, int Suppression) CappedEnemyDebuffs(in Battle battle)
    {
        int weak = 0, vulnerable = 0, suppression = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.InCombat) == 0 || !enemy.Creature.IsAlive)
                continue;
            byte owner = Kernel.EnemyOwner(index);
            weak += Math.Clamp(Amount(in battle, owner, CrossTurnWeakPowerId), 0, CrossTurnDebuffLine);
            vulnerable += Math.Clamp(Amount(in battle, owner, CrossTurnVulnerablePowerId), 0,
                CrossTurnDebuffLine);
            suppression += Math.Min(-Amount(in battle, owner, CrossTurnStrengthPowerId),
                CrossTurnDebuffLine);
        }
        return (weak, vulnerable, suppression);
    }

    private static int Amount(in Battle battle, byte owner, ushort powerId)
        => powerId == ushort.MaxValue ? 0 : Kernel.GetAmount(in battle, owner, powerId);

    private static int ProcessedEnemyDeaths(in Battle battle)
    {
        int count = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if ((battle.Enemies[index].Flags & EnemyFlags.DeathProcessed) != 0)
                count++;
        }
        return count;
    }
}
