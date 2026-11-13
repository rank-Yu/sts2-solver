using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Defect;

[ContentId("CLAW")]
public sealed class Claw : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Increase"];

    // CardEffectSpecRegistry.Claw: attack, then every Claw in combat gains Increase damage.
    // ExtraDamageFromClawPlays is carried in Private (ProjectPrivate) so DowngradeCard can restore it.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int increase = (int)CardEffects.Var(x, 1);
        if (increase == 0)
            return;
        if (!Registry.TryCard("CLAW", out ushort claw))
            return;
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref Card card = ref x.B.Cards[position];
            if (card.Id != claw)
                continue;
            AddIncrease(ref card, increase);
        }
    }

    public override void ProjectPrivate(ref Card card) => card.Private = card.Var0;

    public override void AfterDowngraded(ref Battle battle, int position)
    {
        // Claw.AfterDowngraded: DynamicVars.Damage.BaseValue += ExtraDamageFromClawPlays.
        battle.Cards[position].Var0 = battle.Cards[position].Private;
    }

    private static void AddIncrease(ref Card card, int increase)
    {
        if (increase > short.MaxValue - card.Var0 || increase > short.MaxValue - card.Private)
            throw new UnsupportedException("利爪的伤害累加超出卡牌私有计数范围。");
        card.Var0 = (short)(card.Var0 + increase);
        card.Private = (short)(card.Private + increase);
    }
}

[ContentId("FOCUSED_STRIKE")]
public sealed class FocusedStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "FocusPower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CardEffectSpecRegistry's FocusedStrike → ApplyTemporaryFocus<FocusedStrikePower>.
        Kernel.ApplyTemporaryFocus(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.FocusedStrikePower>(), (int)CardEffects.Var(x, 1),
            Limits.PlayerOwner, sign: 1);
    }
}

[ContentId("MOMENTUM_STRIKE")]
public sealed class MomentumStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardEffectSpecRegistry: AttackSingle, then playedCard.MutablePreview.EnergyCost.SetThisCombat(0).
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.AddEnergyCostModifier(ref Kernel.CurrentPlayCard(ref x), 0,
            CostType.Absolute, CostExpiration.EndOfCombat, reduceOnly: false);
    }
}

[ContentId("BARRAGE")]
public sealed class Barrage : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // GeneralAttackOnPlay reads CalculatedHits = orb count.
    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x, CardEffects.Var(x, 0), x.B.OrbCount);
}

[ContentId("SWEEPING_BEAM")]
public sealed class SweepingBeam : CardDef
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
}

[ContentId("BEAM_CELL")]
public sealed class BeamCell : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower"];

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
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

[ContentId("COLD_SNAP")]
public sealed class ColdSnap : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrb(ref x, Kernel.OrbId("FROST_ORB"));
    }
}

[ContentId("BALL_LIGHTNING")]
public sealed class BallLightning : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ChannelOrb(ref x, Kernel.OrbId("LIGHTNING_ORB"));
    }
}

[ContentId("GO_FOR_THE_EYES")]
public sealed class GoForTheEyes : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "WeakPower"];

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
        // CardEffectSpecRegistry.GoForTheEyes: only against an enemy whose current move attacks.
        if (x.Target != Limits.NoOwner && Kernel.EnemyIntendsToAttack(x.B, x.R, x.Target))
            CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 1);
    }
}

[ContentId("COMPILE_DRIVER")]
public sealed class CompileDriver : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardDrawCardMirrors.CompileDriverOnPlay: attack, then draw one per distinct orb id.
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
        Kernel.Draw(ref x, Kernel.DistinctOrbCount(x.B), fromHandDraw: false);
    }
}

[ContentId("UPROAR")]
public sealed class Uproar : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];

    public override void OnPlay(ref Exec x)
    {
        // CardSelectionCardMirrors.UproarOnPlay: two hits, then auto-play the first attack of a stable
        // shuffle of the draw pile (playable attacks first, then every attack).
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), (int)CardEffects.Var(x, 1));
        if (x.NeedsChoice)
            return;
        // DamageCmd.Attack.Execute returns after the killed enemies' death powers have resolved.
        // The auto-play decision below must observe that state; for example, killing the Shrinker
        // Beetle removes its ShrinkPower before the chosen attack's damage is calculated.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        int drawCount = x.B.DrawCount;
        Span<Card> buffer = stackalloc Card[drawCount];
        Span<int> origins = stackalloc int[drawCount];
        int eligible = 0;
        for (int index = 0; index < drawCount; index++)
        {
            Card card = x.B.Cards[x.B.HandCount + index];
            if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Attack)
                continue;
            if ((card.Keywords & Keywords.Unplayable) != 0)
                continue;
            buffer[eligible] = card;
            origins[eligible] = index;
            eligible++;
        }
        int count = eligible;
        if (count == 0)
        {
            x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
            for (int index = 0; index < drawCount; index++)
            {
                Card card = x.B.Cards[x.B.HandCount + index];
                if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Attack)
                    continue;
                buffer[count] = card;
                origins[count] = index;
                count++;
            }
        }
        CardSort.SortWithOrigins(buffer[..count], origins[..count], x.R, in x.B, Pile.Draw);
        RngState rng = x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        if (count == 0)
            return;
        for (int n = count; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (buffer[k], buffer[n]) = (buffer[n], buffer[k]);
            (origins[k], origins[n]) = (origins[n], origins[k]);
        }
        int position = origins[0];
        if (position >= x.B.DrawCount
            || !Fingerprint.Equal(Piles.At(ref x.B, Pile.Draw, position), buffer[0]))
        {
            throw new UnsupportedException("Uproar 选中的抽牌在结算前离开了抽牌堆。");
        }
        Kernel.AutoPlay(ref x, Pile.Draw, position);
    }
}

[ContentId("GUNK_UP")]
public sealed class GunkUp : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SLIMED", 0)];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), (int)CardEffects.Var(x, 1));
        if (x.NeedsChoice)
            return;
        if (!Registry.TryCard("SLIMED", out ushort id))
            throw new UnsupportedException("黏液需要 SLIMED 的 Sts2Solver 定义。");
        Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, id), Pile.Discard);
    }
}
