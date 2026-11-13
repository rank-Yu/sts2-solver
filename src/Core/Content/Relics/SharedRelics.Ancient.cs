using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>IronClub</c>: <c>AfterCardPlayed</c> counts cards toward <c>Cards</c>, then draws 1 when the
/// counter wraps (<c>AfterCardPlayedMirrors.HandleIronClub</c>). Counter = cards played toward the
/// cycle; the bridge projects <c>CounterPredictionState.Value</c>, falling back to
/// <c>IronClub.CardsPlayed</c>.
/// </summary>
[ContentId("IRON_CLUB")]
public sealed class IronClub : RelicDef
{
    public override bool HasCombatState => true;

    // IronClub.cs: CardsVar(4).
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Counter++;
        if (!Kernel.IsInProgress(x.B) || relic.Counter % (int)x.R.RelicVar(Id, 0) != 0)
            return;
        Kernel.Draw(ref x, 1, fromHandDraw: false);
    }
}

/// <summary>
/// <c>Sai</c>: <c>AfterSideTurnStart</c> gains its unpowered block on every player side
/// (<c>SimulatedCombatState.RelicTurnStart.TriggerRelicsAfterSideTurnStart</c>).
/// </summary>
[ContentId("SAI")]
public sealed class Sai : RelicDef
{
    // Sai.cs: BlockVar(7m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>VelvetChoker</c>: <c>ModifyMaxEnergy</c> adds Energy; <c>ShouldPlay</c> forbids playing once
/// Cards cards were played this turn; <c>AfterCardPlayed</c> counts them and
/// <c>BeforeSideTurnStart</c> resets (<c>ShouldPlayMirrors.HandleVelvetChoker</c>,
/// <c>AfterCardPlayedMirrors.HandleVelvetChoker</c>). Counter = cards played this turn, projected from
/// <c>CounterPredictionState.Value</c> with the live field as fallback.
/// </summary>
[ContentId("VELVET_CHOKER")]
public sealed class VelvetChoker : RelicDef
{
    public override bool HasCombatState => true;

    // VelvetChoker.cs: CardsVar(6), EnergyVar(1).
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 1);

    public override bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card)
        => battle.GetRelic(rules, self.Index).Counter < (int)rules.RelicVar(Id, 0);

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
        => x.B.RelicState(x.R, self.Index).Counter++;

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
        => x.B.RelicState(x.R, self.Index).Counter = 0;
}

/// <summary>
/// <c>Fiddle</c>: <c>ModifyHandDraw</c> adds Cards; <c>ShouldDraw</c> refuses a non-hand draw while the
/// player side is current (<c>ShouldDrawMirrors.HandleFiddle</c>). The native
/// <c>AfterPreventingDraw</c> only flashes.
/// </summary>
[ContentId("FIDDLE")]
public sealed class Fiddle : RelicDef
{
    // Fiddle.cs: CardsVar(2).
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);

    public override bool ShouldDraw(ref Exec x, Listener self, bool fromHandDraw)
        => fromHandDraw || x.B.Side != Side.Player;
}

/// <summary>
/// <c>PaelsBlood</c>: <c>ModifyHandDraw</c> adds Cards on every player turn
/// (<c>PersistentPowerSupport</c> native relic contribution).
/// </summary>
[ContentId("PAELS_BLOOD")]
public sealed class PaelsBlood : RelicDef
{
    // PaelsBlood.cs: CardsVar(1).
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);
}

/// <summary>
/// <c>PaelsFlesh</c>: <c>ModifyMaxEnergy</c> adds Energy from player turn 3
/// (<c>PersistentPowerSupport.GetModifiedMaxEnergy</c> native relic contribution).
/// </summary>
[ContentId("PAELS_FLESH")]
public sealed class PaelsFlesh : RelicDef
{
    // PaelsFlesh.cs: EnergyVar(1); the turn threshold 3 is a code constant.
    private const int EnergyFromTurn = 3;

    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => x.B.PlayerTurn < EnergyFromTurn ? amount : amount + x.R.RelicVar(Id, 0);
}

/// <summary>
/// <c>PaelsTears</c>: <c>BeforeSideTurnEnd</c> records leftover energy and <c>AfterSideTurnStart</c>
/// gains Energy while the flag is set (<c>SimulatedCombatState.RelicTurnStart</c>).
/// Counter = HadLeftoverEnergy (<c>StatefulRelicState.Current</c>).
/// </summary>
[ContentId("PAELS_TEARS")]
public sealed class PaelsTears : RelicDef
{
    public override bool HasCombatState => true;

    // PaelsTears.cs: EnergyVar(2).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner))
            return;
        x.B.RelicState(x.R, self.Index).Counter = x.B.Energy > 0 ? 1 : 0;
    }

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.RelicState(x.R, self.Index).Counter == 0)
            return;
        Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>BlessedAntler</c>: <c>ModifyMaxEnergy</c> adds Energy. The native turn-1 Dazed generation runs at
/// the turn start before the play-phase root and is imported; later turns do not satisfy its condition.
/// The rules are compiled at combat start, before that generation, so the card is declared here.
/// </summary>
[ContentId("BLESSED_ANTLER")]
public sealed class BlessedAntler : RelicDef
{
    // BlessedAntler.cs: EnergyVar(1), CardsVar(3).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("DAZED", 0)];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);
}

/// <summary>
/// <c>BloodSoakedRose</c>: <c>ModifyMaxEnergy</c> adds Energy. The Enthralled curse is added to the deck
/// when the relic is obtained, outside combat.
/// </summary>
[ContentId("BLOOD_SOAKED_ROSE")]
public sealed class BloodSoakedRose : RelicDef
{
    // BloodSoakedRose.cs: EnergyVar(1).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);
}

/// <summary>
/// <c>Ectoplasm</c>: <c>ModifyMaxEnergy</c> adds Energy, and <c>ModifyGoldGained</c> zeroes any
/// positive gain, including HandOfGreed's in-combat gain. The native <c>AfterModifyingGoldGained</c>
/// only flashes and has no state.
/// </summary>
[ContentId("ECTOPLASM")]
public sealed class Ectoplasm : RelicDef
{
    // Ectoplasm.cs: EnergyVar(1).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);

    public override decimal ModifyGoldGained(ref Exec x, Listener self, decimal amount)
        => self.Owner == Limits.PlayerOwner ? 0m : amount;
}

/// <summary>
/// <c>PhilosophersStone</c>: <c>ModifyMaxEnergy</c> adds Energy, and every enemy added after the
/// root gains its Strength (<c>AfterCreatureAddedToCombat</c>). The enemies present at the root got
/// that Strength before the captured state and are imported with it.
/// </summary>
[ContentId("PHILOSOPHERS_STONE")]
public sealed partial class PhilosophersStone : RelicDef
{
    // PhilosophersStone.cs: PowerVar&lt;StrengthPower&gt;(1m), EnergyVar(1).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);

    public override void AfterCreatureAddedToCombat(ref Exec x, Listener self, byte creature)
    {
        if (!Kernel.IsEnemyOwner(creature))
            return;
        // The relic's StrengthPower var is not part of the compiled `RelicVar` table (its only
        // canonical var there is Energy), so the importer freezes it in Relic.Aux.
        int strength = x.B.GetRelic(x.R, self.Index).Aux;
        if (strength <= 0)
            return;
        Kernel.ApplyPower(ref x, creature, Registry.PowerId<Powers.StrengthPower>(),
            strength, Limits.NoOwner);
    }
}

/// <summary>
/// <c>PrismaticGem</c>: <c>ModifyMaxEnergy</c> adds Energy; the card-reward pool modifier is out of combat.
/// </summary>
[ContentId("PRISMATIC_GEM")]
public sealed class PrismaticGem : RelicDef
{
    // PrismaticGem.cs: EnergyVar(1).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);
}

/// <summary>
/// <c>Sozu</c>: <c>ModifyMaxEnergy</c> adds Energy and <c>ShouldProcurePotion</c> refuses every potion.
/// </summary>
[ContentId("SOZU")]
public sealed class Sozu : RelicDef
{
    // Sozu.cs: EnergyVar(1).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);

    public override bool ShouldProcurePotion(in Battle battle, ushort relicId, ushort potionId)
        => false;
}

/// <summary>
/// <c>PumpkinCandle</c>: <c>ModifyMaxEnergy</c> adds Energy while KindleCount is positive. Counter =
/// KindleCount, projected from the live model field (the value does not change during combat).
/// </summary>
[ContentId("PUMPKIN_CANDLE")]
public sealed class PumpkinCandle : RelicDef
{
    // PumpkinCandle.cs: EnergyVar(1), DynamicVar("CombatCount", 5m).
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => x.B.GetRelic(x.R, self.Index).Counter > 0 ? amount + x.R.RelicVar(Id, 0) : amount;
}

/// <summary>
/// <c>RunicPyramid</c>: <c>ShouldFlush</c> declines the owner's hand flush on every turn
/// (<c>PersistentRelicSupport.ShouldFlush</c>).
/// </summary>
[ContentId("RUNIC_PYRAMID")]
public sealed class RunicPyramid : RelicDef
{
    public override bool ShouldFlush(ref Exec x, Listener self) => false;
}

/// <summary>
/// <c>VeryHotCocoa</c>: <c>AfterSideTurnStart</c> gains 4 energy when the player turn number is &lt;= 1.
/// A play-phase root is captured after that; a turn 1 setup root (a turn-start choice page) is
/// captured at the native <c>SetupPlayerTurn</c> entry, so the replayed preamble runs it.
/// </summary>
[ContentId("VERY_HOT_COCOA")]
public sealed partial class VeryHotCocoa : RelicDef;

/// <summary><c>LoomingFruit</c>: <c>AfterObtained</c> max-HP gain only.</summary>
[ContentId("LOOMING_FRUIT")]
public sealed class LoomingFruit : RelicDef;

/// <summary>
/// <c>BiiigHug</c>: after a shuffle by its owner, create one Soot into the draw pile at a random
/// position (<c>AfterShuffleMirrors.HandleBiiigHug</c>). <c>AfterObtained</c> is a deck-only effect.
/// </summary>
[ContentId("BIIIG_HUG")]
public sealed class BiiigHug : RelicDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOOT", 0)];

    public override void AfterShuffle(ref Exec x, Listener self, byte shuffler)
    {
        if (shuffler != Limits.PlayerOwner)
            return;
        Card soot = Kernel.CreateCard(x.R, Registry.CardId<Cards.Status.Soot>());
        Kernel.AddGeneratedCard(ref x, soot, Pile.Draw, PilePosition.Random);
    }
}

/// <summary>
/// <c>ChoicesParadox</c>: on the first player turn start, generate <c>Cards</c> distinct character
/// cards, add Retain to each, and add the one chosen from the generated options to hand
/// (<c>SimulatedCombatState.RelicTurnStart</c> case ChoicesParadox / <c>TurnStartChoiceSupport.ResolveGeneratedToHand</c>).
/// </summary>
[ContentId("CHOICES_PARADOX")]
public sealed class ChoicesParadox : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn > 1)
            return;
        int count = (int)x.R.RelicVar(Id, 0);
        if (count <= 0)
            return;
        if (count > Limits.Cards)
            throw new CapacityException("GeneratedCards", Limits.Cards);
        Span<Card> options = stackalloc Card[count];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAll, count,
            distinct: true, RngStream.CombatCardGeneration, options);
        if (sampled == 0)
            return;
        for (int index = 0; index < sampled; index++)
            options[index].AddKeyword(Keywords.Retain);
        int selected = 0;
        if (sampled > 1)
        {
            // CardSelectCmd.FromSimpleGrid auto-selects when cards.Count <= MinSelect.
            if (!Kernel.ChooseOption(ref x, ChoiceEffect.GenerateToHand, options[..sampled],
                    out selected))
            {
                return;
            }
        }
        Kernel.AddGeneratedCard(ref x, options[selected], Pile.Hand, PilePosition.Bottom);
    }
}

/// <summary>
/// <c>Crossbow</c>: at every player side turn start, generate one distinct character Attack, make it
/// free this turn and add it to hand (<c>SimulatedCombatState.RelicTurnStart</c> case Crossbow).
/// </summary>
[ContentId("CROSSBOW")]
public sealed class Crossbow : RelicDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAttacks];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        Span<Card> selected = stackalloc Card[1];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAttacks, 1,
            distinct: true, RngStream.CombatCardGeneration, selected);
        if (sampled == 0)
            return;
        Card card = selected[0];
        Kernel.MakeFreeThisTurn(x.R, ref card);
        Kernel.AddGeneratedCard(ref x, card, Pile.Hand, PilePosition.Bottom);
    }
}

/// <summary>
/// <c>FurCoat</c>: enemies created at a marked map coordinate start at 1 current HP. The marked
/// coordinate is out-of-combat data frozen into <see cref="Rules.FurCoatSpawnHpOne"/>
/// (<c>MonsterSpawnSupport.ApplyCreatureAddedRelics</c>); the relic itself has no combat hook.
/// </summary>
[ContentId("FUR_COAT")]
public sealed partial class FurCoat : RelicDef
{
}

/// <summary>
/// <c>MusicBox</c>: the first Attack played each turn is cloned with Ethereal and added to hand once
/// (<c>BeforeCardPlayedMirrors.HandleMusicBox</c> + <c>AfterCardPlayedMirrors.HandleMusicBox</c>;
/// the per-turn latch is <c>MusicBoxPredictionState.WasUsedThisTurn</c>).
/// </summary>
[ContentId("MUSIC_BOX")]
public sealed class MusicBox : RelicDef
{
    public override bool HasCombatState => true;

    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        if (relic.Counter != 0 || x.MusicBoxPlayIdentity != 0)
            return;
        if (x.R.Card(play.Card.Id, play.Card.Upgrade).Type != CardType.Attack)
            return;
        x.MusicBoxPlayIdentity = x.PlayIdentity;
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        if (x.MusicBoxPlayIdentity == 0 || x.MusicBoxPlayIdentity != x.LastPlayIdentity)
            return;
        x.MusicBoxPlayIdentity = 0;
        Card clone = Kernel.CloneCard(play.Card);
        clone.AddKeyword(Keywords.Ethereal);
        if (!Kernel.AddGeneratedCard(ref x, clone, Pile.Hand, PilePosition.Bottom))
            return;
        if (x.NeedsChoice)
            return;
        relic.Counter = 1;
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player)
            x.B.RelicState(x.R, self.Index).Counter = 0;
    }
}

/// <summary>
/// <c>PaelsEye</c>: at the end of a player turn with no manually played card, exhaust the hand and
/// take an extra turn once per combat. <c>BeforeSideTurnEndEarly</c> exhausts, <c>ShouldTakeExtraTurn</c>
/// grants the turn and <c>AfterTakingExtraTurn</c> sets the used latch
/// (<c>SimulatedCombatState.ReactiveRelics.cs</c> + <c>ExtraTurnMirrors</c>).
/// </summary>
[ContentId("PAELS_EYE")]
public sealed class PaelsEye : RelicDef
{
    public override bool HasCombatState => true;

    public override bool ExtraTurnPriority => true;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        // TriggerRelicsAfterPlayerTurnStart case PaelsEye: state.Previous = 1.
        x.B.RelicState(x.R, self.Index).Aux = 1;
    }

    public override void BeforeSideTurnEndEarly(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (side != Side.Player
            || !Kernel.Participates(participants, Limits.PlayerOwner)
            || !ShouldTrigger(x.B, x.R, x.B.GetRelic(x.R, self.Index)))
            return;
        int handCount = x.B.HandCount;
        Span<int> selected = stackalloc int[handCount];
        for (int index = 0; index < handCount; index++)
            selected[index] = index;
        Kernel.ExhaustSelected(ref x, Pile.Hand, selected);
    }

    public override bool ShouldTakeExtraTurn(ref Exec x, Listener self)
        => ShouldTrigger(x.B, x.R, x.B.GetRelic(x.R, self.Index));

    public override void AfterTakingExtraTurn(ref Exec x, Listener self)
    {
        x.B.RelicState(x.R, self.Index).Counter = 1;
    }

    private bool ShouldTrigger(in Battle battle, Rules rules, in Relic relic)
    {
        if (relic.Counter != 0 || relic.Aux == 0)
            return false;
        if (battle.Counter(Counter.ManualCardsPlayedThisTurn) != 0)
            return false;
        if (battle.PlayerTurn == 1 && HasRelic(battle, rules, "WHISPERING_EARRING"))
            return false;
        return true;
    }

    private static bool HasRelic(in Battle battle, Rules rules, string nativeId)
    {
        if (!Registry.TryRelic(nativeId, out ushort id))
            return false;
        for (int index = 0; index < rules.Relics.Count; index++)
        {
            if (rules.Relics.Id(index) == id && (battle.GetRelic(rules, index).Flags & RelicFlags.Melted) == 0)
                return true;
        }
        return false;
    }
}

/// <summary>
/// <c>PaelsLegion</c>: doubles its owner's card block while the cooldown is down; a block-gaining
/// card play starts a cooldown of Turns. The cooldown ticks down at each player side turn start
/// (<c>ModifyBlockMultiplicativeMirrors</c>, <c>AfterModifyingBlockAmountMirrors</c>,
/// <c>AfterCardPlayedMirrors</c> and <c>RelicPredictionStateSupport.ResetAfterSideTurnStart</c>).
/// </summary>
[ContentId("PAELS_LEGION")]
public sealed class PaelsLegion : RelicDef
{
    public override bool HasCombatState => true;

    public override bool IsPaelsLegion => true;

    public override IReadOnlyList<string> VarNames => ["Turns"];


    public override decimal ModifyBlockMultiplicative(ref Exec x, Listener self, in BlockContext block,
        decimal amount)
    {
        if ((block.Props & ValueProps.Move) == 0 || !block.HasCardSource)
            return 1m;
        Relic relic = x.B.GetRelic(x.R, self.Index);
        return relic.Counter <= 0 && (relic.Aux == 0 || relic.Aux == x.PlayIdentity) ? 2m : 1m;
    }

    public override void AfterModifyingBlockAmount(ref Exec x, Listener self, decimal modified,
        in BlockContext block)
    {
        if (modified <= 0m)
            return;
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        if (relic.Aux != 0 && relic.Aux != x.PlayIdentity)
            return;
        relic.Aux = x.PlayIdentity;
    }

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        if (relic.Aux == 0 || relic.Aux != x.LastPlayIdentity)
            return;
        relic.Aux = 0;
        relic.Counter = (int)x.R.RelicVar(Id, 0);
        x.B.RelicFlags(x.R, self.Index) |= RelicFlags.UsedThisCombat;
    }

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        // RelicPredictionStateSupport.ResetAfterSideTurnStart case PaelsLegion runs before the case
        // switch: cooldown-- and the display latch clears, whatever the current cooldown sign is.
        ref RelicState relic = ref x.B.RelicState(x.R, self.Index);
        relic.Counter--;
        x.B.RelicFlags(x.R, self.Index) &= ~RelicFlags.UsedThisCombat;
    }
}

/// <summary>
/// <c>SpikedGauntlets</c>: +Energy maximum energy, and Power cards cost 1 more
/// (<c>ModifyMaxEnergy</c> and the verified native <c>TryModifyEnergyCostInCombat</c>).
/// </summary>
[ContentId("SPIKED_GAUNTLETS")]
public sealed class SpikedGauntlets : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);

    public override decimal TryModifyEnergyCostInCombat(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
        => rules.Card(card.Id, card.Upgrade).Type == CardType.Power ? amount + 1m : amount;
}

/// <summary>
/// <c>ThrowingAxe</c>: the first card played each combat is played one extra time
/// (<c>ModifyCardPlayCountMirrors.HandleThrowingAxe</c> / <c>HandleThrowingAxeAfter</c>; the latch is
/// <c>ThrowingAxePredictionState.UsedThisCombat</c>, reset out of combat by <c>AfterRoomEntered</c>).
/// </summary>
[ContentId("THROWING_AXE")]
public sealed partial class ThrowingAxe : RelicDef
{
    public override bool HasCombatState => true;

    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount)
        => x.B.RelicState(x.R, self.Index).Counter == 0 ? playCount + 1 : playCount;

    public override void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
    {
        x.B.RelicState(x.R, self.Index).Counter = 1;
    }
}

/// <summary>
/// <c>ToastyMittens</c>: at the player turn start exhaust one card from hand, then apply its Strength
/// variable (<c>SimulatedCombatState.RelicTurnStart</c> case ToastyMittens).
/// </summary>
[ContentId("TOASTY_MITTENS")]
public sealed class ToastyMittens : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        // CardSelectCmd.FromHand is called even with an empty hand; with CardSelectCmd.Selector
        // null it signals the native setup pause before it builds the list. Run the pause first so
        // a card added at side start (Furnace/Crossbow) is a candidate. Outside the setup preamble
        // this is a no-op and the historical EndTurn shortcut is unchanged.
        bool setup = x.RunSetupPause();
        if (x.B.HandCount > 0)
        {
            if (setup || x.B.HandCount > 1)
            {
                Span<int> options = stackalloc int[x.B.HandCount];
                for (int index = 0; index < options.Length; index++)
                    options[index] = index;
                Span<int> selected = stackalloc int[1];
                if (!Kernel.ChooseCards(ref x, ChoiceEffect.Exhaust, Pile.Hand, options, 1,
                        selected, out int chosen))
                {
                    return;
                }
                Kernel.ExhaustSelected(ref x, Pile.Hand, selected[..chosen]);
                if (x.NeedsChoice)
                    return;
            }
            else
            {
                // CardSelectCmd.FromHand auto-selects when the hand has at most MinSelect cards
                // (RequireManualConfirmation is false for the 1-of-1 prompt). Outside the setup
                // preamble the candidate list is not rebuilt after a pause, so it keeps this
                // shortcut; in the preamble the implicit one-option request above records the
                // selection so deferred side-start effects still run first.
                Span<int> only = stackalloc int[1];
                Kernel.ExhaustSelected(ref x, Pile.Hand, only);
                if (x.NeedsChoice)
                    return;
            }
        }
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.StrengthPower>(), (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>SealOfGold</c>: at the player side turn start, if the owner has at least its Gold variable, gain
/// Energy and lose that gold (<c>SimulatedCombatState.RelicTurnStart</c> case SealOfGold).
/// </summary>
[ContentId("SEAL_OF_GOLD")]
public sealed class SealOfGold : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Energy", "Gold"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        int gold = (int)x.R.RelicVar(Id, 1);
        if (x.B.Gold < gold)
            return;
        Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
        if (x.NeedsChoice)
            return;
        x.B.Gold = Math.Max(0, x.B.Gold - gold);
    }
}

/// <summary>
/// <c>SneckoEye</c>: adds its Cards variable to the player's hand draw; ConfusedPower is applied before
/// combat and is already in the root.
/// </summary>
[ContentId("SNECKO_EYE")]
public sealed partial class SneckoEye : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
    {
        return amount + x.R.RelicVar(Id, 0);
    }
}

/// <summary>
/// <c>WhisperingEarring</c>: +Energy maximum energy. Its first-turn auto-play
/// (<c>AfterAutoPrePlayPhaseEnteredLate</c>) runs before the captured root, like Imbued; the relic's
/// presence is also read by PaelsEye's first-turn gate.
/// </summary>
[ContentId("WHISPERING_EARRING")]
public sealed class WhisperingEarring : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => amount + x.R.RelicVar(Id, 0);
}

/// <summary>
/// <c>DelicateFrond</c>: fills the open potion slots at <c>BeforeCombatStart</c>; the potions are in
/// the captured root, so there is no in-combat hook.
/// </summary>
[ContentId("DELICATE_FROND")]
public sealed class DelicateFrond : RelicDef
{
    public override bool SetupOnlyOutOfCombatPotionPool => true;


    private static bool HasOpenPotionSlot(in Battle battle)
    {
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            if (battle.Potions[slot] == 0)
                return true;
        }
        return false;
    }
}

/// <summary>
/// <c>DiamondDiadem</c>: gain 20 Unpowered block and 1 Blur at the first side turn start
/// (<c>AfterSideTurnStart</c> with <c>TurnNumber &lt;= 1</c>).
/// A play-phase root is captured after that; a turn 1 setup root (a turn-start choice page) is
/// captured at the native <c>SetupPlayerTurn</c> entry, so the replayed preamble runs it.
/// </summary>
[ContentId("DIAMOND_DIADEM")]
public sealed class DiamondDiadem : RelicDef
{
    // DiamondDiadem.cs: BlockVar(20, Unpowered) then 1 Blur, turn number <= 1.
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, 20m, ValueProps.Unpowered, fromCard: false);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<BlurPower>(), 1, Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>RadiantPearl</c>: adds its Cards Luminesce cards to hand at the first <c>BeforeHandDraw</c>,
/// before the captured root; the cards are already in the root hand and have their own definition.
/// </summary>
[ContentId("RADIANT_PEARL")]
public sealed class RadiantPearl : RelicDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("LUMINESCE", 0), ("LUMINESCE", 1)];

}

// --- rewards, shops, rest sites, map and pickup effects --------------------------------
[ContentId("ALCHEMICAL_COFFER")] public sealed class AlchemicalCoffer : RelicDef; // AfterObtained / TryModifyRewards

[ContentId("ARCANE_SCROLL")] public sealed class ArcaneScroll : RelicDef; // AfterObtained

[ContentId("ARCHAIC_TOOTH")] public sealed class ArchaicTooth : RelicDef; // AfterCloned / AfterObtained

[ContentId("ASTROLABE")] public sealed class Astrolabe : RelicDef; // AfterObtained

[ContentId("BEAUTIFUL_BRACELET")] public sealed class BeautifulBracelet : RelicDef; // AfterObtained

[ContentId("BLACK_STAR")] public sealed class BlackStar : RelicDef; // TryModifyRewards

[ContentId("CALLING_BELL")] public sealed class CallingBell : RelicDef; // AfterObtained

[ContentId("CLAWS")] public sealed class Claws : RelicDef; // AfterObtained

[ContentId("CURSED_PEARL")] public sealed class CursedPearl : RelicDef; // AfterObtained

[ContentId("DISTINGUISHED_CAPE")] public sealed class DistinguishedCape : RelicDef; // AfterObtained

[ContentId("DOWSING_ROD")] public sealed class DowsingRod : RelicDef; // AfterObtained / TryModifyRewards

[ContentId("DRIFTWOOD")] public sealed class Driftwood : RelicDef; // TryModifyRewardsLate

[ContentId("DUSTY_TOME")] public sealed class DustyTome : RelicDef; // AfterObtained

[ContentId("ELECTRIC_SHRYMP")] public sealed class ElectricShrymp : RelicDef; // AfterObtained

[ContentId("EMPTY_CAGE")] public sealed class EmptyCage : RelicDef; // AfterObtained

[ContentId("FISHING_ROD")] public sealed class FishingRod : RelicDef; // AfterCombatEnd

[ContentId("GLASS_EYE")] public sealed class GlassEye : RelicDef; // AfterObtained

[ContentId("GLITTER")] public sealed class Glitter : RelicDef; // TryModifyCardRewardOptionsLate

[ContentId("GOLDEN_COMPASS")] public sealed class GoldenCompass : RelicDef; // AfterObtained / map

[ContentId("GOLDEN_PEARL")] public sealed class GoldenPearl : RelicDef; // AfterObtained

[ContentId("HEFTY_TABLET")] public sealed class HeftyTablet : RelicDef; // AfterObtained

[ContentId("JEWELRY_BOX")] public sealed class JewelryBox : RelicDef; // AfterObtained

[ContentId("KALEIDOSCOPE")] public sealed class Kaleidoscope : RelicDef; // AfterObtained / IsAllowedAtNeow

[ContentId("LARGE_CAPSULE")] public sealed class LargeCapsule : RelicDef; // AfterObtained

[ContentId("LAVA_ROCK")] public sealed class LavaRock : RelicDef; // TryModifyRewards

[ContentId("LEAD_PAPERWEIGHT")] public sealed class LeadPaperweight : RelicDef; // AfterObtained

[ContentId("LEAFY_POULTICE")] public sealed class LeafyPoultice : RelicDef; // AfterObtained

[ContentId("LORDS_PARASOL")] public sealed class LordsParasol : RelicDef; // AfterRoomEntered (merchant)

[ContentId("LOST_COFFER")] public sealed class LostCoffer : RelicDef; // AfterObtained

[ContentId("MASSIVE_SCROLL")] public sealed class MassiveScroll : RelicDef; // IsAllowed / AfterObtained

[ContentId("MEAT_CLEAVER")] public sealed class MeatCleaver : RelicDef; // TryModifyRestSiteOptions

[ContentId("NEOWS_BONES")] public sealed class NeowsBones : RelicDef; // AfterObtained

[ContentId("NEOWS_SACRIFICE")] public sealed class NeowsSacrifice : RelicDef; // AfterObtained

[ContentId("NEOWS_TALISMAN")] public sealed class NeowsTalisman : RelicDef; // AfterObtained

[ContentId("NEOWS_TORMENT")] public sealed class NeowsTorment : RelicDef; // AfterObtained

[ContentId("NEW_LEAF")] public sealed class NewLeaf : RelicDef; // AfterObtained

[ContentId("NUTRITIOUS_OYSTER")] public sealed class NutritiousOyster : RelicDef; // AfterObtained

[ContentId("NUTRITIOUS_SOUP")] public sealed class NutritiousSoup : RelicDef; // AfterObtained

[ContentId("PAELS_CLAW")] public sealed class PaelsClaw : RelicDef; // AfterObtained (deck enchantment)

[ContentId("PAELS_GROWTH")] public sealed class PaelsGrowth : RelicDef; // AfterObtained / rest site

[ContentId("PAELS_HORN")] public sealed class PaelsHorn : RelicDef; // AfterObtained

[ContentId("PAELS_TOOTH")] public sealed class PaelsTooth : RelicDef; // rewards / AfterObtained

[ContentId("PAELS_WING")] public sealed class PaelsWing : RelicDef; // TryModifyCardRewardAlternatives

[ContentId("PANDORAS_BOX")] public sealed class PandorasBox : RelicDef; // AfterObtained

[ContentId("PHIAL_HOLSTER")] public sealed class PhialHolster : RelicDef; // AfterObtained

[ContentId("POMANDER")] public sealed class Pomander : RelicDef; // AfterObtained

[ContentId("PRECARIOUS_SHEARS")] public sealed class PrecariousShears : RelicDef; // AfterObtained

[ContentId("PRECISE_SCISSORS")] public sealed class PreciseScissors : RelicDef; // AfterObtained

[ContentId("PRESERVED_FOG")] public sealed class PreservedFog : RelicDef; // AfterObtained

[ContentId("SAND_CASTLE")] public sealed class SandCastle : RelicDef; // AfterObtained

[ContentId("SCROLL_BOXES")] public sealed class ScrollBoxes : RelicDef; // AfterObtained / IsAllowedAtNeow

[ContentId("SEA_GLASS")] public sealed class SeaGlass : RelicDef; // AfterObtained

[ContentId("SERE_TALON")] public sealed class SereTalon : RelicDef; // AfterObtained

[ContentId("SIGNET_RING")] public sealed class SignetRing : RelicDef; // AfterObtained

[ContentId("SILKEN_TRESS")] public sealed class SilkenTress : RelicDef; // TryModifyCardRewardOptionsLate

[ContentId("SILVER_CRUCIBLE")] public sealed class SilverCrucible : RelicDef; // rewards / IsAllowed

[ContentId("SMALL_CAPSULE")] public sealed class SmallCapsule : RelicDef; // AfterObtained

[ContentId("STONE_HUMIDIFIER")] public sealed class StoneHumidifier : RelicDef; // AfterRestSiteHeal

[ContentId("STORYBOOK")] public sealed class Storybook : RelicDef; // AfterObtained

[ContentId("TANXS_WHISTLE")] public sealed class TanxsWhistle : RelicDef; // AfterObtained

[ContentId("TOUCH_OF_OROBAS")] public sealed class TouchOfOrobas : RelicDef; // AfterCloned / AfterObtained

[ContentId("TOY_BOX")] public sealed class ToyBox : RelicDef; // AfterObtained / AfterCombatEnd

[ContentId("TRI_BOOMERANG")] public sealed class TriBoomerang : RelicDef; // AfterObtained

[ContentId("WAR_HAMMER")] public sealed class WarHammer : RelicDef; // AfterCombatVictory

[ContentId("WINGED_BOOTS")] public sealed class WingedBoots : RelicDef; // map / IsAllowed

[ContentId("YUMMY_COOKIE")] public sealed class YummyCookie : RelicDef; // AfterObtained

/// <summary>
/// <c>BoomingConch</c>: on turn 1 in an elite room, add its <c>Cards</c> to the hand draw and gain its
/// <c>Energy</c> at the player side turn start. The room type is carried by the opening
/// <see cref="Exec"/>; later turns and rooms leave the base draw alone.
/// </summary>
[ContentId("BOOMING_CONCH")]
public sealed class BoomingConch : RelicDef
{

}

/// <summary>
/// <c>JeweledMask</c>: on turn 1, before the hand draw, move one random Power card from the draw pile
/// to the hand, preferring Power cards without Innate, and make it free this turn.
/// </summary>
[ContentId("JEWELED_MASK")]
public sealed class JeweledMask : RelicDef
{
    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn > 1)
            return;
        int drawStart = Piles.Start(x.B, Pile.Draw);
        int drawCount = x.B.DrawCount;
        if (drawCount == 0)
            return;
        Span<int> candidates = stackalloc int[drawCount];
        int count = 0;
        for (int index = 0; index < drawCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[drawStart + index];
            if (x.R.Card(card.Id, card.Upgrade).Type == CardType.Power)
                candidates[count++] = index;
        }
        if (count == 0)
            return;
        int preferred = 0;
        for (int index = 0; index < count; index++)
        {
            ref readonly Card card = ref x.B.Cards[drawStart + candidates[index]];
            if ((card.Keywords & Keywords.Innate) == 0)
                candidates[preferred++] = candidates[index];
        }
        if (preferred > 0)
            count = preferred;
        int pick = x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, count);
        int position = candidates[pick];
        ref Card selected = ref x.B.Cards[drawStart + position];
        Kernel.MakeFreeThisCombat(x.R, ref selected);
        Kernel.MoveCard(ref x.B, Pile.Draw, position, Pile.Hand, PilePosition.Bottom);
    }
}
