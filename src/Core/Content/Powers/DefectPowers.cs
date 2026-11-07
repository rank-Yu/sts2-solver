using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

[ContentId("FOCUS_POWER")]
public sealed class FocusPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => true;

    public override decimal ModifyOrbValue(in Battle battle, Rules rules, Listener self, in Orb orb,
        decimal value)
    {
        if (self.Owner != Limits.PlayerOwner)
            return value;
        return Math.Max(value + battle.Powers[self.Index].Amount, 0m);
    }
}

public abstract class TemporaryFocusDef : PowerDef
{
    public override PowerType Type => IsPositive ? PowerType.Buff : PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    protected virtual bool IsPositive => true;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryFocus(ref x, self);
    }
}

[ContentId("FOCUSED_STRIKE_POWER")]
public sealed class FocusedStrikePower : TemporaryFocusDef;

[ContentId("HOTFIX_POWER")]
public sealed class HotfixPower : TemporaryFocusDef;

[ContentId("SYNCHRONIZE_POWER")]
public sealed class SynchronizePower : TemporaryFocusDef;

[ContentId("HYPERBEAM_FOCUS_DOWN_POWER")]
public sealed class HyperbeamFocusDownPower : TemporaryFocusDef
{
    protected override bool IsPositive => false;
}

[ContentId("ITERATION_POWER")]
public sealed class IterationPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if ((uint)drawnPosition >= (uint)x.B.CardCount)
            throw new UnsupportedException("抽牌触发时抽到的牌已不在牌堆。");
        Card card = x.B.Cards[drawnPosition];
        if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Status)
            return;
        if (x.B.Counter(Counter.StatusCardsDrawnThisTurn) > 1)
            return;
        Kernel.Draw(ref x, x.B.Powers[self.Index].Amount, fromHandDraw: false);
    }
}

[ContentId("MACHINE_LEARNING_POWER")]
public sealed class MachineLearningPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? amount + x.B.Powers[self.Index].Amount : amount;
}

[ContentId("LOOP_POWER")]
public sealed class LoopPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner || x.B.OrbCount == 0)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        for (int index = 0; index < amount; index++)
        {
            Kernel.OrbPassiveAt(ref x, 0);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("CREATIVE_AI_POWER")]
public sealed class CreativeAiPower : PowerDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterPowers];

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Span<Card> generated = stackalloc Card[Limits.MaxHandSize];
        int count = 0;
        for (int index = 0; index < amount; index++)
        {
            // CardFactory.GetDistinctForCombat(..., 1, CombatCardGeneration) once per stack.
            if (count >= generated.Length)
                throw new CapacityException("CreativeAi.Generated", generated.Length);
            if (CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.Power, 1,
                    generated[count..], CardPools.CharacterPowers) == 0)
            {
                break;
            }
            count++;
        }
        // TurnStartPowerSupport queues all generated cards first, then adds them one by one.
        for (int index = 0; index < count; index++)
        {
            if (!Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("SPINNER_POWER")]
public sealed class SpinnerPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterEnergyReset(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("GLASS_ORB"), x.B.Powers[self.Index].Amount);
    }
}

[ContentId("LIGHTNING_ROD_POWER")]
public sealed class LightningRodPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterEnergyReset(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.ChannelOrb(ref x, Kernel.OrbId("LIGHTNING_ORB"));
        if (x.NeedsChoice)
            return;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, amount - 1);
    }
}

[ContentId("FREE_POWER_POWER")]
public sealed class FreePowerPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        if (self.Owner != Limits.PlayerOwner
            || Registry.Card(card.Id).ResolveCardType(battle, rules, card) != CardType.Power)
            return amount;
        return pile is Pile.Hand or Pile.Play ? 0m : amount;
    }

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Power)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.ModifyPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, amount - 1);
    }
}

[ContentId("HAILSTORM_POWER")]
public sealed class HailstormPower : PowerDef
{
    public override IReadOnlyList<string> VarNames => ["FrostOrbs"];

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner))
            return;
        int frost = Kernel.CountOrbs(x.B, "FROST_ORB");
        if (frost < (int)x.R.PowerVar(x.B.Powers[self.Index].Id, 0))
            return;
        CardEffects.DamageAllEnemies(ref x, x.B.Powers[self.Index].Amount, ValueProps.Unpowered,
            self.Owner);
    }
}

[ContentId("STORM_POWER")]
public sealed class StormPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) == CardType.Power)
            x.PushCardPlayPair(CardPlayPairKind.StormLightning, self.Owner, x.B.Powers[self.Index].Id,
                x.B.Powers[self.Index].Amount);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (x.TryTakeCardPlayPair(CardPlayPairKind.StormLightning, self.Owner, x.B.Powers[self.Index].Id,
                out int amount) && amount > 0)
        {
            Kernel.ChannelOrbs(ref x, Kernel.OrbId("LIGHTNING_ORB"), amount);
        }
    }
}

[ContentId("SUBROUTINE_POWER")]
public sealed class SubroutinePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) == CardType.Power)
            x.PushCardPlayPair(CardPlayPairKind.SubroutineEnergy, self.Owner, x.B.Powers[self.Index].Id,
                x.B.Powers[self.Index].Amount);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (x.TryTakeCardPlayPair(CardPlayPairKind.SubroutineEnergy, self.Owner, x.B.Powers[self.Index].Id,
                out int amount) && amount > 0)
        {
            Kernel.GainEnergy(ref x, amount);
        }
    }
}

[ContentId("THUNDER_POWER")]
public sealed class ThunderPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterOrbEvoked(ref Exec x, Listener self, in Orb orb,
        scoped ReadOnlySpan<byte> targets)
    {
        if (self.Owner != Limits.PlayerOwner || !Registry.TryOrb("LIGHTNING_ORB", out ushort lightning)
            || orb.Id != lightning)
        {
            return;
        }
        Span<byte> living = stackalloc byte[Limits.Enemies + 1];
        int count = 0;
        foreach (byte target in targets)
        {
            if (Kernel.IsHittable(x.B, target))
                living[count++] = target;
        }
        if (count > 0)
            Kernel.Damage(ref x, living[..count], x.B.Powers[self.Index].Amount, ValueProps.Unpowered,
                self.Owner, hasCardSource: false, default);
    }
}

[ContentId("SMOKESTACK_POWER")]
public sealed class SmokestackPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position, byte creator)
    {
        if (creator != Limits.PlayerOwner)
            return;
        if ((uint)position >= (uint)x.B.CardCount)
            throw new UnsupportedException("生成牌触发时该牌已不在牌堆。");
        Card card = x.B.Cards[position];
        if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Status)
            return;
        CardEffects.DamageAllEnemies(ref x, x.B.Powers[self.Index].Amount, ValueProps.Unpowered,
            self.Owner);
    }
}

[ContentId("TRASH_TO_TREASURE_POWER")]
public sealed class TrashToTreasurePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position, byte creator)
    {
        if (creator != Limits.PlayerOwner)
            return;
        if ((uint)position >= (uint)x.B.CardCount)
            throw new UnsupportedException("生成牌触发时该牌已不在牌堆。");
        Card card = x.B.Cards[position];
        if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Status)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        for (int index = 0; index < amount; index++)
        {
            // Native draws the random orb before OrbCmd.Channel; a refused channel does not stop the
            // loop, so every layer still consumes its CombatOrbGeneration draw.
            ushort orb = Kernel.RandomOrb(ref x.B);
            Kernel.ChannelOrb(ref x, orb);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("ECHO_FORM_POWER")]
public sealed class EchoFormPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount)
    {
        if (self.Owner != Limits.PlayerOwner)
            return playCount;
        int started = x.B.Counter(Counter.CardPlaySeriesStartedThisTurn);
        return started < x.B.Powers[self.Index].Amount ? playCount + 1 : playCount;
    }
}

[ContentId("SIGNAL_BOOST_POWER")]
public sealed class SignalBoostPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount)
        => Kernel.ResolveCardType(x.B, x.R, card) == CardType.Power ? playCount + 1 : playCount;

    public override void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.ModifyPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, amount - 1);
    }
}

[ContentId("FERAL_POWER")]
public sealed class FeralPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // FeralPredictionState.zeroCostAttacksPlayed lives in Power.Aux0.
    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        x.B.Powers[self.Index].Aux0 = 0;
    }

    public override Pile ModifyCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        bool isAutoPlay, int energyValue, Pile location, ref PilePosition position)
    {
        // Native FeralPower checks CardModel.IsDupe, not IsClone. The only dupes Sts2Solver creates are the
        // HistoryCourse auto-play copies; Card.IsClone marks native CreateClone copies
        // (AdaptiveStrike), which FeralPower does not exclude.
        if (x.CurrentPlayIsHistoryCourseDupe
            || Kernel.ResolveCardType(x.B, x.R, card) != CardType.Attack || energyValue > 0)
            return location;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 >= power.Amount)
            return location;
        position = PilePosition.Top;
        return Pile.Hand;
    }

    public override void AfterModifyingCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        Pile location)
    {
        x.B.Powers[self.Index].Aux0++;
    }
}

[ContentId("CONSUMING_SHADOW_POWER")]
public sealed class ConsumingShadowPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates || x.B.OrbCount == 0)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        for (int index = 0; index < amount && x.B.OrbCount > 0; index++)
        {
            Kernel.EvokeOrb(ref x, x.B.OrbCount - 1);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("COOLANT_POWER")]
public sealed class CoolantPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        int block = Kernel.DistinctOrbCount(x.B) * x.B.Powers[self.Index].Amount;
        if (block > 0)
            Kernel.GainBlock(ref x, self.Owner, block, ValueProps.Unpowered, fromCard: false);
    }
}

[ContentId("BIASED_COGNITION_POWER")]
public sealed class BiasedCognitionPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<FocusPower>(),
            -x.B.Powers[self.Index].Amount, self.Owner);
    }
}

[ContentId("REPTILE_TRINKET_POWER")]
public sealed class ReptileTrinketPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryStrength(ref x, self);
    }
}
