using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>TheBoot</c>: <c>ModifyHpLostAfterOstyLate</c> raises a powered attack from the owner to another
/// creature to at least <c>DamageMinimum</c> when the truncated amount is between 1 and below it
/// (<c>ModifyHpLostMirrors</c> native fallback). Osty is rejected at admission, so only the owner can
/// deal the attack.
/// </summary>
[ContentId("THE_BOOT")]
public sealed class TheBoot : RelicDef
{
    // TheBoot.cs: DynamicVar("DamageMinimum", 5m); _damageMinimum and DamageThreshold are constants and
    // the threshold is not read by the hook.
    public override IReadOnlyList<string> VarNames => ["DamageMinimum"];

    public override decimal ModifyHpLostAfterOstyLate(ref Exec x, Listener self, in HpLossContext loss,
        decimal amount)
    {
        // TheBoot.cs: dealer == Owner.Creature || dealer == Owner.Osty.
        if (!Kernel.IsOwnerOrOsty(self.Owner, loss.Dealer) || loss.Target == self.Owner)
            return amount;
        if (!loss.Props.IsPoweredAttack() || amount < 1m)
            return amount;
        decimal minimum = x.R.RelicVar(Id, 0);
        return amount >= minimum ? amount : minimum;
    }
}

/// <summary>
/// <c>FakeStrikeDummy</c>: <c>ModifyDamageAdditive</c> adds <c>ExtraDamage</c> to a powered attack whose
/// card has the Strike tag (<c>ModifyDamageMirrors</c> native fallback).
/// </summary>
[ContentId("FAKE_STRIKE_DUMMY")]
public sealed class FakeStrikeDummy : RelicDef
{
    // FakeStrikeDummy.cs: DynamicVar("ExtraDamage", 1m).
    public override IReadOnlyList<string> VarNames => ["ExtraDamage"];

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage,
        decimal amount)
    {
        if (!damage.Props.IsPoweredAttack() || !damage.HasCardSource)
            return 0m;
        return (x.R.Card(damage.CardSource.Id, damage.CardSource.Upgrade).Tags & CardTag.Strike) != 0
            ? x.R.RelicVar(Id, 0)
            : 0m;
    }
}

/// <summary>
/// <c>ForgottenSoul</c>: <c>AfterCardExhausted</c> deals its Damage to one random hittable enemy
/// (<c>AfterCardExhaustedMirrors.HandleForgottenSoul</c>).
/// </summary>
[ContentId("FORGOTTEN_SOUL")]
public sealed class ForgottenSoul : RelicDef
{
    // ForgottenSoul.cs: DamageVar(1m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void AfterCardExhausted(ref Exec x, Listener self, in Card card,
        bool causedByEthereal)
    {
        CardEffects.DamageRandomEnemy(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
        Kernel.ApplyEnemyDeathPowers(ref x);
    }
}

/// <summary>
/// <c>HandDrill</c>: <c>AfterBlockBroken</c> applies Vulnerable when the owner breaks a non-player
/// creature's block (<c>AfterBlockBrokenMirrors.HandleHandDrill</c>). Osty is rejected at admission.
/// </summary>
[ContentId("HAND_DRILL")]
public sealed class HandDrill : RelicDef
{
    // HandDrill.cs: PowerVar&lt;VulnerablePower&gt;(2m).
    public override IReadOnlyList<string> VarNames => ["VulnerablePower"];

    public override void AfterBlockBroken(ref Exec x, Listener self, byte target, byte breaker)
    {
        // HandDrill.cs: breaker == Owner.Creature || breaker.PetOwner == Owner.
        if (!Kernel.IsOwnerOrOsty(self.Owner, breaker) || target == Limits.PlayerOwner)
            return;
        Kernel.ApplyPower(ref x, target, Registry.PowerId<VulnerablePower>(),
            (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>DaughterOfTheWind</c>: <c>AfterCardPlayed</c>, after an Attack, gains its unpowered block
/// (<c>AfterCardPlayedMirrors.HandleDaughterOfTheWind</c>).
/// </summary>
[ContentId("DAUGHTER_OF_THE_WIND")]
public sealed class DaughterOfTheWind : RelicDef
{
    // DaughterOfTheWind.cs: BlockVar(1m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
            ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>LostWisp</c>: <c>AfterCardPlayed</c>, after a Power, deals its unpowered damage to every hittable
/// enemy (<c>AfterCardPlayedMirrors.HandleLostWisp</c>).
/// </summary>
[ContentId("LOST_WISP")]
public sealed class LostWisp : RelicDef
{
    // LostWisp.cs: DamageVar(8m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (!Kernel.IsInProgress(x.B)
            || Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Power)
            return;
        CardEffects.DamageAllEnemies(ref x, x.R.RelicVar(Id, 0), ValueProps.Unpowered,
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>MrStruggles</c>: <c>AfterPlayerTurnStart</c> deals unpowered damage equal to the player turn
/// number to every hittable enemy (existing-engine relic batch, <c>ContinueRelicsAfterPlayerTurnStart</c>).
/// </summary>
[ContentId("MR_STRUGGLES")]
public sealed class MrStruggles : RelicDef
{
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
        => CardEffects.DamageAllEnemies(ref x, x.B.PlayerTurn, ValueProps.Unpowered,
            Limits.PlayerOwner);
}

/// <summary>
/// <c>FakeHappyFlower</c>: <c>AfterSideTurnStart</c> advances the cycle and grants Energy on wrap
/// (<c>TriggerRelicsAfterSideTurnStart</c>). Counter = turns seen (the engine's
/// <c>StatefulRelicState.Current</c>).
/// </summary>
[ContentId("FAKE_HAPPY_FLOWER")]
public sealed class FakeHappyFlower : RelicDef
{
    // FakeHappyFlower.cs: EnergyVar(1), DynamicVar("Turns", 5m).
    public override IReadOnlyList<string> VarNames => ["Energy", "Turns"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || Kernel.IsOverOrEnding(x.B))
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter = (relic.Counter + 1) % (int)x.R.RelicVar(Id, 1);
        if (relic.Counter == 0)
            Kernel.GainEnergy(ref x, x.R.RelicVar(Id, 0));
    }
}

/// <summary>
/// <c>FakeVenerableTeaSet</c>: <c>AfterEnergyReset</c> grants Energy once while the pending flag is
/// set (<c>TriggerRelicsAfterEnergyReset</c>). Counter = the engine's
/// <c>StatefulRelicState.Current</c> (GainEnergyInNextCombat).
/// </summary>
[ContentId("FAKE_VENERABLE_TEA_SET")]
public sealed class FakeVenerableTeaSet : RelicDef
{
    // FakeVenerableTeaSet.cs: EnergyVar(1).
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
/// <c>FakeOrichalcum</c>: <c>BeforeSideTurnEndVeryEarly</c> latches when the player has no block;
/// <c>BeforeSideTurnEnd</c> spends the latch for its Block; <c>BeforeSideTurnStart</c> clears it
/// (<c>OrichalcumMirrors</c>). Counter = the latched trigger, projected from the same
/// <c>OrichalcumMirrors</c> store entry as Orichalcum.
/// </summary>
[ContentId("FAKE_ORICHALCUM")]
public sealed class FakeOrichalcum : RelicDef
{
    // FakeOrichalcum.cs: BlockVar(3m, ValueProp.Unpowered).
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void BeforeSideTurnEndVeryEarly(ref Exec x, Listener self, Side side,
        ushort participants)
    {
        if (Kernel.Participates(participants, self.Owner) && x.B.Creature(self.Owner).Block <= 0)
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
/// <c>PollinousCore</c>: <c>BeforeHandDraw</c> advances TurnsSeen and <c>ModifyHandDraw</c> adds Cards
/// once the cycle completes, then <c>CompleteRelicHandDraw</c> resets it
/// (<c>SimulatedCombatState.Relics.PrepareRelicsBeforeHandDraw</c>,
/// <c>GetStatefulRelicHandDrawContribution</c>, <c>CompleteRelicHandDraw</c>).
/// <see cref="ListenerDef.ModifyHandDraw"/> is evaluated exactly once per turn-start hand draw, so
/// the increment, contribution and reset happen in that one method. Counter = TurnsSeen
/// (<c>StatefulRelicState.Current</c>).
/// </summary>
[ContentId("POLLINOUS_CORE")]
public sealed class PollinousCore : RelicDef
{
    // PollinousCore.cs: CardsVar(2), DynamicVar("Turns", 4m).
    public override IReadOnlyList<string> VarNames => ["Cards", "Turns"];

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
    {
        ref Relic relic = ref x.B.Relics[self.Index];
        relic.Counter++;
        if (relic.Counter < (int)x.R.RelicVar(Id, 1))
            return amount;
        relic.Counter = 0;
        return amount + x.R.RelicVar(Id, 0);
    }
}

/// <summary><c>FresnelLens</c>: merchant and card-reward creation hooks only.</summary>
[ContentId("FRESNEL_LENS")]
public sealed class FresnelLens : RelicDef;

/// <summary>
/// <c>HistoryCourse</c>: at the player turn start's AutoPrePlay it clones the last attack played on the
/// previous player turn and auto-plays it (<c>SimulatedCombatState.TriggerScheduledAutoPlays</c> →
/// <c>ContinueHistoryCourse</c>). The clone itself lives in the kernel's AutoPrePlay stage; the relic
/// has no other combat hook. <see cref="Rules.HasHistoryCourse"/> gates the per-turn card slot.
/// </summary>
[ContentId("HISTORY_COURSE")]
public sealed class HistoryCourseRelic : RelicDef
{
}

/// <summary>
/// <c>BingBong</c>: duplicates a card added to the owner's out-of-combat Deck pile
/// (<c>AfterCardChangedPiles</c> with <c>PileType.Deck</c>). Combat piles never produce a Deck-pile
/// event, so it has no hook inside the search window.
/// </summary>
[ContentId("BING_BONG")]
public sealed class BingBong : RelicDef
{
}

/// <summary>
/// <c>DarkstonePeriapt</c>: gains MaxHp when a Curse is added to the owner's Deck pile
/// (<c>AfterCardChangedPiles</c> with <c>PileType.Deck</c>); deck-only, inert in combat.
/// </summary>
[ContentId("DARKSTONE_PERIAPT")]
public sealed class DarkstonePeriapt : RelicDef
{
}

/// <summary>
/// <c>FakeSneckoEye</c>: applies Confused 1 at <c>BeforeCombatStart</c>; the power is in the captured
/// root, so the relic itself has no in-combat hook.
/// </summary>
[ContentId("FAKE_SNECKO_EYE")]
public sealed partial class FakeSneckoEye : RelicDef
{
}

[ContentId("CHOSEN_CHEESE")] public sealed class ChosenCheese : RelicDef; // AfterCombatEnd

[ContentId("DREAM_CATCHER")] public sealed class DreamCatcher : RelicDef; // TryModifyRestSiteHealRewards

[ContentId("EMBER_TEA")] public sealed partial class EmberTea : RelicDef; // AfterRoomEntered Strength

[ContentId("FAKE_LEES_WAFFLE")] public sealed class FakeLeesWaffle : RelicDef; // AfterObtained

[ContentId("FAKE_MANGO")] public sealed class FakeMango : RelicDef; // AfterObtained

[ContentId("FAKE_MERCHANTS_RUG")] public sealed class FakeMerchantsRug : RelicDef; // no hooks

[ContentId("FRAGRANT_MUSHROOM")] public sealed class FragrantMushroom : RelicDef; // AfterObtained

[ContentId("MAW_BANK")] public sealed class MawBank : RelicDef; // AfterItemPurchased

[ContentId("SWORD_OF_STONE")] public sealed class SwordOfStone : RelicDef; // AfterCombatVictory

[ContentId("WONGO_CUSTOMER_APPRECIATION_BADGE")] public sealed class WongoCustomerAppreciationBadge : RelicDef; // no hooks

[ContentId("WONGOS_MYSTERY_TICKET")] public sealed class WongosMysteryTicket : RelicDef; // TryModifyRewards / AfterCombatEnd

// --- combat-start / player turn-1 effects imported with the root ------------------------
// The native hook fires before the play-phase root; the resulting block, power, hand or
// counter is part of the imported Battle.
[ContentId("BIG_MUSHROOM")]
public sealed class BigMushroom : RelicDef
{
}

[ContentId("FAKE_ANCHOR")] public sealed partial class FakeAnchor : RelicDef; // BeforeCombatStart block

[ContentId("FAKE_BLOOD_VIAL")]
public sealed class FakeBloodVial : RelicDef
{
}

[ContentId("ROYAL_POISON")]
public sealed class RoyalPoison : RelicDef
{
}

[ContentId("SWORD_OF_JADE")] public sealed partial class SwordOfJade : RelicDef; // AfterRoomEntered Strength

/// <summary>
/// <c>TeaOfDiscourtesy</c>: at <c>BeforeCombatStart</c>, while the run-side <c>CombatsLeft</c> is
/// positive, add its <c>DazedCount</c> Dazed cards to random draw-pile positions and spend one
/// combat. The run layer seeds <see cref="Relic.Counter"/> with <c>CombatsLeft</c> before the build
/// and copies the decremented value back.
/// </summary>
[ContentId("TEA_OF_DISCOURTESY")]
public sealed class TeaOfDiscourtesy : RelicDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("DAZED", 0)];

}

// --- state projected without a later combat effect --------------------------------------
/// <summary>
/// <c>BoneTea</c>: upgrades the turn-1 hand and spends a combat at the player turn start before the
/// play-phase root; no later hook. Counter = CombatsLeft, projected from
/// <c>StatefulRelicState.Current</c>.
/// </summary>
[ContentId("BONE_TEA")]
public sealed class BoneTea : RelicDef
{
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        ref Relic relic = ref x.B.Relics[self.Index];
        if (relic.Counter <= 0 || x.B.PlayerTurn > 1)
            return;
        for (int index = 0; index < x.B.HandCount; index++)
            Kernel.UpgradeCard(x.R, ref x.B.Cards[index]);
        relic.Counter--;
    }
}
