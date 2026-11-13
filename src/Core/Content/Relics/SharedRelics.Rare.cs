using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>Bookmark</c>: after the turn-end flush, pick one retained hand card that is not X-cost and whose
/// local cost is positive, and add an until-played −1
/// (<c>SimulatedCombatState.TriggerBookmarkAfterFlush</c> / <c>CorePowerSupport.FlushPlayerHandAtTurnEnd</c>).
/// </summary>
[ContentId("BOOKMARK")]
public sealed class Bookmark : RelicDef
{
    public override void AfterFlush(ref Exec x, Listener self)
    {
        int handCount = x.B.HandCount;
        Span<int> candidates = stackalloc int[handCount];
        int count = 0;
        for (int index = 0; index < handCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if ((card.Flags & CardFlags.CostsX) != 0)
                continue;
            if (Kernel.EnergyCostWithLocalModifiers(x.B, card) > 0)
                candidates[count++] = index;
        }
        if (count == 0)
            return;
        int pick = candidates[x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, count)];
        Kernel.AddEnergyCostModifier(ref x.B.Cards[pick], -1, CostType.Relative,
            CostExpiration.WhenPlayed, reduceOnly: false);
    }
}

/// <summary>
/// <c>EmotionChip</c>: trigger every orb's passive at the player turn start when its owner took
/// unblocked damage in the previous turn. The two counters are the existing
/// <c>StatefulRelicState</c> Current/Previous pair and rotate in
/// <c>SimulatedCombatState.RecordRelicRoundDamage</c>.
/// </summary>
[ContentId("EMOTION_CHIP")]
public sealed class EmotionChip : RelicDef
{
    public override bool HasCombatState => true;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver == Limits.PlayerOwner && result.UnblockedDamage > 0)
            x.B.RelicState(x.R, self.Index).Aux = 1;
    }

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (x.B.RelicState(x.R, self.Index).Counter == 0)
            return;
        int count = x.B.OrbCount;
        for (int index = 0; index < count; index++)
        {
            Kernel.TriggerOrbPassive(ref x, index);
            if (x.NeedsChoice)
                return;
        }
    }

    public override void AfterRelicRound(ref Exec x, Listener self)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Counter = relic.Aux;
        relic.Aux = 0;
    }
}

/// <summary>
/// <c>Metronome</c>: every orb channelled increments the relic counter; when it equals OrbCount it
/// deals its Unpowered Damage to every hittable enemy (<c>AfterOrbChanneledMirrors.HandleMetronome</c>).
/// The counter is not reset by the trigger.
/// </summary>
[ContentId("METRONOME")]
public sealed partial class Metronome : RelicDef
{
    public override bool HasCombatState => true;

    public override IReadOnlyList<string> VarNames => ["Damage", "OrbCount"];

    public override void AfterOrbChanneled(ref Exec x, Listener self, in Orb orb)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Counter++;
        if (relic.Counter != (int)x.R.RelicVar(Id, 1))
            return;
        CardEffects.DamageAllEnemies(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>ToughBandages</c>: after one of the current side's cards is discarded, gain its Block variable
/// (Unpowered; <c>AfterCardDiscardedMirrors.HandleToughBandages</c>).
/// </summary>
[ContentId("TOUGH_BANDAGES")]
public sealed class ToughBandages : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterCardDiscarded(ref Exec x, Listener self, in Card card)
    {
        if (x.B.Side != Side.Player)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            fromCard: false);
    }
}

/// <summary>
/// <c>LunarPastry</c>: gains its Stars variable at the end of each side turn it participates in
/// (<c>SimulatedCombatState.TriggerRelicsAfterSideTurnEnd</c> case LunarPastry).
/// </summary>
[ContentId("LUNAR_PASTRY")]
public sealed class LunarPastry : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Stars"];

    public override void AfterSideTurnEnd(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        Kernel.GainStars(ref x, (int)x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>Chandelier</c>: <c>AfterSideTurnStart</c>, player turn 3, gains 3 energy
/// (<c>TriggerRelicsAfterSideTurnStart</c>).
/// </summary>
[ContentId("CHANDELIER")]
public sealed class Chandelier : RelicDef
{
    // Chandelier.cs: EnergyVar(3), _energyTurn = 3.
    private const int EnergyTurn = 3;

    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn == EnergyTurn)
            Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>CaptainsWheel</c>: <c>AfterBlockCleared</c>, player turn 3, gains 18 unpowered block
/// (<c>SimulatedCombatState.ReactiveRelics.TriggerRelicsAfterBlockCleared</c>).
/// </summary>
[ContentId("CAPTAINS_WHEEL")]
public sealed class CaptainsWheel : RelicDef
{
    // CaptainsWheel.cs: BlockVar(18m, ValueProp.Unpowered); block turn 3 is a code constant.
    private const int BlockTurn = 3;

    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterBlockCleared(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn == BlockTurn)
            Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
                ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>CloakClasp</c>: <c>BeforeSideTurnEnd</c> hook (normal pass) gains 1 unpowered block per card in hand
/// (<c>BeforeSideTurnEndMirrors.HandleCloakClasp</c>).
/// </summary>
[ContentId("CLOAK_CLASP")]
public sealed class CloakClasp : RelicDef
{
    // CloakClasp.cs: BlockVar(1m, ValueProp.Unpowered), multiplied by the hand count.
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner) || x.B.HandCount == 0)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.B.HandCount * x.R.RelicVar(Id, 0),
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>StoneCalendar</c>: <c>BeforeSideTurnEnd</c> on player turn 7 deals 52 unpowered damage to every
/// hittable enemy (<c>BeforeSideTurnEndMirrors.HandleStoneCalendar</c>). The existing engine reads the
/// turn number, not a private counter, so no imported state is needed.
/// </summary>
[ContentId("STONE_CALENDAR")]
public sealed class StoneCalendar : RelicDef
{
    // StoneCalendar.cs: DamageVar(52m, ValueProp.Unpowered), DynamicVar("DamageTurn", 7m).
    public override IReadOnlyList<string> VarNames => ["Damage", "DamageTurn"];

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner)
            || x.B.PlayerTurn != (int)x.R.RelicVar(Id, 1))
            return;
        CardEffects.DamageAllEnemies(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>GamePiece</c>: <c>AfterCardPlayed</c>, after a Power card, draws 1
/// (<c>AfterCardPlayedMirrors.HandleGamePiece</c>).
/// </summary>
[ContentId("GAME_PIECE")]
public sealed class GamePiece : RelicDef
{
    // GamePiece.cs: CardsVar(1).
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Power)
            return;
        Kernel.Draw(ref x, (int)x.R.RelicVar(Id, 0), fromHandDraw: false);
    }
}

/// <summary>
/// <c>RazorTooth</c>: <c>AfterCardPlayed</c> upgrades the played Attack or Skill when it is still
/// upgradable (<c>AfterCardPlayedMirrors.HandleRazorTooth</c>).
/// </summary>
[ContentId("RAZOR_TOOTH")]
public sealed class RazorTooth : RelicDef
{
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        CardStats stats = x.R.Card(play.Card.Id, play.Card.Upgrade);
        // Native reads cardPlay.Card.Type, which for MadScience is the TinkerTime runtime type, not
        // the canonical card type frozen in the rule table.
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) is not (CardType.Attack or CardType.Skill))
            return;
        if (play.Card.Upgrade >= stats.MaxUpgradeLevel)
            return;
        CardEffects.UpgradeCurrentCard(ref x);
    }
}

/// <summary>
/// <c>IntimidatingHelmet</c>: <c>BeforeCardPlayed</c>, when the play's energy value is at least 2, gains
/// 4 unpowered block (<c>BeforeCardPlayedMirrors.HandleIntimidatingHelmet</c>; Sts2Solver's
/// <see cref="Exec.XValue"/> carries the same <c>CardPlay.Resources.EnergyValue</c>).
/// </summary>
[ContentId("INTIMIDATING_HELMET")]
public sealed class IntimidatingHelmet : RelicDef
{
    // IntimidatingHelmet.cs: BlockVar(4m, ValueProp.Unpowered), EnergyVar(2).
    public override IReadOnlyList<string> VarNames => ["Block", "Energy"];

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (x.XValue < (int)x.R.RelicVar(Id, 1))
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>CharonsAshes</c>: <c>AfterCardExhausted</c> deals 3 unpowered damage to every hittable enemy
/// (<c>AfterCardExhaustedMirrors.HandleCharonsAshes</c>).
/// </summary>
[ContentId("CHARONS_ASHES")]
public sealed class CharonsAshes : RelicDef
{
    // CharonsAshes.cs: DamageVar(3m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void AfterCardExhausted(ref Exec x, Listener self, in Card card, bool causedByEthereal)
        => CardEffects.DamageAllEnemies(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
}

/// <summary>
/// <c>SturdyClamp</c>: <c>ShouldClearBlock</c> declines the player's block clear and
/// <c>BlockAfterPreventingClear</c> keeps at most 10
/// (<c>PersistentRelicSupport.ShouldClearBlock</c>/<c>BlockAfterPreventingClear</c>).
/// </summary>
[ContentId("STURDY_CLAMP")]
public sealed class SturdyClamp : RelicDef
{
    // SturdyClamp.cs: BlockVar(10m, ValueProp.Unpowered); the engine reads DynamicVars.Block.
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override bool ShouldClearBlock(ref Exec x, Listener self, byte owner)
        => owner != Limits.PlayerOwner;

    public override int BlockAfterPreventingClear(ref Exec x, Listener self, byte owner, int block)
        => owner == Limits.PlayerOwner ? (int)Math.Min(block, x.R.RelicVar(Id, 0)) : block;
}

/// <summary>
/// <c>TungstenRod</c>: <c>ModifyHpLostAfterOsty</c> reduces the owner's HP loss by
/// <c>HpLossReduction</c> (<c>ModifyHpLostMirrors</c> native fallback,
/// <c>PredictionCoverage.IsVerifiedNativeRelicHook</c>). <c>AfterModifyingHpLostAfterOsty</c> only flashes.
/// </summary>
[ContentId("TUNGSTEN_ROD")]
public sealed class TungstenRod : RelicDef
{
    // TungstenRod.cs: DynamicVar("HpLossReduction", 1m).
    public override IReadOnlyList<string> VarNames => ["HpLossReduction"];

    public override decimal ModifyHpLostAfterOsty(ref Exec x, Listener self, in HpLossContext loss,
        decimal amount)
    {
        if (loss.Target != self.Owner)
            return amount;
        return Math.Max(0m, amount - x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>MummifiedHand</c>: <c>AfterCardPlayed</c>, after a Power, picks one hand card with the
/// <c>CombatCardSelection</c> stream and makes it free this turn
/// (<c>AfterCardPlayedMirrors.HandleMummifiedHand</c>; native <c>CardModel.SetToFreeThisTurn</c>).
/// </summary>
[ContentId("MUMMIFIED_HAND")]
public sealed class MummifiedHand : RelicDef
{
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B)
            || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Power)
            return;
        // Native: naturallyCostly.Where(CostsResources), then all hand.Where(CostsResources),
        // then naturallyCostly, then all hand. Rng.NextItem consumes the stream only for a non-empty list.
        int index = PickHandCard(ref x, naturallyCostly: true, costsResources: true);
        if (index < 0)
            index = PickHandCard(ref x, naturallyCostly: false, costsResources: true);
        if (index < 0)
            index = PickHandCard(ref x, naturallyCostly: true, costsResources: false);
        if (index < 0)
            index = PickHandCard(ref x, naturallyCostly: false, costsResources: false);
        if (index < 0)
            return;
        // CardModel.SetToFreeThisTurn: the energy modifier is only added when the card's base cost is
        // not negative (an Unplayable card stays Unplayable with no modifier), and the star cost is
        // set to 0 for this turn.
        Kernel.MakeFreeThisTurn(x.R, ref x.B.Cards[index]);
    }

    private static int PickHandCard(ref Exec x, bool naturallyCostly, bool costsResources)
    {
        int matches = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            if (Matches(ref x, index, naturallyCostly, costsResources))
                matches++;
        }
        if (matches == 0)
            return -1;
        int pick = x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, matches);
        for (int index = 0; index < x.B.HandCount; index++)
        {
            if (!Matches(ref x, index, naturallyCostly, costsResources))
                continue;
            if (pick-- == 0)
                return index;
        }
        throw new InvalidOperationException("手牌随机选择没有落到任何候选。");
    }

    private static bool Matches(ref Exec x, int index, bool naturallyCostly, bool costsResources)
    {
        if (naturallyCostly && !IsNaturallyCostly(x.B, x.B.Cards[index]))
            return false;
        if (!costsResources)
            return true;
        ref readonly Card card = ref x.B.Cards[index];
        return Kernel.EnergyCostWithModifiers(x.B, x.R, card, Pile.Hand) > 0
            || Kernel.StarCostWithModifiers(x.B, x.R, card, Pile.Hand) > 0;
    }

    /// <summary>
    /// <c>CardEnergyCost.GetWithModifiers(CostModifiers.None)</c>: the current base cost (upgrade
    /// included) without local or global modifiers. A negative (Unplayable) or X cost is not costly.
    /// </summary>
    private static bool IsNaturallyCostly(in Battle battle, in Card card)
        => card.BaseStarCost > 0 || card.BaseCost > 0;
}

/// <summary>
/// <c>IvoryTile</c>: <c>AfterCardPlayed</c> gains Energy when the play's energy value reaches
/// <c>EnergyThreshold</c> (<c>AfterCardPlayedMirrors.HandleIvoryTile</c>; <see cref="Exec.XValue"/>
/// carries <c>CardPlay.Resources.EnergyValue</c>).
/// </summary>
[ContentId("IVORY_TILE")]
public sealed class IvoryTile : RelicDef
{
    // IvoryTile.cs: EnergyVar(1), EnergyVar("EnergyThreshold", 3).
    public override IReadOnlyList<string> VarNames => ["Energy", "EnergyThreshold"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (x.XValue < (int)x.R.RelicVar(Id, 1))
            return;
        Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>IceCream</c>: the player keeps their energy between turns after the first
/// (<c>PersistentRelicSupport.ShouldPlayerResetEnergy</c>: the native relic returns false from turn 2).
/// </summary>
[ContentId("ICE_CREAM")]
public sealed class IceCream : RelicDef
{
    public override bool ShouldPlayerResetEnergy(in Battle battle, Rules rules, Listener self,
        byte player)
        => self.Owner != player || battle.PlayerTurn <= 1;
}

/// <summary>
/// <c>RuinedHelmet</c>: the first positive Strength received this combat is doubled
/// (<c>SimulatedCombatState.ModifyPowerAmountForRelics</c> / <c>ModifyPowerAmountForRuinedHelmet</c>,
/// native <c>TryModifyPowerAmountReceived</c> and <c>AfterModifyingPowerAmountReceived</c>). The
/// used flag lives in <see cref="RelicFlags.UsedThisCombat"/>; the bridge projects
/// <c>RuinedHelmet._usedThisCombat</c>.
/// </summary>
[ContentId("RUINED_HELMET")]
public sealed class RuinedHelmet : RelicDef
{
    public override bool HasCombatState => true;

    public override bool TryModifyPowerAmountReceived(in Battle battle, Rules rules, Listener self,
        ushort powerId, byte target, int amount, out int modified)
    {
        modified = amount;
        if (amount <= 0
            || target != self.Owner
            || !Registry.TryPower("STRENGTH_POWER", out ushort strength)
            || powerId != strength
            || (battle.GetRelic(rules, self.Index).Flags & RelicFlags.UsedThisCombat) != 0)
        {
            return false;
        }
        modified = amount * 2;
        return true;
    }

    public override void AfterModifyingPowerAmountReceived(ref Exec x, Listener self, ushort powerId,
        byte target)
        => x.B.RelicFlags(x.R, self.Index) |= RelicFlags.UsedThisCombat;
}

/// <summary>
/// <c>UnceasingTop</c>: the draw is a kernel special case in
/// <c>SimulatedCombatState.TriggerRelicsAfterHandEmptied</c> (the existing engine does not dispatch a
/// generic relic hook for it), so the definition only makes the relic admissible.
/// </summary>
[ContentId("UNCEASING_TOP")]
public sealed class UnceasingTop : RelicDef;

/// <summary>
/// <c>Bellows</c>: <c>AfterPlayerTurnStart</c> upgrades the hand only when the turn number is &lt;= 1
/// (<c>ContinueRelicsAfterPlayerTurnStart</c>). The player turn 1 start is before the root and turn 2 no
/// longer satisfies the condition.
/// </summary>
[ContentId("BELLOWS")]
public sealed partial class Bellows : RelicDef;

/// <summary>
/// <c>GamblingChip</c>: <c>AfterPlayerTurnStart</c> offers its discard/draw choice only on turn 1
/// (<c>TurnStartChoiceSupport.ResolveDiscardAndDraw</c> from <c>ContinueRelicsAfterPlayerTurnStart</c>).
/// The choice belongs to the player turn start, before the play-phase root.
/// </summary>
[ContentId("GAMBLING_CHIP")]
public sealed class GamblingChip : RelicDef
{
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn > 1 || x.B.HandCount == 0)
            return;
        // The native caller fixes MaxSelect from the hand size before FromHand pauses; only the
        // candidate list is read after the pause.
        int max = x.B.HandCount;
        x.RunSetupPause();
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[x.B.HandCount];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.DiscardAndDraw, Pile.Hand, options,
                0, max, selected, out int chosen))
        {
            return;
        }
        if (chosen == 0)
            return;
        Kernel.DiscardAndDraw(ref x, Pile.Hand, selected[..chosen], chosen);
    }
}

/// <summary>
/// <c>VexingPuzzlebox</c>: <c>AfterPlayerTurnStart</c> generates one free card only when the turn number is
/// 1 (<c>ContinueRelicsAfterPlayerTurnStart</c>), at the player turn start before the play-phase root.
/// </summary>
[ContentId("VEXING_PUZZLEBOX")]
public sealed class VexingPuzzlebox : RelicDef
{
    // Turn-start generation samples the character pool; the pool is part of the play-phase rules
    // closure (the generated card itself also carries its stats in the captured root).
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn > 1)
            return;
        Span<Card> options = stackalloc Card[1];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAll, 1,
            distinct: true, RngStream.CombatCardGeneration, options);
        if (sampled == 0)
            return;
        Card card = options[0];
        Kernel.MakeFreeThisTurn(x.R, ref card);
        Kernel.AddGeneratedCard(ref x, card, Pile.Hand, PilePosition.Bottom);
    }
}

/// <summary><c>FrozenEgg</c>: merchant/card-reward upgrade hooks and <c>IsAllowed</c> only.</summary>
[ContentId("FROZEN_EGG")]
public sealed class FrozenEgg : RelicDef;

/// <summary><c>Girya</c>: <c>AfterRoomEntered</c>, <c>TryModifyRestSiteOptions</c> and <c>IsAllowed</c> only.</summary>
[ContentId("GIRYA")]
public sealed class Girya : RelicDef;

/// <summary><c>Mango</c>: <c>AfterObtained</c> max-HP gain only.</summary>
[ContentId("MANGO")]
public sealed class Mango : RelicDef;

/// <summary>
/// <c>MeatOnTheBone</c>: <c>BeforeCombatStart</c> and <c>AfterCurrentHpChanged</c> only maintain the
/// relic status for the after-victory heal in <c>AfterCombatVictoryEarly</c>. The existing engine's
/// combat-hook catalog marks it NotCombatRelevant, so there is no combat dispatch to mirror.
/// </summary>
[ContentId("MEAT_ON_THE_BONE")]
public sealed partial class MeatOnTheBone : RelicDef;

/// <summary><c>MoltenEgg</c>: merchant/card-reward upgrade hooks and <c>IsAllowed</c> only.</summary>
[ContentId("MOLTEN_EGG")]
public sealed class MoltenEgg : RelicDef;

/// <summary><c>OldCoin</c>: <c>AfterObtained</c> gold gain and <c>IsAllowed</c> only.</summary>
[ContentId("OLD_COIN")]
public sealed class OldCoin : RelicDef;

/// <summary><c>PrayerWheel</c>: <c>TryModifyRewards</c> only.</summary>
[ContentId("PRAYER_WHEEL")]
public sealed class PrayerWheel : RelicDef;

/// <summary><c>Shovel</c>: <c>TryModifyRestSiteOptions</c> and <c>IsAllowed</c> only.</summary>
[ContentId("SHOVEL")]
public sealed class Shovel : RelicDef;

/// <summary><c>TheCourier</c>: <c>ModifyMerchantPrice</c> and <c>ShouldRefillMerchantEntry</c> only.</summary>
[ContentId("THE_COURIER")]
public sealed class TheCourier : RelicDef;

/// <summary><c>ToxicEgg</c>: merchant/card-reward upgrade hooks and <c>IsAllowed</c> only.</summary>
[ContentId("TOXIC_EGG")]
public sealed class ToxicEgg : RelicDef;

/// <summary><c>WhiteBeastStatue</c>: <c>ShouldForcePotionReward</c> and <c>IsAllowed</c> only.</summary>
[ContentId("WHITE_BEAST_STATUE")]
public sealed class WhiteBeastStatue : RelicDef;

/// <summary><c>WhiteStar</c>: <c>TryModifyRewards</c> and <c>IsAllowed</c> only.</summary>
[ContentId("WHITE_STAR")]
public sealed class WhiteStar : RelicDef;

/// <summary>
/// <c>ArtOfWar</c>: <c>AfterCardPlayed</c> marks an attack played this turn; <c>AfterSideTurnEnd</c>
/// moves that mark to last turn; <c>AfterEnergyReset</c> grants its Energy when last turn had no
/// attack (<c>SimulatedCombatState.RecordRelicCardPlayed</c>, <c>CompleteRelicsAfterSideTurnEnd</c>,
/// <c>TriggerRelicsAfterEnergyReset</c>).
/// Counter = any attack played this turn (0/1), Aux = any attack played last turn (0/1).
/// </summary>
[ContentId("ART_OF_WAR")]
public sealed class ArtOfWar : RelicDef
{
    public override bool HasCombatState => true;

    // ArtOfWar.cs: EnergyVar("GainEnergy", 1).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) == CardType.Attack)
            x.B.RelicState(x.R, self.Index).Counter = 1;
    }

    public override void AfterSideTurnEnd(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Aux = relic.Counter;
        relic.Counter = 0;
    }

    public override void AfterEnergyReset(ref Exec x, Listener self)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        if (x.B.PlayerTurn > 1 && relic.Aux == 0)
            Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
        relic.Counter = 0;
        relic.Aux = 0;
    }
}

/// <summary>
/// <c>DemonTongue</c>: on the player's side, the first unblocked damage taken in a turn is healed
/// back; <c>BeforeSideTurnStart</c> resets the latch (<c>AfterDamageReceivedMirrors</c>,
/// <c>RelicPredictionStateSupport.ResetBeforeSideTurnStart</c>).
/// Counter = 1 once triggered this turn.
/// </summary>
[ContentId("DEMON_TONGUE")]
public sealed class DemonTongue : RelicDef
{
    public override bool HasCombatState => true;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (!Kernel.IsInProgress(x.B) || x.B.Side != Side.Player
            || result.Receiver != Limits.PlayerOwner || result.UnblockedDamage <= 0
            || x.B.RelicState(x.R, self.Index).Counter != 0)
            return;
        x.B.RelicState(x.R, self.Index).Counter = 1;
        Kernel.Heal(ref x, Limits.PlayerOwner, result.UnblockedDamage);
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
        => x.B.RelicState(x.R, self.Index).Counter = 0;
}

/// <summary>
/// <c>Kunai</c>: every third attack played this turn applies Dexterity; <c>BeforeSideTurnStart</c>
/// resets the counter (<c>AfterCardPlayedMirrors.HandleKunai</c> and
/// <c>RelicPredictionStateSupport.ResetBeforeSideTurnStart</c>).
/// Counter = attacks played this turn.
/// </summary>
[ContentId("KUNAI")]
public sealed class Kunai : RelicDef
{
    public override bool HasCombatState => true;

    // Kunai.cs: CardsVar(3), PowerVar<DexterityPower>(1m).
    public override IReadOnlyList<string> VarNames => ["Cards", "DexterityPower"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B) || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Counter++;
        // ApplyRelicStatPower declines while the combat is ending; the counter still increments.
        if (relic.Counter % (int)x.R.RelicVar(Id, 0) != 0 || Kernel.IsEnding(x.B))
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<DexterityPower>(),
            (int)x.R.RelicVar(Id, 1), Limits.PlayerOwner);
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
        => x.B.RelicState(x.R, self.Index).Counter = 0;
}

/// <summary>
/// <c>Pocketwatch</c>: counts cards played each turn; <c>BeforeSideTurnStart</c> shifts this turn's
/// count to last turn; <c>ModifyHandDraw</c> adds its Cards while last turn's count did not exceed the
/// threshold (<c>AfterCardPlayedMirrors</c> ignored case,
/// <c>SimulatedCombatState.RecordRelicCardPlayed</c>, <c>PrepareRelicBeforeSideTurnStart</c> and
/// <c>PersistentPowerSupport.GetStatefulRelicHandDrawContribution</c>).
/// Counter = cards played this turn, Aux = cards played last turn.
/// </summary>
[ContentId("POCKETWATCH")]
public sealed class Pocketwatch : RelicDef
{
    public override bool HasCombatState => true;

    // Pocketwatch.cs: DynamicVar("CardThreshold", 3m), CardsVar(3).
    public override IReadOnlyList<string> VarNames => ["CardThreshold", "Cards"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
        => x.B.RelicState(x.R, self.Index).Counter++;

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Aux = relic.Counter;
        relic.Counter = 0;
    }

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
    {
        if (x.B.PlayerTurn == 1 || x.B.RelicState(x.R, self.Index).Aux > x.R.RelicVar(Id, 0))
            return amount;
        return amount + x.R.RelicVar(Id, 1);
    }
}

/// <summary>
/// <c>RainbowRing</c>: counts one attack, Skill and Power per turn; once all three have appeared it
/// applies Strength and Dexterity and stops counting (<c>AfterCardPlayedMirrors.HandleRainbowRing</c>,
/// <c>BeforeSideTurnStart</c> reset). Counter = activation count this turn (the latch), Aux = Powers
/// played this turn; the attack and Skill counts are the kernel's
/// <see cref="Counter.AttacksPlayedThisTurn"/> and <see cref="Counter.SkillCardsPlayedThisTurn"/>
/// (the existing engine's own fields are not read after the latch closes).
/// </summary>
[ContentId("RAINBOW_RING")]
public sealed class RainbowRing : RelicDef
{
    public override bool HasCombatState => true;

    // RainbowRing.cs: PowerVar<StrengthPower>(1m), PowerVar<DexterityPower>(1m).
    public override IReadOnlyList<string> VarNames => ["StrengthPower", "DexterityPower"];

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Counter = 0;
        relic.Aux = 0;
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B) || x.B.RelicState(x.R, self.Index).Counter >= 1)
            return;
        CardType type = Kernel.ResolveCardType(x.B, x.R, play.Card);
        int attacks = x.B.Counter(Counter.AttacksPlayedThisTurn) + (type == CardType.Attack ? 1 : 0);
        int skills = x.B.Counter(Counter.SkillCardsPlayedThisTurn) + (type == CardType.Skill ? 1 : 0);
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        if (type == CardType.Power)
            relic.Aux++;
        if (attacks == 0 || skills == 0 || relic.Aux == 0)
            return;
        if (!Kernel.IsEnding(x.B))
        {
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StrengthPower>(),
                (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<DexterityPower>(),
                (int)x.R.RelicVar(Id, 1), Limits.PlayerOwner);
        }
        relic.Counter = 1;
    }
}

/// <summary>
/// <c>Shuriken</c>: every third attack played this turn applies Strength;
/// <c>BeforeSideTurnStart</c> resets the counter (<c>AfterCardPlayedMirrors.HandleShuriken</c>).
/// Counter = attacks played this turn.
/// </summary>
[ContentId("SHURIKEN")]
public sealed class Shuriken : RelicDef
{
    public override bool HasCombatState => true;

    // Shuriken.cs: CardsVar(3), PowerVar<StrengthPower>(1m).
    public override IReadOnlyList<string> VarNames => ["Cards", "StrengthPower"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B) || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Counter++;
        // ApplyRelicStatPower declines while the combat is ending; the counter still increments.
        if (relic.Counter % (int)x.R.RelicVar(Id, 0) != 0 || Kernel.IsEnding(x.B))
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StrengthPower>(),
            (int)x.R.RelicVar(Id, 1), Limits.PlayerOwner);
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
        => x.B.RelicState(x.R, self.Index).Counter = 0;
}

/// <summary>
/// <c>BeatingRemnant</c>: HP loss from the owner is capped at its MaxHpLoss per player turn and
/// unblocked damage taken accumulates until the next player side turn start
/// (<c>ModifyHpLostMirrors.HandleBeatingRemnant</c>,
/// <c>AfterDamageReceivedMirrors.HandleBeatingRemnant</c>,
/// <c>RelicPredictionStateSupport.ResetBeforeSideTurnStart</c>).
/// Counter = damage received this turn (the engine's decimal state only ever accumulates the
/// integer <c>UnblockedDamage</c>); the bridge projects
/// <c>BeatingRemnantPredictionState.DamageReceivedThisTurn</c>.
/// </summary>
[ContentId("BEATING_REMNANT")]
public sealed class BeatingRemnant : RelicDef
{
    public override bool HasCombatState => true;

    // BeatingRemnant.cs: DynamicVar("MaxHpLoss", 20m).
    public override IReadOnlyList<string> VarNames => ["MaxHpLoss"];

    public override decimal ModifyHpLostAfterOsty(ref Exec x, Listener self, in HpLossContext loss,
        decimal amount)
    {
        if (loss.Target != self.Owner)
            return amount;
        return Math.Min(amount, x.R.RelicVar(Id, 0) - x.B.RelicState(x.R, self.Index).Counter);
    }

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver == self.Owner)
            x.B.RelicState(x.R, self.Index).Counter += result.UnblockedDamage;
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
        => x.B.RelicState(x.R, self.Index).Counter = 0;
}

/// <summary>
/// <c>LizardTail</c>: <c>ShouldDieLate</c> prevents its owner's first death of the combat, then
/// <c>AfterPreventingDeath</c> marks it used and heals Heal percent of max HP
/// (<c>DeathPreventerMirrors.LizardTailMirrors</c>). Counter = WasUsed (0/1); the bridge projects
/// <c>LizardTailPredictionState.WasUsed</c>.
/// </summary>
[ContentId("LIZARD_TAIL")]
public sealed class LizardTail : RelicDef
{
    public override bool HasCombatState => true;

    // LizardTail.cs: HealVar(50m).
    public override IReadOnlyList<string> VarNames => ["Heal"];

    public override bool ShouldDieLate(ref Exec x, Listener self, byte creature)
        => creature != self.Owner || x.B.RelicState(x.R, self.Index).Counter != 0;

    public override void AfterPreventingDeath(ref Exec x, Listener self, byte creature)
    {
        x.B.RelicState(x.R, self.Index).Counter = 1;
        decimal amount = Math.Max(1m, x.B.Creature(creature).MaxHp * (x.R.RelicVar(Id, 0) / 100m));
        int hpBeforeRevive = x.B.Creature(creature).CurrentHp;
        Kernel.Heal(ref x, creature, amount);
        int restored = x.B.Creature(creature).CurrentHp - hpBeforeRevive;
        if (restored > 0)
        {
            x.B.AddCounter(Counter.DeathSaveRelicHpRestored, restored);
            x.B.AddCounter(Counter.DeathSaveUseCount, 1);
        }
    }
}

/// <summary>
/// <c>HelicalDart</c>: after a card with the Shiv tag is played, apply 1 temporary Dexterity
/// (<c>AfterCardPlayedMirrors.HandleHelicalDart</c> → <c>ApplyTemporaryDexterity</c>).
/// </summary>
[ContentId("HELICAL_DART")]
public sealed class HelicalDart : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["DexterityPower"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if ((x.R.Card(play.Card.Id, play.Card.Upgrade).Tags & CardTag.Shiv) == 0)
            return;
        int amount = (int)x.R.RelicVar(Id, 0);
        Kernel.ApplyTemporaryDexterity(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.HelicalDartPower>(), amount, Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>PaperKrane</c>: <c>ModifyWeakMultiplier</c> subtracts 0.15 for powered attacks against the
/// owner. The existing engine routes it through <c>WeakPower</c>'s multiplier; the Sts2Solver kernel reads
/// the relic there, so this definition only declares the rule-table variable.
/// </summary>
[ContentId("PAPER_KRANE")]
public sealed class PaperKrane : RelicDef
{
}
