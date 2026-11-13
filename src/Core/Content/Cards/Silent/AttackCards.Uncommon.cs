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
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        byte target = Limits.NoOwner;
        int lowestHp = int.MaxValue;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            int index = x.B.Roster[position];
            byte owner = Kernel.EnemyOwner(index);
            int hp = x.B.Enemies[index].Creature.CurrentHp;
            if (!Kernel.IsHittable(x.B, owner) || hp >= lowestHp)
                continue;
            target = owner;
            lowestHp = hp;
        }
        if (target != Limits.NoOwner)
            Kernel.CardAttack(ref x, CardEffects.Var(x, 0), 1, AttackTargeting.Single, target);
    }
}

[ContentId("PRECISE_CUT")]
public sealed class PreciseCut : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedVarSpecRegistry.PreciseCut: the played card is in the play pile, so the multiplier is
    // -hand count.
    // CalculatedDamageVar.Calculate: CalculationBase + ExtraDamage * the multiplier below; Thrash reads
    // it with a null target. OnPlay uses the same formula.
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        damage = CardEffects.Var(in x.B, x.R, in self, 0) + CardEffects.Var(in x.B, x.R, in self, 1) * (-(x.B.HandCount - (selfInHand ? 1 : 0)));
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, selfInHand: false, out decimal damage);
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
