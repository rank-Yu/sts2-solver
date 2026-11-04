using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// <c>PoisonPower</c>: the counter is triggered by the kernel's <c>TriggerPoison</c> (enemy side start
/// for enemies, enemy side end for the player), matching <c>CorePowerSupport.TriggerPoison</c>. No hook
/// of its own runs there.
/// </summary>
[ContentId("POISON_POWER")]
public sealed class PoisonPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// <c>AccelerantPower</c>: read only by the poison trigger (one extra hit per layer, capped at the
/// poison amount in <c>TriggerPoison</c>).
/// </summary>
[ContentId("ACCELERANT_POWER")]
public sealed class AccelerantPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// <c>DrawCardsNextTurnPower</c>: <c>ModifyHandDraw</c> adds the amount once it was snapshotted at a
/// turn start (<c>AmountOnTurnStart != 0</c>); <c>BaseSideTurnStart</c> clears it
/// (<c>SimulatedCombatState.TriggerBaseSideTurnStart</c>). The not-yet-snapshotted case is added by the
/// kernel's <c>ConsumeModifiedHandDraw</c> equivalent.
/// </summary>
[ContentId("DRAW_CARDS_NEXT_TURN_POWER")]
public sealed class DrawCardsNextTurnPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool UsesAmountOnTurnStart => true;

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
    {
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (power.AmountOnTurnStart != 0 && power.Amount > 0)
        {
            return amount + power.Amount;
        }
        return amount;
    }

    public override void BaseSideTurnStart(ref Exec x, Listener self, bool decrementPlating)
    {
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (power.Amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, power.Id, 0);
    }
}

/// <summary>
/// <c>EnergyNextTurnPower</c>: consumed directly by the turn-start energy reset
/// (<c>SimulatedCombatState.ConsumeEnergyNextTurn</c>), so its native AfterEnergyReset never runs.
/// </summary>
[ContentId("ENERGY_NEXT_TURN_POWER")]
public sealed class EnergyNextTurnPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// <c>DoubleDamagePower</c>: doubles powered card attacks of its owner (or its owner's pets), and
/// decrements at its owner's side turn end (<c>CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects</c>
/// for the player, <c>TriggerEnemySideTurnEndEffects</c> for enemies).
/// </summary>
[ContentId("DOUBLE_DAMAGE_POWER")]
public sealed class DoubleDamagePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        bool pet = self.Owner == Limits.PlayerOwner && damage.Dealer == Limits.OstyOwner;
        if (damage.Dealer != self.Owner && !pet)
            return 1m;
        if (!damage.Props.IsPoweredAttack() || !damage.HasCardSource)
            return 1m;
        return 2m;
    }

    public override void PlayerSideTurnEnd(ref Exec x, Listener self)
        => Tick(ref x, self);

    public override void EnemySideTurnEnd(ref Exec x, Listener self)
        => Tick(ref x, self);

    private static void Tick(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, amount - 1);
    }
}

/// <summary>
/// <c>BlurPower</c>: <c>ShouldClearBlock</c> keeps the owner's block at the side start, then the power
/// decrements there (<c>SimulatedCombatState.TriggerBaseSideTurnStart</c>'s <c>TickDuration&lt;BlurPower&gt;</c>).
/// </summary>
[ContentId("BLUR_POWER")]
public sealed class BlurPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool ShouldClearBlock(ref Exec x, Listener self, byte owner)
    {
        if (owner == self.Owner)
        {
            return false;
        }
        return true;
    }

    public override void BaseSideTurnStart(ref Exec x, Listener self, bool decrementPlating)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, amount - 1);
    }
}

/// <summary>
/// <c>ShadowStepPower</c>: at the owner's side turn start it applies DoubleDamage with its amount and
/// clears itself (<c>PersistentPowerSupport.TriggerOwnerAfterSideTurnStart</c>'s SHADOW_STEP_POWER case).
/// </summary>
[ContentId("SHADOW_STEP_POWER")]
public sealed class ShadowStepPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<DoubleDamagePower>(), amount, self.Owner);
        Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// <c>AccuracyPower</c>: <c>ModifyDamageAdditive</c> adds the amount to powered attacks from a Shiv
/// (<c>AccuracyPower.ModifyDamageAdditive</c>; the card is read through the damage source).
/// </summary>
[ContentId("ACCURACY_POWER")]
public sealed class AccuracyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        if (damage.Dealer != self.Owner || !damage.Props.IsPoweredAttack() || !damage.HasCardSource)
            return 0m;
        if ((x.R.Card(damage.CardSource.Id, damage.CardSource.Upgrade).Tags & CardTag.Shiv) == 0)
            return 0m;
        return x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// <c>AfterimagePower</c>: <c>BeforeCardPlayed</c> snapshots the amount per play, and
/// <c>AfterCardPlayed</c> gains that much Unpowered block. The snapshot is the existing engine's
/// <c>CardPlayPairPredictionState</c>, carried on the transient play stack.
/// </summary>
[ContentId("AFTERIMAGE_POWER")]
public sealed class AfterimagePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        x.PushCardPlayPair(CardPlayPairKind.AfterimageBlock, self.Owner, x.B.Powers[self.Index].Id,
            x.B.Powers[self.Index].Amount);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!x.TryTakeCardPlayPair(CardPlayPairKind.AfterimageBlock, self.Owner,
                x.B.Powers[self.Index].Id, out int amount))
        {
            return;
        }
        if (amount > 0)
        {
            Kernel.GainBlock(ref x, self.Owner, amount, ValueProps.Unpowered, fromCard: false);
        }
    }
}

/// <summary>
/// <c>InfiniteBladesPower</c>: <c>BeforeHandDraw</c> adds its amount of Shivs to hand
/// (<c>SimulatedCombatState.GenerateTurnStartPowerCards</c>).
/// </summary>
[ContentId("INFINITE_BLADES_POWER")]
public sealed class InfiniteBladesPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SHIV", 0)];

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0 || self.Owner != Limits.PlayerOwner)
            return;
        if (!Registry.TryCard("SHIV", out ushort shiv))
            throw new UnsupportedException("无限刀刃需要 SHIV 的 Sts2Solver 定义。");
        for (int index = 0; index < amount; index++)
        {
            if (!Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, shiv), Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// <c>MasterPlannerPower</c>: <c>AfterCardPlayed</c> adds the Sly keyword to a Skill its owner played
/// (<c>AfterCardPlayedMirrors.HandleMasterPlannerPower</c>). Sts2Solver has no card identity, so the keyword
/// is written to the card's play-pile entry, which becomes the discard-pile entry.
/// </summary>
[ContentId("MASTER_PLANNER_POWER")]
public sealed class MasterPlannerPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Skill)
            return;
        if ((uint)play.Position >= (uint)x.B.CardCount)
            throw new UnsupportedException("策划大师找不到刚打出的技能牌。");
        x.B.Cards[play.Position].Keywords |= Keywords.Sly;
    }
}

/// <summary>
/// <c>FanOfKnivesPower</c>: a marker read by <c>Shiv.ResolveTargetType</c> (all enemies while present). The
/// amount is not read.
/// </summary>
[ContentId("FAN_OF_KNIVES_POWER")]
public sealed class FanOfKnivesPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;
}

/// <summary><c>WellLaidPlansPower</c>: <c>ShouldFlush</c> keeps the whole hand at the turn end.</summary>
[ContentId("WELL_LAID_PLANS_POWER")]
public sealed class WellLaidPlansPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool ShouldFlush(ref Exec x, Listener self) => self.Owner != Limits.PlayerOwner;
}

/// <summary>
/// <c>SpeedsterPower</c>: <c>AfterCardDrawn</c> for a draw outside the hand draw deals its amount
/// Unpowered to every hittable enemy (<c>AfterCardDrawnMirrors.HandleSpeedsterPower</c>).
/// </summary>
[ContentId("SPEEDSTER_POWER")]
public sealed class SpeedsterPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (fromHandDraw || self.Owner != Limits.PlayerOwner || x.B.Side != Side.Player)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        CardEffects.DamageAllEnemies(ref x, amount, ValueProps.Unpowered, self.Owner);
    }
}

/// <summary>
/// <c>EnvenomPower</c>: <c>AfterDamageGiven</c> applies Poison to the target of a powered attack that
/// dealt unblocked damage (<c>AfterDamageGivenMirrors.HandleEnvenomPower</c>).
/// </summary>
[ContentId("ENVENOM_POWER")]
public sealed class EnvenomPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageGiven(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (dealer != self.Owner || !result.Props.IsPoweredAttack() || result.UnblockedDamage <= 0)
            return;
        Kernel.ApplyPowerWithoutCardSource(ref x, result.Receiver, Registry.PowerId<PoisonPower>(),
            x.B.Powers[self.Index].Amount, self.Owner);
    }
}

/// <summary>
/// <c>TrackingPower</c>: <c>ModifyDamageMultiplicative</c> makes a powered card attack deal 1 + amount%
/// against a Weak target (<c>ModifyDamageMirrors.HandleTrackingPower</c>).
/// </summary>
[ContentId("TRACKING_POWER")]
public sealed class TrackingPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        if (!damage.Props.IsPoweredAttack() || !damage.HasCardSource || damage.Target == Limits.NoOwner)
            return 1m;
        bool pet = self.Owner == Limits.PlayerOwner && damage.Dealer == Limits.OstyOwner;
        if (damage.Dealer != self.Owner && !pet)
            return 1m;
        return Kernel.GetAmount(x.B, damage.Target, Registry.PowerId<WeakPower>()) > 0
            ? 1m + (decimal)x.B.Powers[self.Index].Amount / 100m
            : 1m;
    }
}

/// <summary>
/// <c>StranglePower</c>: <c>BeforeCardPlayed</c> snapshots the amount for the applier's card plays and
/// <c>AfterCardPlayed</c> deals it to the owner (the power's own application happens after its play's
/// snapshot, so it never triggers on itself). <c>AfterSideTurnEnd</c> removes it when the owner
/// participates (<c>EndTurnPowerSupport.TriggerBatch048</c>).
/// </summary>
[ContentId("STRANGLE_POWER")]
public sealed class StranglePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref readonly Power power = ref x.B.Powers[self.Index];
        if (power.Applier != Limits.PlayerOwner)
            return;
        x.PushCardPlayPair(CardPlayPairKind.StrangleDamage, self.Owner, power.Id, power.Amount);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if ((uint)self.Index >= (uint)x.B.PowerCount)
            return;
        ushort id = x.B.Powers[self.Index].Id;
        if (!x.TryTakeCardPlayPair(CardPlayPairKind.StrangleDamage, self.Owner, id, out int amount))
            return;
        Span<byte> target = stackalloc byte[1];
        target[0] = self.Owner;
        Kernel.Damage(ref x, target, amount,
            ValueProps.Unblockable | ValueProps.Unpowered, Limits.NoOwner,
            hasCardSource: false, default);
    }

    public override void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// <c>NoxiousFumesPower</c>: at its owner's side turn start, applies its amount of Poison to every
/// hittable enemy, resolving the queued amount changes after each
/// (<c>PersistentPowerSupport.TriggerOwnerAfterSideTurnStart</c>'s NOXIOUS_FUMES_POWER case).
/// </summary>
[ContentId("NOXIOUS_FUMES_POWER")]
public sealed class NoxiousFumesPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        ushort poison = Registry.PowerId<PoisonPower>();
        // Noxious Fumes resolves after each application; a Sleight of Flesh death compacts the live
        // roster, so visit the same snapshot the native loop sees.
        Span<byte> owners = stackalloc byte[Limits.Enemies];
        int rosterCount = Kernel.RosterOwners(x.B, owners);
        for (int position = 0; position < rosterCount; position++)
        {
            byte enemy = owners[position];
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            Kernel.ApplyPower(ref x, enemy, poison, amount, self.Owner);
            Kernel.ResolvePowerAmountChanges(ref x);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// <c>CorrosiveWavePower</c>: every card drawn by its owner applies the power amount of Poison to each
/// hittable enemy (<c>AfterCardDrawnMirrors.HandleCorrosiveWavePower</c>); it is removed at the owner's
/// side turn end (<c>EndTurnPowerSupport.TriggerRegular</c>'s CORROSIVE_WAVE_POWER case).
/// </summary>
[ContentId("CORROSIVE_WAVE_POWER")]
public sealed class CorrosiveWavePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        ushort poison = Registry.PowerId<PoisonPower>();
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            // CorrosiveWavePower passes cardSource: null, so UnsettlingLamp must not observe or
            // double these Poison applications even though they happen during a card play.
            Kernel.ApplyPowerWithoutCardSource(ref x, enemy, poison, amount, self.Owner);
            if (x.NeedsChoice)
                return;
        }
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, 0);
    }
}

/// <summary>
/// <c>FreeSkillPower</c> (Pounce): a Skill in hand or in the play pile costs 0 in the late cost pass,
/// and playing a Skill decrements the power (<c>ModifyEnergyCostInCombatMirrors.HandleFreeSkillPower</c>,
/// <c>BeforeCardPlayedMirrors.HandleFreeSkillPower</c>).
/// </summary>
[ContentId("FREE_SKILL_POWER")]
public sealed class FreeSkillPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules,
        Listener self, in Card card, Pile pile, decimal amount)
    {
        if (self.Owner != Limits.PlayerOwner || pile is not (Pile.Hand or Pile.Play))
            return amount;
        return Registry.Card(card.Id).ResolveCardType(battle, rules, card) == CardType.Skill ? 0m : amount;
    }

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Skill)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, x.B.Powers[self.Index].Id, amount - 1);
    }
}

/// <summary>
/// <c>PiercingWailPower</c> (a negative <c>TemporaryStrengthPower</c>): applied through
/// <c>ApplyTemporaryStrengthLoss</c> and retired at its owner's side turn end while restoring Strength
/// (<c>EndTurnPowerSupport.TriggerRegular</c>'s TemporaryStrengthPower case).
/// </summary>
[ContentId("PIERCING_WAIL_POWER")]
public sealed class PiercingWailPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override string? InternallyAppliedPowerId => "STRENGTH_POWER";

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsTemporaryPower => true;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryStrength(ref x, self);
    }
}
