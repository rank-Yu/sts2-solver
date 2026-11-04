namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>Index of the stacking instance of <paramref name="powerId"/> on <paramref name="owner"/>, or -1.</summary>
    public static int FindPower(in Battle battle, byte owner, ushort powerId)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner == owner && power.Id == powerId)
                return index;
            if (power.Owner > owner)
                break;
        }
        return -1;
    }

    /// <summary>
    /// Index of the <paramref name="occurrence"/>-th instance of <paramref name="powerId"/> on
    /// <paramref name="owner"/> in table order, or -1. Instanced powers share (owner, id), so a
    /// listener snapshot must keep the occurrence to address the exact row after a removal.
    /// </summary>
    public static int FindPowerOccurrence(in Battle battle, byte owner, ushort powerId, int occurrence)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != owner || power.Id != powerId)
                continue;
            if (occurrence-- == 0)
                return index;
        }
        return -1;
    }

    /// <summary>
    /// Index of the most recently added instance of <paramref name="powerId"/> on <paramref name="owner"/>,
    /// or -1. Used by a card after applying an instanced power to initialize that exact instance.
    /// </summary>
    public static int FindLastPower(in Battle battle, byte owner, ushort powerId)
    {
        int found = -1;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner == owner && power.Id == powerId)
                found = index;
        }
        return found;
    }

    /// <summary>
    /// <c>CorePowerSupport</c>'s <c>targetDeathTriggersFatal</c>: every power on the target allows its
    /// owner's death to count as a fatal kill (HandOfGreed, Feed, TheHunt).
    /// </summary>
    public static bool TargetDeathTriggersFatal(in Battle battle, byte owner)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != owner)
                continue;
            if (!Registry.Power(power.Id).ShouldOwnerDeathTriggerFatal(battle,
                    new Listener(ListenerKind.Power, (byte)index, owner)))
                return false;
        }
        return true;
    }

    /// <summary>Mirrors <c>SimulatedCombatState.GetAmount&lt;T&gt;</c>.</summary>
    public static int GetAmount(in Battle battle, byte owner, ushort powerId)
    {
        int index = FindPower(battle, owner, powerId);
        return index < 0 ? 0 : battle.Powers[index].Amount;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.ApplyWithBeforeApplied</c> for the supported powers and
    /// returns the applied delta.
    /// </summary>
    public static int ApplyPower(ref Exec x, byte target, ushort powerId, int amount, byte applier)
        => ApplyPowerInternal(ref x, target, powerId, amount, applier, Limits.NoOwner);

    /// <summary>
    /// The escape hatch for the five native sites that write <c>PowerModel.Target</c>: SwipePower,
    /// ThieveryPower, SandpitPower, HeistPower and WitheringPresencePower (the last arrives through
    /// the importer). Every other application must keep <see cref="Power.Target"/> at
    /// <see cref="Limits.NoOwner"/>, because <c>PowerCmd.Apply</c> never writes it.
    /// </summary>
    public static int ApplyPowerTargeted(ref Exec x, byte owner, byte powerTarget, ushort powerId,
        int amount, byte applier)
    {
        if (amount == 0)
            return 0;
        // Native order: PowerCmd.Apply returns the instance first, then the caller assigns Target.
        // Do not pass powerTarget into the insertion: hooks during application must see NoOwner.
        int applied = ApplyPowerInternal(ref x, owner, powerId, amount, applier, Limits.NoOwner);
        if (applied == 0 || x.NeedsChoice)
            return applied;
        int index = FindLastPower(x.B, owner, powerId);
        if (index >= 0)
            x.B.Powers[index].Target = powerTarget;
        return applied;
    }

    /// <summary>
    /// The clone path of the non-generic <c>PowerCmd.Apply</c>: <c>Misery</c> clones a debuff
    /// <c>PowerModel</c> (preserving its <c>Target</c>) and applies the clone, so the existing target
    /// must survive. This is not a new native write site: the source row's target is copied verbatim.
    /// </summary>
    public static int ApplyPowerPreservingTarget(ref Exec x, byte target, ushort powerId, int amount,
        byte applier, byte powerTarget)
        => ApplyPowerInternal(ref x, target, powerId, amount, applier, powerTarget);

    private static int ApplyPowerInternal(ref Exec x, byte target, ushort powerId, int amount,
        byte applier, byte powerTarget)
    {
        if (amount == 0 || !CanReceivePowers(x.B, target))
            return 0;
        PowerDef def = Registry.Power(powerId);
        // SimulatedCombatState.ModifyPowerAmountForRelics: ObserveUnsettlingLamp (the native
        // BeforePowerAmountChanged pass), the additive given pass (SneckoSkull), the multiplicative
        // given pass (UnsettlingLamp), then the received pass (Ruined Helmet), all before Artifact.
        // UnsettlingLamp is the only listener of the two lamp-specific passes.
        int cardSourceSerial = x.CardPowerSourceActive ? x.CardPowerSourceSerial : 0;
        bool lampHooks = x.R.UnsettlingLampRelicId != ushort.MaxValue
            || (x.R.HasIdentityContent && x.B.RelicCount != 0 && HasRelic(x.B, "UNSETTLING_LAMP"));
        if (lampHooks)
        {
            HookDispatch.BeforePowerAmountChanged(ref x, powerId, amount, target, applier, cardSourceSerial);
            if (x.NeedsChoice)
                return 0;
        }
        amount = (int)HookDispatch.ModifyPowerAmountGiven(x.B, x.R, powerId, applier, target, amount);
        if (lampHooks)
        {
            amount = (int)HookDispatch.ModifyPowerAmountGivenMultiplicative(ref x, powerId, applier, target,
                amount, cardSourceSerial);
        }
        Span<Listener> received = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        amount = HookDispatch.ModifyPowerAmountReceived(x.B, x.R, powerId, target, amount, received,
            out int receivedCount);
        if (receivedCount > 0)
        {
            HookDispatch.AfterModifyingPowerAmountReceived(ref x, powerId, target, received[..receivedCount]);
            if (x.NeedsChoice)
                return 0;
        }
        if (def.TypeForAmount(amount) == PowerType.Debuff && ConsumeArtifact(ref x.B, target))
            return 0;
        return AddPowerAmount(ref x, target, powerId, def, amount, applier, out _, powerTarget);
    }

    /// <summary>
    /// <c>SimulatedCombatState.ApplyPowerFromSource(..., cardSource: null)</c>: the application is not
    /// part of the current card's power scope, so card-source relic passes such as UnsettlingLamp do
    /// not observe or double it. Power hooks whose native mirror passes a null card source use this
    /// (Envenom/ReaperForm doom and MonarchsGaze's temporary Strength loss).
    /// </summary>
    public static int ApplyPowerWithoutCardSource(ref Exec x, byte target, ushort powerId, int amount,
        byte applier)
    {
        bool active = x.CardPowerSourceActive;
        int serial = x.CardPowerSourceSerial;
        x.CardPowerSourceActive = false;
        int applied = ApplyPower(ref x, target, powerId, amount, applier);
        x.CardPowerSourceActive = active;
        x.CardPowerSourceSerial = serial;
        return applied;
    }

    /// <summary>
    /// <see cref="ApplyTemporaryStrength"/> with <c>ApplyPowerFromSource(..., cardSource: null)</c>'s
    /// suppressed card scope (MonarchsGazePower).
    /// </summary>
    public static int ApplyTemporaryStrengthWithoutCardSource(ref Exec x, byte target, ushort temporaryId,
        int amount, byte applier, int sign)
    {
        bool active = x.CardPowerSourceActive;
        int serial = x.CardPowerSourceSerial;
        x.CardPowerSourceActive = false;
        int applied = ApplyTemporaryStrength(ref x, target, temporaryId, amount, applier, sign);
        x.CardPowerSourceActive = active;
        x.CardPowerSourceSerial = serial;
        return applied;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.ApplyTemporaryStrength</c> (<c>ApplyTemporaryStrengthGain</c> with
    /// <paramref name="sign"/> 1, <c>ApplyTemporaryStrengthLoss</c> with -1): Artifact is checked against
    /// the temporary power; a first (or instanced) application runs its native <c>BeforeApplied</c>,
    /// changing Strength by sign × amount before the temporary power is added; afterwards a Strength
    /// change by sign × amount follows when the requested amount differs from the resulting counter
    /// (the stacking case, native <c>AfterPowerAmountChanged</c>). Returns the applied delta.
    /// </summary>
    public static int ApplyTemporaryStrength(ref Exec x, byte target, ushort temporaryId, int amount, byte applier, int sign)
    {
        if (amount == 0 || !CanReceivePowers(x.B, target))
            return 0;
        PowerDef def = Registry.Power(temporaryId);
        // ApplyWithBeforeApplied runs ModifyPowerAmountForRelics before the native BeforeApplied, so the
        // modified amount is what the inner Strength change uses (and what UnsettlingLamp doubles).
        Span<Listener> received = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        amount = ApplyTemporaryPowerAmountModifiers(ref x, temporaryId, amount, target, applier, received,
            out int receivedCount);
        if (receivedCount > 0)
        {
            HookDispatch.AfterModifyingPowerAmountReceived(ref x, temporaryId, target, received[..receivedCount]);
            if (x.NeedsChoice)
                return 0;
        }
        if (def.TypeForAmount(amount) == PowerType.Debuff && ConsumeArtifact(ref x.B, target))
            return 0;
        ushort strength = Registry.TryPower("STRENGTH_POWER", out ushort id)
            ? id
            : throw new UnsupportedException("临时力量需要力量的 Sts2Solver 定义。");
        if (def.IsInstanced || GetAmount(x.B, target, temporaryId) == 0)
            ApplyPower(ref x, target, strength, sign * amount, applier);
        int applied = AddPowerAmount(ref x, target, temporaryId, def, amount, applier, out int resulting);
        if (amount != resulting)
            ApplyPower(ref x, target, strength, sign * amount, applier);
        return applied;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.ApplyTemporaryFocus</c> (<c>ApplyTemporaryFocusGain</c> with
    /// <paramref name="sign"/> 1, <c>ApplyTemporaryFocusLoss</c> with -1): the temporary power's native
    /// <c>BeforeApplied</c> applies sign × amount for a first/instanced application, and a stacking
    /// application applies sign × the resulting delta after the counter changes.
    /// </summary>
    public static int ApplyTemporaryFocus(ref Exec x, byte target, ushort temporaryId, int amount,
        byte applier, int sign)
    {
        if (amount == 0 || !CanReceivePowers(x.B, target))
            return 0;
        PowerDef def = Registry.Power(temporaryId);
        Span<Listener> received = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        amount = ApplyTemporaryPowerAmountModifiers(ref x, temporaryId, amount, target, applier, received,
            out int receivedCount);
        if (receivedCount > 0)
        {
            HookDispatch.AfterModifyingPowerAmountReceived(ref x, temporaryId, target, received[..receivedCount]);
            if (x.NeedsChoice)
                return 0;
        }
        if (def.TypeForAmount(amount) == PowerType.Debuff && ConsumeArtifact(ref x.B, target))
            return 0;
        ushort focus = Registry.TryPower("FOCUS_POWER", out ushort id)
            ? id
            : throw new UnsupportedException("临时集中需要集中的 Sts2Solver 定义。");
        bool alreadyApplied = GetAmount(x.B, target, temporaryId) != 0;
        if (def.IsInstanced || !alreadyApplied)
            ApplyPower(ref x, target, focus, sign * amount, applier);
        int applied = AddPowerAmount(ref x, target, temporaryId, def, amount, applier, out _);
        if (alreadyApplied && applied != 0)
            ApplyPower(ref x, target, focus, sign * applied, applier);
        return applied;
    }

    /// <summary>
    /// The relic part of <c>ApplyWithBeforeApplied</c> for a temporary stat power: the lamp observation,
    /// additive/multiplicative given passes and the received pass. The caller runs Artifact after it.
    /// </summary>
    private static int ApplyTemporaryPowerAmountModifiers(ref Exec x,
        ushort powerId, int amount, byte target, byte applier, scoped Span<Listener> received,
        out int receivedCount)
    {
        int cardSourceSerial = x.CardPowerSourceActive ? x.CardPowerSourceSerial : 0;
        HookDispatch.BeforePowerAmountChanged(ref x, powerId, amount, target, applier, cardSourceSerial);
        amount = (int)HookDispatch.ModifyPowerAmountGiven(x.B, x.R, powerId, applier, target, amount);
        amount = (int)HookDispatch.ModifyPowerAmountGivenMultiplicative(ref x, powerId, applier, target,
            amount, cardSourceSerial);
        amount = HookDispatch.ModifyPowerAmountReceived(x.B, x.R, powerId, target, amount, received,
            out receivedCount);
        return amount;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.ApplyTemporaryDexterity</c> (<c>ApplyTemporaryStat</c>):
    /// native <c>TemporaryDexterityPower.BeforeApplied</c> applies the Dexterity before
    /// <c>PowerCmd.Apply</c> inserts the temporary power row. A first application therefore gives
    /// Dexterity and then inserts the row; a stacking application inserts the row and gives the
    /// applied delta afterwards.
    /// </summary>
    public static int ApplyTemporaryDexterity(ref Exec x, byte target, ushort temporaryId, int amount,
        byte applier)
    {
        if (amount == 0 || !CanReceivePowers(x.B, target))
            return 0;
        PowerDef def = Registry.Power(temporaryId);
        Span<Listener> received = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        amount = ApplyTemporaryPowerAmountModifiers(ref x, temporaryId, amount, target, applier, received,
            out int receivedCount);
        if (receivedCount > 0)
        {
            HookDispatch.AfterModifyingPowerAmountReceived(ref x, temporaryId, target, received[..receivedCount]);
            if (x.NeedsChoice)
                return 0;
        }
        if (def.TypeForAmount(amount) == PowerType.Debuff && ConsumeArtifact(ref x.B, target))
            return 0;
        ushort dexterity = Registry.TryPower("DEXTERITY_POWER", out ushort id)
            ? id
            : throw new UnsupportedException("临时敏捷需要敏捷的 Sts2Solver 定义。");
        bool alreadyApplied = GetAmount(x.B, target, temporaryId) != 0;
        if (def.IsInstanced || !alreadyApplied)
            ApplyPower(ref x, target, dexterity, amount, applier);
        int applied = AddPowerAmount(ref x, target, temporaryId, def, amount, applier, out _);
        if (alreadyApplied && applied != 0)
            ApplyPower(ref x, target, dexterity, applied, applier);
        return applied;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.RetireTemporaryStat</c> for a temporary Focus power: removes it,
    /// then changes Focus by -amount for a buff and +amount for a debuff (type for the current amount).
    /// </summary>
    public static void RetireTemporaryFocus(ref Exec x, Listener self)
    {
        ref Power power = ref x.B.Powers[self.Index];
        ushort id = power.Id;
        int amount = power.Amount;
        int delta = Registry.Power(id).TypeForAmount(amount) == PowerType.Buff ? -amount : amount;
        SetPowerAmount(ref x.B, self.Owner, id, 0);
        if (delta == 0)
            return;
        ushort focus = Registry.TryPower("FOCUS_POWER", out ushort focusId)
            ? focusId
            : throw new UnsupportedException("临时集中需要集中的 Sts2Solver 定义。");
        ApplyPower(ref x, self.Owner, focus, delta, self.Owner);
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.RetireTemporaryStat</c> for a temporary Strength power: removes it,
    /// then changes Strength by -amount for a buff and +amount for a debuff (type for the current amount).
    /// </summary>
    public static void RetireTemporaryStrength(ref Exec x, Listener self)
    {
        ref Power power = ref x.B.Powers[self.Index];
        ushort id = power.Id;
        int amount = power.Amount;
        int delta = Registry.Power(id).TypeForAmount(amount) == PowerType.Buff ? -amount : amount;
        SetPowerAmount(ref x.B, self.Owner, id, 0);
        if (delta == 0)
            return;
        ushort strength = Registry.TryPower("STRENGTH_POWER", out ushort strengthId)
            ? strengthId
            : throw new UnsupportedException("临时力量需要力量的 Sts2Solver 定义。");
        ApplyPower(ref x, self.Owner, strength, delta, self.Owner);
    }

    /// <summary>The counter update of <c>ApplyWithBeforeApplied</c> after its guards and Artifact check.</summary>
    private static int AddPowerAmount(ref Exec x, byte target, ushort powerId, PowerDef def, int amount,
        byte applier, out int resulting, byte powerTarget = Limits.NoOwner)
    {
        ref Battle battle = ref x.B;
        int index;
        if (def.IsInstanced)
            index = InsertPower(ref battle, target, powerId, applier, powerTarget);
        else
        {
            index = FindPower(battle, target, powerId);
            if (index < 0)
                index = InsertPower(ref battle, target, powerId, applier, powerTarget);
        }

        ref Power power = ref battle.Powers[index];
        int previous = power.Amount;
        power.Amount = Clamp((long)previous + amount);
        if (previous == 0 && power.Amount != 0 && IsPlayerSide(target) && def.UsesNativeDurationSkip)
            power.Flags |= PowerFlags.SkipNextDurationTick;
        int applied = power.Amount - previous;
        resulting = power.Amount;
        if (def.MarksJustAppliedByEnemy && previous <= 0 && power.Amount > 0 && IsEnemyOwner(target))
            power.Flags |= PowerFlags.WasJustAppliedByEnemy;
        // SimulatedCombatState.ApplyWithBeforeApplied records every positive Doom application by the
        // player for DeathsDoor's WasDoomAppliedThisTurn.
        if (applied > 0 && applier == Limits.PlayerOwner
            && Registry.TryPower("DOOM_POWER", out ushort doomId) && powerId == doomId)
        {
            battle.SetCounter(Counter.DoomAppliedThisTurn, 1);
        }
        if (power.Amount == 0)
            RemovePowerAt(ref battle, index);
        // RecordPowerAmountChange: resolved later by ResolvePowerAmountChanges at the same points as
        // the existing engine (after card effects, turn phases, enemy moves and enemy death powers).
        x.RecordPowerAmountChange(powerId, applied, applier, target);
        return applied;
    }

    /// <summary>
    /// Mirrors <c>PowerLifecycleSupport.ResolvePowerAmountChanges</c>: drains the queued changes in
    /// order; for each change it visits the current powers in listener order (Vicious draws after a
    /// Vulnerable gain whose applier is its owner), stops at a pending choice, and repeats while the
    /// handlers appended new changes.
    /// </summary>
    public static void ResolvePowerAmountChanges(ref Exec x)
    {
        // PowerLifecycleSupport.ResolvePowerAmountChanges drains the whole pending list before it
        // dispatches any handler. Keeping the batch in flight in Exec would let a nested
        // resolution (Vicious -> draw -> Hellraiser auto-play -> PlayFromPlay) consume the outer
        // batch and leave its captured count stale.
        Span<PowerAmountChange> drained = stackalloc PowerAmountChange[Limits.PendingPowerAmountChanges];
        while (x.PendingPowerChangeCount > 0)
        {
            int count = x.PendingPowerChangeCount;
            for (int index = 0; index < count; index++)
                drained[index] = x.PendingPowerChanges[index];
            x.ClearPowerAmountChanges();
            for (int index = 0; index < count; index++)
            {
                PowerAmountChange change = drained[index];
                AfterPowerAmountChangedHook hook = new()
                {
                    PowerId = change.PowerId,
                    Delta = change.Delta,
                    Applier = change.Applier,
                    Target = change.Target,
                };
                HookDispatch.DispatchPowerHook(ref x, ref hook);
                if (x.NeedsChoice)
                    return;
            }
        }
    }

    /// <summary>Mirrors <c>SimulatedCombatState.ApplyFromMonster&lt;T&gt;</c>.</summary>
    public static void ApplyPowerFromMonster(ref Exec x, byte target, ushort powerId, int amount, byte applier)
    {
        bool alreadyPresent = GetAmount(x.B, target, powerId) > 0;
        ApplyPower(ref x, target, powerId, amount, applier);
        PowerDef def = Registry.Power(powerId);
        if (!def.UsesNativeDurationSkip && !alreadyPresent && amount > 0)
        {
            int index = FindPower(x.B, target, powerId);
            if (index >= 0 && x.B.Powers[index].Amount > 0)
                x.B.Powers[index].Flags |= PowerFlags.SkipNextTickFromMonster;
        }
    }

    /// <summary>Mirrors <c>SimulatedCombatState.SetAmount&lt;T&gt;</c>.</summary>
    public static void SetPowerAmount(ref Battle battle, byte owner, ushort powerId, int amount)
    {
        int index = FindPower(battle, owner, powerId);
        int current = index < 0 ? 0 : battle.Powers[index].Amount;
        if (current == amount)
            return;
        if (index < 0)
            index = InsertPower(ref battle, owner, powerId, Limits.NoOwner);
        battle.Powers[index].Amount = Clamp(amount);
        if (battle.Powers[index].Amount == 0)
            RemovePowerAt(ref battle, index);
    }

    /// <summary>
    /// <c>SimulatedCombatState.SetPowerAmount</c> addressed by table row rather than (owner, id):
    /// instanced powers share both keys, and the existing engine's Sandpit decrement targets the exact
    /// instance it is visiting.
    /// </summary>
    public static void SetPowerAmountAt(ref Battle battle, int index, int amount)
    {
        if ((uint)index >= (uint)battle.PowerCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Power 行越界。");
        battle.Powers[index].Amount = Clamp(amount);
        if (battle.Powers[index].Amount == 0)
            RemovePowerAt(ref battle, index);
    }

    /// <summary>Mirrors <c>SimulatedCombatState.TickDuration&lt;T&gt;</c>.</summary>
    public static void TickDuration(ref Battle battle, byte owner, ushort powerId)
    {
        int index = FindPower(battle, owner, powerId);
        if (index >= 0)
        {
            ref Power power = ref battle.Powers[index];
            if ((power.Flags & PowerFlags.SkipNextTickFromMonster) != 0)
            {
                power.Flags &= ~PowerFlags.SkipNextTickFromMonster;
                return;
            }
            if ((power.Flags & PowerFlags.SkipNextDurationTick) != 0)
            {
                power.Flags &= ~PowerFlags.SkipNextDurationTick;
                return;
            }
        }
        int amount = index < 0 ? 0 : battle.Powers[index].Amount;
        if (amount > 0)
            SetPowerAmount(ref battle, owner, powerId, amount - 1);
    }

    /// <summary>Mirrors <c>SimulatedCombatState.CanReceivePredictedPowers</c>.</summary>
    public static bool CanReceivePowers(in Battle battle, byte owner)
    {
        // DieForYouPower.ShouldAllowHitting returns creature.IsAlive for every creature while Osty
        // exists; PowerCmd.Apply therefore refuses powers on a dead target even before the death
        // pass (ReaperFormPower's lethal AfterDamageGiven is the observable case).
        if ((battle.Flags & BattleFlags.OstyEverPresent) != 0 && battle.Creature(owner).IsDead)
            return false;
        if (!IsEnemyOwner(owner))
            return true;
        ref readonly Enemy enemy = ref battle.Enemies[owner - Limits.FirstEnemyOwner];
        return enemy.DeathPhase == DeathPhase.None && (enemy.Flags & EnemyFlags.InCombat) != 0;
    }

    public static bool IsPlayerSide(byte owner) => owner == Limits.PlayerOwner || owner == Limits.OstyOwner;

    /// <summary>
    /// Native <c>dealer == Owner.Creature || dealer == Owner.Osty</c>: the owner's creature or its
    /// pet. Osty is the only pet in the supported content.
    /// </summary>
    public static bool IsOwnerOrOsty(byte owner, byte dealer)
        => dealer == owner || (owner == Limits.PlayerOwner && dealer == Limits.OstyOwner);

    /// <summary>Mirrors <c>SimulatedCombatState.ConsumeArtifact</c>.</summary>
    private static bool ConsumeArtifact(ref Battle battle, byte target)
    {
        if (!Registry.TryPower("ARTIFACT_POWER", out ushort artifact))
            return false;
        int amount = GetAmount(battle, target, artifact);
        if (amount <= 0)
            return false;
        SetPowerAmount(ref battle, target, artifact, amount - 1);
        return true;
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.IncrementSandpitTargeting</c>: the first positive Sandpit power
    /// owned by a roster creature and targeting <paramref name="target"/> gains one stack.
    /// </summary>
    public static void IncrementSandpitTargeting(ref Exec x, byte target)
    {
        if (!Registry.TryPower("SANDPIT_POWER", out ushort sandpit))
            return;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref Power power = ref x.B.Powers[index];
            if (power.Id != sandpit || power.Amount <= 0 || power.Target != target)
                continue;
            if (!IsEnemyOwner(power.Owner) || !InRoster(x.B, power.Owner))
                continue;
            SetPowerAmountAt(ref x.B, index, power.Amount + 1);
            return;
        }
    }

    /// <summary>Inserts a zero-amount power after the owner's last power, keeping the table grouped by owner.</summary>
    private static int InsertPower(ref Battle battle, byte owner, ushort powerId, byte applier,
        byte powerTarget = Limits.NoOwner)
    {
        int count = battle.PowerCount;
        if (count >= Limits.Powers)
            throw new CapacityException("Powers", Limits.Powers);
        int position = count;
        for (int index = 0; index < count; index++)
        {
            if (battle.Powers[index].Owner > owner)
            {
                position = index;
                break;
            }
        }
        for (int index = count; index > position; index--)
            battle.Powers[index] = battle.Powers[index - 1];
        battle.Powers[position] = new Power
        {
            Id = powerId,
            Owner = owner,
            Applier = applier,
            Target = powerTarget,
        };
        battle.PowerCount++;
        return position;
    }

    /// <summary>Removes a power and compacts the table, mirroring the retirement of its listener slot.</summary>
    public static void RemovePowerAt(ref Battle battle, int index)
    {
        if ((uint)index >= (uint)battle.PowerCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Power 行越界。");
        ref Power removed = ref battle.Powers[index];
        PowerDef definition = Registry.Power(removed.Id);
        if (definition.UsesDetachedCard && removed.Aux0 != 0)
        {
            int slot = removed.Aux0 - 1;
            if ((uint)slot >= (uint)Limits.DetachedCards)
                throw new UnsupportedException($"Power 行 {removed.Id} 的 detached 槽位 {removed.Aux0} 越界。");
            // SwipePower keeps the stolen card value in the detached slot after its removal:
            // BeforeDeath returns it to the run deck, while an escape loses it. RemovedFromState
            // (set when the card left combat) distinguishes the two at write-back. Every other
            // detached power (Nightmare) drops its selection.
            if (definition.NativeId == "SWIPE_POWER")
            {
                int enemy = removed.Owner - Limits.FirstEnemyOwner;
                bool escaped = removed.Owner >= Limits.FirstEnemyOwner
                    && (uint)enemy < (uint)battle.EnemyCount
                    && (battle.Enemy(removed.Owner).Flags & EnemyFlags.Escaped) != 0;
                if (battle.Creature(removed.Owner).IsDead && !escaped)
                    battle.DetachedCards[slot].Flags &= ~CardFlags.RemovedFromState;
            }
            else
            {
                battle.DetachedCards[slot] = default;
            }
        }
        int count = battle.PowerCount;
        for (int next = index + 1; next < count; next++)
            battle.Powers[next - 1] = battle.Powers[next];
        battle.Powers[count - 1] = default;
        battle.PowerCount--;
    }

    /// <summary>Allocates the first zero detached-card slot, or throws the capacity boundary.</summary>
    public static int AllocateDetachedCard(ref Battle battle, in Card card)
    {
        for (int slot = 0; slot < Limits.DetachedCards; slot++)
        {
            if (battle.DetachedCards[slot].Id != 0)
                continue;
            battle.DetachedCards[slot] = card;
            return slot;
        }
        throw new CapacityException("DetachedCards", Limits.DetachedCards);
    }

    /// <summary>Reads the detached card referenced by <paramref name="power"/>.</summary>
    public static Card DetachedCard(in Battle battle, in Power power)
    {
        if (power.Aux0 == 0)
            throw new UnsupportedException($"Power 行 {power.Id} 没有 detached 卡。");
        int slot = power.Aux0 - 1;
        if ((uint)slot >= (uint)Limits.DetachedCards)
            throw new UnsupportedException($"Power 行 {power.Id} 的 detached 槽位 {power.Aux0} 越界。");
        return battle.DetachedCards[slot];
    }

    /// <summary>Mirrors <c>SimulatedCombatState.RemovePowersAfterDeath</c>: drops every power of the owner.</summary>
    public static void RemovePowersOf(ref Battle battle, byte owner)
        => RemovePowersOfInternal(ref battle, owner, keepPendingAmountStates: false);

    /// <summary>
    /// <c>RemovePowersAfterDeath</c> for the player. A row whose native mirror created a
    /// <c>PowerAmountPredictionState</c> during this action is not removed: the boundary
    /// synchronization writes the stored amount back into the model, so the native state keeps the
    /// power (GigantificationPower's BeforeAttack reads it for every attack).
    /// </summary>
    private static void RemovePowersOfInternal(ref Battle battle, byte owner, bool keepPendingAmountStates)
    {
        for (int index = battle.PowerCount - 1; index >= 0; index--)
        {
            if (battle.Powers[index].Owner != owner)
                continue;
            if (keepPendingAmountStates
                && (battle.Powers[index].Flags & PowerFlags.PendingAmountState) != 0)
            {
                continue;
            }
            RemovePowerAt(ref battle, index);
        }
    }

    /// <summary>
    /// Drops the transient <see cref="PowerFlags.PendingAmountState"/> markers at a stable
    /// boundary; the native entry is consumed by <c>SynchronizePowerAmountPredictionStates</c> there.
    /// </summary>
    public static void ClearPendingPowerAmountStates(ref Battle battle)
    {
        for (int index = 0; index < battle.PowerCount; index++)
            battle.Powers[index].Flags &= ~PowerFlags.PendingAmountState;
    }
    private static bool HasRelic(in Battle battle, string nativeId)
    {
        if (!Registry.TryRelic(nativeId, out ushort id))
            return false;
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if (relic.Id == id && (relic.Flags & RelicFlags.Melted) == 0)
                return true;
        }
        return false;
    }

}
