using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Event;

[ContentId("BYRD_SWOOP")]
public sealed class ByrdSwoop : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // Native OnPlay is a plain DamageCmd.Attack; CardOnPlayInferrer infers the attack recipe.
    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("CALTROPS")]
public sealed class Caltrops : CardDef
{
    public override IReadOnlyList<string> VarNames => ["ThornsPower"];

    // CorePowerSupport.ApplyCardPowers: combat.Apply<ThornsPower>(owner, ThornsPower, owner).
    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.ThornsPower>(), 0);
}

[ContentId("CLASH")]
public sealed class Clash : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardIsPlayableMirrors.HandleClash: playable only while every hand card is an Attack.
    public override bool IsPlayable(in Battle battle, Rules rules, in Card card)
    {
        for (int index = 0; index < battle.HandCount; index++)
        {
            ref readonly Card handCard = ref battle.Cards[index];
            if (Kernel.ResolveCardType(battle, rules, handCard) != CardType.Attack)
                return false;
        }
        return true;
    }

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("DISTRACTION")]
public sealed class Distraction : CardDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    // CardGenerationCardMirrors.DistractionOnPlay: one distinct unlocked character Skill at upgrade 0,
    // free this turn, added to hand. No choice is recorded.
    public override void OnPlay(ref Exec x)
    {
        Span<Card> generated = stackalloc Card[1];
        int count = CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.Skill, 1,
            generated);
        for (int index = 0; index < count; index++)
        {
            Kernel.MakeFreeThisTurn(x.R, ref generated[index]);
            if (!Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand))
                return;
        }
    }
}

[ContentId("DUAL_WIELD")]
public sealed class DualWield : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // CardChoiceSupport.GetSpec: Duplicate, Hand, 1, Attack or Power hand cards.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            CardType type = Kernel.ResolveCardType(x.B, x.R, card);
            if (type is CardType.Attack or CardType.Power)
                options[count++] = index;
        }
        if (count == 0)
            return;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Duplicate, Pile.Hand, options[..count], 1,
                selected, out int chosen))
        {
            return;
        }
        if (chosen == 0)
            return;
        Card original = Piles.At(ref x.B, Pile.Hand, selected[0]);
        int copies = (int)CardEffects.Var(x, 0);
        for (int index = 0; index < copies; index++)
        {
            Card clone = Kernel.CloneCard(original);
            if (!Kernel.AddGeneratedCard(ref x, clone, Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("ENLIGHTENMENT")]
public sealed class Enlightenment : CardDef
{
    // CardPileOnPlaySupport.ReduceHandCosts: every hand card is set to 1, reduce-only; upgraded for
    // the whole combat, otherwise this turn or until played.
    public override void OnPlay(ref Exec x)
    {
        CostExpiration expiration = x.Card.Upgrade > 0
            ? CostExpiration.EndOfCombat
            : CostExpiration.EndOfTurnOrWhenPlayed;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            Kernel.AddEnergyCostModifier(ref x.B.Cards[index], 1, CostType.Absolute,
                expiration, reduceOnly: true);
        }
    }
}

[ContentId("ENTRENCH")]
public sealed class Entrench : CardDef
{
    // BespokeCardMirrors.EntrenchOnPlay: gain block equal to the current block with
    // Unpowered | Move and the card as source.
    public override void OnPlay(ref Exec x)
        => Kernel.GainBlock(ref x, Limits.PlayerOwner, x.B.Creature(Limits.PlayerOwner).Block,
            ValueProps.Unpowered | ValueProps.Move, fromCard: true);
}

[ContentId("EXTERMINATE")]
public sealed class Exterminate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];

    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x, CardEffects.Var(x, 0), (int)CardEffects.Var(x, 1));
}

[ContentId("FEEDING_FRENZY")]
public sealed class FeedingFrenzy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];

    // CardPowerOnPlaySupport.FeedingFrenzy: Strength first, then the temporary power. The temporary
    // power's native BeforeApplied is not run by combat.Apply, so this is not ApplyTemporaryStrength.
    public override void OnPlay(ref Exec x)
    {
        int amount = (int)CardEffects.Var(x, 0);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.StrengthPower>(), amount, Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.FeedingFrenzyPower>(), amount, Limits.PlayerOwner);
    }
}

[ContentId("HELLO_WORLD")]
public sealed class HelloWorld : CardDef
{
    public override IReadOnlyList<string> AppliedPowers => ["HELLO_WORLD_POWER"];

    // CardPowerOnPlaySupport.HelloWorld: combat.Apply<HelloWorldPower>(owner, 1, owner).
    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.HelloWorldPower>(), 1, Limits.PlayerOwner);
}

[ContentId("METAMORPHOSIS")]
public sealed class Metamorphosis : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAttacks];

    // CardGenerationCardMirrors.MetamorphosisOnPlay: GetUnlockedCharacterAttacksForCombat (with
    // replacement, one NextItem per card), each free this combat, into the draw pile at a random
    // position in generation order.
    public override void OnPlay(ref Exec x)
    {
        int count = (int)CardEffects.Var(x, 0);
        if (count > Limits.Cards)
            throw new CapacityException("Metamorphosis.Generated", Limits.Cards);
        Span<Card> generated = stackalloc Card[Math.Max(0, count)];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAttacks, count,
            distinct: false, RngStream.CombatCardGeneration, generated);
        for (int index = 0; index < sampled; index++)
        {
            Kernel.MakeFreeThisCombat(x.R, ref generated[index]);
            if (!Kernel.AddGeneratedCard(ref x, generated[index], Pile.Draw, PilePosition.Random))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("OUTMANEUVER")]
public sealed class Outmaneuver : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];

    // CardPowerOnPlaySupport.Outmaneuver: combat.Apply<EnergyNextTurnPower>(owner, Energy, owner).
    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.EnergyNextTurnPower>(), 0);
}

[ContentId("PECK")]
public sealed class Peck : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];

    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x, CardEffects.Var(x, 0), (int)CardEffects.Var(x, 1));
}

[ContentId("REBOUND")]
public sealed class Rebound : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override IReadOnlyList<string> AppliedPowers => ["REBOUND_POWER"];

    // Inferred attack; CardEffectSpecRegistry.Rebound applies ReboundPower 1 afterwards.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.ReboundPower>(), 1, Limits.PlayerOwner);
    }
}

[ContentId("RIP_AND_TEAR")]
public sealed class RipAndTear : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];

    // RandomTargetAttackCardMirrors.RipAndTearOnPlay: two hits on random opponents (each hit rerolls).
    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x, CardEffects.Var(x, 0), hitCount: (int)CardEffects.Var(x, 1));
}

[ContentId("SQUASH")]
public sealed class Squash : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower"];

    // Inferred attack; CorePowerSupport.ApplyCardPowers applies Vulnerable after the enemy-death pass.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

[ContentId("STACK")]
public sealed class Stack : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "CalculationExtra", "CalculatedBlock"];

    // CalculatedVarSpecRegistry's Stack multiplier is the discard pile count:
    // CalculatedBlock = CalculationBase + CalculationExtra × count; GeneralCardMirrors' block recipe
    // uses CalculatedBlock.Props (Move) with the card as source.
    public override void OnPlay(ref Exec x)
    {
        decimal block = CardEffects.Var(x, 0)
            + CardEffects.Var(x, 1) * x.B.DiscardCount;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, block, ValueProps.Move, fromCard: true);
    }
}

[ContentId("TORIC_TOUGHNESS")]
public sealed class ToricToughness : CardDef
{
    // The card creates an instanced power whose rows can be removed in the same listener pass; the
    // root must enable the occurrence-tracked power passes (as the Orbit card does).
    public override bool IsIdentityContent => true;

    public override IReadOnlyList<string> VarNames => ["Turns", "Block"];

    // CorePowerSupport.ApplyCardPowers.ToricToughness: gain the Block variable's block, then add an
    // instanced ToricToughnessPower with Turns and store the settled block amount on that instance.
    public override void OnPlay(ref Exec x)
    {
        decimal gained = Kernel.GainBlock(ref x, Limits.PlayerOwner,
            CardEffects.Var(x, 1), ValueProps.Move, fromCard: true);
        if (x.NeedsChoice)
            return;
        int turns = (int)CardEffects.Var(x, 0);
        ushort id = Registry.PowerId<Powers.ToricToughnessPower>();
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, id, turns, Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        int index = Kernel.FindLastPower(x.B, Limits.PlayerOwner, id);
        if (index < 0)
            return;
        Kernel.SetInstanceBlockValue(ref x.B, index, gained);
    }
}

/// <summary>
/// <c>MadScience</c> (TinkerTime event): the saved <c>TinkerTimeType</c> selects Attack (damage),
/// Skill (block) or Power (one of three setup powers), and <c>TinkerTimeRider</c> adds the rider
/// chosen by the event. Existing engine: <c>CardGenerationCardMirrors.MadScienceOnPlay</c> reads
/// both saved properties; the rule table is canonical Attack/No rider, so Sts2Solver packs the two saved
/// enum values into <see cref="Card.Private"/> and projects them at admission.
/// </summary>
[ContentId("MAD_SCIENCE")]
public sealed class MadScience : CardDef
{
    // Native TinkerTime.RiderEffect values.
    public const int RiderNone = 0;
    public const int RiderSapping = 1;
    public const int RiderViolence = 2;
    public const int RiderChoking = 3;
    public const int RiderEnergized = 4;
    public const int RiderWisdom = 5;
    public const int RiderChaos = 6;
    public const int RiderExpertise = 7;
    public const int RiderCurious = CardPrivateRiders.MadScienceCurious;
    public const int RiderImprovement = 9;

    public override IReadOnlyList<string> VarNames =>
    [
        "Damage", "Block", "SappingWeak", "SappingVulnerable", "ViolenceHits", "ChokingDamage",
        "EnergizedEnergy", "WisdomCards", "ExpertiseStrength", "ExpertiseDexterity", "CuriousReduction",
    ];

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    public override bool HasDynamicCardType => true;

    public override int PrivateRider(in Card card) => RiderOf(card);

    public static short Encode(int type, int rider) => checked((short)((byte)type | ((byte)rider << 8)));

    public static CardType TypeOf(in Card card) => (CardType)(card.Private & 0xFF);

    public static int RiderOf(in Card card) => (card.Private >> 8) & 0xFF;

    public override CardType ResolveCardType(in Battle battle, Rules rules, in Card card)
        => TypeOf(card);

    public override TargetType ResolveTargetType(in Battle battle, Rules rules, in Card card)
        => TypeOf(card) == CardType.Attack ? TargetType.AnyEnemy : TargetType.Self;

    public override void OnPlay(ref Exec x)
    {
        CardType type = TypeOf(x.Card);
        int rider = RiderOf(x.Card);
        switch (type)
        {
            case CardType.Attack:
                CardEffects.Attack(ref x, CardEffects.Var(x, 0),
                    rider == RiderViolence ? (int)CardEffects.Var(x, 4) : 1);
                if (x.NeedsChoice)
                    return;
                ApplyRider(ref x, rider);
                break;
            case CardType.Skill:
                Kernel.GainBlock(ref x, Limits.PlayerOwner, CardEffects.Var(x, 1),
                    ValueProps.Move, fromCard: true);
                if (x.NeedsChoice)
                    return;
                ApplyRider(ref x, rider);
                break;
            case CardType.Power:
                ApplyPowerRider(ref x, rider);
                break;
            default:
                throw new UnsupportedException($"MadScience 的 TinkerTimeType {(int)type} 不是可打出的类型。");
        }
    }

    private static void ApplyRider(ref Exec x, int rider)
    {
        switch (rider)
        {
            case RiderSapping:
                CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 2);
                if (x.NeedsChoice)
                    return;
                CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 3);
                break;
            case RiderChoking:
                CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.StranglePower>(), 5);
                break;
            case RiderEnergized:
                Kernel.GainEnergy(ref x, CardEffects.Var(x, 6));
                break;
            case RiderWisdom:
                Kernel.Draw(ref x, CardEffects.Var(x, 7), fromHandDraw: false);
                break;
            case RiderChaos:
            {
                Span<Card> generated = stackalloc Card[1];
                int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterAll, 1,
                    distinct: true, RngStream.CombatCardGeneration, generated);
                if (count == 0)
                    return;
                Kernel.MakeFreeThisTurn(x.R, ref generated[0]);
                Kernel.AddGeneratedCard(ref x, generated[0], Pile.Hand);
                break;
            }
        }
    }

    private static void ApplyPowerRider(ref Exec x, int rider)
    {
        switch (rider)
        {
            case RiderExpertise:
                Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.StrengthPower>(), (int)CardEffects.Var(x, 8),
                    Limits.PlayerOwner);
                if (x.NeedsChoice)
                    return;
                Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.DexterityPower>(), (int)CardEffects.Var(x, 9),
                    Limits.PlayerOwner);
                break;
            case RiderCurious:
                Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.CuriousPower>(), (int)CardEffects.Var(x, 10),
                    Limits.PlayerOwner);
                break;
            case RiderImprovement:
            {
                // CardGenerationCardMirrors.MadScienceOnPlay records the growth reward only when the
                // ImprovementPower amount actually increased.
                int applied = Kernel.ApplyPower(ref x, Limits.PlayerOwner,
                    Registry.PowerId<Powers.ImprovementPower>(), 1, Limits.PlayerOwner);
                if (applied > 0)
                    GrowthRewards.Add(ref x.B, GrowthRewardSource.MadScience);
                break;
            }
        }
    }
}
