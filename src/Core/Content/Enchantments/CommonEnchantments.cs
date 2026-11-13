using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Enchantments;

/// <summary><c>Sharp</c>: adds the enchantment amount to powered attacks (decompiled Sharp.cs).</summary>
[ContentId("SHARP")]
public sealed class Sharp : EnchantmentDef
{
    public override decimal EnchantDamageAdditive(in Card card, decimal damage, ValueProps props)
    {
        if (!props.IsPoweredAttack())
            return 0m;
        return card.EnchantmentAmount;
    }
}

/// <summary><c>Instinct</c>: doubles powered attack damage (decompiled Instinct.cs).</summary>
[ContentId("INSTINCT")]
public sealed class Instinct : EnchantmentDef
{
    public override decimal EnchantDamageMultiplicative(in Card card, decimal damage, ValueProps props)
    {
        if (!props.IsPoweredAttack())
            return 1m;
        return 2m;
    }
}

/// <summary>
/// <c>Corrupted</c>: 1.5x powered attack damage, and on play the owner loses 2 HP
/// (Unblockable | Unpowered | Move) with this card as the source (decompiled Corrupted.cs).
/// </summary>
[ContentId("CORRUPTED")]
public sealed class Corrupted : EnchantmentDef
{
    private const decimal SelfDamage = 2m;

    public override decimal EnchantDamageMultiplicative(in Card card, decimal damage, ValueProps props)
        => props.IsPoweredAttack() ? 1.5m : 1m;

    public override void OnPlay(ref Exec x, in Card card, byte target)
        => CardEffects.DamageOwnerFromCard(ref x, SelfDamage,
            ValueProps.Unblockable | ValueProps.Unpowered | ValueProps.Move);
}

/// <summary>
/// <c>Momentum</c>: kept <c>_extraDamage</c> adds to powered attacks; OnPlay increments it by the
/// enchantment amount (decompiled Momentum.cs).
/// </summary>
[ContentId("MOMENTUM")]
public sealed class Momentum : EnchantmentDef
{
    public override decimal EnchantDamageAdditive(in Card card, decimal damage, ValueProps props)
    {
        if (!props.IsPoweredAttack())
            return 0m;
        return card.EnchantmentPrivate;
    }

    public override void OnPlay(ref Exec x, in Card card, byte target)
    {
        ref Card played = ref Kernel.CurrentPlayCard(ref x);
        long extra = (long)played.EnchantmentPrivate + played.EnchantmentAmount;
        if (extra is < short.MinValue or > short.MaxValue)
            throw new UnsupportedException($"Momentum 累计伤害 {extra} 超出 Sts2Solver 的短整型表示。");
        played.EnchantmentPrivate = (short)extra;
        Kernel.SyncCurrentPlayCard(ref x);
    }
}

/// <summary><c>Nimble</c>: adds the enchantment amount to block gain (decompiled Nimble.cs).</summary>
[ContentId("NIMBLE")]
public sealed class Nimble : EnchantmentDef
{
    public override decimal EnchantBlockAdditive(in Card card, decimal block)
    {
        return card.EnchantmentAmount;
    }
}

/// <summary>
/// <c>Goopy</c>: block additive is amount − 1, and every play increments the amount
/// (decompiled Goopy.cs).
/// </summary>
[ContentId("GOOPY")]
public sealed class Goopy : EnchantmentDef
{
    public override decimal EnchantBlockAdditive(in Card card, decimal block)
    {
        return card.EnchantmentAmount - 1;
    }

    // Goopy.cs: OnEnchant adds Exhaust; CardModel.DowngradeInternal re-runs it after resetting
    // the card's local keywords.
    public override void OnEnchant(ref Exec x, ref Card card)
        => card.AddKeyword(Keywords.Exhaust);

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (play.Position != self.Index)
            return;
        ref Card card = ref x.B.Cards[self.Index];
        if (card.EnchantmentAmount == short.MaxValue)
            throw new UnsupportedException($"Goopy 层数 {card.EnchantmentAmount} 超出 Sts2Solver 的短整型表示。");
        card.EnchantmentAmount++;
        if ((card.Flags & CardFlags.DeckCard) != 0)
        {
            // AfterCardPlayedMirrors.HandleGoopy records the long-term resource and growth reward
            // only for a card with a permanent deck version.
            x.B.AddCounter(Counter.LongTermResourceValue, 1);
            GrowthRewards.Add(ref x.B, GrowthRewardSource.Goopy);
        }
    }
}

/// <summary>
/// <c>TezcatarasEmber</c>: adds the canonical 3 damage to powered attacks; the zero cost and Eternal
/// keyword come from OnEnchant and are already in the imported card value (decompiled
/// TezcatarasEmber.cs, CanonicalVars DamageVar(3)).
/// </summary>
[ContentId("TEZCATARAS_EMBER")]
public sealed class TezcatarasEmber : EnchantmentDef
{
    private const decimal BonusDamage = 3m;

    // TezcatarasEmber.cs: OnEnchant zeroes the base energy cost and adds Eternal. Both are replayed
    // by CardModel.DowngradeInternal after Dampen resets the canonical cost and local keywords.
    public override void OnEnchant(ref Exec x, ref Card card)
    {
        card.BaseCost = 0;
        card.AddKeyword(Keywords.Eternal);
    }

    public override decimal EnchantDamageAdditive(in Card card, decimal damage, ValueProps props)
    {
        if (!props.IsPoweredAttack())
            return 0m;
        return BonusDamage;
    }
}

/// <summary>
/// <c>Vigorous</c>: adds its amount to powered attacks while Normal, then disables itself after the
/// card is played (decompiled Vigorous.cs).
/// </summary>
[ContentId("VIGOROUS")]
public sealed class Vigorous : EnchantmentDef
{
    public override decimal EnchantDamageAdditive(in Card card, decimal damage, ValueProps props)
        => card.EnchantmentStatus == 0 && props.IsPoweredAttack() ? card.EnchantmentAmount : 0m;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (play.Position != self.Index || x.B.Cards[self.Index].EnchantmentStatus != 0)
            return;
        x.B.Cards[self.Index].EnchantmentStatus = 1;
    }
}

/// <summary><c>Adroit</c>: OnPlay gains block equal to the enchantment amount (decompiled Adroit.cs).</summary>
[ContentId("ADROIT")]
public sealed class Adroit : EnchantmentDef
{
    public override void OnPlay(ref Exec x, in Card card, byte target)
    {
        Kernel.GainBlock(ref x, Limits.PlayerOwner, card.EnchantmentAmount, ValueProps.Move, fromCard: true);
    }
}

/// <summary><c>Inky</c>: OnPlay applies Weak 1 (decompiled Inky.cs, PowerVar&lt;WeakPower&gt;(1)).</summary>
[ContentId("INKY")]
public sealed class Inky : EnchantmentDef
{
    private const int WeakAmount = 1;

    public override void OnPlay(ref Exec x, in Card card, byte target)
    {
        // Inky.cs branches on the native CardModel.TargetType, which is dynamic (Shiv targets all
        // enemies while FanOfKnivesPower is present).
        TargetType targetType = Registry.Card(card.Id).ResolveTargetType(x.B, x.R, card);
        ushort weak = Registry.PowerId<Powers.WeakPower>();
        if (targetType == TargetType.AllEnemies)
        {
            for (int position = 0; position < x.B.RosterCount; position++)
            {
                byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
                if (!Kernel.IsHittable(x.B, owner))
                    continue;
                Kernel.ApplyPowerFromCard(ref x, owner, weak, WeakAmount, Limits.PlayerOwner, card);
                if (x.NeedsChoice)
                    return;
            }
            return;
        }
        if (target != Limits.NoOwner)
            Kernel.ApplyPowerFromCard(ref x, target, weak, WeakAmount, Limits.PlayerOwner, card);
    }
}

/// <summary><c>Sown</c>: the first play gains the enchantment amount of energy and disables itself (decompiled Sown.cs).</summary>
[ContentId("SOWN")]
public sealed class Sown : EnchantmentDef
{
    public override void OnPlay(ref Exec x, in Card card, byte target)
    {
        if (card.EnchantmentStatus != 0)
            return;
        ref Card played = ref Kernel.CurrentPlayCard(ref x);
        played.EnchantmentStatus = 1;
        Kernel.SyncCurrentPlayCard(ref x);
        Kernel.GainEnergy(ref x, played.EnchantmentAmount);
    }
}

/// <summary><c>Swift</c>: the first play draws the enchantment amount and disables itself (decompiled Swift.cs).</summary>
[ContentId("SWIFT")]
public sealed class Swift : EnchantmentDef
{
    public override void OnPlay(ref Exec x, in Card card, byte target)
    {
        if (card.EnchantmentStatus != 0)
            return;
        ref Card played = ref Kernel.CurrentPlayCard(ref x);
        played.EnchantmentStatus = 1;
        Kernel.SyncCurrentPlayCard(ref x);
        Kernel.Draw(ref x, played.EnchantmentAmount, fromHandDraw: false);
    }
}

/// <summary>
/// <c>Glam</c>: the first play of the combat adds its canonical Times (1) replay, then disables
/// itself; <c>_usedThisCombat</c> is the card's enchantment private slot (decompiled Glam.cs).
/// </summary>
[ContentId("GLAM")]
public sealed class Glam : EnchantmentDef
{
    private const int Times = 1;

    public override int EnchantPlayCount(in Card card, int replayCount)
        => card.EnchantmentStatus == 0 ? replayCount + Times : replayCount;

    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (play.Position != self.Index)
            return;
        ref Card card = ref x.B.Cards[self.Index];
        card.EnchantmentStatus = 1;
    }
}

/// <summary><c>Spiral</c>: adds its canonical Times (1) replay to Basic Strikes/Defends (decompiled Spiral.cs).</summary>
[ContentId("SPIRAL")]
public sealed class Spiral : EnchantmentDef
{
    private const int Times = 1;

    public override int EnchantPlayCount(in Card card, int replayCount)
    {
        return replayCount + Times;
    }
}

/// <summary><c>Clone</c>: no combat behavior; admitted so a cloned deck card is not rejected (decompiled Clone.cs).</summary>
[ContentId("CLONE")]
public sealed class Clone : EnchantmentDef;

/// <summary><c>Steady</c>: Retain keyword only, already in the imported card value (decompiled Steady.cs).</summary>
[ContentId("STEADY")]
public sealed class Steady : EnchantmentDef
{
    // Steady.cs: OnEnchant adds Retain; replayed after a downgrade resets local keywords.
    public override void OnEnchant(ref Exec x, ref Card card)
        => card.AddKeyword(Keywords.Retain);
}

/// <summary><c>RoyallyApproved</c>: Innate/Retain only, already in the imported card value (decompiled RoyallyApproved.cs).</summary>
[ContentId("ROYALLY_APPROVED")]
public sealed class RoyallyApproved : EnchantmentDef
{
    // RoyallyApproved.cs: OnEnchant adds Innate and Retain; replayed after a downgrade resets
    // local keywords.
    public override void OnEnchant(ref Exec x, ref Card card)
        => card.AddKeyword(Keywords.Innate | Keywords.Retain);
}

/// <summary>
/// <c>SoulsPower</c>: removes Exhaust at enchant time (decompiled SoulsPower.cs). The imported card
/// value already has the removal; the hook is replayed after a downgrade resets local keywords.
/// </summary>
[ContentId("SOULS_POWER")]
public sealed class SoulsPower : EnchantmentDef
{
    public override void OnEnchant(ref Exec x, ref Card card) => card.RemoveKeyword(Keywords.Exhaust);
}

/// <summary>
/// <c>Imbued</c>: on the first player turn, after the scheduled auto-plays, every card with this
/// enchantment is auto-played (<c>EnchantmentLifecycleSupport.TriggerAutoPrePlay</c>, vanilla
/// <c>AfterAutoPrePlayPhaseEntered</c>). The auto-play itself is kernel-side in
/// <c>Kernel.AutoPrePlay</c>; the definition only admits the enchantment. ShouldStartAtBottomOfDrawPile
/// is applied by the root setup before the snapshot, so the imported draw pile already reflects it.
/// </summary>
[ContentId("IMBUED")]
public sealed class Imbued : EnchantmentDef
{
    public override bool StartsAtBottomOfDrawPile => true;
}

/// <summary>
/// <c>Slither</c>: when this card is drawn into the hand, its combat energy cost becomes a random
/// 0..3 (<c>CombatEnergyCosts.NextInt(4)</c>, <c>SetThisCombat</c> absolute; decompiled Slither.cs and
/// <c>AfterCardDrawnMirrors.HandleSlither</c>). The existing engine ignores the test-only override.
/// </summary>
[ContentId("SLITHER")]
public sealed class Slither : EnchantmentDef
{
    private const int CostRange = 4;

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        // HandleSlither matches the drawn object against the enchantment's card and requires it to be
        // in hand. The kernel collects listeners after the card moved, so drawnPosition is the card's
        // hand slot; a hook that moved it first makes this no longer the drawn card's slot (the same
        // position contract used by TakeIntrinsicDraw).
        if (self.Kind != ListenerKind.Enchantment || self.Index != drawnPosition
            || (uint)drawnPosition >= (uint)x.B.HandCount)
            return;
        int cost = x.B.RngStream(RngStream.CombatEnergyCosts).NextInt(CostRange);
        Kernel.AddEnergyCostModifier(ref x.B.Cards[drawnPosition], cost, CostType.Absolute,
            CostExpiration.EndOfCombat, reduceOnly: false);
    }
}

/// <summary>
/// <c>SlumberingEssence</c>: at the player turn end, before the hand is flushed, the card gains a
/// <c>WhenPlayed</c> −1 energy cost modifier (vanilla <c>BeforeFlush</c>;
/// <c>EnchantmentLifecycleSupport.BeforeFlush</c>).
/// </summary>
[ContentId("SLUMBERING_ESSENCE")]
public sealed class SlumberingEssence : EnchantmentDef
{
    public override void BeforeFlush(ref Exec x, Listener self)
    {
        if ((uint)self.Index >= (uint)x.B.HandCount)
            return;
        Kernel.AddEnergyCostModifier(ref x.B.Cards[self.Index], -1, CostType.Relative,
            CostExpiration.WhenPlayed, reduceOnly: false);
    }
}

/// <summary>
/// <c>PerfectFit</c>: every non-initial shuffle that contains this card moves it to the front of the
/// merged list (vanilla <c>ModifyShuffleOrder</c>, <c>ModifyShuffleOrderMirrors.HandlePerfectFit</c>).
/// </summary>
[ContentId("PERFECT_FIT")]
public sealed class PerfectFit : EnchantmentDef
{
    public override void ModifyShuffleOrder(ref Exec x, Listener self, scoped Span<Card> cards, int count,
        bool isInitialShuffle)
    {
        if (isInitialShuffle || (uint)self.Index >= (uint)x.B.CardCount)
            return;
        Card own = x.B.Cards[self.Index];
        int found = -1;
        for (int index = 0; index < count; index++)
        {
            if (CardEffects.Equal(cards[index], own))
            {
                found = index;
                break;
            }
        }
        if (found <= 0)
            return;
        Card card = cards[found];
        for (int index = found; index > 0; index--)
            cards[index] = cards[index - 1];
        cards[0] = card;
    }
}
