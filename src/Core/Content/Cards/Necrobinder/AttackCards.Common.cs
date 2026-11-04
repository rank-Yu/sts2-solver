using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Necrobinder;

/// <summary>
/// <c>BlightStrike</c>: attack, then apply Doom equal to the attack's blocked + unblocked damage against
/// the target (<c>CorePowerSupport</c>'s BlightStrike case, which sums the play's damage entries;
/// the single-target attack makes the last damage call that total).
/// </summary>
[ContentId("BLIGHT_STRIKE")]
public sealed class BlightStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        // BlightStrike.cs sums this attack command's DamageResult.TotalDamage. Capture it before
        // ApplyEnemyDeathPowers, whose nested damage overwrites the per-call "last damage" fields.
        int damage = x.LastDamageTotal;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack
        // and before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        if (x.Target == Limits.NoOwner)
            return;
        if (damage > 0)
            Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.DoomPower>(), damage,
                Limits.PlayerOwner);
    }
}

[ContentId("POKE")]
public sealed class Poke : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage"];

    public override void OnPlay(ref Exec x) => NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));
}

/// <summary>
/// <c>Snap</c>: Osty attack, then select a hand card without the local Retain keyword and add Retain
/// (<c>CardChoiceSupport</c>'s ApplyRetain Spec and CardCmd.ApplyKeyword).
/// </summary>
[ContentId("SNAP")]
public sealed class Snap : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage"];

    public override void OnPlay(ref Exec x) => NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            if (!x.B.Cards[index].HasKeyword(Keywords.Retain))
                options[count++] = index;
        }
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.ApplyRetain, Pile.Hand, options[..count], 1,
                selected, out int chosen))
        {
            return;
        }
        if (chosen > 0)
            Piles.At(ref x.B, Pile.Hand, selected[0]).Keywords |= Keywords.Retain;
    }
}

[ContentId("FLATTEN")]
public sealed class Flatten : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage"];

    public override void OnPlay(ref Exec x) => NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));

    // AfterAttackMirrors.HandleFlatten: after any attack by the owner's Osty, every Flatten copy in
    // combat gets SetThisTurn(0) (an absolute zero EndOfTurn modifier, one per trigger).
    public override void AfterAttack(ref Exec x, Listener self, byte attacker)
    {
        if (attacker != Limits.OstyOwner)
            return;
        // AfterAttackMirrors.HandleFlatten runs once per Flatten listener and zeroes that listener's
        // own card via State.FindCard(card): one modifier per Flatten copy, not per copy per listener.
        if ((uint)self.Index >= (uint)x.B.CardCount)
            return;
        ref Card listenerCard = ref x.B.Cards[self.Index];
        if (listenerCard.Id != Id)
            return;
        Kernel.AddEnergyCostModifier(ref listenerCard, 0, CostType.Absolute,
            CostExpiration.EndOfTurn, reduceOnly: false);
    }

    // SimulatedCombatState.CardEventHistory.AfterCardEnteredCombat: a Flatten that enters combat after
    // Osty attacked this turn gets the same zero-cost modifier.
    public override void AfterEnteredCombat(ref Exec x, Pile pile, int index)
    {
        if (x.B.Counter(Counter.OstyAttacksThisTurn) <= 0)
            return;
        ref Card card = ref Piles.At(ref x.B, pile, index);
        Kernel.AddEnergyCostModifier(ref card, 0, CostType.Absolute, CostExpiration.EndOfTurn,
            reduceOnly: false);
    }

}

/// <summary><c>Fear</c>: player attack, then Vulnerable on the target (<c>CorePowerSupport</c>).</summary>
[ContentId("FEAR")]
public sealed class Fear : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower"];

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
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

[ContentId("SOW")]
public sealed class Sow : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("REAP")]
public sealed class Reap : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("DEFILE")]
public sealed class Defile : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

/// <summary>
/// <c>Graveblast</c>: attack, then move one discard-pile card to hand
/// (<c>CardChoiceSupport</c>'s MoveToHand spec over the discard pile).
/// </summary>
[ContentId("GRAVEBLAST")]
public sealed class Graveblast : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.DiscardCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToHand, Pile.Discard, options, 1, selected,
                out int chosen))
        {
            return;
        }
        if (chosen > 0)
            Kernel.MoveSelected(ref x.B, Pile.Discard, selected[..chosen], Pile.Hand,
                PilePosition.Bottom);
    }
}

/// <summary>
/// <c>Reave</c>: attack, then the Cards variable's Souls (upgraded with the card) at random draw-pile
/// positions (<c>CardEffectSpecRegistry</c>'s Reave completion).
/// </summary>
[ContentId("REAVE")]
public sealed class Reave : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOUL", 0), ("SOUL", 1)];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int count = (int)NecrobinderContent.Var(ref x, 1);
        bool upgraded = x.Card.Upgrade > 0;
        for (int index = 0; index < count; index++)
        {
            Card soul = NecrobinderContent.CreateSoul(x.B, x.R, upgraded);
            if (!Kernel.AddGeneratedCard(ref x, soul, Pile.Draw, PilePosition.Random)
                || x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// <c>SculptingStrike</c>: attack, then add the local Ethereal keyword to a selected hand card
/// (<c>CardChoiceSupport</c>'s ApplyEthereal spec + <c>CardCmd.ApplyKeyword</c>).
/// </summary>
[ContentId("SCULPTING_STRIKE")]
public sealed class SculptingStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            if (!x.B.Cards[index].HasKeyword(Keywords.Ethereal))
                options[count++] = index;
        }
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.ApplyEthereal, Pile.Hand, options[..count], 1,
                selected, out int chosen))
        {
            return;
        }
        if (chosen > 0)
            Piles.At(ref x.B, Pile.Hand, selected[0]).Keywords |= Keywords.Ethereal;
    }
}

/// <summary>
/// <c>DrainPower</c>: single-target attack, then upgrade up to Cards random upgradable discard cards
/// (<c>CardSelectionCardMirrors.DrainPowerOnPlay</c>: filtered list, full Fisher-Yates with the
/// CombatCardSelection stream, take the first Cards).
/// </summary>
[ContentId("DRAIN_POWER")]
public sealed class DrainPower : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int discardStart = Piles.Start(x.B, Pile.Discard);
        int discardCount = x.B.DiscardCount;
        Span<int> order = stackalloc int[discardCount];
        int candidates = 0;
        for (int index = 0; index < discardCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[discardStart + index];
            CardStats stats = x.R.Card(card.Id, card.Upgrade);
            if (card.Upgrade < stats.MaxUpgradeLevel)
                order[candidates++] = index;
        }
        if (candidates == 0)
            return;
        ref RngState rng = ref x.B.RngStream(RngStream.CombatCardSelection);
        for (int n = candidates; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (order[k], order[n]) = (order[n], order[k]);
        }
        int take = Math.Min((int)NecrobinderContent.Var(ref x, 1), candidates);
        // CombatPredictionSimulator.Upgrade is a no-op while the combat is ending; the random
        // selection (and its RNG draws) still happen first. A killing DrainPower must not upgrade.
        if (Kernel.IsEnding(x.B))
            return;
        for (int index = 0; index < take; index++)
            Kernel.UpgradeCard(x.R, ref x.B.Cards[discardStart + order[index]]);
    }
}
