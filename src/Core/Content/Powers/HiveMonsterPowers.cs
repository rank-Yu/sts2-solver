using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>HardToKillPower</c> (Exoskeleton, nine stacks applied in <c>AfterAddedToRoom</c>): the
/// owner's damage taken from one hit is capped at the power amount
/// (<c>ModifyDamageCap</c>). No private slot.
/// </summary>
[ContentId("HARD_TO_KILL_POWER")]
public sealed class HardToKillPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageCap(ref Exec x, Listener self, in DamageContext damage)
        => damage.Target == self.Owner ? x.B.Powers[self.Index].Amount : decimal.MaxValue;
}

/// <summary>
/// Native <c>PersonalHivePower</c> (Entomancer, one stack applied in <c>AfterAddedToRoom</c>): a
/// powered attack that hits the owner puts its amount of Dazed into the player's draw pile at random
/// (<c>AfterDamageReceivedMirrors.HandlePersonalHivePower</c>). No private slot.
/// </summary>
[ContentId("PERSONAL_HIVE_POWER")]
public sealed class PersonalHivePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("DAZED", 0)];

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || !result.Props.IsPoweredAttack())
            return;
        if (dealer != Limits.PlayerOwner && dealer != Limits.OstyOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        for (int index = 0; index < amount; index++)
        {
            Kernel.AddGeneratedCard(ref x,
                Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Dazed>()),
                Pile.Draw, PilePosition.Random, creator: Limits.NoOwner);
        }
    }
}

/// <summary>
/// Native <c>HatchPower</c> (ToughEgg, one or two stacks applied on entrance): loses one stack at the
/// end of its side (<c>EndTurnPowerSupport.TriggerRegular</c> case HatchPower). The count only gates
/// the hatch animation; the move state machine already starts at HATCH_MOVE.
/// </summary>
[ContentId("HATCH_POWER")]
public sealed class HatchPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, x.B.Powers[self.Index].Amount - 1);
    }
}

/// <summary>
/// Native <c>BurrowedPower</c> (Tunneler's BURROW_MOVE): the owner keeps its block at its side start
/// (<c>ShouldClearBlock</c>) and when that block is broken it removes this power, loses its remaining
/// block as unpowered block damage and is forced to the synthetic STUNNED with <c>BITE_MOVE</c> as
/// the follow-up (<c>TriggeredPowerSupport.CompensateBurrow</c>). The private <c>_isStunned</c> flag
/// is animation-only.
/// </summary>
[ContentId("BURROWED_POWER")]
public sealed class BurrowedPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool ShouldClearBlock(ref Exec x, Listener self, byte owner)
        => owner != self.Owner;

    // BurrowedPower.cs: AfterBlockBroken stuns the Tunneler and removes this power. Unlike the old
    // engine's deferred compensation, the native hook runs for every block break (including Thorns
    // and the enemy turn) and before the damage result's death pass.
    public override void AfterBlockBroken(ref Exec x, Listener self, byte target, byte breaker)
    {
        if (target != self.Owner || x.B.Powers[self.Index].Amount <= 0)
            return;
        if (x.B.Creature(self.Owner).IsDead)
            return;
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
        int block = x.B.Creature(self.Owner).Block;
        if (block > 0)
            Kernel.DamageBlock(ref x.B.Creature(self.Owner), block, ValueProps.Move);
        Kernel.ForceStunnedMove(ref x, self.Owner,
            Kernel.MonsterRules(ref x, self.Owner).NodeIndex("BITE_MOVE"));
    }
}

/// <summary>
/// Native <c>SandpitPower</c> (TheInsatiable's LIQUIFY_GROUND): a target-marking debuff. The existing
/// engine's compensation runs it in <c>TurnStartPowerSupport.TriggerAfterSideTurnStart</c> on the
/// enemy side (<c>S/../Prediction/TurnStartPowerSupport.cs:449</c>): decrement the exact stack, and
/// when it reaches zero force-kill the recorded target through the damage pipeline while both the
/// owner and the target are alive. The native screen/music hooks are out of scope.
/// </summary>
[ContentId("SANDPIT_POWER")]
public sealed class SandpitPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // SimulatedCombatState.ApplyTargeted branches on PowerInstanceType.Instanced: every LIQUIFY_GROUND
    // adds a fresh native Sandpit instance instead of stacking into the previous one.
    public override bool IsInstanced => true;

    public override void GlobalAfterSideTurnStart(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side != Side.Enemy || !participates)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Amount <= 0)
            return;
        byte target = power.Target;
        int occurrence = 0;
        for (int prior = 0; prior < self.Index; prior++)
        {
            ref readonly Power earlier = ref x.B.Powers[prior];
            if (earlier.Owner == self.Owner && earlier.Id == Id)
                occurrence++;
        }
        int remaining = power.Amount - 1;
        Kernel.ModifyPowerAmountAt(ref x.B, self.Index, remaining);
        // PowerCmd.Decrement is a no-op while IsEnding; only the captured row reaching zero kills.
        int row = Kernel.FindPowerOccurrence(x.B, self.Owner, Id, occurrence);
        if (row >= 0 && x.B.Powers[row].Amount > 0)
            return;
        if (target == Limits.NoOwner)
            throw new UnsupportedException("流沙坑归零时没有目标。");
        if (!x.B.Creature(self.Owner).IsAlive || !x.B.Creature(target).IsAlive)
            return;
        Span<byte> victims = stackalloc byte[1];
        victims[0] = target;
        Kernel.Kill(ref x, victims, force: true);
    }
}
