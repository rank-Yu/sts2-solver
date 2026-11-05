namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>
    /// Mirrors the <c>UsePotion</c> branch of <c>CombatBeamSolver.Replay</c> with
    /// <c>PotionExecutionSupport.Prepare</c> and <c>Complete</c>: consume the slot, <c>BeforePotionUsed</c>,
    /// the potion's effect, <c>AfterPotionUsed</c> while the player lives, the enemy-death pass, then
    /// shuffle accounting, the win check and a requested turn end.
    /// </summary>
    /// <summary>
    /// <c>PowerLifecycleSupport.UpdateSurroundedForTarget</c> for a potion's target: the player's
    /// SurroundedPower faces the back-attack marker of the targeted enemy.
    /// </summary>
    private static void UpdateSurroundedForPotionTarget(ref Exec x, byte target)
    {
        if (!Registry.TryPower("SURROUNDED_POWER", out ushort surrounded))
            return;
        bool left = Registry.TryPower("BACK_ATTACK_LEFT_POWER", out ushort backLeft)
            && GetAmount(x.B, target, backLeft) > 0;
        bool right = Registry.TryPower("BACK_ATTACK_RIGHT_POWER", out ushort backRight)
            && GetAmount(x.B, target, backRight) > 0;
        if (!left && !right)
            return;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref Power power = ref x.B.Powers[index];
            if (power.Id != surrounded || power.Owner != Limits.PlayerOwner || power.Amount <= 0)
                continue;
            power.Aux0 = left ? 1 : 0;
        }
    }

    private static void UsePotionAction(ref Exec x, in PlayerAction action)
    {
        if (action.PotionSlot >= x.B.PotionSlotCount)
            throw new ArgumentOutOfRangeException(nameof(action), action.PotionSlot, "药水槽位越界。");
        ushort stored = x.B.Potions[action.PotionSlot];
        if (stored == 0)
            throw new InvalidOperationException($"药水槽位 {action.PotionSlot} 为空。");
        PotionDef def = Registry.Potion((ushort)(stored - 1));
        // SimulatedCombatState.ConsumePotion: a freeFromEntropic slot records a zero strategic cost and
        // clears its free flag. The root-slot diff cannot see generated potions, so the scorer adds the
        // generated-use counter; this explicit use is one of them.
        int freeMask = x.B.Counter(Counter.EntropicFreePotionMask);
        int slotBit = 1 << action.PotionSlot;
        bool freeFromEntropic = (freeMask & slotBit) != 0;
        if (freeFromEntropic)
            x.B.SetCounter(Counter.EntropicFreePotionMask, freeMask & ~slotBit);
        // SimulatedCombatState.PotionUses records every use; a potion that is not the root potion of
        // this slot was generated in combat and its use is invisible to the root-slot diff.
        if (stored != x.R.RootPotion(action.PotionSlot))
        {
            x.B.AddCounter(Counter.GeneratedPotionUseCount, 1);
            RecordGeneratedPotionStrategicCost(ref x.B,
                freeFromEntropic ? 0 : x.R.Potion(def.Id).StrategicCost, automatic: false);
        }

        // Prepare: ConsumePotion (the slot empties; the use record only feeds search scoring), then
        // BeforePotionUsed, which has no supported listeners yet.
        x.B.Potions[action.PotionSlot] = 0;
        x.Target = action.Target;
        x.Card = default;
        x.Stats = null;
        // SimulatedCombatState.BeforePotionUsed: SurroundedPower's facing follows the potion's target
        // before the potion effect runs.
        UpdateSurroundedForPotionTarget(ref x,
            action.Target != Limits.NoOwner ? action.Target : Limits.PlayerOwner);
        x.EffectDepth++;
        ChoiceSource savedOrigin = x.ChoiceOrigin;
        x.ChoiceOrigin = new ChoiceSource(ChoiceSourceKind.Potion, def.Id);
        try
        {
            def.OnUse(ref x);
        }
        finally
        {
            x.ChoiceOrigin = savedOrigin;
        }
        x.EffectDepth--;
        if (x.NeedsChoice)
            return;
        // Complete: potion choices are not supported yet. AfterPotionUsed runs while the player lives,
        // before the power-amount resolution and the enemy-death pass (PotionExecutionSupport.Complete).
        if (x.B.Player.IsAlive)
        {
            HookDispatch.AfterPotionUsed(ref x, def.Id);
            if (x.NeedsChoice)
                return;
        }
        ResolvePowerAmountChanges(ref x);
        if (x.NeedsChoice)
            return;
        // PotionExecutionSupport.Complete runs the enemy-death pass after the effect.
        ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        // SettleReplayActionBoundary: no supported power reactions or normalizations.
        CheckWinCondition(ref x.B);
        AdvanceForcedTurnEnds(ref x);
    }

    /// <summary>
    /// Accumulates the packed strategic cost of generated potion uses
    /// (<see cref="Counter.GeneratedPotionStrategicCost"/>): low 16 bits explicit, high 16 bits
    /// automatic. Free-from-Entropic uses pass zero.
    /// </summary>
    public static void RecordGeneratedPotionStrategicCost(ref Battle battle, int cost, bool automatic)
    {
        if (cost < 0 || cost > 0xFFFF)
            throw new CapacityException("GeneratedPotionStrategicCost", 0xFFFF);
        int packed = battle.Counter(Counter.GeneratedPotionStrategicCost);
        int total = automatic ? (int)((uint)packed >> 16) : packed & 0xFFFF;
        if (total > 0xFFFF - cost)
            throw new CapacityException("GeneratedPotionStrategicCost", 0xFFFF);
        total += cost;
        battle.SetCounter(Counter.GeneratedPotionStrategicCost,
            automatic ? (total << 16) | (packed & 0xFFFF) : (packed & unchecked((int)0xFFFF0000)) | total);
    }

    /// <summary>A variable of the potion being used, from the rule table.</summary>
    public static decimal PotionVar(in Exec x, PotionDef def, int index) => x.R.Potion(def.Id).Vars[index];

    public static ValueProps PotionVarProps(in Exec x, PotionDef def, int index) => x.R.Potion(def.Id).VarProps[index];

    /// <summary>
    /// Mirrors <c>PotionFactory.CreateRandomPotionInCombat</c>: one CombatPotionGeneration
    /// <c>NextFloat</c> for the rarity (Rare <= 0.1, Uncommon <= 0.35, else Common), then one
    /// <c>NextItem</c> draw over the candidates of that rarity in frozen pool order.
    /// </summary>
    public static ushort GenerateRandomPotion(ref Battle battle, Rules rules)
        => GenerateRandomPotionFromPool(ref battle, rules, rules.PotionPool);

    /// <summary>
    /// Mirrors <c>PotionFactory.CreateRandomPotionOutOfCombat</c> (EntropicBrew): the same rarity and
    /// <c>NextItem</c> draws as <see cref="GenerateRandomPotion"/>, but over the unfiltered character +
    /// shared pool frozen at admission (potions that cannot be generated in combat are included).
    /// </summary>
    public static ushort GenerateRandomPotionOutOfCombat(ref Battle battle, Rules rules)
        => GenerateRandomPotionFromPool(ref battle, rules, rules.OutOfCombatPotionPool);

    private static ushort GenerateRandomPotionFromPool(ref Battle battle, Rules rules,
        IReadOnlyList<ushort> pool)
    {
        if (pool.Count == 0)
            throw new UnsupportedException("随机药水池为空。");
        ref RngState rng = ref battle.RngStream(RngStream.CombatPotionGeneration);
        float roll = rng.NextFloat(1f);
        PotionRarity rarity = roll <= 0.1f
            ? PotionRarity.Rare
            : roll <= 0.35f ? PotionRarity.Uncommon : PotionRarity.Common;
        Span<ushort> candidates = stackalloc ushort[pool.Count];
        int count = 0;
        for (int index = 0; index < pool.Count; index++)
        {
            if (rules.Potion(pool[index]).Rarity == rarity)
                candidates[count++] = pool[index];
        }
        if (count == 0)
            throw new UnsupportedException($"随机药水没有稀有度 {rarity} 的候选。");
        return candidates[rng.NextInt(0, count)];
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.TryProcurePotion</c> for a supported root: the first empty slot
    /// receives the potion. <c>ShouldProcurePotion</c> listeners (Sozu) are consulted first;
    /// <c>AfterPotionProcured</c> reactions are outside this effect's scope.
    /// </summary>
    public static bool TryProcurePotion(ref Battle battle, ushort potionId)
        => TryProcurePotion(ref battle, potionId, freeFromEntropic: false);

    /// <summary>
    /// <see cref="TryProcurePotion(ref Battle, ushort)"/> with the native <c>freeFromEntropic</c>
    /// flag: the slot is marked in <see cref="Counter.EntropicFreePotionMask"/> so a later use
    /// records zero strategic cost.
    /// </summary>
    public static bool TryProcurePotion(ref Battle battle, ushort potionId, bool freeFromEntropic)
    {
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if ((relic.Flags & RelicFlags.Melted) != 0)
                continue;
            if (!Registry.Relic(relic.Id).ShouldProcurePotion(in battle, relic.Id, potionId))
                return false;
        }
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            if (battle.Potions[slot] != 0)
                continue;
            battle.Potions[slot] = (ushort)(potionId + 1);
            if (freeFromEntropic)
                battle.SetCounter(Counter.EntropicFreePotionMask,
                    battle.Counter(Counter.EntropicFreePotionMask) | (1 << slot));
            return true;
        }
        return false;
    }
}
