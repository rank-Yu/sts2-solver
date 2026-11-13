using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>FeelNoPainPower</c>: <c>AfterCardExhausted</c> gains its amount as Unpowered block
/// when the exhausted card belongs to the power owner
/// (M/Hooks/Card/AfterCardExhaustedMirrors.HandleFeelNoPainPower).
/// </summary>
[ContentId("FEEL_NO_PAIN_POWER")]
public sealed class FeelNoPainPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardExhausted(ref Exec x, Listener self, in Card card, bool causedByEthereal)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// Native <c>JuggernautPower</c>: after its owner gains positive block, deal its amount as Unpowered
/// damage to a random hittable enemy (M/Hooks/Block/AfterBlockGainedMirrors.HandleJuggernautPower).
/// </summary>
[ContentId("JUGGERNAUT_POWER")]
public sealed class JuggernautPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterBlockGained(ref Exec x, Listener self, byte owner, decimal amount,
        ValueProps props, bool fromCard)
    {
        if (owner != self.Owner || amount <= 0m)
            return;
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte candidate = Kernel.EnemyOwner(x.B.Roster[position]);
            if (Kernel.IsHittable(x.B, candidate))
                targets[count++] = candidate;
        }
        // Rng.NextItem returns before drawing when the list is empty.
        if (count == 0)
            return;
        int pick = x.B.RngStream(RngStream.CombatTargets).NextInt(0, count);
        Kernel.Damage(ref x, targets[pick..(pick + 1)], x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, self.Owner, hasCardSource: false, default);
    }
}

/// <summary>
/// Native <c>CrueltyPower</c>: its amount is added to the Vulnerable multiplier as a percentage by
/// <c>ModifyDamageMirrors.HandleVulnerablePower</c>, which the Sts2Solver Vulnerable definition mirrors.
/// The power itself has no hook of its own.
/// </summary>
[ContentId("CRUELTY_POWER")]
public sealed class CrueltyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// Native <c>FlameBarrierPower</c>: <c>AfterDamageReceived</c> deals its amount as Unpowered damage
/// back to the powered-attack dealer, with the barrier owner as the damage dealer
/// (M/Hooks/Damage/AfterDamageReceivedMirrors.HandleFlameBarrierPower). The power itself is removed
/// by <c>CorePowerSupport.TriggerTransientSideTurnEndPowers</c> when the opposing side turn ends.
/// </summary>
[ContentId("FLAME_BARRIER_POWER")]
public sealed class FlameBarrierPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || dealer == Limits.NoOwner || !result.Props.IsPoweredAttack())
            return;
        Span<byte> targets = stackalloc byte[1];
        targets[0] = dealer;
        Kernel.Damage(ref x, targets, x.B.Powers[self.Index].Amount, ValueProps.Unpowered,
            self.Owner, hasCardSource: false, default);
    }
}

/// <summary>
/// Native <c>PyrePower</c>: <c>ModifyMaxEnergy</c> adds its amount to the owner's maximum energy
/// (src/Prediction/PersistentPowerSupport.cs).
/// </summary>
[ContentId("PYRE_POWER")]
public sealed class PyrePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
    {
        if (self.Owner != Limits.PlayerOwner)
            return amount;
        return amount + x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// Native <c>DemonFormPower</c>: <c>AfterSideTurnStart</c> applies Strength equal to its amount to
/// its owner (src/Prediction/PersistentPowerSupport.cs, TriggerOwnerAfterSideTurnStart).
/// </summary>
[ContentId("DEMON_FORM_POWER")]
public sealed class DemonFormPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
        => Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(),
            x.B.Powers[self.Index].Amount, self.Owner);
}

/// <summary>
/// Native <c>BarricadePower</c>: <c>ShouldClearBlock</c> keeps its owner's block at side start
/// (src/Search/SimulatedCombatState.cs, game 0.111.0 Hook.ShouldClearBlock).
/// </summary>
[ContentId("BARRICADE_POWER")]
public sealed class BarricadePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool ShouldClearBlock(ref Exec x, Listener self, byte owner)
        => owner != self.Owner;
}

/// <summary>
/// Native <c>ManglePower</c> (a <c>TemporaryStrengthPower</c> with negative sign): at its owner's
/// side turn end it is removed and its amount of Strength is restored
/// (src/Search/SimulatedCombatState.cs, RetireTemporaryStat, and EndTurnPowerSupport.TriggerRegular).
/// </summary>
[ContentId("MANGLE_POWER")]
public sealed class ManglePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override string? InternallyAppliedPowerId => "STRENGTH_POWER";
    public override bool IsTemporaryPower => true;
    // EndTurnPowerSupport.TriggerRegular: case TemporaryStrengthPower when the owner participates.
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryStrength(ref x, self);
    }
}

/// <summary>
/// Native <c>SetupStrikePower</c> (a positive <c>TemporaryStrengthPower</c>): at its owner's side turn
/// end it is removed and its amount of Strength is taken back
/// (src/Search/SimulatedCombatState.cs, RetireTemporaryStat, and EndTurnPowerSupport.TriggerRegular).
/// </summary>
[ContentId("SETUP_STRIKE_POWER")]
public sealed class SetupStrikePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override string? InternallyAppliedPowerId => "STRENGTH_POWER";
    public override bool IsTemporaryPower => true;
    // EndTurnPowerSupport.TriggerRegular: case TemporaryStrengthPower when the owner participates.
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryStrength(ref x, self);
    }
}

/// <summary>
/// Native <c>RagePower</c>: <c>AfterCardPlayed</c> gains its amount as Unpowered block after an
/// Attack card (<c>AfterCardPlayedMirrors.HandleRagePower</c>). Its removal at the player side turn
/// end is already part of <c>CorePowerSupport.TriggerTransientSideTurnEndPowers</c>.
/// </summary>
[ContentId("RAGE_POWER")]
public sealed class RagePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        Kernel.GainBlock(ref x, self.Owner, x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// Native <c>JugglingPower</c>: <c>BeforeCardPlayed</c> counts Attack cards started this turn; the
/// third one adds its amount of clones of the played card to the hand; the player side turn end
/// resets the count (<c>BeforeCardPlayedMirrors.HandleJugglingPower</c>,
/// <c>EndTurnPowerSupport.TriggerRegular</c>, and
/// <c>SimulatedCombatState.InitializeJugglingAfterApplied</c> for the value at application).
/// Aux0 = attacks played this turn.
/// </summary>
[ContentId("JUGGLING_POWER")]
public sealed class JugglingPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        power.Aux0++;
        if (power.Aux0 == 3)
            power.Aux1 = play.Position - Piles.Start(x.B, Pile.Play) + 1;
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref Power power = ref x.B.Powers[self.Index];
        int position = play.Position - Piles.Start(x.B, Pile.Play) + 1;
        if (power.Aux1 == 0 || power.Aux1 != position)
            return;
        power.Aux1 = 0;
        int copies = power.Amount;
        for (int index = 0; index < copies; index++)
        {
            if (!Kernel.AddGeneratedCard(ref x, Kernel.CloneCard(play.Card), Pile.Hand))
                return;
        }
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
        {
            x.B.Powers[self.Index].Aux0 = 0;
        }
    }
}

/// <summary>
/// Native <c>DarkEmbracePower</c>: a non-Ethereal exhaust draws its amount, an Ethereal exhaust is
/// deferred to the player side turn end, which then draws amount per deferred exhaust
/// (<c>AfterCardExhaustedMirrors.HandleDarkEmbracePower</c>,
/// <c>EndTurnPowerSupport.TriggerRegular</c>). Aux0 = deferred Ethereal exhausts.
/// </summary>
[ContentId("DARK_EMBRACE_POWER")]
public sealed class DarkEmbracePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardExhausted(ref Exec x, Listener self, in Card card, bool causedByEthereal)
    {
        if (causedByEthereal)
        {
            x.B.Powers[self.Index].Aux0++;
            return;
        }
        Kernel.Draw(ref x, x.B.Powers[self.Index].Amount, fromHandDraw: false);
    }

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (!participates)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        int draws = power.Amount * power.Aux0;
        if (draws > 0)
            Kernel.Draw(ref x, draws, fromHandDraw: false);
        power.Aux0 = 0;
    }
}

/// <summary>
/// Native <c>CrimsonMantlePower</c>: <c>AfterPlayerTurnStart</c> takes its accumulated
/// <c>SelfDamage</c> as Unblockable Unpowered damage, then gains its amount as Unpowered block
/// (<c>TurnStartPowerSupport.ContinueAfterPlayerTurnStart</c>). The SelfDamage dynamic variable is
/// private state; the bridge projects it into Aux0 and the card increments it on application.
/// </summary>
[ContentId("CRIMSON_MANTLE_POWER")]
public sealed class CrimsonMantlePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        Power power = x.B.Powers[self.Index];
        if (power.Aux0 > 0)
        {
            Span<byte> target = stackalloc byte[1];
            target[0] = self.Owner;
            Kernel.Damage(ref x, target, power.Aux0,
                ValueProps.Unblockable | ValueProps.Unpowered, self.Owner,
                hasCardSource: false, default);
        }
        Kernel.GainBlock(ref x, self.Owner, power.Amount, ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// Native <c>InfernoPower</c>: <c>AfterPlayerTurnStart</c> takes its accumulated <c>SelfDamage</c>
/// as Unblockable Unpowered damage; on its own side, unblocked damage taken deals its amount as
/// Unpowered damage to every hittable enemy (<c>TurnStartPowerSupport.ContinueAfterPlayerTurnStart</c>,
/// <c>AfterDamageReceivedMirrors.HandleInfernoPower</c>). Aux0 = the private SelfDamage variable.
/// </summary>
[ContentId("INFERNO_POWER")]
public sealed class InfernoPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 <= 0)
            return;
        Span<byte> target = stackalloc byte[1];
        target[0] = self.Owner;
        Kernel.Damage(ref x, target, power.Aux0,
            ValueProps.Unblockable | ValueProps.Unpowered, self.Owner,
            hasCardSource: false, default);
    }

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || result.UnblockedDamage <= 0 || x.B.Side != Side.Player)
            return;
        CardEffects.DamageAllEnemies(ref x, x.B.Powers[self.Index].Amount, ValueProps.Unpowered,
            self.Owner);
    }
}

/// <summary>
/// Native <c>AggressionPower</c>: at its owner's side turn start it unstable-shuffles the Attack
/// cards of the discard pile with the CombatCardSelection stream and adds the first
/// <c>Amount</c> to the hand, upgrading each upgradable card
/// (<c>TurnStartPowerSupport.ApplyBeforeSideTurnStartPower</c>, the dedicated Aggression pass run
/// before every other power). The card does not keep private state: the selection is re-derived
/// from the discard pile, exactly as the existing engine does.
/// </summary>
[ContentId("AGGRESSION_POWER")]
public sealed class AggressionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
    {
        if (!participates || self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0)
            return;
        int discardCount = x.B.DiscardCount;
        int discardStart = Piles.Start(x.B, Pile.Discard);
        Span<int> order = stackalloc int[discardCount];
        int candidates = 0;
        for (int index = 0; index < discardCount; index++)
        {
            Card card = x.B.Cards[discardStart + index];
            if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Attack)
                order[candidates++] = index;
        }
        // ListExtensions.UnstableShuffle: Fisher-Yates from the end over the discard-order list.
        RngState rng = x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        for (int n = candidates; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (order[k], order[n]) = (order[n], order[k]);
        }
        Span<bool> removed = stackalloc bool[discardCount];
        int take = Math.Min(amount, candidates);
        for (int index = 0; index < take; index++)
        {
            int original = order[index];
            // AddToPile removes the card before inserting, so earlier removals shift this occurrence.
            int current = original;
            for (int earlier = 0; earlier < original; earlier++)
            {
                if (removed[earlier])
                    current--;
            }
            Pile destination = x.B.HandCount < x.B.MaxHandSize ? Pile.Hand : Pile.Discard;
            if (!Kernel.MoveCard(ref x.B, Pile.Discard, current, destination))
                return;
            // AddToPile inserts at the bottom (Hand, or Discard when the hand is full).
            int inserted = destination == Pile.Hand ? x.B.HandCount - 1 : x.B.DiscardCount - 1;
            ref Card added = ref Piles.At(ref x.B, destination, inserted);
            // CardCmd.Upgrade on an upgradable card; UpgradeCard is a no-op at the maximum level.
            Kernel.UpgradeCard(x.R, ref added);
            removed[original] = true;
        }
    }
}

/// <summary>
/// Native <c>UnmovablePower</c>: <c>ModifyBlockMultiplicative</c> doubles block from a card or
/// monster move while fewer than <c>Amount</c> block-gain events happened earlier this turn
/// (<c>ModifyBlockMultiplicativeMirrors.HandleUnmovablePower</c>). The existing engine subtracts the
/// current card play's powered block events from
/// <c>SimulatedCombatState.GetBlockCardsPlayedThisTurn</c>; Aux0 stores that turn counter when the
/// card play starts. Card-sourced block is the only reader, and it runs before the play's tail, so
/// Aux0 is reset in <c>AfterCardPlayed</c> and is zero at every action boundary
/// (<c>CombatPredictionSimulator.CardTailContinuation</c> releases the same per-play count there).
/// </summary>
[ContentId("UNMOVABLE_POWER")]
public sealed class UnmovablePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    // Aux0 = BlockCardsPlayedThisTurn at the start of the current card play.
    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
        => x.B.Powers[self.Index].Aux0 = x.B.Counter(Counter.BlockCardsPlayedThisTurn);

    // The play is complete: the engine releases its per-CardPlay powered-block count at the same tail.
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
        => x.B.Powers[self.Index].Aux0 = 0;

    public override decimal ModifyBlockMultiplicative(ref Exec x, Listener self,
        in BlockContext block, decimal amount)
    {
        // The native guard is target.IsMonster; Osty does not exist in an admitted root, so only the
        // power owner's block reaches the doubling branch.
        if (block.Target != self.Owner || (block.Props & ValueProps.Move) == 0)
            return 1m;
        // A card source from another owner cannot occur in single-player combat, and non-card block
        // (relic, power) has no card play, so the engine's CardSource owner guard passes.
        ref Power power = ref x.B.Powers[self.Index];
        int earlier = block.HasCardSource
            ? power.Aux0
            : x.B.Counter(Counter.BlockCardsPlayedThisTurn);
        return earlier < power.Amount ? 2m : 1m;
    }
}

/// <summary>
/// Native <c>RupturePower</c>: while its owner plays a card, unblocked damage that card deals to
/// the owner is accumulated and applied as Strength at the play's AfterCardPlayed; any other
/// unblocked damage taken on the owner's own side applies Strength immediately
/// (<c>BeforeCardPlayedMirrors.HandleRupturePower</c>,
/// <c>AfterDamageReceivedMirrors.HandleRupturePower</c>,
/// <c>AfterCardPlayedMirrors.HandleRupturePower</c>).
/// Aux0 = Strength accumulated for the current card play, Aux1 = the current play's pending entry.
/// The card source is read directly from <see cref="DamageResult.HasCardSource"/> (the existing
/// engine passes <c>AfterDamageReceivedMirrorContext.Source</c>), so no source marker is staged on
/// BeforeDamageReceived. The existing engine keeps the pending information in
/// <c>RupturePredictionState.StrengthByCard</c>, a map keyed by CardModel; only one card play can be
/// in flight, so the map is empty or has exactly one keyed entry, and the pending Strength is its
/// only value. The bridge claims an empty entry and rejects a non-empty one
/// (<see cref="StateProjections"/>), so every non-terminal imported state has Aux0 = Aux1 = 0.
/// </summary>
[ContentId("RUPTURE_POWER")]
public sealed class RupturePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref Power power = ref x.B.Powers[self.Index];
        power.Aux0 = 0;
        power.Aux1 = 1;
    }

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || result.UnblockedDamage <= 0 || x.B.Side != Side.Player)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux1 != 0 && result.HasCardSource)
        {
            power.Aux0 += power.Amount;
            return;
        }
        // The existing engine applies with cardSource: null; Sts2Solver has no card source on powers.
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(), power.Amount, self.Owner);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref Power power = ref x.B.Powers[self.Index];
        int gained = power.Aux0;
        power.Aux0 = 0;
        power.Aux1 = 0;
        if (gained != 0)
            Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(), gained, self.Owner);
    }
}

/// <summary>
/// Native <c>HellraiserPower</c>: when its owner draws a Strike, auto-play that card
/// (M/Hooks/Card/AfterCardDrawnMirrors.HandleHellraiserPower). When every hittable enemy shows
/// infinite HP (Waterfall Giant's steam-eruption rewrite) the auto-play stops after nine draws and
/// the counter keeps incrementing; the finite branch resets it. Aux0 is
/// <c>infiniteAutoPlaysThisTurn</c> (HellraiserPredictionState; without an entry the model's internal
/// data). <c>EndTurnBatch048</c> resets it when the owner participates (ResetInfiniteAutoPlayData).
/// </summary>
[ContentId("HELLRAISER_POWER")]
public sealed class HellraiserPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override void AfterCardDrawnEarly(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (self.Owner != Limits.PlayerOwner || drawnPosition >= x.B.HandCount)
            return;
        CardStats stats = x.R.Card(x.B.Cards[drawnPosition].Id, x.B.Cards[drawnPosition].Upgrade);
        if ((stats.Tags & CardTag.Strike) == 0)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        bool autoPlay = true;
        if (AllHittableEnemiesInfinite(ref x))
        {
            if (power.Aux0 >= InfiniteAutoPlayCap)
                autoPlay = false;
            power.Aux0++;
        }
        else
        {
            power.Aux0 = 0;
        }
        if (autoPlay)
            Kernel.AutoPlayDrawnCard(ref x, drawnPosition);
    }

    public override void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
        // CorePowerSupport/EndTurnPowerSupport resets the turn counter when the owner participates.
        if (participates)
        {
            x.B.Powers[self.Index].Aux0 = 0;
        }
    }

    private const int InfiniteAutoPlayCap = 9;

    /// <summary>
    /// <c>HittableEnemies.All(c =&gt; c.HpDisplay.IsInfinite())</c>: hittable enemies are the roster
    /// entries not in a death phase. The only infinite display is a Waterfall Giant rewritten by
    /// <c>TriggerSteamEruptionDeath</c>; Sts2Solver records that as the steam phase or the value-clamp HP
    /// (the Explode phase clears SteamPhase but keeps the rewritten HP until the forced kill).
    /// </summary>
    private static bool AllHittableEnemiesInfinite(ref Exec x)
    {
        ushort hardenedShell = Registry.TryPower("HARDENED_SHELL_POWER", out ushort shell)
            ? shell
            : ushort.MaxValue;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            // HittableEnemies is Enemies.Where(IsHittable): a lethal hit excludes the creature
            // immediately (its HP is already 0), before the deferred death pass changes DeathPhase.
            if (!Kernel.IsHittable(x.B, owner))
                continue;
            ref Enemy enemy = ref x.B.Enemy(owner);
            if (enemy.DeathPhase != DeathPhase.None)
                continue;
            // Waterfall Giant's steam phases and HardenedShellPower's per-turn latch both set
            // HpDisplay.Infinite*; an empty hittable set makes All() true.
            if (x.R.HasInfiniteHpDisplayMonster
                && Registry.Monster(enemy.MonsterId).NativeId == "WATERFALL_GIANT"
                && (enemy.SteamPhase != SteamPhase.None
                    || x.B.Creature(owner).CurrentHp >= Limits.ValueClamp))
            {
                continue;
            }
            if (hardenedShell != ushort.MaxValue)
            {
                int row = Kernel.FindPower(x.B, owner, hardenedShell);
                if (row >= 0 && x.B.Powers[row].Amount > 0
                    && x.B.Powers[row].Aux0 >= x.B.Powers[row].Amount)
                {
                    continue;
                }
            }
            return false;
        }
        return true;
    }
}

/// <summary>
/// Native <c>StampedePower</c>: at the auto-post-play phase, auto-play up to its amount of Attacks from
/// hand, each picked with the Shuffle stream
/// (M/Hooks/TurnEnd/AfterAutoPostPlayPhaseEnteredMirrors.HandleStampedePower).
/// </summary>
[ContentId("STAMPEDE_POWER")]
public sealed class StampedePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterAutoPostPlayPhaseEntered(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        int amount = x.B.Powers[self.Index].Amount;
        for (int index = 0; index < amount; index++)
        {
            int count = 0;
            int pick = -1;
            for (int hand = 0; hand < x.B.HandCount; hand++)
            {
                Card card = x.B.Cards[hand];
                if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Attack
                    || card.HasKeyword(Keywords.Unplayable))
                    continue;
                count++;
            }
            if (count == 0)
                return;
            int selected = x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, count);
            for (int hand = 0; hand < x.B.HandCount; hand++)
            {
                Card card = x.B.Cards[hand];
                if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Attack
                    || card.HasKeyword(Keywords.Unplayable))
                    continue;
                if (selected-- == 0)
                {
                    pick = hand;
                    break;
                }
            }
            if (pick < 0)
                throw new UnsupportedException("Stampede 无法在手牌中定位选中的攻击牌。");
            Kernel.AutoPlay(ref x, Pile.Hand, pick);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>CalamityPower</c>: an Attack play snapshots the power amount, then
/// <c>AfterCardPlayed</c> generates that many character Attacks with replacement into hand
/// (BeforeCardPlayedMirrors.HandleCalamityPower / AfterCardPlayedMirrors.HandleCalamityPower).
/// Aux0 is the pending-play marker; it is zero at every action boundary.
/// </summary>
[ContentId("CALAMITY_POWER")]
public sealed class CalamityPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAttacks];

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        // Native keys the pending amount by the played CardModel, so a nested auto-play gets its own
        // entry and the outer Attack still fires afterwards. The play-index stack has the same
        // nesting order; a single Aux flag would let the nested After consume the outer play.
        x.PushCardPlayPair(CardPlayPairKind.CalamityAttacks, self.Owner,
            x.B.Powers[self.Index].Id, x.B.Powers[self.Index].Amount);
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!x.TryTakeCardPlayPair(CardPlayPairKind.CalamityAttacks, self.Owner,
                x.B.Powers[self.Index].Id, out int amount))
        {
            return;
        }
        if (amount > Limits.Cards)
            throw new CapacityException("Cards", Limits.Cards);
        Span<Card> generated = stackalloc Card[Math.Max(0, amount)];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAttacks,
            amount, distinct: false, RngStream.CombatCardGeneration, generated);
        for (int index = 0; index < count; index++)
            Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand);
        if (x.NeedsChoice)
            return;
    }
}

/// <summary>
/// Native <c>CorruptionPower</c>: Skills cost 0 through the late energy-cost pass, and a played Skill
/// changes its result location to the exhaust pile
/// (<c>ModifyEnergyCostInCombatMirrors</c>, <c>ModifyCardPlayResultLocationMirrors</c>). The native
/// <c>AfterModifyingCardPlayResultLocation</c> only flashes; it has no state.
/// </summary>
[ContentId("CORRUPTION_POWER")]
public sealed class CorruptionPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        if (self.Owner != Limits.PlayerOwner
            || Registry.Card(card.Id).ResolveCardType(battle, rules, card) != CardType.Skill)
        {
            return amount;
        }
        return 0m;
    }

    public override Pile ModifyCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        bool isAutoPlay, int energyValue, Pile location, ref PilePosition position)
    {
        if (self.Owner != Limits.PlayerOwner
            || Kernel.ResolveCardType(x.B, x.R, card) != CardType.Skill)
        {
            return location;
        }
        return Pile.Exhaust;
    }

    public override void AfterModifyingCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        Pile location)
    {
    }
}

/// <summary>
/// Native <c>FreeAttackPower</c>: Attack cards in hand or the play pile cost 0 through the late cost
/// pass, and the power loses one stack on <c>BeforeCardPlayed</c> for such an Attack
/// (<c>ModifyEnergyCostInCombatMirrors.HandleFreeAttackPower</c>,
/// <c>BeforeCardPlayedMirrors.HandleFreeAttackPower</c>).
/// </summary>
[ContentId("FREE_ATTACK_POWER")]
public sealed class FreeAttackPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        if (self.Owner != Limits.PlayerOwner
            || Registry.Card(card.Id).ResolveCardType(battle, rules, card) != CardType.Attack
            || pile is not (Pile.Hand or Pile.Play))
        {
            return amount;
        }
        return 0m;
    }

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner
            || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
        {
            return;
        }
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }
}

/// <summary>
/// Native <c>OneTwoPunchPower</c>: each played Attack owned by the power owner gets one extra play;
/// <c>AfterModifyingCardPlayCount</c> decrements the power, and the player turn end removes it
/// (<c>ModifyCardPlayCountMirrors</c>, <c>CorePowerSupport.TriggerTransientSideTurnEndPowers</c>).
/// </summary>
[ContentId("ONE_TWO_PUNCH_POWER")]
public sealed class OneTwoPunchPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount)
    {
        if (self.Owner != Limits.PlayerOwner
            || Kernel.ResolveCardType(x.B, x.R, card) != CardType.Attack)
        {
            return playCount;
        }
        return playCount + 1;
    }

    public override void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, amount - 1);
    }
}

/// <summary>
/// Native <c>ViciousPower</c>: when its owner applies Vulnerable, draw one card per stack
/// (<c>PowerLifecycleSupport.ResolvePowerAmountChanges</c>'s Vicious case). The draw happens when the
/// queued amount change is resolved, not inside the effect that applied Vulnerable.
/// </summary>
[ContentId("VICIOUS_POWER")]
public sealed class ViciousPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target)
    {
        if (self.Owner != Limits.PlayerOwner
            || delta <= 0
            || applier != self.Owner
            || !Registry.TryPower("VULNERABLE_POWER", out ushort vulnerable)
            || powerId != vulnerable)
        {
            return;
        }
        int amount = x.B.Powers[self.Index].Amount;
        if (amount > 0)
            Kernel.Draw(ref x, amount, fromHandDraw: false);
    }
}
