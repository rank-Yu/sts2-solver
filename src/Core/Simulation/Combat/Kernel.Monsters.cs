namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    private const int MonsterAdvanceGuard = 32;

    /// <summary>Native id of the synthetic stunned move (<c>SimulatedCombatState.ForceStunnedMove</c>).</summary>
    public const string StunnedNodeId = "STUNNED";

    /// <summary>Native id of the synthetic revive move (<c>SimulatedCombatState.BeginIllusionRevive</c>).</summary>
    public const string ReviveNodeId = "REVIVE_MOVE";

    /// <summary><see cref="Enemy.Slot"/> value of a monster with no encounter slot name.</summary>
    public const int NoSlot = byte.MaxValue;

    /// <summary>The rule entry of an enemy, resolved through the <see cref="Enemy.Rules"/> index.</summary>
    public static MonsterRules MonsterRules(ref Exec x, byte owner)
        => x.R.MonsterRule(x.B.Enemy(owner).Rules);

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.NextFreeSlot</c>: the first encounter slot with no roster
    /// enemy, <c>null</c> when the battle has no encounter, or <c>""</c> when every slot is taken.
    /// </summary>
    public static string? NextFreeSlot(in Battle battle, Rules rules)
    {
        if (!rules.HasEncounter)
            return null;
        for (int slot = 0; slot < rules.EncounterSlots.Count; slot++)
        {
            if (!SlotOccupied(battle, slot))
                return rules.EncounterSlots[slot];
        }
        return string.Empty;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.LastFreeSlot</c>: the last free encounter slot, <c>null</c>
    /// when the battle has no encounter or when every slot is taken.
    /// </summary>
    public static string? LastFreeSlot(in Battle battle, Rules rules)
    {
        if (!rules.HasEncounter)
            return null;
        for (int slot = rules.EncounterSlots.Count - 1; slot >= 0; slot--)
        {
            if (!SlotOccupied(battle, slot))
                return rules.EncounterSlots[slot];
        }
        return null;
    }

    /// <summary>Index of <paramref name="slotName"/> in the encounter slots, or <see cref="NoSlot"/>.</summary>
    public static int SlotIndex(Rules rules, string? slotName)
    {
        if (slotName == null)
            return NoSlot;
        for (int slot = 0; slot < rules.EncounterSlots.Count; slot++)
        {
            if (string.Equals(rules.EncounterSlots[slot], slotName, StringComparison.Ordinal))
                return slot;
        }
        // SimulatedCombatState.GetSlotIndex returns -1 for a name outside _encounterSlots (an encounter
        // whose EncounterModel.Slots is empty still uses names in GenerateMonsters); NoSlot is that -1.
        return NoSlot;
    }

    private static bool SlotOccupied(in Battle battle, int slot)
    {
        for (int position = 0; position < battle.RosterCount; position++)
        {
            if (battle.Enemies[battle.Roster[position]].Slot == slot)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Mirrors <c>MonsterSpawnSupport.AddCreated</c> for one created monster: assigns the CombatId and
    /// initial HP (<c>SimulatedCombatState.CreatePredictedMonster</c>), appends it to the known
    /// enemies, inserts it into the roster in encounter-slot order
    /// (<c>AddCreature</c>/<c>SortEnemiesBySlotName</c>), applies the compiled <c>SetUpForCombat</c>
    /// values and entrance powers, rolls or defers the initial action
    /// (<c>PreparePredictedMonster</c>), and finally applies Minion.
    /// </summary>
    public static byte SpawnMonster(ref Exec x, byte source, int rulesId, int slotIndex, bool minion,
        int hpRangeShift = 0)
    {
        Enemy pending = default;
        CreateMonster(ref x, rulesId, slotIndex, int.MinValue, ref pending, hpRangeShift);
        return AddCreatedMonster(ref x, source, ref pending, minion);
    }

    /// <summary>
    /// The creation half of <c>MonsterSpawnSupport.Create</c> + <c>CreatePredictedMonster</c>: validate
    /// the rule/slot, roll the initial HP (or use <paramref name="initialHp"/>), assign the CombatId
    /// from <see cref="Battle.NextCombatId"/>, write the compiled initial AI registers and return
    /// the row value on the stack. The creature is not in the known-enemy array or roster yet; this
    /// split preserves the native order for Surprise (fat gremlin created before the sneaky one, both
    /// added afterwards) and the Niche/CombatId draw order.
    /// </summary>
    public static void CreateMonster(ref Exec x, int rulesId, int slotIndex, int initialHp,
        ref Enemy pending, int hpRangeShift = 0)
    {
        if (rulesId < 0 || rulesId >= Limits.MonsterRules)
            throw new ArgumentOutOfRangeException(nameof(rulesId), rulesId, "怪物规则条目越界。");
        MonsterRules rules = x.R.MonsterRule(checked((byte)rulesId));
        if (slotIndex != NoSlot && (uint)slotIndex >= (uint)x.R.EncounterSlots.Count)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "遭遇槽位越界。");
        if (x.B.EnemyCount >= Limits.Enemies)
            throw new CapacityException("Enemies", Limits.Enemies);
        if (rules.InitialAiRegisters.Length > Limits.EnemyAiRegisters)
            throw new CapacityException("Enemy.Ai", Limits.EnemyAiRegisters);
        pending = default;
        pending.MonsterId = rules.MonsterId;
        pending.Rules = checked((byte)rulesId);
        pending.Slot = checked((byte)slotIndex);
        pending.CombatId = x.B.NextCombatId++;
        int rolledHp = initialHp == int.MinValue ? RollInitialHp(ref x.B, rules, hpRangeShift) : initialHp;
        pending.Creature.CurrentHp = rolledHp;
        pending.Creature.MaxHp = rolledHp;
        pending.Creature.Flags = CreatureFlags.Present;
        pending.Flags = EnemyFlags.InCombat;
        for (int register = 0; register < rules.InitialAiRegisters.Length; register++)
            pending.Ai[register] = rules.InitialAiRegisters[register];
    }

    /// <summary>
    /// The add half of <c>MonsterSpawnSupport.AddCreated</c>: append the created row to the known
    /// enemies, insert it into the roster in encounter-slot order, apply the compiled entrance powers
    /// and initial action, then Minion.
    /// </summary>
    public static byte AddCreatedMonster(ref Exec x, byte source, ref Enemy pending, bool minion)
    {
        int enemyIndex = x.B.EnemyCount;
        if (enemyIndex >= Limits.Enemies)
            throw new CapacityException("Enemies", Limits.Enemies);
        byte owner = EnemyOwner(enemyIndex);
        int slotIndex = pending.Slot == NoSlot ? NoSlot : pending.Slot;
        MonsterRules rules = x.R.MonsterRule(pending.Rules);
        x.B.Enemies[enemyIndex] = pending;
        x.B.EnemyCount = checked((byte)(enemyIndex + 1));
        InsertIntoRoster(ref x.B, enemyIndex, slotIndex);
        MonsterDef def = Registry.Monster(rules.MonsterId);
        def.ApplyEntrancePowers(ref x, owner, source);
        if (x.NeedsChoice)
            return owner;
        // MonsterSpawnSupport.ApplyCreatureAddedRelics: FurCoat sets a newly added enemy's current HP
        // to 1 (never its max HP) after the entrance powers and before PreparePredictedMonster.
        if (x.R.FurCoatSpawnHpOne)
        {
            x.B.Creature(owner).CurrentHp = 1;
        }
        PrepareSpawnedMonster(ref x, owner, rules);
        if (x.NeedsChoice)
            return owner;
        if (minion && Registry.TryPower("MINION_POWER", out ushort minionPower)
            && GetAmount(x.B, owner, minionPower) <= 0)
        {
            ApplyPower(ref x, owner, minionPower, 1, source);
        }
        return owner;
    }

    /// <summary>
    /// <c>CreatePredictedMonster</c>'s HP roll: the eligible values are <c>[MinInitialHp,
    /// MaxInitialHp]</c> minus the MaxHp of every roster enemy, in ascending order (the native
    /// <c>HashSet&lt;int&gt;</c> enumeration order); with none left it draws
    /// <c>NextInt(MinInitialHp, MaxInitialHp + 1)</c>.
    /// </summary>
    private static int RollInitialHp(ref Battle battle, in MonsterRules rules, int hpRangeShift)
    {
        // Axebot's stock replacement changes RespawnMaxHpBonus (RespawnCount * 10) before the native
        // HP roll, so both bounds of the eligible range move; the compiled rule is the stock-1
        // variant and the shift is (1 - new stock) * 10.
        int minimum = rules.InitialHpMin + hpRangeShift;
        int maximum = rules.InitialHpMax + hpRangeShift;
        int eligible = 0;
        for (int hp = minimum; hp <= maximum; hp++)
        {
            if (!RosterMaxHp(battle, hp))
                eligible++;
        }
        if (eligible == 0)
            return battle.RngStream(RngStream.Niche).NextInt(minimum, maximum + 1);
        int index = battle.RngStream(RngStream.Niche).NextInt(0, eligible);
        for (int hp = minimum; hp <= maximum; hp++)
        {
            if (RosterMaxHp(battle, hp))
                continue;
            if (index-- == 0)
                return hp;
        }
        throw new InvalidOperationException("怪物初始生命在可取值上越界。");
    }

    private static bool RosterMaxHp(in Battle battle, int maxHp)
    {
        for (int position = 0; position < battle.RosterCount; position++)
        {
            if (battle.Enemies[battle.Roster[position]].Creature.MaxHp == maxHp)
                return true;
        }
        return false;
    }

    /// <summary>
    /// <c>AddCreature</c> then <c>SortEnemiesBySlotName</c> (stable ascending slot index, a missing
    /// slot name sorting as -1). The known-enemy array keeps its append order.
    /// </summary>
    private static void InsertIntoRoster(ref Battle battle, int enemyIndex, int slotIndex)
    {
        int position = battle.RosterCount;
        // A null slot name sorts as -1, before every named slot but after earlier nulls.
        int key = slotIndex == NoSlot ? -1 : slotIndex;
        while (position > 0)
        {
            int previousSlot = battle.Enemies[battle.Roster[position - 1]].Slot;
            int previousKey = previousSlot == NoSlot ? -1 : previousSlot;
            if (previousKey <= key)
                break;
            position--;
        }
        for (int index = battle.RosterCount; index > position; index--)
            battle.Roster[index] = battle.Roster[index - 1];
        battle.Roster[position] = checked((byte)enemyIndex);
        battle.RosterCount = checked((byte)(battle.RosterCount + 1));
    }

    /// <summary>
    /// Mirrors <c>PreparePredictedMonster</c>: during the player turn the initial action is rolled
    /// immediately; during the enemy turn the creature keeps the pending-roll placeholder until the
    /// end-of-enemy-turn pass (<see cref="PrepareMonsterMovesForNextRound"/>).
    /// </summary>
    private static void PrepareSpawnedMonster(ref Exec x, byte owner, in MonsterRules rules)
    {
        ref Enemy enemy = ref x.B.Enemy(owner);
        if (x.B.Side == Side.Player || rules.InitialMoveLogged)
        {
            // On the player side PreparePredictedMonster rolls the initial move; during the enemy
            // turn RegisterPendingInitialMonsterAi adopts the MoveState the state-machine constructor
            // already logged. Only an unlogged (branch) initial waits for NeedsInitialRoll.
            ResolveMonsterState(ref x, owner, rules.InitialNode);
            return;
        }
        enemy.Move = checked((byte)rules.PendingInitialMoveNode);
        enemy.ForcedMove = byte.MaxValue;
        enemy.Flags |= EnemyFlags.NeedsInitialRoll;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.BeginIllusionRevive</c>
    /// (<c>DeathLifecycle.cs:100-119</c>): sets the reviving death phase and forces the synthetic
    /// REVIVE_MOVE whose dynamic follow-up is the last logged real move.
    /// </summary>
    public static void BeginIllusionRevive(ref Exec x, byte owner)
    {
        MonsterRules rules = MonsterRules(ref x, owner);
        if (rules.ReviveNode < 0)
            throw new UnsupportedException($"怪物 {Registry.Monster(x.B.Enemy(owner).MonsterId).NativeId} 没有复活行动节点。");
        ref Enemy enemy = ref x.B.Enemy(owner);
        if (enemy.DeathPhase == DeathPhase.Reviving)
            return;
        int followUp = LastLoggedMove(enemy, rules);
        if (followUp < 0)
            throw new UnsupportedException("幻象进入复活时没有可恢复的正式行动记录。");
        if ((enemy.Flags & (EnemyFlags.PerformedMoveThisRound | EnemyFlags.ActingThisRound)) != 0)
            enemy.Flags |= EnemyFlags.ForcedMustPerformAfterMove;
        enemy.DeathPhase = DeathPhase.Reviving;
        enemy.ForcedMove = checked((byte)followUp);
        enemy.Move = checked((byte)rules.ReviveNode);
        enemy.Flags &= ~EnemyFlags.NeedsInitialRoll;
    }

    private static int LastLoggedMove(in Enemy enemy, in MonsterRules rules)
    {
        for (int index = Math.Min((int)enemy.LoggedMoves, Limits.EnemyRecentMoves) - 1; index >= 0; index--)
        {
            if (enemy.RecentMoves[index] != rules.ReviveNode)
                return enemy.RecentMoves[index];
        }
        return -1;
    }

    /// <summary>
    /// Mirrors <c>MonsterMoveSemantics.ApplyForecastMove</c>'s revive handling and the caller's
    /// <c>processedEnemyDeaths.Remove</c>: a living monster after its move is no longer marked as a
    /// processed death.
    /// </summary>
    public static bool ExecuteMonsterMove(ref Exec x, byte owner)
    {
        uint combatId = x.B.Enemy(owner).CombatId;
        x.B.Enemy(owner).Flags |= EnemyFlags.ActingThisRound;
        bool playerDied = ExecuteMonsterMoveCore(ref x, owner);
        // Escape (CreatureEscaped) compacts the known-enemy array, so the owner index may belong to a
        // different row now (or be past the end). The escaped creature no longer needs its in-turn
        // flags cleared; both flags are zero at stable boundaries.
        int row = FindEnemyIndex(x.B, combatId);
        if (row < 0)
            return playerDied;
        ref Enemy enemy = ref x.B.Enemies[row];
        enemy.Flags &= ~EnemyFlags.ActingThisRound;
        if (enemy.Creature.IsAlive)
            enemy.Flags &= ~EnemyFlags.DeathProcessed;
        return playerDied;
    }

    /// <summary>Index of the known enemy with <paramref name="combatId"/>, or -1.</summary>
    public static int FindEnemyIndex(in Battle battle, uint combatId)
    {
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (battle.Enemies[index].CombatId == combatId)
                return index;
        }
        return -1;
    }

    private static bool ExecuteMonsterMoveCore(ref Exec x, byte owner)
    {
        MonsterRules rules = MonsterRules(ref x, owner);
        uint combatId = x.B.Enemy(owner).CombatId;
        int moveNode = x.B.Enemy(owner).Move;
        MonsterNode node = rules.Nodes[moveNode];
        MonsterDef def = Registry.Monster(x.B.Enemy(owner).MonsterId);
        // MonsterMoveEffects.ApplyBeforeAttack runs before the attack context (Living Fog's BLOAT).
        def.BeforeMove(ref x, owner, moveNode);
        if (x.NeedsChoice)
            return x.B.Player.IsDead;
        bool playerDied = false;
        bool fullyBlockedAttack = false;
        if (node.Attacks.Length > 0)
        {
            // BeginAttackContext: BeforeAttack iterates the combat listeners behind the ending gate
            // (HookMirrors.BeforeAttack).
            BeforeAttackHook before = new() { Attacker = owner, Props = ValueProps.Move };
            HookDispatch.Dispatch(ref x, ref before, gatedByEnding: true);
            if (x.NeedsChoice)
                return x.B.Player.IsDead;
            x.AttackUnblockedHits = 0;
            BeginAttack(ref x, ValueProps.Move, hasCardSource: false);
            Span<byte> player = stackalloc byte[1];
            player[0] = Limits.PlayerOwner;
            int attackIndex = 0;
            foreach (MonsterAttack attack in node.Attacks)
            {
                int baseDamage = def.ModifyAttackDamage(x.B, x.R, owner, moveNode, attackIndex, attack.BaseDamage);
                int repeats = Math.Max(
                    def.ModifyAttackRepeats(x.B, x.R, owner, moveNode, attackIndex, attack.Repeats), 1);
                attackIndex++;
                for (int hit = 0; hit < repeats; hit++)
                {
                    if (DamagePlayerFromMonster(ref x, owner, player, baseDamage))
                        fullyBlockedAttack = true;
                    if (x.NeedsChoice)
                        return x.B.Player.IsDead;
                    ApplyEnemyDeathPowers(ref x);
                    if (x.B.Player.IsDead)
                    {
                        playerDied = true;
                        goto AttackDone;
                    }
                    if (x.B.Creature(owner).IsDead)
                        goto AttackDone;
                }
            }
        AttackDone:
            // EndAttackContext(completed): AfterAttack iterates the combat listeners without the ending gate.
            EndAttackContext(ref x, owner);
            if (x.NeedsChoice)
                return x.B.Player.IsDead;
        }
        if (playerDied)
            return true;
        // MonsterMoveSemantics: a fully blocked attack stuns a monster with ImbalancedPower, using
        // HEADBUTT_MOVE as the synthetic follow-up for BowlbugRock and the default otherwise, then
        // skips its next move. This runs before ResolveReviveMove and ApplyMoveEffects.
        if (fullyBlockedAttack
            && Registry.TryPower("IMBALANCED_POWER", out ushort imbalanced)
            && GetAmount(x.B, owner, imbalanced) > 0)
        {
            ForceStunnedMove(ref x, owner, def.ImbalancedStunFollowUp(rules));
            x.B.Enemy(owner).Flags |= EnemyFlags.SkipNextMove;
        }
        // MonsterMoveEffects.Apply starts with ResolveReviveMove; the synthetic illusion revive node has
        // no attacks, so this is the same position as running it before the action's effects.
        def.ResolveReviveMove(ref x, owner, moveNode);
        if (x.NeedsChoice)
            return x.B.Player.IsDead;
        if (moveNode == rules.ReviveNode)
        {
            ref Creature creature = ref x.B.Creature(owner);
            creature.CurrentHp = creature.MaxHp;
            x.B.Enemy(owner).DeathPhase = DeathPhase.None;
        }
        bool ownerAliveBefore = x.B.Creature(owner).IsAlive;
        def.ApplyMoveEffects(ref x, owner, moveNode);
        if (x.NeedsChoice)
            return x.B.Player.IsDead;
        // Escape compacts the known-enemy array; resolve the owner again before reading its vitals.
        int resolved = FindEnemyIndex(x.B, combatId);
        if (resolved < 0)
            return x.B.Player.IsDead;
        byte resolvedOwner = EnemyOwner(resolved);
        if (ownerAliveBefore && x.B.Creature(resolvedOwner).IsDead
            && (x.B.Enemies[resolved].Flags & EnemyFlags.DeathProcessed) == 0)
        {
            ApplyEnemyDeathPowers(ref x);
        }
        // MonsterMoveSemantics resolves the move's queued power amount changes before normalizing.
        if (!x.NeedsChoice)
            ResolvePowerAmountChanges(ref x);
        if (!x.NeedsChoice)
            NormalizeAeonglassWithers(ref x);
        if (!x.NeedsChoice)
            NormalizeCardAfflictions(ref x);
        return x.B.Player.IsDead;
    }

    /// <summary>
    /// <c>MonsterMoveSemantics.DamagePlayer</c>: a dead Osty whose <c>DieForYouPower</c> is still positive
    /// has that power zeroed for the duration of the hit and restored afterwards, so a dead pet's
    /// redirect listener cannot observe the attack.
    /// </summary>
    private static bool DamagePlayerFromMonster(ref Exec x, byte attacker, scoped ReadOnlySpan<byte> player,
        int baseDamage)
    {
        ushort dieForYou = PowerIdOrMax("DIE_FOR_YOU_POWER");
        int suppressed = -1;
        if (dieForYou != ushort.MaxValue
            && (x.B.Flags & BattleFlags.OstyPresent) != 0
            && x.B.Osty.IsDead
            && GetAmount(x.B, Limits.OstyOwner, dieForYou) > 0)
        {
            suppressed = GetAmount(x.B, Limits.OstyOwner, dieForYou);
            SetPowerAmount(ref x.B, Limits.OstyOwner, dieForYou, 0);
        }
        bool fullyBlocked = Damage(ref x, player, baseDamage, ValueProps.Move, attacker,
            hasCardSource: false, default);
        if (suppressed >= 0)
            SetPowerAmount(ref x.B, Limits.OstyOwner, dieForYou, suppressed);
        return fullyBlocked;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.ForceMonsterMove</c> (<c>MonsterAi.cs:58-74</c>): replaces the
    /// current move and clears the pending initial roll. Nothing is logged here; the state machine log
    /// only receives moves resolved by <see cref="ResolveMonsterState"/>.
    /// </summary>
    public static void ForceMonsterMove(ref Exec x, byte owner, int node)
    {
        MonsterRules rules = MonsterRules(ref x, owner);
        if ((uint)node >= (uint)rules.Nodes.Length)
            throw new ArgumentOutOfRangeException(nameof(node), node, "强制行动节点越界。");
        if (node == rules.StunnedNode)
            throw new UnsupportedException("合成 STUNNED 节点只能由 ForceStunnedMove 设置。");
        if (node == rules.ReviveNode)
            throw new UnsupportedException("合成 REVIVE_MOVE 节点只能由 BeginIllusionRevive 设置。");
        ref Enemy enemy = ref x.B.Enemy(owner);
        MonsterNode target = rules.Nodes[node];
        // Native ForceMonsterMove stores a MoveState object. If it differs from the move performed this
        // round, a must-perform-once target must not transition (MonsterAi.cs:122-126); the external
        // performed flag is only set after the action (Replay.cs:1164), so the in-action marker counts.
        if (node != enemy.Move
            && target.MustPerformOnceBeforeTransitioning
            && (enemy.Flags & (EnemyFlags.PerformedMoveThisRound | EnemyFlags.ActingThisRound)) != 0)
        {
            enemy.Flags |= EnemyFlags.ForcedMustPerformAfterMove;
        }
        enemy.Move = checked((byte)node);
        enemy.ForcedMove = byte.MaxValue;
        enemy.Flags &= ~EnemyFlags.NeedsInitialRoll;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.ForceStunnedMove</c> (<c>MonsterAi.cs:76-86</c>): builds a new
    /// <c>MoveState("STUNNED")</c> with no attacks or effects, <c>MustPerformOnceBeforeTransitioning</c>,
    /// and a dynamic follow-up. <paramref name="nextNode"/> below zero takes the native default,
    /// <c>StateLog.LastOrDefault() ?? Current.Id</c> (the most recently logged move, newest last, or the
    /// current node when nothing has been logged). Like the native method it writes no state-log entry.
    /// </summary>
    public static void ForceStunnedMove(ref Exec x, byte owner, int nextNode)
    {
        MonsterRules rules = MonsterRules(ref x, owner);
        ref Enemy enemy = ref x.B.Enemy(owner);
        if (nextNode < 0)
            nextNode = DefaultStunnedFollowUp(enemy, rules);
        if ((uint)nextNode >= (uint)rules.Nodes.Length)
            throw new ArgumentOutOfRangeException(nameof(nextNode), nextNode, "STUNNED 后继节点越界。");
        if (nextNode == rules.StunnedNode)
            throw new UnsupportedException("STUNNED 的后继不能是合成 STUNNED 节点。");
        if (nextNode == rules.ReviveNode)
            throw new UnsupportedException("STUNNED 的后继不能是合成 REVIVE_MOVE 节点。");
        // A forced STUNNED is always a new MoveState object, so after this enemy already performed the
        // round the native must-perform-once check refuses to advance (MonsterAi.cs:122-126).
        if ((enemy.Flags & (EnemyFlags.PerformedMoveThisRound | EnemyFlags.ActingThisRound)) != 0)
            enemy.Flags |= EnemyFlags.ForcedMustPerformAfterMove;
        enemy.ForcedMove = checked((byte)nextNode);
        enemy.Move = checked((byte)rules.StunnedNode);
        enemy.Flags &= ~EnemyFlags.NeedsInitialRoll;
    }

    /// <summary>
    /// <c>FlutterPower.AfterDamageReceived</c>: when the last stack is consumed, the owner is stunned
    /// with the next state of its most recently logged move (<c>StateLog.Last().GetNextState</c>),
    /// not the logged move itself. A follow-up that is not a direct move node (a conditional or random
    /// branch) is not modeled and rejects explicitly.
    /// </summary>
    public static void ForceStunnedAfterLastMove(ref Exec x, byte owner)
    {
        MonsterRules rules = MonsterRules(ref x, owner);
        ref readonly Enemy enemy = ref x.B.Enemy(owner);
        int recent = Math.Min((int)enemy.LoggedMoves, Limits.EnemyRecentMoves);
        if (recent == 0)
            throw new UnsupportedException("振翅眩晕缺少最近行动记录。");
        MonsterNode last = rules.Nodes[enemy.RecentMoves[recent - 1]];
        if (last.Kind != MonsterNodeKind.Move || last.FollowUp < 0)
            throw new UnsupportedException($"振翅眩晕的后继 {last.NativeId} 不是直接行动节点。");
        int next = last.FollowUp;
        if (next == rules.StunnedNode || next == rules.ReviveNode)
            throw new UnsupportedException("振翅眩晕的后继是合成节点。");
        if (rules.Nodes[next].Kind != MonsterNodeKind.Move)
            throw new UnsupportedException($"振翅眩晕的后继 {rules.Nodes[next].NativeId} 不是直接行动节点。");
        ForceStunnedMove(ref x, owner, next);
    }

    private static int DefaultStunnedFollowUp(in Enemy enemy, in MonsterRules rules)
    {
        int recent = Math.Min((int)enemy.LoggedMoves, Limits.EnemyRecentMoves);
        int next = recent > 0 ? enemy.RecentMoves[recent - 1] : enemy.Move;
        if (next == rules.StunnedNode)
            throw new UnsupportedException("STUNNED 的缺省后继仍是合成 STUNNED 节点。");
        if (next == rules.ReviveNode)
            throw new UnsupportedException("STUNNED 的缺省后继是合成 REVIVE_MOVE 节点。");
        return next;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.PrepareMonsterMoveForNextRound</c> (<c>MonsterAi.cs:102-128</c>)
    /// for every roster enemy.
    /// </summary>
    public static void PrepareMonsterMovesForNextRound(ref Exec x)
    {
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = EnemyOwner(x.B.Roster[position]);
            ref Enemy enemy = ref x.B.Enemy(owner);
            bool forcedAfterMove = (enemy.Flags & EnemyFlags.ForcedMustPerformAfterMove) != 0;
            enemy.Flags &= ~EnemyFlags.ForcedMustPerformAfterMove;
            if (!CanPerformMonsterMove(enemy))
                continue;
            if ((enemy.Flags & EnemyFlags.NeedsInitialRoll) != 0)
            {
                ResolveMonsterState(ref x, owner, MonsterRules(ref x, owner).InitialNode);
                x.B.Enemy(owner).Flags &= ~EnemyFlags.NeedsInitialRoll;
                continue;
            }
            if ((enemy.Flags & EnemyFlags.PerformedMoveThisRound) == 0)
                continue;
            MonsterRules rules = MonsterRules(ref x, owner);
            if (enemy.Move == rules.StunnedNode)
            {
                // Native checks STUNNED first, then the must-perform-once gate (MonsterAi.cs:120-126).
                if ((enemy.Flags & EnemyFlags.SkipNextMove) != 0)
                    continue;
                if (forcedAfterMove)
                    continue;
                int next = enemy.ForcedMove;
                if (next == byte.MaxValue)
                    throw new UnsupportedException("STUNNED 缺少动态后继。");
                if (next == rules.StunnedNode)
                    throw new UnsupportedException("STUNNED 的动态后继是合成 STUNNED 节点。");
                if (next == rules.ReviveNode)
                    throw new UnsupportedException("STUNNED 的动态后继是合成 REVIVE_MOVE 节点。");
                enemy.ForcedMove = byte.MaxValue;
                ResolveMonsterState(ref x, owner, next);
                continue;
            }
            if (enemy.Move == rules.ReviveNode)
            {
                // SimulatedCombatState.BeginIllusionRevive builds a MoveState with
                // MustPerformOnceBeforeTransitioning and a dynamic FollowUpStateId; the native
                // skip-next-move check only covers STUNNED.
                if (forcedAfterMove)
                    continue;
                int next = enemy.ForcedMove;
                if (next == byte.MaxValue)
                    throw new UnsupportedException("REVIVE_MOVE 缺少动态后继。");
                if (next == rules.StunnedNode || next == rules.ReviveNode)
                    throw new UnsupportedException("REVIVE_MOVE 的动态后继是合成节点。");
                enemy.ForcedMove = byte.MaxValue;
                ResolveMonsterState(ref x, owner, next);
                continue;
            }
            MonsterNode current = rules.Nodes[enemy.Move];
            if (current.MustPerformOnceBeforeTransitioning && forcedAfterMove)
                continue;
            if (current.FollowUp < 0)
                throw new UnsupportedException($"行动 {current.NativeId} 没有后继状态。");
            ResolveMonsterState(ref x, owner, current.FollowUp);
        }
    }

    /// <summary>Mirrors <c>SimulatedCombatState.CanPerformMonsterMove</c>.</summary>
    public static bool CanPerformMonsterMove(in Enemy enemy)
        => enemy.Creature.IsAlive || enemy.DeathPhase == DeathPhase.Reviving;

    /// <summary>
    /// Walks the compiled state machine from <paramref name="start"/> to a move node, mirroring the loop of
    /// <c>BranchMonsterAi.Advance</c> / <c>RollInitial</c>, and logs that move.
    /// </summary>
    private static void ResolveMonsterState(ref Exec x, byte owner, int start)
    {
        MonsterRules rules = MonsterRules(ref x, owner);
        MonsterDef def = Registry.Monster(x.B.Enemy(owner).MonsterId);
        int state = start;
        for (int guard = 0; guard < MonsterAdvanceGuard; guard++)
        {
            MonsterNode node = rules.Nodes[state];
            switch (node.Kind)
            {
                case MonsterNodeKind.Move:
                    ref Enemy enemy = ref x.B.Enemy(owner);
                    enemy.Move = checked((byte)state);
                    // A resolved node is a native state; the dynamic STUNNED follow-up no longer applies.
                    enemy.ForcedMove = byte.MaxValue;
                    LogMonsterMove(ref enemy, state);
                    return;
                case MonsterNodeKind.RandomBranch:
                    state = PickRandomBranch(ref x, owner, rules, state, def);
                    break;
                case MonsterNodeKind.ConditionalBranch:
                    int special = def.ResolveConditional(ref x, owner, state);
                    state = special >= 0 ? special : node.ConditionalTarget >= 0
                        ? node.ConditionalTarget
                        : throw new UnsupportedException($"条件分支 {node.NativeId} 没有根选择。");
                    break;
                default:
                    throw new UnsupportedException($"未知的怪物状态类型 {node.Kind}。");
            }
        }
        throw new InvalidOperationException("怪物行动状态机未能在 32 步内落到行动节点。");
    }

    /// <summary>Mirrors <c>MonsterRandomBranchResolver.Pick</c>, including its float arithmetic order.</summary>
    private static int PickRandomBranch(ref Exec x, byte owner, MonsterRules rules, int branch, MonsterDef def)
    {
        RandomBranchOption[] options = rules.Nodes[branch].Options;
        Span<float> weights = stackalloc float[options.Length];
        float total = 0f;
        for (int index = 0; index < options.Length; index++)
        {
            float weight = RepeatWeight(x.B.Enemy(owner), rules, options[index])
                * def.BranchWeight(ref x, owner, branch, index, options[index].BaseWeight);
            weights[index] = weight;
            total += weight;
        }
        float roll = x.B.RngStream(RngStream.MonsterAi).NextFloat(total);
        for (int index = 0; index < options.Length; index++)
        {
            roll -= weights[index];
            if (roll <= 0f)
                return options[index].Target;
        }
        for (int index = options.Length - 1; index >= 0; index--)
        {
            if (weights[index] > 0f)
                return options[index].Target;
        }
        throw new InvalidOperationException($"随机分支 {rules.Nodes[branch].NativeId} 没有可选状态。");
    }

    private static float RepeatWeight(in Enemy enemy, MonsterRules rules, in RandomBranchOption option)
    {
        int target = option.Target;
        bool targetIsMove = rules.Nodes[target].Kind == MonsterNodeKind.Move;
        int recent = Math.Min((int)enemy.LoggedMoves, Limits.EnemyRecentMoves);
        if (option.RepeatType == MoveRepeatType.UseOnlyOnce && targetIsMove && (enemy.EverUsedMoves & (1UL << target)) != 0)
            return 0f;
        if (option.RepeatType == MoveRepeatType.CannotRepeat && recent > 0 && enemy.RecentMoves[recent - 1] == target)
            return 0f;
        if (option.RepeatType == MoveRepeatType.CanRepeatXTimes)
        {
            int consecutive = 0;
            for (int index = recent - 1; index >= 0 && enemy.RecentMoves[index] == target; index--)
                consecutive++;
            if (consecutive >= recent && enemy.LoggedMoves > Limits.EnemyRecentMoves && consecutive < option.MaxTimes)
                throw new UnsupportedException("连续行动次数超出近期日志容量。");
            if (consecutive >= option.MaxTimes)
                return 0f;
        }
        if (option.Cooldown > 0)
        {
            if (option.Cooldown > Limits.EnemyRecentMoves)
                throw new UnsupportedException("怪物行动冷却超出近期日志容量。");
            int window = Math.Min(option.Cooldown, recent);
            for (int index = recent - window; index < recent; index++)
            {
                if (enemy.RecentMoves[index] == target)
                    return 0f;
            }
        }
        return 1f;
    }

    public static void LogMonsterMove(ref Enemy enemy, int moveNode)
    {
        if ((uint)moveNode >= 64)
            throw new CapacityException("Enemy.MoveNodes", 64);
        enemy.EverUsedMoves |= 1UL << moveNode;
        int recent = Math.Min((int)enemy.LoggedMoves, Limits.EnemyRecentMoves);
        if (recent == Limits.EnemyRecentMoves)
        {
            for (int index = 1; index < Limits.EnemyRecentMoves; index++)
                enemy.RecentMoves[index - 1] = enemy.RecentMoves[index];
            enemy.RecentMoves[Limits.EnemyRecentMoves - 1] = (byte)moveNode;
        }
        else
        {
            enemy.RecentMoves[recent] = (byte)moveNode;
        }
        if (enemy.LoggedMoves < byte.MaxValue)
            enemy.LoggedMoves++;
    }
}
