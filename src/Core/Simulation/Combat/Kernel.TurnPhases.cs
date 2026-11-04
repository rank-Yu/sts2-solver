namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Turn-phase skeleton mirroring the existing engine's phase functions. Two shapes occur there:
/// listener-order passes over powers or relics with a per-type switch (each switch case becomes the
/// definition's phase method), and fixed type sequences (kept here as ordered native-id lists, copied
/// from the existing code). A type without a Sts2Solver definition is skipped; admission already rejects
/// roots that hold it.
/// </summary>
public static partial class Kernel
{
    // PersistentPowerSupport.TriggerOwnerAfterSideTurnStart, in source order.
    private static readonly string[] OwnerAfterSideTurnStartOrder =
    [
        "BIASED_COGNITION_POWER", "COOLANT_POWER", "DEMON_FORM_POWER", "FERAL_POWER", "FURNACE_POWER",
        "NEUROSURGE_POWER", "NOXIOUS_FUMES_POWER", "PREP_TIME_POWER", "REFLECT_POWER", "SHADOW_STEP_POWER",
        "WRAITH_FORM_POWER", "CLARITY_POWER",
    ];

    // SimulatedCombatState.TriggerBaseSideTurnStart, in source order.
    private static readonly string[] BaseSideTurnStartOrder =
        ["BLUR_POWER", "DRAW_CARDS_NEXT_TURN_POWER", "PLATING_POWER", "SLOW_POWER"];

    // TurnStartPowerSupport.TriggerAfterSideTurnStart, in source order (Sandpit only on the enemy side).
    private static readonly string[] GlobalAfterSideTurnStartOrder = ["COUNTDOWN_POWER", "SANDPIT_POWER"];

    // CorePowerSupport.TriggerTransientSideTurnEndPowers: removals then decrements, per participant.
    private static readonly string[] TransientRemovals =
    [
        "BORROWED_TIME_POWER", "BURST_POWER", "DUPLICATION_POWER", "NO_ENERGY_GAIN_POWER", "ONE_TWO_PUNCH_POWER",
        "RAGE_POWER", "REBOUND_POWER", "SHADOWMELD_POWER",
    ];

    private static readonly string[] TransientDecrements = ["CONQUEROR_POWER", "RETAIN_HAND_POWER"];

    // CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects, per player, before TriggerRegular.
    private static readonly string[] PlayerSideTurnEndOrder = ["CONSTRICT_POWER", "TANGLED_POWER", "RINGING_POWER", "DOUBLE_DAMAGE_POWER"];

    // CorePowerSupport.TriggerEnemySideTurnEndEffects, per enemy, before TriggerRegular.
    private static readonly string[] EnemySideTurnEndOrder = ["REGEN_POWER", "PLATING_POWER", "DOUBLE_DAMAGE_POWER"];

    // CorePowerSupport.CompletePlayerEarlySideTurnEndEffects, per player.
    private static readonly string[] CompletePlayerEarlyOrder = ["REGEN_POWER"];

    private static readonly Lazy<ushort[]> CompletePlayerEarlyIds = new(() => Resolve(CompletePlayerEarlyOrder));

    // CorePowerSupport.ApplyEnemyDeathPowers: checked on each player creature, in source order.
    private static readonly string[] ApplierDeathOrder = ["CONSTRICT_POWER", "HEX_POWER", "SHRINK_POWER"];

    private static readonly Lazy<ushort[]> ApplierDeathIds = new(() => Resolve(ApplierDeathOrder));
    private static readonly Lazy<ushort> MagicBombId = new(() => PowerIdOrMax("MAGIC_BOMB_POWER"));

    private static readonly Lazy<ushort[]> OwnerAfterSideTurnStartIds = new(() => Resolve(OwnerAfterSideTurnStartOrder));
    private static readonly Lazy<ushort[]> BaseSideTurnStartIds = new(() => Resolve(BaseSideTurnStartOrder));
    private static readonly Lazy<ushort[]> GlobalAfterSideTurnStartIds = new(() => Resolve(GlobalAfterSideTurnStartOrder));
    private static readonly Lazy<ushort[]> TransientRemovalIds = new(() => Resolve(TransientRemovals));
    private static readonly Lazy<ushort[]> TransientDecrementIds = new(() => Resolve(TransientDecrements));
    private static readonly Lazy<ushort[]> PlayerSideTurnEndIds = new(() => Resolve(PlayerSideTurnEndOrder));
    private static readonly Lazy<ushort[]> EnemySideTurnEndIds = new(() => Resolve(EnemySideTurnEndOrder));

    /// <summary>Registered internal ids of the listed native ids, keeping order; unregistered ones are dropped.</summary>
    private static ushort[] Resolve(string[] nativeIds)
    {
        List<ushort> ids = new(nativeIds.Length);
        foreach (string nativeId in nativeIds)
        {
            if (Registry.TryPower(nativeId, out ushort id))
                ids.Add(id);
        }
        return [.. ids];
    }

    /// <summary>Owner filter of a listener pass: the player creature and Osty (<c>power.Owner.Player == player</c>).</summary>
    private const byte PlayerSideOwners = Limits.NoOwner - 1;

    private static ushort PowerIdOrMax(string nativeId) => Registry.TryPower(nativeId, out ushort id) ? id : ushort.MaxValue;

    private static readonly Lazy<ushort> PlatingPowerId = new(() => PowerIdOrMax("PLATING_POWER"));
    private static readonly Lazy<ushort> AggressionPowerId = new(() => PowerIdOrMax("AGGRESSION_POWER"));
    private static readonly Lazy<ushort> BlockNextTurnPowerId = new(() => PowerIdOrMax("BLOCK_NEXT_TURN_POWER"));
    private static readonly Lazy<ushort> DoomPowerId = new(() => PowerIdOrMax("DOOM_POWER"));
    private static readonly Lazy<ushort> FlameBarrierPowerId = new(() => PowerIdOrMax("FLAME_BARRIER_POWER"));

    private enum PowerPhase : byte
    {
        BeforeSideTurnStart,
        BaseSideTurnStart,
        OwnerAfterSideTurnStart,
        GlobalAfterSideTurnStart,
        AfterBlockCleared,
        EndTurnRegular,
        EndTurnBatch048,
        PlayerSideTurnEnd,
        EnemySideTurnEnd,
        AfterEnergyReset,
        BeforeHandDraw,
        AfterPlayerTurnStart,
        CompletePlayerEarlySideTurnEnd,
    }

    private static void InvokePowerPhase(ref Exec x, PowerPhase phase, int index, Side side, bool participates, bool flag)
    {
        ref Power power = ref x.B.Powers[index];
        PowerDef def = Registry.Power(power.Id);
        Listener self = new(ListenerKind.Power, (byte)index, power.Owner);
        ChoiceSource savedOrigin = x.ChoiceOrigin;
        x.ChoiceOrigin = ListenerChoiceSource(x.B, self);
        try
        {
            switch (phase)
            {
                case PowerPhase.BeforeSideTurnStart: def.BeforeSideTurnStart(ref x, self, participates); break;
                case PowerPhase.BaseSideTurnStart: def.BaseSideTurnStart(ref x, self, flag); break;
                case PowerPhase.OwnerAfterSideTurnStart: def.OwnerAfterSideTurnStart(ref x, self); break;
                case PowerPhase.GlobalAfterSideTurnStart: def.GlobalAfterSideTurnStart(ref x, self, side, participates); break;
                case PowerPhase.AfterBlockCleared: def.AfterBlockCleared(ref x, self); break;
                case PowerPhase.EndTurnRegular: def.EndTurnRegular(ref x, self, side, participates); break;
                case PowerPhase.EndTurnBatch048: def.EndTurnBatch048(ref x, self, side, participates); break;
                case PowerPhase.PlayerSideTurnEnd: def.PlayerSideTurnEnd(ref x, self); break;
                case PowerPhase.EnemySideTurnEnd: def.EnemySideTurnEnd(ref x, self); break;
                case PowerPhase.AfterEnergyReset: def.AfterEnergyReset(ref x, self); break;
                case PowerPhase.BeforeHandDraw: def.BeforeHandDraw(ref x, self); break;
                case PowerPhase.AfterPlayerTurnStart: def.AfterPlayerTurnStart(ref x, self); break;
                case PowerPhase.CompletePlayerEarlySideTurnEnd: def.CompletePlayerEarlySideTurnEnd(ref x, self); break;
                default: throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
            }
        }
        finally
        {
            x.ChoiceOrigin = savedOrigin;
        }
    }

    /// <summary>
    /// Listener-order pass over a snapshot of the power table (the existing engine iterates the
    /// published EffectivePowers array). Each entry is re-resolved and must still have a positive amount.
    /// </summary>
    private static void PowerListenerPass(ref Exec x, PowerPhase phase, Side side,
        scoped ReadOnlySpan<byte> participants, byte ownerFilter, ushort onlyId, ushort exceptA, ushort exceptB, bool flag = false)
    {
        int count = x.B.PowerCount;
        if (count == 0)
            return;
        Span<(byte Owner, ushort Id, int Occurrence)> snapshot =
            stackalloc (byte, ushort, int)[count];
        Span<byte> owners = stackalloc byte[Limits.Enemies + 2];
        int ownerCount = ListenerCreatureOrder(x.B, owners);
        int taken = 0;
        // Listener order: creature order (player, Osty, roster enemies), then table order within an owner.
        // Instanced powers share (owner, id), so the snapshot keeps the occurrence to address the exact row.
        for (int creature = 0; creature < ownerCount; creature++)
        {
            for (int index = 0; index < count; index++)
            {
                ref readonly Power power = ref x.B.Powers[index];
                if (power.Owner != owners[creature])
                    continue;
                int occurrence = 0;
                for (int prior = 0; prior < index; prior++)
                {
                    ref readonly Power earlier = ref x.B.Powers[prior];
                    if (earlier.Owner == power.Owner && earlier.Id == power.Id)
                        occurrence++;
                }
                snapshot[taken++] = (power.Owner, power.Id, occurrence);
            }
        }
        if (!x.R.HasIdentityContent)
        {
            for (int entry = 0; entry < taken; entry++)
            {
                (byte owner, ushort id, int occurrence) = snapshot[entry];
                if (ownerFilter == PlayerSideOwners
                        ? !IsPlayerSide(owner)
                        : ownerFilter != Limits.NoOwner && owner != ownerFilter)
                    continue;
                if (onlyId != ushort.MaxValue && id != onlyId || id == exceptA || id == exceptB)
                    continue;
                int index = FindPowerOccurrence(x.B, owner, id, occurrence);
                if (index < 0 || x.B.Powers[index].Amount <= 0)
                    continue;
                InvokePowerPhase(ref x, phase, index, side, Contains(participants, owner), flag);
                if (x.NeedsChoice)
                    return;
            }
            return;
        }
        // A phase can remove its own instanced row (Nightmare before hand draw, Monologue at the side
        // turn end), which shifts the occurrence of later same-(owner,id) rows. Track removals per key;
        // the listener snapshot is by object, so the original occurrence is authoritative.
        Span<byte> keyOwners = stackalloc byte[taken];
        Span<ushort> keyIds = stackalloc ushort[taken];
        Span<int> keyRemoved = stackalloc int[taken];
        int keyCount = 0;
        for (int entry = 0; entry < taken; entry++)
        {
            (byte owner, ushort id, int occurrence) = snapshot[entry];
            if (ownerFilter == PlayerSideOwners ? !IsPlayerSide(owner) : ownerFilter != Limits.NoOwner && owner != ownerFilter)
                continue;
            if (onlyId != ushort.MaxValue && id != onlyId || id == exceptA || id == exceptB)
                continue;
            int key = -1;
            for (int existing = 0; existing < keyCount; existing++)
            {
                if (keyOwners[existing] == owner && keyIds[existing] == id)
                {
                    key = existing;
                    break;
                }
            }
            if (key < 0)
            {
                key = keyCount++;
                keyOwners[key] = owner;
                keyIds[key] = id;
                keyRemoved[key] = 0;
            }
            occurrence -= keyRemoved[key];
            int index = FindPowerOccurrence(x.B, owner, id, occurrence);
            if (index < 0 || x.B.Powers[index].Amount <= 0)
                continue;
            int before = CountPowers(x.B, owner, id);
            InvokePowerPhase(ref x, phase, index, side, Contains(participants, owner), flag);
            if (x.NeedsChoice)
                return;
            int after = CountPowers(x.B, owner, id);
            if (after < before)
                keyRemoved[key] += before - after;
        }
    }

    /// <summary>Number of rows of one (owner, id), including zeroed rows not yet compacted.</summary>
    private static int CountPowers(in Battle battle, byte owner, ushort id)
    {
        int count = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            if (battle.Powers[index].Owner == owner && battle.Powers[index].Id == id)
                count++;
        }
        return count;
    }

    /// <summary>Fixed type sequence for one owner: each listed type the owner holds with a positive amount.</summary>
    private static void PowerTypeSequence(ref Exec x, PowerPhase phase, byte owner, scoped ReadOnlySpan<ushort> order,
        Side side, bool participates, bool flag = false)
    {
        foreach (ushort id in order)
        {
            int index = FindPower(x.B, owner, id);
            if (index < 0 || x.B.Powers[index].Amount <= 0)
                continue;
            InvokePowerPhase(ref x, phase, index, side, participates, flag);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>Player, Osty, then roster enemies: the creature order of the combat listener list.</summary>
    private static int ListenerCreatureOrder(in Battle battle, Span<byte> owners)
    {
        int count = 0;
        owners[count++] = Limits.PlayerOwner;
        if ((battle.Flags & BattleFlags.OstyPresent) != 0)
            owners[count++] = Limits.OstyOwner;
        for (int position = 0; position < battle.RosterCount; position++)
            owners[count++] = EnemyOwner(battle.Roster[position]);
        return count;
    }

    private static bool Contains(ReadOnlySpan<byte> owners, byte owner)
    {
        foreach (byte candidate in owners)
        {
            if (candidate == owner)
                return true;
        }
        return false;
    }

    private enum RelicPhase : byte
    {
        BeforeSideTurnStart,
        AfterEnergyReset,
        AfterEnergyResetLate,
        BeforeHandDraw,
        AfterPlayerTurnStart,
        AfterPlayerTurnStartLate,
        AfterSideTurnStart,
        PrepareBeforeSideTurnEnd,
        AfterSideTurnEnd,
        AfterBlockCleared,
    }

    /// <summary>
    /// Relic pass in relic order over unmelted player relics (the player participates in the side).
    /// <paramref name="snapshot"/> keeps the listener set fixed for a hook that may pause for a
    /// choice: another listener can add or remove relics while the hook is suspended, and the
    /// native <c>ICombatState.IterateHookListeners</c> snapshot does not call newly added models and
    /// skips removed ones. The default live walk is unchanged for the EndTurn path.
    /// </summary>
    private static void RelicPass(ref Exec x, RelicPhase phase, Side side, bool snapshot = false)
    {
        if (!snapshot)
        {
            for (int index = 0; index < x.B.RelicCount; index++)
            {
                ref Relic relic = ref x.B.Relics[index];
                if ((relic.Flags & RelicFlags.Melted) != 0)
                    continue;
                InvokeRelicPhase(ref x, phase, index, side);
                if (x.NeedsChoice)
                    return;
            }
            return;
        }
        int relicCount = x.B.RelicCount;
        Span<(ushort Id, int Occurrence)> entries = stackalloc (ushort, int)[relicCount];
        int taken = 0;
        for (int index = 0; index < relicCount; index++)
        {
            ref Relic relic = ref x.B.Relics[index];
            if ((relic.Flags & RelicFlags.Melted) != 0)
                continue;
            int occurrence = 0;
            for (int prior = 0; prior < index; prior++)
            {
                if (x.B.Relics[prior].Id == relic.Id)
                    occurrence++;
            }
            entries[taken++] = (relic.Id, occurrence);
        }
        for (int entry = 0; entry < taken; entry++)
        {
            (ushort id, int occurrence) = entries[entry];
            int index = FindRelicOccurrence(x.B, id, occurrence);
            if (index < 0 || (x.B.Relics[index].Flags & RelicFlags.Melted) != 0)
                continue;
            InvokeRelicPhase(ref x, phase, index, side);
            if (x.NeedsChoice)
                return;
        }
    }

    private static int FindRelicOccurrence(in Battle battle, ushort id, int occurrence)
    {
        for (int index = 0; index < battle.RelicCount; index++)
        {
            if (battle.Relics[index].Id != id)
                continue;
            if (occurrence-- == 0)
                return index;
        }
        return -1;
    }

    private static void InvokeRelicPhase(ref Exec x, RelicPhase phase, int index, Side side)
    {
        RelicDef def = Registry.Relic(x.B.Relics[index].Id);
        Listener self = new(ListenerKind.Relic, (byte)index, Limits.PlayerOwner);
        ChoiceSource savedOrigin = x.ChoiceOrigin;
        x.ChoiceOrigin = ListenerChoiceSource(x.B, self);
        try
        {
            switch (phase)
            {
                case RelicPhase.BeforeSideTurnStart: def.BeforeSideTurnStart(ref x, self, side); break;
                case RelicPhase.AfterEnergyReset: def.AfterEnergyReset(ref x, self); break;
                case RelicPhase.AfterEnergyResetLate: def.AfterEnergyResetLate(ref x, self); break;
                case RelicPhase.BeforeHandDraw: def.BeforeHandDraw(ref x, self); break;
                case RelicPhase.AfterPlayerTurnStart: def.AfterPlayerTurnStart(ref x, self); break;
                case RelicPhase.AfterPlayerTurnStartLate: def.AfterPlayerTurnStartLate(ref x, self); break;
                case RelicPhase.AfterSideTurnStart: def.AfterSideTurnStart(ref x, self, side); break;
                case RelicPhase.PrepareBeforeSideTurnEnd: def.PrepareBeforeSideTurnEnd(ref x, self, side); break;
                case RelicPhase.AfterSideTurnEnd: def.AfterSideTurnEnd(ref x, self, side); break;
                case RelicPhase.AfterBlockCleared: def.AfterBlockCleared(ref x, self); break;
                default: throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
            }
        }
        finally
        {
            x.ChoiceOrigin = savedOrigin;
        }
    }

    /// <summary>
    /// The applier cleanup of <c>CorePowerSupport.ApplyEnemyDeathPowers</c> for one newly dead enemy:
    /// Constrict, Hex and Shrink on the player creature whose applier it was, then every Magic Bomb it
    /// applied (listener order). Powers are read directly, without the positive-amount filter.
    /// </summary>
    private static void ApplierDeath(ref Exec x, byte dead)
    {
        foreach (ushort id in ApplierDeathIds.Value)
        {
            int index = FindPower(x.B, Limits.PlayerOwner, id);
            if (index < 0 || x.B.Powers[index].Applier != dead)
                continue;
            Registry.Power(id).ApplierDied(ref x, new Listener(ListenerKind.Power, (byte)index, Limits.PlayerOwner));
            if (x.NeedsChoice)
                return;
        }
        ushort magicBomb = MagicBombId.Value;
        if (magicBomb == ushort.MaxValue)
            return;
        Span<byte> owners = stackalloc byte[Limits.Powers];
        int count = 0;
        Span<byte> creatures = stackalloc byte[Limits.Enemies + 2];
        int creatureCount = ListenerCreatureOrder(x.B, creatures);
        for (int creature = 0; creature < creatureCount; creature++)
        {
            int index = FindPower(x.B, creatures[creature], magicBomb);
            if (index >= 0 && x.B.Powers[index].Applier == dead)
                owners[count++] = creatures[creature];
        }
        for (int entry = 0; entry < count; entry++)
        {
            int index = FindPower(x.B, owners[entry], magicBomb);
            if (index < 0)
                continue;
            Registry.Power(magicBomb).ApplierDied(ref x, new Listener(ListenerKind.Power, (byte)index, owners[entry]));
            if (x.NeedsChoice)
                return;
        }
    }

    // ---- Phase functions, named after the existing ones. ----

    /// <summary>
    /// <c>HookMirrors.BeforeSideTurnStart</c> on the vanilla path: relic batch
    /// (<c>PrepareRelicsBeforeSideTurnStart</c>) then <c>TurnStartPowerSupport.TriggerBeforeSideTurnStart</c>
    /// (Plating pass, Aggression pass, then every other power in listener order).
    /// </summary>
    internal static void BeforeSideTurnStart(ref Exec x, Side side, scoped ReadOnlySpan<byte> participants)
    {
        if (Contains(participants, Limits.PlayerOwner))
        {
            RelicPass(ref x, RelicPhase.BeforeSideTurnStart, side);
            if (x.NeedsChoice)
                return;
        }
        ushort plating = PlatingPowerId.Value;
        ushort aggression = AggressionPowerId.Value;
        if (plating != ushort.MaxValue)
            PowerListenerPass(ref x, PowerPhase.BeforeSideTurnStart, side, participants, Limits.NoOwner, plating, ushort.MaxValue, ushort.MaxValue);
        if (x.NeedsChoice)
            return;
        if (aggression != ushort.MaxValue)
            PowerListenerPass(ref x, PowerPhase.BeforeSideTurnStart, side, participants, Limits.NoOwner, aggression, ushort.MaxValue, ushort.MaxValue);
        if (x.NeedsChoice)
            return;
        PowerListenerPass(ref x, PowerPhase.BeforeSideTurnStart, side, participants, Limits.NoOwner, ushort.MaxValue, plating, aggression);
    }

    /// <summary>Block clear at side start: <c>ShouldClearBlock</c> preventers, then <c>CorePowerSupport.TriggerAfterBlockCleared</c>.</summary>
    internal static void ClearBlockAtSideStart(ref Exec x, byte owner)
    {
        if (x.B.Creature(owner).Block > 0)
        {
            if (ShouldClearBlock(ref x, owner, out Listener preventer))
            {
                DamageBlock(ref x.B.Creature(owner), x.B.Creature(owner).Block, ValueProps.Move);
            }
            else
            {
                // PersistentRelicSupport.TriggerAfterPreventingBlockClear.
                int block = x.B.Creature(owner).Block;
                int retained = HookDispatch.Definition(x.B, preventer).BlockAfterPreventingClear(ref x, preventer, owner, block);
                if (retained < block)
                    DamageBlock(ref x.B.Creature(owner), block - retained, ValueProps.Unpowered);
            }
        }
        // TriggerAfterBlockCleared: BlockNextTurn first, then a listener-order pass, then relics.
        ushort blockNextTurn = BlockNextTurnPowerId.Value;
        if (blockNextTurn != ushort.MaxValue)
        {
            PowerListenerPass(ref x, PowerPhase.AfterBlockCleared, Side.None, default, owner, blockNextTurn, ushort.MaxValue, ushort.MaxValue);
            if (x.NeedsChoice)
                return;
        }
        PowerListenerPass(ref x, PowerPhase.AfterBlockCleared, Side.None, default, owner, ushort.MaxValue, blockNextTurn, ushort.MaxValue);
        if (x.NeedsChoice || owner != Limits.PlayerOwner)
            return;
        RelicPass(ref x, RelicPhase.AfterBlockCleared, Side.None);
    }

    /// <summary>
    /// Native <c>Hook.ShouldClearBlock</c>: the first combat listener that declines is the preventer.
    /// Combat hook listeners are not enumerated while combat is over or ending.
    /// </summary>
    private static bool ShouldClearBlock(ref Exec x, byte owner, out Listener preventer)
    {
        preventer = default;
        if ((Registry.AnyHooks & HookMask.ShouldClearBlock) == 0 || IsOverOrEnding(x.B))
            return true;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        int count = HookDispatch.CollectListeners(x.B, HookMask.ShouldClearBlock, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!HookDispatch.Definition(x.B, listeners[index]).ShouldClearBlock(ref x, listeners[index], owner))
            {
                preventer = listeners[index];
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <c>PersistentPowerSupport.TriggerRampart</c>: every positive RampartPower in listener order
    /// grants its amount as Unpowered Block to each living Turret Operator.
    /// </summary>
    private static void RampartPass(ref Exec x)
    {
        if ((Registry.AnyHooks & HookMask.Rampart) == 0)
            return;
        // PersistentPowerSupport.TriggerRampart walks EffectivePowers() and only filters by amount,
        // unlike the ordinary listener passes: a Living Shield that left the roster keeps its
        // RampartPower row and still grants the Turret Operator its block. Walk the power table in
        // row order instead of CollectListeners, which skips non-roster owners.
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            PowerDef def = Registry.Power(x.B.Powers[index].Id);
            if ((def.Hooks & HookMask.Rampart) == 0 || x.B.Powers[index].Amount <= 0)
                continue;
            def.Rampart(ref x, new Listener(ListenerKind.Power, (byte)index, x.B.Powers[index].Owner));
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// <c>SimulatedCombatState.TriggerSideTurnStart</c>: base effects per participant, then
    /// <c>PersistentPowerSupport.TriggerAfterSideTurnStart</c>, <c>TurnStartPowerSupport.TriggerAfterSideTurnStart</c>
    /// and the relic <c>AfterSideTurnStart</c> batch.
    /// </summary>
    internal static void TriggerSideTurnStart(ref Exec x, Side side, scoped ReadOnlySpan<byte> participants,
        bool decrementPlating, bool isExtraTurn = false)
    {
        foreach (byte owner in participants)
        {
            PowerTypeSequence(ref x, PowerPhase.BaseSideTurnStart, owner, BaseSideTurnStartIds.Value, side, participates: true, decrementPlating);
            if (x.NeedsChoice)
                return;
        }
        foreach (byte owner in participants)
        {
            PowerTypeSequence(ref x, PowerPhase.OwnerAfterSideTurnStart, owner, OwnerAfterSideTurnStartIds.Value, side, participates: true);
            if (x.NeedsChoice)
                return;
        }
        // PersistentPowerSupport.TriggerAfterSideTurnStart: Rampart only at the player side turn
        // start of a non-extra turn.
        if (side == Side.Player && !isExtraTurn && Contains(participants, Limits.PlayerOwner))
        {
            RampartPass(ref x);
            if (x.NeedsChoice)
                return;
        }
        foreach (ushort id in GlobalAfterSideTurnStartIds.Value)
        {
            PowerListenerPass(ref x, PowerPhase.GlobalAfterSideTurnStart, side, participants, Limits.NoOwner, id, ushort.MaxValue, ushort.MaxValue);
            if (x.NeedsChoice)
                return;
        }
        if (Contains(participants, Limits.PlayerOwner))
            RelicPass(ref x, RelicPhase.AfterSideTurnStart, side);
        if (x.NeedsChoice)
            return;
        ResolvePowerAmountChanges(ref x);
    }

    /// <summary><c>CorePowerSupport.CompletePlayerEarlySideTurnEndEffects</c>.</summary>
    internal static void CompletePlayerEarlySideTurnEnd(ref Exec x)
        => PowerTypeSequence(ref x, PowerPhase.CompletePlayerEarlySideTurnEnd, Limits.PlayerOwner, CompletePlayerEarlyIds.Value,
            Side.Player, participates: true);

    /// <summary><c>CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects</c> plus the relic and late passes of phase two.</summary>
    internal static void PlayerTurnEndPhaseTwo(ref Exec x)
    {
        // Hook.AfterSideTurnEnd is one listener iteration: its over/ending check runs when the
        // iterator starts, before any power or relic listener. A listener later in that same pass
        // can kill the last enemy, and the remaining listeners still run. Capture the phase-entry
        // state for the relic pass instead of re-reading IsOverOrEnding after the power passes.
        bool sideTurnEndStarted = !IsOverOrEnding(x.B);
        Span<byte> participants = stackalloc byte[1];
        participants[0] = Limits.PlayerOwner;
        PowerTypeSequence(ref x, PowerPhase.PlayerSideTurnEnd, Limits.PlayerOwner, PlayerSideTurnEndIds.Value, Side.Player, participates: true);
        if (x.NeedsChoice)
            return;
        EndTurnRegularAndBatch(ref x, Side.Player, participants);
        if (x.NeedsChoice)
            return;
        ResolvePowerAmountChanges(ref x);
        if (x.NeedsChoice)
            return;
        TriggerTransientSideTurnEndPowers(ref x, Side.Player, participants);
        // Native Hook.AfterSideTurnEnd iterates combat listeners, and IsOverOrEnding makes that
        // iterator empty before the pass starts. The check is therefore at the phase entry: a
        // listener that kills the last enemy later in the same pass still lets the remaining
        // listeners run (the native iterator snapshot), while a combat already ending when this
        // phase starts must not reset ArtOfWar/Kusarigama or run the other turn-end relics.
        // This also covers a pending loss: both ending paths drop the whole pass.
        if (sideTurnEndStarted)
        {
            RelicPass(ref x, RelicPhase.AfterSideTurnEnd, Side.Player);
            if (x.NeedsChoice)
                return;
        }
        if (IsOverOrEnding(x.B))
            return;
        AfterSideTurnEndLatePass(ref x, Side.Player, participants);
        if (!x.NeedsChoice)
            NormalizeCardAfflictions(ref x);
    }

    /// <summary><c>CorePowerSupport.TriggerEnemySideTurnEndEffects</c>.</summary>
    internal static void TriggerEnemySideTurnEndEffects(ref Exec x, scoped ReadOnlySpan<byte> enemies)
    {
        x.EnemySideTurnEndReached = true;
        // EndTurnPowerSupport.TriggerVeryEarly has no supported source (Asleep is rejected at admission).
        TriggerEnemyDoom(ref x, enemies);
        if (x.NeedsChoice)
            return;
        foreach (byte owner in enemies)
        {
            PowerTypeSequence(ref x, PowerPhase.EnemySideTurnEnd, owner, EnemySideTurnEndIds.Value, Side.Enemy, participates: true);
            if (x.NeedsChoice)
                return;
        }
        EndTurnRegularAndBatch(ref x, Side.Enemy, enemies);
        if (x.NeedsChoice)
            return;
        ResolvePowerAmountChanges(ref x);
        if (x.NeedsChoice)
            return;
        TriggerTransientSideTurnEndPowers(ref x, Side.Enemy, enemies);
        TickDurations(ref x.B);
        AfterSideTurnEndLatePass(ref x, Side.Enemy, enemies);
    }

    /// <summary>
    /// <c>HookMirrors.AfterSideTurnEndLate</c>: a fresh combat-listener snapshot after the regular
    /// end-of-side hooks; only listeners whose definition overrides the Ext hook run.
    /// </summary>
    private static void AfterSideTurnEndLatePass(ref Exec x, Side side, scoped ReadOnlySpan<byte> participants)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.AfterSideTurnEndLate) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        int count = HookDispatch.CollectListeners(x.B, HookMaskExt.AfterSideTurnEndLate, listeners);
        for (int index = 0; index < count; index++)
        {
            Listener self = listeners[index];
            HookDispatch.Definition(x.B, self).AfterSideTurnEndLate(ref x, self, side, Contains(participants, self.Owner));
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// <c>EndTurnPowerSupport.TriggerEnemyDoom</c>: every living participant whose Doom amount reaches
    /// its current HP dies together (so <c>ReattachPower</c>'s fatal count sees the whole batch).
    /// </summary>
    private static void TriggerEnemyDoom(ref Exec x, scoped ReadOnlySpan<byte> enemies)
    {
        ushort doom = DoomPowerId.Value;
        if (doom == ushort.MaxValue)
            return;
        Span<byte> doomed = stackalloc byte[Limits.Enemies];
        int count = 0;
        foreach (byte owner in enemies)
        {
            ref readonly Creature creature = ref x.B.Creature(owner);
            if (creature.IsAlive && GetAmount(x.B, owner, doom) >= creature.CurrentHp)
                doomed[count++] = owner;
        }
        if (count > 0)
            DoomKill(ref x, doomed[..count]);
    }

    /// <summary>
    /// <c>EndTurnPowerSupport.TriggerRegular</c>: its listener-order pass, <c>ResetPowerLifecycleTurn</c>
    /// (no supported per-turn power state yet), then the <c>TriggerBatch048</c> pass.
    /// </summary>
    private static void EndTurnRegularAndBatch(ref Exec x, Side side, scoped ReadOnlySpan<byte> participants)
    {
        PowerListenerPass(ref x, PowerPhase.EndTurnRegular, side, participants, Limits.NoOwner, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);
        if (x.NeedsChoice)
            return;
        // EndTurnPowerSupport.TriggerRegular's ResetPowerLifecycleTurn for every participant.
        foreach (byte owner in participants)
        {
            if (owner == Limits.PlayerOwner)
                x.B.SetCounter(Counter.SkillPlayedThisSideTurn, 0);
        }
        PowerListenerPass(ref x, PowerPhase.EndTurnBatch048, side, participants, Limits.NoOwner, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);
    }

    /// <summary><c>CorePowerSupport.TriggerTransientSideTurnEndPowers</c>.</summary>
    private static void TriggerTransientSideTurnEndPowers(ref Exec x, Side side, scoped ReadOnlySpan<byte> participants)
    {
        Span<byte> creatures = stackalloc byte[Limits.Enemies + 2];
        int count = ListenerCreatureOrder(x.B, creatures);
        ushort flameBarrier = FlameBarrierPowerId.Value;
        for (int index = 0; index < count; index++)
        {
            byte creature = creatures[index];
            Side creatureSide = IsPlayerSide(creature) ? Side.Player : Side.Enemy;
            if (flameBarrier != ushort.MaxValue && creatureSide != side && GetAmount(x.B, creature, flameBarrier) > 0)
                SetPowerAmount(ref x.B, creature, flameBarrier, 0);
            if (!Contains(participants, creature))
                continue;
            foreach (ushort id in TransientRemovalIds.Value)
            {
                if (GetAmount(x.B, creature, id) != 0)
                    SetPowerAmount(ref x.B, creature, id, 0);
            }
            foreach (ushort id in TransientDecrementIds.Value)
            {
                int amount = GetAmount(x.B, creature, id);
                if (amount > 0)
                    SetPowerAmount(ref x.B, creature, id, amount - 1);
            }
        }
    }

    /// <summary><c>PersistentPowerSupport.TriggerAfterEnergyReset</c>: the player's positive powers in listener order.</summary>
    internal static void PowersAfterEnergyReset(ref Exec x)
        => PowerListenerPass(ref x, PowerPhase.AfterEnergyReset, Side.Player, default, Limits.PlayerOwner, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);

    /// <summary><c>TurnStartPowerSupport.TriggerBeforeHandDraw</c>.</summary>
    internal static void PowerBeforeHandDraw(ref Exec x)
        => PowerListenerPass(ref x, PowerPhase.BeforeHandDraw, Side.Player, default, PlayerSideOwners,
            ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);

    /// <summary>
    /// <c>TurnStartPowerSupport.TriggerBeforeHandDraw</c> then the relic stage of
    /// <c>PrepareBeforeHandDraw</c>. The rootSetup preamble calls the power and relic stages
    /// separately in native listener order (powers, then relics) and runs the returning-card stage
    /// afterwards; this combined form keeps the EndTurn path unchanged.
    /// </summary>
    internal static void BeforeHandDraw(ref Exec x)
    {
        PowerBeforeHandDraw(ref x);
        if (x.NeedsChoice)
            return;
        // Relic stage: the modelled BeforeHandDraw relics (Pendulum, PollinousCore) act through
        // ModifyHandDraw, which the turn-start path reads separately. First-turn-only generation
        // (FuneraryMask) runs from StartFirstPlayerTurn, not from every round transition.
        ReturnCardsToHand(ref x);
    }

    /// <summary>
    /// <c>SimulatedCombatState.PrepareBeforeHandDraw</c>'s returning-card stage: the cards played last
    /// turn with Bolas/ThrummingHatchet (native <c>_returnToHandNextTurn</c>) move to hand in current
    /// pile order. Their membership is carried on <see cref="Card.ReturnToHandNextTurn"/>.
    /// </summary>
    private static void ReturnCardsToHand(ref Exec x)
    {
        // The existing engine moves every card in _returnToHandNextTurn (a set keyed by the played
        // object) to the hand bottom. Re-scan for the next flagged value instead of capturing
        // positions: moving an earlier card shifts the later ones, and a captured occurrence no longer
        // addresses the same card when two returning copies are byte-equal.
        while (true)
        {
            int position = -1;
            for (int candidate = 0; candidate < x.B.CardCount; candidate++)
            {
                if (x.B.Cards[candidate].ReturnToHandNextTurn)
                {
                    position = candidate;
                    break;
                }
            }
            if (position < 0)
                return;
            int handStart = Piles.Start(x.B, Pile.Hand);
            int handEnd = handStart + x.B.HandCount;
            if (position >= handStart && position < handEnd)
            {
                x.B.Cards[position].ReturnToHandNextTurn = false;
                continue;
            }
            Pile pile = Pile.Hand;
            int index = position;
            ReadOnlySpan<Pile> sources = [Pile.Draw, Pile.Discard, Pile.Exhaust, Pile.Play];
            foreach (Pile candidate in sources)
            {
                int start = Piles.Start(x.B, candidate);
                int pileCount = Piles.Count(x.B, candidate);
                if (position < start || position >= start + pileCount)
                    continue;
                pile = candidate;
                index = position - start;
                break;
            }
            x.B.Cards[position].ReturnToHandNextTurn = false;
            MoveCard(ref x.B, pile, index, Pile.Hand, PilePosition.Bottom);
        }
    }

    /// <summary>Position of the <paramref name="occurrence"/>-th byte-equal card over the whole battle.</summary>
    private static bool TryLocateCard(in Battle battle, in Card card, int occurrence, out int position)
    {
        for (int index = 0; index < battle.CardCount; index++)
        {
            if (!Fingerprint.Equal(battle.Cards[index], card))
                continue;
            if (occurrence-- == 0)
            {
                position = index;
                return true;
            }
        }
        position = -1;
        return false;
    }

    /// <summary>
    /// <c>Hook.AfterPlayerTurnStart</c> on the vanilla path: powers, then relics, each in the regular
    /// and late passes (native iterates the same listener list once per pass).
    /// </summary>
    internal static void AfterPlayerTurnStart(ref Exec x, bool snapshotRelics = false)
    {
        PowerListenerPass(ref x, PowerPhase.AfterPlayerTurnStart, Side.Player, default, Limits.PlayerOwner, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);
        if (x.NeedsChoice)
            return;
        RelicPass(ref x, RelicPhase.AfterPlayerTurnStart, Side.Player, snapshotRelics);
        if (x.NeedsChoice)
            return;
        RelicPass(ref x, RelicPhase.AfterPlayerTurnStartLate, Side.Player, snapshotRelics);
    }

    /// <summary><c>PersistentPowerSupport.GetModifiedMaxEnergy</c>: every combat listener's modifier.</summary>
    internal static int ModifiedMaxEnergy(ref Exec x)
    {
        decimal result = x.R.BaseMaxEnergy;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        int count = HookDispatch.CollectListeners(x.B, HookMask.ModifyMaxEnergy, listeners);
        for (int index = 0; index < count; index++)
            result = HookDispatch.Definition(x.B, listeners[index]).ModifyMaxEnergy(ref x, listeners[index], result);
        return Math.Max(0, (int)result);
    }

    /// <summary><c>PersistentPowerSupport.GetModifiedHandDraw</c>: normal pass then late pass over the combat listeners.</summary>
    internal static int ModifiedHandDraw(ref Exec x, int baseDraw)
    {
        // Native Hook.ModifyHandDraw goes through IterateCombatHookListeners, which is empty while
        // the combat is over or ending. A turn can still be prepared at that boundary (the native
        // StartTurn keeps running), but the listeners must not advance their state (Pendulum's
        // TurnsSeen, DrawCardsNextTurn and the other ModifyHandDraw powers/relics).
        if (IsOverOrEnding(x.B))
            return Math.Max(0, baseDraw);
        decimal result = baseDraw;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        int count = HookDispatch.CollectListeners(x.B, HookMask.ModifyHandDraw, listeners);
        for (int index = 0; index < count; index++)
            result = HookDispatch.Definition(x.B, listeners[index]).ModifyHandDraw(ref x, listeners[index], result);
        count = HookDispatch.CollectListeners(x.B, HookMask.ModifyHandDrawLate, listeners);
        for (int index = 0; index < count; index++)
            result = HookDispatch.Definition(x.B, listeners[index]).ModifyHandDrawLate(ref x, listeners[index], result);
        return Math.Max(0, (int)result);
    }
}
