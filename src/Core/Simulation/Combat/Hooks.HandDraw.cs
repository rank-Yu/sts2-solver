namespace Sts2Solver.Simulation.Combat;

public static partial class HookDispatch
{
    // Native Hook.ModifyHandDraw + AfterModifyingHandDraw (game 0.113.0, 16ac0fe3).
    // Only listeners that changed the integer count are notified, before the cards are drawn.
    internal static int ModifiedHandDraw(ref Exec x, int baseDraw)
    {
        decimal result = baseDraw;
        int capacity = MaxListenerCount(x.B, x.R);
        Span<Listener> listeners = stackalloc Listener[capacity];
        Span<SnapshotEntry> modified = stackalloc SnapshotEntry[capacity * 2];
        int changed = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int count = CollectListeners(x.B, x.R,
                pass == 0 ? HookMask.ModifyHandDraw : HookMask.ModifyHandDrawLate, listeners);
            for (int index = 0; index < count; index++)
            {
                Listener listener = listeners[index];
                var def = Definition(x.B, x.R, listener);
                decimal before = result;
                result = pass == 0 ? def.ModifyHandDraw(ref x, listener, result)
                    : def.ModifyHandDrawLate(ref x, listener, result);
                if ((int)before != (int)result)
                    modified[changed++] = Snapshot(x.B, x.R, listener);
            }
        }
        for (int index = 0; index < changed; index++)
        {
            if (!TryResolve(x.B, x.R, modified[index], out Listener listener))
                continue;
            int instances = CountSnapshotPower(x.B, modified[index]);
            Definition(x.B, x.R, listener).AfterModifyingHandDraw(ref x, listener);
            AdvancePowerSnapshot(x.B, modified[..changed], index, instances);
        }
        return Math.Max(0, (int)result);
    }
}
