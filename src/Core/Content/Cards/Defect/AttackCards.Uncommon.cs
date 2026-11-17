using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Defect;

[ContentId("SYNTHESIS")]
public sealed class Synthesis : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardEffectSpecRegistry.Synthesis: attack, then one FreePowerPower.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.FreePowerPower>(), 1,
            Limits.PlayerOwner);
    }
}

[ContentId("TESLA_COIL")]
public sealed class TeslaCoil : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Span<int> lightning = stackalloc int[Limits.Orbs];
        if (!Registry.TryOrb("LIGHTNING_ORB", out ushort id))
            return;
        int count = 0;
        for (int index = 0; index < x.B.OrbCount; index++)
        {
            if (x.B.Orbs[index].Id == id)
                lightning[count++] = index;
        }
        int triggers = x.Card.Upgrade > 0 ? 2 : 1;
        for (int index = 0; index < count; index++)
        {
            for (int trigger = 0; trigger < triggers; trigger++)
            {
                Kernel.OrbPassiveAt(ref x, lightning[index], x.Target);
                if (x.NeedsChoice)
                    return;
            }
        }
    }
}

[ContentId("REFRACT")]
public sealed class Refract : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat", "Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.Attack(ref x, CardEffects.Var(x, 1), hitCount: 2);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("GLASS_ORB"), (int)CardEffects.Var(x, 0));
    }
}

[ContentId("NULL")]
public sealed class Null : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "WeakPower"];

    public override void OnPlay(ref Exec x)
    {
        // Native Null.OnPlay is attack, Weak, then channel. The channel can evoke a Dark Orb whose
        // ThunderPower kill rewrites Waterfall Giant and strips its powers, so Weak must be applied
        // before the channel for the death cleanup to see it.
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack and
        // before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 1);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrb(ref x, Kernel.OrbId("DARK_ORB"));
    }
}

[ContentId("ROCKET_PUNCH")]
public sealed class RocketPunch : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers settles newly dead enemies after the mirror attack and
        // before the card-specific draw compensation; HittableEnemies/Hellraiser read that phase.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        Kernel.Draw(ref x, (int)CardEffects.Var(x, 1), fromHandDraw: false);
    }

    // AfterCardGeneratedForCombatMirrors.HandleRocketPunch: a generated Status lowers this card's cost
    // by 1 until played. All generated cards in Sts2Solver come from the player.
    public override void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position, byte creator)
    {
        if (creator != Limits.PlayerOwner)
            return;
        if ((uint)position >= (uint)x.B.CardCount)
            throw new UnsupportedException("生成牌触发时该牌已不在牌堆。");
        Card generated = x.B.Cards[position];
        if (Kernel.ResolveCardType(x.B, x.R, generated) != CardType.Status)
            return;
        if ((uint)self.Index >= (uint)x.B.CardCount)
            throw new UnsupportedException("火箭拳在生成牌结算前离开了牌堆。");
        Kernel.AddEnergyCostModifier(ref x.B.Cards[self.Index], -1, CostType.Relative,
            CostExpiration.WhenPlayed, reduceOnly: false);
    }
}

[ContentId("FTL")]
public sealed class Ftl : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "PlayMax", "Cards"];

    // CardDrawCardMirrors.FtlOnPlay: attack, then draw while fewer than PlayMax cards were played.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers settles newly dead enemies after the mirror attack and
        // before the card-specific draw compensation; HittableEnemies/Hellraiser read that phase.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        if (x.B.Counter(Counter.CardsPlayedThisTurn) < (int)CardEffects.Var(x, 1))
            Kernel.Draw(ref x, (int)CardEffects.Var(x, 2), fromHandDraw: false);
    }
}

[ContentId("SUNDER")]
public sealed class Sunder : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Energy"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.Sunder: WasCardKill on the chosen target.
        if (x.CardKilledTarget)
            Kernel.GainEnergy(ref x, CardEffects.Var(x, 1));
    }
}

[ContentId("SCRAPE")]
public sealed class Scrape : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    // CardDrawCardMirrors.ScrapeOnPlay: attack, draw Cards, discard the drawn cards that are not free.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int request = (int)CardEffects.Var(x, 1);
        // Draw returns one entry per requested draw, including the same CardModel drawn again after a
        // draw hook put it back into the draw pile; the batch identity range is sized by that request.
        if (request < 0 || request > Limits.Cards)
            throw new CapacityException("DrawnCards", Limits.Cards);
        int capacity = request;
        Span<Card> drawn = stackalloc Card[capacity];
        Span<byte> outerMarks = stackalloc byte[capacity];
        Span<byte> markOf = stackalloc byte[capacity];
        int markBase = Kernel.ReserveTransientMarks(ref x, capacity, out int markStart);
        int drawnCount = 0;
        try
        {
            drawnCount = Kernel.DrawIntoMarked(ref x, request, fromHandDraw: false, drawn,
                outerMarks, markOf, markStart);
            if (x.NeedsChoice)
                return;
            Span<int> chosen = stackalloc int[drawnCount];
            int discard = 0;
            for (int index = 0; index < drawnCount; index++)
            {
                // The existing engine filters the live CardModel list after the whole draw, so a
                // draw hook that changed the card's cost is visible here. The mark locates that same
                // card even if the hook moved it; the pre-mark value is only used in the error path.
                if (!Kernel.TryFindUniqueTransientMark(x.B, markOf[index], out Pile pile,
                        out int position))
                {
                    throw new UnsupportedException(
                        $"Scrape 抽到的 {Registry.Card(drawn[index].Id).NativeId}+{drawn[index].Upgrade} "
                        + $"在费用判定前失去瞬态标记（mark={markOf[index]}）。");
                }
                Card card = Piles.At(ref x.B, pile, position);
                int cost = Kernel.EnergyCostValueWithModifiers(x.B, x.R, card, Pile.Hand);
                if ((card.Flags & CardFlags.CostsX) != 0 || cost != 0)
                    chosen[discard++] = index;
            }
            if (discard > 0)
            {
                Kernel.DiscardDrawn(ref x, drawn[..drawnCount], outerMarks[..drawnCount],
                    markOf[..drawnCount], markStart, chosen[..discard]);
            }
        }
        finally
        {
            Kernel.RestoreDrawnTransientMarks(ref x.B, markOf[..drawnCount],
                outerMarks[..drawnCount]);
            Kernel.ReleaseTransientMarks(ref x, markBase, markStart, capacity);
        }
    }
}
