// Checked against game ArrowCmd, and CombatSolver at f48b69c590cb8b689ec7fdf033c2f62e625e0f1e:
// src/Engine/InCombat/Simulation/CombatPredictionSimulator.Arrows.cs
// (MIT, Copyright (c) 2026 Torch1230 and contributors). State and lists use Battle's packed arrays.
namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    private static ushort ImpaledId => Registry.TryPower("IMPALED_POWER", out ushort id)
        ? id : throw new UnsupportedException("Missing ImpaledPower definition.");

    public static int AvailableArrows(in Battle battle)
    {
        int count = 0;
        for (int i = 0; i < battle.Fool.ArrowCount; i++)
            if (battle.Fool.Arrows[i].Available) count++;
        return count;
    }

    public static void GainArrow(ref Battle battle)
    {
        if (battle.Fool.ArrowCount >= FoolState.ArrowCapacity)
            throw new CapacityException("Arrows", FoolState.ArrowCapacity);
        battle.Fool.Arrows[battle.Fool.ArrowCount++] = default;
    }

    public static int TryShootArrow(ref Exec x)
    {
        int arrow = -1;
        for (int i = x.B.Fool.ArrowCount - 1; i >= 0; i--)
            if (x.B.Fool.Arrows[i].Available) { arrow = i; break; }
        if (arrow >= 0) x.B.Fool.Arrows[arrow].SetStatus(1);
        HookDispatch.Arrow(ref x, HookMaskExt.AfterArrowShot, Limits.NoOwner, arrow);
        return arrow;
    }

    public static bool ShootArrow(ref Exec x, decimal damage, int hits = 1)
    {
        byte target = x.Target;
        if (x.B.Creature(target).IsDead) return false;
        int arrow = TryShootArrow(ref x);
        if (x.NeedsChoice || arrow < 0 || x.B.Creature(target).IsDead) return arrow >= 0;
        int outer = x.B.Fool.ActiveAttack;
        x.B.Fool.ActiveAttack = ++x.B.Fool.AttackSerial;
        try
        {
            CardAttack(ref x, damage, hits, AttackTargeting.Single, target,
                ValueProps.Move | ValueProps.FromArrow);
        }
        finally { x.B.Fool.ActiveAttack = outer; }
        if (!x.NeedsChoice) ImpaleArrow(ref x, target, arrow);
        return true;
    }

    public static int ImpaledPowerIndex(in Battle battle, byte target)
        => FindPower(battle, target, ImpaledId);

    public static void ImpaleArrow(ref Exec x, byte target, int arrow)
    {
        ushort id = ImpaledId;
        int power = FindPower(x.B, target, id);
        if (power < 0)
        {
            // Native keeps a detached power when the target died or combat is ending.
            // A living target retains the away arrow when the shooter has died.
            if (x.B.Creature(target).IsDead || IsEnding(x.B))
            {
                HookDispatch.Arrow(ref x, HookMaskExt.AfterArrowImpaled, target, arrow);
                if (x.NeedsChoice) return;
                if (x.B.Creature(target).IsDead)
                    RetrieveShotArrow(ref x, target, arrow, true, x.Card);
                else if (x.B.Fool.Arrows[arrow].DestroyAfterImpaled)
                    x.B.Fool.Arrows[arrow].SetStatus(2);
                return;
            }
            ApplyPower(ref x, target, id, 1, Limits.PlayerOwner);
            if (x.NeedsChoice) return;
            power = FindPower(x.B, target, id);
            if (power < 0) throw new UnsupportedException("ImpaledPower application did not create an instance.");
            x.B.Powers[power].Aux0 = (int)x.R.PowerVar(id, 0);
        }
        int tail = x.B.Powers[power].Aux1 - 1;
        if (tail < 0) x.B.Powers[power].Aux1 = arrow + 1;
        else
        {
            while (x.B.Fool.Arrows[tail].Next != 0) tail = x.B.Fool.Arrows[tail].Next - 1;
            x.B.Fool.Arrows[tail].Next = checked((byte)(arrow + 1));
        }
        x.B.Fool.Arrows[arrow].Next = 0;
        HookDispatch.Arrow(ref x, HookMaskExt.AfterArrowImpaled, target, arrow);
        if (x.NeedsChoice) return;
        if (x.B.Fool.Arrows[arrow].DestroyAfterImpaled)
        {
            UnlinkArrow(ref x.B, target, arrow);
            x.B.Fool.Arrows[arrow].SetStatus(2);
        }
    }

    private static void UnlinkArrow(ref Battle battle, byte target, int arrow)
    {
        int power = ImpaledPowerIndex(battle, target);
        if (power < 0) return;
        int head = battle.Powers[power].Aux1 - 1;
        if (head == arrow) battle.Powers[power].Aux1 = battle.Fool.Arrows[arrow].Next;
        else
        {
            while (head >= 0 && battle.Fool.Arrows[head].Next != arrow + 1)
                head = battle.Fool.Arrows[head].Next - 1;
            if (head >= 0) battle.Fool.Arrows[head].Next = battle.Fool.Arrows[arrow].Next;
        }
        battle.Fool.Arrows[arrow].Next = 0;
        if (battle.Powers[power].Aux1 == 0) RemovePowerAt(ref battle, power);
    }

    public static void DislodgeArrow(ref Exec x, byte target)
        => DislodgeArrow(ref x, target, true, x.Card);

    public static void DislodgeArrow(ref Exec x, byte target, bool hasCardSource, in Card cardSource)
    {
        int power = ImpaledPowerIndex(x.B, target);
        if (power < 0) return;
        int arrow = x.B.Powers[power].Aux1 - 1;
        if (arrow < 0) throw new InvalidOperationException("ImpaledPower has no arrow.");
        // Keep the power present through the dislodge hook, as native RemoveIfNoArrows runs afterwards.
        x.B.Powers[power].Aux1 = x.B.Fool.Arrows[arrow].Next;
        x.B.Fool.Arrows[arrow].Next = 0;
        RetrieveShotArrow(ref x, target, arrow, hasCardSource, cardSource);
        power = ImpaledPowerIndex(x.B, target);
        if (power >= 0 && x.B.Powers[power].Aux1 == 0) RemovePowerAt(ref x.B, power);
    }

    private static void RetrieveShotArrow(ref Exec x, byte target, int arrow,
        bool hasCardSource, in Card cardSource)
    {
        if (x.B.Fool.Arrows[arrow].DestroyAfterImpaled)
        {
            x.B.Fool.Arrows[arrow].SetStatus(2);
            return;
        }
        HookDispatch.Arrow(ref x, HookMaskExt.AfterArrowDislodged, target, arrow, hasCardSource, cardSource);
        if (!x.NeedsChoice) x.B.Fool.Arrows[arrow].Flags = 0;
    }

    public static void DislodgeAllArrows(ref Exec x, byte target)
        => DislodgeAllArrows(ref x, target, true, x.Card);

    public static void DislodgeAllArrows(ref Exec x, byte target, bool hasCardSource, in Card cardSource)
    {
        while (ImpaledPowerIndex(x.B, target) >= 0)
        {
            DislodgeArrow(ref x, target, hasCardSource, cardSource);
            if (x.NeedsChoice) return;
        }
    }
}
