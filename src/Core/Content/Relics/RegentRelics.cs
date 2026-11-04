using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>DivineRight</c> (starter): <c>AfterRoomEntered</c> gains 3 stars, which runs before a combat root
/// is captured; the imported <see cref="Battle.Stars"/> already includes it and the relic has no
/// in-combat hook of its own.
/// </summary>
[ContentId("DIVINE_RIGHT")]
public sealed partial class DivineRight : RelicDef;

/// <summary>
/// <c>GalacticDust</c>: <c>SimulatedCombatState.TriggerRelicsAfterStarsSpent</c> accumulates stars in the
/// relic's StatefulRelicState (Counter) and gains Block per completed multiple of the Stars variable.
/// </summary>
[ContentId("GALACTIC_DUST")]
public sealed class GalacticDust : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Stars", "Block"];

    public override void AfterStarsSpent(ref Exec x, Listener self, int amount)
    {
        if (amount <= 0 || self.Owner != Limits.PlayerOwner)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        int threshold = (int)x.R.RelicVar(Id, 0);
        if (threshold <= 0)
            throw new UnsupportedException("银河星尘的星能阈值不是正数。");
        int total = relic.Counter + amount;
        int triggers = total / threshold;
        relic.Counter = total % threshold;
        if (triggers > 0)
            Kernel.GainBlock(ref x, Limits.PlayerOwner, (decimal)triggers * x.R.RelicVar(Id, 1),
                ValueProps.Unpowered, fromCard: false);
    }
}

/// <summary>
/// <c>MiniRegent</c>: AfterStarsSpent grants the Strength variable once per turn (StatefulRelicState
/// Current is the used flag), reset at the player's side turn start
/// (<c>BeforeSideTurnStartMirrors.Vanilla</c>).
/// </summary>
[ContentId("MINI_REGENT")]
public sealed class MiniRegent : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];

    public override void AfterStarsSpent(ref Exec x, Listener self, int amount)
    {
        if (amount <= 0 || self.Owner != Limits.PlayerOwner)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        if (relic.Counter != 0)
            return;
        relic.Counter = 1;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.StrengthPower>(),
            (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player)
            x.B.Relics[self.Index].Counter = 0;
    }
}

/// <summary>
/// <c>Regalite</c>: the first card generated for the player each turn gains the Block variable as
/// Unpowered block (<c>AfterCardGeneratedForCombatMirrors.HandleRegalite</c>; RegalitePredictionState's
/// UsedThisTurn flag). Reset at the player's side turn start.
/// </summary>
[ContentId("REGALITE")]
public sealed class Regalite : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position, byte creator)
    {
        if (creator != Limits.PlayerOwner || self.Owner != Limits.PlayerOwner)
            return;
        ref Relic relic = ref x.B.Relics[self.Index];
        if (relic.Counter != 0)
            return;
        relic.Counter = 1;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0),
            ValueProps.Unpowered, fromCard: false);
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player)
            x.B.Relics[self.Index].Counter = 0;
    }
}

/// <summary>
/// <c>VitruvianMinion</c>: powered damage and block from a card tagged Minion deal/gain double
/// (the relic's ModifyDamageMultiplicative / ModifyBlockMultiplicative mirrors).
/// </summary>
[ContentId("VITRUVIAN_MINION")]
public sealed class VitruvianMinion : RelicDef
{
    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self,
        in DamageContext damage, decimal amount)
        => IsMinionCard(x, damage.HasCardSource, damage.CardSource) ? 2m : 1m;

    public override decimal ModifyBlockMultiplicative(ref Exec x, Listener self,
        in BlockContext block, decimal amount)
        => IsMinionCard(x, block.HasCardSource, block.CardSource) ? 2m : 1m;

    private static bool IsMinionCard(in Exec x, bool hasCard, in Card card)
        => hasCard && (x.R.Card(card.Id, card.Upgrade).Tags & CardTag.Minion) != 0;
}

/// <summary>
/// <c>FencingManual</c>: <c>AfterSideTurnStart</c> forges 10 on turn 1 only. A combat root is captured
/// after that turn start, so the forge already happened before the root; later turns are no-ops.
/// </summary>
[ContentId("FENCING_MANUAL")]
public sealed partial class FencingManual : RelicDef;

/// <summary>
/// <c>OrangeDough</c>: <c>AfterSideTurnStart</c> generates its <c>Cards</c> distinct colorless cards on
/// turn 1 only.
/// </summary>
[ContentId("ORANGE_DOUGH")]
public sealed class OrangeDough : RelicDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.ColorlessAll];

}

/// <summary>
/// <c>BrilliantScarf</c>: while the owner has played Cards-1 non-auto cards this turn, cards in hand or
/// play cost 0 energy and 0 stars (<c>TryModifyEnergyCostInCombatLate</c> / <c>TryModifyStarCost</c>);
/// the per-turn counter increments in <c>AfterCardPlayed</c> for non-auto plays and resets at the
/// player's side turn start (<c>BeforeSideTurnStartMirrors.Vanilla</c>). Counter = the engine's
/// CounterPredictionState (the live <c>_cardsPlayedThisTurn</c> is its constructor's fallback).
/// </summary>
[ContentId("BRILLIANT_SCARF")]
public sealed class BrilliantScarf : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules,
        Listener self, in Card card, Pile pile, decimal amount)
        => IsFreeTurn(battle, rules, self.Index, pile) ? 0m : amount;

    public override decimal TryModifyStarCost(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
        => IsFreeTurn(battle, rules, self.Index, pile) ? 0m : amount;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (x.CurrentPlayIsAutoPlay || self.Owner != Limits.PlayerOwner)
            return;
        x.B.Relics[self.Index].Counter++;
    }

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player)
            x.B.Relics[self.Index].Counter = 0;
    }

    private bool IsFreeTurn(in Battle battle, Rules rules, int relicIndex, Pile pile)
    {
        if (pile is not (Pile.Hand or Pile.Play))
            return false;
        int threshold = (int)rules.RelicVar(Id, 0) - 1;
        return battle.Relics[relicIndex].Counter == threshold;
    }
}
