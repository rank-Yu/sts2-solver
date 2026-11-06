using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Necrobinder;

/// <summary>
/// <c>DeathMarch</c>: calculated attack with CalculationBase + ExtraDamage × non-hand draws this turn
/// (<c>CalculatedVarSpecRegistry.DeathMarch</c>).
/// </summary>
[ContentId("DEATH_MARCH")]
public sealed class DeathMarch : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    // CalculatedDamageVar.Calculate: CalculationBase + ExtraDamage * the multiplier below; Thrash reads
    // it with a null target. OnPlay uses the same formula.
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        damage = CardEffects.Var(in x.B, x.R, in self, 0) + CardEffects.Var(in x.B, x.R, in self, 1) * (x.B.Counter(Counter.NonHandDrawsThisTurn));
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, selfInHand: false, out decimal damage);
        CardEffects.Attack(ref x, damage);
    }
}

/// <summary>
/// <c>PullFromBelow</c>: Osty-free attack with CalculatedHits = CalculationBase + CalculationExtra ×
/// Ethereal card plays this combat (<c>CalculatedVarSpecRegistry.PullFromBelow</c>).
/// </summary>
[ContentId("PULL_FROM_BELOW")]
public sealed class PullFromBelow : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];

    public override void OnPlay(ref Exec x)
    {
        int hits = (int)NecrobinderContent.Calculated(ref x, 1, 2,
            x.B.Counter(Counter.EtherealPlays));
        CardEffects.Attack(ref x, NecrobinderContent.Var(ref x, 0), hits);
    }
}

/// <summary>
/// Native <c>Fetch</c>: an Osty attack; if this exact card has not finished a play this turn, draw one.
/// Existing engine: <c>CardDrawCardMirrors.FetchOnPlay</c> and
/// <c>SimulatedCombatState.WasFetchPlayedThisTurn</c>/<c>_fetchCardsPlayedThisTurn</c>.
/// </summary>
[ContentId("FETCH")]
public sealed class Fetch : CardDef
{
    public override bool IsIdentityContent => true;

    public override IReadOnlyList<string> VarNames => ["OstyDamage", "Cards"];

    public override bool TracksFetchPlay => true;

    public override void OnPlay(ref Exec x)
    {
        if (!NecrobinderContent.HasOsty(x.B))
            return;
        NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));
        if (x.NeedsChoice)
            return;
        if ((x.Card.Private & Kernel.FetchPlayedBit) == 0)
            Kernel.Draw(ref x, (int)NecrobinderContent.Var(ref x, 1), fromHandDraw: false);
    }
}

/// <summary>
/// <c>HighFive</c>: playable only while Osty is alive; Osty hits all enemies, then every hittable enemy
/// gains Vulnerable (<c>CorePowerSupport</c>'s HighFive case).
/// </summary>
[ContentId("HIGH_FIVE")]
public sealed class HighFive : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage", "VulnerablePower"];

    public override bool IsPlayable(in Battle battle, Rules rules, in Card card)
        => NecrobinderContent.HasOsty(battle);

    public override void OnPlay(ref Exec x)
    {
        NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToAllEnemies(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

/// <summary>
/// <c>BoneShards</c>: Osty hits all enemies, the player gains block, then the pet is killed with force
/// (<c>CardEffectSpecRegistry</c>'s BoneShards completion, after the inferred attack/block mirror).
/// </summary>
[ContentId("BONE_SHARDS")]
public sealed class BoneShards : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage", "Block"];

    public override void OnPlay(ref Exec x)
    {
        // BoneShardsOnPlay wraps attack + block in Osty.CheckMissingWithAnim: both are skipped when the
        // pet is missing or dead. The sacrifice in CardEffectSpecRegistry uses the same alive gate.
        if (!NecrobinderContent.HasOsty(x.B))
            return;
        NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));
        if (x.NeedsChoice)
            return;
        CardEffects.GeneralBlock(ref x, 1);
        if (x.NeedsChoice || !NecrobinderContent.HasOsty(x.B))
            return;
        Span<byte> osty = stackalloc byte[1];
        osty[0] = Limits.OstyOwner;
        Kernel.Kill(ref x, osty, force: true);
    }
}

[ContentId("RIGHT_HAND_HAND")]
public sealed class RightHandHand : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage", "Energy"];

    public override void OnPlay(ref Exec x) => NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));

    // RightHandHand.AfterCardPlayedLate: the listener does NOT check that the played card is this copy;
    // after any play that spent at least the Energy variable, each RightHandHand copy sitting in the
    // discard pile goes back to hand (AfterCardPlayedMirrors.HandleRightHandHand).
    public override void AfterCardPlayedLate(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if ((uint)self.Index >= (uint)x.B.CardCount)
            return;
        ref readonly Card card = ref x.B.Cards[self.Index];
        if (card.Id != Id)
            return;
        if (x.CurrentPlayEnergyValue < x.R.Card(card.Id, card.Upgrade).Vars[1])
            return;
        int start = Piles.Start(x.B, Pile.Discard);
        int count = Piles.Count(x.B, Pile.Discard);
        if (self.Index < start || self.Index >= start + count)
            return;
        Kernel.MoveCard(ref x.B, Pile.Discard, self.Index - start, Pile.Hand,
            PilePosition.Bottom);
    }
}

/// <summary>
/// <c>Rattle</c>: Osty attack whose hit count is the CalculatedHits variable,
/// CalculationBase + CalculationExtra × (1 + Osty's attacks this turn).
/// </summary>
[ContentId("RATTLE")]
public sealed class Rattle : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage", "CalculationBase", "CalculationExtra"];

    public override void OnPlay(ref Exec x)
    {
        if (!NecrobinderContent.HasOsty(x.B))
            return;
        decimal multiplier = 1 + x.B.Counter(Counter.OstyAttacksThisTurn);
        int hits = (int)NecrobinderContent.Calculated(ref x, 1, 2, multiplier);
        NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0), hits);
    }
}

/// <summary>
/// <c>SicEm</c>: Osty attack, then apply SicEmPower to the target even when the pet was missing
/// (<c>CardEffectSpecRegistry</c>'s SicEm target power).
/// </summary>
[ContentId("SIC_EM")]
public sealed class SicEm : CardDef
{
    public override IReadOnlyList<string> VarNames => ["OstyDamage", "SicEmPower"];

    public override void OnPlay(ref Exec x)
    {
        NecrobinderContent.OstyAttack(ref x, NecrobinderContent.Var(ref x, 0));
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.SicEmPower>(), 1);
    }
}

/// <summary>
/// <c>Debilitate</c>: attack, then apply DebilitatePower to the target
/// (<c>CardEffectSpecRegistry</c>'s target DebilitatePower).
/// </summary>
[ContentId("DEBILITATE")]
public sealed class Debilitate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "DebilitatePower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack
        // and before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.DebilitatePower>(), 1);
    }
}

/// <summary>
/// <c>Veilpiercer</c>: attack, then one VeilpiercerPower on the player
/// (<c>CardEffectSpecRegistry</c>'s owner VeilpiercerPower).
/// </summary>
[ContentId("VEILPIERCER")]
public sealed class Veilpiercer : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.VeilpiercerPower>(), 1,
            Limits.PlayerOwner);
    }
}

[ContentId("BURY")]
public sealed class Bury : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

/// <summary>
/// <c>Severance</c>: attack, then three unupgraded Souls — one to the draw pile at a random position,
/// one to the discard pile, one to hand (<c>CardEffectSpecRegistry</c>'s Severance completion).
/// </summary>
[ContentId("SEVERANCE")]
public sealed class Severance : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOUL", 0)];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Card soul = NecrobinderContent.CreateSoul(x.B, x.R, upgraded: false);
        if (!Kernel.AddGeneratedCard(ref x, soul, Pile.Draw, PilePosition.Random)
            || x.NeedsChoice)
            return;
        if (!Kernel.AddGeneratedCard(ref x, soul, Pile.Discard)
            || x.NeedsChoice)
            return;
        Kernel.AddGeneratedCard(ref x, soul, Pile.Hand);
    }
}
