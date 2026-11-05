using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Powers;

/// <summary>Native <c>StrengthPower</c>: additive damage for its owner's powered attacks.</summary>
[ContentId("STRENGTH_POWER")]
public sealed class StrengthPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => true;

    public override decimal ModifyDamageAdditive(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (self.Owner != damage.Dealer || !damage.Props.IsPoweredAttack())
            return 0m;
        return x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// Native <c>VulnerablePower</c> as mirrored by the existing engine: multiplies powered attacks
/// against its owner by <c>DamageIncrease</c>, plus Paper Phrog's 0.25 and the attacker's Cruelty
/// percentage (<c>ModifyDamageMirrors.HandleVulnerablePower</c>). Debilitate is rejected at admission
/// until supported. Ticks at the end of the enemy side (<c>TickDurations</c>).
/// </summary>
[ContentId("VULNERABLE_POWER")]
public sealed class VulnerablePower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool UsesNativeDurationSkip => true;

    public override bool TicksAtEnemySideEnd => true;

    public override IReadOnlyList<string> VarNames => ["DamageIncrease"];

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (damage.Target != self.Owner || !damage.Props.IsPoweredAttack())
            return 1m;
        decimal multiplier = x.R.PowerVar(Id, 0);
        // ModifyDamageMirrors.HandleVulnerablePower calls PaperPhrog.ModifyVulnerableMultiplier when the
        // attacker's player owns the relic: it adds 0.25 unless the relic owner is the target.
        if (damage.Dealer == Limits.PlayerOwner && damage.Target != Limits.PlayerOwner
            && OwnsPaperPhrog(x.B))
            multiplier += 0.25m;
        // ModifyDamageMirrors.HandleVulnerablePower adds CrueltyPower.Amount / 100m; the native
        // CrueltyPower.ModifyVulnerableMultiplier uses the same percentage divisor and reads the
        // dealer's power, falling back to the dealer's pet owner (Osty -> player).
        if (damage.Dealer != Limits.NoOwner)
        {
            ushort crueltyId = Registry.PowerId<CrueltyPower>();
            int row = Kernel.FindPower(x.B, damage.Dealer, crueltyId);
            if (row < 0 && damage.Dealer == Limits.OstyOwner)
                row = Kernel.FindPower(x.B, Limits.PlayerOwner, crueltyId);
            if (row >= 0)
                multiplier += (decimal)x.B.Powers[row].Amount / 100m;
        }
        // ModifyDamageMirrors.HandleVulnerablePower applies DebilitatePower on the vulnerable target:
        // multiplier += multiplier - 1.
        if (Registry.TryPower("DEBILITATE_POWER", out ushort debilitate)
            && Kernel.GetAmount(x.B, self.Owner, debilitate) > 0)
        {
            multiplier += multiplier - 1m;
        }
        return multiplier;
    }

    /// <summary><c>Player.GetRelic&lt;PaperPhrog&gt;()</c> for the single player.</summary>
    private static bool OwnsPaperPhrog(in Battle battle)
    {
        if (!Registry.TryRelic("PAPER_PHROG", out ushort id))
            return false;
        for (int index = 0; index < battle.RelicCount; index++)
        {
            ref readonly Relic relic = ref battle.Relics[index];
            if (relic.Id == id)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Native <c>WeakPower</c>: multiplies its owner's powered attacks by <c>DamageDecrease</c>.
/// Paper Krane and Debilitate are rejected at admission until supported. Ticks at the end of the
/// enemy side (<c>TickDurations</c>) and skips the first player-side duration tick.
/// </summary>
[ContentId("WEAK_POWER")]
public sealed class WeakPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool UsesNativeDurationSkip => true;

    public override bool TicksAtEnemySideEnd => true;

    public override IReadOnlyList<string> VarNames => ["DamageDecrease"];

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (damage.Dealer != self.Owner || !damage.Props.IsPoweredAttack())
            return 1m;
        decimal multiplier = x.R.PowerVar(Id, 0);
        // ModifyDamageMirrors.HandleWeakPower asks the target player's PaperKrane relic for the
        // weak multiplier (subtract 0.15) before folding in DebilitatePower.
        if (damage.Target == Limits.PlayerOwner
            && Registry.TryRelic("PAPER_KRANE", out ushort paperKrane))
        {
            for (int index = 0; index < x.B.RelicCount; index++)
            {
                ref readonly Relic relic = ref x.B.Relics[index];
                if (relic.Id == paperKrane && (relic.Flags & RelicFlags.Melted) == 0)
                {
                    multiplier -= 0.15m;
                    break;
                }
            }
        }
        // ModifyDamageMirrors.HandleWeakPower folds in DebilitatePower on the attacking owner:
        // multiplier -= 1 - multiplier.
        if (Registry.TryPower("DEBILITATE_POWER", out ushort debilitate)
            && Kernel.GetAmount(x.B, self.Owner, debilitate) > 0)
        {
            multiplier -= 1m - multiplier;
        }
        return multiplier;
    }
}

/// <summary>
/// Native <c>FrailPower</c>: multiplies powered card or monster-move block gained by its owner.
/// Ticks at the end of the enemy side (<c>TickDurations</c>) and skips the first player-side tick.
/// </summary>
[ContentId("FRAIL_POWER")]
public sealed class FrailPower : PowerDef
{
    // FrailPower.ModifyBlockMultiplicative returns this literal; the power declares no dynamic var.
    private const decimal BlockMultiplier = 0.75m;

    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool UsesNativeDurationSkip => true;

    public override bool TicksAtEnemySideEnd => true;

    public override decimal ModifyBlockMultiplicative(ref Exec x, Listener self, in BlockContext block, decimal amount)
    {
        if (block.Target != self.Owner || !block.Props.IsPoweredAttack())
            return 1m;
        return BlockMultiplier;
    }
}

/// <summary>
/// Native <c>DexterityPower</c>: adds its amount to powered block gained by its owner. Positive
/// amounts are buffs; negative amounts are allowed and treated as debuffs by
/// <see cref="PowerDef.TypeForAmount"/>.
/// </summary>
[ContentId("DEXTERITY_POWER")]
public sealed class DexterityPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => true;

    public override decimal ModifyBlockAdditive(ref Exec x, Listener self, in BlockContext block, decimal amount)
    {
        // Card sources are player-owned; monster-move block has no card source and must target its
        // owner directly. Both branches and the powered-block filter match
        // DexterityPower.ModifyBlockAdditive.
        if (block.HasCardSource)
        {
            if (self.Owner != Limits.PlayerOwner)
                return 0m;
        }
        else if (self.Owner != block.Target)
        {
            return 0m;
        }
        if (!block.Props.IsPoweredAttack())
            return 0m;
        return x.B.Powers[self.Index].Amount;
    }
}

/// <summary>
/// Native <c>ColossusPower</c>: a powered attack against its owner by a dealer that has Vulnerable
/// is multiplied by <c>DamageDecrease</c>. Ticks at the end of the enemy side
/// (<c>ColossusPower.AfterSideTurnEnd</c>).
/// </summary>
[ContentId("COLOSSUS_POWER")]
public sealed class ColossusPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool TicksAtEnemySideEnd => true;

    public override IReadOnlyList<string> VarNames => ["DamageDecrease"];

    public override decimal ModifyDamageMultiplicative(ref Exec x, Listener self, in DamageContext damage, decimal amount)
    {
        if (damage.Target != self.Owner || !damage.Props.IsPoweredAttack() || damage.Dealer == Limits.NoOwner)
            return 1m;
        if (Kernel.GetAmount(x.B, damage.Dealer, Registry.PowerId<VulnerablePower>()) <= 0)
            return 1m;
        return x.R.PowerVar(Id, 0);
    }
}

/// <summary>
/// Native <c>IntangiblePower</c>, called through the existing engine's original-method fallback (no mirror is
/// registered): <c>ModifyHpLostAfterOsty</c> caps its owner's HP loss of at least 1 to 1 and
/// <c>ModifyDamageCap</c> caps damage to its owner at 1. The native in-progress check reads the live combat,
/// which is in progress whenever the existing engine predicts, so it is not mirrored. Ticks at the end of the
/// enemy side (<c>CorePowerSupport.TickDurations</c>); <c>AfterModifyingHpLostAfterOsty</c> only flashes.
/// </summary>
[ContentId("INTANGIBLE_POWER")]
public sealed class IntangiblePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool TicksAtEnemySideEnd => true;

    public override decimal ModifyDamageCap(ref Exec x, Listener self, in DamageContext damage)
        => damage.Target == self.Owner ? 1m : decimal.MaxValue;

    public override decimal ModifyHpLostAfterOsty(ref Exec x, Listener self, in HpLossContext loss, decimal amount)
        => loss.Target == self.Owner && amount >= 1m ? 1m : amount;
}

/// <summary>
/// Native <c>NoDrawPower</c> (BattleTrance, BulletTime and similar): while its owner is a player,
/// a non-hand draw is refused (<c>ShouldDrawMirrors.HandleNoDrawPower</c>). The existing engine removes
/// it in <c>EndTurnPowerSupport.TriggerRegular</c> when the owner participates in the side turn end;
/// Sts2Solver's <c>EndTurnRegular</c> is that pass. No private slot.
/// </summary>
[ContentId("NO_DRAW_POWER")]
public sealed class NoDrawPower : PowerDef
{
    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Single;

    public override bool ShouldDraw(ref Exec x, Listener self, bool fromHandDraw)
        => fromHandDraw || self.Owner != Limits.PlayerOwner;

    public override void EndTurnRegular(ref Exec x, Listener self, Side side, bool participates)
    {
        if (participates)
            Kernel.SetPowerAmount(ref x.B, self.Owner, Id, 0);
    }
}

/// <summary>
/// Native <c>BufferPower</c> (LuckyTonic): the owner's after-Osty HP loss is reduced to 0 in the Late
/// pass and one stack is consumed by <c>AfterModifyingHpLostAfterOsty</c>
/// (<c>ModifyHpLostMirrors.HandleBufferPower</c> /
/// <c>AfterModifyingHpLostAfterOstyMirrors.HandleBufferPower</c>). No private slot.
/// </summary>
[ContentId("BUFFER_POWER")]
public sealed class BufferPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyHpLostAfterOstyLate(ref Exec x, Listener self, in HpLossContext loss,
        decimal amount)
        => loss.Target == self.Owner ? 0m : amount;

    public override void AfterModifyingHpLostAfterOsty(ref Exec x, Listener self)
        => Kernel.ModifyPowerAmount(ref x.B, self.Owner, Id, Math.Max(0, x.B.Powers[self.Index].Amount - 1));
}
