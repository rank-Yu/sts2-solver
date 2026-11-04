using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Silent;

[ContentId("SKEWER")]
public sealed class Skewer : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // GeneralCardMirrors.GeneralAttackOnPlay: an X-cost attack uses ResolveEnergyXValue as its hit
    // count (resolved once).
    public override void OnPlay(ref Exec x)
    {
        int hits = Kernel.ResolveEnergyXValue(ref x);
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}

[ContentId("DASH")]
public sealed class Dash : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Block"];

    // Dash.cs: GainBlock before the attack; the inferred recipe keeps that order.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 1);
        if (x.NeedsChoice)
            return;
        CardEffects.GeneralAttack(ref x, 0);
    }
}

[ContentId("POUNCE")]
public sealed class Pounce : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardEffectSpecRegistry.Pounce: attack, then FreeSkillPower 1.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.FreeSkillPower>(), 1, Limits.PlayerOwner);
    }
}

[ContentId("MEMENTO_MORI")]
public sealed class MementoMori : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedVarSpecRegistry.MementoMori: CalculationBase + ExtraDamage * cards discarded this turn.
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target,
        out decimal damage)
    {
        CardStats stats = x.R.Card(self.Id, self.Upgrade);
        if (stats.Vars.Length < 2)
            throw new UnsupportedException(
                $"MEMENTO_MORI 规则表只有 {stats.Vars.Length} 个动态变量，需要 CalculationBase 和 ExtraDamage。");
        decimal baseValue = stats.Vars[0] + DecimalVars.Delta(in x.B, in self, 0);
        decimal extra = stats.Vars[1] + DecimalVars.Delta(in x.B, in self, 1);
        damage = baseValue + extra * x.B.Counter(Counter.CardsDiscardedThisTurn);
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        if (!TryGetCalculatedDamage(ref x, x.Card, x.Target, out decimal damage))
            throw new UnsupportedException("MEMENTO_MORI 没有可读取的 CalculatedDamage。");
        CardEffects.Attack(ref x, damage);
    }
}

[ContentId("PRECISE_CUT")]
public sealed class PreciseCut : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedVarSpecRegistry.PreciseCut: the played card is in the play pile, so the multiplier is
    // -hand count.
    public override void OnPlay(ref Exec x)
    {
        decimal damage = CardEffects.Var(x, 0) - CardEffects.Var(x, 1) * x.B.HandCount;
        CardEffects.Attack(ref x, damage);
    }
}

[ContentId("FINISHER")]
public sealed class Finisher : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];

    // CalculatedVarSpecRegistry.Finisher: CalculatedHits = CalculationBase + CalculationExtra *
    // attacks played this turn (GetAttacksPlayedThisTurn), the hit count of the Damage attack.
    public override void OnPlay(ref Exec x)
    {
        int hits = (int)(CardEffects.Var(x, 1)
            + CardEffects.Var(x, 2) * x.B.Counter(Counter.AttacksPlayedThisTurn));
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}

[ContentId("BACKSTAB")]
public sealed class Backstab : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("STRANGLE")]
public sealed class Strangle : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "StranglePower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack
        // and before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.StranglePower>(), 1);
    }
}

[ContentId("PINPOINT")]
public sealed class Pinpoint : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    // Pinpoint.cs: entering combat reduces its cost by the Skills already finished this turn.
    public override void AfterEnteredCombat(ref Exec x, Pile pile, int index)
    {
        int amount = x.B.Counter(Counter.SkillCardsPlayedThisTurn);
        if (amount <= 0)
            return;
        ref Card card = ref Piles.At(ref x.B, pile, index);
        if (card.Id != Id)
            return;
        Kernel.AddEnergyCostModifier(ref card, -amount, CostType.Relative,
            CostExpiration.EndOfTurn, reduceOnly: false);
    }

    // Pinpoint.cs.AfterCardPlayed: each Skill its owner plays reduces its cost by 1 this turn, while
    // it sits in any combat pile.
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Owner != Limits.PlayerOwner)
            return;
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Skill)
            return;
        ref Card card = ref x.B.Cards[self.Index];
        if (card.Id != Id)
            return;
        Kernel.AddEnergyCostModifier(ref card, -1, CostType.Relative,
            CostExpiration.EndOfTurn, reduceOnly: false);
    }
}

/// <summary>
/// <c>EchoingSlash</c>: one attack context hits all enemies, and every target killed adds one more
/// wave. Existing engine: <c>CardOnPlaySupport.Batch042.ApplyEchoingSlash</c>.
/// </summary>
[ContentId("ECHOING_SLASH")]
public sealed class EchoingSlash : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
        => Kernel.CardAttackRepeating(ref x, 0, initialWaves: 1);
}
