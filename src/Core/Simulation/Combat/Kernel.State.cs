namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// The Sts2Solver simulation kernel. Each partial file mirrors one area of the existing engine and
/// names the existing method it follows; the step order inside each method is the existing
/// engine's order.
/// </summary>
[System.Runtime.CompilerServices.SkipLocalsInit]
public static partial class Kernel
{
    /// <summary>Mirrors <c>CombatPredictionSimulator.IsInProgress</c>.</summary>
    public static bool IsInProgress(in Battle battle) => battle.Outcome == Outcome.None;

    public static bool IsPendingLoss(in Battle battle) => (battle.Flags & BattleFlags.PendingLoss) != 0;

    /// <summary>
    /// Native live <c>CardModel.Type</c>: the rule table's canonical value unless the definition
    /// reads per-card state (MadScience's saved TinkerTimeType). Callers that decide on the type of a
    /// card in a pile or in play use this instead of <c>rules.Card(...).Type</c>.
    /// </summary>
    public static CardType ResolveCardType(in Battle battle, Rules rules, in Card card)
        => Registry.Card(card.Id).ResolveCardType(battle, rules, card);

    /// <summary>Mirrors <c>CombatPredictionSimulator.IsCombatEnding</c>.</summary>
    public static bool IsEnding(in Battle battle)
    {
        if (!IsInProgress(battle))
            return false;
        if (IsPendingLoss(battle))
            return true;
        for (int index = 0; index < battle.RosterCount; index++)
        {
            byte owner = EnemyOwner(battle.Roster[index]);
            ref readonly Enemy enemy = ref battle.Enemies[battle.Roster[index]];
            if (enemy.Creature.IsAlive && IsPrimaryEnemy(battle, owner))
                return false;
            // Native Hook.ShouldStopCombatFromEnding over the active listeners (Adaptable, Stock).
            for (int power = 0; power < battle.PowerCount; power++)
            {
                ref readonly Power candidate = ref battle.Powers[power];
                if (candidate.Owner == owner && candidate.Amount > 0
                    && Registry.Power(candidate.Id).PreventsCombatEnding)
                {
                    return false;
                }
            }
        }
        // Native Hook.ShouldStopCombatFromEnding iterates the active listeners, which no longer hold a
        // removed dead creature's powers, so the existing engine's equivalent stop is
        // SimulatedCombatState.HasUnresolvedSpawningDeath: an enemy that left the roster, has not
        // completed its death effects, and still holds a power that spawns primary enemies. A kill
        // completes the death effects inside Kill, so only a death outside Kill can reach this.
        if (HasUnresolvedSpawningDeath(battle))
            return false;
        return true;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.HasUnresolvedSpawningDeath</c> for the death powers the Sts2Solver
    /// content implements (<see cref="PowerDef.SpawnsPrimaryEnemyOnDeath"/>).
    /// </summary>
    private static bool HasUnresolvedSpawningDeath(in Battle battle)
    {
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            ref readonly Enemy enemy = ref battle.Enemies[index];
            if ((enemy.Flags & EnemyFlags.InCombat) != 0)
                continue;
            // HasCompletedDeathEffects covers both remaining death phases.
            if (enemy.DeathPhase != DeathPhase.None)
                continue;
            byte owner = EnemyOwner(index);
            for (int power = 0; power < battle.PowerCount; power++)
            {
                ref readonly Power candidate = ref battle.Powers[power];
                if (candidate.Owner == owner && candidate.Amount > 0
                    && Registry.Power(candidate.Id).SpawnsPrimaryEnemyOnDeath)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.IsPrimaryEnemy</c>: an enemy is secondary while it holds a
    /// positive power whose definition marks its owner secondary.
    /// </summary>
    public static bool IsPrimaryEnemy(in Battle battle, byte owner)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner == owner && power.Amount > 0 && Registry.Power(power.Id).OwnerIsSecondaryEnemy)
                return false;
        }
        return true;
    }

    public static bool IsOverOrEnding(in Battle battle) => !IsInProgress(battle) || IsEnding(battle);

    /// <summary>Mirrors <c>CombatPredictionSimulator.CheckWinCondition</c>: locks the outcome at a safe point.</summary>
    public static bool CheckWinCondition(ref Battle battle)
    {
        if (!IsInProgress(battle))
            return true;
        bool pendingLoss = IsPendingLoss(battle);
        if (!pendingLoss && !IsEnding(battle))
            return false;
        battle.Outcome = pendingLoss ? Outcome.Defeat : Outcome.Victory;
        battle.Flags &= ~BattleFlags.PendingLoss;
        return true;
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.LoseCombat</c>.</summary>
    public static void LoseCombat(ref Battle battle) => battle.Flags |= BattleFlags.PendingLoss;

    public static bool IsEnemyOwner(byte owner) => owner >= Limits.FirstEnemyOwner && owner != Limits.NoOwner;

    public static byte EnemyOwner(int enemyIndex) => checked((byte)(Limits.FirstEnemyOwner + enemyIndex));

    /// <summary>
    /// <c>SimulatedCombatState.GetPoweredAttackHitsThisTurn(player, enemy)</c>: the enemy's
    /// BeatIntoShape calculated-forge multiplier for the current player turn.
    /// </summary>
    public static int PoweredAttackHitsByPlayer(in Battle battle, byte owner)
        => IsEnemyOwner(owner) ? battle.Enemies[owner - Limits.FirstEnemyOwner].PoweredAttackHitsThisTurn : 0;

    /// <summary><c>SimulatedCombatState.RequestPlayerTurnEnd</c>: the action boundary ends the turn.</summary>
    public static void RequestPlayerTurnEnd(ref Battle battle)
    {
        battle.Flags |= BattleFlags.PlayerTurnEndRequested;
    }

    public static bool InRoster(in Battle battle, byte owner)
    {
        int enemy = owner - Limits.FirstEnemyOwner;
        for (int index = 0; index < battle.RosterCount; index++)
        {
            if (battle.Roster[index] == enemy)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.IsEnemyIntendingToAttack</c>: the current compiled move node has
    /// at least one attack (native <c>IntentType.Attack</c>/<c>DeathBlow</c> are the node's attacks).
    /// </summary>
    public static bool EnemyIntendsToAttack(in Battle battle, Rules rules, byte owner)
    {
        if (!IsEnemyOwner(owner))
            return false;
        ref readonly Enemy enemy = ref battle.Enemies[owner - Limits.FirstEnemyOwner];
        return rules.MonsterRule(enemy.Rules).Nodes[enemy.Move].Attacks.Length > 0;
    }

    /// <summary>Mirrors <c>CombatPredictionState.IsHittable</c>: alive and still in the roster.</summary>
    public static bool IsHittable(in Battle battle, byte owner)
    {
        if (owner == Limits.PlayerOwner)
            return battle.Player.IsAlive;
        if (owner == Limits.OstyOwner)
            return (battle.Flags & BattleFlags.OstyPresent) != 0 && battle.Osty.IsAlive;
        ref readonly Enemy enemy = ref battle.Enemies[owner - Limits.FirstEnemyOwner];
        return enemy.Creature.IsAlive && InRoster(battle, owner);
    }

    /// <summary>Mirrors <c>CombatPredictionState.RemoveCreature</c> for an enemy.</summary>
    public static void RemoveFromRoster(ref Battle battle, byte owner)
    {
        int enemy = owner - Limits.FirstEnemyOwner;
        int count = battle.RosterCount;
        for (int index = 0; index < count; index++)
        {
            if (battle.Roster[index] != enemy)
                continue;
            for (int next = index + 1; next < count; next++)
                battle.Roster[next - 1] = battle.Roster[next];
            battle.Roster[count - 1] = 0;
            battle.RosterCount--;
            battle.Enemies[enemy].Flags &= ~EnemyFlags.InCombat;
            return;
        }
    }

    internal static int Clamp(long value) => (int)Math.Clamp(value, -Limits.ValueClamp, Limits.ValueClamp);
}
