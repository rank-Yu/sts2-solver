using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>TwistedFunnel</c>: at the first player side turn start applies Poison to every hittable enemy.
/// The first turn starts before the captured root (the existing engine registers it as ignored for
/// the same reason), so the definition has no combat hook.
/// </summary>
[ContentId("TWISTED_FUNNEL")]
public sealed partial class TwistedFunnel : RelicDef
{
}

/// <summary>
/// <c>BookRepairKnife</c>: heals its owner after a Doom kill. The Doom settlement
/// (<c>Kernel.DoomKill</c>) already reads the relic's Heal variable; this definition exists so a
/// root holding it can be admitted and provides the variable.
/// </summary>
[ContentId("BOOK_REPAIR_KNIFE")]
public sealed class BookRepairKnife : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Heal"];
}

/// <summary>
/// <c>FuneraryMask</c>: adds 3 Souls to the draw pile at the first <c>BeforeHandDraw</c>, before the
/// captured root; the Souls and their random positions are already in the root.
/// </summary>
[ContentId("FUNERARY_MASK")]
public sealed partial class FuneraryMask : RelicDef
{
}

/// <summary>
/// <c>Tingsha</c>: after a card is discarded, while its owner is the current side, deal 3 unpowered
/// damage to a random hittable enemy (<c>AfterCardDiscardedMirrors.HandleTingsha</c>).
/// </summary>
[ContentId("TINGSHA")]
public sealed class Tingsha : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void AfterCardDiscarded(ref Exec x, Listener self, in Card card)
    {
        if (x.B.Side != Side.Player)
            return;
        CardEffects.DamageRandomEnemy(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
        Kernel.ApplyEnemyDeathPowers(ref x);
    }
}

/// <summary>
/// <c>Candelabra</c>: <c>AfterSideTurnStart</c>, player turn 2, gains 2 energy
/// (<c>SimulatedCombatState.RelicTurnStart.TriggerRelicsAfterSideTurnStart</c>). Turn 2 is reached after
/// the root through <c>AdvanceRound</c>.
/// </summary>
[ContentId("CANDELABRA")]
public sealed class Candelabra : RelicDef
{
    // Candelabra.cs: EnergyVar(2), _energyTurn = 2.
    private const int EnergyTurn = 2;

    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn == EnergyTurn)
            Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>HornCleat</c>: <c>AfterBlockCleared</c>, player turn 2, gains 14 unpowered block
/// (<c>TriggerRelicsAfterBlockCleared</c>).
/// </summary>
[ContentId("HORN_CLEAT")]
public sealed class HornCleat : RelicDef
{
    // HornCleat.cs: BlockVar(14m, ValueProp.Unpowered); block turn 2 is a code constant.
    private const int BlockTurn = 2;

    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterBlockCleared(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn == BlockTurn)
            Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
                ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>SparklingRouge</c>: <c>AfterBlockCleared</c>, player turn 3, applies 1 Strength and 1 Dexterity
/// (<c>TriggerRelicsAfterBlockCleared</c>).
/// </summary>
[ContentId("SPARKLING_ROUGE")]
public sealed class SparklingRouge : RelicDef
{
    // SparklingRouge.cs: PowerVar<StrengthPower>(1m), PowerVar<DexterityPower>(1m); block turn 3 is
    // a code constant.
    private const int BlockTurn = 3;

    public override IReadOnlyList<string> VarNames => ["StrengthPower", "DexterityPower"];

    public override void AfterBlockCleared(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn != BlockTurn)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StrengthPower>(),
            (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<DexterityPower>(),
            (int)x.R.RelicVar(Id, 1), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>RippleBasin</c>: <c>BeforeSideTurnEnd</c>, if no attack card was played this turn, gains 4 block
/// (<c>BeforeSideTurnEndMirrors.HandleRippleBasin</c>; Sts2Solver's
/// <see cref="Counter.AttacksPlayedThisTurn"/> is the branch-local counterpart of
/// <c>GetAttacksPlayedThisTurn</c>).
/// </summary>
[ContentId("RIPPLE_BASIN")]
public sealed class RippleBasin : RelicDef
{
    // RippleBasin.cs: BlockVar(4m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner) || x.B.Counter(Counter.AttacksPlayedThisTurn) != 0)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>MercuryHourglass</c>: <c>AfterPlayerTurnStart</c> deals 3 unpowered damage to every hittable enemy
/// on every player turn (<c>ContinueRelicsAfterPlayerTurnStart</c>).
/// </summary>
[ContentId("MERCURY_HOURGLASS")]
public sealed class MercuryHourglass : RelicDef
{
    // MercuryHourglass.cs: DamageVar(3m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
        => CardEffects.DamageAllEnemies(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
}

/// <summary>
/// <c>MiniatureCannon</c>: <c>ModifyDamageAdditive</c> adds 3 to powered attacks from an upgraded card
/// (<c>ModifyDamageMirrors</c> native fallback; <c>PredictionCoverage.IsVerifiedNativeRelicHook</c>).
/// </summary>
[ContentId("MINIATURE_CANNON")]
public sealed class MiniatureCannon : RelicDef
{
    // MiniatureCannon.cs: DynamicVar("ExtraDamage", 3m).
    public override IReadOnlyList<string> VarNames => ["ExtraDamage"];

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        // Native condition: reject only when neither the dealer is the owner's creature nor the
        // card source belongs to the owner. An Osty attack has the pet as dealer but the player's
        // card as source, so it still gains the bonus.
        if (!damage.Props.IsPoweredAttack() || !damage.HasCardSource)
            return 0m;
        return damage.CardSource.Upgrade > 0 ? x.R.RelicVar(Id, 0) : 0m;
    }
}

/// <summary>
/// <c>ParryingShield</c>: <c>AfterSideTurnEnd</c>, when the player has at least 10 block, deals 6 unpowered
/// damage to one random hittable enemy (<c>SimulatedCombatState.ReactiveRelics.TriggerRelicsAfterSideTurnEnd</c>).
/// </summary>
[ContentId("PARRYING_SHIELD")]
public sealed class ParryingShield : RelicDef
{
    // ParryingShield.cs: BlockVar(10m, ValueProp.Unpowered), DamageVar(6m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block", "Damage"];

    public override void AfterSideTurnEnd(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.Creature(Limits.PlayerOwner).Block < x.R.RelicVar(Id, 0))
            return;
        CardEffects.DamageRandomEnemy(ref x, x.R.RelicVar(Id, 1), ValueProps.Unpowered,
            Limits.PlayerOwner);
        Kernel.ApplyEnemyDeathPowers(ref x);
    }
}

/// <summary>
/// <c>PaperPhrog</c>: the relic has no hook of its own; <c>VulnerablePower</c> adds 0.25 to its
/// multiplier when the owner deals the powered attack (native <c>ModifyVulnerableMultiplier</c>,
/// called from <c>ModifyDamageMirrors.HandleVulnerablePower</c>). The empty definition admits roots
/// holding it; the power carries the effect.
/// </summary>
[ContentId("PAPER_PHROG")]
public sealed class PaperPhrog : RelicDef;

/// <summary>
/// <c>GremlinHorn</c>: <c>AfterDeath</c> of an enemy gains Energy and draws Cards
/// (<c>AfterDeathMirrors.HandleGremlinHorn</c>).
/// </summary>
[ContentId("GREMLIN_HORN")]
public sealed class GremlinHorn : RelicDef
{
    // GremlinHorn.cs: EnergyVar(1), CardsVar(1).
    public override IReadOnlyList<string> VarNames => ["Energy", "Cards"];

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented)
    {
        if (!Kernel.IsEnemyOwner(creature))
            return;
        Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
        if (x.NeedsChoice)
            return;
        Kernel.Draw(ref x, (int)x.R.RelicVar(Id, 1), fromHandDraw: false);
    }
}

/// <summary>
/// <c>SelfFormingClay</c>: <c>AfterDamageReceived</c> applies <c>BlockNextTurn</c> worth of
/// <see cref="SelfFormingClayPower"/> on unblocked damage to the owner
/// (<c>AfterDamageReceivedMirrors.HandleSelfFormingClay</c>).
/// </summary>
[ContentId("SELF_FORMING_CLAY")]
public sealed class SelfFormingClay : RelicDef
{
    // SelfFormingClay.cs: DynamicVar("BlockNextTurn", 3m).
    public override IReadOnlyList<string> VarNames => ["BlockNextTurn"];

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result,
        byte dealer)
    {
        if (!Kernel.IsInProgress(x.B) || result.Receiver != Limits.PlayerOwner
            || result.UnblockedDamage <= 0)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<SelfFormingClayPower>(),
            (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>Akabeko</c>: <c>AfterSideTurnStart</c> applies Vigor only when the player turn number is &lt;= 1
/// (<c>SimulatedCombatState.RelicTurnStart.TriggerRelicsAfterSideTurnStart</c>). The player-side turn 1
/// start precedes the root; the relic owner never participates in the enemy side's relic batch
/// (<c>RelicsParticipatingInSideTurn</c> requires the owner creature in the participants), so no later
/// side can trigger it.
/// </summary>
[ContentId("AKABEKO")]
public sealed partial class Akabeko : RelicDef;

/// <summary>
/// <c>Pantograph</c>: <c>BeforeCombatStart</c> heals 25 only in a boss room, during combat setup; no later
/// hook reads it.
/// </summary>
[ContentId("PANTOGRAPH")]
public sealed partial class Pantograph : RelicDef;

/// <summary>
/// <c>PetrifiedToad</c>: <c>BeforeCombatStartLate</c> procures a PotionShapedRock before the combat root;
/// the potion is part of the imported starting state and there is no later hook.
/// </summary>
[ContentId("PETRIFIED_TOAD")]
public sealed partial class PetrifiedToad : RelicDef;

/// <summary>
/// <c>StoneCracker</c>: <c>AfterRoomEntered</c> upgrades two upgradable draw-pile cards when entering a
/// combat room, before the root; the upgraded cards are imported.
/// </summary>
[ContentId("STONE_CRACKER")]
public sealed partial class StoneCracker : RelicDef;

/// <summary><c>BowlerHat</c>: <c>ModifyGoldGained</c>/<c>AfterModifyingGoldGained</c> only; the existing engine registers it in <c>GoldGainedMirrors</c>, never as a combat hook.</summary>
[ContentId("BOWLER_HAT")]
public sealed class BowlerHat : RelicDef;

/// <summary><c>EternalFeather</c>: <c>AfterRoomEntered</c> rest-site heal only.</summary>
[ContentId("ETERNAL_FEATHER")]
public sealed class EternalFeather : RelicDef;

/// <summary><c>LastingCandy</c>: <c>BeforeCombatRewardOffered</c>, reward card options and <c>IsAllowed</c> only.</summary>
[ContentId("LASTING_CANDY")]
public sealed class LastingCandy : RelicDef;

/// <summary><c>LuckyFysh</c>: <c>AfterCardChangedPiles</c> only for deck changes, plus <c>IsAllowed</c>.</summary>
[ContentId("LUCKY_FYSH")]
public sealed class LuckyFysh : RelicDef;

/// <summary><c>Pear</c>: <c>AfterObtained</c> max-HP gain only.</summary>
[ContentId("PEAR")]
public sealed class Pear : RelicDef;

/// <summary><c>Planisphere</c>: <c>AfterRoomEntered</c> and <c>IsAllowed</c> only.</summary>
[ContentId("PLANISPHERE")]
public sealed class Planisphere : RelicDef;

/// <summary><c>TinyMailbox</c>: rest-site heal reward hooks only.</summary>
[ContentId("TINY_MAILBOX")]
public sealed class TinyMailbox : RelicDef;

/// <summary>
/// <c>JossPaper</c>: exhausted cards advance a counter and draw per threshold; Ethereal exhausts are
/// deferred to <c>AfterSideTurnEnd</c> (<c>AfterCardExhaustedMirrors.HandleJossPaper</c> and the
/// native <c>AfterSideTurnEnd</c>).
/// Counter = exhausts toward the threshold, Aux = Ethereal exhausts awaiting the turn-end flush.
/// </summary>
[ContentId("JOSS_PAPER")]
public sealed class JossPaper : RelicDef
{
    // JossPaper.cs: DynamicVar("ExhaustAmount", 5m).
    public override IReadOnlyList<string> VarNames => ["ExhaustAmount"];

    public override void AfterCardExhausted(ref Exec x, Listener self, in Card card, bool causedByEthereal)
    {
        ref Relic relic = ref x.B.Relics[self.Index];
        if (causedByEthereal)
        {
            relic.Aux++;
            return;
        }
        relic.Counter++;
        DrawIfThresholdMet(ref x, ref relic);
    }

    public override void AfterSideTurnEnd(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter += relic.Aux;
        relic.Aux = 0;
        DrawIfThresholdMet(ref x, ref relic);
    }

    private void DrawIfThresholdMet(ref Exec x, ref Relic relic)
    {
        int threshold = (int)x.R.RelicVar(Id, 0);
        if (relic.Counter < threshold)
            return;
        int draws = relic.Counter / threshold;
        Kernel.Draw(ref x, draws, fromHandDraw: false);
        relic.Counter %= threshold;
    }
}

/// <summary>
/// <c>Kusarigama</c>: every third attack played this turn deals its Damage to a random enemy;
/// <c>AfterSideTurnEnd</c> resets the counter (<c>AfterCardPlayedMirrors.HandleKusarigama</c>,
/// <c>RelicPredictionStateSupport.ResetAfterSideTurnEnd</c>).
/// Counter = attacks played this turn.
/// </summary>
[ContentId("KUSARIGAMA")]
public sealed class Kusarigama : RelicDef
{
    // Kusarigama.cs: CardsVar(3), DamageVar(6m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Cards", "Damage"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter++;
        if (relic.Counter % (int)x.R.RelicVar(Id, 0) != 0)
            return;
        CardEffects.DamageRandomEnemy(ref x, x.R.RelicVar(Id, 1), ValueProps.Unpowered,
            Limits.PlayerOwner);
        // CreatureCmd.Damage resolves the killed enemy's death triggers before the relic hook
        // returns; the next replay of the same card must observe them (PossessStrengthPower's
        // refund changes the following hit).
        Kernel.ApplyEnemyDeathPowers(ref x);
    }

    public override void AfterSideTurnEnd(ref Exec x, Listener self, Side side)
    {
        // Native phase two (the reset) is skipped when the last enemy died during phase one; the
        // kernel's relic pass still runs, so the ending gate lives here.
        if (side == Side.Player && !Kernel.IsOverOrEnding(x.B))
            x.B.Relics[self.Index].Counter = 0;
    }
}

/// <summary>
/// <c>LetterOpener</c>: every third Skill played this turn deals its Damage to all enemies;
/// <c>AfterSideTurnStart</c> resets the counter after turn 1
/// (<c>AfterCardPlayedMirrors.HandleLetterOpener</c>, <c>ResetAfterSideTurnStart</c>).
/// Counter = Skills played this turn.
/// </summary>
[ContentId("LETTER_OPENER")]
public sealed class LetterOpener : RelicDef
{
    // LetterOpener.cs: CardsVar(3), DamageVar(5m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Cards", "Damage"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B) || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Skill)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter++;
        if (relic.Counter % (int)x.R.RelicVar(Id, 0) != 0)
            return;
        CardEffects.DamageAllEnemies(ref x, x.R.RelicVar(Id, 1), ValueProps.Unpowered,
            Limits.PlayerOwner);
    }

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn > 1)
            x.B.Relics[self.Index].Counter = 0;
    }
}

/// <summary>
/// <c>Nunchaku</c>: every tenth attack played grants Energy; the counter survives turns
/// (<c>AfterCardPlayedMirrors.HandleNunchaku</c>).
/// Counter = attacks played this combat.
/// </summary>
[ContentId("NUNCHAKU")]
public sealed class Nunchaku : RelicDef
{
    // Nunchaku.cs: CardsVar(10), EnergyVar(1).
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter++;
        if (!Kernel.IsInProgress(x.B) || relic.Counter % (int)x.R.RelicVar(Id, 0) != 0)
            return;
        Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 1));
    }
}

/// <summary>
/// <c>Orichalcum</c>: <c>BeforeSideTurnEndVeryEarly</c> latches when the player has no block;
/// <c>BeforeSideTurnEnd</c> spends the latch for its Block; <c>BeforeSideTurnStart</c> clears it
/// (<c>OrichalcumMirrors</c>).
/// Counter = latch set by the VeryEarly pass (always 0 at a stable boundary).
/// </summary>
[ContentId("ORICHALCUM")]
public sealed class Orichalcum : RelicDef
{
    // Orichalcum.cs: BlockVar(6m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void BeforeSideTurnEndVeryEarly(ref Exec x, Listener self, Side side,
        ushort participants)
    {
        if (Kernel.Participates(participants, self.Owner)
            && x.B.Creature(self.Owner).Block <= 0)
            x.B.Relics[self.Index].Counter = 1;
    }

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        ref Relic relic = ref x.B.Relics[self.Index];
        if (relic.Counter == 0)
            return;
        relic.Counter = 0;
        Kernel.GainBlock(ref x, self.Owner, x.R.RelicVar(Id, 0), ValueProps.Unpowered, fromCard: false);
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
        => x.B.Relics[self.Index].Counter = 0;
}

/// <summary>
/// <c>OrnamentalFan</c>: every third attack played this turn gains its Block;
/// <c>BeforeSideTurnStart</c> resets the counter (<c>AfterCardPlayedMirrors.HandleOrnamentalFan</c>).
/// Counter = attacks played this turn.
/// </summary>
[ContentId("ORNAMENTAL_FAN")]
public sealed class OrnamentalFan : RelicDef
{
    // OrnamentalFan.cs: CardsVar(3), BlockVar(4m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Cards", "Block"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B) || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter++;
        if (relic.Counter % (int)x.R.RelicVar(Id, 0) != 0)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 1),
            ValueProps.Unpowered, fromCard: false);
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
        => x.B.Relics[self.Index].Counter = 0;
}

/// <summary>
/// <c>PenNib</c>: <c>BeforeCardPlayed</c> advances the attack counter; the tenth attack doubles its
/// own damage and the latch clears when that card finishes (<c>BeforeCardPlayedMirrors.HandlePenNib</c>,
/// <c>ModifyDamageMirrors.HandlePenNib</c>, <c>AfterCardPlayedMirrors.HandlePenNib</c>).
/// Counter = attacks played modulo 10.
/// </summary>
[ContentId("PEN_NIB")]
public sealed class PenNib : RelicDef
{
    // PenNib.cs: _attacksThreshold = 10 (a code constant, not a DynamicVar).
    private const int AttacksThreshold = 10;

    // The native latch is the CardPlay whose damage to double (PenNibPredictionState.AttackToDouble).
    // Sts2Solver stores that play's one-action identity instead of a boolean: a Sly card auto-played by the
    // tenth attack's own selection is a different CardPlay and must not inherit the doubling.
    // Aux is 0 at every stable boundary because the triggering card's AfterCardPlayed clears it.
    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter = (relic.Counter + 1) % AttacksThreshold;
        if (relic.Counter == 0)
            relic.Aux = x.PlayIdentity;
    }

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        // PenNib.cs: dealer == Owner.Creature || dealer == Owner.Osty.
        if (!damage.Props.IsPoweredAttack() || !damage.HasCardSource
            || !Kernel.IsOwnerOrOsty(self.Owner, damage.Dealer))
            return 1m;
        // Native: `AttackToDouble == context.CardSource.Original`. The play identity names the
        // CardPlay that owns the latch; a nested auto-play has already replaced x.PlayIdentity.
        return x.B.Relics[self.Index].Aux != 0 && x.PlayIdentity == x.B.Relics[self.Index].Aux
            ? 2m
            : 1m;
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref Relic relic = ref x.B.Relics[self.Index];
        if (relic.Aux != 0 && relic.Aux == x.LastPlayIdentity)
            relic.Aux = 0;
    }
}

/// <summary>
/// <c>Permafrost</c>: the first Power card played this combat gains its block
/// (<c>AfterCardPlayedMirrors.HandlePermafrost</c>).
/// Counter = 1 once used this combat.
/// </summary>
[ContentId("PERMAFROST")]
public sealed class Permafrost : RelicDef
{
    // Permafrost.cs: BlockVar(7m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B)
            || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Power
            || x.B.Relics[self.Index].Counter != 0)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
            ValueProps.Unpowered, fromCard: false);
        x.B.Relics[self.Index].Counter = 1;
    }
}

/// <summary>
/// <c>TuningFork</c>: every tenth Skill played gains its Block and the counter wraps
/// (<c>AfterCardPlayedMirrors.HandleTuningFork</c>). Counter = Skills played toward the threshold.
/// </summary>
[ContentId("TUNING_FORK")]
public sealed class TuningFork : RelicDef
{
    // TuningFork.cs: CardsVar(10), BlockVar(7m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Cards", "Block"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Skill)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter++;
        int threshold = (int)x.R.RelicVar(Id, 0);
        if (relic.Counter < threshold)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 1),
            ValueProps.Unpowered, fromCard: false);
        relic.Counter -= threshold;
    }
}
