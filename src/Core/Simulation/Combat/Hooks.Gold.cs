namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Mirrors <c>GoldGainSupport</c> in the original engine: the modifier pass and its notification
/// over the combat listeners, then the run-listener after-gain pass. The native calls are
/// <c>Hook.ModifyGoldGained</c>, <c>Hook.AfterModifyingGoldGained</c> and
/// <c>Hook.AfterGoldGained</c> from <c>PlayerCmd.GainGold</c>.
/// </summary>
public static partial class HookDispatch
{
    /// <summary>
    /// Native <c>Hook.ModifyGoldGained</c>: every combat listener receives the running amount in
    /// order. Listeners whose truncated amount changed are written to <paramref name="changed"/> for
    /// the following <see cref="AfterModifyingGoldGained"/> pass. No ending gate.
    /// </summary>
    public static decimal ModifyGoldGained(ref Exec x, decimal amount, scoped Span<Listener> changed,
        out int changedCount)
    {
        changedCount = 0;
        if ((Registry.AnyHooksExt & HookMaskExt.ModifyGoldGained) == 0)
            return amount;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B)];
        int count = CollectListeners(x.B, HookMaskExt.ModifyGoldGained, listeners);
        // Native order is the run listeners (deck cards, relics, potions, ...) followed by the
        // combat listeners. Deck card listeners are rejected at admission, so the first pass is
        // relics and potions; the second keeps the kernel's combat listener order.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int index = 0; index < count; index++)
            {
                Listener listener = listeners[index];
                bool runListener = listener.Kind is ListenerKind.Relic or ListenerKind.Potion;
                if ((pass == 0) != runListener || !PlayerListenerActive(x.B, listener))
                    continue;
                decimal before = amount;
                amount = Definition(x.B, listener).ModifyGoldGained(ref x, listener, amount);
                if ((int)before != (int)amount)
                {
                    if (changedCount >= changed.Length)
                        throw new CapacityException("Hook.ModifyGoldGained", changed.Length);
                    changed[changedCount++] = listener;
                }
                ThrowOnChoice(ref x, nameof(ListenerDef.ModifyGoldGained));
            }
        }
        return amount;
    }

    /// <summary>
    /// Native <c>Hook.AfterModifyingGoldGained</c>: only the listeners that changed the truncated
    /// amount, in their original listener order.
    /// </summary>
    public static void AfterModifyingGoldGained(ref Exec x, scoped ReadOnlySpan<Listener> modifiers,
        decimal amount)
    {
        if (modifiers.Length == 0 || (Registry.AnyHooksExt & HookMaskExt.AfterModifyingGoldGained) == 0)
            return;
        for (int index = 0; index < modifiers.Length; index++)
        {
            Listener listener = modifiers[index];
            if ((Definition(x.B, listener).HooksExt & HookMaskExt.AfterModifyingGoldGained) == 0)
                continue;
            Definition(x.B, listener).AfterModifyingGoldGained(ref x, listener, amount);
            ThrowOnChoice(ref x, nameof(ListenerDef.AfterModifyingGoldGained));
        }
    }

    /// <summary>
    /// Native <c>Hook.AfterGoldGained</c> uses <c>RunState.IterateHookListeners(null)</c>, so combat
    /// powers, monsters, orbs and combat card copies are not listeners; only run relics and potions
    /// are represented in <see cref="Battle"/>. Deck-card listeners are rejected at admission.
    /// </summary>
    public static void AfterGoldGained(ref Exec x)
    {
        if ((Registry.AnyHooksExt & HookMaskExt.AfterGoldGained) == 0)
            return;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B)];
        int count = CollectListeners(x.B, HookMaskExt.AfterGoldGained, listeners);
        for (int index = 0; index < count; index++)
        {
            Listener listener = listeners[index];
            if (listener.Kind is not (ListenerKind.Relic or ListenerKind.Potion)
                || !PlayerListenerActive(x.B, listener))
            {
                continue;
            }
            Definition(x.B, listener).AfterGoldGained(ref x, listener);
            ThrowOnChoice(ref x, nameof(ListenerDef.AfterGoldGained));
        }
    }

    private static void ThrowOnChoice(ref Exec x, string hook)
    {
        if (x.NeedsChoice)
            throw new UnsupportedException($"金币 Hook {hook} 请求选择，内核没有该延续。");
    }
}
