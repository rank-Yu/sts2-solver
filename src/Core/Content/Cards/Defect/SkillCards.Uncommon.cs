using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Defect;

[ContentId("COMPACT")]
public sealed class Compact : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("FUEL", 0), ("FUEL", 1)];

    // CardEffectSpecRegistry.Compact: block, then transform every transformable Status card in hand
    // into Fuel (upgraded when Compact is upgraded). CardChoiceSupport.TransformCardBatch removes all
    // originals before inserting the replacements.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        if (!Registry.TryCard("FUEL", out ushort fuel))
            throw new UnsupportedException("压缩变换需要 FUEL 的 Sts2Solver 定义。");
        Span<int> statuses = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            Card card = x.B.Cards[index];
            if ((card.Keywords & Keywords.Eternal) != 0)
                continue;
            if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Status)
                continue;
            statuses[count++] = index;
        }
        if (count == 0)
            return;
        Kernel.TransformCards(ref x, Pile.Hand, statuses[..count],
            Kernel.CreateCard(x.R, fuel), x.Card.Upgrade > 0 ? 1 : 0);
    }
}

[ContentId("GLACIER")]
public sealed class Glacier : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("FROST_ORB"), 2);
    }
}

[ContentId("GLASSWORK")]
public sealed class Glasswork : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrb(ref x, Kernel.OrbId("GLASS_ORB"));
    }
}

[ContentId("FUSION")]
public sealed class Fusion : CardDef
{
    public override void OnPlay(ref Exec x)
        => Kernel.ChannelOrb(ref x, Kernel.OrbId("PLASMA_ORB"));
}

[ContentId("CHILL")]
public sealed class Chill : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        // OrbCardMirrors.ChillOnPlay: one Frost per hittable enemy, counted when the card resolves.
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (Kernel.IsHittable(x.B, owner))
                count++;
        }
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("FROST_ORB"), count);
    }
}

[ContentId("CHAOS")]
public sealed class Chaos : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat"];

    public override void OnPlay(ref Exec x)
    {
        // Killing the last Dampen caster can restore this card's upgrade during a channel.
        for (int index = 0; index < (int)CardEffects.Var(x, 0); index++)
        {
            ushort orb = Kernel.RandomOrb(ref x.B);
            // Native Chaos.OnPlay (0.113.0) draws every random orb even when OrbCmd.Channel
            // has no slot available. Only an unresolved choice suspends the loop.
            Kernel.ChannelOrb(ref x, orb);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("DARKNESS")]
public sealed class Darkness : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        Kernel.ChannelOrb(ref x, Kernel.OrbId("DARK_ORB"));
        if (x.NeedsChoice)
            return;
        // OrbCardMirrors.DarknessOnPlay: every Dark orb in the resulting queue triggers Passive once
        // (twice when upgraded). OrbCmd.Passive, so no ModifyOrbPassiveTriggerCounts.
        Span<int> dark = stackalloc int[Limits.Orbs];
        int count = DarkIndices(x.B, dark);
        int triggers = x.Card.Upgrade > 0 ? 2 : 1;
        for (int index = 0; index < count; index++)
        {
            for (int trigger = 0; trigger < triggers; trigger++)
            {
                Kernel.OrbPassiveAt(ref x, dark[index]);
                if (x.NeedsChoice)
                    return;
            }
        }
    }

    private static int DarkIndices(in Battle battle, Span<int> output)
    {
        if (!Registry.TryOrb("DARK_ORB", out ushort id))
            return 0;
        int count = 0;
        for (int index = 0; index < battle.OrbCount; index++)
        {
            if (battle.Orbs[index].Id == id)
                output[count++] = index;
        }
        return count;
    }
}

[ContentId("TEMPEST")]
public sealed class Tempest : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        int count = Kernel.ResolveEnergyXValue(ref x);
        if (x.Card.Upgrade > 0)
            count++;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("LIGHTNING_ORB"), count);
    }
}

[ContentId("OVERCLOCK")]
public sealed class Overclock : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("BURN", 0)];

    // Draw is inferred; CardEffectSpecRegistry's generation adds one Burn to the discard pile.
    public override void OnPlay(ref Exec x)
    {
        Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);
        if (x.NeedsChoice)
            return;
        if (!Registry.TryCard("BURN", out ushort burn))
            throw new UnsupportedException("过载生成需要 BURN 的 Sts2Solver 定义。");
        Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, burn), Pile.Discard);
    }
}

[ContentId("SHADOW_SHIELD")]
public sealed class ShadowShield : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrb(ref x, Kernel.OrbId("DARK_ORB"));
    }
}

[ContentId("BOOT_SEQUENCE")]
public sealed class BootSequence : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralBlock(ref x, 0);
}

[ContentId("SKIM")]
public sealed class Skim : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
        => Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);
}

[ContentId("DOUBLE_ENERGY")]
public sealed class DoubleEnergy : CardDef
{
    // CardOnPlaySupport.DoubleEnergy: GainEnergy(playerState.Energy) after the card's own cost.
    public override void OnPlay(ref Exec x) => Kernel.GainEnergy(ref x, x.B.Energy);
}

[ContentId("SCAVENGE")]
public sealed class Scavenge : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: Exhaust, Hand, 1, every hand card; BuildRequiredEmptyChoice asks an empty choice when
        // the hand is empty (ResolveNestedCardChoice applies it directly during an auto-play). The
        // EnergyNextTurn application is ApplyPostChoiceEffects, after the exhaust.
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        int chosen = 0;
        if (options.IsEmpty)
        {
            if (!x.CurrentPlayIsAutoPlay
                && !Kernel.ChooseCardsInRange(ref x, ChoiceEffect.Exhaust, Pile.Hand, options, 0, 0,
                    selected, out chosen))
            {
                return;
            }
        }
        else if (!Kernel.ChooseCards(ref x, ChoiceEffect.Exhaust, Pile.Hand, options, 1, selected,
                     out chosen))
        {
            return;
        }
        if (chosen > 0)
            Kernel.ExhaustSelected(ref x, Pile.Hand, selected[..chosen]);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.EnergyNextTurnPower>(), 0);
    }
}

[ContentId("FIGHT_THROUGH")]
public sealed class FightThrough : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("WOUND", 0)];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        if (!Registry.TryCard("WOUND", out ushort id))
            throw new UnsupportedException("突破需要 WOUND 的 Sts2Solver 定义。");
        for (int index = 0; index < 2; index++)
        {
            if (!Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, id), Pile.Discard))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("WHITE_NOISE")]
public sealed class WhiteNoise : CardDef
{
    public override bool MayGenerateIdentityContent => true;

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterPowers];

    // CardGenerationCardMirrors.WhiteNoiseOnPlay: one free distinct Power card to hand.
    public override void OnPlay(ref Exec x)
    {
        Span<Card> generated = stackalloc Card[1];
        if (CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.Power, 1, generated,
                CardPools.CharacterPowers) == 0)
        {
            return;
        }
        Kernel.MakeFreeThisTurn(x.R, ref generated[0]);
        Kernel.AddGeneratedCard(ref x, generated[0], Pile.Hand);
    }
}

[ContentId("SYNCHRONIZE")]
public sealed class Synchronize : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "CalculationExtra"];

    // CardOnPlaySupport.Synchronize: CalculationBase + CalculationExtra × distinct orb ids of
    // temporary Focus (SynchronizePower).
    public override void OnPlay(ref Exec x)
    {
        int amount = (int)CardEffects.Var(x, 0)
            + (int)CardEffects.Var(x, 1) * Kernel.DistinctOrbCount(x.B);
        Kernel.ApplyTemporaryFocus(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.SynchronizePower>(),
            amount, Limits.PlayerOwner, sign: 1);
    }
}
