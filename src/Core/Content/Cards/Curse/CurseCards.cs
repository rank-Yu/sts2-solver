using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Curse;

/// <summary>
/// Native <c>AscendersBane</c>: Eternal, Unplayable, Ethereal; no combat hook is overridden. Its
/// Ethereal keyword makes the kernel exhaust it from the hand at turn end.
/// </summary>
[ContentId("ASCENDERS_BANE")]
public sealed class AscendersBane : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>BadLuck</c>: Eternal, Unplayable; at the player's turn end it loses 13 HP
/// (Unblockable|Unpowered|Move) with the card as source.
/// </summary>
[ContentId("BAD_LUCK")]
public sealed class BadLuck : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HpLoss"];

    public override bool HasTurnEndInHandEffect => true;

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OnTurnEndInHand(ref Exec x)
        => CardEffects.DamageOwnerFromCard(ref x, CardEffects.Var(x, 0),
            ValueProps.Unblockable | ValueProps.Unpowered | ValueProps.Move);
    public override bool TryGetTurnEndSelfDamage(in Battle battle, Rules rules, in Card card,
        out TurnEndSelfDamage damage)
    {
        damage = TurnEndSelfDamage.FromCard(CardEffects.Var(in battle, rules, in card, 0), unblockable: true);
        return true;
    }

}

/// <summary>Native <c>Clumsy</c>: Unplayable, Ethereal; no combat hook is overridden.</summary>
[ContentId("CLUMSY")]
public sealed class Clumsy : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>Native <c>CurseOfTheBell</c>: Eternal, Unplayable; no combat hook is overridden.</summary>
[ContentId("CURSE_OF_THE_BELL")]
public sealed class CurseOfTheBell : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Decay</c>: Unplayable; at the player's turn end it takes 2 unpowered, blockable
/// damage with the card as source.
/// </summary>
[ContentId("DECAY")]
public sealed class Decay : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override bool HasTurnEndInHandEffect => true;

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OnTurnEndInHand(ref Exec x)
        => CardEffects.DamageOwnerFromCard(ref x, CardEffects.Var(x, 0),
            ValueProps.Unpowered | ValueProps.Move);
    public override bool TryGetTurnEndSelfDamage(in Battle battle, Rules rules, in Card card,
        out TurnEndSelfDamage damage)
    {
        damage = TurnEndSelfDamage.FromCard(CardEffects.Var(in battle, rules, in card, 0), unblockable: false);
        return true;
    }

}

/// <summary>
/// Native <c>Doubt</c>: Unplayable; at the player's turn end it applies 1 Weak and skips that
/// instance's first duration tick when Weak was absent before the application.
/// </summary>
[ContentId("DOUBT")]
public sealed class Doubt : CardDef
{
    public override IReadOnlyList<string> VarNames => ["WeakPower"];

    public override bool HasTurnEndInHandEffect => true;

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OnTurnEndInHand(ref Exec x)
    {
        ushort powerId = Registry.PowerId<Powers.WeakPower>();
        bool alreadyPresent = Kernel.FindPower(x.B, Limits.PlayerOwner, powerId) >= 0;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, powerId, (int)CardEffects.Var(x, 0), Limits.NoOwner);
        int index = Kernel.FindPower(x.B, Limits.PlayerOwner, powerId);
        if (!alreadyPresent && index >= 0)
            x.B.Powers[index].Flags |= PowerFlags.SkipNextDurationTick;
    }
}

/// <summary>Native <c>Folly</c>: Unplayable, Eternal, Innate, Ethereal; no combat hook is overridden.</summary>
[ContentId("FOLLY")]
public sealed class Folly : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>Native <c>Greed</c>: Eternal, Unplayable; no combat hook is overridden.</summary>
[ContentId("GREED")]
public sealed class Greed : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Guilty</c>: Unplayable. Its only override is AfterCombatEnd, which removes a deck copy
/// outside combat and is not a combat hook; nothing executes in combat.
/// </summary>
[ContentId("GUILTY")]
public sealed class Guilty : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>Native <c>Injury</c>: Unplayable; no combat hook is overridden.</summary>
[ContentId("INJURY")]
public sealed class Injury : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>Native <c>PoorSleep</c>: Unplayable, Retain; no combat hook is overridden.</summary>
[ContentId("POOR_SLEEP")]
public sealed class PoorSleep : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Shame</c>: Unplayable; at the player's turn end it applies 1 Frail and skips that
/// instance's first duration tick (the same first-application rule as Doubt).
/// </summary>
[ContentId("SHAME")]
public sealed class Shame : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Frail"];

    public override bool HasTurnEndInHandEffect => true;

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OnTurnEndInHand(ref Exec x)
    {
        ushort powerId = Registry.PowerId<Powers.FrailPower>();
        bool alreadyPresent = Kernel.FindPower(x.B, Limits.PlayerOwner, powerId) >= 0;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, powerId, (int)CardEffects.Var(x, 0), Limits.NoOwner);
        int index = Kernel.FindPower(x.B, Limits.PlayerOwner, powerId);
        if (!alreadyPresent && index >= 0)
            x.B.Powers[index].Flags |= PowerFlags.SkipNextDurationTick;
    }
}

/// <summary>Native <c>SporeMind</c>: cost 1, Exhaust; playable with no effect.</summary>
[ContentId("SPORE_MIND")]
public sealed class SporeMind : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>Native <c>Writhe</c>: Innate, Unplayable; no combat hook is overridden.</summary>
[ContentId("WRITHE")]
public sealed class Writhe : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Enthralled</c>: cost 2, Eternal; while it is in hand no other card can be played
/// (<c>SimulatedCombatState.IsCardPlayPrevented</c>, implemented by
/// <see cref="CardDef.LocksOtherHandCards"/>). It has no OnPlay effect.
/// </summary>
[ContentId("ENTHRALLED")]
public sealed class Enthralled : CardDef
{
    public override bool LocksOtherHandCards => true;

    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Normality</c>: Unplayable; while it is in hand no card can be played once three card
/// plays have started this turn (<c>ShouldPlayMirrors.HandleNormality</c>, the native
/// <c>_numOfCardsPerTurn</c> constant).
/// </summary>
[ContentId("NORMALITY")]
public sealed class Normality : CardDef
{
    private const int CardsPerTurn = 3;

    public override void OnPlay(ref Exec x)
    {
    }

    public override bool ShouldPlay(in Battle battle, Rules rules, Listener self, in Card card)
        => self.Index >= battle.HandCount
            || battle.Counter(Counter.CardPlayStartsThisTurn) < CardsPerTurn;
}

/// <summary>
/// Native <c>Regret</c>: Unplayable; <c>BeforeSideTurnEnd</c> records the hand size in the card's
/// private counter (<c>BeforeSideTurnEndMirrors.HandleRegret</c>), and at the turn end it loses that
/// much HP unblockable/unpowered and resets the counter
/// (<c>CardOnTurnEndInHandMirrors.HandleRegret</c>). Private = cards in hand when the turn end began.
/// </summary>
[ContentId("REGRET")]
public sealed class Regret : CardDef
{
    public override bool HasTurnEndInHandEffect => true;

    public override void OnPlay(ref Exec x)
    {
    }

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner) || self.Index >= x.B.HandCount)
            return;
        ref Card card = ref x.B.Cards[self.Index];
        if (card.Id != Id)
            return;
        card.Private = checked((short)x.B.HandCount);
    }

    public override void OnTurnEndInHand(ref Exec x)
    {
        CardEffects.DamageOwnerFromCard(ref x, x.Card.Private,
            ValueProps.Unblockable | ValueProps.Unpowered | ValueProps.Move);
        if (x.NeedsChoice)
            return;
        int position = x.B.PlayCount - 1;
        ref Card played = ref Piles.At(ref x.B, Pile.Play, position);
        if (played.Id != x.Card.Id || played.Upgrade != x.Card.Upgrade)
            throw new InvalidOperationException("回合末在手效果没有把瑞格瑞特放在打出堆顶。");
        played.Private = 0;
        x.Card.Private = 0;
    }
    public override bool TryGetTurnEndSelfDamage(in Battle battle, Rules rules, in Card card,
        out TurnEndSelfDamage damage)
    {
        damage = TurnEndSelfDamage.FromCard(card.Private > 0 ? card.Private : battle.HandCount, unblockable: true);
        return true;
    }

}

/// <summary>
/// Native <c>Debt</c>: Unplayable, no upgrade; its <c>OnTurnEndInHand</c> loses up to 10 gold. The
/// existing engine has no mirror/compensation for that hook, so Sts2Solver keeps the definition empty to
/// match the authority (the card still enters the play pile and is discarded at turn end).
/// </summary>
[ContentId("DEBT")]
public sealed class Debt : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}
