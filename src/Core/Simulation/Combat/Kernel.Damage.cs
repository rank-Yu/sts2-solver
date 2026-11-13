namespace Sts2Solver.Simulation.Combat;

/// <summary>Native <c>DamageResult</c> fields the pipeline uses.</summary>
public struct DamageResult
{
    public byte Receiver;
    public ValueProps Props;
    public bool WasTargetKilled;
    public bool WasBlockBroken;
    public bool WasFullyBlocked;
    public int UnblockedDamage;
    public int OverkillDamage;
    public int BlockedDamage;

    /// <summary><c>AfterDamageReceivedMirrorContext.Source</c>: the played card that dealt the damage, if any.</summary>
    public bool HasCardSource;
    public Card CardSource;
}

public enum AttackTargeting : byte
{
    Single = 0,
    AllOpponents = 1,
    Random = 2,
}

public static partial class Kernel
{
    // Every target may redirect once (Osty) and then spill over to the original receiver, so one
    // Damage call over all enemies plus the player can produce two results per target.
    private const int MaxDamageResults = (Limits.Enemies + 1) * 2;

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.ExecuteAttack</c> for a card attack.
    /// </summary>
    public static void CardAttack(ref Exec x, decimal damagePerHit, int hitCount, AttackTargeting targeting,
        byte singleTarget, ValueProps props = ValueProps.Move, byte attacker = Limits.PlayerOwner,
        bool recordAttack = true, bool endAttack = true)
    {
        // Single-target attacks read Creature(singleTarget); a missing target is an unsupported
        // caller assumption, not an index error.
        if (targeting == AttackTargeting.Single && singleTarget == Limits.NoOwner)
            throw new UnsupportedException("单体攻击需要目标 owner。");
        if (IsOverOrEnding(x.B))
            return;
        // A card attack from Osty (<c>AttackCommand.FromOsty</c>) uses Osty as the attacker, so strength,
        // weak and the attack history apply to the pet. The existing engine returns early while the
        // attacker is dead.
        if (x.B.Creature(attacker).IsDead)
            return;
        x.AttackUnblockedHits = 0;
        x.AttackSuckHitLists = 0;
        // BeginAttackContext: BeforeAttack iterates the combat listeners behind the ending gate
        // (HookMirrors.BeforeAttack). ModifyAttackHitCount has no supported listeners yet.
        BeforeAttackHook before = new() { Attacker = attacker, Props = props };
        HookDispatch.Dispatch(ref x, ref before, gatedByEnding: true);
        if (x.NeedsChoice)
            return;
        BeginAttack(ref x, props, hasCardSource: true);
        Span<byte> targets = stackalloc byte[Limits.Enemies + 1];
        for (int hit = 0; hit < hitCount; hit++)
        {
            if (x.B.Creature(attacker).IsDead)
                break;
            int count = 0;
            if (targeting == AttackTargeting.Single)
            {
                if (x.B.Creature(singleTarget).IsAlive)
                    targets[count++] = singleTarget;
            }
            else
            {
                for (int position = 0; position < x.B.RosterCount; position++)
                {
                    byte owner = EnemyOwner(x.B.Roster[position]);
                    if (x.B.Creature(owner).IsAlive)
                        targets[count++] = owner;
                }
            }
            if (count == 0)
                break;
            if (targeting == AttackTargeting.Random)
            {
                // DamageCmd's TargetingRandomOpponents defaults to allowDuplicates: true; every
                // vanilla random-target attack uses that default, so each hit picks among all living
                // enemies and consumes one CombatTargets draw.
                targets[0] = targets[x.B.RngStream(RngStream.CombatTargets).NextInt(0, count)];
                count = 1;
            }
            Damage(ref x, targets[..count], damagePerHit, props, attacker, hasCardSource: true, x.Card);
            if (x.NeedsChoice)
                return;
        }
        // EndAttackContext(completed): RecordCreatureAttacked, then AfterAttack iterates the combat
        // listeners without the ending gate. The per-creature attack counter is keyed by the attacker
        // (<c>SimulatedCombatState.RecordCreatureAttacked</c>): the player's is PlayerAttacksThisTurn and
        // Osty's is OstyAttacksThisTurn (Rattle).
        // AttackCommand.Execute records CreatureAttacked; an AttackContext (EchoingSlash,
        // Omnislice) only calls AfterAttack on dispose and leaves these counters unchanged.
        if (recordAttack)
        {
            if (attacker == Limits.PlayerOwner)
                x.B.AddCounter(Counter.PlayerAttacksThisTurn, 1);
            else if (attacker == Limits.OstyOwner)
                x.B.AddCounter(Counter.OstyAttacksThisTurn, 1);
        }
        if (endAttack)
            EndAttackContext(ref x, attacker);
    }

    /// <summary>
    /// One attack context with repeated damage waves (EchoingSlash): a wave hits every living enemy,
    /// then the roster-only death pass runs, and every target killed adds one more wave. This mirrors
    /// <c>CardOnPlaySupport.Batch042.ApplyEchoingSlash</c> (one <c>BeginAttackContext</c> around all
    /// waves and one <c>EndAttackContext</c> at the end).
    /// </summary>
    public static void CardAttackRepeating(ref Exec x, int damageVar, int initialWaves,
        ValueProps props = ValueProps.Move, byte attacker = Limits.PlayerOwner)
    {
        if (IsOverOrEnding(x.B))
            return;
        if (x.B.Creature(attacker).IsDead)
            return;
        x.AttackUnblockedHits = 0;
        BeforeAttackHook before = new() { Attacker = attacker, Props = props };
        HookDispatch.Dispatch(ref x, ref before, gatedByEnding: true);
        if (x.NeedsChoice)
            return;
        BeginAttack(ref x, props, hasCardSource: true);
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int waves = initialWaves;
        int totalKills = 0;
        int wave = 0;
        while (waves-- > 0)
        {
            if (x.B.Creature(attacker).IsDead)
                break;
            // EchoingSlash.OnPlay reads base.DynamicVars.Damage on every wave. A mid-attack upgrade
            // (the last DampenPower caster dying to this wave restores the card's original level)
            // therefore changes later waves. Sts2Solver plays from the copy in Exec.Card; re-sync it
            // and its rules table before each wave after the first.
            if (wave++ > 0)
                SyncCurrentPlayCard(ref x);
            decimal damagePerHit = CardEffects.Var(x, damageVar);
            int count = 0;
            for (int position = 0; position < x.B.RosterCount; position++)
            {
                byte owner = EnemyOwner(x.B.Roster[position]);
                if (x.B.Creature(owner).IsAlive)
                    targets[count++] = owner;
            }
            if (count == 0)
                break;
            Damage(ref x, targets[..count], damagePerHit, props, attacker, hasCardSource: true, x.Card);
            if (x.NeedsChoice)
                return;
            int kills = x.LastDamageKills;
            totalKills = checked(totalKills + kills);
            waves += kills;
            // ApplyEchoingSlash passes combat.Enemies (current roster) to the death pass.
            ApplyEnemyDeathPowersInRoster(ref x);
            if (x.NeedsChoice)
                return;
            if (totalKills > Limits.Enemies)
                throw new CapacityException("AttackWaves", Limits.Enemies);
        }
        // EchoingSlash uses one AttackContext around every wave (CardOnPlaySupport.Batch042), so its
        // History.CreatureAttacked is never recorded; only AfterAttack fires on context dispose.
        EndAttackContext(ref x, attacker);
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.Damage</c>. Returns whether any result was fully blocked,
    /// which the monster-move path reads for the Imbalanced stun (<c>MonsterMoveSemantics</c> keeps the
    /// same per-hit flag).
    /// </summary>
    public static bool Damage(ref Exec x, scoped ReadOnlySpan<byte> targets, decimal amount, ValueProps props,
        byte dealer, bool hasCardSource, in Card cardSource, bool capOnly = false)
    {
        if (dealer != Limits.NoOwner && x.B.Creature(dealer).IsDead || targets.Length == 0)
            return false;
        x.DamageCallDepth++;
        try
        {
            Span<DamageResult> results = stackalloc DamageResult[MaxDamageResults];
            int count = 0;
            x.LastDamageKills = 0;
            foreach (byte target in targets)
            {
                if (!TryDamageTarget(ref x, target, amount, props, dealer, hasCardSource, cardSource, results, ref count, capOnly))
                    return false;
            }
            // Capture the damage before the result hooks run: a hook may deal nested damage and overwrite
            // the "last damage" fields. Fisticuffs sums the whole card play; Omnislice reads this call's
            // amount (TotalDamage + OverkillDamage) for the copied hit.
            int total = 0;
            int overkill = 0;
            int kills = 0;
            for (int index = 0; index < count; index++)
            {
                // Native DamageResult.TotalDamage is BlockedDamage + UnblockedDamage; OverkillDamage is
                // the part beyond the target's HP. Fisticuffs sums TotalDamage + OverkillDamage.
                total += results[index].BlockedDamage + results[index].UnblockedDamage;
                overkill += results[index].OverkillDamage;
                if (hasCardSource)
                {
                    x.CardDamageTotal += results[index].BlockedDamage + results[index].UnblockedDamage
                        + results[index].OverkillDamage;
                    if (results[index].WasTargetKilled)
                    {
                        kills++;
                        x.CardKilledTarget = true;
                    }
                }
            }
            x.LastDamageTotal = total;
            x.LastDamageOverkill = overkill;
            x.LastDamageKills = kills;
            // The outermost Damage call belongs to the current attack context. Nested damage from a
            // result hook is a separate native Damage call and must not become a hit of the attack.
            if (x.DamageCallDepth == 1)
            {
                RecordAttackResults(ref x, results[..count]);
                bool playerSideUnblocked = false;
                for (int index = 0; index < count; index++)
                {
                    if ((results[index].Receiver == Limits.PlayerOwner
                         || results[index].Receiver == Limits.OstyOwner)
                        && results[index].UnblockedDamage > 0)
                        playerSideUnblocked = true;
                }
                // SuckPower.AfterAttack removes the player result paired with a pet hit, but keeps
                // the pet hit itself; one native hit list therefore counts once when either the
                // player or Osty took unblocked damage. A redirected hit with overkill spilling
                // back to the player is still exactly one list, not two.
                if (playerSideUnblocked)
                    x.AttackSuckHitLists++;
            }
            ProcessDamageResults(ref x, results[..count], dealer);
            // Result hooks can deal damage recursively. Return this call's results to the caller
            // (BlossomShot, RainShot, Omnislice), rather than the last retaliation's results.
            x.LastDamageTotal = total;
            x.LastDamageOverkill = overkill;
            x.LastDamageKills = kills;
            for (int index = 0; index < count; index++)
            {
                if (results[index].Receiver == Limits.PlayerOwner && results[index].UnblockedDamage > 0)
                    x.AttackUnblockedHits++;
            }
            for (int index = 0; index < count; index++)
            {
                if (results[index].WasFullyBlocked)
                    return true;
            }
            return false;
        }
        finally
        {
            x.DamageCallDepth--;
        }
    }

    /// <summary>
    /// Native <c>AttackCommand</c> results: the first damage result per enemy in the attack context.
    /// SkittishPower.AfterAttack reads the first result for its owner.
    /// </summary>
    private static void RecordAttackResults(ref Exec x, scoped ReadOnlySpan<DamageResult> results)
    {
        foreach (DamageResult result in results)
        {
            if (!IsEnemyOwner(result.Receiver))
                continue;
            ulong bit = 1UL << (result.Receiver - Limits.FirstEnemyOwner);
            if ((x.AttackFirstResultSeen & bit) != 0)
                continue;
            x.AttackFirstResultSeen |= bit;
            if (result.UnblockedDamage != 0)
                x.AttackFirstResultUnblocked |= bit;
        }
    }

    /// <summary>BeginAttackContext: the props and card-source fact every AfterAttack listener reads.</summary>
    public static void BeginAttack(ref Exec x, ValueProps props, bool hasCardSource)
    {
        x.AttackProps = props;
        x.AttackHasCardSource = hasCardSource;
        x.AttackFirstResultSeen = 0;
        x.AttackFirstResultUnblocked = 0;
        x.AttackSuckHitLists = 0;
    }

    /// <summary>
    /// EndAttackContext(completed): AfterAttack over the listeners without the ending gate, then the
    /// attack-only facts are cleared. Omnislice opens one context around its original hit and the
    /// copied hits and ends it here.
    /// </summary>
    public static void EndAttackContext(ref Exec x, byte attacker)
    {
        AfterAttackHook after = new() { Attacker = attacker };
        HookDispatch.Dispatch(ref x, ref after, gatedByEnding: false);
        x.AttackProps = ValueProps.None;
        x.AttackHasCardSource = false;
        x.AttackFirstResultSeen = 0;
        x.AttackFirstResultUnblocked = 0;
        x.AttackSuckHitLists = 0;
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.TryDamageTarget</c>, including the <c>ModifyUnblockedDamageTarget</c>
    /// redirect (Osty's <c>DieForYouPower</c>) and its overkill spill-over to the original receiver.
    /// </summary>
    private static bool TryDamageTarget(ref Exec x, byte target, decimal amount, ValueProps props, byte dealer,
        bool hasCardSource, in Card cardSource, scoped Span<DamageResult> results, ref int count, bool capOnly)
    {
        if (x.B.Creature(target).IsDead)
            return true;
        decimal modified = HookDispatch.ModifyDamage(ref x, target, dealer, amount, props, hasCardSource, cardSource, capOnly);
        if (x.NeedsChoice)
            return false;
        // HookMirrors.BeforeDamageReceived iterates the run listeners without the ending gate; deck cards
        // outside combat with this hook are rejected at admission, so the combat listeners are the list.
        BeforeDamageReceivedHook before = new()
        {
            Target = target, Amount = modified, Props = props, Dealer = dealer, HasCardSource = hasCardSource, CardSource = cardSource,
        };
        HookDispatch.Dispatch(ref x, ref before, gatedByEnding: false);
        if (x.NeedsChoice)
            return false;
        // The block holder is the pet owner (CreatureCmd.UseBlockTarget): Osty's incoming damage spends
        // the player's block.
        byte blockTarget = target == Limits.OstyOwner ? Limits.PlayerOwner : target;
        decimal blocked = DamageBlock(ref x.B.Creature(blockTarget), modified, props);
        decimal unblocked = Math.Max(modified - blocked, 0m);
        // ModifyHpLost's before-Osty phase is evaluated on the original target, then
        // Hook.ModifyUnblockedDamageTarget chooses the HP-loss receiver, then the after-Osty phase runs
        // on that receiver. A listener that changed the truncated amount is notified afterwards. The
        // redirect query itself is dispatched even without HP-loss hooks (DieForYouPower has none).
        HpLossContext loss = new(target, props, dealer, hasCardSource, cardSource);
        Span<Listener> changed = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R) * 2];
        bool hasHpLossHooks = (Registry.AnyHooks & HpLossHooks) != 0;
        if (hasHpLossHooks)
        {
            unblocked = HookDispatch.ModifyHpLost(ref x, loss, unblocked, afterOsty: false, changed, out _);
            if (x.NeedsChoice)
                return false;
        }
        byte redirect = HookDispatch.ModifyUnblockedDamageTarget(ref x, target, unblocked, props, dealer);
        if (x.NeedsChoice)
            return false;
        if (hasHpLossHooks && (redirect == target
                || (Registry.AnyHooks & AfterOstyHooks) != 0))
        {
            HpLossContext redirectedLoss = new(redirect, props, dealer, hasCardSource, cardSource);
            unblocked = HookDispatch.ModifyHpLost(ref x, redirectedLoss, unblocked, afterOsty: true, changed, out int changedCount);
            HookDispatch.AfterModifyingHpLostAfterOsty(ref x, changed[..changedCount]);
            if (x.NeedsChoice)
                return false;
        }
        if (redirect != target)
            return RecordRedirectedDamage(ref x, target, redirect, blocked, unblocked, props, dealer,
                hasCardSource, cardSource, results, ref count);
        target = redirect;
        ref Creature creature = ref x.B.Creature(target);
        DamageResult result = LoseHp(ref creature, target, unblocked, props);
        result.HasCardSource = hasCardSource;
        result.CardSource = cardSource;
        result.BlockedDamage = (int)blocked;
        result.WasBlockBroken = creature.Block <= 0 && blocked > 0m;
        result.WasFullyBlocked = (props & ValueProps.Unblockable) == 0
            && (blocked > 0m || creature.Block > 0)
            && (int)unblocked == 0;
        RecordDamageReceived(ref x.B, target, dealer, result);
        results[count++] = result;
        return true;
    }

    /// <summary>
    /// The two-result branch of <c>CombatPredictionSimulator.TryDamageTarget</c> when
    /// <c>ModifyUnblockedDamageTarget</c> moved the HP loss off the original receiver: the redirected
    /// receiver loses <paramref name="unblocked"/>, then the original receiver loses the modified overkill.
    /// </summary>
    private static bool RecordRedirectedDamage(ref Exec x, byte originalTarget, byte redirect,
        decimal blocked, decimal unblocked, ValueProps props, byte dealer, bool hasCardSource,
        in Card cardSource, scoped Span<DamageResult> results, ref int count)
    {
        ref Creature redirected = ref x.B.Creature(redirect);
        DamageResult redirectedResult = LoseHp(ref redirected, redirect, unblocked, props);
        redirectedResult.HasCardSource = hasCardSource;
        redirectedResult.CardSource = cardSource;
        redirectedResult.BlockedDamage = (int)blocked;
        ref Creature original = ref x.B.Creature(originalTarget);
        redirectedResult.WasBlockBroken = original.Block <= 0 && blocked > 0m;
        redirectedResult.WasFullyBlocked = (props & ValueProps.Unblockable) == 0
            && (blocked > 0m || original.Block > 0)
            && (int)unblocked == 0;
        RecordDamageReceived(ref x.B, redirect, dealer, redirectedResult);
        results[count++] = redirectedResult;

        decimal overflow = redirectedResult.OverkillDamage;
        if ((Registry.AnyHooks & HpLossHooks) != 0)
        {
            Span<Listener> changed = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R) * 2];
            HpLossContext loss = new(originalTarget, props, dealer, hasCardSource, cardSource);
            overflow = HookDispatch.ModifyHpLost(ref x, loss, overflow, afterOsty: true, changed, out int changedCount);
            HookDispatch.AfterModifyingHpLostAfterOsty(ref x, changed[..changedCount]);
            if (x.NeedsChoice)
                return false;
        }
        DamageResult originalResult = overflow > 0m
            ? LoseHp(ref original, originalTarget, overflow, props)
            : new DamageResult { Receiver = originalTarget, Props = props };
        originalResult.HasCardSource = hasCardSource;
        originalResult.CardSource = cardSource;
        originalResult.BlockedDamage = (int)blocked;
        originalResult.WasBlockBroken = redirectedResult.WasBlockBroken;
        originalResult.WasFullyBlocked = redirectedResult.WasFullyBlocked;
        RecordDamageReceived(ref x.B, originalTarget, dealer, originalResult);
        results[count++] = originalResult;
        return true;
    }

    private const HookMask HpLossHooks = HookMask.ModifyHpLostBeforeOsty | HookMask.ModifyHpLostBeforeOstyLate
        | HookMask.ModifyHpLostAfterOsty | HookMask.ModifyHpLostAfterOstyLate | HookMask.AfterModifyingHpLostAfterOsty;

    private const HookMask AfterOstyHooks = HookMask.ModifyHpLostAfterOsty | HookMask.ModifyHpLostAfterOstyLate
        | HookMask.AfterModifyingHpLostAfterOsty;

    /// <summary>Mirrors <c>CombatPredictionSimulator.ProcessDamageResults</c>.</summary>
    private static void ProcessDamageResults(ref Exec x, scoped ReadOnlySpan<DamageResult> results, byte dealer)
    {
        Span<byte> killed = stackalloc byte[MaxDamageResults];
        int killedCount = 0;
        foreach (DamageResult result in results)
        {
            if (result.WasBlockBroken)
            {
                AfterBlockBrokenHook broken = new() { Target = result.Receiver, Breaker = dealer };
                HookDispatch.Dispatch(ref x, ref broken, gatedByEnding: false);
                if (x.NeedsChoice)
                    return;
            }
            if (result.UnblockedDamage > 0)
            {
                AfterCurrentHpChanged(ref x, result.Receiver, -result.UnblockedDamage);
                if (x.NeedsChoice)
                    return;
            }
            AfterDamageGivenHook given = new() { Result = result, Dealer = dealer };
            HookDispatch.Dispatch(ref x, ref given, gatedByEnding: false);
            if (x.NeedsChoice)
                return;
            if (!result.WasTargetKilled || !x.B.Creature(result.Receiver).IsDead)
            {
                AfterDamageReceivedHook received = new() { Result = result, Dealer = dealer };
                HookDispatch.Dispatch(ref x, ref received, gatedByEnding: false);
                if (x.NeedsChoice)
                    return;
                AfterDamageReceivedLateHook late = new() { Result = result, Dealer = dealer };
                HookDispatch.Dispatch(ref x, ref late, gatedByEnding: false);
                if (x.NeedsChoice)
                    return;
            }
            else
            {
                killed[killedCount++] = result.Receiver;
            }
        }
        if (killedCount > 0)
            Kill(ref x, killed[..killedCount], force: false, results[0].HasCardSource, results[0].CardSource);
        else if (x.B.Player.IsDead)
            LoseCombat(ref x.B);
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.Kill(IReadOnlyList&lt;Creature&gt;, bool)</c>.</summary>
    public static void Kill(ref Exec x, scoped ReadOnlySpan<byte> creatures, bool force,
        bool hasCardSource = false, Card cardSource = default)
    {
        foreach (byte creature in creatures)
        {
            KillWithoutCheckingWinCondition(ref x, creature, force, hasCardSource, cardSource);
            if (x.NeedsChoice)
                return;
        }
        if (x.B.Player.IsDead)
            LoseCombat(ref x.B);
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.KillWithoutCheckingWinCondition</c>: lose all HP and
    /// <c>AfterCurrentHpChanged</c>, <c>BeforeDeath</c>, then <c>ShouldDie</c> (normal and Late; skipped when forced or
    /// at zero max HP). A death removes the enemy from the roster unless a power prevents it, runs <c>AfterDeath</c>,
    /// and kills the remaining secondary teammates of a primary enemy; a prevented death runs <c>AfterDeath</c> and
    /// the preventer's <c>AfterPreventingDeath</c>, and retries while the creature is still dead.
    /// </summary>
    private static void KillWithoutCheckingWinCondition(ref Exec x, byte owner, bool force,
        bool hasCardSource, in Card cardSource, int recursion = 0)
    {
        int currentHp = x.B.Creature(owner).CurrentHp;
        if (currentHp > 0)
        {
            LoseHp(ref x.B.Creature(owner), owner, currentHp, ValueProps.Unblockable | ValueProps.Unpowered);
            AfterCurrentHpChanged(ref x, owner, -currentHp);
            if (x.NeedsChoice)
                return;
        }
        BeforeDeathHook before = new() { Creature = owner };
        HookDispatch.Dispatch(ref x, ref before, gatedByEnding: false);
        if (x.NeedsChoice)
            return;
        bool shouldDie = force || x.B.Creature(owner).MaxHp <= 0;
        Listener preventer = default;
        if (!shouldDie)
            shouldDie = ShouldDie(ref x, owner, out preventer);
        if (x.NeedsChoice)
            return;
        if (!shouldDie)
        {
            AfterDeathHook prevented = new() { Creature = owner, WasRemovalPrevented = true, HasCardSource = hasCardSource, CardSource = cardSource };
            HookDispatch.Dispatch(ref x, ref prevented, gatedByEnding: false);
            if (x.NeedsChoice)
                return;
            HookDispatch.Definition(x.B, x.R, preventer).AfterPreventingDeath(ref x, preventer, owner);
            if (x.NeedsChoice)
                return;
            if (x.B.Creature(owner).IsDead)
            {
                if (recursion >= 10)
                    throw new InvalidOperationException("死亡被连续阻止十次后，生物仍未复活。");
                KillWithoutCheckingWinCondition(ref x, owner, force, hasCardSource, cardSource, recursion + 1);
            }
            return;
        }
        bool remove = ShouldRemoveAfterDeath(x.B, owner);
        // HookMirrors.AfterDeath iterates every run hook listener on any death; a listener whose
        // dispatch is inferred/unsupported records a MethodNotMirrored history entry even when the
        // compensation path applies the same state. The flag is transient (pure input only).
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            if (x.R.PowerRecordsDeathHistoryRisk(x.B.Powers[index].Id))
            {
                x.HistoryRisk = true;
                break;
            }
        }
        AfterDeathHook after = new() { Creature = owner, WasRemovalPrevented = false, HasCardSource = hasCardSource, CardSource = cardSource };
        HookDispatch.Dispatch(ref x, ref after, gatedByEnding: false);
        if (x.NeedsChoice)
            return;
        if (IsEnemyOwner(owner))
        {
            // Native Kill runs every AfterDeath listener inside the kill, before it reads the teammates
            // and removes the creature from combat. The death powers (Surprise and Infested spawns,
            // Possess returning the stolen Strength/Dexterity, Reattach, Adaptable) therefore act before
            // the next hit of the same attack picks its target or computes its damage.
            bool deathPass = BeginEnemyDeath(ref x, owner);
            if (x.NeedsChoice)
                return;
            Span<byte> aliveTeammates = stackalloc byte[Limits.Enemies];
            int aliveCount = 0;
            for (int position = 0; position < x.B.RosterCount; position++)
            {
                byte teammate = EnemyOwner(x.B.Roster[position]);
                if (teammate != owner && x.B.Creature(teammate).IsAlive)
                    aliveTeammates[aliveCount++] = teammate;
            }
            if (remove && InRoster(x.B, owner))
                RemoveFromRoster(ref x.B, owner);
            // ICombatPredictionCreatureSemantics.IsPrimaryEnemy reads EffectivePowers, which hold only the powers of
            // roster creatures: once removed, the dead enemy has no secondary-making power there. Native
            // reads IsPrimaryEnemy before RemoveAllPowersAfterDeath.
            bool isPrimary = !InRoster(x.B, owner) || IsPrimaryEnemy(x.B, owner);
            if (deathPass)
            {
                CompleteEnemyDeath(ref x, owner);
                if (x.NeedsChoice)
                    return;
            }
            if (isPrimary && aliveCount > 0)
            {
                bool allSecondary = true;
                for (int index = 0; index < aliveCount; index++)
                    allSecondary &= !IsPrimaryEnemy(x.B, aliveTeammates[index]);
                if (allSecondary)
                    Kill(ref x, aliveTeammates[..aliveCount], force: false);
            }
        }
        else if (owner == Limits.PlayerOwner)
        {
            HandlePlayerDeath(ref x);
        }
        else if (owner == Limits.OstyOwner)
        {
            // Native v0.111.0 Commands/CreatureCmd.Kill and Creature.RemoveAllPowersAfterDeath:
            // remove mortal powers from a retained dead pet too.
            // DieForYou survives, while Strength and other temporary combat state do not.
            RemovePowersAfterDeath(ref x, owner);
        }
    }

    /// <summary>Native <c>ShouldDie</c> then <c>ShouldDieLate</c> over the run listeners; the first refusal is the preventer.</summary>
    private static bool ShouldDie(ref Exec x, byte owner, out Listener preventer)
    {
        preventer = default;
        if ((Registry.AnyHooks & (HookMask.ShouldDie | HookMask.ShouldDieLate)) == 0)
            return true;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R)];
        for (int pass = 0; pass < 2; pass++)
        {
            int count = HookDispatch.CollectListeners(x.B, x.R, pass == 0 ? HookMask.ShouldDie : HookMask.ShouldDieLate, listeners);
            for (int index = 0; index < count; index++)
            {
                ListenerDef def = HookDispatch.Definition(x.B, x.R, listeners[index]);
                bool dies = pass == 0 ? def.ShouldDie(ref x, listeners[index], owner) : def.ShouldDieLate(ref x, listeners[index], owner);
                if (!dies)
                {
                    preventer = listeners[index];
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary><c>ICombatPredictionCreatureSemantics.ShouldRemoveAfterDeath</c>: no positive removal-preventing power.</summary>
    private static bool ShouldRemoveAfterDeath(in Battle battle, byte owner)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner == owner && power.Amount > 0 && Registry.Power(power.Id).PreventsRemovalAfterDeath)
                return false;
        }
        return true;
    }

    /// <summary>Native <c>AfterCurrentHpChanged</c> over the run listeners, without the ending gate.</summary>
    internal static void AfterCurrentHpChanged(ref Exec x, byte creature, decimal delta)
    {
        AfterCurrentHpChangedHook changed = new() { Creature = creature, Delta = delta };
        HookDispatch.Dispatch(ref x, ref changed, gatedByEnding: false);
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.HandlePlayerDeath</c> for a single-player combat.</summary>
    private static void HandlePlayerDeath(ref Exec x)
    {
        RemovePowersOfInternal(ref x.B, Limits.PlayerOwner, keepPendingAmountStates: true);
        // CreatureCmd's player-death cleanup calls OrbQueue.Clear(), which also drops the capacity.
        x.B.OrbCapacity = 0;
        x.B.OrbCount = 0;
        for (int index = 0; index < Limits.Orbs; index++)
            x.B.Orbs[index] = default;
        // The native flow kills a still-alive Osty with force after clearing the orbs and before the
        // player's hooks are deactivated (<c>HandlePlayerDeath</c> in CombatPredictionSimulator).
        if ((x.B.Flags & BattleFlags.OstyPresent) != 0 && x.B.Osty.IsAlive)
        {
            Span<byte> osty = stackalloc byte[1];
            osty[0] = Limits.OstyOwner;
            Kill(ref x, osty, force: true);
            if (x.NeedsChoice)
                return;
        }
        x.B.Flags &= ~BattleFlags.PlayerHooksActive;
        // With every player dead the native flow does not remove the cards from combat.
    }

    /// <summary>Mirrors <c>SimCreatureState.DamageBlock</c>.</summary>
    public static decimal DamageBlock(ref Creature creature, decimal amount, ValueProps props)
    {
        decimal blocked = (props & ValueProps.Unblockable) != 0 ? 0m : Math.Min(creature.Block, amount);
        creature.Block -= (int)blocked;
        return blocked;
    }

    /// <summary>
    /// Mirrors <c>CreatureCmd.LoseBlock</c>: remove block without going through the damage pipeline,
    /// then dispatch <c>AfterBlockBroken</c> when the creature had block before and none remains.
    /// </summary>
    public static void LoseBlock(ref Exec x, byte target, decimal amount, byte remover)
    {
        if (IsOverOrEnding(x.B) || x.B.Creature(target).IsDead || amount <= 0m)
            return;
        ref Creature creature = ref x.B.Creature(target);
        int before = creature.Block;
        DamageBlock(ref creature, amount, ValueProps.Move);
        if (before <= 0 || creature.Block > 0)
            return;
        AfterBlockBrokenHook broken = new() { Target = target, Breaker = remover };
        HookDispatch.Dispatch(ref x, ref broken, gatedByEnding: false);
    }

    /// <summary>Mirrors <c>SimCreatureState.LoseHp</c>.</summary>
    public static DamageResult LoseHp(ref Creature creature, byte owner, decimal amount, ValueProps props)
    {
        bool killed = creature.CurrentHp > 0 && amount >= creature.CurrentHp;
        int previous = creature.CurrentHp;
        int damage = (int)Math.Min(amount, 999_999_999m);
        creature.CurrentHp = Math.Max(previous - damage, 0);
        return new DamageResult
        {
            Receiver = owner,
            Props = props,
            UnblockedDamage = previous - creature.CurrentHp,
            WasTargetKilled = killed,
            OverkillDamage = killed ? Math.Max(damage - previous, 0) : 0,
        };
    }

    /// <summary>
    /// Mirrors the parts of <c>SimulatedCombatState.RecordDamageReceived</c> that Sts2Solver tracks: the
    /// powered attack hits the player dealt to each enemy (BeatIntoShape's calculated-forge
    /// multiplier), the lost-HP-this-turn mark of every receiver and the player's cumulative HP loss.
    /// The hit is recorded even when the attack was fully blocked, exactly like the existing engine.
    /// </summary>
    private static void RecordDamageReceived(ref Battle battle, byte receiver, byte dealer,
        in DamageResult result)
    {
        if (result.Props.IsPoweredAttack() && dealer == Limits.PlayerOwner && IsEnemyOwner(receiver))
        {
            ref Enemy enemy = ref battle.Enemies[receiver - Limits.FirstEnemyOwner];
            if (enemy.PoweredAttackHitsThisTurn == ushort.MaxValue)
                throw new UnsupportedException("单个敌人的本回合强化攻击命中数超过 65535，Sts2Solver 无法表示。");
            enemy.PoweredAttackHitsThisTurn++;
        }
        if (result.UnblockedDamage <= 0)
            return;
        battle.Creature(receiver).Flags |= CreatureFlags.LostHpThisTurn;
        if (receiver == Limits.PlayerOwner)
        {
            battle.AddCounter(Counter.CumulativeHpLost, result.UnblockedDamage);
            battle.AddCounter(Counter.PlayerUnblockedDamageEvents, 1);
        }
    }

    /// <summary>
    /// The boundary death pass (<c>CorePowerSupport.ApplyEnemyDeathPowers</c>): completes the death of
    /// any dead enemy not processed yet, then resolves the queued power amount changes. An enemy killed
    /// through <see cref="Kill"/> is already processed inside the kill (<see cref="BeginEnemyDeath"/>,
    /// <see cref="CompleteEnemyDeath"/>), so for it this pass only resolves the queued changes.
    /// </summary>
    public static void ApplyEnemyDeathPowers(ref Exec x) => ApplyEnemyDeathPowersCore(ref x, rosterOnly: false);

    /// <summary>
    /// The boundary death pass over enemies still in the roster (between the waves of Omnislice and
    /// EchoingSlash). Kills are processed inside <see cref="Kill"/>, so a summoning death power
    /// (Infested) has already added its creatures when the next wave reads the hittable enemies.
    /// </summary>
    public static void ApplyEnemyDeathPowersInRoster(ref Exec x) => ApplyEnemyDeathPowersCore(ref x, rosterOnly: true);

    private static void ApplyEnemyDeathPowersCore(ref Exec x, bool rosterOnly)
    {
        Span<byte> newlyDead = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int index = 0; index < x.B.EnemyCount; index++)
        {
            ref Enemy enemy = ref x.B.Enemies[index];
            if (rosterOnly && !InRoster(x.B, EnemyOwner((byte)index)))
                continue;
            if ((enemy.Flags & EnemyFlags.DeathProcessed) != 0 || enemy.Creature.IsAlive)
                continue;
            // An illusion that just began reviving (IllusionPower.AfterDeath) still goes through the death
            // pass: DeathPowerSupport.Trigger calls BeginIllusionRevive and then RemovePowersAfterDeath for
            // the same creature, so it loses its non-temporary debuffs.
            if (enemy.DeathPhase is DeathPhase.PermanentlyDead)
            {
                enemy.Flags |= EnemyFlags.DeathProcessed;
                continue;
            }
            newlyDead[count++] = (byte)index;
        }
        for (int index = 0; index < count; index++)
        {
            byte owner = EnemyOwner(newlyDead[index]);
            BeginEnemyDeath(ref x, owner);
            if (x.NeedsChoice)
                return;
            CompleteEnemyDeath(ref x, owner);
            if (x.NeedsChoice)
                return;
        }
        ResolvePowerAmountChanges(ref x);
    }

    /// <summary>
    /// The first half of one enemy's death (<c>DeathPowerSupport.Trigger</c>), at the native
    /// <c>AfterDeath</c> point of <c>Kill</c>: marks the death processed, runs the death cases of every
    /// positive power over a snapshot taken before this death's power cleanup, then recovers the stolen
    /// resources. Returns false (doing nothing) when the enemy is alive or its death was processed.
    /// Every kill runs it, including a Reattach segment another segment's death has just marked
    /// permanently dead: native runs <c>AfterDeath</c> and the power cleanup on each kill.
    /// </summary>
    private static bool BeginEnemyDeath(ref Exec x, byte owner)
    {
        ref Enemy enemy = ref x.B.Enemy(owner);
        if ((enemy.Flags & EnemyFlags.DeathProcessed) != 0 || enemy.Creature.IsAlive)
            return false;
        enemy.Flags |= EnemyFlags.DeathProcessed;
        TriggerDeathPowers(ref x, owner);
        if (x.NeedsChoice)
            return true;
        // DeathPowerSupport.Trigger: stolen resources are recovered after the death cases and before
        // the dead owner's powers are removed.
        RecoverStolenResources(ref x.B, owner);
        return true;
    }

    /// <summary>
    /// The second half of one enemy's death, after it left the roster (native
    /// <c>RemoveAllPowersAfterDeath</c>): removes its powers, completes its death phase and runs the
    /// applier cleanup of the powers it applied.
    /// </summary>
    private static void CompleteEnemyDeath(ref Exec x, byte owner)
    {
        RemovePowersAfterDeath(ref x, owner);
        ref Enemy enemy = ref x.B.Enemy(owner);
        if (enemy.DeathPhase != DeathPhase.Reviving)
            enemy.DeathPhase = DeathPhase.PermanentlyDead;
        ApplierDeath(ref x, owner);
    }

    /// <summary>
    /// The death cases of <c>DeathPowerSupport.Trigger</c>: every positive power's
    /// <see cref="PowerDef.DeathTrigger"/>, over the listener order snapshot taken before this
    /// death's own power cleanup (so newly spawned powers are not visited by the same pass).
    /// </summary>
    private static void TriggerDeathPowers(ref Exec x, byte dead)
    {
        Span<byte> owners = stackalloc byte[Limits.Powers];
        Span<ushort> ids = stackalloc ushort[Limits.Powers];
        int count = 0;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Amount <= 0)
                continue;
            owners[count] = power.Owner;
            ids[count] = power.Id;
            count++;
        }
        for (int index = 0; index < count; index++)
        {
            int position = FindPower(x.B, owners[index], ids[index]);
            if (position < 0 || x.B.Powers[position].Amount <= 0)
                continue;
            Registry.Power(ids[index]).DeathTrigger(ref x,
                new Listener(ListenerKind.Power, checked((byte)position), owners[index]), dead);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.RemovePowersAfterDeath</c>
    /// (<c>SimulatedCombatState.DeathLifecycle.cs</c>): an illusion owner keeps its buffs and
    /// temporary debuffs; every other power follows
    /// <see cref="PowerDef.ShouldPowerBeRemovedAfterOwnerDeath"/>. A creature no longer in the
    /// roster loses every power that does not explicitly survive its owner (FlutterPower keeps the
    /// existing engine's mirrored pending amount row); the row-addressed setter matches the native
    /// per-instance cleanup and releases detached slots.
    /// </summary>
    private static void RemovePowersAfterDeath(ref Exec x, byte owner)
    {
        bool ownerInRoster = owner == Limits.OstyOwner
            ? (x.B.Flags & BattleFlags.OstyPresent) != 0
            : InRoster(x.B, owner);
        bool illusionHook = false;
        if (ownerInRoster)
        {
            for (int index = 0; index < x.B.PowerCount; index++)
            {
                ref readonly Power power = ref x.B.Powers[index];
                if (power.Owner == owner && power.Amount > 0
                    && Registry.Power(power.Id).KeepsOwnerBuffsAfterDeath)
                {
                    illusionHook = true;
                    break;
                }
            }
        }
        for (int index = x.B.PowerCount - 1; index >= 0; index--)
        {
            Power power = x.B.Powers[index];
            if (power.Owner != owner || power.Amount == 0)
                continue;
            if ((power.Flags & PowerFlags.PendingAmountState) != 0)
                continue;
            PowerDef def = Registry.Power(power.Id);
            bool keep = ownerInRoster
                ? !def.ShouldPowerBeRemovedAfterOwnerDeath
                : def.PowerRemainsAfterOwnerLeavesRoster;
            if (ownerInRoster && illusionHook)
                keep = def.Type != PowerType.Debuff || def.IsTemporaryPower;
            // The existing engine's cleanup addresses the exact native instance (GetMutablePowerInstance),
            // so use the row-addressed setter for instanced powers and to release a detached slot.
            if (!keep)
                SetPowerAmountAt(ref x.B, index, 0);
        }
    }
}
