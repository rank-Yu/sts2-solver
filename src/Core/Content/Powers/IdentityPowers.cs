using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>MonologuePower</c> (instanced): every card the player plays while it is active adds its
/// Strength to the owner after the play, then the applied total is removed at the owner's side turn
/// end. Strength lives in Aux0, StrengthApplied in Aux1; the transient pending list is in
/// <see cref="Exec.MonologuePending"/>. Existing engine:
/// <c>CorePowerSupport.CapturePendingMonologues/ResolveMonologues</c> and
/// <c>EndTurnPowerSupport.Batch048</c>'s Monologue case.
/// </summary>
[ContentId("MONOLOGUE_POWER")]
public sealed class MonologuePower : PowerDef
{
    public override bool IsIdentityContent => true;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        int strengthApplied = power.Aux1;
        int amount = power.Amount;
        int strengthValue = power.Aux0;
        byte owner = power.Owner;
        if (strengthApplied != 0 && Registry.TryPower("STRENGTH_POWER", out ushort strength))
        {
            // Retiring the temporary Strength buff can remove it (and shift this row), so address the
            // Monologue instance by its captured value, not by the pre-application row index.
            Kernel.ApplyPower(ref x, owner, strength, -strengthApplied, owner);
        }
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power candidate = ref x.B.Powers[index];
            if (candidate.Id == Id && candidate.Owner == owner && candidate.Amount == amount
                && candidate.Aux0 == strengthValue && candidate.Aux1 == strengthApplied)
            {
                Kernel.SetPowerAmountAt(ref x.B, index, 0);
                return;
            }
        }
    }
}

/// <summary>
/// Native <c>SwordSagePower</c>: its only state is the amount. The per-blade applied replay bonus is
/// normalized by <c>Kernel.NormalizeCardAfflictions</c> exactly like
/// <c>SimulatedCombatState.NormalizeSwordSageReplays</c>; the native <c>AfterCardEnteredCombat</c> /
/// <c>AfterRemoved</c> / <c>AfterPowerAmountChanged</c> have no separate mirror in the existing engine.
/// </summary>
[ContentId("SWORD_SAGE_POWER")]
public sealed class SwordSagePower : PowerDef
{
    public override bool IsIdentityContent => true;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// Native <c>OrbitPower</c> (instanced): four energy spent trigger <c>Amount</c> energy. Aux0 is the
/// accumulated remainder (<c>SimulatedCombatState._orbitEnergyRemainders</c>,
/// <c>PowerLifecycleSupport.AfterEnergySpent</c>).
/// </summary>
[ContentId("ORBIT_POWER")]
public sealed class OrbitPower : PowerDef
{
    public override bool IsIdentityContent => true;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override void AfterEnergySpent(ref Exec x, Listener self, in Card card, int amount)
    {
        if (amount <= 0 || self.Owner != Limits.PlayerOwner)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        int total = power.Aux0 + amount;
        power.Aux0 = total % 4;
        int triggers = total / 4;
        if (triggers > 0)
        {
            Kernel.GainEnergy(ref x, power.Amount * triggers);
        }
    }
}

/// <summary>
/// Native <c>PaleBlueDotPower</c>: a single stacking power whose per-turn latch (Aux0) prevents more
/// than one next-turn draw grant. <c>PowerLifecycleSupport.AfterCardPlayed</c> and
/// <c>EndTurnPowerSupport.TriggerRegular</c>'s reset are mirrored by the kernel and this definition.
/// </summary>
[ContentId("PALE_BLUE_DOT_POWER")]
public sealed class PaleBlueDotPower : PowerDef
{
    public override bool IsIdentityContent => true;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // PaleBlueDotPower.cs: after the fifth finished card play this turn, each positive instance
    // latches once (Aux0) and applies DrawCardsNextTurnPower with its amount. This runs from the
    // normal AfterCardPlayed pass, after the played card's own choice and tail effects, so a
    // DecisionsDecisions auto-play sequence counts toward the same threshold like the native
    // CardPlaysFinished history.
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Amount <= 0 || power.Aux0 != 0)
            return;
        if (x.B.Counter(Counter.CardsPlayedThisTurn) + 1 < 5)
            return;
        if (!Registry.TryPower("DRAW_CARDS_NEXT_TURN_POWER", out ushort drawNextTurn))
            throw new UnsupportedException("PaleBlueDot 需要 DRAW_CARDS_NEXT_TURN_POWER 定义。");
        power.Aux0 = 1;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, drawNextTurn, power.Amount, Limits.PlayerOwner);
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates && self.Owner == Limits.PlayerOwner)
            x.B.Powers[self.Index].Aux0 = 0;
    }
}

/// <summary>
/// Native <c>PhantomBladesPower</c>: Shivs gain Retain on application and on entering combat, and the
/// first Shiv powered attack of a turn gains <c>Amount</c> damage.
/// <c>PhantomBladesPowerMirrors.AfterApplied/AfterCardEnteredCombat</c> and
/// <c>ModifyDamageMirrors.HandlePhantomBladesPower</c>.
/// </summary>
[ContentId("PHANTOM_BLADES_POWER")]
public sealed class PhantomBladesPower : PowerDef
{
    public override bool IsIdentityContent => true;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        if (!damage.Props.IsPoweredAttack()
            || !damage.HasCardSource
            || (x.R.Card(damage.CardSource.Id, damage.CardSource.Upgrade).Tags & CardTag.Shiv) == 0
            || damage.Dealer != self.Owner
            || x.PlayIndex > 0
            || x.B.Counter(Counter.ShivsPlayedThisTurn) != 0)
        {
            return 0m;
        }
        return x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// Native <c>SpectrumShiftPower</c>: BeforeHandDraw adds <c>Amount</c> distinct colorless cards to the
/// hand. Existing engine: <c>TurnStartPowerSupport.ContinueBeforeHandDraw</c>'s SpectrumShift case.
/// </summary>
[ContentId("SPECTRUM_SHIFT_POWER")]
public sealed class SpectrumShiftPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.ColorlessAll];

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        if (amount > Limits.Cards)
            throw new CapacityException("GeneratedCards", Limits.Cards);
        Span<Card> generated = stackalloc Card[amount];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.ColorlessAll, amount,
            distinct: true, RngStream.CombatCardGeneration, generated);
        for (int index = 0; index < sampled; index++)
        {
            Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>NightmarePower</c> (instanced): at the owner's BeforeHandDraw, add <c>Amount</c> clones of
/// the selected card to the hand, then remove the instance. The selected card is a detached value in
/// <see cref="Battle.DetachedCards"/> (Aux0 = slot + 1). Existing engine:
/// <c>GenerateTurnStartPowerCards</c> and <c>SetNightmareSelection</c>.
/// </summary>
[ContentId("NIGHTMARE_POWER")]
public sealed class NightmarePower : PowerDef
{
    public override bool IsIdentityContent => true;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override bool UsesDetachedCard => true;

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        int amount = power.Amount;
        ushort slot = (ushort)power.Aux0;
        Card selected = Kernel.DetachedCard(x.B, power);
        for (int index = 0; index < amount; index++)
        {
            Kernel.AddGeneratedCard(ref x, Kernel.CloneCard(selected), Pile.Hand);
            if (x.NeedsChoice)
                return;
        }
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power candidate = ref x.B.Powers[index];
            if (candidate.Id == Id && candidate.Owner == self.Owner && candidate.Aux0 == slot)
            {
                Kernel.SetPowerAmountAt(ref x.B, index, 0);
                return;
            }
        }
    }
}
