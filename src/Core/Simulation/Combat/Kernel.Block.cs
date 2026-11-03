namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>Mirrors <c>CombatPredictionSimulator.GainBlock</c>.</summary>
    public static decimal GainBlock(ref Exec x, byte owner, decimal amount, ValueProps props, bool fromCard)
        => GainBlock(ref x, owner, amount, props, fromCard, fromCard ? x.Card : default);

    /// <summary>
    /// <c>GainBlock</c> with the explicit card source. The existing engine passes the source card and
    /// card play to every block hook; the kernel's card effects use the current play card above.
    /// </summary>
    public static decimal GainBlock(ref Exec x, byte owner, decimal amount, ValueProps props,
        bool hasCardSource, in Card cardSource)
    {
        if (IsOverOrEnding(x.B) || x.B.Creature(owner).IsDead)
            return 0m;
        // BeforeBlockGained has no supported listeners yet.
        decimal modified;
        // AfterModifyingBlockAmount only needs the changed-listener list when a listener can change the
        // block: Vambrace (identity content) or PaelsLegion's x2 while its cooldown is down.
        if (x.R.HasIdentityContent || x.R.HasPaelsLegion)
        {
            Span<Listener> changed = stackalloc Listener[HookDispatch.MaxListenerCount(x.B) * 2];
            modified = HookDispatch.ModifyBlock(ref x, owner, amount, props, hasCardSource, cardSource, out _,
                changed, out int changedCount);
            modified = Math.Max(modified, 0m);
            if (changedCount > 0)
            {
                HookDispatch.AfterModifyingBlockAmount(ref x, owner, modified, props, hasCardSource, cardSource,
                    changed[..changedCount]);
            }
        }
        else
        {
            modified = HookDispatch.ModifyBlock(ref x, owner, amount, props, hasCardSource, cardSource, out _);
            modified = Math.Max(modified, 0m);
        }
        if (x.NeedsChoice)
            return modified;
        if (modified > 0m)
        {
            // RecordPoweredCardBlockGained: block gained inside a card play with a Move prop.
            if (hasCardSource && (props & ValueProps.Move) != 0)
                x.B.AddCounter(Counter.BlockCardsPlayedThisTurn, 1);
            ref Creature creature = ref x.B.Creature(owner);
            creature.Block = (int)Math.Min(creature.Block + modified, Limits.ValueClamp);
        }
        AfterBlockGainedHook after = new() { Owner = owner, Amount = modified, Props = props, FromCard = hasCardSource };
        HookDispatch.Dispatch(ref x, ref after, gatedByEnding: true);
        return modified;
    }
}
