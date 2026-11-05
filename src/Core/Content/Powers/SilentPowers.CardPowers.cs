using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>BurstPower</c>: a Skill gets one extra play; each modification decrements the power,
/// and the whole power is removed at the end of its owner's side turn
/// (<c>ModifyCardPlayCountMirrors</c>, <c>CorePowerSupport.EndTurnRegular</c>'s Burst case).
/// </summary>
[ContentId("BURST_POWER")]
public sealed class BurstPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount)
    {
        if (self.Owner != Limits.PlayerOwner || Kernel.ResolveCardType(x.B, x.R, card) != CardType.Skill)
            return playCount;
        return playCount + 1;
    }

    public override void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
    {
        if (x.B.Powers[self.Index].Amount > 0)
            Kernel.ModifyPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id,
                x.B.Powers[self.Index].Amount - 1);
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates && x.B.Powers[self.Index].Amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// Native <c>ShadowmeldPower</c>: every block its owner gains is multiplied by 2^Amount
/// (<c>ModifyBlockMultiplicativeMirrors.HandleShadowmeldPower</c>); removed at the end of the owner's
/// side turn.
/// </summary>
[ContentId("SHADOWMELD_POWER")]
public sealed class ShadowmeldPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyBlockMultiplicative(ref Exec x, Listener self, in BlockContext block,
        decimal amount)
    {
        if (block.Target != self.Owner)
            return 1m;
        int stacks = x.B.Powers[self.Index].Amount;
        // The native double already leaves decimal range at 96 stacks; the existing engine's facade
        // returns decimal.MaxValue there so the settlement ceiling applies.
        return stacks >= 96 ? decimal.MaxValue : (decimal)Math.Pow(2.0, stacks);
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates && x.B.Powers[self.Index].Amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// Native <c>ToolsOfTheTradePower</c>: <c>ModifyHandDraw</c> adds the amount, and at the player's
/// turn start the owner discards that many cards (<c>TurnStartPowerSupport</c>'s
/// ToolsOfTheTradePower case with <c>PlanChoiceEffect.Discard</c>).
/// </summary>
[ContentId("TOOLS_OF_THE_TRADE_POWER")]
public sealed class ToolsOfTheTradePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + x.B.Powers[self.Index].Amount : amount;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        // CardSelectCmd.FromHand signals the setup pause before it reads the hand. A side-start
        // card generated during that pause (Crossbow) must be part of the discard candidates, so
        // run the pause before taking the hand size.
        x.RunSetupPause();
        int hand = x.B.HandCount;
        if (hand == 0)
            return;
        Span<int> options = stackalloc int[hand];
        for (int index = 0; index < hand; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[hand];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Discard, Pile.Hand, options, amount, selected,
                out int chosen))
        {
            return;
        }
        // TurnStartChoiceSupport.Resolve(Discard) calls simulator.Discard(selected), which is the same
        // path as DiscardAndDraw with no draw: counter, AfterCardDiscarded and Sly auto-play.
        Kernel.DiscardAndDraw(ref x, Pile.Hand, selected[..chosen], 0);
    }
}

/// <summary>
/// Native <c>TheHuntPower</c>: a visual counter only. Its amount is read by the evaluation
/// (<c>missedTheHuntRewards</c>); it has no combat hook.
/// </summary>
[ContentId("THE_HUNT_POWER")]
public sealed class TheHuntPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// Native <c>AnticipatePower</c> (a positive <c>TemporaryDexterityPower</c>): removed at its owner's
/// side turn end, taking back its amount of Dexterity
/// (<c>EndTurnPowerSupport.TriggerRegular</c>, <c>SimulatedCombatState.RetireTemporaryStat</c>).
/// </summary>
[ContentId("ANTICIPATE_POWER")]
public sealed class AnticipatePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override string? InternallyAppliedPowerId => "DEXTERITY_POWER";

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsTemporaryPower => true;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
        if (amount == 0)
            return;
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<DexterityPower>(), -amount, self.Owner);
    }
}

/// <summary>
/// Native <c>SerpentFormPower</c>: before a card is played, snapshot the power amount for that play;
/// after it, deal that amount as Unpowered damage to a random hittable enemy
/// (<c>BeforeCardPlayedMirrors.HandleSerpentFormPower</c> + <c>AfterCardPlayedMirrors</c>). The
/// per-play snapshot is the stack-only <see cref="CardPlayPairKind.SerpentFormDamage"/> pair.
/// </summary>
[ContentId("SERPENT_FORM_POWER")]
public sealed class SerpentFormPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        x.PushCardPlayPair(CardPlayPairKind.SerpentFormDamage, self.Owner, Id,
            x.B.Powers[self.Index].Amount);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!x.TryTakeCardPlayPair(CardPlayPairKind.SerpentFormDamage, self.Owner, Id, out int amount))
            return;
        if (amount <= 0)
            return;
        CardEffects.DamageRandomEnemy(ref x, amount, ValueProps.Unpowered, self.Owner);
        Kernel.ApplyEnemyDeathPowers(ref x);
    }
}
