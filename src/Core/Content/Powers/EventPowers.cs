using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>FeedingFrenzyPower</c>, a positive <c>TemporaryStrengthPower</c>: the application adds
/// Strength (inside <c>BeforeApplied</c>) and the owner's side turn end removes it and takes the
/// Strength back.
/// </summary>
[ContentId("FEEDING_FRENZY_POWER")]
public sealed class FeedingFrenzyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override string? InternallyAppliedPowerId => "STRENGTH_POWER";

    public override bool IsTemporaryPower => true;

    // EndTurnPowerSupport.TriggerRegular: case TemporaryStrengthPower when the owner participates.
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
        {
            Kernel.RetireTemporaryStrength(ref x, self);
        }
    }
}

/// <summary>
/// Native <c>HelloWorldPower</c>: BeforeHandDraw adds <c>AmountOnTurnStart</c> distinct common
/// character cards to the player's hand (<c>TurnStartPowerSupport.ContinueBeforeHandDraw</c>).
/// </summary>
[ContentId("HELLO_WORLD_POWER")]
public sealed class HelloWorldPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool UsesAmountOnTurnStart => true;

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].AmountOnTurnStart;
        if (amount < 1)
            return;
        if (amount > Limits.Cards)
            throw new CapacityException("HelloWorld.Generated", Limits.Cards);
        Span<Card> generated = stackalloc Card[amount];
        int sampled = CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.None, amount,
            generated, CardPools.CharacterAll, CardRarity.Common);
        for (int index = 0; index < sampled; index++)
        {
            if (!Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>ReboundPower</c>: the next card played from the owner's hand goes to the top of the
/// draw pile instead of the discard pile; then the power decrements. It is removed at the owner's
/// side turn end by <c>CorePowerSupport.TriggerTransientSideTurnEndPowers</c>.
/// </summary>
[ContentId("REBOUND_POWER")]
public sealed class ReboundPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override Pile ModifyCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        bool isAutoPlay, int energyValue, Pile location, ref PilePosition position)
    {
        // ModifyCardPlayResultLocationMirrors.HandleReboundPower skips CardModel.IsDupe.
        if (x.CurrentPlayIsHistoryCourseDupe
            || self.Owner != Limits.PlayerOwner || location != Pile.Discard)
            return location;
        position = PilePosition.Top;
        return Pile.Draw;
    }

    public override void AfterModifyingCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        Pile location)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        Kernel.ModifyPowerAmountAt(ref x.B, self.Index, power.Amount - 1);
    }
}

/// <summary>
/// Native <c>ToricToughnessPower</c> (instanced): <c>AfterBlockCleared</c> gains the block value
/// stored by <c>SetBlock</c> as unpowered block, then decrements the turn count. Sts2Solver stores the
/// block value in <see cref="Power.Aux0"/> (Amount is the turn count).
/// </summary>
[ContentId("TORIC_TOUGHNESS_POWER")]
public sealed class ToricToughnessPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    // Two instances can be removed in the same block-clear pass, which shifts the occurrence of the
    // later row; the listener pass must track removals (root-level HasIdentityContent).
    public override bool IsIdentityContent => true;

    public override void AfterBlockCleared(ref Exec x, Listener self)
    {
        ref Power power = ref x.B.Powers[self.Index];
        int block = power.Aux0;
        if (block > 0)
        {
            Kernel.GainBlock(ref x, self.Owner, block, ValueProps.Unpowered, fromCard: false);
            if (x.NeedsChoice)
                return;
        }
        Kernel.ModifyPowerAmountAt(ref x.B, self.Index, power.Amount - 1);
    }
}

/// <summary>
/// Native <c>WraithFormPower</c>: at its owner's side turn start it applies -Amount Dexterity
/// (<c>PersistentPowerSupport.TriggerOwnerAfterSideTurnStart</c>).
/// </summary>
[ContentId("WRAITH_FORM_POWER")]
public sealed class WraithFormPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<DexterityPower>(), -amount, self.Owner);
    }
}

/// <summary>
/// Native <c>CuriousPower</c>: its owner's Power cards cost <c>Amount</c> less
/// (<c>HookMirrors.ModifyEnergyCostInCombat</c>, a pure query; the caller clamps at zero).
/// </summary>
[ContentId("CURIOUS_POWER")]
public sealed class CuriousPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombat(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        if (self.Owner != Limits.PlayerOwner)
            return amount;
        if (Registry.Card(card.Id).ResolveCardType(battle, rules, card) != CardType.Power)
            return amount;
        if (amount <= 0m)
            return amount;
        decimal modified = amount - battle.Powers[self.Index].Amount;
        return modified < 0m ? 0m : modified;
    }
}

/// <summary>
/// Native <c>BattlewornDummyTimeLimitPower</c> (the three Battleworn Dummy variants): at its owner's
/// side turn end it decrements while the amount is above 1. On the last tick the native power sets
/// the encounter's <c>RanOutOfTime</c> and escapes the dummy; the existing engine records the
/// encounter flag (<c>SimulatedCombatState.MarkBattlewornDummyTimedOut</c>) and ends the action at
/// <c>SearchBoundaryReason.EventDefeat</c> before the death/poison passes. Sts2Solver mirrors exactly that
/// amount-1 state and sets <see cref="Outcome.EventDefeat"/>; it does not remove the dummy (the
/// existing engine returns before the escape).
/// </summary>
[ContentId("BATTLEWORN_DUMMY_TIME_LIMIT_POWER")]
public sealed class BattlewornDummyTimeLimitPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates || !x.B.Creature(self.Owner).IsAlive)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Amount > 1)
        {
            Kernel.ModifyPowerAmountAt(ref x.B, self.Index, power.Amount - 1);
            return;
        }
        // EndTurnPowerSupport: the last tick leaves Amount at 1 and the engine's flag is read only
        // after TriggerEnemySideTurnEndEffects returns. RunEnemyTurn stops there.
        x.B.Outcome = Outcome.EventDefeat;
    }
}

/// <summary>
/// Native <c>ForbiddenGrimoirePower</c>: its only hook is <c>AfterCombatEnd</c> (adds a card-removal
/// reward). A combat has no such effect, so the definition only carries the counter; the existing
/// engine records <c>RecordLongTermResource(50)</c> and a growth reward when the card is played.
/// </summary>
[ContentId("FORBIDDEN_GRIMOIRE_POWER")]
public sealed class ForbiddenGrimoirePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// Native <c>ImprovementPower</c>: its only hook is <c>AfterCombatEnd</c> (upgrades run-deck cards
/// after the fight). No combat settlement reads it, so the definition carries the counter only.
/// </summary>
[ContentId("IMPROVEMENT_POWER")]
public sealed class ImprovementPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}
