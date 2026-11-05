using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>UndyingSigil</c>: powered attacks from a doomed creature against its owner are multiplied by
/// DamageDecrease (<c>ModifyDamageMirrors.HandleUndyingSigil</c>).
/// </summary>
[ContentId("UNDYING_SIGIL")]
public sealed class UndyingSigil : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["DamageDecrease"];

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
    {
        if (damage.Dealer == Limits.NoOwner
            || damage.Dealer == Limits.PlayerOwner
            || damage.Target != Limits.PlayerOwner
            || !damage.Props.IsPoweredAttack())
            return 1m;
        ushort doom = Registry.PowerId<Powers.DoomPower>();
        return x.B.Creature(damage.Dealer).CurrentHp <= Kernel.GetAmount(x.B, damage.Dealer, doom)
            ? x.R.RelicVar(Id, 0)
            : 1m;
    }
}

/// <summary>
/// <c>NinjaScroll</c>: adds 3 Shivs to hand at the first <c>BeforeHandDraw</c>, before the captured
/// root; the Shivs are already in the root hand.
/// </summary>
[ContentId("NINJA_SCROLL")]
public sealed class NinjaScroll : RelicDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SHIV", 0)];

}

/// <summary>
/// <c>Bread</c>: <c>ModifyMaxEnergy</c> adds 1 on every turn except turn 1
/// (<c>PersistentPowerSupport.GetTurnBasedMaxEnergyContribution</c>). The native
/// <c>AfterSideTurnStart</c> lose-2-energy case is turn 1 only and belongs to the combat-start turn,
/// before the root.
/// </summary>
[ContentId("BREAD")]
public sealed partial class Bread : RelicDef
{
    // Bread.cs: EnergyVar("GainEnergy", 1).
    public override IReadOnlyList<string> VarNames => ["GainEnergy"];

    public override decimal ModifyMaxEnergy(ref Exec x, Listener self, decimal amount)
        => x.B.PlayerTurn == 1 ? amount : amount + x.R.RelicVar(Id, 0);
}

/// <summary>
/// <c>ScreamingFlagon</c>: <c>BeforeSideTurnEnd</c>, with an empty hand, deals 20 unpowered damage to
/// every hittable enemy (<c>BeforeSideTurnEndMirrors.HandleScreamingFlagon</c>).
/// </summary>
[ContentId("SCREAMING_FLAGON")]
public sealed class ScreamingFlagon : RelicDef
{
    // ScreamingFlagon.cs: DamageVar(20m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner) || x.B.HandCount != 0)
            return;
        CardEffects.DamageAllEnemies(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>Brimstone</c>: <c>AfterSideTurnStart</c> on every player turn applies 2 Strength to the player and
/// 1 Strength to every living opponent (<c>TriggerRelicsAfterSideTurnStart</c>).
/// </summary>
[ContentId("BRIMSTONE")]
public sealed class Brimstone : RelicDef
{
    // Brimstone.cs: PowerVar<StrengthPower>("SelfStrength", 2m), ("EnemyStrength", 1m).
    public override IReadOnlyList<string> VarNames => ["SelfStrength", "EnemyStrength"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        ushort strength = Registry.PowerId<StrengthPower>();
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, strength, (int)x.R.RelicVar(Id, 0),
            Limits.PlayerOwner);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, owner))
                continue;
            Kernel.ApplyPower(ref x, owner, strength, (int)x.R.RelicVar(Id, 1), Limits.NoOwner);
        }
    }
}

/// <summary>
/// <c>RingingTriangle</c>: <c>ShouldFlush</c> declines the owner's hand flush on player turn 1 only
/// (<c>PersistentRelicSupport.ShouldFlush</c> uses the simulated turn number).
/// </summary>
[ContentId("RINGING_TRIANGLE")]
public sealed class RingingTriangle : RelicDef
{
    public override bool ShouldFlush(ref Exec x, Listener self) => x.B.PlayerTurn > 1;
}

/// <summary>
/// <c>TheAbacus</c>: <c>AfterShuffle</c> gains its unpowered block when the shuffling player is its
/// owner (<c>AfterShuffleMirrors.HandleTheAbacus</c>).
/// </summary>
[ContentId("THE_ABACUS")]
public sealed class TheAbacus : RelicDef
{
    // TheAbacus.cs: BlockVar(6m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterShuffle(ref Exec x, Listener self, byte shuffler)
    {
        if (self.Owner != Limits.PlayerOwner || shuffler != Limits.PlayerOwner)
            return;
        Kernel.GainBlock(ref x, self.Owner, x.R.RelicVar(Id, 0), ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>ChemicalX</c>: <c>ModifyXValue</c> adds its <c>Increase</c> variable to an X value read by its
/// owner's card (<c>CardModel.ResolveEnergyXValue</c>); the native <c>BeforeCardPlayed</c> only flashes.
/// </summary>
[ContentId("CHEMICAL_X")]
public sealed class ChemicalX : RelicDef
{
    // ChemicalX.cs: DynamicVar("Increase", 2m).
    public override IReadOnlyList<string> VarNames => ["Increase"];

    public override int ModifyXValue(in Battle battle, Rules rules, Listener self, in Card card,
        int value)
    {
        if (self.Owner != Limits.PlayerOwner)
            return value;
        return value + (int)rules.RelicVar(Id, 0);
    }
}

/// <summary>
/// <c>GhostSeed</c>: a Basic Strike or Defend that enters combat without Ethereal gains it
/// (<c>SimulatedCombatState.CardEventHistory.AfterCardEnteredCombat</c> /
/// <c>GhostSeedMirrors.AfterCardEnteredCombat</c>).
/// </summary>
[ContentId("GHOST_SEED")]
public sealed partial class GhostSeed : RelicDef
{
    public override void AfterCardEnteredCombat(ref Exec x, Listener self, Pile pile, int index,
        in Card card)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        CardStats stats = x.R.Card(card.Id, card.Upgrade);
        if (stats.Rarity != CardRarity.Basic
            || (stats.Tags & (CardTag.Strike | CardTag.Defend)) == 0)
        {
            return;
        }
        ref Card stored = ref Piles.At(ref x.B, pile, index);
        if ((stored.Keywords & Keywords.Ethereal) == 0)
            stored.Keywords |= Keywords.Ethereal;
    }
}

/// <summary>
/// <c>SlingOfCourage</c>: <c>AfterRoomEntered</c> applies Strength only for an elite room, during combat
/// setup, and has no later hook.
/// </summary>
[ContentId("SLING_OF_COURAGE")]
public sealed partial class SlingOfCourage : RelicDef;

/// <summary>
/// <c>Toolbox</c>: <c>BeforeHandDraw</c> offers its colourless card choice only on turn 1
/// (<c>SimulatedCombatState.Relics.ContinueRelicsBeforeHandDraw</c>), i.e. at the player turn start
/// before the play-phase root. Native <c>Toolbox.BeforeHandDraw</c> generates
/// <c>DynamicVars.Cards.IntValue</c> distinct combat cards from
/// <c>ColorlessCardPool.GetUnlockedCards(player.UnlockState, CardMultiplayerConstraint)</c> with the
/// <c>CombatCardGeneration</c> RNG, asks for one, then adds it to hand
/// [GAME MegaCrit/Sts2/Core/Models/Relics/Toolbox.cs:20-34].
/// </summary>
[ContentId("TOOLBOX")]
public sealed class Toolbox : RelicDef
{
    /// <summary>
    /// <c>Toolbox.CanonicalVars</c> is <c>new CardsVar(3)</c> (GAME Toolbox.cs:20). The value is not
    /// exposed through <see cref="RelicDef.VarNames"/>: the frozen <c>config.br</c> for the setup
    /// fixtures has <c>relicVars[TOOLBOX]=missing</c>, and adding the name would change every root's
    /// <c>Rules</c> bytes. The native model has no runtime writer for this variable, so the
    /// canonical value is exact.
    /// </summary>
    private const int Cards = 3;

    // The pool row already exists in the frozen rules for TOOLBOX roots. It is compiled from the
    // same GetUnlockedCards + FilterForCombat path that the native generation filters again.
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.ColorlessAll];

    public override bool MayGenerateIdentityContent => true;

    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner || x.B.PlayerTurn > 1)
            return;
        Span<Card> options = stackalloc Card[Cards];
        // CardFactory.GetDistinctForCombat: FilterForPlayerCount + FilterForCombat then
        // TakeRandom(CombatCardGeneration), which is a full Fisher-Yates over the filtered list.
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.ColorlessAll, Cards,
            distinct: true, RngStream.CombatCardGeneration, options);
        if (sampled == 0)
            return;
        // CardSelectCmd.FromChooseACardScreen with min 0 / max 1. The existing engine records the
        // supported one-of choice as min 1 / max 1 GenerateToHand (the frozen setup roots have one
        // root per option), and auto-selects when only one card was generated.
        int selected = 0;
        if (sampled > 1)
        {
            Span<int> selectedIndexes = stackalloc int[1];
            if (!Kernel.ChooseGeneratedCardsInRange(ref x, ChoiceEffect.GenerateToHand,
                    options[..sampled], minCount: 1, maxCount: 1, selectedIndexes,
                    out int selectedCount))
            {
                return;
            }
            if (selectedCount != 1)
                throw new InvalidOperationException("Toolbox 的选牌没有选中一张。");
            selected = selectedIndexes[0];
        }
        Kernel.AddGeneratedCard(ref x, options[selected], Pile.Hand, PilePosition.Bottom);
    }
}

/// <summary><c>Cauldron</c>: <c>AfterObtained</c> potion rewards only.</summary>
[ContentId("CAULDRON")]
public sealed class Cauldron : RelicDef;

/// <summary><c>DingyRug</c>: <c>ModifyCardRewardCreationOptions</c> only.</summary>
[ContentId("DINGY_RUG")]
public sealed class DingyRug : RelicDef;

/// <summary><c>DollysMirror</c>: <c>AfterObtained</c> card duplication only.</summary>
[ContentId("DOLLYS_MIRROR")]
public sealed class DollysMirror : RelicDef;

/// <summary>
/// <c>DragonFruit</c>: <c>AfterGoldGained</c> gains its <c>MaxHp</c> variable and heals the same
/// amount (<c>CreatureCmd.GainMaxHp</c>). HandOfGreed's in-combat gain therefore raises the player's
/// max HP during combat.
/// </summary>
[ContentId("DRAGON_FRUIT")]
public sealed class DragonFruit : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["MaxHp"];

    public override void AfterGoldGained(ref Exec x, Listener self)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        Kernel.GainMaxHp(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0));
    }
}

/// <summary><c>GnarledHammer</c>: <c>AfterObtained</c> card upgrades only.</summary>
[ContentId("GNARLED_HAMMER")]
public sealed class GnarledHammer : RelicDef;

/// <summary><c>Kifuda</c>: <c>AfterObtained</c> only.</summary>
[ContentId("KIFUDA")]
public sealed class Kifuda : RelicDef;

/// <summary>
/// <c>LavaLamp</c>: <c>AfterDamageReceived</c> records whether the player took unblocked,
/// blockable damage this combat (<c>TookDamageThisCombat</c>), consumed by
/// <c>TryModifyCardRewardOptionsLate</c> after combat. The flag does not change combat resolution;
/// <c>Relic.Counter</c> carries it to the write-back. <c>AfterRoomEntered</c> resets the native flag,
/// which the fresh combat's zero counter represents.
/// </summary>
[ContentId("LAVA_LAMP")]
public sealed class LavaLamp : RelicDef
{
    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != Limits.PlayerOwner
            || result.UnblockedDamage <= 0
            || (result.Props & ValueProps.Unblockable) != 0)
        {
            return;
        }
        x.B.Relics[self.Index].Counter = 1;
    }
}

/// <summary><c>LeesWaffle</c>: <c>AfterObtained</c> max-HP gain only.</summary>
[ContentId("LEES_WAFFLE")]
public sealed class LeesWaffle : RelicDef;

/// <summary><c>MembershipCard</c>: <c>ModifyMerchantPrice</c> only.</summary>
[ContentId("MEMBERSHIP_CARD")]
public sealed class MembershipCard : RelicDef;

/// <summary><c>MiniatureTent</c>: <c>ShouldDisableRemainingRestSiteOptions</c> only.</summary>
[ContentId("MINIATURE_TENT")]
public sealed class MiniatureTent : RelicDef;

/// <summary><c>Orrery</c>: <c>AfterObtained</c> card rewards only.</summary>
[ContentId("ORRERY")]
public sealed class Orrery : RelicDef;

/// <summary><c>PunchDagger</c>: <c>AfterObtained</c> only.</summary>
[ContentId("PUNCH_DAGGER")]
public sealed class PunchDagger : RelicDef;

/// <summary><c>RoyalStamp</c>: <c>AfterObtained</c> only.</summary>
[ContentId("ROYAL_STAMP")]
public sealed class RoyalStamp : RelicDef;

/// <summary><c>WingCharm</c>: <c>TryModifyCardRewardOptionsLate</c> only.</summary>
[ContentId("WING_CHARM")]
public sealed class WingCharm : RelicDef;

/// <summary>
/// <c>BurningSticks</c>: the first Skill exhausted in the combat puts a clone into the hand
/// (<c>AfterCardExhaustedMirrors.HandleBurningSticks</c>).
/// Counter = 1 once used this combat.
/// </summary>
[ContentId("BURNING_STICKS")]
public sealed partial class BurningSticks : RelicDef
{
    public override void AfterCardExhausted(ref Exec x, Listener self, in Card card, bool causedByEthereal)
    {
        if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Skill || x.B.Relics[self.Index].Counter != 0)
            return;
        if (!Kernel.AddGeneratedCard(ref x, Kernel.CloneCard(card), Pile.Hand))
            return;
        x.B.Relics[self.Index].Counter = 1;
    }
}

/// <summary>
/// <c>BeltBuckle</c>: applies 2 Dexterity when the owner has no potions. The pre-combat application
/// is already in the captured root; <c>AfterPotionUsed</c> reapplies it once when the last potion is
/// used (<c>SimulatedCombatState.TriggerRelicsAfterPotionUsed</c>, gated by the relic's
/// StatefulRelicState.Current flag).
/// </summary>
[ContentId("BELT_BUCKLE")]
public sealed partial class BeltBuckle : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["DexterityPower"];

    public override void AfterPotionUsed(ref Exec x, Listener self, ushort potionId)
    {
        if (x.B.Relics[self.Index].Counter != 0 || HasAvailablePotion(x.B))
            return;
        x.B.Relics[self.Index].Counter = 1;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.DexterityPower>(), (int)x.R.RelicVar(Id, 0), Limits.NoOwner);
    }

    private static bool HasAvailablePotion(in Battle battle)
    {
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            if (battle.Potions[slot] != 0)
                return true;
        }
        return false;
    }
}
