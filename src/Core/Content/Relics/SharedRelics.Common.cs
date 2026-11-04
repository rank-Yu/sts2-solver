using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>StrikeDummy</c>: <c>ModifyDamageAdditive</c> adds 3 to powered attacks from a card with the Strike
/// tag (<c>ModifyDamageMirrors</c> falls back to the native relic hook;
/// <c>PredictionCoverage.IsVerifiedNativeRelicHook</c>).
/// </summary>
[ContentId("STRIKE_DUMMY")]
public sealed class StrikeDummy : RelicDef
{
    // StrikeDummy.cs: DynamicVar("ExtraDamage", 3m).
    public override IReadOnlyList<string> VarNames => ["ExtraDamage"];

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        // StrikeDummy.cs: reject only when neither the dealer is the owner's creature nor the
        // card source belongs to the owner. Every card source in a root is the player's, so an
        // Osty attack still satisfies the card-source branch.
        if (!damage.Props.IsPoweredAttack() || !damage.HasCardSource)
            return 0m;
        return (x.R.Card(damage.CardSource.Id, damage.CardSource.Upgrade).Tags & CardTag.Strike) != 0
            ? x.R.RelicVar(Id, 0)
            : 0m;
    }
}

/// <summary>
/// <c>Anchor</c>: <c>BeforeCombatStart</c> gains 10 unpowered block. That is before the combat root and
/// no hook remains afterwards.
/// </summary>
[ContentId("ANCHOR")]
public sealed partial class Anchor : RelicDef;

/// <summary>
/// <c>BagOfMarbles</c>: <c>BeforeSideTurnStart</c> applies Vulnerable only when the player turn number is
/// &lt;= 1 (<c>PrepareRelicBeforeSideTurnStart</c>). Only the player side participates, and the player turn
/// 1 start is before the root; from turn 2 the condition is false.
/// </summary>
[ContentId("BAG_OF_MARBLES")]
public sealed partial class BagOfMarbles : RelicDef;

/// <summary>
/// <c>BagOfPreparation</c>: <c>ModifyHandDraw</c> adds 2 only when the turn number is &lt;= 1
/// (<c>PersistentPowerSupport.IsBranchOwnedHandDrawRelic</c> and <c>GetTurnBasedHandDrawContribution</c>).
/// The turn 1 hand draw happens before the root; on turn 2 the condition is false.
/// </summary>
[ContentId("BAG_OF_PREPARATION")]
public sealed partial class BagOfPreparation : RelicDef;

/// <summary>
/// <c>BloodVial</c>: <c>AfterPlayerTurnStartLate</c> heals 2 on turn &lt;= 1; the existing engine runs the
/// same heal from <c>ContinueRelicsAfterPlayerTurnStart</c> on turn &lt;= 1. That is before the root and
/// no later turn satisfies it.
/// </summary>
[ContentId("BLOOD_VIAL")]
public sealed partial class BloodVial : RelicDef;

/// <summary>
/// <c>BronzeScales</c>: <c>AfterRoomEntered</c> applies Thorns when entering a combat room, i.e. during
/// combat setup. The root already carries the power, and the relic has no later hook.
/// </summary>
[ContentId("BRONZE_SCALES")]
public sealed partial class BronzeScales : RelicDef;

/// <summary>
/// <c>FestivePopper</c>: <c>AfterPlayerTurnStart</c> damages all opponents only when the turn number is
/// &lt;= 1 (<c>ContinueRelicsAfterPlayerTurnStart</c>). The turn 1 start is before the root.
/// </summary>
[ContentId("FESTIVE_POPPER")]
public sealed partial class FestivePopper : RelicDef;

/// <summary>
/// <c>Gorget</c>: <c>AfterRoomEntered</c> applies Plating when entering a combat room, during combat
/// setup, and has no later hook.
/// </summary>
[ContentId("GORGET")]
public sealed partial class Gorget : RelicDef;

/// <summary>
/// <c>Lantern</c>: <c>AfterSideTurnStart</c> gains 1 energy only when the player turn number is &lt;= 1
/// (<c>TriggerRelicsAfterSideTurnStart</c>). The player turn 1 start is before the root.
/// </summary>
[ContentId("LANTERN")]
public sealed partial class Lantern : RelicDef;

/// <summary>
/// <c>OddlySmoothStone</c>: <c>AfterRoomEntered</c> applies Dexterity when entering a combat room, during
/// combat setup, and has no later hook.
/// </summary>
[ContentId("ODDLY_SMOOTH_STONE")]
public sealed partial class OddlySmoothStone : RelicDef;

/// <summary>
/// <c>RedMask</c>: <c>BeforeSideTurnStart</c> applies Weak only when the player turn number is &lt;= 1
/// (<c>PrepareRelicBeforeSideTurnStart</c>); only the player side participates and the turn 1 start is
/// before the root.
/// </summary>
[ContentId("RED_MASK")]
public sealed partial class RedMask : RelicDef;

/// <summary>
/// <c>Vajra</c>: <c>AfterRoomEntered</c> applies Strength when entering a combat room, during combat
/// setup, and has no later hook.
/// </summary>
[ContentId("VAJRA")]
public sealed partial class Vajra : RelicDef;

/// <summary>Native <c>AmethystAubergine</c>: <c>TryModifyRewards</c>, <c>AfterModifyingRewards</c> and <c>IsAllowed</c> only.</summary>
[ContentId("AMETHYST_AUBERGINE")]
public sealed class AmethystAubergine : RelicDef;

/// <summary><c>BookOfFiveRings</c>: <c>AfterCardChangedPiles</c> only fires for <c>PileType.Deck</c> changes; <c>IsAllowed</c> is run-state only.</summary>
[ContentId("BOOK_OF_FIVE_RINGS")]
public sealed class BookOfFiveRings : RelicDef;

/// <summary><c>JuzuBracelet</c>: <c>ModifyUnknownMapPointRoomTypes</c> and <c>IsAllowed</c> only.</summary>
[ContentId("JUZU_BRACELET")]
public sealed class JuzuBracelet : RelicDef;

/// <summary><c>MealTicket</c>: <c>AfterRoomEntered</c> shop heal and <c>IsAllowed</c> only.</summary>
[ContentId("MEAL_TICKET")]
public sealed class MealTicket : RelicDef;

/// <summary><c>PotionBelt</c>: <c>AfterObtained</c> extra potion slots only.</summary>
[ContentId("POTION_BELT")]
public sealed class PotionBelt : RelicDef;

/// <summary><c>RegalPillow</c>: rest-site heal hooks only.</summary>
[ContentId("REGAL_PILLOW")]
public sealed class RegalPillow : RelicDef;

/// <summary><c>Strawberry</c>: <c>AfterObtained</c> max-HP gain only.</summary>
[ContentId("STRAWBERRY")]
public sealed class Strawberry : RelicDef;

/// <summary><c>WarPaint</c>: <c>AfterObtained</c> card upgrades only.</summary>
[ContentId("WAR_PAINT")]
public sealed class WarPaint : RelicDef;

/// <summary><c>Whetstone</c>: <c>AfterObtained</c> attack upgrades only.</summary>
[ContentId("WHETSTONE")]
public sealed class Whetstone : RelicDef;

/// <summary>
/// <c>CentennialPuzzle</c>: the first unblocked damage taken draws its Cards
/// (<c>AfterDamageReceivedMirrors</c>, engine case in <c>SimulatedCombatState</c> mirror).
/// Counter = 1 once used this combat.
/// </summary>
[ContentId("CENTENNIAL_PUZZLE")]
public sealed class CentennialPuzzle : RelicDef
{
    // CentennialPuzzle.cs: CardsVar(3).
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (!Kernel.IsInProgress(x.B) || result.Receiver != Limits.PlayerOwner
            || result.UnblockedDamage <= 0 || x.B.Relics[self.Index].Counter != 0)
            return;
        x.B.Relics[self.Index].Counter = 1;
        Kernel.Draw(ref x, (int)x.R.RelicVar(Id, 0), fromHandDraw: false);
    }
}

/// <summary>
/// <c>HappyFlower</c>: <c>AfterSideTurnStart</c> advances its cycle and grants Energy on wrap
/// (<c>SimulatedCombatState.RelicTurnStart.TriggerRelicsAfterSideTurnStart</c>).
/// Counter = turns seen in the current cycle.
/// </summary>
[ContentId("HAPPY_FLOWER")]
public sealed class HappyFlower : RelicDef
{
    // HappyFlower.cs: EnergyVar(1), DynamicVar("Turns", 3m).
    public override IReadOnlyList<string> VarNames => ["Energy", "Turns"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        int turns = (int)x.R.RelicVar(Id, 1);
        relic.Counter = (relic.Counter + 1) % turns;
        if (relic.Counter == 0)
            Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>Pendulum</c>: <c>BeforeHandDraw</c> advances its cycle, and <c>ModifyHandDraw</c> adds its
/// Cards on the turn the cycle wraps (<c>SimulatedCombatState.Relics.PrepareRelicsBeforeHandDraw</c>
/// and <c>PersistentPowerSupport.GetStatefulRelicHandDrawContribution</c>).
/// Counter = turns seen in the current cycle.
/// </summary>
[ContentId("PENDULUM")]
public sealed class Pendulum : RelicDef
{
    // Pendulum.cs: CardsVar(1), DynamicVar("Turns", 3m).
    public override IReadOnlyList<string> VarNames => ["Cards", "Turns"];

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
    {
        ref Relic relic = ref x.B.Relics[self.Index];
        int turns = (int)x.R.RelicVar(Id, 1);
        relic.Counter = (relic.Counter + 1) % turns;
        return relic.Counter == 0 ? amount + x.R.RelicVar(Id, 0) : amount;
    }
}

/// <summary>
/// <c>VenerableTeaSet</c>: <c>AfterEnergyReset</c> grants its Energy once when the pre-combat tea is
/// pending (<c>SimulatedCombatState.TriggerRelicsAfterEnergyReset</c>). Counter = 1 while pending.
/// </summary>
[ContentId("VENERABLE_TEA_SET")]
public sealed class VenerableTeaSet : RelicDef
{
    // VenerableTeaSet.cs: EnergyVar(2).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void AfterEnergyReset(ref Exec x, Listener self)
    {
        ref Relic relic = ref x.B.Relics[self.Index];
        if (relic.Counter == 0)
            return;
        Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
        relic.Counter = 0;
    }
}

/// <summary>
/// <c>RedSkull</c>: whenever any creature's current HP changes, the relic keeps Strength applied
/// exactly while its owner is at or below the HP threshold
/// (<c>AfterCurrentHpChangedMirrors.HandleRedSkull</c>; the native room-entry application happens
/// before the root and is imported). Counter = the engine's stateful StrengthApplied flag (0/1),
/// projected by <c>ClaimStatefulRelic</c>.
/// </summary>
[ContentId("RED_SKULL")]
public sealed partial class RedSkull : RelicDef
{
    // RedSkull.cs: DynamicVar("HpThreshold", 50m), PowerVar<StrengthPower>(3m).
    public override IReadOnlyList<string> VarNames => ["HpThreshold", "StrengthPower"];

    public override void AfterCurrentHpChanged(ref Exec x, Listener self, byte creature, decimal delta)
    {
        decimal threshold = x.B.Player.MaxHp * (x.R.RelicVar(Id, 0) / 100m);
        bool shouldApply = x.B.Player.CurrentHp <= threshold;
        ref Relic relic = ref x.B.Relics[self.Index];
        if (shouldApply == (relic.Counter != 0))
            return;
        int strength = (int)x.R.RelicVar(Id, 1);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StrengthPower>(),
            shouldApply ? strength : -strength, Limits.PlayerOwner);
        relic.Counter = shouldApply ? 1 : 0;
    }
}

/// <summary>
/// <c>BoneFlute</c>: after an attack by the owner's Osty, gain its Block variable
/// (<c>AfterAttackMirrors.HandleBoneFlute</c>; the BlockVar is Unpowered).
/// </summary>
[ContentId("BONE_FLUTE")]
public sealed class BoneFlute : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        if (attacker != Limits.OstyOwner)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            fromCard: false);
    }
}

/// <summary>
/// <c>DataDisk</c>: applies Focus 1 on entering a combat room (<c>AfterRoomEntered</c>), which is
/// before the captured root; the Focus power is already in the root.
/// </summary>
[ContentId("DATA_DISK")]
public sealed partial class DataDisk : RelicDef
{
}

/// <summary>
/// <c>SneckoSkull</c>: <c>ModifyPowerAmountGivenAdditive</c> adds 1 to every Poison amount the owner
/// gives (<c>PersistentRelicSupport.ModifyPowerAmountGiven</c>). The after-hook is cosmetic.
/// </summary>
[ContentId("SNECKO_SKULL")]
public sealed class SneckoSkull : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["PoisonPower"];

    public override decimal ModifyPowerAmountGiven(in Battle battle, Rules rules, Listener self,
        ushort powerId, byte giver, byte target, decimal amount)
    {
        if (giver != Limits.PlayerOwner)
            return 0m;
        if (!Registry.TryPower("POISON_POWER", out ushort poison) || powerId != poison)
            return 0m;
        return rules.RelicVar(Id, 0);
    }
}
