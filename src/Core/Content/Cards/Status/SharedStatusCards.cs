using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Status;

/// <summary>
/// Native <c>Beckon</c>: cost 1, playable with no OnPlay effect; at the player's turn end it loses
/// 6 HP (Unblockable|Unpowered|Move) with the card as source.
/// </summary>
[ContentId("BECKON")]
public sealed class Beckon : CardDef
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
        damage = TurnEndSelfDamage.FromCard(CardEffects.Var(in battle, rules, in card, 0),
            unblockable: true);
        return true;
    }
}

/// <summary>
/// Native <c>Burn</c>: cost -1, Unplayable; at the player's turn end it takes 2 unpowered,
/// blockable damage with the card as source.
/// </summary>
[ContentId("BURN")]
public sealed class Burn : CardDef
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
        damage = TurnEndSelfDamage.FromCard(CardEffects.Var(in battle, rules, in card, 0),
            unblockable: false);
        return true;
    }
}

/// <summary>Native <c>Debris</c>: cost 1, Exhaust; its OnPlay is an explicit no-op.</summary>
[ContentId("DEBRIS")]
public sealed class Debris : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Infection</c>: cost -1, Unplayable; at the player's turn end it takes 3 unpowered,
/// blockable damage with the card as source.
/// </summary>
[ContentId("INFECTION")]
public sealed class Infection : CardDef
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
        damage = TurnEndSelfDamage.FromCard(CardEffects.Var(in battle, rules, in card, 0),
            unblockable: false);
        return true;
    }
}

/// <summary>Native <c>Soot</c>: cost -1, Unplayable; no combat hook is overridden.</summary>
[ContentId("SOOT")]
public sealed class Soot : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Toxic</c>: cost 1, Exhaust; playable but its OnPlay has no effect. At the player's
/// turn end it takes 5 unpowered, blockable damage with the card as source.
/// </summary>
[ContentId("TOXIC")]
public sealed class Toxic : CardDef
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
        damage = TurnEndSelfDamage.FromCard(CardEffects.Var(in battle, rules, in card, 0),
            unblockable: false);
        return true;
    }
}

/// <summary>
/// Native <c>Wither</c>: cost -1, Unplayable; at the player's turn end it takes 3 unpowered,
/// blockable damage with the card as source. Aeonglass upgrades the Damage variable through a
/// private counter that admission rejects, so the rule table value is the whole supported state.
/// </summary>
[ContentId("WITHER")]
public sealed class Wither : CardDef
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
        damage = TurnEndSelfDamage.FromCard(CardEffects.Var(in battle, rules, in card, 0),
            unblockable: false);
        return true;
    }
}

/// <summary>Native <c>Wound</c>: cost -1, Unplayable; no combat hook is overridden.</summary>
[ContentId("WOUND")]
public sealed class Wound : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

/// <summary>
/// Native <c>Void</c>: cost -1, Unplayable, Ethereal; when drawn it loses its Energy
/// (<c>AfterCardDrawnMirrors.HandleVoid</c>, claimed once with
/// <c>Kernel.TakeIntrinsicDraw</c>). The Energy variable comes from the card drawn at
/// <paramref name="drawnPosition"/>; the definition touches no other card state.
/// </summary>
[ContentId("VOID")]
public sealed class Void : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void OnPlay(ref Exec x)
    {
    }

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (!Kernel.TakeIntrinsicDraw(ref x, self, drawnPosition))
            return;
        ref readonly Card drawn = ref x.B.Cards[drawnPosition];
        Kernel.LoseEnergy(ref x, x.R.Card(drawn.Id, drawn.Upgrade).Vars[0]);
    }
}

/// <summary>
/// Native <c>FranticEscape</c> (TheInsatiable's LIQUIFY_GROUND): when played, increments the Sandpit
/// targeting counter of its owner and adds one to its own combat energy cost
/// (<c>P/CardOnPlaySupport.Batch042.cs:64</c>, the existing engine's compensation; its native
/// damage/effect hooks are otherwise out of scope). The played card is the last entry of the play
/// pile while OnPlay runs, matching UpMySleeve's handling.
/// </summary>
[ContentId("FRANTIC_ESCAPE")]
public sealed class FranticEscape : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        Kernel.IncrementSandpitTargeting(ref x, Limits.PlayerOwner);
        int position = x.B.PlayCount - 1;
        if (position < 0)
            throw new InvalidOperationException("夺路而逃没有正在打出的卡。");
        Kernel.AddEnergyCostModifier(ref Piles.At(ref x.B, Pile.Play, position), 1,
            CostType.Relative, CostExpiration.EndOfCombat, reduceOnly: false);
    }
}
