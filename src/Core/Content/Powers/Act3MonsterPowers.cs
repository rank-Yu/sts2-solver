using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>PaperCutsPower</c>: after its owner deals unblocked powered-attack damage to a player,
/// that player loses <c>Amount</c> max HP
/// (<c>AfterDamageGivenMirrors.HandlePaperCutsPower</c>, <c>AfterDamageGiven</c> pass).
/// </summary>
[ContentId("PAPER_CUTS_POWER")]
public sealed class PaperCutsPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageGiven(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (dealer != self.Owner
            || result.Receiver != Limits.PlayerOwner
            || !result.Props.IsPoweredAttack()
            || result.UnblockedDamage <= 0)
        {
            return;
        }
        Kernel.LoseMaxHp(ref x, Limits.PlayerOwner, x.B.Powers[self.Index].Amount);
    }
}

/// <summary>
/// Native <c>RampartPower</c>: at the player side turn start it grants its amount as Unpowered Block
/// to every living Turret Operator (<c>PersistentPowerSupport.TriggerRampart</c>).
/// </summary>
[ContentId("RAMPART_POWER")]
public sealed class RampartPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void Rampart(ref Exec x, Listener self)
    {
        int amount = x.B.Powers[self.Index].Amount;
        if (amount <= 0 || !Registry.TryMonster("TURRET_OPERATOR", out ushort turret))
            return;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (x.B.Enemy(owner).MonsterId == turret && x.B.Creature(owner).IsAlive)
            {
                Kernel.GainBlock(ref x, owner, amount, ValueProps.Unpowered, fromCard: false);
                if (x.NeedsChoice)
                    return;
            }
        }
    }
}

/// <summary>
/// Native <c>SoarPower</c>: multiplies powered-attack damage dealt to its owner by
/// <c>DamageDecrease / 100</c>. The existing engine leaves Soar unregistered in
/// <c>ModifyDamageMirrors</c> and therefore falls back to the native model method.
/// </summary>
[ContentId("SOAR_POWER")]
public sealed class SoarPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override IReadOnlyList<string> VarNames => ["DamageDecrease"];

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (damage.Target != self.Owner || !damage.Props.IsPoweredAttack())
            return 1m;
        return x.R.PowerVar(Id, 0) / 100m;
    }
}

/// <summary>
/// Native <c>GalvanicPower</c>: after a Galvanized card is played, the <b>card's owner</b> takes
/// <c>Amount</c> Unpowered damage (<c>AfterCardPlayedMirrors.HandleGalvanicPower</c> damages
/// <c>context.PreviewCard.Owner.Creature</c>, not the power's owner; the Globe Head carries the power
/// while the player plays the card). Every card in a Sts2Solver battle is the player's, so the target is
/// the player owner. Its <c>AfterCardEnteredCombat</c> is mirrored by
/// <c>Kernel.NormalizeCardAfflictions</c> (newly entered Power cards get Galvanized with the
/// power's amount).
/// </summary>
[ContentId("GALVANIC_POWER")]
public sealed class GalvanicPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;


    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (play.Card.AfflictionId == 0
            || play.Card.AfflictionId - 1 != Registry.AfflictionId<Afflictions.GalvanizedAffliction>())
        {
            return;
        }
        Span<byte> target = stackalloc byte[1];
        target[0] = Limits.PlayerOwner;
        Kernel.Damage(ref x, target, x.B.Powers[self.Index].Amount,
            ValueProps.Unpowered, Limits.NoOwner, hasCardSource: false, default);
    }
}

/// <summary>
/// Native <c>ChainsOfBindingPower</c>: on the player's card draws during the player's side this turn
/// it afflicts up to <c>Amount</c> cards with Bound (<c>AfterCardDrawnMirrors</c>), a Bound card's
/// play marks <c>boundCardPlayed</c> (<c>BeforeCardPlayedMirrors</c>) which makes every Bound card
/// unplayable until the player side turn end clears the mark and every Bound affliction
/// (<c>ShouldPlayMirrors</c>, <c>BeforeSideTurnEndMirrors</c>). Aux0 is the mark and Aux1 the count
/// of Bound cards afflicted this turn (ChainsOfBindingPredictionState).
/// </summary>
[ContentId("CHAINS_OF_BINDING_POWER")]
public sealed class ChainsOfBindingPower : PowerDef
{
    private const int BoundCardPlayedAux = 0;
    private const int AfflictedThisTurnAux = 1;

    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card)
    {
        if (card.AfflictionId == 0
            || card.AfflictionId - 1 != Registry.AfflictionId<Afflictions.BoundAffliction>())
        {
            return true;
        }
        int index = Kernel.FindPower(battle, self.Owner, Id);
        return index < 0 || battle.Powers[index].Aux0 == 0;
    }

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        // BeforeCardPlayedMirrors.HandleChainsOfBindingPower skips CardModel.IsDupe, not IsClone.
        // The only dupes Sts2Solver creates are HistoryCourse copies; Card.IsClone marks native
        // CreateClone copies (Swift, AdaptiveStrike), which must set the mark when Bound.
        if (x.CurrentPlayIsHistoryCourseDupe || play.Card.AfflictionId == 0
            || play.Card.AfflictionId - 1 != Registry.AfflictionId<Afflictions.BoundAffliction>())
        {
            return;
        }
        x.B.Powers[self.Index].Aux0 = 1;
    }

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (x.B.Side != Side.Player
            || self.Owner != Limits.PlayerOwner
            || x.B.Powers[self.Index].Aux1 >= x.B.Powers[self.Index].Amount)
        {
            return;
        }
        ref readonly Card card = ref x.B.Cards[drawnPosition];
        ushort bound = Registry.AfflictionId<Afflictions.BoundAffliction>();
        Kernel.ApplyAffliction(ref x, drawnPosition, bound, x.B.Powers[self.Index].Amount);
        if (x.B.Cards[drawnPosition].AfflictionId == bound + 1)
        {
            x.B.Powers[self.Index].Aux1++;
        }
    }

    /// <summary>
    /// Native keeps the per-turn <c>CardAfflictedEntry</c> count until the player turn number
    /// changes; only the play mark and the Bound afflictions are cleared at the side turn end.
    /// Reset the count at the next player side start instead of here, so the turn-end state still
    /// reports this turn's count like <c>ChainsOfBindingPredictionState</c>.
    /// </summary>
    public override void BeforeSideTurnStart(ref Exec x, Listener self, bool participates)
    {
        if (!participates || self.Owner != Limits.PlayerOwner)
            return;
        x.B.Powers[self.Index].Aux1 = 0;
    }

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (side != Side.Player
            || self.Owner != Limits.PlayerOwner
            || !Kernel.Participates(participants, self.Owner))
        {
            return;
        }
        x.B.Powers[self.Index].Aux0 = 0;
        Kernel.ClearAfflictions(ref x.B, Registry.AfflictionId<Afflictions.BoundAffliction>());
    }
}

/// <summary>
/// Native <c>WitheringPresencePower</c> (instanced, applied by Aeonglass to the player): after the
/// player plays a card it decrements its private <c>CardsLeft</c>; at zero it adds one Wither to the
/// hand and resets to 6 (<c>AfterCardPlayedMirrors.HandleWitheringPresencePower</c>). Aux0 is
/// <c>CardsLeft</c> (CounterPredictionState when present, otherwise the native dynamic variable).
/// </summary>
[ContentId("WITHERING_PRESENCE_POWER")]
public sealed class WitheringPresencePower : PowerDef
{
    private const int CardsLeftAux = 0;
    private const int BaseCardsLeft = 6;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override IReadOnlyList<string> VarNames => ["CardsLeft"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("WITHER", 0)];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref Power power = ref x.B.Powers[self.Index];
        power.Aux0--;
        if (power.Aux0 > 0)
            return;
        Kernel.AddGeneratedCard(ref x,
            Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Wither>()), Pile.Hand, creator: Limits.NoOwner);
        if (x.NeedsChoice)
            return;
        x.B.Powers[self.Index].Aux0 = BaseCardsLeft;
    }
}

/// <summary>
/// Native <c>DampenPower</c>: a player-side debuff with no counter behavior of its own. Its casters
/// are tracked in the kernel's <see cref="Counter.DampenCasterMask"/>; MagiKnight applies the
/// downgrades and restores them when the last caster dies.
/// </summary>
[ContentId("DAMPEN_POWER")]
public sealed class DampenPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.None;
}

/// <summary>
/// Native <c>HighVoltagePower</c> (Zapbot's entrance power): at its owner's side turn end it applies
/// its amount as Strength (<c>EndTurnPowerSupport</c>, <c>EndTurnRegular</c>).
/// </summary>
[ContentId("HIGH_VOLTAGE_POWER")]
public sealed class HighVoltagePower : PowerDef
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
/// Native <c>PossessStrengthPower</c>: whenever its owner lowers the player's Strength, the negative
/// delta is recorded; when the owner dies the accumulated amount is given back with no applier
/// (<c>AfterPowerAmountChanged</c>, <c>SimulatedCombatState.RefundPossessedStats</c>). Aux0 is the
/// ledger value (SimulatedCombatState._stolenStrength).
/// </summary>
[ContentId("POSSESS_STRENGTH_POWER")]
public sealed class PossessStrengthPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target)
    {
        if (applier != self.Owner
            || target != Limits.PlayerOwner
            || delta >= 0
            || powerId != Registry.PowerId<StrengthPower>())
        {
            return;
        }
        x.B.Powers[self.Index].Aux0 += delta;
    }

    public override void DeathTrigger(ref Exec x, Listener self, byte dead)
    {
        if (dead != self.Owner)
            return;
        int stolen = x.B.Powers[self.Index].Aux0;
        if (stolen != 0)
        {
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StrengthPower>(),
                -stolen, Limits.NoOwner);
        }
    }
}

/// <summary>Native <c>PossessSpeedPower</c>: the Dexterity counterpart of <see cref="PossessStrengthPower"/>.</summary>
[ContentId("POSSESS_SPEED_POWER")]
public sealed class PossessSpeedPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target)
    {
        if (applier != self.Owner
            || target != Limits.PlayerOwner
            || delta >= 0
            || powerId != Registry.PowerId<DexterityPower>())
        {
            return;
        }
        x.B.Powers[self.Index].Aux0 += delta;
    }

    public override void DeathTrigger(ref Exec x, Listener self, byte dead)
    {
        if (dead != self.Owner)
            return;
        int stolen = x.B.Powers[self.Index].Aux0;
        if (stolen != 0)
        {
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<DexterityPower>(),
                -stolen, Limits.NoOwner);
        }
    }
}

/// <summary>
/// Native <c>StockPower</c>: on its owner's death it spawns a replacement Axebot in the same slot with
/// one less StockPower, and the combat does not end while unresolved (<c>ShouldStopCombatFromEnding</c>,
/// <c>DeathPowerSupport.SpawnsPrimaryEnemyOnDeath</c>). The immediate spawn is implemented by the
/// Axebot monster's <c>AfterDeath</c>; this definition only carries the removal/ending flags.
/// </summary>
[ContentId("STOCK_POWER")]
public sealed class StockPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool SpawnsPrimaryEnemyOnDeath => true;
}

/// <summary>
/// Native <c>AdaptablePower</c> (Test Subject): its owner is not removed on death; the deferred death
/// pass forces the native RESPAWN_MOVE and marks the death phase reviving
/// (<c>DeathPowerSupport.Trigger</c> → <c>BeginAdaptableRevive</c>). Its power survives the owner's
/// death and keeps the combat from ending while the next form is pending.
/// </summary>
[ContentId("ADAPTABLE_POWER")]
public sealed class AdaptablePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool PreventsRemovalAfterDeath => true;

    public override bool PreventsCombatEnding => true;

    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;

    public override void DeathTrigger(ref Exec x, Listener self, byte dead)
    {
        if (dead != self.Owner)
            return;
        MonsterRules rules = Kernel.MonsterRules(ref x, self.Owner);
        x.B.Enemy(self.Owner).DeathPhase = DeathPhase.Reviving;
        Kernel.ForceMonsterMove(ref x, self.Owner, rules.NodeIndex("RESPAWN_MOVE"));
    }
}

/// <summary>
/// Native <c>PainfulStabsPower</c>: after its owner's powered attack, each player hit with unblocked
/// damage adds <c>Amount</c> Wound to the discard pile
/// (<c>AfterAttackMirrors.HandlePainfulStabsPower</c>). The unblocked hit count is kept on the
/// stack-only <see cref="Exec.AttackUnblockedHits"/> for the current attack.
/// </summary>
[ContentId("PAINFUL_STABS_POWER")]
public sealed class PainfulStabsPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("WOUND", 0)];

    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        if (attacker != self.Owner || x.AttackUnblockedHits <= 0)
            return;
        int count = x.AttackUnblockedHits * x.B.Powers[self.Index].Amount;
        for (int index = 0; index < count; index++)
        {
            Kernel.AddGeneratedCard(ref x,
                Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Wound>()), Pile.Discard, creator: Limits.NoOwner);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>NemesisPower</c>: at its owner's side turn end it toggles a private flag and applies or
/// removes Intangible 1 (<c>EndTurnPowerSupport.TriggerNemesis</c>). Aux0 is the toggled flag
/// (SimulatedCombatState._nemesisShouldApplyIntangible).
/// </summary>
[ContentId("NEMESIS_POWER")]
public sealed class NemesisPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (side != Side.Enemy || !participates)
            return;
        ref Power power = ref x.B.Powers[self.Index];
        bool apply = power.Aux0 == 0;
        power.Aux0 = apply ? 1 : 0;
        if (apply)
            Kernel.ApplyPowerFromMonster(ref x, self.Owner,
                Registry.PowerId<IntangiblePower>(), 1, self.Owner);
        else
            Kernel.SetPowerAmount(ref x.B, self.Owner, Registry.PowerId<IntangiblePower>(), 0);
    }
}

/// <summary>
/// Native <c>EnragePower</c> (Test Subject's entrance power): after any Skill card is played its owner
/// gains <c>Amount</c> Strength (<c>AfterCardPlayedMirrors.HandleEnragePower</c>).
/// </summary>
[ContentId("ENRAGE_POWER")]
public sealed class EnragePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Skill)
            return;
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(),
            x.B.Powers[self.Index].Amount, self.Owner);
    }
}
