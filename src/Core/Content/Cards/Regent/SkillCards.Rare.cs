using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Regent;

/// <summary>
/// <c>DecisionsDecisions</c>: draw the Cards variable, choose one non-unplayable Skill in hand, then
/// auto-play it Repeat times (<c>CardChoiceResolution.AutoPlayRepeated</c> →
/// <c>ContinueRepeatedSelection</c>). The selected instance carries a dynamic 64..255 transient
/// mark and is followed through every pile move; equal copies elsewhere are never substituted.
/// </summary>
[ContentId("DECISIONS_DECISIONS")]
public sealed class DecisionsDecisions : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "Repeat"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Skill
                && !Kernel.HasKeyword(x.B, card, Keywords.Unplayable))
                options[count++] = index;
        }
        if (count == 0)
            return;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.AutoPlayRepeated, Pile.Hand, options[..count],
                1, selected, out int chosen))
        {
            return;
        }
        if (chosen == 0)
            return;
        // ContinueRepeatedSelection keeps the selected CardModel, so equal copies elsewhere are not
        // interchangeable. Mark the selected instance with the dynamic 64..255 identity range
        // (TransientMarkBase, shared with Sly/Stoke and the Mayhem queue) and follow the mark through
        // every pile move. The mark never enters a state key and is cleared in finally.
        int markBase = x.TransientMarkBase;
        if (64 + markBase + 1 > 256)
            throw new CapacityException("TransientMarks", 256 - 64);
        x.TransientMarkBase = checked((byte)(markBase + 1));
        byte mark = checked((byte)(64 + markBase));
        ref Card selectedCard = ref Piles.At(ref x.B, Pile.Hand, selected[0]);
        selectedCard.TransientMark = mark;
        Card repeated = selectedCard;
        bool leftCombat = false;
        try
        {
            int repeats = (int)CardEffects.Var(x, 1);
            for (int index = 0; index < repeats; index++)
            {
                if (Kernel.IsOverOrEnding(x.B) || x.B.Player.IsDead)
                    return;
                bool played;
                Card playedCard;
                Pile resultPile;
                int resultIndex;
                if (Kernel.TryFindUniqueTransientMark(x.B, mark, out Pile pile, out int position))
                {
                    played = Kernel.AutoPlay(ref x, pile, position, Limits.NoOwner, skipXCapture: false,
                        out playedCard, out resultPile, out resultIndex);
                }
                else if (leftCombat)
                {
                    // The existing engine's AutoPlay re-adds a live card whose GetPile(State) is null
                    // and plays it again (a Power that left combat on its previous play).
                    played = Kernel.AutoPlay(ref x, repeated, Limits.NoOwner, skipXCapture: false,
                        historyCourseDupe: false, out playedCard, out resultPile, out resultIndex);
                }
                else
                {
                    throw new UnsupportedException(
                        $"重复自动打出的 {Registry.Card(repeated.Id).NativeId}+{repeated.Upgrade} 失去瞬态标记。");
                }
                if (x.NeedsChoice)
                    return;
                if (!played)
                    break;
                repeated = playedCard;
                leftCombat = resultIndex < 0;
            }
        }
        finally
        {
            for (int position = 0; position < x.B.CardCount; position++)
            {
                if (x.B.Cards[position].TransientMark == mark)
                    x.B.Cards[position].TransientMark = 0;
            }
            for (int slot = 0; slot < Limits.DetachedCards; slot++)
            {
                if (x.B.DetachedCards[slot].TransientMark == mark)
                    x.B.DetachedCards[slot].TransientMark = 0;
            }
            x.TransientMarkBase = checked((byte)markBase);
        }
    }
}

/// <summary>
/// <c>Guards</c>: optionally choose any number of hand cards and transform each into the canonical
/// Minion Sacrifice, upgraded when Guards is upgraded.
/// </summary>
[ContentId("GUARDS")]
public sealed class Guards : CardDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("MINION_SACRIFICE", 0)];

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[options.Length];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.Transform, Pile.Hand, options, 0,
                options.Length, selected, out int chosen))
        {
            return;
        }
        Charge.TransformSelected(ref x, Pile.Hand, selected[..chosen], "MINION_SACRIFICE");
    }
}

/// <summary>
/// <c>ForegoneConclusion</c>: applies ForegoneConclusionPower with the Cards variable
/// (<c>CardPowerOnPlaySupport</c>); the power's draw-pile choice runs at BeforeHandDraw.
/// </summary>
[ContentId("FOREGONE_CONCLUSION")]
public sealed class ForegoneConclusion : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<string> AppliedPowers => ["FOREGONE_CONCLUSION_POWER"];

    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.ForegoneConclusionPower>(), (int)CardEffects.Var(x, 0),
            Limits.PlayerOwner);
}

/// <summary><c>TheSmith</c>: CardOnPlaySupport forges the Forge variable.</summary>
[ContentId("THE_SMITH")]
public sealed class TheSmith : ForgeCardDef
{
    public override IReadOnlyList<string> VarNames => ["Forge"];

    public override void OnPlay(ref Exec x)
        => Kernel.Forge(ref x, (int)CardEffects.Var(x, 0));
}

/// <summary>
/// <c>IAmInvincible</c>: the inferred recipe gains Block; AfterAutoPostPlayPhaseEnteredMirrors then
/// auto-plays one card from the top of the draw pile when this card is that top card.
/// </summary>
[ContentId("I_AM_INVINCIBLE")]
public sealed class IAmInvincible : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralBlock(ref x, 0);

    public override void AfterAutoPostPlayPhaseEntered(ref Exec x, Listener self)
    {
        if (self.Kind != ListenerKind.Card || self.Owner != Limits.PlayerOwner)
            return;
        // DrawPile.TopCard?.References(card): the top of the draw pile is this card. Sts2Solver has no
        // instance identity, so the hook does not test the listener's own position (it can be stale
        // after an earlier copy was auto-played); instead it checks the current top value. Every copy
        // anywhere is a listener, and there is at least one listener per draw-pile copy, so all
        // consecutive I Am Invincible cards at the top are auto-played exactly once each.
        if (x.B.DrawCount == 0)
            return;
        ref readonly Card top = ref Piles.At(ref x.B, Pile.Draw, 0);
        if (top.Id != Registry.CardId<IAmInvincible>())
            return;
        Kernel.AutoPlayFromDrawPile(ref x, 1, forceExhaust: false);
    }
}

/// <summary>
/// <c>BigBang</c> (Exhaust): the inferred recipe draws the Cards variable, then
/// CardEffectSpecRegistry's <c>BigBang</c> gains Energy, gains Stars and forges, in that order.
/// </summary>
[ContentId("BIG_BANG")]
public sealed class BigBang : ForgeCardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy", "Stars", "Forge"];

    public override void OnPlay(ref Exec x)
    {
        Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);
        if (x.NeedsChoice)
            return;
        Kernel.GainEnergy(ref x, CardEffects.Var(x, 1));
        if (!Kernel.GainStars(ref x, (int)CardEffects.Var(x, 2)))
            return;
        Kernel.Forge(ref x, (int)CardEffects.Var(x, 3));
    }
}
