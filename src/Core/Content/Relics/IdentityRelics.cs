using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// Native <c>Vambrace</c>: the first card-sourced block each combat is doubled once; the triggering
/// card's identity lives only until that card's AfterCardPlayed. Existing engine:
/// <c>AfterModifyingBlockAmountMirrors.HandleVambrace</c>,
/// <c>ModifyBlockMultiplicativeMirrors.HandleVambrace</c> and
/// <c>AfterCardPlayedMirrors.HandleVambrace</c> (VambracePredictionState).
/// </summary>
[ContentId("VAMBRACE")]
public sealed partial class Vambrace : RelicDef
{
    public override bool HasCombatState => true;

    public override bool IsIdentityContent => true;

    public override decimal ModifyBlockMultiplicative(ref Exec x, Listener self, in BlockContext block,
        decimal amount)
    {
        if (x.B.RelicState(x.R, self.Index).Counter != 0
            || (block.Props & ValueProps.Move) == 0
            || !block.HasCardSource)
        {
            return 1m;
        }
        if (x.VambraceTriggerActive && x.VambraceTriggerSerial != SourceSerial(x))
            return 1m;
        return 2m;
    }

    /// <summary>
    /// The card scope a block belongs to. Native Vambrace keys on the source card itself
    /// (TriggeringCard = cardSource); the kernel approximates it by the card power-source scope. An
    /// enchantment's block (Adroit) resolves after the card's own scope completed, so outside an
    /// open scope the block belongs to the play that just completed it.
    /// </summary>
    private static int SourceSerial(in Exec x)
        => x.CardPowerSourceActive ? x.CardPowerSourceSerial : x.LastCardPowerSourceSerial;

    public override void AfterModifyingBlockAmount(ref Exec x, Listener self, decimal modified,
        in BlockContext block)
    {
        if (modified <= 0m || !block.HasCardSource)
            return;
        x.VambraceTriggerActive = true;
        x.VambraceTriggerPosition = x.PlayPosition;
        x.VambraceTriggerSerial = SourceSerial(x);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (x.B.RelicState(x.R, self.Index).Counter != 0 || !x.VambraceTriggerActive)
            return;
        int relative = play.Position - Piles.Start(x.B, Pile.Play);
        if (relative != x.VambraceTriggerPosition || x.VambraceTriggerSerial != x.LastCardPowerSourceSerial)
            return;
        x.B.RelicState(x.R, self.Index).Counter = 1;
    }
}

/// <summary>
/// Native <c>UnsettlingLamp</c>: the first debuff a player's card applies doubles once per instance of
/// that temporary power's internal type. The triggering card lives only inside the card's power
/// application scope; the finished latch is the relic Counter. Existing engine:
/// <c>SimulatedCombatState.ObserveUnsettlingLamp/ModifyPowerAmountForUnsettlingLamp/
/// CompleteCardPowerApplication</c>.
/// </summary>
[ContentId("UNSETTLING_LAMP")]
public sealed partial class UnsettlingLamp : RelicDef
{
    public override bool HasCombatState => true;

    public override bool IsIdentityContent => true;

    public override bool IsUnsettlingLamp => true;

    public override void BeforePowerAmountChanged(ref Exec x, Listener self, ushort powerId, int amount,
        byte target, byte applier, int cardSourceSerial)
    {
        if (cardSourceSerial == 0
            || x.LampTriggerSerial != 0
            || x.B.RelicState(x.R, self.Index).Counter != 0
            || applier != self.Owner
            || Kernel.IsPlayerSide(target))
        {
            return;
        }
        PowerDef def = Registry.Power(powerId);
        if (!def.IsVisible || def.TypeForAmount(amount) != PowerType.Debuff)
            return;
        if (Registry.TryPower("ARTIFACT_POWER", out ushort artifact)
            && Kernel.GetAmount(x.B, target, artifact) > 0)
        {
            return;
        }
        x.LampTriggerSerial = cardSourceSerial;
        if (!def.IsTemporaryPower || def.InternallyAppliedPowerId is not { } internalId)
            return;
        if (!Registry.TryPower(internalId, out ushort internalPower))
            throw new UnsupportedException($"临时 Power {def.NativeId} 的内部 Power {internalId} 没有定义。");
        for (int index = 0; index < x.LampInternalTypeCount; index++)
        {
            if (x.LampInternalTypes[index] == internalPower)
                return;
        }
        if (x.LampInternalTypeCount >= Limits.LampInternalPowerTypes)
            throw new CapacityException("LampInternalPowerTypes", Limits.LampInternalPowerTypes);
        x.LampInternalTypes[x.LampInternalTypeCount++] = internalPower;
    }

    public override decimal ModifyPowerAmountGivenMultiplicative(ref Exec x, Listener self,
        ushort powerId, byte giver, byte target, decimal amount, int cardSourceSerial)
    {
        if (cardSourceSerial == 0
            || x.LampTriggerSerial == 0
            || x.LampTriggerSerial != cardSourceSerial)
        {
            return 1m;
        }
        for (int index = 0; index < x.LampInternalTypeCount; index++)
        {
            if (x.LampInternalTypes[index] == powerId)
                return 1m;
        }
        if (Registry.Power(powerId).TypeForAmount((int)amount) != PowerType.Debuff)
            return 1m;
        return 2m;
    }
}
