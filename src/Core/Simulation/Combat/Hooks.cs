namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Hook dispatch mirroring <c>HookMirrors</c>. Listeners are visited in the existing engine's
/// combat listener order (<c>SimulatedCombatState</c> list construction): for each creature —
/// player, Osty, then enemies in roster order — its powers, then the player's relics and potions or
/// the enemy's monster model; then the player's orbs; then cards in Hand, Draw, Discard, Exhaust and
/// Play order, each followed by its affliction and enchantment.
/// </summary>
[System.Runtime.CompilerServices.SkipLocalsInit]
public static partial class HookDispatch
{
    /// <summary>
    /// Upper bound of <see cref="CollectListeners"/> output for this battle: every fixed table can
    /// fill, and each of the <c>CardCount</c> cards contributes at most three listeners (card,
    /// affliction, enchantment). Callers size stack buffers with this instead of the absolute
    /// 640-card ceiling so a normal deck does not reserve the maximum frame.
    /// </summary>
    public static int MaxListenerCount(in Battle battle, Rules rules)
        => Limits.Powers + rules.Relics.ListenerCapacity + Limits.PotionSlots
            + Limits.Enemies + Limits.Orbs + battle.CardCount * 3;

    /// <summary>Collects the listeners whose definition overrides <paramref name="hook"/>, in dispatch order.</summary>
    public static int CollectListeners(in Battle battle, Rules rules, HookMask hook, Span<Listener> output)
        => (Registry.AnyHooks & hook) == 0 ? 0 : CollectListeners(battle, rules, new HookBits(hook, HookMaskExt.None), output);

    /// <summary>The <see cref="HookMaskExt"/> counterpart of the overload above, in the same dispatch order.</summary>
    public static int CollectListeners(in Battle battle, Rules rules, HookMaskExt hook, Span<Listener> output)
        => (Registry.AnyHooksExt & hook) == 0 ? 0 : CollectListeners(battle, rules, new HookBits(HookMask.None, hook), output);

    private readonly record struct HookBits(HookMask Main, HookMaskExt Ext);

    private static int CollectListeners(in Battle battle, Rules rules, HookBits hook, Span<Listener> output)
    {
        int count = 0;
        bool playerHooks = (battle.Flags & BattleFlags.PlayerHooksActive) != 0;
        int nextPower = 0;

        // Player creature.
        nextPower = CollectPowers(battle, Limits.PlayerOwner, hook, nextPower, output, ref count);
        if (playerHooks)
        {
            ReadOnlySpan<ushort> relics = hook.Ext == HookMaskExt.None
                ? rules.Relics.Listeners(hook.Main) : rules.Relics.Listeners(hook.Ext);
            foreach (ushort index in relics)
                output[count++] = new Listener(ListenerKind.Relic, index, Limits.PlayerOwner);
            for (int slot = 0; slot < battle.PotionSlotCount; slot++)
            {
                ushort potion = battle.Potions[slot];
                if (potion != 0 && Has(Registry.Potion((ushort)(potion - 1)), hook))
                    output[count++] = new Listener(ListenerKind.Potion, (byte)slot, Limits.PlayerOwner);
            }
        }

        // Osty: inserted before the enemy block.
        if ((battle.Flags & BattleFlags.OstyPresent) != 0)
            nextPower = CollectPowers(battle, Limits.OstyOwner, hook, nextPower, output, ref count);

        // Enemies in roster order: powers, then the monster itself.
        for (int position = 0; position < battle.RosterCount; position++)
        {
            byte enemyIndex = battle.Roster[position];
            byte owner = Kernel.EnemyOwner(enemyIndex);
            CollectPowers(battle, owner, hook, 0, output, ref count);
            if (Has(Registry.Monster(battle.Enemies[enemyIndex].MonsterId), hook))
                output[count++] = new Listener(ListenerKind.Monster, enemyIndex, owner);
        }

        if (!playerHooks)
            return count;

        for (int index = 0; index < battle.OrbCount; index++)
        {
            if (Has(Registry.Orb(battle.Orbs[index].Id), hook))
                output[count++] = new Listener(ListenerKind.Orb, (byte)index, Limits.PlayerOwner);
        }

        // The per-card Has test cannot match when no card/affliction/enchantment definition answers
        // this hook; skip the whole card-zone scan in that case (the cost and threat-projection hooks
        // have no card-side listeners).
        if ((hook.Main & Registry.CardHooks) != 0 || (hook.Ext & Registry.CardHooksExt) != 0)
        {
            int total = battle.CardCount;
            for (int position = 0; position < total; position++)
            {
                ref readonly Card card = ref battle.Cards[position];
                if (Has(Registry.Card(card.Id), hook))
                    output[count++] = new Listener(ListenerKind.Card, (ushort)position, Limits.PlayerOwner);
                if (card.AfflictionId != 0 && Has(Registry.Affliction((ushort)(card.AfflictionId - 1)), hook))
                    output[count++] = new Listener(ListenerKind.Affliction, (ushort)position, Limits.PlayerOwner);
                if (card.EnchantmentId != 0 && Has(Registry.Enchantment((ushort)(card.EnchantmentId - 1)), hook))
                    output[count++] = new Listener(ListenerKind.Enchantment, (ushort)position, Limits.PlayerOwner);
            }
        }
        return count;
    }

    public static ListenerDef Definition(in Battle battle, Rules rules, Listener listener) => listener.Kind switch
    {
        ListenerKind.Power => Registry.Power(battle.Powers[listener.Index].Id),
        ListenerKind.Relic => Registry.Relic(rules.Relics.Fixed(listener.Index).Id),
        ListenerKind.Potion => Registry.Potion((ushort)(battle.Potions[listener.Index] - 1)),
        ListenerKind.Monster => Registry.Monster(battle.Enemies[listener.Index].MonsterId),
        ListenerKind.Orb => Registry.Orb(battle.Orbs[listener.Index].Id),
        ListenerKind.Card => Registry.Card(battle.Cards[listener.Index].Id),
        ListenerKind.Affliction => Registry.Affliction((ushort)(battle.Cards[listener.Index].AfflictionId - 1)),
        ListenerKind.Enchantment => Registry.Enchantment((ushort)(battle.Cards[listener.Index].EnchantmentId - 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(listener), listener.Kind, null),
    };

    /// <summary>
    /// Native <c>Hook.AfterCreatureAddedToCombat</c> in combat listener order. The only supported
    /// listener is PhilosophersStone's enemy Strength; a spawn during the hook itself is not revisited.
    /// </summary>
    public static void AfterCreatureAddedToCombat(ref Exec x, byte creature)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.AfterCreatureAddedToCombat) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMaskExt.AfterCreatureAddedToCombat, listeners);
        for (int index = 0; index < count; index++)
        {
            Definition(x.B, x.R, listeners[index]).AfterCreatureAddedToCombat(ref x, listeners[index], creature);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>HookMirrors.ModifyDamage</c>: card enchantment, additive pass, multiplicative pass,
    /// then the minimum cap, clamped at zero. Decimal throughout, never rounded here.
    /// </summary>
    public static decimal ModifyDamage(ref Exec x, byte target, byte dealer, decimal damage, ValueProps props,
        bool hasCardSource, in Card cardSource, bool capOnly = false)
    {
        // HookMirrors.ModifyDamage: the source card's enchantment runs before every listener.
        if (hasCardSource && cardSource.EnchantmentId != 0)
        {
            EnchantmentDef enchantment = Registry.Enchantment((ushort)(cardSource.EnchantmentId - 1));
            damage += enchantment.EnchantDamageAdditive(cardSource, damage, props);
            damage *= enchantment.EnchantDamageMultiplicative(cardSource, damage, props);
        }
        // Run listeners put out-of-combat deck cards before the combat list; deck cards with
        // damage hooks are rejected at admission.
        DamageContext context = new(target, dealer, props, hasCardSource, cardSource);
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];

        int count;
        if (!capOnly)
        {
        count = CollectListeners(x.B, x.R, HookMask.ModifyDamageAdditive, listeners);
        for (int index = 0; index < count; index++)
            damage += Definition(x.B, x.R, listeners[index]).ModifyDamageAdditive(ref x, listeners[index], context, damage);

        if (hasCardSource)
            damage *= Registry.Card(cardSource.Id).ModifyOwnDamageMultiplier(ref x, context);
        count = CollectListeners(x.B, x.R, HookMask.ModifyDamageMultiplicative, listeners);
        for (int index = 0; index < count; index++)
            damage *= Definition(x.B, x.R, listeners[index]).ModifyDamageMultiplicative(ref x, listeners[index], context, damage);

        }

        decimal cap = decimal.MaxValue;
        count = CollectListeners(x.B, x.R, HookMask.ModifyDamageCap, listeners);
        for (int index = 0; index < count; index++)
            cap = Math.Min(cap, Definition(x.B, x.R, listeners[index]).ModifyDamageCap(ref x, listeners[index], context));

        return Math.Max(0, Math.Min(damage, cap));
    }

    /// <summary>
    /// Mirrors <c>Hook.ModifyUnblockedDamageTarget</c>: every combat listener in dispatch order, each
    /// receiving the target the previous listener returned, without the ending gate. Pure query.
    /// </summary>
    public static byte ModifyUnblockedDamageTarget(ref Exec x, byte target, decimal amount,
        ValueProps props, byte dealer)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.ModifyUnblockedDamageTarget) == 0)
            return target;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMaskExt.ModifyUnblockedDamageTarget, listeners);
        for (int index = 0; index < count; index++)
            target = Definition(x.B, x.R, listeners[index]).ModifyUnblockedDamageTarget(ref x, listeners[index], target, amount, props, dealer);
        return target;
    }

    /// <summary>
    /// Mirrors one <c>HookMirrors.ModifyHpLost</c> call: the normal then Late pass of the before-Osty
    /// (<paramref name="afterOsty"/> false) or after-Osty phase. Listeners whose call changed the truncated amount
    /// are written to <paramref name="changed"/>.
    /// </summary>
    public static decimal ModifyHpLost(ref Exec x, in HpLossContext loss, decimal amount, bool afterOsty,
        scoped Span<Listener> changed, out int changedCount)
    {
        changedCount = 0;
        HookMask normalMask = afterOsty ? HookMask.ModifyHpLostAfterOsty : HookMask.ModifyHpLostBeforeOsty;
        HookMask lateMask = afterOsty ? HookMask.ModifyHpLostAfterOstyLate : HookMask.ModifyHpLostBeforeOstyLate;
        if ((Registry.AnyHooks & (normalMask | lateMask)) == 0)
            return amount;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        for (int pass = 0; pass < 2; pass++)
        {
            int count = CollectListeners(x.B, x.R, pass == 0 ? normalMask : lateMask, listeners);
            for (int index = 0; index < count; index++)
            {
                ListenerDef def = Definition(x.B, x.R, listeners[index]);
                decimal previous = amount;
                amount = (afterOsty, pass) switch
                {
                    (false, 0) => def.ModifyHpLostBeforeOsty(ref x, listeners[index], loss, amount),
                    (false, _) => def.ModifyHpLostBeforeOstyLate(ref x, listeners[index], loss, amount),
                    (true, 0) => def.ModifyHpLostAfterOsty(ref x, listeners[index], loss, amount),
                    _ => def.ModifyHpLostAfterOstyLate(ref x, listeners[index], loss, amount),
                };
                if (decimal.Truncate(previous) != decimal.Truncate(amount))
                    changed[changedCount++] = listeners[index];
            }
        }
        return amount;
    }

    /// <summary>
    /// Mirrors <c>HookMirrors.AfterModifyingHpLostAfterOsty</c>: the run listeners in order, each notified when it is
    /// among <paramref name="changed"/>.
    /// </summary>
    public static void AfterModifyingHpLostAfterOsty(ref Exec x, scoped ReadOnlySpan<Listener> changed)
    {
        if (changed.IsEmpty)
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.AfterModifyingHpLostAfterOsty, listeners);
        for (int index = 0; index < count; index++)
        {
            if (changed.Contains(listeners[index]))
            {
                Definition(x.B, x.R, listeners[index]).AfterModifyingHpLostAfterOsty(ref x, listeners[index]);
                if (x.NeedsChoice)
                    return;
            }
        }
    }

    /// <summary>Mirrors <c>HookMirrors.ModifyBlock</c> including the settlement overflow clamp.</summary>
    public static decimal ModifyBlock(ref Exec x, byte target, decimal block, ValueProps props,
        bool hasCardSource, in Card cardSource, out bool anyModifier)
        => ModifyBlock(ref x, target, block, props, hasCardSource, cardSource, out anyModifier,
            Span<Listener>.Empty, out _);

    /// <summary>
    /// <c>HookMirrors.ModifyBlock</c> with the listeners whose additive/multiplicative value changed
    /// (the input of <c>AfterModifyingBlockAmount</c>), in listener order.
    /// </summary>
    public static decimal ModifyBlock(ref Exec x, byte target, decimal block, ValueProps props,
        bool hasCardSource, in Card cardSource, out bool anyModifier,
        scoped Span<Listener> changed, out int changedCount)
    {
        anyModifier = false;
        changedCount = 0;
        bool overflowed = false;
        // HookMirrors.ModifyBlock: the source card's enchantment runs before every listener and is
        // part of the settlement overflow tracking.
        if (hasCardSource && cardSource.EnchantmentId != 0)
        {
            EnchantmentDef enchantment = Registry.Enchantment((ushort)(cardSource.EnchantmentId - 1));
            block += enchantment.EnchantBlockAdditive(cardSource, block);
            block = MultiplyBlockWithinSettlement(block,
                enchantment.EnchantBlockMultiplicative(cardSource, block), ref overflowed);
            anyModifier = true;
        }
        BlockContext context = new(target, props, hasCardSource, cardSource);
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];

        int count = CollectListeners(x.B, x.R, HookMask.ModifyBlockAdditive, listeners);
        for (int index = 0; index < count; index++)
        {
            decimal additive = Definition(x.B, x.R, listeners[index]).ModifyBlockAdditive(ref x, listeners[index], context, block);
            block += additive;
            anyModifier |= additive != 0;
            if (additive != 0 && !changed.IsEmpty)
                changed[changedCount++] = listeners[index];
        }

        count = CollectListeners(x.B, x.R, HookMask.ModifyBlockMultiplicative, listeners);
        for (int index = 0; index < count; index++)
        {
            decimal multiplier = Definition(x.B, x.R, listeners[index]).ModifyBlockMultiplicative(ref x, listeners[index], context, block);
            block = MultiplyBlockWithinSettlement(block, multiplier, ref overflowed);
            anyModifier |= multiplier != 1;
            if (multiplier != 1 && !changed.IsEmpty)
                changed[changedCount++] = listeners[index];
        }

        return overflowed ? Math.Clamp(block, 0m, Limits.ValueClamp) : Math.Max(0, block);
    }

    /// <summary><c>HookMirrors.AfterModifyingBlockAmount</c> for the recorded listeners.</summary>
    public static void AfterModifyingBlockAmount(ref Exec x, byte target, decimal modified,
        ValueProps props, bool hasCardSource, in Card cardSource,
        scoped ReadOnlySpan<Listener> changed)
    {
        if (changed.IsEmpty)
            return;
        BlockContext context = new(target, props, hasCardSource, cardSource);
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMaskExt.AfterModifyingBlockAmount, listeners);
        for (int index = 0; index < count; index++)
        {
            if (changed.Contains(listeners[index]))
            {
                Definition(x.B, x.R, listeners[index]).AfterModifyingBlockAmount(ref x, listeners[index], modified, context);
                if (x.NeedsChoice)
                    return;
            }
        }
    }

    /// <summary>Mirrors <c>HookMirrors.MultiplyBlockWithinSettlement</c>.</summary>
    private static decimal MultiplyBlockWithinSettlement(decimal block, decimal multiplier, ref bool overflowed)
    {
        if (block == 0m || multiplier == 0m)
            return 0m;
        if (multiplier == decimal.MaxValue)
            overflowed = true;
        if (Math.Abs(multiplier) > 1m && Math.Abs(block) > decimal.MaxValue / Math.Abs(multiplier))
        {
            overflowed = true;
            return (block < 0m) ^ (multiplier < 0m) ? decimal.MinValue : decimal.MaxValue;
        }
        return block * multiplier;
    }

    /// <summary>
    /// Mirrors <c>HookMirrors.ModifyEnergyCostInCombat</c>: the normal pass over every combat
    /// listener, then the late pass over every combat listener, without an ending gate. The caller
    /// truncates and clamps at zero.
    /// </summary>
    public static decimal ModifyEnergyCost(in Battle battle, Rules rules, Pile pile,
        in Card card, decimal amount)
    {
        HookMask masks = HookMask.TryModifyEnergyCostInCombat | HookMask.TryModifyEnergyCostInCombatLate;
        // HookMirrors iterates IterateCombatHookListeners, which returns no combat listeners while
        // the combat is over or ending.
        if ((Registry.AnyHooks & masks) == 0 || Kernel.IsOverOrEnding(battle))
            return amount;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMask.TryModifyEnergyCostInCombat, listeners);
        for (int index = 0; index < count; index++)
        {
                amount = Definition(battle, rules, listeners[index]).TryModifyEnergyCostInCombat(battle, rules, listeners[index],
                card, pile, amount);
        }
        amount = Registry.Card(card.Id).ModifyOwnEnergyCost(battle, rules, card, amount);
        count = CollectListeners(battle, rules, HookMask.TryModifyEnergyCostInCombatLate, listeners);
        for (int index = 0; index < count; index++)
        {
            amount = Definition(battle, rules, listeners[index]).TryModifyEnergyCostInCombatLate(battle, rules, listeners[index],
                card, pile, amount);
        }
        return amount;
    }

    /// <summary>
    /// Mirrors <c>Hook.ModifyOrbValue</c>: every combat listener in order, skipped while the combat is
    /// over/ending (<c>IterateCombatHookListeners</c> yields no listeners then). A pure query.
    /// </summary>
    public static decimal ModifyOrbValue(in Battle battle, Rules rules, in Orb orb, decimal value)
    {
        if ((Registry.AnyHooks & HookMask.ModifyOrbValue) == 0 || Kernel.IsOverOrEnding(battle))
            return value;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMask.ModifyOrbValue, listeners);
        for (int index = 0; index < count; index++)
            value = Definition(battle, rules, listeners[index]).ModifyOrbValue(battle, rules, listeners[index], orb, value);
        return value;
    }

    /// <summary>
    /// <c>HookMirrors.ModifyOrbPassiveTriggerCount</c>: every combat listener in order, recording the
    /// ones that changed the count. The existing engine then calls
    /// <c>AfterModifyingOrbPassiveTriggerCount</c> on those listeners; its only vanilla listener
    /// (GoldPlatedCables) is cosmetic, so the Sts2Solver kernel does not model that pass.
    /// </summary>
    public static int ModifyOrbPassiveTriggerCounts(in Battle battle, Rules rules, in Orb orb,
        int orbIndex, int triggerCount)
    {
        if ((Registry.AnyHooks & HookMask.ModifyOrbPassiveTriggerCounts) == 0
            || Kernel.IsOverOrEnding(battle))
        {
            return triggerCount;
        }
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMask.ModifyOrbPassiveTriggerCounts, listeners);
        for (int index = 0; index < count; index++)
        {
            int modified = Definition(battle, rules, listeners[index]).ModifyOrbPassiveTriggerCounts(
                battle, rules, listeners[index], orb, orbIndex, triggerCount);
            if (modified != triggerCount)
                triggerCount = modified;
        }
        return triggerCount;
    }

    /// <summary><c>HookMirrors.AfterOrbChanneled</c> over a listener snapshot.</summary>
    public static void AfterOrbChanneled(ref Exec x, in Orb orb)
    {
        if ((Registry.AnyHooks & HookMask.AfterOrbChanneled) == 0 || Kernel.IsOverOrEnding(x.B))
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.AfterOrbChanneled, listeners);
        if (count == 0)
            return;
        Span<SnapshotEntry> snapshot = stackalloc SnapshotEntry[count];
        for (int index = 0; index < count; index++)
            snapshot[index] = Snapshot(x.B, x.R, listeners[index]);
        for (int index = 0; index < count; index++)
        {
            if (!TryResolve(x.B, x.R, snapshot[index], out Listener current))
                continue;
            Definition(x.B, x.R, current).AfterOrbChanneled(ref x, current, orb);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary><c>HookMirrors.AfterOrbEvoked</c> over a listener snapshot with the evoke result targets.</summary>
    public static void AfterOrbEvoked(ref Exec x, in Orb orb, scoped ReadOnlySpan<byte> targets)
    {
        if ((Registry.AnyHooks & HookMask.AfterOrbEvoked) == 0 || Kernel.IsOverOrEnding(x.B))
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.AfterOrbEvoked, listeners);
        if (count == 0)
            return;
        Span<SnapshotEntry> snapshot = stackalloc SnapshotEntry[count];
        for (int index = 0; index < count; index++)
            snapshot[index] = Snapshot(x.B, x.R, listeners[index]);
        for (int index = 0; index < count; index++)
        {
            if (!TryResolve(x.B, x.R, snapshot[index], out Listener current))
                continue;
            Definition(x.B, x.R, current).AfterOrbEvoked(ref x, current, orb, targets);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// <c>SimulatedCombatState.AfterPotionUsed</c>'s relic batch: every combat listener carrying the
    /// hook, in listener order, after the potion effect (no ending gate in the existing engine).
    /// </summary>
    public static void AfterPotionUsed(ref Exec x, ushort potionId)
    {
        if ((Registry.AnyHooks & HookMask.AfterPotionUsed) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.AfterPotionUsed, listeners);
        if (count == 0)
            return;
        Span<SnapshotEntry> snapshot = stackalloc SnapshotEntry[count];
        for (int index = 0; index < count; index++)
            snapshot[index] = Snapshot(x.B, x.R, listeners[index]);
        for (int index = 0; index < count; index++)
        {
            if (!TryResolve(x.B, x.R, snapshot[index], out Listener current))
                continue;
            Definition(x.B, x.R, current).AfterPotionUsed(ref x, current, potionId);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>Mirrors <c>Hook.ModifyXValue</c>: every combat listener in order, skipped while the combat is over/ending.</summary>
    public static int ModifyXValue(in Battle battle, Rules rules, in Card card, int value)
    {
        if ((Registry.AnyHooks & HookMask.ModifyXValue) == 0 || Kernel.IsOverOrEnding(battle))
            return value;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMask.ModifyXValue, listeners);
        for (int index = 0; index < count; index++)
        {
            value = Definition(battle, rules, listeners[index]).ModifyXValue(battle, rules, listeners[index], card, value);
        }
        return value;
    }

    /// <summary>
    /// Mirrors <c>PersistentRelicSupport.ShouldPlayerResetEnergy</c>: every combat listener in order,
    /// no ending gate; the first false keeps the current energy.
    /// </summary>
    public static bool ShouldPlayerResetEnergy(in Battle battle, Rules rules, byte player)
    {
        if ((Registry.AnyHooks & HookMask.ShouldPlayerResetEnergy) == 0)
            return true;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMask.ShouldPlayerResetEnergy, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!Definition(battle, rules, listeners[index]).ShouldPlayerResetEnergy(battle, rules, listeners[index], player))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Mirrors <c>HookMirrors.ModifyCardPlayResultLocation</c> plus <c>CardModel.OnPlayWrapper</c>'s direct
    /// after dispatch over the listeners that changed the location. Both use the combat-listener gate.
    /// </summary>
    public static Pile ModifyCardPlayResultLocation(ref Exec x, in Card card, bool isAutoPlay,
        int energyValue, Pile location, PilePosition basePosition,
        scoped Span<Listener> modifiers, out int modifierCount, out PilePosition position)
    {
        modifierCount = 0;
        position = basePosition;
        if ((Registry.AnyHooks & HookMask.ModifyCardPlayResultLocation) == 0 || Kernel.IsOverOrEnding(x.B))
            return location;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.ModifyCardPlayResultLocation, listeners);
        for (int index = 0; index < count; index++)
        {
            Pile previous = location;
            PilePosition previousPosition = position;
            location = Definition(x.B, x.R, listeners[index]).ModifyCardPlayResultLocation(ref x, listeners[index], card,
                isAutoPlay, energyValue, location, ref position);
            if (location != previous || position != previousPosition)
                modifiers[modifierCount++] = listeners[index];
        }
        return location;
    }

    /// <summary><c>HookMirrors.AfterModifyingCardPlayResultLocation</c>: the recorded modifiers in order.</summary>
    public static void AfterModifyingCardPlayResultLocation(ref Exec x, in Card card, Pile location,
        scoped ReadOnlySpan<Listener> modifiers)
    {
        for (int index = 0; index < modifiers.Length; index++)
        {
            ListenerDef def = Definition(x.B, x.R, modifiers[index]);
            if ((def.Hooks & HookMask.AfterModifyingCardPlayResultLocation) == 0)
                continue;
            def.AfterModifyingCardPlayResultLocation(ref x, modifiers[index], card, location);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>HookMirrors.ModifyCardPlayCount</c>: every combat listener in order; the changed
    /// listeners are recorded for the after pass. Skipped while the combat is over/ending.
    /// </summary>
    public static int ModifyCardPlayCount(ref Exec x, in Card card, byte target, int playCount,
        scoped Span<Listener> modifiers, out int modifierCount)
    {
        modifierCount = 0;
        if ((Registry.AnyHooks & HookMask.ModifyCardPlayCount) == 0 || Kernel.IsOverOrEnding(x.B))
            return playCount;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.ModifyCardPlayCount, listeners);
        for (int index = 0; index < count; index++)
        {
            int previous = playCount;
            playCount = Definition(x.B, x.R, listeners[index]).ModifyCardPlayCount(ref x, listeners[index], card, target,
                playCount);
            if (playCount != previous)
                modifiers[modifierCount++] = listeners[index];
        }
        return playCount;
    }

    /// <summary>
    /// <c>HookMirrors.AfterModifyingCardPlayCount</c>: re-reads the listener list and calls the after
    /// hook only on the recorded modifiers. OneTwoPunch and Duplication decrement themselves here.
    /// </summary>
    public static void AfterModifyingCardPlayCount(ref Exec x, in Card card,
        scoped ReadOnlySpan<Listener> modifiers)
    {
        if (modifiers.IsEmpty || (Registry.AnyHooks & HookMask.AfterModifyingCardPlayCount) == 0
            || Kernel.IsOverOrEnding(x.B))
        {
            return;
        }
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.AfterModifyingCardPlayCount, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!Contains(modifiers, listeners[index]))
                continue;
            Definition(x.B, x.R, listeners[index]).AfterModifyingCardPlayCount(ref x, listeners[index], card);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// <c>PersistentRelicSupport.ModifyPowerAmountGiven</c>: every combat listener in order adds its
    /// delta to the running amount. A pure query without the ending gate.
    /// </summary>
    public static decimal ModifyPowerAmountGiven(in Battle battle, Rules rules, ushort powerId,
        byte giver, byte target, decimal amount)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.ModifyPowerAmountGiven) == 0)
            return amount;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMaskExt.ModifyPowerAmountGiven, listeners);
        for (int index = 0; index < count; index++)
        {
            Listener listener = listeners[index];
            amount += Definition(battle, rules, listener).ModifyPowerAmountGiven(battle, rules, listener, powerId,
                giver, target, amount);
        }
        return amount;
    }

    /// <summary>
    /// <c>Hook.ModifyPowerAmountGivenMultiplicative</c>: a pure query after the additive given pass
    /// (<c>SimulatedCombatState.ModifyPowerAmountForUnsettlingLamp</c> in the existing engine).
    /// </summary>
    public static decimal ModifyPowerAmountGivenMultiplicative(ref Exec x,
        ushort powerId, byte giver, byte target, decimal amount, int cardSourceSerial)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.ModifyPowerAmountGivenMultiplicative) == 0)
            return amount;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMaskExt.ModifyPowerAmountGivenMultiplicative, listeners);
        for (int index = 0; index < count; index++)
        {
            Listener listener = listeners[index];
            amount *= Definition(x.B, x.R, listener).ModifyPowerAmountGivenMultiplicative(ref x, listener,
                powerId, giver, target, amount, cardSourceSerial);
        }
        return amount;
    }

    /// <summary>
    /// <c>Hook.BeforePowerAmountChanged</c>: the observation pass
    /// (<c>SimulatedCombatState.ObserveUnsettlingLamp</c>) before the additive given pass.
    /// </summary>
    public static void BeforePowerAmountChanged(ref Exec x, ushort powerId, int amount, byte target,
        byte applier, int cardSourceSerial)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.BeforePowerAmountChanged) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMaskExt.BeforePowerAmountChanged, listeners);
        for (int index = 0; index < count; index++)
        {
            Listener listener = listeners[index];
            Definition(x.B, x.R, listener).BeforePowerAmountChanged(ref x, listener, powerId, amount, target, applier,
                cardSourceSerial);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary><c>AfterEnergySpent</c> over the combat listeners, in listener order.</summary>
    public static void AfterEnergySpent(ref Exec x, in Card card, int amount)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.AfterEnergySpent) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMaskExt.AfterEnergySpent, listeners);
        for (int index = 0; index < count; index++)
        {
            Definition(x.B, x.R, listeners[index]).AfterEnergySpent(ref x, listeners[index], card, amount);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.ModifyPowerAmountForRelics</c>'s received pass: a pure query over
    /// every combat listener; the listeners that changed the amount are recorded. Artifact stays in the
    /// kernel after this pass, as the existing engine consumes it after the relic modifiers.
    /// </summary>
    public static int ModifyPowerAmountReceived(in Battle battle, Rules rules, ushort powerId,
        byte target, int amount, scoped Span<Listener> modifiers, out int modifierCount)
    {
        modifierCount = 0;
        if ((Registry.AnyHooks & HookMask.TryModifyPowerAmountReceived) == 0)
            return amount;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMask.TryModifyPowerAmountReceived, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!Definition(battle, rules, listeners[index]).TryModifyPowerAmountReceived(battle, rules, listeners[index],
                powerId, target, amount, out int modified))
            {
                continue;
            }
            amount = modified;
            modifiers[modifierCount++] = listeners[index];
        }
        return amount;
    }

    /// <summary><c>Hook.AfterModifyingPowerAmountReceived</c> for the recorded listeners, in order.</summary>
    public static void AfterModifyingPowerAmountReceived(ref Exec x, ushort powerId, byte target,
        scoped ReadOnlySpan<Listener> modifiers)
    {
        for (int index = 0; index < modifiers.Length; index++)
        {
            ListenerDef def = Definition(x.B, x.R, modifiers[index]);
            if ((def.Hooks & HookMask.AfterModifyingPowerAmountReceived) == 0)
                continue;
            def.AfterModifyingPowerAmountReceived(ref x, modifiers[index], powerId, target);
            if (x.NeedsChoice)
                return;
        }
    }

    private static bool Contains(scoped ReadOnlySpan<Listener> listeners, Listener listener)
    {
        for (int index = 0; index < listeners.Length; index++)
        {
            if (listeners[index] == listener)
                return true;
        }
        return false;
    }

    private static int CollectPowers(in Battle battle, byte owner, HookBits hook, int start,
        Span<Listener> output, ref int count)
    {
        int index = 0;
        for (; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner < owner)
                continue;
            if (power.Owner > owner)
                break;
            if (Has(Registry.Power(power.Id), hook))
                output[count++] = new Listener(ListenerKind.Power, (byte)index, owner);
        }
        return index;
    }

    private static bool Has(ContentDef def, HookBits hook)
        => (def.Hooks & hook.Main) != 0 || (def.HooksExt & hook.Ext) != 0;
}
