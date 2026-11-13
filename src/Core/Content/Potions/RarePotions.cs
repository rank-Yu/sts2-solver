using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Potions;

/// <summary>
/// Native <c>DistilledChaos</c>: <c>AutoPlayPotionMirrors.DistilledChaosOnUse</c> calls
/// <c>simulator.AutoPlayFromDrawPile(player, Repeat, CardPilePosition.Top)</c> with
/// <c>forceExhaust: false</c>, i.e. it moves up to Repeat cards from the draw-pile top to the play
/// pile and auto-plays them in order with their normal result piles.
/// </summary>
[ContentId("DISTILLED_CHAOS")]
public sealed class DistilledChaos : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat"];

    public override void OnUse(ref Exec x)
        => Kernel.AutoPlayFromDrawPile(ref x, (int)Kernel.PotionVar(x, this, 0), forceExhaust: false);
}

[ContentId("OROBIC_ACID")]
public sealed class OrobicAcid : PotionDef
{
    // One distinct Attack, then Skill, then Power from the character pool; the Attack/Skill/Power
    // union is the whole character pool, so the full pool is the exact closure.
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    public override void OnUse(ref Exec x)
    {
        Span<Card> generated = stackalloc Card[3];
        Span<Card> one = stackalloc Card[1];
        int count = 0;
        if (CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.Attack, 1, one) == 1)
            generated[count++] = one[0];
        if (CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.Skill, 1, one) == 1)
            generated[count++] = one[0];
        if (CardEffects.SampleCharacterCardsOfType(ref x.B, x.R, CardType.Power, 1, one) == 1)
            generated[count++] = one[0];
        // CardGenerationPotionMirrors.GenerateOrobicAcidCards: SetToFreeThisTurn on every generated
        // card, then AddGeneratedCardsToCombat(hand, Bottom) keeps that order.
        for (int index = 0; index < count; index++)
        {
            Kernel.MakeFreeThisTurn(x.R, ref generated[index]);
            Kernel.AddGeneratedCard(ref x, generated[index], Pile.Hand);
        }
    }
}

[ContentId("BEETLE_JUICE")]
public sealed class BeetleJuice : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["DamageDecrease", "Repeat"];

    // BeetleJuice when target != null: combat.Apply<ShrinkPower>(target, Repeat.IntValue, owner).
    public override void OnUse(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.ShrinkPower>(),
            (int)Kernel.PotionVar(x, this, 1), Limits.PlayerOwner);
    }
}

[ContentId("MAZALETHS_GIFT")]
public sealed class MazalethsGift : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["RitualPower"];

    // MazalethsGift: combat.Apply<RitualPower>(player, RitualPower, owner).
    public override void OnUse(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.RitualPower>(),
            (int)Kernel.PotionVar(x, this, 0), Limits.PlayerOwner);
}

[ContentId("SHACKLING_POTION")]
public sealed class ShacklingPotion : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];

    // ShacklingPotion: combat.ApplyTemporaryStrengthLoss<ShacklingPotionPower> for each living
    // hittable enemy with the Strength amount.
    public override void OnUse(ref Exec x)
    {
        int amount = (int)Kernel.PotionVar(x, this, 0);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, owner))
                continue;
            Kernel.ApplyTemporaryStrength(ref x, owner, Registry.PowerId<Powers.ShacklingPotionPower>(),
                amount, Limits.PlayerOwner, sign: -1);
        }
    }
}

[ContentId("GHOST_IN_A_JAR")]
public sealed class GhostInAJar : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["IntangiblePower"];

    // GhostInAJar: combat.Apply<IntangiblePower>(playerTarget, IntangiblePower, owner).
    public override void OnUse(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.IntangiblePower>(),
            (int)Kernel.PotionVar(x, this, 0), Limits.PlayerOwner);
}

[ContentId("LUCKY_TONIC")]
public sealed class LuckyTonic : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["BufferPower"];

    // LuckyTonic: combat.Apply<BufferPower>(playerTarget, BufferPower, owner).
    public override void OnUse(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.BufferPower>(),
            (int)Kernel.PotionVar(x, this, 0), Limits.PlayerOwner);
}

[ContentId("GIGANTIFICATION_POTION")]
public sealed class GigantificationPotion : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["GigantificationPower"];

    public override IReadOnlyList<string> AppliedPowers => ["GIGANTIFICATION_POWER"];

    public override void OnUse(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.GigantificationPower>(),
            (int)Kernel.PotionVar(x, this, 0), Limits.PlayerOwner);
}

/// <summary>
/// <c>EssenceOfDarkness</c>: channel one Dark orb per orb slot
/// (<c>OrbPotionMirrors.EssenceOfDarknessOnUse</c>). The count is the current queue capacity.
/// </summary>
[ContentId("ESSENCE_OF_DARKNESS")]
public sealed class EssenceOfDarkness : PotionDef
{
    public override void OnUse(ref Exec x)
    {
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("DARK_ORB"), x.B.OrbCapacity);
    }
}

/// <summary>
/// Native <c>FairyInABottle</c>: an automatic potion (<c>PotionOnUseSupport.CanSearch</c> is false),
/// so the search never offers an explicit use. Its <c>ShouldDie</c> prevents its owner's death and
/// <c>AfterPreventingDeath</c> consumes the slot and heals
/// <c>max(1, 30% of max HP)</c> (<c>FairyInABottleMirrors</c>).
/// </summary>
[ContentId("FAIRY_IN_A_BOTTLE")]
public sealed class FairyInABottle : PotionDef
{
    public override bool Searchable => false;

    // Never reached through the search; the effect runs from AfterPreventingDeath below.
    public override void OnUse(ref Exec x)
        => throw new UnsupportedException("现有搜索不提供 FAIRY_IN_A_BOTTLE 的显式使用。");

    public override bool ShouldDie(ref Exec x, Listener self, byte creature)
        => creature != self.Owner;

    public override void AfterPreventingDeath(ref Exec x, Listener self, byte creature)
    {
        // PotionOnUseMirrors consume the slot, then the native OnUse heals max(1, 30% max HP).
        int freeMask = x.B.Counter(Counter.EntropicFreePotionMask);
        int slotBit = 1 << self.Index;
        bool freeFromEntropic = (freeMask & slotBit) != 0;
        if (freeFromEntropic)
            x.B.SetCounter(Counter.EntropicFreePotionMask, freeMask & ~slotBit);
        ushort storedPotion = x.B.Potions[self.Index];
        if (storedPotion != x.R.RootPotion(self.Index))
        {
            x.B.AddCounter(Counter.GeneratedPotionUseCount, 1);
            Kernel.RecordGeneratedPotionStrategicCost(ref x.B,
                freeFromEntropic ? 0 : x.R.Potion((ushort)(storedPotion - 1)).StrategicCost, automatic: true);
        }
        x.B.Potions[self.Index] = 0;
        x.B.AddCounter(Counter.AutomaticPotionUseCount, 1);
        decimal amount = Math.Max(1m, x.B.Creature(creature).MaxHp * 0.3m);
        int hpBeforeRevive = x.B.Creature(creature).CurrentHp;
        Kernel.Heal(ref x, creature, amount);
        int restored = x.B.Creature(creature).CurrentHp - hpBeforeRevive;
        if (restored > 0)
        {
            x.B.AddCounter(Counter.DeathSavePotionHpRestored, restored);
            x.B.AddCounter(Counter.DeathSaveUseCount, 1);
        }
        // FairyInABottleMirrors.AfterPreventingDeath calls ICombatPredictionEffectSink.AfterPotionUsed
        // while the revived player lives; the automatic use must reach ReptileTrinket/BeltBuckle too.
        if (x.B.Player.IsAlive)
            HookDispatch.AfterPotionUsed(ref x, Id);
    }
}

/// <summary>
/// <c>CosmicConcoction</c>: <c>Cards</c> distinct colourless cards, each upgraded and added to hand
/// (<c>CardGenerationPotionMirrors.Generate</c> / <c>PotionOnUseSupport.Use</c> case CosmicConcoction).
/// </summary>
[ContentId("COSMIC_CONCOCTION")]
public sealed class CosmicConcoctionPotion : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.ColorlessAll];

    public override void OnUse(ref Exec x)
    {
        int count = (int)Kernel.PotionVar(x, this, 0);
        if (count <= 0)
            return;
        if (count > Limits.Cards)
            throw new CapacityException("GeneratedCards", Limits.Cards);
        Span<Card> selected = stackalloc Card[count];
        int sampled = Kernel.SampleCardPool(ref x.B, x.R, CardPools.ColorlessAll, count,
            distinct: true, RngStream.CombatCardGeneration, selected);
        for (int index = 0; index < sampled; index++)
        {
            Card card = selected[index];
            Kernel.UpgradeCard(x.R, ref card);
            if (!Kernel.AddGeneratedCard(ref x, card, Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// <c>PotOfGhouls</c>: creates <c>Cards</c> Soul cards and adds them to the hand bottom
/// (<c>PotionOnUseSupport.Use</c> case PotOfGhouls, CardGenerationResultKind.Fixed).
/// </summary>
[ContentId("POT_OF_GHOULS")]
public sealed class PotOfGhouls : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOUL", 0)];

    public override void OnUse(ref Exec x)
    {
        int count = (int)Kernel.PotionVar(x, this, 0);
        for (int index = 0; index < count; index++)
        {
            Card soul = Kernel.CreateCard(x.R, Registry.CardId<Cards.Necrobinder.Soul>());
            if (!Kernel.AddGeneratedCard(ref x, soul, Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("BOTTLED_POTENTIAL")]
public sealed class BottledPotential : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // BottledPotential: MoveHandToDrawPile(player), Shuffle(player), Draw(player, Cards).
    public override void OnUse(ref Exec x)
    {
        while (x.B.HandCount > 0)
        {
            if (!Kernel.MoveCard(ref x.B, Pile.Hand, 0, Pile.Draw, PilePosition.Bottom))
                break;
        }
        Kernel.Shuffle(ref x);
        Kernel.Draw(ref x, (int)Kernel.PotionVar(x, this, 0), fromHandDraw: false);
    }
}

[ContentId("DROPLET_OF_PRECOGNITION")]
public sealed class DropletOfPrecognition : PotionDef
{
    // DropletOfPrecognition is a no-op in PotionOnUseSupport.Use; its whole effect is
    // PotionChoiceSupport.GetSpec/Apply: ExactSpec(MoveToHand, Draw, the complete draw pile) and
    // AddToPile(selected, Hand, Bottom). The option order is the draw pile order (index 0 is the top,
    // which is also Pile.Draw's order). A card played into a full hand goes to the discard pile
    // inside AddToPile, which Kernel.MoveCard reproduces.
    public override void OnUse(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.DrawCount];
        for (int index = 0; index < x.B.DrawCount; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.MoveToHand, Pile.Draw,
                options, 1, 1, selected, out int chosen))
        {
            return;
        }
        Kernel.MoveSelected(ref x.B, Pile.Draw, selected[..chosen], Pile.Hand,
            PilePosition.Bottom);
    }
}

[ContentId("FRUIT_JUICE")]
public sealed class FruitJuice : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["MaxHp"];

    // FruitJuice: GainMaxHp(player, MaxHp).
    public override void OnUse(ref Exec x)
        => Kernel.GainMaxHp(ref x, Limits.PlayerOwner, Kernel.PotionVar(x, this, 0));
}

[ContentId("LIQUID_MEMORIES")]
public sealed class LiquidMemories : PotionDef
{
    // PotionChoiceSupport.GetSpec: ExactSpec(MoveToHandFreeThisTurn, Discard, every discard-pile
    // card), in discard-pile order. Apply marks each selected card free for this turn before
    // AddToPile(selected, Hand), which appends at the bottom.
    public override void OnUse(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.DiscardCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.MoveToHandFreeThisTurn,
                Pile.Discard, options, 1, 1, selected, out int chosen))
        {
            return;
        }
        int discardStart = Piles.Start(x.B, Pile.Discard);
        for (int index = 0; index < chosen; index++)
        {
            ref Card card = ref x.B.Cards[discardStart + selected[index]];
            Kernel.MakeFreeThisTurn(x.R, ref card);
        }
        Kernel.MoveSelected(ref x.B, Pile.Discard, selected[..chosen], Pile.Hand,
            PilePosition.Bottom);
    }
}

[ContentId("SHIP_IN_A_BOTTLE")]
public sealed class ShipInABottle : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    // ShipInABottle: GainBlock(player, Block, Unpowered), then Apply<BlockNextTurnPower>(player, Block, owner).
    public override void OnUse(ref Exec x)
    {
        Kernel.GainBlock(ref x, Limits.PlayerOwner, Kernel.PotionVar(x, this, 0),
            ValueProps.Unpowered, fromCard: false);
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.BlockNextTurnPower>(),
            (int)Kernel.PotionVar(x, this, 0), Limits.PlayerOwner);
    }
}

[ContentId("SOLDIERS_STEW")]
public sealed class SoldiersStew : PotionDef
{
    // SoldiersStew: BaseReplayCount++ for every card in containment whose canonical tags include Strike.
    public override void OnUse(ref Exec x)
    {
        for (int index = 0; index < x.B.CardCount; index++)
        {
            ref Card card = ref x.B.Cards[index];
            if ((x.R.Card(card.Id, card.Upgrade).Tags & CardTag.Strike) == 0)
                continue;
            card.ReplayCount = checked((byte)(card.ReplayCount + 1));
        }
    }
}

[ContentId("SNECKO_OIL")]
public sealed class SneckoOil : PotionDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // SneckoOil (DrawPotionMirrors.SneckoOilOnUse): draw Cards, then for each card in the resulting
    // hand that does not cost X and whose canonical cost is not negative, append
    // CardEnergyCost.SetThisTurnOrUntilPlayed(CombatEnergyCosts.NextInt(4)): an absolute local
    // modifier that expires at the turn end or when the card is played (both expiration bits).
    public override void OnUse(ref Exec x)
    {
        Kernel.Draw(ref x, (int)Kernel.PotionVar(x, this, 0), fromHandDraw: false);
        if (x.NeedsChoice)
            return;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref Card card = ref x.B.Cards[index];
            if ((card.Flags & CardFlags.CostsX) != 0 || card.BaseCost < 0)
                continue;
            int cost = x.B.RngStream(RngStream.CombatEnergyCosts).NextInt(4);
            if (card.EnergyModifierCount >= Limits.CardEnergyCostModifiers)
                throw new CapacityException("Card.EnergyModifiers", Limits.CardEnergyCostModifiers);
            card.EnergyModifiers[card.EnergyModifierCount] = CostModifier.Create(cost,
                CostType.Absolute,
                CostExpiration.EndOfTurn | CostExpiration.WhenPlayed, reduceOnly: false);
            card.EnergyModifierCount++;
        }
    }
}

/// <summary>
/// Native <c>EntropicBrew</c>: while the player has an open potion slot, draw a random potion from
/// <c>PotionFactory.CreateRandomPotionOutOfCombat</c> (the unfiltered character + shared pool) and
/// procure it with <c>freeFromEntropic: true</c>; stop when procurement fails
/// (<c>PotionOnUseSupport.Use</c> EntropicBrew case). The free flag matters only for the later
/// use's strategic cost, so procurement marks the slot in <see cref="Counter.EntropicFreePotionMask"/>.
/// </summary>
[ContentId("ENTROPIC_BREW")]
public sealed class EntropicBrew : PotionDef
{
    public override bool GeneratesOutOfCombatPotion => true;

    public override void OnUse(ref Exec x)
    {
        for (int guard = 0; guard < Limits.PotionSlots; guard++)
        {
            bool open = false;
            for (int slot = 0; slot < x.B.PotionSlotCount; slot++)
            {
                if (x.B.Potions[slot] == 0)
                {
                    open = true;
                    break;
                }
            }
            if (!open)
                return;
            ushort potion = Kernel.GenerateRandomPotionOutOfCombat(ref x.B, x.R);
            if (!Kernel.TryProcurePotion(ref x.B, x.R, potion, freeFromEntropic: true))
                return;
        }
    }
}
