using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>
/// Native <c>DarkShacklesPower</c> (a negative <c>TemporaryStrengthPower</c>): the application path
/// also changes Strength by -amount (DarkShackles card), and the power is removed at its owner's side
/// turn end while restoring that Strength.
/// </summary>
[ContentId("DARK_SHACKLES_POWER")]
public sealed class DarkShacklesPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override string? InternallyAppliedPowerId => "STRENGTH_POWER";

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsTemporaryPower => true;

    // EndTurnPowerSupport.TriggerRegular: case TemporaryStrengthPower when the owner participates.
    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.RetireTemporaryStrength(ref x, self);
    }
}

/// <summary>
/// Native <c>FastenPower</c>: additive block for its owner's powered card/monster-move block, and
/// only for card sources tagged Defend. The native <c>AfterModifyingBlockAmount</c> body is a visual
/// flash, which the existing engine registers as ignored.
/// </summary>
[ContentId("FASTEN_POWER")]
public sealed class FastenPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyBlockAdditive(ref Exec x, Listener self, in BlockContext block, decimal amount)
    {
        if (block.Target != self.Owner)
            return 0m;
        if ((block.Props & ValueProps.Move) == 0 || (block.Props & ValueProps.Unpowered) != 0)
            return 0m;
        if (block.HasCardSource
            && (x.R.Card(block.CardSource.Id, block.CardSource.Upgrade).Tags & CardTag.Defend) == 0)
        {
            return 0m;
        }
        return x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// Native <c>NoBlockPower</c>: powered block from a card source becomes zero for its owner, and the
/// power loses one stack at the end of the enemy side (CorePowerSupport.TickDurations).
/// </summary>
[ContentId("NO_BLOCK_POWER")]
public sealed class NoBlockPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool TicksAtEnemySideEnd => true;

    public override decimal ModifyBlockMultiplicative(ref Exec x, Listener self, in BlockContext block, decimal amount)
    {
        if (block.Target != self.Owner)
            return 1m;
        if ((block.Props & ValueProps.Unpowered) != 0)
            return 1m;
        if (!block.HasCardSource)
            return 1m;
        return 0m;
    }
}

/// <summary>
/// Native <c>PrepTimePower</c>: at its owner's side turn start it applies <c>Amount</c> Vigor to the
/// owner. The existing engine resolves it in PersistentPowerSupport.TriggerOwnerAfterSideTurnStart.
/// </summary>
[ContentId("PREP_TIME_POWER")]
public sealed class PrepTimePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void OwnerAfterSideTurnStart(ref Exec x, Listener self)
    {
        Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<VigorPower>(),
            x.B.Powers[self.Index].Amount, self.Owner);
    }
}

/// <summary>
/// Native <c>TheGambitPower</c>: a single-stack debuff that removes itself and kills its owner when
/// the owner takes unblocked damage from a powered attack (AfterDamageReceivedMirrors.HandleTheGambitPower).
/// </summary>
[ContentId("THE_GAMBIT_POWER")]
public sealed class TheGambitPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Receiver != self.Owner || !result.Props.IsPoweredAttack() || result.UnblockedDamage <= 0)
            return;
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
        Span<byte> targets = stackalloc byte[1];
        targets[0] = self.Owner;
        // CreatureCmd.Kill(owner): a normal (non-forced) kill, so death prevention still runs.
        Kernel.Kill(ref x, targets, force: false);
    }
}

/// <summary>
/// Native <c>AutomationPower</c> (instanced): every 10 cards drawn grants its owner <c>Amount</c>
/// energy and restarts the counter. AfterCardDrawnMirrors keeps the counter in
/// AutomationPredictionState; the Sts2Solver row stores it in Aux0.
/// </summary>
[ContentId("AUTOMATION_POWER")]
public sealed class AutomationPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux0 <= 0)
            power.Aux0 = 10;
        power.Aux0--;
        if (power.Aux0 > 0)
            return;
        Kernel.GainEnergy(ref x, power.Amount);
        power.Aux0 = 10;
    }
}

/// <summary>
/// Native <c>PanachePower</c> (instanced): every fifth card played after the power, deal
/// <c>Amount</c> unpowered damage to all hittable enemies and restart the cycle. The first
/// AfterCardPlayed (the card that applied it) only arms the counter. Aux0 is CardsLeft, Aux1 is
/// alreadyApplied; EndTurnPowerSupport.Batch048 resets CardsLeft at the owner's side turn end.
/// </summary>
[ContentId("PANACHE_POWER")]
public sealed class PanachePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Aux1 == 0)
        {
            power.Aux1 = 1;
            return;
        }
        if (power.Aux0 <= 0)
            power.Aux0 = 5;
        power.Aux0--;
        if (power.Aux0 > 0)
            return;
        byte owner = power.Owner;
        ushort id = power.Id;
        int occurrence = 0;
        for (int prior = 0; prior < self.Index; prior++)
        {
            ref readonly Power earlier = ref x.B.Powers[prior];
            if (earlier.Owner == owner && earlier.Id == id)
                occurrence++;
        }
        CardEffects.DamageAllEnemies(ref x, power.Amount, ValueProps.Unpowered, self.Owner);
        if (x.NeedsChoice)
            return;
        // A kill inside the damage completes the enemy's death (Possess returning stolen Strength can
        // remove an earlier player row), so the pre-damage row may have moved. Re-resolve the instance
        // by (owner, id, occurrence) before restarting CardsLeft, like RollingBoulderPower.
        int row = Kernel.FindPowerOccurrence(x.B, owner, id, occurrence);
        if (row < 0)
            throw new UnsupportedException("Panache 的 Power 行在伤害后消失。");
        x.B.Powers[row].Aux0 = 5;
    }

    public override void EndTurnBatch048(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            x.B.Powers[self.Index].Aux0 = 5;
    }
}

/// <summary>
/// Native <c>RollingBoulderPower</c> (instanced): at its owner's player turn start deal its Amount
/// as unpowered damage to every hittable enemy, then add the power's canonical Damage (5) to Amount.
/// TurnStartPowerSupport's RollingBoulderPower case; Amount is the damage.
/// </summary>
[ContentId("ROLLING_BOULDER_POWER")]
public sealed class RollingBoulderPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        ref Power power = ref x.B.Powers[self.Index];
        byte owner = power.Owner;
        ushort id = power.Id;
        int amount = power.Amount;
        int occurrence = 0;
        for (int prior = 0; prior < self.Index; prior++)
        {
            ref readonly Power earlier = ref x.B.Powers[prior];
            if (earlier.Owner == owner && earlier.Id == id)
                occurrence++;
        }
        CardEffects.DamageAllEnemies(ref x, amount, ValueProps.Unpowered, self.Owner);
        if (x.NeedsChoice)
            return;
        // Enemy deaths during the damage can remove enemy power rows and shift this player-side
        // instanced row. Re-resolve the snapshot instance by (owner, id, occurrence) before writing;
        // the native hook mutates the captured model object, not the pre-damage table index.
        int row = Kernel.FindPowerOccurrence(x.B, owner, id, occurrence);
        if (row < 0)
            throw new UnsupportedException("Rolling Boulder 的 Power 行在伤害后消失。");
        // RollingBoulderPower.CanonicalVars Damage = 5; the card upgrade raises the initial Amount,
        // not this increment (RollingBoulder.incrementAmount).
        x.B.Powers[row].Amount = (int)Math.Clamp((long)x.B.Powers[row].Amount + 5, 0, Limits.ValueClamp);
    }
}

/// <summary>
/// Native <c>TheBombPower</c> (instanced): Amount is the turns left; Aux0 is the per-instance bomb
/// damage (SetDamage from the card's BombDamage). At its owner's side turn end it decrements, and on
/// the last turn deals Aux0 unpowered damage to every hittable enemy and removes itself.
/// </summary>
[ContentId("THE_BOMB_POWER")]
public sealed class TheBombPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool IsInstanced => true;

    public override void BeforeSideTurnEnd(ref Exec x, Listener self, Side side, ushort participants)
    {
        if (!Kernel.Participates(participants, self.Owner))
            return;
        ref Power power = ref x.B.Powers[self.Index];
        if (power.Amount > 1)
        {
            power.Amount--;
            return;
        }
        int damage = power.Aux0;
        CardEffects.DamageAllEnemies(ref x, damage, ValueProps.Unpowered, self.Owner);
        if (x.NeedsChoice)
            return;
        Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

/// <summary>
/// Native <c>NostalgiaPower</c>: an Attack or Skill card that would go to the discard pile returns to
/// the top of the draw pile while fewer than Amount Attack/Skill cards have been played this turn.
/// ModifyCardPlayResultLocationMirrors.HandleNostalgiaPower.
/// </summary>
[ContentId("NOSTALGIA_POWER")]
public sealed class NostalgiaPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override Pile ModifyCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        bool isAutoPlay, int energyValue, Pile location, ref PilePosition position)
    {
        if (location != Pile.Discard)
            return location;
        CardType type = Kernel.ResolveCardType(x.B, x.R, card);
        if (type is not (CardType.Attack or CardType.Skill))
            return location;
        if (x.B.Counter(Counter.AttackSkillStartsThisTurn) >= x.B.Powers[self.Index].Amount)
            return location;
        position = PilePosition.Top;
        return Pile.Draw;
    }
}

/// <summary>
/// Native <c>StratagemPower</c>: after each shuffle choose Amount cards from the draw pile and put
/// them into hand (AfterShuffleMirrors.HandleStratagemPower →
/// TurnStartChoiceSupport.Resolve MoveToHand over the draw pile).
/// </summary>
[ContentId("STRATAGEM_POWER")]
public sealed class StratagemPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterShuffle(ref Exec x, Listener self, byte shuffler)
    {
        int amount = x.B.Powers[self.Index].Amount;
        int options = x.B.DrawCount;
        int count = Math.Min(amount, options);
        if (count <= 0)
            return;
        Span<int> all = stackalloc int[options];
        for (int index = 0; index < options; index++)
            all[index] = index;
        Span<int> selected = stackalloc int[count];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.MoveToHand, Pile.Draw, all,
                count, count, selected, out int selectedCount))
            return;
        Kernel.MoveSelected(ref x.B, Pile.Draw, selected[..selectedCount], Pile.Hand,
            PilePosition.Bottom);
    }
}

/// <summary>
/// Native <c>MayhemPower</c>: the existing engine models its auto-play in
/// <c>TriggerScheduledAutoPlays</c> (AutoPrePlay phase) at the player's turn start. The kernel's
/// <see cref="Kernel.AutoPrePlay"/> reads this amount, so the definition keeps only the power
/// value and no hook of its own.
/// </summary>
[ContentId("MAYHEM_POWER")]
public sealed class MayhemPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;
}

/// <summary>
/// Native <c>EntropyPower</c>: at the owner's player turn start, choose Amount cards from hand and
/// transform each into a random unlocked card of the same pool (TurnStartPowerSupport's EntropyPower
/// case → TurnStartChoiceSupport.Transform → CombatPredictionSimulator.CreateRandomCardForTransform).
/// </summary>
[ContentId("ENTROPY_POWER")]
public sealed class EntropyPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override IReadOnlyList<string> GeneratedCardPools =>
        [CardPools.CharacterAll, CardPools.ColorlessAll, CardPools.StatusAll, CardPools.CurseAll];

    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        int hand = x.B.HandCount;
        if (hand == 0)
            return;
        int count = Math.Min(x.B.Powers[self.Index].Amount, hand);
        if (count <= 0)
            return;
        Span<int> options = stackalloc int[hand];
        for (int index = 0; index < hand; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[count];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.Transform, Pile.Hand, options,
                count, count, selected, out int selectedCount))
            return;
        for (int index = 0; index < selectedCount; index++)
        {
            ref Card original = ref Piles.At(ref x.B, Pile.Hand, selected[index]);
            Card replacement = RandomTransform(ref x, original);
            Kernel.ReplaceCard(ref x, Pile.Hand, selected[index], replacement);
            if (x.NeedsChoice)
                return;
        }
    }

    private static Card RandomTransform(ref Exec x, in Card original)
    {
        CardStats stats = x.R.Card(original.Id, original.Upgrade);
        // CardFactory.GetDefaultTransformationOptions picks original.Pool unless the card is a
        // Quest/Event/Ancient/Token card (then the colorless pool). CharacterAll excludes Basic cards
        // (FilterForCombat), so a starter card still belongs to the character pool.
        // CardFactory.GetDefaultTransformationOptions: a Quest/Event/Ancient/Token card uses the
        // colorless pool; every other card uses its canonical pool (character, colorless, status or
        // curse), regardless of whether that particular card is currently unlocked.
        string poolKey = stats.Type == CardType.Quest
            || stats.Rarity is CardRarity.Event or CardRarity.Ancient or CardRarity.Token
            ? CardPools.ColorlessAll
            : stats.Pool switch
            {
                TransformPool.Status => CardPools.StatusAll,
                TransformPool.Curse => CardPools.CurseAll,
                TransformPool.Colorless => CardPools.ColorlessAll,
                _ => CardPools.CharacterAll,
            };
        ReadOnlySpan<ushort> pool = x.R.CardPool(poolKey).Cards;
        Span<ushort> candidates = stackalloc ushort[pool.Length];
        int count = 0;
        // CardFactory.GetFilteredTransformationOptions keeps the original's rarity band for ordinary
        // cards; Status and Curse are the two "rarity - 8 <= 1" values that skip the band filter.
        bool filterRarity = stats.Rarity is not (CardRarity.Event or CardRarity.Ancient
            or CardRarity.Status or CardRarity.Curse);
        for (int index = 0; index < pool.Length; index++)
        {
            ushort id = pool[index];
            if (id == original.Id)
                continue;
            CardRarity rarity = x.R.Card(id, 0).Rarity;
            if (filterRarity && rarity is not (CardRarity.Common or CardRarity.Uncommon
                or CardRarity.Rare))
                continue;
            candidates[count++] = id;
        }
        if (count == 0)
            throw new UnsupportedException($"随机变化没有可用的 {poolKey} 候选。");
        ushort chosen = candidates[x.B.RngStream(RngStream.CombatCardGeneration).NextInt(0, count)];
        return Kernel.CreateCard(x.R, chosen);
    }
}
