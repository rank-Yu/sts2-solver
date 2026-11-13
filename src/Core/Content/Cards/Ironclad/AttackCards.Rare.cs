using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Ironclad;

[ContentId("CONFLAGRATION")]
public sealed class Conflagration : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];

    public override void OnPlay(ref Exec x)
        => CardEffects.GeneralAttack(ref x, 0, hitCount: (int)CardEffects.Var(x, 1));
}

[ContentId("FEED")]
public sealed class Feed : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "MaxHp"];

    public override void OnPlay(ref Exec x)
    {
        byte target = x.Target;
        // Native Feed reads Target.Powers.All(ShouldOwnerDeathTriggerFatal) before the attack: a minion
        // never counts, and a Decimillipede segment (ReattachPower) counts only when every other
        // segment is already dead.
        bool fatal = target != Limits.NoOwner && Kernel.TargetDeathTriggersFatal(x.B, target);
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice || target == Limits.NoOwner || x.B.Creature(target).IsAlive)
            return;
        // Feed only gains MaxHp after a fatal hit.
        if (!fatal)
            return;
        Kernel.GainMaxHp(ref x, Limits.PlayerOwner, CardEffects.Var(x, 1));
        GrowthRewards.Add(ref x.B, GrowthRewardSource.Feed);
    }
}

[ContentId("PACTS_END")]
public sealed class PactsEnd : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override void OnPlay(ref Exec x)
    {
        // BespokeCardMirrors.PactsEndOnPlay only attacks when the exhaust pile already has the
        // CardsVar amount; the card itself stays in the play pile during this check.
        if (x.B.ExhaustCount < (int)CardEffects.Var(x, 1))
            return;
        CardEffects.GeneralAttack(ref x, 0);
    }
}

[ContentId("MANGLE")]
public sealed class Mangle : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "StrengthLoss"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice || x.Target == Limits.NoOwner)
            return;
        // CardEffectSpecRegistry: ApplyTemporaryStrengthLoss<ManglePower>.
        Kernel.ApplyTemporaryStrength(ref x, x.Target, Registry.PowerId<Powers.ManglePower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner, sign: -1);
    }
}

[ContentId("FIEND_FIRE")]
public sealed class FiendFire : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        int cardCount = x.B.HandCount;
        for (int index = 0; index < cardCount; index++)
        {
            if (Kernel.IsOverOrEnding(x.B))
                return;
            Kernel.Exhaust(ref x, Pile.Hand, 0);
            if (x.NeedsChoice)
                return;
        }
        CardEffects.GeneralAttack(ref x, 0, hitCount: cardCount);
    }
}

[ContentId("TEAR_ASUNDER")]
public sealed class TearAsunder : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CalculatedHits = 1 + History DamageReceived entries for the owner with unblocked damage.
    public override void OnPlay(ref Exec x)
    {
        int hits = 1 + x.B.Counter(Counter.PlayerUnblockedDamageEvents);
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}

[ContentId("THRASH")]
public sealed class Thrash : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];

    // The native card adds ModifyDamage's decimal result to DynamicVars.Damage.BaseValue and to
    // the private ExtraDamage field, and AfterDowngraded re-adds ExtraDamage. Sts2Solver keeps the
    // exact delta in the battle's decimal table; downgrade leaves the table untouched.
    public override bool UsesDecimalDamageVar => true;

    public override void OnPlay(ref Exec x)
    {
        // Thrash.cs: two hits, then a random Attack in hand is read for its damage, that value goes
        // through ModifyDamage with a null target (dealer-side modifiers such as Strength and Strike
        // Dummy apply, target-side ones such as Vulnerable do not), is added to this card's Damage,
        // and the selected card is exhausted.
        CardEffects.GeneralAttack(ref x, 0, hitCount: (int)CardEffects.Var(x, 1));
        if (x.NeedsChoice)
            return;
        // DamageCmd.Execute settles a kill (AfterDeath) inside the attack, before the hand is read:
        // a Possess Strength holder killed by these hits has already returned the stolen Strength.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        int selected = PickRandomHandAttack(ref x);
        if (selected < 0)
            return;
        Card victim = x.B.Cards[selected];
        CardDef def = Registry.Card(victim.Id);
        if (!def.TryGetCalculatedDamage(ref x, victim, Limits.NoOwner, selfInHand: true, out decimal damage))
            damage = NamedDamage(ref x, victim, def);
        damage = HookDispatch.ModifyDamage(ref x, Limits.NoOwner, Limits.PlayerOwner, damage,
            ValueProps.Move, hasCardSource: true, victim, sourceInPlay: false);
        CardEffects.AddCurrentCardVar(ref x, 0, damage);
        Kernel.Exhaust(ref x, Pile.Hand, selected);
    }

    private static int PickRandomHandAttack(ref Exec x)
    {
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            if (Kernel.ResolveCardType(x.B, x.R, x.B.Cards[index]) == CardType.Attack)
                count++;
        }
        if (count == 0)
            return -1;
        int pick = x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, count);
        for (int index = 0; index < x.B.HandCount; index++)
        {
            if (Kernel.ResolveCardType(x.B, x.R, x.B.Cards[index]) != CardType.Attack)
                continue;
            if (pick-- == 0)
                return index;
        }
        throw new InvalidOperationException("Thrash 的手牌随机选择没有落到任何候选。");
    }

    private static decimal NamedDamage(ref Exec x, in Card card, CardDef def)
    {
        CardStats stats = x.R.Card(card.Id, card.Upgrade);
        for (int index = 0; index < def.VarNames.Count && index < stats.Vars.Length; index++)
        {
            string name = def.VarNames[index];
            if (name is not ("Damage" or "OstyDamage"))
                continue;
            return stats.Vars[index] + DecimalVars.Delta(in x.B, in card, index);
        }
        throw new UnsupportedException($"Thrash 选中的攻击 {def.NativeId} 没有 Damage 变量。");
    }
}
