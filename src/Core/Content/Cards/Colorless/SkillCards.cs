using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Colorless;

[ContentId("DARK_SHACKLES")]
public sealed class DarkShackles : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthLoss"];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyTemporaryStrength(ref x, x.Target,
            Registry.PowerId<Powers.DarkShacklesPower>(),
            (int)CardEffects.Var(x, 0), Limits.PlayerOwner, sign: -1);
    }
}

[ContentId("EQUILIBRIUM")]
public sealed class Equilibrium : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Equilibrium"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.RetainHandPower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}

[ContentId("FINESSE")]
public sealed class Finesse : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Cards"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.Draw(ref x, CardEffects.Var(x, 1), fromHandDraw: false);
    }
}

[ContentId("IMPATIENCE")]
public sealed class Impatience : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
    {
        // CardDrawCardMirrors.ImpatienceOnPlay: draw only while no Attack card is in hand. The
        // played card is already in the play pile, so the hand read here excludes it.
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Attack)
                return;
        }
        Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);
    }
}

[ContentId("MASTER_OF_STRATEGY")]
public sealed class MasterOfStrategy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);
}

[ContentId("PANIC_BUTTON")]
public sealed class PanicButton : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Turns"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.NoBlockPower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}

[ContentId("PRODUCTION")]
public sealed class Production : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void OnPlay(ref Exec x)
        => Kernel.GainEnergy(ref x, CardEffects.Var(x, 0));
}

[ContentId("PROLONG")]
public sealed class Prolong : CardDef
{
    // CardOnPlaySupport `case Prolong`: BlockNextTurnPower amount = the owner's current block.
    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.BlockNextTurnPower>(),
            Math.Max(0, x.B.Player.Block), Limits.PlayerOwner);
}

[ContentId("PURITY")]
public sealed class Purity : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // CardChoiceSupport.GetSpec: RangeSpec(Exhaust, Hand, 0, Cards, every hand card); an empty hand
    // still asks an (empty) choice.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[options.Length];
        int max = (int)CardEffects.Var(x, 0);
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.Exhaust, Pile.Hand, options,
                0, max, selected, out int chosen))
        {
            return;
        }
        Kernel.ExhaustSelected(ref x, Pile.Hand, selected[..chosen]);
    }
}

[ContentId("RESTLESSNESS")]
public sealed class Restlessness : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards", "Energy"];

    public override void OnPlay(ref Exec x)
    {
        // Native IsOnlyCardInHand: this card is in the play pile during resolution, so the condition
        // is an empty hand. Each of the Cards draws is a separate native Draw(1) call.
        if (x.B.HandCount != 0)
            return;
        // CardDrawCardMirrors.RestlessnessOnPlay draws the whole Cards amount in one call.
        Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);
        if (x.NeedsChoice)
            return;
        Kernel.GainEnergy(ref x, CardEffects.Var(x, 1));
    }
}

[ContentId("SCRAWL")]
public sealed class Scrawl : CardDef
{
    // CardDrawCardMirrors.ScrawlOnPlay / native: draw up to the hand size limit.
    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, Math.Max(0, x.B.MaxHandSize - x.B.HandCount), fromHandDraw: false);
}

[ContentId("SECRET_TECHNIQUE")]
public sealed class SecretTechnique : CardDef
{
    // GetSpec: MoveToHand, Draw, 1, the draw pile's Skill cards; no options asks nothing.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        int start = Piles.Start(x.B, Pile.Draw);
        Span<int> options = stackalloc int[x.B.DrawCount];
        int count = 0;
        for (int index = 0; index < x.B.DrawCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[start + index];
            if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Skill)
                options[count++] = index;
        }
        Span<int> selected = stackalloc int[Math.Max(1, count)];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToHand, Pile.Draw, options[..count],
                1, selected, out int chosen))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Draw, selected[..chosen], Pile.Hand, PilePosition.Bottom);
    }
}

[ContentId("SECRET_WEAPON")]
public sealed class SecretWeapon : CardDef
{
    // GetSpec: MoveToHand, Draw, 1, the draw pile's Attack cards; no options asks nothing.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        int start = Piles.Start(x.B, Pile.Draw);
        Span<int> options = stackalloc int[x.B.DrawCount];
        int count = 0;
        for (int index = 0; index < x.B.DrawCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[start + index];
            if (Kernel.ResolveCardType(x.B, x.R, card) == CardType.Attack)
                options[count++] = index;
        }
        Span<int> selected = stackalloc int[Math.Max(1, count)];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToHand, Pile.Draw, options[..count],
                1, selected, out int chosen))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Draw, selected[..chosen], Pile.Hand, PilePosition.Bottom);
    }
}

[ContentId("SHOCKWAVE")]
public sealed class Shockwave : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Power"];

    public override void OnPlay(ref Exec x)
    {
        // CardOnPlaySupport `case Shockwave`: per hittable enemy in roster order, Weak then Vulnerable.
        int amount = (int)CardEffects.Var(x, 0);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, owner))
                continue;
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.WeakPower>(), amount, Limits.PlayerOwner);
            Kernel.ApplyPower(ref x, owner, Registry.PowerId<Powers.VulnerablePower>(), amount, Limits.PlayerOwner);
        }
    }
}

[ContentId("THE_GAMBIT")]
public sealed class TheGambit : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.TheGambitPower>(),
            1, Limits.PlayerOwner);
    }
}

[ContentId("THINKING_AHEAD")]
public sealed class ThinkingAhead : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, CardEffects.Var(x, 0), fromHandDraw: false);

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: MoveToDrawTop, Hand, 1, every hand card.
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[Math.Max(1, options.Length)];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToDrawTop, Pile.Hand, options,
                1, selected, out int chosen))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Hand, selected[..chosen], Pile.Draw, PilePosition.Top);
    }
}

[ContentId("ULTIMATE_DEFEND")]
public sealed class UltimateDefend : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
        => CardEffects.GeneralBlock(ref x, 0);
}

[ContentId("ANOINTED")]
public sealed class Anointed : CardDef
{
    // CardSelectionCardMirrors.AnointedOnPlay: hand-space many rare cards from the draw pile taken by
    // TakeRandom (a full UnstableShuffle over the rare list, then the first cards) and put into hand.
    public override void OnPlay(ref Exec x)
    {
        int space = x.B.MaxHandSize - x.B.HandCount;
        if (space <= 0 || x.B.DrawCount == 0)
            return;
        Span<int> rares = stackalloc int[x.B.DrawCount];
        int rareCount = 0;
        for (int index = 0; index < x.B.DrawCount; index++)
        {
            Card card = Piles.At(ref x.B, Pile.Draw, index);
            if (x.R.Card(card.Id, card.Upgrade).Rarity == CardRarity.Rare)
                rares[rareCount++] = index;
        }
        if (rareCount == 0)
            return;
        rares = rares[..rareCount];
        ref RngState rng = ref x.B.RngStream(RngStream.CombatCardSelection);
        for (int n = rares.Length; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (rares[k], rares[n]) = (rares[n], rares[k]);
        }
        int take = Math.Min(space, rares.Length);
        Kernel.MoveSelected(ref x.B, Pile.Draw, rares[..take], Pile.Hand, PilePosition.Bottom);
    }
}

[ContentId("HIDDEN_GEM")]
public sealed class HiddenGem : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Replay"];

    // CardSelectionCardMirrors.HiddenGemOnPlay: CombatCardSelection.NextItem over the playable draw
    // cards (Attack/Skill/Power preferred, else the others), then BaseReplayCount += Replay.
    public override void OnPlay(ref Exec x)
    {
        if (x.B.DrawCount == 0)
            return;
        Span<int> eligible = stackalloc int[x.B.DrawCount];
        Span<int> preferred = stackalloc int[x.B.DrawCount];
        int eligibleCount = 0;
        int preferredCount = 0;
        for (int index = 0; index < x.B.DrawCount; index++)
        {
            Card card = Piles.At(ref x.B, Pile.Draw, index);
            // CardModel.Type is dynamic (Mad Science takes its Tinker Time type), so the filter reads
            // the resolved type, not the frozen canonical one.
            CardType type = Kernel.ResolveCardType(x.B, x.R, card);
            if (card.HasKeyword(Keywords.Unplayable)
                || type is CardType.Curse or CardType.Quest
                || Kernel.EnchantedReplayCount(card) >= 1)
                continue;
            eligible[eligibleCount++] = index;
            if (type is CardType.Attack or CardType.Skill or CardType.Power)
                preferred[preferredCount++] = index;
        }
        ReadOnlySpan<int> pool = preferredCount > 0 ? preferred[..preferredCount] : eligible[..eligibleCount];
        if (pool.IsEmpty)
            return;
        int pick = pool[x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, pool.Length)];
        int replay = (int)CardEffects.Var(x, 0);
        ref Card chosen = ref Piles.At(ref x.B, Pile.Draw, pick);
        int next = chosen.ReplayCount + replay;
        if ((uint)next > byte.MaxValue)
            throw new CapacityException("Card.ReplayCount", byte.MaxValue);
        chosen.ReplayCount = (byte)next;
    }
}

[ContentId("THE_BOMB")]
public sealed class TheBomb : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Turns", "BombDamage"];

    // CardOnPlaySupport.Batch043's TheBomb case: AddPowerInstance<TheBombPower>(Turns) then
    // SetDamage(BombDamage). Amount is the turn count; the damage lives in Aux0.
    public override void OnPlay(ref Exec x)
    {
        ushort id = Registry.PowerId<Powers.TheBombPower>();
        int turns = (int)CardEffects.Var(x, 0);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, id, turns, Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        int index = Kernel.FindLastPower(x.B, Limits.PlayerOwner, id);
        if (index < 0)
            return;
        x.B.Powers[index].Aux0 = (int)CardEffects.Var(x, 1);
    }
}
