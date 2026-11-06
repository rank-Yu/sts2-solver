using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Regent;

/// <summary>
/// <c>Supermassive</c>: a calculated attack (<c>CalculatedVarSpecRegistry.Supermassive</c>):
/// CalculationBase + ExtraDamage × cards generated this combat. The generated-card count is a
/// combat-long counter imported from the root history.
/// </summary>
[ContentId("SUPERMASSIVE")]
public sealed class Supermassive : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedDamageVar.Calculate: CalculationBase + ExtraDamage * the multiplier below; Thrash reads
    // it with a null target. OnPlay uses the same formula.
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        damage = CardEffects.Var(in x.B, x.R, in self, 0) + CardEffects.Var(in x.B, x.R, in self, 1) * (x.B.Counter(Counter.CardsGeneratedThisCombat));
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, selfInHand: false, out decimal damage);
        CardEffects.Attack(ref x, damage);
    }
}

/// <summary>
/// <c>KinglyKick</c>: the inferred attack; drawing it lowers its own cost by 1 for the rest of combat
/// (<c>AfterCardDrawnMirrors.HandleKinglyKick</c> → <c>EnergyCost.AddThisCombat(-1)</c>).
/// </summary>
[ContentId("KINGLY_KICK")]
public sealed class KinglyKick : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (!Kernel.TakeIntrinsicDraw(ref x, self, drawnPosition))
            return;
        // The drawn card is this card, at its post-draw hand position (AddThisCombat(-1)).
        Kernel.AddEnergyCostModifier(ref x.B.Cards[drawnPosition], -1, CostType.Relative,
            CostExpiration.EndOfCombat, reduceOnly: false);
    }
}

/// <summary>
/// <c>KinglyPunch</c>: the inferred attack; drawing it adds the Increase variable to its Damage for
/// the rest of combat (<c>AfterCardDrawnMirrors.HandleKinglyPunch</c>). The accumulated increase is
/// kept in <see cref="Card.Private"/> so <c>AfterDowngraded</c> can re-add it after Dampen.
/// </summary>
[ContentId("KINGLY_PUNCH")]
public sealed class KinglyPunch : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Increase"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (!Kernel.TakeIntrinsicDraw(ref x, self, drawnPosition))
            return;
        ref Card drawn = ref x.B.Cards[drawnPosition];
        int increase = (int)x.R.Card(drawn.Id, drawn.Upgrade).Vars[1];
        if (increase > short.MaxValue - drawn.Var0 || increase > short.MaxValue - drawn.Private)
            throw new UnsupportedException("KinglyPunch 的伤害累加超出卡牌私有计数范围。");
        drawn.Var0 = (short)(drawn.Var0 + increase);
        drawn.Private = (short)(drawn.Private + increase);
    }

    public override void ProjectPrivate(ref Card card) => card.Private = card.Var0;

    public override void AfterDowngraded(ref Battle battle, int position)
        => battle.Cards[position].Var0 = battle.Cards[position].Private;
}

/// <summary>
/// <c>Radiate</c>: CalculatedHits = CalculationBase + CalculationExtra × stars gained this turn
/// (<c>CalculatedVarSpecRegistry.Radiate</c> → <c>GetStarsGainedThisTurn</c>), used as the all-enemy
/// attack's hit count.
/// </summary>
[ContentId("RADIATE")]
public sealed class Radiate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];

    public override void OnPlay(ref Exec x)
    {
        int hits = (int)(CardEffects.Var(x, 1)
            + CardEffects.Var(x, 2) * x.B.Counter(Counter.StarsGainedThisTurn));
        CardEffects.GeneralAttack(ref x, 0, hitCount: hits);
    }
}

/// <summary><c>Devastate</c> (4 star cost): the general attack.</summary>
[ContentId("DEVASTATE")]
public sealed class Devastate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

/// <summary><c>GammaBlast</c> (3 star cost): attack, then Weak and Vulnerable after the enemy deaths.</summary>
[ContentId("GAMMA_BLAST")]
public sealed class GammaBlast : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower", "WeakPower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice || x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.WeakPower>(),
            (int)CardEffects.Var(x, 2), Limits.PlayerOwner);
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.VulnerablePower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}

/// <summary><c>Hegemony</c>: the inferred attack, then EnergyNextTurnPower with the Energy variable.</summary>
[ContentId("HEGEMONY")]
public sealed class Hegemony : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Energy"];

    public override IReadOnlyList<string> AppliedPowers => ["ENERGY_NEXT_TURN_POWER"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.EnergyNextTurnPower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>ShiningStrike</c>: attack, then the spec gains Stars; CardResultLocationMirrors sends a
/// discard result to the top of the draw pile instead.
/// </summary>
[ContentId("SHINING_STRIKE")]
public sealed class ShiningStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Stars"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.GainStars(ref x, (int)CardEffects.Var(x, 1));
    }

    public override Pile ResultLocation(in Battle battle, Rules rules, in Card card,
        Pile baseLocation, out PilePosition position)
    {
        position = PilePosition.Bottom;
        if (baseLocation != Pile.Discard)
            return baseLocation;
        position = PilePosition.Top;
        return Pile.Draw;
    }
}

/// <summary>
/// <c>KnockoutBlow</c>: attack, then CorePowerSupport's case gains Stars when the attack killed its
/// target; the deaths settle before that compensation.
/// </summary>
[ContentId("KNOCKOUT_BLOW")]
public sealed class KnockoutBlow : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Stars"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice || !x.CardKilledTarget)
            return;
        Kernel.GainStars(ref x, (int)CardEffects.Var(x, 1));
    }
}

/// <summary>
/// <c>Stardust</c> (X star cost): RandomTargetAttackCardMirrors attacks a random opponent once per star
/// spent (<c>ResolveStarXValue</c>).
/// </summary>
[ContentId("STARDUST")]
public sealed class Stardust : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        int hits = Kernel.ResolveStarXValue(ref x);
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}
