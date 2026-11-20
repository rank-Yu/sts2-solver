namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Star hooks (Regent). The existing engine's order is copied from
/// <c>HookMirrors.ModifyStarCost</c>, <c>PowerLifecycleSupport.AfterStarsSpent</c> and
/// <c>HookMirrors.AfterStarsGained</c>.
/// </summary>
public static partial class HookDispatch
{
    /// <summary>
    /// Mirrors <c>HookMirrors.ModifyStarCost</c>: only a non-negative original cost is offered to the
    /// listeners, in combat-listener order, and the combat-ending gate of
    /// <c>IterateCombatHookListeners</c> applies. The caller truncates and clamps at zero.
    /// </summary>
    public static decimal ModifyStarCost(in Battle battle, Rules rules, Pile pile,
        in Card card, decimal amount)
    {
        if (amount < 0 || (Registry.AnyHooks & HookMask.TryModifyStarCost) == 0
            || Kernel.IsOverOrEnding(battle))
        {
            return amount;
        }
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(battle, rules)];
        int count = CollectListeners(battle, rules, HookMask.TryModifyStarCost, listeners);
        for (int index = 0; index < count; index++)
        {
            amount = Definition(battle, rules, listeners[index]).TryModifyStarCost(battle, rules, listeners[index],
                card, pile, amount);
        }
        return amount;
    }

    public static void AfterStarsSpent(ref Exec x, int amount)
    {
        if (amount <= 0)
            return;
        var hook = new StarsSpentHook(amount);
        Dispatch(ref x, ref hook, gatedByEnding: true);
    }

    private readonly struct StarsSpentHook(int amount) : IActionHook
    {
        public static HookMask Mask => HookMask.AfterStarsSpent;

        public void Invoke(ref Exec x, ListenerDef definition, Listener self)
            => definition.AfterStarsSpent(ref x, self, amount);
    }

    /// <summary>
    /// Mirrors <c>HookMirrors.AfterStarsGained</c>: every combat listener in order, stopping at a
    /// pending choice. The existing engine's <c>GainStars</c> already skipped the gain while the combat
    /// is ending, so this pass needs no extra gate.
    /// </summary>
    public static bool AfterStarsGained(ref Exec x, int amount)
    {
        if ((Registry.AnyHooks & HookMask.AfterStarsGained) == 0)
            return true;
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, HookMask.AfterStarsGained, listeners);
        for (int index = 0; index < count; index++)
        {
            Definition(x.B, x.R, listeners[index]).AfterStarsGained(ref x, listeners[index], amount);
            if (x.NeedsChoice)
                return false;
        }
        return true;
    }
}
