namespace Sts2Solver.Simulation.Combat;

public static partial class HookDispatch
{
    internal static void Arrow(ref Exec x, HookMaskExt hook, byte target, int arrow,
        bool hasCardSource = false, Card cardSource = default)
    {
        Span<Listener> listeners = stackalloc Listener[MaxListenerCount(x.B, x.R)];
        int count = CollectListeners(x.B, x.R, hook, listeners);
        Span<SnapshotEntry> snapshot = stackalloc SnapshotEntry[count];
        for (int i = 0; i < count; i++) snapshot[i] = Snapshot(x.B, x.R, listeners[i]);
        for (int i = 0; i < count; i++)
        {
            if (!TryResolve(x.B, x.R, snapshot[i], out Listener current)) continue;
            var def = Definition(x.B, x.R, current);
            switch (hook)
            {
                case HookMaskExt.AfterArrowShot: def.AfterArrowShot(ref x, current, arrow); break;
                case HookMaskExt.AfterArrowImpaled: def.AfterArrowImpaled(ref x, current, target, arrow); break;
                case HookMaskExt.AfterArrowDislodged: def.AfterArrowDislodged(ref x, current, target, arrow, hasCardSource, cardSource); break;
                default: throw new ArgumentOutOfRangeException(nameof(hook));
            }
            if (x.NeedsChoice) return;
        }
    }
}
