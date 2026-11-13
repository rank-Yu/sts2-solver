using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>ShrinkPower</c>: the Shrunk creature's powered attacks deal less damage. Applied with
/// amount -1 by ShrinkerBeetle (<c>MonsterMoveEffects</c> "ShrinkerBeetle", "SHRINKER_MOVE",
/// <c>combat.Apply&lt;ShrinkPower&gt;</c>), and decremented at the end of the owner's side
/// (<c>EndTurnPowerSupport.TriggerRegular</c> case <c>ShrinkPower</c>). The existing engine skips
/// powers whose amount is not positive, so the -1 form never ticks down. The engine registers no
/// mirror for this power's damage multiplier, so it calls the native
/// <c>ModifyDamageMultiplicative</c> through <c>ModifyDamageMirrors.InvokeOriginalMultiplicative</c>:
/// <c>(100 - DamageDecrease) / 100</c> against the power owner's powered attacks. Removed when its
/// applier dies (<c>CorePowerSupport.ApplyEnemyDeathPowers</c>).
/// </summary>
[ContentId("SHRINK_POWER")]
public sealed class ShrinkPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => true;

    public override IReadOnlyList<string> VarNames => ["DamageDecrease"];

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (damage.Dealer != self.Owner || !damage.Props.IsPoweredAttack())
            return 1m;
        return (100m - x.R.PowerVar(Id, 0)) / 100m;
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }

    // CorePowerSupport.ApplyEnemyDeathPowers: SetAmount to 0 when its applier died.
    public override void ApplierDied(ref Exec x, Listener self)
        => Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
}

/// <summary>
/// Native <c>MinionPower</c> (KinFollower, one stack applied in <c>AfterAddedToRoom</c>): its owner is
/// a secondary enemy, so killing the primary enemy kills the remaining minions and a minion's death
/// does not count as a fatal kill. In the existing engine that is
/// <c>PowerModel.OwnerIsSecondaryEnemy</c> plus <c>ShouldOwnerDeathTriggerFatal</c>; the kernel's
/// <c>IsPrimaryEnemy</c> and the Feed fatal-kill check read it through
/// <see cref="PowerDef.OwnerIsSecondaryEnemy"/>. The power is not removed when its owner dies
/// (<c>ShouldPowerBeRemovedAfterOwnerDeath</c>), which is inert because a removed enemy is no longer
/// in the listener roster; no private slot.
/// </summary>
[ContentId("MINION_POWER")]
public sealed class MinionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool OwnerIsSecondaryEnemy => true;

    // Native MinionPower.ShouldPowerBeRemovedAfterOwnerDeath: false.
    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;

    // Native MinionPower.ShouldOwnerDeathTriggerFatal: false.
    public override bool ShouldOwnerDeathTriggerFatal(in Battle battle, Listener self) => false;
}

/// <summary>
/// Native <c>IllusionPower</c> (EyeWithTeeth and Parafright): a single buff that keeps its owner in
/// the roster after death and, on death, forces the synthetic REVIVE_MOVE. The solver's
/// <c>BeginIllusionRevive</c> (<c>SimulatedCombatState.DeathLifecycle.cs:100-119</c>) selects the
/// last logged non-revive move as the follow-up; <c>ReviveMove</c> heals the owner to its max HP and
/// clears the reviving death phase (<c>ResolveReviveMove</c>, same file). While the power is on the
/// dead owner, the solver keeps its buffs and temporary debuffs and removes other debuffs
/// (<c>RemovePowersAfterDeath</c>), exposed to the kernel as
/// <see cref="KeepsOwnerBuffsAfterDeath"/>.
/// </summary>
[ContentId("ILLUSION_POWER")]
public sealed class IllusionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    // ICombatPredictionCreatureSemantics.ShouldRemoveAfterDeath keeps an owner with IllusionPower.
    public override bool PreventsRemovalAfterDeath => true;

    public override bool KeepsOwnerBuffsAfterDeath => true;

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (wasRemovalPrevented || creature != self.Owner)
            return;
        // The existing engine's IllusionPower death dispatch is not mirrored and records
        // MethodNotMirrored risk even though BeginIllusionRevive applies the same state.
        x.HistoryRisk = true;
        Kernel.BeginIllusionRevive(ref x, self.Owner);
    }
}

/// <summary>
/// Native <c>SkittishPower</c> (PhantasmalGardener, <c>SkittishAmount</c> stacks applied in
/// <c>AfterAddedToRoom</c>): the first damage result against the owner in a card attack that is
/// still unblocked makes it gain its amount as Unpowered block, at most once per player side
/// (<c>AfterAttackMirrors.HandleSkittishPower</c> through <c>SkittishPredictionState</c>). The flag
/// resets at the end of the player side (<c>SkittishPower.AfterSideTurnEnd</c>).
/// Aux0 = HasGainedBlockThisTurn. The first result for the owner is kept on <see cref="Exec"/> for
/// the duration of the attack context; the bridge projects Aux0 from the prediction state, falling
/// back to the model's live flag.
/// </summary>
[ContentId("SKITTISH_POWER")]
public sealed class SkittishPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // SkittishPower.AfterAttack: at most once per player side, when the attack is a card attack
    // with Move props and the first damage result against the owner is unblocked. The attack's
    // results are recorded per hit on Exec; the hook itself runs once at the end of the attack.
    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 != 0 || power.Amount <= 0)
            return;
        if ((x.AttackProps & ValueProps.Move) == 0 || !x.AttackHasCardSource)
            return;
        if (!Kernel.IsEnemyOwner(self.Owner))
            return;
        ulong bit = 1UL << (self.Owner - Limits.FirstEnemyOwner);
        if ((x.AttackFirstResultSeen & bit) == 0 || (x.AttackFirstResultUnblocked & bit) == 0)
            return;
        power.Aux0 = 1;
        Kernel.GainBlock(ref x, self.Owner, power.Amount, ValueProps.Unpowered, fromCard: false);
    }

    // SkittishPower.AfterSideTurnEnd resets HasGainedBlockThisTurn when the ending side is not the
    // owner's side; the owner is an enemy, so that is the player side end.
    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (side == Side.Player)
            x.B.Powers[self.Index].Aux0 = 0;
    }
}

/// <summary>
/// Native <c>ConstrictPower</c>: applied to the player by SlitheringStrangler
/// (<c>MonsterMoveEffects</c> "SlitheringStrangler", "CONSTRICT", <c>combat.Apply&lt;ConstrictPower&gt;</c>).
/// At the end of the owner's side it damages the owner for its amount as Unpowered damage with the
/// owner as dealer (<c>CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects</c>).
/// </summary>
[ContentId("CONSTRICT_POWER")]
public sealed class ConstrictPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void PlayerSideTurnEnd(ref Exec x, Listener self)
    {
        if (x.B.Creature(self.Owner).IsDead)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        Span<byte> targets = stackalloc byte[1];
        targets[0] = self.Owner;
        Kernel.Damage(ref x, targets, amount, ValueProps.Unpowered, self.Owner, hasCardSource: false, default);
    }

    public override bool TryGetTurnEndSelfDamage(in Battle battle, Rules rules, int powerIndex,
        out TurnEndSelfDamage damage)
    {
        damage = default;
        ref readonly Power power = ref battle.Powers[powerIndex];
        if (power.Amount <= 0 || battle.Creature(power.Owner).IsDead)
            return false;
        damage = TurnEndSelfDamage.FromPower(TurnEndSelfDamageTiming.PlayerSideTurnEnd,
            power.Amount, ValueProps.Unpowered, power.Owner);
        return true;
    }

    // CorePowerSupport.ApplyEnemyDeathPowers: SetAmount to 0 when its applier died.
    public override void ApplierDied(ref Exec x, Listener self)
        => Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
}

/// <summary>
/// Native <c>TerritorialPower</c> as mirrored by the existing engine: at the end of its owner's side
/// it applies Strength equal to its amount to the owner
/// (<c>EndTurnPowerSupport.TriggerRegular</c> case <c>TerritorialPower</c>). Byrdonis carries one.
/// </summary>
[ContentId("TERRITORIAL_POWER")]
public sealed class TerritorialPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(),
            x.B.Powers[self.Index].Amount, self.Owner);
    }
}

/// <summary>
/// Native <c>ArtifactPower</c>: carried by CubexConstruct (one stack at combat start). The kernel's
/// <c>ConsumeArtifact</c> already negates one debuff application and decrements this power; the
/// definition only needs to exist so the root can be imported and the power can be stacked.
/// </summary>
[ContentId("ARTIFACT_POWER")]
public sealed class ArtifactPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// Native <c>SlipperyPower</c> (Inklet, one stack applied in <c>AfterAddedToRoom</c>): the owner's
/// HP loss of at least 1 is capped at 1 (<c>ModifyHpLostAfterOsty</c>) and the stack decrements
/// after unblocked damage (<c>AfterDamageReceived</c>). The existing engine keeps the effective
/// amount in a <c>PowerAmountPredictionState</c> instead of the cloned model
/// (<c>ModifyHpLostMirrors</c> / <c>AfterDamageReceivedMirrors.HandleSlipperyPower</c>); the kernel
/// keeps it in the power amount itself, and the bridge's synchronization applies the same value at
/// the next boundary. No private slot.
/// </summary>
[ContentId("SLIPPERY_POWER")]
public sealed class SlipperyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHpLostAfterOsty(ref Exec x, Listener self, in HpLossContext loss,
        decimal amount)
    {
        if (loss.Target != self.Owner || amount < 1m)
            return amount;
        return 1m;
    }

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || result.UnblockedDamage < 1)
            return;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, x.B.Powers[self.Index].Amount - 1);
    }
}

/// <summary>
/// Native <c>SlowPower</c> (BygoneEffigy, one stack applied in <c>AfterAddedToRoom</c>):
/// <c>AfterCardPlayed</c> advances the private <c>SlowAmount</c> counter and a powered attack
/// against the owner is multiplied by <c>1 + 0.1 * counter</c>; <c>BaseSideTurnStart</c> resets the
/// counter when the owner's side starts. The existing engine stores the counter in a
/// <c>CounterPredictionState</c> (<c>AfterCardPlayedMirrors.HandleSlowPower</c>,
/// <c>ModifyDamageMirrors.HandleSlowPower</c>, <c>SimulatedCombatState.TriggerBaseSideTurnStart</c>).
/// Aux0 = SlowAmount counter; the bridge projects the store entry, falling back to the model's
/// <c>DynamicVars["SlowAmount"].BaseValue</c>.
/// </summary>
[ContentId("SLOW_POWER")]
public sealed class SlowPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
        => x.B.Powers[self.Index].Aux0++;

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        if (damage.Target != self.Owner || !damage.Props.IsPoweredAttack())
            return 1m;
        return 1m + 0.1m * x.B.Powers[self.Index].Aux0;
    }

    public override void BaseSideTurnStart(ref Exec x, Listener self, bool decrementPlating)
        => x.B.Powers[self.Index].Aux0 = 0;
}

/// <summary>
/// Native <c>AsleepPower</c> (LagavulinMatriarch, three stacks applied in <c>AfterAddedToRoom</c>):
/// unblocked damage wakes the owner — the existing engine removes Plating, forces
/// <c>STUNNED</c> with SLASH_MOVE as the follow-up and removes this power
/// (<c>AfterDamageReceivedMirrors.HandleAsleepPower</c>). At its side turn end it loses one stack
/// (<c>EndTurnPowerSupport.TriggerRegular</c>); reaching zero removes Plating. The separate
/// <c>TriggerVeryEarly</c> Plating removal is handled in <see cref="PlatingPower.EnemySideTurnEnd"/>,
/// where it precedes the Plating block gain exactly as in <c>CorePowerSupport</c>.
/// </summary>
[ContentId("ASLEEP_POWER")]
public sealed class AsleepPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || result.UnblockedDamage == 0)
            return;
        Kernel.SetPowerAmount(ref x.B, self.Owner, Registry.PowerId<PlatingPower>(), 0);
        Kernel.ForceStunnedMove(ref x, self.Owner, Kernel.MonsterRules(ref x, self.Owner).NodeIndex("SLASH_MOVE"));
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        int remaining = x.B.Powers[self.Index].Amount - 1;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, remaining);
        // PowerCmd.Decrement is a no-op while IsEnding; read the live amount instead of the
        // pre-modification remaining value so the Plating cleanup follows the native branch.
        if (Kernel.GetAmount(x.B, self.Owner, Id) <= 0)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Registry.PowerId<PlatingPower>(), 0);
    }
}

/// <summary>
/// Native <c>RavenousPower</c> (CorpseSlug, applied with its native starting amount): when a living
/// teammate dies, the owner is stunned with the default follow-up and gains its amount as Strength
/// (<c>DeathPowerSupport.Trigger</c>). The private <c>_isRavenous</c> flag only selects animations.
/// </summary>
[ContentId("RAVENOUS_POWER")]
public sealed class RavenousPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (wasRemovalPrevented
            || creature == self.Owner
            || !Kernel.IsEnemyOwner(creature)
            || x.B.Creature(self.Owner).IsDead)
        {
            return;
        }
        int amount = x.B.Powers[self.Index].Amount;
        Kernel.ForceStunnedMove(ref x, self.Owner, -1);
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(), amount, self.Owner);
    }
}

/// <summary>
/// Native <c>ShriekPower</c> (TerrorEel, applied with its native starting amount 70/75): unblocked
/// damage that leaves the owner at or below that amount forces <c>STUNNED</c> with TERROR_MOVE as the
/// follow-up and removes the power (<c>AfterDamageReceivedMirrors.HandleShriekPower</c>).
/// </summary>
[ContentId("SHRIEK_POWER")]
public sealed class ShriekPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => true;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner
            || result.UnblockedDamage <= 0
            || x.B.Creature(self.Owner).CurrentHp > x.B.Powers[self.Index].Amount)
        {
            return;
        }
        Kernel.ForceStunnedMove(ref x, self.Owner, Kernel.MonsterRules(ref x, self.Owner).NodeIndex("TERROR_MOVE"));
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

/// <summary>
/// Native <c>VigorPower</c> (TerrorEel THRASH applies 6): a powered attack by the owner deals its
/// amount as extra damage and loses the amount captured when that attack started
/// (<c>VigorPowerMirrors.BeforeAttack</c>/<c>ModifyDamageAdditive</c>/<c>AfterAttack</c>).
/// Aux0 = amount captured for the current attack; zero at every action boundary. The existing
/// engine keeps the same pair in a private <c>VigorPowerMirrors.State</c> store entry that is
/// asserted clean before a fork and claimed by the bridge projection.
/// </summary>
[ContentId("VIGOR_POWER")]
public sealed class VigorPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    /// <summary>
    /// <c>VigorPowerMirrors.BeforeAttack</c>: the owner's powered attack from a card or a monster move
    /// captures the amount once (Aux0); <see cref="AfterAttack"/> spends exactly that amount.
    /// </summary>
    public override void BeforeAttack(ref Exec x, Listener self, byte attacker, ValueProps props)
    {
        if (attacker != self.Owner || !props.IsPoweredAttack())
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Amount > 0 && power.Aux0 == 0)
            power.Aux0 = power.Amount;
    }

    /// <summary>Side-effect free, like every modifier: forecasts and the scorer call it on read-only states.</summary>
    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (damage.Dealer != self.Owner || !damage.Props.IsPoweredAttack())
            return 0m;
        int vigor = x.B.Powers[self.Index].Amount;
        return vigor > 0 ? vigor : 0m;
    }

    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        if (attacker != self.Owner)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        int captured = power.Aux0;
        if (captured == 0)
            return;
        power.Aux0 = 0;
        Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, Math.Max(0, power.Amount - captured));
    }
}
