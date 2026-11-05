namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>
    /// Mirrors <c>PlayerCmd.GainGold</c>'s state mutation and hooks, omitting SFX and map-point
    /// history: modify the decimal amount, notify the modifiers whose truncated amount changed, add
    /// the truncated amount when it is positive, then dispatch the after-gain listeners. Returns the
    /// truncated amount that was added, or zero when the modified amount is not positive.
    /// </summary>
    public static int GainGold(ref Exec x, decimal amount)
    {
        Span<Listener> modifiers = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
        amount = HookDispatch.ModifyGoldGained(ref x, amount, modifiers, out int modifierCount);
        HookDispatch.AfterModifyingGoldGained(ref x, modifiers[..modifierCount], amount);
        if (!(amount > 0m))
            return 0;
        int gained = (int)amount;
        long newGold = (long)x.B.Gold + gained;
        if (newGold > int.MaxValue)
            throw new CapacityException("Gold", int.MaxValue);
        x.B.Gold = (int)newGold;
        HookDispatch.AfterGoldGained(ref x);
        return gained;
    }
}
