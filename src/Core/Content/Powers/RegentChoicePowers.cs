using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// <c>TyrannyPower</c>: the owner draws Amount extra cards (<c>ModifyHandDraw</c>) and at the player
/// turn start exhausts Amount cards from hand (<c>TurnStartPowerSupport.ContinueAfterPlayerTurnStart</c>
/// TyrannyPower case → <c>TurnStartChoiceSupport.Resolve(Exhaust)</c>).
/// </summary>
[ContentId("TYRANNY_POWER")]
public sealed class TyrannyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + x.B.Powers[self.Index].Amount : amount;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        // CardSelectCmd.FromHand is called even with an empty hand and pauses before it reads the
        // hand; the prefs carry the power amount fixed before that pause. A side-start card added
        // during the pause therefore becomes a candidate.
        x.RunSetupPause();
        int count = Math.Min(amount, x.B.HandCount);
        if (count <= 0)
            return;
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[count];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Exhaust, Pile.Hand, options, count, selected,
                out int chosen))
        {
            return;
        }
        Kernel.ExhaustSelected(ref x, Pile.Hand, selected[..chosen]);
    }
}

/// <summary>
/// <c>ForegoneConclusionPower</c>: at the player's BeforeHandDraw, shuffle if necessary, move Amount
/// cards from the draw pile into hand, then remove the power
/// (<c>TurnStartPowerSupport.ContinueBeforeHandDraw</c> ForegoneConclusionPower case).
/// </summary>
[ContentId("FOREGONE_CONCLUSION_POWER")]
public sealed class ForegoneConclusionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if (x.B.DrawCount == 0 && x.B.DiscardCount != 0)
        {
            Kernel.Shuffle(ref x);
            if (x.NeedsChoice)
                return;
        }
        int count = Math.Min(x.B.Powers[self.Index].Amount, x.B.DrawCount);
        // CardSelectCmd.FromCombatPile pauses before it reads the pile, and it pauses even when
        // the pile is empty. The prefs are fixed by the caller before the pause.
        x.RunSetupPause();
        if (count > 0)
        {
            Span<int> options = stackalloc int[x.B.DrawCount];
            for (int index = 0; index < options.Length; index++)
                options[index] = index;
            Span<int> selected = stackalloc int[count];
            if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToHand, Pile.Draw, options, count,
                    selected, out int chosen))
            {
                return;
            }
            Kernel.MoveSelected(ref x.B, Pile.Draw, selected[..chosen], Pile.Hand,
                PilePosition.Bottom);
            if (x.NeedsChoice)
                return;
        }
        Kernel.SetPowerAmount(ref x.B, Limits.PlayerOwner,
            Registry.PowerId<ForegoneConclusionPower>(), 0);
    }
}

/// <summary>
/// <c>TheSealedThronePower</c>: its owner gains Amount stars before each card play
/// (<c>BeforeCardPlayedMirrors.HandleTheSealedThronePower</c>).
/// </summary>
[ContentId("THE_SEALED_THRONE_POWER")]
public sealed class TheSealedThronePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        Kernel.GainStars(ref x, x.B.Powers[self.Index].Amount);
    }
}

/// <summary>
/// <c>VoidFormPower</c>: the first Amount non-auto card plays of its owner each turn cost 0 energy and
/// 0 stars in Hand/Play (<c>ModifyEnergyCostInCombatMirrors</c>,
/// <c>ModifyStarCostMirrors</c> and <c>AfterCardPlayedMirrors</c>). <see cref="Power.Aux0"/> is
/// <c>VoidFormPredictionState.CardsPlayedThisTurn</c>; <c>PrepareVoidFormApplication</c> maxes it when
/// the power is already active during a VoidForm play.
/// </summary>
[ContentId("VOID_FORM_POWER")]
public sealed class VoidFormPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules,
        Listener self, in Card card, Pile pile, decimal amount)
        => ShouldFree(battle, self, pile) ? 0m : amount;

    public override decimal TryModifyStarCost(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
        => ShouldFree(battle, self, pile) ? 0m : amount;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner || x.CurrentPlayIsAutoPlay
            || play.PlayIndex != play.PlayCount - 1)
        {
            return;
        }
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Amount <= 0)
            return;
        if (power.Aux0 == int.MaxValue)
            throw new UnsupportedException("VoidForm 的本回合出牌计数溢出。");
        power.Aux0++;
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
    {
        if (participates && self.Owner == Limits.PlayerOwner)
            x.B.Powers[self.Index].Aux0 = 0;
    }

    private static bool ShouldFree(in Battle battle, Listener self, Pile pile)
    {
        if (self.Owner != Limits.PlayerOwner || pile is not (Pile.Hand or Pile.Play))
            return false;
        return battle.Powers[self.Index].Aux0 < battle.Powers[self.Index].Amount;
    }
}
