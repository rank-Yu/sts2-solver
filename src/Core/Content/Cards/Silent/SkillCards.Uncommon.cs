using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Silent;

[ContentId("ACROBATICS")]
public sealed class Acrobatics : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: Acrobatics → Discard, Hand, 1, every hand card.
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Discard, Pile.Hand, options, 1, selected,
                out int chosen))
        {
            return;
        }
        Kernel.DiscardAndDraw(ref x, Pile.Hand, selected[..chosen], 0);
    }
}

[ContentId("CALCULATED_GAMBLE")]
public sealed class CalculatedGamble : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        // CardDrawCardMirrors.CalculatedGambleOnPlay: DiscardAndDraw(hand, hand.Length), where the
        // draw count is the hand size captured before the discard.
        int hand = x.B.HandCount;
        Kernel.DiscardAllInHand(ref x, hand);
    }
}

[ContentId("REFLEX")]
public sealed class Reflex : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);
}

[ContentId("TACTICIAN")]
public sealed class Tactician : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void OnPlay(ref Exec x)
        => Kernel.GainEnergy(ref x, CardEffects.Var(x, 0));
}

[ContentId("HIDDEN_DAGGERS")]
public sealed class HiddenDaggers : ShivGeneratingCardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "Shivs"];

    // CardOnPlaySupport has no OnPlay for HiddenDaggers: everything is the choice and its
    // ApplyPostChoiceEffects.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: Discard, Hand, Cards, every hand card; BuildRequiredEmptyChoice asks an empty
        // Discard choice when the hand is empty. ApplyPostChoiceEffects then adds Shivs Shivs,
        // upgraded when this card is upgraded.
        int cards = (int)CardEffects.Var(x, 0);
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[x.B.HandCount];
        int chosen;
        if (options.IsEmpty)
        {
            // The existing engine's nested-choice sink applies BuildRequiredEmptyChoice without
            // asking during an auto-play (ResolveNestedCardChoice); a manual play asks it.
            if (x.CurrentPlayIsAutoPlay)
            {
                chosen = 0;
            }
            else if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.Discard, Pile.Hand, options, 0, 0,
                         selected, out chosen))
            {
                return;
            }
        }
        else if (!Kernel.ChooseCards(ref x, ChoiceEffect.Discard, Pile.Hand, options, cards,
                     selected, out chosen))
        {
            return;
        }
        Kernel.DiscardAndDraw(ref x, Pile.Hand, selected[..chosen], 0);
        if (x.NeedsChoice)
            return;
        SilentContent.GenerateShivs(ref x, (int)CardEffects.Var(x, 1), upgraded: x.Card.Upgrade != 0);
    }
}

[ContentId("HAND_TRICK")]
public sealed class HandTrick : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralBlock(ref x, 0);

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: ApplySly, Hand, 1, hand Skills that are not already Sly this turn. With no
        // option, CardChoiceSupport.Spec returns null and nothing is asked.
        Span<int> options = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Skill || Kernel.IsSlyThisTurn(x.B, card))
                continue;
            options[count++] = index;
        }
        if (count == 0)
            return;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.ApplySly, Pile.Hand, options[..count], 1,
                selected, out int chosen))
        {
            return;
        }
        for (int index = 0; index < chosen; index++)
        {
            x.B.Cards[selected[index]].Flags |= CardFlags.SlyThisTurn;
        }
    }
}

[ContentId("ESCAPE_PLAN")]
public sealed class EscapePlan : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    // CardDrawCardMirrors.EscapePlanOnPlay: draw one card; gain block only when the single drawn card
    // is a Skill.
    public override void OnPlay(ref Exec x)
    {
        if (!Kernel.TryDrawOne(ref x, fromHandDraw: false, out Card card))
            return;
        if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Skill)
            return;
        CardEffects.GeneralBlock(ref x, 0);
    }
}

[ContentId("EXPERTISE")]
public sealed class Expertise : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override IReadOnlyList<string> AppliedPowers => ["RETAIN_HAND_POWER"];

    public override void OnPlay(ref Exec x)
    {
        Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);
        if (!x.NeedsChoice)
            Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                Registry.PowerId<Powers.RetainHandPower>(), 1, Limits.PlayerOwner);
    }
}

[ContentId("BUBBLE_BUBBLE")]
public sealed class BubbleBubble : CardDef
{
    public override IReadOnlyList<string> VarNames => ["PoisonPower"];

    // CardOnPlaySupport.BubbleBubble: only when the target already has Poison.
    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        if (Kernel.GetAmount(x.B, x.Target, Registry.PowerId<Powers.PoisonPower>()) <= 0)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.PoisonPower>(), 0);
    }
}

[ContentId("BOUNCING_FLASK")]
public sealed class BouncingFlask : CardDef
{
    public override IReadOnlyList<string> VarNames => ["PoisonPower", "Repeat"];

    // CardOnPlaySupport.Batch042.ApplyBouncingFlask: Repeat times, a random living hittable enemy
    // (CombatTargets NextItem), applying Poison; an empty candidate list stops without consuming RNG.
    public override void OnPlay(ref Exec x)
    {
        int repeats = (int)CardEffects.Var(x, 1);
        ushort poison = Registry.PowerId<Powers.PoisonPower>();
        int amount = (int)CardEffects.Var(x, 0);
        Span<byte> candidates = stackalloc byte[Limits.Enemies];
        for (int repeat = 0; repeat < repeats; repeat++)
        {
            int count = 0;
            for (int position = 0; position < x.B.RosterCount; position++)
            {
                byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
                if (Kernel.IsHittable(x.B, owner))
                    candidates[count++] = owner;
            }
            if (count == 0)
                break;
            byte target = candidates[x.B.RngStream(RngStream.CombatTargets).NextInt(0, count)];
            Kernel.ApplyPower(ref x, target, poison, amount, Limits.PlayerOwner);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("HAZE")]
public sealed class Haze : CardDef
{
    public override IReadOnlyList<string> VarNames => ["PoisonPower", "WeakPower"];

    // CardOnPlaySupport.Haze: Poison to every hittable enemy, then Weak to every hittable enemy. The
    // existing case applies both passes without resolving the power amount changes in between.
    public override void OnPlay(ref Exec x)
    {
        ushort poison = Registry.PowerId<Powers.PoisonPower>();
        ushort weak = Registry.PowerId<Powers.WeakPower>();
        int poisonAmount = (int)CardEffects.Var(x, 0);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, owner))
                continue;
            Kernel.ApplyPower(ref x, owner, poison, poisonAmount, Limits.PlayerOwner);
            if (x.NeedsChoice)
                return;
        }
        int weakAmount = (int)CardEffects.Var(x, 1);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, owner))
                continue;
            Kernel.ApplyPower(ref x, owner, weak, weakAmount, Limits.PlayerOwner);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("EXPOSE")]
public sealed class Expose : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Power"];

    // CardOnPlaySupport.Expose: lose all block as Move block damage, clear Artifact, then Vulnerable.
    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        if (x.B.Creature(x.Target).Block > 0)
            Kernel.LoseBlock(ref x, x.Target, x.B.Creature(x.Target).Block, Limits.PlayerOwner);
        if (Kernel.GetAmount(x.B, x.Target, Registry.PowerId<Powers.ArtifactPower>()) > 0)
            Kernel.SetPowerAmount(ref x.B, x.Target, Registry.PowerId<Powers.ArtifactPower>(), 0);
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 0);
    }
}

[ContentId("FLECHETTES")]
public sealed class Flechettes : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];

    // CalculatedVarSpecRegistry.Flechettes: CalculatedHits counts Skill cards in hand.
    public override void OnPlay(ref Exec x)
    {
        int skills = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Skill)
                skills++;
        }
        int hits = (int)(CardEffects.Var(x, 1) + CardEffects.Var(x, 2) * skills);
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}

[ContentId("UP_MY_SLEEVE")]
public sealed class UpMySleeve : ShivGeneratingCardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
    {
        SilentContent.GenerateShivs(ref x, (int)CardEffects.Var(x, 0), upgraded: false);
        if (x.NeedsChoice)
            return;
        // UpMySleeve.cs: playedCard.EnergyCost.AddThisCombat(-1). The played card is the last entry of
        // the play pile while its OnPlay runs.
        int position = x.B.PlayCount - 1;
        if (position < 0)
            throw new InvalidOperationException("袖中藏刃没有正在打出的卡。");
        Kernel.AddEnergyCostModifier(ref Piles.At(ref x.B, Pile.Play, position), -1,
            CostType.Relative, CostExpiration.EndOfCombat, reduceOnly: false);
    }
}

[ContentId("LEG_SWEEP")]
public sealed class LegSweep : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "WeakPower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 1);
    }
}

[ContentId("BLUR")]
public sealed class Blur : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Blur"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.BlurPower>(), 1);
    }
}

[ContentId("SIDESTEP")]
public sealed class Sidestep : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    // CorePowerSupport.ApplyCardPowers.Sidestep: AddEnergyNextTurn (EnergyNextTurnPower amount).
    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.EnergyNextTurnPower>(), 0);
}

/// <summary>
/// <c>Mirage</c>: CalculatedBlock = CalculationBase + CalculationExtra × the poison on every living
/// enemy (<c>CalculatedVarSpecRegistry</c>'s Mirage multiplier). The card gains that block as a Move
/// card block; upgraded Mirage drops Exhaust, which is part of the rule-table keyword set.
/// </summary>
[ContentId("MIRAGE")]
public sealed class Mirage : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "CalculationExtra"];

    public override void OnPlay(ref Exec x)
    {
        int poison = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (x.B.Creature(owner).IsAlive)
                poison += Kernel.GetAmount(x.B, owner, Registry.PowerId<PoisonPower>());
        }
        decimal block = CardEffects.Var(x, 0) + CardEffects.Var(x, 1) * poison;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, block, ValueProps.Move, fromCard: true);
    }
}
