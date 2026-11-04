namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    private const int MaxChanneledOrbs = 1000;

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.OrbChannel(Player, OrbModel)</c> / <c>OrbCmd.Channel</c>:
    /// a channel that finds the queue full first evokes the next (leftmost) orb, then enqueues; the
    /// player's own queue is used and the first channel of a character with no base slots adds one.
    /// Returns whether the orb was enqueued.
    /// </summary>
    public static bool ChannelOrb(ref Exec x, ushort orbId)
    {
        if (IsOverOrEnding(x.B) || x.NeedsChoice)
            return false;
        ref Battle battle = ref x.B;
        // CombatPredictionSimulator caps the simulated channels at 1000, records the risk and stops
        // this channel. The risk travels with the battle and is carried into live-root comparisons.
        if (battle.Counter(Counter.OrbChannels) >= MaxChanneledOrbs)
        {
            battle.Flags |= BattleFlags.HasRisk;
            return false;
        }
        if (x.R.BaseOrbSlotCount == 0 && battle.OrbCapacity == 0)
            AddOrbSlots(ref battle, 1);
        Orb orb = new() { Id = checked((byte)orbId), Aux = Registry.Orb(orbId).InitialAux };
        if (battle.OrbCapacity > 0 && battle.OrbCount >= battle.OrbCapacity)
        {
            // Vanilla evicts the leftmost orb, then enqueues. The existing engine aborts the prediction
            // if the evoke synchronously refills the freed slot; Sts2Solver cannot represent the resulting
            // risk either, so it rejects instead of inventing another evoke.
            EvokeNext(ref x);
            if (x.NeedsChoice)
                return false;
            if (battle.OrbCount >= battle.OrbCapacity)
                throw new UnsupportedException("满槽激发后球槽仍无空位。");
        }
        if (battle.OrbCapacity == 0 || battle.OrbCount >= battle.OrbCapacity)
            return false;
        if (battle.OrbCount >= Limits.Orbs)
            throw new CapacityException("Orbs", Limits.Orbs);
        battle.Orbs[battle.OrbCount++] = orb;
        battle.AddCounter(Counter.OrbChannels, 1);
        if (IsLightning(orbId))
            battle.AddCounter(Counter.LightningChannels, 1);
        HookDispatch.AfterOrbChanneled(ref x, orb);
        return !x.NeedsChoice;
    }

    /// <summary>Channels <paramref name="count"/> orbs of one type, stopping when a channel fails.</summary>
    public static void ChannelOrbs(ref Exec x, ushort orbId, int count)
    {
        for (int index = 0; index < count; index++)
        {
            if (!ChannelOrb(ref x, orbId))
                return;
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.OrbEvokeNext</c> / <c>OrbCmd.EvokeNext</c>: the leftmost orb
    /// evoked <paramref name="repeat"/> times; only the last evoke dequeues (Dualcast, MultiCast,
    /// Shatter). Pending enemy deaths resolve after each evoke, as the existing engine does.
    /// </summary>
    public static void EvokeNext(ref Exec x, int repeat = 1, bool dequeue = true)
    {
        if (x.NeedsChoice || x.B.OrbCount == 0)
            return;
        for (int index = 0; index < repeat; index++)
        {
            EvokeOrb(ref x, 0, dequeue && index == repeat - 1);
            if (x.NeedsChoice)
                return;
            ApplyEnemyDeathPowers(ref x);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.OrbEvoke</c> / <c>OrbCmd.Evoke</c>: the dequeued orb value
    /// is captured before the effect, the evoke runs, then <c>AfterOrbEvoked</c> gets its targets.
    /// </summary>
    public static void EvokeOrb(ref Exec x, int index, bool dequeue = true)
    {
        if (IsOverOrEnding(x.B) || x.NeedsChoice)
            return;
        if ((uint)index >= (uint)x.B.OrbCount)
            return;
        Orb orb = x.B.Orbs[index];
        if (dequeue)
            RemoveOrbAt(ref x.B, index);
        Span<byte> targets = stackalloc byte[Limits.Enemies + 1];
        int targetCount = Registry.Orb(orb.Id).Evoke(ref x, orb, targets);
        if (targetCount < 0 || targetCount > targets.Length)
            throw new InvalidOperationException($"球 {Registry.Orb(orb.Id).NativeId} 的激发目标数 {targetCount} 越界。");
        if (x.NeedsChoice)
            return;
        HookDispatch.AfterOrbEvoked(ref x, orb, targets[..targetCount]);
    }

    /// <summary>
    /// Mirrors <c>CombatPredictionSimulator.OrbPassive</c> / <c>OrbCmd.Passive</c>: one passive without
    /// <c>ModifyOrbPassiveTriggerCounts</c>, then the deferred enemy-death pass.
    /// </summary>
    public static void OrbPassiveAt(ref Exec x, int orbIndex, byte target = Limits.NoOwner)
    {
        if (IsOverOrEnding(x.B) || x.NeedsChoice)
            return;
        if ((uint)orbIndex >= (uint)x.B.OrbCount)
            throw new UnsupportedException("被动结算时球槽已为空。");
        Orb orb = x.B.Orbs[orbIndex];
        Registry.Orb(orb.Id).Passive(ref x, orbIndex, target);
        if (x.NeedsChoice)
            return;
        ApplyEnemyDeathPowers(ref x);
    }

    /// <summary>
    /// <c>OrbModel.TriggerPassive</c>: query <c>ModifyOrbPassiveTriggerCounts</c> first, then run that
    /// many passives. Each passive resolves pending enemy deaths (the existing engine's
    /// <c>TriggerOrbPassive</c> → <c>OrbPassive</c> path).
    /// </summary>
    public static void TriggerOrbPassive(ref Exec x, int orbIndex)
    {
        if (x.NeedsChoice)
            return;
        if ((uint)orbIndex >= (uint)x.B.OrbCount)
            throw new UnsupportedException("被动结算时球槽已为空。");
        Orb orb = x.B.Orbs[orbIndex];
        int triggerCount = HookDispatch.ModifyOrbPassiveTriggerCounts(x.B, x.R, orb, orbIndex, 1);
        if (x.NeedsChoice)
            return;
        for (int index = 0; index < triggerCount; index++)
        {
            if ((uint)orbIndex >= (uint)x.B.OrbCount || x.B.Orbs[orbIndex].Id != orb.Id)
                throw new UnsupportedException("被动结算期间充能球队列发生了变化。");
            OrbPassiveAt(ref x, orbIndex);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// <c>OrbQueue.BeforeTurnEnd</c>: iterate a snapshot of the queue in order; each orb's
    /// <c>BeforeTurnEndOrbTrigger</c> runs (Plasma's is empty). Once the combat is ending or over the
    /// existing engine's remaining triggers are no-ops, so the loop stops.
    /// </summary>
    public static void TriggerBeforeTurnEndOrbs(ref Exec x)
    {
        Span<Orb> snapshot = stackalloc Orb[Limits.Orbs];
        int count = x.B.OrbCount;
        for (int index = 0; index < count; index++)
            snapshot[index] = x.B.Orbs[index];
        for (int index = 0; index < count; index++)
        {
            if (x.NeedsChoice || IsOverOrEnding(x.B))
                return;
            if ((uint)index >= (uint)x.B.OrbCount || x.B.Orbs[index].Id != snapshot[index].Id)
                throw new UnsupportedException("回合末被动前充能球队列与快照不一致。");
            Registry.Orb(snapshot[index].Id).BeforeTurnEndOrbTrigger(ref x, index);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// <c>OrbQueue.AfterTurnStart</c>: a snapshot of the queue, each orb's
    /// <c>AfterTurnStartOrbTrigger</c> (only Plasma triggers its passive).
    /// </summary>
    public static void TriggerAfterTurnStartOrbs(ref Exec x)
    {
        Span<Orb> snapshot = stackalloc Orb[Limits.Orbs];
        int count = x.B.OrbCount;
        for (int index = 0; index < count; index++)
            snapshot[index] = x.B.Orbs[index];
        for (int index = 0; index < count; index++)
        {
            if (x.NeedsChoice)
                return;
            if ((uint)index >= (uint)x.B.OrbCount || x.B.Orbs[index].Id != snapshot[index].Id)
                throw new UnsupportedException("回合开始被动前充能球队列与快照不一致。");
            Registry.Orb(snapshot[index].Id).AfterTurnStartOrbTrigger(ref x, index);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>Mirrors <c>CombatPredictionSimulator.AddOrbSlots</c> / <c>OrbCmd.AddSlots</c>.</summary>
    public static void AddOrbSlots(ref Battle battle, int amount)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        int added = Math.Min(Limits.Orbs - battle.OrbCapacity, amount);
        if (added > 0)
            battle.OrbCapacity = checked((byte)(battle.OrbCapacity + added));
    }

    /// <summary>
    /// Mirrors <c>SimOrbQueue.RemoveCapacity</c> / <c>OrbCmd.RemoveSlots</c>: clamp at zero and drop
    /// orbs from the back until the queue fits. No evoke hooks run.
    /// </summary>
    public static void RemoveOrbSlots(ref Battle battle, int amount)
    {
        int capacity = Math.Max(0, battle.OrbCapacity - amount);
        battle.OrbCapacity = checked((byte)capacity);
        while (battle.OrbCount > battle.OrbCapacity)
            RemoveOrbAt(ref battle, battle.OrbCount - 1);
    }

    /// <summary>Removes one orb and compacts the queue; the caller has already consumed its value.</summary>
    public static void RemoveOrbAt(ref Battle battle, int index)
    {
        if ((uint)index >= (uint)battle.OrbCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "球槽没有这个位置。");
        for (int next = index + 1; next < battle.OrbCount; next++)
            battle.Orbs[next - 1] = battle.Orbs[next];
        battle.Orbs[battle.OrbCount - 1] = default;
        battle.OrbCount--;
    }

    /// <summary>Current passive value with the <c>ModifyOrbValue</c> listeners (Focus). A pure query.</summary>
    public static decimal OrbPassiveValue(in Battle battle, Rules rules, int index)
    {
        if ((uint)index >= (uint)battle.OrbCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "球槽没有这个位置。");
        Orb orb = battle.Orbs[index];
        OrbDef def = Registry.Orb(orb.Id);
        decimal raw = def.BasePassiveValue(battle, orb);
        return def.PassiveUsesModifyOrbValue ? HookDispatch.ModifyOrbValue(battle, rules, orb, raw) : raw;
    }

    /// <summary>Current evoke value with the <c>ModifyOrbValue</c> listeners. A pure query.</summary>
    public static decimal OrbEvokeValue(in Battle battle, Rules rules, int index)
    {
        if ((uint)index >= (uint)battle.OrbCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "球槽没有这个位置。");
        Orb orb = battle.Orbs[index];
        OrbDef def = Registry.Orb(orb.Id);
        decimal raw = def.BaseEvokeValue(battle, orb);
        return def.EvokeUsesModifyOrbValue ? HookDispatch.ModifyOrbValue(battle, rules, orb, raw) : raw;
    }

    /// <summary>
    /// <c>OrbModel.GetRandomOrb</c>: one <c>CombatOrbGeneration.NextItem</c> over the native
    /// <c>_validOrbs</c> order (Lightning, Frost, Dark, Plasma, Glass).
    /// </summary>
    public static ushort RandomOrb(ref Battle battle)
    {
        int pick = battle.RngStream(RngStream.CombatOrbGeneration).NextInt(0, 5);
        return pick switch
        {
            0 => OrbId("LIGHTNING_ORB"),
            1 => OrbId("FROST_ORB"),
            2 => OrbId("DARK_ORB"),
            3 => OrbId("PLASMA_ORB"),
            4 => OrbId("GLASS_ORB"),
            _ => throw new InvalidOperationException($"随机球索引 {pick} 越界。"),
        };
    }

    /// <summary>Number of orbs in the queue whose native id is <paramref name="nativeId"/>.</summary>
    public static int CountOrbs(in Battle battle, string nativeId)
    {
        if (!Registry.TryOrb(nativeId, out ushort id))
            return 0;
        int count = 0;
        for (int index = 0; index < battle.OrbCount; index++)
        {
            if (battle.Orbs[index].Id == id)
                count++;
        }
        return count;
    }

    /// <summary>Number of distinct orb ids in the queue.</summary>
    public static int DistinctOrbCount(in Battle battle)
    {
        Span<byte> seen = stackalloc byte[byte.MaxValue + 1];
        seen.Clear();
        int count = 0;
        for (int index = 0; index < battle.OrbCount; index++)
        {
            byte id = battle.Orbs[index].Id;
            if (seen[id] != 0)
                continue;
            seen[id] = 1;
            count++;
        }
        return count;
    }

    /// <summary>Whether the queue has any orb whose native id is <paramref name="nativeId"/>.</summary>
    public static bool HasOrb(in Battle battle, string nativeId)
        => CountOrbs(battle, nativeId) > 0;

    /// <summary>Internal id of a native orb, or a hard boundary when the definition is absent.</summary>
    public static ushort OrbId(string nativeId)
        => Registry.TryOrb(nativeId, out ushort id)
            ? id
            : throw new UnsupportedException($"充能球 {nativeId} 没有 Sts2Solver 定义。");

    private static bool IsLightning(ushort orbId)
        => Registry.TryOrb("LIGHTNING_ORB", out ushort lightning) && lightning == orbId;
}
