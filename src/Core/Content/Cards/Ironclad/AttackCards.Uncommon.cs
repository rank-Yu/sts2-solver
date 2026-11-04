using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Ironclad;

[ContentId("BLUDGEON")]
public sealed class Bludgeon : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("HEMOKINESIS")]
public sealed class Hemokinesis : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HpLoss", "Damage"];

    public override void OnPlay(ref Exec x)
    {
        // Hemokinesis.cs: lose HP first, with the card as the damage source, then attack.
        CardEffects.LoseHpFromCard(ref x, CardEffects.Var(x, 0));
        if (x.NeedsChoice)
            return;
        CardEffects.GeneralAttack(ref x, 1);
    }
}

[ContentId("WHIRLWIND")]
public sealed class Whirlwind : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
        => CardEffects.GeneralAttack(ref x, 0, hitCount: Kernel.ResolveEnergyXValue(ref x));
}

[ContentId("PILLAGE")]
public sealed class Pillage : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CardDrawCardMirrors.PillageOnPlay: draw one card at a time until the drawn card is not an
        // Attack or the hand is full.
        while (true)
        {
            if (!Kernel.TryDrawOne(ref x, fromHandDraw: false, out Card drawn))
                return;
            if (x.B.HandCount >= x.B.MaxHandSize)
                return;
            // CardDrawCardMirrors.PillageOnPlay tests the returned drawn card, which stays the same
            // value even when AfterCardDrawn auto-played (Hellraiser) and removed it from hand.
            if (Kernel.ResolveCardType(x.B, x.R, drawn) != CardType.Attack)
                return;
        }
    }
}

[ContentId("BULLY")]
public sealed class Bully : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, out decimal damage)
    {
        int vulnerable = target == Limits.NoOwner
            ? 0
            : Kernel.GetAmount(x.B, target, Registry.PowerId<Powers.VulnerablePower>());
        damage = BodySlam.SelfVar(x, self, 0) + BodySlam.SelfVar(x, self, 1) * vulnerable;
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        int vulnerable = x.Target == Limits.NoOwner
            ? 0
            : Kernel.GetAmount(x.B, x.Target, Registry.PowerId<Powers.VulnerablePower>());
        CardEffects.Attack(ref x, CardEffects.Var(x, 0) + CardEffects.Var(x, 1) * vulnerable);
    }
}

[ContentId("DISMANTLE")]
public sealed class Dismantle : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        int vulnerable = x.Target == Limits.NoOwner
            ? 0
            : Kernel.GetAmount(x.B, x.Target, Registry.PowerId<Powers.VulnerablePower>());
        CardEffects.GeneralAttack(ref x, 0, hitCount: vulnerable > 0 ? 2 : 1);
    }
}

[ContentId("ASHEN_STRIKE")]
public sealed class AshenStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x,
            CardEffects.Var(x, 0) + CardEffects.Var(x, 1) * x.B.ExhaustCount);

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, out decimal damage)
    {
        damage = BodySlam.SelfVar(x, self, 0) + BodySlam.SelfVar(x, self, 1) * x.B.ExhaustCount;
        return true;
    }
}

[ContentId("FIGHT_ME")]
public sealed class FightMe : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat", "StrengthPower", "EnemyStrength"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0, hitCount: (int)CardEffects.Var(x, 1));
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack
        // and before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        ushort strength = Registry.PowerId<Powers.StrengthPower>();
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, strength,
            (int)CardEffects.Var(x, 2), Limits.PlayerOwner);
        CardEffects.ApplyToTarget(ref x, strength, 3);
    }
}

[ContentId("SPITE")]
public sealed class Spite : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    public override void OnPlay(ref Exec x)
    {
        bool lostHp = (x.B.Player.Flags & CreatureFlags.LostHpThisTurn) != 0;
        CardEffects.GeneralAttack(ref x, 0, hitCount: lostHp ? (int)CardEffects.Var(x, 1) : 1);
    }
}

[ContentId("RAMPAGE")]
public sealed class Rampage : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Increase"];

    // Rampage.AfterDowngraded re-adds _extraDamageFromPlays; the delta in Var0 is that accumulator.
    public override bool PreservesVar0OnDowngrade => true;

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // The native DynamicVars are read from the live CardModel after the attack; a mid-play
        // Dampen restore can upgrade this card before the Increase is added.
        Kernel.SyncCurrentPlayCard(ref x);
        CardEffects.AddCurrentCardVar(ref x, 0, CardEffects.Var(x, 1));
    }
}

[ContentId("UNRELENTING")]
public sealed class Unrelenting : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // Unrelenting.cs: the single-target attack runs first, then FreeAttackPower 1 is applied.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.FreeAttackPower>(),
            1, Limits.PlayerOwner);
    }
}

[ContentId("STOMP")]
public sealed class Stomp : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
        => CardEffects.GeneralAttack(ref x, 0);

    // Stomp.AfterCardEnteredCombat: reduce the cost by the Attacks finished this turn.
    public override void AfterEnteredCombat(ref Exec x, Pile pile, int index)
    {
        int attacks = x.B.Counter(Counter.AttacksPlayedThisTurn);
        if (attacks <= 0)
            return;
        ref Card card = ref Piles.At(ref x.B, pile, index);
        AddThisTurn(ref card, -attacks);
    }

    // BeforeCardPlayedMirrors.HandleStomp: every Attack the owner plays reduces this card by 1.
    public override void BeforeCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Attack)
            return;
        ref Card card = ref x.B.Cards[self.Index];
        if (card.Id != Id)
            return;
        AddThisTurn(ref card, -1);
    }

    private static void AddThisTurn(ref Card card, int amount)
    {
        if (amount == 0)
            return;
        if (card.EnergyModifierCount >= Limits.CardEnergyCostModifiers)
            throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
        card.EnergyModifiers[card.EnergyModifierCount] = CostModifier.Create(amount, CostType.Relative,
            CostExpiration.EndOfTurn, reduceOnly: false);
        card.EnergyModifierCount++;
    }
}

[ContentId("UPPERCUT")]
public sealed class Uppercut : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Power"];

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
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 1);
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

[ContentId("HOWL_FROM_BEYOND")]
public sealed class HowlFromBeyond : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void AfterAutoPostPlayPhaseEntered(ref Exec x, Listener self)
    {
        if (self.Kind != ListenerKind.Card || self.Owner != Limits.PlayerOwner)
            return;
        int exhaustStart = x.B.HandCount + x.B.DrawCount + x.B.DiscardCount;
        int exhaustEnd = exhaustStart + x.B.ExhaustCount;
        if (self.Index < exhaustStart || self.Index >= exhaustEnd)
            return;
        Kernel.AutoPlay(ref x, Pile.Exhaust, self.Index - exhaustStart);
    }
}
